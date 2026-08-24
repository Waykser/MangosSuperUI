-- MSUI_BotBar :: UI.lua   (v0.1)
--
-- A standalone movable panel: bot tabs down the left, the selected bot's
-- spellbook in the middle, and a 12-slot bar along the bottom.
--
-- Interaction is drag-and-drop within the addon rather than the real cursor:
-- vanilla's PickupSpell/CursorHasSpell operate on the PLAYER's spellbook by
-- index, and these spells are not in it. So a click on a spellbook row arms an
-- internal "carried" spell and the next click on a slot drops it there.
--
-- No secure-frame restrictions exist in 1.12, so a plain OnClick may send a
-- chat command in combat. That is what makes the bar usable at all.

local B = MSUI_BotBar

local PANEL_W = 420
local ROW_H = 18
local ROWS = 12
local SLOT_SIZE = 30
local SLOT_GAP = 2
local TAB_H = 20

-- Vertical layout, all measured in the direction the anchor runs.
--
-- The panel height is COMPUTED, never fixed: it has to track both the row count
-- and whether the spellbook is collapsed. Everything above the bar anchors to the
-- top, the bar anchors to the bottom, so getting the height right is the whole of
-- the layout work.
local HEADER_H   = 76     -- top edge down to below the control row
local LIST_TOP   = -74    -- spellbook / tab column start
local LIST_H     = ROWS * ROW_H + 8
local LIST_SPAN  = -LIST_TOP + LIST_H   -- top edge down to the bottom of the list
local BAR_BOTTOM = 22     -- bottom edge of the lowest slot row
local BAR_CLEAR  = 10     -- breathing room between the bar and whatever is above it

B.carried = nil          -- spellId armed for the next slot click
B.flash = {}             -- [spellId] = { expires = t, kind = "ok"|"fail"|"sent" }

-- ============================================================
-- Frame construction
-- ============================================================

local f = CreateFrame("Frame", "MSUI_BotBarFrame", UIParent)
f:SetWidth(PANEL_W)
f:SetHeight(400)          -- placeholder; B.Relayout computes the real height
f:SetPoint("CENTER", UIParent, "CENTER", 0, 0)
f:SetMovable(true)
f:EnableMouse(true)
f:RegisterForDrag("LeftButton")
f:SetScript("OnDragStart", function() this:StartMoving() end)
f:SetScript("OnDragStop", function() this:StopMovingOrSizing() end)
f:SetBackdrop({
    bgFile = "Interface\\DialogFrame\\UI-DialogBox-Background",
    edgeFile = "Interface\\DialogFrame\\UI-DialogBox-Border",
    tile = true, tileSize = 32, edgeSize = 32,
    insets = { left = 11, right = 12, top = 12, bottom = 11 }
})
f:Hide()

local title = f:CreateFontString(nil, "OVERLAY", "GameFontNormal")
title:SetPoint("TOP", f, "TOP", 0, -16)
title:SetText("Bot Bar")

local subtitle = f:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
subtitle:SetPoint("TOP", title, "BOTTOM", 0, -4)
subtitle:SetText("")
f.subtitle = subtitle

local close = CreateFrame("Button", "MSUI_BotBarClose", f, "UIPanelCloseButton")
close:SetPoint("TOPRIGHT", f, "TOPRIGHT", -8, -8)

local refresh = CreateFrame("Button", "MSUI_BotBarRefresh", f, "UIPanelButtonTemplate")
refresh:SetWidth(70)
refresh:SetHeight(20)
refresh:SetPoint("TOPLEFT", f, "TOPLEFT", 16, -14)
refresh:SetText("Refresh")
refresh:SetScript("OnClick", function()
    B.Refresh()
    B.Print("Refreshing spellbook...")
end)

