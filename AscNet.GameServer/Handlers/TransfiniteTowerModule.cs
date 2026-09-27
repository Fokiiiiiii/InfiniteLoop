using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.fuben.transfinitetower;
using AscNet.Table.V2.share.robot;
using MongoDB.Bson;

namespace AscNet.GameServer.Handlers;

// Overclock Simulation (TransfiniteTower, stage type 103). Wire shapes come from the installed 4.8 Lua;
// rollback, scoring, MVP, energy accounting and calendars are AscNet policy, not retail parity.
internal static partial class TransfiniteTowerModule
{
    internal const int ActivityNotOpen = 20431001, ChapterNotOpen = 20431002, StageProgressError = 20431003,
        NavigatorRequired = 20431004, ChallengeCountNotEnough = 20431005, NavigatorLocked = 20431006,
        RollbackInvalid = 20431007, NoCurBattle = 20431008, SettleCondNotEnough = 20431009,
        ChapterCfgNotFound = 20431012, StageCfgNotFound = 20431013, StageNotSettled = 20431015,
        NoPendingStage = 20431016, LastRecordNotReset = 20431017, NoLastRecord = 20431018,
        TeamSlotOverLimit = 20431019, TeamSlotConflict = 20431020, TeamMemberDuplicate = 20431021,
        TeamMemberNotInGroup = 20431022, TeamCharacterMismatch = 20431023, NavigatorForbidden = 20431024,
        CharacterLocked = 20431025, NavigatorCountOverLimit = 20431026, CharacterGroupCfgNotFound = 20431027,
        RefightTeamChanged = 20431028;
    // Generic fight-authority failure (FightModule.FightAuthorizationError); also used when persistence fails.
    internal const int FightAuthorizationError = 1033;
    // Single injectable clock for every tower path (entry, settle, ranking); tests freeze it at an open activity window.
    internal static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;
    private const int ChapterTypeTeach = 1, NavigatorType = 2, ConditionTowerStagePassed = 10105;

