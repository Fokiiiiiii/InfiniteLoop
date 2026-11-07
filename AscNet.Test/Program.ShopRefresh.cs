using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Handlers;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using ClientShop = AscNet.Common.MsgPack.GetShopInfoResponse.GetShopInfoResponseClientShop;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateShopRefreshCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        var savedPlayers = DispatchProxy.Create<MongoDB.Driver.IMongoCollection<Player>, RecordingMongoCollectionProxy<Player>>();
        var playerSaves = (RecordingMongoCollectionProxy<Player>)(object)savedPlayers;
        MongoCollectionOverride.SetStaticField(MongoCollectionOverride.RequiredCollectionField(typeof(Player)), savedPlayers);
        const long playerId = 99_164;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, [new Item { Id = Inventory.Coin, Count = 1000 }]);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, inventory, "shop-refresh");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        int packetId = 49_200;
        RefreshShopResponse Refresh(uint id)
        {
            InvokeRegisteredRequestHandler(nameof(RefreshShopRequest), harness.Session, ++packetId, new RefreshShopRequest { Id = id });
            return (RefreshShopResponse)ReadResponsePayload(harness, packetId, nameof(RefreshShopResponse), "manual refresh", typeof(RefreshShopResponse), maxPacketsToRead: 64);
        }
        AssertEqual(20030018, Refresh(1).Code, "real nonrefreshable catalog rejected");
        AssertEqual(20030007, Refresh(uint.MaxValue).Code, "unknown catalog rejected");
        Type module = typeof(RefreshShopRequest).Assembly.GetType("AscNet.GameServer.Handlers.ShopModule")!;
        object Field(string name) => module.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var catalogs = ((Lazy<Dictionary<uint, ClientShop>>)Field("RetailShopSnapshot")).Value;
        var rules = (Dictionary<uint, (int ClockId, bool ResetManualTimes)>)Field("ShopRefreshRules");
        var resetGoods = (HashSet<uint>)Field("ShopResetGoods");
        uint[] ids = [1_900_001, 1_900_002];
        try
        {
            for (int index = 0; index < ids.Length; index++)
            {
                uint id = ids[index];
                catalogs[id] = new ClientShop
                {
                    Id = id, Name = "test-only refresh", ManualResetTimesLimit = index + 1,
                    RefreshCostId = Inventory.Coin, RefreshCostCount = (index + 1) * 10,
                    GoodsList = [new() { Id = id, BuyTimesLimit = 5, RewardGoods = new() { TemplateId = (uint)Inventory.Coin, Count = 1, RewardType = 1 } },
                        new() { Id = id + 10, BuyTimesLimit = 5, RewardGoods = new() { TemplateId = (uint)Inventory.Coin, Count = 1, RewardType = 1 } }]
                };
                rules[id] = (10001, true);
                resetGoods.Add(id);
                player.ShopBuyTimes[id] = 3;
                player.ShopBuyTimes[id + 10] = 2;
            }
            RefreshShopResponse first = Refresh(ids[0]);
            AssertEqual(0, first.Code, "refresh succeeds");
            AssertEqual(990L, inventory.Items.Single(item => item.Id == Inventory.Coin).Count, "refresh debits catalog cost");
            AssertEqual(0, first.ClientShop!.GoodsList.Single(goods => goods.Id == ids[0]).TotalBuyTimes, "refresh resets eligible goods");
            AssertEqual(2, first.ClientShop.GoodsList.Single(goods => goods.Id == ids[0] + 10).TotalBuyTimes, "refresh preserves nonrefreshable goods");
            AssertEqual(1, first.ClientShop.ManualRefreshTimes, "response includes new counter");
            AssertEqual(20030010, Refresh(ids[0]).Code, "retry consumes no cost when limit reached");
            AssertEqual(990L, inventory.Items.Single(item => item.Id == Inventory.Coin).Count, "limit failure preserves currency");
            Player reloaded = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Manual refresh did not save player state."));
            AssertEqual(1, reloaded.ShopManualRefreshTimes[ids[0]], "refresh counter survives BSON reload");
            harness.Session.player = reloaded;
            player = reloaded;
            AssertEqual(20030010, Refresh(ids[0]).Code, "reloaded limit enforced");
            player.ShopManualRefreshPeriods[ids[0]] = 0;
            player.ShopBuyTimes[ids[0]] = 4;
            AssertEqual(0, Refresh(ids[0]).Code, "clock boundary restores refresh allowance");
            AssertEqual(1, player.ShopManualRefreshTimes[ids[0]], "boundary resets then increments counter");
            inventory.Items.Single(item => item.Id == Inventory.Coin).Count = 0;
            AssertEqual(20012004, Refresh(ids[1]).Code, "insufficient currency rejected");
            AssertEqual(3, player.ShopBuyTimes[ids[1]], "currency failure preserves counts");
            inventory.Items.Single(item => item.Id == Inventory.Coin).Count = 100;
            AssertEqual(0, Refresh(ids[1]).Code, "second catalog refresh succeeds");
            AssertEqual(80L, inventory.Items.Single(item => item.Id == Inventory.Coin).Count, "second catalog uses distinct cost");
            AssertEqual(0, Refresh(ids[1]).Code, "second catalog permits second refresh");
            AssertEqual(20030010, Refresh(ids[1]).Code, "second catalog own limit enforced");
            InvokeRegisteredRequestHandler(nameof(GetShopBaseInfoRequest), harness.Session, ++packetId, new GetShopBaseInfoRequest());
            GetShopBaseInfoResponse bases = ReadResponsePayload<GetShopBaseInfoResponse>(harness, packetId, nameof(GetShopBaseInfoResponse), "dorm base metadata");
            AssertEqual(true, bases.ShopBaseInfoList.Any(row => Convert.ToInt32(row["Type"]) == 101), "dorm catalog tabs advertised");
        }
        finally
        {
            foreach (uint id in ids) { catalogs.Remove(id); rules.Remove(id); resetGoods.Remove(id); }
        }
    }
}
