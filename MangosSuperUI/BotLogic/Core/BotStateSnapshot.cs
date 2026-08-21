namespace MangosSuperUI.BotLogic.Core;

using MangosSuperUI.BotLogic.Data;   // QuestLogEntry (the quest-log snapshot now rides on STATE)

/// <summary>
/// Lightweight snapshot populated from the most recent bridge STATE message.
/// Passed into every domain method so they don't need to query the bridge themselves.
/// </summary>
public class BotStateSnapshot
{
    // From STATE message
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public int Mana { get; set; }
    public int MaxMana { get; set; }
    public int Level { get; set; }
    public int MapId { get; set; }
    public int ZoneId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public bool InCombat { get; set; }
    public bool IsDead { get; set; }
    public long TargetGuid { get; set; }

    // Enriched STATE fields (Session 6 C++ � freeSlots/totalSlots/copper)
    public uint FreeSlots { get; set; } = 16;
    public uint TotalSlots { get; set; } = 16;
    public uint Copper { get; set; } = 0;

    // Min equipped-slot durability % (100 = full / no damageable gear). Added to STATE
    // alongside the at_graveyard rez; drives the durability-repair errand.
    public uint Durability { get; set; } = 100;

    // [HUB-ERRAND] The "do your rounds" run token (2026-07-08 §3). Stamped on BotState by the
    // BotBridgeService CHAT_RECV recognizer (boss party-chat), NOT by the STATE parse — it
    // persists across STATEs on the connection object and simply rides FromBridgeState into
    // every snapshot. Null = no errand armed / "lets move" cleared it. The GoalSelector runs
    // the errand goal only while this is live AND != ctx.HubErrandDone (the consumed-token
    // check), so each command runs exactly once and expiry auto-reverts to the follow hold.
    public DateTime? HubErrandUntil { get; set; }

    // [HUB-ERRAND] Distance to the party boss (C++ ppdist on STATE, 2026-07-08): -1 = no boss
    // resolved, 99999 = boss on ANOTHER map (instance/boat), else 3D yards. Feeds the errand
    // abort guard (boss >150yd / off-map -> drop the rounds, resume follow). Up to one 5s
    // STATE cycle stale, which is fine for a 150yd gate.
    public int PartyBossDist { get; set; } = -1;

    // [PLAYERPARTY] This bot's group contains a REAL player (C++ pparty on STATE, 2026-07-07).
    // A human invited the bot in-game; C++ owns the whole escort behaviour (PlayerParty
    // doctrine: follow + assist), and C# stands down — GoalSelector holds Goal.Idle on this
    // flag ("player-party"). Server truth, NOT the C# GroupManager (which only knows about
    // groups the god-bot formed).
    public bool InPlayerParty { get; set; } = false;

    // [AUTHORSHIP] This body answers to a human, not to the brain: a real client is driving
    // it (Possessed) or it is one of the owner's own characters enrolled with
    // `.sui companion add` (IsCompanion). Distinct from InPlayerParty, which only says a human
    // shares the group — a fabricated bot escorting a player is still the brain's to plan for.
    // These two mean the brain has no authorship at all: sense, never plan. See
    // BotBrainService.RunBrainTicksAsync.
    public bool Possessed { get; set; } = false;
    public bool IsCompanion { get; set; } = false;
    public bool IsPlayerDriven => Possessed || IsCompanion;

    // Computed
    public float HealthPercent => MaxHealth > 0 ? Health / (float)MaxHealth : 1f;
    public float ManaPercent => MaxMana > 0 ? Mana / (float)MaxMana : 1f;
    public float BagFullness => TotalSlots > 0 ? (TotalSlots - FreeSlots) / (float)TotalSlots : 0f;

    // Enriched by BotStateTracker
    public long XP { get; set; }
    public long XPToNextLevel { get; set; }
    public bool IsNearTown { get; set; }
    public int NearbyPlayerCount { get; set; }
    public int NearbyBotCount { get; set; }

    // Server-side quest status (from C++ GetQuestStatus � authoritative)
    public uint ServerQuestId { get; set; } = 0;
    public uint ServerQuestStatus { get; set; } = 0;

    // --- Group state (Session 31 — enriched by BotBrainService from GroupManager + bridge) ---
    public int? GroupId { get; set; }
    public int? GroupLeaderGuid { get; set; }
    public float? LeaderX { get; set; }
    public float? LeaderY { get; set; }
    public float? LeaderZ { get; set; }
    public bool IsGrouped => GroupId.HasValue;

    // --- C++ held-task echo (Held-Objective build §4) — what C++ reports running right now ---
    // Unknown until the C++ STATE echo lands (Session 3). The bridge's STATE parse fills this from the
    // task_* fields in the producer movement; until then it stays Unknown and the reconcile is a no-op
    // (FromBridgeState does not set it, so the default applies).
    public HeldTaskEcho HeldTask { get; set; } = HeldTaskEcho.Unknown;

    // --- Full quest-log snapshot, pushed on STATE (retired the QUERY_QUEST_STATUS pull) ---
    // The authoritative mirror of the C++ player quest log (me->GetQuestStatusMap()), parsed from the
    // STATE "quests" blob. ctx.QuestLog is set from this in Sense every tick, so the planner always reads
    // ground truth and never a stale/partial/empty request-reply cache. StateUtc is when this STATE landed
    // (bs.LastUpdate) — the freshness clock the objective re-derive (obj_sync) gates on.
    public Dictionary<int, QuestLogEntry> QuestLog { get; set; } = new();
    public DateTime StateUtc { get; set; }

