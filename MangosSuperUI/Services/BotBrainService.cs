using System.Collections.Concurrent;
using Dapper;
using MangosSuperUI.BotLogic.Core;
using MangosSuperUI.BotLogic.Brain;
using MangosSuperUI.BotLogic.Data;
using MangosSuperUI.BotLogic.Tracking;
using MangosSuperUI.Models;

namespace MangosSuperUI.Services;

/// <summary>
/// The behavioral engine HOST (rebuild — Strategy B, Phase 1).
///
/// Ownership is inverted from the old design: this service no longer scatters
/// control across DecisionEngine + per-domain phase state. It is a thin host that
///   1. keeps the bridge roster mirrored into BotIdentity (for the dashboard +
///      grouping, which is rebuilt last) and into BotContext (the new live-state
///      keystone the spine drives),
///   2. drives the spine — BotBrain (driver) → BotExecutor (issue + WAIT) →
///      BotSupervisor (stall) — one BotContext per bot per tick, and
///   3. prints the FleetReport: one bounded, context-window-sized fleet picture
///      that replaces grepping six log streams.
///
/// Phase 1 is spine, no behavior: every bot is held in Goal.Idle and the only
/// live correction is the Supervisor's universal deadline rule. Goal selection
/// and per-goal planners land in Phases 2+ inside BotBrain, untouched here.
///
/// Retained for the dashboard / grouping (carved out in their own phases):
///   GroupManager + group endpoints, BotIdentity roster (AllBots), personality
///   load/roll/persist, story-rider toggles (demoted — superseded by FleetReport),
///   GetBotBrainSummary, BrainEnabled.
/// Shed: DecisionEngine + all domains as the driver, the grouping batch/errand
///   fan-out, known-good destinations, in-flight MOVE_TO recovery, fleet
///   diagnostics, and the flight recorder.
///
/// The bridge contract is unchanged: it calls SetBrainService(this) and routes
/// every EVENT/CHAT_RECV through HandleBridgeEventAsync(guid, BotEvent).
/// </summary>
public class BotBrainService : BackgroundService
{
    private readonly BotBridgeService _bridge;
    private readonly ConnectionFactory _db;
    private readonly BotStateTracker _tracker;
    private readonly QuirkLoader _quirkLoader;
    private readonly BotBrainDbInit _dbInit;
    private readonly ILogger<BotBrainService> _logger;
    private readonly BotBrain _driver;           // the spine (BotBrain → BotExecutor/BotSupervisor)
    private readonly GroupManager _groupManager;
    private readonly QuestGraphLoader _quests;    // the shared quest graph -> handed to the god-bot pre-pass for union objective selection
    private readonly ZoneSafetyMap _safety;       // §5.1 weakest-member travel gate (path creature-level)
    private readonly CreatureSpawnLoader _spawns; // Scatter Build 2: real-spawn anchor sampler -> god-bot shared-objective dispersal
    private readonly ZoneDataLoader _zoneData;    // GAP G (2026-07-02): nearest vendor/repair NPC for the whole-group vendor errand (GroupCoordinator.Update)
    private readonly BotFallRecorder _fallRecorder; // always-on void/fall black box — Observe(ctx) each tick, flush-only-on-fall

    // Roster mirrors. _bots feeds the dashboard + grouping; _contexts is the
    // live-state the spine drives. One entry per connected bot in both.
    private readonly ConcurrentDictionary<int, BotIdentity> _bots = new();
    private readonly ConcurrentDictionary<int, BotContext> _contexts = new();

    private readonly HashSet<int> _initializedGuids = new();
    private readonly ConcurrentDictionary<int, DateTime> _disconnectedAt = new();

    private volatile bool _brainEnabled = false;
    private DateTime _lastFleetLog = DateTime.MinValue;

    private const double EVICT_DISCONNECT_SEC = 60.0;
    private const double FLEET_LOG_INTERVAL_SEC = 30.0;

    public BotBrainService(
        BotBridgeService bridge,
        ConnectionFactory db,
        BotStateTracker tracker,
        QuirkLoader quirkLoader,
        BotBrainDbInit dbInit,
        ILogger<BotBrainService> logger,
        ILoggerFactory loggerFactory,
        BotBrain driver,
        QuestGraphLoader quests,
        ZoneSafetyMap safety,
        CreatureSpawnLoader spawns,
        ZoneDataLoader zoneData,
        BotFallRecorder fallRecorder)
    {
        _bridge = bridge;
        _db = db;
        _tracker = tracker;
        _quirkLoader = quirkLoader;
        _dbInit = dbInit;
        _logger = logger;
        _driver = driver;
        _quests = quests;
        _safety = safety;
        _spawns = spawns;
        _zoneData = zoneData;
        _fallRecorder = fallRecorder;
        _groupManager = new GroupManager(_db, loggerFactory.CreateLogger<GroupManager>());
    }

    // ==================== Public API (controller / hub) ====================

    /// <summary>Group manager — exposed for the dashboard controller.</summary>
    public GroupManager GroupManager => _groupManager;

