//! Main-thread Lua bridge for the graphics worker, plus the `ultrawide` feature built on it.
//!
//! Lua must run on Unity's main thread, so the worker only queues chunks. A detour on `XLuaEngine.Update()` drains
//! the queue and runs each chunk through `XLua.LuaEnv.DoString(string chunk, string chunkName, LuaTable env)` on
//! `XLuaEngine.Env` (the game's one LuaEnv: public static field, read fresh every call because `Reload` replaces it).
//!
//! Why `XLuaEngine.Update` (RVA 0x17CEDA0, verified in GameAssembly.dll 4.8):
//! - It is `static void Update()` with no arguments, so the JmpBack detour needs no argument handling.
//! - Callers: `XGame.Update` (0x2377E00) calls it once per frame right after `XGraphicManager.Update`, and
//!   `XGame.Update` is called only by `XApplication.Update` (0x2371FC0, a MonoBehaviour `Update`, i.e. the Unity
//!   main thread, once per frame).
//! - Its body only ticks `Env` once a second; it is the Lua engine's own pump, the one place the game itself
//!   re-enters the LuaEnv from C#.
//!
//! A chunk that returns the string `pending` is not finished: it is retried every 0.5 s (its way to say "the game's
//! Lua is not initialised yet"). Any other first return value is the outcome message; a managed exception (Lua
//! error) is the failure message. Both are kept per chunk name for the worker (`LuaBridge::outcome`).
//!
//! The detour never blocks the game thread: queue and outcome locks are `try_lock`ed (a missed lock is retried next
//! frame) and nothing runs once `QUITTING` is set.
use std::{
    cell::RefCell,
    ffi::{c_void, CString},
    ptr::null_mut,
    sync::{atomic::{AtomicI32, AtomicU64, AtomicUsize, Ordering::Relaxed}, Mutex, OnceLock, PoisonError},
    time::{Duration, Instant},
};

use anyhow::{anyhow, bail, Result};
use ilhook::x64::Registers;

use super::graphics_tweaks::{code, QUITTING};
use super::ok;
use crate::interceptor::Interceptor;
use crate::modules::il2cpp::{self, export, Il2cpp};
use crate::util::{c_string, read_csharp_string, readable};

/// The screen is re-read every this many frames (about a quarter second at 60 fps).
const SCREEN_EVERY: u64 = 15;
const RETRY: Duration = Duration::from_millis(500);
const PENDING: &str = "pending";
/// `Il2CppArray`: klass, monitor, bounds, max_length (8 bytes each), then the elements.
const ARRAY_VECTOR: usize = 0x20;
const MESSAGE_LIMIT: usize = 400;

type StringNew = unsafe extern "system" fn(*const i8) -> *mut c_void;
type FormatException = unsafe extern "system" fn(*mut c_void, *mut i8, i32);
type ArrayLength = unsafe extern "system" fn(*mut c_void) -> u32;
type ObjectClass = unsafe extern "system" fn(*mut c_void) -> *mut c_void;
type ClassName = unsafe extern "system" fn(*mut c_void) -> *const i8;
type ClassMethods = unsafe extern "system" fn(*mut c_void, *mut *mut c_void) -> *mut c_void;
type MethodName = unsafe extern "system" fn(*mut c_void) -> *const i8;

/// Everything the detour needs, resolved once in `install`.
struct Ctx {
    il: Il2cpp,
    string_new: StringNew,
    format_exception: FormatException,
    array_length: ArrayLength,
    object_class: ObjectClass,
    class_name: ClassName,
    do_string: *mut c_void,
    env_field: *mut c_void,
    /// `Screen.get_width`, `get_height`, `get_fullScreen`.
    screen: [*mut c_void; 3],
}

// The raw pointers are IL2CPP metadata (MethodInfo/FieldInfo), immutable and valid for the process lifetime.
unsafe impl Send for Ctx {}
unsafe impl Sync for Ctx {}

static CTX: OnceLock<Ctx> = OnceLock::new();

struct Job {
    name: String,
    source: String,
    due: Instant,
    console_seq: Option<u64>,
    console_line: Option<String>,
}

#[derive(Clone)]
struct Outcome {
    name: String,
    seq: u64,
    result: Result<String, String>,
}

static INBOX: Mutex<Vec<Job>> = Mutex::new(Vec::new());
static INBOX_LEN: AtomicUsize = AtomicUsize::new(0);
static OUTCOMES: Mutex<Vec<Outcome>> = Mutex::new(Vec::new());
static SEQ: AtomicU64 = AtomicU64::new(0);
static CONSOLE_SEQ: AtomicU64 = AtomicU64::new(0);
static BRIDGE_ERROR: Mutex<Option<String>> = Mutex::new(None);

