# MSUI Companion

An ability hotbar for your **own characters** running beside you as party companions.

A companion is one of your alts, logged in headless on its own account with its real
gear, talents, quest log and progression — not a fabricated fleet bot. It follows you,
assists your target, defends the party and runs its class rotation, but never picks its
own goals. This addon gives you buttons to tell it to do specific things.

## Setup

**1. Each companion needs its own account.** One account carries one session, so a
character on the account you are playing cannot also be loaded as a companion. Create
the extra accounts from the mangosd console — raw SQL will not produce a valid SRP6
password hash.

**2. Enrol from in-game** (requires GM level, which the `.sui` command family uses):

```
.sui companion add <charactername>
```

The character logs in and joins your party. Repeat for each one. Enrolment is runtime
only, so re-issue it after a server restart.

```
.sui companion list              -- who is enrolled, and what each is fighting
.sui companion remove <name>     -- log one out (progress is saved)
```

## Does their progress persist?

**Yes — fully.** A companion is a real character on a real account, and it saves through the
ordinary player path: the normal autosave timer (`PlayerSave.Interval`, 15 minutes by
default), plus a full save when you `.sui companion remove` it and when the server shuts
down. It earns XP from group kills and quest turn-ins and levels up normally.

A server crash loses up to 15 minutes of it — exactly the same exposure your own character
has, no more.

## Talents

```
.sui companion talent <name>                  -- how many unspent points it has
.sui companion talent <name> <talent>         -- spend one point (shift-click a talent in)
.sui companion talent <name> <talent> <rank>  -- explicit rank, 0-based
.sui companion untalent <name>                -- full respec, free
```

Shift-click a talent out of your own tree and paste it as the argument. With no rank given
the command spends the **next** rank, worked out from the companion's own spellbook — so the
rank baked into the link (which describes *your* character) is ignored and cannot mislead it.

Refusals name the reason: wrong class, no unspent points, already at that rank, or an unmet
prerequisite / row requirement. A newly learned ability enters the companion's rotation
immediately, without a relog.

Respec is free because a companion cannot walk itself to a trainer.

## Taking over a companion

`.sui possess <name>` hands you the body: WASD moves it, and it works on a stock client.
`.sui release` gives it back.

**It is a combat and movement driver, not a way to play the character.** While possessing:

| Goes to the companion | Still acts on YOUR OWN character |
|---|---|
| movement | talent frame (**refused** with a message, so it can't misfire) |
| target selection | bags, equipping, destroying items |
| melee attack / stop | trainers, vendors, quest accept and turn-in, loot |
| spell casting — see below | |

Two traps worth knowing:

- **Casting only fires spells in *both* spellbooks.** Your action bar sends *your* spell ids,
  and the server validates them against the *companion's* book. Anything it doesn't know is
  dropped.
- **You cannot use items at all while possessing** — not even your own. The server blocks
  `CMSG_USE_ITEM` for a session whose mover isn't its own body.

Your character sheet, bags, spellbook and quest log keep showing **your** character
throughout, because they are your character's. Nothing on screen reflects the companion.

**For anything structural — moving items, equipping gear, training, quests — remove the
companion and log into it with a real WoW client on its own account.** It's a real character,
so everything behaves normally. Re-enrol when you're done.

**3. Install this addon**, then `/reload`. The bar appears when a companion whose
abilities are in the catalog joins your party.

## Using the bar

Each companion gets a row: four order buttons, then its abilities.

| Click | Casts at |
|---|---|
| plain | your current target |
| **Alt** | the companion itself — its own buffs and cooldowns |
| **Ctrl** | you — heals and shields |

| Order | Effect |
|---|---|
| **St** | Stop — break off and cancel a cast still closing in |
| **Cm** | Come — walk to where you are standing |
| **Hd** | Hold — stand this ground, keep assisting from it |
| **Fo** | Follow — resume formation on you |

Drag the frame by its title to move it. `/companion` lists the slash commands.

## What "to the best of their ability" means

Casts run through the server's real spell path, so range, line of sight, power cost,
cooldown, the GCD and facing are enforced exactly as they are for you. When one fails
you get a system message naming the reason — *out of range*, *no line of sight*, *not
enough power*, *on cooldown*.

Range and line of sight are not final: the companion walks toward the target and retries
for about five seconds before reporting failure. Any new order cancels a cast still in
that window.

## Keeping the ability list current

`CompanionData.lua` is generated from the character database each time the Downloads
page is opened, so a fresh download reflects what each character knew at that moment.
After levelling, re-download to pick up new ranks.

A stale catalog cannot invent an ability — the server independently refuses any spell
the character does not actually know, and says so.

## Why chat commands

A 1.12 client cannot send a custom opcode, so `SendChatMessage` is the only channel that
reaches the server's command parser. The same approach is used by MSUI_DualSpec and
MangosSuperUI_Placer. It also happens to be why this works at all: 1.12 predates the
protected-function/taint model, so an addon button may fire a command **in combat**.
