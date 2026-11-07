using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.chat;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.report;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

#region MsgPackScheme
[MessagePackObject(true)]
public class SaveEmojiPackageIdRequest { public List<int>? OrderEmojiPackageIds { get; set; } }
[MessagePackObject(true)]
public class SaveEmojiPackageIdResponse { public int Code { get; set; } }

[MessagePackObject(true)]
public class GetGiftsRequest { public int GiftId { get; set; } }
[MessagePackObject(true)]
public class GetGiftsResponse
{
    public int Code { get; set; }
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public class GetAllGiftsRequest { }
[MessagePackObject(true)]
public class ChatGiftInfo
{
    public long FriendId { get; set; }
    public int GiftId { get; set; }
}
[MessagePackObject(true)]
public class GetAllGiftsResponse
{
    public int Code { get; set; }
    public List<ChatGiftInfo> GiftInfoList { get; set; } = [];
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public class ReportRequest
{
    public long PlayerId { get; set; }
    public string? PlayerName { get; set; }
    public int Entry { get; set; }
    public List<int>? Tags { get; set; }
    public string? Message { get; set; }
    public int PlayerLevel { get; set; }
    public string? ReportMessage { get; set; }
    public int ChatChannel { get; set; }
}
[MessagePackObject(true)]
public class ReportResponse { public int Code { get; set; } }

[MessagePackObject(true)]
public class ReportGuildRequest
{
    public long GuildId { get; set; }
    public int Entry { get; set; }
    public List<int>? Tags { get; set; }
    public string? Message { get; set; }
    public string? ReportMessage { get; set; }
}
[MessagePackObject(true)]
public class ReportGuildResponse { public int Code { get; set; } }

[MessagePackObject(true)]
public class GuildListScriptRequest { }
[MessagePackObject(true)]
public class GuildListScriptResponse
{
    public int Code { get; set; }
    public List<string> Scripts { get; set; } = [];
    public bool AutoChat { get; set; }
}
#endregion

internal static class ChatSocialModule
{
    // EN XChatManager/XReportManager: client error codes (CodeText.tsv).
    private const int ParamsError = 5;
    private const int CannotSendGift = 20033001, InvalidGift = 20033003, GiftQuotaReached = 20033005, GiftAlreadySentToday = 20033008, InvalidEmojiPackageIds = 20033014;
    private const int InvalidReportTag = 20139001, ReportTooSoon = 20139002, ReportQuotaReached = 20139003, ReportedPlayerMissing = 20139004, ReportedGuildMissing = 20139005;
    // XReportConfigs.EnterType.Guild: XUiReport routes Guild entries to ReportGuildRequest and everything else to ReportRequest.
    private const int GuildReportEntry = 2;
    private const int GuildScriptNotMember = 20063125;

    private sealed class Failure(int code) : Exception { public int Code { get; } = code; }
    private static void Require(bool valid, int code) { if (!valid) throw new Failure(code); }

    private static readonly Lazy<Dictionary<string, string>> Settings = new(() => TableReaderV2.Parse<ConfigTable>().ToDictionary(r => r.Key, r => r.Value));
    private static int Setting(string key) => int.Parse(Settings.Value[key]);
    private static readonly Lazy<Dictionary<int, ReportEntryTable>> Entries = new(() => TableReaderV2.Parse<ReportEntryTable>().ToDictionary(r => r.Id));
    private static readonly Lazy<Dictionary<int, ReportTagTable>> Tags = new(() => TableReaderV2.Parse<ReportTagTable>().ToDictionary(r => r.Id));
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static long Day(long now) => GuildModule.DailyPeriod(DateTimeOffset.FromUnixTimeSeconds(now));
    private static long DayStart(long day) => day * 86400 + Setting("DailyResetTimestamp");

