//! Write side of the graphics options: the `renderscale` detours and the `<Setting>.<Field>` overrides, both driven
//! by `Desired` every tick, plus the catalogue of every quality field for the overlay.
use std::{
    ffi::{c_void, CString},
    ptr::null_mut,
    sync::atomic::{AtomicBool, AtomicU32, Ordering::Relaxed},
};

use anyhow::{anyhow, bail, Result};
use ilhook::x64::Registers;

use super::graphics_live::{FieldInfo, FieldKind, SettingInfo};
use super::graphics_params::{bool_value, check_divisor, float_value, floats_value, int_value, ints_value, Override, Typed};
use super::{failed, ok, Value, Walker, FIELD_LITERAL, T_BOOLEAN, T_CLASS, T_I4, T_R4, T_SZARRAY, T_VALUETYPE};
use crate::interceptor::Interceptor;
use crate::modules::il2cpp::{Il2cpp, FLAG_STATIC};
use crate::util::{c_string, game_assembly_code, readable};

/// `SCALE_BITS` value meaning "no override": the detours leave the game's arguments alone (a quiet NaN).
const PASSTHROUGH: u32 = 0x7FC0_0000;
/// Scale substituted into every `SetupExtraRenderScale` call (f32 bits), or `PASSTHROUGH`.
static SCALE_BITS: AtomicU32 = AtomicU32::new(PASSTHROUGH);
/// Last scale the game itself asked for (f32 bits); NaN until the first call.
static REQUESTED_BITS: AtomicU32 = AtomicU32::new(u32::MAX);
/// Last `EnableExtraRenderScale` argument the game passed (0/1); `u32::MAX` until the first call.
static REQUESTED_ENABLE: AtomicU32 = AtomicU32::new(u32::MAX);
/// `EnableExtraRenderScale(false)` calls turned into `true`.
static DISABLES: AtomicU32 = AtomicU32::new(0);
/// Set while the worker itself calls the hooked methods, so those calls are not taken for the game's.
static OWN_CALL: AtomicBool = AtomicBool::new(false);
/// Set by the `Application.Internal_ApplicationQuit` detour; from then on the worker must not call into Unity.
pub(super) static QUITTING: AtomicBool = AtomicBool::new(false);

unsafe extern "win64" fn on_application_quit(_: *mut Registers, _: usize) {
    QUITTING.store(true, Relaxed);
}

/// Detours `UnityEngine.Application.Internal_ApplicationQuit()`, which Unity calls when it starts quitting.
pub(super) unsafe fn watch_quit(il: &Il2cpp, assemblies: &[*mut c_void]) -> Result<Interceptor> {
    let (class, _) = il.find_class(assemblies, "UnityEngine", "Application").ok_or_else(|| anyhow!("UnityEngine.Application not found"))?;
    let method = il.static_method(class, "Application", "Internal_ApplicationQuit", &[])?;
    let at = code(method, "Internal_ApplicationQuit")?;
    let mut hook = Interceptor::new();
    hook.attach(at, on_application_quit)?;
    ok("exit detection", &format!("UnityEngine.Application.Internal_ApplicationQuit at 0x{at:X}"));
    Ok(hook)
}

unsafe extern "win64" fn on_setup_extra_render_scale(registers: *mut Registers, _: usize) {
    // static void SetupExtraRenderScale(float renderScale): Win64 passes the float in xmm0.
    if !OWN_CALL.load(Relaxed) {
        REQUESTED_BITS.store((*registers).xmm0 as u32, Relaxed);
    }
    let bits = SCALE_BITS.load(Relaxed);
    if bits != PASSTHROUGH {
        (*registers).xmm0 = ((*registers).xmm0 & !0xFFFF_FFFF) | u128::from(bits);
    }
}

