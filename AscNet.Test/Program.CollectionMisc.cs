using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.exhibition;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.mailcollectionbox;
using AscNet.Table.V2.share.reward;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateCollectionMiscCompatibility()
    {
        const long playerId = 99_610;
        List<ExhibitionLimitTable> limits = TableReaderV2.Parse<ExhibitionLimitTable>()
            .Where(row => row.AureoleIds.Distinct().Count() >= 2).Take(2).ToList();
        AssertEqual(2, limits.Count, "two distinct authored ExhibitionLimit rows");
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
        Character roster = CreateDrawCompatibilityCharacter(playerId);
        roster.Characters = limits.Select(row => new CharacterData { Id = (uint)row.CharacterId, Level = 1, LiberateLv = 5 }).ToList();
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness harness = new(roster, player, inventory, "collection-misc");
        int id = 99_610_000;
        T Call<T>(string name, object request) where T : class
        {
            InvokeRegisteredRequestHandler(name + "Request", harness.Session, ++id, request);
            return ReadResponsePayload<T>(harness, id, name + "Response", name, maxPacketsToRead: 16);
        }

        // GatherRewardList mirrors the persisted claims, including after a BSON reload.
        ExhibitionRewardTable[] gathers = TableReaderV2.Parse<ExhibitionRewardTable>().Take(2).ToArray();
        AssertEqual(0, Call<GatherRewardListResponse>("GatherRewardList", new { }).GatherRewards.Count, "fresh gather list empty");
        player.AddGatherReward(gathers[0].Id);
        AssertEqual(true, Call<GatherRewardListResponse>("GatherRewardList", new { }).GatherRewards.SequenceEqual([gathers[0].Id]), "gather list shows first claim");
        player.AddGatherReward(gathers[1].Id);
        player = harness.Session.player = BsonSerializer.Deserialize<Player>(player.ToBson());
        AssertEqual(true, Call<GatherRewardListResponse>("GatherRewardList", new { }).GatherRewards.SequenceEqual([gathers[0].Id, gathers[1].Id]), "gather list survives reload");

        // Aureole: ownership, authored per-character list, liberation level, persistence.
        int aureoleA = limits[0].AureoleIds[0];
        int aureoleB = limits[0].AureoleIds.First(value => value != aureoleA);
        int foreignAureole = limits[1].AureoleIds.First(value => !limits[0].AureoleIds.Contains(value));
        int missingAureole = TableReaderV2.Parse<FashionAureoleTable>().Max(row => row.Id) + 1;
        CharacterSetLiberateAureoleIdResponse SetAureole(int characterId, int aureoleId) =>
            Call<CharacterSetLiberateAureoleIdResponse>("CharacterSetLiberateAureoleId",
                new CharacterSetLiberateAureoleIdRequest { CharacterId = characterId, AureoleId = aureoleId });
        CharacterData first = harness.Session.character.Characters[0];
        AssertEqual(20010007, SetAureole(1, aureoleA).Code, "unowned character");
        AssertEqual(20010006, SetAureole(limits[0].CharacterId, missingAureole).Code, "aureole not in FashionAureole");
        AssertEqual(20010009, SetAureole(limits[0].CharacterId, foreignAureole).Code, "aureole not in the character's list");
        first.LiberateLv = 4;
        AssertEqual(20010008, SetAureole(limits[0].CharacterId, aureoleA).Code, "below Super liberation");
        AssertEqual(0, first.LiberateAureoleId, "rejections persist nothing");
        first.LiberateLv = 5;
        AssertEqual(0, SetAureole(limits[0].CharacterId, aureoleA).Code, "aureole A set");
        AssertEqual(aureoleA, first.LiberateAureoleId, "aureole A applied to the live character");
        AssertEqual(0, SetAureole(limits[0].CharacterId, aureoleA).Code, "same aureole idempotent");
        AssertEqual(0, SetAureole(limits[0].CharacterId, aureoleB).Code, "aureole B set");
        AssertEqual(aureoleB, BsonSerializer.Deserialize<Character>(harness.Session.character.ToBson()).Characters[0].LiberateAureoleId, "aureole survives BSON reload");
        AssertEqual(0, harness.Session.character.Characters[1].LiberateAureoleId, "other character untouched");

        // Favorite mail: validated against the table-derived open collection box, persisted, rewards once.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<MailCollectionBoxActivityTable> open = TableReaderV2.Parse<MailCollectionBoxActivityTable>()
            .Where(activity => CollectionMiscModule.IsFavoriteMailActivityOpen(activity, now)).ToList();
        HashSet<int> openMailIds = open.SelectMany(activity => activity.FavoriteMailIds).ToHashSet();
        Dictionary<int, List<RewardGoodsTable>> itemRewards = TableReaderV2.Parse<MailCollectionBoxFavoriteMailTable>()
            .Where(mail => openMailIds.Contains(mail.Id) && mail.RewardIds.Any(rewardId => rewardId > 0))
            .ToDictionary(mail => mail.Id, mail => mail.RewardIds.Where(rewardId => rewardId > 0).SelectMany(RewardHandler.GetRewardGoods).ToList())
            .Where(entry => entry.Value.Count > 0 && entry.Value.All(goods => RewardHandler.GetRewardType(goods) == RewardType.Item))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        int[] rewarded = itemRewards.Keys.Take(2).ToArray();
        int plain = TableReaderV2.Parse<MailCollectionBoxFavoriteMailTable>()
            .First(mail => openMailIds.Contains(mail.Id) && !mail.RewardIds.Any(rewardId => rewardId > 0)).Id;
        AssertEqual(2, rewarded.Length, "two distinct rewarded favorite mails");
        long Owned(int itemId) => inventory.Items.Where(item => item.Id == itemId).Sum(item => (long)item.Count);
        MailCollectionBoxReceiveFavoriteMailIdsResponse Receive(List<int>? ids) =>
            Call<MailCollectionBoxReceiveFavoriteMailIdsResponse>("MailCollectionBoxReceiveFavoriteMailIds",
                new MailCollectionBoxReceiveFavoriteMailIdsRequest { FavoriteMailIds = ids });

        MailCollectionBoxReceiveFavoriteMailIdsResponse nothing = Receive(null);
        AssertEqual(20027007, nothing.Code, "nil list (empty Lua table) rejected");
        AssertEqual(0, nothing.SuccessedIds.Count, "nil list SuccessedIds present and empty");
        AssertEqual(20027007, Receive([int.MaxValue]).Code, "unknown mail id");
        AssertEqual(0, player.ReceivedFavoriteMailIds.Count, "rejections persist nothing");

        int itemId = (int)itemRewards[rewarded[0]][0].TemplateId;
        long before = Owned(itemId);
        MailCollectionBoxReceiveFavoriteMailIdsResponse received = Receive([rewarded[0], int.MaxValue]);
        AssertEqual(0, received.Code, "partial success is Code 0");
        AssertEqual(true, received.SuccessedIds.SequenceEqual([rewarded[0]]), "only the valid id succeeds");
        AssertEqual(true, received.Rewards.Count > 0, "rewards listed");
        AssertEqual(before + itemRewards[rewarded[0]].Where(goods => goods.TemplateId == itemId).Sum(goods => (long)goods.Count), Owned(itemId), "inventory grew by authored reward");
        long after = Owned(itemId);
        MailCollectionBoxReceiveFavoriteMailIdsResponse repeat = Receive([rewarded[0]]);
        AssertEqual(20027003, repeat.Code, "repeat receive rejected");
        AssertEqual(after, Owned(itemId), "repeat grants nothing");

        MailCollectionBoxReceiveFavoriteMailIdsResponse second = Receive([rewarded[0], rewarded[1], plain]);
        AssertEqual(0, second.Code, "batch with a repeat succeeds");
        AssertEqual(true, second.SuccessedIds.SequenceEqual([rewarded[1], plain]), "batch accepts only the new ids");
        player = harness.Session.player = BsonSerializer.Deserialize<Player>(player.ToBson());
        AssertEqual(true, player.ReceivedFavoriteMailIds.OrderBy(value => value).SequenceEqual(new[] { rewarded[0], rewarded[1], plain }.OrderBy(value => value)), "receipts survive BSON reload");
        AssertEqual(true, CollectionMiscModule.BuildLoginData(player, now).MailCollectionBoxDataDb.ReceivedFavoriteMailIds.OrderBy(value => value)
            .SequenceEqual(player.ReceivedFavoriteMailIds.OrderBy(value => value)), "login push carries receipts");
        AssertEqual(20027003, Receive([plain]).Code, "receipt-only mail repeat rejected after reload");

        // A window known to the calendar and already ended closes its activity.
        var ended = AscNet.GameServer.Game.ActivityScheduleService.All.First(entry => entry.EndTime > 0 && entry.EndTime < now.ToUnixTimeSeconds());
        var closed = new MailCollectionBoxActivityTable { Id = 1, TimeId = (int)ended.Id };
        AssertEqual(false, CollectionMiscModule.IsFavoriteMailActivityOpen(closed, now), "ended calendar window closes the activity");

        // BoardMutualRequest is a fire-and-forget client push (XNetwork.Send): no request handler, no response.
        AssertEqual(true, Session.IsKnownClientPush("BoardMutualRequest"), "BoardMutualRequest is a known client push");
        AssertEqual(true, PacketFactory.GetRequestPacketHandler("BoardMutualRequest") is null, "BoardMutualRequest has no response handler");
    }
}
