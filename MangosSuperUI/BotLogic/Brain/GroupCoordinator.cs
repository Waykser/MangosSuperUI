using MangosSuperUI.BotLogic.Core;
using MangosSuperUI.BotLogic.Data;
using MangosSuperUI.BotLogic.Planners;   // reuse QuestPlanner.ReachTier/InReach (match solo, don't reimplement)
using Microsoft.Extensions.Logging;

namespace MangosSuperUI.BotLogic.Brain;

// ============================================================================
// GroupCoordinator -- the "god bot" central driver (AIBOT_GROUPING_DESIGN).
//
// A STATIC stamping pre-pass the host runs ONCE per tick (BotBrainService
// .RunBrainTicksAsync) BEFORE the per-bot TickAsync loop -- NOT an IBotPlanner.
// It holds the union of every present member's objectives and drives the whole
// group through ONE TASK AT A TIME, TOGETHER (§0): accept together, grind
// together, turn in together, then advance. No leader, no follower -- members
// are PEERS that execute the god bot's stamped GroupOrder. It issues NO command;
// the spine (BotBrain) alone turns intent into wire, so there is exactly ONE
// decision layer (§1 / §8) -- the "second live decider" that deadlocked every
// prior attempt never exists.
//
// State lives on BotGroup.Plan (a transient GroupPlan, mutated here each tick;
// never persisted -- §7). The coordinator stays static and stateless-at-fleet-
// level: it recomputes from ground truth every tick and mutates group.Plan. Every
// phase gate is a LIVE POLL over member state with a timeout + a liveness escape
// (§3 / §6) -- never a stored boolean (a miscounted flag is what froze the old
// leader, §8). The only thing held across ticks is the LATCHED objective (so the
// focus-fire target does not thrash, §3).
//
// Two seams stamped per present grouped member each tick:
//   • CombatDirective.Assist(anchor)  -- the focus-fire half, already wired end-to-
//     end (BotContext.CombatDirective -> COMBAT_DIRECTIVE -> C++ TeamPlay). UNCHANGED.
//   • GroupOrder (the §3 phase + target NPC + embedded kill objective) -- consumed
//     IN-PROCESS by GoalSelector (route on Phase != None) and QuestPlanner.DriveGroup
//     (branch on Phase). Not a wire command.
//
// v1 boundaries (deliberate, documented; the design defers them): the pool is the
// union of fully-LOCAL quests (giver + turn-in on the anchor's map, and a grindable
// on-map objective -- a creature kill OR a kill-for-loot item whose best drop source is
// an on-map creature -- or an instant-complete quest). GAME-OBJECT-sourced items (herbs
// / chests) and cross-map travel are deferred (matching the solo planner's own drop-
// source phase gate); they are accepted opportunistically for breadth but not driven as
// group objectives. Group-coordinated MAINTENANCE (the §4 whole-group vendor / repair /
// 2-level training errands) is also a follow-on -- see the note in DriveGroup; this
// driver scopes to grouped QUESTING.
// ============================================================================
public static class GroupCoordinator
{
    // ── Tunables ──
    private const int QuestLogCap = 20;               // 1.12 quest-log size (min-headroom sizing, §6)
    private const int QuestStatusComplete = 1;        // VMaNGOS QUEST_STATUS: COMPLETE=1 (INCOMPLETE=3)
    private const int QuestStatusIncomplete = 3;      // union status-merge (BuildVirtualSnapshot): merged reads COMPLETE only if EVERY holder is COMPLETE
    private const int TravelSafetyMargin = 3;         // §5.1: weakest may face up to weakest+3 on a travel leg
    private const float ArrivalReachYards = 15f;      // "the group has arrived at the NPC together" gate
    private const double GateLivenessSec = 90;        // §6 liveness escape: a stuck/away member stops gating after this
    private const int GroupTrainLevelGap = 2;         // §4: every present member must clear TrainBaselineLevel + this before a training round opens

    // ── Group-vendor thresholds (GAP G, 2026-07-02) ── mirror GoalSelector / MaintenancePlanner EXACTLY
    // so the whole-group errand triggers on the same condition the solo peel would have, and the peel
    // suppression in GoalSelector keys on the same numbers (no window where one fires and the other doesn't).
    private const int GroupDurabilityVendorThreshold = 30;   // == GoalSelector.DurabilityVendorThreshold / MaintenancePlanner.DurabilityVendorThreshold
    private const int GroupRepairRequiredBelowDurability = 70;  // == MaintenancePlanner.RepairRequiredBelowDurability — below this, the shared vendor lookup HARD-filters to repair-capable NPCs

    // ── Group-vendor window hardening (2026-07-03, the GroupVendor livelock fix) ──
    // A single member unable to REACH the shared vendor (a genuine navmesh graph disconnection — see
    // the 2026-07-03 session notes) used to hold the WHOLE group in GroupVendor forever: the gate
    // re-derived every tick for as long as anyNeedsVendor stayed true, which it does by construction
    // for a member that can never arrive. GroupVendorWindowCapSec bounds how long the group waits
    // before force-releasing to questing regardless (the member's own BotBrain no-path quarantine
    // and/or its solo MaintenancePlanner backstop
    // still own actually resolving ITS OWN wedge; this cap only stops that from ALSO freezing the
    // team). GroupVendorCooldownSec then holds the window closed so a member whose need hasn't
    // actually cleared doesn't immediately re-open a fresh window the very next tick.
    private const double GroupVendorWindowCapSec = 180;
    private const double GroupVendorCooldownSec = 300;

    // ── Instrumentation (logic-neutral): make the decider SAY which door it took. ──
    // One [GROUP] line per group on a phase CHANGE, plus a ~15s heartbeat while parked in a
    // "stuck" phase (Hold/None/Forming) so a persistent park keeps reporting LIVE gate values.
    // Falls back to Console (→ journald) when no logger is attached, so this is a single-file
    // drop with zero wiring. Set GroupCoordinator.Log to route through ILogger if preferred.
    public static ILogger? Log;
    private static readonly Dictionary<int, DateTime> _lastEmit = new();
    private const double EmitHeartbeatSec = 15;

    private static void Emit(int anchorGuid, GroupPhase prev, GroupPhase now, string detail, List<BotContext> members)
    {
        bool changed = prev != now;
        bool stuck = now == GroupPhase.HoldAtAnchor || now == GroupPhase.None || now == GroupPhase.Forming
                     || now == GroupPhase.GroupGrind    // grinding windows keep the ~15s heartbeat (the countdown lines)
                     || now == GroupPhase.GroupDefend;  // ...and so do defensive stands (corpse/heal guards)
        if (!changed)
        {
            if (!stuck) return;
            if (_lastEmit.TryGetValue(anchorGuid, out var last)
                && (DateTime.UtcNow - last).TotalSeconds < EmitHeartbeatSec) return;
        }
        _lastEmit[anchorGuid] = DateTime.UtcNow;
        var who = string.Join(" ", members.Select(m =>
            $"[{m.Guid}:L{m.Level} hp{(int)(m.HpPct * 100)} dead={m.Dead} prog{(int)m.TimeSinceProgressSec}s]"));
        var line = $"[GROUP] anchor={anchorGuid} {prev}->{now} {detail} | {who}";
        if (Log != null) Log.LogInformation(line);
        else Console.WriteLine(line);
    }

    // Rare, decision-changing events (the Fix-4 exhaust ladder; the Fix-1 virtual deadline) must
    // NEVER be swallowed by the heartbeat throttle or the unchanged-phase gate — a silent ladder is
    // the silent-gap class the 2026-07-04 shakedown flagged (a frozen Objective phase emitted
    // nothing for 10 hours). Bypasses both gates; still stamps _lastEmit so the following heartbeat
    // cadence stays honest.
    private static void EmitForce(int anchorGuid, GroupPhase prev, GroupPhase now, string detail, List<BotContext> members)
    {
        _lastEmit[anchorGuid] = DateTime.UtcNow;
        var who = string.Join(" ", members.Select(m =>
            $"[{m.Guid}:L{m.Level} hp{(int)(m.HpPct * 100)} dead={m.Dead} prog{(int)m.TimeSinceProgressSec}s]"));
        var line = $"[GROUP] anchor={anchorGuid} {prev}->{now} {detail} | {who}";
        if (Log != null) Log.LogInformation(line);
        else Console.WriteLine(line);
    }

    /// <summary>
    /// Stamp every context this tick. Pure side effect on BotContext (CombatDirective +
    /// GroupOrder) and BotGroup.Plan; issues nothing. Reads member state + group membership
    /// + the loaders (read-only).
    /// </summary>
    public static void Update(
        IReadOnlyDictionary<int, BotContext> contexts,
        GroupManager groups,
        QuestGraphLoader quests,
        ZoneSafetyMap safety,
        CreatureSpawnLoader spawns,   // Scatter Build 2: real-spawn anchor sampling for the shared objective
        QuestPlanner questPlanner,    // §Option A (2026-07-01): drives the group's shared decisions through
                                      // the REAL solo decision machinery instead of a hand-rolled parallel one
        ZoneDataLoader zoneData)      // GAP G (2026-07-02): GetNearestVendor for the whole-group vendor errand.
                                      // CALLER NOTE: the only caller is BotBrainService.RunBrainTicksAsync
                                      // (BotBrainService.cs, the GroupCoordinator.Update(...) line) -- it holds
                                      // the ZoneDataLoader singleton as a ctor-injected field and passes it here.
    {
        // Default EVERY bot to None on BOTH seams, then overwrite grouped members below. A bot
        // that left a group this tick (or the whole mode going Off) reverts to solo for combat
        // AND execution: the spine emits one combat mode=none, and GoalSelector falls back to the
        // bot's own planner (GroupOrder.None -> Phase==None -> not routed to the group executor).
        foreach (var ctx in contexts.Values)
        {
            ctx.CombatDirective = CombatDirective.None;
            ctx.GroupOrder = GroupOrder.None;
        }

        // Mode Off disbands all groups, so GetAllGroups() is empty and the None pass is the whole
        // story. The explicit guard makes the off-switch obvious + cheap.
        if (groups.Mode == GroupingMode.Off)
        {
            // Grouping off → drop any coordinator-assigned held objective so each bot reverts fully to
            // solo (its own producers own Held). Self-solo objectives are left untouched. (§6.)
            foreach (var ctx in contexts.Values)
                if (ctx.Held is { Source: ObjectiveSource.Coordinator })
                    ctx.ClearObjective();
            return;
        }

        // Track who got an active group order this tick, so the post-pass can clear stale coordinator-
        // assigned objectives on bots that dropped out of an active group (ungrouped / sub-quorum /
        // graph-not-loaded) without disturbing the grace clock of bots still on the same order.
        var groupedGuids = new HashSet<int>();

        foreach (var group in groups.GetAllGroups())
        {
            // Resolve member guids -> live contexts; skip any without a connected context.
            // [AUTHORSHIP] Also skip bodies a human is driving — a possessed bot, or one of the
            // owner's own characters enrolled as a companion. Counting one into a fleet group
            // would hand it a shared objective and a focus-fire directive it must never receive,
            // and would let it satisfy the >=2 quorum that makes the OTHER members act as a team.
            // The None pass above already reset their two seams, so skipping here leaves them solo.
            var members = new List<BotContext>(group.MemberGuids.Count);
            foreach (var guid in group.MemberGuids)
                if (contexts.TryGetValue(guid, out var ctx) && !ctx.IsPlayerDriven)
                    members.Add(ctx);

            // Need >=2 PRESENT members to act as a team; otherwise leave None (solo).
            if (members.Count < 2)
                continue;

            int anchorGuid = ElectAnchor(members);

            // ── Combat seam (focus-fire): every member assists the anchor's live victim. ──
            var combat = CombatDirective.Assist(anchorGuid);
            foreach (var ctx in members)
                ctx.CombatDirective = combat;

            // ── Execution seam: run the §3 machine, stamp every member the SAME GroupOrder. ──
            // The phase/target/objective are GROUP properties (the per-member differences --
            // which quests THIS bot accepts, whether IT still owes kills -- are read by the
            // executor from the bot's own log). Without the graph we can't drive questing, but
            // combat assist still stands; leave GroupOrder.None so members solo-grind.
            if (!quests.IsLoaded)
                continue;

            var order = DriveGroup(group.Plan, members, anchorGuid, quests, safety, spawns, questPlanner, zoneData);
            foreach (var ctx in members)
            {
                ctx.GroupOrder = order;
                StampHeld(ctx, order);            // mirror the order as the reconcile/observability anchor (§3/§6)
                groupedGuids.Add(ctx.Guid);
            }
        }

        // Clear stale coordinator-assigned objectives on bots no longer in an active group this tick
        // (leaves self-solo objectives — the solo producers own those). A post-pass, NOT the default
        // None pass, so a bot still on the SAME order keeps its grace clock intact (SetObjective only
        // re-stamps the clock on a CHANGE). §6.
        foreach (var ctx in contexts.Values)
            if (!groupedGuids.Contains(ctx.Guid) && ctx.Held is { Source: ObjectiveSource.Coordinator })
                ctx.ClearObjective();
    }

