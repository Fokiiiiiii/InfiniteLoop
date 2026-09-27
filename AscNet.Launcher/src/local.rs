use anyhow::{bail, Context, Result};
use reqwest::Url;
use serde::Deserialize;
use std::{
    env, fs,
    io::{BufRead, BufReader, Write},
    net::{IpAddr, Ipv4Addr, SocketAddr, TcpListener, TcpStream},
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc, Mutex,
    },
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

const SCHEMA_VERSION: u32 = 1;
const START_TIMEOUT: Duration = Duration::from_secs(45);
const SETUP_TIMEOUT: Duration = Duration::from_secs(60 * 60);
const STOP_TIMEOUT: Duration = Duration::from_secs(10);
static OPERATION: AtomicBool = AtomicBool::new(false);
static LOG_WRITE: Mutex<()> = Mutex::new(());

struct LauncherLog {
    path: PathBuf,
    file: fs::File,
}

impl LauncherLog {
    fn open(path: PathBuf) -> Result<Self> {
        let file = fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)
            .with_context(|| format!("open launcher log {}", path.display()))?;
        Ok(Self { path, file })
    }

    fn beside_executable() -> Result<Self> {
        Self::open(
            env::current_exe()?
                .parent()
                .context("launcher executable has no parent directory")?
                .join("launcher.log"),
        )
    }

    fn write(&mut self, message: &str) -> Result<()> {
        let _guard = LOG_WRITE.lock().unwrap_or_else(|e| e.into_inner());
        let timestamp = SystemTime::now().duration_since(UNIX_EPOCH)?.as_millis();
        writeln!(self.file, "[{timestamp} unix-ms] {message}")
            .and_then(|_| self.file.flush())
            .with_context(|| format!("write launcher log {}", self.path.display()))
    }

    fn finish<T>(&mut self, result: Result<T>) -> Result<T> {
        let message = match &result {
            Ok(_) => "Setup attempt completed".to_owned(),
            Err(error) => format!("Setup attempt failed: {error:#}"),
        };
        let logged = self.write(&message).and_then(|_| {
            self.file.sync_data()
                .with_context(|| format!("flush launcher log {}", self.path.display()))
        });
        match (result, logged) {
            (Err(error), Err(log_error)) => Err(error.context(format!("{log_error:#}"))),
            (result, Ok(())) => result,
            (Ok(_), Err(error)) => Err(error),
        }
    }
}

pub fn launcher_log(message: &str) -> Result<()> {
    LauncherLog::beside_executable()?.write(message)
}

pub fn logged_error(message: &str) -> String {
    match launcher_log(&format!("Launcher error: {message}")) {
        Ok(()) => message.to_owned(),
        Err(error) => format!("{message}\n\nCould not save launcher diagnostics: {error:#}"),
    }
}

/// Logs the full error chain; returns a one-line summary for the activity log.
pub fn summarized_error(action: &str, error: &anyhow::Error) -> String {
    let _ = launcher_log(&format!("Launcher error: Couldn't {action}: {error:#}"));
    summarize_error(action, error)
}

fn summarize_error(action: &str, error: &anyhow::Error) -> String {
    let chain = format!("{error:#}").to_ascii_lowercase();
    // ponytail: substring classifier over reqwest/io/git text; downcast if messages drift.
    const OFFLINE: [&str; 11] = [
        "dns error",
        "host not found",
        "no such host",
        "failed to lookup",
        "could not resolve host",
        "tunnel error",
        "timed out",
        "connection refused",
        "connection reset",
        "(connect)",
        "unable to access",
    ];
    if OFFLINE.iter().any(|marker| chain.contains(marker)) {
        return format!("Couldn't {action} — offline or GitHub unreachable");
    }
    let top = error.to_string();
    let cause = if top.contains("url (") { error.root_cause().to_string() } else { top };
    format!("Couldn't {action}: {}", cause.lines().next().unwrap_or_default())
}

