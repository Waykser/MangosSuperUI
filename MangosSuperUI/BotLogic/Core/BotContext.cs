namespace MangosSuperUI.BotLogic.Core;

using MangosSuperUI.BotLogic.Data;   // QuestNode (quest scratch)

// ============================================================================
// BotContext — THE live state. The keystone of the rebuild (§3.4).
//
// One per bot: the authoritative record of what the bot is doing and how it's
// going. The architecture fix AND the observability fix in one object — dump it
// (FleetReport) and you have the complete picture, no grep, no six log streams.
//
// Owned and mutated by the brain (BotBrain/BotExecutor). The Supervisor writes
// the verdict fields. Planners READ it and never mutate control state.
// ============================================================================

// ------------------------------ Geometry -----------------------------------
public readonly struct Vec3
{
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

    public float Dist2D(Vec3 o) { float dx = X - o.X, dy = Y - o.Y; return MathF.Sqrt(dx * dx + dy * dy); }
    public float Dist3D(Vec3 o) { float dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z; return MathF.Sqrt(dx * dx + dy * dy + dz * dz); }

    public override string ToString() => $"({X:F0},{Y:F0},{Z:F0})";
}

public readonly struct Vec4
{
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public int Map { get; }
    public Vec4(float x, float y, float z, int map) { X = x; Y = y; Z = z; Map = map; }

    public Vec3 Pos => new(X, Y, Z);
    public override string ToString() => $"({X:F0},{Y:F0},{Z:F0})@{Map}";
}

// ---------------------------- The WAIT (§3.5) ------------------------------
// Every command BotBrain issues sets ctx.Pending. The matching outcome clears
// it and stamps a progress timer. The Supervisor's first, universal stall rule
// is DeadlineUtc exceeded — independent of any domain.
public sealed class Outstanding
{
    public string CommandType { get; init; } = "";
    public string ExpectedEvent { get; init; } = "";
    public DateTime SentUtc { get; init; }
    public DateTime DeadlineUtc { get; set; }   // settable so the executor can PUSH it on KILL — progress-extending objective deadline (§6B.2)

    // True when this WAIT is an ENRICHED objective MOVE_TO (§4): a MOVE_TO that carries
    // creature_entry/kill_count, so C++ travels then GRINDS IN PLACE under one WAIT. The
    // initial deadline (TravelDeadline) covers the travel leg; once the bot arrives and
    // starts killing, the executor's KILL-push rolls the deadline forward on each kill —
    // exactly as it does for a SET_TASK grind — so a long grind never false-fails on the
    // travel ceiling. Set in BotExecutor.IssueAsync from the command payload.
    public bool IsObjectiveGrind { get; init; }

    // When set, this WAIT is an INTERRUPTIBLE leg (a quest trek): while it's still
    // pending, BotBrain calls the planner's Rescan on this cadence WITHOUT clearing the
    // WAIT, so newly-discovered work en route can preempt a long journey. Null = not
    // interruptible (the default — most legs ride to their ack/deadline untouched).
    // Settable so the brain can push it forward when the planner chooses to keep waiting.
    public DateTime? RescanAtUtc { get; set; }

    // Quest id this WAIT is acting on — set only for a QUEST_INTERACT (accept OR complete),
    // pulled from the issued command's quest_id payload field (same trick as the MOVE_TO target
    // cache below). 2026-06-30: turn-in is the one planner step whose bookkeeping
    // (BotIdentity.CompletedQuestIds.Add) writes DURABLE, NON-RE-DERIVABLE state — a rewarded
    // quest vanishes from C++'s log entirely, so ctx.QuestLog can never resurrect "this was
    // completed" if the bookkeeping is missed on a goal bounce (e.g. GoalSelector's Training
    // trigger firing off the SAME LEVEL_UP event the reward granted, before QuestPlanner's
    // "turnin" case ever runs). Stamped ack-driven in BotExecutor.OnEvent so it can never be
    // skipped regardless of what the goal does next tick; QuestPlanner's own Add stays as a
    // harmless idempotent duplicate on the happy path.
    public int? QuestId { get; init; }

    public double AgeSec => (DateTime.UtcNow - SentUtc).TotalSeconds;
    public bool Expired => DateTime.UtcNow > DeadlineUtc;
}

