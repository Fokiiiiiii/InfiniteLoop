//! Opt-in (`ASCNET_PATCH_NOFADE=1`, set by AscNet.Launcher) character/camera proximity fade disable.
//! Native equivalent of tools/apply_pgr_nofade.py: no bundle is edited. A lucia thread attached to
//! IL2CPP sets `XCameraDitherDetector.UseCameraDither=false` and calls
//! `XNpcDither.SetPlayerSelfDitherParameter` with opaque values every 2 s (game code may reset them).
//! `XDitherHelper.GlobalEnableDitherState` is deliberately untouched.
#[path = "nofade_params.rs"]
mod nofade_params;

use std::ffi::{c_void, CString};

use anyhow::{bail, Context, Result};

use super::il2cpp::{self, report, Il2cpp, FLAG_STATIC, PERIOD};
use nofade_params::{enabled, PlayerSelfDither, OPAQUE};

const DETECTOR_CLASS: &str = "XCameraDitherDetector";
const DETECTOR_FIELD: &str = "UseCameraDither";
const DITHER_CLASS: &str = "XNpcDither";
const DITHER_METHOD: &str = "SetPlayerSelfDitherParameter";
const UNAFFECTED: &str = "the game is unaffected and the camera fade stays at its default";

fn ok(what: &str, detail: &str) {
    il2cpp::ok(what, detail);
}

fn failed(what: &str, reason: &str) {
    il2cpp::failed(what, reason, UNAFFECTED);
}

/// Starts the worker when the launcher asked for it. Never blocks the caller.
pub fn spawn_if_enabled() {
    if !enabled(std::env::var("ASCNET_PATCH_NOFADE").ok().as_deref()) {
        return;
    }
    ok("no camera fade requested", "ASCNET_PATCH_NOFADE=1; applying natively once IL2CPP is ready");
    std::thread::spawn(|| {
        if let Err(error) = unsafe { run() } {
            failed("no camera fade", &format!("{error:#}"));
        }
    });
}

unsafe fn run() -> Result<()> {
    let il = Il2cpp::load().context("il2cpp exports for no-fade")?;
    ok("il2cpp exports (no camera fade)", "runtime_class_init, class_get_field_from_name, field_static_set_value, runtime_invoke and helpers resolved");
    // Stays attached for the whole process; the loop below runs until the game exits.
    let assemblies = il.wait_attach()?;

    let (detector, image) = il.find_class(&assemblies, "", DETECTOR_CLASS)
        .with_context(|| format!("class `{DETECTOR_CLASS}` (global namespace) not found in any of {} loaded assemblies; this client build renamed it", assemblies.len()))?;
    ok(&format!("lookup {DETECTOR_CLASS}"), &format!("assembly={image}"));
    let field_name = CString::new(DETECTOR_FIELD)?;
    let field = (il.class_field)(detector, field_name.as_ptr());
    if field.is_null() || (il.field_flags)(field) & FLAG_STATIC == 0 {
        bail!("`{DETECTOR_CLASS}.{DETECTOR_FIELD}` is missing or not static in this build")
    }
    let field_type = il.type_name_of((il.field_type)(field));
    if field_type != "System.Boolean" {
        bail!("`{DETECTOR_CLASS}.{DETECTOR_FIELD}` has type `{field_type}`, expected System.Boolean")
    }
    ok(&format!("lookup {DETECTOR_CLASS}.{DETECTOR_FIELD}"), "static System.Boolean");

    // The camera flag is independent of the player-self parameter; keep going if only XNpcDither is missing.
    let method = match il.find_class(&assemblies, "", DITHER_CLASS) {
        Some((class, image)) => {
            ok(&format!("lookup {DITHER_CLASS}"), &format!("assembly={image}"));
            let method_name = CString::new(DITHER_METHOD)?;
            let method = (il.class_method)(class, method_name.as_ptr(), 1);
            let param = if method.is_null() { String::new() } else { il.type_name_of((il.param)(method, 0)) };
            if method.is_null() || (il.param_count)(method) != 1 || (il.method_flags)(method, std::ptr::null_mut()) & FLAG_STATIC == 0 || !param.ends_with("PlayerSelfDitherParameter") {
                failed(&format!("lookup {DITHER_CLASS}.{DITHER_METHOD}(PlayerSelfDitherParameter)"), "static one-argument method with a PlayerSelfDitherParameter parameter not found; only the camera fade is disabled");
                None
            } else {
                ok(&format!("lookup {DITHER_CLASS}.{DITHER_METHOD}({param})"), "static, 1 parameter");
                (il.class_init)(class);
                Some(method)
            }
        }
        None => {
            failed(&format!("lookup {DITHER_CLASS}"), &format!("class (global namespace) not found in any of {} loaded assemblies; only the camera fade is disabled", assemblies.len()));
            None
        }
    };
    // The static initialiser sets UseCameraDither=true; it must have run before our write.
    (il.class_init)(detector);
    ok("class init", &format!("{DETECTOR_CLASS}{}", if method.is_some() { format!(", {DITHER_CLASS}") } else { String::new() }));

    let (mut camera_state, mut param_state) = (None, None);
    loop {
        let mut off = 0u8;
        (il.static_set)(field, (&mut off as *mut u8).cast());
        let mut readback = 1u8;
        (il.static_get)(field, (&mut readback as *mut u8).cast());
        report(&mut camera_state, readback == 0, &format!("write {DETECTOR_CLASS}.{DETECTOR_FIELD}=false"), "read back true after the write", UNAFFECTED);

        if let Some(method) = method {
            let mut parameter: PlayerSelfDither = OPAQUE;
            let mut args = [(&mut parameter as *mut PlayerSelfDither).cast::<c_void>()];
            let mut exception = std::ptr::null_mut();
            (il.invoke)(method, std::ptr::null_mut(), args.as_mut_ptr(), &mut exception);
            report(&mut param_state, exception.is_null(), &format!("apply {DITHER_CLASS}.{DITHER_METHOD}(opaque)"), "the call threw a managed exception", UNAFFECTED);
        }
        std::thread::sleep(PERIOD);
    }
}
