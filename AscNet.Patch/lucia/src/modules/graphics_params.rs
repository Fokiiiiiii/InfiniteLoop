/// Graphics options live in `ascnet-graphics.ini` beside PGR.exe, so they work however the game is started
/// (launcher, CrossOver shortcut, script). One `key = value` per line; `#` or `;` starts a comment.
/// Absent file = feature off. Every malformed line or unknown key is reported, never silently dropped.
///
/// Keys:
/// - `dump = 1`: log every render-pipeline / quality value, then each change.
/// - `fps = 240`: unlock the frame rate (`Application.targetFrameRate`, vsync off while set); a positive whole number.
/// - `nofade = on`: disable the camera / character proximity fade.
/// - `dpsmeter = on`: show settlement-recorded per-character damage in normal fights, including summons.
/// - `dpscorner = topright`: anchor the DPS window (topright, topleft, bottomright, bottomleft).
/// - `dpsdetails = on`: show DPS detail columns (default off).
/// - `dpsscale = 1`: DPS window scale (0.75-2.0).
/// - `dpshideoutside = on`: hide the DPS window outside fights (default off).
/// - `dpscompact = off`: hide the compact DPS window (default on).
/// - `ultrawide = on`: run fullscreen at the monitor's own resolution (21:9, 32:9, ...) instead of the game's
///   2560x2048 cap (see `graphics_lua.rs`); off restores the game's own window fallback.
/// - `ui16x9 = on`: on screens wider than 16:9, keep menu content and the fight HUD in a centred 16:9 area through the
///   game's own safe area (see `graphics_lua.rs`).
/// - `uifov = 1.3`: widen the vertical field of view of the UI-scene cameras (War Zone map, team select) by this
///   factor (0.5-2.0, applied to tan(fov/2)).
/// - `fightfov = 1.3`: widen supported perspective fight cameras by this factor (0.5-2.0, on tan(fov/2)).
/// - `uiscale = 0.85`: scale UI content relative to the game's own canvas scale (0.5-1.5).
/// - `renderscale = 2.0`: internal render scale in every scene (0.25-4.0).
/// - `<Setting>.<Field> = value`: override one field of a render-pipeline quality setting object, e.g.
///   `ShadowSetting.CascadeSize = 4096`. Names are the game's own (case-sensitive) and are checked in-game;
///   the value is typed there too (bool, number, enum name or number, comma list for arrays).
pub(crate) const CONFIG_FILE: &str = "ascnet-graphics.ini";
pub(crate) const RENDER_SCALE_RANGE: (f32, f32) = (0.25, 4.0);
pub(crate) const UI_FOV_RANGE: (f32, f32) = (0.5, 2.0);
pub(crate) const FIGHT_FOV_RANGE: (f32, f32) = (0.5, 2.0);
pub(crate) const UI_SCALE_RANGE: (f32, f32) = (0.5, 1.5);
pub(crate) const DPS_SCALE_RANGE: (f32, f32) = (0.75, 2.0);

use super::graphics_live::Desired;

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct Override {
    pub setting: String,
    pub field: String,
    pub value: String,
}

#[derive(Debug, PartialEq)]
pub(crate) struct Config {
    pub dump: bool,
    pub overlay: bool,
    pub dps_meter: bool,
    pub dps_corner: u8,
    pub dps_details: bool,
    pub dps_scale: f32,
    pub dps_hide_outside: bool,
    pub dps_compact: bool,
    pub fps: Option<i32>,
    pub nofade: bool,
    pub ultrawide: bool,
    pub ui16x9: bool,
    pub ui_fov: Option<f32>,
    pub fight_fov: Option<f32>,
    pub ui_scale: Option<f32>,
    pub render_scale: Option<f32>,
    /// In file order; a repeated key keeps its first position and takes the last value.
    pub overrides: Vec<Override>,
    pub problems: Vec<String>,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            dump: false,
            overlay: false,
            dps_meter: false,
            dps_corner: 0,
            dps_details: false,
            dps_scale: 1.0,
            dps_hide_outside: false,
            dps_compact: true,
            fps: None,
            nofade: false,
            ultrawide: false,
            ui16x9: false,
            ui_fov: None,
            fight_fov: None,
            ui_scale: None,
            render_scale: None,
            overrides: Vec::new(),
            problems: Vec::new(),
        }
    }
}

