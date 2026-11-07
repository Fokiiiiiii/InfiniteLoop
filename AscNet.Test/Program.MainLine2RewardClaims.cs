using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben.mainline2;
using AscNet.Table.V2.share.reward;
using MongoDB.Bson;
using AscNet.Table.V2.share.scoretitle;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;
internal partial class Program
{
    private static int[] ClaimChapterStages(IEnumerable<int> chapters)
    {
        HashSet<int> ids = chapters.ToHashSet();
        HashSet<int> groups = TableReaderV2.Parse<MainLine2ChapterTable>().Where(x => ids.Contains(x.ChapterId)).SelectMany(x => x.StageGroupIds).ToHashSet();
        return TableReaderV2.Parse<MainLine2StageGroupTable>().Where(x => groups.Contains(x.Id)).SelectMany(x => x.StageIds).Where(x => x > 0).Distinct().ToArray();
    }
    private static void ValidateMainLine2RewardClaimsCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        var treasures = TableReaderV2.Parse<MainLine2TreasureTable>().ToDictionary(x => x.Id);
        var chapters = TableReaderV2.Parse<MainLine2ChapterTable>();
        var rewardRows = TableReaderV2.Parse<RewardGoodsTable>();
        var selected = chapters.Where(x => treasures.ContainsKey(x.TreasureId ?? 0))
            .Where(x => ClaimChapterStages([x.ChapterId]).Length >= treasures[x.TreasureId!.Value].StageCounts[0]).Take(2).ToArray();
        AssertEqual(2, selected.Length, "two distinct MainLine2 chapter treasure fixtures");
        long uid = 48_800;
        foreach (var chapter in selected)
        {
            uid++;
            var treasure = treasures[chapter.TreasureId!.Value];
            int required = treasure.StageCounts[0]; int[] stages = ClaimChapterStages([chapter.ChapterId]);
            Player player = CreateDrawCompatibilityPlayer(uid);
            using (LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, CreateDrawCompatibilityInventory(uid, []), "mainline2-chapter-claim"))
            {
                harness.Session.stage = PassedStages(uid, stages.Take(required - 1));
                var request = new MainLine2ReceiveTreasureRequest { ChapterId = chapter.ChapterId };
                InvokeRegisteredRequestHandler(nameof(MainLine2ReceiveTreasureRequest), harness.Session, 48_801, request);
                AssertEqual(20003009, ReadResponsePayload<MainLine2ReceiveTreasureResponse>(harness, 48_801, nameof(MainLine2ReceiveTreasureResponse), "MainLine2 insufficient chapter progress").Code, "MainLine2 chapter threshold boundary");
                harness.Session.stage = PassedStages(uid, stages.Take(required));
                InvokeRegisteredRequestHandler(nameof(MainLine2ReceiveTreasureRequest), harness.Session, 48_802, request);
                var response = ReadResponsePayload<MainLine2ReceiveTreasureResponse>(harness, 48_802, nameof(MainLine2ReceiveTreasureResponse), "MainLine2 chapter claim", maxPacketsToRead: 24);
                AssertEqual(0, response.Code, "MainLine2 chapter success");
                AssertIntegerList([0], response.RewardIdxs.Select(x => (long)x).ToArray(), "MainLine2 chapter zero-based claim index");
                var expected = ResolveRewardGoods(treasure.HighlightRewardIds[0], rewardRows, "chapter highlight").Concat(ResolveRewardGoods(treasure.RewardIds[0], rewardRows, "chapter normal")).ToArray();
                AssertEqual(string.Join(";", expected.Select(x => $"{x.TemplateId}:{x.Count}").Order()), string.Join(";", response.RewardGoodsList.Select(x => $"{x.TemplateId}:{x.Count}").Order()), "MainLine2 chapter authored reward goods");
            }
            Player reloaded = BsonSerializer.Deserialize<Player>(player.ToBson());
            using LoopbackSessionHarness retry = new(CreateDrawCompatibilityCharacter(uid), reloaded, CreateDrawCompatibilityInventory(uid, []), "mainline2-chapter-reload");
            retry.Session.stage = PassedStages(uid, stages.Take(required));
            InvokeRegisteredRequestHandler(nameof(MainLine2ReceiveTreasureRequest), retry.Session, 48_803, new MainLine2ReceiveTreasureRequest { ChapterId = chapter.ChapterId });
            AssertEqual(20003010, ReadResponsePayload<MainLine2ReceiveTreasureResponse>(retry, 48_803, nameof(MainLine2ReceiveTreasureResponse), "MainLine2 chapter duplicate reload").Code, "MainLine2 durable claim");
        }
        var achievements = TableReaderV2.Parse<MainLine2AchievementTable>().ToDictionary(x => x.Id);
        var stageRows = TableReaderV2.Parse<MainLine2StageTable>();
        foreach (var main in TableReaderV2.Parse<MainLine2MainTable>().Where(x => achievements.ContainsKey(x.AchievementId)).Take(2))
        {
            uid++; Player player = CreateDrawCompatibilityPlayer(uid);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, CreateDrawCompatibilityInventory(uid, []), "mainline2-achievement");
            int[] ids = ClaimChapterStages(main.ChapterIds);
            harness.Session.stage = PassedStages(uid, ids);
            var request = new MainLine2ReceiveAchievementRequest { ChapterId = main.Id };
            InvokeRegisteredRequestHandler(nameof(MainLine2ReceiveAchievementRequest), harness.Session, 48_811, request);
            AssertEqual(20219008, ReadResponsePayload<MainLine2ReceiveAchievementResponse>(harness, 48_811, nameof(MainLine2ReceiveAchievementResponse), "MainLine2 passed stages without achievements").Code, "clears alone do not grant achievements");
            foreach (var row in stageRows.Where(x => ids.Contains(x.Id)))
                for (int i = 0; i < row.AchievementTpyes.Count; i++)
                    if (row.AchievementTpyes[i] is 1 or 2) harness.Session.stage.Stages[row.Id].Achievement |= 1L << i;
            InvokeRegisteredRequestHandler(nameof(MainLine2ReceiveAchievementRequest), harness.Session, 48_812, request);
            var response = ReadResponsePayload<MainLine2ReceiveAchievementResponse>(harness, 48_812, nameof(MainLine2ReceiveAchievementResponse), "MainLine2 achievement success", maxPacketsToRead: 24);
            AssertEqual(0, response.Code, "MainLine2 achievement authored normal/special bits");
            AssertEqual(ResolveRewardGoods(achievements[main.AchievementId].ClearRewardId, rewardRows, "achievement reward").Count, response.RewardGoodsList.Count, "MainLine2 achievement table goods");
            foreach (var goods in response.RewardGoodsList.Where(x => x.RewardType == (int)RewardType.Collection))
            {
                var config = TableReaderV2.Parse<ScoreTitleTable>().Single(x => x.Id == goods.TemplateId);
                var title = harness.Session.character.ScoreTitles.Single(x => x.Id == goods.TemplateId);
                AssertEqual(config.InitQuality, title.Quality, "MainLine2 collectible authored initial quality");
                AssertEqual(goods.Count, title.Score, "MainLine2 collectible granted score");
                Character saved = BsonSerializer.Deserialize<Character>(harness.Session.character.ToBson());
                AssertEqual(title.Quality, saved.ScoreTitles.Single(x => x.Id == goods.TemplateId).Quality, "MainLine2 collectible survives reload");
            }
            Player reloaded = BsonSerializer.Deserialize<Player>(player.ToBson());
            AssertEqual(true, reloaded.FubenMainLine2Data.MainDatas.Single(x => x.Id == main.Id).IsAchievementGet, "achievement claim survives reload");
            using LoopbackSessionHarness retry = new(CreateDrawCompatibilityCharacter(uid), reloaded, CreateDrawCompatibilityInventory(uid, []), "mainline2-achievement-reload");
            retry.Session.stage = harness.Session.stage;
            InvokeRegisteredRequestHandler(nameof(MainLine2ReceiveAchievementRequest), retry.Session, 48_813, request);
            AssertEqual(20219006, ReadResponsePayload<MainLine2ReceiveAchievementResponse>(retry, 48_813, nameof(MainLine2ReceiveAchievementResponse), "MainLine2 achievement duplicate").Code, "achievement retry rejected after reload");
        }
        foreach (var egg in TableReaderV2.Parse<MainLine2EggsTreasureTable>())
        {
            var chapter = chapters.Single(x => x.EggIds == egg.Id);
            uid++; Player player = CreateDrawCompatibilityPlayer(uid);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, CreateDrawCompatibilityInventory(uid, []), "mainline2-egg-condition");
            harness.Session.stage = PassedStages(uid, ClaimChapterStages([chapter.ChapterId]));
            byte[] before = player.ToBson();
            InvokeRegisteredRequestHandler(nameof(MainLine2ReceiveEggsTreasureRequest), harness.Session, 48_821, new MainLine2ReceiveEggsTreasureRequest { ChapterId = chapter.ChapterId, EggId = egg.Id });
            AssertEqual(20219013, ReadResponsePayload<MainLine2ReceiveEggsTreasureResponse>(harness, 48_821, nameof(MainLine2ReceiveEggsTreasureResponse), "MainLine2 egg behavior predicate").Code, "clearing stages cannot satisfy egg behavior event predicate");
            AssertEqual(Convert.ToHexString(before), Convert.ToHexString(player.ToBson()), "rejected egg does not mutate player");
        }
    }
}
