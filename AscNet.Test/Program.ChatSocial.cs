using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.chat;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.player;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;
internal partial class Program
{
    private static void ValidateChatSocialCompatibility()
    {
        var originalPlayer = Player.collection;
        var originalGuild = Guild.collection;
        string databaseName = "ascnet_chat_social_" + Guid.NewGuid().ToString("N")[..12];
        var database = AscNet.Common.Common.db.Client.GetDatabase(databaseName);
        Player.collection = database.GetCollection<Player>("players");
        Guild.collection = database.GetCollection<Guild>("guilds");
        List<LoopbackSessionHarness> sessions = new();
        List<long> uids = new();
        try
        {
            PacketFactory.LoadPacketHandlers();
            var config = TableReaderV2.Parse<ConfigTable>().ToDictionary(r => r.Key, r => int.TryParse(r.Value, out int v) ? v : 0);
            LoopbackSessionHarness Create(long uid, string name)
            {
                Player player = CreateDrawCompatibilityPlayer(uid);
                player.PlayerData.Name = name;
                player.PlayerData.Level = TableReaderV2.Parse<PlayerTable>().First().Level;
                Player.collection.InsertOne(player);
                Character character = CreateDrawCompatibilityCharacter(uid);
                Inventory inventory = CreateDrawCompatibilityInventory(uid, []);
                Character.collection.InsertOne(character);
                Inventory.collection.InsertOne(inventory);
                uids.Add(uid);
                var harness = new LoopbackSessionHarness(character, player, inventory, "chatsocial-" + Guid.NewGuid().ToString("N"), startClientLoop: false);
                Server.Instance.Sessions.TryAdd(harness.Session.id, harness.Session);
                sessions.Add(harness);
                return harness;
            }
            long baseUid = 1800000200 + Random.Shared.Next(1, 1_000_000);
            var a = Create(baseUid, "Chat Alpha");
            var b = Create(baseUid + 1, "Chat Beta");
            var c = Create(baseUid + 2, "Chat Gamma");
            uint A = (uint)a.Session.player.PlayerData.Id, B = (uint)b.Session.player.PlayerData.Id, C = (uint)c.Session.player.PlayerData.Id;
            JObject Rpc(LoopbackSessionHarness h, string name, object data) => GuildRpc(h, name + "Request", data);
            void Code(JObject result, int expected, string scenario) => GuildAssert(result.Value<int>("Code") == expected, scenario + ": " + result);
            Player Reload(uint id) => Player.collection.Find(p => p.PlayerData.Id == id).Single();
            long Coins(LoopbackSessionHarness h) => h.Session.inventory.Items.FirstOrDefault(i => i.Id == Inventory.Coin)?.Count ?? 0;
            a.Session.player.Social.Friends[B] = 1; b.Session.player.Social.Friends[A] = 1;
            a.Session.player.Social.Friends[C] = 1; c.Session.player.Social.Friends[A] = 1;
            b.Session.player.Social.Friends[C] = 1; c.Session.player.Social.Friends[B] = 1;
            foreach (var h in new[] { a, b, c }) h.Session.player.SaveChecked();

            // Emoji pack order.
            var packs = TableReaderV2.Parse<EmojiPackTable>().Where(p => p.Id != 0).Select(p => p.Id).ToHashSet();
            var emojiByPack = TableReaderV2.Parse<EmojiTable>().Where(e => e.PackageId is > 0 && packs.Contains(e.PackageId.Value)).GroupBy(e => e.PackageId!.Value).OrderBy(g => g.Key).ToList();
            GuildAssert(emojiByPack.Count >= 3, "fixture needs three emoji packs");
            int p1 = emojiByPack[0].Key, p2 = emojiByPack[1].Key, p3 = emojiByPack[2].Key;
            long future = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400;
            foreach (int pack in new[] { p1, p2 })
            {
                var emoji = emojiByPack.Single(g => g.Key == pack).First();
                a.Session.character.ChatEmojis.Add(new OwnedChatEmoji { Id = emoji.Id, EndTime = future });
            }
            bool p3Owned = a.Session.character.CanUseChatEmoji(emojiByPack[2].First().Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                || emojiByPack[2].Any(e => a.Session.character.CanUseChatEmoji(e.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            Code(Rpc(a, "SaveEmojiPackageId", new { OrderEmojiPackageIds = new[] { p2, p1 } }), 0, "save order");
            GuildAssert(Reload(A).ChatSocial.EmojiPackOrder.SequenceEqual([p2, p1]), "emoji order persisted");
            GuildAssert(Rpc(a, "GetEmojiPackageId", new { })["OrderEmojiPackageIds"]!.Values<int>().Take(2).SequenceEqual([p2, p1]), "get returns saved order");
            Code(Rpc(a, "SaveEmojiPackageId", new { OrderEmojiPackageIds = new[] { p1, p2 } }), 0, "reorder");
            GuildAssert(Rpc(a, "GetEmojiPackageId", new { })["OrderEmojiPackageIds"]!.Values<int>().Take(2).SequenceEqual([p1, p2]), "get returns reordered");
            Code(Rpc(a, "SaveEmojiPackageId", new { OrderEmojiPackageIds = new[] { p1, p1 } }), 20033014, "duplicate");
            Code(Rpc(a, "SaveEmojiPackageId", new { OrderEmojiPackageIds = new[] { 0 } }), 20033014, "default pack");
            Code(Rpc(a, "SaveEmojiPackageId", new { OrderEmojiPackageIds = new[] { 987654321 } }), 20033014, "unknown pack");
            if (!p3Owned) Code(Rpc(a, "SaveEmojiPackageId", new { OrderEmojiPackageIds = new[] { p1, p3 } }), 20033014, "unowned pack");
            GuildAssert(Reload(A).ChatSocial.EmojiPackOrder.SequenceEqual([p1, p2]), "rejected saves keep order");
            Code(Rpc(a, "SaveEmojiPackageId", new { }), 0, "nil list clears");
            GuildAssert(Reload(A).ChatSocial.EmojiPackOrder.Count == 0, "cleared");

            // Chat gifts.
            int min = config["ChatGiftCountMin"], max = config["ChatGiftCountMax"], quota = config["GetGiftMaxCountEveryDay"];
            SendChatRequest Gift(params long[] targets) => new() { ChatData = new ChatData { ChannelType = ChatChannelType.Private, MsgType = ChatMsgType.Gift, Content = "" }, TargetIdList = targets.ToList() };
            Code(Rpc(a, "SendChat", Gift(999999)), 20033001, "gift to non-friend");
            Code(Rpc(a, "SendChat", new SendChatRequest { ChatData = new ChatData { ChannelType = ChatChannelType.World, MsgType = ChatMsgType.Gift } }), 20033001, "gift outside private");
            Code(Rpc(a, "SendChat", Gift(B)), 0, "send gift");
            var live = ReadPushPayload<NotifyChatMessage>(b, "NotifyChatMessage", "gift push to receiver");
            var inbox = Reload(B).ChatSocial.Gifts;
            GuildAssert(inbox.Count == 1 && inbox[0].SenderId == A && inbox[0].Count >= min && inbox[0].Count <= max && inbox[0].Status == 1 && inbox[0].ItemId == Inventory.Coin, "receiver inbox persisted");
            GuildAssert(live.MsgType == ChatMsgType.Gift && live.GiftId == inbox[0].GiftId && live.GiftCount == inbox[0].Count && live.GiftStatus == ChatGiftState.WaitReceive && live.SenderId == A && live.TargetId == B, "receiver push mirrors record");
            Code(Rpc(a, "SendChat", Gift(B)), 20033008, "second gift same day");
            GuildAssert(Reload(B).ChatSocial.Gifts.Count == 1, "rejected gift not delivered");
            var offline = Rpc(b, "OfflineMessage", new { MessageId = 0 });
            GuildAssert(offline["Messages"]!.Count() == 1 && offline["Messages"]![0]!.Value<int>("GiftId") == inbox[0].GiftId, "offline replay of pending gift");
            GuildAssert(Rpc(b, "OfflineMessage", new { MessageId = inbox[0].GiftId })["Messages"]!.Count() == 0, "offline replay honours cursor");
            Code(Rpc(b, "GetGifts", new { GiftId = inbox[0].GiftId + 100 }), 20033003, "unknown gift");
            long before = Coins(b);
            var claim = Rpc(b, "GetGifts", new { GiftId = inbox[0].GiftId });
            Code(claim, 0, "claim");
            GuildAssert(claim["RewardGoodsList"]![0]!.Value<int>("TemplateId") == Inventory.Coin && claim["RewardGoodsList"]![0]!.Value<int>("Count") == inbox[0].Count, "reward matches gift");
            GuildAssert(Coins(b) == before + inbox[0].Count, "coins granted once");
            Code(Rpc(b, "GetGifts", new { GiftId = inbox[0].GiftId }), 20033003, "retry claim");
            GuildAssert(Coins(b) == before + inbox[0].Count, "retry grants nothing");
            GuildAssert(Reload(B).ChatSocial.Gifts[0].Status == 2 && Reload(B).ChatSocial.GiftReceivedToday == 1, "received status persisted");
            GuildAssert(Inventory.collection.Find(i => i.Uid == B).Single().Items.Single(i => i.Id == Inventory.Coin).Count == Coins(b), "inventory persisted");

            // Group send and claim-all with pairing and quota.
            Code(Rpc(c, "SendChat", Gift(A, B)), 0, "group send");
            Code(Rpc(a, "SendChat", Gift(C)), 0, "send to gamma");
            Code(Rpc(b, "SendChat", Gift(A)), 0, "beta to alpha");
            var all = Rpc(a, "GetAllGifts", new { });
            Code(all, 0, "claim all");
            var infos = all["GiftInfoList"]!.ToList();
            var goods = all["RewardGoodsList"]!.ToList();
            var aInbox = Reload(A).ChatSocial.Gifts;
            GuildAssert(infos.Count == 2 && goods.Count == 2 && aInbox.Count == 2 && aInbox.All(g => g.Status == 2), "both gifts claimed");
            for (int i = 0; i < 2; i++)
            {
                var g = aInbox.Single(x => x.GiftId == infos[i].Value<int>("GiftId"));
                GuildAssert(g.SenderId == infos[i].Value<uint>("FriendId") && goods[i].Value<int>("Count") == g.Count, "reward aligned with gift info " + i);
            }
            GuildAssert(infos.Select(i => i.Value<uint>("FriendId")).OrderBy(x => x).SequenceEqual(new[] { B, C }.OrderBy(x => x)), "two distinct senders");
            Code(Rpc(a, "GetAllGifts", new { }), 20033003, "nothing left");
            // Daily quota.
            var cs = Reload(C); cs.ChatSocial.GiftSentDays.Clear(); cs.SaveChecked(); c.Session.player.ChatSocial.GiftSentDays.Clear();
            Code(Rpc(c, "SendChat", Gift(A)), 0, "gamma again");
            a.Session.player.ChatSocial.GiftReceivedToday = quota; a.Session.player.SaveChecked();
            Code(Rpc(a, "GetAllGifts", new { }), 20033005, "quota reached all");
            Code(Rpc(a, "GetGifts", new { GiftId = a.Session.player.ChatSocial.Gifts.Last().GiftId }), 20033005, "quota reached single");
            // Expiry.
            a.Session.player.ChatSocial.GiftReceivedToday = 0;
            a.Session.player.ChatSocial.Gifts.Last().CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3 * 86400; a.Session.player.SaveChecked();
            Code(Rpc(a, "GetGifts", new { GiftId = a.Session.player.ChatSocial.Gifts.Last().GiftId }), 20033003, "expired gift");
            GuildAssert(Reload(A).ChatSocial.Gifts.Last().Status == 3, "expired gift marked Fetched");

            // Reports.
            int interval = config["ReportInterval"], daily = config["ReportDailyMaxTimes"];
            object Report(long target, int entry, int[]? tags) => new { PlayerId = target, PlayerName = "x", Entry = entry, Tags = tags, Message = "note", PlayerLevel = 1, ReportMessage = "bad words", ChatChannel = 3 };
            Code(Rpc(a, "Report", Report(B, 99, [101])), 20139001, "unknown entry");
            Code(Rpc(a, "Report", Report(B, 2, [6, 151])), 20139001, "guild entry via player report");
            Code(Rpc(a, "Report", Report(B, 1, [999])), 20139001, "unknown tag");
            Code(Rpc(a, "Report", Report(B, 1, null)), 20139001, "nil tags");
            Code(Rpc(a, "Report", Report(B, 3, [2])), 20139001, "tier-one entry needs sub tag");
            Code(Rpc(a, "Report", Report(B, 3, [2, 121])), 20139001, "sub tag of other parent");
            Code(Rpc(a, "Report", Report(123456789, 1, [101])), 20139004, "missing player");
            GuildAssert(Reload(A).ChatSocial.Reports.Count == 0 && Reload(A).ChatSocial.ReportTimes == 0, "rejected reports leave no record");
            Code(Rpc(a, "Report", Report(B, 1, [101])), 0, "report chat");
            var rec = Reload(A).ChatSocial;
            GuildAssert(rec.Reports.Single().TargetId == B && rec.Reports[0].TargetName == "Chat Beta" && rec.Reports[0].ChatChannel == 3 && rec.Reports[0].ReportMessage == "bad words" && rec.ReportTimes == 1 && rec.LastReportTime > 0, "report persisted from server state");
            GuildAssert(ChatSocialBuild(a).ReportData.ReportTimes == 1, "login report data");
            Code(Rpc(a, "Report", Report(C, 3, [2, 111])), 20139002, "interval");
            a.Session.player.ChatSocial.LastReportTime -= interval; a.Session.player.SaveChecked();
            Code(Rpc(a, "Report", Report(C, 3, [3, 121, 1201])), 20139001, "bad third tier");
            Code(Rpc(a, "Report", Report(C, 3, [2, 111])), 0, "report player");
            GuildAssert(Reload(A).ChatSocial.Reports.Count == 2 && Reload(A).ChatSocial.Reports[1].TargetId == C && Reload(A).ChatSocial.ReportTimes == 2, "second report distinct");
            a.Session.player.ChatSocial.LastReportTime = 0; a.Session.player.ChatSocial.ReportTimes = daily; a.Session.player.SaveChecked();
            Code(Rpc(a, "Report", Report(C, 3, [2, 111])), 20139003, "daily quota");
            a.Session.player.ChatSocial.ReportDay -= 1; a.Session.player.SaveChecked();
            GuildAssert(ChatSocialBuild(a).ReportData.ReportTimes == 0, "quota resets on the next daily period");
            Code(Rpc(a, "ReportGuild", new { GuildId = 424242, Entry = 2, Tags = new[] { 6, 151 }, Message = "m", ReportMessage = "r" }), 20139005, "missing guild");

            // Guild reports and scripts need a guild.
            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Guild guild = new() { Id = 7000001, Name = "ChatSocialGuild", LeaderId = B, Active = true, MemberIds = [B], CreatedAt = created };
            guild.Members[B] = new GuildMemberState { Rank = 1 };
            Guild.collection.InsertOne(guild);
            Code(Rpc(a, "ReportGuild", new { GuildId = 7000001, Entry = 1, Tags = new[] { 101 } }), 20139001, "guild report wrong entry");
            Code(Rpc(a, "ReportGuild", new { GuildId = 7000001, Entry = 2, Tags = new[] { 6, 151 }, Message = "m", ReportMessage = "r" }), 0, "report guild");
            var gr = Reload(A).ChatSocial.Reports.Last();
            GuildAssert(gr.Kind == 1 && gr.TargetId == 7000001 && gr.TargetName == "ChatSocialGuild" && gr.Tags.SequenceEqual([6, 151]), "guild report persisted");
            Code(Rpc(b, "GuildListScript", new { }), 0, "list scripts empty");
            b.Session.player.GuildState.Scripts = ["hello", "welcome"]; b.Session.player.SaveChecked();
            var listed = Rpc(b, "GuildListScript", new { });
            GuildAssert(listed["Scripts"]!.Values<string>().SequenceEqual(["hello", "welcome"]), "scripts listed from persisted state");
            Code(Rpc(a, "GuildListScript", new { }), 20063125, "non-member scripts");
        }
        finally
        {
            foreach (var h in sessions) Server.Instance.Sessions.TryRemove(h.Session.id, out _);
            foreach (var h in sessions) h.Dispose();
            foreach (long uid in uids)
            {
                Character.collection.DeleteOne(x => x.Uid == uid);
                Inventory.collection.DeleteOne(x => x.Uid == uid);
            }
            AscNet.Common.Common.db.Client.DropDatabase(databaseName);
            Player.collection = originalPlayer;
            Guild.collection = originalGuild;
        }
    }

    private static NotifyPlayerReportData ChatSocialBuild(LoopbackSessionHarness h) =>
        (NotifyPlayerReportData)RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.ChatSocialModule"), "BuildReportData",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, [typeof(Player)]).Invoke(null, [h.Session.player])!;
}
