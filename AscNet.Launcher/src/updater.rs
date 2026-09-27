//! GitHub HTTPS/repository ownership is the release trust root, not a signing key.
use anyhow::{bail, ensure, Context, Result};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{collections::BTreeMap, fs::{self, File, OpenOptions}, io::{Read, Write}, path::{Path, PathBuf}, process::Command, time::{Duration, Instant}};
use crate::package::sha256_file;

const FILES: [&str; 7] = ["AscNetLauncher.exe", "background.bmp", "background.mp4", "background.wav", "launcher.json", "setup-local.ps1", "supported-client.json"];
const MAX_ARCHIVE: u64 = 128 * 1024 * 1024;
const MAX_EXPANDED: u64 = 256 * 1024 * 1024;
const TRANSACTION: &str = ".ascnet-launcher-update";
// Retained in the executable and checked without executing downloaded code.
static VERSION_MARKER: &str = concat!("ASCNET_LAUNCHER_VERSION=", env!("CARGO_PKG_VERSION"), "\0");

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Release {
    pub version: String,
    url: String,
    digest: String,
    size: u64,
}

#[derive(Debug)]
pub struct StagedUpdate { root: PathBuf }
impl Drop for StagedUpdate {
    fn drop(&mut self) { let _ = fs::remove_dir_all(&self.root); }
}

pub fn updates_suppressed() -> bool {
    std::env::var_os("ASCNET_DISABLE_AUTOMATIC_UPDATES").is_some_and(|value| value == "1")
        || std::env::args().any(|arg| arg == "--self-update-rolled-back")
}

pub fn acknowledge_startup() -> Result<()> {
    if std::env::args().any(|arg| arg == "--self-update-health") {
        synced_write(&install_dir()?.join(TRANSACTION).join("committed"), b"healthy")?;
    }
    Ok(())
}

#[derive(Serialize, Deserialize)]
struct Journal {
    release: Release,
    files: BTreeMap<String, String>,
    old: BTreeMap<String, Option<String>>,
}

fn repository_path(repository: &str) -> Result<String> {
    let path = repository.strip_prefix("https://github.com/").context("repository must use https://github.com/owner/repository")?.trim_end_matches('/');
    let path = path.strip_suffix(".git").unwrap_or(path);
    let parts: Vec<_> = path.split('/').collect();
    ensure!(parts.len() == 2 && parts.iter().all(|p| !p.is_empty() && *p != "." && *p != ".." && p.bytes().all(|b| b.is_ascii_alphanumeric() || b"._-".contains(&b))), "invalid GitHub repository");
    Ok(path.to_owned())
}

fn stable_version(value: &str) -> Option<semver::Version> {
    let version = semver::Version::parse(value.strip_prefix('v').unwrap_or(value)).ok()?;
    (version.pre.is_empty() && version.build.is_empty()).then_some(version)
}

fn client() -> Result<reqwest::blocking::Client> {
    Ok(reqwest::blocking::Client::builder().https_only(true).user_agent(concat!("AscNetLauncher/", env!("CARGO_PKG_VERSION")))
        .connect_timeout(Duration::from_secs(15)).timeout(Duration::from_secs(180))
        .redirect(reqwest::redirect::Policy::custom(|attempt| {
            let url = attempt.url();
            if attempt.previous().len() < 5 && url.scheme() == "https" && url.port_or_known_default() == Some(443)
                && matches!(url.host_str(), Some("github.com" | "api.github.com" | "release-assets.githubusercontent.com" | "objects.githubusercontent.com")) {
                attempt.follow()
            } else { attempt.error("untrusted release redirect") }
        })).build()?)
}

#[derive(Deserialize)]
struct ApiRelease { tag_name: String, draft: bool, prerelease: bool, assets: Vec<ApiAsset> }
#[derive(Deserialize)]
struct ApiAsset { name: String, browser_download_url: String, size: u64, digest: Option<String>, state: String }