    private static void Run<TResponse>(Session session, Packet.Request packet, TResponse response, Action action, Action<int> setCode) where TResponse : new()
    {
        try { lock (GuildModule.MembershipLock) action(); setCode(0); }
        catch (Failure failure) { setCode(failure.Code); }
        catch (MessagePackSerializationException) { setCode(ParamsError); }
        session.SendResponse(response, packet.Id);
    }

    #region Emoji pack order
    // A pack is owned when the player can use at least one emoji of it; pack 0 is the default pack and is never ordered (XUiPanelEmojiPackSetting lists Order > 0 only).
    private static List<int> OwnedPacks(Session session)
    {
        long now = Now;
        HashSet<int> packs = TableReaderV2.Parse<EmojiPackTable>().Select(r => r.Id).Where(id => id != 0).ToHashSet();
        return TableReaderV2.Parse<EmojiTable>()
            .Where(emoji => emoji.PackageId is > 0 && packs.Contains(emoji.PackageId.Value) && session.character.CanUseChatEmoji(emoji.Id, now))
            .Select(emoji => emoji.PackageId!.Value).Distinct().ToList();
    }

    internal static List<int> EmojiPackOrder(Session session)
    {
        List<int> owned = OwnedPacks(session);
        List<int> saved = session.player.ChatSocial.EmojiPackOrder.Where(owned.Contains).ToList();
        Dictionary<int, int> tableOrder = TableReaderV2.Parse<EmojiPackTable>().ToDictionary(r => r.Id, r => r.Order);
        return saved.Concat(owned.Except(saved).OrderBy(id => tableOrder[id]).ThenBy(id => id)).ToList();
    }

    [RequestPacketHandler("SaveEmojiPackageIdRequest")]
    public static void SaveEmojiPackageIdRequestHandler(Session session, Packet.Request packet)
    {
        SaveEmojiPackageIdResponse response = new();
        Run(session, packet, response, () =>
        {
            // The client encodes an empty Lua table as nil.
            List<int> order = packet.Deserialize<SaveEmojiPackageIdRequest>().OrderEmojiPackageIds ?? [];
            List<int> owned = OwnedPacks(session);
            Require(order.Distinct().Count() == order.Count && order.All(owned.Contains), InvalidEmojiPackageIds);
            session.player.ChatSocial.EmojiPackOrder = order;
            session.player.SaveChecked();
        }, code => response.Code = code);
    }
    #endregion

    #region Chat gifts
    // The gift is a box of Cogs (EN Text GiftMoney*; XItemManager.ItemId.Coin).
    private static NotifyChatMessage GiftMessage(Player sender, Character? senderCharacter, uint receiverId, ChatGiftRecord gift, int messageId)
    {
        Guild? guild = GuildModule.FindMembership(sender.PlayerData.Id);
        long now = Now;
        return new()
        {
            MessageId = messageId,
            ChannelType = ChatChannelType.Private,
            MsgType = ChatMsgType.Gift,
            SenderId = sender.PlayerData.Id,
            TargetId = receiverId,
            Icon = (int)sender.PlayerData.CurrHeadPortraitId,
            HeadFrameId = (int)sender.PlayerData.CurrHeadFrameId,
            CreateTime = gift.CreateTime,
            NickName = sender.PlayerData.Name,
            NameplateId = senderCharacter?.GetCurrentWearNameplate(now) ?? 0,
            Content = string.Empty,
            GiftId = gift.GiftId,
            GiftCount = gift.Count,
            GiftStatus = (ChatGiftState)gift.Status,
            CurrMedalId = (int)sender.PlayerData.CurrMedalId,
            GuildRankLevel = guild is null ? 9 : GuildModule.Rank(guild, sender.PlayerData.Id),
            GuildName = guild?.Name ?? string.Empty,
            MentorType = 1,
            ChatBoardId = (int)sender.PlayerData.CurrentChatBoardId
        };
    }

    private static Character? CharacterOf(long uid) =>
        Server.Instance.SessionFromUID((uint)uid)?.character ?? Character.collection.Find(c => c.Uid == uid).FirstOrDefault();