    private static readonly Lazy<List<TransfiniteTowerActivityTable>> Activities = new(() => TableReaderV2.Parse<TransfiniteTowerActivityTable>());
    private static readonly Lazy<Dictionary<int, TransfiniteTowerChapterTable>> Chapters = new(() => TableReaderV2.Parse<TransfiniteTowerChapterTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, TransfiniteTowerStageTable>> Stages = new(() => TableReaderV2.Parse<TransfiniteTowerStageTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, TransfiniteTowerStageTable>> StagesByBattleId = new(() => Stages.Value.Values.ToDictionary(x => x.StageId));
    private static readonly Lazy<Dictionary<int, TransfiniteTowerCharacterTable>> Characters = new(() => TableReaderV2.Parse<TransfiniteTowerCharacterTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, TransfiniteTowerCharacterGroupTable>> Groups = new(() => TableReaderV2.Parse<TransfiniteTowerCharacterGroupTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, TransfiniteTowerFightCountTable>> FightCounts = new(() => TableReaderV2.Parse<TransfiniteTowerFightCountTable>().ToDictionary(x => x.FightCount));
    private static readonly Lazy<Dictionary<int, RobotTable>> Robots = new(() => TableReaderV2.Parse<RobotTable>().GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First()));
    private static readonly Lazy<Dictionary<int, ConditionTable>> Conditions = new(() => TableReaderV2.Parse<ConditionTable>().ToDictionary(x => x.Id));
    // Config FightCount is also the Lua ENERGY_MAX default.
    private static readonly Lazy<int> DefaultFightCount = new(() =>
        int.TryParse(TableReaderV2.Parse<TransfiniteTowerConfigTable>().FirstOrDefault(x => x.Key == "FightCount")?.Values.FirstOrDefault(), out int value) ? value : 3);

    internal static bool IsBattleStage(uint stageId) => stageId <= int.MaxValue && StagesByBattleId.Value.ContainsKey((int)stageId);

    private static TransfiniteTowerActivityTable? OpenActivity(DateTimeOffset now) =>
        Activities.Value.FirstOrDefault(x => ActivityScheduleService.IsOpen(x.TimeId, now));

    private static bool IsTimeOpen(int timeId, DateTimeOffset now) => timeId <= 0 || ActivityScheduleService.IsOpen(timeId, now);

    // Activity rollover starts a fresh document; it is persisted by the first mutation.
    private static TransfiniteTowerState State(Session session, TransfiniteTowerActivityTable activity)
    {
        if (session.player.TransfiniteTower is not { } state || state.ActivityId != activity.Id)
            session.player.TransfiniteTower = state = new() { ActivityId = activity.Id };
        return state;
    }

    private static TransfiniteTowerChapterInfo Chapter(TransfiniteTowerState state, int chapterId)
    {
        TransfiniteTowerChapterInfo? info = state.ChapterInfoList.FirstOrDefault(x => x.ChapterId == chapterId);
        if (info is null)
            state.ChapterInfoList.Add(info = new() { ChapterId = chapterId });
        return info;
    }

    private static TransfiniteTowerChapterTable? ChapterOf(TransfiniteTowerActivityTable activity, TransfiniteTowerStageTable stage) =>
        activity.ChapterIds.Select(id => Chapters.Value.GetValueOrDefault(id)).FirstOrDefault(x => x?.StageGroupId == stage.StageGroupId);

    private static int StageCount(TransfiniteTowerChapterTable chapter) => Stages.Value.Values.Count(x => x.StageGroupId == chapter.StageGroupId);
    // Task condition 15201 [1, stageId]: the floor was committed at least once in the current activity.
    internal static bool IsFloorCleared(Player player, int battleStageId)
    {
        if (player.TransfiniteTower is not { } state || !StagesByBattleId.Value.TryGetValue(battleStageId, out TransfiniteTowerStageTable? stage)
            || Activities.Value.FirstOrDefault(x => x.Id == state.ActivityId) is not { } activity)
            return false;
        return ChapterOf(activity, stage) is { } chapter
            ? (state.ChapterInfoList.FirstOrDefault(x => x.ChapterId == chapter.Id)?.MaxPassedOrder ?? 0) >= stage.Order
            : state.PassedTeachStageIds.Contains(stage.Id);
    }

    internal static IEnumerable<(int TaskTimeLimitId, int TimeId)> TaskGroups() => Activities.Value.Select(x => (x.TaskTimeLimitId, x.TimeId));


    // XTransfiniteTowerAgency.CheckPassedByStageId: a tower stage counts as passed once the whole chapter was cleared.
    internal static bool IsStagePassed(Player player, int battleStageId)
    {
        if (player.TransfiniteTower is not { } state || !StagesByBattleId.Value.TryGetValue(battleStageId, out TransfiniteTowerStageTable? stage))
            return false;
        TransfiniteTowerActivityTable? activity = Activities.Value.FirstOrDefault(x => x.Id == state.ActivityId);
        if (activity is null)
            return false;
        if (ChapterOf(activity, stage) is not { } chapter)
            return state.PassedTeachStageIds.Contains(stage.Id);
        int total = StageCount(chapter);
        return total > 0 && (state.ChapterInfoList.FirstOrDefault(x => x.ChapterId == chapter.Id)?.MaxPassedOrder ?? 0) >= total;
    }

    private static bool IsConditionMet(Player player, int conditionId) =>
        conditionId <= 0 || Conditions.Value.TryGetValue(conditionId, out ConditionTable? condition)
            && condition.Type == ConditionTowerStagePassed && condition.Params.Count > 0
            && IsStagePassed(player, condition.Params[0]);

    private static bool IsChapterOpen(Player player, TransfiniteTowerChapterTable chapter, DateTimeOffset now) =>
        IsTimeOpen(chapter.UnLockTimeId, now) && IsConditionMet(player, chapter.ConditionId);

    internal static void SendLoginData(Session session)
    {
        if (OpenActivity(Clock()) is not { } activity)
            return;
        TransfiniteTowerState state = State(session, activity);
        session.SendPush(new NotifyTransfiniteTowerData { TransfiniteTowerDataDb = state });
    }

    internal static void SendChapterInfo(Session session, TransfiniteTowerChapterInfo chapter) =>
        session.SendPush(new NotifyTransfiniteTowerChapterInfo { ChapterInfo = chapter });

    // Persist-before-ack: the whole Player document is the unit of the write, so an uncertain save is decided
    // by storage instead of by the in-memory snapshot, which would silently overwrite a committed floor or
    // rank on the next unrelated save. Storage showing the exact staged document means the write landed and
    // the lost acknowledgement is reported as the success it was; storage showing anything else means nothing
    // landed and the durable state replaces the staged one; an unreadable document proves neither outcome, so
    // the session closes without persisting the possibly stale player. True when the staged state is durable.
    private static bool TrySave(Session session)
    {
        TransfiniteTowerState? staged = session.player.TransfiniteTower;
        try
        {
            session.player.SaveChecked();
            return true;
        }
        catch (Exception exception)
        {
            // TryFromPlayerId returns null when storage cannot be read: the outcome is unknown, not "did not land".
            Player? stored = Player.TryFromPlayerId(session.player.PlayerData.Id);
            if (stored is null)
            {
                session.log.Error($"TransfiniteTower save outcome is unresolved: {exception.Message}");
                session.DisconnectProtocol(persistState: false);
                throw;
            }
            bool committed = staged is not null && stored.TransfiniteTower is not null
                && staged.ToBson().SequenceEqual(stored.TransfiniteTower.ToBson());
            session.player.TransfiniteTower = stored.TransfiniteTower;
            if (committed)
            {
                session.log.Warn($"TransfiniteTower save acknowledgement was lost after the write landed: {exception.Message}");
                return true;
            }
            session.log.Error($"TransfiniteTower save failed: {exception.Message}");
            return false;
        }
    }

    private static bool TryResolveChapter(Session session, int chapterId, out TransfiniteTowerActivityTable activity,
        out TransfiniteTowerChapterTable chapter, out TransfiniteTowerChapterInfo info, out int code)
    {
        activity = null!; chapter = null!; info = null!;
        DateTimeOffset now = Clock();
        if (OpenActivity(now) is not { } open) { code = ActivityNotOpen; return false; }
        activity = open;
        if (!activity.ChapterIds.Contains(chapterId) || !Chapters.Value.TryGetValue(chapterId, out TransfiniteTowerChapterTable? row))
        { code = ChapterCfgNotFound; return false; }
        chapter = row;
        info = Chapter(State(session, activity), chapterId);
        code = 0;
        return true;
    }

    [RequestPacketHandler("TransfiniteTowerStageSettleRequest")]
    public static void StageSettle(Session session, Packet.Request packet)
    {
        TransfiniteTowerStageSettleRequest request = packet.Deserialize<TransfiniteTowerStageSettleRequest>();
        if (!TryResolveChapter(session, request.ChapterId, out _, out _, out TransfiniteTowerChapterInfo info, out int code))
        { session.SendResponse(new TransfiniteTowerStageSettleResponse { Code = code }, packet.Id); return; }
        if (info.CurBattleInfo?.PendingStageRecord is not { } pending)
        { session.SendResponse(new TransfiniteTowerStageSettleResponse { Code = NoPendingStage }, packet.Id); return; }

        TransfiniteTowerBattleInfo battle = info.CurBattleInfo;
        if (pending.Order == battle.StageProgressIndex + 1)
        {
            battle.StageProgressIndex = pending.Order;
            foreach (TransfiniteTowerCharacterCount charged in pending.Charged)
                AddUsedCount(battle, charged.CharacterCfgId, charged.CharacterId, 1);
            // Teach tower replays from floor 1 while last round is kept (control.lua:440); a committed floor starts the new round.
            info.LastStageRecordList.Clear();
        }
        battle.StageRecordList.RemoveAll(x => x.Order == pending.Order);
        battle.StageRecordList.Add(pending);
        battle.StageRecordList.Sort((a, b) => a.Order.CompareTo(b.Order));
        battle.PendingStageRecord = null;
        battle.LastTeamSelection = pending.Selection;
        battle.RollbackOrder = battle.StageRecordList.Where(x => Stages.Value.GetValueOrDefault(x.StageCfgId)?.IsReset == 1)
            .Select(x => x.Order).DefaultIfEmpty(0).Max();
        info.MaxPassedOrder = Math.Max(info.MaxPassedOrder, battle.StageProgressIndex);
        if (!TrySave(session))
        { session.SendResponse(new TransfiniteTowerStageSettleResponse { Code = FightAuthorizationError }, packet.Id); return; }
        session.SendResponse(new TransfiniteTowerStageSettleResponse(), packet.Id);
        SendChapterInfo(session, info);
        TaskModule.SendTransfiniteTowerTaskSync(session);
    }

    [RequestPacketHandler("TransfiniteTowerChapterSettleRequest")]
    public static void ChapterSettle(Session session, Packet.Request packet)
    {
        TransfiniteTowerChapterSettleRequest request = packet.Deserialize<TransfiniteTowerChapterSettleRequest>();
        if (!TryResolveChapter(session, request.ChapterId, out _, out TransfiniteTowerChapterTable chapter, out TransfiniteTowerChapterInfo info, out int code))
        { session.SendResponse(new TransfiniteTowerChapterSettleResponse { Code = code }, packet.Id); return; }
        TransfiniteTowerBattleInfo? battle = info.CurBattleInfo;
        code = battle is null ? NoCurBattle : battle.PendingStageRecord is not null ? StageNotSettled
            : battle.StageProgressIndex <= 0 ? SettleCondNotEnough : 0;
        if (code != 0)
        { session.SendResponse(new TransfiniteTowerChapterSettleResponse { Code = code }, packet.Id); return; }

        List<TransfiniteTowerStageRecord> records = battle!.StageRecordList.OrderBy(x => x.Order).ToList();
        List<TransfiniteTowerSettleCharacter> characters = records.SelectMany(x => x.Team).Select(x => x.FightId)
            .Distinct().Select(id => BuildSettleCharacter(session, id)).ToList();
        long now = Clock().ToUnixTimeSeconds();
        TransfiniteTowerSettleInfo settle = new()
        {
            Order = battle.StageProgressIndex,
            TotalSpendTime = (int)Math.Min(int.MaxValue, records.Sum(x => (long)x.SpendTime)),
            TotalPower = (int)Math.Min(int.MaxValue, characters.Sum(x => (long)x.Power)),
            Characters = characters,
            // Lua agency.lua:188: the server picks the MVP by power (policy tie-break: lowest FightId).
            MvpFightId = characters.OrderByDescending(x => x.Power).ThenBy(x => x.FightId).Select(x => x.FightId).FirstOrDefault(),
            AchievedAt = now
        };
        settle.IsNewRecord = info.BestOrder == 0 || settle.Order > info.BestOrder
            || settle.Order == info.BestOrder && settle.TotalSpendTime < info.BestTotalSpendTime;
        // Persisted SettleInfo is the best round (agency.lua:150); only a new record replaces it.
        info.CanSetMvp = settle.IsNewRecord;
        if (settle.IsNewRecord)
        {
            info.SettleInfo = settle;
            info.BestOrder = settle.Order;
            info.BestTotalSpendTime = settle.TotalSpendTime;
            if (chapter.IsRank == 1)
                info.RankInfo = new()
                {
                    MaxOrder = settle.Order, TotalSpendTime = settle.TotalSpendTime, TotalPower = settle.TotalPower,
                    MvpFightId = settle.MvpFightId, Characters = characters.ToList(), AchievedAt = now
                };
        }
        info.LastStageRecordList = records;
        info.CurBattleInfo = null;
        if (!TrySave(session))
        { session.SendResponse(new TransfiniteTowerChapterSettleResponse { Code = FightAuthorizationError }, packet.Id); return; }
        int rank = 0, total = 0;
        if (chapter.IsRank == 1)
            TryPublishRank(session, chapter.Id, out rank, out total);
        session.SendResponse(new TransfiniteTowerChapterSettleResponse { SettleInfo = settle, Rank = rank, TotalCount = total }, packet.Id);
        SendChapterInfo(session, info);
    }

    // AscNet policy: rollback returns progress to the active rollback point and refunds energy of discarded floors.
    [RequestPacketHandler("TransfiniteTowerRollbackRequest")]
    public static void Rollback(Session session, Packet.Request packet)
    {
        TransfiniteTowerRollbackRequest request = packet.Deserialize<TransfiniteTowerRollbackRequest>();
        if (!TryResolveChapter(session, request.ChapterId, out _, out _, out TransfiniteTowerChapterInfo info, out int code))
        { session.SendResponse(new TransfiniteTowerRollbackResponse { Code = code }, packet.Id); return; }
        TransfiniteTowerBattleInfo? battle = info.CurBattleInfo;
        code = battle is null ? NoCurBattle : battle.PendingStageRecord is not null ? StageNotSettled
            : battle.RollbackOrder <= 0 || battle.StageProgressIndex <= battle.RollbackOrder ? RollbackInvalid : 0;
        if (code != 0)
        { session.SendResponse(new TransfiniteTowerRollbackResponse { Code = code }, packet.Id); return; }

        battle!.StageRecordList.RemoveAll(x => x.Order > battle.RollbackOrder);
        battle.StageProgressIndex = battle.RollbackOrder;
        battle.CharacterCountList.Clear();
        foreach (TransfiniteTowerCharacterCount charged in battle.StageRecordList.SelectMany(x => x.Charged))
            AddUsedCount(battle, charged.CharacterCfgId, charged.CharacterId, 1);
        battle.LastTeamSelection = battle.StageRecordList.LastOrDefault()?.Selection;
        if (!TrySave(session))
        { session.SendResponse(new TransfiniteTowerRollbackResponse { Code = FightAuthorizationError }, packet.Id); return; }
        session.SendResponse(new TransfiniteTowerRollbackResponse(), packet.Id);
        SendChapterInfo(session, info);
    }

    [RequestPacketHandler("TransfiniteTowerResetChapterRequest")]
    public static void ResetChapter(Session session, Packet.Request packet)
    {
        TransfiniteTowerResetChapterRequest request = packet.Deserialize<TransfiniteTowerResetChapterRequest>();
        if (!TryResolveChapter(session, request.ChapterId, out _, out _, out TransfiniteTowerChapterInfo info, out int code))
        { session.SendResponse(new TransfiniteTowerResetChapterResponse { Code = code }, packet.Id); return; }
        if (info.LastStageRecordList.Count == 0)
        { session.SendResponse(new TransfiniteTowerResetChapterResponse { Code = NoLastRecord }, packet.Id); return; }
        info.LastStageRecordList.Clear();
        if (!TrySave(session))
        { session.SendResponse(new TransfiniteTowerResetChapterResponse { Code = FightAuthorizationError }, packet.Id); return; }
        session.SendResponse(new TransfiniteTowerResetChapterResponse(), packet.Id);
        SendChapterInfo(session, info);
    }

    private static TransfiniteTowerSettleCharacter BuildSettleCharacter(Session session, int fightId)
    {
        if (Robots.Value.TryGetValue(fightId, out RobotTable? robot))
            return new() { FightId = fightId, IsTrial = true, Quality = robot.CharacterQuality, Power = robot.ShowAbility ?? 0 };
        CharacterData? character = session.character.Characters.FirstOrDefault(x => x.Id == (uint)fightId);
        return character is null
            ? new() { FightId = fightId }
            : new() { FightId = fightId, Quality = character.Quality, Power = (int)Math.Clamp(CharacterPower.Calculate(session, character), 0, int.MaxValue) };
    }

    private static void AddUsedCount(TransfiniteTowerBattleInfo battle, int cfgId, int characterId, int amount)
    {
        TransfiniteTowerCharacterCount? count = UsedEntry(battle, cfgId, characterId);
        if (count is null)
            battle.CharacterCountList.Add(new() { CharacterCfgId = cfgId, CharacterId = characterId, UsedCount = amount });
        else
            count.UsedCount += amount;
    }

    // Same matching as XTransfiniteTowerModel.AddCharacterUsedCount: configured rows by cfg id, unlisted own units by character id.
    private static TransfiniteTowerCharacterCount? UsedEntry(TransfiniteTowerBattleInfo? battle, int cfgId, int characterId) =>
        battle?.CharacterCountList.FirstOrDefault(x => cfgId > 0 ? x.CharacterCfgId == cfgId : x.CharacterCfgId <= 0 && x.CharacterId == characterId);
}