impl From<Config> for Desired {
    fn from(config: Config) -> Self {
        Desired { render_scale: config.render_scale, overrides: config.overrides, dump: config.dump, overlay: config.overlay, dps_meter: config.dps_meter, dps_corner: config.dps_corner, dps_details: config.dps_details, dps_scale: config.dps_scale, dps_hide_outside: config.dps_hide_outside, dps_compact: config.dps_compact, fps: config.fps, nofade: config.nofade, ultrawide: config.ultrawide, ui16x9: config.ui16x9, ui_fov: config.ui_fov, fight_fov: config.fight_fov, ui_scale: config.ui_scale }
    }
}

/// The ini text for `desired`; `Config::parse` reads it back to the same `Desired`.
pub(crate) fn render(desired: &Desired) -> String {
    let mut text = String::from("# AscNet graphics options (written by the in-game overlay)\n");
    if let Some(scale) = desired.render_scale {
        text.push_str(&format!("renderscale = {scale}\n"));
    }
    if let Some(scale) = desired.ui_fov {
        text.push_str(&format!("uifov = {scale}\n"));
    }
    if let Some(scale) = desired.fight_fov {
        text.push_str(&format!("fightfov = {scale}\n"));
    }
    if let Some(scale) = desired.ui_scale {
        text.push_str(&format!("uiscale = {scale}\n"));
    }
    for (key, on) in [("dump", desired.dump), ("overlay", desired.overlay), ("dpsmeter", desired.dps_meter), ("ultrawide", desired.ultrawide), ("ui16x9", desired.ui16x9)] {
        if on {
            text.push_str(&format!("{key} = on\n"));
        }
    }
    if desired.dps_corner != 0 {
        let corner = ["topright", "topleft", "bottomright", "bottomleft"].get(usize::from(desired.dps_corner)).copied().unwrap_or("topright");
        text.push_str(&format!("dpscorner = {corner}\n"));
    }
    if desired.dps_details {
        text.push_str("dpsdetails = on\n");
    }
    if desired.dps_scale != 1.0 {
        text.push_str(&format!("dpsscale = {}\n", desired.dps_scale));
    }
    if desired.dps_hide_outside {
        text.push_str("dpshideoutside = on\n");
    }
    if !desired.dps_compact {
        text.push_str("dpscompact = off\n");
    }
    if let Some(fps) = desired.fps {
        text.push_str(&format!("fps = {fps}\n"));
    }
    if desired.nofade {
        text.push_str("nofade = on\n");
    }
    for o in &desired.overrides {
        text.push_str(&format!("{}.{} = {}\n", o.setting, o.field, o.value));
    }
    text
}