pub fn check(repository: &str, current_version: &str) -> Result<Option<Release>> {
    let repository = repository_path(repository)?;
    let current = stable_version(current_version).context("compiled launcher version is not stable semver")?;
    let response = client()?.get(format!("https://api.github.com/repos/{repository}/releases?per_page=100"))
        .header("Accept", "application/vnd.github+json").header("X-GitHub-Api-Version", "2022-11-28").send()?.error_for_status()?;
    let mut bytes = Vec::new();
    response.take(2 * 1024 * 1024 + 1).read_to_end(&mut bytes)?;
    ensure!(bytes.len() <= 2 * 1024 * 1024, "release metadata exceeds limit");
    let releases: Vec<ApiRelease> = serde_json::from_slice(&bytes)?;
    let Some((version, release)) = releases.into_iter().filter(|r| !r.draft && !r.prerelease)
        .filter_map(|r| stable_version(&r.tag_name).map(|v| (v, r))).filter(|(v, _)| v > &current).max_by(|a,b| a.0.cmp(&b.0)) else { return Ok(None) };
    let assets: Vec<_> = release.assets.into_iter().filter(|a| a.name == "AscNetLauncher.zip").collect();
    ensure!(assets.len() == 1, "release must contain exactly one AscNetLauncher.zip");
    let asset = &assets[0];
    ensure!(asset.state == "uploaded" && asset.size > 0 && asset.size <= MAX_ARCHIVE, "invalid release asset size/state");
    let digest = asset.digest.as_deref().and_then(|d| d.strip_prefix("sha256:")).context("GitHub asset SHA-256 digest is required")?;
    ensure!(digest.len() == 64 && digest.bytes().all(|b| b.is_ascii_hexdigit()), "invalid GitHub SHA-256 digest");
    let expected = format!("https://github.com/{repository}/releases/download/{}/AscNetLauncher.zip", release.tag_name);
    ensure!(asset.browser_download_url == expected, "release asset URL does not match repository/tag");
    Ok(Some(Release { version: version.to_string(), url: expected, digest: digest.to_ascii_lowercase(), size: asset.size }))
}

