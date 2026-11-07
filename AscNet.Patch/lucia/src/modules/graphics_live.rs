//! State shared between the graphics worker (IL2CPP side, `graphics.rs`/`graphics_tweaks.rs`) and the in-game
//! overlay (render thread, `graphics_overlay.rs`). The overlay never touches IL2CPP: it edits `desired` and reads
//! `catalogue`/`status`; the worker applies `desired` every tick and republishes the rest.
use std::sync::{LazyLock, Mutex};

use super::graphics_params::Override;
use super::graphics_dps::DpsSnapshot;

/// What the player wants; the same data as `ascnet-graphics.ini`. Override values stay text in the ini syntax
/// (`on`/`off`, numbers, enum names, comma lists); the worker types them against the live field.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct Desired {
    pub render_scale: Option<f32>,
    pub overrides: Vec<Override>,
    pub dump: bool,
    pub overlay: bool,
    /// `dpsmeter = on`: sample recorded normal-fight damage for the overlay.
    pub dps_meter: bool,
    /// DPS window corner: 0 top-right, 1 top-left, 2 bottom-right, 3 bottom-left.
    pub dps_corner: u8,
    pub dps_details: bool,
    pub dps_scale: f32,
    pub dps_hide_outside: bool,
    pub dps_compact: bool,
    /// `fps = <n>`: `Application.targetFrameRate = n` (and `vSyncCount = 0`) while set; `None` = the game's own.
    pub fps: Option<i32>,
    /// `nofade = on`: camera/character proximity fade disabled while true.
    pub nofade: bool,
    /// `ultrawide = on`: fullscreen at the monitor's own resolution, past the game's 2560x2048 cap.
    pub ultrawide: bool,
    /// `ui16x9 = on`: on screens wider than 16:9, menus and the fight HUD keep to a centred 16:9 safe area.
    pub ui16x9: bool,
    /// `uifov = <scale>`: UI-scene cameras' vertical field of view widened by this factor (on tan(fov/2)).
    pub ui_fov: Option<f32>,
    /// `fightfov = <scale>`: supported perspective fight cameras' FOV factor (on tan(fov/2)).
    pub fight_fov: Option<f32>,
    /// `uiscale = <scale>`: UI content scale relative to the game's own canvas scale.
    pub ui_scale: Option<f32>,
}

impl Default for Desired {
    fn default() -> Self {
        Self {
            render_scale: None,
            overrides: Vec::new(),
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
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) enum FieldKind {
    Bool,
    Int,
    Float,
    /// Constant name and value, in declaration order.
    Enum(Vec<(String, i32)>),
    Ints,
    Floats,
    /// Shown, not editable (structs, objects, other types).
    ReadOnly,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct FieldInfo {
    pub name: String,
    pub kind: FieldKind,
    /// Current live value, formatted in ini syntax (enum = constant name, arrays = comma list).
    pub value: String,
    /// The game's own value while this field is overridden (what removing the override restores).
    pub game_value: Option<String>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct SettingInfo {
    /// Field name on `RenderPipelineQualityRuntimeData`, e.g. `ShadowSetting` (the `<Setting>` in an override key).
    pub name: String,
    pub fields: Vec<FieldInfo>,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub(crate) struct Status {
    pub scene: String,
    /// Last scale the game itself asked for through `SetupExtraRenderScale`.
    pub game_render_scale: Option<f32>,
    /// `GetRenderScale()` as the pipeline reports it.
    pub effective_render_scale: Option<f32>,
    /// Latest problem per override key (`Setting.Field`), `renderscale`, `fps`, `nofade` or `ultrawide`; cleared when the key
    /// applies cleanly.
    pub errors: Vec<(String, String)>,
    /// `Application.targetFrameRate` and `QualitySettings.vSyncCount` as last read (None until read).
    pub target_frame_rate: Option<i32>,
    pub vsync_count: Option<i32>,
    /// `Screen.width/height` and `Screen.fullScreen` as the main thread last read them (None until read).
    pub screen: Option<(i32, i32)>,
    pub fullscreen: Option<bool>,
}

#[derive(Debug, Default)]
pub(crate) struct Live {
    pub desired: Desired,
    /// Empty until IL2CPP and `GetRuntimeData()` are ready; refreshed by the worker every tick.
    pub catalogue: Vec<SettingInfo>,
    pub status: Status,
    pub dps: Option<DpsSnapshot>,
    /// Overlay reset requests; the worker consumes changes without calling Lua on the render thread.
    pub dps_reset: u64,
}

pub(crate) static LIVE: LazyLock<Mutex<Live>> = LazyLock::new(|| Mutex::new(Live::default()));