    // Mirror the assigned GroupOrder as the bot's held strategic objective (§3/§6) — the reconcile /
    // observability anchor. Only the MOVING (Travel) and GRINDING (Grind) phases are reconcilable; the
    // at-NPC interact phases (Accept/TurnIn) and the anchor hold are PASSIVE (Hold), never re-issued by
    // the reconcile. SetObjective preserves the grace clock when the order is unchanged.
    private static void StampHeld(BotContext ctx, GroupOrder o)
    {
        switch (o.Phase)
        {
            case GroupPhase.Objective:
                var d = o.Objective;
                ctx.SetObjective(Objective.Grind(ObjectiveSource.Coordinator, d.CreatureEntry,
                    d.X, d.Y, d.Z, d.Map, 0, d.QuestId, d.Slot));   // killCount 0 = indefinite (coordinator gate owns completion)
                break;
            case GroupPhase.GroupDefend:
                // Axiom 2 hardened: passive guard at the point. Objective.Hold is never reconciled
                // (a C++ Idle echo is the CORRECT state here), so the reconcile can't re-issue
                // anything into a defensive stand.
                ctx.SetObjective(Objective.Hold(o.TargetPos));
                break;
            case GroupPhase.GroupGrind:
                // Axiom 1: an indefinite entry-0 grind at the clump point. Reconcilable: echo Grind
                // (any entry) or MoveTo toward the point both match; Idle mismatches and re-issues —
                // the self-heal for a dropped C++ grind, with the Fix-3 streak backoff preventing a
                // metronome against an unreachable point.
                ctx.SetObjective(Objective.Grind(ObjectiveSource.Coordinator, 0,
                    o.TargetPos.X, o.TargetPos.Y, o.TargetPos.Z, o.TargetPos.Map, 0));
                break;
            case GroupPhase.HoldAtAnchor:
                if (o.Objective.IsActive)
                {
                    var h = o.Objective;
                    ctx.SetObjective(Objective.Grind(ObjectiveSource.Coordinator, h.CreatureEntry,
                        h.X, h.Y, h.Z, h.Map, 0, h.QuestId, h.Slot));
                }
                else
                {
                    ctx.SetObjective(Objective.Hold(o.TargetPos));
                }
                break;
            case GroupPhase.GroupTrain:
                // No NPC target (each trainee routes to its OWN class trainer) -- unlike HoldAtAnchor,
                // there's no anchor coord to fall back to when nothing's latched, so a member with no
                // mob to grind gets no committed objective at all (the reconcile has nothing to defend,
                // exactly like Forming/None below).
                if (o.Objective.IsActive)
                {
                    var t = o.Objective;
                    ctx.SetObjective(Objective.Grind(ObjectiveSource.Coordinator, t.CreatureEntry,
                        t.X, t.Y, t.Z, t.Map, 0, t.QuestId, t.Slot));
                }
                else
                {
                    ctx.ClearObjective();
                }
                break;
            case GroupPhase.TravelToGiver:
            case GroupPhase.TravelToTurnIn:
                ctx.SetObjective(Objective.Travel(ObjectiveSource.Coordinator,
                    o.TargetPos.X, o.TargetPos.Y, o.TargetPos.Z, o.TargetPos.Map, o.TargetNpcEntry));
                break;
            case GroupPhase.Accept:
            case GroupPhase.TurnIn:
                ctx.SetObjective(Objective.Hold(o.TargetPos));   // at the NPC interacting — passive, not reconciled
                break;
            default:
                ctx.ClearObjective();   // Forming (transient) / None / unhandled → no committed objective this tick
                break;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // The phase machine. Returns the single GroupOrder to stamp on every member.
    // ────────────────────────────────────────────────────────────────────────
    private static GroupOrder DriveGroup(
        GroupPlan plan, List<BotContext> members, int anchorGuid,
        QuestGraphLoader quests, ZoneSafetyMap safety, CreatureSpawnLoader spawns, QuestPlanner questPlanner,
        ZoneDataLoader zoneData)
    {
        var anchor = AnchorOf(members, anchorGuid);
        var prevPhase = plan.Phase;   // instrumentation: phase BEFORE this tick's decision

        // Round 5 (2026-07-04): death-episode bookkeeping + the meat-grinder trigger. Runs every
        // tick, before any branch, so deaths are counted no matter which phase they land in.
        TrackDeaths(plan, members, anchor, anchorGuid, prevPhase);

        // ── Peel preemption (§4) ──
        // A peeled (recovering) member: the REST hold on the same target at the anchor. The
        // recovering member's own GoalSelector routes it to Maintenance/Training regardless of
        // this stamp (recovery/upkeep-first is non-negotiable, §4) -- so stamping HoldAtAnchor on
        // all is correct. AnyRecovering catches death, a vendor/repair errand, AND a training trip
        // (§4) -- any of the three means this member is off running its OWN planner right now.
        if (AnyRecovering(members))
        {
            var peeled = string.Join(",", members
                .Where(m => m.Dead || m.Goal == Goal.Maintenance || m.Goal == Goal.Training)
                .Select(m => $"{m.Guid}({(m.Dead ? $"dead,hp{(int)(m.HpPct * 100)}" : m.Goal.ToString().ToLowerInvariant())})"));

            // Axiom 2 (Nico, 2026-07-04), HARDENED after round 5: a death NEVER splits the group —
            // AND the converge is DEFENSIVE. The clump point becomes the DEAD MEMBER'S live position
            // (the corpse while it lies there; the graveyard the moment a ghost/GY rez moves it) and
            // everyone converges there and stands GUARD: SET_TASK IDLE, fight only what aggros, pull
            // NOTHING. The first cut of this protocol ordered a GRIND at the corpse — converging the
            // team into the camp that just produced a corpse and actively pulling it was a wipe
            // engine (291 deaths at a flat ~80/hr, 33-45% of re-deaths inside 90s of the rez; every
            // top corpse coordinate a dense L8-10 camp). Position rounded to 5yd so a static corpse
            // produces a stable (change-guard-friendly) order; a GY jump changes it once,
            // deliberately, and the whole team re-paths to the member.
            var deadMember = members.FirstOrDefault(m => m.Dead);
            if (deadMember != null)
            {
                var cp = new Vec4(
                    MathF.Round(deadMember.Pos.X / 5f) * 5f,
                    MathF.Round(deadMember.Pos.Y / 5f) * 5f,
                    deadMember.Pos.Z, deadMember.MapId);
                plan.GrindPoint = cp;
                plan.HasGrindPoint = true;
                plan.SetPhase(GroupPhase.GroupDefend);
                Emit(anchorGuid, prevPhase, GroupPhase.GroupDefend,
                    $"DOOR1 defend corpse member={deadMember.Guid} @({cp.X:F0},{cp.Y:F0}) recovering={peeled}", members);
                return GroupOrder.DefendAt(anchorGuid, cp);
            }

            // A member is ALIVE but healing off a rez (50% HP, eating): keep standing GUARD at the
            // memoized point (it persists across the dead->healing flip, so this is the same spot
            // the corpse lay). Grinding here would pull the camp onto the eater — the exact loop
            // the defend phase exists to break.
            var healingMember = members.FirstOrDefault(m =>
                !m.Dead && m.Maintenance is { RezSent: true, HealDone: false });
            if (healingMember != null)
            {
                Vec4 hp;
                if (plan.HasGrindPoint
                    && (plan.Phase == GroupPhase.GroupDefend || plan.Phase == GroupPhase.GroupGrind))
                {
                    hp = plan.GrindPoint;
                }
                else
                {
                    hp = new Vec4(
                        MathF.Round(healingMember.Pos.X / 5f) * 5f,
                        MathF.Round(healingMember.Pos.Y / 5f) * 5f,
                        healingMember.Pos.Z, healingMember.MapId);
                }
                plan.GrindPoint = hp;
                plan.HasGrindPoint = true;
                plan.SetPhase(GroupPhase.GroupDefend);
                Emit(anchorGuid, prevPhase, GroupPhase.GroupDefend,
                    $"DOOR1 guard heal member={healingMember.Guid} @({hp.X:F0},{hp.Y:F0}) recovering={peeled}", members);
                return GroupOrder.DefendAt(anchorGuid, hp);
            }

            // Alive ERRAND peel (vendor/training trip): the waiters grind at the anchor (Axiom 1) —
            // errand ground is the group's own held position, not a hostile camp.
            var gp = EnsureGrindPoint(plan, anchor);
            Emit(anchorGuid, prevPhase, GroupPhase.GroupGrind, $"DOOR1 peel recovering={peeled} -> grind@({gp.X:F0},{gp.Y:F0})", members);
            plan.SetPhase(GroupPhase.GroupGrind);
            return GroupOrder.GrindAt(anchorGuid, gp);
        }

        // ── Meat-grinder retreat (round 5) ──
        // Armed by TrackDeaths when the death window fills; STARTED only here — the first tick the
        // party is whole again (a corpse is never abandoned; the defend protocol above owns the
        // group until everyone is up and healed). The retreat is an Axiom-1 grind at the last
        // proven-safe ground for a fixed hold, and the active quest that kept sending the group
        // into the camp was already queued for a shelve (PendingGrinderDefer -> the virtual layer's
        // Recover) — so when normal planning resumes after the hold, it derives DIFFERENT work
        // instead of walking back in. No safe point captured yet (a group that has never had a
        // calm window) -> the retreat leg is skipped and only the shelve applies.
        if (plan.RetreatPending)
        {
            plan.RetreatPending = false;
            if (plan.HasLastSafePoint)
            {
                plan.RetreatUntil = DateTime.UtcNow.AddSeconds(RetreatHoldSec);
                EmitForce(anchorGuid, prevPhase, GroupPhase.GroupGrind,
                    $"MEAT-GRINDER retreat begins -> ({plan.LastSafePoint.X:F0},{plan.LastSafePoint.Y:F0}) for {RetreatHoldSec}s", members);
            }
        }
        if (plan.RetreatUntil is DateTime ru)
        {
            if (DateTime.UtcNow < ru && plan.HasLastSafePoint)
            {
                plan.GrindPoint = plan.LastSafePoint;
                plan.HasGrindPoint = true;
                plan.SetPhase(GroupPhase.GroupGrind);
                Emit(anchorGuid, prevPhase, GroupPhase.GroupGrind,
                    $"retreat {(int)(ru - DateTime.UtcNow).TotalSeconds}s -> regroup @({plan.LastSafePoint.X:F0},{plan.LastSafePoint.Y:F0})", members);
                return GroupOrder.GrindAt(anchorGuid, plan.LastSafePoint);
            }
            plan.RetreatUntil = null;   // hold elapsed — resume normal planning below
        }

        // ── Group-gated training window (§4) ──
        // "Every 2 levels, together" -- not the per-bot spawn reflex the individual trigger normally
        // is. Reached only when AnyRecovering is false, i.e. nobody is CURRENTLY mid-trip -- so this
        // only meaningfully fires on the tick a round OPENS (the first trainee's very next tick flips
        // Goal.Training, which flips AnyRecovering true and this block is skipped on every subsequent
        // tick until the whole party is back).
        //
        // Lazy-seed the baseline the FIRST time this group is ever evaluated (0 = genuinely never
        // seeded -- real levels start at 1, so 0 is a safe "unset" sentinel, and it only reads 0 until
        // the first round below sets it for real). Without this a fresh L1 party would ding to L2
        // together and read "everyone already clears baseline(0)+2" on its very FIRST level-up -- the
        // exact per-bot-spawn-reflex bum-rush this phase exists to prevent, just moved from "on
        // connect" to "on first ding." Seeding to the CURRENT min level means the clock always starts
        // from wherever the party actually is, whether that's a fresh L1 spawn (next round needs L3)
        // or an already-leveled group squadded up for the first time (next round needs current+2, not
        // an immediate trip on tick one). This composes for free with the L1 case specifically: a
        // seeded baseline is always >= 1, so the gate (baseline+2) can never be satisfied by a L1
        // member -- no separate "skip at level 1" special-case needed.
        if (plan.TrainBaselineLevel == 0)
            plan.TrainBaselineLevel = members.Min(m => m.Level);

        // Every present member must have cleared TrainBaselineLevel + GroupTrainLevelGap; if nobody
        // actually owes a visit (HasUnlearnedSpells) the level bar is met for nothing to do, so just
        // advance the baseline without forcing a trip.
        if (members.All(m => m.Level >= plan.TrainBaselineLevel + GroupTrainLevelGap))
        {
            if (members.Any(m => m.Identity is { HasUnlearnedSpells: true }))
            {
                plan.TrainBaselineLevel = members.Min(m => m.Level);   // lock the floor for this round
                plan.SetPhase(GroupPhase.GroupTrain);
                Emit(anchorGuid, prevPhase, GroupPhase.GroupTrain,
                    $"train-window open baseline={plan.TrainBaselineLevel} minLvl={members.Min(m => m.Level)}", members);
                return GroupOrder.Train(anchorGuid, plan.LatchedObjective);
            }
            // Level bar cleared but nobody has anything new to learn (e.g. no rank at this bracket
            // for any present class) -- reset the cadence clock so we don't re-derive this every tick,
            // but don't force a pointless trainer trip.
            plan.TrainBaselineLevel = members.Min(m => m.Level);
        }

        // ── Group-gated vendor / repair errand (§4, GAP G 2026-07-02) ──
        // "Maintenance is NEVER a solo peel" (Nico's rule: only training splits the group). A member
        // whose durability craters or bags fill would, solo, peel to MaintenancePlanner and vendor ALONE
        // while the rest HoldAtAnchor -- a split the rule forbids. Instead, when ANY present member needs
        // a vendor, route the WHOLE group to one shared vendor together (unlike training, where classes
        // scatter to different trainers). Structured exactly like the group-train gate above: reached only
        // when AnyRecovering is false (nobody already mid-trip), so it meaningfully fires on the tick the
        // round OPENS; once a member flips into the errand the peel-hold / liveness machinery carries the
        // rest. The solo durability peel in GoalSelector is SUPPRESSED for a grouped member whenever this
        // phase is STAMPED this tick (GoalSelector GAP G change keys on ctx.GroupOrder.Phase), so a member
        // can't bolt solo before the group errand forms -- but stays free to peel as a backstop on a tick
        // where no vendor was reachable and the phase was NOT stamped (see the null-vendor fall-through).
        //
        // requireRepair: if ANY member is below the repair-durability floor, hard-filter the shared lookup
        // to a repair-capable NPC (an armorer) -- a sell-only vendor wouldn't fix the gear that triggered
        // this. A null vendor (none in range, or none repair-capable when required) is a clean FALL-THROUGH
        // to questing, NOT a freeze: the member's own solo MaintenancePlanner backstop still owns actually
        // resolving an unreachable-vendor wedge; the group just doesn't gate on it. Same GateLivenessSec
        // escape as every other gate keeps one stuck member from freezing the errand forever.
        //
        // 2026-07-03 hardening (the GroupVendor livelock): two problems, fixed together.
        // (a) The lookup used to re-run EVERY TICK for as long as anyNeedsVendor stayed true — ~4/sec
        //     in the live capture that diagnosed this — and re-logged every time. The vendor is now
        //     MEMOIZED on plan.VendorNpcEntry/VendorPos the first tick the window opens, and re-stamped
        //     from the memo on every subsequent tick: one lookup per errand, not one per tick.
        // (b) Nothing stopped the group re-deriving the SAME GroupVendor phase forever if the chosen
        //     vendor happened to be unreachable for one member. plan.TimeInPhaseSec (already tracked
        //     for every phase) is now checked against GroupVendorWindowCapSec; past the cap the window
        //     force-closes and the group falls through to questing regardless of anyNeedsVendor, then
        //     cools down (GroupVendorCooldownSec) so a member whose need hasn't actually cleared can't
        //     re-open a fresh window on the very next tick — the same amnesiac-retry shape the cap
        //     exists to break.
        bool anyNeedsVendor = members.Any(m => m.Durability < GroupDurabilityVendorThreshold || m.FreeSlots <= 0);
        bool vendorOnCooldown = plan.VendorWindowCooldownUntil is DateTime cd && DateTime.UtcNow < cd;

        if (prevPhase == GroupPhase.GroupVendor && plan.VendorNpcEntry != 0)
        {
            if (anyNeedsVendor && plan.TimeInPhaseSec < GroupVendorWindowCapSec)
            {
                // Re-stamp from the memo -- no re-lookup, no re-log.
                return GroupOrder.ToNpc(GroupPhase.GroupVendor, anchorGuid, plan.VendorNpcEntry, plan.VendorPos);
            }

            if (anyNeedsVendor)
            {
                // Wall-clock cap tripped -- the group must never be hostage to one member's errand.
                plan.VendorWindowCooldownUntil = DateTime.UtcNow.AddSeconds(GroupVendorCooldownSec);
                Emit(anchorGuid, prevPhase, prevPhase,
                    $"vendor-window CAPPED at {plan.TimeInPhaseSec:F0}s (npc={plan.VendorNpcEntry}) -> releasing to questing for {GroupVendorCooldownSec:F0}s; member's own escalation/backstop owns the rest",
                    members);
            }
            // Either the need genuinely cleared (errand done) or the cap tripped -- either way this
            // window is over. Clear the memo so a future window re-derives fresh.
            plan.ClearVendorTarget();
        }

        if (anyNeedsVendor && !vendorOnCooldown)
        {
            bool anyNeedsRepair = members.Any(m => m.Durability < GroupRepairRequiredBelowDurability);
            var vendor = zoneData.GetNearestVendor(anchor.ZoneId, anchor.MapId, anchor.Pos.X, anchor.Pos.Y,
                                                   members.Min(m => m.Level), anyNeedsRepair);
            if (vendor != null)
            {
                var vpos = new Vec4(vendor.X, vendor.Y, vendor.Z, vendor.MapId);
                plan.SetVendorTarget(vendor.NpcEntry, vpos);   // memoize -- one lookup for the whole window
                plan.SetPhase(GroupPhase.GroupVendor);
                Emit(anchorGuid, prevPhase, GroupPhase.GroupVendor,
                    $"vendor-window open npc={vendor.NpcEntry} \"{vendor.NpcName}\" repair={(vendor.CanRepair ? "Y" : "N")} needRepair={anyNeedsRepair}", members);
                return GroupOrder.ToNpc(GroupPhase.GroupVendor, anchorGuid, vendor.NpcEntry, vpos);
            }
            // No suitable vendor reachable from the anchor -> don't stamp the phase (would strand the group
            // walking nowhere). Fall through to questing; a genuinely broke-gear member still has its own
            // solo MaintenancePlanner backstop. That backstop stays available precisely BECAUSE the phase
            // isn't stamped: GoalSelector suppresses the solo peel only when it sees GroupPhase.GroupVendor
            // on ctx.GroupOrder this tick, so an unstamped (unreachable-vendor) tick leaves the solo peel
            // free to fire -- no need for GoalSelector to re-run the vendor lookup itself.
            Emit(anchorGuid, prevPhase, prevPhase,
                $"vendor needed but none in range (needRepair={anyNeedsRepair}) -> questing; solo backstop owns it", members);
        }

        return DriveGroupViaVirtual(plan, members, anchorGuid, anchor, quests, safety, questPlanner, prevPhase);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    // §Option A (2026-07-01) — the virtual member.
    //
    // Replaces the old hand-rolled Forming/GatherLocalPool/NextGiver/NextObjective/NextEnder/
    // StillOwed/ComputeAvail chain (all removed). Every one of those was GroupCoordinator's OWN
    // re-derivation of a fragment of what QuestPlanner's solo machinery already does correctly --
    // PriorityLeg's started>level>band>dist ordering, TagOutliers' red-deprioritize, GatherLocals'
    // R40 co-located-follow-up drain, the turn-in-yield check, overflow-grind, the grind-lock
    // invariant -- and every one of those fragments has independently drifted from solo at some
    // point this session (that's the actual root cause behind every "group behaves differently
    // from solo" symptom found so far). The fix: stop re-deriving. Drive a persistent SYNTHETIC
    // BotContext (GroupPlan.Virtual) through QuestPlanner.PlanNext directly -- the exact same
    // Derive/BuildBatch/GatherLocals/PriorityLeg/TagOutliers/Recover a real solo bot runs, zero
    // reimplementation, zero drift possible by construction.
    //
    // The virtual bot never has a real bridge connection. Each tick its sensory state (Pos/
    // QuestLog/Level) is refreshed from the UNION of present real members (RefreshVirtualSensory,
    // via ctx.Sense -- reused unchanged), and its durable exclusion state (CompletedQuestIds/
    // DeferredQuestIds/AbandonedGreyQuestIds/PathBlacklist) is unioned from every present member's
    // own BotIdentity ("defer for all", 2026-07-01: any ONE member's exclusion applies group-wide --
    // the conservative default). GroupOrder is NEVER set on the virtual ctx, which is what keeps
    // QuestPlanner.PlanNext routing it through the solo decision path instead of recursing back into
    // this file's own DriveGroup.
    //
    // The ONE genuinely new piece of logic -- because it has no solo analog -- is eligibility: which
    // NEW quests are offerable to AT LEAST ONE present member (GatherLocals is hardcoded to one
    // ctx.Identity and can't see this on its own), and which REAL members still owe a specific
    // accept/turn-in/kill before the virtual bot's WAIT is allowed to resolve. That's exactly the
    // "checked during quest accepts and eligibility for which group members are actually able" carve-
    // out. Everything else -- what to accept, what order to work it in, when to abandon a grey quest,
    // when to overflow-grind a stale count, when to grind-lock -- is the real QuestPlanner deciding,
    // not this file.
    //
    // A StepResult.Issue never becomes a real bridge send for the virtual bot: BuildGroupOrderFromVirtual
    // translates it into the SAME GroupOrder stamps this coordinator already produced before (ToNpc /
    // Engage / Hold) -- real per-member eligibility is then checked exactly where it always was,
    // untouched, in QuestPlanner's own GroupAccept/GroupTurnIn/GroupObjective/GroupHold executor.
    // ════════════════════════════════════════════════════════════════════════════════════════

    private const int GroupInjectCap = 8;          // mirrors QuestPlanner.BatchCap -- ceiling on how many freshly-eligible quests RefreshVirtualEligibility injects per tick
    private const float GroupInjectRadiusYards = 300f;   // loose locality gate for injection only; PriorityLeg (inside Derive) does the real near/far ordering once a candidate is in the batch
    private const float GroupInjectRadiusWideYards = 1500f;   // Fix 4 rung 4: after 4+ identical exhaust cycles, reach for the next hub (the solo ReachTier analog)

    // ── Cluster-aware path gate (2026-07-04 rounds 4/5) ──
    // The old gate vetoed a corridor on its single highest creature level — maximally sensitive
    // to exactly the noise Nico called out (patrolling mobs; a lone rare a bot simply paths
    // around). Round 4 measured it: Thuros Lightfingers L11, ONE spawn, vetoing the entire
    // kobold-cave corridor for any group with weakest under 8, while solo bots (which never run
    // this gate) cleared the same content freely. The gate now vetoes on CLUSTERS: 1-2 over-band
    // stragglers pass; a camp in one cell, a blanketed corridor, or paired deep-reds veto.
    private const int PathClusterCellVeto = 3;    // over-band spawns in ONE 100yd cell = a camp on the line
    private const int PathClusterTotalVeto = 5;   // over-band spawns across the corridor = blanketed even if spread
    private const int PathDeepPairVeto = 2;       // >= this many spawns 3+ OVER the band in one cell = lethal pocket
    private const float GroupGuardYards = 30f;    // a living groupmate this close to a corpse = the rez is guarded

    // ── Meat-grinder breaker (2026-07-04 round 5) ──
    private const int MeatGrinderDeaths = 3;         // this many alive->dead transitions...
    private const int MeatGrinderWindowSec = 300;    // ...inside this window = the camp is winning
    private const int SafePointCalmSec = 300;        // party whole + death-free this long -> current spot is proven-safe ground
    private const int RetreatHoldSec = 120;          // how long the retreat grind holds at the safe point before normal planning resumes

    private static GroupOrder DriveGroupViaVirtual(
        GroupPlan plan, List<BotContext> members, int anchorGuid, BotContext anchor,
        QuestGraphLoader quests, ZoneSafetyMap safety, QuestPlanner questPlanner, GroupPhase prevPhase)
    {
        var vctx = GetOrCreateVirtual(plan);
        var vsnap = BuildVirtualSnapshot(anchor, members);
        vctx.Sense(vsnap);                                   // Pos/MapId/ZoneId/Level/QuestLog -- exactly what Derive reads
        RefreshVirtualEligibility(vctx, members, quests, plan);    // union exclusions + inject newly-eligible-for-someone content

        // Meat-grinder shelve (round 5): the breaker also removes the quest that kept sending the
        // group into the camp. Synthesized as a normal WaitFailure so the real Recover machinery
        // shelves it — durable, escalating, logged ("shelving [id] (meat_grinder ...)"), expiring —
        // instead of a bespoke side-channel defer. No active quest (deaths during a pure grind
        // window) -> nothing to shelve; the retreat leg alone applies.
        if (plan.PendingGrinderDefer)
        {
            plan.PendingGrinderDefer = false;
            if (vctx.Quest?.Active != null)
            {
                vctx.Pending = null;
                vctx.Failure = new WaitFailure { CommandType = "MOVE_TO", Reason = "meat_grinder", Utc = DateTime.UtcNow };
            }
        }

        // GAP F fix (2026-07-02): consume the virtual grind-lock. When the virtual bot's own Derive hit
        // a deferral-driven batch exhaust on a PRIOR tick, it stamped GrindLockUntil on the virtual
        // identity and returned Block -- but nothing read that clock (only real members read their OWN
        // GrindLockUntil in GoalSelector), so it was set-then-ignored: the group fell to solo grind AND
        // re-ran the entire virtual Derive (batch build / gather / exhaust) every tick just to land on
        // Block again -- the quest-grind oscillation this lock exists to prevent, now at the group level.
        // While the lock is active, short-circuit BEFORE PlanNext: skip the expensive re-derive for the
        // window. The union-workability guard that PREVENTS a wrongful lock still runs where it matters:
        // it's inside the virtual Derive that SETS the lock (WorkableInLog reads the union QuestLog), so
        // the lock never gets stamped while any member has workable in-log content in the first place --
        // this consumer only honors a lock that guard already permitted.
        //
        // 2026-07-04 (Axiom 1): the lock window IS now the "grind together" form this comment once
        // deferred -- the old wire fear conflated the WAITed SET_TASK shape (where kill_count=0
        // insta-completes the WAIT) with the no-WAIT Dispatch shape solo GrindPlanner has shipped daily
        // (394 entry=0 grinds in the 2026-07-04 Server.log tail alone). The prior alternative -- holding
        // the LATCHED single mob at its verbatim spawn coordinate -- camped group B on Garrick's empty
        // one-spawn hill all night: zero XP, so the weakest-ding release below could never fire and the
        // level-keyed defers could never expire (the four-lock deadlock in the shakedown, D3/D4).
        // ── Fix 4 (2026-07-04): the exhaust escape ladder. Evaluated ONCE per lock, on the tick a
        // fresh lock is first observed (GrindLockWeakestLevel still unstamped). Fingerprint the
        // deferred set; an identical set to the previous lock means the 20-minute window changed
        // nothing — the amnesiac clockwork that ran 30+ identical cycles on 2026-07-04. Escalate:
        // cycle 2 force-expires TIME defers; cycle 3 also LEVEL defers (their stamping condition is
        // a straight-line danger read from a position the group has since left — stale by
        // definition, and their release key (the weakest leveling) is exactly what a bad lock
        // prevents); cycle 4+ additionally widens the injection radius so acquisition can reach the
        // next hub. Any expiry also DROPS the lock so the re-derive happens NOW, not in 20 minutes.
        if (vctx.Identity?.GrindLockUntil is DateTime vglLadder && DateTime.UtcNow < vglLadder
            && plan.GrindLockWeakestLevel == 0)
        {
            string fp = string.Join(",", vctx.Identity.DeferredQuestIds.Keys.OrderBy(k => k));
            if (fp.Length > 0 && fp == plan.LastExhaustSet) plan.ExhaustCycles++;
            else { plan.ExhaustCycles = 1; plan.LastExhaustSet = fp; }

            if (plan.ExhaustCycles >= 2)
            {
                bool expireLevel = plan.ExhaustCycles >= 3;
                int cleared = ForceExpireDeferrals(vctx, members, expireLevel);
                string widened = plan.ExhaustCycles >= 4 ? " + widened pick radius" : "";
                if (cleared > 0)
                {
                    vctx.Identity.GrindLockUntil = null;   // re-derive NOW with the defers cleared
                    EmitForce(anchorGuid, prevPhase, prevPhase,
                        $"exhaust cycle={plan.ExhaustCycles} unchanged set=[{fp}] -> force-expired {cleared} defer(s){(expireLevel ? " incl level-gated" : "")}{widened} -- re-deriving now", members);
                }
                else
                {
                    EmitForce(anchorGuid, prevPhase, prevPhase,
                        $"exhaust cycle={plan.ExhaustCycles} unchanged set=[{fp}] -> nothing expirable{widened}", members);
                }
            }
        }

        if (vctx.Identity?.GrindLockUntil is DateTime vgl && DateTime.UtcNow < vgl)
        {
            // BREAK THE LOCK ON A WEAKEST LEVEL-UP (2026-07-03): a path_unsafe grind-lock waits ONLY on the
            // weakest member's level -- the shelved quests are LEVEL-deferred (requiredLevel = danger -
            // margin), and RefreshVirtualEligibility above has already pruned any the current weakest clears.
            // So a weakest-member ding is exactly the signal the block has (or may have) lifted. If the
            // weakest has risen since this lock began, DROP the lock and fall through to the real Derive to
            // re-decide -- it re-checks path safety at the new level and either resumes the now-safe quest or
            // simply re-locks. Without this the group burned the whole fixed window (~13 min live) after the
            // block had already cleared. (Solo grind-lock deliberately does NOT clear on level-up -- "earn the
            // hour of XP" -- but that's the genuinely-nothing-to-do case; a GROUP path_unsafe lock is gated on
            // a level a ding resolves, a different animal.)
            if (plan.GrindLockWeakestLevel != 0 && vctx.Level > plan.GrindLockWeakestLevel)
            {
                vctx.Identity.GrindLockUntil = null;
                plan.GrindLockWeakestLevel = 0;
                // fall through -- the Pending resolve + PlanNext below re-derive at the new weakest level.
            }
            else
            {
                if (plan.GrindLockWeakestLevel == 0)
                    plan.GrindLockWeakestLevel = vctx.Level;   // stamp the weakest level this lock began at

                // NEVER solo-grind in a group (Nico, 2026-07-03) — and NEVER latch a single mob for
                // the window (Nico, 2026-07-04 / Axiom 1: the old latched hold camped Garrick's empty
                // one-spawn hill all night — zero XP, so the weakest-ding release could never fire).
                // The lock window is now a real GroupGrind: everyone clumps at the memoized point and
                // grinds nearby level-appropriate mobs (entry=0, C++ scan), focus-fire on. XP flows,
                // the weakest actually levels, and both release keys (the ding above, the wall clock)
                // become reachable.
                var lockPoint = EnsureGrindPoint(plan, anchor);
                Emit(anchorGuid, prevPhase, GroupPhase.GroupGrind,
                    $"virtual: grind-lock {(int)Math.Ceiling((vgl - DateTime.UtcNow).TotalMinutes)}m -> grind together @({lockPoint.X:F0},{lockPoint.Y:F0})", members);
                plan.SetPhase(GroupPhase.GroupGrind);
                return GroupOrder.GrindAt(anchorGuid, lockPoint);
            }
        }
        else
        {
            plan.GrindLockWeakestLevel = 0;   // no lock active -> reset so the next lock captures fresh
        }

        // Resolve any in-flight virtual WAIT against REAL group state first. Still outstanding AND
        // within its deadline -> re-stamp the SAME order (idempotent; real members' own
        // LastGroupOrder change-guard no-ops on an unchanged stamp) and stop here without asking
        // Derive for a fresh decision this tick.
        //
        // Fix 1 (2026-07-04): the deadline check MUST run on the UNRESOLVED path. The old shape
        // returned early on "still owed" and only checked Expired after a successful resolve — but
        // every successful resolve already nulls Pending inside the resolver, so the "universal
        // deadline backstop" was unreachable under every input. Consequence: a WAIT whose union
        // gate is UNSATISFIABLE (5624's kill-a-friendly objective; a single-drop item with a
        // detached holder) was IMMORTAL, and while it stood, PlanNext never ran again — no
        // re-derive, no defer expiry, no exhaust, no turn-ins of OTHER quests. Group A froze
        // behind exactly this for 10+ hours on 2026-07-04. Now: resolve wins if it can; an
        // unresolved-but-expired WAIT becomes a deadline Failure the real Recover shelves
        // (DeferAcceptedQuest — durable, carried); only an unresolved, unexpired WAIT re-stamps.
        if (vctx.Pending != null && !TryResolveVirtualWait(plan, vctx, members, quests))
        {
            if (vctx.Pending is { Expired: true })
            {
                vctx.Failure ??= new WaitFailure { CommandType = vctx.Pending.CommandType, Reason = "deadline", Utc = DateTime.UtcNow };
                vctx.Pending = null;
                EmitForce(anchorGuid, prevPhase, prevPhase,
                    $"virtual: WAIT {vctx.Failure.CommandType} deadline (union gate never satisfied) -> Recover shelves it", members);
                // fall through to PlanNext — Recover consumes the Failure this same tick.
            }
            else
            {
                return BuildGroupOrderFromVirtual(plan, vctx, anchor, anchorGuid, members, safety, prevPhase);
            }
        }

        var step = questPlanner.PlanNext(vctx, vsnap);

        switch (step)
        {
            case StepResult.Issue issue:
                ArmVirtualPending(plan, vctx, issue.Command, issue.ExpectedEvent, issue.Deadline);
                break;
            case StepResult.Blocked:
            case StepResult.Done:
                // No workable SHARED quest this tick -> the group stays TOGETHER (Nico, 2026-07-03: a
                // group NEVER splits to solo-grind) and, per Axiom 1 (2026-07-04), it GRINDS — nearby
                // level-appropriate mobs at the clump point, never a latched single mob and never an
                // idle clump. When a union quest becomes workable again (a defer expires, the weakest
                // levels, the Fix-4 ladder force-expires), the next tick re-derives it.
                var idlePoint = EnsureGrindPoint(plan, anchor);
                Emit(anchorGuid, prevPhase, GroupPhase.GroupGrind, $"virtual: no shared quest -> grind together @({idlePoint.X:F0},{idlePoint.Y:F0})", members);
                plan.SetPhase(GroupPhase.GroupGrind);
                return GroupOrder.GrindAt(anchorGuid, idlePoint);
                // Dispatch (fire-and-forget, e.g. ABANDON_QUEST grey-drop) and Continue: nothing to arm;
                // fall through and let BuildGroupOrderFromVirtual read whatever vctx.Step/Quest.Active
                // Derive left behind (a grey-drop mutates the batch/Identity directly, no group translation
                // needed -- open item, see the note at ABANDON_QUEST below).
        }

        return BuildGroupOrderFromVirtual(plan, vctx, anchor, anchorGuid, members, safety, prevPhase);
    }

    // Lazily create the persistent virtual member. Deliberately NOT reset by anything in this file --
    // its accrued state (deferrals, overflow-grind attempts, the in-flight leg) is exactly as durable
    // as a real bot's own BotIdentity/QuestScratch.
    private static BotContext GetOrCreateVirtual(GroupPlan plan)
    {
        if (plan.Virtual == null)
        {
            var vctx = new BotContext { Guid = -1, Name = "<virtual>" };
            vctx.Identity = new BotIdentity { Guid = -1, Name = "<virtual>" };
            plan.Virtual = vctx;
            // GroupOrder is left at its default None forever -- that is what routes
            // QuestPlanner.PlanNext through the solo path instead of back into DriveGroup.
        }
        return plan.Virtual;
    }

    // Sensory snapshot for the virtual bot: Pos = anchor (the group's reference point, same as every
    // other reach/safety gate in this file already uses), Level = WEAKEST present member (so
    // grey/red/reach checks protect the low member, matching PathSafeForWeakest's existing bias),
    // QuestLog = union of every present member's own log (a quest is "in the log" if ANY holder has
    // it; per-slot MobCounts/ItemCounts = MIN across holders, Status = COMPLETE only if EVERY holder is
    // COMPLETE -- the UNION OF NEEDS: the group is only as done as its least-progressed owing member, so
    // Derive never advances turn-in ahead of anyone). Health/mana always full and
    // never dead -- the virtual bot itself is never the reason a leg stalls; real member health is
    // MaintenancePlanner's job via the normal AnyRecovering peel above.
    private static BotStateSnapshot BuildVirtualSnapshot(BotContext anchor, List<BotContext> members)
    {
        var merged = new Dictionary<int, QuestLogEntry>();
        foreach (var m in members)
        {
            foreach (var kv in m.QuestLog)
            {
                if (!merged.TryGetValue(kv.Key, out var e))
                {
                    merged[kv.Key] = new QuestLogEntry
                    {
                        Status = kv.Value.Status,
                        MobCounts = (int[])kv.Value.MobCounts.Clone(),
                        ItemCounts = (int[])kv.Value.ItemCounts.Clone()
                    };
                }
                else
                {
                    // UNION OF NEEDS (2026-07-03): per-slot progress is the MINIMUM across holders --
                    // the group's objective is only as done as its LEAST-progressed owing member. MAX
                    // (the old read) let the FASTEST looter's count stand in for the whole group, so Derive
                    // advanced to turn-in the moment ONE member finished and stranded the rest (the
                    // premature-advance / "group moved on before everyone collected" bug). Status is
                    // recomputed as an all-holders-complete union in the post-pass below.
                    for (int i = 0; i < 4 && i < e.MobCounts.Length && i < kv.Value.MobCounts.Length; i++)
                        e.MobCounts[i] = Math.Min(e.MobCounts[i], kv.Value.MobCounts[i]);
                    for (int i = 0; i < 4 && i < e.ItemCounts.Length && i < kv.Value.ItemCounts.Length; i++)
                        e.ItemCounts[i] = Math.Min(e.ItemCounts[i], kv.Value.ItemCounts[i]);
                }
            }
        }
        // Status merge = UNION OF NEEDS (2026-07-03): the group's quest reads COMPLETE only when EVERY
        // holder reads COMPLETE. A single holder still INCOMPLETE keeps the merged status INCOMPLETE, so
        // the virtual bot's Derive keeps the quest in-work and never advances to turn-in ahead of the
        // slowest owing member. Status-level companion to the per-slot MIN counts above. (The old rule --
        // COMPLETE if ANY holder complete -- was the OR that stranded slow members.)
        foreach (var kv in merged)
        {
            bool allComplete = true;
            foreach (var m in members)
                if (m.QuestLog.TryGetValue(kv.Key, out var he) && he.Status != QuestStatusComplete)
                {
                    allComplete = false;
                    break;
                }
            kv.Value.Status = allComplete ? QuestStatusComplete : QuestStatusIncomplete;
        }

        int weakestLevel = members.Min(m => m.Level);
        return new BotStateSnapshot
        {
            Health = 100,
            MaxHealth = 100,
            Mana = 100,
            MaxMana = 100,
            Level = weakestLevel,
            MapId = anchor.MapId,
            ZoneId = anchor.ZoneId,
            X = anchor.Pos.X,
            Y = anchor.Pos.Y,
            Z = anchor.Pos.Z,
            InCombat = false,
            IsDead = false,
            FreeSlots = (uint)Math.Max(0, members.Min(m => QuestLogCap - m.QuestLog.Count)),
            TotalSlots = (uint)QuestLogCap,
            QuestLog = merged,
            StateUtc = DateTime.UtcNow
        };
    }

    // Union the durable exclusion state ("defer for all") and inject newly-eligible-for-someone
    // content. This is the one place group-specific eligibility logic belongs (§ above).
    private static void RefreshVirtualEligibility(BotContext vctx, List<BotContext> members, QuestGraphLoader quests, GroupPlan plan)
    {
        var vid = vctx.Identity!;
        vid.Level = vctx.Level;

        foreach (var m in members)
        {
            var id = m.Identity;
            if (id == null) continue;
            foreach (var qid in id.CompletedQuestIds) vid.CompletedQuestIds.Add(qid);
            foreach (var qid in id.AbandonedGreyQuestIds) vid.AbandonedGreyQuestIds.Add(qid);
            foreach (var kv in id.DeferredQuestIds)
                if (!vid.DeferredQuestIds.ContainsKey(kv.Key)) vid.DeferredQuestIds[kv.Key] = kv.Value;
            foreach (var kv in id.PathBlacklist)
                if (!vid.PathBlacklist.TryGetValue(kv.Key, out var d) || kv.Value > d)
                    vid.PathBlacklist[kv.Key] = kv.Value;
        }
        vid.PruneExpiredDeferrals();
        vid.PrunePathBlacklist();

        // GAP E fix (2026-07-02): the inverse down-union for grey-drops. The virtual bot runs the real
        // solo Derive, whose "0. grey drop" phase calls AbandonGrey on the VIRTUAL identity and returns
        // an ABANDON_QUEST Fire -- but that Fire never reaches the wire for a bodyless virtual bot (it
        // falls through BuildGroupOrderFromVirtual to HoldAtAnchor). So the group's PLANNING correctly
        // stops working the grey quest, but nothing tells real members holding it to drop it -- a member
        // under a GroupOrder isn't running its own solo grey-drop, so it lingers in that member's log.
        // Pushing the virtual grey set back DOWN onto each present member's own AbandonedGreyQuestIds is
        // the exact inverse of the up-union above, and it's all real members need: every solo consult
        // that could re-work the quest (BuildBatch resume, Pickable, WorkableInLog) already excludes its
        // own AbandonedGreyQuestIds, so the quest leaves every member's planning and can't re-enter. This
        // does NOT force an immediate in-game ABANDON_QUEST (that would be option 1, a new GroupOrder
        // phase); it guarantees no member re-works the grey, and each member's next solo-path visit
        // clears it from the C++ log.
        //
        // TIGHTENED (2026-07-02): gate the push on "grey at the GROUP's level" (vctx.Level = the weakest
        // present member, which the virtual bot already plans at). vid.AbandonedGreyQuestIds is a UNION,
        // so it also contains greys a HIGHER member solo-abandoned pre-group that may still be GREEN for a
        // lower member -- pushing those down unconditionally would stamp a quest onto a member for whom
        // it's still workable (over-exclusion). GrayLevel is monotonic in level, so a quest grey at the
        // weakest level is grey for EVERY present member: one check per id at vctx.Level is exact -- it
        // pushes only genuine group-greys (safe for all) and skips the higher-member solo-history entries
        // (which those members already carry in their own set anyway). The grey test is QuestPlanner's own
        // IsQuestGreyForLevel -- the SAME IsGrey/GrayLevel the solo path uses, not a parallel copy here.
        foreach (var qid in vid.AbandonedGreyQuestIds)
        {
            if (!QuestPlanner.IsQuestGreyForLevel(quests, qid, vctx.Level))
                continue;   // a higher member's pre-group solo grey, still green for the weakest -> don't propagate
            foreach (var m in members)
                m.Identity?.AbandonedGreyQuestIds.Add(qid);
        }

        if (!quests.IsLoaded) return;
        var q = vctx.Quest;
        if (q == null) return;   // BuildBatch hasn't run yet this cycle (first-ever tick) -- nothing to inject into
        var have = q.Batch.Select(b => b.QuestId).ToHashSet();
        int injected = 0;

        foreach (var m in members)
        {
            if (q.Batch.Count >= GroupInjectCap || injected >= GroupInjectCap) break;
            var id = m.Identity;
            if (id == null) continue;
            int raceBit = QuestGraphLoader.RaceToBitmask(id.Race);
            int classBit = QuestGraphLoader.ClassToBitmask(id.ClassId);
            var active = new HashSet<int>(m.QuestLog.Keys);
            foreach (var node in quests.GetAvailableQuests(raceBit, classBit, id.Level, id.CompletedQuestIds, active))
            {
                if (q.Batch.Count >= GroupInjectCap || injected >= GroupInjectCap) break;
                if (have.Contains(node.QuestId)) continue;
                if (!QuestPlanner.IsPickable(node, id)) continue;               // THIS member's own eligibility (grey/red/blacklist/etc for them)
                if (vid.DeferredQuestIds.ContainsKey(node.QuestId)) continue;    // group-level defer (any member) still applies
                if (vid.AbandonedGreyQuestIds.Contains(node.QuestId)) continue;
                if (node.Giver == null || node.Giver.Map != vctx.MapId) continue;
                // Fix 4 rung 4: a group stuck in identical exhaust cycles widens its reach so
                // acquisition can seed the NEXT hub (Goldshire -> Westbrook / the Westfall border
                // givers) instead of re-locking on a drained one forever.
                float injectRadius = plan.ExhaustCycles >= 4 ? GroupInjectRadiusWideYards : GroupInjectRadiusYards;
                if (Dist2(vctx.Pos.X, vctx.Pos.Y, node.Giver.X, node.Giver.Y) > injectRadius) continue;

                q.Batch.Add(new BatchQuest { QuestId = node.QuestId, Node = node, Accepted = false });
                have.Add(node.QuestId);
                injected++;
            }
        }
    }

    // Given the virtual bot's CURRENT in-flight WAIT (vctx.Pending), decide whether REAL group state
    // now satisfies it. True = resolved (Pending cleared, MarkProgress'd, safe to ask Derive for the
    // next step this same tick). False = still outstanding (caller re-stamps the same order).
    private static bool TryResolveVirtualWait(GroupPlan plan, BotContext vctx, List<BotContext> members, QuestGraphLoader quests)
    {
        var p = vctx.Pending;
        if (p == null) return true;
        var active = vctx.Quest?.Active;

        if (p.CommandType == "MOVE_TO" && p.IsObjectiveGrind)
        {
            if (active == null || plan.LastVirtualCommand == null) { vctx.Pending = null; return true; }
            if (!TryExtractCoords(plan.LastVirtualCommand, out _, out _, out _, out _, out int creatureEntry, out _, out _, out _))
            { vctx.Pending = null; return true; }

            // Which objective/item slot(s) does this creature_entry actually satisfy? (ActiveSlot is
            // NOT usable here -- DispatchObjectiveLeg hardcodes it to 0 for the normal dispatch path;
            // "legs aren't slot-routed" per its own comment. Match on creature_entry directly instead,
            // exactly what the leg was dispatched on.)
            var killSlots = active.Node.Objectives.Where(o => o.IsCreature && o.CreatureEntry == creatureEntry)
                                                   .Select(o => o.Slot).ToList();
            var itemSlots = active.Node.ItemObjectives.Where(it =>
                                    it.BestDropSource?.CreatureEntry == creatureEntry ||
                                    (it.AltDropEntries?.Contains(creatureEntry) ?? false))
                                                   .Select(it => it.Slot).ToList();
            if (killSlots.Count == 0 && itemSlots.Count == 0) { vctx.Pending = null; return true; }   // can't identify the leg -- don't wedge

            bool reachableStillOwes = false;
            bool quarantinedStillOwes = false;
            Vec4? quarantinedDest = null;
            foreach (var m in members)
            {
                // COMPLETION IS A HARD UNION (2026-07-03): a slow-but-alive member still OWES and MUST gate --
                // NO TimeSinceProgressSec liveness bypass here (that bypass was the second leak that let the
                // group advance while a member was still killing/collecting, stranding it). A quarantined
                // member still owes the work; it is a proven route FAILURE, not successful completion. Let
                // reachable holders finish, then surface no_path into the virtual planner so Recover shelves
                // this objective for the whole group instead of false-advancing to obj_sync.
                if (m.Dead) continue;
                if (!m.QuestLog.TryGetValue(active.QuestId, out var e)) continue;   // not a holder
                if (e.Status == QuestStatusComplete) continue;
                bool owes = false;
                foreach (var slot in killSlots)
                {
                    if (slot < 1 || slot > e.MobCounts.Length) continue;
                    var obj = active.Node.Objectives.First(o => o.Slot == slot);
                    if (obj.Count > e.MobCounts[slot - 1]) { owes = true; break; }
                }
                if (!owes)
                {
                    foreach (var slot in itemSlots)
                    {
                        if (slot < 1 || slot > e.ItemCounts.Length) continue;
                        var it = active.Node.ItemObjectives.First(x => x.Slot == slot);
                        if (it.Count > e.ItemCounts[slot - 1]) { owes = true; break; }
                    }
                }

                if (!owes) continue;
                if (m.NoPathQuarantinedOrder is { } quarantined && quarantined == m.GroupOrder)
                {
                    quarantinedStillOwes = true;
                    quarantinedDest ??= m.NoPathQuarantinedDest;
                }
                else
                {
                    reachableStillOwes = true;
                }
            }

            if (reachableStillOwes) return false;
            if (quarantinedStillOwes)
            {
                vctx.Pending = null;
                vctx.Failure = new WaitFailure
                {
                    CommandType = "MOVE_TO",
                    Reason = "no_path",
                    Dest = quarantinedDest,
                    Utc = DateTime.UtcNow
                };
                return true;   // caller runs QuestPlanner.Recover this tick; this is NOT progress
            }

            vctx.Pending = null;
            vctx.MarkProgress();
            plan.ExhaustCycles = 0; plan.LastExhaustSet = "";   // Fix 4: real progress resets the ladder
            return true;
        }

        if (p.CommandType == "MOVE_TO")
        {
            // Plain travel (to_giver / to_turnin): resolved once the WHOLE present group has arrived.
            var npc = vctx.Step == "to_giver" ? active?.Node.Giver : (active?.Node.TurnIn ?? active?.Node.Giver);
            if (npc == null) { vctx.Pending = null; return true; }
            if (!AllWithinReach(members, npc, ArrivalReachYards)) return false;
            vctx.Pending = null;
            vctx.MarkProgress();
            plan.ExhaustCycles = 0; plan.LastExhaustSet = "";   // Fix 4: real progress resets the ladder
            return true;
        }

        if (p.CommandType == "QUEST_INTERACT")
        {
            int? qid = p.QuestId;
            if (qid == null) { vctx.Pending = null; return true; }
            bool accept = p.ExpectedEvent == "QUEST_ACCEPT_ACK";
            bool anyoneStillOwes = members.Any(m =>
            {
                var id = m.Identity;
                if (id == null) return false;
                if (accept)
                {
                    if (m.QuestLog.ContainsKey(qid.Value)) return false;
                    int raceBit = QuestGraphLoader.RaceToBitmask(id.Race);
                    int classBit = QuestGraphLoader.ClassToBitmask(id.ClassId);
                    var activeIds = new HashSet<int>(m.QuestLog.Keys);
                    // NOTE: intentionally NOT gated on QuestPlanner.IsPickable here -- once the virtual
                    // bot has committed to accepting this quest, a member who's simply eligible per the
                    // graph (race/class/level/prereqs) should accept it too, even if some OTHER solo-only
                    // pick filter would have deprioritized it for them individually.
                    return quests.GetAvailableQuests(raceBit, classBit, id.Level, id.CompletedQuestIds, activeIds)
                                 .Any(n => n.QuestId == qid.Value);
                }
                return m.QuestLog.TryGetValue(qid.Value, out var e) && e.Status == QuestStatusComplete;
            });
            if (anyoneStillOwes) return false;
            vctx.Pending = null;
            vctx.MarkProgress();
            plan.ExhaustCycles = 0; plan.LastExhaustSet = "";   // Fix 4: real progress resets the ladder
            return true;
        }

        vctx.Pending = null;   // an unhandled command type (shouldn't happen) -- don't wedge forever
        return true;
    }

    // Mirrors BotExecutor.IssueAsync's Outstanding construction, minus the actual bridge send (the
    // virtual bot has no real connection). LastVirtualCommand carries the payload BuildGroupOrderFromVirtual
    // needs for the enriched-objective case (x/y/z/creature_entry aren't retained on Outstanding itself).
    private static void ArmVirtualPending(GroupPlan plan, BotContext vctx, BridgeCommand cmd, string expectedEvent, TimeSpan deadline)
    {
        var now = DateTime.UtcNow;
        bool objectiveGrind = cmd.Type == "MOVE_TO" && cmd.Payload.ContainsKey("creature_entry");
        int? questId = null;
        if (cmd.Type == "QUEST_INTERACT" && cmd.Payload.TryGetValue("quest_id", out var qo) && qo is IConvertible)
            questId = Convert.ToInt32(qo);

        vctx.Pending = new Outstanding
        {
            CommandType = cmd.Type,
            ExpectedEvent = expectedEvent,
            SentUtc = now,
            DeadlineUtc = now + deadline,
            IsObjectiveGrind = objectiveGrind,
            QuestId = questId
        };
        plan.LastVirtualCommand = cmd;
    }

    // Translate the virtual bot's CURRENT state (Step / Pending / Quest.Active) into the GroupOrder to
    // stamp on real members this tick. Called both right after a fresh Issue (arms + translates the
    // same tick) and when re-stamping an unresolved WAIT (reads the same state, produces the same
    // order -- idempotent by construction). This is where the group-only travel-safety gate applies:
    // solo has no concept of "protect the weakest teammate", so PathSafeForWeakest is checked HERE,
    // and an unsafe target is fed back into the virtual ctx as a path_unsafe Failure -- letting the
    // REAL Recover() (blacklist + level-gated defer) handle it next tick, exactly like solo.
    private static GroupOrder BuildGroupOrderFromVirtual(
        GroupPlan plan, BotContext vctx, BotContext anchor, int anchorGuid, List<BotContext> members,
        ZoneSafetyMap safety, GroupPhase prevPhase)
    {
        var active = vctx.Quest?.Active;

        switch (vctx.Step)
        {
            case "to_giver" when active?.Node.Giver != null:
                {
                    var npc = active.Node.Giver;
                    if (!PathSafeForWeakest(members, anchor, npc, safety))
                    {
                        RouteVirtualUnsafe(vctx, npc, anchor, safety);
                        Emit(anchorGuid, prevPhase, GroupPhase.GroupGrind, $"virtual: giver={npc.NpcEntry} unsafe -> path_unsafe defer; grind together", members);
                        return GroupGrindAt(plan, anchor, anchorGuid);
                    }
                    // Two-phase, matching the old design: TravelToGiver (StampHeld mirrors this as a
                    // RECONCILABLE Objective.Travel -- the self-heal catches a C++ task silently dropping
                    // mid-walk) until everyone's actually there, THEN Accept (passive Hold; GroupAccept's
                    // own per-member AtNpc/MoveTo already covers the last few individual yards regardless).
                    if (AllWithinReach(members, npc, ArrivalReachYards))
                    {
                        Emit(anchorGuid, prevPhase, GroupPhase.Accept, $"virtual: giver={npc.NpcEntry} q=[{active.QuestId}] allInReach=T", members);
                        return ToNpc(plan, GroupPhase.Accept, anchorGuid, npc);
                    }
                    Emit(anchorGuid, prevPhase, GroupPhase.TravelToGiver, $"virtual: giver={npc.NpcEntry} q=[{active.QuestId}] traveling", members);
                    return ToNpc(plan, GroupPhase.TravelToGiver, anchorGuid, npc);
                }

            case "to_turnin" when active != null && (active.Node.TurnIn ?? active.Node.Giver) != null:
                {
                    var npc = active.Node.TurnIn ?? active.Node.Giver!;
                    if (!PathSafeForWeakest(members, anchor, npc, safety))
                    {
                        RouteVirtualUnsafe(vctx, npc, anchor, safety);
                        Emit(anchorGuid, prevPhase, GroupPhase.GroupGrind, $"virtual: ender={npc.NpcEntry} unsafe -> path_unsafe defer; grind together", members);
                        return GroupGrindAt(plan, anchor, anchorGuid);
                    }
                    if (AllWithinReach(members, npc, ArrivalReachYards))
                    {
                        Emit(anchorGuid, prevPhase, GroupPhase.TurnIn, $"virtual: ender={npc.NpcEntry} q=[{active.QuestId}] allInReach=T", members);
                        return ToNpc(plan, GroupPhase.TurnIn, anchorGuid, npc);
                    }
                    Emit(anchorGuid, prevPhase, GroupPhase.TravelToTurnIn, $"virtual: ender={npc.NpcEntry} q=[{active.QuestId}] traveling", members);
                    return ToNpc(plan, GroupPhase.TravelToTurnIn, anchorGuid, npc);
                }

            case "to_objective" when active != null && plan.LastVirtualCommand != null
                                      && TryExtractCoords(plan.LastVirtualCommand, out float x, out float y, out float z, out int map, out int creatureEntry, out int alt1, out int alt2, out int alt3):
                {
                    var dest = new QuestNpcLocation { NpcEntry = 0, X = x, Y = y, Z = z, Map = map };
                    if (!PathSafeForWeakest(members, anchor, dest, safety))
                    {
                        RouteVirtualUnsafe(vctx, dest, anchor, safety);
                        Emit(anchorGuid, prevPhase, GroupPhase.GroupGrind, $"virtual: objective cre={creatureEntry} unsafe -> path_unsafe defer; grind together", members);
                        return GroupGrindAt(plan, anchor, anchorGuid);
                    }
                    var directive = ExecDirective.Objective(active.QuestId, 0, creatureEntry, x, y, z, map, anchorGuid, alt1, alt2, alt3);
                    plan.LatchedObjective = directive;
                    plan.SetPhase(GroupPhase.Objective);
                    Emit(anchorGuid, prevPhase, GroupPhase.Objective, $"virtual: quest={active.QuestId} cre={creatureEntry}", members);
                    return GroupOrder.Engage(anchorGuid, directive);
                }

            // 2026-07-01 BUG FIX: "accept" and "turnin" are the step names PlanNext's OWN switch sets
            // — inside its "to_giver"/"to_turnin" cases — in the SAME call that returns the
            // QUEST_INTERACT Issue, once AtNpc(vctx, ...) (checked against the anchor's position) is
            // already true. That Issue was falling into `default` below, completely untranslated: no
            // GroupOrder ever told a real member to actually fire the accept/turn-in, so
            // TryResolveVirtualWait's "does anyone still owe this" check could never resolve --
            // permanent stall, and silently at that (no Emit in the old default branch either). This
            // is what a "totally stalled immediately" fresh group was hitting on the very first accept.
            case "accept" when active?.Node.Giver != null:
                {
                    var npc = active.Node.Giver;
                    Emit(anchorGuid, prevPhase, GroupPhase.Accept, $"virtual: accept giver={npc.NpcEntry} q=[{active.QuestId}]", members);
                    return ToNpc(plan, GroupPhase.Accept, anchorGuid, npc);
                }

            case "turnin" when active != null && (active.Node.TurnIn ?? active.Node.Giver) != null:
                {
                    var npc = active.Node.TurnIn ?? active.Node.Giver!;
                    Emit(anchorGuid, prevPhase, GroupPhase.TurnIn, $"virtual: turnin ender={npc.NpcEntry} q=[{active.QuestId}]", members);
                    return ToNpc(plan, GroupPhase.TurnIn, anchorGuid, npc);
                }

            default:
                // Genuine between-leg transients (obj_sync / detour / grind_obj / plan -- PlanNext
                // returned Continue and will re-derive once external state catches up, e.g. obj_sync
                // waiting for the next STATE heartbeat), OR an ABANDON_QUEST grey-drop tick. The grey-drop
                // is now HANDLED at the identity level (GAP E fix, 2026-07-02): RefreshVirtualEligibility
                // unions the virtual bot's grey set down onto every present member, so by the time we're
                // here the quest has already left every member's planning -- the Fire itself has no group
                // wire translation and correctly needs none. Hold at the anchor rather than going fully
                // idle, so a latched objective (if any) keeps the rest productive -- but SAY so, so a real
                // stall here is still visible in the log instead of silent (the gap that hid the
                // accept/turnin bug above).
                Emit(anchorGuid, prevPhase, GroupPhase.HoldAtAnchor, $"virtual: step={vctx.Step} unhandled -> hold", members);
                return HoldAtAnchor(plan, anchor);
        }
    }

    // Feed a path_unsafe failure back into the virtual ctx -- Recover() (the REAL solo logic) picks
    // this up on the NEXT PlanNext call and blacklists + level-defers it, exactly like a real bot.
    private static void RouteVirtualUnsafe(BotContext vctx, QuestNpcLocation target, BotContext anchor, ZoneSafetyMap safety)
    {
        int danger = safety.IsLoaded
            ? safety.GetMaxCreatureLevelOnPath(anchor.MapId, anchor.Pos.X, anchor.Pos.Y, target.X, target.Y,
                                               ZoneSafetyMap.TeamFromFaction(anchor.Identity?.Faction))
            : 0;
        vctx.Pending = null;   // the leg never really committed -- nothing to ack, just fail it now
        vctx.Failure = new WaitFailure
        {
            CommandType = "MOVE_TO",
            Reason = "path_unsafe",
            Dest = new Vec4(target.X, target.Y, 0, target.Map),
            DangerLevel = danger,
            Utc = DateTime.UtcNow
        };
    }

    // Same extraction pattern BotExecutor.ExtractTarget uses -- BridgeCommand.Payload is a flat
    // key/value bag built from an anonymous object, so this is the only way back to the raw
    // coordinates once a StepResult.Issue has been produced.
    //
    // alt1/2/3 (GAP C fix, 2026-07-02): the virtual bot's LastVirtualCommand IS the real solo
    // MoveToObjectiveLeg dispatch (the virtual bot ran DispatchObjectiveLeg for real), so
    // alt_entry1/2/3 -- the tied item-drop siblings -- are already sitting in this same payload
    // whenever the leg has any. Pulled here so BuildGroupOrderFromVirtual's to_objective case can
    // thread them into ExecDirective.Objective(...) with no new resolution logic of its own. Plain
    // scalar out-params, not a list -- ExecDirective's own equality is load-bearing (see its
    // comment on BotContext), so nothing on this path should ever construct a reference type that
    // would defeat it.
    private static bool TryExtractCoords(BridgeCommand cmd, out float x, out float y, out float z, out int map, out int creatureEntry, out int alt1, out int alt2, out int alt3)
    {
        x = y = z = 0; map = 0; creatureEntry = 0; alt1 = 0; alt2 = 0; alt3 = 0;
        if (!cmd.Payload.TryGetValue("x", out var xo) || !cmd.Payload.TryGetValue("y", out var yo) || !cmd.Payload.TryGetValue("z", out var zo))
            return false;
        x = ToFloat(xo); y = ToFloat(yo); z = ToFloat(zo);
        if (cmd.Payload.TryGetValue("mapId", out var mo)) map = ToInt(mo);
        if (cmd.Payload.TryGetValue("creature_entry", out var ceo)) creatureEntry = ToInt(ceo);
        if (cmd.Payload.TryGetValue("alt_entry1", out var a1o)) alt1 = ToInt(a1o);
        if (cmd.Payload.TryGetValue("alt_entry2", out var a2o)) alt2 = ToInt(a2o);
        if (cmd.Payload.TryGetValue("alt_entry3", out var a3o)) alt3 = ToInt(a3o);
        return true;
    }

    private static float ToFloat(object o) => o is IConvertible ? Convert.ToSingle(o) : 0f;
    private static int ToInt(object o) => o is IConvertible ? Convert.ToInt32(o) : 0;

    // ── Whole-group errands (§4) ──

    // TRANSIENT hold only (2026-07-04): between-leg ticks (obj_sync / detour / grey-drop) where the
    // right move is "keep whatever task is running, stamp nothing new". Every real idle state goes
    // through GroupGrindAt instead (Axiom 1). Still embeds the latch so an Objective<->hold flap
    // doesn't drop a live shared grind mid-objective.
    private static GroupOrder HoldAtAnchor(GroupPlan plan, BotContext anchor)
    {
        plan.SetPhase(GroupPhase.HoldAtAnchor);
        var anchorPos = new Vec4(anchor.Pos.X, anchor.Pos.Y, anchor.Pos.Z, anchor.MapId);
        return GroupOrder.Hold(anchor.Guid, plan.LatchedObjective, anchorPos);
    }

    // Axiom 1 (2026-07-04): the group's idle behavior — grind nearby level-appropriate mobs together
    // at a memoized clump point. Point = wherever the group stood when the window opened (or the
    // corpse/rez position on the DOOR1 corpse-defense path, which sets plan.GrindPoint directly).
    private static GroupOrder GroupGrindAt(GroupPlan plan, BotContext anchor, int anchorGuid)
    {
        var gp = EnsureGrindPoint(plan, anchor);
        plan.SetPhase(GroupPhase.GroupGrind);
        return GroupOrder.GrindAt(anchorGuid, gp);
    }

    // Memoize the clump point for the CURRENT GroupGrind window (a live anchor position would change
    // the stamped order — structural equality — every tick and re-path the team at tick speed, the
    // vendor-memo lesson). Rounded to 5yd; SetPhase clears it when the window closes.
    private static Vec4 EnsureGrindPoint(GroupPlan plan, BotContext anchor)
    {
        if (plan.Phase == GroupPhase.GroupGrind && plan.HasGrindPoint)
            return plan.GrindPoint;
        var p = new Vec4(
            MathF.Round(anchor.Pos.X / 5f) * 5f,
            MathF.Round(anchor.Pos.Y / 5f) * 5f,
            anchor.Pos.Z, anchor.MapId);
        plan.GrindPoint = p;
        plan.HasGrindPoint = true;
        return p;
    }

    // Round 5 (2026-07-04): death-episode bookkeeping. Counts alive->dead TRANSITIONS (not
    // dead-ticks) into a rolling window; captures proven-safe ground whenever the party has been
    // whole and death-free for a full calm window; and arms the meat-grinder breaker when the
    // window fills — the retreat itself is applied by DriveGroup once the party is whole, and the
    // active-quest shelve by DriveGroupViaVirtual where the virtual context is in scope.
    private static void TrackDeaths(GroupPlan plan, List<BotContext> members, BotContext anchor,
        int anchorGuid, GroupPhase prevPhase)
    {
        var now = DateTime.UtcNow;

        var deadNow = new HashSet<int>(members.Where(m => m.Dead).Select(m => m.Guid));
        foreach (var g in deadNow)
        {
            if (!plan.LastDeadGuids.Contains(g))
            {
                plan.RecentDeaths.Enqueue(now);
                plan.LastDeathUtc = now;
            }
        }
        plan.LastDeadGuids.Clear();
        foreach (var g in deadNow) plan.LastDeadGuids.Add(g);

        // Rez guard stamp (round 5): every tick a LIVING groupmate stands within guard range of a
        // dead member's corpse, refresh that member's GroupGuardNearUtc. MaintenancePlanner's
        // in-place rez gate reads this — a grouped bot does not stand up at 50% HP alone in the
        // camp that killed it; it waits (capped) for the defend protocol's converge to arrive.
        foreach (var dm in members)
        {
            if (!dm.Dead) continue;
            bool guarded = members.Any(o => o.Guid != dm.Guid && !o.Dead
                && o.MapId == dm.MapId
                && Dist2(o.Pos.X, o.Pos.Y, dm.Pos.X, dm.Pos.Y) <= GroupGuardYards);
            if (guarded) dm.GroupGuardNearUtc = now;
        }

        while (plan.RecentDeaths.Count > 0
               && (now - plan.RecentDeaths.Peek()).TotalSeconds > MeatGrinderWindowSec)
            plan.RecentDeaths.Dequeue();

        // Proven-safe ground: the party is whole and has not lost anyone for the full calm window.
        // (LastDeathUtc default = MinValue, so a fresh group's spawn point qualifies immediately.)
        if (deadNow.Count == 0 && (now - plan.LastDeathUtc).TotalSeconds > SafePointCalmSec)
        {
            plan.LastSafePoint = new Vec4(
                MathF.Round(anchor.Pos.X / 5f) * 5f,
                MathF.Round(anchor.Pos.Y / 5f) * 5f,
                anchor.Pos.Z, anchor.MapId);
            plan.HasLastSafePoint = true;
        }

        bool retreatActive = plan.RetreatUntil is DateTime r && now < r;
        if (plan.RecentDeaths.Count >= MeatGrinderDeaths && !plan.RetreatPending && !retreatActive)
        {
            plan.RetreatPending = true;
            plan.PendingGrinderDefer = true;
            EmitForce(anchorGuid, prevPhase, prevPhase,
                $"MEAT-GRINDER: {plan.RecentDeaths.Count} deaths/{MeatGrinderWindowSec}s"
                + (plan.HasLastSafePoint
                    ? $" -> retreat to ({plan.LastSafePoint.X:F0},{plan.LastSafePoint.Y:F0}) when whole + shelve active quest"
                    : " -> no safe point yet; shelve active quest only"), members);
        }
    }

    // Fix 4 (2026-07-04): force-expire the group's deferrals — from the VIRTUAL identity AND from
    // every present member's own identity (the virtual re-unions the raw member dicts every tick,
    // so clearing only the virtual copy would be undone one tick later). Time-based always;
    // level-based only when the ladder has escalated to rung 3. Also clears the per-quest fail
    // bookkeeping so the retry isn't instantly re-shelved by a stale streak.
    private static int ForceExpireDeferrals(BotContext vctx, List<BotContext> members, bool includeLevel)
    {
        var vid = vctx.Identity;
        if (vid == null) return 0;

        var toClear = vid.DeferredQuestIds
            .Where(kv => kv.Value.ExpiresAt.HasValue || (includeLevel && kv.Value.RequiredLevel.HasValue))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var qid in toClear)
        {
            vid.DeferredQuestIds.Remove(qid);
            vid.QuestFailStreak.Remove(qid);
            vid.QuestDeferralCounts.Remove(qid);
            foreach (var m in members)
            {
                var id = m.Identity;
                if (id == null) continue;
                id.DeferredQuestIds.Remove(qid);
                id.QuestFailStreak.Remove(qid);
                id.QuestDeferralCounts.Remove(qid);
            }
        }
        return toClear.Count;
    }

    // ── Predicates / helpers ──

    // A peeled member -- dead (death recovery), or ALIVE but off on its own survival/upkeep
    // errand (a vendor/repair trip under Goal.Maintenance, or a training trip under Goal.Training).
    // All three are solo trips this bot's OWN planner drives outside the group's stamp; without
    // catching the alive cases here the coordinator kept advancing the shared objective as if
    // every member were still present, so a bot mid-vendor-run or mid-trainer-run got left behind
    // ungated (until the §6 liveness escape eventually stopped counting it) instead of the team
    // holding for it the same way it already holds for a death.
    //
    // 2026-07-01: the two ALIVE cases (Maintenance / Training) carry the SAME liveness escape every
    // other gate in this file already uses (GateLivenessSec) -- a vendor/trainer errand that's
    // genuinely wedged (unreachable NPC, dead-end pocket, stuck cooldown loop) must not freeze the
    // whole team FOREVER; its own planner's give-up backstop (VendorRouteGiveupSec / TrainingPlanner's
    // RouteDeadline) still owns actually resolving the wedge -- this only stops it from ALSO
    // deadlocking the group in the meantime. DEATH is deliberately left unconditional, matching the
    // original (pre-this-session) behavior: a slow multi-phase heal-to-full can legitimately run
    // TimeSinceProgressSec past 90s without being stuck, and MaxDeadSec (300s) is already death's own
    // backstop -- narrowing the escape to just the two NEW cases avoids regressing a working path to
    // chase a bug that may not even be there.
    private static bool AnyRecovering(List<BotContext> members)
        => members.Any(m => m.Dead)
           || members.Any(m => (m.Goal == Goal.Maintenance || m.Goal == Goal.Training)
                                && m.TimeSinceProgressSec <= GateLivenessSec);

    // A member that is dead or hasn't made progress within the liveness window stops gating the
    // group's phases (§6): one frozen member must never freeze the team. Its own progress clock
    // (kill / quest / level / ack) resets this, so it is stateless.
    private static bool IsStuck(BotContext m)
        => m.Dead
           || (m.NoPathQuarantinedOrder is { } order && order == m.GroupOrder)
           || m.TimeSinceProgressSec > GateLivenessSec;

    // Every present, LIVE member is within reach of the NPC on the same map (a stuck/away member is
    // waited-for only up to the liveness escape, then ignored so the group can advance).
    private static bool AllWithinReach(List<BotContext> members, QuestNpcLocation npc, float reach)
    {
        foreach (var m in members)
        {
            if (IsStuck(m)) continue;
            if (m.MapId != npc.Map) return false;
            if (Dist2(m.Pos.X, m.Pos.Y, npc.X, npc.Y) > reach) return false;
        }
        return true;
    }

    // §5.1 weakest-member travel gate: don't march the group to a target whose path (from the anchor)
    // runs through creatures above the WEAKEST present member's safe band. Acceptance stays per-member;
    // only the group's TRAVEL TARGET is gated. Degrades open if the grid isn't loaded.
    // Cluster-aware (2026-07-04): the corridor is judged by the SHAPE of what's over the band,
    // not its single maximum. Over-band count 0 = the old clean pass. A camp (3+ over-band in one
    // cell), a blanketed corridor (5+ over-band total), or paired deep-reds (2+ spawns 3+ over the
    // band in one cell) veto. Anything else — a lone rare, a straggler pair, a patrol read — is
    // pathable-around and PASSES: the area rule ("avoid what is simply beyond our level") stays,
    // the lone-mob veto goes. The defer-level math downstream still keys the corridor MAX
    // (RouteVirtualUnsafe reads GetMaxCreatureLevelOnPath), unchanged.
    private static bool PathSafeForWeakest(List<BotContext> members, BotContext anchor, QuestNpcLocation target, ZoneSafetyMap safety)
    {
        if (target.Map != anchor.MapId) return false;   // cross-map travel is per-bot, later
        if (!safety.IsLoaded) return true;
        int weakest = members.Min(m => m.Level);
        int threshold = weakest + TravelSafetyMargin;
        // A group is single-faction — the anchor's team is the group's team (FINDING_002).
        var threat = safety.GetPathThreat(anchor.MapId, anchor.Pos.X, anchor.Pos.Y, target.X, target.Y, threshold,
                                          ZoneSafetyMap.TeamFromFaction(anchor.Identity?.Faction));
        if (threat.OverCount == 0) return true;
        if (threat.MaxCellOver >= PathClusterCellVeto) return false;
        if (threat.OverCount >= PathClusterTotalVeto) return false;
        if (threat.MaxCellDeep >= PathDeepPairVeto) return false;
        return true;
    }

    // Stamp a travel-or-interact phase keyed to an NPC, recording the phase on the plan.
    private static GroupOrder ToNpc(GroupPlan plan, GroupPhase phase, int anchorGuid, QuestNpcLocation npc)
    {
        plan.SetPhase(phase);
        return GroupOrder.ToNpc(phase, anchorGuid, npc.NpcEntry, new Vec4(npc.X, npc.Y, npc.Z, npc.Map));
    }

    // Highest level wins; lowest guid breaks ties (stable tick-to-tick). Members is non-empty.
    private static int ElectAnchor(List<BotContext> members)
    {
        var anchor = members[0];
        foreach (var ctx in members)
            if (ctx.Level > anchor.Level || (ctx.Level == anchor.Level && ctx.Guid < anchor.Guid))
                anchor = ctx;
        return anchor.Guid;
    }

    private static BotContext AnchorOf(List<BotContext> members, int anchorGuid)
    {
        foreach (var m in members)
            if (m.Guid == anchorGuid) return m;
        return members[0];
    }

    private static float Dist2(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx, dy = ay - by;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
