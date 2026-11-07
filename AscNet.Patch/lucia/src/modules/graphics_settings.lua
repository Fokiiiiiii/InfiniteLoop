-- Loaded by the main-thread Lua bridge; Rust owns state and consumes pending edits.
-- Layout verified by a runtime hierarchy probe of UiSet (PC, 4.8): tabs are XUiButtons under
-- SafeAreaContentPane/PanelMainSet/PanelTabToggles (XUiButtonGroup); sub-panel containers live
-- under SafeAreaContentPane/SubPanels; graphics rows are SView/Viewport/PanelContent children of
-- the UiSetPanelGraphicsSetPC prefab, sliders the Music row of UiSetPanelVoiceSet.
local box = rawget(_G, "__lucia_settings")
local XLuaUiManager = rawget(_G, "XLuaUiManager")
if not box or not XLuaUiManager or not XLuaUiManager.New then return "pending" end
if box.installed or box.waiting then return "OK lua AscNet Settings: installed" end
-- Never require XUi/XUiSet/XUiSet ourselves at start-up: its top level registers the class
-- (line 15) and then reads XDataCenter.FubenManager (line 33), which is not ready yet. The
-- throw leaves an empty registered class that the game then uses instead of the real one.
local MODULE = "XUi/XUiSet/XUiSet"
local class, originalStart, originalShow, originalDestroy

