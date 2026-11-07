using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.prequel;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateDailyStageRewardClaimsCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        const long uid = 99790;
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, []), "daily-stage-reward-boundary");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        int packetId = 997900;
        foreach (int section in new[] { -1, 1 })
        {
            string inventory = Convert.ToBase64String(harness.Session.inventory.ToBson());
            string stages = Convert.ToBase64String(harness.Session.stage.ToBson());
            InvokeRegisteredRequestHandler(nameof(ReceiveDailyRewardRequest), harness.Session, packetId, new ReceiveDailyRewardRequest { DailySectionId = section });
            var response = ReadResponsePayload<ReceiveDailyRewardResponse>(harness, packetId++, nameof(ReceiveDailyRewardResponse), "Daily missing reward authority");
            AssertEqual(20020003, response.Code, "Daily section reward source unavailable");
            AssertEqual(0, response.RewardGoodsList.Count, "Daily closed claim grants nothing");
            AssertEqual(inventory, Convert.ToBase64String(harness.Session.inventory.ToBson()), "Daily closed claim preserves inventory");
            AssertEqual(stages, Convert.ToBase64String(harness.Session.stage.ToBson()), "Daily closed claim preserves stage records");
            harness.Session.stage = BsonSerializer.Deserialize<Stage>(harness.Session.stage.ToBson());
        }
    }

    private static void ValidateStagePurchasesCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        const long uid = 99791;
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, [new AscNet.Common.MsgPack.Item { Id = Inventory.FreeGem, Count = 1000 }]), "stage-purchase-boundary");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        int packetId = 997910;
        int Buy(int stageId)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(BuyMainLineChallengeCountRequest), harness.Session, id, new BuyMainLineChallengeCountRequest { StageId = stageId });
            return ReadResponsePayload<BuyMainLineChallengeCountResponse>(harness, id, nameof(BuyMainLineChallengeCountResponse), "Mainline attempt purchase boundary").Code;
        }
        int Unlock(int coverId, int challengeId)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(UnlockPrequelChallengeRequest), harness.Session, id, new UnlockPrequelChallengeRequest { CoverId = coverId, ChallengeId = challengeId });
            return ReadResponsePayload<UnlockPrequelChallengeResponse>(harness, id, nameof(UnlockPrequelChallengeResponse), "Prequel unlock boundary").Code;
        }
        string inventory = Convert.ToBase64String(harness.Session.inventory.ToBson());
        string stages = Convert.ToBase64String(harness.Session.stage.ToBson());
        AssertEqual(20003161, Buy(-1), "Unknown purchase stage rejected");
        foreach (var stage in TableReaderV2.Parse<StageTable>().Where(row => row.Type == 1).Take(2))
        {
            AssertEqual(20003020, Buy(stage.StageId), "Unauthored attempt purchase disabled");
            AssertEqual(20003020, Buy(stage.StageId), "Repeated disabled purchase grants no attempts");
        }
        AssertEqual(20003095, Unlock(-1, 1), "Unknown prequel cover rejected");
        foreach (var cover in TableReaderV2.Parse<CoverTable>().Take(2))
        {
            AssertEqual(20003096, Unlock(cover.CoverId, 0), "Prequel challenge index is one-based");
            AssertEqual(20003096, Unlock(cover.CoverId, 2), "Prequel challenge upper bound (one authored ChallengeStage)");
            AssertEqual(20003097, Unlock(cover.CoverId, 1), "Missing authoritative unlock cost stays locked");
            harness.Session.stage = BsonSerializer.Deserialize<Stage>(harness.Session.stage.ToBson());
            AssertEqual(20003097, Unlock(cover.CoverId, 1), "Reloaded locked challenge cannot be unlocked for free");
        }
        AssertEqual(inventory, Convert.ToBase64String(harness.Session.inventory.ToBson()), "Closed purchases never deduct currency");
        AssertEqual(stages, Convert.ToBase64String(harness.Session.stage.ToBson()), "Closed purchases never mutate stage records");
    }
}
