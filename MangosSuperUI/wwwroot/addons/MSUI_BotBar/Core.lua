-- MSUI_BotBar :: Core.lua   (v0.1)
-- Bot spellbook cache, GM command bridge, server response parsing.
--
-- Server contract (src/game/Commands/BotSpellCommands.cpp):
--     .botspell list <bot>               -> MSUIBS|BEGIN|<bot>|<class>|<level>|<chunks>
--                                           MSUIBS|SPELLS|<bot>|<seq>|<id,id,id,...>
--                                           MSUIBS|END|<bot>|<count>
--     .botspell cast <bot> <id> <anchor> -> MSUIBS|SENT|<bot>|<id>|<anchor>
--                                           MSUIBS|CAST|<bot>|<id>|OK|<detail>
--                                           MSUIBS|CAST|<bot>|<id>|FAIL|<reason>
--     .botspell stop <bot>               -> MSUIBS|STOP|<bot>
--     any failure                        -> MSUIBS|ERR|<bot>|<reason>|<id>
--
-- Those commands are SEC_PLAYER and authorise on group membership, so nothing
-- here needs a GM account. Every guard that matters is server-side; this file
-- is a convenience layer and is assumed to be bypassable.
--
-- WHY THE SPELL DATA FILE EXISTS
-- The server sends spell IDs and nothing else. Vanilla 1.12 has no API to turn
-- an arbitrary spell ID into a name or an icon (GetSpellName only indexes the
-- PLAYER's own book), so the lookup ships with the addon as the generated
-- MSUI_BotSpellsData.lua. Bare IDs are the fallback when it is missing.

MSUI_BotBar = MSUI_BotBar or {}
MSUI_BotBarDB = MSUI_BotBarDB or {}
-- Panel settings live in their own saved variable rather than a reserved key in
-- MSUI_BotBarDB, which is keyed by bot name and would otherwise need a migration.
MSUI_BotBarConfig = MSUI_BotBarConfig or {}

local B = MSUI_BotBar

-- The bar is a grid: SLOTS_PER_ROW wide, 1..MAX_ROWS tall. Every slot button is
-- built once at MAX_ROWS and simply hidden above the current row count, so adding
-- a row costs nothing at runtime and slots keep their contents when a row is
-- removed and put back.
B.SLOTS_PER_ROW = 12
B.MIN_ROWS = 1
B.MAX_ROWS = 4
B.MAX_SLOTS = B.SLOTS_PER_ROW * B.MAX_ROWS
B.SEND_GAP = 0.6      -- seconds between commands; the server throttles at 0.4
B.LIST_TIMEOUT = 6

-- [name] = {
--   state   = "unknown" | "loading" | "ready" | "notbot",
--   class, level,
--   spells  = { id, id, ... },
--   chunks  = expected, got = received,
--   asked   = GetTime() when the request went out,
-- }
B.bots = {}

B.queue = {}          -- names waiting for a .botspell list
B.lastSend = 0
B.selected = nil      -- bot name shown in the UI

-- ============================================================
-- Output
-- ============================================================

function B.Print(msg)
    DEFAULT_CHAT_FRAME:AddMessage("|cff66ccff[BotBar]|r " .. tostring(msg))
end

function B.Error(msg)
    DEFAULT_CHAT_FRAME:AddMessage("|cffff5555[BotBar]|r " .. tostring(msg))
end

-- Every command goes out as a GM-style slash command, the same transport the
-- Placer, DualSpec and LootBrowser addons use. Commands beginning with "." are
-- consumed server-side and never broadcast to the SAY channel.
function B.GM(cmd)
    SendChatMessage("." .. cmd, "SAY")
end

-- ============================================================
-- Spell metadata (from the generated data file)
-- ============================================================

function B.SpellInfo(id)
    local rec = MSUIBS_SPELLS and MSUIBS_SPELLS[id]
    if rec then
        return rec.n, rec.r, "Interface\\Icons\\" .. (rec.i or "INV_Misc_QuestionMark"), rec.g
    end
    -- No data file, or a spell it does not know: still usable, just unlabelled.
    return "Spell " .. id, nil, "Interface\\Icons\\INV_Misc_QuestionMark", nil
end

function B.IsGround(id)
    local _, _, _, g = B.SpellInfo(id)
    if g then return true end
    return false
end

-- ============================================================
-- Request queue
--
-- The server rate-limits a caller to one command per 400ms and answers a list
-- request with a burst of chat lines. Requests are therefore queued and drained
-- on a timer rather than fired in a loop on PARTY_MEMBERS_CHANGED, which would
-- otherwise trip the throttle on the second party member every time.
-- ============================================================

function B.Enqueue(name)
    if not name or name == "" then return end
    local i
    for i = 1, table.getn(B.queue) do
        if B.queue[i] == name then return end
    end
    table.insert(B.queue, name)
end

function B.PumpQueue()
    local now = GetTime()
    if now - B.lastSend < B.SEND_GAP then return end
    if table.getn(B.queue) == 0 then return end

    local name = table.remove(B.queue, 1)
    local rec = B.bots[name]
    if not rec then return end
    if rec.state == "ready" or rec.state == "notbot" then return end

    rec.state = "loading"
    rec.spells = {}
    rec.chunks = nil
    rec.got = 0
    rec.asked = now
    B.lastSend = now
    B.GM("botspell list " .. name)
end

-- A request that never gets an answer must not pin the entry on "loading"
-- forever, or the bot can never be retried.
function B.ExpireStale()
    local now = GetTime()
    local name, rec
    for name, rec in pairs(B.bots) do
        if rec.state == "loading" and rec.asked and (now - rec.asked) > B.LIST_TIMEOUT then
            rec.state = "unknown"
            rec.asked = nil
        end
    end
end

-- ============================================================
-- Party scanning
--
-- Nothing client-side can tell a bot from a player, so the addon simply asks:
-- a real player answers "not_a_bot" and is remembered as such. Self-discovering,
-- and it costs one command per new party member per session.
-- ============================================================

function B.ScanParty()
    local present = {}
    local i
    for i = 1, GetNumPartyMembers() do
        local name = UnitName("party" .. i)
        if name then
            present[name] = true
            if not B.bots[name] then
                B.bots[name] = { state = "unknown" }
            end
            if B.bots[name].state == "unknown" then
                B.Enqueue(name)
            end
        end
    end

    -- Forget anyone who left, so rejoining re-queries rather than showing a
    -- stale book from before they trained.
    local name, rec
    for name, rec in pairs(B.bots) do
        if not present[name] then
            B.bots[name] = nil
            if B.selected == name then B.selected = nil end
        end
    end

    if B.selected and not B.bots[B.selected] then B.selected = nil end
    if not B.selected then
        for name, rec in pairs(B.bots) do
            if rec.state ~= "notbot" then
                B.selected = name
                break
            end
        end
    end

    if B.RefreshUI then B.RefreshUI() end
end

function B.Refresh(name)
    if not name then name = B.selected end
    if not name then return end
    if not B.bots[name] then return end
    B.bots[name].state = "unknown"
    B.Enqueue(name)
end

-- ============================================================
-- Commands
-- ============================================================

-- Modifiers pick the anchor so the player never types one. See
-- BotSpellCommands.cpp for what each resolves to server-side.
function B.CurrentAnchor()
    if IsShiftKeyDown() then return "self" end
    if IsControlKeyDown() then return "cluster" end
    if IsAltKeyDown() then return "bottarget" end
    return "target"
end

function B.Cast(botName, spellId, anchor)
    if not botName or not spellId then return end
    if not anchor then anchor = B.CurrentAnchor() end
    B.lastSend = GetTime()
    B.GM("botspell cast " .. botName .. " " .. spellId .. " " .. anchor)
end

function B.Stop(botName)
    if not botName then return end
    B.lastSend = GetTime()
    B.GM("botspell stop " .. botName)
end

-- ============================================================
-- Slot storage (per character, keyed by bot name)
-- ============================================================

-- ---------- Panel settings ----------

function B.Rows()
    local n = MSUI_BotBarConfig.rows
    if not n or n < B.MIN_ROWS then n = 2 end
    if n > B.MAX_ROWS then n = B.MAX_ROWS end
    return n
end

function B.NumSlots()
    return B.Rows() * B.SLOTS_PER_ROW
end

-- delta is +1 / -1. Slots on a removed row are deliberately left in the DB so
-- that adding the row back restores what was on it.
function B.AddRows(delta)
    local n = B.Rows() + delta
    if n < B.MIN_ROWS then n = B.MIN_ROWS end
    if n > B.MAX_ROWS then n = B.MAX_ROWS end
    MSUI_BotBarConfig.rows = n
    if B.Relayout then B.Relayout() end
    if B.RefreshUI then B.RefreshUI() end
    return n
end

function B.SetRows(n)
    n = tonumber(n)
    if not n then return B.Rows() end
    return B.AddRows(n - B.Rows())
end

function B.Collapsed()
    if MSUI_BotBarConfig.collapsed then return true end
    return false
end

function B.ToggleCollapsed()
    if B.Collapsed() then
        MSUI_BotBarConfig.collapsed = nil
    else
        MSUI_BotBarConfig.collapsed = true
    end
    if B.Relayout then B.Relayout() end
    if B.RefreshUI then B.RefreshUI() end
end

-- Bot selection has to stay reachable with the spellbook hidden, so the panel
-- offers a cycler as well as the tab column.
function B.CycleBot(delta)
    local names = {}
    local name, rec
    for name, rec in pairs(B.bots) do
        if rec.state ~= "notbot" then table.insert(names, name) end
    end
    table.sort(names)

    local count = table.getn(names)
    if count == 0 then return end

    local idx = 1
    local i
    for i = 1, count do
        if names[i] == B.selected then idx = i break end
    end

    idx = idx + delta
    if idx < 1 then idx = count end
    if idx > count then idx = 1 end

    B.selected = names[idx]
    B.carried = nil
    if B.bots[B.selected] and B.bots[B.selected].state == "unknown" then
        B.Enqueue(B.selected)
    end
    if B.RefreshUI then B.RefreshUI() end
end

function B.Slots(botName)
    if not botName then return {} end
    if not MSUI_BotBarDB[botName] then MSUI_BotBarDB[botName] = {} end
    return MSUI_BotBarDB[botName]
end

function B.SetSlot(botName, slot, spellId)
    if not botName or not slot then return end
    B.Slots(botName)[slot] = spellId
    if B.RefreshUI then B.RefreshUI() end
end

-- ============================================================
-- Server responses
-- ============================================================

local function Split(str, sep)
    local out = {}
    local pat = "[^" .. sep .. "]+"
    local w
    for w in string.gfind(str, pat) do
        table.insert(out, w)
    end
    return out
end

function B.OnSystem(msg)
    if not msg then return end
    if string.sub(msg, 1, 7) ~= "MSUIBS|" then return end

    local f = Split(msg, "|")
    local kind = f[2]
    local name = f[3]

    if kind == "BEGIN" then
        local rec = B.bots[name]
        if not rec then
            rec = {}
            B.bots[name] = rec
        end
        rec.state = "loading"
        rec.class = tonumber(f[4])
        rec.level = tonumber(f[5])
        rec.chunks = tonumber(f[6])
        rec.got = 0
        rec.spells = {}
        rec.asked = GetTime()

    elseif kind == "SPELLS" then
        local rec = B.bots[name]
        if rec then
            local ids = Split(f[5] or "", ",")
            local i
            for i = 1, table.getn(ids) do
                local id = tonumber(ids[i])
                if id then table.insert(rec.spells, id) end
            end
            rec.got = (rec.got or 0) + 1
        end

    elseif kind == "END" then
        local rec = B.bots[name]
        if rec then
            rec.state = "ready"
            rec.asked = nil
            if not B.selected then B.selected = name end
            if B.RefreshUI then B.RefreshUI() end
        end

    elseif kind == "SENT" then
        -- Accepted by the server and handed to the bot. Not yet a cast.
        if B.FlashSlot then B.FlashSlot(name, tonumber(f[4]), "sent") end

    elseif kind == "CAST" then
        local spellId = tonumber(f[4])
        local verb = f[5]
        if verb == "OK" then
            if B.FlashSlot then B.FlashSlot(name, spellId, "ok") end
        else
            if B.FlashSlot then B.FlashSlot(name, spellId, "fail") end
            local sname = B.SpellInfo(spellId)
            B.Error(name .. " could not cast " .. sname .. " (" .. (f[6] or "?") .. ")")
        end

    elseif kind == "STOP" then
        B.Print("Cleared the pending order for " .. tostring(name) .. ".")

    elseif kind == "ERR" then
        local reason = f[4]
        local spellId = tonumber(f[5])
        if reason == "not_a_bot" then
            -- A real player. Remember it so we never ask again this session.
            if not B.bots[name] then B.bots[name] = {} end
            B.bots[name].state = "notbot"
            if B.selected == name then B.selected = nil end
            if B.RefreshUI then B.RefreshUI() end
        elseif reason == "throttled" then
            -- Requeue rather than surface it; the pump retries after the gap.
            if B.bots[name] and B.bots[name].state == "loading" then
                B.bots[name].state = "unknown"
                B.Enqueue(name)
            end
        else
            if spellId and spellId > 0 and B.FlashSlot then
                B.FlashSlot(name, spellId, "fail")
            end
            B.Error(tostring(name) .. ": " .. tostring(reason))
            if B.bots[name] and B.bots[name].state == "loading" then
                B.bots[name].state = "unknown"
            end
        end
    end
end

-- ============================================================
-- Chat suppression
--
-- A level 60 mage's book is a dozen data lines. Left alone they would scroll the
-- player's chat frame off the screen every time the bar refreshed, so the
-- prefixed lines are swallowed before ChatFrame ever sees them. Only lines
-- carrying the MSUIBS| prefix are touched; everything else passes straight
-- through to the original handler.
-- ============================================================

local origChatFrame_OnEvent = ChatFrame_OnEvent
function ChatFrame_OnEvent(event)
    if event == "CHAT_MSG_SYSTEM" and arg1 and string.sub(arg1, 1, 7) == "MSUIBS|" then
        return
    end
    return origChatFrame_OnEvent(event)
end

-- ============================================================
-- Events
-- ============================================================

local bus = CreateFrame("Frame", "MSUI_BotBarBus")
bus:RegisterEvent("CHAT_MSG_SYSTEM")
bus:RegisterEvent("PARTY_MEMBERS_CHANGED")
bus:RegisterEvent("PLAYER_ENTERING_WORLD")
bus:RegisterEvent("ADDON_LOADED")

bus:SetScript("OnEvent", function()
    if event == "CHAT_MSG_SYSTEM" then
        B.OnSystem(arg1)

    elseif event == "ADDON_LOADED" then
        -- First point at which MSUI_BotBarConfig actually holds the saved values:
        -- SavedVariables are applied after every file in the addon has run, so the
        -- row count and collapsed state can only be applied from here.
        if arg1 == "MSUI_BotBar" and B.Relayout then
            B.Relayout()
        end

    else
        B.ScanParty()
    end
end)

bus.elapsed = 0
bus:SetScript("OnUpdate", function()
    bus.elapsed = bus.elapsed + arg1
    if bus.elapsed < 0.2 then return end
    bus.elapsed = 0
    B.ExpireStale()
    B.PumpQueue()
end)

-- ============================================================
-- Slash commands
-- ============================================================

SLASH_MSUIBOTBAR1 = "/botbar"
SLASH_MSUIBOTBAR2 = "/bb"
SlashCmdList["MSUIBOTBAR"] = function(msg)
    if not msg then msg = "" end
    local _, _, cmd, rest = string.find(msg, "^(%S*)%s*(.*)$")
    if not cmd then cmd = "" end
    cmd = string.lower(cmd)

    if cmd == "" or cmd == "toggle" then
        if B.Toggle then B.Toggle() end

    elseif cmd == "refresh" then
        if rest == "" then rest = nil end
        B.Refresh(rest)
        B.Print("Refreshing spellbook...")

    elseif cmd == "scan" then
        local name, rec
        for name, rec in pairs(B.bots) do rec.state = "unknown" end
        B.ScanParty()
        B.Print("Rescanning party...")

    elseif cmd == "stop" then
        if rest == "" then rest = B.selected end
        B.Stop(rest)

    elseif cmd == "collapse" or cmd == "expand" or cmd == "book" then
        B.ToggleCollapsed()

    elseif cmd == "rows" then
        if rest == "" then
            B.Print("Bar is " .. B.Rows() .. " row(s). /botbar rows <" .. B.MIN_ROWS
                .. "-" .. B.MAX_ROWS .. ">")
        else
            local n = tonumber(rest)
            if not n then
                B.Error("Usage: /botbar rows <" .. B.MIN_ROWS .. "-" .. B.MAX_ROWS .. ">")
            else
                B.Print("Bar set to " .. B.SetRows(n) .. " row(s).")
            end
        end

    else
        B.Print("/botbar           toggle the panel")
        B.Print("/botbar collapse  hide or show the spellbook")
        B.Print("/botbar rows <n>  set the number of slot rows (" .. B.MIN_ROWS
            .. "-" .. B.MAX_ROWS .. ")")
        B.Print("/botbar refresh   re-read the selected bot's spellbook")
        B.Print("/botbar scan      re-scan the whole party")
        B.Print("/botbar stop      cancel the bot's pending cast order")
        B.Print("Click a slot to cast. Shift = at you, Ctrl = at the pack, Alt = at the bot's target.")
    end
end