local stopBtn = CreateFrame("Button", "MSUI_BotBarStop", f, "UIPanelButtonTemplate")
stopBtn:SetWidth(50)
stopBtn:SetHeight(20)
stopBtn:SetPoint("LEFT", refresh, "RIGHT", 4, 0)
stopBtn:SetText("Stop")
stopBtn:SetScript("OnClick", function() B.Stop(B.selected) end)

-- ---------- Control row ----------
-- Sits on its own line under the title so it cannot collide with the centred
-- heading, and stays visible when the spellbook is collapsed.

local collapseBtn = CreateFrame("Button", "MSUI_BotBarCollapse", f, "UIPanelButtonTemplate")
collapseBtn:SetWidth(86)
collapseBtn:SetHeight(20)
collapseBtn:SetPoint("TOPLEFT", f, "TOPLEFT", 16, -48)
collapseBtn:SetText("Hide Book")
collapseBtn:SetScript("OnClick", function() B.ToggleCollapsed() end)
f.collapseBtn = collapseBtn

-- Bot selection has to survive the tab column being hidden, so the cycler is
-- the collapsed-mode way to change bots. It is useful expanded too, so it is
-- always shown rather than swapped in and out.
local prevBot = CreateFrame("Button", "MSUI_BotBarPrev", f, "UIPanelButtonTemplate")
prevBot:SetWidth(24)
prevBot:SetHeight(20)
prevBot:SetPoint("LEFT", collapseBtn, "RIGHT", 6, 0)
prevBot:SetText("<")
prevBot:SetScript("OnClick", function() B.CycleBot(-1) end)

local nextBot = CreateFrame("Button", "MSUI_BotBarNext", f, "UIPanelButtonTemplate")
nextBot:SetWidth(24)
nextBot:SetHeight(20)
nextBot:SetPoint("LEFT", prevBot, "RIGHT", 2, 0)
nextBot:SetText(">")
nextBot:SetScript("OnClick", function() B.CycleBot(1) end)

local rowMinus = CreateFrame("Button", "MSUI_BotBarRowMinus", f, "UIPanelButtonTemplate")
rowMinus:SetWidth(24)
rowMinus:SetHeight(20)
rowMinus:SetPoint("LEFT", nextBot, "RIGHT", 12, 0)
rowMinus:SetText("-")
rowMinus:SetScript("OnClick", function() B.AddRows(-1) end)

local rowsLabel = f:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
rowsLabel:SetWidth(46)
rowsLabel:SetHeight(20)
rowsLabel:SetJustifyH("CENTER")
rowsLabel:SetPoint("LEFT", rowMinus, "RIGHT", 2, 0)
f.rowsLabel = rowsLabel

local rowPlus = CreateFrame("Button", "MSUI_BotBarRowPlus", f, "UIPanelButtonTemplate")
rowPlus:SetWidth(24)
rowPlus:SetHeight(20)
rowPlus:SetPoint("LEFT", rowsLabel, "RIGHT", 2, 0)
rowPlus:SetText("+")
rowPlus:SetScript("OnClick", function() B.AddRows(1) end)
f.rowMinus = rowMinus
f.rowPlus = rowPlus

-- ---------- Bot tabs (left column) ----------

f.tabs = {}
local i
for i = 1, 5 do
    local tab = CreateFrame("Button", nil, f)
    tab:SetWidth(96)
    tab:SetHeight(TAB_H)
    tab:SetPoint("TOPLEFT", f, "TOPLEFT", 16, (LIST_TOP - 2) - (i - 1) * (TAB_H + 2))

    tab.bg = tab:CreateTexture(nil, "BACKGROUND")
    tab.bg:SetAllPoints(tab)
    tab.bg:SetTexture(0.15, 0.15, 0.15, 0.8)

    tab.label = tab:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    tab.label:SetPoint("LEFT", tab, "LEFT", 4, 0)
    tab.label:SetJustifyH("LEFT")

    tab:SetScript("OnClick", function()
        if this.botName then
            B.selected = this.botName
            B.carried = nil
            if B.bots[this.botName] and B.bots[this.botName].state == "unknown" then
                B.Enqueue(this.botName)
            end
            B.RefreshUI()
        end
    end)

    f.tabs[i] = tab
