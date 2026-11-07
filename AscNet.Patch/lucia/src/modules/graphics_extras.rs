//! `fps` and `nofade` from `ascnet-graphics.ini`, applied by the graphics worker thread every tick (no thread of
//! their own). Look-ups are resolved once at worker start; each option has its own lookup failure, reported under
//! `status.errors` (`fps` / `nofade`) while the rest keeps working. Turning an option off restores the game's own
//! values, which are recorded before the first write and whenever game code changes them while we hold an override.
//!
//! - FPS: `Application.targetFrameRate = n` and `QualitySettings.vSyncCount = 0` (Unity ignores targetFrameRate
//!   while vsync paces the frame, and the game's own "UseVSync" option sets it).
//! - No fade: `XCameraDitherDetector.UseCameraDither = false` and `XNpcDither.SetPlayerSelfDitherParameter(OPAQUE)`.
//!   The game's own parameter is the private static `XNpcDither.playerSelfDitherParameter`, read before the first write.
//!   `XDitherHelper.GlobalEnableDitherState` is deliberately untouched.
use std::ffi::{c_void, CString};

use anyhow::{bail, Context, Result};

use super::graphics_nofade_params::{PlayerSelfDither, OPAQUE};
use crate::modules::il2cpp::{self, Il2cpp, FLAG_STATIC};

const FPS_UNAFFECTED: &str = "the game is unaffected and keeps its own frame rate";
const FADE_UNAFFECTED: &str = "the game is unaffected and the camera fade stays at its default";
const UNITY: &str = "UnityEngine";
const DETECTOR_CLASS: &str = "XCameraDitherDetector";
const DETECTOR_FIELD: &str = "UseCameraDither";
const DITHER_CLASS: &str = "XNpcDither";
const DITHER_METHOD: &str = "SetPlayerSelfDitherParameter";
const DITHER_FIELD: &str = "playerSelfDitherParameter";

/// Logs only when `good` flips (the steady state stays silent at the 0.5 s tick).
fn note(last: &mut Option<bool>, good: bool, what: &str, detail: &str, problem: &str, consequence: &str) {
    if *last == Some(good) {
        return;
    }
    *last = Some(good);
    if good {
        il2cpp::ok(what, detail);
    } else {
        il2cpp::failed(what, problem, consequence);
    }
}

#[derive(Clone, Copy)]
struct FpsCalls {
    set_rate: *mut c_void,
    get_rate: *mut c_void,
    /// Optional: without it the unlock still works for players who run with vsync off.
    vsync: Option<(*mut c_void, *mut c_void)>,
}

pub(super) struct Fps {
    calls: Result<FpsCalls, String>,
    /// The game's own (targetFrameRate, vSyncCount) while we override.
    game: (Option<i32>, Option<i32>),
    /// What we last wrote; a different value read back means game code changed it.
    written: Option<(i32, i32)>,
    state: Option<bool>,
}

impl Fps {
    pub(super) unsafe fn new(il: &Il2cpp, assemblies: &[*mut c_void]) -> Self {
        let calls = Self::lookup(il, assemblies).map_err(|error| {
            let message = format!("{error:#}");
            il2cpp::failed("lookup FPS unlock", &message, FPS_UNAFFECTED);
            message
        });
        Self { calls, game: (None, None), written: None, state: None }
    }