// ---------------------- Negative-ack outcome (§3.5b) -----------------------
// A pending WAIT can be resolved two ways: by its positive ack (ExpectedEvent),
// or NEGATED by a failure event that means "this won't complete" — MOVE_FAILED /
// PATH_UNSAFE (a MOVE_TO WAIT) or QUEST_INTERACT_FAIL (a QUEST_INTERACT WAIT).
// The executor clears the WAIT and stamps this so the planner escapes PROMPTLY
// (PlanNext → Blocked(reason) → OnStall) instead of waiting out the deadline
// (the no_path-plateau fix). The brain clears it once consumed.
public sealed class WaitFailure
{
    public string CommandType { get; init; } = "";   // the command that failed (MOVE_TO / QUEST_INTERACT)
    public string Reason { get; init; } = "";          // no_path / no_progress / empty_path / cross_map / path_unsafe / interact reason
    public Vec4? Dest { get; init; }                    // failed destination (for blacklist / last-leg force detection)
    public int DangerLevel { get; init; }               // PATH_UNSAFE danger_level (0 if n/a)
    public int? QuestId { get; init; }                  // QUEST_INTERACT_FAIL quest_id (if carried)
    public DateTime Utc { get; init; }

    public double AgeSec => (DateTime.UtcNow - Utc).TotalSeconds;
}

// --------------------- Typed goal scratch (§3.4) ---------------------------
// TYPED per goal — never an untyped bag. This is what replaces PhaseData.
// Only the active goal's scratch is populated; the rest stay null.

// One quest inside the batch. CARRIED — accepted in the C++ quest log with its
// partial kill progress — until it is rewarded, goes grey (out-leveled), or, for
// the span of a single sweep, gets shelved (far outlier or a failed leg). The
// durable truth is the C++ log + QUEST_STATUS_ALL; this list is rebuilt from it
// on every (re)entry to Questing, so shelving survives goal bounces for free.
public sealed class BatchQuest
{
    public int QuestId { get; init; }
    public QuestNode Node { get; init; } = null!;
    public bool Accepted { get; set; }      // in the log (resumed) or QUEST_ACCEPT_ACK'd this run
    public bool TurnedIn { get; set; }       // QUEST_COMPLETE_ACK seen — dropped from the batch next derive
    public bool Deferred { get; set; }       // far outlier THIS sweep: skip; cleared on reprocess
    public bool Failed { get; set; }         // a leg failed THIS sweep: skip, keep accepted; cleared on fresh BuildBatch
    public bool ForceMode { get; set; }      // WMO-interior NPC: interact via force_*
}

// Batch quest scratch (§ P3 batching). Drives ALL quests the bot accepted in the
// current sweep, not one at a time:
//   gather → accept-all → objective-sweep (nearest-first, outlier-shelved) →
//   turn-in-all → reprocess (follow-ups + new locals re-evaluated) → ↺
public sealed class QuestScratch
{
    // The carried batch. Rebuilt from the C++ quest log on (re)entry.
    public List<BatchQuest> Batch { get; } = new();

    // The quest whose leg armed the current WAIT (to_giver/accept/to_objective/
    // to_turnin/turnin). Cleared when that leg's outcome is applied.
    public BatchQuest? Active { get; set; }
    public int ActiveSlot { get; set; }                  // objective index within Active.Node.Objectives

    // Which spell in the active CAST objective's ordered spell list has been fired so far (0-based).
    // Reset to 0 when a cast leg is dispatched; advanced only on a real QUEST_CAST_ACK (see
    // QuestPlanner's "casting" step). Lets a multi-spell script-credited kit (e.g. Garments'
    // [heal, fortify]) fire one spell per QUEST_CAST across ticks. Irrelevant to non-cast legs.
    public int CastIndex { get; set; }

    // En-route re-gather throttle: re-scan for new local givers once the bot has
    // moved this far from where we last gathered (so a hub passed mid-sweep is caught).
    public Vec3 LastGatherPos { get; set; }

    // FleetReport display — the live batch ids.
    public List<int> ActiveQuestIds { get; } = new();

    // ---- back-compat read shims (anything that peeked the old single-quest scratch) ----
    public int QuestId => Active?.QuestId ?? (Batch.Count > 0 ? Batch[0].QuestId : 0);
    public QuestNode? Node => Active?.Node ?? (Batch.Count > 0 ? Batch[0].Node : null);
}

public sealed class GrindScratch
{
    public int CreatureEntry { get; set; }
    public Vec4 AreaCenter { get; set; }
    public float Radius { get; set; }
    public int KillGoal { get; set; }                     // 0 = indefinite grind (never TASK_COMPLETEs)
    public int KillCount { get; set; }
}

// Stage of a vendor/repair errand (driven by MaintenancePlanner under Goal.Maintenance,
// on ctx.Service). None = no errand in flight (the GoalSelector hold keys off this).
public enum VendorPhase { None, Route, Sell, Repair }

public sealed class ServiceScratch
{
    public int TargetNpcEntry { get; set; }               // vendor or trainer
    public Vec4 TargetPos { get; set; }
    public List<int> ToLearn { get; } = new();            // spell ids queued to train
    public long GoldNeeded { get; set; }                  // > 0 when gold-blocked
    public Dictionary<string, DateTime> Cooldowns { get; } = new();

