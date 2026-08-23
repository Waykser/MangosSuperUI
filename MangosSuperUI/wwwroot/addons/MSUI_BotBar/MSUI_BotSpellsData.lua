--[[ MSUI_BotSpellsData.lua

     GENERATED FILE. Produced by MangosSuperUI: /Downloads
     Do not hand-edit -- it is rewritten on every Downloads page visit.

     This is the PLACEHOLDER shipped with the source tree. Visit the Downloads
     page once (or GET /Downloads/BotBarInfo) and the real table is written over
     it from spell_template + Spell.dbc.

     Why this file exists
     --------------------
     The server sends the addon a bot's spellbook as bare IDs. Vanilla 1.12 Lua
     cannot turn an arbitrary spell ID into a name or an icon -- GetSpellName
     only indexes the PLAYER's own spellbook -- so the lookup has to ship with
     the addon.

     MSUIBS_SPELLS[id] = { n = name, r = rank text, i = icon, g = ground-targeted }

     g = 1 means the spell needs a destination (Blizzard, Rain of Fire, Volley,
     Hurricane, Flamestrike). The addon offers the anchor modifiers on those
     buttons; every other spell casts on the resolved unit.

     Without this table the addon still works -- spells simply show as
     "Spell <id>" with a question-mark icon, and no spell is marked as ground
     targeted, so the anchor modifiers still function but are not advertised.
]]

MSUIBS_SPELLS = {}
