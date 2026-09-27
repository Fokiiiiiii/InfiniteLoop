use crate::package::{compare_versions, sha256_file, validate_server_origin, PatchPackage};
use anyhow::{bail, Context, Result};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::path::{Component, Path, PathBuf};
use std::process::Command;
use uuid::Uuid;

const STATE_DIR: &str = ".ascnet-launcher";
const STATE_FILE: &str = "state.json";
const JOURNAL_FILE: &str = "journal.json";
const ORIGINAL_KEYS: [&str; 3] = ["PGR.exe", "GameAssembly.dll", "PGR_Data/Plugins/KRSDK.dll"];

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PatchState {
    Unpatched,
    Current,
    AdoptionRequired,
    UpdateAvailable,
    Unsupported(String),
    RepairRequired(String),
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct State {
    schema_version: u32,
    release_version: String,
    originals: BTreeMap<String, String>,
    files: BTreeMap<String, FileState>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FileState {
    original: Option<String>,
    installed: String,
    backup: Option<String>,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Journal {
    schema_version: u32,
    files: BTreeMap<String, RollbackFile>,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RollbackFile {
    hash: Option<String>,
    backup: Option<String>,
}

#[derive(Deserialize)]
struct LegacyManifest {
    client: String,
    pinned_client: BTreeMap<String, String>,
    files: BTreeMap<String, LegacyFile>,
}
#[derive(Deserialize)]
struct LegacyFile {
    original: Option<String>,
    installed: String,
}

struct PreparedPgrBase {
    original: Vec<u8>,
    patched: Vec<u8>,
    observed: String,
}

fn prepare_pgrbase(client: &Path, package: &PatchPackage) -> Result<(PatchPackage, Option<PreparedPgrBase>)> {
    use sha2::{Digest, Sha256};
    let mut package = package.clone();
    let Some(build) = &package.manifest.pgr_base else {
        if read_state(client)?.is_some_and(|state| state.files.contains_key("PGRBase.dll")) {
            bail!("this package cannot update a managed PGRBase startup patch; use restore or a package with verified PGRBase metadata");
        }
        return Ok((package, None));
    };
    let read_client = |relative: &str| -> Result<Vec<u8>> {
        let path = client.join(relative);
        check_target(client, &path)?;
        Ok(fs::read(path)?)
    };
    let game = read_client("PGR.exe")?;
    if !package.manifest.accepts_original("PGR.exe", Some(&format!("{:x}", Sha256::digest(&game)))) {
        bail!("{}", crate::package::unsupported_client(&package.manifest.application_version, "PGR.exe"));
    }
    let unity = read_client("UnityPlayer.dll")?;
    if !build.unity_players.contains(&format!("{:x}", Sha256::digest(&unity))) {
        bail!("unsupported UnityPlayer.dll for PGRBase startup patch");
    }
    let current = read_client("PGRBase.dll")?;
    let observed = format!("{:x}", Sha256::digest(&current));
    if !build.originals.contains(&observed) {
        let original = crate::pgrbase::original(&current, &game, &unity, &build.original_export_jump)?;
        if !build.originals.contains(&format!("{:x}", Sha256::digest(&original))) {
            bail!("unsupported PGRBase.dll: exact stock reconstruction hash does not match");
        }
    }
    // Keep any verified pre-existing Wine NOP; AscNet only installs the startup stub.
    let patched = crate::pgrbase::patch(&current, &game, &unity)?;
    package.manifest.files.push(crate::package::File {
        path: "PGRBase.dll".into(),
        source: "PGRBase.dll".into(),
        sha256: format!("{:x}", Sha256::digest(&patched)),
        size: patched.len() as u64,
    });
    Ok((package, Some(PreparedPgrBase { original: current, patched, observed })))
}

pub fn inspect(client: &Path, package: &PatchPackage) -> Result<PatchState> {
    let client = checked_client(client)?;
    let (package, _) = match prepare_pgrbase(&client, package) {
        Ok(prepared) => prepared,
        Err(error) => return Ok(PatchState::Unsupported(error.to_string())),
    };
    inspect_prepared(&client, &package)
}

fn inspect_prepared(client: &Path, package: &PatchPackage) -> Result<PatchState> {
    if client.join(STATE_DIR).join(JOURNAL_FILE).exists() {
        return Ok(PatchState::RepairRequired(
            "an interrupted transaction must be recovered by install or restore".into(),
        ));
    }
    for key in [ORIGINAL_KEYS[0], ORIGINAL_KEYS[1]] {
        let actual = file_hash(&client.join(key))?;
        if !package.manifest.accepts_original(key, actual.as_deref()) {
            return Ok(PatchState::Unsupported(crate::package::unsupported_client(&package.manifest.application_version, key)));
        }
    }

    if let Some(state) = read_state(&client)? {
        if let Some(base) = state.files.get("PGRBase.dll") {
            let actual = file_hash(&client.join("PGRBase.dll"))?;
            if actual.as_deref() != Some(&base.installed) && actual.as_deref() != base.original.as_deref() {
                return Ok(PatchState::RepairRequired("managed PGRBase.dll was modified".into()));
            }
        }
        if state.schema_version != 1 {
            return Ok(PatchState::RepairRequired(
                "unknown launcher state version".into(),
            ));
        }
        // PGR.exe/GameAssembly.dll are never modified or backed up, only validated on disk
        // above; client updates may replace them, so only KRSDK's saved original must match.
        if !package
            .manifest
            .accepts_original(ORIGINAL_KEYS[2], state.originals.get(ORIGINAL_KEYS[2]).map(String::as_str))
        {
            return Ok(PatchState::Unsupported(
                "saved retail originals do not match this release".into(),
            ));
        }
        if let Err(error) = verify_saved_backups(&client, &state) {
            return Ok(PatchState::RepairRequired(error.to_string()));
        }
        match compare_versions(&package.manifest.version, &state.release_version) {
            Ok(std::cmp::Ordering::Less) => {
                return Ok(PatchState::Unsupported("release downgrade refused".into()))
            }
            Err(error) => return Ok(PatchState::RepairRequired(error.to_string())),
            _ => {}
        }
        let mut all_target = true;
        let mut all_tracked = true;
        let mut all_original = true;
        for file in &package.manifest.files {
            let actual = file_hash(&client.join(&file.path))?;
            let old = state.files.get(&file.path);
            let new_base = file.path == "PGRBase.dll" && old.is_none();
            all_target &= !new_base && actual.as_deref() == Some(&file.sha256);
            all_tracked &= new_base || old.is_some_and(|old| actual.as_deref() == Some(&old.installed));
            all_original &= new_base || old.is_some_and(|old| actual.as_deref() == old.original.as_deref());
        }
        if all_target {
            return Ok(PatchState::Current);
        }
        if all_original {
            return Ok(PatchState::Unpatched);
        }
        if all_tracked {
            return Ok(PatchState::UpdateAvailable);
        }
        return Ok(PatchState::RepairRequired(
            "a managed patch file was modified or removed".into(),
        ));
    }

    if let Some((_path, legacy)) = find_legacy(&client, package)? {
        let mut all_target = true;
        let mut all_installed = true;
        let mut all_original = true;
        for file in &package.manifest.files {
            let actual = file_hash(&client.join(&file.path))?;
            let old = legacy.files.get(&file.path);
            let new_base = file.path == "PGRBase.dll" && old.is_none();
            all_target &= !new_base && actual.as_deref() == Some(&file.sha256);
            all_installed &= new_base || old.is_some_and(|old| actual.as_deref() == Some(&old.installed));
            all_original &= new_base || old.is_some_and(|old| actual.as_deref() == old.original.as_deref());
        }
        if all_target {
            return Ok(PatchState::AdoptionRequired);
        }
        if all_original {
            return Ok(PatchState::Unpatched);
        }
        if all_installed {
            return Ok(PatchState::UpdateAvailable);
        }
        return Ok(PatchState::RepairRequired(
            "legacy-managed patch files are inconsistent".into(),
        ));
    }

    let krsdk = ORIGINAL_KEYS[2];
    if !package
        .manifest
        .accepts_original(krsdk, file_hash(&client.join(krsdk))?.as_deref())
    {
        return Ok(PatchState::Unsupported(
            "KRSDK.dll is neither a supported retail original nor a verified managed patch".into(),
        ));
    }
    for file in &package.manifest.files {
        if file.path != krsdk && file.path != "PGRBase.dll" && file_hash(&client.join(&file.path))?.is_some() {
            return Ok(PatchState::Unsupported(format!(
                "unmanaged file would be replaced: {}",
                file.path
            )));
        }
    }
    Ok(PatchState::Unpatched)
}

/// A declined UAC request is cancellation, not a failed transaction.
#[derive(Debug)]
pub struct ElevationCancelled;
impl std::fmt::Display for ElevationCancelled {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("Administrator approval cancelled; game files were not changed")
    }
}
impl std::error::Error for ElevationCancelled {}

pub fn install_with_consent(
    client: &Path,
    package: &PatchPackage,
    progress: &mut dyn FnMut(String),
) -> Result<PathBuf> {
    #[cfg(windows)]
    if inspect(client, package)? == PatchState::Current && crate::access::document_writable(client)? {
        return Ok(client.join(STATE_DIR).join(STATE_FILE));
    }
    #[cfg(windows)]
    if needs_elevation(client)? || !crate::access::document_writable(client)? {
        let expected = package_fingerprint(package)?;
        elevate(client, Some((&package.directory, &expected)), progress)?;
        if inspect(client, package)? != PatchState::Current {
            bail!("elevated installation did not produce the verified prepared patch");
        }
        if !crate::access::document_writable(client)? {
            bail!("resource document directory remains inaccessible to this user after approval");
        }
        return Ok(client.join(STATE_DIR).join(STATE_FILE));
    }
    install(client, package, progress)
}

pub fn restore_with_consent(client: &Path, progress: &mut dyn FnMut(String)) -> Result<()> {
    #[cfg(windows)]
    if needs_elevation(client)? {
        let client = checked_client(client)?;
        let state = read_state(&client)?.context("no launcher-managed installation to restore")?;
        verify_saved_backups(&client, &state)?;
        elevate(&client, None, progress)?;
        for (relative, record) in state.files {
            if file_hash(&client.join(&relative))? != record.original {
                bail!("elevated restore did not restore the verified original: {relative}");
            }
        }
        if read_state(&client)?.is_some() || client.join(STATE_DIR).join(JOURNAL_FILE).exists() {
            bail!("elevated restore left an incomplete transaction");
        }
        return Ok(());
    }
    restore(client, progress)
}

#[cfg(windows)]
fn package_fingerprint(package: &PatchPackage) -> Result<String> {
    use sha2::{Digest, Sha256};
    Ok(format!("{:x}", Sha256::digest(serde_json::to_vec(&package.manifest)?)))
}

// Opening existing objects with the rights used by atomic replacement does not
// create a probe file or alter retail/state files before the consent prompt.
#[cfg(windows)]
fn needs_elevation(client: &Path) -> Result<bool> {
    use std::os::windows::ffi::OsStrExt;
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{CloseHandle, ERROR_ACCESS_DENIED};
    use windows::Win32::Storage::FileSystem::*;
    let client = checked_client(client)?;
    if game_running()? {
        bail!("close PGR.exe before changing game files");
    }
    let mut paths = vec![client.clone()];
    for relative in ["PGR_Data", "PGR_Data/Plugins", STATE_DIR,
        "version.dll", "lucia.dll", "libraries.txt", "PGR_Data/Plugins/KRSDK.dll", "PGRBase.dll"] {
        let path = client.join(relative);
        if path.exists() { paths.push(path); }
    }
    let mut denied = false;
    while let Some(path) = paths.pop() {
        refuse_reparse(&path)?;
        let directory = path.is_dir();
        if directory && path.starts_with(client.join(STATE_DIR)) {
            match fs::read_dir(&path) {
                Ok(entries) => { for entry in entries { paths.push(entry?.path()); } }
                Err(error) if error.kind() == std::io::ErrorKind::PermissionDenied => denied = true,
                Err(error) => return Err(error.into()),
            }
        }
        let name: Vec<u16> = path.as_os_str().encode_wide().chain(Some(0)).collect();
        // FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY; existing files need DELETE.
        let access = if directory { 0x6 } else { 0x10000 };
        match unsafe { CreateFileW(PCWSTR(name.as_ptr()), access,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, None,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, None) } {
            Ok(handle) => { unsafe { CloseHandle(handle)?; } }
            Err(error) if error.code() == windows::core::HRESULT::from_win32(ERROR_ACCESS_DENIED.0) => denied = true,
            Err(error) => return Err(error).with_context(|| format!("checking write access to {}", path.display())),
        }
    }
    Ok(denied)
}

#[cfg(windows)]
fn elevate(client: &Path, package: Option<(&Path, &str)>, progress: &mut dyn FnMut(String)) -> Result<()> {
    use std::io::Read;
    use std::net::TcpListener;
    use std::os::windows::ffi::OsStrExt;
    use windows::core::{w, PCWSTR};
    use windows::Win32::Foundation::{CloseHandle, ERROR_CANCELLED, WAIT_OBJECT_0};
    use windows::Win32::System::Threading::{GetExitCodeProcess, TerminateProcess, WaitForSingleObject};
    use windows::Win32::UI::Shell::{ShellExecuteExW, SHELLEXECUTEINFOW, SEE_MASK_NOCLOSEPROCESS, SEE_MASK_NOASYNC};
    use windows::Win32::UI::WindowsAndMessaging::SW_HIDE;
    let client = checked_client(client)?;
    let listener = TcpListener::bind(("127.0.0.1", 0))?;
    listener.set_nonblocking(true)?;
    let mut args = vec!["--patch-worker".to_owned(), client.to_string_lossy().into_owned(),
        listener.local_addr()?.port().to_string()];
    if let Some((path, fingerprint)) = package {
        args.push(fs::canonicalize(path)?.to_string_lossy().into_owned());
        args.push(fingerprint.to_owned());
        let (pid, creation) = crate::access::caller_identity()?;
        args.push(pid.to_string());
        args.push(creation.to_string());
    }
    let parameters = args.iter().map(|arg| quote_windows_arg(arg)).collect::<Vec<_>>().join(" ");
    let parameters: Vec<u16> = parameters.encode_utf16().chain(Some(0)).collect();
    let executable: Vec<u16> = std::env::current_exe()?.as_os_str().encode_wide().chain(Some(0)).collect();
    let mut info = SHELLEXECUTEINFOW {
        cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
        fMask: SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC,
        lpVerb: w!("runas"), lpFile: PCWSTR(executable.as_ptr()),
        lpParameters: PCWSTR(parameters.as_ptr()), nShow: SW_HIDE.0,
        ..Default::default()
    };
    progress("Windows administrator approval is required only to change protected game files…".into());
    if let Err(error) = unsafe { ShellExecuteExW(&mut info) } {
        if error.code() == windows::core::HRESULT::from_win32(ERROR_CANCELLED.0) {
            return Err(ElevationCancelled.into());
        }
        return Err(error).context("requesting administrator approval for game files");
    }
    let result = (|| -> Result<()> {
        let mut report = None;
        let started = std::time::Instant::now();
        loop {
            if let Ok((mut stream, _)) = listener.accept() {
                let _ = stream.set_read_timeout(Some(std::time::Duration::from_secs(2)));
                let mut bytes = vec![0; 65536];
                if let Ok(count) = stream.read(&mut bytes) {
                    if let Ok(value) = serde_json::from_slice::<std::result::Result<Vec<String>, String>>(&bytes[..count]) {
                        report = Some(value);
                    }
                }
            }
            let wait = unsafe { WaitForSingleObject(info.hProcess, 50) };
            if wait == WAIT_OBJECT_0 { break; }
            if wait != windows::Win32::Foundation::WAIT_TIMEOUT { bail!("waiting for protected-file worker failed"); }
            if started.elapsed() >= std::time::Duration::from_secs(600) {
                unsafe {
                    TerminateProcess(info.hProcess, 1).context("stopping timed-out protected-file worker")?;
                    let _ = WaitForSingleObject(info.hProcess, 30_000);
                }
                bail!("protected game-file operation timed out; any transaction journal is retained for recovery on the next install or restore");
            }
        }
        // The process exit is authoritative; loopback diagnostics alone never
        // establish success. The caller also reinspects every changed game file.
        let mut exit = 1;
        unsafe { GetExitCodeProcess(info.hProcess, &mut exit)?; }
        if report.is_none() {
            if let Ok((mut stream, _)) = listener.accept() {
                let _ = stream.set_read_timeout(Some(std::time::Duration::from_secs(2)));
                let mut bytes = vec![0; 65536];
                if let Ok(count) = stream.read(&mut bytes) {
                    report = serde_json::from_slice(&bytes[..count]).ok();
                }
            }
        }
        if exit == 124 {
            bail!("protected game-file operation timed out; any transaction journal is retained for recovery on the next install or restore");
        }
        if exit != 0 {
            if let Some(Err(error)) = report {
                bail!("protected game-file worker exited with code {exit}: {error}");
            }
            bail!("protected game-file worker exited with code {exit}");
        }
        // Diagnostics are unauthenticated and cannot veto a genuine successful
        // exit. Only the caller's independent file inspection establishes success.
        progress("Protected game-file worker finished; verifying game files…".into());
        Ok(())
    })();
    unsafe { let _ = CloseHandle(info.hProcess); }
    result
}

fn quote_windows_arg(value: &str) -> String {
    let mut output = String::from("\"");
    let mut slashes = 0;
    for c in value.chars() {
        if c == '\\' { slashes += 1; continue; }
        output.extend(std::iter::repeat('\\').take(if c == '"' { slashes * 2 + 1 } else { slashes }));
        output.push(c);
        slashes = 0;
    }
    output.extend(std::iter::repeat('\\').take(slashes * 2));
    output.push('"');
    output
}

/// Internal worker: no setup, profile lookup, process launch, or arbitrary command.
#[cfg(windows)]
pub fn patch_worker(args: &[String]) -> i32 {
    use std::net::TcpStream;
    // An unelevated parent may not have termination rights over this process.
    // Self-expiry leaves the durable journal available to the next operation.
    std::thread::spawn(|| {
        std::thread::sleep(std::time::Duration::from_secs(580));
        std::process::exit(124);
    });
    WORKER_DIRECTORIES.with(|pins| *pins.borrow_mut() = Some(BTreeMap::new()));
    let mut lines = Vec::new();
    let result = (|| -> Result<()> {
        if args.len() != 3 && args.len() != 7 { bail!("invalid patch worker arguments"); }
        let client = Path::new(&args[1]);
        if !client.is_absolute() { bail!("worker game path must be absolute"); }
        let client = checked_client(client)?;
        for name in ["PGR.exe", "GameAssembly.dll"] {
            check_target(&client, &client.join(name))?;
            if !client.join(name).is_file() { bail!("worker requires a PGR game directory"); }
        }
        // Restrict even persisted recovery/state input to the patch's fixed write set.
        validate_worker_state(&client)?;
        if args.len() == 7 {
            let directory = Path::new(&args[3]);
            if !directory.is_absolute() { bail!("worker package path must be absolute"); }
            refuse_ancestor_reparse(directory)?;
            let package = crate::package::load_package(directory)?;
            if package_fingerprint(&package)? != args[4] { bail!("prepared package changed before approval"); }
            install(&client, &package, &mut |line| lines.push(line))?;
            crate::access::prepare_document_for_caller(&client, args[5].parse()?, args[6].parse()?)?;
        } else {
            restore(&client, &mut |line| lines.push(line))?;
        }
        Ok(())
    })();
    WORKER_DIRECTORIES.with(|pins| *pins.borrow_mut() = None);
    let exit = if result.is_ok() { 0 } else { 1 };
    let report = result.map(|()| lines).map_err(|error| format!("{error:#}"));
    if let Some(port) = args.get(2).and_then(|port| port.parse::<u16>().ok()) {
        if let Ok(mut stream) = TcpStream::connect(("127.0.0.1", port)) {
            let _ = stream.set_write_timeout(Some(std::time::Duration::from_secs(2)));
            if let Ok(bytes) = serde_json::to_vec(&report) {
                let _ = stream.write_all(&bytes);
            }
        }
    }
    exit
}

#[cfg(windows)]
fn validate_worker_state(client: &Path) -> Result<()> {
    let allowed = ["version.dll", "lucia.dll", "libraries.txt", "PGR_Data/Plugins/KRSDK.dll", "PGRBase.dll"];
    let root = client.join(STATE_DIR);
    if root.exists() {
        let mut paths = vec![root.clone()];
        while let Some(path) = paths.pop() {
            refuse_reparse(&path)?;
            if path.is_dir() {
                pin_worker_directory(&path)?;
                for entry in fs::read_dir(path)? { paths.push(entry?.path()); }
            }
        }
    }
    if let Some(state) = read_state(client)? {
        if state.files.keys().any(|key| !allowed.contains(&key.as_str())) {
            bail!("saved state contains a destination outside the patch write set");
        }
    }
    let journal = root.join(JOURNAL_FILE);
    if journal.exists() {
        let journal: Journal = serde_json::from_reader(File::open(journal)?)?;
        if journal.files.keys().any(|key| !allowed.contains(&key.as_str()) && key != ".ascnet-launcher/state.json") {
            bail!("recovery journal contains a destination outside the patch write set");
        }
    }
    Ok(())
}

pub fn install(
    client: &Path,
    package: &PatchPackage,
    progress: &mut dyn FnMut(String),
) -> Result<PathBuf> {
    let _lock = OperationLock::acquire()?;
    if game_running()? {
        bail!("refusing to install while PGR.exe is running");
    }
    let client = checked_client(client)?;
    recover_if_needed(&client)?;
    validate_package_paths(package)?;
    let (prepared_package, pgr_base) = prepare_pgrbase(&client, package)?;
    let package = &prepared_package;
    let observed = inspect_prepared(&client, package)?;
    if let PatchState::Unsupported(reason) | PatchState::RepairRequired(reason) = &observed {
        bail!("refusing installation: {reason}");
    }
    if observed == PatchState::Current {
        return Ok(client.join(STATE_DIR).join(STATE_FILE));
    }

    let state_root = client.join(STATE_DIR);
    create_private_dir(&state_root)?;
    refuse_reparse(&state_root)?;

    let prior = match read_state(&client)? {
        Some(state) => {
            verify_saved_backups(&client, &state)?;
            Some(state)
        }
        None => adopt_legacy(&client, package)?,
    };
    if let Some(old) = &prior {
        if old.release_version != "legacy"
            && compare_versions(&package.manifest.version, &old.release_version)?
                == std::cmp::Ordering::Less
        {
            bail!("release downgrade refused");
        }
    }
    if observed == PatchState::AdoptionRequired {
        let mut adopted = prior.context("verified legacy backup disappeared during adoption")?;
        adopted.release_version = package.manifest.version.clone();
        for key in [ORIGINAL_KEYS[0], ORIGINAL_KEYS[1]] {
            adopted.originals.insert(key.into(), sha256_file(&client.join(key))?);
        }
        for file in &package.manifest.files {
            adopted
                .files
                .get_mut(&file.path)
                .context("legacy backup does not track every release file")?
                .installed = file.sha256.clone();
        }
        verify_saved_backups(&client, &adopted)?;
        let state_path = state_root.join(STATE_FILE);
        write_json_atomic(&state_path, &adopted)?;
        progress("Adopted verified existing patch".into());
        return Ok(state_path);
    }
    let mut originals = prior.as_ref().map(|state| state.originals.clone()).unwrap_or_default();
    for key in ORIGINAL_KEYS {
        // Saved KRSDK original is preserved; unmanaged client binaries track the verified disk copy.
        if key != ORIGINAL_KEYS[2] || !originals.contains_key(key) {
            originals.insert(key.into(), sha256_file(&client.join(key))?);
        }
    }
    let id = Uuid::new_v4().to_string();
    let backup_parent = state_root.join("backups");
    create_private_dir(&backup_parent)?;
    let backup_root = backup_parent.join(&id);
    create_private_dir(&backup_root)?;
    let mut files = BTreeMap::new();
    for file in &package.manifest.files {
        let target = client.join(&file.path);
        check_target(&client, &target)?;
        let old = prior.as_ref().and_then(|s| s.files.get(&file.path));
        let (original, backup) = if let Some(old) = old {
            if let Some(rel) = &old.backup {
                let source = client.join(STATE_DIR).join(rel);
                let dest = backup_root.join(&file.path);
                atomic_copy(&source, &dest)?;
                if file_hash(&dest)?.as_deref() != old.original.as_deref() {
                    bail!("saved original changed: {}", file.path);
                }
                (
                    old.original.clone(),
                    Some(format!("backups/{id}/{}", file.path.replace('\\', "/"))),
                )
            } else {
                (None, None)
            }
        } else if file.path == "PGRBase.dll" && pgr_base.is_some() {
            let prepared = pgr_base.as_ref().unwrap();
            if file_hash(&target)?.as_deref() != Some(&prepared.observed) {
                bail!("PGRBase.dll changed during preparation");
            }
            let dest = backup_root.join(&file.path);
            check_target(&client, &dest)?;
            fs::write(&dest, &prepared.original)?;
            (
                Some(sha256_file(&dest)?),
                Some(format!("backups/{id}/{}", file.path)),
            )
        } else if target.is_file() {
            let hash = sha256_file(&target)?;
            let dest = backup_root.join(&file.path);
            atomic_copy(&target, &dest)?;
            if sha256_file(&dest)? != hash {
                bail!("backup verification failed: {}", file.path);
            }
            (
                Some(hash),
                Some(format!("backups/{id}/{}", file.path.replace('\\', "/"))),
            )
        } else {
            (None, None)
        };
        files.insert(
            file.path.clone(),
            FileState {
                original,
                installed: file.sha256.clone(),
                backup,
            },
        );
    }
    let state = State {
        schema_version: 1,
        release_version: package.manifest.version.clone(),
        originals,
        files,
    };
    verify_saved_backups_at(&client, &state, &state_root)?;

    let rollback_root = state_root.join("rollback").join(&id);
    create_private_dir(&state_root.join("rollback"))?;
    create_private_dir(&rollback_root)?;
    let mut rollback = BTreeMap::new();
    let generated_base = rollback_root.join("PGRBase.payload");
    if let Some(prepared) = &pgr_base {
        check_target(&client, &generated_base)?;
        fs::write(&generated_base, &prepared.patched)?;
    }
    snapshot_for_rollback(
        &client,
        &state_root,
        &rollback_root,
        &format!("{STATE_DIR}/{STATE_FILE}"),
        &mut rollback,
    )?;
    for file in &package.manifest.files {
        let target = client.join(&file.path);
        if target.is_file() {
            let hash = sha256_file(&target)?;
            let saved = rollback_root.join(&file.path);
            atomic_copy(&target, &saved)?;
            rollback.insert(
                file.path.clone(),
                RollbackFile {
                    hash: Some(hash),
                    backup: Some(format!("rollback/{id}/{}", file.path.replace('\\', "/"))),
                },
            );
        } else {
            rollback.insert(
                file.path.clone(),
                RollbackFile {
                    hash: None,
                    backup: None,
                },
            );
        }
    }
    write_json_atomic(
        &state_root.join(JOURNAL_FILE),
        &Journal {
            schema_version: 1,
            files: rollback,
        },
    )?;

    let result = (|| -> Result<()> {
        for file in &package.manifest.files {
            let source = if file.path == "PGRBase.dll" && pgr_base.is_some() {
                generated_base.clone()
            } else {
                package.directory.join(&file.source)
            };
            refuse_reparse(&source)?;
            if sha256_file(&source)? != file.sha256 {
                bail!("release payload changed after verification: {}", file.path);
            }
            let target = client.join(&file.path);
            check_target(&client, &target)?;
            if file.path == "PGRBase.dll" {
                if let Some(base) = &pgr_base {
                    if file_hash(&target)?.as_deref() != Some(&base.observed) {
                        bail!("PGRBase.dll changed before installation");
                    }
                }
            }
            progress(format!("Installing {}", file.path));
            atomic_copy(&source, &target)?;
            if sha256_file(&target)? != file.sha256 {
                bail!("installed-file verification failed: {}", file.path);
            }
        }
        write_json_atomic(&state_root.join(STATE_FILE), &state)?;
        Ok(())
    })();
    if let Err(error) = result {
        recover_if_needed(&client).context("installation failed and rollback failed")?;
        return Err(error);
    }
    fs::remove_file(state_root.join(JOURNAL_FILE))?;
    remove_rollback_directory(&rollback_root);
    sync_dir(&state_root)?;
    Ok(state_root.join(STATE_FILE))
}

pub fn restore(client: &Path, progress: &mut dyn FnMut(String)) -> Result<()> {
    let _lock = OperationLock::acquire()?;
    if game_running()? {
        bail!("refusing to restore while PGR.exe is running");
    }
    let client = checked_client(client)?;
    recover_if_needed(&client)?;
    let state = read_state(&client)?.context("no launcher-managed installation to restore")?;
    verify_saved_backups(&client, &state)?;
    for (relative, record) in &state.files {
        let target = client.join(relative);
        check_target(&client, &target)?;
        let actual = file_hash(&target)?;
        if actual.as_deref() != Some(&record.installed)
            && actual.as_deref() != record.original.as_deref()
        {
            bail!("refusing to overwrite modified managed file: {relative}");
        }
    }
    let state_root = client.join(STATE_DIR);
    let id = Uuid::new_v4().to_string();
    let rollback_root = state_root.join("rollback").join(&id);
    create_private_dir(&state_root.join("rollback"))?;
    create_private_dir(&rollback_root)?;
    let mut rollback = BTreeMap::new();
    snapshot_for_rollback(
        &client,
        &state_root,
        &rollback_root,
        &format!("{STATE_DIR}/{STATE_FILE}"),
        &mut rollback,
    )?;
    for (relative, _) in &state.files {
        let target = client.join(relative);
        if target.is_file() {
            let saved = rollback_root.join(relative);
            atomic_copy(&target, &saved)?;
            rollback.insert(
                relative.clone(),
                RollbackFile {
                    hash: Some(sha256_file(&target)?),
                    backup: Some(format!("rollback/{id}/{}", relative.replace('\\', "/"))),
                },
            );
        } else {
            rollback.insert(
                relative.clone(),
                RollbackFile {
                    hash: None,
                    backup: None,
                },
            );
        }
    }
    write_json_atomic(
        &state_root.join(JOURNAL_FILE),
        &Journal {
            schema_version: 1,
            files: rollback,
        },
    )?;
    let result = (|| -> Result<()> {
        for (relative, record) in &state.files {
            progress(format!("Restoring {relative}"));
            let target = client.join(relative);
            if let Some(backup) = &record.backup {
                atomic_copy(&state_root.join(backup), &target)?;
            } else if target.exists() {
                fs::remove_file(&target)?;
                sync_dir(target.parent().unwrap())?;
            }
        }
        fs::remove_file(state_root.join(STATE_FILE))?;
        sync_dir(&state_root)?;
        Ok(())
    })();
    if let Err(error) = result {
        recover_if_needed(&client).context("restore failed and rollback failed")?;
        return Err(error);
    }
    fs::remove_file(state_root.join(JOURNAL_FILE))?;
    remove_rollback_directory(&rollback_root);
    Ok(())
}

fn remove_rollback_directory(path: &Path) {
    #[cfg(windows)]
    WORKER_DIRECTORIES.with(|pins| {
        if let Some(pins) = pins.borrow_mut().as_mut() {
            pins.retain(|directory, _| !directory.starts_with(path));
        }
    });
    // std's remove_dir_all does not follow directory links. Release only this
    // finished rollback subtree; its ancestors and live transaction stay pinned.
    let _ = fs::remove_dir_all(path);
}

pub fn game_running() -> Result<bool> {
    #[cfg(windows)]
    {
        let output = crate::local::hide_console(&mut Command::new("tasklist.exe"))
            .args(["/FI", "IMAGENAME eq PGR.exe", "/FO", "CSV", "/NH"])
            .output()
            .context("querying running processes")?;
        if !output.status.success() {
            bail!("tasklist failed; refusing to assume the game is stopped");
        }
        Ok(String::from_utf8_lossy(&output.stdout).lines().any(|line| {
            line.trim_start_matches('\u{feff}')
                .starts_with("\"PGR.exe\",")
        }))
    }
    #[cfg(not(windows))]
    {
        Ok(false)
    }
}

pub fn launch(client: &Path, origin: &str) -> Result<()> {
    let _lock = OperationLock::acquire()?;
    if game_running()? {
        bail!("PGR.exe is already running");
    }
    let client = checked_client(client)?;
    let origin = validate_server_origin(origin)?;
    let executable = client.join("PGR.exe");
    if !executable.is_file() {
        bail!("PGR.exe is missing");
    }
    let mut command = Command::new(executable);
    command
        .current_dir(&client)
        .env("ASCNET_PATCH_ORIGIN", origin);
    for key in [
        "ASCNET_PATCH_TRACE",
        "ASCNET_PATCH_PROBE",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
        "no_proxy",
    ] {
        command.env_remove(key);
    }
    if running_under_wine() {
        let old = std::env::var("WINEDLLOVERRIDES").unwrap_or_default();
        let merged = if old.is_empty() {
            "version=n,b".into()
        } else {
            format!("{old};version=n,b")
        };
        command.env("WINEDLLOVERRIDES", merged);
    }
    command.spawn().context("launching PGR.exe")?;
    Ok(())
}
#[cfg(windows)]
fn running_under_wine() -> bool {
    use windows::core::{s, w};
    use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};

    unsafe {
        GetModuleHandleW(w!("ntdll.dll"))
            .ok()
            .and_then(|module| GetProcAddress(module, s!("wine_get_version")))
            .is_some()
    }
}

#[cfg(not(windows))]
fn running_under_wine() -> bool {
    false
}

fn checked_client(client: &Path) -> Result<PathBuf> {
    #[cfg(windows)]
    pin_worker_directory(&std::path::absolute(client)?)?;
    #[cfg(windows)]
    refuse_ancestor_reparse(&std::path::absolute(client)?)?;
    let client = fs::canonicalize(client)
        .with_context(|| format!("invalid game directory: {}", client.display()))?;
    if !client.is_dir() {
        bail!("game path is not a directory");
    }
    refuse_reparse(&client)?;
    Ok(client)
}

#[cfg(windows)]
thread_local! {
    // Holding directory handles without WRITE/DELETE sharing prevents both
    // junction conversion and ancestor replacement throughout elevated writes.
    static WORKER_DIRECTORIES: std::cell::RefCell<Option<BTreeMap<PathBuf, File>>> =
        const { std::cell::RefCell::new(None) };
}

#[cfg(windows)]
fn pin_worker_directory(path: &Path) -> Result<()> {
    use std::os::windows::fs::{MetadataExt, OpenOptionsExt};
    use windows::Win32::Storage::FileSystem::{
        FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_READ,
    };
    WORKER_DIRECTORIES.with(|pins| -> Result<()> {
        let mut pins = pins.borrow_mut();
        let Some(pins) = pins.as_mut() else { return Ok(()); };
        let mut ancestors: Vec<_> = path.ancestors().collect();
        ancestors.reverse();
        for directory in ancestors {
            if pins.contains_key(directory) { continue; }
            // Metadata-only opens do not participate in Windows share checks.
            let handle = OpenOptions::new().read(true)
                .share_mode(FILE_SHARE_READ.0)
                .custom_flags(FILE_FLAG_BACKUP_SEMANTICS.0 | FILE_FLAG_OPEN_REPARSE_POINT.0)
                .open(directory).with_context(|| format!("locking protected write directory {}", directory.display()))?;
            let metadata = handle.metadata()?;
            if !metadata.is_dir() || metadata.file_attributes() & 0x400 != 0 {
                bail!("refusing link/reparse write directory: {}", directory.display());
            }
            pins.insert(directory.to_path_buf(), handle);
        }
        Ok(())
    })
}

#[cfg(windows)]
fn refuse_ancestor_reparse(path: &Path) -> Result<()> {
    for ancestor in path.ancestors() {
        if ancestor.as_os_str().is_empty() { continue; }
        refuse_reparse(ancestor)?;
    }
    Ok(())
}

fn validate_package_paths(package: &PatchPackage) -> Result<()> {
    for file in &package.manifest.files {
        validate_relative(&file.path)?;
        validate_relative(&file.source)?;
        let source = package.directory.join(&file.source);
        check_contained(&package.directory, &source)?;
    }
    Ok(())
}
fn validate_relative(value: &str) -> Result<()> {
    let path = Path::new(value);
    if value.contains('\\')
        || path
            .components()
            .any(|c| !matches!(c, Component::Normal(_)))
    {
        bail!("unsafe relative path: {value}");
    }
    Ok(())
}
fn check_target(client: &Path, target: &Path) -> Result<()> {
    check_contained(client, target)?;
    let mut cursor = client.to_path_buf();
    let relative = target.strip_prefix(client)?;
    #[cfg(windows)]
    pin_worker_directory(target.parent().context("target has no parent")?)?;
    for part in relative.components() {
        cursor.push(part);
        if cursor.exists() {
            refuse_reparse(&cursor)?;
        }
    }
    if target.exists() && !target.is_file() {
        bail!("refusing non-regular destination: {}", target.display());
    }
    Ok(())
}
fn check_contained(root: &Path, path: &Path) -> Result<()> {
    if !path.starts_with(root) {
        bail!("path escapes trusted root: {}", path.display());
    }
    Ok(())
}
fn refuse_reparse(path: &Path) -> Result<()> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() {
        bail!("refusing link/reparse path: {}", path.display());
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if metadata.file_attributes() & 0x400 != 0 {
            bail!("refusing link/reparse path: {}", path.display());
        }
    }
    Ok(())
}
fn file_hash(path: &Path) -> Result<Option<String>> {
    if !path.exists() {
        return Ok(None);
    }
    refuse_reparse(path)?;
    if !path.is_file() {
        bail!("not a regular file: {}", path.display());
    }
    Ok(Some(sha256_file(path)?))
}
fn create_private_dir(path: &Path) -> Result<()> {
    #[cfg(windows)]
    if WORKER_DIRECTORIES.with(|pins| pins.borrow().is_some()) {
        let path = std::path::absolute(path)?;
        let mut ancestors: Vec<_> = path.ancestors().collect();
        ancestors.reverse();
        for directory in ancestors {
            if !directory.exists() { fs::create_dir(directory)?; }
            pin_worker_directory(directory)?;
        }
        return Ok(());
    }
    fs::create_dir_all(path).with_context(|| format!("creating {}", path.display()))
}

fn atomic_copy(source: &Path, destination: &Path) -> Result<()> {
    let parent = destination.parent().context("destination has no parent")?;
    create_private_dir(parent)?;
    let temporary = parent.join(format!(
        ".{}.{}.tmp",
        destination.file_name().unwrap().to_string_lossy(),
        Uuid::new_v4()
    ));
    let result = (|| -> Result<()> {
        let mut input = open_copy_source(source)?;
        let mut output = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)?;
        std::io::copy(&mut input, &mut output)?;
        output.sync_all()?;
        drop(output);
        atomic_replace(&temporary, destination)
            .with_context(|| format!("committing {}", destination.display()))?;
        sync_dir(parent)?;
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}
fn open_copy_source(path: &Path) -> Result<File> {
    #[cfg(windows)]
    {
        use std::os::windows::fs::{MetadataExt, OpenOptionsExt};
        use windows::Win32::Storage::FileSystem::{FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_READ};
        pin_worker_directory(path.parent().context("copy source has no parent")?)?;
        let file = OpenOptions::new().read(true).share_mode(FILE_SHARE_READ.0)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT.0).open(path)?;
        let metadata = file.metadata()?;
        if !metadata.is_file() || metadata.file_attributes() & 0x400 != 0 {
            bail!("refusing non-regular copy source: {}", path.display());
        }
        Ok(file)
    }
    #[cfg(not(windows))]
    { Ok(File::open(path)?) }
}

fn atomic_replace(source: &Path, destination: &Path) -> Result<()> {
    #[cfg(windows)]
    {
        use std::os::windows::{ffi::OsStrExt, fs::OpenOptionsExt, io::AsRawHandle};
        use windows::Wdk::Storage::FileSystem::{
            NtSetInformationFile, FileRenameInformation, FILE_RENAME_INFORMATION,
        };
        use windows::core::PCWSTR;
        use windows::Win32::Foundation::{HANDLE, STATUS_OBJECT_PATH_SYNTAX_BAD};
        use windows::Win32::Storage::FileSystem::{
            DELETE, SYNCHRONIZE, FILE_FLAG_OPEN_REPARSE_POINT,
            MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
        };
        use windows::Win32::System::IO::IO_STATUS_BLOCK;

        // All callers stage a sibling. A full destination path makes Windows
        // reopen its parent for FILE_WRITE_DATA, conflicting with our directory
        // pin. A native bare-name rename stays in the source's pinned directory.
        anyhow::ensure!(source.parent() == destination.parent(), "atomic replacement requires sibling paths");
        let name: Vec<u16> = destination.file_name().context("destination has no filename")?
            .encode_wide().collect();
        let bytes = std::mem::size_of::<FILE_RENAME_INFORMATION>() + name.len() * 2;
        // usize storage provides the alignment required by the trailing-array ABI.
        let mut storage = vec![0usize; bytes.div_ceil(std::mem::size_of::<usize>())];
        let info = storage.as_mut_ptr().cast::<FILE_RENAME_INFORMATION>();
        let file = OpenOptions::new().access_mode((DELETE | SYNCHRONIZE).0)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT.0).open(source)?;
        let mut status = IO_STATUS_BLOCK::default();
        let renamed = unsafe {
            (*info).Anonymous.ReplaceIfExists = true.into();
            (*info).FileNameLength = (name.len() * 2).try_into()?;
            std::ptr::copy_nonoverlapping(name.as_ptr(), (*info).FileName.as_mut_ptr(), name.len());
            NtSetInformationFile(
                HANDLE(file.as_raw_handle() as isize), &mut status, info.cast(), bytes.try_into()?,
                FileRenameInformation,
            )
        };
        if renamed != STATUS_OBJECT_PATH_SYNTAX_BAD {
            return renamed.ok().with_context(|| format!("atomically replacing {}", destination.display()));
        }
        // Wine does not implement NULL-root bare-name renames. Its path-based
        // rename works with the same pins; never fall back on a sharing failure.
        drop(file);
        let src: Vec<u16> = source.as_os_str().encode_wide().chain(Some(0)).collect();
        let dst: Vec<u16> = destination.as_os_str().encode_wide().chain(Some(0)).collect();
        unsafe {
            MoveFileExW(
                PCWSTR(src.as_ptr()), PCWSTR(dst.as_ptr()),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
            ).with_context(|| format!("atomically replacing {}", destination.display()))
        }
    }
    #[cfg(not(windows))]
    {
        fs::rename(source, destination).map_err(Into::into)
    }
}
#[cfg(not(windows))]
fn sync_dir(path: &Path) -> Result<()> {
    File::open(path)
        .with_context(|| format!("opening directory for durability sync: {}", path.display()))?
        .sync_all()
        .with_context(|| format!("syncing directory: {}", path.display()))
}
#[cfg(windows)]
fn sync_dir(_path: &Path) -> Result<()> {
    // File contents are flushed before the atomic, same-directory Windows rename.
    // Directory fsync is not available through std::fs::File on Windows.
    Ok(())
}
fn write_json_atomic<T: Serialize>(path: &Path, value: &T) -> Result<()> {
    let parent = path.parent().unwrap();
    create_private_dir(parent)?;
    let temp = parent.join(format!(
        ".{}.{}.tmp",
        path.file_name().unwrap().to_string_lossy(),
        Uuid::new_v4()
    ));
    let result = (|| -> Result<()> {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temp)?;
        serde_json::to_writer_pretty(&mut file, value)?;
        file.write_all(b"\n")?;
        file.sync_all()?;
        drop(file);
        atomic_replace(&temp, path)?;
        sync_dir(parent)
    })();
    if result.is_err() {
        let _ = fs::remove_file(temp);
    }
    result
}
fn read_state(client: &Path) -> Result<Option<State>> {
    let path = client.join(STATE_DIR).join(STATE_FILE);
    if !path.exists() {
        return Ok(None);
    }
    refuse_reparse(&path)?;
    Ok(Some(
        serde_json::from_reader(File::open(path)?).context("invalid launcher state")?,
    ))
}
fn verify_saved_backups(client: &Path, state: &State) -> Result<()> {
    verify_saved_backups_at(client, state, &client.join(STATE_DIR))
}
fn verify_saved_backups_at(client: &Path, state: &State, root: &Path) -> Result<()> {
    for (relative, record) in &state.files {
        validate_relative(relative)?;
        match (&record.original, &record.backup) {
            (Some(expected), Some(backup)) => {
                validate_relative(backup)?;
                let path = root.join(backup);
                check_contained(root, &path)?;
                if file_hash(&path)?.as_deref() != Some(expected) {
                    bail!("backup is missing or modified: {relative}");
                }
            }
            (None, None) => {}
            _ => bail!("invalid backup record: {relative}"),
        }
    }
    let _ = client;
    Ok(())
}
fn snapshot_for_rollback(
    client: &Path,
    state_root: &Path,
    rollback_root: &Path,
    relative: &str,
    rollback: &mut BTreeMap<String, RollbackFile>,
) -> Result<()> {
    let source = client.join(relative);
    if source.is_file() {
        let hash = sha256_file(&source)?;
        let saved = rollback_root.join(relative);
        atomic_copy(&source, &saved)?;
        rollback.insert(
            relative.into(),
            RollbackFile {
                hash: Some(hash),
                backup: Some(
                    saved
                        .strip_prefix(state_root)?
                        .to_string_lossy()
                        .replace('\\', "/"),
                ),
            },
        );
    } else {
        rollback.insert(
            relative.into(),
            RollbackFile {
                hash: None,
                backup: None,
            },
        );
    }
    Ok(())
}
fn recover_if_needed(client: &Path) -> Result<()> {
    let root = client.join(STATE_DIR);
    let path = root.join(JOURNAL_FILE);
    if !path.exists() {
        return Ok(());
    }
    let journal: Journal = serde_json::from_reader(File::open(&path)?)
        .context("invalid transaction journal; refusing mutation")?;
    if journal.schema_version != 1 {
        bail!("unknown transaction journal version");
    }
    for (relative, record) in &journal.files {
        validate_relative(relative)?;
        let target = client.join(relative);
        check_target(client, &target)?;
        match (&record.hash, &record.backup) {
            (Some(expected), Some(backup)) => {
                validate_relative(backup)?;
                let saved = root.join(backup);
                check_contained(&root, &saved)?;
                if file_hash(&saved)?.as_deref() != Some(expected) {
                    bail!("rollback backup is missing or modified: {relative}");
                }
                atomic_copy(&saved, &target)?;
            }
            (None, None) => {
                if target.exists() {
                    fs::remove_file(&target)?;
                    sync_dir(target.parent().unwrap())?;
                }
            }
            _ => bail!("invalid rollback record: {relative}"),
        }
    }
    fs::remove_file(path)?;
    sync_dir(&root)?;
    Ok(())
}

