//! In-game Dear ImGui overlay (hudhook, D3D11 Present hook) over the shared `LIVE` state. It only edits
//! `desired` and reads `catalogue`/`status`; applying is the graphics worker's job. Ctrl+Shift+G toggles.
use std::collections::{HashMap, VecDeque};

use hudhook::hooks::dx11::ImguiDx11Hooks;
use hudhook::imgui::{Condition, Io, Key, ProgressBar, StyleColor, TreeNodeFlags, Ui, WindowFlags};
use hudhook::windows::Win32::Foundation::HINSTANCE;
use hudhook::{Hudhook, ImguiRenderLoop, MessageFilter};

use super::graphics_live::{Desired, FieldInfo, FieldKind, Live, Status, LIVE};
use super::graphics_dps::DpsSnapshot;
use super::graphics_lua;
use super::graphics_params::{render, Override, CONFIG_FILE, DPS_SCALE_RANGE, FIGHT_FOV_RANGE, RENDER_SCALE_RANGE, UI_FOV_RANGE, UI_SCALE_RANGE};
use super::il2cpp;

const RED: [f32; 4] = [1.0, 0.35, 0.35, 1.0];
const AMBER: [f32; 4] = [1.0, 0.8, 0.3, 1.0];

pub(super) fn start() {
    let mut builder = Hudhook::builder().with::<ImguiDx11Hooks>(Overlay::default());
    if let Some(module) = crate::LUCIA_MODULE.get() {
        builder = builder.with_hmodule(HINSTANCE(*module as *mut _));
    }
    match builder.build().apply() {
        Ok(()) => il2cpp::ok("overlay", "D3D11 Present hook installed; Ctrl+Shift+G toggles"),
        Err(error) => il2cpp::failed("overlay", &format!("{error:?}"), "the game runs without the overlay"),
    }
}

#[derive(Default)]
struct Overlay {
    visible: bool,
    /// Text being edited for array fields, keyed `Setting.Field`; committed on Enter.
    buffers: HashMap<String, String>,
    save_result: String,
    /// Last FPS value before the unlock was switched off (re-enabling restores it; 0 = never set, uses 240).
    fps_last: i32,
    /// The last typed FPS value was below 1 and was not applied.
    fps_invalid: bool,
    console_source: String,
    console_history: VecDeque<(u64, String, Option<Result<String, String>>)>,
    console_seen: Option<u64>,
    console_scroll: bool,
    /// Frames until swallowed key-ups are replayed after hiding (0 = nothing pending).
    release_in: u8,
}

impl ImguiRenderLoop for Overlay {
    fn message_filter(&self, _io: &Io) -> MessageFilter {
        if self.visible { MessageFilter::InputAll } else { MessageFilter::empty() }
    }

    fn render(&mut self, ui: &mut Ui) {
        self.poll_console();
        let was_visible = self.visible;
        let io = ui.io();
        let overlay_enabled = LIVE.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).desired.overlay;
        if overlay_enabled && io.key_ctrl && io.key_shift && ui.is_key_pressed_no_repeat(Key::G) {
            self.visible = !self.visible;
        }
        if !overlay_enabled {
            self.visible = false;
        }
        self.draw_compact_dps(ui);
        if !self.visible {
            self.release_after_hide(was_visible);
            return;
        }
        let mut save = None;
        let mut open = true;
        ui.window("AscNet graphics")
            .size([520.0, 640.0], Condition::FirstUseEver)
            .opened(&mut open)
            .build(|| {
                let mut live = LIVE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
                save = self.draw(ui, &mut live);
            });
        self.visible = open;
        if !self.visible {
            self.release_after_hide(true);
        }
        if let Some(text) = save {
            self.save_result = save_ini(&text);
        }
    }
}