    /// <summary>
    /// Whether the spine DRIVES (selects goals, issues commands, supervises).
    /// When false, bots are still sensed each tick so FleetReport stays live, but
    /// no command is issued and no stall is raised. Toggling off resets session
    /// roster state (it re-syncs from the bridge on the next loop). In Phase 1 the
    /// spine only holds Idle, so this gates nothing visible yet — from Phase 2 it
    /// gates planner execution.
    /// </summary>
    public bool BrainEnabled
    {
        get => _brainEnabled;
        set
        {
            _brainEnabled = value;
            _logger.LogInformation("BotBrain: driving {State}", value ? "ENABLED" : "DISABLED");

            if (!value)
            {
                var count = _contexts.Count;
                _bots.Clear();
                _contexts.Clear();
                _initializedGuids.Clear();
                _disconnectedAt.Clear();
                _logger.LogInformation("BotBrain: cleared {Count} bot entries on disable — next sync starts clean", count);
            }
        }
    }

    /// <summary>Live BotIdentity roster — consumed by the dashboard (BrainStatus).</summary>
    public IReadOnlyDictionary<int, BotIdentity> AllBots => _bots;

    public BotIdentity? GetBotIdentity(int guid) =>
        _bots.TryGetValue(guid, out var bot) ? bot : null;

    public int ActiveBotCount => _contexts.Count;

    /// <summary>
    /// Story-rider toggle (demoted: superseded by FleetReport, kept for the dashboard).
    /// Flips each listed bot's rider Enabled flag (null/empty = whole fleet) and returns
    /// the guids affected. Passive — sets a flag only; nothing emits in the rebuild.
    /// </summary>
    public IReadOnlyList<int> SetStoryEnabled(IEnumerable<int>? guids, bool on)
    {
        var targets = guids?.ToList();
        var affected = new List<int>();
        foreach (var kvp in _bots)
        {
            if (targets != null && targets.Count > 0 && !targets.Contains(kvp.Key)) continue;
            var rider = kvp.Value.Story;
            if (rider == null) continue;
            rider.Enabled = on;
            affected.Add(kvp.Key);
        }
        _logger.LogInformation("BotBrain: story rider {State} on {Count} bot(s)", on ? "ENABLED" : "DISABLED", affected.Count);
        return affected;
    }

    /// <summary>Read-only story-rider status for the dashboard.</summary>
    public IReadOnlyList<object> GetStoryStatus()
    {
        var list = new List<object>();
        foreach (var kvp in _bots)
        {
            var rider = kvp.Value.Story;
            if (rider == null) continue;
            list.Add(new
            {
                guid = kvp.Key,
                name = kvp.Value.Name,
                enabled = rider.Enabled,
                dropped = rider.DroppedRecords,
                lastError = rider.LastError
            });
        }
        return list;
    }

    /// <summary>
    /// Per-bot brain summary for the dashboard. Shape preserved from the old design
    /// so the existing UI keeps rendering; the values now reflect the Idle spine.
    /// </summary>
    public object? GetBotBrainSummary(int guid)
    {
        if (!_bots.TryGetValue(guid, out var bot)) return null;
        var bs = _bridge.GetBotState(guid);
        return new
        {
            guid = bot.Guid,
            name = bot.Name,
            activity = bot.CurrentActivity.Type.ToString(),
            activityDuration = bot.CurrentActivity.MinutesInState,
            subPhase = bot.CurrentActivity.SubPhase,
            contextTag = bot.CurrentActivity.ContextTag,
            personality = bot.Personality.ToSummary(),
            tickBase = bot.Personality.DecisionTickBase,
            nextTick = bot.NextDecisionTick,
            copper = bs?.Copper ?? 0,
            freeSlots = bs?.FreeSlots ?? 16,
            totalSlots = bs?.TotalSlots ?? 16,
            inventoryCount = bot.ShadowInventory.Count,
            hasUnlearnedSpells = bot.HasUnlearnedSpells,
            questProgress = bot.CurrentQuestProgress,
            activeQuestId = bot.ActiveQuestId,
            pendingAction = bot.PendingAction != null ? new
            {
                returnTo = bot.PendingAction.ReturnTo.ToString(),
                subPhase = bot.PendingAction.SubPhase,
                questId = bot.PendingAction.QuestId
            } : null
        };
    }

    /// <summary>
    /// The one-shot fleet picture (§3.6): rollups + one bounded line per bot.
    /// Exposed for a future endpoint; also logged on an interval (see the loop).
    /// </summary>
    public string GetFleetReport() => FleetReport.Render(_contexts.Values.ToList());

    // ==================== Live spine state (UI: the "Live" tab) ====================
    // The structured, per-bot projection of BotContext — the same picture FleetReport
    // renders as text, but as JSON the dashboard can render and tick client-side. This
    // is the spine's live state (Goal/Step/why/WAIT/Failure/timers/typed scratch), NOT
    // the old DecisionEngine summary that GetBotBrainSummary serves.

    /// <summary>Structured live state for one bot, or null if it has no live context.</summary>
    public object? GetLiveState(int guid) =>
        _contexts.TryGetValue(guid, out var ctx) ? ProjectLive(ctx) : null;

