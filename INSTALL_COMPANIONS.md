# Installing Companions on an Existing Server

> **Audience:** Someone already running SuperUI-Core + MangosSuperUI per [`INSTALL.md`](INSTALL.md).
> This guide covers only what the **companion** feature adds on top of a working install.

A *companion* is one of **your own characters** — real gear, talents, quest log and
progression — logged in headless beside you, in your party, under your command. It follows
you, assists your target, defends the party and runs its class rotation, but never picks its
own goals. It is **not** a fabricated fleet bot and is never written to the `playerbot`
registry.

---

## Summary: what actually has to happen

| | Required? |
|---|---|
| **Database schema changes** | **None.** No new tables, no new columns, no migration to run. |
| **`mangosd.conf` changes** | **None.** (Two optional tunings noted at the end.) |
| **Rebuild SuperUI-Core** | **Yes** — every companion command lives in the core. |
| **Rebuild/redeploy MangosSuperUI** | **Yes** — the brain opt-out and the addon catalog generator. |
| **Manual work outside `git pull`** | **Yes** — one game account per companion, and moving characters onto them. This is the bulk of the work. |
| **CMake reconfigure** | Not needed. Every core change edits an existing file; no source files were added. |

**Do it in this order.** Accounts first (the core refuses to enrol until they're right), then
the core, then the web app, then the addon.

---

## Part 1: Accounts (the manual work)

### Step 1: Understand the one hard constraint

**One account carries one session.** A character on the account you are logged into cannot
also be loaded as a companion — `PlayerBotMgr::AddBot` refuses when the account already has a
session. So:

- The account **you play on** stays as it is.
- **Each companion needs its own separate account.** Three companions = three extra accounts.

This is not a tunable. It is how the session model works.

### Step 2: Create one account per companion

Accounts **must** be created through the console — raw SQL does not generate the SRP6
password hash, and the account will never be usable. `account create` is `SEC_CONSOLE`, so it
cannot be run in-game.

Two places work:

- The **mangosd console** itself (the `mangos>` prompt, e.g. inside `screen`).
- The **MangosSuperUI web console** (*Console* in the sidebar) — RA promotes an account to
  `SEC_CONSOLE` when it is level 6 *and* `Ra.Restricted = 0` (`RASocket.cpp:197`). Both are
  what `INSTALL.md` already has you set, so this normally just works. If `account create`
  comes back as a permission error, use the mangosd console instead.

Run one per companion:

```
account create companion1 SOME_PASSWORD
account create companion2 SOME_PASSWORD
```

These accounts never need a GM level — nothing logs into them with a client. They exist only
to carry a session.

### Step 3: Put the characters on those accounts

If you are creating **fresh** alts, just make them while logged into those accounts with a
real client, and skip to Step 4.

To use **existing** characters, they must be moved. There is no GM command for this — the
`.character` command family has no account-move — so it is raw SQL.

> ⚠️ **Stop mangosd first.** `ObjectMgr::GetPlayerAccountIdByGUID` answers from an in-memory
> player cache that is loaded at startup. Moving a character while the server is running
> leaves that cache holding the *old* account, and `.sui companion add` will then fail with
> `REFUSING to spawn guid N as a bot: character belongs to account X, session is account Y` —
> the DB says one thing and the cache says another. Stopping mangosd for the update avoids
> this entirely.

```bash
sudo systemctl stop mangosd
```

Find the account ids and the character:

```sql
SELECT id, username FROM realmd.account WHERE username IN ('COMPANION1','COMPANION2');
SELECT guid, name, account, level FROM characters.characters WHERE name = 'YOURALT';
```

> Usernames are stored uppercase. Note the character's current `account` value before you
> change it, in case you want to move it back.

Move it:

```sql
UPDATE characters.characters SET account = NEW_ACCOUNT_ID WHERE guid = CHARACTER_GUID;
```

Repeat per companion, then start the server again:

```bash
sudo systemctl start mangosd
```

> The character disappears from your main account's character list and appears on the new
> one. That is expected — it is now that account's character. Nothing else about it changes:
> gear, talents, bags, quests, level and money all move with it.

### Step 4: Give the character you PLAY GM level 6

The `.sui` command family is `SEC_ADMINISTRATOR`, which is **level 6** in SuperUI-Core. This
applies to the account you play on, not the companion accounts.

From the mangosd console (`account set gmlevel` is `SEC_CONSOLE`):

```
account set gmlevel YOUR_PLAY_ACCOUNT 6
```

Log out and back in for it to take effect.

> If you would rather not play on a GM-level account, the alternative is to lower the `.sui`
> table's security in `src/game/Chat/Chat.cpp` — `.spec` was lowered to `SEC_PLAYER` for
> exactly this reason and is the precedent to copy. That is a code change, not a setting.

---

## Part 2: Deploy SuperUI-Core

All companion commands live in the core, so nothing works until this is rebuilt and running.

```bash
cd YOUR_SUPERUI_CORE_SOURCE_DIR
git pull
```

Rebuild with your existing build process — **no CMake changes are needed**, because every
change edits a file that was already in the build. If your build tree is already configured,
an incremental build is enough.

Then swap the binary in and restart:

```bash
sudo systemctl stop mangosd
# copy the freshly built mangosd into YOUR_BIN_DIRECTORY as you normally do
sudo systemctl start mangosd
```

Confirm it came up and the commands exist — in-game, on your GM character:

```
.sui companion list
```

You should get `[SUI] no companions online.` rather than an unknown-command error.

### What changed in the core

- `.sui companion add|remove|list|talent|untalent`, `.sui cast`, `.sui order`
- Companions save their progress on the normal player save path
- Level-up no longer maxes a companion's weapon/defense skills
- The talent frame is refused while possessing, so a click can't silently spend *your* point

---

## Part 3: Deploy MangosSuperUI

```bash
cd /tmp && rm -rf MangosSuperUI
git clone https://github.com/Yafrovon/MangosSuperUI.git
cd MangosSuperUI
dotnet publish -c Release -o /tmp/mangossuperui-publish
sudo cp -r /tmp/mangossuperui-publish/* /opt/mangossuperui/
sudo chown -R YOUR_USERNAME:YOUR_USERNAME /opt/mangossuperui
sudo systemctl restart mangossuperui
```

(Adjust to however you normally deploy — the only requirement is that the new `wwwroot` ships,
since the new addon lives there.)

### What changed in the web app

- The fleet brain now reads the `possessed` / `companion` flags off STATE and **never plans
  for a companion** — it still senses one, so it stays visible on the dashboard, badged amber.
- The Downloads page generates the addon's ability catalog.

> **Both halves should be deployed.** They degrade safely if you only do one — an old web app
> ignores the new flag, a new web app defaults it to 0 — but with only the core updated, the
> brain will keep issuing goals at your companions and fight you for control of them.

---

## Part 4: The in-game addon

**1. Open the Downloads page once** (`http://YOUR_SERVER_IP:5000/Downloads`). Opening it is
what regenerates `CompanionData.lua` from your character database — the ability list the
hotbar is built from. Downloading before ever opening the page gets you an empty catalog.

**2. Download `MSUI_Companion`** and extract it into your client's
`Interface\AddOns\MSUI_Companion\`.

**3. Fully restart the game client** — exit to desktop and launch again. WoW enumerates
`Interface\AddOns` only at launch, so a UI reload cannot discover a folder that was not
present when the client started. Confirm it appears and is ticked under **AddOns** at the
character-select screen.

The bar appears when a companion whose abilities are in the catalog joins your party. If
nothing shows, run **`/companion status`** in game — it reports every gate (catalog size,
party members with and without data, bar visibility) and names the failing one.

> A UI reload is only enough when *updating* an already-installed addon. If `/reload` is not
> available on your client, `/console reloadui` is the reliable equivalent.

> **Re-download after levelling.** The catalog is a snapshot of what each character knew when
> the page was last opened. It cannot invent an ability — the server independently refuses any
> spell the character doesn't actually know — but new ranks won't appear on the bar until you
> refresh it.

Only characters on **real** accounts are baked into the catalog (accounts present in
`realmd.account`), highest level first, currently capped at 60 characters and 24 abilities
each. Fabricated fleet bots are deliberately excluded.

---

## Database notes

**There is no schema change to apply.** Companion enrolment is runtime-only and is
deliberately never written to `characters.playerbot` — that table is the fabricated-bot
registry, and a real character in it would be respawned on a synthetic account after a
restart, stamping over the owner. The only DB write you make by hand is the account move in
Step 3.

Two things worth **verifying** rather than changing:

**1. The web app's DB user needs `SELECT` on `realmd.account`.** The addon catalog generator
identifies "your own characters" the same way the bot brain's safety wall already does:

```sql
SELECT ... FROM characters c WHERE c.account IN (SELECT id FROM realmd.account)
```

If your fleet brain already runs, this grant exists. If the Downloads page produces an empty
catalog and the log shows a permissions error:

```sql
GRANT SELECT ON realmd.* TO 'mangos'@'localhost';
FLUSH PRIVILEGES;
```

**2. Your realmd schema must literally be named `realmd`.** That name is hardcoded in the
query above (as it already is in `BotBrainService`). If yours is named something else, both
the catalog generator and the pre-existing brain wall need that string changed.

---

## Optional config

Neither of these is required.

| Setting | Note |
|---|---|
| `PlayerBot.AllowSaving` | **No longer relevant to companions.** They now save regardless, per-entry. This setting still controls whether *fabricated* bots persist; leave it at whatever you had. |
| `PlayerSave.Interval` | Default 15 minutes. This is how much a companion can lose to a server crash — the same exposure your own character has. Lower it if you want a tighter window, at the cost of more DB writes. |

---

## Verification

Work down this list; each step depends on the ones above it.

**Enrolment**
1. `.sui companion add YOURALT` — it logs in and joins your party.
2. `.sui companion list` — shows it with level and distance.
3. The server log shows the doctrine resolve to `PlayerParty`.

**Behaviour**
4. Attack something — it assists your target within a tick.
5. Stand near neutral mobs doing nothing — it must **never** initiate a pull.

**Persistence (the one that matters)**
6. Let it gain a level and loot an item.
7. Note its weapon skill — it must be **normal for its level, not maxed**. If it jumped to
   cap, the new core binary isn't actually running.
8. `.sui companion remove YOURALT`, restart mangosd, then log that character in with a real
   client. The level and the item must be there.

**Commands**
9. `.sui cast YOURALT <spellId>` in range — it casts. Out of range — it walks in and retries,
   then reports honestly.
10. `.sui companion talent YOURALT` — reports unspent points. Shift-click a talent into
    `.sui companion talent YOURALT <paste>` — a point goes in, and a new active ability shows
    up in its rotation without a relog.
11. `.sui possess YOURALT`, open your talent frame, click a talent — you must get a **refusal
    message**, and your own unspent points must be unchanged.

**Brain separation**
12. With the fleet brain enabled and bots running, the companion receives **zero** commands —
    no `MOVE_TO`, no `SET_TASK_GRIND` against its GUID in the bridge log.
13. It still shows live position and health on the dashboard, badged as a companion.

**Addon**
14. With the addon installed and a companion grouped — one row per companion, correct
    icons. `/companion status` reports the catalog size and a non-zero party count.
15. Press an ability button **during combat** — it fires. (1.12 predates the taint model,
    which is what makes this possible at all.)

---

## Troubleshooting

**`[PlayerBotMgr] Account N is already online!`**
or **`[SUI] <name> is on the account you are logged into.`**
The character is on the account you're playing. One account, one session — see Step 1.

**`[AIBOT] REFUSING to spawn guid N as a bot: character belongs to account X, session is account Y`**
The character's DB row and the in-memory player cache disagree about its account. You almost
certainly moved it between accounts with mangosd running. Restart mangosd. If it persists,
re-check the `UPDATE` in Step 3 actually committed.

**`.sui` reports an unknown command**
Either the account you're playing isn't GM level 6 (Step 4 — log out and back in after
setting it), or the rebuilt core isn't the binary that's running.

**Companion joins the party but has no row on the bar**
Its abilities aren't in the catalog — the addon says so in red and names the character.
Open the Downloads page, re-download, replace the addon folder, and reload the UI.
Check the character is on an account that exists in `realmd.account`.

**The bar is empty for everyone**
`CompanionData.lua` was never generated. Open the Downloads page at least once, then
re-download.

**Companion's progress didn't save**
Confirm the new core binary is running — this is the fix that landed in `CharacterHandler.cpp`
and `WorldSession.cpp`. `PlayerBot.AllowSaving` is *not* the cause; companions bypass it.

**A talent won't go in**
The command names the reason: wrong class, no unspent points, already at that rank, or an
unmet prerequisite / not enough points spent in that tree to open the row. Use
`.sui companion untalent <name>` for a free respec if you want to start over.

**The brain keeps sending your companion off to grind**
The web app wasn't redeployed, so it isn't reading the `companion` flag. See Part 3.