unsafe extern "win64" fn on_enable_extra_render_scale(registers: *mut Registers, _: usize) {
    // static void EnableExtraRenderScale(bool Enable): the bool is in cl. While off, the pipeline falls back to the
    // quality level's own scale (capped by its max vertical resolution), so it is kept on while a scale is set.
    let enable = (*registers).rcx as u8;
    if !OWN_CALL.load(Relaxed) {
        REQUESTED_ENABLE.store(u32::from(enable != 0), Relaxed);
    }
    if SCALE_BITS.load(Relaxed) != PASSTHROUGH {
        if enable == 0 && !OWN_CALL.load(Relaxed) {
            DISABLES.fetch_add(1, Relaxed);
        }
        (*registers).rcx = ((*registers).rcx & !0xFF) | 1;
    }
}

pub(super) unsafe fn code(method: *mut c_void, name: &str) -> Result<usize> {
    if !readable(method as usize, std::mem::size_of::<usize>()) {
        bail!("MethodInfo of `{name}` is not readable memory")
    }
    let target = *(method as *const usize);
    if !game_assembly_code(target) {
        bail!("`{name}` methodPointer 0x{target:X} is not executable code inside GameAssembly.dll")
    }
    Ok(target)
}

unsafe fn invoke(il: &Il2cpp, method: *mut c_void, argument: *mut c_void) -> Result<()> {
    let mut args = [argument];
    let mut exception = null_mut();
    OWN_CALL.store(true, Relaxed);
    (il.invoke)(method, null_mut(), args.as_mut_ptr(), &mut exception);
    OWN_CALL.store(false, Relaxed);
    if !exception.is_null() {
        bail!("the render-scale call threw a managed exception")
    }
    Ok(())
}

struct Hooks {
    /// Owns the hook points; dropping one restores the original code, so this lives as long as the worker loop.
    _hooks: Interceptor,
    setup: *mut c_void,
    enable: *mut c_void,
}

/// The `renderscale` option. The detours are installed on the first scale and stay; with no scale set they pass the
/// game's own arguments through.
#[derive(Default)]
pub(super) struct RenderScale {
    hooks: Option<Hooks>,
    /// Install failure and the scale that triggered it; retried when the wanted scale changes.
    broken: Option<(f32, String)>,
    get_render_scale: Option<*mut c_void>,
    get_tried: bool,
    applied: Option<f32>,
    /// Problem applying `applied`; cleared when the wanted scale changes.
    error: Option<String>,
    requested: u32,
    disables: u32,
    reported: Option<bool>,
    /// `GetRenderScale()` as of the last `update`.
    pub(super) effective: Option<f32>,
}

impl RenderScale {
    /// Last scale the game itself asked for; `None` until the detours have seen a call.
    pub(super) fn game_scale() -> Option<f32> {
        Some(f32::from_bits(REQUESTED_BITS.load(Relaxed))).filter(|s| !s.is_nan())
    }

    unsafe fn install(il: &Il2cpp, urp: *mut c_void) -> Result<Hooks> {
        const CLASS: &str = "RenderPIplineQualityManger";
        let setup = il.static_method(urp, CLASS, "SetupExtraRenderScale", &["System.Single"])?;
        let enable = il.static_method(urp, CLASS, "EnableExtraRenderScale", &["System.Boolean"])?;
        let (setup_code, enable_code) = (code(setup, "SetupExtraRenderScale")?, code(enable, "EnableExtraRenderScale")?);
        let mut hooks = Interceptor::new();
        hooks.attach(setup_code, on_setup_extra_render_scale)?;
        hooks.attach(enable_code, on_enable_extra_render_scale)?;
        ok("render scale detours", &format!("RenderPIplineQualityManger.SetupExtraRenderScale at 0x{setup_code:X}, EnableExtraRenderScale at 0x{enable_code:X}"));
        Ok(Hooks { _hooks: hooks, setup, enable })
    }

    /// Applies now; the game only calls these on scene and quality changes. Both calls pass through the detours.
    unsafe fn reapply(il: &Il2cpp, hooks: &Hooks, want: Option<f32>) -> Result<()> {
        let (scale, enable) = match want {
            Some(scale) => (Some(scale), Some(1u8)),
            None => (Self::game_scale(), u8::try_from(REQUESTED_ENABLE.load(Relaxed)).ok()),
        };
        if let Some(mut on) = enable {
            invoke(il, hooks.enable, (&mut on as *mut u8).cast())?;
        }
        if let Some(mut value) = scale {
            invoke(il, hooks.setup, (&mut value as *mut f32).cast())?;
        }
        Ok(())
    }