#[cfg(test)]
mod summary_tests {
    use super::summarize_error;
    use anyhow::anyhow;

    #[test]
    fn network_failures_collapse_to_offline() {
        let error = anyhow!("dns error: Host not found. (os error 11001)")
            .context("client error (Connect): tunnel error")
            .context("error sending request for url (https://api.github.com/x)");
        assert_eq!(
            summarize_error("check for launcher updates", &error),
            "Couldn't check for launcher updates — offline or GitHub unreachable"
        );
    }

    #[test]
    fn other_failures_keep_first_line_without_url_chain() {
        let error = anyhow!("HTTP status 404").context("error sending request for url (https://x/y)");
        assert_eq!(summarize_error("do it", &error), "Couldn't do it: HTTP status 404");
        let error = anyhow!("disk full").context("Staged update is invalid\nsecond line");
        assert_eq!(summarize_error("do it", &error), "Couldn't do it: Staged update is invalid");
    }
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct LocalBuild {
    pub schema_version: u32,
    pub revision: String,
    pub repository: String,
    pub dotnet: PathBuf,
    pub mongod: PathBuf,
    pub server_directory: PathBuf,
    pub resource_directory: PathBuf,
    pub patch_directory: PathBuf,
    pub sdk_port: u16,
    pub game_port: u16,
    pub mongo_port: u16,
}

pub fn root() -> Result<PathBuf> {
    let base = env::var_os("LOCALAPPDATA").context("LOCALAPPDATA is not set")?;
    let base = PathBuf::from(base);
    if !base.is_absolute() {
        bail!("LOCALAPPDATA must be an absolute path");
    }
    Ok(base.join("AscNetLauncher").join("local"))
}

pub fn load_build() -> Result<Option<LocalBuild>> {
    let root = root()?;
    let state = root.join("build-state.json");
    let bytes = match fs::read(&state) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(error).with_context(|| format!("read {}", state.display())),
    };
    let build: LocalBuild = serde_json::from_slice(&bytes).context("invalid local build state")?;
    validate_build(&root, &build)?;
    crate::package::refresh_supported_client(
        &build.patch_directory,
        &env::current_exe()?
            .parent()
            .context("launcher executable has no parent directory")?
            .join("supported-client.json"),
    )?;
    Ok(Some(build))
}

pub fn prepare(
    repository: &str,
    branch: &str,
    game: &Path,
    progress: &mut dyn FnMut(&str),
) -> Result<LocalBuild> {
    let mut log = LauncherLog::beside_executable()?;
    log.write("Setup attempt started")?;
    let result = prepare_logged(repository, branch, game, progress, &mut log);
    log.finish(result)
}

