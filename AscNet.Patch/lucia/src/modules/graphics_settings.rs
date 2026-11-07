//! Settings-tab transport: only the main-thread bridge touches Lua; desired state stays in Rust.
use super::{graphics_live::{Desired, Live}, graphics_lua::LuaBridge, graphics_params::{self, Config}};
use std::fmt::Write;

const BOOTSTRAP: &str = "ascnet-settings-bootstrap";
const POLL: &str = "ascnet-settings-state";
const KEYS: [&str; 16] = ["renderscale", "fps", "nofade", "ultrawide", "ui16x9", "uifov", "fightfov", "uiscale", "dpsmeter", "dpscorner", "dpsdetails", "dpsscale", "dpshideoutside", "dpscompact", "dump", "overlay"];
const MAX_PENDING: usize = 1024;
const INIT: &str = "local b=rawget(_G,'__lucia_settings'); if type(b)~='table' then b={state={},pending={},seq=0}; rawset(_G,'__lucia_settings',b) end; ";

#[derive(Default)]
pub(super) struct Settings {
    boot_queued: bool,
    boot_seen: u64,
    poll_seen: u64,
    in_flight: bool,
    message: String,
}

impl Settings {
    pub(super) fn receive(&mut self, bridge: &Result<LuaBridge, String>) {
        let Ok(bridge) = bridge else { return };
        if let Some((seq, result)) = bridge.outcome(BOOTSTRAP) {
            if seq != self.boot_seen {
                self.boot_seen = seq;
                match result {
                    Ok(message) => {
                        crate::diag::log(&message);
                        if message.starts_with("FAILED ") { self.message = message; }
                    }
                    Err(error) => {
                        self.message = format!("FAILED lua AscNet Settings: {error}");
                        crate::diag::log(&self.message);
                    }
                }
            }
        }
        let Some((seq, result)) = bridge.outcome(POLL) else { return };
        if seq == self.poll_seen { return; }
        self.poll_seen = seq;
        self.in_flight = false;
        match result {
            Err(error) => {
                self.message = format!("FAILED lua AscNet Settings: bridge: {error}");
                crate::diag::log(&self.message);
            }
            Ok(text) => {
                let (pending, logs) = text.split_once("\n!logs\n").unwrap_or((&text, ""));
                let (changed, errors, save) = {
                    let mut live = super::live();
                    let (changed, errors) = apply_pending(&mut live.desired, pending);
                    let save = changed.then(|| graphics_params::render(&live.desired));
                    (changed, errors, save)
                };
                if let Some(text) = save { self.message = super::graphics_overlay::save_ini(&text); }
                if !errors.is_empty() {
                    let errors = errors.join("; ");
                    self.message = if changed { format!("{}; {errors}", self.message) } else { errors };
                }
                for line in logs.lines().take(16) {
                    if line.len() <= 400 && (line.starts_with("OK lua AscNet Settings: ") || line.starts_with("FAILED lua AscNet Settings: ")) {
                        crate::diag::log(line);
                        if line.starts_with("FAILED ") { self.message = line.into(); }
                    }
                }
            }
        }
    }

    pub(super) fn publish(&mut self, bridge: &Result<LuaBridge, String>) {
        let Ok(bridge) = bridge else { return };
        if !self.boot_queued {
            bridge.queue(BOOTSTRAP, format!("{INIT}\n{}", include_str!("graphics_settings.lua")));
            self.boot_queued = true;
        }
        if self.in_flight { return; }
        let mut source = {
            let live = super::live();
            encode_state(&live, &self.message)
        };
        source.push_str("local out={}; local p=b.pending; b.pending={}; if type(p)=='table' then ");
        // Only named scalar controls cross the boundary; neither arbitrary keys nor Lua expressions do.
        for key in KEYS {
            let _ = write!(source, "do local v=rawget(p,'{key}'); if v~=nil then local t=type(v); if t=='boolean' or t=='number' or t=='string' then v=tostring(v); if #v<=64 and not string.find(v,'[\\r\\n]') then out[#out+1]='{key}='..v else out[#out+1]='{key}=invalid' end else out[#out+1]='{key}=invalid' end end end; ");
        }
        source.push_str("end; local logs={}; local queued=b.logs; b.logs={}; if type(queued)=='table' then for i=1,math.min(#queued,16) do local v=queued[i]; if type(v)=='string' then logs[#logs+1]=string.sub(string.gsub(v,'[\\r\\n]',' '),1,400) end end end; return table.concat(out,'\\n')..'\\n!logs\\n'..table.concat(logs,'\\n')");
        bridge.queue(POLL, source);
        self.in_flight = true;
    }
}

