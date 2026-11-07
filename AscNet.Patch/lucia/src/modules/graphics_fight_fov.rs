//! Fight-only, per-write tangent FOV scaling; the worker owns both detours.
use std::{ffi::c_void, ptr::null_mut, sync::atomic::{AtomicU32, AtomicUsize, Ordering::Relaxed}};

use anyhow::{anyhow, bail, Result};
use ilhook::x64::Registers;

use super::graphics_tweaks::{code, QUITTING};
use crate::{interceptor::Interceptor, modules::il2cpp::{export, Il2cpp, FLAG_STATIC}, util::readable};

const PASSTHROUGH: u32 = 0x7FC0_0000;
static FACTOR: AtomicU32 = AtomicU32::new(PASSTHROUGH);
static CAMERAS: [AtomicUsize; 2] = [AtomicUsize::new(0), AtomicUsize::new(0)];
static GAME_FOV: [AtomicU32; 2] = [AtomicU32::new(PASSTHROUGH), AtomicU32::new(PASSTHROUGH)];
static HITS: AtomicU32 = AtomicU32::new(0);

type FieldOffset = unsafe extern "system" fn(*mut c_void) -> usize;

fn scaled_fov(degrees: f32, factor: f32) -> f32 {
    if factor == 1.0 || !degrees.is_finite() || !(0.0..179.0).contains(&degrees) {
        return degrees;
    }
    (2.0 * ((degrees.to_radians() * 0.5).tan() * factor).atan()).to_degrees().min(179.0)
}

fn camera_slot(camera: usize) -> Option<usize> {
    (camera != 0).then(|| CAMERAS.iter().position(|p| p.load(Relaxed) == camera)).flatten()
}

unsafe extern "win64" fn on_fov(regs: *mut Registers, _: usize) {
    if QUITTING.load(Relaxed) {
        return;
    }
    let Some(slot) = camera_slot((*regs).rcx as usize) else { return };
    let bits = (*regs).xmm1 as u32;
    GAME_FOV[slot].store(bits, Relaxed);
    HITS.fetch_add(1, Relaxed);
    let factor = f32::from_bits(FACTOR.load(Relaxed));
    if factor.is_nan() {
        return;
    }
    let value = scaled_fov(f32::from_bits(bits), factor);
    (*regs).xmm1 = ((*regs).xmm1 & !0xFFFF_FFFF) | u128::from(value.to_bits());
}

// LensSettings is a 0x28-byte value type: Win64 uses rcx for the hidden return buffer,
// rdx for Camera and r8 for MethodInfo. Preserve every lens field except copied fight FOV.
unsafe extern "win64" fn on_from_camera(regs: *mut Registers, original: usize, _: usize) -> usize {
    let call: unsafe extern "win64" fn(usize, usize, usize) -> usize = std::mem::transmute(original);
    let output = (*regs).rcx as usize;
    let camera = (*regs).rdx as usize;
    let result = call(output, camera, (*regs).r8 as usize);
    if !QUITTING.load(Relaxed) && !f32::from_bits(FACTOR.load(Relaxed)).is_nan() {
        if let Some(slot) = camera_slot(camera) {
            let value = f32::from_bits(GAME_FOV[slot].load(Relaxed));
            if value.is_finite() && output != 0 {
                *(output as *mut f32) = value;
            }
        }
    }
    result
}

struct Root {
    field: *mut c_void,
    camera_offset: usize,
}

struct Hooks {
    _interceptor: Interceptor,
    roots: [Root; 2],
}

#[derive(Default)]
pub(super) struct FightFov {
    hooks: Option<Hooks>,
    broken: Option<(u32, String)>,
    last_hits: u32,
    quiet_ticks: u8,
}