    unsafe fn lookup(il: &Il2cpp, assemblies: &[*mut c_void]) -> Result<FpsCalls> {
        let (application, image) = il.find_class(assemblies, UNITY, "Application")
            .with_context(|| format!("class `{UNITY}.Application` not found in any of {} loaded assemblies", assemblies.len()))?;
        il2cpp::ok("lookup UnityEngine.Application", &format!("assembly={image}"));
        let set_rate = il.static_method(application, "Application", "set_targetFrameRate", &["System.Int32"])?;
        let get_rate = il.static_method(application, "Application", "get_targetFrameRate", &[])?;
        il2cpp::ok("lookup Application.set_targetFrameRate(Int32)/get_targetFrameRate()", "static property accessors");
        (il.class_init)(application);

        let vsync = match il.find_class(assemblies, UNITY, "QualitySettings") {
            Some((class, image)) => {
                let accessors = il.static_method(class, "QualitySettings", "get_vSyncCount", &[])
                    .and_then(|get| Ok((get, il.static_method(class, "QualitySettings", "set_vSyncCount", &["System.Int32"])?)));
                match accessors {
                    Ok(accessors) => {
                        il2cpp::ok("lookup UnityEngine.QualitySettings.vSyncCount", &format!("assembly={image}"));
                        (il.class_init)(class);
                        Some(accessors)
                    }
                    Err(error) => {
                        il2cpp::failed("lookup QualitySettings.vSyncCount", &format!("{error:#}"), "a game-enabled vsync would still cap the frame rate");
                        None
                    }
                }
            }
            None => {
                il2cpp::failed("lookup UnityEngine.QualitySettings", "class not found", "a game-enabled vsync would still cap the frame rate");
                None
            }
        };
        Ok(FpsCalls { set_rate, get_rate, vsync })
    }

    /// Applies or releases the override; returns the (targetFrameRate, vSyncCount) read afterwards.
    pub(super) unsafe fn update(&mut self, il: &Il2cpp, want: Option<i32>, errors: &mut Vec<(String, String)>) -> (Option<i32>, Option<i32>) {
        let calls = match &self.calls {
            Ok(calls) => *calls,
            Err(message) => {
                if want.is_some() {
                    errors.push(("fps".into(), message.clone()));
                }
                return (None, None);
            }
        };
        let read = || (il.call_get_int(calls.get_rate), calls.vsync.and_then(|(get, _)| il.call_get_int(get)));
        if let Some(fps) = want {
            let (rate, vsync) = read();
            let before = self.game;
            if let Some(rate) = rate.filter(|&r| self.written.map_or(true, |w| r != w.0)) {
                self.game.0 = Some(rate);
            }
            if let Some(vsync) = vsync.filter(|&v| self.written.map_or(true, |w| v != w.1)) {
                self.game.1 = Some(vsync);
            }
            if self.game != before {
                il2cpp::ok("FPS unlock", &format!("targetFrameRate={fps}, vSyncCount=0; the game's own values are targetFrameRate={}, vSyncCount={}", show(self.game.0), show(self.game.1)));
            }
            let rate_ok = rate == Some(fps) || (il.call_set_int(calls.set_rate, fps) && il.call_get_int(calls.get_rate) == Some(fps));
            let vsync_ok = calls.vsync.map_or(true, |(get, set)| vsync == Some(0) || (il.call_set_int(set, 0) && il.call_get_int(get) == Some(0)));
            self.written = Some((fps, 0));
            note(&mut self.state, rate_ok && vsync_ok, "apply FPS unlock", &format!("targetFrameRate={fps}, vSyncCount=0 held every 0.5 s"),
                "a call threw a managed exception or the value read back differently", FPS_UNAFFECTED);
            if !(rate_ok && vsync_ok) {
                errors.push(("fps".into(), "could not set Application.targetFrameRate / QualitySettings.vSyncCount (see the log)".into()));
            }
        } else if self.written.take().is_some() {
            let rate_ok = self.game.0.map_or(true, |rate| il.call_set_int(calls.set_rate, rate));
            let vsync_ok = match (self.game.1, calls.vsync) {
                (Some(count), Some((_, set))) => il.call_set_int(set, count),
                _ => true,
            };
            let restored = format!("targetFrameRate={}, vSyncCount={}", show(self.game.0), show(self.game.1));
            if rate_ok && vsync_ok {
                il2cpp::ok("FPS unlock", &format!("removed, restored {restored}"));
            } else {
                il2cpp::failed("FPS unlock", &format!("removed but restoring {restored} threw a managed exception"), "the game may keep the unlocked values until restart");
            }
            self.game = (None, None);
            self.state = None;
        }
        read()
    }
}

fn show(value: Option<i32>) -> String {
    value.map_or("?".into(), |v| v.to_string())
}