fn prepare_logged(
    repository: &str,
    branch: &str,
    game: &Path,
    progress: &mut dyn FnMut(&str),
    log: &mut LauncherLog,
) -> Result<LocalBuild> {
    validate_repository(repository)?;
    validate_branch(branch)?;
    #[cfg(not(windows))]
    bail!("local source setup is supported on Windows only");
    let _operation = operation_lock()?;
    let root = root()?;
    fs::create_dir_all(&root).with_context(|| format!("create {}", root.display()))?;
    #[cfg(windows)]
    let _setup_lock = open_setup_lock(&root)?;
    let script = env::current_exe()?
        .parent()
        .context("launcher executable has no parent directory")?
        .join("setup-local.ps1");
    if !script.is_file() {
        bail!("local setup script is missing: {}", script.display());
    }
    // Refuse before setup-local.ps1 installs dependencies for a client we cannot patch.
    crate::package::check_supported_client(&script.with_file_name("supported-client.json"), game)?;

    progress("Starting local source setup");
    #[cfg(windows)]
    let setup_job = create_job()?;
    let mut child = OwnedChild(
        hide_console(&mut Command::new("powershell.exe"))
            .args([
                "-NoLogo",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
            ])
            .arg(&script)
            .arg("-Root")
            .arg(&root)
            .arg("-Repository")
            .arg(repository)
            .arg("-Branch")
            .arg(branch)
            .arg("-SetupLockHeld")
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .context("start PowerShell local setup")?,
    );
    #[cfg(windows)]
    assign_to_job(&setup_job, &child.0)?;
    capture_setup(&mut child.0, log, progress, SETUP_TIMEOUT)?;
    let pending = root.join("build-state.pending.json");
    let bytes = fs::read(&pending).with_context(|| format!("read {}", pending.display()))?;
    let build: LocalBuild =
        serde_json::from_slice(&bytes).context("invalid pending local build state")?;
    validate_build(&root, &build)?;
    crate::package::refresh_supported_client(
        &build.patch_directory,
        &script
            .parent()
            .context("local setup script has no parent directory")?
            .join("supported-client.json"),
    )?;
    let package = crate::package::load_package(&build.patch_directory)
        .context("validate prepared patch package")?;
    fs::OpenOptions::new()
        .write(true)
        .open(&pending)?
        .sync_all()
        .context("flush pending local build state")?;
    crate::install::install_with_consent(game, &package, &mut |message| progress(&message))?;
    anyhow::ensure!(
        matches!(crate::install::inspect(game, &package)?, crate::install::PatchState::Current),
        "prepared patch did not become current"
    );
    #[cfg(windows)]
    atomic_replace(&pending, &root.join("build-state.json"))?;
    Ok(build)
}

