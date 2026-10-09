//! Opt-in (`ASCNET_PATCH_FPS=<n>`, set by AscNet.Launcher) frame-rate unlock. Native replacement for the old
//! launcher bundle patch (XUiMain.lua hook): no game file is edited. A lucia thread attached to IL2CPP calls
//! `UnityEngine.Application.set_targetFrameRate(n)` every 2 s (game code may reset it).
//!
//! Unity ignores `Application.targetFrameRate` while `QualitySettings.vSyncCount > 0` (vsync paces
//! the frame instead), and the game has its own "UseVSync" option that sets it. So when vsync is on, it is
//! switched off (logged) -- otherwise the unlock would silently do nothing for those players.
#[path = "fps_params.rs"]
mod fps_params;

use anyhow::{Context, Result};

use super::il2cpp::{self, report, Il2cpp, PERIOD};
use fps_params::target;

const NAMESPACE: &str = "UnityEngine";
const UNAFFECTED: &str = "the game is unaffected and keeps its own frame rate";

fn ok(what: &str, detail: &str) {
    il2cpp::ok(what, detail);
}

fn failed(what: &str, reason: &str) {
    il2cpp::failed(what, reason, UNAFFECTED);
}

/// Starts the worker when the launcher asked for it. Never blocks the caller.
pub fn spawn_if_enabled() {
    let requested = std::env::var("ASCNET_PATCH_FPS").ok();
    let Some(fps) = target(requested.as_deref()) else {
        if let Some(value) = requested {
            failed("FPS unlock", &format!("ASCNET_PATCH_FPS=`{value}` is not a positive whole number"));
        }
        return;
    };
    ok("FPS unlock requested", &format!("ASCNET_PATCH_FPS={fps}; applying natively once IL2CPP is ready"));
    std::thread::spawn(move || {
        if let Err(error) = unsafe { run(fps) } {
            failed("FPS unlock", &format!("{error:#}"));
        }
    });
}

unsafe fn run(fps: i32) -> Result<()> {
    let il = Il2cpp::load().context("il2cpp exports for FPS unlock")?;
    ok("il2cpp exports (FPS unlock)", "runtime_class_init, class_get_method_from_name, runtime_invoke, object_unbox and helpers resolved");
    // Stays attached for the whole process; the loop below runs until the game exits.
    let assemblies = il.wait_attach()?;

    let (application, image) = il.find_class(&assemblies, NAMESPACE, "Application")
        .with_context(|| format!("class `{NAMESPACE}.Application` not found in any of {} loaded assemblies", assemblies.len()))?;
    ok("lookup UnityEngine.Application", &format!("assembly={image}"));
    let set_rate = il.static_method(application, "Application", "set_targetFrameRate", &["System.Int32"])?;
    let get_rate = il.static_method(application, "Application", "get_targetFrameRate", &[])?;
    ok("lookup Application.set_targetFrameRate(Int32)/get_targetFrameRate()", "static property accessors");
    (il.class_init)(application);

    // vsync is optional: without it the unlock still works for players who run with vsync off.
    let vsync = match il.find_class(&assemblies, NAMESPACE, "QualitySettings") {
        Some((class, image)) => {
            let accessors = il.static_method(class, "QualitySettings", "get_vSyncCount", &[])
                .and_then(|get| Ok((get, il.static_method(class, "QualitySettings", "set_vSyncCount", &["System.Int32"])?)));
            match accessors {
                Ok(accessors) => {
                    ok("lookup UnityEngine.QualitySettings.vSyncCount", &format!("assembly={image}"));
                    (il.class_init)(class);
                    Some(accessors)
                }
                Err(error) => {
                    failed("lookup QualitySettings.vSyncCount", &format!("{error:#}; a game-enabled vsync would still cap the frame rate"));
                    None
                }
            }
        }
        None => {
            failed("lookup UnityEngine.QualitySettings", "class not found; a game-enabled vsync would still cap the frame rate");
            None
        }
    };

    let (mut state, mut vsync_state, mut vsync_seen) = (None, None, None);
    loop {
        if let Some((get, set)) = vsync {
            let count = il.call_get_int(get);
            report(&mut vsync_state, count.is_some(), "read QualitySettings.vSyncCount", "the call threw a managed exception; targetFrameRate may be ignored if vsync is on", UNAFFECTED);
            // Act and log only when the value we last saw changes (the game may re-enable vsync later).
            if let Some(count) = count.filter(|&count| vsync_seen != Some(count)) {
                vsync_seen = Some(count);
                if count == 0 {
                    ok("read QualitySettings.vSyncCount", "0 (vsync off)");
                } else if il.call_set_int(set, 0) {
                    vsync_seen = Some(0);
                    ok("write QualitySettings.vSyncCount=0", &format!("the game had vSyncCount={count}, which makes Unity ignore targetFrameRate"));
                } else {
                    failed("write QualitySettings.vSyncCount=0", &format!("the game has vSyncCount={count} and the write threw a managed exception; targetFrameRate may be ignored"));
                }
            }
        }
        let applied = il.call_set_int(set_rate, fps) && il.call_get_int(get_rate) == Some(fps);
        report(&mut state, applied, &format!("apply Application.targetFrameRate={fps}"), "the call threw or the value read back differently", UNAFFECTED);
        std::thread::sleep(PERIOD);
    }
}