    /// Brings the detours and the pipeline in line with `want`, logs what the game asked for and confirms the pipeline
    /// reports the chosen scale; silent in steady state. Problems are appended to `errors` under `renderscale`.
    pub(super) unsafe fn update(&mut self, il: &Il2cpp, urp: Option<*mut c_void>, want: Option<f32>, scene: &dyn Fn() -> String, errors: &mut Vec<(String, String)>) {
        if let (Some(scale), None) = (want, &self.hooks) {
            if self.broken.as_ref().is_none_or(|(tried, _)| *tried != scale) {
                self.broken = None;
                match urp.ok_or_else(|| anyhow!("RenderPIplineQualityManger is missing in this build")).and_then(|urp| Self::install(il, urp)) {
                    Ok(hooks) => {
                        self.requested = REQUESTED_BITS.load(Relaxed);
                        self.hooks = Some(hooks);
                    }
                    Err(error) => {
                        failed(&format!("render scale {scale}"), &format!("{error:#}"));
                        self.broken = Some((scale, format!("{error:#}")));
                    }
                }
            }
        }
        if let (Some(urp), false) = (urp, self.get_tried) {
            self.get_tried = true;
            self.get_render_scale = il.static_method(urp, "RenderPIplineQualityManger", "GetRenderScale", &["System.Int32", "System.Int32"]).ok();
        }
        if let Some(hooks) = &self.hooks {
            if want != self.applied {
                self.applied = want;
                self.error = None;
                self.reported = None;
                SCALE_BITS.store(want.map_or(PASSTHROUGH, f32::to_bits), Relaxed);
                match Self::reapply(il, hooks, want) {
                    Ok(()) => ok("render scale", &want.map_or("override removed; the game's own scale applies".into(), |s| format!("set to {s} (scene={:?})", scene()))),
                    Err(error) => {
                        failed(&format!("render scale {want:?}"), &format!("{error:#}"));
                        self.error = Some(format!("{error:#}"));
                    }
                }
            }
            let requested = REQUESTED_BITS.load(Relaxed);
            if requested != self.requested {
                self.requested = requested;
                if let Some(scale) = self.applied {
                    ok("render scale", &format!("the game asked for {} (scene={:?}); using {scale}", f32::from_bits(requested), scene()));
                }
            }
            let disables = DISABLES.load(Relaxed);
            if disables != self.disables {
                self.disables = disables;
                ok("render scale", &format!("the game switched the extra render scale off (scene={:?}); kept on", scene()));
            }
        }
        self.effective = self.get_render_scale.and_then(|get| {
            let (mut width, mut height) = (1920i32, 1080i32);
            let mut args = [(&mut width as *mut i32).cast::<c_void>(), (&mut height as *mut i32).cast()];
            let mut exception = null_mut();
            let boxed = (il.invoke)(get, null_mut(), args.as_mut_ptr(), &mut exception);
            (exception.is_null() && !boxed.is_null()).then(|| *((il.unbox)(boxed) as *const f32))
        });
        if let (Some(scale), true) = (self.applied, self.error.is_none()) {
            let good = self.effective == Some(scale);
            if self.reported != Some(good) {
                self.reported = Some(good);
                match self.effective {
                    _ if good => ok("render scale", &format!("RenderPIplineQualityManger.GetRenderScale() = {scale}")),
                    Some(actual) => failed("render scale", &format!("GetRenderScale() returned {actual}, expected {scale}")),
                    None => failed("render scale", "GetRenderScale() threw"),
                }
            }
        }
        if want.is_some() {
            if let Some(message) = self.error.clone().or_else(|| self.broken.as_ref().map(|(_, m)| m.clone())) {
                errors.push(("renderscale".into(), message));
            }
        }
    }
}