impl Config {
    pub(crate) fn parse(text: &str) -> Self {
        let mut config = Config::default();
        for (index, raw) in text.lines().enumerate() {
            let line = raw.split(['#', ';']).next().unwrap_or("").trim();
            if line.is_empty() {
                continue;
            }
            let number = index + 1;
            let Some((key, value)) = line.split_once('=') else {
                config.problems.push(format!("line {number}: expected `key = value`, got `{line}`"));
                continue;
            };
            let (key, value) = (key.trim(), value.trim());
            match key.to_ascii_lowercase().as_str() {
                "dump" => match flag(value) {
                    Some(on) => config.dump = on,
                    None => config.problems.push(format!("line {number}: `dump` must be 1/0, true/false or on/off, got `{value}`")),
                },
                "overlay" => match flag(value) {
                    Some(on) => config.overlay = on,
                    None => config.problems.push(format!("line {number}: `overlay` must be 1/0, true/false or on/off, got `{value}`")),
                },
                "dpsmeter" => match flag(value) {
                    Some(on) => config.dps_meter = on,
                    None => config.problems.push(format!("line {number}: `dpsmeter` must be 1/0, true/false or on/off, got `{value}`")),
                },
                "dpscorner" => match value.to_ascii_lowercase().as_str() {
                    "topright" => config.dps_corner = 0,
                    "topleft" => config.dps_corner = 1,
                    "bottomright" => config.dps_corner = 2,
                    "bottomleft" => config.dps_corner = 3,
                    _ => config.problems.push(format!("line {number}: `dpscorner` must be topright, topleft, bottomright or bottomleft, got `{value}`")),
                },
                "dpsdetails" | "dpshideoutside" | "dpscompact" => match flag(value) {
                    Some(on) => match key.to_ascii_lowercase().as_str() {
                        "dpsdetails" => config.dps_details = on,
                        "dpshideoutside" => config.dps_hide_outside = on,
                        _ => config.dps_compact = on,
                    },
                    None => config.problems.push(format!("line {number}: `{}` must be 1/0, true/false or on/off, got `{value}`", key.to_ascii_lowercase())),
                },
                "dpsscale" => match value.parse::<f32>().ok().filter(|s| (DPS_SCALE_RANGE.0..=DPS_SCALE_RANGE.1).contains(s)) {
                    Some(scale) => config.dps_scale = scale,
                    None => config.problems.push(format!(
                        "line {number}: `dpsscale` must be a number from {} to {}, got `{value}`", DPS_SCALE_RANGE.0, DPS_SCALE_RANGE.1)),
                },
                "fps" => match fps(value) {
                    Some(fps) => config.fps = Some(fps),
                    None => config.problems.push(format!("line {number}: `fps` must be a positive whole number, got `{value}`")),
                },
                "nofade" => match flag(value) {
                    Some(on) => config.nofade = on,
                    None => config.problems.push(format!("line {number}: `nofade` must be 1/0, true/false or on/off, got `{value}`")),
                },
                "ultrawide" => match flag(value) {
                    Some(on) => config.ultrawide = on,
                    None => config.problems.push(format!("line {number}: `ultrawide` must be 1/0, true/false or on/off, got `{value}`")),
                },
                "ui16x9" => match flag(value) {
                    Some(on) => config.ui16x9 = on,
                    None => config.problems.push(format!("line {number}: `ui16x9` must be 1/0, true/false or on/off, got `{value}`")),
                },
                "renderscale" => match value.parse::<f32>().ok().filter(|s| (RENDER_SCALE_RANGE.0..=RENDER_SCALE_RANGE.1).contains(s)) {
                    Some(scale) => config.render_scale = Some(scale),
                    None => config.problems.push(format!(
                        "line {number}: `renderscale` must be a number from {} to {}, got `{value}`", RENDER_SCALE_RANGE.0, RENDER_SCALE_RANGE.1)),
                },
                "uifov" => match value.parse::<f32>().ok().filter(|s| (UI_FOV_RANGE.0..=UI_FOV_RANGE.1).contains(s)) {
                    Some(scale) => config.ui_fov = Some(scale),
                    None => config.problems.push(format!(
                        "line {number}: `uifov` must be a number from {} to {}, got `{value}`", UI_FOV_RANGE.0, UI_FOV_RANGE.1)),
                },
                "fightfov" => match value.parse::<f32>().ok().filter(|s| (FIGHT_FOV_RANGE.0..=FIGHT_FOV_RANGE.1).contains(s)) {
                    Some(scale) => config.fight_fov = Some(scale),
                    None => config.problems.push(format!(
                        "line {number}: `fightfov` must be a number from {} to {}, got `{value}`", FIGHT_FOV_RANGE.0, FIGHT_FOV_RANGE.1)),
                },
                "uiscale" => match value.parse::<f32>().ok().filter(|s| (UI_SCALE_RANGE.0..=UI_SCALE_RANGE.1).contains(s)) {
                    Some(scale) => config.ui_scale = Some(scale),
                    None => config.problems.push(format!(
                        "line {number}: `uiscale` must be a number from {} to {}, got `{value}`", UI_SCALE_RANGE.0, UI_SCALE_RANGE.1)),
                },
                _ => match key.split_once('.') {
                    Some((setting, field)) if identifier(setting) && identifier(field) && !value.is_empty() => {
                        match config.overrides.iter_mut().find(|o| o.setting == setting && o.field == field) {
                            Some(existing) => existing.value = value.to_string(),
                            None => config.overrides.push(Override { setting: setting.into(), field: field.into(), value: value.into() }),
                        }
                    }
                    Some(_) if value.is_empty() => config.problems.push(format!("line {number}: `{key}` has no value")),
                    _ => config.problems.push(format!("line {number}: unknown key `{key}` (expected dump, overlay, dpsmeter, dpscorner, dpsdetails, dpsscale, dpshideoutside, dpscompact, renderscale, fps, nofade, ultrawide, ui16x9, uifov, fightfov, uiscale or Setting.Field)")),
                },
            }
        }
        config
    }
}

