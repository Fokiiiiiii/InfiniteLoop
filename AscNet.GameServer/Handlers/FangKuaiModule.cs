using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.miniactivity.fangkuai;

namespace AscNet.GameServer.Handlers;

internal static class FangKuaiModule
{
    private const int Invalid = 1;
    // Runtime clock seam (wire scenarios inject it; zero restores UtcNow). The calendar rows themselves
    // stay authored data: this only moves "now" so a bounded probe can reach an authored window.
    internal static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;
    private static readonly Lazy<List<FangKuaiActivityTable>> Activities = new(() => TableReaderV2.Parse<FangKuaiActivityTable>());
    private static readonly Lazy<List<FangKuaiChapterTable>> Chapters = new(() => TableReaderV2.Parse<FangKuaiChapterTable>());
    private static readonly Lazy<Dictionary<int, FangKuaiStageTable>> Stages = new(() => TableReaderV2.Parse<FangKuaiStageTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, FangKuaiStageGroupTable>> Groups = new(() => TableReaderV2.Parse<FangKuaiStageGroupTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, FangKuaiBlockTable>> Blocks = new(() => TableReaderV2.Parse<FangKuaiBlockTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, FangKuaiItemTable>> Items = new(() => TableReaderV2.Parse<FangKuaiItemTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<(int, int), int>> Points = new(() => TableReaderV2.Parse<FangKuaiBlockPointTable>().ToDictionary(x => (x.BlockType, x.BlockLength), x => x.Point));
    private static readonly Lazy<Dictionary<int, int>> Combos = new(() => TableReaderV2.Parse<FangKuaiComboTable>().ToDictionary(x => x.Id, x => x.Radio));
    private static readonly Lazy<Dictionary<int, FangKuaiStageEnvironmentTable>> Environments = new(() => TableReaderV2.Parse<FangKuaiStageEnvironmentTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<int> EnhanceLimit = new(() => TableReaderV2.Parse<AscNet.Table.V2.client.miniactivity.fangkuai.FangKuaiClientConfigTable>()
        .Single(x => x.Key == "ItemEnhanceAccumulateLimit").Values.Single());
    private static readonly Lazy<Dictionary<int, HashSet<int>>> AllowedScores = new(() =>
    {
        Dictionary<int, HashSet<int>> result = [];
        foreach (FangKuaiBlockTable block in Blocks.Value.Values)
        {
            if (!result.TryGetValue(block.StageId, out HashSet<int>? scores))
                result.Add(block.StageId, scores = []);
            for (int length = 1; length <= block.Length; length++)
            {
                if (!Points.Value.TryGetValue((block.Type, length), out int full)) continue;
                scores.Add(full);
                for (int remainingLength = 1; remainingLength < length; remainingLength++)
                    if (Points.Value.TryGetValue((block.Type, remainingLength), out int remaining))
                        scores.Add(full - remaining);
            }
        }
        return result;
    });
    private static readonly Lazy<HashSet<int>> ActiveStageIds = new(() =>
    {
        HashSet<int> ids = [];
        foreach (FangKuaiChapterTable chapter in Chapters.Value)
            if (Activities.Value.Any(a => a.Id == 5 && a.ChapterIds.Contains(chapter.Id)))
                foreach (int groupId in chapter.StageGroupIds)
                    if (Groups.Value.TryGetValue(groupId, out FangKuaiStageGroupTable? group))
                    {
                        if (group.SimpleStageId > 0) ids.Add(group.SimpleStageId);
                        if (group.DiffcultStageId > 0) ids.Add(group.DiffcultStageId);
                    }
        return ids;
    });

    private static FangKuaiActivityTable? Active(DateTimeOffset now) =>
        Activities.Value.Where(a => a.TimeId > 0 && ActivityScheduleService.IsOpen(a.TimeId, now))
            .OrderByDescending(a => a.Id).FirstOrDefault();

    private static bool TryStage(int stageId, DateTimeOffset now, out FangKuaiStageTable stage)
    {
        stage = null!;
        if (!ActiveStageIds.Value.Contains(stageId) || !Stages.Value.TryGetValue(stageId, out stage!)) return false;
        // A missing chapter calendar is not permission to unlock it early.
        return stage.TimeId > 0 && ActivityScheduleService.IsOpen(stage.TimeId, now)
            && Groups.Value.Values.Any(g => (g.SimpleStageId == stageId || g.DiffcultStageId == stageId)
                && g.TimeId > 0 && ActivityScheduleService.IsOpen(g.TimeId, now));
    }

    internal static NotifyFangKuaiData BuildLoginData(Player player, DateTimeOffset now)
    {
        FangKuaiActivityTable? active = Active(now);
        PlayerFangKuaiState state = player.FangKuai;
        return active is null ? new NotifyFangKuaiData() : new NotifyFangKuaiData
        {
            ActivityId = active.Id,
            FinishedStageIds = state.ActivityId == active.Id ? state.FinishedStageIds.ToList() : [],
            StageHistroyDict = state.ActivityId == active.Id ? new(state.StageHistroyDict) : [],
            StageDataDict = state.ActivityId == active.Id ? new(state.StageDataDict) : []
        };
    }

    internal static void SendLoginData(Session session) => session.SendPush(BuildLoginData(session.player, Clock()));

    private static PlayerFangKuaiState Prepare(Player player, int activityId)
    {
        PlayerFangKuaiState state = player.FangKuai;
        if (state.ActivityId == activityId) return state;
        state.ActivityId = activityId;
        state.FinishedStageIds.Clear();
        state.StageHistroyDict.Clear();
        state.StageDataDict.Clear();
        state.PlayedStageIds.Clear();
        state.TotalScoresByStage.Clear();
        return state;
    }

    private static bool TryGetRun(Player player, int stageId, DateTimeOffset now, out FangKuaiStageTable stage, out FangKuaiStageData run, out int chapterId)
    {
        stage = null!;
        run = null!;
        chapterId = 0;
        if (Active(now) is not { } activity || !TryStage(stageId, now, out stage) || player.FangKuai.ActivityId != activity.Id) return false;
        foreach (FangKuaiChapterTable chapter in Chapters.Value)
            if (activity.ChapterIds.Contains(chapter.Id) && chapter.StageGroupIds.Any(groupId =>
                Groups.Value.TryGetValue(groupId, out FangKuaiStageGroupTable? group) &&
                (group.SimpleStageId == stageId || group.DiffcultStageId == stageId)))
            {
                chapterId = chapter.Id;
                return player.FangKuai.StageDataDict.TryGetValue(chapterId, out run!) && run.StageId == stageId;
            }
        return false;
    }

    [RequestPacketHandler("FangKuaiStageStartRequest")]
    public static void Start(Session session, Packet.Request packet)
    {
        FangKuaiStageStartRequest request = packet.Deserialize<FangKuaiStageStartRequest>();
        DateTimeOffset now = Clock();
        FangKuaiActivityTable? activity = Active(now);
        if (activity is null || !TryStage(request.StageId, now, out FangKuaiStageTable stage) ||
            (stage.PreStageId > 0 && !session.player.FangKuai.FinishedStageIds.Contains(stage.PreStageId)) ||
            request.CharacterId <= 0 || !session.character.Characters.Any(c => c.Id == request.CharacterId))
        {
            session.SendResponse(new FangKuaiStageStartResponse { Code = Invalid }, packet.Id);
            return;
        }
        int chapterId = 0;
        foreach (FangKuaiChapterTable chapter in Chapters.Value)
            if (activity.ChapterIds.Contains(chapter.Id) && chapter.StageGroupIds.Any(groupId =>
                Groups.Value.TryGetValue(groupId, out FangKuaiStageGroupTable? group) &&
                (group.SimpleStageId == stage.Id || group.DiffcultStageId == stage.Id))) { chapterId = chapter.Id; break; }
        if (chapterId == 0)
        {
            session.SendResponse(new FangKuaiStageStartResponse { Code = Invalid }, packet.Id);
            return;
        }
        PlayerFangKuaiState state = Prepare(session.player, activity.Id);
        FangKuaiStageData run = new()
        {
            StageId = stage.Id,
            // v2.0 creates the initial board in Lua *after* this response, then sends its first sync.
            ItemIds = stage.InitialItemIds.ToList(),
            FrenzyVal = stage.InitialFev,
            FallingBlockCd = TableReaderV2.Parse<FangKuaiDropBlockTable>()
                .Where(x => x.StageId == stage.Id && x.ActionRange.Count == 2
                    && x.ActionRange[0] <= 1 && x.ActionRange[1] >= 1)
                .Select(x => x.ActionCd).FirstOrDefault(),
            Combo = 1
        };
        state.StageDataDict[chapterId] = run;
        if (!state.PlayedStageIds.Contains(stage.Id)) state.PlayedStageIds.Add(stage.Id);
        session.player.SaveChecked();
        session.SendResponse(new FangKuaiStageStartResponse { CurData = run }, packet.Id);
        TaskModule.SendFangKuaiTaskSync(session);
    }

    [RequestPacketHandler("FangKuaiStageSyncOperatorRequest")]
    public static void Sync(Session session, Packet.Request packet)
    {
        FangKuaiStageSyncOperatorRequest request = packet.Deserialize<FangKuaiStageSyncOperatorRequest>();
        if (!TryGetRun(session.player, request.StageId, Clock(), out FangKuaiStageTable stage, out FangKuaiStageData run, out int chapter) ||
            request.OperatorData is null || !TryApplyOperator(run, request.OperatorData, stage, out FangKuaiStageData updated))
        {
            session.SendResponse(new FangKuaiStageSyncOperatorResponse { Code = Invalid }, packet.Id);
            return;
        }
        // The v2 board is client-owned. Only shape, authored block/item/clear operands, score math,
        // and the persisted attempt/turn sequence can be verified; this is not physical-move proof.
        session.player.FangKuai.StageDataDict[chapter] = updated;
        session.player.SaveChecked();
        session.SendResponse(new FangKuaiStageSyncOperatorResponse(), packet.Id);
    }

    internal static bool TryApplyOperator(FangKuaiStageData previous, FangKuaiOperatorData op, FangKuaiStageTable stage, out FangKuaiStageData result)
    {
        result = null!;
        int maxItems = Activities.Value.Single(a => a.Id == 5).MaxItemCount;
        if (op.Blocks is null || op.PreviewBlocks is null || op.ItemOperatorList is null || op.ComboScoreList is null ||
            op.EnhancedItemIndexes is null || op.HistoryFrenzyRecords is null ||
            op.Round < previous.Round || op.Round > previous.Round + 1 || op.Round > stage.MaxRound + previous.ExtraRound ||
            previous.Blocks.Count == 0 && previous.PreviewBlocks.Count == 0 && (op.Round != 0 || op.Blocks.Count == 0) ||
            op.Combo < previous.Combo || op.FrenzyVal != 0 || op.FrenzyStage != 0 || op.FrenzyEnergy != 0 || op.HistoryFrenzyRecords.Count != 0 ||
            op.FrozenRoundCount < 0 || op.FrozenRoundCount > stage.MaxUseCount ||
            op.FrozenRoundCount < previous.FrozenRoundCount - (op.Round - previous.Round) ||
            op.FallingBlockCount < previous.FallingBlockCount || op.FallingBlockCd < 0 ||
            op.AccumulatedEnhanceCount < 0 || op.AccumulatedEnhanceCount > EnhanceLimit.Value ||
            op.EnhancedItemIndexes.Distinct().Count() != op.EnhancedItemIndexes.Count ||
            op.EnhancedItemIndexes.Any(index => index < 1 || index > maxItems) ||
            !ValidateBlocks(op, stage)) return false;

        List<int> slots = previous.ItemIds.ToList();
        while (slots.Count < maxItems) slots.Add(0);
        List<int> obtained = previous.HistoryItemIds.ToList();
        List<int> used = previous.HistoryUsedItemIds.ToList();
        List<int> discarded = previous.HistoryDiscardItemIds.ToList();
        int extraRound = previous.ExtraRound;
        foreach (FangKuaiItemOperator item in op.ItemOperatorList)
        {
            if (item is null || item.Index < 1 || item.Index > maxItems || !Items.Value.ContainsKey(item.Id)) return false;
            int index = item.Index - 1;
            if (item.OperatorType == 1)
            {
                if (slots[index] != 0) return false;
                slots[index] = item.Id;
                obtained.Add(item.Id);
            }
            else if (item.OperatorType is 0 or 2)
            {
                if (slots[index] != item.Id) return false;
                slots[index] = 0;
                (item.OperatorType == 0 ? used : discarded).Add(item.Id);
                if (item.OperatorType == 0 && Items.Value[item.Id].Kind == 4)
                    extraRound = checked(extraRound + Items.Value[item.Id].Params.FirstOrDefault());
            }
            else return false;
        }
        if (op.Round > stage.MaxRound + extraRound || op.EnhancedItemIndexes.Any(index => slots[index - 1] == 0)) return false;
        long score = previous.Point;
        int maxCombo = previous.Combo;
        int maxConfiguredCombo = Combos.Value.Keys.Max();
        if (!AllowedScores.Value.TryGetValue(stage.Id, out HashSet<int>? allowedScores)) return false;
        foreach (FangKuaiComboScore clear in op.ComboScoreList)
        {
            if (clear is null || clear.ComboCount < 1 || clear.ComboCount > previous.Combo + op.ComboScoreList.Count + 1 ||
                clear.BaseScore is <= 0 or > int.MaxValue || !allowedScores.Contains((int)clear.BaseScore)) return false;
            int ratio = Combos.Value[Math.Min(clear.ComboCount, maxConfiguredCombo)];
            score += clear.BaseScore * ratio / 10000;
            maxCombo = Math.Max(maxCombo, clear.ComboCount);
        }
        int maxScore = Activities.Value.Single(a => a.Id == 5).MaxLevelScore;
        if (score != op.Point || op.Point < 0 || maxCombo != op.Combo || op.Point > maxScore) return false;
        result = new FangKuaiStageData
        {
            StageId = stage.Id, Point = score, Round = op.Round, ExtraRound = extraRound, Combo = op.Combo,
            FrozenRoundCount = op.FrozenRoundCount, FallingBlockCount = op.FallingBlockCount, FallingBlockCd = op.FallingBlockCd,
            ItemIds = slots, HistoryItemIds = obtained, HistoryUsedItemIds = used, HistoryDiscardItemIds = discarded,
            Blocks = op.Blocks, PreviewBlocks = op.PreviewBlocks,
            AccumulatedEnhanceCount = op.AccumulatedEnhanceCount, EnhancedItemIndexes = op.EnhancedItemIndexes
        };
        return true;
    }

    private static bool ValidateBlocks(FangKuaiOperatorData op, FangKuaiStageTable stage)
    {
        int previewLineCount = Environments.Value.TryGetValue(stage.EnvironmentId, out FangKuaiStageEnvironmentTable? environment)
            && environment.Type == 1 ? environment.Params.Max() : 1;
        if (op.Blocks.Count > stage.SizeX * stage.SizeY ||
            op.PreviewBlocks.Count > stage.SizeX * previewLineCount + 1) return false;
        HashSet<int> ids = [];
        HashSet<(int, int)> cells = [];
        for (int index = 0; index < op.Blocks.Count + op.PreviewBlocks.Count; index++)
        {
            bool onBoard = index < op.Blocks.Count;
            FangKuaiBlockData block = onBoard ? op.Blocks[index] : op.PreviewBlocks[index - op.Blocks.Count];
            if (block is null || block.Id <= 0 || !ids.Add(block.Id) ||
                !Blocks.Value.TryGetValue(block.BlockId, out FangKuaiBlockTable? template) || template.StageId != stage.Id ||
                template.Type != block.Type || block.Length < 1 || block.Length > template.Length ||
                !template.Colors.Contains(block.Color) ||
                (template.Direction != 0 && template.Direction != block.Direction) || block.Direction is < 1 or > 2 ||
                block.HitCount < 0 || block.HitCount > Math.Max(1, template.MaxHitTimes) ||
                block.X < 1 || block.X + block.Length > stage.SizeX + 1 ||
                (block.ItemId != 0 && !Items.Value.ContainsKey(block.ItemId))) return false;
            if (onBoard && (block.Y < 1 || block.Y > stage.SizeY) ||
                !onBoard && (block.Y == 0 || block.Y < -previewLineCount || block.Y > stage.SizeY)) return false;
            if (onBoard)
                for (int x = block.X; x < block.X + block.Length; x++)
                    if (!cells.Add((x, block.Y))) return false;
        }
        return true;
    }

    internal static bool CanSettle(FangKuaiStageData run, FangKuaiStageTable stage, int settleType)
    {
        if (settleType is 2 or 4) return true; // reset / give up
        if (run.Blocks.Count == 0 && run.Point == 0) return false; // no accepted board or earned clear
        if (settleType == 1) return run.Round > 0; // turn limit or unsent overflow, both end a run in Lua
        if (settleType != 3 || stage.SettleScoreGrade <= 0) return false;
        return TableReaderV2.Parse<FangKuaiScoreRateTable>().Any(rate =>
            rate.StageId == stage.Id && rate.Grade == stage.SettleScoreGrade && run.Point >= rate.Score);
    }

    [RequestPacketHandler("FangKuaiStageSettleRequest")]
    public static void Settle(Session session, Packet.Request packet)
    {
        FangKuaiStageSettleRequest request = packet.Deserialize<FangKuaiStageSettleRequest>();
        if (!TryGetRun(session.player, request.StageId, Clock(), out FangKuaiStageTable stage, out FangKuaiStageData run, out int chapterId) ||
            !CanSettle(run, stage, request.SettleType))
        {
            session.SendResponse(new FangKuaiStageSettleResponse { Code = Invalid }, packet.Id);
            return;
        }
        PlayerFangKuaiState state = session.player.FangKuai;
        bool finished = request.SettleType is 1 or 3;
        RewardApplicationResult? reward = null;
        if (finished && state.FinishedStageIds.Count == 0)
        {
            int rewardId = Activities.Value.Single(a => a.Id == state.ActivityId).RewardId;
            var goods = RewardHandler.GetRewardGoods(rewardId);
            if (goods.Count == 0)
            {
                session.SendResponse(new FangKuaiStageSettleResponse { Code = Invalid }, packet.Id);
                return;
            }
            try
            {
                reward = RewardHandler.ApplyRewardsOnceAndPersist(
                    [new RewardGrant($"fangkuai:first:{session.player.PlayerData.Id}:{state.ActivityId}", goods)], session);
            }
            catch (Exception exception)
            {
                session.log.Error($"Failed to grant FangKuai first-stage reward: {exception}");
                session.SendResponse(new FangKuaiStageSettleResponse { Code = Invalid }, packet.Id);
                return;
            }
        }
        FangKuaiStageHistory history = state.StageHistroyDict.TryGetValue(stage.Id, out FangKuaiStageHistory? old) ? old : new();
        bool newScore = finished && run.Point > history.MaxScore;
        bool newRound = finished && run.Round > history.MaxRound;
        if (finished)
        {
            history.MaxScore = Math.Max(history.MaxScore, run.Point);
            history.MaxRound = Math.Max(history.MaxRound, run.Round);
            history.TotalRound = checked(history.TotalRound + run.Round);
            state.StageHistroyDict[stage.Id] = history;
            if (!state.FinishedStageIds.Contains(stage.Id)) state.FinishedStageIds.Add(stage.Id);
            state.TotalScoresByStage[stage.Id] = checked(state.TotalScoresByStage.GetValueOrDefault(stage.Id) + run.Point);
        }
        state.StageDataDict.Remove(chapterId);
        session.player.SaveChecked();
        reward?.SendPushes(session);
        session.SendResponse(new FangKuaiStageSettleResponse { SettleData = new FangKuaiSettleData
        {
            Point = run.Point, Round = run.Round, IsStageFinished = finished,
            IsNewScoreRecord = newScore, IsNewRoundRecord = newRound
        } }, packet.Id);
        TaskModule.SendFangKuaiTaskSync(session);
    }
}