struct Entry {
    spec: Override,
    key: String,
    /// Parsed once the in-game field type is known; reset when the value changes.
    desired: Option<Typed>,
    /// Why `spec.value` cannot be applied; the entry is retried only when the value changes.
    error: Option<String>,
    /// The value the game last had before an override write (log text).
    game_value: Option<String>,
    /// The same value in ini syntax: what removing the override writes back.
    game_ini: Option<String>,
    /// Ini text of the field right after our last write; anything else seen later was written by the game.
    written: Option<String>,
}

#[derive(Default)]
pub(super) struct Overrides(Vec<Entry>);

fn key_of(spec: &Override) -> String {
    format!("{}.{}", spec.setting, spec.field)
}

impl Overrides {
    /// The game's own value of `key` while it is overridden, in ini syntax.
    pub(super) fn game_value(&self, key: &str) -> Option<String> {
        self.0.iter().find(|e| e.key == key).and_then(|e| e.game_ini.clone())
    }

    /// One pass over the pipeline's runtime quality data (`RenderPipelineDefaultQualityData`): restore removed overrides, then rewrite every
    /// desired field that differs. Problems are appended to `errors` under the override key.
    pub(super) unsafe fn apply(&mut self, walker: &Walker, data: *mut c_void, desired: &[Override], scene: &dyn Fn() -> String, errors: &mut Vec<(String, String)>) {
        let mut index = 0;
        while index < self.0.len() {
            if desired.iter().any(|o| key_of(o) == self.0[index].key) {
                index += 1;
                continue;
            }
            match restore_one(walker, data, &self.0[index]) {
                Ok(false) => index += 1,
                Ok(true) => {
                    self.0.remove(index);
                }
                Err(problem) => {
                    failed(&format!("graphics override {}", self.0[index].key), &format!("{problem}; the field keeps its override value"));
                    self.0.remove(index);
                }
            }
        }
        for spec in desired {
            let key = key_of(spec);
            let position = match self.0.iter().position(|e| e.key == key) {
                Some(position) => position,
                None => {
                    self.0.push(Entry { spec: spec.clone(), key: key.clone(), desired: None, error: None, game_value: None, game_ini: None, written: None });
                    self.0.len() - 1
                }
            };
            let entry = &mut self.0[position];
            if entry.spec.value != spec.value {
                entry.spec.value.clone_from(&spec.value);
                entry.desired = None;
                entry.error = None;
            }
            if entry.error.is_none() {
                if let Err(problem) = apply_one(walker, data, entry, scene) {
                    failed(&format!("graphics override {key}"), &format!("{problem}; ignored until its value changes"));
                    entry.error = Some(problem);
                }
            }
            if let Some(problem) = &entry.error {
                errors.push((key, problem.clone()));
            }
        }
    }
}

/// Instance field `name` of `class`, or the names it does have.
unsafe fn field(walker: &Walker, class: *mut c_void, name: &str) -> Result<*mut c_void, String> {
    let c_name = CString::new(name).map_err(|_| "bad name".to_string())?;
    let field = (walker.il.class_field)(class, c_name.as_ptr());
    if !field.is_null() && (walker.il.field_flags)(field) & (FLAG_STATIC | FIELD_LITERAL) == 0 {
        return Ok(field);
    }
    let mut names = Vec::new();
    let mut iterator = null_mut();
    loop {
        let f = (walker.rf.class_fields)(class, &mut iterator);
        if f.is_null() {
            break;
        }
        if (walker.il.field_flags)(f) & (FLAG_STATIC | FIELD_LITERAL) == 0 {
            names.push(c_string((walker.rf.field_name)(f)).unwrap_or_default());
        }
    }
    Err(format!("`{}` has no field `{name}`; it has: {}", walker.class_name(class), names.join(", ")))
}

unsafe fn text(walker: &Walker, ty: *mut c_void, at: *const u8) -> String {
    match walker.read(ty, at) {
        Value::Text(text) => text,
        Value::Object(object) if object.is_null() => "null".into(),
        Value::Object(_) => "<object>".into(),
    }
}