fn regular(path: &Path) -> Result<()> {
    let metadata = fs::symlink_metadata(path)?;
    ensure!(metadata.is_file() && !metadata.file_type().is_symlink(), "not a regular file: {}", path.display());
    #[cfg(windows)] {
        use std::os::windows::fs::MetadataExt;
        ensure!(metadata.file_attributes() & 0x400 == 0, "reparse file refused");
    }
    Ok(())
}
fn directory(path: &Path) -> Result<()> {
    for ancestor in path.ancestors() {
        let metadata = fs::symlink_metadata(ancestor)?;
        ensure!(metadata.is_dir() && !metadata.file_type().is_symlink(), "linked update directory refused");
        #[cfg(windows)] {
            use std::os::windows::fs::MetadataExt;
            ensure!(metadata.file_attributes() & 0x400 == 0, "reparse directory refused");
        }
    }
    Ok(())
}
fn install_dir() -> Result<PathBuf> {
    let exe = std::env::current_exe()?;
    ensure!(exe.file_name().and_then(|s| s.to_str()) == Some("AscNetLauncher.exe"), "self-update requires AscNetLauncher.exe deployment");
    let root = exe.parent().context("launcher has no directory")?.to_path_buf();
    directory(&root)?;
    Ok(root)
}
fn synced_write(path: &Path, bytes: &[u8]) -> Result<()> {
    ensure!(!path.exists(), "durable update record already exists");
    let temporary = path.with_extension(format!("tmp-{}", uuid::Uuid::new_v4()));
    let mut file = OpenOptions::new().write(true).create_new(true).open(&temporary)?;
    file.write_all(bytes)?;
    file.sync_all()?;
    drop(file);
    replace(&temporary, path)?;
    Ok(())
}
fn extract(archive: &Path, target: &Path, version: &str) -> Result<BTreeMap<String, String>> {
    let mut zip = zip::ZipArchive::new(File::open(archive)?)?;
    ensure!(zip.len() == FILES.len(), "launcher ZIP must contain exactly seven flat files");
    let mut hashes = BTreeMap::new();
    let mut expanded = 0u64;
    for index in 0..zip.len() {
        let mut member = zip.by_index(index)?;
        let name = member.name().to_owned();
        ensure!(FILES.contains(&name.as_str()) && !hashes.contains_key(&name), "unexpected or duplicate ZIP member: {name}");
        ensure!(!member.is_dir() && member.unix_mode().map_or(true, |m| m & 0o170000 == 0 || m & 0o170000 == 0o100000), "nonregular ZIP member");
        ensure!(matches!(member.compression(), zip::CompressionMethod::Stored | zip::CompressionMethod::Deflated), "unsupported ZIP compression");
        expanded = expanded.checked_add(member.size()).context("expanded size overflow")?;
        ensure!(member.size() > 0 && member.size() <= MAX_ARCHIVE && expanded <= MAX_EXPANDED, "launcher ZIP exceeds expanded size limit");
        let mut output = OpenOptions::new().write(true).create_new(true).open(target.join(&name))?;
        let expected = member.size();
        let copied = std::io::copy(&mut (&mut member).take(expected + 1), &mut output)?;
        ensure!(copied == expected, "ZIP member size mismatch");
        output.sync_all()?;
        hashes.insert(name.clone(), sha256_file(&target.join(name))?);
    }
    verify_version(&target.join(FILES[0]), version)?;
    Ok(hashes)
}
fn verify_version(exe: &Path, version: &str) -> Result<()> {
    regular(exe)?;
    ensure!(fs::metadata(exe)?.len() <= MAX_ARCHIVE, "executable too large");
    let bytes = fs::read(exe)?;
    ensure!(bytes.starts_with(b"MZ"), "launcher asset is not a Windows executable");
    let marker = format!("ASCNET_LAUNCHER_VERSION={version}\0");
    ensure!(bytes.windows(marker.len()).any(|w| w == marker.as_bytes()), "release tag does not match embedded launcher version");
    Ok(())
}

pub fn stage(release: &Release) -> Result<StagedUpdate> {
    let install = install_dir()?;
    ensure!(!install.join(TRANSACTION).exists(), "a launcher update needs recovery first");
    let root = install.join(format!(".ascnet-stage-{}", uuid::Uuid::new_v4()));
    fs::create_dir(&root)?;
    let result = (|| -> Result<()> {
        let archive = root.join("release.zip");
        // Metadata keeps the client's 180 s total; the ~93 MB asset needs slow-link headroom.
        let response = client()?.get(&release.url).timeout(Duration::from_secs(2 * 60 * 60)).send()?.error_for_status()?;
        if let Some(length) = response.content_length() { ensure!(length == release.size, "release Content-Length mismatch"); }
        let mut input = response.take(release.size + 1);
        let mut output = OpenOptions::new().write(true).create_new(true).open(&archive)?;
        let mut hash = Sha256::new();
        let mut buffer = [0u8; 65536];
        let mut size = 0u64;
        loop { let n = input.read(&mut buffer)?; if n == 0 { break; } size += n as u64; ensure!(size <= release.size, "release download exceeds size"); hash.update(&buffer[..n]); output.write_all(&buffer[..n])?; }
        output.sync_all()?;
        ensure!(size == release.size && format!("{:x}", hash.finalize()) == release.digest, "release SHA-256/size verification failed");
        fs::create_dir(root.join("new"))?;
        let files = extract(&archive, &root.join("new"), &release.version)?;
        let journal = Journal { release: release.clone(), files, old: BTreeMap::new() };
        synced_write(&root.join("staged.json"), &serde_json::to_vec(&journal)?)?;
        Ok(())
    })();
    if result.is_err() { let _ = fs::remove_dir_all(&root); }
    result?;
    Ok(StagedUpdate { root })
}

