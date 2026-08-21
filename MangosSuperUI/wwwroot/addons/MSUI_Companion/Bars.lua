--[[
     MSUI_Companion :: Bars.lua   (v0.1)

     The bar itself: one row per companion in the party, each row a label plus its
     ability buttons, plus four standing-order buttons.

     Buttons are plain frames driving SendChatMessage. In 1.12 that is legal from a
     click handler IN COMBAT -- the protected-function/taint model arrived with 2.0,
     so nothing here needs a secure template. This whole design stops working on
     later clients.

     Vanilla 1.12 / Lua 5.0 rules throughout:
       SetPoint always takes 5 arguments
       handlers read this / event / arg1, never a self parameter
       no string.match, no string.gmatch, no # operator, no table.getn
]]

local C = MSUI_Companion;

local BTN        = 30;      -- ability button edge, pixels
local PAD        = 3;
local ROW_LABEL  = 90;      -- width of the name column
local ROW_H      = BTN + PAD;
local PER_ROW    = 12;      -- abilities before wrapping to a second line
local FALLBACK_ICON = "Interface\\Icons\\INV_Misc_QuestionMark";

local ORDERS = {
    { word = "stop",   label = "St", tip = "Stop -- break off and cancel a pending cast" },
    { word = "come",   label = "Cm", tip = "Come -- walk to where you are standing" },
    { word = "hold",   label = "Hd", tip = "Hold -- stand this ground, keep assisting" },
    { word = "follow", label = "Fo", tip = "Follow -- resume formation on you" },
};

local root;                 -- the draggable container
local rows = {};            -- reusable row frames, index 1..n
local rowCount = 0;

-- ============================================================
-- Button construction
-- ============================================================

local function AbilityTooltip()
    local spell = this.msuiSpell;
    if not spell then return end
    GameTooltip:SetOwner(this, "ANCHOR_RIGHT");
    GameTooltip:AddLine(spell.name or "?");
    if spell.rank and spell.rank ~= "" then
        GameTooltip:AddLine(spell.rank, 0.7, 0.7, 0.7);
    end
    GameTooltip:AddLine(this.msuiOwner or "?", 0.4, 0.8, 1.0);
    GameTooltip:AddLine("Click: your target", 0.6, 0.6, 0.6);
    GameTooltip:AddLine("Alt: the companion itself", 0.6, 0.6, 0.6);
    GameTooltip:AddLine("Ctrl: you", 0.6, 0.6, 0.6);
    GameTooltip:Show();
end

local function HideTooltip()
    GameTooltip:Hide();
end

local function AbilityClick()
    if not this.msuiSpell or not this.msuiOwner then return end
    C.Cast(this.msuiOwner, this.msuiSpell.id, C.TargetSpecFromModifiers());
end

local function OrderTooltip()
    if not this.msuiTip then return end
    GameTooltip:SetOwner(this, "ANCHOR_RIGHT");
    GameTooltip:AddLine(this.msuiTip);
    GameTooltip:AddLine(this.msuiOwner or "?", 0.4, 0.8, 1.0);
    GameTooltip:Show();
end

local function OrderClick()
    if not this.msuiWord or not this.msuiOwner then return end
    C.Order(this.msuiOwner, this.msuiWord);
end

local function MakeAbilityButton(parent, index)
    local b = CreateFrame("Button", parent:GetName() .. "Ability" .. index, parent);
    b:SetWidth(BTN);
    b:SetHeight(BTN);
    b:SetNormalTexture(FALLBACK_ICON);
    b:SetHighlightTexture("Interface\\Buttons\\ButtonHilight-Square", "ADD");
    b:SetScript("OnEnter", AbilityTooltip);
    b:SetScript("OnLeave", HideTooltip);
    b:SetScript("OnClick", AbilityClick);
    return b;
end

local function MakeOrderButton(parent, index)
    local b = CreateFrame("Button", parent:GetName() .. "Order" .. index, parent);
    b:SetWidth(BTN);
    b:SetHeight(BTN);
    b:SetNormalTexture("Interface\\Buttons\\UI-Panel-Button-Up");
    b:SetHighlightTexture("Interface\\Buttons\\ButtonHilight-Square", "ADD");

    local fs = b:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall");
    fs:SetPoint("CENTER", b, "CENTER", 0, 0);
    b.msuiLabel = fs;

    b:SetScript("OnEnter", OrderTooltip);
    b:SetScript("OnLeave", HideTooltip);
    b:SetScript("OnClick", OrderClick);
    return b;
end

-- ============================================================
-- Rows
-- ============================================================