/// Overlay-facing APIs only touch Rust state; Lua executes in the main-thread detour.
pub(super) fn queue_console(source: &str) -> u64 {
    let mut inbox = INBOX.lock().unwrap_or_else(PoisonError::into_inner);
    let seq = CONSOLE_SEQ.fetch_add(1, Relaxed) + 1;
    inbox.retain(|job| job.name != "console");
    if let Some(message) = console_error() {
        INBOX_LEN.store(inbox.len(), Relaxed);
        let mut outcomes = OUTCOMES.lock().unwrap_or_else(PoisonError::into_inner);
        outcomes.retain(|outcome| outcome.name != "console");
        outcomes.push(Outcome { name: "console".into(), seq, result: Err(message) });
        ok("lua console", source.lines().next().unwrap_or_default());
        return seq;
    }
    let console_line = Some(source.lines().next().unwrap_or_default().to_owned());
    let source = format!(r##"
local function stringify(...)
    local values = {{}}
    for i = 1, select("#", ...) do values[i] = tostring(select(i, ...)) end
    return table.concat(values, "\t")
end
return stringify((function()
{source}
end)())
"##);
    inbox.push(Job { name: "console".into(), source, due: Instant::now(), console_seq: Some(seq), console_line });
    INBOX_LEN.store(inbox.len(), Relaxed);
    seq
}

pub(super) fn console_outcome() -> Option<(u64, Result<String, String>)> {
    OUTCOMES.lock().unwrap_or_else(PoisonError::into_inner).iter()
        .find(|o| o.name == "console").map(|o| (o.seq, o.result.clone()))
}

pub(super) fn console_error() -> Option<String> {
    BRIDGE_ERROR.lock().unwrap_or_else(PoisonError::into_inner).clone()
}
/// 0 = not read yet.
static SCREEN_W: AtomicI32 = AtomicI32::new(0);
static SCREEN_H: AtomicI32 = AtomicI32::new(0);
/// -1 = not read yet.
static SCREEN_FULL: AtomicI32 = AtomicI32::new(-1);

/// State owned by the main thread (only the detour touches it).
#[derive(Default)]
struct Local {
    jobs: Vec<Job>,
    frame: u64,
    /// Outcomes whose `OUTCOMES` lock was busy; published on a later frame.
    late: Vec<Outcome>,
    /// Chunk names already logged as waiting for the game's Lua.
    waiting: Vec<String>,
}

thread_local! {
    static LOCAL: RefCell<Local> = RefCell::new(Local::default());
}

/// Owns the `XLuaEngine.Update` detour; keep it alive as long as the worker runs (dropping unhooks).
pub(super) struct LuaBridge {
    _hook: Interceptor,
}

impl LuaBridge {
    pub(super) unsafe fn install(il: &Il2cpp, assemblies: &[*mut c_void]) -> Result<Self> {
        let result = Self::install_inner(il, assemblies);
        *BRIDGE_ERROR.lock().unwrap_or_else(PoisonError::into_inner) =
            result.as_ref().err().map(|error| format!("{error:#}"));
        result
    }

    unsafe fn install_inner(il: &Il2cpp, assemblies: &[*mut c_void]) -> Result<Self> {
        let (engine, _) = il.find_class(assemblies, "", "XLuaEngine").ok_or_else(|| anyhow!("class `XLuaEngine` not found in any of {} loaded assemblies", assemblies.len()))?;
        let env_name = CString::new("Env")?;
        let env_field = (il.class_field)(engine, env_name.as_ptr());
        if env_field.is_null() {
            bail!("`XLuaEngine.Env` field not found in this build")
        }
        let update = il.static_method(engine, "XLuaEngine", "Update", &[])?;
        let (lua_env, _) = il.find_class(assemblies, "XLua", "LuaEnv").ok_or_else(|| anyhow!("class `XLua.LuaEnv` not found in any of {} loaded assemblies", assemblies.len()))?;
        let class_methods: ClassMethods = export("il2cpp_class_get_methods")?;
        let method_name: MethodName = export("il2cpp_method_get_name")?;
        let do_string = find_do_string(il, lua_env, class_methods, method_name)?;
        let (screen, _) = il.find_class(assemblies, "UnityEngine", "Screen").ok_or_else(|| anyhow!("class `UnityEngine.Screen` not found"))?;
        let screen = [
            il.static_method(screen, "Screen", "get_width", &[])?,
            il.static_method(screen, "Screen", "get_height", &[])?,
            il.static_method(screen, "Screen", "get_fullScreen", &[])?,
        ];
        let ctx = Ctx {
            il: *il,
            string_new: export("il2cpp_string_new")?,
            format_exception: export("il2cpp_format_exception")?,
            array_length: export("il2cpp_array_length")?,
            object_class: export("il2cpp_object_get_class")?,
            class_name: export("il2cpp_class_get_name")?,
            do_string,
            env_field,
            screen,
        };
        CTX.set(ctx).map_err(|_| anyhow!("the Lua bridge is already installed"))?;
        let at = code(update, "XLuaEngine.Update")?;
        let mut hook = Interceptor::new();
        hook.attach(at, on_update)?;
        ok("Lua bridge", &format!("XLuaEngine.Update at 0x{at:X} runs queued chunks through LuaEnv.DoString on the main thread"));
        Ok(Self { _hook: hook })
    }

    /// Queues `source` to run on the main thread under `chunk_name`; a chunk still queued or waiting under the same
    /// name is replaced. Never calls IL2CPP, so any thread may use it.
    pub(super) fn queue(&self, chunk_name: &str, source: String) {
        let mut inbox = INBOX.lock().unwrap_or_else(PoisonError::into_inner);
        inbox.retain(|job| job.name != chunk_name);
        inbox.push(Job { name: chunk_name.to_owned(), source, due: Instant::now(), console_seq: None, console_line: None });
        INBOX_LEN.store(inbox.len(), Relaxed);
    }

    /// The latest finished run of `chunk_name`: a sequence number (new for every run) and the returned message or
    /// the error text.
    pub(super) fn outcome(&self, chunk_name: &str) -> Option<(u64, Result<String, String>)> {
        let outcomes = OUTCOMES.lock().unwrap_or_else(PoisonError::into_inner);
        outcomes.iter().find(|o| o.name == chunk_name).map(|o| (o.seq, o.result.clone()))
    }

    /// `Screen.width/height` as the main thread last read them.
    pub(super) fn screen() -> Option<(i32, i32)> {
        let (w, h) = (SCREEN_W.load(Relaxed), SCREEN_H.load(Relaxed));
        (w > 0 && h > 0).then_some((w, h))
    }

    /// `Screen.fullScreen` as the main thread last read it.
    pub(super) fn fullscreen() -> Option<bool> {
        match SCREEN_FULL.load(Relaxed) {
            -1 => None,
            value => Some(value != 0),
        }
    }
}

/// `LuaEnv` has two `DoString(x, string chunkName, LuaTable env)` overloads (`byte[]` and `string`); the
/// name-and-count lookup would pick either, so the first parameter type decides.
unsafe fn find_do_string(il: &Il2cpp, class: *mut c_void, methods: ClassMethods, name: MethodName) -> Result<*mut c_void> {
    let mut iterator = null_mut();
    loop {
        let method = methods(class, &mut iterator);
        if method.is_null() {
            bail!("`LuaEnv.DoString(string, string, LuaTable)` not found in this build")
        }
        if c_string(name(method)).as_deref() == Some("DoString") && (il.param_count)(method) == 3 && il.type_name_of((il.param)(method, 0)) == "System.String" {
            return Ok(method);
        }
    }
}

/// Set by the worker; the next `XLuaEngine.Update` on the main thread installs the overlay (see `graphics.rs`).
static START_OVERLAY: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

pub(super) fn start_overlay_on_main_thread() {
    START_OVERLAY.store(true, Relaxed);
}

unsafe extern "win64" fn on_update(_: *mut Registers, _: usize) {
    if QUITTING.load(Relaxed) {
        return;
    }
    if START_OVERLAY.swap(false, Relaxed) {
        super::graphics_overlay::start();
    }
    let Some(ctx) = CTX.get() else { return };
    // try_borrow: a chunk that somehow re-enters `XLuaEngine.Update` must not panic across the hook.
    LOCAL.with(|local| {
        if let Ok(mut local) = local.try_borrow_mut() {
            tick(ctx, &mut local);
        }
    });
}

unsafe fn tick(ctx: &Ctx, local: &mut Local) {
    local.frame += 1;
    if local.frame % SCREEN_EVERY == 1 {
        read_screen(ctx);
    }
    if !local.late.is_empty() {
        for outcome in std::mem::take(&mut local.late) {
            publish(local, outcome);
        }
    }
    if INBOX_LEN.load(Relaxed) != 0 {
        if let Ok(mut inbox) = INBOX.try_lock() {
            for job in inbox.drain(..) {
                local.jobs.retain(|queued| queued.name != job.name);
                local.jobs.push(job);
            }
            INBOX_LEN.store(0, Relaxed);
        }
    }
    if local.jobs.is_empty() {
        return;
    }
    let mut env = null_mut::<c_void>();
    (ctx.il.static_get)(ctx.env_field, (&mut env as *mut *mut c_void).cast());
    if env.is_null() {
        return; // the Lua engine has not created its environment yet; chunks wait
    }
    let now = Instant::now();
    let mut index = 0;
    while index < local.jobs.len() {
        if local.jobs[index].due > now {
            index += 1;
            continue;
        }
        let mut job = local.jobs.remove(index);
        if job.console_seq.is_some_and(|seq| seq != CONSOLE_SEQ.load(Relaxed)) {
            continue;
        }
        let result = run(ctx, env, &job);
        if job.console_seq.is_none() && matches!(&result, Ok(Some(message)) if message == PENDING) {
            if !local.waiting.contains(&job.name) {
                local.waiting.push(job.name.clone());
                ok(&format!("lua {}", job.name), "the game's Lua is not initialised yet; retrying every 0.5 s");
            }
            job.due = now + RETRY;
            local.jobs.insert(index, job);
            index += 1;
            continue;
        }
        local.waiting.retain(|name| *name != job.name);
        let result = result.map(Option::unwrap_or_default);
        if let Some(line) = &job.console_line {
            ok("lua console", line);
        }
        if job.console_seq.is_none() {
            if let Err(message) = &result {
                il2cpp::failed(&format!("lua {}", job.name), message, "effects of the chunk before the error are not rolled back");
            }
        }
        let outcome = Outcome { name: job.name, seq: job.console_seq.unwrap_or_else(|| SEQ.fetch_add(1, Relaxed) + 1), result };
        publish(local, outcome);
    }
}

fn publish(local: &mut Local, outcome: Outcome) {
    match OUTCOMES.try_lock() {
        Ok(mut outcomes) => match outcomes.iter_mut().find(|o| o.name == outcome.name) {
            Some(existing) => *existing = outcome,
            None => outcomes.push(outcome),
        },
        Err(_) => local.late.push(outcome),
    }
}

unsafe fn read_screen(ctx: &Ctx) {
    let [width, height, full] = ctx.screen;
    if let (Some(w), Some(h)) = (ctx.il.call_get_int(width), ctx.il.call_get_int(height)) {
        SCREEN_W.store(w, Relaxed);
        SCREEN_H.store(h, Relaxed);
    }
    if let Some(full) = ctx.il.call_get_bool(full) {
        SCREEN_FULL.store(i32::from(full), Relaxed);
    }
}

/// `Ok(None)` = the chunk finished without a string result.
unsafe fn run(ctx: &Ctx, env: *mut c_void, job: &Job) -> Result<Option<String>, String> {
    let chunk = CString::new(job.source.as_str()).map_err(|_| "the Lua source contains a NUL byte".to_owned())?;
    let name = CString::new(format!("={}", job.name)).map_err(|_| "the chunk name contains a NUL byte".to_owned())?;
    // The string objects stay referenced from this stack frame, which the GC scans, until the call returns.
    let mut args = [(ctx.string_new)(chunk.as_ptr()), (ctx.string_new)(name.as_ptr()), null_mut()];
    let mut exception = null_mut();
    let results = (ctx.il.invoke)(ctx.do_string, env, args.as_mut_ptr(), &mut exception);
    if !exception.is_null() {
        let mut buffer = [0i8; 2048];
        (ctx.format_exception)(exception, buffer.as_mut_ptr(), buffer.len() as i32 - 1);
        let text = c_string(buffer.as_ptr()).unwrap_or_else(|| "managed exception (message unreadable)".into());
        if job.console_seq.is_some() {
            return Err(text);
        }
        return Err(text.split_whitespace().collect::<Vec<_>>().join(" ").chars().take(MESSAGE_LIMIT).collect());
    }
    Ok(first_string(ctx, results))
}

/// The first element of the returned `object[]` when it is a string.
unsafe fn first_string(ctx: &Ctx, array: *mut c_void) -> Option<String> {
    if array.is_null() || (ctx.array_length)(array) == 0 {
        return None;
    }
    let slot = array as usize + ARRAY_VECTOR;
    if !readable(slot, std::mem::size_of::<usize>()) {
        return None;
    }
    let element = *(slot as *const *mut c_void);
    if element.is_null() || c_string((ctx.class_name)((ctx.object_class)(element))).as_deref() != Some("String") {
        return None;
    }
    read_csharp_string(element as usize)
}

/// Lua for the `ultrawide` option. The game decides fullscreen eligibility in Lua (`XUiPcManager.FullScreenableCheck`
/// compares the device resolution with the largest row of `UiPcScreenResolution.json`, max 2560x2048, and forces a
/// window above it), so it is lifted by adding rows to the cached table `XUiPcConfig.GetTabUiPcResolution()` and
/// clearing `XSettingHelper.ForceWindow`. Fullscreen then goes through the game's own `XUiPcManager.SetResolution`
/// (`XSettingHelper.SetResolution`, then the saved-preference writes), so its settings stay consistent.
///
/// Native side, disassembled in GameAssembly.dll 4.8, has no clamp: `XSettingHelper.SetResolution` (0x23AECB0) calls
/// `Screen.SetResolution(w, h, mode, 0)`; `XResolutionManager.SetResImpl` (0x242FC50) stores Width/Height/Aspect and
/// calls `Screen.SetResolution(w, h, Screen.fullScreen)` (or `SetupExtraRenderScale`); `XResolutionManager.Init`
/// (0x242F370) copies `Screen.width/height` into OriginWidth/OriginHeight; `XResolutionManager.Update` (0x2430520)
/// re-runs Init when `Screen.width/height` differ from them; `XDriver.ForceFullScreenable` (0x2372720) only applies
/// a configured resolution. None of them compares against a maximum or an aspect ratio.
///
/// `ascnet = true` marks the rows we add, so turning the option off removes exactly those. Both chunks wait
/// (`pending`) until `XDataCenter.UiPcManager` exists; it is created in `XDataCenter.InitDependency`, which
/// `XGame.Start2` runs right before its own `FullScreenableCheck()`, so ours always runs after the game's.
/// `rawget` because the game locks `_G` (reading an unknown global logs an error).
const LUA_PREAMBLE: &str = r#"
local data = rawget(_G, "XDataCenter")
local config = rawget(_G, "XUiPcConfig")
local pc = data and rawget(data, "UiPcManager")
if not (pc and config) then return "pending" end
local screen = CS.UnityEngine.Screen
local before = string.format("%dx%d %s, ForceWindow=%s", screen.width, screen.height,
    screen.fullScreen and "fullscreen" or "windowed", tostring(CS.XSettingHelper.ForceWindow))
local list = config.GetTabUiPcResolution()
local prefs = CS.UnityEngine.PlayerPrefs
-- The window size before ultrawide, kept in PlayerPrefs so it survives restarts: switching on saves the game's
-- "last screen" as the monitor size, and the game's own fallback then picks an unrelated table size.
local KEY_W, KEY_H = "AscNetUltrawideWindowW", "AscNetUltrawideWindowH"
"#;

const LUA_ON: &str = r#"
-- The game's gate measures the whole desktop (two stacked monitors read 5120x2520); fullscreen must target the
-- monitor the window is on, which is Screen.currentResolution.
local width, height = pc.GetDeviceScreenResolution()
width, height = math.floor(width), math.floor(height)
local current = screen.currentResolution
local target_w, target_h = current.width, current.height
local function has(x, y)
    for _, size in ipairs(list) do
        if size.x == x and size.y == y then return true end
    end
    return false
end
local added = {}
for _, size in ipairs({ { width, height }, { 2560, 1080 }, { 3440, 1440 }, { 5120, 1440 } }) do
    if size[1] <= width and size[2] <= height and not has(size[1], size[2]) then
        list[#list + 1] = { x = size[1], y = size[2], ascnet = true }
        added[#added + 1] = string.format("%dx%d", size[1], size[2])
    end
end
table.sort(list, function(a, b)
    if a.x == b.x then return a.y < b.y end
    return a.x < b.x
end)
if not screen.fullScreen then
    prefs.SetInt(KEY_W, screen.width)
    prefs.SetInt(KEY_H, screen.height)
    prefs.Save()
end
CS.XSettingHelper.ForceWindow = false
pc.SetResolution(target_w, target_h, CS.UnityEngine.FullScreenMode.FullScreenWindow)
return string.format("was %s; desktop %dx%d; monitor %dx%d; resolution table +[%s]; requested fullscreen window", before, width, height, target_w, target_h, table.concat(added, ", "))
"#;

const LUA_OFF: &str = r#"
local removed = {}
for i = #list, 1, -1 do
    if list[i].ascnet then
        removed[#removed + 1] = string.format("%dx%d", list[i].x, list[i].y)
        table.remove(list, i)
    end
end
pc.FullScreenableCheck()
local current = screen.currentResolution
local width, height = current.width, current.height
local w, h = prefs.GetInt(KEY_W, 0), prefs.GetInt(KEY_H, 0)
local restored = "none saved"
if w > 0 and h > 0 and w <= width and h <= height then
    pc.SetResolution(w, h, CS.UnityEngine.FullScreenMode.Windowed)
    restored = string.format("%dx%d windowed", w, h)
end
return string.format("was %s; resolution table -[%s]; the game's own windowed fallback re-checked; restored window %s", before, table.concat(removed, ", "), restored)
"#;

const ULTRAWIDE: &str = "ultrawide";
/// Ticks (0.5 s) to wait for the screen to change after a run before reporting the resolution anyway.
const WATCH_TICKS: u8 = 8;

/// `ultrawide = on|off`: applies the option on every change of the wish (and once at start when on).
#[derive(Default)]
pub(super) struct Ultrawide {
    applied: bool,
    /// Sequence number of the last outcome already reported.
    seen: u64,
    /// Screen/fullscreen at the last finished run, and ticks waited for a change.
    watch: Option<(Option<(i32, i32)>, Option<bool>, u8)>,
}

impl Ultrawide {
    /// Called every worker tick; problems are appended to `errors` under `ultrawide`.
    pub(super) fn update(&mut self, bridge: &Result<LuaBridge, String>, want: bool, errors: &mut Vec<(String, String)>) {
        let bridge = match bridge {
            Ok(bridge) => bridge,
            Err(message) => {
                if want {
                    errors.push((ULTRAWIDE.into(), message.clone()));
                }
                return;
            }
        };
        if want != self.applied {
            self.applied = want;
            bridge.queue(ULTRAWIDE, format!("{LUA_PREAMBLE}{}", if want { LUA_ON } else { LUA_OFF }));
        }
        if let Some((seq, result)) = bridge.outcome(ULTRAWIDE) {
            if seq != self.seen {
                self.seen = seq;
                if let Ok(message) = &result {
                    ok(ULTRAWIDE, message);
                    self.watch = Some((LuaBridge::screen(), LuaBridge::fullscreen(), 0));
                }
            }
            if let Err(message) = result {
                errors.push((ULTRAWIDE.into(), message));
            }
        }
        if let Some((screen, fullscreen, ticks)) = &mut self.watch {
            *ticks += 1;
            let now = (LuaBridge::screen(), LuaBridge::fullscreen());
            if now != (*screen, *fullscreen) || *ticks >= WATCH_TICKS {
                let size = now.0.map_or("?".to_owned(), |(w, h)| format!("{w}x{h}"));
                let mode = now.1.map_or("?", |full| if full { "fullscreen" } else { "windowed" });
                ok(ULTRAWIDE, &format!("screen now {size} {mode}"));
                self.watch = None;
            }
        }
    }
}

/// Lua for `ui16x9`, through the game's own safe area (disassembled, 4.8). Every UI canvas scales 1920x1080 with
/// `Expand`, so at 32:9 it is 3840x1080 and edge-anchored content spreads. Moving the UI cameras' viewport does not
/// work: the canvases keep sizing from `Screen` and clicks map wrongly. Instead:
/// - `XResolutionManager.SetSafeArea(Rect px)` stores `CustomSafeArea`; `RefreshResolution()` re-runs `Init`, which
///   copies it into `OriginSafeArea` (it falls back to `Screen.safeArea` when the custom rect is `Rect.zero`).
/// - `XAppPlatBridge.GetNotchSize()` is `(OriginWidth - OriginSafeArea.width) / 2`; `XUiSafeAreaAdapter.SetRectTransform`
///   insets every `SafeAreaContentPane` by that (converted to canvas units) plus the player's `SpecialScreenOff`.
///   `SetSpecialScreenOff` (via `XSetManager.SetScreenOff()`, the saved slider) notifies all live adapters.
/// - `XCustomUi.RefreshResolution()` recomputes the fight HUD's `SafeScreenArea` from `OriginSafeArea` (1920x1080
///   instead of 3840x1080) and re-adapts the HUD layout.
/// Full-screen backgrounds outside the safe panes stay full width by design. Returns "" when nothing changed, so it
/// can run every tick; `XResolutionManager.Update` keeps the custom rect across resolution changes.
fn ui_fit_chunk(want: bool, ui_scale: Option<f32>) -> String {
    let scale = ui_scale.unwrap_or(1.0);
    format!(r#"
local CS = rawget(_G, "CS")
local data = rawget(_G, "XDataCenter")
if not (CS and data and rawget(data, "SetManager")) then return "{PENDING}" end
local U = CS.UnityEngine
local res = CS.XResolutionManager
local sw, sh = U.Screen.width, U.Screen.height
local wide = {want} and sw * 9 > sh * 16 + 9
local w = wide and math.floor(sh * 16 / 9 + 0.5) or sw
local x = wide and math.floor((sw - w) / 2) or 0
local cur = res.OriginSafeArea
local changed = math.abs(cur.x - x) >= 1 or math.abs(cur.width - w) >= 1 or math.abs(cur.height - sh) >= 1
if changed then
    -- Keep a real physical safe area: scaling this rect can make its width negative.
    -- The fight HUD and other consumers use it directly, unlike the 1080-based adapter.
    res.SetSafeArea(wide and U.Rect(x, 0, w, sh) or U.Rect(0, 0, 0, 0))
    res.RefreshResolution()
    local hud = CS.XCustomUi.Instance
    if hud then hud:RefreshResolution() end
    data.SetManager.SetScreenOff()
end
local typeof = rawget(_G, "typeof")
if not typeof then return "{PENDING}" end
local adapters = U.Resources.FindObjectsOfTypeAll(typeof(CS.XUiSafeAreaAdapter))
local seen = {{}}
for i = 0, adapters.Length - 1 do
    local adapter = adapters[i]
    local t = adapter.transform
    local root = false
    while t do
        if t.name == "UiRoot" then root = true; break end
        t = t.parent
    end
    if root and adapter.gameObject.scene:IsValid() then
        local rect = adapter:GetComponent(typeof(U.RectTransform))
        local id = rect and rect:GetInstanceID()
        if id and not seen[id] then
            seen[id] = true
            -- Reset the game's notch + saved SpecialScreenOff first: never compound.
            adapter:UpdateSpecialScreenOff()
            local canvas = adapter:GetComponentInParent(typeof(U.Canvas))
            local scaler = canvas and canvas.rootCanvas:GetComponent(typeof(U.UI.CanvasScaler))
            if wide and scaler and scaler.uiScaleMode == U.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize then
                local ref = scaler.referenceResolution
                if math.abs(ref.x - 1920 / {scale}) < 0.1 and math.abs(ref.y - 1080 / {scale}) < 0.1 then
                    -- Adapter uses notchPx * 1080 / screenHeight; actual reference is
                    -- 1080/f, so add notchCanvas*(1/f-1), leaving saved slider off intact.
                    local delta = CS.XAppPlatBridge.GetNotchSize() * 1080 / sh * (1 / {scale} - 1)
                    local lo, hi = rect.offsetMin, rect.offsetMax
                    rect.offsetMin = U.Vector2(lo.x + delta, lo.y)
                    rect.offsetMax = U.Vector2(hi.x - delta, hi.y)
                end
            end
        end
    end
end
if not changed then return "" end
local now = res.OriginSafeArea
return string.format("safe area %.0f,%.0f %.0fx%.0f on %dx%d (was %.0f,%.0f %.0fx%.0f)", now.x, now.y, now.width, now.height, sw, sh, cur.x, cur.y, cur.width, cur.height)
"#)
}

const UI16X9: &str = "ui16x9";

/// `ui16x9 = on|off`: checked every tick while on (the safe area must follow resolution changes, e.g. ultrawide on and
/// off), once more when switched off to give the game back its own safe area.
#[derive(Default)]
pub(super) struct UiFit {
    applied: bool,
    seen: u64,
    scale: Option<f32>,
}

impl UiFit {
    /// Called every worker tick; problems are appended to `errors` under `ui16x9`.
    pub(super) fn update(&mut self, bridge: &Result<LuaBridge, String>, want: bool, ui_scale: Option<f32>, errors: &mut Vec<(String, String)>) {
        let bridge = match bridge {
            Ok(bridge) => bridge,
            Err(message) => {
                if want {
                    errors.push((UI16X9.into(), message.clone()));
                }
                return;
            }
        };
        if want || self.applied || ui_scale != self.scale {
            bridge.queue(UI16X9, ui_fit_chunk(want, ui_scale));
        }
        self.applied = want;
        self.scale = ui_scale;
        if let Some((seq, result)) = bridge.outcome(UI16X9) {
            if seq != self.seen {
                self.seen = seq;
                if let Ok(message) = &result {
                    if !message.is_empty() {
                        ok(UI16X9, message);
                    }
                }
            }
            if let Err(message) = result {
                errors.push((UI16X9.into(), message));
            }
        }
    }
}

/// Lua for `uifov`: widens the vertical field of view of the UI-scene cameras of the War Zone map
/// (`UiArenaNew3d`) and team select (`UiNewRoomSingle`) only; the lobby and character screens use the same camera
/// names (`UiNearCamera`, `UiFarCamera`, `UiCamera`) under other scene prefabs and are left alone, so cameras are
/// matched by the scene prefab they sit under. The game keeps their vertical FOV fixed at every aspect
/// (runtime probe, 4.8: 22.62 on the War Zone map, 26.99 on team select at both 16:9 and 32:9). Cameras with a
/// `CinemachineBrain` are driven by the active virtual camera every frame, so its `m_Lens.FieldOfView` is changed
/// instead of the camera's. The game's own value is remembered per camera / virtual camera and re-read whenever the
/// game changes it, so animated FOV changes keep working and nothing compounds; `scale = nil` restores the originals.
/// The widening keeps the projection consistent: `2 atan(tan(fov/2) * scale)`. Returns "" when nothing changed.
fn ui_fov_chunk(scale: Option<f32>) -> String {
    let scale = scale.map_or("nil".to_owned(), |s| format!("{s}"));
    format!(r#"
local CS = rawget(_G, "CS")
local pkg = rawget(_G, "package")
local typeof = rawget(_G, "typeof")
if not (CS and pkg and typeof) then return "{PENDING}" end
local st = pkg.loaded.ascnet_uifov
if not st then st = {{ orig = {{}}, set = {{}} }}; pkg.loaded.ascnet_uifov = st end
local scale = {scale}
local U = CS.UnityEngine
local ok_b, brainT = pcall(function() return typeof(CS.Cinemachine.CinemachineBrain) end)
local ok_v, vcamT = pcall(function() return typeof(CS.Cinemachine.CinemachineVirtualCamera) end)
if not (ok_b and ok_v) then brainT, vcamT = nil, nil end
local names = {{ UiNearCamera = true, UiFarCamera = true, UiCamera = true }}
-- Scene prefabs (under `.../UiRoots/`) whose cameras are widened: War Zone map and team select.
local scenes = {{ "^UiArenaNew3d", "^UiNewRoomSingle" }}
local function targeted(t)
    for _ = 1, 6 do
        if not t then return false end
        for _, pattern in ipairs(scenes) do
            if string.find(t.name, pattern) then return true end
        end
        t = t.parent
    end
    return false
end
-- `put` keeps a setter per widened camera: screens disable their cameras when closed but reuse them when reopened,
-- so switching off must also restore cameras that are not active right now.
st.put = st.put or {{}}
local changed = {{}}
local seen = {{}}
local function apply(key, label, s, get, put)
    seen[key] = true
    local cur = get()
    local last = st.set[key]
    if last == nil or math.abs(cur - last) > 0.01 then st.orig[key] = cur end
    local target = s and math.deg(2 * math.atan(math.tan(math.rad(st.orig[key]) / 2) * s)) or st.orig[key]
    if math.abs(cur - target) > 0.01 then
        put(target)
        changed[#changed + 1] = string.format("%s %.2f->%.2f", label, cur, target)
    end
    st.set[key] = s and target or nil
    st.put[key] = s and put or nil
end
local cams = U.Camera.allCameras
for i = 0, cams.Length - 1 do
    local cam = cams[i]
    if names[cam.name] and not cam.orthographic then
        local s = targeted(cam.transform) and scale or nil
        local brain = brainT and cam:GetComponent(brainT)
        local active = brain and brain.ActiveVirtualCamera
        local go = active and active.VirtualCameraGameObject
        local vcam = go and go:GetComponent(vcamT)
        if vcam then
            apply("v" .. vcam:GetInstanceID(), cam.name .. "/" .. go.name, s,
                function() return vcam.m_Lens.FieldOfView end,
                function(x) local lens = vcam.m_Lens; lens.FieldOfView = x; vcam.m_Lens = lens end)
        elseif not brain then
            apply("c" .. cam:GetInstanceID(), cam.name, s,
                function() return cam.fieldOfView end,
                function(x) cam.fieldOfView = x end)
        end
    end
end
if not scale then
    for key, put in pairs(st.put) do
        if not seen[key] and st.orig[key] and pcall(put, st.orig[key]) then
            changed[#changed + 1] = string.format("%s (inactive) ->%.2f", key, st.orig[key])
        end
    end
    st.orig, st.set, st.put = {{}}, {{}}, {{}}
end
if #changed == 0 then return "" end
return table.concat(changed, ", ")
"#)
}

const UIFOV: &str = "uifov";

/// `uifov = <scale>`: checked every tick while set (UI-scene cameras are created per screen), once more when cleared
/// to give the cameras their own field of view back.
#[derive(Default)]
pub(super) struct UiFov {
    applied: bool,
    seen: u64,
}

impl UiFov {
    /// Called every worker tick; problems are appended to `errors` under `uifov`.
    pub(super) fn update(&mut self, bridge: &Result<LuaBridge, String>, scale: Option<f32>, errors: &mut Vec<(String, String)>) {
        let bridge = match bridge {
            Ok(bridge) => bridge,
            Err(message) => {
                if scale.is_some() {
                    errors.push((UIFOV.into(), message.clone()));
                }
                return;
            }
        };
        if scale.is_some() || self.applied {
            bridge.queue(UIFOV, ui_fov_chunk(scale));
        }
        self.applied = scale.is_some();
        if let Some((seq, result)) = bridge.outcome(UIFOV) {
            if seq != self.seen {
                self.seen = seq;
                if let Ok(message) = &result {
                    if !message.is_empty() {
                        ok(UIFOV, message);
                    }
                }
            }
            if let Err(message) = result {
                errors.push((UIFOV.into(), message));
            }
        }
    }
}

fn ui_scale_chunk(scale: Option<f32>) -> String {
    let scale = scale.unwrap_or(1.0);
    format!(r#"
local CS, pkg, typeof = rawget(_G, "CS"), rawget(_G, "package"), rawget(_G, "typeof")
if not (CS and pkg and typeof) then return "{PENDING}" end
local U = CS.UnityEngine
local st = pkg.loaded.ascnet_uiscale
if not st then st = {{ canvases = {{}} }}; pkg.loaded.ascnet_uiscale = st end
local scalers = U.Resources.FindObjectsOfTypeAll(typeof(U.UI.CanvasScaler))
local changed = 0
for i = 0, scalers.Length - 1 do
    local scaler = scalers[i]
    local canvas = scaler:GetComponent(typeof(U.Canvas))
    local t = scaler.transform
    local root = false
    while t do
        if t.name == "UiRoot" then root = true; break end
        t = t.parent
    end
    if root and canvas and canvas.isRootCanvas and scaler.gameObject.scene:IsValid()
        and scaler.uiScaleMode == U.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize then
        local id = scaler:GetInstanceID()
        local ref = scaler.referenceResolution
        if st.canvases[id] or (math.abs(ref.x - 1920) < 0.1 and math.abs(ref.y - 1080) < 0.1) then
            st.canvases[id] = scaler
            local x, y = 1920 / {scale}, 1080 / {scale}
            if math.abs(ref.x - x) > 0.1 or math.abs(ref.y - y) > 0.1 then
                scaler.referenceResolution = U.Vector2(x, y)
                changed = changed + 1
            end
        end
    end
end
if {scale} == 1 then
    for id, scaler in pairs(st.canvases) do
        pcall(function() scaler.referenceResolution = U.Vector2(1920, 1080) end)
        st.canvases[id] = nil
    end
end
return changed > 0 and string.format("%d root canvases, scale %.3f", changed, {scale}) or ""
"#)
}

#[derive(Default)]
pub(super) struct UiScale {
    applied: bool,
    seen: u64,
}

impl UiScale {
    pub(super) fn update(&mut self, bridge: &Result<LuaBridge, String>, scale: Option<f32>, errors: &mut Vec<(String, String)>) {
        let bridge = match bridge {
            Ok(bridge) => bridge,
            Err(message) => {
                if scale.is_some() { errors.push(("uiscale".into(), message.clone())); }
                return;
            }
        };
        if scale.is_some() || self.applied {
            bridge.queue("uiscale", ui_scale_chunk(scale));
        }
        self.applied = scale.is_some();
        if let Some((seq, result)) = bridge.outcome("uiscale") {
            if seq != self.seen {
                self.seen = seq;
                if let Ok(message) = &result {
                    if !message.is_empty() { ok("uiscale", message); }
                }
            }
            if let Err(message) = result { errors.push(("uiscale".into(), message)); }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn chunks_keep_the_pending_contract() {
        assert!(LUA_PREAMBLE.contains(&format!("return \"{PENDING}\"")));
        assert!(LUA_ON.contains("ascnet = true") && LUA_OFF.contains("list[i].ascnet"));
        assert!(ui_fit_chunk(true, Some(1.25)).contains("(1 / 1.25 - 1)"));
        assert!(ui_scale_chunk(Some(1.25)).contains("1920 / 1.25"));
        assert!(ui_scale_chunk(None).contains("1920 / 1"));
    }
}
