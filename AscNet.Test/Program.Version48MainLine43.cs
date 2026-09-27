using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.fuben.mainline2;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    // 4.8 MainLine2 "Anchored in Faith" (Main 1043) and LifeTree chapter 64 / Adelyde 1421003,
    // driven through registered handlers with synthetic (non-captured) player/stage fixtures.
    private static void ValidateVersion48MainLine43()
    {
        PacketFactory.LoadPacketHandlers();
        const int mainId = 1043;
        MainLine2MainTable main = TableReaderV2.Parse<MainLine2MainTable>().Single(row => row.Id == mainId);
        MainLine2TreasureTable treasure = TableReaderV2.Parse<MainLine2TreasureTable>().Single(row => row.Id == main.TreasureId);
        Dictionary<int, MainLine2ChapterTable> chapters = TableReaderV2.Parse<MainLine2ChapterTable>().ToDictionary(row => row.ChapterId);
        Dictionary<int, MainLine2StageGroupTable> groups = TableReaderV2.Parse<MainLine2StageGroupTable>().ToDictionary(row => row.Id);
        int[] stageIds = main.ChapterIds.Where(id => id > 0)
            .SelectMany(id => chapters[id].StageGroupIds.Where(group => group > 0))
            .SelectMany(group => groups[group].StageIds.Where(stage => stage > 0)).ToArray();
        HashSet<int> mainLine2StageIds = TableReaderV2.Parse<MainLine2StageTable>().Select(row => row.Id).ToHashSet();
        AssertEqual(true, stageIds.Length >= treasure.StageCounts.Max() && stageIds.All(mainLine2StageIds.Contains),
            "Main 1043 chapters resolve authored MainLine2 stages covering every treasure tier");

        int required = treasure.StageCounts[0];
        List<RewardGoodsTable> rewardGoods = TableReaderV2.Parse<RewardGoodsTable>();
        int expectedGoods = ResolveRewardGoods(treasure.HighlightRewardIds[0], rewardGoods, "1043 highlight").Count
            + ResolveRewardGoods(treasure.RewardIds[0], rewardGoods, "1043 reward").Count;
        const string requestName = nameof(MainLine2ReceiveMainTreasureRequest);
        MainLine2ReceiveMainTreasureRequest request = new() { MainId = mainId, TreasureIdx = 0 };
        const long playerId = 48_043;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        using (LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, []), "v48-mainline43-treasure"))
        {
            harness.Session.stage = PassedStages(playerId, stageIds.Take(required - 1));
            InvokeRegisteredRequestHandler(requestName, harness.Session, 48_431, request);
            AssertEqual(20003009, ReadResponsePayload<MainLine2ReceiveMainTreasureResponse>(harness, 48_431,
                nameof(MainLine2ReceiveMainTreasureResponse), "1043 below tier").Code, "1043 treasure below authored StageCounts rejected");

            harness.Session.stage = PassedStages(playerId, stageIds.Take(required));
            InvokeRegisteredRequestHandler(requestName, harness.Session, 48_432, request);
            MainLine2ReceiveMainTreasureResponse claimed = ReadResponsePayload<MainLine2ReceiveMainTreasureResponse>(harness, 48_432,
                nameof(MainLine2ReceiveMainTreasureResponse), "1043 at tier", maxPacketsToRead: 8);
            AssertEqual(0, claimed.Code, "1043 treasure at authored StageCounts claims");
            AssertEqual(expectedGoods, claimed.RewardGoodsList.Count, "1043 treasure grants authored highlight+normal reward goods");
        }

        Player reloaded = BsonSerializer.Deserialize<Player>(player.ToBson());
        AssertEqual(true, reloaded.FubenMainLine2Data.MainDatas.Single(data => data.Id == mainId).MainTreasureIdxs.SequenceEqual([0]),
            "1043 claimed treasure index survives BSON reload");
        using (LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), reloaded,
            CreateDrawCompatibilityInventory(playerId, []), "v48-mainline43-treasure-reload"))
        {
            harness.Session.stage = PassedStages(playerId, stageIds.Take(required));
            InvokeRegisteredRequestHandler(requestName, harness.Session, 48_433, request);
            AssertEqual(20003010, ReadResponsePayload<MainLine2ReceiveMainTreasureResponse>(harness, 48_433,
                nameof(MainLine2ReceiveMainTreasureResponse), "1043 reload duplicate").Code, "1043 reloaded claim is not re-granted");
        }

        // LifeTree: current popup chapter 64 navigable only through ExhibitionChapter 64; Adelyde unlock gated by 43-1 clear.
        const int adelyde = 1421003;
        Player lifePlayer = CreateDrawCompatibilityPlayer(playerId + 1);
        using (LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId + 1), lifePlayer,
            CreateDrawCompatibilityInventory(playerId + 1, []), "v48-lifetree-64"))
        {
            harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId + 1);
            InvokeRegisteredRequestHandler(nameof(LifeTreeFinishProcessRequest), harness.Session, 48_434,
                new LifeTreeFinishProcessRequest { Process = 3, ProcessId = 64 });
            AssertEqual(0, ReadResponsePayload<LifeTreeFinishProcessResponse>(harness, 48_434,
                nameof(LifeTreeFinishProcessResponse), "LifeTree chapter 64").Code, "LifeTree chapter 64 is navigable");

            InvokeRegisteredRequestHandler(nameof(LifeTreeUnlockCharacterRequest), harness.Session, 48_435,
                new LifeTreeUnlockCharacterRequest { CharacterId = adelyde, Status = 1 });
            AssertEqual(20326005, ReadResponsePayload<LifeTreeUnlockCharacterResponse>(harness, 48_435,
                nameof(LifeTreeUnlockCharacterResponse), "Adelyde locked").Code, "Adelyde LifeTree locked before 43-1");

            harness.Session.stage = PassedStages(playerId + 1, stageIds.Take(1));
            InvokeRegisteredRequestHandler(nameof(LifeTreeUnlockCharacterRequest), harness.Session, 48_436,
                new LifeTreeUnlockCharacterRequest { CharacterId = adelyde, Status = 1 });
            AssertEqual(0, ReadResponsePayload<LifeTreeUnlockCharacterResponse>(harness, 48_436,
                nameof(LifeTreeUnlockCharacterResponse), "Adelyde unlocked", maxPacketsToRead: 4).Code, "Adelyde LifeTree unlocks after 43-1");
        }
        Player lifeReloaded = BsonSerializer.Deserialize<Player>(lifePlayer.ToBson());
        AssertEqual(true, lifeReloaded.LifeTreeData.FinishedChapters.Contains(64), "LifeTree chapter 64 acknowledgement survives reload");
        AssertEqual(1, lifeReloaded.LifeTreeData.UnlockCharacterData[adelyde].UnlockStatus, "Adelyde unlock survives reload");

        static Stage PassedStages(long uid, IEnumerable<int> ids)
        {
            Stage stage = CreateLoginAccountCompatibilityStage(uid);
            foreach (int id in ids)
                stage.AddStage(new StageDatum { StageId = id, StarsMark = 7, Passed = true, PassTimesTotal = 1, CreateTime = 1_760_000_000, LastPassTime = 1_760_000_000 });
            return stage;
        }
    }
}