impl FightFov {
    unsafe fn root(il: &Il2cpp, assemblies: &[*mut c_void], namespace: &str, manager: &str, offset: FieldOffset) -> Result<Root> {
        let (class, _) = il.find_class(assemblies, namespace, manager).ok_or_else(|| anyhow!("{namespace}.{manager} missing"))?;
        let (camera, _) = il.find_class(assemblies, namespace, "XCamera").ok_or_else(|| anyhow!("{namespace}.XCamera missing"))?;
        let field = (il.class_field)(class, c"<Camera>k__BackingField".as_ptr());
        let backing = (il.class_field)(camera, c"<Camera>k__BackingField".as_ptr());
        if field.is_null() || backing.is_null() {
            bail!("{namespace}.{manager}/XCamera Camera backing field missing")
        }
        if (il.field_flags)(field) & FLAG_STATIC == 0 || (il.field_flags)(backing) & FLAG_STATIC != 0 {
            bail!("{namespace}.{manager}/XCamera Camera field storage changed")
        }
        let expected = if namespace.is_empty() { "XCamera".into() } else { format!("{namespace}.XCamera") };
        if il.type_name_of((il.field_type)(field)) != expected || il.type_name_of((il.field_type)(backing)) != "UnityEngine.Camera" {
            bail!("{namespace}.{manager}/XCamera Camera field type changed")
        }
        let camera_offset = offset(backing);
        if !(0x10..0x10000).contains(&camera_offset) || camera_offset % std::mem::align_of::<usize>() != 0 {
            bail!("{namespace}.XCamera Camera offset is invalid: 0x{camera_offset:X}")
        }
        Ok(Root { field, camera_offset })
    }

    unsafe fn install(il: &Il2cpp, assemblies: &[*mut c_void]) -> Result<Hooks> {
        let offset: FieldOffset = export("il2cpp_field_get_offset")?;
        let roots = [Self::root(il, assemblies, "", "XRLManager", offset)?, Self::root(il, assemblies, "StatusSyncFight", "XFightRLManager", offset)?];
        let (camera, _) = il.find_class(assemblies, "UnityEngine", "Camera").ok_or_else(|| anyhow!("UnityEngine.Camera missing"))?;
        let setter = (il.class_method)(camera, c"set_fieldOfView".as_ptr(), 1);
        if setter.is_null() || (il.param_count)(setter) != 1 || (il.method_flags)(setter, null_mut()) & FLAG_STATIC != 0 {
            bail!("Camera.set_fieldOfView(float) missing or not an instance method")
        }
        if il.type_name_of((il.param)(setter, 0)) != "System.Single" {
            bail!("Camera.set_fieldOfView parameter is not System.Single")
        }
        let (lens, _) = il.find_class(assemblies, "Cinemachine", "LensSettings").ok_or_else(|| anyhow!("Cinemachine.LensSettings missing"))?;
        let from_camera = il.static_method(lens, "LensSettings", "FromCamera", &["UnityEngine.Camera"])?;
        let return_type: unsafe extern "system" fn(*mut c_void) -> *mut c_void = export("il2cpp_method_get_return_type")?;
        let value_size: unsafe extern "system" fn(*mut c_void, *mut u32) -> i32 = export("il2cpp_class_value_size")?;
        let fov = (il.class_field)(lens, c"FieldOfView".as_ptr());
        if il.type_name_of(return_type(setter)) != "System.Void"
            || il.type_name_of(return_type(from_camera)) != "Cinemachine.LensSettings"
            || value_size(lens, null_mut()) != 0x28
            || fov.is_null()
            || il.type_name_of((il.field_type)(fov)) != "System.Single"
            || offset(fov) != 0x10
        {
            // IL2CPP reports value-type fields including the two-word boxed-object header.
            bail!("Camera/LensSettings return type or lens layout changed")
        }
        let mut interceptor = Interceptor::new();
        interceptor.replace(code(from_camera, "LensSettings.FromCamera")?, on_from_camera)?;
        interceptor.attach(code(setter, "Camera.set_fieldOfView")?, on_fov)?;
        Ok(Hooks { _interceptor: interceptor, roots })
    }