local Unity = CS.UnityEngine
local Object = Unity.Object
local instances = setmetatable({}, {__mode = "k"})
local controls = {
    {"renderscale", "Render scale", 1}, {"fps", "FPS limit", 60},
    {"nofade", "No camera fade"}, {"ultrawide", "Ultrawide fullscreen"}, {"ui16x9", "UI in centred 16:9"},
    {"uifov", "Wider UI-scene FOV", 1.3}, {"fightfov", "Fight camera FOV", 1}, {"uiscale", "UI scale", 1},
    {"dpsmeter", "DPS meter"}, {"dpscompact", "DPS meter window"},
    {"dpscorner", "Meter corner", 0, true}, {"dpsscale", "Meter size", 1},
    {"dpsdetails", "Show skills and details"}, {"dpshideoutside", "Hide meter outside fights"},
    {"overlay", "Overlay (Ctrl+Shift+G)"},
}
local function line(reason) return "FAILED lua AscNet Settings: " .. tostring(reason):gsub("[\r\n]+", " ") end
local function report(message)
    box.logs = box.logs or {}
    if #box.logs == 16 then table.remove(box.logs, 1) end
    box.logs[#box.logs + 1] = message:sub(1, 400)
end
local function destroy(go) if go then go:SetActive(false); Object.Destroy(go) end end
-- XClass tables (XLuaUiManager, UiSet) keep methods in a `vtbl` captured by their metatable's __index.
-- With remote-config Debug the class __newindex logs every reassignment as an error (XClass.lua:85),
-- so wrappers are written straight into that table, the way the game's XHotReload reaches upvalues.
local function setMethod(target, key, value)
    local meta = debug.getmetatable(target)
    local index = meta and meta.__index
    if type(index) == "function" then
        for i = 1, 16 do
            local name, upvalue = debug.getupvalue(index, i)
            if not name then break end
            if name == "vtbl" and type(upvalue) == "table" then
                rawset(upvalue, key, value)
                return
            end
        end
    end
    target[key] = value
end
local function child(transform, path) return assert(transform:Find(path), "missing " .. path) end
local UI = Unity.UI
-- Typed lookup searching the node and its children. GetComponent("Slider") matches the short
-- name and is ambiguous with Spine.Slider, returning nil (the game warns: XUiHelper.lua:182).
local function component(transform, type_)
    local kind = typeof(type_)
    local found = transform:GetComponentInChildren(kind, true)
    if not found then error("no " .. kind.Name .. " under " .. transform.name, 2) end
    return found
end

local function rollback(ui, reason)
    local panel = instances[ui]
    if not panel then return end
    instances[ui] = nil
    if ui.SubPanels and panel.index then ui.SubPanels[panel.index] = nil end
    -- An appended button cannot leave the stock group without renumbering it; hiding it keeps
    -- the stock tabs and their indices exactly as the game built them.
    pcall(function() panel.tab:SetActive(false) end)
    pcall(destroy, panel.container)
    if panel.index and ui.SelectedIndex == panel.index then
        pcall(function() ui.PanelTabToggles:SelectIndex(panel.previous, false) end)
        pcall(originalShow, ui, panel.previous)
    end
    report(line(reason))
end

local function guarded(ui, fn)
    return function(...)
        local ok, err = pcall(fn, ...)
        if not ok then rollback(ui, err) end
    end
end

local corners = {"topright", "topleft", "bottomright", "bottomleft"}
local cornerNames = {"Top right", "Top left", "Bottom right", "Bottom left"}
local function format(key, value)
    if key == "dpscorner" then return cornerNames[math.floor(value + 0.5) + 1] end
    return string.format(key == "fps" and "%.0f" or "%.2f", value)
end
-- `fps` crosses as text to the ini parser, which accepts only plain whole numbers ("240", not "240.0").
local function wire(key, value)
    if key == "dpscorner" then return corners[math.floor(value + 0.5) + 1] end
    if key ~= "fps" then return value end
    local rounded = math.floor(value + 0.5)
    return math.tointeger and math.tointeger(rounded) or rounded
end
-- Built on first open so a layout mismatch only costs the AscNet tab, never the stock screen.
local function build(ui, panel)
    local container = Object.Instantiate(ui.PanelOther.gameObject, ui.PanelOther.transform.parent, false)
    container.name = "PanelAscNet"
    for i = container.transform.childCount - 1, 0, -1 do Object.Destroy(container.transform:GetChild(i).gameObject) end
    panel.container = container
    local graphics = container.transform:LoadPrefab(XUiConfigs.GetComponentUrl("UiSetPanelGraphicsSetPC")).transform
    local scratch = Object.Instantiate(ui.PanelOther.gameObject, container.transform, false)
    for i = scratch.transform.childCount - 1, 0, -1 do Object.Destroy(scratch.transform:GetChild(i).gameObject) end
    scratch:SetActive(false)
    local voice = scratch.transform:LoadPrefab(XUiConfigs.GetComponentUrl("UiSetPanelVoiceSet")).transform
    local content = child(graphics, "SView/Viewport/PanelContent")
    local toggleTemplate = child(content, "PanelLiangge3")
    local sliderTemplate = child(voice, "SView/Viewport/PanelContent/Music")
    report("OK lua AscNet Settings: templates found")
    component(child(graphics, "Biaoti/TextTitle"), UI.Text).text = "AscNet"
    -- The scroll view is anchored below the quality preset bar; take over its space so the
    -- options start right under the title. Edges are in the shared parent's local space.
    local function top(rect) return rect.localPosition.y + (1 - rect.pivot.y) * rect.rect.height end
    local quality = child(graphics, "MainQuality")
    local scroll = child(graphics, "SView")
    local lift = top(quality) - top(scroll)
    if lift > 0 then scroll.offsetMax = Unity.Vector2(scroll.offsetMax.x, scroll.offsetMax.y + lift) end
    for _, name in ipairs({"MainQuality", "GameObject", "Jiemian"}) do
        local node = graphics:Find(name)
        if node then node.gameObject:SetActive(false) end
    end
    for i = 0, content.childCount - 1 do content:GetChild(i).gameObject:SetActive(false) end
    for _, spec in ipairs(controls) do
        local key, title, default, alwaysOn = spec[1], spec[2], spec[3], spec[4]
        local row = Object.Instantiate(toggleTemplate.gameObject, content, false)
        row.name = "AscNet_" .. key
        local entry = {key = key, title = title, default = default, alwaysOn = alwaysOn,
            toggle = component(child(row.transform, "TogFxaa"), UI.Toggle),
            label = component(child(row.transform, "TxtFxaa"), UI.Text)}
        local image = row.transform:Find("FXAA")
        if image then image.gameObject:SetActive(false) end
        entry.toggle.onValueChanged:RemoveAllListeners()
        entry.toggle.group = nil
        entry.toggle.onValueChanged:AddListener(guarded(ui, function(on)
            if panel.updating then return end
            if key == "dpsscale" then entry.custom = on end
            box.pending[key] = on and (entry.slider and wire(key, entry.value) or true) or false
            panel.Refresh()
        end))
        row:SetActive(not alwaysOn)
        if default then
            local range = assert(box.state.ranges[key], "missing range: " .. key)
            entry.min, entry.max = range[1], range[2]
            if key == "fps" then entry.min, entry.max = 30, 360 end
            local sliderRow = Object.Instantiate(sliderTemplate.gameObject, content, false)
            sliderRow.name = "AscNet_" .. key .. "_value"
            for _, name in ipairs({"ImgMusicON", "ImgMusicOFF"}) do
                local node = sliderRow.transform:Find(name)
                if node then node.gameObject:SetActive(false) end
            end
            entry.valueLabel = component(child(sliderRow.transform, "TxtMusic"), UI.Text)
            entry.slider = component(sliderRow.transform, UI.Slider)
            entry.slider.onValueChanged:RemoveAllListeners()
            entry.slider.minValue, entry.slider.maxValue = entry.min, entry.max
            entry.slider.wholeNumbers = key == "fps" or key == "dpscorner"
            entry.slider.onValueChanged:AddListener(guarded(ui, function(value)
                if panel.updating then return end
                entry.value = value
                entry.valueLabel.text = (alwaysOn and title .. ": " or "") .. format(key, value)
                if alwaysOn or entry.toggle.isOn then box.pending[key] = wire(key, value) end
            end))
            sliderRow:SetActive(true)
        end
        panel.rows[#panel.rows + 1] = entry
    end
    panel.Refresh()
    report("OK lua AscNet Settings: panel built (" .. #panel.rows .. " options)")
end

local function newPanel(ui)
    local panel = {previous = ui.SelectedIndex or 2, rows = {}, updating = false}
    panel.Refresh = function()
        if not panel.container then return end
        panel.updating = true
        for _, entry in ipairs(panel.rows) do
            local value = box.pending[entry.key]
            if value == nil then value = box.state.desired[entry.key] end
            if entry.key == "dpscorner" and type(value) == "string" then
                for index, corner in ipairs(corners) do
                    if value == corner then value = index - 1; break end
                end
            end
            local on = entry.alwaysOn or (value ~= false and value ~= nil and (entry.key ~= "dpsscale" or value ~= 1))
            if entry.key == "dpsscale" and entry.custom ~= nil then on = entry.custom end
            entry.toggle.isOn = on
            if entry.slider then
                if type(value) == "number" then entry.value = value end
                entry.value = math.max(entry.min, math.min(entry.max, entry.value or entry.default))
                entry.slider.value = entry.value
                entry.slider.interactable = on
                entry.valueLabel.text = (entry.alwaysOn and entry.title .. ": " or "") .. format(entry.key, entry.value) .. (on and "" or (entry.key == "dpsscale" and " (default)" or " (game default)"))
            end
            entry.label.text = entry.title
        end
        panel.updating = false
    end
    panel.Open = function()
        local ok, err = pcall(function()
            if not panel.container then build(ui, panel) end
            panel.container:SetActive(true)
            panel.Refresh()
        end)
        if not ok then rollback(ui, err) end
    end
    panel.Close = function() if panel.container then panel.container:SetActive(false) end end
    panel.CheckDataIsChange = function() return false end
    panel.SaveChange = function() end
    panel.CancelChange = function() end
    panel.ResetToDefault = function() end
    return panel
end

local function addTab(ui)
    if instances[ui] then return end
    local panel = newPanel(ui)
    instances[ui] = panel
    local source = ui.BtnOther
    panel.tab = Object.Instantiate(source.gameObject, source.transform.parent, false)
    panel.tab.name = "BtnAscNet"
    panel.tab.transform:SetSiblingIndex(ui.BtnAccount.transform:GetSiblingIndex() + 1)
    local button = assert(panel.tab:GetComponent("XUiButton"), "copied tab has no XUiButton")
    button:SetNameByGroup(0, "AscNet")
    -- Init(LuaTable) must not be called again: it re-shows tabs the game hid for this screen
    -- and re-selects index 1. AddButton appends and reuses the stock SwitchSubPanel callback.
    local group = ui.PanelTabToggles
    group:AddButton(button)
    for i = 1, group.TabBtnList.Count + 1 do
        if group:GetButtonByIndex(i) == button then panel.index = i end
    end
    assert(panel.index, "appended tab index not found")
    assert(ui.SubPanels[panel.index] == nil, "tab index " .. panel.index .. " is a stock panel")
    ui.SubPanels[panel.index] = panel
    -- XUiButton.CallBack (Action<int>) is set by the group's Init, not by AddButton, and a copied
    -- button has none. SelectIndex re-enters the selected button's CallBack, so guard re-entry
    -- and switch through the stock SwitchSubPanel (save prompt, Open/Close) ourselves.
    local index = panel.index
    button.CallBack = function()
        if panel.selecting then return end
        panel.selecting = true
        local ok, err = pcall(function()
            group:SelectIndex(index, false)
            ui:SwitchSubPanel(index)
        end)
        panel.selecting = false
        if not ok then rollback(ui, err) end
    end
    panel.tab:SetActive(true)
    report("OK lua AscNet Settings: tab added (index " .. panel.index .. ")")
end

local function install(target)
    assert(type(target) == "table" and type(target.OnStart) == "function" and type(target.ShowSubPanel) == "function",
        "UiSet class is incomplete")
    class = target
    originalStart, originalShow, originalDestroy = class.OnStart, class.ShowSubPanel, class.OnDestroy
    local ok, err = pcall(function()
        setMethod(class, "OnStart", function(ui, ...)
            originalStart(ui, ...)
            local success, reason = pcall(addTab, ui)
            if not success then rollback(ui, reason) end
        end)
        setMethod(class, "ShowSubPanel", function(ui, index, ...)
            originalShow(ui, index, ...)
            local panel = instances[ui]
            if panel and index == panel.index then
                ui.BtnSave.gameObject:SetActive(false)
                ui.BtnDefault.gameObject:SetActive(false)
            end
        end)
        setMethod(class, "OnDestroy", function(ui, ...)
            instances[ui] = nil
            if originalDestroy then return originalDestroy(ui, ...) end
        end)
        box.refresh = function()
            for ui, panel in pairs(instances) do
                if panel.index and ui.SelectedIndex == panel.index then
                    local success, reason = pcall(panel.Refresh)
                    if not success then rollback(ui, reason) end
                end
            end
        end
        box.installed = class
    end)
    if not ok then
        setMethod(class, "OnStart", originalStart)
        setMethod(class, "ShowSubPanel", originalShow)
        setMethod(class, "OnDestroy", originalDestroy)
        error(err, 0)
    end
    report("OK lua AscNet Settings: hooked the Settings screen")
end

local package_ = rawget(_G, "package")
if package_ and package_.loaded[MODULE] then
    local ok, err = pcall(install, XLuaUiManager.GetUiClass("UiSet"))
    return ok and "OK lua AscNet Settings: installed" or line(err)
end
-- Load the module only when the game is opening Settings (fully initialised by then).
local function ensure(load)
    if box.installed or box.failed then return end
    if not load and not package_.loaded[MODULE] then return end
    local ok, err = pcall(function()
        if load then require(MODULE) end
        install(XLuaUiManager.GetUiClass("UiSet"))
    end)
    if not ok then
        box.failed = true
        report(line(err))
    end
end
-- A wrapped XLuaUiManager.New was never called for UiSet in-game (C# likely holds its own
-- reference); game Lua opens Settings with XLuaUiManager.Open("UiSet", ...), looked up per call.
local originalOpen = XLuaUiManager.Open
setMethod(XLuaUiManager, "Open", function(uiName, ...)
    if uiName == "UiSet" then ensure(true) end
    return originalOpen(uiName, ...)
end)
-- Other entry points (PopThenOpen, ...) load the module themselves; the per-tick refresh then
-- installs it, so the tab appears from the next time Settings opens.
box.refresh = function() ensure(false) end
box.waiting = true
return "OK lua AscNet Settings: waiting for the Settings screen"
