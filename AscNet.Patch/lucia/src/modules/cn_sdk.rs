//! China (CN) client: the retail `KRSDKEx.dll` stays in place and keeps its own login UI; its HTTP goes through
//! `libkrsdkcurl.dll!kr_sdk_curl_easy_setopt(CURLOPT_URL, ...)` (the only curl import that sets a URL), so
//! `/sdkcom/*` requests are pointed at the AscNet origin right there. See Docs/cn-sdk-support.md.

#[path = "cn_sdk_routing.rs"]
mod cn_sdk_routing;

use std::{
    collections::VecDeque,
    ffi::CString,
    os::windows::ffi::OsStrExt,
    path::PathBuf,
    sync::{
        atomic::{AtomicUsize, Ordering},
        Mutex,
    },
};

use anyhow::{anyhow, Context, Result};
use ilhook::x64::Registers;
use windows::{
    core::{s, PCWSTR},
    Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryExW, LOAD_WITH_ALTERED_SEARCH_PATH},
};

use super::{network, MhyContext, MhyModule, ModuleType};
use crate::diag;
use crate::util::{c_string, readable};

const CONFIG: &str = "PGR_Data/Plugins/KRSDKRes/KRSDKConfig.json";
const KRSDK_BIN: &str = "PGR_Data/Plugins/KRSDKRes/KRSDK.bin";
const CURL: &str = "PGR_Data/Plugins/libkrsdkcurl.dll";
const SETOPT: &str = "kr_sdk_curl_easy_setopt";
/// Cap of the "routed ..." log lines (the SDK heartbeats for the whole session).
const LOG_LIMIT: usize = 100;

static ROUTED: AtomicUsize = AtomicUsize::new(0);
// ponytail: curl copies CURLOPT_URL before setopt returns; the ring only has to outlive the call.
static REPLACEMENTS: Mutex<VecDeque<CString>> = Mutex::new(VecDeque::new());

pub struct CnSdk;

fn client_file(relative: &str) -> Option<PathBuf> {
    Some(std::env::current_exe().ok()?.parent()?.join(relative))
}

/// CN ships `KRSDKConfig.json` and no `KRSDK.bin`; EN/TW/KR/JP are the opposite.
pub fn is_cn_client() -> bool {
    client_file(CONFIG).is_some_and(|path| path.is_file()) && client_file(KRSDK_BIN).is_some_and(|path| !path.exists())
}

/// One diagnostic line: the identity `KRSDKConfig.json` carries, or why it could not be read.
pub fn identity_line() -> String {
    let text = client_file(CONFIG)
        .ok_or_else(|| "cannot locate the game executable".to_string())
        .and_then(|path| std::fs::read_to_string(&path).map_err(|error| format!("cannot read {} ({error})", path.display())));
    match text {
        Ok(text) => {
            let get = |key| cn_sdk_routing::config_string(&text, key).unwrap_or("<missing>");
            diag::ok_line(
                "lucia",
                "KRSDKConfig.json identity",
                &format!(
                    "package={} game={} app={} channel={} channel_name={} source={CONFIG}",
                    get("KR_PackageName"), get("KR_ProjectId"), get("KR_ProductId"), get("KR_ChannelId"), get("KR_ChannelName")
                ),
            )
        }
        Err(reason) => diag::failed_line("lucia", "KRSDKConfig.json identity", &reason),
    }
}

impl MhyModule for MhyContext<CnSdk> {
    unsafe fn init(&mut self) -> Result<()> {
        let origin = network::routing_origin().context("native routing origin is not initialized")?;
        let library = client_file(CURL).ok_or_else(|| anyhow!("cannot locate the game executable"))?;
        let wide: Vec<u16> = library.as_os_str().encode_wide().chain(Some(0)).collect();
        // Loaded by full path first so its own dependencies (libssl/libcrypto/krsdk_zlib) resolve from the plugin
        // directory; KRSDKEx.dll's import of `libkrsdkcurl.dll` later binds to this already-hooked instance.
        let module = LoadLibraryExW(PCWSTR(wide.as_ptr()), None, LOAD_WITH_ALTERED_SEARCH_PATH)
            .with_context(|| format!("loading {} failed (CN client incomplete or not the verified 4.8.0 SDK)", library.display()))?;
        let target = GetProcAddress(module, s!("kr_sdk_curl_easy_setopt"))
            .ok_or_else(|| anyhow!("{CURL} does not export {SETOPT} (not the verified 4.8.0 CN SDK)"))? as usize;
        if std::env::var("ASCNET_PATCH_PROBE").as_deref() == Ok("1") {
            diag::log(&diag::ok_line("lucia", "CN SDK hook install skipped", "ASCNET_PATCH_PROBE=1 probe-only mode, no hook installed"));
            return Ok(());
        }
        self.interceptor.attach(target, CnSdk::on_setopt).with_context(|| {
            format!("installing the {SETOPT} detour failed (ilhook could not patch the prologue at 0x{target:X}; another injector/overlay may have hooked it)")
        })?;
        diag::log(&diag::ok_line("lucia", "CN SDK hook install", &format!("{CURL}!{SETOPT} detour at 0x{target:X}, routing /sdkcom/* to {origin}")));
        Ok(())
    }

    unsafe fn de_init(&mut self) -> Result<()> { Ok(()) }
    fn get_module_type(&self) -> ModuleType { ModuleType::CnSdk }
}

impl CnSdk {
    /// `CURLcode kr_sdk_curl_easy_setopt(CURL*, CURLoption, ...)`: the URL is the first vararg (`r8`, spilled by the
    /// prologue after this hook runs, so replacing the register replaces the argument).
    unsafe extern "win64" fn on_setopt(reg: *mut Registers, _: usize) {
        if (*reg).rdx as u32 != cn_sdk_routing::CURLOPT_URL || !readable((*reg).r8 as usize, 1) {
            return;
        }
        let Some(original) = c_string((*reg).r8 as *const i8) else { return };
        let Some(origin) = network::routing_origin() else { return };
        let Some(replacement) = cn_sdk_routing::redirected_url(origin, &original).and_then(|url| CString::new(url).ok()) else { return };
        if ROUTED.fetch_add(1, Ordering::Relaxed) < LOG_LIMIT {
            diag::log(&format!(
                "[lucia] routed {} -> {}",
                network::safe_url(&original),
                network::safe_url(&replacement.to_string_lossy())
            ));
        }
        let pointer = replacement.as_ptr() as u64;
        let Ok(mut keep) = REPLACEMENTS.lock() else { return };
        keep.push_back(replacement);
        if keep.len() > 64 {
            keep.pop_front();
        }
        (*reg).r8 = pointer;
    }
}