    /// Native 4.8 evidence: CinemachineBrain.PushStateToUnityCamera (0x29C48B0)
    /// loads CameraState.m_Lens.FieldOfView at 0x29C4B28 and calls the managed setter
    /// (0x529D1D0) at 0x29C4B31; that wrapper preserves rcx/xmm1 then tail-jumps
    /// to its Unity icall. Hooking the wrapper therefore covers the actual brain path.
    /// Normal UpdateVirtualCamera (0x26C0AE0) and StatusSync equivalent (0x1A395E0)
    /// do not read Camera.fieldOfView or LensSettings.FromCamera. Custom FOV writes
    /// feed VCamera.m_Lens (+0x90), not the Unity getter: 0x269C152 / 0x1A35BCB.
    /// Babylonia photo setup (0x1A31A90) DOES call FromCamera (0x29FF8D0), whose
    /// 0x29FF98E getter reads back the output. The second detour removes that feedback
    /// from lens copies without changing collision/framing code's real camera reads.
    /// Only the managers' Unity cameras are scaled: lobby/UI, dorm and independent
    /// cutscene cameras are untouched. Clearing passes the exact game argument on
    /// its next setter write; no rounded/inverse-scaled snapshot is restored.
    pub(super) unsafe fn update(&mut self, il: &Il2cpp, assemblies: &[*mut c_void], want: Option<f32>, errors: &mut Vec<(String, String)>) {
        if want.is_none() {
            FACTOR.store(PASSTHROUGH, Relaxed);
        }
        if QUITTING.load(Relaxed) {
            return;
        }
        if let Some(value) = want {
            if !value.is_finite() || !(0.5..=2.0).contains(&value) {
                FACTOR.store(PASSTHROUGH, Relaxed);
                errors.push(("fightfov".into(), "factor must be finite and between 0.5 and 2".into()));
                return;
            }
            if self.hooks.is_none() && self.broken.as_ref().is_none_or(|(bits, _)| *bits != value.to_bits()) {
                self.broken = None;
                match Self::install(il, assemblies) {
                    Ok(hooks) => self.hooks = Some(hooks),
                    Err(error) => self.broken = Some((value.to_bits(), format!("{error:#}"))),
                }
            }
        }
        let Some(hooks) = &self.hooks else {
            if want.is_some() {
                if let Some((_, message)) = &self.broken {
                    errors.push(("fightfov".into(), message.clone()));
                }
            }
            return;
        };
        let mut live = false;
        for (slot, root) in hooks.roots.iter().enumerate() {
            let mut instance: *mut c_void = null_mut();
            (il.static_get)(root.field, (&mut instance as *mut *mut c_void).cast());
            let address = (instance as usize).checked_add(root.camera_offset);
            let camera = address.filter(|&at| !instance.is_null() && readable(at, std::mem::size_of::<usize>())).map_or(0, |at| *(at as *const usize));
            // ponytail: camera identity can be stale for one worker tick (0.5 s);
            // move root refresh into a main-thread lifecycle hook if reuse is observed.
            if CAMERAS[slot].load(Relaxed) != camera {
                GAME_FOV[slot].store(PASSTHROUGH, Relaxed);
                CAMERAS[slot].store(camera, Relaxed);
                self.quiet_ticks = 0;
            }
            live |= camera != 0;
        }
        FACTOR.store(want.map_or(PASSTHROUGH, f32::to_bits), Relaxed);
        let hits = HITS.load(Relaxed);
        if want.is_some() && live && hits == self.last_hits {
            self.quiet_ticks = self.quiet_ticks.saturating_add(1);
            if self.quiet_ticks >= 6 {
                errors.push(("fightfov".into(), "fight camera is live but no FOV setter writes were observed for three seconds".into()));
            }
        } else {
            self.quiet_ticks = 0;
        }
        self.last_hits = hits;
    }
}

impl Drop for FightFov {
    fn drop(&mut self) {
        FACTOR.store(PASSTHROUGH, Relaxed);
    }
}

#[cfg(test)]
mod tests {
    use super::scaled_fov;

    #[test]
    fn tangent_scale_is_identity_reversible_and_bounded() {
        assert_eq!(scaled_fov(60.0, 1.0), 60.0);
        assert!((scaled_fov(scaled_fov(60.0, 2.0), 0.5) - 60.0).abs() < 0.0001);
        assert!(scaled_fov(60.0, 2.0) > 60.0);
        assert!(scaled_fov(178.9, 2.0) <= 179.0);
        assert_eq!(scaled_fov(0.0, 2.0), 0.0);
        assert!(scaled_fov(f32::NAN, 2.0).is_nan());
    }
}