impl Overlay {
    /// While visible the overlay blocks keyboard messages, including the key-ups of keys pressed before it opened
    /// (at least Ctrl and Shift of the hotkey). The game then believes them held, and its key bindings, which match
    /// whole key combinations, stop firing (skill orbs, character switch) while plain movement still works. A few
    /// frames after hiding, when the message filter is open again, release every key that is not physically down.
    fn release_after_hide(&mut self, just_hidden: bool) {
        if just_hidden {
            self.release_in = 3;
        }
        if self.release_in == 0 {
            return;
        }
        self.release_in -= 1;
        if self.release_in == 0 {
            release_keys();
        }
    }

    /// Returns the ini text when "Save" was pressed; the file write happens outside the `LIVE` lock.
    fn draw(&mut self, ui: &Ui, live: &mut Live) -> Option<String> {
        let Live { desired, catalogue, status, dps, dps_reset } = live;
        self.draw_dps_controls(ui, desired, dps.as_ref(), dps_reset);
        if catalogue.is_empty() {
            self.draw_console(ui);
            ui.text("waiting for the game…");
            return None;
        }
        let scale = |v: Option<f32>| v.map_or("-".to_owned(), |v| format!("{v:.2}"));
        ui.text(format!("scene: {}", status.scene));
        ui.text(format!(
            "render scale: game asked {}, effective {}",
            scale(status.game_render_scale),
            scale(status.effective_render_scale)
        ));
        draw_render_scale(ui, desired, status.game_render_scale.or(status.effective_render_scale));
        if let Some((_, message)) = status.errors.iter().find(|(key, _)| key == "renderscale") {
            ui.text_colored(RED, message);
        }
        self.draw_extras(ui, desired, status);
        draw_display(ui, desired, status);
        self.draw_console(ui);
        ui.separator();
        let mut save = None;
        if ui.button("Save to ascnet-graphics.ini") {
            save = Some(render(desired));
        }
        ui.same_line();
        if ui.button("Reset all") {
            desired.overrides.clear();
            desired.render_scale = None;
            self.buffers.clear();
            desired.fps = None;
            desired.nofade = false;
            desired.ultrawide = false;
            desired.ui16x9 = false;
            desired.ui_fov = None;
            desired.fight_fov = None;
            desired.ui_scale = None;
            desired.dps_meter = false;
            desired.dps_corner = 0;
            desired.dps_details = false;
            desired.dps_scale = 1.0;
            desired.dps_hide_outside = false;
            desired.dps_compact = true;
        }
        if !self.save_result.is_empty() {
            ui.text_wrapped(&self.save_result);
        }
        ui.separator();
        for setting in catalogue.iter() {
            if !ui.collapsing_header(&setting.name, TreeNodeFlags::empty()) {
                continue;
            }
            let _setting_id = ui.push_id(&setting.name);
            for field in &setting.fields {
                let key = format!("{}.{}", setting.name, field.name);
                let _field_id = ui.push_id(&field.name);
                if self.draw_field(ui, desired, &setting.name, field, &key) {
                    self.buffers.remove(&key);
                }
                if let Some((_, message)) = status.errors.iter().find(|(k, _)| *k == key) {
                    ui.text_colored(RED, message);
                }
            }
        }
        save
    }

    fn draw_dps_controls(&mut self, ui: &Ui, desired: &mut Desired, snapshot: Option<&DpsSnapshot>, reset: &mut u64) {
        if !ui.collapsing_header("DPS meter", TreeNodeFlags::empty()) {
            return;
        }
        ui.checkbox("Enable DPS meter", &mut desired.dps_meter);
        ui.checkbox("Show compact meter", &mut desired.dps_compact);
        let mut corner = usize::from(desired.dps_corner);
        if ui.combo_simple_string("Meter corner", &mut corner, &["Top right", "Top left", "Bottom right", "Bottom left"]) {
            desired.dps_corner = corner as u8;
        }
        ui.checkbox("Hide compact meter outside fights", &mut desired.dps_hide_outside);
        if ui.slider("Compact meter scale", DPS_SCALE_RANGE.0, DPS_SCALE_RANGE.1, &mut desired.dps_scale) {
            desired.dps_scale = desired.dps_scale.clamp(DPS_SCALE_RANGE.0, DPS_SCALE_RANGE.1);
        }
        ui.checkbox("Show damage details", &mut desired.dps_details);
        if ui.button("Reset DPS") {
            *reset = reset.wrapping_add(1);
        }
        ui.text_wrapped("Recorded normal-fight damage, including owned summons. Not effective HP removed; DLC/Babylonia is unsupported.");
        if let Some(snapshot) = snapshot {
            draw_dps_summary(ui, snapshot);
            draw_dps_rows(ui, snapshot, desired.dps_details, 440.0);
        } else if desired.dps_meter {
            ui.text("Waiting for DPS sample…");
        }
        ui.separator();
    }