end

-- ---------- Spellbook list ----------

local listBg = CreateFrame("Frame", nil, f)
listBg:SetWidth(258)
listBg:SetHeight(LIST_H)
listBg:SetPoint("TOPLEFT", f, "TOPLEFT", 120, LIST_TOP)
listBg:SetBackdrop({
    bgFile = "Interface\\Tooltips\\UI-Tooltip-Background",
    edgeFile = "Interface\\Tooltips\\UI-Tooltip-Border",
    tile = true, tileSize = 16, edgeSize = 12,
    insets = { left = 3, right = 3, top = 3, bottom = 3 }
})
listBg:SetBackdropColor(0, 0, 0, 0.7)

local scroll = CreateFrame("ScrollFrame", "MSUI_BotBarScroll", listBg, "FauxScrollFrameTemplate")
scroll:SetWidth(232)
scroll:SetHeight(ROWS * ROW_H)
scroll:SetPoint("TOPLEFT", listBg, "TOPLEFT", 6, -4)
scroll:SetScript("OnVerticalScroll", function()
    FauxScrollFrame_OnVerticalScroll(ROW_H, function() B.RefreshUI() end)
end)

f.rows = {}
for i = 1, ROWS do
    local row = CreateFrame("Button", nil, listBg)
    row:SetWidth(230)
    row:SetHeight(ROW_H)
    row:SetPoint("TOPLEFT", listBg, "TOPLEFT", 6, -4 - (i - 1) * ROW_H)

    row.icon = row:CreateTexture(nil, "ARTWORK")
    row.icon:SetWidth(16)
    row.icon:SetHeight(16)
    row.icon:SetPoint("LEFT", row, "LEFT", 1, 0)

    row.label = row:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    row.label:SetPoint("LEFT", row.icon, "RIGHT", 4, 0)
    row.label:SetWidth(200)
    row.label:SetJustifyH("LEFT")

    row.hl = row:CreateTexture(nil, "HIGHLIGHT")
    row.hl:SetAllPoints(row)
    row.hl:SetTexture("Interface\\QuestFrame\\UI-QuestTitleHighlight")
    row.hl:SetBlendMode("ADD")

    row:SetScript("OnClick", function()
        if not this.spellId then return end
        -- Shift-click casts straight from the book, for a spell you only ever
        -- need once and would not want to spend a slot on.
        if IsShiftKeyDown() then
            B.Cast(B.selected, this.spellId, "target")
        else
            B.carried = this.spellId
            B.RefreshUI()
        end
    end)

    row:SetScript("OnEnter", function()
        if not this.spellId then return end
        local n, r, _, g = B.SpellInfo(this.spellId)
        GameTooltip:SetOwner(this, "ANCHOR_RIGHT")
        GameTooltip:AddLine(n)
        if r then GameTooltip:AddLine(r, 0.7, 0.7, 0.7) end
        GameTooltip:AddLine("Spell ID " .. this.spellId, 0.5, 0.5, 0.5)
        if g then
            GameTooltip:AddLine("Ground targeted", 1, 0.8, 0.2)
            GameTooltip:AddLine("Click a slot, then use the modifiers to place it.", 0.6, 0.6, 0.6)
        end
        GameTooltip:AddLine("Click to pick up, shift-click to cast now.", 0.4, 0.8, 0.4)
        GameTooltip:Show()
    end)
    row:SetScript("OnLeave", function() GameTooltip:Hide() end)

    f.rows[i] = row
end

-- ---------- The bar ----------