fn capture_setup(
    child: &mut Child,
    log: &mut LauncherLog,
    progress: &mut dyn FnMut(&str),
    timeout: Duration,
) -> Result<()> {
    let (send, receive) = mpsc::channel();
    stream_lines(
        child.stdout.take().context("capture local setup output")?,
        send.clone(),
    );
    stream_lines(
        child.stderr.take().context("capture local setup errors")?,
        send.clone(),
    );
    drop(send);
    let deadline = Instant::now() + timeout;
    loop {
        if Instant::now() >= deadline {
            terminate_child(child);
            bail!("local setup timed out after {} seconds", timeout.as_secs());
        }
        match receive.recv_timeout(Duration::from_millis(250)) {
            Ok(line) => {
                let line = line.context("read local setup output")?;
                log.write(&line)?;
                progress(&line);
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
    }
    let status = loop {
        if let Some(status) = child.try_wait().context("wait for local setup")? {
            break status;
        }
        if Instant::now() >= deadline {
            terminate_child(child);
            bail!("local setup timed out after {} seconds", timeout.as_secs());
        }
        thread::sleep(Duration::from_millis(100));
    };
    log.write(&format!("Setup process exited with {status}"))?;
    if !status.success() {
        bail!("local setup failed with {status}; see {}", log.path.display());
    }
    Ok(())
}

#[cfg(test)]
mod log_tests {
    use super::*;

    #[test]
    fn failed_setup_retains_both_streams_across_attempts() {
        let directory = env::temp_dir().join(format!("ascnet-log-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&directory).unwrap();
        let path = directory.join("launcher.log");
        for _ in 0..2 {
            let mut log = LauncherLog::open(path.clone()).unwrap();
            log.write("Setup attempt started").unwrap();
            #[cfg(windows)]
            let mut command = {
                let mut command = Command::new("cmd.exe");
                command.args(["/D", "/C", "echo compiler-output & echo dependency-failure 1>&2 & exit /b 7"]);
                command
            };
            #[cfg(not(windows))]
            let mut command = {
                let mut command = Command::new("sh");
                command.args(["-c", "printf 'compiler-output\\n'; printf 'dependency-failure\\n' >&2; exit 7"]);
                command
            };
            let mut child = OwnedChild(command.stdout(Stdio::piped()).stderr(Stdio::piped()).spawn().unwrap());
            // The visible UI may discard every detailed line.
            let result = capture_setup(&mut child.0, &mut log, &mut |_| {}, Duration::from_secs(10));
            let error = log.finish(result).unwrap_err();
            assert!(format!("{error:#}").contains("local setup failed"));
        }
        let contents = fs::read_to_string(&path).unwrap();
        assert_eq!(contents.matches("compiler-output").count(), 2);
        assert_eq!(contents.matches("dependency-failure").count(), 2);
        assert_eq!(contents.matches("Setup attempt started").count(), 2);
        assert_eq!(contents.matches("Setup process exited with").count(), 2);
        assert_eq!(contents.matches("Setup attempt failed: local setup failed").count(), 2);
        fs::remove_dir_all(directory).unwrap();
    }
}

pub fn check_update(
    repository: &str,
    branch: &str,
    installed: Option<&LocalBuild>,
) -> Result<Option<bool>> {
    validate_repository(repository)?;
    validate_branch(branch)?;
    let Some(installed) = installed else {
        return Ok(None);
    };
    if installed.repository != repository {
        bail!("active build belongs to a different repository; run Setup manually");
    }
    let checkout = root()?.join("checkout");
    if !checkout.join(".git").is_dir() {
        return Ok(None);
    }
    let Some(git) = git_executable() else {
        return Ok(None);
    };
    check_checkout_update(&git, &checkout, repository, branch, &installed.revision)
}

fn check_checkout_update(
    git: &Path,
    checkout: &Path,
    repository: &str,
    branch: &str,
    installed_revision: &str,
) -> Result<Option<bool>> {
    for (args, expected) in [
        (["remote", "get-url", "origin"].as_slice(), repository),
        (["branch", "--show-current"].as_slice(), branch),
    ] {
        let mut command = Command::new(&git);
        command.arg("-C").arg(&checkout).args(args);
        let output = command_output_timeout(command, Duration::from_secs(15))?;
        if !output.status.success()
            || text_output(&output.stdout, "checkout identity")? != expected
        {
            bail!("checkout repository or branch differs; run Setup manually");
        }
    }
    let mut remote_command = Command::new(&git);
    let remote_ref = format!("refs/heads/{branch}");
    remote_command
        .env("GIT_TERMINAL_PROMPT", "0")
        .env("GCM_INTERACTIVE", "Never")
        .args([
            "-c",
            "credential.interactive=false",
            "ls-remote",
            "--exit-code",
            repository,
            &remote_ref,
        ]);
    let output = command_output_timeout(remote_command, Duration::from_secs(30))
        .context("check remote source revision")?;
    if !output.status.success() {
        bail!("git could not find remote branch {branch}");
    }
    let mut fields = text_output(&output.stdout, "remote git revision")?.split_whitespace();
    let remote = fields.next().context("git returned no remote revision")?;
    if fields.next() != Some(remote_ref.as_str()) || fields.next().is_some() {
        bail!("git returned an unexpected remote branch");
    }
    if remote.len() != 40 || !remote.bytes().all(|b| b.is_ascii_hexdigit()) {
        bail!("git returned an invalid remote revision");
    }
    Ok(Some(!installed_revision.eq_ignore_ascii_case(remote)))
}

#[cfg(test)]
mod update_tests {
    use super::*;

    #[test]
    fn advanced_checkout_does_not_hide_failed_build() {
        let directory = env::temp_dir().join(format!("ascnet-source-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&directory).unwrap();
        let git = |args: &[&str]| {
            let output = Command::new("git").arg("-C").arg(&directory).args(args).output().unwrap();
            assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
            String::from_utf8(output.stdout).unwrap().trim().to_owned()
        };
        git(&["init", "--initial-branch=master"]);
        git(&["-c", "user.name=Launcher Test", "-c", "user.email=launcher@example.invalid",
            "commit", "--allow-empty", "-m", "active build"]);
        let active_revision = git(&["rev-parse", "HEAD"]);
        git(&["-c", "user.name=Launcher Test", "-c", "user.email=launcher@example.invalid",
            "commit", "--allow-empty", "-m", "checkout advanced before failed build"]);
        let remote_revision = git(&["rev-parse", "HEAD"]);
        let repository = directory.to_str().unwrap();
        git(&["remote", "add", "origin", repository]);

        assert_eq!(
            check_checkout_update(Path::new("git"), &directory, repository, "master", &active_revision).unwrap(),
            Some(true),
            "checkout and remote match, but active build still needs updating",
        );
        assert_eq!(
            check_checkout_update(Path::new("git"), &directory, repository, "master", &remote_revision).unwrap(),
            Some(false),
        );
        fs::remove_dir_all(directory).unwrap();
    }
}

pub struct LocalRuntime {
    server: OwnedChild,
    mongo: OwnedChild,
    dotnet: PathBuf,
    server_dll: PathBuf,
    mongo_port: u16,
    stopped: bool,
    #[cfg(windows)]
    job: JobHandle,
    _operation: Option<OperationPermit>,
}

impl LocalRuntime {
    pub fn start(build: &LocalBuild, progress: &mut dyn FnMut(&str)) -> Result<Self> {
        #[cfg(not(windows))]
        {
            let _ = (build, progress);
            bail!("the local runtime is supported on Windows only");
        }
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            use windows::Win32::System::Threading::CREATE_NO_WINDOW;

            let operation = operation_lock()?;
            let root = root()?;
            validate_build(&root, build)?;
            let _ports = reserve_ports([build.mongo_port, build.sdk_port, build.game_port])?;
            fs::create_dir_all(root.join("data/mongo"))?;
            fs::create_dir_all(root.join("logs"))?;
            let job = create_job()?;

            progress("Starting MongoDB");
            drop(_ports);
            let mongo_log = fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(root.join("logs/mongod.log"))?;
            let mut mongo = OwnedChild(
                Command::new(&build.mongod)
                    .args(["--bind_ip", "127.0.0.1", "--dbpath"])
                    .arg(root.join("data/mongo"))
                    .arg("--port")
                    .arg(build.mongo_port.to_string())
                    .stdout(mongo_log.try_clone()?)
                    .stderr(mongo_log)
                    .creation_flags(CREATE_NO_WINDOW.0)
                    .spawn()
                    .context("start MongoDB")?,
            );
            assign_to_job(&job, &mongo.0)?;
            wait_tcp(&mut mongo.0, build.mongo_port, "MongoDB")?;

            progress("Starting AscNet server");
            let origin = format!("http://127.0.0.1:{}", build.sdk_port);
            let server_log = fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(root.join("logs/server.log"))?;
            let mut server = OwnedChild(
                Command::new(&build.dotnet)
                    .arg(build.server_directory.join("AscNet.dll"))
                    .args(["--urls", &origin])
                    .current_dir(&build.resource_directory)
                    .env("ASCNET_GATE_FALLBACK_USERNAME", "")
                    .env("ASCNET_PUBLIC_HTTP_ORIGIN", &origin)
                    .env("ASCNET_GAME_BIND_ADDRESS", "127.0.0.1")
                    .env("ASCNET_MANAGED_STDIN", "1")
                    .stdin(Stdio::piped())
                    .stdout(server_log.try_clone()?)
                    .stderr(server_log)
                    .creation_flags(CREATE_NO_WINDOW.0)
                    .spawn()
                    .context("start AscNet server")?,
            );
            assign_to_job(&job, &server.0)?;
            wait_server(&mut server.0, &origin, build.game_port)?;
            progress("Local backend is ready");
            Ok(Self {
                server,
                mongo,
                dotnet: build.dotnet.clone(),
                server_dll: build.server_directory.join("AscNet.dll"),
                mongo_port: build.mongo_port,
                stopped: false,
                job,
                _operation: Some(operation),
            })
        }
    }

    pub fn stop(&mut self) -> Result<()> {
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            use windows::Win32::System::Threading::CREATE_NO_WINDOW;
            if self.stopped {
                return Ok(());
            }
            let server_request = (|| -> Result<()> {
                let stdin = self
                    .server
                    .0
                    .stdin
                    .as_mut()
                    .context("AscNet server stdin is unavailable")?;
                stdin
                    .write_all(b"shutdown\n")
                    .context("request AscNet server shutdown")?;
                stdin
                    .flush()
                    .context("flush AscNet server shutdown request")
            })();
            let server_result = wait_or_kill(&mut self.server.0, STOP_TIMEOUT);

            let helper_result = (|| -> Result<bool> {
                let mut shutdown = OwnedChild(
                    Command::new(&self.dotnet)
                        .arg(&self.server_dll)
                        .args(["--shutdown-local-mongo", &self.mongo_port.to_string()])
                        .current_dir(
                            self.server_dll
                                .parent()
                                .context("AscNet server DLL has no directory")?,
                        )
                        .stdout(Stdio::null())
                        .stderr(Stdio::null())
                        .creation_flags(CREATE_NO_WINDOW.0)
                        .spawn()
                        .context("start owned MongoDB shutdown helper")?,
                );
                assign_to_job(&self.job, &shutdown.0)?;
                wait_or_kill(&mut shutdown.0, STOP_TIMEOUT)
            })();
            let mongo_result = wait_or_kill(&mut self.mongo.0, STOP_TIMEOUT);
            self.stopped = true;
            self._operation.take();
            let server_forced = server_result?;
            let helper_forced = helper_result?;
            let mongo_forced = mongo_result?;
            if server_forced || helper_forced || mongo_forced {
                bail!("local runtime did not stop gracefully; forced termination was required");
            }
            server_request?;
        }
        Ok(())
    }
}
fn git_executable() -> Option<PathBuf> {
    if let Some(path) = env::var_os("PATH").and_then(|path| {
        env::split_paths(&path)
            .map(|directory| directory.join("git.exe"))
            .find(|candidate| candidate.is_file())
    }) {
        return Some(path);
    }
    ["ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA"]
        .into_iter()
        .filter_map(env::var_os)
        .map(PathBuf::from)
        .flat_map(|base| {
            [
                base.join("Git/cmd/git.exe"),
                base.join("Programs/Git/cmd/git.exe"),
            ]
        })
        .find(|candidate| candidate.is_file())
}

struct OwnedChild(Child);

impl Drop for OwnedChild {
    fn drop(&mut self) {
        terminate_child(&mut self.0);
    }
}

struct OperationPermit;

impl Drop for OperationPermit {
    fn drop(&mut self) {
        OPERATION.store(false, Ordering::Release);
    }
}

fn stream_lines<R: std::io::Read + Send + 'static>(
    stream: R,
    send: mpsc::Sender<std::io::Result<String>>,
) {
    thread::spawn(move || {
        let mut stream = BufReader::new(stream);
        let mut bytes = Vec::new();
        loop {
            bytes.clear();
            match stream.read_until(b'\n', &mut bytes) {
                Ok(0) => break,
                Ok(_) => {
                    let line = String::from_utf8_lossy(&bytes)
                        .trim_end_matches(&['\r', '\n'][..])
                        .to_owned();
                    if send.send(Ok(line)).is_err() {
                        break;
                    }
                }
                Err(error) => {
                    let _ = send.send(Err(error));
                    break;
                }
            }
        }
    });
}

fn operation_lock() -> Result<OperationPermit> {
    OPERATION
        .compare_exchange(false, true, Ordering::Acquire, Ordering::Relaxed)
        .map(|_| OperationPermit)
        .map_err(|_| anyhow::anyhow!("another local setup or runtime operation is active"))
}
#[cfg(windows)]
fn open_setup_lock(root: &Path) -> Result<fs::File> {
    use std::os::windows::fs::OpenOptionsExt;
    fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .share_mode(0)
        .open(root.join("setup.lock"))
        .context("another local setup is already active")
}

fn validate_repository(repository: &str) -> Result<()> {
    if repository.trim() != repository {
        bail!("repository URL contains surrounding whitespace");
    }
    let url = Url::parse(repository).context("invalid repository URL")?;
    if url.scheme() != "https"
        || url.host_str() != Some("github.com")
        || url.username() != ""
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
        || !url.path().ends_with(".git")
        || url.path_segments().map(|s| s.count()) != Some(2)
    {
        bail!("repository must be an HTTPS github.com owner/repository.git URL");
    }
    Ok(())
}

fn validate_branch(branch: &str) -> Result<()> {
    if branch.is_empty()
        || branch.len() > 255
        || branch.starts_with('-')
        || branch.starts_with('.')
        || branch.starts_with('/')
        || branch.ends_with('.')
        || branch.ends_with('/')
        || branch.contains("..")
        || branch.contains("@{")
        || branch.contains("//")
        || branch
            .bytes()
            .any(|b| b <= b' ' || b == 0x7f || b"~^:?*[\\".contains(&b))
    {
        bail!("invalid git branch name");
    }
    Ok(())
}

fn validate_build(root: &Path, build: &LocalBuild) -> Result<()> {
    if build.schema_version != SCHEMA_VERSION {
        bail!("unsupported local build schema {}", build.schema_version);
    }
    validate_repository(&build.repository)?;
    if build.revision.len() != 40 || !build.revision.bytes().all(|b| b.is_ascii_hexdigit()) {
        bail!("invalid local build revision");
    }
    if build.sdk_port == 0
        || build.game_port == 0
        || build.mongo_port == 0
        || build.sdk_port == build.game_port
        || build.sdk_port == build.mongo_port
        || build.game_port == build.mongo_port
    {
        bail!("local build ports must be distinct non-zero ports");
    }
    let canonical_root = root
        .canonicalize()
        .with_context(|| format!("resolve {}", root.display()))?;
    for (name, path) in [
        ("serverDirectory", &build.server_directory),
        ("resourceDirectory", &build.resource_directory),
        ("patchDirectory", &build.patch_directory),
    ] {
        if !path.is_absolute()
            || !path.is_dir()
            || !path.canonicalize()?.starts_with(&canonical_root)
        {
            bail!("{name} must be an existing directory beneath the local root");
        }
    }
    for (name, path) in [("dotnet", &build.dotnet), ("mongod", &build.mongod)] {
        if !path.is_absolute() || !path.is_file() {
            bail!("{name} must be an existing absolute executable path");
        }
    }
    if !build.server_directory.join("AscNet.dll").is_file() {
        bail!("serverDirectory does not contain AscNet.dll");
    }
    Ok(())
}

fn reserve_ports(ports: [u16; 3]) -> Result<Vec<TcpListener>> {
    ports
        .into_iter()
        .map(|port| {
            TcpListener::bind((Ipv4Addr::LOCALHOST, port))
                .with_context(|| format!("local port {port} is already occupied"))
        })
        .collect()
}

fn wait_tcp(child: &mut Child, port: u16, name: &str) -> Result<()> {
    let deadline = Instant::now() + START_TIMEOUT;
    loop {
        if let Some(status) = child.try_wait()? {
            bail!("{name} exited before becoming ready ({status})");
        }
        if TcpStream::connect_timeout(
            &SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), port),
            Duration::from_millis(250),
        )
        .is_ok()
        {
            return Ok(());
        }
        if Instant::now() >= deadline {
            bail!("timed out waiting for {name} on port {port}");
        }
        thread::sleep(Duration::from_millis(100));
    }
}

fn wait_server(child: &mut Child, origin: &str, game_port: u16) -> Result<()> {
    let client = reqwest::blocking::Client::builder()
        .no_proxy()
        .timeout(Duration::from_secs(2))
        .build()?;
    let deadline = Instant::now() + START_TIMEOUT;
    loop {
        if let Some(status) = child.try_wait()? {
            bail!("AscNet server exited before becoming ready ({status})");
        }
        let game_ready = TcpStream::connect_timeout(
            &SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), game_port),
            Duration::from_millis(100),
        )
        .is_ok();
        let api_ready = client
            .get(format!("{origin}/api/launcher/status"))
            .send()
            .and_then(reqwest::blocking::Response::error_for_status)
            .and_then(|response| response.json::<serde_json::Value>())
            .ok()
            .and_then(|value| {
                value
                    .get("schemaVersion")
                    .and_then(serde_json::Value::as_u64)
            })
            == Some(1);
        if game_ready && api_ready {
            return Ok(());
        }
        if Instant::now() >= deadline {
            bail!("timed out waiting for AscNet game and launcher-status endpoints");
        }
        thread::sleep(Duration::from_millis(150));
    }
}

