# MSUI Bot Bar

Browse the spellbook of any bot in your group and order it to cast.

`/botbar` (or `/bb`) opens the panel. Bots in your party appear as tabs down the
left; pick one and its castable spells fill the list. Click a spell to pick it
up, then click a slot on the bar to place it there. Click the slot to make the
bot cast it. Right-click a slot to clear it. Slots are saved per character, per
bot.

## Collapsing and resizing the bar

**Hide Book** collapses the panel down to just the bar, which is what you want
once the slots are set up — a 2-row bar goes from 392px tall to 170px. The
spellbook, the tab column and the scroll list all go away; **Show Book** brings
them back. Because the tabs are part of what gets hidden, the `<` and `>` buttons
in the control row cycle the selected bot and work in either mode.

**− / +** change the number of slot rows, from 1 to 4 (12 to 48 slots). Rows are
added upward, so the existing bottom row never moves under your cursor. Removing
a row does not erase what was on it — put the row back and the slots return.

Both settings are saved per character. `/botbar collapse` and `/botbar rows <n>`
do the same things from chat.

## Where the spell lands

Vanilla 1.12 has no way for an addon to read world coordinates from a mouse
click — there is no camera ray and no API for it — so there is no click-to-place
reticle. Instead the addon sends an *anchor* and the server resolves it, which it
can do better anyway because it can see the whole fight.

Hold a modifier as you click a slot:

| Modifier | Where it lands |
| --- | --- |
| *(none)* | your current target |
| Shift | your own feet |
| Ctrl | the densest pack of mobs already fighting your group |
| Alt | whatever the bot is currently fighting |

Spells marked with a gold `*` are ground targeted (Blizzard, Rain of Fire,
Volley, Hurricane, Flamestrike) — those are the ones where the choice really
matters. For everything else the anchor just picks which unit gets the spell, so
Shift-click is how you ask a bot to heal or buff *you*.

## Best effort, not a guarantee

An order is not a command the bot obeys instantly. It parks for about five
seconds and is retried four times a second until it fires, so a click landing
mid-GCD, mid-cast, or a step out of range still works once the bot can act. The
slot flashes yellow when the order is accepted, green when the spell actually
goes off, and red if it could not — a bot will not cast something it has not
learned, cannot afford, has on cooldown, or has no line of sight to. `/botbar
stop` (or the Stop button) drops a pending order early.

## Requirements

- You must be **grouped** with the bot. That is the whole permission model: the
  server refuses spell orders for any bot outside your own party.
- No GM access is needed. The commands are `SEC_PLAYER`.
- Works on a stock 1.12 client. No patched executable, no custom opcode.

## Files

| File | |
| --- | --- |
| `Core.lua` | transport, spellbook cache, server response parsing, panel settings |
| `UI.lua` | the panel, the list, the bar, layout |
| `MSUI_BotSpellsData.lua` | **generated** — spell names, icons, ground flags |

`MSUI_BotSpellsData.lua` is rewritten by MangosSuperUI every time the Downloads
page is visited or this addon is downloaded. It exists because vanilla Lua cannot
turn an arbitrary spell ID into a name or an icon, and the server only sends IDs.
If it is missing or stale the addon still works — spells just show as
`Spell <id>` with a placeholder icon.

## Server side

`src/game/Commands/BotSpellCommands.cpp` in SuperUI-Core:

```
.botspell list <bot>                dump the bot's castable spellbook
.botspell cast <bot> <id> [anchor]  order a one-shot cast
.botspell stop <bot>                drop the bot's pending order
```

You can type these by hand; the addon is only a convenience layer over them.

## Saved variables

| | |
| --- | --- |
| `MSUI_BotBarDB` | `[botName] = { [slotIndex] = spellId }` |
| `MSUI_BotBarConfig` | `{ rows = n, collapsed = true/nil }` |

Kept separate because `MSUI_BotBarDB` is keyed by bot name, and folding settings
into it would mean reserving a key that a character could in principle be called.