    /// <summary>Structured live state for the whole fleet (stalled first, then by name).</summary>
    public IReadOnlyList<object> GetLiveFleet() =>
        _contexts.Values
            .OrderByDescending(c => c.Stalled)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ProjectLive)
            .ToList();

    private static object ProjectLive(BotContext c)
    {
        var now = DateTime.UtcNow;
        return new
        {
            guid = c.Guid,
            name = c.Name,
            level = c.Level,

            // intent
            goal = c.Goal.ToString(),
            step = c.Step,
            why = c.GoalReason,

            // timers
            timeInGoalSec = (int)c.TimeInGoalSec,
            timeInStepSec = (int)c.TimeInStepSec,
            noProgressSec = (int)c.TimeSinceProgressSec,
            lastKillSec = AgoOrNull(c.LastKillUtc, now),
            lastQuestSec = AgoOrNull(c.LastQuestAdvanceUtc, now),
            lastLevelSec = AgoOrNull(c.LastLevelUtc, now),

            // sensory
            hpPct = (int)Math.Round(c.HpPct * 100),
            manaPct = (int)Math.Round(c.ManaPct * 100),
            pos = new { x = c.Pos.X, y = c.Pos.Y, z = c.Pos.Z },
            mapId = c.MapId,
            zoneId = c.ZoneId,
            durability = c.Durability,
            freeSlots = c.FreeSlots,
            copper = c.Copper,
            inCombat = c.InCombat,
            dead = c.Dead,

            // combat directive (grouping §3.6) -- the coordinator's per-tick stamp. Assist = this bot
            // is focus-firing the anchor member's victim (anchorGuid == self => this bot IS the anchor;
            // the team assists it). anchorGuid joins to a name client-side from the same fleet list,
            // like classId. null = solo / unstamped.
            combat = c.CombatDirective.IsActive
                ? new { mode = c.CombatDirective.Mode.ToString(), anchorGuid = c.CombatDirective.AnchorGuid }
                : null,

            // where it's driving
            target = c.Target.HasValue
                ? new { x = c.Target.Value.X, y = c.Target.Value.Y, z = c.Target.Value.Z, map = c.Target.Value.Map }
                : null,
            distToTarget = c.Target.HasValue ? (int?)(int)c.DistToTarget : null,

            // THE WAIT — the spine
            pending = c.Pending == null ? null : new
            {
                cmd = c.Pending.CommandType,
                expect = c.Pending.ExpectedEvent,
                ageSec = (int)c.Pending.AgeSec,
                secsToDeadline = (int)Math.Round((c.Pending.DeadlineUtc - now).TotalSeconds),
                isObjectiveGrind = c.Pending.IsObjectiveGrind,
                interruptible = c.Pending.RescanAtUtc.HasValue
            },

            // last negative outcome
            failure = c.Failure == null ? null : new
            {
                cmd = c.Failure.CommandType,
                reason = c.Failure.Reason,
                ageSec = (int)c.Failure.AgeSec,
                dest = c.Failure.Dest.HasValue
                    ? new { x = c.Failure.Dest.Value.X, y = c.Failure.Dest.Value.Y, z = c.Failure.Dest.Value.Z, map = c.Failure.Dest.Value.Map }
                    : null,
                danger = c.Failure.DangerLevel,
                questId = c.Failure.QuestId
            },

            // supervisor verdict
            stall = c.Stalled
                ? new { reason = c.StallReason, sinceSec = (int)(now - c.StalledSinceUtc).TotalSeconds }
                : null,

            // the active goal's typed scratch only
            scratch = ProjectScratch(c, now)
        };
    }

    // Recovery > vendor errand > quest batch > grind. Only one goal's scratch is live.
    private static object ProjectScratch(BotContext c, DateTime now)
    {
        if (c.Maintenance is { } m)
        {
            string phase =
                !m.RezSent ? "rez-wait"
                : c.Dead ? "resurrecting"
                : (m.RelocateSent && !m.RelocateDone) ? "relocate"
                : (m.IdleFired && !m.HealDone) ? "heal"
                : m.HealDone ? "done"
                : "post-rez";

            return new
            {
                kind = "maintenance",
                phase,
                deathLoop = m.DeathLoop,
                escalated = m.Escalated,
                rezSent = m.RezSent,
                relocateSent = m.RelocateSent,
                relocateDone = m.RelocateDone,
                healDone = m.HealDone,
                deadForSec = m.DeadSinceUtc == default ? (int?)null : (int)(now - m.DeadSinceUtc).TotalSeconds,
                rezInSec = m.RezAtUtc == default ? (int?)null : (int)Math.Round((m.RezAtUtc - now).TotalSeconds)
            };
        }

        if (c.Service is { } sv && sv.Phase != VendorPhase.None)
        {
            return new
            {
                kind = "vendor",
                phase = sv.Phase.ToString(),
                npcEntry = sv.TargetNpcEntry,
                canRepair = sv.CanRepair,
                startedSec = sv.StartedUtc == default ? (int?)null : (int)(now - sv.StartedUtc).TotalSeconds,
                target = new { x = sv.TargetPos.X, y = sv.TargetPos.Y, z = sv.TargetPos.Z, map = sv.TargetPos.Map }
            };
        }

        if (c.Quest is { } q && q.Batch.Count > 0)
        {
            return new
            {
                kind = "quest",
                count = q.Batch.Count,
                activeId = q.Active?.QuestId ?? 0,
                activeSlot = q.ActiveSlot,
                batch = q.Batch.Select(bq => new
                {
                    id = bq.QuestId,
                    title = bq.Node?.Title ?? "",
                    accepted = bq.Accepted,
                    turnedIn = bq.TurnedIn,
                    deferred = bq.Deferred,
                    failed = bq.Failed,
                    force = bq.ForceMode
                }).ToList(),
                active = ProjectActiveQuest(c)
            };
        }

        if (c.Grind is { } g)
        {
            return new
            {
                kind = "grind",
                creatureEntry = g.CreatureEntry,
                killGoal = g.KillGoal,
                killCount = g.KillCount,
                radius = g.Radius,
                center = new { x = g.AreaCenter.X, y = g.AreaCenter.Y, z = g.AreaCenter.Z, map = g.AreaCenter.Map }
            };
        }

        return new { kind = "none" };
    }

    // The active quest's human-readable detail: title, where to accept / hand in, and
    // each objective with its resolved name, required/current counts, and world coords.
    // Names come straight from the quest graph (TargetName/ItemName/CreatureName/GoName) —
    // no DB hit. Kill "have" counts come from the QUEST_STATUS_ALL cache (slot-1 indexed).
    private static object? ProjectActiveQuest(BotContext c)
    {
        var aq = c.Quest?.Active;
        var node = aq?.Node;
        if (aq == null || node == null) return null;

        c.QuestLog.TryGetValue(aq.QuestId, out var log);
        var objectives = new List<object>();

        // kill / interact objectives
        for (int i = 0; i < node.Objectives.Length; i++)
        {
            var o = node.Objectives[i];
            int have = (log != null && o.Slot >= 1 && o.Slot <= 4) ? log.MobCounts[o.Slot - 1] : 0;
            objectives.Add(new
            {
                slot = o.Slot,
                kind = o.IsGameObject ? "interact" : "kill",
                name = !string.IsNullOrEmpty(o.TargetName) ? o.TargetName
                       : o.IsGameObject ? ("Object #" + o.GameObjectEntry) : ("Creature #" + o.CreatureEntry),
                entry = o.IsGameObject ? o.GameObjectEntry : o.CreatureEntry,
                need = o.Count,
                have = (int?)have,
                from = (string?)null,
                x = o.GrindX,
                y = o.GrindY,
                map = o.GrindMap,
                active = i == c.Quest!.ActiveSlot
            });
        }

        // item-gather objectives (no live "have" — QUEST_STATUS_ALL cache holds only mob counts)
        foreach (var it in node.ItemObjectives)
        {
            var src = it.BestDropSource;
            var go = it.BestGoSource;
            objectives.Add(new
            {
                slot = it.Slot,
                kind = "gather",
                name = !string.IsNullOrEmpty(it.ItemName) ? it.ItemName : ("Item #" + it.ItemId),
                entry = it.ItemId,
                need = it.Count,
                have = (int?)null,
                from = src?.CreatureName ?? go?.GoName,
                x = src?.GrindX ?? go?.X ?? 0f,
                y = src?.GrindY ?? go?.Y ?? 0f,
                map = src?.GrindMap ?? go?.Map ?? c.MapId,
                active = false
            });
        }

        return new
        {
            id = aq.QuestId,
            title = node.Title,
            level = node.QuestLevel,
            giver = node.Giver == null ? null
                : new { name = node.Giver.Name, x = node.Giver.X, y = node.Giver.Y, map = node.Giver.Map },
            turnIn = node.TurnIn == null ? null
                : new { name = node.TurnIn.Name, x = node.TurnIn.X, y = node.TurnIn.Y, map = node.TurnIn.Map },
            objectives
        };
    }

    private static int? AgoOrNull(DateTime utc, DateTime now)
        => utc == default ? (int?)null : (int)(now - utc).TotalSeconds;

    // -------------------- Grouping (delegates to GroupManager) --------------------

    /// <summary>Set grouping mode from the dashboard and persist to DB.</summary>
    public async Task SetGroupingModeAsync(GroupingMode mode)
    {
        _groupManager.Mode = mode;
        try
        {
            using var conn = _db.Admin();
            await conn.ExecuteAsync(@"
                INSERT INTO bot_settings (setting_key, setting_value)
                VALUES ('grouping_mode', @Value)
                ON DUPLICATE KEY UPDATE setting_value = @Value",
                new { Value = ((int)mode).ToString() });
            await _groupManager.SaveGroupsToDbAsync();
            _groupManager.EnrichAllBots(_bots.Values);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BotBrain: failed to persist grouping mode");
        }
    }

    /// <summary>Form a group from the dashboard. Sends FORM_GROUP to the C++ leader.</summary>
    public async Task<BotGroup?> FormGroupAsync(int leaderGuid, params int[] followerGuids)
    {
        var group = _groupManager.FormGroup(leaderGuid, followerGuids);
        if (group == null) return null;

        foreach (var guid in group.MemberGuids)
            if (_bots.TryGetValue(guid, out var bot))
                _groupManager.EnrichBotIdentity(bot);

        await _bridge.SendToBotAsync(leaderGuid, "FORM_GROUP", new
        {
            member_guids = group.GetFollowers()
        });

        await _groupManager.SaveGroupsToDbAsync();
        return group;
    }

    /// <summary>Disband a group from the dashboard.</summary>
    public async Task DisbandGroupAsync(int groupId)
    {
        var group = _groupManager.GetAllGroups().FirstOrDefault(g => g.GroupId == groupId);
        if (group == null) return;

        int leaderGuid = group.LeaderGuid;
        var members = group.MemberGuids.ToList();

        _groupManager.DisbandGroup(groupId);
        foreach (var guid in members)
            if (_bots.TryGetValue(guid, out var bot))
                _groupManager.EnrichBotIdentity(bot);

        await _bridge.SendToBotAsync(leaderGuid, "DISBAND_GROUP", new { });
        await _groupManager.SaveGroupsToDbAsync();
    }

    /// <summary>Auto-form groups from the dashboard. Returns the formed groups.</summary>
    public async Task<List<BotGroup>> AutoFormGroupsAsync()
    {
        var formed = _groupManager.AutoFormGroups(AllBots,
            guid => _tracker.GetAllPositions().FirstOrDefault(p => p.Guid == guid));
        foreach (var group in formed)
        {
            foreach (var guid in group.MemberGuids)
                if (_bots.TryGetValue(guid, out var bot))
                    _groupManager.EnrichBotIdentity(bot);

            await _bridge.SendToBotAsync(group.LeaderGuid, "FORM_GROUP", new
            {
                member_guids = group.GetFollowers()
            });
        }
        if (formed.Count > 0)
            await _groupManager.SaveGroupsToDbAsync();
        return formed;
    }

    // -------------------- Bridge event entry (unchanged contract) --------------------

    /// <summary>
    /// Routes a bridge EVENT/CHAT_RECV into the spine. Thin in the rebuild: keep the
    /// dashboard's BotIdentity level fresh, then hand the event to BotBrain → BotExecutor
    /// for WAIT/ack matching. The old grouping fan-out + economy loot routing are shed
    /// (grouping → Phase 5, economy → Phase 4).
    /// </summary>
    public Task HandleBridgeEventAsync(int guid, BotEvent evt)
    {
        if (evt.EventType == "LEVEL_UP" && evt.NewLevel > 0 && _bots.TryGetValue(guid, out var bot))
        {
            bot.Level = evt.NewLevel;
            // A level-up unlocks new class spells → flag the bot to visit a trainer (gold-gated in
            // GoalSelector). Clear any training cooldown so a fresh level justifies a retry even if a
            // recent trainer trip gave up. TrainingPlanner buys what the bot can afford and re-clears
            // the flag; what it can't afford waits for the next level-up's gold.
            bot.HasUnlearnedSpells = true;
            bot.TrainCooldownUntil = null;
        }

        if (_contexts.TryGetValue(guid, out var ctx))
            _driver.OnEvent(ctx, evt);

        return Task.CompletedTask;
    }

    // ==================== Lifecycle ====================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BotBrain: host started (Strategy B spine; driving disabled by default — enable from dashboard)");

        // Tables the retained surface needs (bot_personality, bot_settings, …).
        await _dbInit.InitializeAsync();

        // Self-heal the core characters.playerbot schema. Stock VMaNGOS ships the
        // base table (char_guid, chance, ai) but NOT the identity columns the fork's
        // PlayerBotMgr::Load() and our auto-register below both read/write. On a fresh
        // clone of the fork these are absent and every bot load fails with a 1054
        // "unknown column" — so add them here, guarded, once. No-op after the first run.
        await EnsurePlayerbotColumnsAsync();

        // Personality quirk tables (load/roll on connect).
        _quirkLoader.Load();

        // Groups + grouping mode from DB.
        await _groupManager.LoadGroupsFromDbAsync();
        await LoadGroupingModeAsync();

        // Wire event routing from bridge → brain (breaks the circular DI).
        _bridge.SetBrainService(this);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Mirror the bridge roster into BotIdentity + BotContext.
                await SyncBotRosterAsync();

                // 2. Drive the spine (sense always; drive+supervise when enabled).
                await RunBrainTicksAsync();

                // 3. Print the fleet picture on an interval.
                if (_contexts.Count > 0 &&
                    (DateTime.UtcNow - _lastFleetLog).TotalSeconds >= FLEET_LOG_INTERVAL_SEC)
                {
                    _lastFleetLog = DateTime.UtcNow;
                    _logger.LogInformation("BotBrain fleet:\n{Report}", GetFleetReport());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BotBrain: main loop error");
            }

            await Task.Delay(250, stoppingToken);
        }
    }

    // ==================== Roster sync ====================

    /// <summary>
    /// Detect new bot connections from the bridge and initialize BotIdentity +
    /// BotContext; detect disconnects and evict after a grace period.
    /// </summary>
    private async Task SyncBotRosterAsync()
    {
        var bridgeStates = _bridge.BotStates;

        foreach (var kvp in bridgeStates)
        {
            int guid = kvp.Key;
            var bs = kvp.Value;

            if (bs.TaskState == "DISCONNECTED") continue;

            if (!_initializedGuids.Contains(guid))
            {
                await InitializeBotAsync(guid, bs);
                _initializedGuids.Add(guid);
            }
            else if (_bots.TryGetValue(guid, out var bot))
            {
                bot.Level = bs.Level;
                _tracker.UpdatePosition(guid, bs.ZoneId, bs.MapId, bs.X, bs.Y, bs.Z);
            }
        }

        // Evict bots that have been gone for the grace window.
        var disconnected = _initializedGuids
            .Where(g => !bridgeStates.ContainsKey(g) || bridgeStates[g].TaskState == "DISCONNECTED")
            .ToList();

        foreach (var guid in disconnected)
        {
            _disconnectedAt.TryAdd(guid, DateTime.UtcNow);

            if (_disconnectedAt.TryGetValue(guid, out var dcTime) &&
                (DateTime.UtcNow - dcTime).TotalSeconds >= EVICT_DISCONNECT_SEC)
            {
                _groupManager.RemoveFromGroup(guid);
                _bots.TryRemove(guid, out _);
                _contexts.TryRemove(guid, out _);
                _initializedGuids.Remove(guid);
                _disconnectedAt.TryRemove(guid, out _);
                _tracker.Remove(guid);
                _fallRecorder.Forget(guid);   // drop the bot's fall-ring so evicted guids don't leak memory
                _logger.LogInformation("BotBrain: evicted stale bot {Guid} (disconnected {Sec}s+)", guid, (int)EVICT_DISCONNECT_SEC);
            }
        }

        // Clear the disconnect timer for any bot that reconnected.
        var reconnected = _disconnectedAt.Keys
            .Where(g => bridgeStates.ContainsKey(g) && bridgeStates[g].TaskState != "DISCONNECTED")
            .ToList();
        foreach (var guid in reconnected)
            _disconnectedAt.TryRemove(guid, out _);
    }

    // ==================== Spine driver ====================

    /// <summary>
    /// One pass over the fleet. Every bot with a real STATE is sensed (so FleetReport
    /// stays live even when driving is off); when driving is enabled, BotBrain runs the
    /// full tick — Sense → hold Idle (Phase 1) → Supervisor deadline check.
    /// </summary>
    private async Task RunBrainTicksAsync()
    {
        // Grouping pre-pass (§3.2): the "god bot" stamps each grouped member's BotContext BEFORE the
        // per-bot ticks, so each tick consults a fresh stamp. Two seams: the combat directive
        // (Assist(anchor)) and the EXECUTION directive -- the union-chosen shared objective the team
        // grinds together, gated on all eligible holders finishing (needs the quest graph). Pure
        // decision+stamp -- it issues NO commands; the spine emits COMBAT_DIRECTIVE on change (BotBrain
        // step 1a) and the QuestPlanner consults the exec stamp. Only when driving; sensing-only skips it.
        if (_brainEnabled)
            GroupCoordinator.Update(_contexts, _groupManager, _quests, _safety, _spawns, _driver.QuestPlanner, _zoneData);

        foreach (var kvp in _contexts)
        {
            var bs = _bridge.GetBotState(kvp.Key);
            if (bs == null || bs.TaskState == "DISCONNECTED") continue;

            // Gate on the first real STATE; HELLO carries placeholder health/position.
            if (!bs.HasReceivedState) continue;

            var snap = BotStateSnapshot.FromBridgeState(bs);

            // [AUTHORSHIP] This body answers to a human — a real client is driving it, or it is
            // one of the owner's own characters (.sui companion add). SENSE it so the dashboard,
            // FleetReport and the fall recorder keep showing live position and health, but never
            // PLAN for it: a goal issued here would march someone's character off to grind while
            // they were playing it. C++ drops such commands too (COMPANION_DROP / POSSESSED_DROP),
            // but the brain should not be sending them in the first place — a dropped command
            // still burns a supervisor deadline and reads downstream as a stall.
            if (snap.IsPlayerDriven)
            {
                kvp.Value.Sense(snap);
                _fallRecorder.Observe(kvp.Value);
                continue;
            }

            if (_brainEnabled)
                await _driver.TickAsync(kvp.Value, snap);   // runs Sense → hold Idle → Supervise
            else
                kvp.Value.Sense(snap);                        // disabled: still sense so FleetReport stays live

            // Always-on void/fall black box: ctx.Pos is fresh (post-Sense) either way. Cheap; flushes only on a fall.
            _fallRecorder.Observe(kvp.Value);
        }
    }

    // ==================== Bot init ====================

    /// <summary>
    /// Initialize a new bot: load or roll personality, build the BotIdentity (dashboard +
    /// grouping), seed the BotContext (the spine's live state), enrich group membership,
    /// and auto-register in characters.playerbot for restart persistence.
    /// </summary>
    private async Task InitializeBotAsync(int guid, BotState bs)
    {
        // Try to load persisted personality from the admin DB.
        BotPersonality? personality = null;
        try
        {
            using var conn = _db.Admin();
            var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM bot_personality WHERE bot_guid = @Guid", new { Guid = guid });

            if (row != null)
            {
                personality = new BotPersonality
                {
                    Patience = (float)row.patience,
                    Greed = (float)row.greed,
                    Curiosity = (float)row.curiosity,
                    Sociability = (float)row.sociability,
                    Aggression = (float)row.aggression,
                    Efficiency = (float)row.efficiency,
                    Cautiousness = (float)row.cautiousness,
                    Indecisiveness = (float)row.indecisiveness,
                    Spontaneity = (float)row.spontaneity,
                    ChatStyle = (string)row.chat_style,
                    Temperament = (string)row.temperament,
                    Quirks = _quirkLoader.ResolveQuirkIds((string?)row.quirk_ids)
                };
                _logger.LogInformation("BotBrain: loaded persisted personality for {Name} (guid={Guid})", bs.Name, guid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotBrain: failed to load personality for bot {Guid} — will roll new", guid);
        }

        // Roll a new personality if none persisted.
        if (personality == null)
        {
            personality = PersonalityRoller.Roll(_quirkLoader.AllQuirks.ToList());
            await PersistPersonalityAsync(guid, personality);
            _logger.LogInformation("BotBrain: rolled new personality for {Name} (guid={Guid})", bs.Name, guid);
        }

        var bot = new BotIdentity
        {
            Guid = guid,
            Name = bs.Name,
            Race = bs.Race,
            ClassId = bs.ClassId,
            Faction = BotIdentity.FactionForRace(bs.Race),
            Level = bs.Level,
            Personality = personality,
            CurrentActivity = new ActivityState { Type = ActivityType.Idle, StartedAt = DateTime.UtcNow },
            NextDecisionTick = DateTime.UtcNow.AddSeconds(2),
            // Train-up-on-connect catch-up for the post-rebuild fleet (mid-level bots that never ran the
            // trainer pass are spell-starved) — but NOT for a fresh L1. An L1's trainables are trivial (most
            // starting spells are auto-granted), and flagging it makes every fresh spawn bee-line the trainer
            // before it ever quests: the GoalSelector training trigger outranks questing, so the whole fleet
            // routes to the (crowded / interior-pocket) trainer, wedges, and never leaves L1. An L1 quests
            // first and is re-flagged at its first LEVEL_UP (which catches the real L2+ ranks). Higher-level
            // connects still catch up immediately; the trainer-wedge give-up in BotBrain.TryBreakWedgeAsync
            // is the backstop if any trainer trip can't complete. Raise the floor if you ever want L1s to
            // train pre-quest.
            HasUnlearnedSpells = bs.Level > 1
        };
        bot.Story = new BotStoryRider(bot);   // demoted: toggleable but inert in the rebuild

        // Hydrate the durable completed-quest set from the character DB so a restart does NOT
        // re-offer already-rewarded quests (which inflates `av`, re-seeds finished content, and
        // makes chain prereqs read as un-done so follow-ups never unlock). rewarded=1 is the
        // TURN-IN flag -- deliberately NOT status==1 (COMPLETE-but-not-handed-in), so a quest
        // still waiting at its ender stays resumable. character_queststatus is in the CHARACTERS
        // db; guid is the character lowguid == the bot guid.
        try
        {
            using var qsConn = _db.Characters();
            var rewarded = await qsConn.QueryAsync<int>(
                "SELECT quest FROM character_queststatus WHERE guid = @Guid AND rewarded = 1",
                new { Guid = guid });
            foreach (var qid in rewarded)
                bot.CompletedQuestIds.Add(qid);
            _logger.LogInformation("BotBrain: hydrated {Count} completed quests for {Name} (guid={Guid})",
                bot.CompletedQuestIds.Count, bs.Name, guid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotBrain: failed to hydrate completed quests for bot {Guid}", guid);
        }

        _bots[guid] = bot;

        // Seed the spine's live state. Sensory fills in on the first STATE tick.
        _contexts[guid] = new BotContext
        {
            Guid = guid,
            Name = bs.Name,
            Level = bs.Level,
            Identity = bot          // P3: durable roster back-ref for the quest planner
        };

        _tracker.UpdatePosition(guid, bs.ZoneId, bs.MapId, bs.X, bs.Y, bs.Z);

        // Stamp group membership (from DB-loaded groups).
        _groupManager.EnrichBotIdentity(bot);

        // Auto-register in characters.playerbot for restart persistence.
        try
        {
            using var charConn = _db.Characters();

            // [SUI] HARD WALL (2026-08-10, Tesfff): an unattended REAL character
            // enrolls in the fleet like any bot, but must NEVER be persisted to the
            // playerbot registry — the next mangosd restart would respawn it as a
            // fabricated bot on a synthetic account, SaveToDB would stamp that
            // account over the owner's, and the character vanishes from their
            // account list. A character whose account exists in realmd is real.
            var realOwner = await charConn.QueryFirstOrDefaultAsync<int?>(
                "SELECT c.account FROM characters c WHERE c.guid = @Guid AND c.account IN (SELECT id FROM realmd.account)",
                new { Guid = guid });
            if (realOwner != null)
            {
                _logger.LogWarning(
                    "BotBrain: NOT registering {Name} (guid={Guid}) — real character on account {Account}",
                    bs.Name, guid, realOwner);
                return;
            }

            var existing = await charConn.QueryFirstOrDefaultAsync<int?>(
                "SELECT char_guid FROM playerbot WHERE char_guid = @Guid", new { Guid = guid });

            if (existing == null)
            {
                await charConn.ExecuteAsync(@"
                    INSERT INTO playerbot (char_guid, chance, ai, name, race, `class`, level, map, position_x, position_y, position_z)
                    VALUES (@Guid, 100, 'AiBotAI', @Name, @Race, @Class, @Level, @Map, @X, @Y, @Z)",
                    new
                    {
                        Guid = guid,
                        Name = bs.Name,
                        Race = bs.Race,
                        Class = bs.ClassId,
                        Level = bs.Level,
                        Map = bs.MapId,
                        X = bs.X,
                        Y = bs.Y,
                        Z = bs.Z
                    });
                _logger.LogInformation("BotBrain: auto-registered {Name} (guid={Guid}) in playerbot table", bs.Name, guid);
            }
            else
            {
                await charConn.ExecuteAsync(@"
                    UPDATE playerbot SET name=@Name, level=@Level, map=@Map,
                           position_x=@X, position_y=@Y, position_z=@Z
                    WHERE char_guid = @Guid",
                    new
                    {
                        Guid = guid,
                        Name = bs.Name,
                        Level = bs.Level,
                        Map = bs.MapId,
                        X = bs.X,
                        Y = bs.Y,
                        Z = bs.Z
                    });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotBrain: failed to auto-register bot {Guid} in playerbot table", guid);
        }
    }

    // ==================== Grouping mode load ====================

    private async Task LoadGroupingModeAsync()
    {
        try
        {
            using var conn = _db.Admin();
            var value = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT setting_value FROM bot_settings WHERE setting_key = 'grouping_mode'");

            if (value != null && int.TryParse(value, out int mode) && Enum.IsDefined(typeof(GroupingMode), mode))
            {
                _groupManager.Mode = (GroupingMode)mode;
                _logger.LogInformation("BotBrain: loaded grouping mode from DB: {Mode}", _groupManager.Mode);
            }
            else
            {
                _groupManager.Mode = GroupingMode.Off;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotBrain: failed to load grouping mode, defaulting to Off");
            _groupManager.Mode = GroupingMode.Off;
        }
    }

    // ==================== Personality persistence ====================

    private async Task PersistPersonalityAsync(int guid, BotPersonality p)
    {
        try
        {
            using var conn = _db.Admin();
            await conn.ExecuteAsync(@"
                INSERT INTO bot_personality
                    (bot_guid, patience, greed, curiosity, sociability, aggression, efficiency,
                     cautiousness, indecisiveness, spontaneity, chat_style, temperament, quirk_ids)
                VALUES
                    (@Guid, @Patience, @Greed, @Curiosity, @Sociability, @Aggression, @Efficiency,
                     @Cautiousness, @Indecisiveness, @Spontaneity, @ChatStyle, @Temperament, @QuirkIds)
                ON DUPLICATE KEY UPDATE
                    patience=@Patience, greed=@Greed, curiosity=@Curiosity,
                    sociability=@Sociability, aggression=@Aggression, efficiency=@Efficiency,
                    cautiousness=@Cautiousness, indecisiveness=@Indecisiveness,
                    spontaneity=@Spontaneity, chat_style=@ChatStyle, temperament=@Temperament,
                    quirk_ids=@QuirkIds",
                new
                {
                    Guid = guid,
                    p.Patience,
                    p.Greed,
                    p.Curiosity,
                    p.Sociability,
                    p.Aggression,
                    p.Efficiency,
                    p.Cautiousness,
                    p.Indecisiveness,
                    p.Spontaneity,
                    p.ChatStyle,
                    p.Temperament,
                    QuirkIds = string.Join(",", p.Quirks.Select(q => q.Id))
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotBrain: failed to persist personality for bot {Guid}", guid);
        }
    }

    // ==================== Core schema self-heal ====================

    /// <summary>
    /// Add the fork's identity columns to characters.playerbot if they're missing.
    /// The base VMaNGOS table has only (char_guid, chance, ai); the fork reads/writes
    /// name/race/class/level/map/position_x/y/z. Each column is added independently and
    /// guarded against information_schema, so this is idempotent and survives a partial
    /// (some-columns-already-present) state. Portable across MySQL 5.6/5.7/8.0 and
    /// MariaDB — deliberately NOT using "ADD COLUMN IF NOT EXISTS" (MariaDB-only).
    /// AFTER clauses are omitted on purpose: column position is cosmetic and pinning it
    /// to a base column ("AFTER ai") would fail on any schema variant that renamed it.
    /// </summary>
    private async Task EnsurePlayerbotColumnsAsync()
    {
        // (column name, DDL type spec). `class` is a reserved word -> backticked in DDL,
        // but the information_schema lookup uses the bare name.
        var columns = new (string Name, string Ddl)[]
        {
            ("name",       "`name` VARCHAR(12) NOT NULL DEFAULT ''"),
            ("race",       "`race` TINYINT UNSIGNED NOT NULL DEFAULT 0"),
            ("class",      "`class` TINYINT UNSIGNED NOT NULL DEFAULT 0"),
            ("level",      "`level` TINYINT UNSIGNED NOT NULL DEFAULT 1"),
            ("map",        "`map` SMALLINT UNSIGNED NOT NULL DEFAULT 0"),
            ("position_x", "`position_x` FLOAT NOT NULL DEFAULT 0"),
            ("position_y", "`position_y` FLOAT NOT NULL DEFAULT 0"),
            ("position_z", "`position_z` FLOAT NOT NULL DEFAULT 0"),
        };

        try
        {
            using var conn = _db.Characters();
            int added = 0;

            foreach (var (name, ddl) in columns)
            {
                if (await ColumnExistsAsync(conn, "playerbot", name))
                    continue;

                await conn.ExecuteAsync($"ALTER TABLE playerbot ADD COLUMN {ddl}");
                added++;
                _logger.LogInformation("BotBrain: added missing playerbot.{Column} column", name);
            }

            if (added > 0)
                _logger.LogInformation("BotBrain: playerbot schema self-heal added {Count} column(s)", added);
        }
        catch (Exception ex)
        {
            // Non-fatal: log loudly. If this fails (e.g. permissions), bot load will fail
            // later with the same 1054 and the operator gets a second, pointed signal.
            _logger.LogError(ex, "BotBrain: failed to self-heal characters.playerbot schema — bot load may fail with unknown-column errors until the identity columns exist");
        }
    }

    /// <summary>
    /// Portable column-existence check against information_schema. DATABASE() resolves
    /// to the connection's current schema (characters), so no DB name needs threading in.
    /// </summary>
    private static async Task<bool> ColumnExistsAsync(System.Data.IDbConnection conn, string table, string column)
    {
        var count = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM information_schema.COLUMNS
              WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @column",
            new { table, column });
        return count > 0;
    }
}