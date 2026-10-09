using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.bigworld.skygarden.cafe;
using AscNet.Table.V2.share.bigworld.skygarden.sgdronegame;
using AscNet.Table.V2.share.statussyncfight.quest;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    public static void RunBigWorldSkyGardenCompatibility()
    {
        const long playerId = 99_947;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _);
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, []), "big-world-skygarden");
        Session session = harness.Session;
        Dictionary<int, int> questByObjective = TableReaderV2.Parse<DlcQuestObjectiveTable>().ToDictionary(row => row.Id, row => row.QuestId);
        void FinishObjective(int objectiveId) => player.BigWorldState.QuestData.FinishedQuests.Add(questByObjective[objectiveId]);
        long Count(int id) => session.inventory.Items.FirstOrDefault(item => item.Id == id)?.Count ?? 0;

        int packetId = 4_700;
        List<(string Name, byte[] Content)> pushes = [];
        T Call<T>(string name, object? request)
        {
            int id = ++packetId;
            pushes.Clear();
            InvokeRegisteredRequestHandler(name, session, id, request);
            while (true)
            {
                Packet packet = harness.ReadPacket(name);
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    pushes.Add((push.Name, push.Content));
                    continue;
                }
                Packet.Response wire = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, wire.Id, $"{name} response id");
                return MessagePackSerializer.Deserialize<T>(wire.Content);
            }
        }
        T Pushed<T>(string name) => MessagePackSerializer.Deserialize<T>(pushes.Single(push => push.Name == name).Content);
        Player Reload() => BsonSerializer.Deserialize<Player>(player.ToBson());

        // ---------------- Dorm ----------------
        // The retail NotifySgDormData capture is an oracle for table-driven generation, never a runtime source.
        session.player.BigWorldState.SgDorm = null;
        byte[] oracle = Convert.FromBase64String(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BigWorld", "big_world_sg_dorm_data.msgpack.b64")).Trim());
        AssertEqual(Convert.ToHexString(oracle), Convert.ToHexString(MessagePackSerializer.Serialize(Sg("SkyGardenDormModule", "State", player))), "fresh dorm reproduces retail 388-byte capture");
        AssertEqual(388, oracle.Length, "dorm oracle length");

        int Layout(object request) => Call<SgDormSaveAndApplyLayoutResponse>("SgDormSaveAndApplyLayoutRequest", request).Code;
        SgDormPlacedFurniture F(int id, int cfgId, int photoId = 0) => new() { Id = id, CfgId = cfgId, PhotoId = photoId, Scale = 1000, X = 120, Y = -40 };
        SgDormSaveAndApplyLayoutRequest Wall(int save, int apply, params SgDormPlacedFurniture[] placed) => new()
        {
            AreaType = 1, SaveLayoutId = save, ApplyLayoutId = apply,
            SaveFurnitureInfos = [new SgDormFurnitureInfo { Container = F(1, 26100001), PlacementFurniture = placed.ToList() }]
        };
        Sg("SkyGardenDormModule", "ApplySliceRewards", session, new List<RewardGoods> {new RewardGoods { TemplateId = 26110001, Count = 2 }, new RewardGoods { TemplateId = 50005, Count = 9 }, new RewardGoods { TemplateId = 26210001, Count = 1 } });
        Packet.Push furniturePush = MessagePackSerializer.Deserialize<Packet.Push>(harness.ReadPacket("NotifySgDormFurnitureAdd").Content);
        AssertEqual("NotifySgDormFurnitureAdd", furniturePush.Name, "furniture push name");
        AssertEqual("4:26110001,5:26210001", string.Join(",", MessagePackSerializer.Deserialize<NotifySgDormFurnitureAdd>(furniturePush.Content).AddFurnitureList.Select(f => $"{f.Id}:{f.CfgId}")),
            "furniture reward adds max+1 ids, capped by MaxCount, ignores non-dorm goods");

        AssertEqual(20248004, Layout(new SgDormSaveAndApplyLayoutRequest { AreaType = 9, SaveLayoutId = 1 }), "unknown area");
        AssertEqual(20248016, Layout(new SgDormSaveAndApplyLayoutRequest { AreaType = 1 }), "save and apply both zero");
        AssertEqual(20248017, Layout(Wall(2, 2)), "save == apply rejected");
        AssertEqual(20248007, Layout(Wall(4, 0)), "layout of other area");
        AssertEqual(20248002, Layout(Wall(99, 0)), "unknown layout");
        AssertEqual(20248005, Layout(Wall(2, 0, F(77, 26110001))), "unowned furniture");
        AssertEqual(20248005, Layout(Wall(2, 0, F(4, 26110002))), "owned id with forged CfgId");
        AssertEqual(20248014, Layout(Wall(2, 0, F(4, 26110001), F(4, 26110001))), "same furniture placed twice");
        AssertEqual(20248013, Layout(Wall(2, 0, F(2, 26200001))), "container placed as furniture");
        AssertEqual(20248007, Layout(Wall(2, 0, F(5, 26210001))), "shelf ornament on wall");
        AssertEqual(20248020, Layout(Wall(2, 0, F(0, 0))), "Id and PhotoId zero");
        AssertEqual(20248021, Layout(Wall(2, 0, F(0, 0, 7), F(0, 0, 7))), "album photo twice");
        AssertEqual(20248012, Layout(new SgDormSaveAndApplyLayoutRequest
        {
            AreaType = 2, SaveLayoutId = 5,
            SaveFurnitureInfos = [new SgDormFurnitureInfo { Container = F(5, 26210001) }]
        }), "non-container as container");
        AssertEqual(0, Layout(Wall(2, 0, F(4, 26110001), F(3, 26120005), F(0, 0, 7))), "valid wall save-only");
        AssertEqual(0, Layout(new SgDormSaveAndApplyLayoutRequest { AreaType = 1, ApplyLayoutId = 2 }), "apply-only");
        AssertEqual(0, Layout(new SgDormSaveAndApplyLayoutRequest
        {
            AreaType = 2, SaveLayoutId = 5, ApplyLayoutId = 6,
            SaveFurnitureInfos = [new SgDormFurnitureInfo { Container = F(2, 26200001), PlacementFurniture = [F(5, 26210001)] }]
        }), "valid shelf save+apply");
        AssertEqual(20248011, Call<SgDormSetFashionResponse>("SgDormSetFashionRequest", new SgDormSetFashionRequest { FashionId = 27000002 }).Code, "locked fashion");
        AssertEqual(20248003, Call<SgDormSetFashionResponse>("SgDormSetFashionRequest", new SgDormSetFashionRequest { FashionId = 27000009 }).Code, "unknown fashion");
        AssertEqual(20248015, Call<SgDormSetFashionResponse>("SgDormSetFashionRequest", new SgDormSetFashionRequest { FashionId = 0 }).Code, "invalid fashion id");
        AssertEqual(true, AscNet.GameServer.Handlers.BigWorld.BigWorldRewardService.IsSliceOwned(27000003), "fashion goods are slice owned");
        Sg("SkyGardenDormModule", "ApplySliceRewards", session, new List<RewardGoods> { new RewardGoods { TemplateId = 27000003, Count = 1 }, new RewardGoods { TemplateId = 27000001, Count = 1 } });
        Packet.Push fashionPush = MessagePackSerializer.Deserialize<Packet.Push>(harness.ReadPacket("NotifySgDormFashionAdd").Content);
        AssertEqual(("NotifySgDormFashionAdd", 27000003), (fashionPush.Name, MessagePackSerializer.Deserialize<NotifySgDormFashionAdd>(fashionPush.Content).AddDormFashion.Id), "fashion add push");
        Sg("SkyGardenDormModule", "ApplySliceRewards", session, new List<RewardGoods> { new RewardGoods { TemplateId = 27000003, Count = 1 } });
        AssertEqual(1, player.BigWorldState.SgDorm!.DormFashionList.Count(f => f.Id == 27000003), "granted fashion owned once (default and duplicate dropped)");
        AssertEqual(0, Call<SgDormSetFashionResponse>("SgDormSetFashionRequest", new SgDormSetFashionRequest { FashionId = 27000003 }).Code, "owned fashion");
        NotifySgDormData dorm = Reload().BigWorldState.SgDorm!;
        AssertEqual(27000003, dorm.CurFashionId, "fashion persisted");
        AssertEqual("1:2,2:6", string.Join(",", dorm.CurAreaLayout.Select(kv => $"{kv.Key}:{kv.Value}")), "applied layouts persisted");
        AssertEqual("4,3,0", string.Join(",", dorm.LayoutList.Single(l => l.LayoutId == 2).FurnitureInfos[0].PlacementFurniture.Select(f => f.Id)), "saved wall layout persisted");
        AssertEqual(7, dorm.LayoutList.Single(l => l.LayoutId == 2).FurnitureInfos[0].PlacementFurniture[2].PhotoId, "album photo persisted");
        AssertEqual(2, dorm.LayoutList.Count(l => l.AreaType == 1), "default + saved wall layouts");

        // ---------------- Cafe ----------------
        Dictionary<int, SGCafeStageTable> cafeStages = TableReaderV2.Parse<SGCafeStageTable>().ToDictionary(row => row.Id);
        SGCafeStageTable s1 = cafeStages[1001];
        XBigWorldCafeDb cafeLoginDb = (XBigWorldCafeDb)Sg("SkyGardenCafeModule", "State", player)!;
        int presetCard = TableReaderV2.Parse<SGCafeCustomerPresetTable>().First().CustomerIds[0];
        AssertEqual(TableReaderV2.Parse<SGCafeCustomerTable>().Single(row => row.Id == presetCard).DefaultNum, cafeLoginDb.CardDict[presetCard], "initial CardDict from preset/DefaultNum");
        CafeGambling Run(int stage, int round, int sales) => new() { StageId = stage, Round = round, SumSales = sales, ActPoint = 3, HandCards = [presetCard] };
        int NewRound(CafeGambling run, int group = 0, List<int>? cards = null) =>
            Call<BigWorldCafeNewRoundResponse>("BigWorldCafeNewRoundRequest", new BigWorldCafeNewRoundRequest { CafeGambling = run, CardGroupId = group, CardList = cards ?? [] }).Code;
        int NextRound(CafeGambling run) => Call<BigWorldCafeNextRoundResponse>("BigWorldCafeNextRoundRequest", new BigWorldCafeNextRoundRequest { CafeGambling = run }).Code;

        AssertEqual(25200043, NewRound(Run(1001, 1, 0)), "stage condition unmet");
        FinishObjective(20030112);
        AssertEqual(25200040, NewRound(Run(9999, 1, 0)), "unknown stage");
        AssertEqual(25200043, NewRound(Run(1002, 1, 0)), "pre-stage not passed");
        AssertEqual(25200046, NewRound(Run(1001, 2, 0)), "new round must start at round 1");
        AssertEqual(25200052, NewRound(Run(1001, 1, 0), 1, [presetCard]), "story run with deck");
        AssertEqual(25200050, NewRound(new CafeGambling { StageId = 1001, Round = 1, HandCards = [42] }), "unknown card in snapshot");
        AssertEqual(25200048, NextRound(Run(1001, 2, 0)), "next round without run");
        AssertEqual(0, NewRound(Run(1001, 1, 0)), "story new round");
        AssertEqual(25200047, NextRound(Run(1001, 3, 10)), "round skip rejected");
        AssertEqual(25200047, NextRound(Run(1001, 1, 10)), "round replay rejected");
        AssertEqual(25200054, NextRound(Run(1002, 2, 10)), "stage switch rejected");
        AssertEqual(25200061, NextRound(new CafeGambling { StageId = 1001, Round = 2, ActPoint = 7 }), "seat cap");
        AssertEqual(0, NextRound(Run(1001, 2, 20)), "round 2");
        AssertEqual(true, BigWorldConditionService_Check(player, 50010008), "10203002 stage 1001 round 2");
        AssertEqual(false, BigWorldConditionService_Check(player, 50010009), "10203002 not round 3");
        AssertEqual(0, NextRound(Run(1001, 3, 30)), "round 3");
        AssertEqual(false, pushes.Any(p => p.Name == "NotifyBigWorldCafeSettle"), "no settle before last round");
        long coin50005 = Count(50005), coin70001 = Count(70001);
        // SumSales 75: Target 40,70,100 -> 2 stars -> Reward[1..2].
        AssertEqual(0, NextRound(Run(1001, 4, s1.Target[1] + 5)), "final round");
        NotifyBigWorldCafeSettle settle = Pushed<NotifyBigWorldCafeSettle>("NotifyBigWorldCafeSettle");
        AssertEqual(2, settle.Star, "2 stars at SumSales 75");
        AssertEqual($"{s1.Reward[0]},{s1.Reward[1]}", string.Join(",", settle.AwardList), "first-time awards for stars 1-2");
        AssertEqual(coin50005 + 60, Count(50005), "star-1 reward granted");
        AssertEqual(coin70001 + 120, Count(70001), "star-2 reward granted");
        AssertEqual(null, player.BigWorldState.SgCafe!.CafeGambling, "run cleared on settle");
        AssertEqual(true, BigWorldConditionService_Check(player, 50000101), "10203001 stage 1001 star>=1");

        // Replay with SumSales 30: 0 stars, no awards, best kept.
        AssertEqual(0, NewRound(Run(1001, 1, 0)), "replay start");
        foreach (int round in new[] { 2, 3 }) NextRound(Run(1001, round, 0));
        NextRound(Run(1001, 4, 30));
        settle = Pushed<NotifyBigWorldCafeSettle>("NotifyBigWorldCafeSettle");
        AssertEqual("0:", $"{settle.Star}:{string.Join(",", settle.AwardList)}", "0 stars at SumSales 30, nothing granted");
        // SumSales 100: 3 stars, only Reward[3] is new.
        NewRound(Run(1001, 1, 0));
        foreach (int round in new[] { 2, 3 }) NextRound(Run(1001, round, 0));
        long gold = Count(1);
        NextRound(Run(1001, 4, 100));
        settle = Pushed<NotifyBigWorldCafeSettle>("NotifyBigWorldCafeSettle");
        AssertEqual($"3:{s1.Reward[2]}", $"{settle.Star}:{string.Join(",", settle.AwardList)}", "3 stars grant only the new star");
        AssertEqual(gold + 60000, Count(1), "star-3 reward granted once");

        // Deck save and kick-out.
        int SaveDeck(int group, List<int> cards) => Call<BigWorldCafeCardGroupListSaveResponse>("BigWorldCafeCardGroupListSaveRequest",
            new BigWorldCafeCardGroupListSaveRequest { GroupId = group, CardList = cards }).Code;
        AssertEqual(25200038, SaveDeck(4, [presetCard]), "group id out of range");
        AssertEqual(25200039, SaveDeck(1, Enumerable.Repeat(presetCard, 26).ToList()), "deck over GroupMaxCardNum");
        AssertEqual(25200050, SaveDeck(1, [42]), "unknown card");
        AssertEqual(25200045, SaveDeck(1, Enumerable.Repeat(presetCard, cafeLoginDb.CardDict[presetCard] + 1).ToList()), "more copies than owned");
        int unowned = TableReaderV2.Parse<SGCafeCustomerTable>().First(row => !cafeLoginDb.CardDict.ContainsKey(row.Id)).Id;
        AssertEqual(25200057, SaveDeck(1, [unowned]), "unowned card");
        AssertEqual(0, SaveDeck(2, [presetCard]), "valid deck");
        int Kick(int stage) => Call<BigWorldCafeGuideKickOutSceneResponse>("BigWorldCafeGuideKickOutSceneRequest", new BigWorldCafeGuideKickOutSceneRequest { StageId = stage }).Code;
        AssertEqual(25200060, Kick(1001), "stage without kick-out");
        AssertEqual(25200058, Kick(1002), "kick-out before pass");
        AssertEqual(0, NewRound(Run(1002, 1, 0)), "stage 1002 unlocked by 1001 star");
        AssertEqual(0, Call<BigWorldCafeGiveUpResponse>("BigWorldCafeGiveUpRequest", null).Code, "give up");
        AssertEqual(0, Call<BigWorldCafeGiveUpResponse>("BigWorldCafeGiveUpRequest", null).Code, "give up idempotent");
        AssertEqual(null, player.BigWorldState.SgCafe!.CafeGambling, "give up clears run");
        player.BigWorldState.SgCafe.CafeStageList[1002] = new CafeStageRecord { StageId = 1002, GetMaxStarReward = 1 };
        AssertEqual(0, Kick(1002), "kick-out");
        AssertEqual(25200059, Kick(1002), "kick-out twice");
        XBigWorldCafeDb cafe = Reload().BigWorldState.SgCafe!;
        AssertEqual("100:3", $"{cafe.CafeStageList[1001].MaxSales}:{cafe.CafeStageList[1001].GetMaxStarReward}", "cafe record persisted");
        AssertEqual(true, cafe.CafeStageList[1002].IsKickOutCafe, "kick-out persisted");
        AssertEqual($"{presetCard}", string.Join(",", cafe.CardGroupList[2]), "deck persisted");
        // Client typo {BUffId} must be read and re-emitted as BuffId.
        CafeBuff buff = MessagePackSerializer.Deserialize<CafeBuff>(MessagePackSerializer.Serialize(new Dictionary<string, int> { ["BUffId"] = 5, ["CardId"] = 1001 }));
        AssertEqual("5:1001", $"{buff.BuffId}:{buff.CardId}", "BUffId typo accepted");
        AssertEqual("{\"BuffId\":5,\"CardId\":1001}", MessagePackSerializer.SerializeToJson(buff), "BuffId emitted");
        // Empty Lua tables may arrive as arrays; dictionaries must still deserialize.
        CafeGambling luaRun = MessagePackSerializer.Deserialize<CafeGambling>(MessagePackSerializer.Serialize(new Dictionary<string, object>
            { ["StageId"] = 1001, ["UseCardTimes"] = Array.Empty<int>(), ["HandCards"] = new Dictionary<int, int>() }));
        AssertEqual("1001:0:0", $"{luaRun.StageId}:{luaRun.UseCardTimes.Count}:{luaRun.HandCards.Count}", "Lua empty-table shapes accepted");

        // ---------------- Drone ----------------
        Dictionary<int, SgDroneGameStageTable> droneStages = TableReaderV2.Parse<SgDroneGameStageTable>().ToDictionary(row => row.Id);
        SgDroneGameStageTable d1 = droneStages[101];
        SgDroneGameStageStartResponse Start(int stage) => Call<SgDroneGameStageStartResponse>("SgDroneGameStageStartRequest", new SgDroneGameStageStartRequest { StageId = stage });
        SgDroneGameStageSettleResponse Settle(int stage, int time, int score, Dictionary<int, int> progress) =>
            Call<SgDroneGameStageSettleResponse>("SgDroneGameStageSettleRequest", new SgDroneGameStageSettleRequest { StageId = stage, CostTime = time, Score = score, TargetProgress = progress });
        AssertEqual(20312003, Start(999).Code, "unknown drone stage");
        AssertEqual(20312001, Start(101).Code, "chapter condition unmet");
        FinishObjective(20120107);
        AssertEqual(20312005, Start(102).Code, "pre-stage not passed");
        SgDroneGameStageStartResponse started = Start(101);
        AssertEqual(0, started.Code, "start");
        AssertEqual(20312004, Start(103).Code, "other stage archived");
        AssertEqual(20312008, Call<SgDroneGameStageSuspendResponse>("SgDroneGameStageSuspendRequest", new SgDroneGameStageSuspendRequest { StageId = 102, StageSuspendSaveData = new Dictionary<string, int> { ["Score"] = 3 } }).Code, "suspend wrong stage");
        AssertEqual(0, Call<SgDroneGameStageSuspendResponse>("SgDroneGameStageSuspendRequest", new SgDroneGameStageSuspendRequest { StageId = 101, StageSuspendSaveData = new Dictionary<string, int> { ["Score"] = 3, ["DroneId"] = 2 } }).Code, "suspend");
        player = Reload();
        session.player = player;
        SgDroneGameStageStartResponse resumed = Start(101);
        AssertEqual(started.CurStageData!.Seed, resumed.CurStageData!.Seed, "resume keeps seed after reload");
        AssertEqual("{\"Score\":3,\"DroneId\":2}", MessagePackSerializer.SerializeToJson(resumed.CurStageData.SaveData), "resume echoes suspend save");
        int reach = d1.StarTargets[0], collect280 = d1.StarTargets[1], collect500 = d1.StarTargets[2];
        AssertEqual(20312009, Settle(101, 50, 100, new() { [collect280] = 300 }).Code, "win without reaching station");
        AssertEqual(20312009, Settle(101, 50, 100, new() { [reach] = 1, [9999] = 1 }).Code, "foreign target");
        long drone = Count(990014);
        SgDroneGameStageSettleResponse settled = Settle(101, 50, 300, new() { [reach] = 1, [collect280] = 300, [collect500] = 300 });
        AssertEqual(0, settled.Code, "settle 2 targets");
        AssertEqual($"{reach},{collect280}", string.Join(",", settled.StageInfo[101].FinishedStarTargets), "2 targets finished");
        AssertEqual(false, settled.StageInfo[101].IsFinished, "not all targets");
        AssertEqual(drone + 150 + 100, Count(990014), "first-clear rewards for targets 1-2");
        AssertEqual(20312007, Settle(101, 40, 100, new() { [reach] = 1 }).Code, "settle without run");
        AssertEqual(true, BigWorldConditionService_Check(player, 50002031) == false, "10204001 other stage target not set");
        Start(101);
        drone = Count(990014);
        settled = Settle(101, 40, 600, new() { [reach] = 1, [collect280] = 600, [collect500] = 600 });
        AssertEqual(true, settled.StageInfo[101].IsFinished, "all targets finish the stage");
        AssertEqual("40:600", $"{settled.StageInfo[101].MinCostTime}:{settled.StageInfo[101].MaxScore}", "bests updated");
        long third = TableReaderV2.Parse<AscNet.Table.V2.share.bigworld.common.reward.BigWorldRewardGoodsTable>()
            .Single(row => row.Id == TableReaderV2.Parse<AscNet.Table.V2.share.bigworld.common.reward.BigWorldRewardTable>().Single(r => r.Id == d1.StarRewardIds[2]).SubIds[0]).Count;
        AssertEqual(drone + third, Count(990014), "only the new target is rewarded (first-clear once)");
        Start(101);
        drone = Count(990014);
        settled = Settle(101, 70, 10, new() { [reach] = 1, [collect280] = 600, [collect500] = 600 });
        AssertEqual(drone, Count(990014), "replay grants nothing");
        AssertEqual("40:600", $"{settled.StageInfo[101].MinCostTime}:{settled.StageInfo[101].MaxScore}", "worse run keeps bests");
        AssertEqual(0, Call<SgDroneGameStageGiveUpResponse>("SgDroneGameStageGiveUpRequest", null).Code, "give up idempotent");
        AssertEqual(0, Start(102).Code, "next stage unlocked");
        AssertEqual(0, Call<SgDroneGameStageGiveUpResponse>("SgDroneGameStageGiveUpRequest", null).Code, "give up");
        player = Reload();
        AssertEqual(null, player.BigWorldState.SgDroneCurrent, "give up persisted");
        AssertEqual(true, player.BigWorldState.SgDroneStages[101].IsFinished, "drone record persisted");
        session.player = player;
        Sg("SkyGardenDroneModule", "SendDroneData", session);
        Sg("SkyGardenCafeModule", "SendCafeData", session);
        Sg("SkyGardenDormModule", "SendDormData", session);
        pushes.Clear();
        for (int i = 0; i < 3; i++)
        {
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(harness.ReadPacket("enter pushes").Content);
            pushes.Add((push.Name, push.Content));
        }
        AssertEqual(true, Pushed<NotifySgDroneGameData>("NotifySgDroneGameData").GameData.StageInfo[101].IsFinished, "drone enter push");
        AssertEqual(3, Pushed<NotifyBigWorldCafeData>("NotifyBigWorldCafeData").Data.CafeStageList[1001].GetMaxStarReward, "cafe enter push");
        AssertEqual(27000003, Pushed<NotifySgDormData>("NotifySgDormData").CurFashionId, "dorm enter push");
        AssertEqual(true, playerSaves.ReplaceOneCalls > 0, "player saved");
        Console.WriteLine("BigWorld SkyGarden compatibility passed.");
        ValidateBigWorldDormitory();
    }

    // XGameplayDormitory: the client creates the lounge walls itself; the server only supplies the gameplay actor and the level's
    // interaction anchors (ConfigGroup_5001 spots 1-8).
    private static void ValidateBigWorldDormitory()
    {
        const long playerId = 99_948;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, []), "big-world-dormitory");
        BigWorldCoreClient client = new(harness);
        AssertEqual(0, client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest { WorldId = 400 }).Code, "enter world");

        // Every referenced lounge spot exists.
        var spotIds = TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.level.sceneconfig.LevelSpotTable>()
            .Where(row => row is { Sector: "skygarden_sushe", ConfigGroupId: 5001 }).Select(row => row.SpotId).Order().ToList();
        AssertEqual("1,2,3,4,5,6,7,8", string.Join(",", spotIds), "lounge spots");

        static object?[][] Replicates(EnterInstLevelResponse r) =>
            ((object?[])((object?[])MessagePackSerializer.Deserialize<object?[]>(r.EnterResultData!.LevelData!)[2]!)[0]!).Cast<object?[]>().ToArray();
        const int gameplay = (4 << 4) | 15; // XGameplayDormitory: manager sequence 4 of the server controller

        // ---- Input A: fresh account, default layouts (container 26100001 -> photo wall 64, 26200001 -> frame wall 63).
        EnterInstLevelResponse lounge = client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = 4003 });
        AssertEqual(0, lounge.Code, "enter lounge");
        object?[][] replicates = Replicates(lounge);
        AssertEqual(gameplay, (int)BwInt(replicates.Single(r => (string)r[0]! == "XGameplayDormitory")[5]), "gameplay actor uuid");
        // The 4.8 client builds the photo wall / frame wall / goods itself (local-only scene objects) from NotifySgDormData: the
        // lounge snapshot carries only the level's own scene objects, none parented to the gameplay actor.
        AssertEqual(0, replicates.Count(r => (string)r[0]! == "XSceneObject" && BwInt(r[6]) != 0), "no server-built dormitory objects");
        AssertEqual(true, replicates.Where(r => (string)r[0]! == "XSceneObject").Select(r => BwInt(r[5])).Distinct().Count() == replicates.Count(r => (string)r[0]! == "XSceneObject"), "unique uuids");

        // ---- Quest 2002 "View Photo Wall" (2002054, TraceActor scene object PlaceId 1): the lounge's interaction anchor exists.
        var anchor = replicates.Single(r => (string)r[0]! == "XSceneObject" && BwInt(MessagePackSerializer.Deserialize<object?[]>((byte[])r[1]!)[1]) == 1);
        AssertEqual((1L, 0L), (BwInt(MessagePackSerializer.Deserialize<object?[]>((byte[])anchor[1]!)[0]), BwInt(anchor[6])), "anchor 1 is a level scene object (base 1)");
        Dictionary<int, int> questByObjective = TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.quest.DlcQuestObjectiveTable>().ToDictionary(row => row.Id, row => row.QuestId);
        var objectives = TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.quest.DlcQuestObjectiveTable>().Where(row => row.QuestId == 2002)
            .OrderBy(row => row.StepId).ThenBy(row => Convert.ToInt32(row.Order)).ToList();
        // The client-hosted path reports finished objectives per step (a step opens once the previous one completed).
        foreach (IGrouping<int, AscNet.Table.V2.share.statussyncfight.quest.DlcQuestObjectiveTable> step in objectives.TakeWhile(row => row.Id != 2002054).GroupBy(row => row.StepId))
            AssertEqual(0, client.Call<DlcQuestUpdateResponse>("DlcQuestUpdateRequest", new DlcQuestUpdateRequest
                { QuestStepObjectiveList = step.Select(row => new Theatre5DlcQuestStepObjective { Id = row.Id, ObjectiveState = 6 }).ToList() }).Code, $"client reports 2002 step {step.Key}");
        var progress = player.BigWorldState.QuestData.ActiveQuests[2002].DynamicData!.Steps[200202].Objectives[2002054];
        AssertEqual(3, progress.ObjectiveState, "2002054 in progress");
        AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", new object[] { "RpcPlayerEnterLevelComplete", MessagePackSerializer.Serialize(Array.Empty<object>()), (byte)15, (byte)1, 4003 }).Code, "enter level complete");
        object?[] nav = client.XRpcPushes("XRpcCommon").Select(env => new object?[] { env[0], DecodeRpcArgs((byte[])env[1]!) }).Single(env => (string)env[0]! == "RpcAddQuestNavPointForLevelSceneObject");
        AssertEqual((4003L, 1L), (BwInt(((object?[])nav[1]!)[1]), BwInt(((object?[])nav[1]!)[^1])), "2002054 marker targets lounge scene object 1");
        int playerNpcUuid = (int)BwInt(replicates.First(r => (string)r[0]! == "XNpc" && BwInt(r[9]) == playerId)[5]);
        AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", new object[] { "RpcPlayerInteractRequest",
            MessagePackSerializer.Serialize(new object[] { (int)playerId, 1, playerNpcUuid, (int)BwInt(anchor[5]), 1, 2, 4003, 1 }), (byte)15, (byte)1, 4003 }).Code, "interact photo wall anchor");
        AssertEqual(6, progress.ObjectiveState, "2002054 completed by the interaction");
        AssertEqual(true, player.BigWorldState.QuestData.ActiveQuests[2002].DynamicData!.FinishedObjectiveIds.Contains(2002054), "2002054 persisted as finished");

        Console.WriteLine("BigWorld dormitory gameplay passed.");
    }

    private static object? Sg(string module, string method, params object[] args) =>
        RequiredAscNetGameServerType($"AscNet.GameServer.Handlers.BigWorld.{module}")
            .GetMethod(method, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, args);

    private static bool BigWorldConditionService_Check(Player player, int conditionId) =>
        (bool)RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BigWorld.BigWorldConditionService")
            .GetMethod("Check", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [player, conditionId])!;
}