-- Every slot for MAX_ROWS is built up front and hidden above the current row
-- count. Creating frames on demand would work too, but this way a slot keeps its
-- contents and its scripts when a row is removed and added back.
f.slots = {}
for i = 1, B.MAX_SLOTS do
    local slot = CreateFrame("Button", "MSUI_BotBarSlot" .. i, f)
    slot:SetWidth(SLOT_SIZE)
    slot:SetHeight(SLOT_SIZE)

    -- Row 1 is the BOTTOM row, so adding a row grows the bar upward and the
    -- existing slots never move under the player's cursor.
    local row = math.floor((i - 1) / B.SLOTS_PER_ROW) + 1
    local col = math.mod(i - 1, B.SLOTS_PER_ROW) + 1
    slot:SetPoint("BOTTOMLEFT", f, "BOTTOMLEFT",
        16 + (col - 1) * (SLOT_SIZE + SLOT_GAP),
        BAR_BOTTOM + (row - 1) * (SLOT_SIZE + SLOT_GAP))
    slot.index = i

    slot.bg = slot:CreateTexture(nil, "BACKGROUND")
    slot.bg:SetAllPoints(slot)
    slot.bg:SetTexture("Interface\\Buttons\\UI-EmptySlot")
    slot.bg:SetTexCoord(0.2, 0.8, 0.2, 0.8)

    slot.icon = slot:CreateTexture(nil, "ARTWORK")
    slot.icon:SetAllPoints(slot)
    slot.icon:Hide()

    -- Tint overlay: the cast outcome feedback. A button that silently does
    -- nothing is indistinguishable from a broken one, and an ordered cast can
    -- legitimately take a few seconds to land, so "sent" and "fired" are shown
    -- as separate states.
    slot.tint = slot:CreateTexture(nil, "OVERLAY")
    slot.tint:SetAllPoints(slot)
    slot.tint:SetTexture(1, 1, 1, 0.35)
    slot.tint:Hide()

    slot.hl = slot:CreateTexture(nil, "HIGHLIGHT")
    slot.hl:SetAllPoints(slot)
    slot.hl:SetTexture("Interface\\Buttons\\ButtonHilight-Square")
    slot.hl:SetBlendMode("ADD")

    slot:RegisterForClicks("LeftButtonUp", "RightButtonUp")
    slot:SetScript("OnClick", function()
        local slots = B.Slots(B.selected)

        if arg1 == "RightButton" then
            slots[this.index] = nil
            B.RefreshUI()
            return
        end

        -- Carrying a spell from the book: drop it here instead of casting.
        if B.carried then
            slots[this.index] = B.carried
            B.carried = nil
            B.RefreshUI()
            return
        end

        local spellId = slots[this.index]
        if spellId then
            B.Cast(B.selected, spellId)
        end
    end)

    slot:SetScript("OnEnter", function()
        local spellId = B.Slots(B.selected)[this.index]
        GameTooltip:SetOwner(this, "ANCHOR_RIGHT")
        if spellId then
            local n, r, _, g = B.SpellInfo(spellId)
            GameTooltip:AddLine(n)
            if r then GameTooltip:AddLine(r, 0.7, 0.7, 0.7) end
            GameTooltip:AddLine(" ")
            if g then
                GameTooltip:AddLine("Click        at your target", 0.4, 0.8, 0.4)
                GameTooltip:AddLine("Shift-click  at your feet", 0.4, 0.8, 0.4)
                GameTooltip:AddLine("Ctrl-click   at the biggest pack", 0.4, 0.8, 0.4)
                GameTooltip:AddLine("Alt-click    at the bot's target", 0.4, 0.8, 0.4)
            else
                GameTooltip:AddLine("Click to cast on your target", 0.4, 0.8, 0.4)
                GameTooltip:AddLine("Shift-click to cast on you", 0.4, 0.8, 0.4)
            end
            GameTooltip:AddLine("Right-click to clear the slot", 0.6, 0.6, 0.6)
        elseif B.carried then
            local n = B.SpellInfo(B.carried)
            GameTooltip:AddLine("Place " .. n .. " here")
        else
            GameTooltip:AddLine("Empty slot")
            GameTooltip:AddLine("Click a spell in the list, then click here.", 0.6, 0.6, 0.6)
        end
        GameTooltip:Show()
    end)
    slot:SetScript("OnLeave", function() GameTooltip:Hide() end)

    f.slots[i] = slot