fn find_legacy(client: &Path, package: &PatchPackage) -> Result<Option<(PathBuf, LegacyManifest)>> {
    let root = client
        .parent()
        .context("client has no parent")?
        .join("patch_backups");
    if !root.is_dir() {
        return Ok(None);
    }
    let mut manifests = fs::read_dir(&root)?
        .filter_map(|e| e.ok())
        .map(|e| e.path().join("manifest.json"))
        .filter(|p| p.is_file())
        .collect::<Vec<_>>();
    manifests.sort();
    manifests.reverse();
    for path in manifests {
        let manifest: LegacyManifest = match serde_json::from_reader(File::open(&path)?) {
            Ok(v) => v,
            Err(_) => continue,
        };
        if !legacy_client_matches(client, &manifest.client) {
            continue;
        }
        if !package
            .manifest
            .accepts_original(ORIGINAL_KEYS[2], manifest.pinned_client.get(ORIGINAL_KEYS[2]).map(String::as_str))
        {
            continue;
        }
        let mut valid = true;
        for (relative, record) in &manifest.files {
            if validate_relative(relative).is_err() {
                valid = false;
                break;
            }
            let actual = file_hash(&client.join(relative))?;
            if actual.as_deref() != Some(&record.installed)
                && actual.as_deref() != record.original.as_deref()
            {
                valid = false;
                break;
            }
            if let Some(original) = &record.original {
                if file_hash(&path.parent().unwrap().join(relative))?.as_deref() != Some(original) {
                    valid = false;
                    break;
                }
            }
        }
        if valid {
            return Ok(Some((path, manifest)));
        }
    }
    Ok(None)
}
fn legacy_client_matches(client: &Path, claimed: &str) -> bool {
    if Path::new(claimed).canonicalize().ok().as_deref() == Some(client) {
        return true;
    }
    #[cfg(windows)]
    {
        let normalized = claimed.replace('/', "\\");
        let mapped = if normalized.starts_with("\\Volumes\\") {
            format!("Z:{normalized}")
        } else {
            normalized
        };
        if Path::new(&mapped).canonicalize().ok().as_deref() == Some(client) {
            return true;
        }
    }
    false
}
fn adopt_legacy(client: &Path, package: &PatchPackage) -> Result<Option<State>> {
    let Some((manifest_path, legacy)) = find_legacy(client, package)? else {
        return Ok(None);
    };
    let id = Uuid::new_v4().to_string();
    let root = client.join(STATE_DIR);
    let parent = root.join("backups");
    create_private_dir(&parent)?;
    let backup_root = parent.join(&id);
    create_private_dir(&backup_root)?;
    let mut files = BTreeMap::new();
    for file in &package.manifest.files {
        if file.path == "PGRBase.dll" && !legacy.files.contains_key(&file.path) {
            continue;
        }
        let old = legacy
            .files
            .get(&file.path)
            .context("legacy manifest does not track every release file")?;
        let backup = if let Some(original) = &old.original {
            let dest = backup_root.join(&file.path);
            atomic_copy(&manifest_path.parent().unwrap().join(&file.path), &dest)?;
            if file_hash(&dest)?.as_deref() != Some(original) {
                bail!("legacy backup changed while adopting");
            }
            Some(format!("backups/{id}/{}", file.path))
        } else {
            None
        };
        files.insert(
            file.path.clone(),
            FileState {
                original: old.original.clone(),
                installed: old.installed.clone(),
                backup,
            },
        );
    }
    Ok(Some(State {
        schema_version: 1,
        release_version: "legacy".into(),
        originals: legacy.pinned_client,
        files,
    }))
}