    fn draw_compact_dps(&self, ui: &Ui) {
        let live = LIVE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        if !live.desired.dps_compact || !live.desired.dps_meter {
            return;
        }
        if live.desired.dps_hide_outside && !live.dps.as_ref().is_some_and(|snapshot| snapshot.status == "active") {
            return;
        }
        let scale = live.desired.dps_scale.clamp(DPS_SCALE_RANGE.0, DPS_SCALE_RANGE.1);
        let display = ui.io().display_size;
        let right = live.desired.dps_corner % 2 == 0;
        let bottom = live.desired.dps_corner >= 2;
        let position = [if right { display[0] - 12.0 } else { 12.0 }, if bottom { display[1] - 12.0 } else { 12.0 }];
        ui.window("Recorded damage##compact_dps")
            .position(position, Condition::Always)
            .position_pivot([if right { 1.0 } else { 0.0 }, if bottom { 1.0 } else { 0.0 }])
            .flags(WindowFlags::NO_INPUTS | WindowFlags::NO_DECORATION | WindowFlags::ALWAYS_AUTO_RESIZE | WindowFlags::NO_SAVED_SETTINGS | WindowFlags::NO_FOCUS_ON_APPEARING | WindowFlags::NO_NAV)
            .build(|| {
                ui.set_window_font_scale(scale);
                if let Some(snapshot) = &live.dps {
                    draw_dps_summary(ui, snapshot);
                    draw_dps_rows(ui, snapshot, live.desired.dps_details, 360.0 * scale);
                } else {
                    ui.text("DPS: waiting for sample…");
                }
            });
    }

    fn poll_console(&mut self) {
        let Some((sequence, result)) = graphics_lua::console_outcome() else {
            return;
        };
        if self.console_seen == Some(sequence) {
            return;
        }
        self.console_seen = Some(sequence);
        if let Some((_, _, outcome)) = self.console_history.iter_mut().find(|(id, _, _)| *id == sequence) {
            *outcome = Some(result);
            self.console_scroll = true;
        }
    }

    fn draw_console(&mut self, ui: &Ui) {
        if let Some(error) = graphics_lua::console_error() {
            ui.text_colored(RED, error);
        }
        if !ui.collapsing_header("Lua console", TreeNodeFlags::empty()) {
            return;
        }
        let submitted = ui.input_text_multiline("##lua_source", &mut self.console_source, [0.0, 120.0])
            .enter_returns_true(true)
            .build();
        let shortcut = submitted && ui.io().key_ctrl && ui.is_key_pressed_no_repeat(Key::Enter);
        let run = ui.button("Run") || shortcut;
        ui.same_line();
        ui.text("Ctrl+Enter to run; newer submissions replace queued code");
        if run && !self.console_source.trim().is_empty() {
            let sequence = graphics_lua::queue_console(&self.console_source);
            for (_, _, outcome) in &mut self.console_history {
                if outcome.is_none() {
                    *outcome = Some(Err("Superseded by a newer submission".to_owned()));
                }
            }
            if self.console_history.len() == 20 {
                self.console_history.pop_front();
            }
            self.console_history.push_back((
                sequence,
                self.console_source.lines().next().unwrap_or("").to_owned(),
                None,
            ));
            self.console_scroll = true;
        }
        ui.child_window("lua_results").size([0.0, 180.0]).build(|| {
            for (sequence, source, outcome) in &self.console_history {
                ui.text_wrapped(format!("#{sequence} > {source}"));
                match outcome {
                    Some(Ok(value)) => ui.text_wrapped(format!("Ok: {value}")),
                    Some(Err(error)) => {
                        let _color = ui.push_style_color(hudhook::imgui::StyleColor::Text, RED);
                        ui.text_wrapped(format!("Err: {error}"));
                    }
                    None => ui.text("Queued…"),
                }
            }
            if self.console_scroll {
                ui.set_scroll_here_y_with_ratio(1.0);
                self.console_scroll = false;
            }
        });
    }