fn read_journal(root: &Path, name: &str) -> Result<Journal> {
    directory(root)?;
    let path = root.join(name);
    regular(&path)?;
    ensure!(fs::metadata(&path)?.len() < 16384, "update journal too large");
    let journal: Journal = serde_json::from_slice(&fs::read(path)?)?;
    ensure!(journal.files.len() == FILES.len() && FILES.iter().all(|n| journal.files.contains_key(*n)), "invalid update file set");
    ensure!(journal.old.keys().all(|n| FILES.contains(&n.as_str()) && n != "launcher.json"), "invalid backup file set");
    ensure!(name != "journal.json" || journal.old.len() == FILES.len() - 1, "incomplete backup journal");
    ensure!(stable_version(&journal.release.version).is_some(), "invalid journal version");
    Ok(journal)
}
fn verify_files(root: &Path, files: &BTreeMap<String, String>) -> Result<()> {
    directory(root)?;
    for (name, hash) in files { regular(&root.join(name))?; ensure!(sha256_file(&root.join(name))? == *hash, "update file changed: {name}"); }
    Ok(())
}

pub fn launch_update(staged: StagedUpdate) -> Result<()> {
    #[cfg(not(windows))] { let _ = staged; bail!("self-update is supported on Windows only") }
    #[cfg(windows)] {
        let install = install_dir()?;
        ensure!(staged.root.parent() == Some(install.as_path()), "staging directory is not beside launcher");
        let journal = read_journal(&staged.root, "staged.json")?;
        verify_files(&staged.root.join("new"), &journal.files)?;
        let transaction = install.join(TRANSACTION);
        ensure!(!transaction.exists(), "update transaction already exists");
        fs::rename(&staged.root, &transaction)?;
        if let Err(error) = spawn_worker(&install, &transaction) {
            // No mutation is possible until this process exits; leave staging recoverable.
            return Err(error);
        }
        Ok(())
    }
}

#[cfg(windows)]
fn spawn_worker(install: &Path, transaction: &Path) -> Result<()> {
    use std::os::windows::process::CommandExt;
    let helper_root = install.join(format!(".ascnet-worker-{}", uuid::Uuid::new_v4()));
    fs::create_dir(&helper_root)?;
    synced_write(&helper_root.join("owner"), b"AscNet launcher update helper")?;
    let helper = helper_root.join("helper.exe");
    fs::copy(std::env::current_exe()?, &helper)?;
    ensure!(sha256_file(&helper)? == sha256_file(&std::env::current_exe()?)?, "helper copy verification failed");
    OpenOptions::new().write(true).open(&helper)?.sync_all()?;
    let ready = transaction.join("ready");
    if ready.exists() { regular(&ready)?; fs::remove_file(&ready)?; }
    let mut child = Command::new(helper).args(["--self-update-worker", &std::process::id().to_string()]).arg(install)
        .creation_flags(0x08000000).spawn().context("starting launcher replacement helper")?;
    let deadline = Instant::now() + Duration::from_secs(15);
    loop {
        if ready.is_file() { return Ok(()); }
        if let Some(status) = child.try_wait()? { bail!("update helper exited before readiness: {status}"); }
        if Instant::now() >= deadline { child.kill()?; child.wait()?; bail!("update helper readiness timed out"); }
        std::thread::sleep(Duration::from_millis(25));
    }
}

