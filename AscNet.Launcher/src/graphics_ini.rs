//! Launcher-side editing of `fps` / `nofade` in `ascnet-graphics.ini` beside PGR.exe, used only by the
//! one-time legacy migrations. lucia and its overlay own the file; every other line is preserved verbatim.
use anyhow::{Context, Result};
use std::{fs, io, path::Path};

pub const FILE: &str = "ascnet-graphics.ini";

/// `(key, value)` of a setting line; same comment/case rules as lucia's parser.
fn setting(line: &str) -> Option<(String, &str)> {
    let (key, value) = line.split(['#', ';']).next()?.split_once('=')?;
    Some((key.trim().to_ascii_lowercase(), value.trim()))
}

/// Sets (`Some`) or removes (`None`) `key`: the last occurrence is replaced in place,
/// earlier duplicates are dropped, a missing key is appended.
fn edit(text: &str, key: &str, value: Option<&str>) -> String {
    let lines: Vec<&str> = text.lines().collect();
    let is_key = |l: &&str| setting(l).is_some_and(|(k, _)| k == key);
    let last = lines.iter().rposition(|l| is_key(l));
    let mut out = String::with_capacity(text.len() + 16);
    for (i, line) in lines.iter().enumerate() {
        if Some(i) == last {
            if let Some(value) = value {
                out.push_str(&format!("{key} = {value}\n"));
            }
        } else if !is_key(line) {
            out.push_str(line);
            out.push('\n');
        }
    }
    if last.is_none() {
        if let Some(value) = value {
            out.push_str(&format!("{key} = {value}\n"));
        }
    }
    out
}

fn has_key(text: &str, key: &str) -> bool {
    text.lines().filter_map(setting).any(|(k, _)| k == key)
}

fn read_text(client: &Path) -> Result<String> {
    match fs::read_to_string(client.join(FILE)) {
        Ok(text) => Ok(text),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(String::new()),
        Err(e) => Err(e).with_context(|| format!("read {FILE}")),
    }
}

fn write_text(client: &Path, text: &str) -> Result<()> {
    let path = client.join(FILE);
    let temp = client.join(format!("{FILE}.tmp"));
    fs::write(&temp, text).with_context(|| format!("write {FILE}"))?;
    fs::rename(&temp, &path).with_context(|| format!("replace {FILE}"))
}

/// Writes `fps = n`, replacing any existing value.
pub fn set_fps(client: &Path, fps: i32) -> Result<()> {
    write_text(client, &edit(&read_text(client)?, "fps", Some(&fps.to_string())))
}

/// Writes legacy launcher-JSON values only for keys the ini does not have yet.
/// Returns the keys written (for the launcher log).
pub fn migrate(client: &Path, fps: Option<i32>, nofade: bool) -> Result<Vec<&'static str>> {
    let mut text = read_text(client)?;
    let mut written = Vec::new();
    if let Some(fps) = fps.filter(|_| !has_key(&text, "fps")) {
        text = edit(&text, "fps", Some(&fps.to_string()));
        written.push("fps");
    }
    if nofade && !has_key(&text, "nofade") {
        text = edit(&text, "nofade", Some("on"));
        written.push("nofade");
    }
    if !written.is_empty() {
        write_text(client, &text)?;
    }
    Ok(written)
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = "# header\nrenderscale = 1.5\nfps = 60 # old\n\n; note\nOverlay = on\nfps = 120\nGfx.Field = 3\n";

    #[test]
    fn edit_replaces_last_drops_duplicates_keeps_the_rest() {
        let out = edit(SAMPLE, "fps", Some("144"));
        assert_eq!(out, "# header\nrenderscale = 1.5\n\n; note\nOverlay = on\nfps = 144\nGfx.Field = 3\n");
    }

    #[test]
    fn edit_appends_and_removes() {
        assert_eq!(edit("# c\nfps = 60\n", "nofade", Some("on")), "# c\nfps = 60\nnofade = on\n");
        assert_eq!(edit("", "fps", Some("60")), "fps = 60\n");
        assert_eq!(edit(SAMPLE, "fps", None), "# header\nrenderscale = 1.5\n\n; note\nOverlay = on\nGfx.Field = 3\n");
        assert_eq!(edit("a = 1\n", "fps", None), "a = 1\n");
    }

    #[test]
    fn migration_only_fills_absent_keys_and_set_fps_replaces() {
        let dir = std::env::temp_dir().join(format!("ascnet-ini-test-{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        let _ = fs::remove_file(dir.join(FILE));
        let text = |d: &Path| fs::read_to_string(d.join(FILE)).unwrap();

        assert_eq!(migrate(&dir, Some(90), true).unwrap(), ["fps", "nofade"]);
        assert_eq!(text(&dir), "fps = 90\nnofade = on\n");
        // Present keys are never overwritten.
        assert!(migrate(&dir, Some(30), true).unwrap().is_empty());

        fs::write(dir.join(FILE), "# keep\nnofade = off\n").unwrap();
        assert_eq!(migrate(&dir, Some(75), true).unwrap(), ["fps"]);
        assert_eq!(text(&dir), "# keep\nnofade = off\nfps = 75\n");

        set_fps(&dir, 144).unwrap();
        assert_eq!(text(&dir), "# keep\nnofade = off\nfps = 144\n");
        fs::remove_dir_all(dir).unwrap();
    }
}