fn quote(value: &str) -> String {
    let mut out = String::from("\"");
    for byte in value.bytes() {
        match byte {
            b'"' => out.push_str("\\\""),
            b'\\' => out.push_str("\\\\"),
            32..=126 => out.push(byte as char),
            _ => { let _ = write!(out, "\\{byte:03}"); }
        }
    }
    out.push('"');
    out
}

fn optional<T: std::fmt::Display>(value: Option<T>) -> String {
    value.map_or_else(|| "false".into(), |value| value.to_string())
}

fn encode_state(live: &Live, message: &str) -> String {
    let d = &live.desired;
    let s = &live.status;
    let corner = ["topright", "topleft", "bottomright", "bottomleft"].get(usize::from(d.dps_corner)).copied().unwrap_or("topright");
    let mut out = format!("{INIT}b.state={{desired={{renderscale={},fps={},nofade={},ultrawide={},ui16x9={},uifov={},fightfov={},uiscale={},dpsmeter={},dpscorner={},dpsdetails={},dpsscale={},dpshideoutside={},dpscompact={},dump={},overlay={}}},", optional(d.render_scale), optional(d.fps), d.nofade, d.ultrawide, d.ui16x9, optional(d.ui_fov), optional(d.fight_fov), optional(d.ui_scale), d.dps_meter, quote(corner), d.dps_details, d.dps_scale, d.dps_hide_outside, d.dps_compact, d.dump, d.overlay);
    out.push_str("ranges={");
    for (key, (min, max)) in [("renderscale", graphics_params::RENDER_SCALE_RANGE), ("uifov", graphics_params::UI_FOV_RANGE), ("fightfov", graphics_params::FIGHT_FOV_RANGE), ("uiscale", graphics_params::UI_SCALE_RANGE), ("dpsscale", graphics_params::DPS_SCALE_RANGE), ("dpscorner", (0.0, 3.0))] {
        let _ = write!(out, "{key}={{{min},{max}}},");
    }
    let (width, height) = s.screen.map_or((None, None), |(w,h)| (Some(w), Some(h)));
    let _ = write!(out, "fps={{1,2147483647}}}},status={{scene={},game_render_scale={},effective_render_scale={},target_frame_rate={},vsync_count={},screen_width={},screen_height={},fullscreen={}}},message={},errors={{", quote(&s.scene), optional(s.game_render_scale), optional(s.effective_render_scale), optional(s.target_frame_rate), optional(s.vsync_count), optional(width), optional(height), optional(s.fullscreen), quote(message));
    for (key, message) in &s.errors {
        let _ = write!(out, "{{key={},message={}}},", quote(key), quote(message));
    }
    out.push_str("}}; b.seq=(tonumber(b.seq) or 0)+1; if type(b.refresh)=='function' then pcall(b.refresh) end; ");
    out
}

