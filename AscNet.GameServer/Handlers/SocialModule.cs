using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.player;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

internal static class SocialModule
{
    // ponytail: session dispatch already serializes social mutations with GuildModule.MembershipLock;
    // move to ordered per-player locks if the server removes that global dispatch lock.
    private static readonly Lazy<Dictionary<string, string>> Settings = new(() => TableReaderV2.Parse<ConfigTable>().ToDictionary(r => r.Key, r => r.Value));
    private static readonly Lazy<int> RemarkLimit = new(() => TableReaderV2.Parse<AscNet.Table.V2.client.config.ClientConfigTable>().Single(r => r.Key == "MaxNameLength").Value);
    private static int Setting(string key) => int.Parse(Settings.Value[key]);
    private static uint Now => checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private static uint Id(Player p) => checked((uint)p.PlayerData.Id);
    private sealed class SocialFailure(int code) : Exception { public int Code { get; } = code; }
    private static void Require(bool valid, int code) { if (!valid) throw new SocialFailure(code); }
    private static Player Target(Session session, uint id)
    {
        Require(id != Id(session.player), 20031008);
        return Server.Instance.SessionFromUID(id)?.player ?? Player.TryFromPlayerId(id) ?? throw new SocialFailure(20031013);
    }
    private static int Capacity(Player p) => p.PlayerData.HonorLevel > 0
        ? TableReaderV2.Parse<HonorLevelTable>().Single(r => r.HonorLevel == p.PlayerData.HonorLevel).MaxFriendCount
        : TableReaderV2.Parse<PlayerTable>().Single(r => r.Level == p.PlayerData.Level).MaxFriendCount;
    private static void Allowed(Player a, Player b) => Require(!a.Social.Blocked.ContainsKey(Id(b)) && !b.Social.Blocked.ContainsKey(Id(a)), 20031024);
    private static void Push<T>(uint id, T message) where T : new()
    {
        Session? target = Server.Instance.SessionFromUID(id);
        if (target is not null) target.SendPush(message);
    }
    private static int Execute(Action action)
    {
        try { lock (GuildModule.MembershipLock) action(); return 0; }
        catch (SocialFailure failure) { return failure.Code; }
    }
    internal static NotifySocialData BuildLoginData(Player p) => new()
    {
        FriendData = p.Social.Friends.Select(r => new NotifySocialData.NotifySocialDataFriendData { PlayerId = r.Key, CreateTime = r.Value }).ToList(),
        ApplyData = p.Social.Applications.Select(r => new NotifySocialData.NotifySocialDataFriendData { PlayerId = r.Key, CreateTime = r.Value }).ToList(),
        Remarks = p.Social.Remarks.Select(r => new SocialRemark { Id = r.Key, Remark = r.Value }).ToList(),
        BlockData = p.Social.Blocked.Select(r => new SocialBlock { PlayerId = r.Key, BlockTime = r.Value }).ToList()
    };

    [RequestPacketHandler("ApplyFriendRequest")]
    public static void ApplyFriend(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<ApplyFriendRequest>();
        ApplyFriendResponse response = new();
        response.Code = Execute(() =>
        {
            Player a = session.player, b = Target(session, request.TargetPlayerId);
            uint aid = Id(a), bid = Id(b), now = Now;
            Allowed(a, b);
            Require(!a.Social.Friends.ContainsKey(bid), 20031003);
            if (b.Social.Refused.TryGetValue(aid, out uint refused) && (long)refused + Setting("FriendRefuseApplyIntervalSecond") > now)
            { response.OperationTime = refused; throw new SocialFailure(20031001); }
            if (a.Social.Deleted.TryGetValue(bid, out uint deleted) && (long)deleted + Setting("FriendDeleteApplyIntervalSecond") > now)
            { response.OperationTime = deleted; throw new SocialFailure(20031002); }
            Require(!b.Social.Applications.TryGetValue(aid, out uint applied) || (long)applied + Setting("FriendApplyIntervalHour") * 3600L <= now, 20031004);
            Require(a.Social.Friends.Count < Capacity(a), 20031015);
            Require(b.Social.Friends.Count < Capacity(b), 20031016);
            Require(b.Social.Applications.ContainsKey(aid) || b.Social.Applications.Count < Setting("FriendMaxAcceptApplyCount"), 20031017);
            long day = now / 86400;
            if (a.Social.ApplicationDay != day) { a.Social.ApplicationDay = day; a.Social.DailyApplications = 0; }
            Require(a.Social.DailyApplications < Setting("ApplyFriendDailyLimit"), 20031030);
            a.Social.DailyApplications++;
            b.Social.Applications[aid] = now;
            b.SaveChecked(); a.SaveChecked();
            response.OperationTime = now;
            Push(bid, new NotifyAddApply { ApplyId = aid, CreateTime = now });
        });
        if (response.Code == 0) response.Code = 20031011;
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("OperationApplyFriendRequest")]
    public static void Operate(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<OperationApplyFriendRequest>();
        var response = new OperationApplyFriendResponse();
        response.Code = Execute(() =>
        {
            Player a = session.player, b = Target(session, request.TargetPlayerId);
            uint aid = Id(a), bid = Id(b), now = Now;
            Require(a.Social.Applications.ContainsKey(bid), 20031005);
            if (request.IsAccept)
            {
                Allowed(a, b);
                Require(!a.Social.Friends.ContainsKey(bid), 20031020);
                Require(a.Social.Friends.Count < Capacity(a), 20031006);
                Require(b.Social.Friends.Count < Capacity(b), 20031014);
                a.Social.Friends[bid] = now; b.Social.Friends[aid] = now;
                b.Social.Applications.Remove(aid);
            }
            else a.Social.Refused[bid] = now;
            a.Social.Applications.Remove(bid);
            b.SaveChecked(); a.SaveChecked();
            if (request.IsAccept) Push(bid, new NotifyAddFriend { PlayerId = aid, CreateTime = now });
            else Push(bid, new NotifyRefuseApply());
        });
        session.SendResponse(response, packet.Id);
    }