    // ── Vendor/repair errand (MaintenancePlanner) ──
    public VendorPhase Phase { get; set; }                // route → sell → repair → done
    public bool CanRepair { get; set; }                   // selected vendor has UNIT_NPC_FLAG_REPAIR
    public DateTime StartedUtc { get; set; }              // trip start — drives the never-arrived give-up
    public int RouteFails { get; set; }                   // consecutive no_paths on the vendor route leg — drives the teleport-assist (TeleportAssist.AfterNoPaths)
}

// Trainer errand scratch (driven by TrainingPlanner under Goal.Training, on ctx.Train).
// Non-null = a training trip is in flight (the GoalSelector hold keys off this, exactly
// like the vendor hold keys off ctx.Service). Nulled by the brain on goal change, or by
// TrainingPlanner on done / give-up.
public sealed class TrainScratch
{
    public int TrainerEntry { get; set; }                 // class trainer NPC entry (TRAIN_AT_NPC target)
    public Vec4 TrainerPos { get; set; }                  // where to MOVE_TO
    public DateTime StartedUtc { get; set; }              // trip start
    public int ApproachFails { get; set; }                // consecutive no_paths on the to_trainer leg — drives the teleport-assist (TeleportAssist.AfterNoPaths)
}

// Death-recovery scratch (Goal.Maintenance). Transient per death: armed by
// MaintenancePlanner on the first dead tick, nulled by the brain on goal (re)entry.
// Death-LOOP detection is NOT here (it must survive this reset) — it rides durable
// BotIdentity fields (LastDeathTime / LastDeathLocation / RecordDeath / PathBlacklist).
//
// Recovery is a three-phase machine: REZ (corpse-run delay → RESURRECT; at_graveyard on
// a same-spot loop), then RELOCATE (a best-effort 25yd MOVE_TO to safer ground when the
// rez cell has hostile spawns — ported from the old MaintenanceDomain.FindSafeRezSpot,
// but run while ALIVE since a ghost can't move on this binary), then HEAL-TO-FULL
// (SET_TASK IDLE + poll STATE.health). The heal phase is the survival fix: C++ rezzes the
// bot at 50% HP, and a TASKED bot only tops off below 40% while the grind patrol breaks
// the eat channel — so re-engaging at 50% is the death spiral. An IDLE bot below 100%
// eats every tick and never wanders, so we hold it IDLE until ~full, THEN release.
public sealed class MaintenanceScratch
{
    public DateTime DeadSinceUtc { get; set; }            // entered recovery — drives the dead-time backstop
    public DateTime RezAtUtc { get; set; }                // when the corpse-run delay elapses → send RESURRECT
    public Vec4 DeathPos { get; set; }                    // where we died (death-spot blacklist target on a loop)
    public bool DeathLoop { get; set; }                   // quick SAME-SPOT re-death → escalate (blacklist + at_graveyard port)
    public bool DeathCluster { get; set; }                // goal-agnostic: ≥N deaths in the rolling window (any spot/goal) → force the graveyard port even when DeathLoop is false
    public bool HearthEscape { get; set; }                // FINDING_008: persistent loop the graveyard port can't break → port the ghost to the RACIAL START (same-map) instead + hard-reset
    public bool RezSent { get; set; }                     // RESURRECT issued — guards against duplicate sends
    public bool Escalated { get; set; }                   // death-spot blacklisted + at_graveyard sent (once per recovery)

    // ── Post-rez phases (alive) ──
    public bool Rezzed { get; set; }                      // bot was seen ALIVE after RESURRECT — a later dead tick = a re-death → re-arm
    public bool RelocateSent { get; set; }                // safe-spot MOVE_TO issued once (best-effort, ported FindSafeRezSpot)
    public bool RelocateDone { get; set; }                // relocate finished or skipped → heal next
    public bool IdleFired { get; set; }                   // SET_TASK IDLE sent once for the heal phase
    public DateTime HealSinceUtc { get; set; }            // heal phase entered — drives the heal timeout backstop
    public bool HealDone { get; set; }                    // healed (or timed out) → recovery releases to the GoalSelector
}

// ---------------------- Teleport-assist round-trip (final-NPC-approach) ----------------------
// When a final-approach MOVE_TO to a service NPC (trainer / vendor / repair) repeatedly no_paths
// while the bot is already in the vicinity — a nav-dead pocket at the NPC (building interior, bad
// mesh stitch) — the planner teleports the bot the last few yards to the NPC, lets it do its
// business at real proximity, then teleports it BACK to where it came from. The whole round-trip
// rides ctx.Teleport (non-null = a hop is committed/in-flight; the GoalSelector pins the goal while
// it is). The approach no_path COUNT lives on the planner scratch (TrainScratch.ApproachFails /
// ServiceScratch.RouteFails); THIS object exists only once a hop is COMMITTED. Nulled by the brain
// on a death/preempt goal change (recovery owns the bot), and by the planner on return. C++ side:
// BridgeHandleTeleport (NearTeleportTo + TELEPORT_ACK x|y|z|map, max_dist-capped). It shares the
// generic teleport primitive the hearth will use. Orchestration: Planners/TeleportAssist.
public enum TpPhase { Outbound, AtTarget, Inbound }