struct OperationLock {
    #[cfg(windows)]
    handle: windows::Win32::Foundation::HANDLE,
    #[cfg(not(windows))]
    _guard: std::sync::MutexGuard<'static, ()>,
}
impl OperationLock {
    fn acquire() -> Result<Self> {
        #[cfg(windows)]
        {
            use windows::core::w;
            use windows::Win32::Foundation::{WAIT_ABANDONED, WAIT_OBJECT_0};
            use windows::Win32::System::Threading::{CreateMutexW, WaitForSingleObject, INFINITE};
            let handle =
                unsafe { CreateMutexW(None, false, w!("Local\\AscNetLauncherOperations"))? };
            let timeout = WORKER_DIRECTORIES.with(|pins| if pins.borrow().is_some() { 60_000 } else { INFINITE });
            let result = unsafe { WaitForSingleObject(handle, timeout) };
            if result != WAIT_OBJECT_0 && result != WAIT_ABANDONED {
                unsafe {
                    let _ = windows::Win32::Foundation::CloseHandle(handle);
                }
                bail!("could not acquire launcher operation lock");
            }
            Ok(Self { handle })
        }
        #[cfg(not(windows))]
        {
            static LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
            Ok(Self {
                _guard: LOCK
                    .lock()
                    .map_err(|_| anyhow::anyhow!("launcher operation lock poisoned"))?,
            })
        }
    }
}
#[cfg(windows)]
impl Drop for OperationLock {
    fn drop(&mut self) {
        unsafe {
            let _ = windows::Win32::System::Threading::ReleaseMutex(self.handle);
            let _ = windows::Win32::Foundation::CloseHandle(self.handle);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn temp() -> PathBuf {
        let p = std::env::temp_dir().join(format!("ascnet-install-test-{}", Uuid::new_v4()));
        fs::create_dir_all(&p).unwrap();
        p
    }
    #[test]
    fn rollback_preserves_previous_managed_state() {
        let client = temp();
        let root = client.join(STATE_DIR);
        let rollback = root.join("rollback/check");
        fs::create_dir_all(&rollback).unwrap();
        fs::write(root.join(STATE_FILE), b"previous-state").unwrap();
        let mut files = BTreeMap::new();
        snapshot_for_rollback(&client, &root, &rollback,
            &format!("{STATE_DIR}/{STATE_FILE}"), &mut files).unwrap();
        write_json_atomic(&root.join(JOURNAL_FILE), &Journal { schema_version: 1, files }).unwrap();
        fs::write(root.join(STATE_FILE), b"changed-state").unwrap();
        recover_if_needed(&client).unwrap();
        assert_eq!(fs::read(root.join(STATE_FILE)).unwrap(), b"previous-state");
        fs::remove_dir_all(client).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn writable_directory_needs_no_elevation() {
        let client = temp();
        fs::write(client.join("libraries.txt"), b"retail").unwrap();
        let needs_consent = needs_elevation(&client);
        let unchanged = fs::read(client.join("libraries.txt")).unwrap();
        let state_created = client.join(STATE_DIR).exists();
        fs::remove_dir_all(client).unwrap();
        assert!(!needs_consent.unwrap());
        assert_eq!(unchanged, b"retail");
        assert!(!state_created);
    }

    #[cfg(windows)]
    #[test]
    #[ignore = "native Windows only: changes ACLs solely on a disposable directory; close PGR first"]
    fn protected_directory_requires_consent_without_mutation() {
        let client = temp();
        let target = client.join("libraries.txt");
        fs::write(&target, b"retail").unwrap();
        let denied = Command::new("icacls.exe").arg(&client)
            .args(["/deny", "*S-1-1-0:(W,D)"]).status().unwrap();
        assert!(denied.success());
        let result = needs_elevation(&client);
        let granted = Command::new("icacls.exe").arg(&client)
            .args(["/remove:d", "*S-1-1-0"]).status().unwrap();
        assert!(granted.success());
        assert!(result.unwrap());
        assert_eq!(fs::read(target).unwrap(), b"retail");
        assert!(!client.join(STATE_DIR).exists());
        fs::remove_dir_all(client).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn worker_refuses_unrelated_recovery_destination_without_mutation() {
        let client = temp();
        fs::write(client.join("unrelated.txt"), b"leave alone").unwrap();
        let root = client.join(STATE_DIR);
        fs::create_dir_all(&root).unwrap();
        write_json_atomic(&root.join(JOURNAL_FILE), &Journal {
            schema_version: 1,
            files: BTreeMap::from([("unrelated.txt".into(), RollbackFile { hash: None, backup: None })]),
        }).unwrap();
        assert!(validate_worker_state(&client).is_err());
        assert_eq!(fs::read(client.join("unrelated.txt")).unwrap(), b"leave alone");
        fs::remove_dir_all(client).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn worker_pins_prevent_directory_replacement() {
        let root = temp();
        let nested = root.join("created/inside");
        WORKER_DIRECTORIES.with(|pins| *pins.borrow_mut() = Some(BTreeMap::new()));
        let created = create_private_dir(&nested);
        let replaced = fs::rename(&nested, root.join("moved"));
        WORKER_DIRECTORIES.with(|pins| *pins.borrow_mut() = None);
        created.unwrap();
        assert!(replaced.is_err(), "a pinned write directory must not be replaceable");
        fs::rename(&nested, root.join("moved")).unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn worker_pins_allow_new_nested_backup_and_atomic_replacement() {
        let root = temp();
        let source = root.join("KRSDK.dll");
        let backup = root.join(STATE_DIR).join(format!("backups/{}/PGR_Data/Plugins/KRSDK.dll", Uuid::new_v4()));
        fs::write(&source, b"retail").unwrap();
        WORKER_DIRECTORIES.with(|pins| *pins.borrow_mut() = Some(BTreeMap::new()));
        let result = (|| -> Result<()> {
            atomic_copy(&source, &backup)?;
            assert_eq!(fs::read(&backup)?, b"retail");
            fs::write(&source, b"managed")?;
            atomic_copy(&source, &backup)?;
            assert_eq!(fs::read(&backup)?, b"managed");
            assert!(fs::rename(backup.parent().unwrap(), root.join("moved")).is_err());
            Ok(())
        })();
        WORKER_DIRECTORIES.with(|pins| *pins.borrow_mut() = None);
        fs::remove_dir_all(root).unwrap();
        result.unwrap();
    }

    #[test]
    fn rollback_restores_and_removes() {
        let client = temp();
        fs::write(client.join("a"), b"old").unwrap();
        let root = client.join(STATE_DIR);
        fs::create_dir_all(root.join("rollback/x")).unwrap();
        fs::write(root.join("rollback/x/a"), b"old").unwrap();
        let hash = sha256_file(&client.join("a")).unwrap();
        fs::write(client.join("a"), b"new").unwrap();
        fs::write(client.join("b"), b"new").unwrap();
        let journal = Journal {
            schema_version: 1,
            files: BTreeMap::from([
                (
                    "a".into(),
                    RollbackFile {
                        hash: Some(hash),
                        backup: Some("rollback/x/a".into()),
                    },
                ),
                (
                    "b".into(),
                    RollbackFile {
                        hash: None,
                        backup: None,
                    },
                ),
            ]),
        };
        write_json_atomic(&root.join(JOURNAL_FILE), &journal).unwrap();
        recover_if_needed(&client).unwrap();
        assert_eq!(fs::read(client.join("a")).unwrap(), b"old");
        assert!(!client.join("b").exists());
        let _ = fs::remove_dir_all(client);
    }
    #[test]
    fn modified_backup_is_rejected() {
        let client = temp();
        let root = client.join(STATE_DIR);
        fs::create_dir_all(root.join("backups/x")).unwrap();
        fs::write(root.join("backups/x/a"), b"changed").unwrap();
        let state = State {
            schema_version: 1,
            release_version: "1".into(),
            originals: BTreeMap::new(),
            files: BTreeMap::from([(
                "a".into(),
                FileState {
                    original: Some("00".repeat(32)),
                    installed: "11".repeat(32),
                    backup: Some("backups/x/a".into()),
                },
            )]),
        };
        assert!(verify_saved_backups(&client, &state).is_err());
        let _ = fs::remove_dir_all(client);
    }
    #[test]
    fn traversal_and_links_are_refused() {
        assert!(validate_relative("../PGR.exe").is_err());
        assert!(validate_relative("a\\b").is_err());
    }
    #[test]
    fn unknown_state_is_not_overwritten() {
        let client = temp();
        let root = client.join(STATE_DIR);
        fs::create_dir_all(&root).unwrap();
        fs::write(
            root.join(STATE_FILE),
            br#"{"schemaVersion":1,"releaseVersion":"1","originals":{},"files":{},"foreign":true}"#,
        )
        .unwrap();
        assert!(read_state(&client).is_err());
        assert!(fs::read_to_string(root.join(STATE_FILE))
            .unwrap()
            .contains("foreign"));
        let _ = fs::remove_dir_all(client);
    }
    #[test]
    fn restore_refuses_modified_managed_file() {
        let client = temp();
        fs::write(client.join("a"), b"foreign").unwrap();
        let root = client.join(STATE_DIR);
        fs::create_dir_all(&root).unwrap();
        let state = State {
            schema_version: 1,
            release_version: "1".into(),
            originals: BTreeMap::new(),
            files: BTreeMap::from([(
                "a".into(),
                FileState {
                    original: None,
                    installed: "00".repeat(32),
                    backup: None,
                },
            )]),
        };
        write_json_atomic(&root.join(STATE_FILE), &state).unwrap();
        let error = restore(&client, &mut |_| {}).unwrap_err().to_string();
        assert!(error.contains("modified managed file"));
        let _ = fs::remove_dir_all(client);
    }
    #[test]
    fn retained_legacy_restore_and_local_update_preserve_retail_originals() {
        for assembly in [b"assembly-stock".as_slice(), b"assembly-wine".as_slice()] {
            check_retail_originals(assembly);
        }
    }

    fn check_retail_originals(assembly: &[u8]) {
        use crate::package::{File, Manifest};
        use sha2::Digest;
        let root = temp();
        let client = root.join("game");
        let payload = root.join("release");
        fs::create_dir_all(client.join("PGR_Data/Plugins")).unwrap();
        fs::create_dir_all(&payload).unwrap();
        fs::write(client.join("PGR.exe"), b"exe").unwrap();
        fs::write(client.join("GameAssembly.dll"), assembly).unwrap();
        fs::write(client.join("PGR_Data/Plugins/KRSDK.dll"), b"retail-sdk").unwrap();
        let originals: BTreeMap<String, String> = BTreeMap::from([
            (
                "PGR.exe".into(),
                sha256_file(&client.join("PGR.exe")).unwrap(),
            ),
            (
                "GameAssembly.dll".into(),
                sha256_file(&client.join("GameAssembly.dll")).unwrap(),
            ),
            (
                "PGR_Data/Plugins/KRSDK.dll".into(),
                sha256_file(&client.join("PGR_Data/Plugins/KRSDK.dll")).unwrap(),
            ),
        ]);
        let specs = [
            ("version.dll", "version.dll", b"version".as_slice()),
            ("lucia.dll", "lucia.dll", b"lucia".as_slice()),
            (
                "PGR_Data/Plugins/KRSDK.dll",
                "KRSDK.dll",
                b"patched-sdk".as_slice(),
            ),
            (
                "libraries.txt",
                "libraries.txt",
                b"*PGR.exe\nlucia.dll\n".as_slice(),
            ),
        ];
        let files = specs
            .iter()
            .map(|(path, source, bytes)| {
                fs::write(payload.join(source), bytes).unwrap();
                File {
                    path: (*path).into(),
                    source: (*source).into(),
                    sha256: sha256_file(&payload.join(source)).unwrap(),
                    size: bytes.len() as u64,
                }
            })
            .collect::<Vec<_>>();
        let legacy = root.join("patch_backups/legacy");
        fs::create_dir_all(legacy.join("PGR_Data/Plugins")).unwrap();
        fs::write(legacy.join("PGR_Data/Plugins/KRSDK.dll"), b"retail-sdk").unwrap();
        let records = files
            .iter()
            .map(|file| {
                let original = originals.get(&file.path).cloned();
                (
                    file.path.clone(),
                    serde_json::json!({"original": original, "installed": file.sha256.clone()}),
                )
            })
            .collect::<serde_json::Map<_, _>>();
        fs::write(
            legacy.join("manifest.json"),
            serde_json::to_vec(&serde_json::json!({
                "client": fs::canonicalize(&client).unwrap(),
                "pinned_client": originals.clone(),
                "files": records
            }))
            .unwrap(),
        )
        .unwrap();
        let package = PatchPackage {
            manifest: Manifest {
                schema_version: 1,
                version: "1.0.0".into(),
                application_version: "test".into(),
                pgr_base: None,
                originals: originals
                    .clone()
                    .into_iter()
                    .map(|(key, hash)| {
                        let hashes = if key == "GameAssembly.dll" {
                            [b"assembly-stock", b"assembly-wine".as_slice()]
                                .iter()
                                .map(|bytes| format!("{:x}", sha2::Sha256::digest(bytes)))
                                .collect()
                        } else {
                            vec![hash]
                        };
                        (key, hashes)
                    })
                    .collect(),
                files,
            },
            directory: payload,
        };
        fs::write(client.join("GameAssembly.dll"), b"unknown").unwrap();
        assert!(matches!(inspect(&client, &package).unwrap(), PatchState::Unsupported(_)));
        fs::write(client.join("GameAssembly.dll"), assembly).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Unpatched);
        for file in &package.manifest.files {
            let target = client.join(&file.path);
            fs::create_dir_all(target.parent().unwrap()).unwrap();
            fs::copy(package.directory.join(&file.source), target).unwrap();
        }
        assert_eq!(
            inspect(&client, &package).unwrap(),
            PatchState::AdoptionRequired
        );
        install(&client, &package, &mut |_| {}).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Current);
        assert_eq!(read_state(&client).unwrap().unwrap().originals, originals);
        restore(&client, &mut |_| {}).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Unpatched);
        fs::remove_dir_all(client.join(STATE_DIR)).unwrap();
        fs::remove_dir_all(&legacy).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Unpatched);
        install(&client, &package, &mut |_| {}).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Current);
        assert_eq!(read_state(&client).unwrap().unwrap().originals, originals);
        let payload_v2 = root.join("package-v2");
        fs::create_dir_all(&payload_v2).unwrap();
        let mut manifest_v2 = package.manifest.clone();
        manifest_v2.version = "2.0.0".into();
        for file in &mut manifest_v2.files {
            let bytes = format!("v2 payload for {}", file.path);
            fs::write(payload_v2.join(&file.source), bytes.as_bytes()).unwrap();
            file.sha256 = sha256_file(&payload_v2.join(&file.source)).unwrap();
            file.size = bytes.len() as u64;
        }
        let package_v2 = PatchPackage {
            manifest: manifest_v2,
            directory: payload_v2,
        };
        assert_eq!(
            inspect(&client, &package_v2).unwrap(),
            PatchState::UpdateAvailable
        );
        install(&client, &package_v2, &mut |_| {}).unwrap();
        assert_eq!(inspect(&client, &package_v2).unwrap(), PatchState::Current);
        // A newer supported client replaces PGR.exe/GameAssembly.dll under an existing patch.
        let hash = |bytes: &[u8]| format!("{:x}", sha2::Sha256::digest(bytes));
        let mut package_v3 = package.clone();
        package_v3.manifest.version = "3.0.0".into();
        package_v3.manifest.originals.insert("PGR.exe".into(), vec![hash(b"exe-new")]);
        package_v3.manifest.originals.insert("GameAssembly.dll".into(), vec![hash(b"assembly-new")]);
        fs::write(client.join("PGR.exe"), b"exe-new").unwrap();
        fs::write(client.join("GameAssembly.dll"), b"assembly-new").unwrap();
        assert_eq!(inspect(&client, &package_v3).unwrap(), PatchState::UpdateAvailable);
        install(&client, &package_v3, &mut |_| {}).unwrap();
        let state = read_state(&client).unwrap().unwrap();
        assert_eq!(state.originals["PGR.exe"], hash(b"exe-new"));
        assert_eq!(state.originals["GameAssembly.dll"], hash(b"assembly-new"));
        assert_eq!(state.originals["PGR_Data/Plugins/KRSDK.dll"], originals["PGR_Data/Plugins/KRSDK.dll"]);
        // A saved KRSDK original outside the release allowlist is still refused.
        let mut bad = state.clone();
        bad.originals.insert("PGR_Data/Plugins/KRSDK.dll".into(), "00".repeat(32));
        let state_path = client.join(STATE_DIR).join(STATE_FILE);
        write_json_atomic(&state_path, &bad).unwrap();
        assert!(matches!(inspect(&client, &package_v3).unwrap(), PatchState::Unsupported(_)));
        write_json_atomic(&state_path, &state).unwrap();
        fs::write(client.join("PGR.exe"), b"exe").unwrap();
        fs::write(client.join("GameAssembly.dll"), assembly).unwrap();
        restore(&client, &mut |_| {}).unwrap();
        assert_eq!(fs::read(client.join("PGR.exe")).unwrap(), b"exe");
        assert_eq!(
            fs::read(client.join("GameAssembly.dll")).unwrap(),
            assembly
        );
        assert_eq!(
            fs::read(client.join("PGR_Data/Plugins/KRSDK.dll")).unwrap(),
            b"retail-sdk"
        );
        assert!(!client.join("version.dll").exists());
        assert!(!client.join("lucia.dll").exists());
        assert!(!client.join("libraries.txt").exists());
        let _ = fs::remove_dir_all(root);
    }
    #[test]
    fn pgrbase_transaction_restores_stock_and_preexisting_wine_bytes() {
        use crate::package::{File as Payload, Manifest, PgrBaseBuild};
        use sha2::{Digest, Sha256};
        let hash = |bytes: &[u8]| format!("{:x}", Sha256::digest(bytes));
        for wine in [false, true] {
            let root = temp();
            let client = root.join("game");
            let directory = root.join("package");
            fs::create_dir_all(client.join("PGR_Data/Plugins")).unwrap();
            fs::create_dir_all(&directory).unwrap();
            let (mut stock, game, unity) = crate::pgrbase::fixture();
            stock[0x800..0x814].copy_from_slice(&[
                0x21, 0xca, 0x21, 0xca, 0x81, 0xf2, 0xb3, 0xa5, 0xd6, 0x7a,
                0x0f, 0x1f, 0xc2, 0x41, 0x8b, 0x0a, 0x50, 0x48, 0x8d, 0x05,
            ]);
            let mut before = stock.clone();
            if wine {
                before = crate::pgrbase::patch(&before, &game, &unity).unwrap();
                before[0x80a..0x80d].fill(0x90);
            }
            fs::write(client.join("PGRBase.dll"), &before).unwrap();
            fs::write(client.join("UnityPlayer.dll"), &unity).unwrap();
            let originals = [
                ("PGR.exe", game.as_slice()),
                ("GameAssembly.dll", b"stock-assembly".as_slice()),
                ("PGR_Data/Plugins/KRSDK.dll", b"stock-sdk".as_slice()),
            ].into_iter().map(|(path, bytes)| {
                fs::write(client.join(path), bytes).unwrap();
                (path.into(), vec![hash(bytes)])
            }).collect();
            fs::write(directory.join("version.dll"), b"loader").unwrap();
            let package = PatchPackage {
                manifest: Manifest {
                    schema_version: 1,
                    version: "2.0.0".into(),
                    application_version: "fixture".into(),
                    originals,
                    pgr_base: Some(PgrBaseBuild {
                        originals: vec![hash(&stock)],
                        unity_players: vec![hash(&unity)],
                        original_export_jump: stock[0x500..0x505].try_into().unwrap(),
                    }),
                    files: vec![Payload {
                        path: "version.dll".into(), source: "version.dll".into(),
                        sha256: hash(b"loader"), size: 6,
                    }],
                },
                directory,
            };
            let mut unknown = before.clone();
            unknown[0x900] ^= 1;
            fs::write(client.join("PGRBase.dll"), &unknown).unwrap();
            assert!(matches!(inspect(&client, &package).unwrap(), PatchState::Unsupported(_)));
            assert!(install(&client, &package, &mut |_| {}).is_err());
            assert_eq!(fs::read(client.join("PGRBase.dll")).unwrap(), unknown);
            assert!(!client.join(STATE_DIR).exists());
            fs::write(client.join("PGRBase.dll"), &before).unwrap();
            // Upgrade a managed installation whose state predates PGRBase tracking.
            let mut old = package.clone();
            old.manifest.version = "1.0.0".into();
            old.manifest.pgr_base = None;
            install(&client, &old, &mut |_| {}).unwrap();
            assert_eq!(inspect(&client, &package).unwrap(), PatchState::UpdateAvailable);
            install(&client, &package, &mut |_| {}).unwrap();
            assert_eq!(inspect(&client, &package).unwrap(), PatchState::Current);
            let expected = crate::pgrbase::patch(&before, &game, &unity).unwrap();
            assert_eq!(fs::read(client.join("PGRBase.dll")).unwrap(), expected);
            let state = read_state(&client).unwrap().unwrap();
            assert_eq!(state.files["PGRBase.dll"].original.as_deref(), Some(hash(&before).as_str()));
            assert_eq!(fs::read(client.join(STATE_DIR).join(state.files["PGRBase.dll"].backup.as_ref().unwrap())).unwrap(), before);
            restore(&client, &mut |_| {}).unwrap();
            assert_eq!(fs::read(client.join("PGRBase.dll")).unwrap(), before);
            assert!(!client.join("version.dll").exists());
            // Failure after a payload is installed rolls back all files and state.
            let mut failed = package.clone();
            failed.manifest.files.push(Payload {
                path: "lucia.dll".into(), source: "missing.dll".into(),
                sha256: hash(b"missing"), size: 7,
            });
            assert!(install(&client, &failed, &mut |_| {}).is_err());
            assert_eq!(fs::read(client.join("PGRBase.dll")).unwrap(), before);
            assert!(!client.join("version.dll").exists());
            assert!(read_state(&client).unwrap().is_none());
            // Corrupt the generated payload after preparation: post-copy verification
            // must roll back even after PGRBase itself has been replaced.
            let result = install(&client, &package, &mut |line| {
                if line == "Installing PGRBase.dll" {
                    for entry in fs::read_dir(client.join(STATE_DIR).join("rollback")).unwrap() {
                        let payload = entry.unwrap().path().join("PGRBase.payload");
                        if payload.is_file() { fs::write(payload, b"changed after verification").unwrap(); }
                    }
                }
            });
            assert!(result.is_err());
            assert_eq!(fs::read(client.join("PGRBase.dll")).unwrap(), before);
            assert!(!client.join("version.dll").exists());
            assert!(read_state(&client).unwrap().is_none());
            fs::remove_dir_all(root).unwrap();
        }
    }
}
