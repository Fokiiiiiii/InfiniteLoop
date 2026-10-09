
use std::{sync::{LazyLock, RwLock}, time::Duration};

use lazy_static::lazy_static;
use windows::core::PCSTR;
use windows::Win32::System::SystemServices::DLL_PROCESS_ATTACH;
use windows::Win32::{Foundation::HINSTANCE, System::LibraryLoader::GetModuleHandleA};

#[path = "../../diag.rs"]
mod diag;
mod interceptor;
mod modules;
#[allow(dead_code)]
#[path = "../../krsdk/src/exports/sdk_identity.rs"]
mod sdk_identity;
mod util;

use crate::modules::{cn_identity_line, is_cn_client, spawn_fps_if_enabled, spawn_nofade_if_enabled, CnSdk, Http, MhyContext, ModuleManager};

unsafe fn initialize() -> bool {
    let cn = is_cn_client();
    diag::log(&if cn { cn_identity_line() } else { sdk_identity::region_line("lucia", &sdk_identity::read_packaged()) });
    let Ok(game_assembly) = GetModuleHandleA(PCSTR(b"GameAssembly.dll\0".as_ptr())) else {
        diag::log(&diag::failed_line("lucia", "GameAssembly.dll module lookup", "GameAssembly.dll is not loaded in this process; lucia.dll must run inside PGR.exe of the client root"));
        return false;
    };
    diag::log(&diag::ok_line("lucia", "GameAssembly.dll module lookup", &format!("base=0x{:X}", game_assembly.0 as usize)));
    spawn_nofade_if_enabled();
    spawn_fps_if_enabled();
    let Ok(mut module_manager) = MODULE_MANAGER.write() else {
        diag::log(&diag::failed_line("lucia", "module manager", "lock is poisoned by an earlier crash in this process"));
        return false;
    };
    if let Err(error) = module_manager.enable(MhyContext::<Http>::new()) {
        diag::log(&diag::failed_line("lucia", "native routing (all requests stay on retail servers)", &format!("{error:#}")));
        return false;
    }
    // CN keeps the retail KRSDKEx.dll; its HTTP must be routed as well or login talks to Kuro's servers.
    if cn {
        if let Err(error) = module_manager.enable(MhyContext::<CnSdk>::new()) {
            diag::log(&diag::failed_line("lucia", "CN SDK routing (login stays on retail servers)", &format!("{error:#}")));
            return false;
        }
    }
    true
}

/// Initializes once GameAssembly.dll is loaded. Global clients are started by AscNet's KRSDK.dll
/// (`kurosdk_getConfigInfo`); CN keeps the retail KRSDKEx.dll, so nothing would call `ascnet_patch_initialize`
/// and lucia must start itself. The short poll keeps the hooks ahead of the SDK's first request.
unsafe fn startup_thread(probe: bool) {
    if !probe && !is_cn_client() {
        return;
    }
    while GetModuleHandleA(PCSTR(b"GameAssembly.dll\0".as_ptr())).is_err() {
        std::thread::sleep(Duration::from_millis(10));
    }
    ascnet_patch_initialize();
}

lazy_static! {
    static ref MODULE_MANAGER: RwLock<ModuleManager> = RwLock::new(ModuleManager::default());
}
static INITIALIZED: LazyLock<bool> = LazyLock::new(|| unsafe { initialize() });

#[no_mangle]
pub unsafe extern "system" fn ascnet_patch_initialize() -> i32 {
    i32::from(*INITIALIZED)
}

#[no_mangle]
#[allow(non_snake_case)]
unsafe extern "system" fn DllMain(_: HINSTANCE, call_reason: u32, _: *mut ()) -> bool {
    if call_reason == DLL_PROCESS_ATTACH {
        // The CN check reads files, so it runs on the thread, outside the loader lock.
        let probe = std::env::var("ASCNET_PATCH_PROBE").as_deref() == Ok("1");
        std::thread::spawn(move || startup_thread(probe));
    }

    true
}