// Windows rename cannot replace an existing target; MoveFileEx is atomic on the same volume.
fn replace(source: &Path, target: &Path) -> Result<()> {
    #[cfg(windows)] {
        use std::os::windows::ffi::OsStrExt;
        use windows::{core::PCWSTR, Win32::Storage::FileSystem::{MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH}};
        let source: Vec<_> = source.as_os_str().encode_wide().chain(Some(0)).collect();
        let target: Vec<_> = target.as_os_str().encode_wide().chain(Some(0)).collect();
        unsafe { MoveFileExW(PCWSTR(source.as_ptr()), PCWSTR(target.as_ptr()), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)?; }
    }
    #[cfg(not(windows))] fs::rename(source, target)?;
    Ok(())
}
fn rollback(install: &Path, root: &Path, journal: &Journal) -> Result<()> {
    directory(install)?;
    directory(&root.join("old"))?;
    for (name, hash) in &journal.old {
        let target = install.join(name);
        if target.exists() { regular(&target)?; }
        if let Some(hash) = hash {
            let backup = root.join("old").join(name);
            regular(&backup)?;
            ensure!(sha256_file(&backup)? == *hash, "rollback backup corrupted: {name}");
            let temporary = root.join(format!("restore-{name}"));
            if temporary.exists() { regular(&temporary)?; fs::remove_file(&temporary)?; }
            fs::copy(&backup, &temporary)?;
            OpenOptions::new().write(true).open(&temporary)?.sync_all()?;
            replace(&temporary, &target)?;
        } else if target.exists() { fs::remove_file(target)?; }
    }
    Ok(())
}