/// A plain positive decimal integer; signs, spaces, `0`, `-1` ("platform default") and overflow are rejected.
fn fps(value: &str) -> Option<i32> {
    if !value.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    value.parse().ok().filter(|&fps| fps > 0)
}

fn flag(value: &str) -> Option<bool> {
    match value.to_ascii_lowercase().as_str() {
        "1" | "true" | "on" => Some(true),
        "0" | "false" | "off" => Some(false),
        _ => None,
    }
}

fn identifier(name: &str) -> bool {
    !name.is_empty() && !name.starts_with(|c: char| c.is_ascii_digit()) && name.chars().all(|c| c.is_ascii_alphanumeric() || c == '_')
}

/// A value from the file, typed by the in-game field it targets. Enum names are resolved in-game, so they stay text.
#[derive(Debug, PartialEq)]
pub(crate) enum Typed {
    Bool(bool),
    Int(i32),
    Float(f32),
    Ints(Vec<i32>),
    Floats(Vec<f32>),
}

pub(crate) fn bool_value(value: &str) -> Option<Typed> {
    flag(value).map(Typed::Bool)
}

pub(crate) fn int_value(value: &str) -> Option<Typed> {
    value.parse().ok().map(Typed::Int)
}

pub(crate) fn float_value(value: &str) -> Option<Typed> {
    value.parse::<f32>().ok().filter(|v| v.is_finite()).map(Typed::Float)
}

pub(crate) fn ints_value(value: &str) -> Option<Typed> {
    value.split(',').map(|v| v.trim().parse().ok()).collect::<Option<Vec<i32>>>().map(Typed::Ints)
}

pub(crate) fn floats_value(value: &str) -> Option<Typed> {
    value.split(',').map(|v| v.trim().parse::<f32>().ok().filter(|v| v.is_finite())).collect::<Option<Vec<f32>>>().map(Typed::Floats)
}

