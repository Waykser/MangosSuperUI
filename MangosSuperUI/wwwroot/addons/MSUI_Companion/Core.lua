--[[
     MSUI_Companion :: Core.lua   (v0.1)

     A hotbar for the abilities of your OWN characters running beside you as
     party companions (enrolled server-side with ".sui companion add <name>").

     Server contract (src/game/SuperUiBots/SuiPossess.cpp):
         .sui cast <member> <spellId> [target|me|self|<playername>]
         .sui order <member> <stop|come|hold|follow>
         .sui companion list
         .sui companion remove <name>
         .sui companion talent <name> [talent] [rank]
         .sui companion untalent <name>

     The cast runs through the real spell path on the server, so range, line of
     sight, power cost, cooldown and the GCD are enforced exactly as they are for
     you. Failures come back as system messages naming the reason; a cast that
     failed ONLY on range or line of sight is not final -- the companion walks in
     and retries for a few seconds before giving up.

     Why chat messages and not an addon channel: a 1.12 client cannot send a
     custom opcode, and SendChatMessage is the one channel that reaches the
     server's command parser. This is the same approach MSUI_DualSpec and
     MangosSuperUI_Placer already use.

     Vanilla 1.12 / Lua 5.0 rules throughout:
       SetPoint always takes 5 arguments
       handlers read this / event / arg1, never a self parameter
       no string.match, no string.gmatch, no # operator, no table.getn
]]

MSUI_Companion = MSUI_Companion or {};
MSUI_CompanionDB = MSUI_CompanionDB or {};

local C = MSUI_Companion;

C.MAX_PARTY = 4;               -- vanilla party is you + 4
C.roster = {};                 -- ordered list of companion names currently grouped
C.rosterCount = 0;

-- ============================================================
-- Output
-- ============================================================

function C.Print(msg)
    DEFAULT_CHAT_FRAME:AddMessage("|cff00ccff[Companion]|r " .. tostring(msg));
end

function C.Error(msg)
    DEFAULT_CHAT_FRAME:AddMessage("|cffff4444[Companion]|r " .. tostring(msg));
end

-- Every server command goes through here. SAY is used rather than a channel
-- because the command parser reads the chat line before it is ever broadcast --
-- a leading "." never reaches other players.
function C.Send(cmd)
    SendChatMessage("." .. cmd, "SAY");
end

-- ============================================================
-- Data lookup
-- ============================================================

-- CompanionData.lua is regenerated from the character database each time the
-- addon is downloaded, so it reflects what each character knew at that moment.
-- Level up a companion and its new ranks appear after the next download; until
-- then the bar simply shows the older set, and the server refuses anything the
-- character does not actually know.
function C.DataFor(name)
    if not name or not MSUI_COMPANION_SPELLS then return nil end
    return MSUI_COMPANION_SPELLS[name];
end

function C.HasData(name)
    local d = C.DataFor(name);
    if not d or not d.spells then return false end
    return d.spells[1] ~= nil;
end

-- How many characters the packaged catalog covers. Walked rather than counted:
-- MSUI_COMPANION_SPELLS is keyed by name, and Lua 5.0 has no # operator anyway.
function C.CatalogCount()
    if not MSUI_COMPANION_SPELLS then return 0 end
    local n = 0;
    for _ in pairs(MSUI_COMPANION_SPELLS) do n = n + 1 end
    return n;
end

-- ============================================================
-- Roster
-- ============================================================

-- Who in the party do we have an ability list for? That is the working
-- definition of "companion" on the client: the server owns the real enrolment,
-- and a character with no baked data has nothing to put on a bar anyway.
function C.RefreshRoster()
    local previous = C.rosterCount;
    C.roster = {};
    C.rosterCount = 0;

    -- Party members we deliberately skipped because the catalog has nothing for them.
    -- Tracked so the addon can say WHY the bar is empty. Without this the common
    -- first-run failure -- addon installed, catalog never generated -- produces no bar
    -- and no message, which reads as the addon being broken.
    C.skipped = {};
    C.skippedCount = 0;

    local i = 1;
    while i <= C.MAX_PARTY do
        local unit = "party" .. i;
        if UnitExists(unit) then
            local name = UnitName(unit);
            if name then
                if C.HasData(name) then
                    C.rosterCount = C.rosterCount + 1;
                    C.roster[C.rosterCount] = name;
                else
                    C.skippedCount = C.skippedCount + 1;
                    C.skipped[C.skippedCount] = name;
                end
            end
        end
        i = i + 1;
    end

    if C.Bars_Rebuild then C.Bars_Rebuild() end

    if C.rosterCount ~= previous then
        if C.rosterCount > 0 then
            C.Print(C.rosterCount .. " companion(s) on the bar.");
        end
    end

    -- Report the skip every time it changes, independently of rosterCount. A party of
    -- nothing-but-uncatalogued members leaves rosterCount at 0 on both sides of the
    -- comparison above, so the branch there can never fire for the case that matters.
    if C.skippedCount ~= (C.lastSkippedCount or 0) then
        C.lastSkippedCount = C.skippedCount;
        if C.skippedCount > 0 then
            C.Error(C.skippedCount .. " party member(s) have no abilities in the catalog: "
                .. table.concat(C.skipped, ", "));
            if C.CatalogCount() == 0 then
                C.Error("The catalog is EMPTY. Open the MangosSuperUI Downloads page once "
                    .. "to generate it, then re-download this addon.");
            else
                C.Error("Re-download the addon from the Downloads page to refresh it.");
            end
        end
    end
