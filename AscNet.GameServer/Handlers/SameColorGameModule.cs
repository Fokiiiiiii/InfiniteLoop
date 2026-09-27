using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.samecolorgame;
using AscNet.Table.V2.share.task;
using MessagePack;
// Both share/task and share/condition define a ConditionTable; the mode's gates come from the
// shared condition table, so the alias is explicit rather than relying on using order.
using ConditionTable = AscNet.Table.V2.share.condition.ConditionTable;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

/// <summary>Action stream verbs, from XEnumConst.SAME_COLOR_GAME.ACTION_TYPE.</summary>
internal static class SameColorGameActionType
{
    public const int None = 0;
    public const int MapInit = 1;
    public const int ItemRemove = 2;
    public const int ItemDrop = 3;
    public const int ItemCreateNew = 4;
    public const int MapShuffle = 5;
    public const int GameInterrupt = 6;
    public const int SettleScore = 7;
    public const int ItemSwap = 8;
    public const int StepAdd = 9;
    public const int StepSub = 10;
    public const int ItemChangeColor = 11;
    public const int BuffAdd = 12;
    public const int BuffRemove = 13;
    public const int BossReleaseSkill = 14;
    public const int BossSkipSkill = 15;
    public const int EnergyChange = 16;
    public const int SkillCooldownChange = 17;
    public const int LeftTimeChange = 18;
    public const int BuffLeftTimeChange = 19;
    public const int MapReset = 20;
    public const int TimeAdd = 21;
    public const int ItemSwapEx = 22;
    public const int PropCreateNew = 23;
    public const int ItemTransform = 24;
    public const int PropTrigger = 25;
    public const int WeakHit = 26;
    public const int NewMapShuffle = 27;
}

/// <summary>Energy change reasons, from XEnumConst.SAME_COLOR_GAME.ENERGY_CHANGE_FROM.</summary>
internal static class SameColorGameEnergyReason
{
    public const int UseSkill = 1;
    public const int Boss = 2;
    public const int Combo = 3;
    public const int Buff = 4;
    public const int Round = 5;
}

[MessagePackObject(true)]
public sealed class SameColorGamePosition { public int PositionX { get; set; } public int PositionY { get; set; } }

[MessagePackObject(true)]
public sealed class SameColorGameUseSkillTarget { public int ItemId { get; set; } public int PositionX { get; set; } public int PositionY { get; set; } }

[MessagePackObject(true)]
public sealed class SameColorGameUseSkillItemParam
{
    public SameColorGameUseSkillTarget? Item1 { get; set; }
    public SameColorGameUseSkillTarget? Item2 { get; set; }
}