fn text_output<'a>(bytes: &'a [u8], description: &str) -> Result<&'a str> {
    std::str::from_utf8(bytes)
        .with_context(|| format!("{description} is not UTF-8"))
        .map(str::trim)
}

fn terminate_child(child: &mut Child) {
    let _ = child.kill();
    let _ = child.wait();
}

/// The launcher has no console, so console children would each open their own window.
pub(crate) fn hide_console(command: &mut Command) -> &mut Command {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(windows::Win32::System::Threading::CREATE_NO_WINDOW.0);
    }
    command
}

fn command_output_timeout(mut command: Command, timeout: Duration) -> Result<std::process::Output> {
    let mut child = hide_console(&mut command)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .context("start command")?;
    let deadline = Instant::now() + timeout;
    loop {
        if child.try_wait()?.is_some() {
            return child.wait_with_output().context("collect command output");
        }
        if Instant::now() >= deadline {
            terminate_child(&mut child);
            bail!("command timed out after {} seconds", timeout.as_secs());
        }
        thread::sleep(Duration::from_millis(100));
    }
}
#[cfg(windows)]
fn atomic_replace(source: &Path, destination: &Path) -> Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows::{
        core::PCWSTR,
        Win32::Storage::FileSystem::{
            MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
        },
    };
    let source: Vec<u16> = source.as_os_str().encode_wide().chain(Some(0)).collect();
    let destination: Vec<u16> = destination
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect();
    unsafe {
        MoveFileExW(
            PCWSTR(source.as_ptr()),
            PCWSTR(destination.as_ptr()),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
        .context("activate local build state")
    }
}