#[cfg(windows)]
fn worker(pid: u32, install: &Path) -> Result<()> {
    use windows::{core::w, Win32::{Foundation::{CloseHandle, WAIT_OBJECT_0, WAIT_ABANDONED, WAIT_TIMEOUT}, System::Threading::{CreateMutexW, OpenProcess, WaitForSingleObject, PROCESS_SYNCHRONIZE}}};
    use std::os::windows::ffi::OsStringExt;
    use windows::{core::PWSTR, Win32::System::Threading::{QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION}};
    struct Handle(windows::Win32::Foundation::HANDLE);
    impl Drop for Handle { fn drop(&mut self) { unsafe { let _ = CloseHandle(self.0); } } }
    directory(install)?;
    let root = install.join(TRANSACTION);
    directory(&root)?;
    let executable = std::env::current_exe()?;
    let helper_root = executable.parent().context("helper has no directory")?;
    directory(helper_root)?;
    ensure!(helper_root.parent() == Some(install) && helper_root.file_name().and_then(|n| n.to_str()).and_then(|n| n.strip_prefix(".ascnet-worker-")).is_some_and(|n| uuid::Uuid::parse_str(n).is_ok()), "worker must run from its owned helper directory");
    let mutex = Handle(unsafe { CreateMutexW(None, false, w!("Local\\AscNetLauncherSelfUpdate"))? });
    let locked = unsafe { WaitForSingleObject(mutex.0, 0) };
    ensure!(locked == WAIT_OBJECT_0 || locked == WAIT_ABANDONED, "another launcher update helper is active");
    use windows::Win32::System::Diagnostics::ToolHelp::{CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS};
    let snapshot = Handle(unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)? });
    let mut entry = PROCESSENTRY32W { dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32, ..Default::default() };
    unsafe { Process32FirstW(snapshot.0, &mut entry)?; }
    loop {
        if entry.th32ProcessID == std::process::id() {
            ensure!(entry.th32ParentProcessID == pid, "update helper was not spawned by the named launcher");
            break;
        }
        unsafe { Process32NextW(snapshot.0, &mut entry).context("locating update helper parent")?; }
    }
    let parent = Handle(unsafe { OpenProcess(PROCESS_SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid)? });
    ensure!(unsafe { WaitForSingleObject(parent.0, 0) } == WAIT_TIMEOUT, "parent exited before helper acquired its handle");
    let mut image = vec![0u16; 32768];
    let mut length = image.len() as u32;
    unsafe { QueryFullProcessImageNameW(parent.0, PROCESS_NAME_WIN32, PWSTR(image.as_mut_ptr()), &mut length)?; }
    let image = PathBuf::from(std::ffi::OsString::from_wide(&image[..length as usize]));
    ensure!(fs::canonicalize(&image)? == fs::canonicalize(install.join(FILES[0]))?, "update parent is not the installed launcher");
    ensure!(sha256_file(&image)? == sha256_file(&executable)?, "update helper is not a copy of its parent");
    synced_write(&root.join("ready"), b"ready")?;
    ensure!(unsafe { WaitForSingleObject(parent.0, 120_000) } == WAIT_OBJECT_0, "launcher did not exit for update");
    let ui = Handle(unsafe { CreateMutexW(None, false, w!("Local\\AscNetLauncherUI"))? });
    let ui_wait = unsafe { WaitForSingleObject(ui.0, 0) };
    ensure!(ui_wait == WAIT_OBJECT_0 || ui_wait == WAIT_ABANDONED, "another launcher UI is still active");
    drop(ui);
    let outcome = (|| -> Result<()> {
    let recovering = root.join("journal.json").exists();
    let mut journal = read_journal(&root, if recovering { "journal.json" } else { "staged.json" })?;
    if recovering {
        let committed = root.join("committed").exists();
        if !committed {
            rollback(install, &root, &journal)?;
        } else {
            let managed = journal.files.iter().filter(|(n, _)| n.as_str() != "launcher.json").map(|(n,h)| (n.clone(),h.clone())).collect();
            verify_files(install, &managed)?;
            verify_version(&install.join(FILES[0]), &journal.release.version)?;
        }
        retire(&root)?;
        relaunch(install, Some("--self-update-rolled-back"))?;
        return Ok(());
    }
    verify_files(&root.join("new"), &journal.files)?;
    verify_version(&root.join("new").join(FILES[0]), &journal.release.version)?;
    if root.join("old").exists() {
        directory(&root.join("old"))?;
        fs::remove_dir_all(root.join("old"))?;
    }
    fs::create_dir(root.join("old"))?;
    for name in FILES.into_iter().filter(|n| *n != "launcher.json") {
        let target = install.join(name);
        let hash = if target.exists() {
            regular(&target)?;
            let hash = sha256_file(&target)?;
            let backup = root.join("old").join(name);
            fs::copy(&target, &backup)?;
            OpenOptions::new().write(true).open(&backup)?.sync_all()?;
            ensure!(sha256_file(&backup)? == hash, "backup verification failed");
            Some(hash)
        } else { None };
        journal.old.insert(name.to_owned(), hash);
    }
    // The immutable journal and every backup are durable before the first replacement.
    synced_write(&root.join("journal.json"), &serde_json::to_vec(&journal)?)?;
    let result = (|| -> Result<()> {
        for name in FILES.into_iter().filter(|n| *n != "launcher.json") {
            let target = install.join(name);
            if target.exists() { regular(&target)?; }
            replace(&root.join("new").join(name), &target)?;
        }
        let managed = journal.files.iter().filter(|(n, _)| n.as_str() != "launcher.json").map(|(n,h)| (n.clone(),h.clone())).collect();
        verify_files(install, &managed)?;
        let mut child = relaunch(install, Some("--self-update-health"))?;
        let deadline = Instant::now() + Duration::from_secs(30);
        loop {
            if root.join("committed").is_file() { return Ok(()); }
            if child.try_wait()?.is_some() { bail!("updated launcher exited before startup acknowledgment"); }
            if Instant::now() >= deadline { child.kill()?; child.wait()?; bail!("updated launcher startup acknowledgment timed out"); }
            std::thread::sleep(Duration::from_millis(50));
        }
    })();
    if let Err(error) = result {
        rollback(install, &root, &journal)?;
        let _ = crate::local::launcher_log(&format!("Launcher update rolled back: {error:#}"));
        retire(&root)?;
        relaunch(install, Some("--self-update-rolled-back"))?;
        return Ok(());
    }
    retire(&root)?;
    Ok(())
    })();
    if outcome.is_err() && root.exists() && !root.join("journal.json").exists() {
        // No managed file can have changed before the durable journal exists.
        retire(&root)?;
        relaunch(install, Some("--self-update-rolled-back"))?;
    }
    outcome
}