end

-- ============================================================
-- Orders
-- ============================================================

-- targetSpec mirrors the server command's vocabulary:
--   "target" your current target, "me" you, "self" the companion itself.
function C.Cast(name, spellId, targetSpec)
    if not name or not spellId then return end
    C.Send("sui cast " .. name .. " " .. spellId .. " " .. (targetSpec or "target"));
end

-- Read the modifier keys the same way everywhere, so every button on the bar
-- answers to one set of rules.
function C.TargetSpecFromModifiers()
    if IsAltKeyDown() then return "self" end       -- the companion buffs itself
    if IsControlKeyDown() then return "me" end     -- aimed at you (heals, shields)
    return "target";                               -- what you have selected
end

-- Standing orders. word is one of stop / come / hold / follow, matching
-- .sui order's vocabulary exactly -- the addon invents nothing the server
-- does not already understand.
function C.Order(name, word)
    if not name or not word then return end
    C.Send("sui order " .. name .. " " .. word);
end

function C.ListCompanions()
    C.Send("sui companion list");
end

-- ============================================================
-- Events
-- ============================================================

local f = CreateFrame("Frame", "MSUI_CompanionEventFrame");
f:RegisterEvent("VARIABLES_LOADED");
f:RegisterEvent("PARTY_MEMBERS_CHANGED");
f:RegisterEvent("PLAYER_ENTERING_WORLD");

f:SetScript("OnEvent", function()
    if event == "VARIABLES_LOADED" then
        if MSUI_CompanionDB.shown == nil then MSUI_CompanionDB.shown = true end
        if C.Bars_Init then C.Bars_Init() end
        C.RefreshRoster();
        local catalog = C.CatalogCount();
        if catalog == 0 then
            -- Say this loudly at load rather than waiting for a party to form. An empty
            -- catalog means the bar can never appear for anyone, and the cause is not
            -- something you can see or fix from in game.
            C.Error("loaded, but the ability catalog is EMPTY -- the bar cannot appear.");
            C.Error("Open the MangosSuperUI Downloads page once to generate it, then "
                .. "re-download and reinstall this addon.");
        else
            C.Print("loaded, " .. catalog .. " character(s) in the catalog. /companion for options.");
        end
    elseif event == "PARTY_MEMBERS_CHANGED" or event == "PLAYER_ENTERING_WORLD" then
        C.RefreshRoster();
    end
end);

-- ============================================================
-- Slash command
-- ============================================================

SLASH_MSUICOMPANION1 = "/companion";
SLASH_MSUICOMPANION2 = "/msuic";
SlashCmdList["MSUICOMPANION"] = function(msg)
    msg = string.lower(msg or "");

    if msg == "hide" then
        MSUI_CompanionDB.shown = false;
        if C.Bars_UpdateVisibility then C.Bars_UpdateVisibility() end
        C.Print("bar hidden.");
    elseif msg == "show" then
        MSUI_CompanionDB.shown = true;
        if C.Bars_UpdateVisibility then C.Bars_UpdateVisibility() end
        C.Print("bar shown.");
    elseif msg == "list" then
        C.ListCompanions();
    elseif msg == "reload" then
        C.RefreshRoster();
    elseif msg == "status" then
        -- The one command to run when the bar is not appearing. Every gate the bar
        -- passes through, reported in order, so the failing one is obvious.
        C.Print("catalog: " .. C.CatalogCount() .. " character(s)");
        if C.CatalogCount() > 0 then
            for n, d in pairs(MSUI_COMPANION_SPELLS) do
                local count = 0;
                if d and d.spells then
                    for _ in pairs(d.spells) do count = count + 1 end
                end
                C.Print("   " .. n .. " (level " .. tostring(d and d.level or "?")
                    .. ", " .. count .. " abilities)");
            end
        end
        C.Print("in party with catalog data: " .. C.rosterCount);
        C.Print("in party WITHOUT catalog data: " .. (C.skippedCount or 0));
        C.Print("bar setting: " .. (MSUI_CompanionDB.shown and "shown" or "hidden"));
        if C.CatalogCount() == 0 then
            C.Error("-> catalog empty: open the Downloads page, re-download, reinstall.");
        elseif C.rosterCount == 0 then
            C.Error("-> no catalogued companion in your party, so the bar stays hidden.");
        elseif not MSUI_CompanionDB.shown then
            C.Error("-> the bar is hidden. Use /companion show.");
        else
            C.Print("-> the bar should be visible. Drag it by its title if it is off-screen.");
        end
    else
        C.Print("commands:");
        C.Print("  /companion show | hide   -- toggle the ability bar");
        C.Print("  /companion list          -- ask the server who is enrolled");
        C.Print("  /companion status        -- why the bar is not showing");
        C.Print("  /companion reload        -- rescan the party");
        C.Print("clicks: plain = your target, Alt = the companion, Ctrl = you.");
        C.Print("enrol with: .sui companion add <charactername>");
        C.Print("talents: .sui companion talent <name> <shift-clicked talent>");
    end
end;