public sealed class TeleportTrip
{
    public Vec4 Anchor { get; init; }     // on-mesh return point — the bot's pos when the hop was committed (it pathed there, so it's reachable)
    public Vec4 Target { get; init; }     // the NPC coord we hopped to
    public TpPhase Phase { get; set; }    // Outbound (hopping in) → AtTarget (doing business) → Inbound (hopping back)
    public bool Failed { get; set; }      // the business failed at the NPC — return anyway, THEN give up (never strand the bot in the pocket)
}

// One quest's authoritative server-side state, parsed from QUEST_STATUS_ALL (the
// C++ reply to QUERY_QUEST_STATUS). Lets the QuestPlanner RESUME an in-log quest
// after a death/restart instead of re-accepting it (which C++ rejects → zombie).
public sealed class QuestLogEntry
{
    public int Status { get; set; }                       // VMaNGOS QUEST_STATUS — COMPLETE=1, INCOMPLETE=3 (counterintuitive; NONE=0, UNAVAILABLE=2)
    public int[] MobCounts { get; set; } = new int[4];    // per-slot kill counts, indexed by (QuestObjective.Slot - 1)
    public int[] ItemCounts { get; set; } = new int[4];   // per-slot item counts, indexed by (QuestItemReq.Slot - 1)
}

// ----------------------------- Group (Phase 5) -----------------------------
// GroupRole is new; GroupDirective already exists in BotIdentity.cs (same Core
// namespace) with None/Questing/HoldAndGrind/Regroup/GroupErrand — reuse it so
// BotContext.Directive and BotIdentity.GroupDirective share one source of truth.
public enum GroupRole { None, Leader, Member }

// ----------------------- Combat directive (grouping §3.6) ------------------
// The per-member combat seam -- mirror of the C++ CombatDirective struct on AiBotAI
// (NONE=0, ASSIST=1). The god bot (GroupCoordinator pre-pass) stamps this each tick
// alongside the execution directive (§3.2); the emit half fires it as COMBAT_DIRECTIVE
// (BridgeContracts.Combat). v1 carries ONLY Mode + AnchorGuid. Re-stamped every tick,
// idempotent (§3.8.4): assign Assist(anchor) to drive focus-fire, None to clear -- there
// is no in-place mutation, the stamp is replaced wholesale (mirrors the C++ Clear()).
//
// RESERVED as documented intent, NOT declared (the dead-field discipline §3.6): Role
// (holder/escort), FocusGuid (explicit target lock), InterruptGuid (kick target),
// MoveToAllyGuid (formation pull). They arrive as NEW wire keys when their behaviour
// ships -- never as speculative dead props now.
public enum CombatMode { None = 0, Assist = 1 }

public readonly record struct CombatDirective
{
    public CombatMode Mode { get; }
    public int AnchorGuid { get; }      // low GUID of the anchor member to assist (mirrors C++ anchorGuidLow)

    public CombatDirective(CombatMode mode, int anchorGuid)
    {
        Mode = mode;
        AnchorGuid = anchorGuid;
    }

    /// <summary>A non-None mode currently stamped (mirrors C++ IsActive()).</summary>
    public bool IsActive => Mode != CombatMode.None;

    /// <summary>The cleared seam -- solo / unstamped. Emitted as mode=none.</summary>
    public static readonly CombatDirective None = new(CombatMode.None, 0);

    /// <summary>Stamp assist focus-fire on the given anchor member.</summary>
    public static CombatDirective Assist(int anchorGuid) => new(CombatMode.Assist, anchorGuid);
}

// ----------------------- Execution directive (grouping §7.1) ---------------
// The shared KILL-OBJECTIVE payload -- the enriched-grind target (creature_entry + coords), keyed by
// quest+slot so the coordinator can read each holder's remaining count off its quest log. Originally
// the whole per-member execution stamp; as of the central-driver build it is the EMBEDDED payload that
// GroupOrder carries in its Objective / HoldAtAnchor phases (GroupOrder is now the per-member stamp --
// see GroupPlan.cs). UNCHANGED in shape: still a readonly record struct with value-equality and the
// same Objective(...) factory the coordinator builds the shared mob with. A "holder" is simply a member
// whose log contains the quest; a member ineligible for it (e.g. a warrior on a priest quest) still
// helps but never gates completion -- the gate reads each holder's OWN server count. None = no kill
// objective (every phase except Objective / HoldAtAnchor), and the cleared GroupOrder.Objective.
public enum ExecMode { None = 0, Objective = 1 }

