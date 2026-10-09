using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using System.Reflection;
using MessagePack;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    private static int bigWorldStreetPacketId = 7_700_000;

    private static void ValidateBigWorldStreet()
    {
        ValidateBigWorldStreetLifecycle();
        ValidateBigWorldStreetSeeds();
        ValidateBigWorldStreetBillboardTask();
        Console.WriteLine("BigWorld street: lifecycle, seeds, errors, billboard task and persistence passed.");
    }

    private static (TResponse Response, List<Packet.Push> Pushes) StreetCall<TResponse>(LoopbackSessionHarness h, string requestName, object request)
    {
        int id = ++bigWorldStreetPacketId;
        InvokeRegisteredRequestHandler(requestName, h.Session, id, request);
        List<Packet.Push> pushes = [];
        for (int index = 0; index < 64; index++)
        {
            Packet packet = h.ReadPacket(requestName);
            if (packet.Type == Packet.ContentType.Push)
            {
                pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content));
                continue;
            }
            AssertEqual(id, MessagePackSerializer.Deserialize<Packet.Response>(packet.Content).Id, $"{requestName} response id");
            return (ReadResponsePayload<TResponse>(packet, typeof(TResponse).Name), pushes);
        }
        throw new InvalidDataException($"{requestName} produced no response.");
    }

    private static int StreetCode<TResponse>(LoopbackSessionHarness h, string requestName, object request) where TResponse : ISgStreetResponse =>
        StreetCall<TResponse>(h, requestName, request).Response.Code;

    private static T StreetPush<T>(List<Packet.Push> pushes) =>
        MessagePackSerializer.Deserialize<T>(pushes.Last(p => p.Name == typeof(T).Name).Content);

    private static readonly Type StreetModuleType = typeof(Session).Assembly.GetType("AscNet.GameServer.Handlers.BigWorld.SkyGardenStreetModule")!;

    private static int StreetConst(string name) => (int)StreetModuleType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static int StreetShopScore(SgStreetShopData shop) =>
        (int)StreetModuleType.GetMethod("ShopScore", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [shop])!;

    private static bool StreetCondition(Player player, int conditionId) =>
        (bool)typeof(Session).Assembly.GetType("AscNet.GameServer.Handlers.BigWorld.BigWorldConditionService")!
            .GetMethod("Check", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [player, conditionId])!;

    private static string StreetJson(object value) => MessagePackSerializer.SerializeToJson(value);

    private static LoopbackSessionHarness StreetHarness(long uid) =>
        new(CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid, []), "big-world-street");

    private static SgStreetStageData StreetStage(LoopbackSessionHarness h) => h.Session.player.BigWorldState.SgStreet.CurStageData!;

    private static int StreetGold(LoopbackSessionHarness h) => StreetStage(h).ResourceDatas.Single(r => r.ResourceId == 1).Count;

    private static List<SgStreetEventResult> HandleAllEvents(SgStreetOperatingData data) => data.CustomerDatas
        .SelectMany(c => c.CommandDatas).Where(c => c.EventData is not null)
        .Select(c => new SgStreetEventResult { Id = c.EventData!.Id, EmergencyOptionIndex = c.EventData.Type == 2 ? 1 : 0 }).ToList();

    private static SgStreetSettleData PlayRound(LoopbackSessionHarness h)
    {
        (SgStreetOperatingStartResponse start, _) = StreetCall<SgStreetOperatingStartResponse>(h, nameof(SgStreetOperatingStartRequest), new SgStreetOperatingStartRequest());
        AssertEqual(0, start.Code, "Street operating start");
        (SgStreetOperatingSettleResponse settle, List<Packet.Push> pushes) = StreetCall<SgStreetOperatingSettleResponse>(h, nameof(SgStreetOperatingSettleRequest),
            new SgStreetOperatingSettleRequest { SettleParam = new SgStreetSettleParam { AwardGold = 999_999, EventResults = HandleAllEvents(start.OperatingData!) } });
        AssertEqual(0, settle.Code, "Street operating settle");
        AssertEqual(StreetStage(h).Turn, StreetPush<NotifySgStreetAfterOperatingSettleStageData>(pushes).Data.Turn, "Settle pushes the next-turn stage data");
        return settle.SettleData!;
    }

    private static void ValidateBigWorldStreetLifecycle()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out RecordingMongoCollectionProxy<Player> players, out _, out _);
        using LoopbackSessionHarness h = StreetHarness(7_700_001);
        h.Session.stage = CreateLoginAccountCompatibilityStage(7_700_001);
        Player Reload() => BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson ?? throw new InvalidDataException("Street player not saved."));

        (SgStreetStageEnterResponse enter, List<Packet.Push> enterPushes) = StreetCall<SgStreetStageEnterResponse>(h, nameof(SgStreetStageEnterRequest), new SgStreetStageEnterRequest());
        AssertEqual(0, enter.Code, "Street enter");
        AssertEqual(true, StreetPush<NotifySgStreetData>(enterPushes).Data.CurStageData is null, "Fresh street has no stage");
        AssertEqual(StreetConst("StageDataIsNull"), StreetCode<SgStreetOperatingStartResponse>(h, nameof(SgStreetOperatingStartRequest), new SgStreetOperatingStartRequest()), "Operating without stage");
        AssertEqual(StreetConst("StageDataIsNull"), StreetCode<SgStreetStageWinSettleResponse>(h, nameof(SgStreetStageWinSettleRequest), new SgStreetStageWinSettleRequest { StageId = 1 }), "Win without stage");
        AssertEqual(StreetConst("ConditionError"), StreetCode<SgStreetStageStartResponse>(h, nameof(SgStreetStageStartRequest), new SgStreetStageStartRequest { StageId = 2 }), "Stage 2 locked before stage 1");
        AssertEqual(StreetConst("StageCfgNotFound"), StreetCode<SgStreetStageStartResponse>(h, nameof(SgStreetStageStartRequest), new SgStreetStageStartRequest { StageId = 99 }), "Unknown stage");

        (SgStreetStageStartResponse start, List<Packet.Push> startPushes) = StreetCall<SgStreetStageStartResponse>(h, nameof(SgStreetStageStartRequest), new SgStreetStageStartRequest { StageId = 1 });
        AssertEqual(0, start.Code, "Stage 1 start");
        AssertEqual(1, start.StageData!.Turn, "Stage starts at turn 1");
        AssertEqual(125, start.StageData.ResourceDatas.Single().Count, "Stage 1 InitGold");
        AssertEqual("1011,1012,1013", string.Join(",", start.StageData.TaskDatas.Select(t => t.ConfigId)), "Stage target tasks");
        AssertEqual(true, startPushes.Any(p => p.Name == nameof(NotifySgStreetAttrAdds)), "Stage start pushes attrs");
        AssertEqual(StreetConst("StageIsOngoing"), StreetCode<SgStreetStageStartResponse>(h, nameof(SgStreetStageStartRequest), new SgStreetStageStartRequest { StageId = 1 }), "Second start rejected");
        AssertEqual(false, StreetCondition(h.Session.player, 50000204), "Food store condition before build");

        // Invalid shop / slot boundaries.
        AssertEqual(StreetConst("BuildInvalidShopId"), StreetCode<SgStreetShopBuildResponse>(h, nameof(SgStreetShopBuildRequest), new SgStreetShopBuildRequest { ShopId = 1001, Position = 1 }), "Shop outside stage group");
        AssertEqual(StreetConst("BuildInvalidPosition"), StreetCode<SgStreetShopBuildResponse>(h, nameof(SgStreetShopBuildRequest), new SgStreetShopBuildRequest { ShopId = 1008, Position = 2 }), "Locked inside slot");
        AssertEqual(StreetConst("BuildInvalidPosition"), StreetCode<SgStreetShopBuildResponse>(h, nameof(SgStreetShopBuildRequest), new SgStreetShopBuildRequest { ShopId = 1008, Position = 7 }), "Slot beyond InsideShowMaxNum");
        AssertEqual(StreetConst("BuildInvalidPosition"), StreetCode<SgStreetShopBuildResponse>(h, nameof(SgStreetShopBuildRequest), new SgStreetShopBuildRequest { ShopId = 2003, Position = 1 }), "Locked outside slot");

        (SgStreetShopBuildResponse build, List<Packet.Push> buildPushes) = StreetCall<SgStreetShopBuildResponse>(h, nameof(SgStreetShopBuildRequest), new SgStreetShopBuildRequest { ShopId = 1008, Position = 1 });
        AssertEqual(0, build.Code, "Build food shop");
        AssertEqual(75, StreetPush<NotifySgStreetResourceChange>(buildPushes).Datas.Single().Count, "Build cost 50 deducted");
        AssertEqual("1008", string.Join(",", build.CurrentTurnInsideBuilds), "Inside build recorded");
        AssertEqual(true, build.ShopData!.FoodData is not null && build.ShopData.FoodLikeData is not null, "Food setup and preference created");
        AssertEqual(true, StreetCondition(h.Session.player, 50000204), "Food store condition after build");
        AssertEqual(StreetConst("BuildPositionUsed"), StreetCode<SgStreetShopBuildResponse>(h, nameof(SgStreetShopBuildRequest), new SgStreetShopBuildRequest { ShopId = 1010, Position = 1 }), "Occupied slot");
        AssertEqual(StreetConst("ShopBuildInCd"), StreetCode<SgStreetShopBuildResponse>(h, nameof(SgStreetShopBuildRequest), new SgStreetShopBuildRequest { ShopId = 1010, Position = 5 }), "One inside build per turn");
        AssertEqual(StreetConst("ShopDataIsExist"), StreetCode<SgStreetShopBuildResponse>(h, nameof(SgStreetShopBuildRequest), new SgStreetShopBuildRequest { ShopId = 1008, Position = 5 }), "Duplicate shop");

        SgStreetFoodData like = build.ShopData.FoodLikeData!;
        AssertEqual(StreetConst("FoodInvalidChef"), StreetCode<SgStreetShopSetupFoodResponse>(h, nameof(SgStreetShopSetupFoodRequest),
            new SgStreetShopSetupFoodRequest { ShopId = 1008, ChefId = 99, GoodsCountList = like.GoodsCountList, GoldCount = like.Gold }), "Invalid chef");
        AssertEqual(StreetConst("FoodInvalidGold"), StreetCode<SgStreetShopSetupFoodResponse>(h, nameof(SgStreetShopSetupFoodRequest),
            new SgStreetShopSetupFoodRequest { ShopId = 1008, ChefId = like.ChefId, GoodsCountList = like.GoodsCountList, GoldCount = 1000 }), "Invalid price");
        AssertEqual(StreetConst("FoodInvalidGoodsCount"), StreetCode<SgStreetShopSetupFoodResponse>(h, nameof(SgStreetShopSetupFoodRequest),
            new SgStreetShopSetupFoodRequest { ShopId = 1008, ChefId = like.ChefId, GoodsCountList = [1], GoldCount = like.Gold }), "Invalid goods count");
        AssertEqual(StreetConst("GroceryTypeError"), StreetCode<SgStreetShopSetupGroceryResponse>(h, nameof(SgStreetShopSetupGroceryRequest),
            new SgStreetShopSetupGroceryRequest { ShopId = 1008 }), "Grocery setup on food shop");
        AssertEqual(StreetConst("ShopDataNotExist"), StreetCode<SgStreetShopSetupGroceryResponse>(h, nameof(SgStreetShopSetupGroceryRequest),
            new SgStreetShopSetupGroceryRequest { ShopId = 1010 }), "Setup on unbuilt shop");
        SgStreetShopSetupFoodResponse setup = StreetCall<SgStreetShopSetupFoodResponse>(h, nameof(SgStreetShopSetupFoodRequest),
            new SgStreetShopSetupFoodRequest { ShopId = 1008, ChefId = like.ChefId, GoodsCountList = like.GoodsCountList, GoldCount = like.Gold }).Response;
        AssertEqual(0, setup.Code, "Food setup matching preference");
        AssertEqual(50000, StreetShopScore(setup.ShopData!), "Perfect setup scores MaxShopScore");

        int branch = build.ShopData.UpgradeBranchIds[0];
        AssertEqual(StreetConst("UpgradeInvalidBranchId"), StreetCode<SgStreetShopUpgradeResponse>(h, nameof(SgStreetShopUpgradeRequest), new SgStreetShopUpgradeRequest { ShopId = 1008, BranchId = 12345 }), "Invalid branch");
        AssertEqual(StreetConst("ResourceNotEnough"), StreetCode<SgStreetShopUpgradeResponse>(h, nameof(SgStreetShopUpgradeRequest), new SgStreetShopUpgradeRequest { ShopId = 1008, BranchId = branch }), "Upgrade cost 100 > 75 gold");
        AssertEqual(75, StreetGold(h), "Failed upgrade keeps gold");
        AssertEqual(StreetConst("RemoveCountLimit"), StreetCode<SgStreetShopRemoveResponse>(h, nameof(SgStreetShopRemoveRequest), new SgStreetShopRemoveRequest { ShopId = 1008 }), "Last inside shop kept");
        AssertEqual(StreetConst("SetRecommendTypeError"), StreetCode<SgStreetShopSetRecommendResponse>(h, nameof(SgStreetShopSetRecommendRequest), new SgStreetShopSetRecommendRequest { ShopId = 1008 }), "Recommend inside shop");

        // Round 1: validated settle, server-side gold.
        (SgStreetOperatingStartResponse op, _) = StreetCall<SgStreetOperatingStartResponse>(h, nameof(SgStreetOperatingStartRequest), new SgStreetOperatingStartRequest());
        AssertEqual(0, op.Code, "Operating start");
        AssertEqual(5, op.OperatingData!.CustomerDatas.Count, "Customers = InitCustomerNum with no traffic shops");
        AssertEqual(StreetConst("OperatingDataIsExist"), StreetCode<SgStreetOperatingStartResponse>(h, nameof(SgStreetOperatingStartRequest), new SgStreetOperatingStartRequest()), "Second operating start");
        AssertEqual(StreetConst("OperatingDataIsExist"), StreetCode<SgStreetShopBuildResponse>(h, nameof(SgStreetShopBuildRequest), new SgStreetShopBuildRequest { ShopId = 1010, Position = 5 }), "No edits while operating");
        AssertEqual(StreetConst("EventDataIsNull"), StreetCode<SgStreetOperatingSettleResponse>(h, nameof(SgStreetOperatingSettleRequest),
            new SgStreetOperatingSettleRequest { SettleParam = new SgStreetSettleParam { EventResults = [new SgStreetEventResult { Id = -5 }] } }), "Unknown event id");
        Player midRound = Reload();
        AssertEqual(StreetJson(op.OperatingData), StreetJson(midRound.BigWorldState.SgStreet.CurStageData!.OperatingData!), "Open round persists for auto-resume");

        List<SgStreetCommandData> commands = op.OperatingData.CustomerDatas.SelectMany(c => c.CommandDatas).ToList();
        int expectedGold = commands.Where(c => c.Type != 3).Sum(c => c.ShopAwardGold)
            + commands.Where(c => c.EventData?.Type == 1).Sum(c => c.EventData!.DiscontentAwardGold);
        (SgStreetOperatingSettleResponse settle, _) = StreetCall<SgStreetOperatingSettleResponse>(h, nameof(SgStreetOperatingSettleRequest),
            new SgStreetOperatingSettleRequest { SettleParam = new SgStreetSettleParam { AwardGold = 999_999, EventResults = HandleAllEvents(op.OperatingData) } });
        AssertEqual(0, settle.Code, "Round 1 settle");
        AssertEqual(expectedGold, settle.SettleData!.CommandAwardGold + settle.SettleData.DiscontentAwardGold, "Server recomputes shop + discontent gold");
        AssertEqual(75 + settle.SettleData.AwardGold, StreetGold(h), "Gold credited once, client total ignored");
        AssertEqual(2, StreetStage(h).Turn, "Turn advances");
        AssertEqual(50000, StreetStage(h).ShopDatas.Single().Score, "Score published at settle");
        AssertEqual(StreetConst("OperatingDataNotExist"), StreetCode<SgStreetOperatingSettleResponse>(h, nameof(SgStreetOperatingSettleRequest), new SgStreetOperatingSettleRequest()), "Settle twice");

        int maxTurn = 15;
        while (StreetStage(h).Turn < maxTurn)
            PlayRound(h);
        AssertEqual(StreetConst("StageIsCompleted"), StreetCode<SgStreetOperatingStartResponse>(h, nameof(SgStreetOperatingStartRequest), new SgStreetOperatingStartRequest()), "No round at MaxTurn");
        AssertEqual(StreetConst("StageNotStart"), StreetCode<SgStreetStageWinSettleResponse>(h, nameof(SgStreetStageWinSettleRequest), new SgStreetStageWinSettleRequest { StageId = 2 }), "Win for another stage");

        List<int> finishedIndexes = StreetStage(h).TaskDatas.Select((t, i) => (t, i)).Where(x => x.t.State == 3).Select(x => x.i + 1).ToList();
        (SgStreetStageWinSettleResponse win, List<Packet.Push> winPushes) = StreetCall<SgStreetStageWinSettleResponse>(h, nameof(SgStreetStageWinSettleRequest), new SgStreetStageWinSettleRequest { StageId = 1 });
        AssertEqual(0, win.Code, "Stage 1 win");
        AssertEqual(true, win.IsNewStagePassed, "First pass");
        AssertEqual(true, win.RewardGoodsList.Count > 0, "Stage reward granted");
        NotifySgStreetStageSettle stageSettle = StreetPush<NotifySgStreetStageSettle>(winPushes);
        AssertEqual(string.Join(",", finishedIndexes), string.Join(",", stageSettle.PassedStageRecords[1].RewardIndexRecord), "Finished target task indexes recorded");
        AssertEqual(true, stageSettle.StageData is null && stageSettle.SceneData!.ShopDatas.Count == 1, "Scene keeps the last stage shops");
        AssertEqual(true, StreetCondition(h.Session.player, 50000001), "Stage 1 cleared condition");
        AssertEqual(false, StreetCondition(h.Session.player, 50000002), "Stage 2 not cleared");

        Player saved = Reload();
        AssertEqual(StreetJson(h.Session.player.BigWorldState.SgStreet), StreetJson(saved.BigWorldState.SgStreet), "Street state round-trips through BSON");
        AssertEqual(true, saved.BigWorldState.SgStreet.CurStageData is null && saved.BigWorldState.SgStreet.PassedStageRecords.ContainsKey(1), "Reload keeps pass record");

        // Replay stage 1: no second first-pass reward; give up clears.
        AssertEqual(0, StreetCode<SgStreetStageStartResponse>(h, nameof(SgStreetStageStartRequest), new SgStreetStageStartRequest { StageId = 1 }), "Replay passed stage");
        AssertEqual(StreetConst("StageIsNotCompleted"), StreetCode<SgStreetStageWinSettleResponse>(h, nameof(SgStreetStageWinSettleRequest), new SgStreetStageWinSettleRequest { StageId = 1 }), "Win before completion");
        AssertEqual(0, StreetCode<SgStreetStageGiveUpResponse>(h, nameof(SgStreetStageGiveUpRequest), new SgStreetStageGiveUpRequest { StageId = 1 }), "Give up");
        AssertEqual(true, Reload().BigWorldState.SgStreet.CurStageData is null, "Give up persisted");
        AssertEqual(0, StreetCode<SgStreetStageStartResponse>(h, nameof(SgStreetStageStartRequest), new SgStreetStageStartRequest { StageId = 2 }), "Stage 2 unlocked by pass");
    }

    private static string StreetSeedRun(long uid)
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness h = StreetHarness(uid);
        h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        AssertEqual(0, StreetCode<SgStreetStageStartResponse>(h, nameof(SgStreetStageStartRequest), new SgStreetStageStartRequest { StageId = 1 }), "Seed stage start");
        AssertEqual(0, StreetCode<SgStreetShopBuildResponse>(h, nameof(SgStreetShopBuildRequest), new SgStreetShopBuildRequest { ShopId = 1008, Position = 1 }), "Seed build");
        List<string> rounds = [];
        for (int i = 0; i < 3; i++)
            rounds.Add(StreetJson(PlayRound(h)));
        return StreetJson(StreetStage(h).ShopDatas) + string.Join("|", rounds);
    }

    private static void ValidateBigWorldStreetSeeds()
    {
        string a1 = StreetSeedRun(7_700_101), a2 = StreetSeedRun(7_700_101), b = StreetSeedRun(7_700_202);
        AssertEqual(a1, a2, "Same seed replays identically");
        AssertEqual(true, a1 != b, "Different seed changes preferences/customers");
    }

    private static void ValidateBigWorldStreetBillboardTask()
    {
        for (long uid = 7_700_300; uid < 7_700_700; uid++)
        {
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out RecordingMongoCollectionProxy<Player> players, out _, out _);
            using LoopbackSessionHarness h = StreetHarness(uid);
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            SgStreetData street = h.Session.player.BigWorldState.SgStreet;
            for (int stageId = 1; stageId <= 4; stageId++)
                street.PassedStageRecords[stageId] = new SgStreetPassedStageRecord();
            SgStreetStageStartResponse start = StreetCall<SgStreetStageStartResponse>(h, nameof(SgStreetStageStartRequest), new SgStreetStageStartRequest { StageId = 5 }).Response;
            AssertEqual(0, start.Code, "Stage 5 start");
            AssertEqual(2, start.StageData!.BillboardData!.RandomBillboards.Count, "BillboardTaskGenNum billboards offered");
            if (!start.StageData.BillboardData.RandomBillboards.Contains(2091))
                continue;

            AssertEqual(StreetConst("StageNotBillboardSelect"), StreetCode<SgStreetOperatingStartResponse>(h, nameof(SgStreetOperatingStartRequest), new SgStreetOperatingStartRequest()), "Billboard must be chosen first");
            AssertEqual(StreetConst("BillboardIdNotFound"), StreetCode<SgStreetBillboardSelectResponse>(h, nameof(SgStreetBillboardSelectRequest), new SgStreetBillboardSelectRequest { BillboardId = 9999 }), "Unoffered billboard");
            (SgStreetBillboardSelectResponse select, _) = StreetCall<SgStreetBillboardSelectResponse>(h, nameof(SgStreetBillboardSelectRequest), new SgStreetBillboardSelectRequest { BillboardId = 2091 });
            AssertEqual(0, select.Code, "Billboard select");
            AssertEqual(true, StreetStage(h).BuffDatas.Any(b => b.BuffConfigId == 220912), "Restrict buff applied");
            AssertEqual(StreetConst("TaskFinishInvalidState"), StreetCode<SgStreetFinishTasksResponse>(h, nameof(SgStreetFinishTasksRequest), new SgStreetFinishTasksRequest { TaskIds = [select.TaskId] }), "Unachieved task");
            int stageTask = StreetStage(h).TaskDatas.First(t => t.Source == 1).Id;
            AssertEqual(StreetConst("TaskFinishInvalidSource"), StreetCode<SgStreetFinishTasksResponse>(h, nameof(SgStreetFinishTasksRequest), new SgStreetFinishTasksRequest { TaskIds = [stageTask] }), "Stage target is not client-finished");

            StreetStage(h).ResourceDatas.Single().Count = 1000; // test setup: enough funds for build + upgrade
            AssertEqual(0, StreetCode<SgStreetShopBuildResponse>(h, nameof(SgStreetShopBuildRequest), new SgStreetShopBuildRequest { ShopId = 1001, Position = 1 }), "Stage 5 build");
            SgStreetShopData shop = StreetStage(h).ShopDatas.Single();
            (SgStreetShopUpgradeResponse upgrade, List<Packet.Push> upgradePushes) = StreetCall<SgStreetShopUpgradeResponse>(h, nameof(SgStreetShopUpgradeRequest),
                new SgStreetShopUpgradeRequest { ShopId = 1001, BranchId = shop.UpgradeBranchIds[0] });
            AssertEqual(0, upgrade.Code, "Upgrade");
            AssertEqual(2, upgrade.ShopData!.Level, "Level 2");
            AssertEqual(2, StreetPush<NotifySgStreetTaskData>(upgradePushes).TaskDatas.Single(t => t.Id == select.TaskId).State, "Upgrade task achieved");

            (SgStreetFinishTasksResponse finish, List<Packet.Push> finishPushes) = StreetCall<SgStreetFinishTasksResponse>(h, nameof(SgStreetFinishTasksRequest), new SgStreetFinishTasksRequest { TaskIds = [select.TaskId] });
            AssertEqual(0, finish.Code, "Finish billboard task");
            AssertEqual(select.TaskId.ToString(), string.Join(",", finish.FinishedTaskIds), "Finished ids");
            List<int> buffs = StreetPush<NotifySgStreetBuffsData>(finishPushes).BuffDatas.Select(b => b.BuffConfigId).ToList();
            AssertEqual(true, buffs.Contains(220911) && !buffs.Contains(220912), "Reward buff granted, restrict buff removed");
            AssertEqual(1, StreetStage(h).StatisticsData.FinishTaskTimeDict[2], "Billboard finish counted");
            AssertEqual(StreetConst("TaskIdNotFound"), StreetCode<SgStreetFinishTasksResponse>(h, nameof(SgStreetFinishTasksRequest), new SgStreetFinishTasksRequest { TaskIds = [select.TaskId] }), "Task reward only once");
            AssertEqual(1, StreetStage(h).BuffDatas.Count(b => b.BuffConfigId == 220911), "Reward buff not duplicated");

            PlayRound(h);
            AssertEqual(true, StreetStage(h).NewsDatas.Any(n => n.Turn == 2), "Stage 5 rolls turn news");
            Player saved = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
            AssertEqual(StreetJson(h.Session.player.BigWorldState.SgStreet), StreetJson(saved.BigWorldState.SgStreet), "Stage 5 state persists");
            return;
        }
        throw new InvalidDataException("No seed in range offered billboard 2091.");
    }
}
