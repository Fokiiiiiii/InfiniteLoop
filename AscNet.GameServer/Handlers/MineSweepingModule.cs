using System.Security.Cryptography;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.miniactivity.minesweepinggame;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.GameServer.Handlers;

internal static class MineSweepingModule
{
    // XMineSweepingConfigs.StageState / GridType.
    internal const int Prepare = 1, Sweeping = 2, Finish = 3, Failed = 4;
    internal const int GridSafe = 1, GridMine = 2, GridFlag = 3;

    // EN share/text/CodeText (4.8): MineSweeping* 20134001-20134014 plus common item/server codes.
    internal const int ActivityNotOpen = 20134001, ChapterConfigMissing = 20134002, StageNotStart = 20134004,
        StageConfigMissing = 20134005, YOutRange = 20134007, XOutRange = 20134008, FlagError = 20134009,
        StageHadStarted = 20134010, PreChapterNotFinish = 20134011, PreStageNotFinish = 20134012,
        StageNotInChapter = 20134013, GridIsSwept = 20134014, ItemCountNotEnough = 20012004, ServerInternalError = 2;

    private static readonly Lazy<List<MineSweepingActivityTable>> Activities = new(() => TableReaderV2.Parse<MineSweepingActivityTable>());
    private static readonly Lazy<Dictionary<int, MineSweepingChapterTable>> Chapters = new(() => TableReaderV2.Parse<MineSweepingChapterTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, MineSweepingStageTable>> Stages = new(() => TableReaderV2.Parse<MineSweepingStageTable>().ToDictionary(row => row.Id));

    // AscNet policy: MineSweepingStage authors no mine count; a quarter of each board is mined.
    // Help text 1008404 ("stage content will remain the same") => one persisted layout per stage.
    internal static int MineCount(MineSweepingStageTable stage) => stage.RowCount * stage.ColumnCount / 4;

    internal static MineSweepingActivityTable? ActiveActivity(DateTimeOffset now) => Activities.Value
        .Where(row => row.TimeId > 0 && ChapterIds(row).Any() && ActivityScheduleService.IsOpen(row.TimeId, now))
        .OrderByDescending(row => row.Id).FirstOrDefault();

    private static IEnumerable<int> ChapterIds(MineSweepingActivityTable activity) => activity.ChapterIds.Where(id => id > 0);
    private static List<int> StageIds(MineSweepingChapterTable chapter) => chapter.ActivityStageIds.Where(id => id > 0).ToList();
    private static List<int> Chain(MineSweepingActivityTable activity) =>
        ChapterIds(activity).SelectMany(id => StageIds(Chapters.Value[id])).ToList();

    private static MineSweepingStageState Stage(MineSweepingState state, int stageId) =>
        state.Stages.TryGetValue(stageId, out MineSweepingStageState? value) ? value : new();

    internal static int AllowMineNumber(MineSweepingStageTable stage, int failedCounts)
    {
        // xuipanelsettlement.lua shows CanFailedCounts[failed + 2] - [failed + 1] as the next attempt's bonus.
        List<int> allowed = stage.CanFailedCounts.Where(count => count > 0).ToList();
        return allowed[Math.Min(failedCounts, allowed.Count - 1)];
    }

    private static MineSweepingStageInfo Info(MineSweepingStageTable stage, MineSweepingStageState state) => new()
    {
        ActivityStageId = stage.Id,
        WhiteGridTotalNumber = stage.RowCount * stage.ColumnCount - MineCount(stage),
        AllowMineNumber = AllowMineNumber(stage, state.FailedCounts),
        WhiteGridOpenNumber = state.WhiteGridOpenNumber,
        MineGridOpenNumber = state.MineGridOpenNumber,
        FailedCounts = state.FailedCounts,
        Status = state.Status
    };

    private static int CurrentStageId(MineSweepingChapterTable chapter, MineSweepingState state)
    {
        List<int> ids = StageIds(chapter);
        return ids.FirstOrDefault(id => Stage(state, id).Status != Finish, ids[^1]);
    }

    private static int RoundMines(MineSweepingStageTable stage, MineSweepingStageState state, int cell) =>
        Around(stage, cell).Count(state.Mines.Contains);

    private static IEnumerable<int> Around(MineSweepingStageTable stage, int cell)
    {
        int x = cell % stage.ColumnCount, y = cell / stage.ColumnCount;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (x + dx >= 0 && x + dx < stage.ColumnCount && y + dy >= 0 && y + dy < stage.RowCount)
                    yield return (y + dy) * stage.ColumnCount + x + dx;
    }

    private static MineSweepingGridData Grid(MineSweepingStageTable stage, MineSweepingStageState state, int cell) => new()
    {
        XIndex = cell % stage.ColumnCount + 1,
        YIndex = cell / stage.ColumnCount + 1,
        Type = state.Opened.Contains(cell) ? (state.Mines.Contains(cell) ? GridMine : GridSafe) : GridFlag,
        RoundMineNumber = state.Opened.Contains(cell) && !state.Mines.Contains(cell) ? RoundMines(stage, state, cell) : 0
    };

    internal static NotifyMineSweepingData BuildNotify(Player player, MineSweepingActivityTable activity)
    {
        MineSweepingState state = Reconcile(player, activity.Id);
        return new NotifyMineSweepingData
        {
            ActivityId = activity.Id,
            MineSweepingList = ChapterIds(activity).Select(chapterId =>
            {
                MineSweepingChapterTable chapter = Chapters.Value[chapterId];
                MineSweepingStageTable current = Stages.Value[CurrentStageId(chapter, state)];
                MineSweepingStageState currentState = Stage(state, current.Id);
                return new MineSweepingChapterData
                {
                    ActivityChapterId = chapterId,
                    ChallengeCounts = state.ChallengeCounts.GetValueOrDefault(chapterId),
                    ActivityStageList = StageIds(chapter).Select(id => Info(Stages.Value[id], Stage(state, id))).ToList(),
                    // Only revealed/flagged cells; hidden mines never leave the server.
                    CurGridList = currentState.Opened.Concat(currentState.Flagged).Order()
                        .Select(cell => Grid(current, currentState, cell)).ToList()
                };
            }).ToList()
        };
    }

    /// <summary>Main login hook: settles any durable pending claim, then returns the push (null when closed).</summary>
    internal static NotifyMineSweepingData? BuildLogin(Session session)
    {
        RecoverPending(session);
        MineSweepingActivityTable? activity = ActiveActivity(DateTimeOffset.UtcNow);
        return activity is null ? null : BuildNotify(session.player, activity);
    }

    /// <summary>Main resume/relogin hook: same contract as login, sent immediately.</summary>
    internal static void Resume(Session session)
    {
        if (BuildLogin(session) is { } notify) session.SendPush(notify);
    }

    [RequestPacketHandler("MineSweepingStartStageRequest")]
    public static void StartStage(Session session, Packet.Request packet)
    {
        MineSweepingStartStageRequest request = packet.Deserialize<MineSweepingStartStageRequest>();
        MineSweepingStartStageResponse response = new();
        response.Code = Execute(session, request.ActivityChapterId, request.ActivityStageId, (activity, chapter, stage, state) =>
        {
            MineSweepingStageState run = Stage(state, stage.Id);
            if (run.Status is not (Prepare or Failed)) return StageHadStarted;
            if (CurrentStageId(chapter, state) != stage.Id) return PreStageNotFinish;
            List<int> chain = Chain(activity);
            int index = chain.IndexOf(stage.Id);
            if (index > 0 && Stage(state, chain[index - 1]).Status != Finish) return PreChapterNotFinish;
            int count = state.ChallengeCounts[chapter.Id] = state.ChallengeCounts.GetValueOrDefault(chapter.Id) + 1;
            if (run.Status == Prepare)
            {
                long coins = session.inventory.Items.FirstOrDefault(item => item.Id == activity.CoinItemId)?.Count ?? 0;
                if (coins < stage.CostCoinNum) return ItemCountNotEnough;
                if (stage.CostCoinNum > 0)
                    state.Pending = new()
                    {
                        // Chapter challenge count is monotonic, so each paid start owns one claim.
                        ClaimKey = $"minesweeping:{activity.Id}:{chapter.Id}:start:{count}",
                        CostItemId = activity.CoinItemId, CostCount = stage.CostCoinNum
                    };
            }
            else
            {
                run.FailedCounts++; // Help 1008404: a failed stage replays free with more attempts.
            }
            if (run.Mines.Count == 0) run.Mines = Generate(stage);
            run.Status = Sweeping;
            run.Opened = [];
            run.Flagged = [];
            run.WhiteGridOpenNumber = 0;
            run.MineGridOpenNumber = 0;
            state.Stages[stage.Id] = run;
            response.ChallengeCounts = count;
            response.ActivityStageInfo = Info(stage, run);
            return 0;
        }, out RewardApplicationResult? grant);
        session.SendResponse(response, packet.Id);
        grant?.SendPushes(session);
    }

    [RequestPacketHandler("MineSweepingOpenRequest")]
    public static void Open(Session session, Packet.Request packet)
    {
        MineSweepingOpenRequest request = packet.Deserialize<MineSweepingOpenRequest>();
        MineSweepingOpenResponse response = new();
        bool chapterFinished = false;
        MineSweepingActivityTable? finishedActivity = null;
        response.Code = Execute(session, request.ActivityChapterId, request.ActivityStageId, (activity, chapter, stage, state) =>
        {
            MineSweepingStageState run = Stage(state, stage.Id);
            if (run.Status != Sweeping) return StageNotStart;
            int cell = Cell(stage, request.XIndex, request.YIndex, out int bounds);
            if (bounds != 0) return bounds;
            if (run.Opened.Contains(cell)) return GridIsSwept;
            if (run.Flagged.Contains(cell)) return FlagError;
            List<int> refreshed = [cell];
            run.Opened.Add(cell);
            if (run.Mines.Contains(cell))
            {
                // Help 1008403: a mine lights only itself and adds one failure progress.
                run.MineGridOpenNumber++;
                if (run.MineGridOpenNumber >= AllowMineNumber(stage, run.FailedCounts)) run.Status = Failed;
            }
            else
            {
                // Help 1008402: a safe tap lights every safe cell in its 3x3 area (not recursive).
                run.WhiteGridOpenNumber++;
                foreach (int near in Around(stage, cell))
                {
                    if (near == cell || run.Mines.Contains(near) || run.Opened.Contains(near)) continue;
                    run.Flagged.Remove(near);
                    run.Opened.Add(near);
                    run.WhiteGridOpenNumber++;
                    refreshed.Add(near);
                }
                if (run.WhiteGridOpenNumber == stage.RowCount * stage.ColumnCount - MineCount(stage))
                {
                    run.Status = Finish;
                    run.Flagged = [];
                    // Help 1008405: clearing a stage grants its reward; a finished stage never restarts.
                    state.Pending = new() { ClaimKey = $"minesweeping:{activity.Id}:{stage.Id}:clear", RewardId = stage.RewardId };
                    chapterFinished = StageIds(chapter).All(id => id == stage.Id || Stage(state, id).Status == Finish);
                    finishedActivity = activity;
                }
            }
            response.ActivityStageInfo = Info(stage, run);
            response.RefreshGridList = refreshed.Select(value => Grid(stage, run, value)).ToList();
            return 0;
        }, out RewardApplicationResult? grant);
        if (grant is not null) response.RewardGoodsList = grant.RewardGoods;
        session.SendResponse(response, packet.Id);
        grant?.SendPushes(session);
        // xminesweepingmanager.lua: NotifyMineSweepingData is pushed at login and on a chapter's last clear.
        if (response.Code == 0 && chapterFinished && finishedActivity is not null)
            session.SendPush(BuildNotify(session.player, finishedActivity));
    }

    [RequestPacketHandler("MineSweepingFlagRequest")]
    public static void Flag(Session session, Packet.Request packet)
    {
        MineSweepingFlagRequest request = packet.Deserialize<MineSweepingFlagRequest>();
        int code = Execute(session, request.ActivityChapterId, request.ActivityStageId, (_, _, stage, state) =>
        {
            MineSweepingStageState run = Stage(state, stage.Id);
            if (run.Status != Sweeping) return StageNotStart;
            int cell = Cell(stage, request.XIndex, request.YIndex, out int bounds);
            if (bounds != 0) return bounds;
            if (run.Opened.Contains(cell)) return GridIsSwept;
            if (run.Flagged.Contains(cell) == request.IsFlag) return FlagError;
            if (request.IsFlag) run.Flagged.Add(cell);
            else run.Flagged.Remove(cell);
            return 0;
        }, out _);
        session.SendResponse(new MineSweepingFlagResponse { Code = code }, packet.Id);
    }

    private static int Cell(MineSweepingStageTable stage, int x, int y, out int code)
    {
        code = x < 1 || x > stage.ColumnCount ? XOutRange : y < 1 || y > stage.RowCount ? YOutRange : 0;
        return (y - 1) * stage.ColumnCount + x - 1;
    }

    private delegate int Mutation(MineSweepingActivityTable activity, MineSweepingChapterTable chapter,
        MineSweepingStageTable stage, MineSweepingState state);

    // Mutates a staged copy, persists it (with any claim intent) before touching wallet/rewards,
    // then settles the claim through the idempotent RewardHandler receipt.
    private static int Execute(Session session, int chapterId, int stageId, Mutation mutate, out RewardApplicationResult? grant)
    {
        grant = null;
        int recovered = Recover(session);
        if (recovered != 0) return recovered;
        MineSweepingActivityTable? activity = ActiveActivity(DateTimeOffset.UtcNow);
        if (activity is null) return ActivityNotOpen;
        if (!ChapterIds(activity).Contains(chapterId) || !Chapters.Value.TryGetValue(chapterId, out MineSweepingChapterTable? chapter))
            return ChapterConfigMissing;
        if (!StageIds(chapter).Contains(stageId)) return StageNotInChapter;
        if (!Stages.Value.TryGetValue(stageId, out MineSweepingStageTable? stage)) return StageConfigMissing;

        MineSweepingState original = Reconcile(session.player, activity.Id);
        MineSweepingState staged = BsonSerializer.Deserialize<MineSweepingState>(original.ToBson());
        int code = mutate(activity, chapter, stage, staged);
        if (code != 0) return code;
        session.player.MineSweeping = staged;
        try { session.player.SaveChecked(); }
        catch (Exception exception)
        {
            session.player.MineSweeping = original;
            session.log.Error($"MineSweeping save failed: {exception.Message}");
            return ServerInternalError;
        }
        // State is durable; an unsettled claim stays pending and is recovered on the next request/login.
        try { grant = Settle(session); }
        catch (Exception exception) { session.log.Error($"MineSweeping claim deferred: {exception.Message}"); }
        return 0;
    }

    /// <summary>
    /// Session gate (Main): run before dispatching any request other than Login/Handshake. Returns true when no
    /// MineSweeping payment/reward intent remains; false keeps the intent durable and the caller must refuse the request.
    /// </summary>
    internal static bool RecoverPending(Session session) => Recover(session) == 0;

    private static int Recover(Session session)
    {
        try
        {
            Settle(session)?.SendPushes(session);
            return 0;
        }
        catch (InvalidOperationException exception)
        {
            // Unpaid start cost stays pending until the wallet can pay it; never waived.
            session.log.Error($"MineSweeping pending claim unpaid: {exception.Message}");
            return ItemCountNotEnough;
        }
        catch (Exception exception)
        {
            session.log.Error($"MineSweeping pending claim recovery failed: {exception.Message}");
            return ServerInternalError;
        }
    }

    private static RewardApplicationResult? Settle(Session session)
    {
        if (session.player.MineSweeping?.Pending is not { } pending) return null;
        List<RewardGoodsTable> goods = pending.RewardId > 0 ? RewardHandler.GetRewardGoods(pending.RewardId) : [];
        if (pending.RewardId > 0 && goods.Count == 0) throw new InvalidDataException($"Missing MineSweeping reward {pending.RewardId}.");
        RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist([new RewardGrant(pending.ClaimKey, goods,
            pending.CostCount > 0 ? new Dictionary<int, int> { [pending.CostItemId] = pending.CostCount } : null)], session);
        session.player.MineSweeping.Pending = null;
        session.player.SaveChecked();
        return result;
    }

    private static MineSweepingState Reconcile(Player player, int activityId)
    {
        player.MineSweeping ??= new();
        if (player.MineSweeping.ActivityId != activityId)
            player.MineSweeping = new MineSweepingState { ActivityId = activityId };
        player.MineSweeping.ChallengeCounts ??= [];
        player.MineSweeping.Stages ??= [];
        return player.MineSweeping;
    }

    private static List<int> Generate(MineSweepingStageTable stage)
    {
        int[] cells = Enumerable.Range(0, stage.RowCount * stage.ColumnCount).ToArray();
        RandomNumberGenerator.Shuffle(cells.AsSpan());
        return cells.Take(MineCount(stage)).Order().ToList();
    }
}