    /// <summary>
    /// Build a snapshot from the existing BotState (BotBridgeService model).
    /// </summary>
    public static BotStateSnapshot FromBridgeState(Services.BotState bs)
    {
        return new BotStateSnapshot
        {
            Health = bs.Health,
            MaxHealth = bs.MaxHealth,
            Mana = bs.Mana,
            MaxMana = bs.MaxMana,
            Level = bs.Level,
            MapId = bs.MapId,
            ZoneId = bs.ZoneId,
            X = bs.X,
            Y = bs.Y,
            Z = bs.Z,
            InCombat = bs.InCombat,
            IsDead = bs.IsDead,
            TargetGuid = bs.TargetGuid,
            FreeSlots = bs.FreeSlots,
            TotalSlots = bs.TotalSlots,
            Copper = bs.Copper,
            Durability = bs.Durability,
            InPlayerParty = bs.InPlayerParty,   // [PLAYERPARTY] pparty on STATE — needs the BotState parse in BotBridgeService
            Possessed = bs.Possessed,           // [AUTHORSHIP] possessed on STATE
            IsCompanion = bs.IsCompanion,       // [AUTHORSHIP] companion on STATE
            HubErrandUntil = bs.HubErrandUntil, // [HUB-ERRAND] run token — stamped by the CHAT_RECV recognizer, persists on conn.State
            PartyBossDist = bs.PartyBossDist,   // [HUB-ERRAND] ppdist on STATE — the boss-range abort guard
            ServerQuestId = bs.QuestId,
            ServerQuestStatus = bs.QuestStatus,
            HeldTask = ParseHeldTask(bs),
            QuestLog = ParseQuestLog(bs.Quests),   // full quest-log snapshot off STATE (retired pull)
            StateUtc = bs.LastUpdate                // when this STATE landed → the obj-resync freshness clock
        };
    }

    // Parse the pipe-delimited quest-log snapshot carried on STATE (was the QUEST_STATUS_ALL payload):
    //   questId:status:mob0,mob1,mob2,mob3:item0,item1,item2,item3 | questId:...
    // status: COMPLETE=1, INCOMPLETE=3 (VMaNGOS enum). Empty/blank => the bot holds no quests (a real,
    // authoritative "zero", because C++ pushes the full map every heartbeat — there is no stale-cache
    // ambiguity to guard against anymore). Builds a fresh dictionary the caller assigns onto ctx.QuestLog.
    private static Dictionary<int, QuestLogEntry> ParseQuestLog(string? data)
    {
        var log = new Dictionary<int, QuestLogEntry>();
        if (string.IsNullOrWhiteSpace(data)) return log;

        foreach (var part in data.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = part.Split(':');
            if (f.Length < 2) continue;
            if (!int.TryParse(f[0].Trim(), out int qid)) continue;
            if (!int.TryParse(f[1].Trim(), out int status)) continue;

            var mob = new int[4];
            if (f.Length >= 3)
            {
                var mc = f[2].Split(',');
                for (int i = 0; i < 4 && i < mc.Length; i++)
                    int.TryParse(mc[i].Trim(), out mob[i]);
            }

            var item = new int[4];
            if (f.Length >= 4)
            {
                var ic = f[3].Split(',');
                for (int i = 0; i < 4 && i < ic.Length; i++)
                    int.TryParse(ic[i].Trim(), out item[i]);
            }

            log[qid] = new QuestLogEntry { Status = status, MobCounts = mob, ItemCounts = item };
        }
        return log;
    }

    // Build the C++ held-task echo from STATE (Held-Objective build §4). GATED on TaskActivity being
    // non-empty: a pre-Session-3 binary sends no activity → HeldTaskEcho.Unknown → the reconcile is a
    // no-op (today's behavior). Once C++ emits taskActivity, the kind comes from the existing taskState
    // string, the headway from taskActivity, plus the target creature / MOVE_TO dest / kill count.
    private static HeldTaskEcho ParseHeldTask(Services.BotState bs)
    {
        if (string.IsNullOrWhiteSpace(bs.TaskActivity))
            return HeldTaskEcho.Unknown;   // no readback yet

        var kind = ParseTaskKind(bs.TaskKind);   // committed task kind — NOT bs.TaskState (a display status)
        var activity = ParseTaskActivity(bs.TaskActivity);
        var dest = new Vec4(bs.TaskDestX, bs.TaskDestY, bs.TaskDestZ, bs.MapId);
        return new HeldTaskEcho(kind, activity, (int)bs.TaskCreature, dest, bs.TaskKills);
    }

    private static HeldTaskKind ParseTaskKind(string s) => (s ?? "").Trim().ToUpperInvariant() switch
    {
        "GRIND" => HeldTaskKind.Grind,
        "MOVE_TO" or "MOVE" or "MOVETO" => HeldTaskKind.MoveTo,
        "INTERACT" => HeldTaskKind.Interact,
        "IDLE" or "" => HeldTaskKind.Idle,
        _ => HeldTaskKind.Idle
    };

    private static TaskActivity ParseTaskActivity(string s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "traveling" or "travelling" => Core.TaskActivity.Traveling,
        "searching" => Core.TaskActivity.Searching,
        "engaged" => Core.TaskActivity.Engaged,
        "recovering" => Core.TaskActivity.Recovering,
        "blocked" => Core.TaskActivity.Blocked,
        "idle" => Core.TaskActivity.Idle,
        _ => Core.TaskActivity.Unknown
    };
}