    private static bool Expired(ChatGiftRecord gift, long now) =>
        // XChatManager.UpdateGiftStatus: a gift older than the previous daily reset becomes Fetched.
        gift.Status == (int)ChatGiftState.WaitReceive && gift.CreateTime < DayStart(Day(now)) - 86400;

    // Called from SendChatRequest for MsgType.Gift. Returns the SendChatResponse code.
    internal static int SendGifts(Session session, ChatData chat, List<long>? targetIds)
    {
        try
        {
            lock (GuildModule.MembershipLock)
            {
                Require(chat.ChannelType == ChatChannelType.Private, CannotSendGift);
                List<uint> targets = (targetIds ?? []).Select(id => checked((uint)id)).Distinct().ToList();
                Require(targets.Count > 0 && targets.All(session.player.Social.Friends.ContainsKey), CannotSendGift);
                long now = Now, day = Day(now);
                ChatSocialState own = session.player.ChatSocial;
                List<uint> sendable = targets.Where(id => own.GiftSentDays.GetValueOrDefault(id, -1) != day).ToList();
                Require(sendable.Count > 0, GiftAlreadySentToday);
                int min = Setting("ChatGiftCountMin"), max = Setting("ChatGiftCountMax");
                List<(uint Id, ChatGiftRecord Gift)> sent = [];
                foreach (uint id in sendable)
                {
                    Player receiver = Server.Instance.SessionFromUID(id)?.player ?? Player.TryFromPlayerId(id) ?? throw new Failure(CannotSendGift);
                    ChatSocialState inbox = receiver.ChatSocial;
                    ChatGiftRecord gift = new()
                    {
                        GiftId = ++inbox.LastGiftId, SenderId = (uint)session.player.PlayerData.Id, ItemId = Inventory.Coin,
                        Count = Random.Shared.Next(min, max + 1), CreateTime = now, Status = (int)ChatGiftState.WaitReceive
                    };
                    inbox.Gifts.Add(gift);
                    receiver.SaveChecked();
                    own.GiftSentDays[id] = day;
                    session.player.SaveChecked();
                    sent.Add((id, gift));
                    Server.Instance.SessionFromUID(id)?.SendPush(GiftMessage(session.player, session.character, id, gift, gift.GiftId));
                }
                foreach ((uint id, ChatGiftRecord gift) in sent)
                    session.SendPush(GiftMessage(session.player, session.character, id, gift, 0));
            }
            return 0;
        }
        catch (Failure failure) { return failure.Code; }
    }

    internal static List<NotifyChatMessage> OfflineMessages(Session session, int afterMessageId)
    {
        lock (GuildModule.MembershipLock)
        {
            long now = Now;
            return session.player.ChatSocial.Gifts
                .Where(gift => gift.GiftId > afterMessageId && gift.Status == (int)ChatGiftState.WaitReceive && !Expired(gift, now))
                .OrderBy(gift => gift.GiftId)
                .Select(gift => GiftMessage(Player.TryFromPlayerId(gift.SenderId) ?? throw new InvalidDataException($"Gift sender {gift.SenderId} is missing."),
                    CharacterOf(gift.SenderId), (uint)session.player.PlayerData.Id, gift, gift.GiftId))
                .ToList();
        }
    }

    private static RewardGoods ToRewardGoods(ChatGiftRecord gift) => new() { RewardType = (int)RewardType.Item, TemplateId = gift.ItemId, Count = gift.Count };

    // Claims the given gifts atomically; each gift has its own claim key so a retry after a partial failure never grants twice.
    private static void Claim(Session session, List<ChatGiftRecord> gifts)
    {
        ChatSocialState state = session.player.ChatSocial;
        long now = Now, day = Day(now);
        if (state.GiftReceiveDay != day) { state.GiftReceiveDay = day; state.GiftReceivedToday = 0; }
        RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(
            gifts.Select(gift => new RewardGrant($"chatgift:{gift.GiftId}", [new RewardGoodsTable { TemplateId = gift.ItemId, Count = gift.Count }])).ToList(), session);
        foreach (ChatGiftRecord gift in gifts) gift.Status = (int)ChatGiftState.Received;
        state.GiftReceivedToday += gifts.Count;
        session.player.SaveChecked();
        result.SendPushes(session);
    }