public readonly record struct ExecDirective
{
    public ExecMode Mode { get; }
    public int QuestId { get; }          // the group quest this objective belongs to
    public int Slot { get; }             // QuestObjective.Slot (1-4) -- to read each holder's own remaining
    public int CreatureEntry { get; }    // the mob to kill (the enriched-MOVE_TO target)
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public int Map { get; }
    public int AnchorGuid { get; }       // the reference member (stable "nearest" origin; whose credit the count nominally feeds)
    // Tied item-drop alt-entries (GAP C fix, 2026-07-02) -- three plain ints, not a list: this
    // struct's equality is load-bearing (see the LastGroupOrder change-check comment below on
    // BotContext), and a list gets REFERENCE equality by default, which would make every tick's
    // freshly-extracted list compare "different" even with identical values and defeat that guard.
    // 0 = unused slot, same convention as the wire's own alt_entry1/2/3. Threaded through from the
    // virtual bot's own LastVirtualCommand payload (see GroupCoordinator.TryExtractCoords) -- the
    // virtual bot ran the real solo MoveToObjectiveLeg dispatch, so the alternates are already
    // sitting in that payload; this just carries them the rest of the way to GroupObjectiveLeg.
    public int Alt1 { get; }
    public int Alt2 { get; }
    public int Alt3 { get; }

    public ExecDirective(ExecMode mode, int questId, int slot, int creatureEntry,
        float x, float y, float z, int map, int anchorGuid, int alt1 = 0, int alt2 = 0, int alt3 = 0)
    {
        Mode = mode; QuestId = questId; Slot = slot; CreatureEntry = creatureEntry;
        X = x; Y = y; Z = z; Map = map; AnchorGuid = anchorGuid; Alt1 = alt1; Alt2 = alt2; Alt3 = alt3;
    }

    public bool IsActive => Mode != ExecMode.None;
    public static readonly ExecDirective None = new(ExecMode.None, 0, 0, 0, 0f, 0f, 0f, 0, 0);
    public static ExecDirective Objective(int questId, int slot, int creatureEntry,
        float x, float y, float z, int map, int anchorGuid, int alt1 = 0, int alt2 = 0, int alt3 = 0)
        => new(ExecMode.Objective, questId, slot, creatureEntry, x, y, z, map, anchorGuid, alt1, alt2, alt3);
}

// =============================== BotContext =================================
public sealed class BotContext
{
    // ---- identity snapshot ----
    public int Guid { get; init; }
    public string Name { get; set; } = "";
    public int Level { get; set; }

    // ---- durable roster back-ref (set once by the host at seed) ----
    // Quest completed-set / deferrals / path blacklist live on BotIdentity (durable,
    // survive reconnect). The quest planner READS them to pick+filter and routes
    // defer/abandon/blacklist through BotIdentity's own methods. Null until seeded.
    public BotIdentity? Identity { get; set; }

    // ---- intent ----
    public Goal Goal { get; private set; } = Goal.Idle;   // current high-level intent
    public string Step { get; private set; } = "idle";    // explicit step within the goal
    public Vec4? Target { get; set; }                     // x,y,z,map the brain is driving toward

    // Why the GoalSelector chose the current goal — set every tick by the arbitration so
    // FleetReport can explain it (e.g. "q av=12 pick=0", "no-identity"). The decision is
    // first-class observable state, not a throwaway log.
    public string GoalReason { get; set; } = "";

    // ---- THE WAIT — the observability spine ----
    public Outstanding? Pending { get; set; }

    // ---- last negative outcome (set by the executor when a WAIT is negated; consumed by the brain) ----
    public WaitFailure? Failure { get; set; }

    // ---- death attribution (set by the brain at the death transition; consumed by MaintenancePlanner) ----
    // When the bot dies while Questing, BotBrain.EnterGoalAsync stamps the active quest id here
    // BEFORE ResetScratch wipes ctx.Quest, so MaintenancePlanner can count the death against that
    // quest and shelve it (the macro-loop exit). NOT goal scratch — survives the scratch reset.
    // Consumed + cleared by MaintenancePlanner on the first dead tick. Null = no blame pending.
    public int? DeathBlameQuestId { get; set; }

    // ---- progress ----
    public DateTime LastProgressUtc { get; set; } = DateTime.UtcNow;  // last forward motion of ANY kind
    public DateTime LastKillUtc { get; set; }
    public DateTime LastQuestAdvanceUtc { get; set; }
    public DateTime LastLevelUtc { get; set; }
    public float LastPosDelta { get; set; }
    public Vec3 LastPosRef { get; set; }                  // ping-pong / no-progress detection