    /// Returns true when the override was removed (the caller drops its text buffer).
    fn draw_field(&mut self, ui: &Ui, desired: &mut Desired, setting: &str, field: &FieldInfo, key: &str) -> bool {
        let index = desired.overrides.iter().position(|o| o.setting == setting && o.field == field.name);
        let current = index.map_or(field.value.as_str(), |i| desired.overrides[i].value.as_str());
        let mut edited = None;
        match &field.kind {
            FieldKind::Bool => {
                let mut on = current == "on";
                if ui.checkbox(&field.name, &mut on) {
                    edited = Some(if on { "on" } else { "off" }.to_owned());
                }
            }
            FieldKind::Int => {
                let mut v = current.parse::<i32>().unwrap_or_default();
                if ui.input_int(&field.name, &mut v).build() {
                    edited = Some(v.to_string());
                }
            }
            FieldKind::Float => {
                let mut v = current.parse::<f32>().unwrap_or_default();
                if ui.input_float(&field.name, &mut v).build() {
                    edited = Some(v.to_string());
                }
            }
            FieldKind::Enum(items) => {
                let names: Vec<&str> = items.iter().map(|(name, _)| name.as_str()).collect();
                let mut selected = names.iter().position(|name| *name == current).unwrap_or(0);
                if ui.combo_simple_string(&field.name, &mut selected, &names) {
                    edited = Some(names[selected].to_owned());
                }
            }
            FieldKind::Ints | FieldKind::Floats => {
                let buffer = self.buffers.entry(key.to_owned()).or_insert_with(|| current.to_owned());
                if ui.input_text(&field.name, buffer).enter_returns_true(true).build() {
                    edited = Some(buffer.trim().to_owned());
                }
            }
            FieldKind::ReadOnly => ui.text(format!("{}: {}", field.name, current)),
        }
        if let Some(value) = edited {
            match index {
                Some(i) => desired.overrides[i].value = value,
                None => desired.overrides.push(Override { setting: setting.to_owned(), field: field.name.clone(), value }),
            }
        }
        let Some(i) = index.or_else(|| desired.overrides.iter().position(|o| o.setting == setting && o.field == field.name)) else {
            return false;
        };
        ui.same_line();
        ui.text_colored(AMBER, format!("game: {}", field.game_value.as_deref().unwrap_or("?")));
        ui.same_line();
        if ui.button("Reset") {
            desired.overrides.remove(i);
            return true;
        }
        false
    }

    /// Frame rate unlock and camera fade rows.
    fn draw_extras(&mut self, ui: &Ui, desired: &mut Desired, status: &Status) {
        let mut on = desired.fps.is_some();
        if ui.checkbox("Unlock FPS", &mut on) {
            if on {
                desired.fps = Some(if self.fps_last > 0 { self.fps_last } else { 240 });
            } else {
                self.fps_last = desired.fps.take().unwrap_or(0);
            }
            self.fps_invalid = false;
        }
        if let Some(fps) = desired.fps {
            let mut value = fps;
            ui.same_line();
            if ui.input_int("target FPS", &mut value).build() {
                self.fps_invalid = value < 1;
                if !self.fps_invalid {
                    desired.fps = Some(value);
                }
            }
        }
        let show = |v: Option<i32>| v.map_or("-".to_owned(), |v| v.to_string());
        ui.text(format!("frame rate: target {}, vsync {}", show(status.target_frame_rate), show(status.vsync_count)));
        if self.fps_invalid {
            ui.text_colored(RED, "target FPS must be at least 1");
        }
        if let Some((_, message)) = status.errors.iter().find(|(key, _)| key == "fps") {
            ui.text_colored(RED, message);
        }
        ui.checkbox("No camera fade", &mut desired.nofade);
        if let Some((_, message)) = status.errors.iter().find(|(key, _)| key == "nofade") {
            ui.text_colored(RED, message);
        }
    }
}