#[cfg(windows)]
struct JobHandle(windows::Win32::Foundation::HANDLE);

#[cfg(windows)]
impl Drop for JobHandle {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            unsafe {
                let _ = windows::Win32::Foundation::CloseHandle(self.0);
            }
        }
    }
}

#[cfg(windows)]
fn create_job() -> Result<JobHandle> {
    use windows::Win32::System::JobObjects::{
        CreateJobObjectW, JobObjectExtendedLimitInformation, SetInformationJobObject,
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    };
    unsafe {
        let job = JobHandle(CreateJobObjectW(None, None)?);
        let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(
            job.0,
            JobObjectExtendedLimitInformation,
            &info as *const _ as _,
            std::mem::size_of_val(&info) as u32,
        )?;
        Ok(job)
    }
}

#[cfg(windows)]
fn assign_to_job(job: &JobHandle, child: &Child) -> Result<()> {
    use std::os::windows::io::AsRawHandle;
    use windows::Win32::{Foundation::HANDLE, System::JobObjects::AssignProcessToJobObject};
    unsafe {
        AssignProcessToJobObject(job.0, HANDLE(child.as_raw_handle() as isize))
            .context("contain local process in launcher job")
    }
}

#[cfg(windows)]
fn wait_or_kill(child: &mut Child, timeout: Duration) -> Result<bool> {
    let deadline = Instant::now() + timeout;
    while child.try_wait()?.is_none() && Instant::now() < deadline {
        thread::sleep(Duration::from_millis(100));
    }
    if child.try_wait()?.is_none() {
        terminate_child(child);
        return Ok(true);
    }
    Ok(false)
}