    // ---- no-progress circuit breaker (brain-owned; the universal silent/fast-stall net) ----
    // ConsecutiveFailures: negated WAITs since the last success — a fast fail-loop (e.g. relocate
    // MOVE_FAILED no_path at 1Hz) that the slow no-progress clock would take too long to catch.
    // Reset on any positive ack / kill (OnGrindProgress / the executor's ack path).
    public int ConsecutiveFailures { get; set; }

    // Recently-tried grind cell centers (cell granularity) that produced no kills. The breaker records
    // the current spot here on a wedge so the next forced relocation goes somewhere NEW, not back onto
    // the same grid-"good"-but-dead cell. Cleared on a real KILL/level (OnGrindProgress).
    private readonly List<(int X, int Y)> _deadGrindCells = new();
    public void RecordDeadGrindCell(float x, float y)
    {
        var k = ((int)MathF.Round(x / 100f), (int)MathF.Round(y / 100f));
        _deadGrindCells.Remove(k);
        _deadGrindCells.Add(k);
        while (_deadGrindCells.Count > 8) _deadGrindCells.RemoveAt(0);
    }
    public bool IsDeadGrindCell(float x, float y)
    {
        var k = ((int)MathF.Round(x / 100f), (int)MathF.Round(y / 100f));
        return _deadGrindCells.Contains(k);
    }
    /// <summary>A real kill/level — the area works, so forget the dead-cell history and the fail streak.</summary>
    public void OnGrindProgress()
    {
        _deadGrindCells.Clear();
        ConsecutiveFailures = 0;
        if (Identity != null) Identity.WedgeStreak = 0;   // killing again = not stranded (FINDING_010)
    }

    // ---- step / goal timing ----
    public DateTime GoalSinceUtc { get; private set; } = DateTime.UtcNow;
    public DateTime StepSinceUtc { get; private set; } = DateTime.UtcNow;

    // ---- verdict (written by the Supervisor) ----
    public bool Stalled { get; set; }
    public string StallReason { get; set; } = "";
    public DateTime StalledSinceUtc { get; set; }

    // ---- sensory (refreshed each tick from BotStateSnapshot) ----
    public Vec3 Pos { get; set; }
    public int MapId { get; set; }
    public int ZoneId { get; set; }
    public float HpPct { get; set; } = 1f;
    public float ManaPct { get; set; } = 1f;
    public int FreeSlots { get; set; }
    public long Copper { get; set; }
    public int Durability { get; set; } = 100;            // min equipped-slot durability % (100 = full); drives the repair errand
    public bool InCombat { get; set; }
    public bool Dead { get; set; }
    public bool InPlayerParty { get; set; }                // [PLAYERPARTY] a REAL player leads this bot's group (C++ pparty; GoalSelector holds Idle on it)

    // [AUTHORSHIP] This body answers to a human, not to the brain: a real client drives it
    // (Possessed), or it is one of the owner's own characters (IsCompanion, .sui companion add).
    // Stronger than InPlayerParty — that only says a human shares the group, and a fabricated
    // bot escorting a player is still the brain's to plan for. These say the brain has no
    // authorship: it senses, and nothing else. GroupCoordinator skips these too, so a companion
    // never gets counted into a fleet group's shared objective.
    public bool Possessed { get; set; }
    public bool IsCompanion { get; set; }
    public bool IsPlayerDriven => Possessed || IsCompanion;

    // ---- C++ held-task echo (Held-Objective build §4; refreshed each tick from STATE) ----
    // What C++ reports it is ACTUALLY running right now (mirror of m_currentTask). Unknown until the
    // C++ STATE echo lands (Session 3) — the reconcile (BotBrain) treats Unknown as "no readback →
    // today's behavior", so this is a no-op until the wire half ships. Copied from the snapshot in Sense.
    public HeldTaskEcho HeldTask { get; set; } = HeldTaskEcho.Unknown;

    // ---- goal scratch (typed; only the active goal's is populated) ----
    public QuestScratch? Quest { get; set; }
    public GrindScratch? Grind { get; set; }
    public ServiceScratch? Service { get; set; }
    public MaintenanceScratch? Maintenance { get; set; }
    public TrainScratch? Train { get; set; }              // Goal.Training — trainer trip (TrainingPlanner)

    // ---- teleport-assist round-trip (final-NPC-approach; cross-goal, not goal scratch) ----
    // Non-null = a TELEPORT_TO hop-in / do-business / hop-back is committed for the current goal's
    // service-NPC approach. Set by a planner (TrainingPlanner / MaintenancePlanner) when the
    // final-approach MOVE_TO no_paths in the vicinity; nulled by the planner on return or by the
    // brain's ResetScratch on a death/preempt goal change. The GoalSelector holds the goal while
    // it's set so nothing interrupts the short round-trip.
    public TeleportTrip? Teleport { get; set; }

