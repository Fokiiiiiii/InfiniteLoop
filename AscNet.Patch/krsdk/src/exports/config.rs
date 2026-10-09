use crate::exports::sdk_identity;
use crate::globals::{SendPtr, ALLOCATED_STRINGS, LANGUAGE};
use std::ffi::CString;
use std::os::{raw::c_char, windows::ffi::OsStrExt};
use windows::{
    core::{s, PCSTR, PCWSTR},
    Win32::{
        Foundation::HMODULE,
        System::LibraryLoader::{
            GetModuleHandleA, GetProcAddress, LoadLibraryExW,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR, LOAD_LIBRARY_SEARCH_SYSTEM32,
        },
    },
};

fn packaged_config(config: &std::collections::HashMap<String, String>) -> Result<serde_json::Value, String> {
    let id = sdk_identity::identity(config)?;

    Ok(serde_json::json!({
        "channelId": id.channel_id,
        "channelName": id.channel_name,
        "channelOp": id.channel_op,
        "gameId": id.project_id,
        "pkgId": id.product_id
    }))
}

fn initialize_routing() -> Result<(), &'static str> {
    unsafe {
        let module = match GetModuleHandleA(s!("lucia.dll")) {
            Ok(module) => module,
            Err(_) => {
                let exe = std::env::current_exe()
                    .map_err(|_| "could not determine the game executable path")?;
                let path = exe
                    .parent()
                    .ok_or("game executable has no parent directory")?
                    .join("lucia.dll");
                let path: Vec<u16> = path
                    .as_os_str()
                    .encode_wide()
                    .chain(std::iter::once(0))
                    .collect();
                LoadLibraryExW(
                    PCWSTR(path.as_ptr()),
                    None,
                    LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32,
                )
                .map_err(|_| "could not load lucia.dll from beside PGR.exe (missing, blocked by antivirus, or not an AscNet build)")?
            }
        };
        let initialize =
            GetProcAddress::<HMODULE, PCSTR>(module, s!("ascnet_patch_initialize"))
                .ok_or("lucia.dll beside PGR.exe does not export ascnet_patch_initialize (outdated or foreign lucia.dll; reinstall the client patch)")?;
        let initialize: unsafe extern "system" fn() -> i32 = std::mem::transmute(initialize);
        (initialize() == 1)
            .then_some(())
            .ok_or("lucia.dll loaded but its routing initialization failed (see the [lucia] FAILED line in ascnet-patch.log)")
    }
}

#[no_mangle]
pub extern "C" fn kurosdk_getConfigInfo() -> *mut c_char {
    println!("[KRSDK] *** kurosdk_getConfigInfo called ***");
    let packaged = sdk_identity::read_packaged();
    crate::diag::log(&sdk_identity::region_line("KRSDK", &packaged));
    match initialize_routing() {
        Ok(()) => crate::diag::log(&crate::diag::ok_line("KRSDK", "lucia.dll routing initialization", "")),
        Err(error) => {
            crate::diag::log(&crate::diag::failed_line("KRSDK", "lucia.dll routing initialization", error));
            return std::ptr::null_mut();
        }
    }
    let config = packaged.and_then(|config| packaged_config(&config)).unwrap_or_else(|error| {
        crate::diag::log(&crate::diag::failed_line("KRSDK", "kurosdk_getConfigInfo packaged config", &error));
        serde_json::json!({})
    });

    let json_str = config.to_string();
    println!("[KRSDK] Returning config: {}", json_str);

    let c_str = CString::new(json_str).unwrap();
    let ptr = c_str.into_raw();

    ALLOCATED_STRINGS.lock().unwrap().push(SendPtr(ptr));
    ptr
}

#[no_mangle]
pub extern "C" fn kurosdk_getDeviceInfo() -> *mut c_char {
    println!("[KRSDK] *** kurosdk_getDeviceInfo called ***");

    let device_info = serde_json::json!({
        "did": uuid::Uuid::new_v4().to_string(),
        "idfv": "",
        "jyDid": "",
        "oaId": ""
    });

    let json_str = device_info.to_string();
    println!("[KRSDK] Returning device info: {}", json_str);

    let c_str = CString::new(json_str).unwrap();
    let ptr = c_str.into_raw();

    // Keep track of allocated strings
    ALLOCATED_STRINGS.lock().unwrap().push(SendPtr(ptr));

    ptr
}

#[no_mangle]
pub extern "C" fn kurosdk_getProtocolInfo() -> *mut c_char {
    println!("[KRSDK] *** Get Protocol Info called ***");

    let protocol_info = serde_json::json!({"data": []});

    let json_str = protocol_info.to_string();
    println!("[KRSDK] Returning protocol info: {}", json_str);

    let c_ctr = CString::new(json_str).unwrap();
    let ptr = c_ctr.into_raw();

    ALLOCATED_STRINGS.lock().unwrap().push(SendPtr(ptr));
    ptr
}

#[no_mangle]
pub extern "C" fn kurosdk_setLanguage(data: *const c_char) {
    println!("[KRSDK] *** kurosdk_setLanguage called ***");

    if data.is_null() {
        println!("[KRSDK] setLanguage: null data");
        return;
    }

    unsafe {
        let c_str = std::ffi::CStr::from_ptr(data);
        if let Ok(json_str) = c_str.to_str() {
            println!("[KRSDK] setLanguage data: {}", json_str);

            // Parse JSON to get language
            if let Ok(json) = serde_json::from_str::<serde_json::Value>(json_str) {
                if let Some(lang) = json.get("language").and_then(|v| v.as_str()) {
                    *LANGUAGE.lock().unwrap() = lang.to_string();
                    println!("[KRSDK] Language set to: {}", lang);
                }
            }
        }
    }
}