fn apply_pending(desired: &mut Desired, text: &str) -> (bool, Vec<String>) {
    if text.len() > MAX_PENDING { return (false, vec!["Settings request exceeds limit".into()]); }
    let before = desired.clone();
    let mut errors = Vec::new();
    for (index, line) in text.lines().enumerate() {
        if index >= KEYS.len() { errors.push("Too many settings changes".into()); break; }
        let Some((key, value)) = line.split_once('=') else { errors.push("Invalid settings request".into()); continue; };
        if !KEYS.contains(&key) || value.len() > 64 || value.contains(['#', ';', '\r']) {
            errors.push(format!("Invalid settings key/value: {key}"));
            continue;
        }
        let clear = value == "false" && matches!(key, "renderscale" | "fps" | "uifov" | "fightfov" | "uiscale");
        let parsed = Config::parse(if clear { "" } else if key == "dpsscale" && value == "false" { "dpsscale=1" } else { line });
        if !parsed.problems.is_empty() { errors.extend(parsed.problems); continue; }
        match key {
            "renderscale" => desired.render_scale = parsed.render_scale,
            "fps" => desired.fps = parsed.fps,
            "uifov" => desired.ui_fov = parsed.ui_fov,
            "fightfov" => desired.fight_fov = parsed.fight_fov,
            "uiscale" => desired.ui_scale = parsed.ui_scale,
            "nofade" => desired.nofade = parsed.nofade,
            "ultrawide" => desired.ultrawide = parsed.ultrawide,
            "ui16x9" => desired.ui16x9 = parsed.ui16x9,
            "dpsmeter" => desired.dps_meter = parsed.dps_meter,
            "dpscorner" => desired.dps_corner = parsed.dps_corner,
            "dpsdetails" => desired.dps_details = parsed.dps_details,
            "dpsscale" => desired.dps_scale = parsed.dps_scale,
            "dpshideoutside" => desired.dps_hide_outside = parsed.dps_hide_outside,
            "dpscompact" => desired.dps_compact = parsed.dps_compact,
            "dump" => desired.dump = parsed.dump,
            "overlay" => desired.overlay = parsed.overlay,
            _ => {}
        }
    }
    (*desired != before, errors)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn state_encoding_and_pending_validation() {
        let mut live = Live::default();
        live.status.scene = "a\"\\\n雪".into();
        live.status.errors.push(("fps".into(), "not ready".into()));
        let state = encode_state(&live, "saved");
        assert!(state.contains("fps=false"));
        assert!(state.contains("renderscale={0.25,4}"));
        assert!(state.contains("key=\"fps\",message=\"not ready\""));
        assert_eq!(quote("\"\\\n"), "\"\\\"\\\\\\010\"");
        assert!(!state.contains('雪'));
        let (changed, errors) = apply_pending(&mut live.desired, "fps=240\nrenderscale=4\nui16x9=true");
        assert!(changed && errors.is_empty());
        assert_eq!(live.desired.fps, Some(240));
        assert_eq!(live.desired.render_scale, Some(4.0));
        assert!(live.desired.ui16x9);
        let before = live.desired.clone();
        let (changed, errors) = apply_pending(&mut live.desired, "fps=0\nrenderscale=4.1\nuifov=NaN\nunknown=true\nShadow.X=2\nnofade=true;bad");
        assert!(!changed && errors.len() == 6);
        assert_eq!(before, live.desired);
        assert!(!apply_pending(&mut live.desired, &"x".repeat(MAX_PENDING + 1)).0);
        assert!(apply_pending(&mut live.desired, "fps=false\nrenderscale=false").0);
        assert_eq!(live.desired.fps, None);
        assert_eq!(live.desired.render_scale, None);
        assert!(!apply_pending(&mut live.desired, "").0);
        let roundtrip: Desired = Config::parse(&graphics_params::render(&live.desired)).into();
        assert_eq!(roundtrip, live.desired);
        let too_many = "nofade=false\n".repeat(KEYS.len() + 1);
        assert_eq!(apply_pending(&mut live.desired, &too_many).1, vec!["Too many settings changes"]);
    }

    #[test]
    fn dps_preferences_validate_and_clear() {
        let mut live = Live::default();
        let state = encode_state(&live, "");
        assert!(state.contains("dpscorner=\"topright\",dpsdetails=false,dpsscale=1,dpshideoutside=false,dpscompact=true"));
        for (corner, index) in [("topright", 0), ("topleft", 1), ("bottomright", 2), ("bottomleft", 3)] {
            let (_, errors) = apply_pending(&mut live.desired, &format!("dpscorner={corner}"));
            assert!(errors.is_empty());
            assert_eq!(live.desired.dps_corner, index);
        }
        let (changed, errors) = apply_pending(&mut live.desired, "dpsscale=1.25\ndpsdetails=true\ndpshideoutside=true\ndpscompact=false");
        assert!(changed && errors.is_empty());
        assert_eq!(live.desired.dps_scale, 1.25);
        assert!(live.desired.dps_details && live.desired.dps_hide_outside);
        assert!(!live.desired.dps_compact);
        let before = live.desired.clone();
        let (changed, errors) = apply_pending(&mut live.desired, "dpscorner=3\ndpsscale=NaN\ndpsscale=0\ndpsdetails=maybe\ndpshideoutside=maybe\ndpscompact=maybe");
        assert!(!changed && errors.len() == 6);
        assert_eq!(live.desired, before);
        let (changed, errors) = apply_pending(&mut live.desired, "dpsscale=false\ndpscorner=topright\ndpsdetails=false\ndpshideoutside=false\ndpscompact=true");
        assert!(changed && errors.is_empty());
        assert_eq!(live.desired.dps_scale, 1.0);
        assert_eq!(live.desired.dps_corner, 0);
        assert!(!live.desired.dps_details && !live.desired.dps_hide_outside);
        assert!(live.desired.dps_compact);
        assert_eq!(Desired::from(Config::parse(&graphics_params::render(&live.desired))), live.desired);
    }
}