local function GetRow(index)
    if rows[index] then return rows[index] end

    local r = CreateFrame("Frame", "MSUI_CompanionRow" .. index, root);
    r:SetHeight(ROW_H);

    local label = r:CreateFontString(nil, "OVERLAY", "GameFontNormal");
    label:SetPoint("LEFT", r, "LEFT", 4, 0);
    label:SetWidth(ROW_LABEL);
    label:SetJustifyH("LEFT");
    r.msuiLabel = label;

    r.abilities = {};
    r.orders = {};

    local i = 1;
    while i <= PER_ROW do
        r.abilities[i] = MakeAbilityButton(r, i);
        i = i + 1;
    end

    i = 1;
    while ORDERS[i] do
        local b = MakeOrderButton(r, i);
        b.msuiWord = ORDERS[i].word;
        b.msuiTip = ORDERS[i].tip;
        b.msuiLabel:SetText(ORDERS[i].label);
        r.orders[i] = b;
        i = i + 1;
    end

    rows[index] = r;
    return r;
end

-- Lay one companion's row out and point every button at that companion.
local function FillRow(r, name)
    r.msuiLabel:SetText(name);

    local data = C.DataFor(name);
    local spells = (data and data.spells) or {};

    local x = ROW_LABEL + PAD;

    -- Order buttons first, so they hold the same screen position on every row
    -- regardless of how many abilities the character happens to know.
    local i = 1;
    while r.orders[i] do
        local b = r.orders[i];
        b.msuiOwner = name;
        b:ClearAllPoints();
        b:SetPoint("LEFT", r, "LEFT", x, 0);
        b:Show();
        x = x + BTN + PAD;
        i = i + 1;
    end

    x = x + PAD * 2;   -- a gap between orders and abilities

    i = 1;
    while i <= PER_ROW do
        local b = r.abilities[i];
        local spell = spells[i];
        if spell then
            b.msuiOwner = name;
            b.msuiSpell = spell;
            b:SetNormalTexture("Interface\\Icons\\" .. (spell.icon or "INV_Misc_QuestionMark"));
            b:ClearAllPoints();
            b:SetPoint("LEFT", r, "LEFT", x, 0);
            b:Show();
            x = x + BTN + PAD;
        else
            b.msuiSpell = nil;
            b:Hide();
        end
        i = i + 1;
    end

    r:SetWidth(x + PAD);
    return x + PAD;
end

-- ============================================================
-- Public
-- ============================================================

function C.Bars_Rebuild()
    if not root then return end

    local widest = 200;
    local y = -22;   -- below the title bar
    local i = 1;

    while i <= C.rosterCount do
        local r = GetRow(i);
        local w = FillRow(r, C.roster[i]);
        if w > widest then widest = w end
        r:ClearAllPoints();
        r:SetPoint("TOPLEFT", root, "TOPLEFT", 0, y);
        r:Show();
        y = y - ROW_H;
        i = i + 1;
    end

    -- Retire rows left over from a larger party.
    i = C.rosterCount + 1;
    while rows[i] do
        rows[i]:Hide();
        i = i + 1;
    end

    rowCount = C.rosterCount;
    root:SetWidth(widest);
    root:SetHeight(22 + (ROW_H * C.rosterCount) + 12);   -- title strip + rows + bottom inset

    C.Bars_UpdateVisibility();
end

function C.Bars_UpdateVisibility()
    if not root then return end
    -- Nothing to command means nothing to show. The frame reappears by itself
    -- the moment a companion joins the party.
    if MSUI_CompanionDB.shown and C.rosterCount > 0 then
        root:Show();
    else
        root:Hide();
    end
end

function C.Bars_Init()
    if root then return end

    root = CreateFrame("Frame", "MSUI_CompanionBar", UIParent);
    root:SetWidth(300);
    root:SetHeight(60);
    root:SetBackdrop({
        bgFile = "Interface\\DialogFrame\\UI-DialogBox-Background",
        edgeFile = "Interface\\DialogFrame\\UI-DialogBox-Border",
        tile = true, tileSize = 32, edgeSize = 16,
        insets = { left = 5, right = 5, top = 5, bottom = 5 }
    });

    local pos = MSUI_CompanionDB.pos;
    if pos and pos.x and pos.y then
        root:SetPoint("TOPLEFT", UIParent, "BOTTOMLEFT", pos.x, pos.y);
    else
        root:SetPoint("CENTER", UIParent, "CENTER", 0, -180);
    end

    root:SetMovable(true);
    root:EnableMouse(true);
    root:RegisterForDrag("LeftButton");
    root:SetScript("OnDragStart", function() this:StartMoving() end);
    root:SetScript("OnDragStop", function()
        this:StopMovingOrSizing();
        -- Persist as an explicit BOTTOMLEFT offset: GetPoint's anchor can be any
        -- of the nine after a drag, and re-applying it verbatim drifts the frame.
        MSUI_CompanionDB.pos = { x = this:GetLeft(), y = this:GetTop() };
    end);

    local title = root:CreateFontString(nil, "OVERLAY", "GameFontNormal");
    title:SetPoint("TOPLEFT", root, "TOPLEFT", 10, -8);
    title:SetText("Companions");

    local hint = root:CreateFontString(nil, "OVERLAY", "GameFontDisableSmall");
    hint:SetPoint("TOPRIGHT", root, "TOPRIGHT", -10, -10);
    hint:SetText("click target / alt self / ctrl you");

    root:Hide();
end