#[cfg(windows)]
fn relaunch(install: &Path, argument: Option<&str>) -> Result<std::process::Child> {
    let mut command = Command::new(install.join(FILES[0]));
    if let Some(argument) = argument { command.arg(argument); }
    Ok(command.current_dir(install).spawn()?)
}
#[cfg(windows)]
fn retire(root: &Path) -> Result<()> {
    // Move the completed transaction out of the recovery slot before any relaunch.
    // A later ordinary startup removes these verified, update-owned leftovers.
    fs::rename(root, root.with_file_name(format!(".ascnet-update-done-{}", uuid::Uuid::new_v4())))?;
    Ok(())
}

/// Must run before logging, UI initialization, or any game/server operation.
/// Returns true when this process is an internal worker or must exit for recovery.
pub fn startup() -> Result<bool> {
    std::hint::black_box(VERSION_MARKER);
    let args: Vec<_> = std::env::args().skip(1).collect();
    #[cfg(windows)] {
        if args.first().map(String::as_str) == Some("--self-update-worker") {
            ensure!(args.len() == 3, "invalid update worker arguments");
            worker(args[1].parse()?, Path::new(&args[2]))?;
            return Ok(true);
        }
        // A renamed or linked deployment only disables self-update; stage() refuses it again.
        let install = match install_dir() {
            Ok(install) => install,
            Err(error) if !args.iter().any(|a| a.starts_with("--self-update")) => {
                let _ = crate::local::launcher_log(&format!("Launcher self-update disabled: {error:#}"));
                return Ok(false);
            }
            Err(error) => return Err(error),
        };
        let root = install.join(TRANSACTION);
        if args.as_slice() == ["--self-update-health"] {
            let journal = read_journal(&root, "journal.json")?;
            ensure!(journal.release.version == env!("CARGO_PKG_VERSION"), "updated launcher compiled version mismatch");
            let managed = journal.files.into_iter().filter(|(n,_)| n != "launcher.json").collect();
            verify_files(&install, &managed)?;
            // The UI acknowledges only after its resources/window initialize successfully.
            return Ok(false);
        }
        if args.as_slice() == ["--self-update-rolled-back"] { return Ok(false); }
        if root.exists() {
            spawn_worker(&install, &root)?;
            return Ok(true);
        }
        for entry in fs::read_dir(&install)? {
            let entry = entry?;
            let name = entry.file_name();
            if name.to_str().and_then(|n| n.strip_prefix(".ascnet-worker-")).is_some_and(|n| uuid::Uuid::parse_str(n).is_ok()) && directory(&entry.path()).is_ok() {
                let helper = entry.path().join("helper.exe");
                let owner = entry.path().join("owner");
                if regular(&owner).is_ok() && fs::metadata(&owner)?.len() == b"AscNet launcher update helper".len() as u64 && fs::read(&owner)? == b"AscNet launcher update helper" && regular(&helper).is_ok() {
                    if fs::remove_file(helper).is_ok() { let _ = fs::remove_file(owner); let _ = fs::remove_dir(entry.path()); }
                }
            }
            if entry.file_name().to_string_lossy().starts_with(".ascnet-update-done-") && directory(&entry.path()).is_ok() {
                // Only transaction directories with our fixed journal are cleanup candidates.
                if read_journal(&entry.path(), "journal.json").is_ok() { let _ = fs::remove_dir_all(entry.path()); }
            }
        }
    }
    #[cfg(not(windows))] let _ = args;
    Ok(false)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn stable_versions_and_repository_boundaries() {
        assert!(stable_version("v1.0.3").unwrap() > stable_version("1.0.2").unwrap());
        for version in ["release", "1.2", "1.0.3-rc.1", "1.0.3+build", "01.0.3"] { assert!(stable_version(version).is_none()); }
        for repository in ["http://github.com/a/b", "https://github.com.evil/a/b", "https://github.com/a/../b", "https://github.com/a/b?x", "https://github.com/a/b#x"] { assert!(repository_path(repository).is_err()); }
        assert_eq!(repository_path("https://github.com/a/b.git").unwrap(), "a/b");
    }
    #[test]
    fn archive_rejects_traversal_duplicates_and_corruption() {
        let root = std::env::temp_dir().join(uuid::Uuid::new_v4().to_string());
        fs::create_dir(&root).unwrap();
        let root = fs::canonicalize(root).unwrap();
        for (case, bad) in ["../AscNetLauncher.exe", "C:AscNetLauncher.exe"].iter().enumerate() {
            let path = root.join(format!("{case}.zip"));
            let mut zip = zip::ZipWriter::new(File::create(&path).unwrap());
            for (index, name) in FILES.iter().enumerate() {
                zip.start_file(if index == 0 { *bad } else { *name }, zip::write::SimpleFileOptions::default()).unwrap();
                zip.write_all(b"payload").unwrap();
            }
            zip.finish().unwrap();
            let output = root.join(case.to_string()); fs::create_dir(&output).unwrap();
            assert!(extract(&path, &output, "1.0.3").is_err());
        }
        let corrupt = root.join("corrupt.zip"); fs::write(&corrupt, b"not zip").unwrap();
        assert!(extract(&corrupt, &root, "1.0.3").is_err());
        let duplicate = root.join("duplicate.zip");
        let mut zip = zip::ZipWriter::new(File::create(&duplicate).unwrap());
        for name in FILES {
            zip.start_file(name, zip::write::SimpleFileOptions::default()).unwrap();
            zip.write_all(b"payload").unwrap();
        }
        zip.finish().unwrap();
        // ZipWriter correctly refuses duplicate names; corrupt both directory and
        // local headers to represent the hostile archive a downloader can receive.
        let mut bytes = fs::read(&duplicate).unwrap();
        for index in 0..=bytes.len() - b"background.wav".len() {
            if &bytes[index..index + 14] == b"background.wav" {
                bytes[index..index + 14].copy_from_slice(b"background.bmp");
            }
        }
        fs::write(&duplicate, bytes).unwrap();
        let output = root.join("duplicate"); fs::create_dir(&output).unwrap();
        assert!(extract(&duplicate, &output, "1.0.3").is_err());
        let executable = root.join("version.exe");
        fs::write(&executable, b"MZASCNET_LAUNCHER_VERSION=1.0.3\0").unwrap();
        assert!(verify_version(&executable, "1.0.3").is_ok());
        assert!(verify_version(&executable, "1.0.4").is_err());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn rollback_is_repeatable_and_preserves_configuration() {
        let install = std::env::temp_dir().join(uuid::Uuid::new_v4().to_string());
        fs::create_dir(&install).unwrap();
        let install = fs::canonicalize(install).unwrap();
        let root = install.join(TRANSACTION); fs::create_dir_all(root.join("old")).unwrap();
        fs::write(install.join("launcher.json"), b"user config").unwrap();
        fs::write(install.join("background.bmp"), b"new").unwrap();
        fs::write(root.join("old/background.bmp"), b"old").unwrap();
        let hash = sha256_file(&root.join("old/background.bmp")).unwrap();
        let journal = Journal { release: Release { version: "1.0.3".into(), url: String::new(), digest: String::new(), size: 0 }, files: BTreeMap::new(), old: BTreeMap::from([("background.bmp".into(), Some(hash))]) };
        rollback(&install, &root, &journal).unwrap(); rollback(&install, &root, &journal).unwrap();
        assert_eq!(fs::read(install.join("background.bmp")).unwrap(), b"old");
        assert_eq!(fs::read(install.join("launcher.json")).unwrap(), b"user config");
        fs::write(root.join("old/background.bmp"), b"corrupt").unwrap();
        assert!(rollback(&install, &root, &journal).is_err());
        fs::remove_dir_all(install).unwrap();
    }
}
