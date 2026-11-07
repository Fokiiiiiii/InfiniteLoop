using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben.awareness;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateStageRewardClaimsCompatibility()
    {
        ValidateMainLine2RewardClaimsCompatibility();
        ValidateStageChapterRewardClaimsCompatibility();
        ValidateDailyStageRewardClaimsCompatibility();
        ValidateStagePurchasesCompatibility();
        ValidateAwarenessRewardClaimsCompatibility();
    }

    private static void ValidateAwarenessRewardClaimsCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        const long playerId = 48_912;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        int[] chapters = TableReaderV2.Parse<AwarenessChapterTable>().Take(2).Select(x => x.Id).ToArray();
        AssertEqual(2, chapters.Length, "Awareness distinct authored chapter fixtures");
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        using (LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, []), "awareness-reward-boundary"))
        {
            int packetId = 48_912;
            int missingChapter = TableReaderV2.Parse<AwarenessChapterTable>().Max(x => x.Id) + 1;
            InvokeRegisteredRequestHandler(nameof(AwarenessGetRewardRequest), harness.Session, ++packetId,
                new AwarenessGetRewardRequest { ChapterId = missingChapter });
            AssertEqual(20182001, ReadResponsePayload<AwarenessGetRewardResponse>(harness, packetId,
                nameof(AwarenessGetRewardResponse), "Awareness missing chapter").Code, "Awareness unknown chapter rejected");
            foreach (int id in chapters)
            {
                InvokeRegisteredRequestHandler(nameof(AwarenessGetRewardRequest), harness.Session, ++packetId,
                    new AwarenessGetRewardRequest { ChapterId = id });
                AssertEqual(20182012, ReadResponsePayload<AwarenessGetRewardResponse>(harness, packetId,
                    nameof(AwarenessGetRewardResponse), "Awareness insufficient progress").Code, "Awareness unfinished claim rejected");
                player.Awareness.Chapters.Add(new AwarenessChapterState { ChapterId = id });
                byte[] before = player.ToBson();
                for (int retry = 0; retry < 2; retry++)
                {
                    InvokeRegisteredRequestHandler(nameof(AwarenessGetRewardRequest), harness.Session, ++packetId,
                        new AwarenessGetRewardRequest { ChapterId = id });
                    AwarenessGetRewardResponse response = ReadResponsePayload<AwarenessGetRewardResponse>(harness, packetId,
                        nameof(AwarenessGetRewardResponse), "Awareness missing reward source");
                    AssertEqual(20020003, response.Code, "Awareness missing authoritative RewardId fails closed");
                    AssertEqual(0, response.RewardList.Count, "Awareness never invents goods");
                    AssertEqual(Convert.ToHexString(before), Convert.ToHexString(player.ToBson()), "Awareness rejected retry leaves persisted player unchanged");
                }
            }
        }
        Player reloaded = BsonSerializer.Deserialize<Player>(player.ToBson());
        using LoopbackSessionHarness relog = new(CreateDrawCompatibilityCharacter(playerId), reloaded,
            CreateDrawCompatibilityInventory(playerId, []), "awareness-reward-reload");
        InvokeRegisteredRequestHandler(nameof(AwarenessGetRewardRequest), relog.Session, 48_999,
            new AwarenessGetRewardRequest { ChapterId = chapters[0] });
        AssertEqual(20020003, ReadResponsePayload<AwarenessGetRewardResponse>(relog, 48_999,
            nameof(AwarenessGetRewardResponse), "Awareness reloaded missing source").Code, "Awareness reload cannot bypass missing authority");
    }
}