end

local hint = f:CreateFontString(nil, "OVERLAY", "GameFontDisableSmall")
hint:SetPoint("BOTTOM", f, "BOTTOM", 0, 8)
hint:SetText("")
f.hint = hint

-- ============================================================
-- Refresh
-- ============================================================

local function SortedBotNames()
    local names = {}
    local name, rec
    for name, rec in pairs(B.bots) do
        if rec.state ~= "notbot" then table.insert(names, name) end
    end
    table.sort(names)
    return names
end

function B.RefreshUI()
    if not f:IsShown() then return end

    local names = SortedBotNames()

    -- Tabs. Hidden wholesale while collapsed -- the cycler in the control row is
    -- how bots are selected in that mode.
    local collapsed = B.Collapsed()
    local i
    for i = 1, 5 do
        local tab = f.tabs[i]
        local name = names[i]
        if name and not collapsed then
            local rec = B.bots[name]
            tab.botName = name
            local suffix = ""
            if rec.state == "loading" then suffix = " ..." end
            tab.label:SetText(name .. suffix)
            if name == B.selected then
                tab.bg:SetTexture(0.25, 0.35, 0.55, 0.9)
                tab.label:SetTextColor(1, 1, 1)
            else
                tab.bg:SetTexture(0.15, 0.15, 0.15, 0.8)
                tab.label:SetTextColor(0.7, 0.7, 0.7)
            end
            tab:Show()
        else
            tab.botName = nil
            tab:Hide()
        end
    end

    local rec = B.selected and B.bots[B.selected] or nil

    local spells = (rec and rec.spells) or {}
    local total = table.getn(spells)

    if not rec then
        f.subtitle:SetText("No bots in your group")
    elseif rec.state == "ready" then
        f.subtitle:SetText(B.selected .. "  -  level " .. tostring(rec.level)
            .. ", " .. total .. " spells")
    elseif rec.state == "loading" then
        f.subtitle:SetText(B.selected .. "  -  reading spellbook...")
    else
        f.subtitle:SetText(B.selected .. "  -  press Refresh")
    end

    -- Spell list
    FauxScrollFrame_Update(scroll, total, ROWS, ROW_H)
    local offset = FauxScrollFrame_GetOffset(scroll)

    for i = 1, ROWS do
        local row = f.rows[i]
        local id = spells[i + offset]
        if id then
            local n, r, icon, g = B.SpellInfo(id)
            row.spellId = id
            row.icon:SetTexture(icon)
            local text = n
            if r and r ~= "" then text = text .. "  |cff888888" .. r .. "|r" end
            if g then text = text .. "  |cffffcc33*|r" end
            row.label:SetText(text)
            if B.carried == id then
                row.label:SetTextColor(0.4, 1, 0.4)
            else
                row.label:SetTextColor(1, 1, 1)
            end
            row:Show()
        else
            row.spellId = nil
            row:Hide()
        end
    end

    -- Bar
    local slots = B.Slots(B.selected)
    local now = GetTime()
    for i = 1, B.NumSlots() do
        local slot = f.slots[i]
        local id = slots[i]
        if id then
            local _, _, icon = B.SpellInfo(id)
            slot.icon:SetTexture(icon)
            slot.icon:Show()
        else
            slot.icon:Hide()
        end

        local fl = id and B.flash[id] or nil
        if fl and fl.expires > now then
            if fl.kind == "ok" then
                slot.tint:SetTexture(0.2, 1, 0.2, 0.35)
            elseif fl.kind == "fail" then
                slot.tint:SetTexture(1, 0.2, 0.2, 0.45)
            else
                slot.tint:SetTexture(1, 1, 0.3, 0.3)
            end
            slot.tint:Show()
        else
            slot.tint:Hide()
        end
    end

    if B.carried then
        local n = B.SpellInfo(B.carried)
        f.hint:SetText("Carrying |cff66ff66" .. n .. "|r  -  click a slot to place it")
    elseif collapsed then
        f.hint:SetText("Shift/Ctrl/Alt-click a slot to change where it lands.  < > switches bot.")
    else
        f.hint:SetText("* = ground targeted.  Shift/Ctrl/Alt-click a slot to change where it lands.")
    end