[MessagePackObject(true)]
public sealed class SameColorGameBallRecord
{
    public int ItemId { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public int BallType { get; set; }
    public int ItemType { get; set; }
    public int WeakUid { get; set; }
    public int WeakHitTimes { get; set; }
    public List<int>? BuffUids { get; set; }
}

[MessagePackObject(true)]
public sealed class SameColorGameDropRecord
{
    public int ItemId { get; set; }
    public int StartPositionX { get; set; }
    public int StartPositionY { get; set; }
    public int EndPositionX { get; set; }
    public int EndPositionY { get; set; }
}

[MessagePackObject(true)]
public sealed class SameColorGameComboRecord { public int ItemId { get; set; } public int Combo { get; set; } }

[MessagePackObject(true)]
public sealed class SameColorGamePassiveDamage { public int BuffId { get; set; } public int BuffUid { get; set; } public int TargetColor { get; set; } }

[MessagePackObject(true)]
public sealed class SameColorGamePropStatRecord { public int GainCount { get; set; } public int UseCount { get; set; } }

[MessagePackObject(true)]
public sealed class SameColorGameWeakInfoRecord
{
    public int ItemId { get; set; }
    public int CreateRound { get; set; }
    public List<int> UpdateRound { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class SameColorGameAction
{
    public int ActionType { get; set; }
    public int BossId { get; set; }
    public int CurRound { get; set; }
    public int CurrentBossId { get; set; }
    public int Step { get; set; }
    public int BuffUid { get; set; }
    public int BuffId { get; set; }
    public int CurrentCombo { get; set; }
    public long CurrentScore { get; set; }
    public long TotalScore { get; set; }
    public int TotalCombo { get; set; }
    public int CurrentBallCount { get; set; }
    public int IsLastRound { get; set; }
    public int BossSkillId { get; set; }
    public int EnergyChange { get; set; }
    public int EnergyChangeType { get; set; }
    public int SkillId { get; set; }
    public int LeftCd { get; set; }
    public List<SameColorGameBallRecord>? ItemList { get; set; }
    public SameColorGameBallRecord? Source { get; set; }
    public SameColorGameBallRecord? Destination { get; set; }
    public List<SameColorGameBallRecord>? Sources { get; set; }
    public List<SameColorGameBallRecord>? Destinations { get; set; }
    public List<SameColorGameDropRecord>? DropItemList { get; set; }
    public List<SameColorGameComboRecord>? ComboRecord { get; set; }
    public long ObtainEnergy { get; set; }
    public long SkillCostEnergy { get; set; }
    public long BossCostEnergy { get; set; }
    public Dictionary<int, int>? SkillUseCount { get; set; }
    public Dictionary<int, int>? ColorRemoveCount { get; set; }
    public Dictionary<int, Dictionary<int, SameColorGamePropStatRecord>>? PropInfoDict { get; set; }
    public Dictionary<int, SameColorGameWeakInfoRecord>? WeakInfoDict { get; set; }
    public long GameStartTime { get; set; }
    public int LeftTime { get; set; }
    public int CurTimeline { get; set; }
    public List<SameColorGamePassiveDamage>? PassiveSkillMoreDamages { get; set; }
    public double PassiveSkillMoreScoreFactor { get; set; }
    public int TotalGameCombo { get; set; }
    public int Center { get; set; }
    public int InitType { get; set; }
}

[MessagePackObject(true)]
public sealed class SameColorGameEnterStageRequest { public int BossId { get; set; } public int RoleId { get; set; } public List<int>? SkillIds { get; set; } }
[MessagePackObject(true)]
public sealed class SameColorGameEnterStageResponse { public int Code { get; set; } public List<SameColorGameAction> Actions { get; set; } = new(); }
[MessagePackObject(true)]
public sealed class SameColorGameSwapItemRequest { public SameColorGamePosition? Source { get; set; } public SameColorGamePosition? Destination { get; set; } }
[MessagePackObject(true)]
public sealed class SameColorGameSwapItemResponse { public int Code { get; set; } public List<SameColorGameAction> Actions { get; set; } = new(); }
[MessagePackObject(true)]
public sealed class SameColorGameUseItemRequest
{
    public int GroupId { get; set; }
    public int ItemId { get; set; }
    public SameColorGameUseSkillItemParam? UseSkillItemParam { get; set; }
}
[MessagePackObject(true)]
public sealed class SameColorGameUseItemResponse { public int Code { get; set; } public List<SameColorGameAction> Actions { get; set; } = new(); }
[MessagePackObject(true)]
public sealed class SameColorGameCancelUseItemRequest { public int GroupId { get; set; } public int ItemId { get; set; } }
[MessagePackObject(true)]
public sealed class SameColorGameCancelUseItemResponse { public int Code { get; set; } public List<SameColorGameAction> Actions { get; set; } = new(); }
[MessagePackObject(true)]
public sealed class SameColorGameGiveUpRequest { public int BossId { get; set; } }
[MessagePackObject(true)]
public sealed class SameColorGameGiveUpResponse { public int Code { get; set; } }
[MessagePackObject(true)]
public sealed class SameColorGamePauseResumeRequest { }
[MessagePackObject(true)]
public sealed class SameColorGamePauseResumeResponse { public int Code { get; set; } }
[MessagePackObject(true)]
public sealed class SameColorGameCountDownRequest { }
[MessagePackObject(true)]
public sealed class SameColorGameCountDownResponse { public int Code { get; set; } }
[MessagePackObject(true)]
public sealed class SameColorGameOpenRankRequest { public int BossId { get; set; } }

[MessagePackObject(true)]
public sealed class SameColorGameRankInfo
{
    public int Rank { get; set; }
    public int MemberCount { get; set; }
    public long Score { get; set; }
    public long PlayerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public long HeadPortraitId { get; set; }
    public long HeadFrameId { get; set; }
    public int RoleId { get; set; }
    public List<int> RoleSkillId { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class SameColorGameOpenRankResponse
{
    public int Code { get; set; }
    public List<SameColorGameRankInfo> RankList { get; set; } = new();
    public SameColorGameRankInfo MyRankInfo { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class SameColorGameBossRecordData
{
    public int BossId { get; set; }
    public long MaxPoint { get; set; }
    public int MaxCombo { get; set; }
    public int LastUseRoleId { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifySameColorGameData
{
    public int ActivityId { get; set; }
    public List<SameColorGameBossRecordData> BossRecords { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifySameColorGameUpdate { public List<SameColorGameAction> Actions { get; set; } = new(); }

/// <summary>Accumulates one response's actions and the aggregates the settlement needs.</summary>
internal sealed class SameColorResponseContext
{
    internal List<SameColorGameAction> Actions { get; } = new();
    internal decimal RawScore;
    internal int Combo;
    internal int RemovedCount;
    internal bool HasScore;
    internal long MoveDamage;
    internal int PassiveColour;
    internal decimal PassiveColourDelta;
    internal decimal PassiveScoreFactor;
    internal bool CriticalMove;
    // One-shot: the next removal step is a direct effect of the in-use skill and must carry an
    // attribution marker. The client only attributes an action to the in-use skill when one of that
    // action's own balls has a non-zero ItemType, and downstream cascades must not inherit it.
    internal bool AttributeNextRemoval;
    internal Dictionary<int, int> ComboByBall { get; } = new();
    internal Dictionary<int, int> ColourRemoveCount { get; } = new();
    internal HashSet<int> AdjacencyHitWeakUids { get; } = new();
    // Cells cleared by a step whose adjacency walk is deferred until the owning effect resolves.
    internal HashSet<int> PendingAdjacency { get; } = new();
    internal List<SameColorGamePassiveDamage> PassiveDamages { get; } = new();
}

/// <summary>
/// SameColorGame (Circuit Connect) server: eight mode RPCs, the board engine entry points and the
/// shared task/rank integration surface.
/// </summary>
internal static class SameColorGameModule
{
    #region Error codes

    // Authored mode codes from CodeText.json 20143001-20143034. Only the codes this implementation
    // can actually return are declared; the rest of the authored range (service unavailable, energy
    // full, buff mismatch, no cooldown, swap count, countdown running/not running, paused, time
    // exhausted, skill ended) describes states the current seven-role round-mode closure cannot
    // reach, so they are never fabricated.
    internal const int InternalError = 20143001;
    internal const int MapInitializeError = 20143002;
    internal const int CoordinateError = 20143003;
    internal const int MissingItem = 20143004;
    internal const int SwapFailed = 20143005;
    internal const int ActivityUnavailable = 20143006;
    internal const int UnknownBoss = 20143008;
    internal const int BossLocked = 20143009;
    internal const int UnknownRole = 20143011;
    internal const int RoleLocked = 20143012;
    internal const int SkillLocked = 20143013;
    internal const int TurnExhausted = 20143014;
    internal const int SkillUnknown = 20143015;
    internal const int SkillCooldown = 20143016;
    internal const int SkillParamError = 20143017;
    internal const int BossMismatch = 20143018;
    internal const int EnergyNotEnough = 20143019;
    internal const int CannotCancel = 20143020;
    internal const int NotTimedStage = 20143026;
    internal const int SkillConfigError = 20143029;
    internal const int SkillEffectError = 20143033;
    internal const int SummonCannotSwap = 20143034;

    #endregion

    private const int FunctionId = 10413;
    private const int RankLimit = 100;
    private const int BestGradeConditionType = 69005;
    private const int TotalScoreConditionType = 69002;
    private const int BossUnlockConditionType = 17426;

    private static readonly Lazy<List<SameColorGameActivityTable>> Activities =
        new(() => TableReaderV2.Parse<SameColorGameActivityTable>().OrderBy(row => row.Id).ToList());
    private static readonly Lazy<Dictionary<int, ConditionTable>> Conditions =
        new(() => TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, FunctionalOpenTable>> Functions =
        new(() => TableReaderV2.Parse<FunctionalOpenTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, List<int>>> TaskIdsByTimeId = new(() =>
        TableReaderV2.Parse<TaskTimeLimitTable>()
            .Where(row => row.TimeId is > 0)
            .GroupBy(row => row.TimeId!.Value)
            .ToDictionary(group => group.Key, group => group.SelectMany(row => row.TaskId).Distinct().Order().ToList()));
    private static readonly Lazy<List<SameColorGameBossGradeTable>> BossGrades =
        new(() => TableReaderV2.Parse<SameColorGameBossGradeTable>().OrderBy(row => row.BossId).ThenBy(row => row.Damage.GetValueOrDefault()).ToList());

    #region Shared task integration surface

    /// <summary>Login notification: activity id plus this player's durable boss records.</summary>
    internal static NotifySameColorGameData BuildLoginData(Player player)
    {
        SameColorGameState state = player.SameColorGame ??= new SameColorGameState();
        int activeActivityId = ActiveActivityId;
        // Boss records, total score and task progress belong to one activity. When the published
        // activity differs from the stored one the scoreboard starts over, and the login push reports
        // the current activity so the client selects the right content.
        if (activeActivityId != 0 && state.ActivityId != 0 && state.ActivityId != activeActivityId)
            state = player.SameColorGame = new SameColorGameState { ActivityId = activeActivityId };

        return new NotifySameColorGameData
        {
            ActivityId = state.ActivityId != 0 ? state.ActivityId : activeActivityId,
            BossRecords = state.BossRecords
                .Select(record => new SameColorGameBossRecordData
                {
                    BossId = record.BossId,
                    MaxPoint = record.MaxPoint,
                    MaxCombo = record.MaxCombo,
                    LastUseRoleId = record.LastUseRoleId
                })
                .ToList()
        };
    }

    /// <summary>Current activity, or 0 when no activity row is inside its configured window.</summary>
    internal static int ActiveActivityId => ActiveActivity()?.Id ?? 0;

    /// <summary>Task ids of the current activity's task groups (TimeId of the activity row).</summary>
    internal static IReadOnlyCollection<int> GetTaskIds()
    {
        if (ActiveActivity()?.TimerId is not int timeId || timeId <= 0) return Array.Empty<int>();
        return TaskIdsByTimeId.Value.TryGetValue(timeId, out List<int>? taskIds) ? taskIds : Array.Empty<int>();
    }

    /// <summary>
    /// Task condition progress. 69005 [bossId, grade] reports the boss's achieved grade, 69002
    /// [threshold] reports the accumulated completed-run total.
    /// </summary>
    internal static long GetConditionProgress(Player player, int conditionType, IReadOnlyList<int> parameters)
    {
        SameColorGameState state = player.SameColorGame ??= new SameColorGameState();
        return conditionType switch
        {
            BestGradeConditionType when parameters.Count > 0 => AchievedGrade(state, parameters[0]),
            TotalScoreConditionType => state.TotalScore,
            _ => 0
        };
    }

    internal static bool IsTaskAvailable(Player player, int taskId) => GetTaskIds().Contains(taskId);

    internal static string GetTaskClaimKey(Player player, int taskId) => $"samecolorgame:{ActiveActivityId}:{taskId}";

    internal static long AchievedGrade(SameColorGameState state, int bossId)
    {
        long maxPoint = state.BossRecords.FirstOrDefault(record => record.BossId == bossId)?.MaxPoint ?? 0;
        long grade = 0;
        foreach (SameColorGameBossGradeTable row in BossGrades.Value.Where(row => row.BossId == bossId))
        {
            if (row.Damage.GetValueOrDefault() > maxPoint) continue;
            if (row.Grade > grade) grade = row.Grade;
        }
        return grade;
    }

    internal static long GetBossMaxPoint(Player player, int bossId) =>
        (player.SameColorGame ??= new SameColorGameState()).BossRecords
            .FirstOrDefault(record => record.BossId == bossId)?.MaxPoint ?? 0;

    #endregion

    #region Handlers

    [RequestPacketHandler("SameColorGameEnterStageRequest")]
    public static void Enter(Session session, Packet.Request packet)
    {
        SameColorGameEnterStageRequest request = packet.Deserialize<SameColorGameEnterStageRequest>();
        SameColorResponseContext ctx = new();
        int code = RunGuarded(session, ctx, () => TryEnter(session, request, ctx));
        session.SendResponse(new SameColorGameEnterStageResponse { Code = code, Actions = ctx.Actions }, packet.Id);
    }

    [RequestPacketHandler("SameColorGameSwapItemRequest")]
    public static void Swap(Session session, Packet.Request packet)
    {
        SameColorGameSwapItemRequest request = packet.Deserialize<SameColorGameSwapItemRequest>();
        SameColorResponseContext ctx = new();
        int code = RunGuarded(session, ctx, () => TrySwap(session, request, ctx));
        session.SendResponse(new SameColorGameSwapItemResponse { Code = code, Actions = ctx.Actions }, packet.Id);
    }

    [RequestPacketHandler("SameColorGameUseItemRequest")]
    public static void UseItem(Session session, Packet.Request packet)
    {
        SameColorGameUseItemRequest request = packet.Deserialize<SameColorGameUseItemRequest>();
        SameColorResponseContext ctx = new();
        int code = RunGuarded(session, ctx, () => TryUseItem(session, request, ctx));
        session.SendResponse(new SameColorGameUseItemResponse { Code = code, Actions = ctx.Actions }, packet.Id);
    }

    [RequestPacketHandler("SameColorGameCancelUseItemRequest")]
    public static void CancelUseItem(Session session, Packet.Request packet)
    {
        SameColorGameCancelUseItemRequest request = packet.Deserialize<SameColorGameCancelUseItemRequest>();
        SameColorResponseContext ctx = new();
        int code = RunGuarded(session, ctx, () => TryCancelUseItem(session, request));
        session.SendResponse(new SameColorGameCancelUseItemResponse { Code = code, Actions = ctx.Actions }, packet.Id);
    }

    [RequestPacketHandler("SameColorGameGiveUpRequest")]
    public static void GiveUp(Session session, Packet.Request packet)
    {
        SameColorGameGiveUpRequest request = packet.Deserialize<SameColorGameGiveUpRequest>();
        int code = RunGuarded(session, null, () => TryGiveUp(session, request.BossId));
        session.SendResponse(new SameColorGameGiveUpResponse { Code = code }, packet.Id);
    }

    [RequestPacketHandler("SameColorGamePauseResumeRequest")]
    public static void PauseResume(Session session, Packet.Request packet)
    {
        packet.Deserialize<SameColorGamePauseResumeRequest>();
        int code = RunGuarded(session, null, () => TryPauseResume(session));
        session.SendResponse(new SameColorGamePauseResumeResponse { Code = code }, packet.Id);
    }

    [RequestPacketHandler("SameColorGameCountDownRequest")]
    public static void CountDown(Session session, Packet.Request packet)
    {
        packet.Deserialize<SameColorGameCountDownRequest>();
        int code = RunGuarded(session, null, () => TryCountDown(session));
        session.SendResponse(new SameColorGameCountDownResponse { Code = code }, packet.Id);
    }

    [RequestPacketHandler("SameColorGameOpenRankRequest")]
    public static void OpenRank(Session session, Packet.Request packet)
    {
        SameColorGameOpenRankRequest request = packet.Deserialize<SameColorGameOpenRankRequest>();
        session.SendResponse(BuildRankResponse(session, request), packet.Id);
    }

    /// <summary>
    /// Runs one request. A failed durable write publishes nothing, so the action list is dropped and
    /// the client receives the authored internal-error code; CommitState has already restored the
    /// in-memory state to the durable board.
    /// </summary>
    private static int RunGuarded(Session session, SameColorResponseContext? ctx, Func<int> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            session.log.Error($"SameColorGame request failed: {exception}");
            ctx?.Actions.Clear();
            return InternalError;
        }
    }

    #endregion

    #region Enter

    private static int TryEnter(Session session, SameColorGameEnterStageRequest request, SameColorResponseContext ctx)
    {
        SameColorGameActivityTable? activity = ActiveActivity();
        if (activity is null) return ActivityUnavailable;
        if (!FunctionOpen(session)) return ActivityUnavailable;

        SameColorGameBossTable? boss = SameColorGameEngine.Boss(request.BossId);
        if (boss is null) return UnknownBoss;
        if (boss.TimerId > 0 && !ActivityScheduleService.IsOpen(boss.TimerId, DateTimeOffset.UtcNow))
            return BossLocked;
        if (boss.PreBossId is int preBossId && preBossId > 0 && GetBossMaxPoint(session.player, preBossId) == 0)
            return BossLocked;

        SameColorGameRoleTable? role = SameColorGameEngine.Role(request.RoleId);
        if (role is null) return UnknownRole;
        if (role.TimerId > 0 && !ActivityScheduleService.IsOpen(role.TimerId, DateTimeOffset.UtcNow))
            return RoleLocked;
        if (role.ConditionId is int roleConditionId && roleConditionId > 0 && !ConditionSatisfied(session, roleConditionId))
            return RoleLocked;

        int skillCode = TryBuildSkillSet(session, activity, role, request.SkillIds, out List<SameColorGameSkillState> skills);
        if (skillCode != 0) return skillCode;

        SameColorGameState next = Clone(Reconcile(session.player, activity.Id));
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // AscNet policy: an Enter replaces any unfinished run without credit. The client issues a
        // fresh EnterStage for both replay and retry, so rejecting it would break normal play.
        SameColorGameRun run = new()
        {
            BossId = boss.Id,
            RoleId = role.Id,
            SkillGroupIds = skills.Select(skill => skill.GroupId).ToList(),
            Skills = skills,
            Energy = 0,
            StartUnix = now,
            RngSeed = MixSeed(activity.Id, boss.Id, role.Id, next.TotalScore, now),
            RngStep = 0
        };
        SameColorGameEngine.InitializeRun(run, role, boss);
        next.Run = run;
        next.ActivityId = activity.Id;
        SameColorGameBossRecord record = GetOrCreateRecord(next, boss.Id);
        record.LastUseRoleId = role.Id;
        CommitState(session.player, next);

        ctx.Actions.Add(new SameColorGameAction
        {
            ActionType = SameColorGameActionType.MapInit,
            InitType = 1,
            LeftTime = 0,
            ItemList = SameColorGameEngine.BoardRecords(run)
        });
        return 0;
    }

    private static int TryBuildSkillSet(
        Session session, SameColorGameActivityTable activity, SameColorGameRoleTable role, List<int>? requested,
        out List<SameColorGameSkillState> skills)
    {
        skills = new List<SameColorGameSkillState>();
        if (!SameColorGameEngine.SkillGroups.Value.TryGetValue(role.SkillId, out SameColorGameSkillGroupTable? main))
            return SkillConfigError;
        skills.Add(new SameColorGameSkillState { GroupId = main.Id, SkillId = main.SkillId });

        if (requested is null || requested.Count == 0) return 0;
        List<int> distinct = requested.Where(groupId => groupId != main.Id).Distinct().ToList();
        if (distinct.Count > activity.MaxSkillCount) return SkillLocked;

        // AscNet policy: the selected extra groups are validated against the current SkillGroup
        // authority instead of the legacy shop-owned loadout. A group is owned when the role that
        // owns it is unlocked, which is the only ownership gate the current tables express.
        foreach (int groupId in distinct)
        {
            if (!SameColorGameEngine.SkillGroups.Value.TryGetValue(groupId, out SameColorGameSkillGroupTable? group))
                return SkillUnknown;
            SameColorGameRoleTable? owner = SameColorGameEngine.Roles.Value.Values
                .FirstOrDefault(candidate => candidate.SkillId == group.SkillId);
            if (owner is null) return SkillLocked;
            if (owner.ConditionId is int conditionId && conditionId > 0 && !ConditionSatisfied(session, conditionId))
                return SkillLocked;
            skills.Add(new SameColorGameSkillState { GroupId = group.Id, SkillId = group.SkillId });
        }
        return 0;
    }

    #endregion

    #region Swap

    private static int TrySwap(Session session, SameColorGameSwapItemRequest request, SameColorResponseContext ctx)
    {
        if (request.Source is null || request.Destination is null) return CoordinateError;
        SameColorGameState state = Reconcile(session.player, ActiveActivityId);
        SameColorGameRun? current = state.Run;
        if (current is null) return MapInitializeError;

        SameColorGameRun run = current.Clone();
        int code = ResolveMove(run, ctx, request.Source.PositionX, request.Source.PositionY,
            request.Destination.PositionX, request.Destination.PositionY, out bool finished);
        if (code != 0) return code;

        SameColorGameState next = Clone(state, includeRun: false);
        if (finished)
        {
            FoldRun(next, run);
        }
        else
        {
            next.Run = run;
        }
        CommitState(session.player, next);
        if (finished)
        {
            PublishTaskProgress(session);
            // Best effort: the pending projection stays durable either way and a later rank query retries it.
            TryFlushPendingRankUpdates(session);
        }
        return 0;
    }

    private static int ResolveMove(
        SameColorGameRun run, SameColorResponseContext ctx, int srcX, int srcY, int dstX, int dstY,
        out bool finished)
    {
        finished = false;
        if (!HasRoundsLeft(run)) return TurnExhausted;
        if (!SameColorGameEngine.InBounds(run, srcX, srcY) || !SameColorGameEngine.InBounds(run, dstX, dstY))
            return CoordinateError;

        int srcIndex = SameColorGameEngine.Index(run, srcX, srcY);
        int dstIndex = SameColorGameEngine.Index(run, dstX, dstY);
        bool selfSwap = srcIndex == dstIndex;
        if (!selfSwap && !AreNeighbours(run, srcIndex, dstIndex)) return CoordinateError;

        SameColorGameCell? source = SameColorGameEngine.CellAt(run, srcIndex);
        SameColorGameCell? destination = selfSwap ? source : SameColorGameEngine.CellAt(run, dstIndex);
        if (source is null) return MissingItem;
        if (destination is null) return MissingItem;
        if (SameColorGameEngine.IsSummon(source) || SameColorGameEngine.IsSummon(destination)) return SummonCannotSwap;

        bool propActivation = SameColorGameEngine.IsProp(source) || SameColorGameEngine.IsProp(destination);
        if (selfSwap && !propActivation) return SwapFailed;

        // Pre-swap identities: the client animates from these and then applies the removals below.
        SameColorGameBallRecord sourceRecord = SameColorGameEngine.Record(run, srcIndex);
        SameColorGameBallRecord destinationRecord = SameColorGameEngine.Record(run, dstIndex);
        if (SameColorGameEngine.IsProp(source)) sourceRecord.ItemType = 0;

        if (!selfSwap)
        {
            (run.Board[srcIndex], run.Board[dstIndex]) = (run.Board[dstIndex], run.Board[srcIndex]);
            if (!propActivation && !SameColorGameEngine.HasAnyMatch(run))
            {
                (run.Board[srcIndex], run.Board[dstIndex]) = (run.Board[dstIndex], run.Board[srcIndex]);
                // AscNet policy: retail's handling of a legal-looking swap that produces nothing is
                // not recoverable, so the board is left untouched and the move costs nothing.
                return SwapFailed;
            }
        }

        SameColorGameRoleTable role = SameColorGameEngine.Roles.Value[run.RoleId];
        SameColorGameBossTable boss = SameColorGameEngine.Bosses.Value[run.BossId];

        // Every accepted move advances the skill cooldowns and publishes them before the swap.
        SameColorGameEngine.BeginTurn(run, ctx);

        ctx.Actions.Add(new SameColorGameAction
        {
            ActionType = SameColorGameActionType.ItemSwap,
            CurRound = run.CurRound,
            Source = sourceRecord,
            Destination = destinationRecord
        });

        SameColorGameEngine.RollPassives(run, ctx, role);

        if (propActivation)
        {
            if (!selfSwap)
            {
                if (SameColorGameEngine.IsProp(SameColorGameEngine.CellAt(run, dstIndex)))
                    SameColorGameEngine.ActivateProp(run, ctx, dstIndex, source.ItemId);
                if (SameColorGameEngine.IsProp(SameColorGameEngine.CellAt(run, srcIndex)))
                    SameColorGameEngine.ActivateProp(run, ctx, srcIndex, destination.ItemId);
            }
            else
            {
                SameColorGameEngine.ActivateProp(run, ctx, srcIndex, source.ItemId);
            }
        }

        SameColorGameEngine.ResolveBoard(run, role, ctx);
        if (SameColorGameEngine.PostComboBuffRemovals(run, ctx))
            SameColorGameEngine.ResolveBoard(run, role, ctx);
        SameColorGameEngine.ClosePassiveBuff(run, ctx);
        SameColorGameEngine.AppendEnergy(run, ctx, role);
        ctx.MoveDamage = SameColorGameEngine.MoveScore(ctx);

        bool lastRound = IsFinalRound(run);
        SameColorGameEngine.CheckBossSkill(run, ctx, boss);
        SameColorGameEngine.AppendSettlement(run, ctx, lastRound, fullRunStats: lastRound);

        finished = lastRound;
        if (!lastRound)
        {
            // The turn tick runs while the played round is still current, so a buff applied by this
            // move keeps its whole authored duration instead of losing its first turn immediately.
            SameColorGameEngine.TickTurn(run, ctx, role);
            run.CurRound++;
        }
        return 0;
    }

    /// <summary>Task progression is published after a durable terminal settlement.</summary>
    private static void PublishTaskProgress(Session session)
    {
        try
        {
            TaskModule.SendTaskSync(session);
        }
        catch (Exception exception)
        {
            session.log.Error($"Failed to publish SameColorGame task progress: {exception}");
        }
    }

    private static bool AreNeighbours(SameColorGameRun run, int first, int second)
    {
        int firstX = first % run.Cols;
        int firstY = first / run.Cols;
        int secondX = second % run.Cols;
        int secondY = second / run.Cols;
        return Math.Abs(firstX - secondX) + Math.Abs(firstY - secondY) == 1;
    }

    #endregion

    #region Skill use

    private static int TryUseItem(Session session, SameColorGameUseItemRequest request, SameColorResponseContext ctx)
    {
        SameColorGameState state = Reconcile(session.player, ActiveActivityId);
        SameColorGameRun? current = state.Run;
        if (current is null) return MapInitializeError;
        if (!HasRoundsLeft(current)) return TurnExhausted;

        // The group and skill identity are fixed by the loadout: a request may not substitute another
        // known skill id, and every validation reads the live state read-only.
        SameColorGameSkillState? equipped = current.Skills.FirstOrDefault(skill => skill.GroupId == request.GroupId);
        if (equipped is null) return SkillLocked;
        if (equipped.SkillId != request.ItemId) return SkillUnknown;
        if (equipped.LeftCd > 0) return SkillCooldown;

        if (!SameColorGameEngine.Skills.Value.TryGetValue(equipped.SkillId, out SameColorGameSkillTable? skill))
            return SkillUnknown;
        int cost = skill.EnergyCost.GetValueOrDefault();
        if (current.Energy < cost) return EnergyNotEnough;

        int targetCode = ValidateSkillTarget(current, skill, request.UseSkillItemParam, out SameColorGameUseSkillTarget? target);
        if (targetCode != 0) return targetCode;

        SameColorGameRun run = current.Clone();
        SameColorGameSkillState skillState = run.Skills.First(candidate => candidate.GroupId == request.GroupId);
        int code = SameColorGameEngine.UseSkill(run, ctx, skillState, target);
        if (code != 0) return code;

        // AscNet policy: a skill that dissolves orbs deals its damage immediately and settles in the
        // same response with the round unchanged, because retail's cross-move skill scoring is not
        // recoverable and dropping it would silently lose damage. Clearing orbs with a skill still
        // charges the authored combo energy, exactly like a move.
        SameColorGameRoleTable role = SameColorGameEngine.Roles.Value[run.RoleId];
        SameColorGameEngine.AppendEnergy(run, ctx, role);
        if (ctx.HasScore)
        {
            ctx.MoveDamage = SameColorGameEngine.MoveScore(ctx);
            // A skill consumes no turn, so the client's round is still the one the last swap reported
            // (XSCBattleManager:ChangeRound is only driven by ITEM_SWAP.CurRound). The settle carries
            // that same round instead of the next one the engine has already advanced to.
            SameColorGameEngine.AppendSettlement(run, ctx, isLastRound: false, fullRunStats: false,
                round: run.CurRound - 1);
        }

        SameColorGameState next = Clone(state, includeRun: false);
        next.Run = run;
        CommitState(session.player, next);
        return 0;
    }

    /// <summary>
    /// Validates use-skill targets. A null param is valid for every control type; popup skills accept
    /// the client's (-1,-1) sentinel as long as the selected orb id belongs to the role.
    /// </summary>
    private static int ValidateSkillTarget(
        SameColorGameRun run, SameColorGameSkillTable skill, SameColorGameUseSkillItemParam? param,
        out SameColorGameUseSkillTarget? target)
    {
        target = null;
        List<SameColorGameUseSkillTarget> targets = new();
        if (param?.Item1 is not null) targets.Add(param.Item1);
        if (param?.Item2 is not null) targets.Add(param.Item2);

        // SKILL_SCREEN_MASK_TYPE.POPUP = 6. The two colour popup skills (602/603) author that mask
        // while leaving ControlType unset, and the client reports their choice from the popup origin
        // sentinel (-1,-1), so the mask and the skill type are the authority here.
        bool isPopup = skill.ScreenMaskType.GetValueOrDefault() == 6 || skill.Type is 112 or 114;
        if (targets.Count == 0)
        {
            // AscNet policy: board-click skills accept an absent target (the engine picks its own
            // cells); popup skills need a colour, which only the client can choose.
            return isPopup ? SkillParamError : 0;
        }
        if (!isPopup && targets.Count > 1) return SkillParamError;

        SameColorGameRoleTable role = SameColorGameEngine.Roles.Value[run.RoleId];
        foreach (SameColorGameUseSkillTarget candidate in targets)
        {
            if (isPopup)
            {
                // Popup: the client reports the chosen orb with its (-1,-1) origin sentinel.
                if (candidate.ItemId == 0 || !role.BallId.Contains(candidate.ItemId)) return SkillParamError;
                continue;
            }
            if (!SameColorGameEngine.InBounds(run, candidate.PositionX, candidate.PositionY)) return CoordinateError;
            int index = SameColorGameEngine.Index(run, candidate.PositionX, candidate.PositionY);
            if (SameColorGameEngine.CellAt(run, index) is null) return MissingItem;
        }
        target = targets[0];
        return 0;
    }

    private static int TryCancelUseItem(Session session, SameColorGameCancelUseItemRequest request)
    {
        SameColorGameState state = Reconcile(session.player, ActiveActivityId);
        SameColorGameRun? run = state.Run;
        if (run is null) return MapInitializeError;
        SameColorGameSkillState? skillState = run.Skills.FirstOrDefault(skill => skill.GroupId == request.GroupId);
        if (skillState is null) return SkillLocked;
        if (!SameColorGameEngine.Skills.Value.ContainsKey(request.ItemId)) return SkillUnknown;
        // AscNet policy: a skill resolves inside the request that uses it, so no prepared skill can
        // exist server-side. The declared operation stays reachable and reports the authored
        // "cannot cancel" error instead of a fake success.
        return CannotCancel;
    }

    #endregion

    #region Give up, pause, countdown

    private static int TryGiveUp(Session session, int bossId)
    {
        SameColorGameState state = Reconcile(session.player, ActiveActivityId);
        SameColorGameRun? run = state.Run;
        if (run is null) return MapInitializeError;
        if (bossId != 0 && bossId != run.BossId) return BossMismatch;

        // AscNet policy: a given-up run never contributes score or combo; the last used role stays.
        SameColorGameState next = Clone(state, includeRun: false);
        CommitState(session.player, next);
        return 0;
    }

    /// <summary>
    /// The client only issues this for timed stages (round stages resolve pause locally without a
    /// request). No authored boss exposes a time limit, so the current activity always answers with
    /// the authored "not timed stage" error instead of inventing a clock.
    /// </summary>
    private static int TryPauseResume(Session session)
    {
        SameColorGameRun? run = Reconcile(session.player, ActiveActivityId).Run;
        if (run is null) return MapInitializeError;
        return NotTimedStage;
    }

    private static int TryCountDown(Session session)
    {
        SameColorGameRun? run = Reconcile(session.player, ActiveActivityId).Run;
        if (run is null) return MapInitializeError;
        return NotTimedStage;
    }

    /// <summary>A move is legal while the played round is still inside the authored round budget.</summary>
    private static bool HasRoundsLeft(SameColorGameRun run) => run.CurRound <= run.MaxRound;

    /// <summary>The round just played is the last one, so this response settles and completes the run.</summary>
    private static bool IsFinalRound(SameColorGameRun run) => run.CurRound >= run.MaxRound;

    #endregion

    #region Rank

    private static SameColorGameOpenRankResponse BuildRankResponse(Session session, SameColorGameOpenRankRequest request)
    {
        SameColorGameActivityTable? activity = ActiveActivity();
        if (activity is null) return new SameColorGameOpenRankResponse { Code = ActivityUnavailable };
        if (request.BossId != 0 && SameColorGameEngine.Boss(request.BossId) is null)
            return new SameColorGameOpenRankResponse { Code = UnknownBoss };
        if (!TryFlushPendingRankUpdates(session))
            return new SameColorGameOpenRankResponse { Code = InternalError };

        try
        {
            long playerId = session.player.PlayerData.Id;
            FilterDefinition<SameColorGameRankEntry> participants = Builders<SameColorGameRankEntry>.Filter.And(
                Builders<SameColorGameRankEntry>.Filter.Eq(entry => entry.ActivityId, activity.Id),
                Builders<SameColorGameRankEntry>.Filter.Eq(entry => entry.BossId, request.BossId));
            long totalCount = SameColorGameRankEntry.collection.CountDocuments(participants);
            string ownId = SameColorGameRankEntry.BuildId(activity.Id, request.BossId, playerId);
            SameColorGameRankEntry? own = SameColorGameRankEntry.collection.Find(entry => entry.Id == ownId).Limit(1).FirstOrDefault();
            long rank = own is null
                ? 0
                : SameColorGameRankEntry.collection.CountDocuments(Builders<SameColorGameRankEntry>.Filter.And(
                    participants, BetterThan(own))) + 1;
            int memberCount = ToProtocolCount(totalCount);

            List<SameColorGameRankInfo> leaders = SameColorGameRankEntry.collection.Find(participants)
                .SortByDescending(entry => entry.Score)
                .ThenBy(entry => entry.PlayerId)
                .Limit(RankLimit)
                .ToList()
                .Select((entry, index) => ToRankInfo(entry, index + 1, memberCount))
                .ToList();

            SameColorGameRankInfo mine = new()
            {
                Rank = ToProtocolCount(rank),
                MemberCount = memberCount,
                Score = own?.Score ?? GetOwnBoardScore(session.player, activity.Id, request.BossId),
                PlayerId = playerId,
                Name = session.player.PlayerData.Name,
                HeadPortraitId = session.player.PlayerData.CurrHeadPortraitId,
                HeadFrameId = session.player.PlayerData.CurrHeadFrameId,
                RoleId = own?.RoleId ?? LastUseRole(session.player, request.BossId),
                RoleSkillId = new List<int>()
            };
            return new SameColorGameOpenRankResponse { Code = 0, RankList = leaders, MyRankInfo = mine };
        }
        catch (Exception exception)
        {
            session.log.Error($"Failed to query SameColorGame rank: {exception}");
            return new SameColorGameOpenRankResponse { Code = InternalError };
        }
    }

    private static SameColorGameRankInfo ToRankInfo(SameColorGameRankEntry entry, int rank, int memberCount)
    {
        SameColorGameRoleTable? role = SameColorGameEngine.Role(entry.RoleId);
        return new SameColorGameRankInfo
        {
            Rank = rank,
            MemberCount = memberCount,
            Score = entry.Score,
            PlayerId = entry.PlayerId,
            Name = entry.Name,
            HeadPortraitId = entry.HeadPortraitId,
            HeadFrameId = entry.HeadFrameId,
            RoleId = entry.RoleId,
            RoleSkillId = role is null ? new List<int>() : new List<int> { role.SkillId }
        };
    }

    private static int ToProtocolCount(long count) =>
        count >= int.MaxValue ? int.MaxValue : checked((int)Math.Max(0, count));

    private static FilterDefinition<SameColorGameRankEntry> BetterThan(SameColorGameRankEntry own) =>
        Builders<SameColorGameRankEntry>.Filter.Or(
            Builders<SameColorGameRankEntry>.Filter.Gt(entry => entry.Score, own.Score),
            Builders<SameColorGameRankEntry>.Filter.And(
                Builders<SameColorGameRankEntry>.Filter.Eq(entry => entry.Score, own.Score),
                Builders<SameColorGameRankEntry>.Filter.Lt(entry => entry.PlayerId, own.PlayerId)));

    private static long GetOwnBoardScore(Player player, int activityId, int bossId)
    {
        SameColorGameState state = player.SameColorGame ??= new SameColorGameState();
        if (state.ActivityId != activityId) return 0;
        return bossId == 0 ? state.TotalScore : state.BossRecords.FirstOrDefault(record => record.BossId == bossId)?.MaxPoint ?? 0;
    }

    private static int LastUseRole(Player player, int bossId) =>
        (player.SameColorGame ??= new SameColorGameState()).BossRecords.FirstOrDefault(record => record.BossId == bossId)?.LastUseRoleId ?? 0;

    internal static bool TryFlushPendingRankUpdates(Session session)
    {
        try
        {
            FlushPendingRankUpdates(session.player);
            return true;
        }
        catch (Exception exception)
        {
            session.log.Error($"Failed to persist SameColorGame rank projection: {exception}");
            return false;
        }
    }

    private static void FlushPendingRankUpdates(Player player)
    {
        SameColorGameState state = player.SameColorGame ??= new SameColorGameState();
        if (state.PendingRankUpdates.Count == 0) return;
        int activityId = state.ActivityId;
        long playerId = player.PlayerData.Id;
        foreach (SameColorGameRankUpdate update in state.PendingRankUpdates)
        {
            string id = SameColorGameRankEntry.BuildId(activityId, update.BossId, playerId);
            SameColorGameRankEntry? entry = SameColorGameRankEntry.collection.Find(candidate => candidate.Id == id).Limit(1).FirstOrDefault();
            if (entry is not null && entry.Score >= update.Score) continue;
            SameColorGameRankEntry replacement = entry ?? new SameColorGameRankEntry { Id = id, ActivityId = activityId, BossId = update.BossId, PlayerId = playerId };
            replacement.Score = update.Score;
            replacement.RoleId = update.RoleId;
            replacement.AchievedAt = update.AchievedAt;
            replacement.Name = player.PlayerData.Name;
            replacement.HeadPortraitId = player.PlayerData.CurrHeadPortraitId;
            replacement.HeadFrameId = player.PlayerData.CurrHeadFrameId;
            if (entry is null)
                SameColorGameRankEntry.collection.InsertOne(replacement);
            else
                SameColorGameRankEntry.collection.ReplaceOne(candidate => candidate.Id == id, replacement);
        }
        state.PendingRankUpdates.Clear();
        player.SaveChecked();
    }

    #endregion

    #region State plumbing

    private static SameColorGameActivityTable? ActiveActivity()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return Activities.Value
            .Where(row => row.TimerId is > 0 && ActivityScheduleService.IsOpen(row.TimerId!.Value, now))
            .MaxBy(row => row.Id);
    }

    private static bool FunctionOpen(Session session)
    {
        if (!Functions.Value.TryGetValue(FunctionId, out FunctionalOpenTable? function)) return true;
        foreach (int conditionId in function.Condition)
        {
            if (!ConditionSatisfied(session, conditionId)) return false;
        }
        return true;
    }

    /// <summary>
    /// Mode condition evaluator. 17426 is the mode's own boss-score gate; everything else uses the
    /// shared evaluator so level and stage gates keep one implementation.
    /// </summary>
    private static bool ConditionSatisfied(Session session, int conditionId)
    {
        if (!Conditions.Value.TryGetValue(conditionId, out ConditionTable? condition)) return false;
        if (condition.Type == BossUnlockConditionType && condition.Params.Count > 0)
        {
            long threshold = condition.Params.Count > 1 ? condition.Params[1] : 1;
            return GetBossMaxPoint(session.player, condition.Params[0]) >= threshold;
        }
        return LifeTreeModule.ConditionSatisfied(session, condition);
    }

    private static SameColorGameState Reconcile(Player player, int activityId)
    {
        SameColorGameState state = player.SameColorGame ??= new SameColorGameState();
        if (activityId != 0 && state.ActivityId != activityId)
            return new SameColorGameState { ActivityId = activityId };
        return state;
    }

    private static SameColorGameState Clone(SameColorGameState state, bool includeRun = true) => new()
    {
        ActivityId = state.ActivityId,
        BossRecords = state.BossRecords
            .Select(record => new SameColorGameBossRecord
            {
                BossId = record.BossId,
                MaxPoint = record.MaxPoint,
                MaxCombo = record.MaxCombo,
                LastUseRoleId = record.LastUseRoleId
            })
            .ToList(),
        TotalScore = state.TotalScore,
        Run = includeRun ? state.Run?.Clone() : null,
        PendingRankUpdates = state.PendingRankUpdates
            .Select(update => new SameColorGameRankUpdate
            {
                BossId = update.BossId,
                Score = update.Score,
                RoleId = update.RoleId,
                AchievedAt = update.AchievedAt
            })
            .ToList()
    };

    private static SameColorGameBossRecord GetOrCreateRecord(SameColorGameState state, int bossId)
    {
        SameColorGameBossRecord? record = state.BossRecords.FirstOrDefault(row => row.BossId == bossId);
        if (record is null)
        {
            record = new SameColorGameBossRecord { BossId = bossId };
            state.BossRecords.Add(record);
            state.BossRecords = state.BossRecords.OrderBy(row => row.BossId).ToList();
        }
        return record;
    }

    /// <summary>
    /// Commits a fully built next state and persists it before any action is published. The engine
    /// always mutates a clone, so the durable document never holds a partially applied request and a
    /// failed checked save cannot publish actions that were never written.
    /// </summary>
    private static void CommitState(Player player, SameColorGameState state)
    {
        SameColorGameState previous = player.SameColorGame;
        player.SameColorGame = state;
        try
        {
            player.SaveChecked();
        }
        catch
        {
            // A failed checked save published nothing, so the in-memory state must not keep the
            // uncommitted request either: the retry has to run against the durable board.
            player.SameColorGame = previous;
            throw;
        }
    }

    /// <summary>Folds a finished run into TotalScore, boss records and the pending rank projection.</summary>
    private static void FoldRun(SameColorGameState state, SameColorGameRun run)
    {
        state.Run = null;
        state.TotalScore += run.Score;
        SameColorGameBossRecord record = GetOrCreateRecord(state, run.BossId);
        if (run.Score > record.MaxPoint) record.MaxPoint = run.Score;
        if (run.MaxCombo > record.MaxCombo) record.MaxCombo = run.MaxCombo;
        record.LastUseRoleId = run.RoleId;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        AddPendingRankUpdate(state, run.BossId, run.Score, run.RoleId, now);
        AddPendingRankUpdate(state, 0, state.TotalScore, run.RoleId, now);
    }

    private static void AddPendingRankUpdate(SameColorGameState state, int bossId, long score, int roleId, long now)
    {
        SameColorGameRankUpdate? pending = state.PendingRankUpdates.FirstOrDefault(update => update.BossId == bossId);
        if (pending is null)
        {
            state.PendingRankUpdates.Add(new SameColorGameRankUpdate
            {
                BossId = bossId,
                Score = score,
                RoleId = roleId,
                AchievedAt = now
            });
            return;
        }
        if (score <= pending.Score) return;
        pending.Score = score;
        pending.RoleId = roleId;
        pending.AchievedAt = now;
    }

    private static long MixSeed(int activityId, int bossId, int roleId, long totalScore, long now)
    {
        ulong x = (ulong)now * 0x9E3779B97F4A7C15UL
            ^ ((ulong)(uint)activityId << 32)
            ^ ((ulong)(uint)bossId << 16)
            ^ (uint)roleId
            ^ (ulong)totalScore;
        x ^= x >> 30;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        return (long)x;
    }

    #endregion
}
