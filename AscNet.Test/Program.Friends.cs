using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.player;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;
internal partial class Program
{
    private static void ValidateFriendsCompatibility()
    {
        var original = Player.collection;
        string databaseName = "ascnet_friends_verification_" + Guid.NewGuid().ToString("N");
        var client = AscNet.Common.Common.db.Client;
        Player.collection = client.GetDatabase(databaseName).GetCollection<Player>("players");
        List<LoopbackSessionHarness> sessions = new();
        try
        {
            PacketFactory.LoadPacketHandlers();
            LoopbackSessionHarness Create(long uid, string name)
            {
                Player player = CreateDrawCompatibilityPlayer(uid);
                player.PlayerData.Name = name;
                player.PlayerData.Level = TableReaderV2.Parse<PlayerTable>().First().Level;
                player.PlayerData.LastLoginTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Player.collection.InsertOne(player);
                var harness = new LoopbackSessionHarness(CreateDrawCompatibilityCharacter(uid), player,
                    CreateDrawCompatibilityInventory(uid, []), "friends-" + Guid.NewGuid().ToString("N"), startClientLoop: false);
                Server.Instance.Sessions.TryAdd(harness.Session.id, harness.Session);
                sessions.Add(harness);
                return harness;
            }
            var a = Create(1800000101, "Friend Alpha");
            var b = Create(1800000102, "Friend Beta");
            uint A = (uint)a.Session.player.PlayerData.Id, B = (uint)b.Session.player.PlayerData.Id;
            JObject Rpc(LoopbackSessionHarness h, string name, object data) => GuildRpc(h, name + "Request", data);
            void Code(JObject result, int expected, string scenario) => GuildAssert(result.Value<int>("Code") == expected, scenario + ": " + result);
            Player Reload(LoopbackSessionHarness h) => h.Session.player = Player.collection.Find(p => p.PlayerData.Id == h.Session.player.PlayerData.Id).Single();
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = A }), 20031008, "self");
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = 0u }), 20031013, "missing target");
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = B }), 20031011, "apply");
            var application = ReadPushPayload<NotifyAddApply>(b, "NotifyAddApply", "application target push");
            GuildAssert(application.ApplyId == A && application.CreateTime > 0, "application payload");
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = B }), 20031004, "duplicate pending");
            Code(Rpc(b, "OperationApplyFriend", new { TargetPlayerId = A, IsAccept = true }), 0, "accept");
            var added = ReadPushPayload<NotifyAddFriend>(a, "NotifyAddFriend", "accepted applicant push");
            GuildAssert(added.PlayerId == B && added.CreateTime > 0, "accepted payload");
            GuildAssert(Reload(a).Social.Friends.ContainsKey(B) && Reload(b).Social.Friends.ContainsKey(A), "both sides persist after reload");
            Code(Rpc(b, "OperationApplyFriend", new { TargetPlayerId = A, IsAccept = true }), 20031005, "accept retry");
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = B }), 20031003, "existing friend");
            Code(Rpc(a, "FriendRemark", new { Id = B, Name = "Beta note" }), 0, "remark");
            GuildAssert(Reload(a).Social.Remarks[B] == "Beta note", "remark reload");
            Code(Rpc(a, "FriendRemark", new { Id = B, Name = "Second note" }), 0, "distinct remark");
            GuildAssert(Reload(a).Social.Remarks[B] == "Second note", "remark changed");
            Code(Rpc(a, "FriendRemark", new { Id = B, Name = new string('x', 1000) }), 20031022, "remark limit");
            Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.SocialModule");
            MethodInfo login = RequiredMethod(module, "BuildLoginData", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Player)]);
            var loginData = (NotifySocialData)login.Invoke(null, [a.Session.player])!;
            GuildAssert(loginData.FriendData.Single().PlayerId == B && loginData.Remarks.Single().Remark == "Second note", "login persisted friendship and remark");
            var infoA = Rpc(b, "GetSpecifyPlayerInfo", new { Id = A });
            var infoB = Rpc(a, "GetSpecifyPlayerInfo", new { Id = B });
            GuildAssert(infoA["PlayerInfo"]!.Value<string>("Name") == "Friend Alpha" && infoB["PlayerInfo"]!.Value<string>("Name") == "Friend Beta", "distinct real player projections");
            Code(Rpc(a, "DeleteFriend", new { Ids = new[] { B } }), 20031012, "delete");
            GuildAssert(ReadPushPayload<NotifyDeleteFriend>(b, "NotifyDeleteFriend", "deleted target").PlayerId == A, "delete push");
            GuildAssert(Reload(a).Social.Friends.Count == 0 && Reload(b).Social.Friends.Count == 0 && a.Session.player.Social.Remarks.Count == 0, "delete both and remark");
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = B }), 20031002, "delete cooldown");
            a.Session.player.Social.Deleted.Clear(); a.Session.player.SaveChecked();
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = B }), 20031011, "reapply");
            _ = ReadPushPayload<NotifyAddApply>(b, "NotifyAddApply", "reapplication");
            Code(Rpc(b, "OperationApplyFriend", new { TargetPlayerId = A, IsAccept = false }), 0, "refuse");
            GuildAssert(Reload(b).Social.Applications.Count == 0, "refuse persists");
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = B }), 20031001, "refusal cooldown");
            b.Session.player.Social.Refused.Clear(); b.Session.player.SaveChecked();
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = B }), 20031011, "apply after cooldown");
            _ = ReadPushPayload<NotifyAddApply>(b, "NotifyAddApply", "third application");
            Code(Rpc(b, "OperationApplyFriend", new { TargetPlayerId = A, IsAccept = true }), 0, "accept again");
            _ = ReadPushPayload<NotifyAddFriend>(a, "NotifyAddFriend", "friend again");
            Code(Rpc(a, "BlockPlayer", new { PlayerId = B }), 0, "block");
            GuildAssert(ReadPushPayload<NotifyBlock>(b, "NotifyBlock", "blocked target").PlayerId == A, "block target push");
            GuildAssert(Reload(a).Social.Blocked.ContainsKey(B) && a.Session.player.Social.Friends.Count == 0 && Reload(b).Social.Friends.Count == 0, "block persisted removes both sides");
            Code(Rpc(a, "BlockPlayer", new { PlayerId = B }), 20031025, "duplicate block");
            Code(Rpc(b, "ApplyFriend", new { TargetPlayerId = A }), 20031024, "blocked inbound apply");
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = B }), 20031024, "blocked outbound apply");
            var recommendation = Rpc(a, "GetRecommendPlayerList", new { });
            GuildAssert(!recommendation["RecommendFriendsList"]!.Any(p => p.Value<uint>("Id") == B), "recommend excludes blocked");
            Code(Rpc(a, "RemoveBlacklist", new { PlayerId = B }), 0, "unblock");
            GuildAssert(Reload(a).Social.Blocked.Count == 0, "unblock persists");
            Code(Rpc(a, "RemoveBlacklist", new { PlayerId = B }), 20031028, "unblock retry");
            int capacity = TableReaderV2.Parse<PlayerTable>().Single(p => p.Level == a.Session.player.PlayerData.Level).MaxFriendCount;
            for (uint i = 1; i <= capacity; i++) a.Session.player.Social.Friends[i] = 1;
            a.Session.player.SaveChecked();
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = B }), 20031015, "self capacity");
            a.Session.player.Social.Friends.Clear(); a.Session.player.SaveChecked();
            var config = TableReaderV2.Parse<ConfigTable>().ToDictionary(p => p.Key, p => p.Value);
            for (uint i = 1; i <= int.Parse(config["FriendMaxAcceptApplyCount"]); i++) b.Session.player.Social.Applications[i] = 1;
            b.Session.player.SaveChecked();
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = B }), 20031017, "application capacity");
            b.Session.player.Social.Applications.Clear(); b.Session.player.SaveChecked();
            a.Session.player.Social.ApplicationDay = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86400;
            a.Session.player.Social.DailyApplications = int.Parse(config["ApplyFriendDailyLimit"]);
            a.Session.player.SaveChecked();
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = B }), 20031030, "daily limit");
            // Offline recipients must be loaded and mutated from their durable record.
            a.Session.player.Social.DailyApplications = 0; a.Session.player.SaveChecked();
            Server.Instance.Sessions.TryRemove(b.Session.id, out _);
            Code(Rpc(a, "ApplyFriend", new { TargetPlayerId = B }), 20031011, "offline application");
            GuildAssert(Reload(b).Social.Applications.ContainsKey(A), "offline recipient persistence");
            Server.Instance.Sessions.TryAdd(b.Session.id, b.Session);
            Server.Instance.Sessions.TryRemove(a.Session.id, out _);
            Code(Rpc(b, "OperationApplyFriend", new { TargetPlayerId = A, IsAccept = true }), 0, "accept offline applicant");
            GuildAssert(Reload(a).Social.Friends.ContainsKey(B) && Reload(b).Social.Friends.ContainsKey(A), "offline acceptance persists both sides");
            Code(Rpc(b, "DeleteFriend", new { Ids = new[] { A } }), 20031012, "delete offline friend");
            GuildAssert(Reload(a).Social.Friends.Count == 0 && Reload(b).Social.Friends.Count == 0, "offline deletion persists both sides");
        }
        finally
        {
            foreach (var h in sessions) { Server.Instance.Sessions.TryRemove(h.Session.id, out _); h.Dispose(); }
            Player.collection = original;
            client.DropDatabase(databaseName);
        }
    }
}
