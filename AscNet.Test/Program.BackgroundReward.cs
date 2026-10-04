using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.photomode;
using AscNet.Table.V2.share.reward;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static void ValidateBackgroundRewardGrants()
        {
            using MongoCollectionOverride noOpStages = MongoCollectionOverride.InstallNoOpStageCollection(); // login persists Stage rollover
            Type handler = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler");
            Type grantType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardGrant");
            MethodInfo apply = handler.GetMethod("ApplyRewardsOnceAndPersist", BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingMethodException(handler.FullName, "ApplyRewardsOnceAndPersist");
            MethodInfo getRewardType = handler.GetMethod("GetRewardType", BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingMethodException(handler.FullName, "GetRewardType");
            HashSet<int> paidScenes = TableReaderV2.Parse<BackgroundTable>()
                .Where(row => row.Id > 0 && row.SceneModelId > 0 && row.IsFree <= 0)
                .Select(row => row.Id).ToHashSet();
            List<RewardGoodsTable> goods = TableReaderV2.Parse<RewardGoodsTable>()
                .Where(row => getRewardType.Invoke(null, [row]) is RewardType.Background && paidScenes.Contains(row.TemplateId))
                .GroupBy(row => row.TemplateId).Select(group => group.OrderBy(row => row.Id).First())
                .OrderBy(row => row.TemplateId).Take(2).ToList();
            AssertEqual(2, goods.Count, "background reward: two distinct source-backed paid scene RewardGoods");
            int[] ids = goods.Select(row => row.TemplateId).ToArray();

            const long playerId = 99_601;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            player.OwnedBackgroundIds = [];
            Character character = CreateDrawCompatibilityCharacter(playerId);
            using LoopbackSessionHarness harness = new(character, player,
                CreateDrawCompatibilityInventory(playerId, []), "background-reward-grant");

            void AssertNoPacket(string name)
            {
                if (harness.TryReadAvailablePacket($"{name} unexpected packet", out Packet extra))
                    throw new InvalidDataException($"{name}: unexpected extra {extra.Type} packet.");
            }
            void Grant(string claim, bool expectFailure = false, RewardGoodsTable[]? rows = null)
            {
                Array grants = Array.CreateInstance(grantType, 1);
                grants.SetValue(Activator.CreateInstance(grantType, claim, rows ?? goods.ToArray(), null, null), 0);
                object? result;
                try { result = apply.Invoke(null, [grants, harness.Session]); }
                catch (TargetInvocationException) when (expectFailure) { return; }
                if (expectFailure)
                    throw new InvalidDataException($"background reward {claim}: forced Player save failure did not throw.");
                result!.GetType().GetMethod("SendPushes", BindingFlags.Instance | BindingFlags.Public)!
                    .Invoke(result, [harness.Session]);
            }
            static string Ids(IEnumerable<int> values) => string.Join(",", values.Order());
            int[] ReadAdds(string name) => ids.Select((_, i) => ReadPushPayload<NotifyAddBackground>(
                harness, nameof(NotifyAddBackground), $"{name} push {i}").BackgroundId).ToArray();

            // Unknown Background id is rejected before any receipt or Player save.
            RewardGoodsTable unknown = (RewardGoodsTable)typeof(object)
                .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(goods[0], null)!;
            int unknownId = TableReaderV2.Parse<BackgroundTable>().Max(row => row.Id) + 1;
            typeof(RewardGoodsTable).GetProperty(nameof(RewardGoodsTable.TemplateId))!.SetValue(unknown, unknownId);
            AssertEqual(true, getRewardType.Invoke(null, [unknown]) is RewardType.Background, "background reward: unknown id keeps Background type");
            Grant("background-reward:unknown", expectFailure: true, rows: [unknown]);
            AssertEqual(0, playerSaves.ReplaceOneCalls, "background reward: unknown id does not save Player");
            AssertEqual(false, character.AppliedRewardClaims.Contains("background-reward:unknown", StringComparer.Ordinal),
                "background reward: unknown id records no character receipt");
            AssertEqual("", Ids(player.OwnedBackgroundIds), "background reward: unknown id grants nothing");
            AssertNoPacket("background reward unknown id");

            // Player save fails after Inventory/Character receipts land: ownership rolls back, retry recovers.
            playerSaves.ThrowOnReplaceOne = true;
            Grant("background-reward:first", expectFailure: true);
            playerSaves.ThrowOnReplaceOne = false;
            AssertEqual("", Ids(player.OwnedBackgroundIds), "background reward: failed Player save restores ownership");
            AssertEqual(true, character.AppliedRewardClaims.Contains("background-reward:first", StringComparer.Ordinal),
                "background reward: character receipt survives Player save failure");
            AssertNoPacket("background reward failure");
            int savesBefore = playerSaves.ReplaceOneCalls;

            Grant("background-reward:first");
            AssertEqual(Ids(ids), Ids(ReadAdds("background reward recovery")), "background reward: recovery pushes both scenes");
            AssertNoPacket("background reward recovery");
            AssertEqual(Ids(ids), Ids(player.OwnedBackgroundIds), "background reward: recovery grants both scenes");
            AssertEqual(savesBefore + 1, playerSaves.ReplaceOneCalls, "background reward: recovery saves Player once");

            Grant("background-reward:first");
            Grant("background-reward:duplicate");
            AssertNoPacket("background reward retry/owned duplicate");
            AssertEqual(Ids(ids), Ids(player.OwnedBackgroundIds), "background reward: retry/duplicate stays exactly-once");
            AssertEqual(savesBefore + 1, playerSaves.ReplaceOneCalls, "background reward: retry/duplicate does not save Player");

            Player relogged = BsonSerializer.Deserialize<Player>(playerSaves.LastReplacement!.ToBson());
            AssertEqual(Ids(ids), Ids(relogged.OwnedBackgroundIds), "background reward: BSON relog keeps scenes");
            harness.Session.player = relogged;
            NotifyLogin login = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
                    "BuildNotifyLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)])
                .Invoke(null, [harness.Session]) as NotifyLogin
                ?? throw new InvalidDataException("AccountModule.BuildNotifyLogin returned nil.");
            foreach (int id in ids)
                AssertEqual(true, login.HaveBackgroundIds.Contains(id), $"background reward: NotifyLogin HaveBackgroundIds has {id}");
        }
    }
}