fn draw_dps_summary(ui: &Ui, snapshot: &DpsSnapshot) {
    let state = if snapshot.status == "active" { "In fight" } else { "Last fight" };
    ui.text(format!("{state} | {:.1}s", snapshot.elapsed));
    ui.text(format!("Team {} | {} DPS | {} peak 5s",
        human_number(snapshot.total as f64), human_number(snapshot.dps), human_number(snapshot.peak5)));
    if snapshot.status != "active" && snapshot.status != "retained" {
        ui.text(&snapshot.status);
    }
    if snapshot.dropped_hits > 0 {
        ui.text_colored(AMBER, format!("Dropped hit samples: {} (hit details incomplete)", snapshot.dropped_hits));
    }
}

fn draw_dps_rows(ui: &Ui, snapshot: &DpsSnapshot, details: bool, width: f32) {
    const COLORS: [[f32; 4]; 6] = [
        [0.25, 0.62, 0.90, 1.0], [0.70, 0.38, 0.75, 1.0], [0.22, 0.70, 0.48, 1.0],
        [0.85, 0.53, 0.22, 1.0], [0.78, 0.31, 0.40, 1.0], [0.38, 0.65, 0.68, 1.0],
    ];
    for (index, row) in snapshot.rows.iter().enumerate() {
        if index > 0 {
            if details { ui.new_line(); } else { ui.spacing(); }
        }
        let label = format!("{} | {} | {} DPS | {:.1}%", row.name,
            human_number(row.total as f64), human_number(row.dps), row.share * 100.0);
        {
            // Character-ID order, not damage rank, keeps team colors distinct as damage order changes.
            let color_index = snapshot.rows.iter().filter(|other| other.character_id < row.character_id).count();
            let _color = ui.push_style_color(StyleColor::PlotHistogram, COLORS[color_index % COLORS.len()]);
            ProgressBar::new(row.share.clamp(0.0, 1.0) as f32)
                .size([width, ui.text_line_height_with_spacing() + 4.0])
                .overlay_text(&label)
                .build(ui);
        }
        if !details {
            continue;
        }
        let crit_rate = if row.hits > 0 { row.crits as f64 * 100.0 / row.hits as f64 } else { 0.0 };
        ui.text(format!("Crit {crit_rate:.1}% ({}/{} hits) | Biggest {} | Peak 5s {}",
            row.crits, row.hits, human_number(row.biggest_hit as f64), human_number(row.peak5)));
        // Characters only swapped in for QTE/assists have damage but no controlled time.
        let on_field = if row.on_field_seconds >= 0.5 {
            format!("{} DPS ({:.1}s)", human_number(row.on_field_dps), row.on_field_seconds)
        } else {
            "not controlled".to_owned()
        };
        let mut line = format!("On-field {on_field}");
        if row.cure > 0 { line.push_str(&format!(" | Healing {}", human_number(row.cure as f64))); }
        if row.hurt > 0 { line.push_str(&format!(" | Taken {}", human_number(row.hurt as f64))); }
        ui.text(line);
        if !row.skills.is_empty() {
            ui.text("Skills (share of hit-sampled damage):");
            ui.indent();
            for skill in &row.skills {
                ui.text_wrapped(format!("{} | {} | {:.1}%", skill.name,
                    human_number(skill.damage as f64), skill.share * 100.0));
            }
            ui.unindent();
        }
    }
}

fn human_number(value: f64) -> String {
    let (scaled, suffix) = if value.abs() >= 999_950.0 {
        (value / 1_000_000.0, "M")
    } else if value.abs() >= 999.95 {
        (value / 1_000.0, "k")
    } else {
        (value, "")
    };
    let precision = if scaled.abs() < 10.0 { 2 } else { 1 };
    format!("{scaled:.precision$}{suffix}")
}