/// `*Downsample*` fields are render-target divisors (1 = full resolution). Verified for SSAO: its pass does
/// `width / SSAOSetting.Downsample` (`idiv`), so 0 crashed the game entering Babylonia. Other downsample fields are
/// held to the same rule.
pub(crate) fn check_divisor(field: &str, value: &Typed) -> Result<(), String> {
    match value {
        Typed::Int(n) if *n < 1 && field.to_ascii_lowercase().contains("downsample") => {
            Err(format!("`{field}` is a divisor (1 = full resolution, 2 = half); {n} would crash the renderer"))
        }
        _ => Ok(()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dump_flag_and_comments() {
        let config = Config::parse("# graphics\n\n  DUMP = On ; diagnostics\n");
        assert!(config.dump && config.problems.is_empty());
        assert!(!Config::parse("dump=0").dump);
    }

    #[test]
    fn later_lines_override_earlier_ones() {
        let config = Config::parse("dump = 1\ndump = off\nShadowSetting.CascadeSize = 2048\nrenderscale = 1.5\nShadowSetting.CascadeSize = 4096\nrenderscale=2");
        assert!(!config.dump);
        assert_eq!(config.render_scale, Some(2.0));
        assert_eq!(config.overrides, [Override { setting: "ShadowSetting".into(), field: "CascadeSize".into(), value: "4096".into() }]);
    }

    #[test]
    fn render_scale_is_bounded() {
        assert_eq!(Config::parse("RenderScale = 0.25").render_scale, Some(0.25));
        assert_eq!(Config::parse("renderscale = 4").render_scale, Some(4.0));
        for bad in ["0.2", "4.01", "NaN", "inf", "2x", ""] {
            let config = Config::parse(&format!("renderscale = {bad}"));
            assert_eq!(config.render_scale, None, "{bad}");
            assert_eq!(config.problems.len(), 1, "{bad}");
        }
    }

    #[test]
    fn overrides_keep_order_case_and_raw_value() {
        let config = Config::parse("SSAOSetting.Enable = on\nAntiAliasingSetting.ForwardAAMethod = SubpixelMorphologicalAntiAliasing\nShadowSetting.CascadeFrameUpdate = 0, 1, 2, 4");
        assert!(config.problems.is_empty());
        let keys: Vec<_> = config.overrides.iter().map(|o| (o.setting.as_str(), o.field.as_str(), o.value.as_str())).collect();
        assert_eq!(keys, [
            ("SSAOSetting", "Enable", "on"),
            ("AntiAliasingSetting", "ForwardAAMethod", "SubpixelMorphologicalAntiAliasing"),
            ("ShadowSetting", "CascadeFrameUpdate", "0, 1, 2, 4"),
        ]);
    }

    #[test]
    fn problems_are_reported_with_line_numbers() {
        let config = Config::parse("dump = yes\nrenderscale 2\nshadows = 4\nA.B.C = 1\nShadowSetting.CascadeSize =\n9a.b = 1");
        assert_eq!(config.problems, [
            "line 1: `dump` must be 1/0, true/false or on/off, got `yes`",
            "line 2: expected `key = value`, got `renderscale 2`",
            "line 3: unknown key `shadows` (expected dump, overlay, dpsmeter, dpscorner, dpsdetails, dpsscale, dpshideoutside, dpscompact, renderscale, fps, nofade, ultrawide, ui16x9, uifov, fightfov, uiscale or Setting.Field)",
            "line 4: unknown key `A.B.C` (expected dump, overlay, dpsmeter, dpscorner, dpsdetails, dpsscale, dpshideoutside, dpscompact, renderscale, fps, nofade, ultrawide, ui16x9, uifov, fightfov, uiscale or Setting.Field)",
            "line 5: `ShadowSetting.CascadeSize` has no value",
            "line 6: unknown key `9a.b` (expected dump, overlay, dpsmeter, dpscorner, dpsdetails, dpsscale, dpshideoutside, dpscompact, renderscale, fps, nofade, ultrawide, ui16x9, uifov, fightfov, uiscale or Setting.Field)",
        ]);
    }

    #[test]
    fn ui_fov_is_bounded() {
        assert_eq!(Config::parse("UIFov = 1.3").ui_fov, Some(1.3));
        assert_eq!(Config::parse("uifov = 0.5").ui_fov, Some(0.5));
        for bad in ["0.49", "2.01", "wide", "NaN", ""] {
            let config = Config::parse(&format!("uifov = {bad}"));
            assert_eq!(config.ui_fov, None, "{bad}");
            assert_eq!(config.problems.len(), 1, "{bad}");
        }
    }

    #[test]
    fn fight_fov_and_ui_scale_are_bounded() {
        for (key, upper) in [("FightFov", 2.0), ("UIScale", 1.5)] {
            for value in [0.5, 1.0, upper] {
                let config = Config::parse(&format!("{key} = {value}"));
                let actual = if key == "FightFov" { config.fight_fov } else { config.ui_scale };
                assert_eq!(actual, Some(value));
                assert!(config.problems.is_empty());
            }
            for bad in ["0.49", "2.01", "NaN", "inf", "-inf", "wide", ""] {
                let config = Config::parse(&format!("{key} = {bad}"));
                assert_eq!((config.fight_fov, config.ui_scale), (None, None), "{key}={bad}");
                assert_eq!(config.problems.len(), 1, "{key}={bad}");
            }
        }
        assert_eq!(Config::parse("uiscale = 1.51").ui_scale, None);
        let config = Config::parse("fightfov = 0.5\nfightfov = 2\nuiscale = 0.5\nuiscale = 1.5");
        assert_eq!((config.fight_fov, config.ui_scale), (Some(2.0), Some(1.5)));
        assert!(config.problems.is_empty());
    }

    #[test]
    fn downsample_fields_reject_divisors_below_one() {
        assert!(check_divisor("Downsample", &Typed::Int(0)).is_err());
        assert!(check_divisor("DownsampleFactor", &Typed::Int(-1)).is_err());
        assert!(check_divisor("ScreenSpaceShadowDownSample", &Typed::Int(0)).is_err());
        assert!(check_divisor("Downsample", &Typed::Int(1)).is_ok());
        assert!(check_divisor("CascadeSize", &Typed::Int(0)).is_ok());
    }

    #[test]
    fn typed_values() {
        assert_eq!(bool_value("TRUE"), Some(Typed::Bool(true)));
        assert_eq!(bool_value("2"), None);
        assert_eq!(int_value("-3"), Some(Typed::Int(-3)));
        assert_eq!(int_value("1.5"), None);
        assert_eq!(float_value("1e2"), Some(Typed::Float(100.0)));
        assert_eq!(float_value("inf"), None);
        assert_eq!(ints_value("0, 1,2 ,4"), Some(Typed::Ints(vec![0, 1, 2, 4])));
        assert_eq!(ints_value("0,,1"), None);
        assert_eq!(floats_value("0.15,0.4"), Some(Typed::Floats(vec![0.15, 0.4])));
    }

    #[test]
    fn overlay_flag() {
        let config = Config::parse("overlay = on");
        assert!(config.overlay && config.problems.is_empty());
        assert!(!Config::parse("overlay = 0").overlay);
        assert_eq!(Config::parse("overlay = maybe").problems, ["line 1: `overlay` must be 1/0, true/false or on/off, got `maybe`"]);
    }

    #[test]
    fn dps_meter_flag() {
        assert!(!Desired::default().dps_meter);
        assert!(!Config::parse("").dps_meter);
        for value in ["on", "1", "true"] {
            let config = Config::parse(&format!("DpsMeter = {value}"));
            assert!(config.dps_meter && config.problems.is_empty());
            let desired = Desired::from(config);
            assert!(desired.dps_meter);
            assert!(render(&desired).contains("dpsmeter = on\n"));
        }
        for value in ["off", "0", "false"] {
            let config = Config::parse(&format!("dpsmeter = {value}"));
            assert!(!config.dps_meter && config.problems.is_empty());
            assert!(!render(&Desired::from(config)).contains("dpsmeter"));
        }
        assert!(!Config::parse("dpsmeter = on\ndpsmeter = off").dps_meter);
        assert_eq!(Config::parse("dpsmeter = maybe").problems, ["line 1: `dpsmeter` must be 1/0, true/false or on/off, got `maybe`"]);
    }

    #[test]
    fn fps_accepts_plain_positive_integers() {
        for (text, want) in [("240", 240), ("1", 1), ("2147483647", i32::MAX)] {
            let config = Config::parse(&format!("FPS = {text}"));
            assert_eq!(config.fps, Some(want), "{text}");
            assert!(config.problems.is_empty());
        }
    }

    #[test]
    fn fps_rejects_everything_else() {
        for bad in ["", "0", "000", "-1", "+60", "6.0", "abc", "2147483648"] {
            let config = Config::parse(&format!("fps = {bad}"));
            assert_eq!(config.fps, None, "{bad:?}");
            assert_eq!(config.problems, [format!("line 1: `fps` must be a positive whole number, got `{bad}`")], "{bad:?}");
        }
    }

    #[test]
    fn ultrawide_flag() {
        let config = Config::parse("Ultrawide = on");
        assert!(config.ultrawide && config.problems.is_empty());
        assert!(!Config::parse("ultrawide = off").ultrawide);
        assert_eq!(Config::parse("ultrawide = wide").problems, ["line 1: `ultrawide` must be 1/0, true/false or on/off, got `wide`"]);
    }

    #[test]
    fn ui16x9_flag() {
        let config = Config::parse("UI16x9 = on");
        assert!(config.ui16x9 && config.problems.is_empty());
        assert!(!Config::parse("ui16x9 = off").ui16x9);
        assert_eq!(Config::parse("ui16x9 = yes").problems, ["line 1: `ui16x9` must be 1/0, true/false or on/off, got `yes`"]);
    }

    #[test]
    fn nofade_flag() {
        let config = Config::parse("nofade = On");
        assert!(config.nofade && config.problems.is_empty());
        assert!(!Config::parse("nofade = 0").nofade);
        assert!(!Config::parse("").nofade);
        assert_eq!(Config::parse("nofade = maybe").problems, ["line 1: `nofade` must be 1/0, true/false or on/off, got `maybe`"]);
    }

    #[test]
    fn render_round_trips() {
        let o = |setting: &str, field: &str, value: &str| Override { setting: setting.into(), field: field.into(), value: value.into() };
        let desired = Desired {
            render_scale: Some(1.333),
            overrides: vec![
                o("ShadowSetting", "CascadeFrameUpdate", "0,1,2,4"),
                o("AntiAliasingSetting", "ForwardAAMethod", "SubpixelMorphologicalAntiAliasing"),
                o("SSAOSetting", "Enable", "off"),
            ],
            dump: true,
            overlay: true,
            dps_meter: true,
            dps_corner: 3,
            dps_details: true,
            dps_scale: 1.5,
            dps_hide_outside: true,
            dps_compact: false,
            fps: Some(240),
            nofade: true,
            ultrawide: true,
            ui16x9: true,
            ui_fov: Some(1.35),
            fight_fov: Some(1.25),
            ui_scale: Some(0.85),
        };
        let config = Config::parse(&render(&desired));
        assert!(config.problems.is_empty());
        assert_eq!(Desired::from(config), desired);
        assert_eq!(Desired::from(Config::parse(&render(&Desired::default()))), Desired::default());
    }

    #[test]
    fn dps_preferences_parse_and_round_trip() {
        let defaults = Desired::default();
        assert_eq!((defaults.dps_corner, defaults.dps_details, defaults.dps_scale, defaults.dps_hide_outside, defaults.dps_compact), (0, false, 1.0, false, true));
        assert!(!render(&defaults).contains("dps"));
        for (corner, word) in ["topright", "topleft", "bottomright", "bottomleft"].into_iter().enumerate() {
            let config = Config::parse(&format!("DPSCorner = {}", word.to_ascii_uppercase()));
            assert!(config.problems.is_empty());
            assert_eq!(config.dps_corner, corner as u8);
            let desired = Desired::from(config);
            assert_eq!(Desired::from(Config::parse(&render(&desired))), desired);
        }
        for bad in ["", "0", "4", "top", "bottom-right"] {
            let config = Config::parse(&format!("dpscorner = {bad}"));
            assert_eq!(config.dps_corner, 0);
            assert_eq!(config.problems, [format!("line 1: `dpscorner` must be topright, topleft, bottomright or bottomleft, got `{bad}`")]);
        }
        for scale in [0.75, 1.0, 2.0] {
            let config = Config::parse(&format!("DPSScale = {scale}"));
            assert!(config.problems.is_empty());
            assert_eq!(config.dps_scale, scale);
            let desired = Desired::from(config);
            assert_eq!(Desired::from(Config::parse(&render(&desired))), desired);
        }
        for bad in ["", "0.749", "2.001", "-1", "NaN", "inf", "-inf", "large"] {
            let config = Config::parse(&format!("dpsscale = {bad}"));
            assert_eq!(config.dps_scale, 1.0);
            assert_eq!(config.problems, [format!("line 1: `dpsscale` must be a number from 0.75 to 2, got `{bad}`")]);
        }
        for key in ["dpsdetails", "dpshideoutside", "dpscompact"] {
            for (value, expected) in [("on", true), ("TRUE", true), ("1", true), ("OFF", false), ("false", false), ("0", false)] {
                let config = Config::parse(&format!("{key} = {value}"));
                assert!(config.problems.is_empty());
                let actual = match key {
                    "dpsdetails" => config.dps_details,
                    "dpshideoutside" => config.dps_hide_outside,
                    _ => config.dps_compact,
                };
                assert_eq!(actual, expected);
                let desired = Desired::from(config);
                assert_eq!(Desired::from(Config::parse(&render(&desired))), desired);
            }
            let config = Config::parse(&format!("{key} = maybe"));
            assert_eq!(config.problems, [format!("line 1: `{key}` must be 1/0, true/false or on/off, got `maybe`")]);
        }
        let config = Config::parse("dpscorner = bottomleft\ndpscorner = top\ndpsscale = 1.5\ndpsscale = NaN\ndpsdetails = on\ndpsdetails = maybe");
        assert_eq!((config.dps_corner, config.dps_scale, config.dps_details), (3, 1.5, true));
        assert_eq!(config.problems.len(), 3);
    }
}