#[derive(Clone, Copy)]
struct FadeCalls {
    camera: *mut c_void,
    /// (`SetPlayerSelfDitherParameter`, static `playerSelfDitherParameter` field); missing = only the camera fade.
    dither: Option<(*mut c_void, *mut c_void)>,
}

pub(super) struct NoFade {
    calls: Result<FadeCalls, String>,
    on: bool,
    /// The game's own values while we override.
    game_camera: Option<bool>,
    game_param: Option<PlayerSelfDither>,
    camera_state: Option<bool>,
    param_state: Option<bool>,
}

impl NoFade {
    pub(super) unsafe fn new(il: &Il2cpp, assemblies: &[*mut c_void]) -> Self {
        let calls = Self::lookup(il, assemblies).map_err(|error| {
            let message = format!("{error:#}");
            il2cpp::failed("lookup no camera fade", &message, FADE_UNAFFECTED);
            message
        });
        Self { calls, on: false, game_camera: None, game_param: None, camera_state: None, param_state: None }
    }

    unsafe fn lookup(il: &Il2cpp, assemblies: &[*mut c_void]) -> Result<FadeCalls> {
        let (detector, image) = il.find_class(assemblies, "", DETECTOR_CLASS)
            .with_context(|| format!("class `{DETECTOR_CLASS}` (global namespace) not found in any of {} loaded assemblies; this client build renamed it", assemblies.len()))?;
        il2cpp::ok(&format!("lookup {DETECTOR_CLASS}"), &format!("assembly={image}"));
        let camera = (il.class_field)(detector, CString::new(DETECTOR_FIELD)?.as_ptr());
        if camera.is_null() || (il.field_flags)(camera) & FLAG_STATIC == 0 {
            bail!("`{DETECTOR_CLASS}.{DETECTOR_FIELD}` is missing or not static in this build")
        }
        let field_type = il.type_name_of((il.field_type)(camera));
        if field_type != "System.Boolean" {
            bail!("`{DETECTOR_CLASS}.{DETECTOR_FIELD}` has type `{field_type}`, expected System.Boolean")
        }
        il2cpp::ok(&format!("lookup {DETECTOR_CLASS}.{DETECTOR_FIELD}"), "static System.Boolean");
        // The static initialiser sets UseCameraDither=true; it must have run before we read or write it.
        (il.class_init)(detector);

        // The camera flag is independent of the player-self parameter; keep going if only XNpcDither is missing.
        let dither = match il.find_class(assemblies, "", DITHER_CLASS) {
            Some((class, image)) => {
                il2cpp::ok(&format!("lookup {DITHER_CLASS}"), &format!("assembly={image}"));
                let method = (il.class_method)(class, CString::new(DITHER_METHOD)?.as_ptr(), 1);
                let param = if method.is_null() { String::new() } else { il.type_name_of((il.param)(method, 0)) };
                let field = (il.class_field)(class, CString::new(DITHER_FIELD)?.as_ptr());
                let field_type = if field.is_null() { String::new() } else { il.type_name_of((il.field_type)(field)) };
                if method.is_null() || (il.param_count)(method) != 1 || (il.method_flags)(method, std::ptr::null_mut()) & FLAG_STATIC == 0 || !param.ends_with("PlayerSelfDitherParameter") {
                    il2cpp::failed(&format!("lookup {DITHER_CLASS}.{DITHER_METHOD}(PlayerSelfDitherParameter)"), "static one-argument method with a PlayerSelfDitherParameter parameter not found", "only the camera fade is disabled");
                    None
                } else if field.is_null() || (il.field_flags)(field) & FLAG_STATIC == 0 || !field_type.ends_with("PlayerSelfDitherParameter") {
                    il2cpp::failed(&format!("lookup {DITHER_CLASS}.{DITHER_FIELD}"), "static PlayerSelfDitherParameter field (the game's own value to restore) not found", "only the camera fade is disabled");
                    None
                } else {
                    il2cpp::ok(&format!("lookup {DITHER_CLASS}.{DITHER_METHOD}({param})"), &format!("static, 1 parameter; game value read from {DITHER_FIELD}"));
                    (il.class_init)(class);
                    Some((method, field))
                }
            }
            None => {
                il2cpp::failed(&format!("lookup {DITHER_CLASS}"), &format!("class (global namespace) not found in any of {} loaded assemblies", assemblies.len()), "only the camera fade is disabled");
                None
            }
        };
        Ok(FadeCalls { camera, dither })
    }