/// Every instance field of `class` that is not static or a constant, with its name.
unsafe fn instance_fields(walker: &Walker, class: *mut c_void) -> Vec<(String, *mut c_void)> {
    let mut fields = Vec::new();
    let mut iterator = null_mut();
    loop {
        let f = (walker.rf.class_fields)(class, &mut iterator);
        if f.is_null() {
            return fields;
        }
        if (walker.il.field_flags)(f) & (FLAG_STATIC | FIELD_LITERAL) == 0 {
            fields.push((c_string((walker.rf.field_name)(f)).unwrap_or_default(), f));
        }
    }
}

/// Constants of an enum class in declaration order.
unsafe fn enum_constants(walker: &Walker, class: *mut c_void) -> Vec<(String, i32)> {
    let mut constants = Vec::new();
    let mut iterator = null_mut();
    loop {
        let f = (walker.rf.class_fields)(class, &mut iterator);
        if f.is_null() {
            return constants;
        }
        if (walker.il.field_flags)(f) & FIELD_LITERAL == 0 {
            continue;
        }
        let mut constant = 0i32;
        (walker.il.static_get)(f, (&mut constant as *mut i32).cast());
        constants.push((c_string((walker.rf.field_name)(f)).unwrap_or_default(), constant));
    }
}

unsafe fn int_enum(walker: &Walker, ty: *mut c_void) -> Option<*mut c_void> {
    if (walker.rf.type_type)(ty) != T_VALUETYPE {
        return None;
    }
    let class = (walker.rf.class_from_type)(ty);
    (!class.is_null() && (walker.rf.class_is_enum)(class) && (walker.rf.type_type)((walker.rf.enum_basetype)(class)) == T_I4).then_some(class)
}

/// Enum constant by name (case-insensitive) or number; only Int32-backed enums are writable.
unsafe fn enum_value(walker: &Walker, class: *mut c_void, value: &str) -> Result<Typed, String> {
    let class_name = walker.class_name(class);
    if (walker.rf.type_type)((walker.rf.enum_basetype)(class)) != T_I4 {
        return Err(format!("enum `{class_name}` is not Int32-backed"));
    }
    if let Ok(number) = value.parse::<i32>() {
        return Ok(Typed::Int(number));
    }
    let constants = enum_constants(walker, class);
    match constants.iter().find(|(name, _)| name.eq_ignore_ascii_case(value)) {
        Some(&(_, constant)) => Ok(Typed::Int(constant)),
        None => Err(format!("`{value}` is not a `{class_name}`; use a number or one of: {}", constants.iter().map(|(n, _)| n.as_str()).collect::<Vec<_>>().join(", "))),
    }
}

unsafe fn parse(walker: &Walker, ty: *mut c_void, value: &str) -> Result<Typed, String> {
    let kind = (walker.rf.type_type)(ty);
    let type_name = walker.il.type_name_of(ty);
    let parsed = match kind {
        T_BOOLEAN => bool_value(value),
        T_I4 => int_value(value),
        T_R4 => float_value(value),
        T_VALUETYPE => {
            let class = (walker.rf.class_from_type)(ty);
            if class.is_null() || !(walker.rf.class_is_enum)(class) {
                return Err(format!("field type `{type_name}` is not writable (struct)"));
            }
            return enum_value(walker, class, value);
        }
        T_SZARRAY if type_name == "System.Int32[]" => ints_value(value),
        T_SZARRAY if type_name == "System.Single[]" => floats_value(value),
        _ => return Err(format!("field type `{type_name}` is not writable (bool, Int32, Single, Int32 enums, Int32[]/Single[] only)")),
    };
    parsed.ok_or_else(|| format!("`{value}` is not a valid {type_name}"))
}