end

-- Outcome feedback, driven from Core's response parser.
function B.FlashSlot(botName, spellId, kind)
    if not spellId then return end
    B.flash[spellId] = { expires = GetTime() + 1.5, kind = kind }
    B.RefreshUI()
end

-- The flash has to clear itself; nothing else fires afterwards to redraw it.
f.elapsed = 0
f:SetScript("OnUpdate", function()
    this.elapsed = this.elapsed + arg1
    if this.elapsed < 0.25 then return end
    this.elapsed = 0

    local now = GetTime()
    local id, fl
    local dirty = false
    for id, fl in pairs(B.flash) do
        if fl.expires <= now then
            B.flash[id] = nil
            dirty = true
        end
    end
    if dirty then B.RefreshUI() end
end)

-- ============================================================
-- Relayout
--
-- Applies the row count and the collapsed state: shows the right number of
-- slots, shows or hides the spellbook, and recomputes the panel height. Called
-- whenever either setting changes, and once on load.
-- ============================================================

-- Resizing a CENTER-anchored frame moves both edges, which would shift the bar
-- out from under the cursor every time a row is added or the book is collapsed.
-- Re-anchoring to the bottom-left afterwards keeps the bar where it was, which is
-- the part the player is actually looking at.
local function SetHeightKeepingBottom(newH)
    local left, bottom = f:GetLeft(), f:GetBottom()
    f:SetHeight(newH)
    if left and bottom then
        f:ClearAllPoints()
        f:SetPoint("BOTTOMLEFT", UIParent, "BOTTOMLEFT", left, bottom)
    end
end

function B.Relayout()
    local rows = B.Rows()
    local collapsed = B.Collapsed()
    local shown = rows * B.SLOTS_PER_ROW

    local i
    for i = 1, B.MAX_SLOTS do
        if i <= shown then f.slots[i]:Show() else f.slots[i]:Hide() end
    end

    -- Top edge of the highest slot row, measured from the frame's bottom.
    local barTop = BAR_BOTTOM + rows * (SLOT_SIZE + SLOT_GAP) - SLOT_GAP

    if collapsed then
        listBg:Hide()
        for i = 1, 5 do f.tabs[i]:Hide() end
        f.collapseBtn:SetText("Show Book")
        SetHeightKeepingBottom(HEADER_H + BAR_CLEAR + barTop)
    else
        listBg:Show()
        f.collapseBtn:SetText("Hide Book")
        SetHeightKeepingBottom(LIST_SPAN + BAR_CLEAR + barTop)
    end

    if rows == 1 then
        f.rowsLabel:SetText("1 row")
    else
        f.rowsLabel:SetText(rows .. " rows")
    end

    -- Grey the end stops rather than letting a click do nothing silently.
    if rows <= B.MIN_ROWS then f.rowMinus:Disable() else f.rowMinus:Enable() end
    if rows >= B.MAX_ROWS then f.rowPlus:Disable() else f.rowPlus:Enable() end
end

-- ============================================================
-- Toggle
-- ============================================================

function B.Toggle()
    if f:IsShown() then
        f:Hide()
    else
        f:Show()
        B.Relayout()
        B.ScanParty()
        B.RefreshUI()
    end
end

-- NOTE: Relayout is deliberately NOT called here. SavedVariables are not populated
-- until after every file in the addon has executed, so a file-scope call would read
-- the defaults and the player's saved row count / collapsed state would be lost.
-- Core.lua drives it from ADDON_LOADED instead.