    pub(super) unsafe fn update(&mut self, il: &Il2cpp, want: bool, errors: &mut Vec<(String, String)>) {
        let calls = match &self.calls {
            Ok(calls) => *calls,
            Err(message) => {
                if want {
                    errors.push(("nofade".into(), message.clone()));
                }
                return;
            }
        };
        let set_camera = |value: bool| -> bool {
            let mut flag = value as u8;
            (il.static_set)(calls.camera, (&mut flag as *mut u8).cast());
            read_camera(il, calls.camera) == value
        };
        let set_param = |method: *mut c_void, mut value: PlayerSelfDither| -> bool {
            let mut args = [(&mut value as *mut PlayerSelfDither).cast::<c_void>()];
            let mut exception = std::ptr::null_mut();
            (il.invoke)(method, std::ptr::null_mut(), args.as_mut_ptr(), &mut exception);
            exception.is_null()
        };
        if want {
            let camera = read_camera(il, calls.camera);
            // Record the game's value before our first write, and again whenever the game resets the flag.
            let mut first = false;
            if !self.on || camera {
                first = self.game_camera != Some(camera);
                self.game_camera = Some(camera);
            }
            let camera_ok = !camera || set_camera(false);
            note(&mut self.camera_state, camera_ok, &format!("write {DETECTOR_CLASS}.{DETECTOR_FIELD}=false"), "held every 0.5 s", "read back true after the write", FADE_UNAFFECTED);
            let mut param_ok = true;
            if let Some((method, field)) = calls.dither {
                let current = read_param(il, field);
                if !self.on || current != OPAQUE {
                    first |= self.game_param != Some(current);
                    self.game_param = Some(current);
                }
                if current != OPAQUE {
                    param_ok = set_param(method, OPAQUE);
                }
                note(&mut self.param_state, param_ok, &format!("apply {DITHER_CLASS}.{DITHER_METHOD}(opaque)"), "held every 0.5 s", "the call threw a managed exception", FADE_UNAFFECTED);
            }
            if first {
                il2cpp::ok("no camera fade", &format!("applied; the game's own values are {DETECTOR_FIELD}={}, {DITHER_FIELD}={}", self.game_camera.map_or("?".into(), |v| v.to_string()), self.game_param.map_or("n/a".into(), |p| format!("{p:?}"))));
            }
            self.on = true;
            if !(camera_ok && param_ok) {
                errors.push(("nofade".into(), "could not disable the camera fade (see the log)".into()));
            }
        } else if self.on {
            self.on = false;
            let camera_ok = self.game_camera.map_or(true, |value| set_camera(value));
            let param_ok = match (calls.dither, self.game_param) {
                (Some((method, _)), Some(value)) => set_param(method, value),
                _ => true,
            };
            let restored = format!("{DETECTOR_FIELD}={}, {DITHER_FIELD}={}", self.game_camera.map_or("?".into(), |v| v.to_string()), self.game_param.map_or("n/a".into(), |p| format!("{p:?}")));
            if camera_ok && param_ok {
                il2cpp::ok("no camera fade", &format!("removed, restored {restored}"));
            } else {
                il2cpp::failed("no camera fade", &format!("removed but restoring {restored} failed"), "the game may keep the camera fade disabled until restart");
            }
            self.game_camera = None;
            self.game_param = None;
            self.camera_state = None;
            self.param_state = None;
        }
    }
}

unsafe fn read_camera(il: &Il2cpp, field: *mut c_void) -> bool {
    let mut value = 1u8;
    (il.static_get)(field, (&mut value as *mut u8).cast());
    value != 0
}

unsafe fn read_param(il: &Il2cpp, field: *mut c_void) -> PlayerSelfDither {
    let mut value = OPAQUE;
    (il.static_get)(field, (&mut value as *mut PlayerSelfDither).cast());
    value
}