/// Writes `desired` at `at` when it differs; `Ok(true)` if it wrote.
unsafe fn write(walker: &Walker, at: *mut u8, desired: &Typed) -> Result<bool, String> {
    match desired {
        Typed::Bool(on) => {
            let byte = u8::from(*on);
            Ok(at.read() != byte && { at.write(byte); true })
        }
        Typed::Int(number) => Ok(at.cast::<i32>().read_unaligned() != *number && { at.cast::<i32>().write_unaligned(*number); true }),
        Typed::Float(number) => Ok(at.cast::<f32>().read_unaligned() != *number && { at.cast::<f32>().write_unaligned(*number); true }),
        Typed::Ints(items) => write_array(walker, at, items),
        Typed::Floats(items) => write_array(walker, at, items),
    }
}

unsafe fn write_array<T: Copy + PartialEq>(walker: &Walker, at: *mut u8, items: &[T]) -> Result<bool, String> {
    let array = at.cast::<*mut c_void>().read_unaligned();
    if array.is_null() {
        return Ok(false); // not allocated yet; retried next tick
    }
    let length = (walker.rf.array_length)(array) as usize;
    if length != items.len() {
        return Err(format!("the game's array has {length} element(s), the value has {}", items.len()));
    }
    // Il2CppArray elements start at 0x20 (header 16 + bounds 8 + max_length 8).
    let data = array.cast::<u8>().add(0x20).cast::<T>();
    let mut wrote = false;
    for (index, item) in items.iter().enumerate() {
        if data.add(index).read_unaligned() != *item {
            data.add(index).write_unaligned(*item);
            wrote = true;
        }
    }
    Ok(wrote)
}

unsafe fn array_items<T: Copy + ToString>(walker: &Walker, at: *const u8) -> String {
    let array = at.cast::<*mut c_void>().read_unaligned();
    if array.is_null() {
        return String::new();
    }
    let length = (walker.rf.array_length)(array) as usize;
    let data = array.cast::<u8>().add(0x20).cast::<T>();
    (0..length).map(|i| data.add(i).read_unaligned().to_string()).collect::<Vec<_>>().join(",")
}

/// The field's value in ini syntax (what `parse` reads back): bool on/off, enum constant name, comma lists; other
/// types as the dump prints them.
unsafe fn ini_value(walker: &Walker, ty: *mut c_void, at: *const u8) -> String {
    match (walker.rf.type_type)(ty) {
        T_BOOLEAN => if at.read() != 0 { "on" } else { "off" }.into(),
        T_I4 => at.cast::<i32>().read_unaligned().to_string(),
        T_R4 => at.cast::<f32>().read_unaligned().to_string(),
        T_VALUETYPE => match int_enum(walker, ty) {
            Some(class) => {
                let number = at.cast::<i32>().read_unaligned();
                enum_constants(walker, class).into_iter().find(|&(_, c)| c == number).map_or(number.to_string(), |(name, _)| name)
            }
            None => text(walker, ty, at),
        },
        T_SZARRAY => match walker.il.type_name_of(ty).as_str() {
            "System.Int32[]" => array_items::<i32>(walker, at),
            "System.Single[]" => array_items::<f32>(walker, at),
            _ => text(walker, ty, at),
        },
        _ => text(walker, ty, at),
    }
}

unsafe fn kind_of(walker: &Walker, ty: *mut c_void) -> FieldKind {
    match (walker.rf.type_type)(ty) {
        T_BOOLEAN => FieldKind::Bool,
        T_I4 => FieldKind::Int,
        T_R4 => FieldKind::Float,
        T_VALUETYPE => int_enum(walker, ty).map_or(FieldKind::ReadOnly, |class| FieldKind::Enum(enum_constants(walker, class))),
        T_SZARRAY => match walker.il.type_name_of(ty).as_str() {
            "System.Int32[]" => FieldKind::Ints,
            "System.Single[]" => FieldKind::Floats,
            _ => FieldKind::ReadOnly,
        },
        _ => FieldKind::ReadOnly,
    }
}