    // ---- hub-errand consumed run token ([HUB-ERRAND] 2026-07-08 §3; cross-goal, NOT scratch) ----
    // Set to the HubErrandUntil stamp when a "do your rounds" round finishes/aborts
    // (HubErrandPlanner), wedges (its IsProgressing ceiling), or the bot dies mid-round
    // (GoalSelector's dead branch). The GoalSelector runs the errand goal only while the live
    // stamp != this — the timestamp IS the once-only latch: a re-issued command carries a NEW
    // timestamp and runs fresh, while the same stamp can never run twice. Like DeathBlameQuestId
    // above, this is a plain property the brain's scratch reset doesn't know BY DESIGN —
    // surviving the goal change is the whole mechanism.
    public DateTime? HubErrandDone { get; set; }

    // ---- quest-log cache (refreshed by QUEST_STATUS_ALL; read by QuestPlanner to resume) ----
    // Reference-swapped by the executor (not mutated in place) so the planner can read a
    // stable snapshot without locking. Stamp = when it was last refreshed.
    public Dictionary<int, QuestLogEntry> QuestLog { get; set; } = new();
    public DateTime QuestLogStampUtc { get; set; }

    // ---- group (Phase 5 — empty until then) ----
    public int? GroupId { get; set; }
    public GroupRole Role { get; set; } = GroupRole.None;
    public int? LeaderGuid { get; set; }
    public GroupDirective Directive { get; set; } = GroupDirective.None;
    public Vec4? Anchor { get; set; }

    // The per-member combat seam (grouping §3.6) -- the god bot re-stamps this each tick
    // (Assist(anchor) / None) and the emit half fires it as COMBAT_DIRECTIVE. Mirror of the
    // C++ m_combatDirective; a transient per-tick stamp, so it lives HERE on the live context,
    // not on durable BotIdentity. Default None = solo / unstamped (a no-op seam).
    public CombatDirective CombatDirective { get; set; } = CombatDirective.None;

    // The combat seam the spine last EMITTED to C++ (BotBrain step 1a). The coordinator re-stamps
    // CombatDirective every tick (idempotent), but the wire only fires when it differs from this --
    // keeping COMBAT_DIRECTIVE at brain-cadence, not per-tick traffic (§3.8.4 / §1). Value-equality
    // on the record struct drives that change check.
    public CombatDirective LastEmittedCombat { get; set; } = CombatDirective.None;

    // The group ORDER (grouping §7.1) -- the god bot's (GroupCoordinator pre-pass) per-tick per-member
    // stamp. It GENERALIZES the old kill-only ExecDirective stamp (which carried only the shared mob) to
    // the full §3 phase machine: the phase the whole group is in this tick, the target NPC for the
    // travel / accept / turn-in / errand phases, and the embedded ExecDirective kill objective for the
    // Objective / HoldAtAnchor phases (see GroupPlan.cs). Consumed IN-PROCESS by this bot's own
    // QuestPlanner consult (DriveGroup branches on GroupOrder.Phase) -- not a wire command, so unlike
    // CombatDirective it needs no LastEmitted WIRE marker. The no-WAIT group grind uses LastGroupOrder to
    // re-issue the leg only when the order CHANGES (structural value-equality on the record struct),
    // keeping the bridge quiet while the member works the same stamped order. Default None = solo /
    // ungrouped; GoalSelector routes a grouped member to Goal.Questing whenever Phase != None (IsActive).
    public GroupOrder GroupOrder { get; set; } = GroupOrder.None;
    public GroupOrder LastGroupOrder { get; set; } = GroupOrder.None;

    // A group leg that repeatedly has no Detour route is quarantined until the
    // coordinator changes the structural order. This prevents both a hot
    // MOVE_TO retry loop and the former uncapped teleport-to-destination escape.
    public GroupOrder? NoPathQuarantinedOrder { get; set; }
    public Vec4? NoPathQuarantinedDest { get; set; }

    /// <summary>Round 5 (2026-07-04): last time the GroupCoordinator saw a LIVING groupmate within
    /// guard range of this bot's corpse (stamped by TrackDeaths every tick while dead+guarded).
    /// MaintenancePlanner's in-place rez gate waits for a fresh stamp — capped — so a grouped bot
    /// never stands up at 50% HP alone in the camp that killed it. Solo bots never read it.</summary>
    public DateTime GroupGuardNearUtc { get; set; }