    private static void MarkExpired(Session session, long now)
    {
        bool changed = false;
        foreach (ChatGiftRecord gift in session.player.ChatSocial.Gifts.Where(gift => Expired(gift, now))) { gift.Status = (int)ChatGiftState.Fetched; changed = true; }
        if (changed) session.player.SaveChecked();
    }

    private static int Remaining(ChatSocialState state, long day) =>
        Setting("GetGiftMaxCountEveryDay") - (state.GiftReceiveDay == day ? state.GiftReceivedToday : 0);

    [RequestPacketHandler("GetGiftsRequest")]
    public static void GetGiftsRequestHandler(Session session, Packet.Request packet)
    {
        GetGiftsResponse response = new();
        Run(session, packet, response, () =>
        {
            GetGiftsRequest request = packet.Deserialize<GetGiftsRequest>();
            long now = Now;
            MarkExpired(session, now);
            ChatGiftRecord gift = session.player.ChatSocial.Gifts.FirstOrDefault(g => g.GiftId == request.GiftId && g.Status == (int)ChatGiftState.WaitReceive)
                ?? throw new Failure(InvalidGift);
            Require(Remaining(session.player.ChatSocial, Day(now)) > 0, GiftQuotaReached);
            Claim(session, [gift]);
            response.RewardGoodsList = [ToRewardGoods(gift)];
        }, code => response.Code = code);
    }

    [RequestPacketHandler("GetAllGiftsRequest")]
    public static void GetAllGiftsRequestHandler(Session session, Packet.Request packet)
    {
        GetAllGiftsResponse response = new();
        Run(session, packet, response, () =>
        {
            long now = Now;
            MarkExpired(session, now);
            List<ChatGiftRecord> waiting = session.player.ChatSocial.Gifts.Where(g => g.Status == (int)ChatGiftState.WaitReceive).OrderBy(g => g.GiftId).ToList();
            Require(waiting.Count > 0, InvalidGift);
            int remaining = Remaining(session.player.ChatSocial, Day(now));
            Require(remaining > 0, GiftQuotaReached);
            List<ChatGiftRecord> claimed = waiting.Take(remaining).ToList();
            Claim(session, claimed);
            // The client pairs GiftInfoList[i] with RewardGoodsList[i].
            response.GiftInfoList = claimed.Select(g => new ChatGiftInfo { FriendId = g.SenderId, GiftId = g.GiftId }).ToList();
            response.RewardGoodsList = claimed.Select(ToRewardGoods).ToList();
        }, code => response.Code = code);
    }
    #endregion

    #region Reports
    // XUiReport: Guild/Player-style entries (TagLevel 1) pick main > sub [> detail]; chat-style entries (TagLevel > 1) start at the sub tier.
    private static bool ValidTags(ReportEntryTable entry, List<int>? tags)
    {
        bool threeTier = entry.TagLevel == 1;
        if (tags is null || tags.Count < (threeTier ? 2 : 1) || tags.Count > (threeTier ? 3 : 2)) return false;
        if (!entry.TagIds.Any(id => id == tags[0])) return false;
        for (int i = 0; i < tags.Count; i++)
        {
            if (!Tags.Value.TryGetValue(tags[i], out ReportTagTable? tag)) return false;
            if (i > 0 && tag.ParentId != tags[i - 1]) return false;
        }
        return true;
    }

    private static void CheckReportAllowed(ChatSocialState state, long now)
    {
        Require(now - state.LastReportTime >= Setting("ReportInterval"), ReportTooSoon);
        Require(CurrentReportTimes(state, now) < Setting("ReportDailyMaxTimes"), ReportQuotaReached);
    }