#[cfg(test)]
mod dps_display_tests {
    use super::human_number;

    #[test]
    fn human_numbers_keep_one_or_two_decimals_and_promote_rounded_units() {
        for (value, expected) in [
            (0.0, "0.00"), (9.123, "9.12"), (42.24, "42.2"),
            (999.0, "999.0"), (999.96, "1.00k"), (1_234.0, "1.23k"),
            (12_345.0, "12.3k"), (999_960.0, "1.00M"),
            (1_234_567.0, "1.23M"), (12_345_678.0, "12.3M"),
        ] {
            assert_eq!(human_number(value), expected);
        }
    }
}

/// Ultrawide fullscreen toggle and the screen as the game's main thread last read it.
fn draw_display(ui: &Ui, desired: &mut Desired, status: &Status) {
    ui.separator();
    ui.text("Display");
    ui.checkbox("Ultrawide fullscreen", &mut desired.ultrawide);
    let size = status.screen.map_or("-".to_owned(), |(w, h)| format!("{w}x{h}"));
    let mode = status.fullscreen.map_or("-", |full| if full { "fullscreen" } else { "windowed" });
    ui.text(format!("screen: {size} {mode}"));
    ui.checkbox("UI in centred 16:9", &mut desired.ui16x9);
    let mut fov_on = desired.ui_fov.is_some();
    if ui.checkbox("Widen UI-scene FOV (map, team select)", &mut fov_on) {
        desired.ui_fov = fov_on.then_some(1.3);
    }
    if let Some(scale) = desired.ui_fov.as_mut() {
        ui.slider("UI-scene FOV factor", UI_FOV_RANGE.0, UI_FOV_RANGE.1, scale);
    }
    let mut fight_fov_on = desired.fight_fov.is_some();
    if ui.checkbox("Override fight-camera FOV", &mut fight_fov_on) {
        desired.fight_fov = fight_fov_on.then_some(1.0);
    }
    if let Some(scale) = desired.fight_fov.as_mut() {
        ui.slider("Fight-camera FOV factor", FIGHT_FOV_RANGE.0, FIGHT_FOV_RANGE.1, scale);
    }
    let mut ui_scale_on = desired.ui_scale.is_some();
    if ui.checkbox("Override UI scale", &mut ui_scale_on) {
        desired.ui_scale = ui_scale_on.then_some(1.0);
    }
    if let Some(scale) = desired.ui_scale.as_mut() {
        ui.slider("UI scale factor", UI_SCALE_RANGE.0, UI_SCALE_RANGE.1, scale);
    }
    for key in ["ultrawide", "ui16x9", "uifov", "fightfov", "uiscale", "console"] {
        if let Some((_, message)) = status.errors.iter().find(|(k, _)| k == key) {
            ui.text_colored(RED, message);
        }
    }
}

fn draw_render_scale(ui: &Ui, desired: &mut Desired, fallback: Option<f32>) {
    let mut on = desired.render_scale.is_some();
    if ui.checkbox("Override render scale", &mut on) {
        desired.render_scale = on.then(|| fallback.unwrap_or(1.0));
    }
    let Some(scale) = desired.render_scale.as_mut() else {
        return;
    };
    ui.slider("Render scale", RENDER_SCALE_RANGE.0, RENDER_SCALE_RANGE.1, scale);
    for preset in [1.0_f32, 1.5, 2.0, 2.5] {
        ui.same_line();
        if ui.button(format!("{preset:.1}")) {
            *scale = preset;
        }
    }
}

pub(super) fn save_ini(text: &str) -> String {
    let path = std::env::current_exe().ok().and_then(|exe| exe.parent().map(|dir| dir.join(CONFIG_FILE)));
    let Some(path) = path else {
        let reason = "cannot locate PGR.exe's folder";
        il2cpp::failed("overlay save", reason, "the settings stay live but are not saved");
        return format!("save failed: {reason}");
    };
    match std::fs::write(&path, text) {
        Ok(()) => {
            il2cpp::ok("overlay save", &path.display().to_string());
            format!("saved {}", path.display())
        }
        Err(error) => {
            il2cpp::failed("overlay save", &format!("{}: {error}", path.display()), "the settings stay live but are not saved");
            format!("save failed: {error}")
        }
    }
}