    private static void RemovePair(Player a, Player b)
    {
        uint aid = Id(a), bid = Id(b);
        a.Social.Friends.Remove(bid); b.Social.Friends.Remove(aid);
        a.Social.Remarks.Remove(bid); b.Social.Remarks.Remove(aid);
        a.Social.Applications.Remove(bid); b.Social.Applications.Remove(aid);
    }
    [RequestPacketHandler("DeleteFriendRequest")]
    public static void Delete(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<DeleteFriendRequest>();
        var response = new DeleteFriendResponse();
        response.Code = Execute(() =>
        {
            Require(request.Ids is { Count: > 0 }, 20031010);
            Player a = session.player;
            var targets = request.Ids.Distinct().Select(id => Target(session, id)).ToList();
            Require(targets.All(b => a.Social.Friends.ContainsKey(Id(b))), 20031021);
            foreach (Player b in targets)
            {
                RemovePair(a, b); a.Social.Deleted[Id(b)] = Now;
                b.SaveChecked();
            }
            a.SaveChecked();
            foreach (Player b in targets) Push(Id(b), new NotifyDeleteFriend { PlayerId = Id(a) });
        });
        if (response.Code == 0) response.Code = 20031012;
        session.SendResponse(response, packet.Id);
    }
    [RequestPacketHandler("FriendRemarkRequest")]
    public static void Remark(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<FriendRemarkRequest>();
        var response = new FriendRemarkResponse { Code = Execute(() =>
        {
            _ = Target(session, request.Id);
            Require(session.player.Social.Friends.ContainsKey(request.Id), 20031021);
            Require(request.Name is not null && request.Name.EnumerateRunes().Count() <= RemarkLimit.Value && !request.Name.Any(char.IsControl), 20031022);
            if (request.Name!.Length == 0) session.player.Social.Remarks.Remove(request.Id);
            else session.player.Social.Remarks[request.Id] = request.Name;
            session.player.SaveChecked();
        }) };
        session.SendResponse(response, packet.Id);
    }
    [RequestPacketHandler("BlockPlayerRequest")]
    public static void Block(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<BlockPlayerRequest>();
        var response = new BlockPlayerResponse { Code = Execute(() =>
        {
            Player a = session.player, b = Target(session, request.PlayerId);
            Require(!a.Social.Blocked.ContainsKey(Id(b)), 20031025);
            Require(a.Social.Blocked.Count < Setting("FriendMaxBlacklistCount"), 20031026);
            RemovePair(a, b); a.Social.Blocked[Id(b)] = Now;
            b.SaveChecked(); a.SaveChecked();
            Push(Id(b), new NotifyBlock { PlayerId = Id(a) });
        }) };
        session.SendResponse(response, packet.Id);
    }
    [RequestPacketHandler("RemoveBlacklistRequest")]
    public static void Unblock(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<RemoveBlacklistRequest>();
        var response = new RemoveBlacklistResponse { Code = Execute(() =>
        {
            _ = Target(session, request.PlayerId);
            Require(session.player.Social.Blocked.Remove(request.PlayerId), 20031028);
            session.player.SaveChecked();
        }) };
        session.SendResponse(response, packet.Id);
    }
    [RequestPacketHandler("GetSpecifyPlayerInfoRequest")]
    public static void Specify(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<GetSpecifyPlayerInfoRequest>();
        var response = new GetSpecifyPlayerInfoResponse();
        response.Code = Execute(() =>
        {
            Player? p = request.Id == Id(session.player) ? session.player : Server.Instance.SessionFromUID(request.Id)?.player ?? Player.TryFromPlayerId(request.Id);
            Require(p is not null, 20031013);
            response.PlayerInfo = PlayerModule.ToPlayerInfo(p!);
        });
        session.SendResponse(response, packet.Id);
    }
    [RequestPacketHandler("GetRecommendPlayerListRequest")]
    public static void Recommend(Session session, Packet.Request packet)
    {
        var response = new GetRecommendPlayerListResponse();
        response.Code = Execute(() =>
        {
            Player a = session.player; uint now = Now;
            Require(a.Social.RecommendTime == 0 || (long)a.Social.RecommendTime + Setting("FriendRecommendIntervalTime") <= now, 20031007);
            // AscNet policy: stable recent-login ordering instead of retail's unavailable ranking algorithm.
            response.RecommendFriendsList = Player.collection.Find(p => p.PlayerData.Id != a.PlayerData.Id).ToEnumerable()
                .Where(p => !a.Social.Friends.ContainsKey(Id(p)) && !a.Social.Blocked.ContainsKey(Id(p)) && !p.Social.Blocked.ContainsKey(Id(a)))
                .OrderByDescending(p => p.PlayerData.LastLoginTime).ThenBy(p => p.PlayerData.Id)
                .Take(Setting("FriendMaxRecommendCount")).Select(PlayerModule.ToPlayerInfo).ToList();
            a.Social.RecommendTime = now; a.SaveChecked();
            response.RefreshTime = checked(now + (uint)Setting("FriendRecommendIntervalTime"));
        });
        session.SendResponse(response, packet.Id);
    }
}