/// Every setting object held by a class-typed field of the runtime data, with all its instance fields.
pub(super) unsafe fn catalogue(walker: &Walker, data: *mut c_void, overrides: &Overrides) -> Vec<SettingInfo> {
    let data_class = (walker.rf.object_class)(data);
    let mut settings = Vec::new();
    for (name, setting_field) in instance_fields(walker, data_class) {
        if (walker.rf.type_type)((walker.il.field_type)(setting_field)) != T_CLASS {
            continue;
        }
        let setting = data.cast::<u8>().add((walker.rf.field_offset)(setting_field)).cast::<*mut c_void>().read_unaligned();
        if setting.is_null() {
            continue;
        }
        let class = (walker.rf.object_class)(setting);
        let fields = instance_fields(walker, class).into_iter().map(|(field_name, f)| {
            let ty = (walker.il.field_type)(f);
            let at = setting.cast::<u8>().add((walker.rf.field_offset)(f));
            FieldInfo { game_value: overrides.game_value(&format!("{name}.{field_name}")), name: field_name, kind: kind_of(walker, ty), value: ini_value(walker, ty, at) }
        }).collect();
        settings.push(SettingInfo { name, fields });
    }
    settings
}

/// Type and address of `<setting>.<field>`; `None` while the setting object is not built yet.
unsafe fn locate(walker: &Walker, data: *mut c_void, spec: &Override) -> Result<Option<(*mut c_void, *mut u8)>, String> {
    let data_class = (walker.rf.object_class)(data);
    let setting_field = field(walker, data_class, &spec.setting)?;
    let setting = data.cast::<u8>().add((walker.rf.field_offset)(setting_field)).cast::<*mut c_void>().read_unaligned();
    if setting.is_null() {
        return Ok(None);
    }
    let setting_class = (walker.rf.object_class)(setting);
    let target = field(walker, setting_class, &spec.field)?;
    Ok(Some(((walker.il.field_type)(target), setting.cast::<u8>().add((walker.rf.field_offset)(target)))))
}

/// Writes the recorded game value back; `Ok(true)` when the entry can be forgotten.
unsafe fn restore_one(walker: &Walker, data: *mut c_void, entry: &Entry) -> Result<bool, String> {
    let Some(game) = &entry.game_ini else { return Ok(true) };
    let Some((ty, at)) = locate(walker, data, &entry.spec)? else { return Ok(false) };
    write(walker, at, &parse(walker, ty, game)?)?;
    ok(&format!("graphics override {}", entry.key), &format!("removed, restored {game}"));
    Ok(true)
}

unsafe fn apply_one(walker: &Walker, data: *mut c_void, entry: &mut Entry, scene: &dyn Fn() -> String) -> Result<(), String> {
    let Some((ty, at)) = locate(walker, data, &entry.spec)? else { return Ok(()) }; // not built yet; retried next tick
    if entry.desired.is_none() {
        let desired = parse(walker, ty, &entry.spec.value)?;
        check_divisor(&entry.spec.field, &desired)?;
        entry.desired = Some(desired);
    }
    let (before, before_ini) = (text(walker, ty, at), ini_value(walker, ty, at));
    let Some(desired) = &entry.desired else { return Ok(()) };
    if !write(walker, at, desired)? {
        if entry.game_ini.is_none() && !before_ini.is_empty() {
            entry.game_ini = Some(before_ini.clone());
        }
        entry.written = Some(before_ini);
        return Ok(());
    }
    let after = text(walker, ty, at);
    if entry.written.as_deref() == Some(before_ini.as_str()) {
        // Our own earlier value replaced by an edited one; the game's value is already recorded.
        ok(&format!("graphics override {}", entry.key), &format!("{before} -> {after} (scene={:?}; re-applied whenever the game resets it)", scene()));
    } else {
        match &entry.game_value {
            None => ok(&format!("graphics override {}", entry.key), &format!("{before} -> {after} (scene={:?}; re-applied whenever the game resets it)", scene())),
            Some(previous) if *previous != before => ok(&format!("graphics override {}", entry.key), &format!("the game set {before} (scene={:?}); re-applied {after}", scene())),
            Some(_) => {}
        }
        entry.game_value = Some(before);
        entry.game_ini = Some(before_ini);
    }
    entry.written = Some(ini_value(walker, ty, at));
    Ok(())
}