    private static int CurrentReportTimes(ChatSocialState state, long now) => state.ReportDay == Day(now) ? state.ReportTimes : 0;

    private static void Record(Session session, ChatReportRecord record)
    {
        ChatSocialState state = session.player.ChatSocial;
        record.Time = Now;
        state.ReportTimes = CurrentReportTimes(state, record.Time) + 1;
        state.ReportDay = Day(record.Time);
        state.LastReportTime = record.Time;
        state.Reports.Add(record);
        session.player.SaveChecked();
        session.SendPush(BuildReportData(session.player));
    }

    internal static NotifyPlayerReportData BuildReportData(Player player) => new()
    {
        ReportData = new NotifyPlayerReportData.NotifyPlayerReportDataReportData
        {
            ReportTimes = CurrentReportTimes(player.ChatSocial, Now),
            LastReportTime = checked((int)player.ChatSocial.LastReportTime)
        }
    };

    [RequestPacketHandler("ReportRequest")]
    public static void ReportRequestHandler(Session session, Packet.Request packet)
    {
        ReportResponse response = new();
        Run(session, packet, response, () =>
        {
            ReportRequest request = packet.Deserialize<ReportRequest>();
            Require(Entries.Value.TryGetValue(request.Entry, out ReportEntryTable? entry) && request.Entry != GuildReportEntry && ValidTags(entry, request.Tags), InvalidReportTag);
            CheckReportAllowed(session.player.ChatSocial, Now);
            Player target = Server.Instance.SessionFromUID((uint)request.PlayerId)?.player ?? Player.TryFromPlayerId(request.PlayerId) ?? throw new Failure(ReportedPlayerMissing);
            Record(session, new ChatReportRecord
            {
                Kind = 0, TargetId = target.PlayerData.Id, TargetName = target.PlayerData.Name, TargetLevel = checked((int)target.PlayerData.Level),
                Entry = request.Entry, Tags = request.Tags!, Message = request.Message ?? string.Empty,
                ReportMessage = request.ReportMessage ?? string.Empty, ChatChannel = request.ChatChannel
            });
        }, code => response.Code = code);
    }

    [RequestPacketHandler("ReportGuildRequest")]
    public static void ReportGuildRequestHandler(Session session, Packet.Request packet)
    {
        ReportGuildResponse response = new();
        Run(session, packet, response, () =>
        {
            ReportGuildRequest request = packet.Deserialize<ReportGuildRequest>();
            Require(request.Entry == GuildReportEntry && Entries.Value.TryGetValue(request.Entry, out ReportEntryTable? entry) && ValidTags(entry, request.Tags), InvalidReportTag);
            CheckReportAllowed(session.player.ChatSocial, Now);
            Guild guild = request.GuildId is > 0 and <= uint.MaxValue ? Guild.FindById((uint)request.GuildId) ?? throw new Failure(ReportedGuildMissing) : throw new Failure(ReportedGuildMissing);
            Require(guild.Active, ReportedGuildMissing);
            Record(session, new ChatReportRecord
            {
                Kind = 1, TargetId = guild.Id, TargetName = guild.Name, Entry = request.Entry, Tags = request.Tags!,
                Message = request.Message ?? string.Empty, ReportMessage = request.ReportMessage ?? string.Empty
            });
        }, code => response.Code = code);
    }
    #endregion

    #region Guild scripts
    // The client lists scripts it saved with GuildChangeScriptRequest (personal, persisted in GuildState.Scripts); AutoChat stays client-owned.
    [RequestPacketHandler("GuildListScriptRequest")]
    public static void GuildListScriptRequestHandler(Session session, Packet.Request packet)
    {
        GuildListScriptResponse response = new();
        Run(session, packet, response, () =>
        {
            Require(GuildModule.FindMembership(session.player.PlayerData.Id) is not null, GuildScriptNotMember);
            response.Scripts = [.. session.player.GuildState.Scripts];
        }, code => response.Code = code);
    }
    #endregion
}