/// The game's top-level Unity window in this process.
fn game_window() -> Option<windows::Win32::Foundation::HWND> {
    use windows::Win32::Foundation::{BOOL, HWND, LPARAM};
    use windows::Win32::UI::WindowsAndMessaging::{EnumWindows, GetClassNameW, GetWindowThreadProcessId};
    unsafe extern "system" fn visit(hwnd: HWND, found: LPARAM) -> BOOL {
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, Some(&mut pid));
        let mut class = [0u16; 32];
        let len = GetClassNameW(hwnd, &mut class).max(0) as usize;
        if pid == std::process::id() && String::from_utf16_lossy(&class[..len]) == "UnityWndClass" {
            *(found.0 as *mut HWND) = hwnd;
            return BOOL(0);
        }
        BOOL(1)
    }
    let mut found = HWND::default();
    // EnumWindows reports an error when the callback stops early; `found` is what matters.
    let _ = unsafe { EnumWindows(Some(visit), LPARAM(&mut found as *mut HWND as isize)) };
    (found.0 != 0).then_some(found)
}

/// Posts a key-up for every key that is not physically held, with the scan code and extended bit Unity uses to tell
/// left and right modifiers apart. Key-ups for keys the game never saw go down are ignored by Unity.
fn release_keys() {
    use windows::Win32::Foundation::{LPARAM, WPARAM};
    use windows::Win32::UI::Input::KeyboardAndMouse::{
        GetAsyncKeyState, MapVirtualKeyW, MAPVK_VK_TO_VSC_EX, VK_CONTROL, VK_LCONTROL, VK_LMENU, VK_LSHIFT, VK_MENU,
        VK_RCONTROL, VK_RMENU, VK_RSHIFT, VK_SHIFT,
    };
    use windows::Win32::UI::WindowsAndMessaging::{PostMessageW, WM_KEYUP, WM_SYSKEYUP};
    let Some(hwnd) = game_window() else {
        il2cpp::failed("overlay key release", "the game window was not found", "keys held while the overlay opened may stay stuck until pressed again");
        return;
    };
    let sided = [(VK_LSHIFT, VK_SHIFT), (VK_RSHIFT, VK_SHIFT), (VK_LCONTROL, VK_CONTROL), (VK_RCONTROL, VK_CONTROL), (VK_LMENU, VK_MENU), (VK_RMENU, VK_MENU)];
    let mut keys: Vec<(u16, u16)> = sided.iter().map(|(side, generic)| (side.0, generic.0)).collect();
    keys.extend((0x08u16..=0xFE).filter(|&vk| ![VK_SHIFT.0, VK_CONTROL.0, VK_MENU.0].contains(&vk) && !sided.iter().any(|(s, _)| s.0 == vk)).map(|vk| (vk, vk)));
    let mut released = 0;
    for (physical, sent) in keys {
        if unsafe { GetAsyncKeyState(i32::from(physical)) } as u16 & 0x8000 != 0 {
            continue;
        }
        let scan = unsafe { MapVirtualKeyW(u32::from(physical), MAPVK_VK_TO_VSC_EX) };
        if scan == 0 {
            continue;
        }
        let extended = u32::from(scan & 0xFF00 == 0xE000);
        // repeat 1, scan code, extended, previous state down, transition up
        let lparam = 1 | ((scan & 0xFF) << 16) | (extended << 24) | (1 << 30) | (1 << 31);
        let message = if sent == VK_MENU.0 { WM_SYSKEYUP } else { WM_KEYUP };
        if unsafe { PostMessageW(hwnd, message, WPARAM(usize::from(sent)), LPARAM(lparam as isize)) }.is_ok() {
            released += 1;
        }
    }
    il2cpp::ok("overlay key release", &format!("posted {released} key-up(s) after hiding"));
}