    // ---- held strategic objective (Held-Objective build §2) ----
    // The bot's COMMITTED strategic task, ABOVE Goal and OUTLIVING the leg-level WAIT (ctx.Pending),
    // ResetScratch, and the EnterGoalAsync SET_TASK IDLE bounce. Set by the strategic decider (a
    // planner committing a leg, or the GroupCoordinator assigning the shared objective); CLEARED only
    // on done / reassign / hard cap — NEVER by a goal change. That survival is the whole point: it lets
    // the reconcile detect "C++ dropped my objective" (echo=IDLE while Held=Grind) and re-commit.
    // GoalSelector consults it to stay the course instead of re-deriving the pick filter every tick.
    // Null = no objective held → free to select. (Nothing stamps it until the producer movement; until
    // then every consult/reconcile branch is inert and behavior is byte-for-byte today's.)
    public Objective? Held { get; private set; }
    public DateTime ObjectiveSinceUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Commit (or re-commit) the held strategic objective. Stamps the grace clock only on a
    /// CHANGE (structural equality on the record), so re-stamping the SAME objective each tick doesn't
    /// keep resetting the reconcile adoption grace.</summary>
    public void SetObjective(Objective o)
    {
        if (Held is not { } cur || !cur.Equals(o)) ObjectiveSinceUtc = DateTime.UtcNow;
        Held = o;
    }

    /// <summary>Drop the held objective (done / reassigned / capped) → free to select next.</summary>
    public void ClearObjective() => Held = null;

    /// <summary>Seconds since the held objective was last (re)committed — the reconcile adoption grace.</summary>
    public double TimeSinceObjectiveSec => (DateTime.UtcNow - ObjectiveSinceUtc).TotalSeconds;

    /// <summary>Last time BotBrain.ReconcileHeldObjective actually ACTED on a mismatch (cleared
    /// Pending/LastGroupOrder and re-issued) for the CURRENTLY held objective — 2026-07-03, the
    /// reconcile-storm fix. Distinct from ObjectiveSinceUtc (when the objective was committed): this
    /// is a re-fire COOLDOWN, not an adoption grace, so the reconcile can't hammer the bridge with a
    /// fresh re-issue every tick while C++ is still working the previous one. Compared against
    /// ObjectiveSinceUtc (not used as a bare timestamp) so a cooldown stamp left over from a PRIOR
    /// objective can never suppress a legitimate reconcile on a freshly-committed one. Default
    /// DateTime.MinValue = never fired yet.</summary>
    public DateTime LastReconcileUtc { get; set; } = DateTime.MinValue;

    // ----------------------------- helpers ---------------------------------
    public double TimeInGoalSec => (DateTime.UtcNow - GoalSinceUtc).TotalSeconds;
    public double TimeInStepSec => (DateTime.UtcNow - StepSinceUtc).TotalSeconds;
    public double TimeSinceProgressSec => (DateTime.UtcNow - LastProgressUtc).TotalSeconds;
    public float DistToTarget => Target.HasValue ? Pos.Dist2D(Target.Value.Pos) : -1f;

    /// <summary>Switch the high-level goal, resetting step + timers. Scratch is the brain's to swap.</summary>
    public void SetGoal(Goal goal, string step)
    {
        if (Goal != goal) GoalSinceUtc = DateTime.UtcNow;
        Goal = goal;
        SetStep(step);
    }

    /// <summary>Move to a new step within the current goal; stamps the step timer on change.</summary>
    public void SetStep(string step)
    {
        if (Step != step) StepSinceUtc = DateTime.UtcNow;
        Step = step;
    }

    /// <summary>Stamp generic forward progress (resets the no-progress clock).</summary>
    public void MarkProgress() => LastProgressUtc = DateTime.UtcNow;

    /// <summary>Refresh the sensory fields from the latest bridge snapshot. No control logic here.</summary>
    public void Sense(BotStateSnapshot snap)
    {
        Level = snap.Level;
        Pos = new Vec3(snap.X, snap.Y, snap.Z);
        MapId = snap.MapId;
        ZoneId = snap.ZoneId;
        HpPct = snap.HealthPercent;
        ManaPct = snap.ManaPercent;
        FreeSlots = (int)snap.FreeSlots;
        Copper = snap.Copper;
        Durability = (int)snap.Durability;
        InCombat = snap.InCombat;
        Dead = snap.IsDead;
        InPlayerParty = snap.InPlayerParty;
        Possessed = snap.Possessed;       // [AUTHORSHIP] a real client is driving this body
        IsCompanion = snap.IsCompanion;   // [AUTHORSHIP] one of the owner's own characters
        HeldTask = snap.HeldTask;   // C++ task readback (Unknown until the Session-3 STATE echo lands)

        // Quest log now rides on STATE (the QUERY_QUEST_STATUS pull is retired). Set it here — on the TICK
        // thread, the SINGLE writer — so there is no cross-thread mutation race and no request/reply cache to
        // go stale, partial, or empty. snap.QuestLog is the full C++ player log; StateUtc is when this STATE
        // landed, so QuestLogStampUtc is a true "data produced at" clock (advances only on a new STATE, not
        // every 250ms tick) — exactly what the objective re-derive freshness gate needs.
        QuestLog = snap.QuestLog;
        QuestLogStampUtc = snap.StateUtc;
    }
}
