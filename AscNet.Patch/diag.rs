//! Shared by lucia and krsdk via `#[path]` (std only). Player-facing diagnostics: every line goes to
//! stdout (visible only when PGR.exe is started from a console/Wine) and is appended to
//! `ascnet-patch.log` beside PGR.exe, because PGR.exe has no console so stdout is otherwise lost.

use std::{fs::OpenOptions, io::Write, sync::Once};

const LOG_FILE: &str = "ascnet-patch.log";
// ponytail: truncate-on-oversize at first write of each process; rotate properly if history matters.
const MAX_BYTES: u64 = 1 << 20;

pub(crate) fn log(line: &str) {
    println!("{line}");
    static TRIM: Once = Once::new();
    let Some(path) = std::env::current_exe().ok().and_then(|exe| Some(exe.parent()?.join(LOG_FILE))) else { return };
    TRIM.call_once(|| {
        if std::fs::metadata(&path).is_ok_and(|meta| meta.len() > MAX_BYTES) {
            let _ = std::fs::remove_file(&path);
        }
    });
    if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(path) {
        let _ = writeln!(file, "{line}");
    }
}

/// `[tag] OK <what>[: detail]`
pub(crate) fn ok_line(tag: &str, what: &str, detail: &str) -> String {
    if detail.is_empty() { format!("[{tag}] OK {what}") } else { format!("[{tag}] OK {what}: {detail}") }
}

/// `[tag] FAILED <what>: <reason> | send: <log path hint>`
pub(crate) fn failed_line(tag: &str, what: &str, reason: &str) -> String {
    format!("[{tag}] FAILED {what}: {reason} | send: {LOG_FILE} (beside PGR.exe) and the client region")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_are_stable_and_greppable() {
        assert_eq!(ok_line("lucia", "x", ""), "[lucia] OK x");
        assert_eq!(ok_line("lucia", "x", "y"), "[lucia] OK x: y");
        assert_eq!(
            failed_line("krsdk", "KRSDK.bin", "missing"),
            "[krsdk] FAILED KRSDK.bin: missing | send: ascnet-patch.log (beside PGR.exe) and the client region"
        );
    }
}
