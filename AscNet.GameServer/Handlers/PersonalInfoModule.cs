using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.config;

namespace AscNet.GameServer.Handlers;

internal static class PersonalInfoModule
{
    internal static int Write(Player player, string text, long now)
    {
        if (!TextValid(text) || player.Profile.Diaries.Count >= ProfileShowcaseModule.Policy("DiaryCount")) return 5;
        player.Profile.NextDiaryId = Math.Max(player.Profile.NextDiaryId, player.Profile.Diaries.Select(d => d.Id).DefaultIfEmpty().Max()) + 1;
        player.Profile.Diaries.Add(new ProfileDiary { Id = player.Profile.NextDiaryId, PlayerId = player.PlayerData.Id, Name = player.PlayerData.Name, CurrHeadPortraitId = player.PlayerData.CurrHeadPortraitId, Content = text, CreateTime = now });
        return 0;
    }
    private static bool TextValid(string? text) => !string.IsNullOrWhiteSpace(text) && text.EnumerateRunes().Count() <= ProfileShowcaseModule.Policy("TextLength") && !text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t');
    private static bool Allowed(Player actor, Player target) => !actor.Social.Blocked.ContainsKey(checked((uint)target.PlayerData.Id)) && !target.Social.Blocked.ContainsKey(checked((uint)actor.PlayerData.Id));
    internal static int Leave(Player actor, Player target, PersonalInfoRequest request, long now)
    {
        var diary = target.Profile.Diaries.FirstOrDefault(d => d.Id == request.DailyId);
        if (diary is null) return 5;
        if (diary.IsBanMsg || !Allowed(actor, target)) return 20037002;
        if (!TextValid(request.LeaveMsg) || diary.Msgs.Count >= ProfileShowcaseModule.Policy("MessageCount")) return 5;
        target.Profile.NextMessageId = Math.Max(target.Profile.NextMessageId, target.Profile.Diaries.SelectMany(d => d.Msgs).Select(m => m.Id).DefaultIfEmpty().Max()) + 1;
        diary.Msgs.Add(new ProfileMessage { Id = target.Profile.NextMessageId, DailyId = diary.Id, PlayerId = actor.PlayerData.Id, Name = actor.PlayerData.Name, CurrHeadPortraitId = actor.PlayerData.CurrHeadPortraitId, Content = request.LeaveMsg, LeaveMsgTime = now });
        return 0;
    }
    internal static int Like(Player actor, Player target, long diaryId, long now)
    {
        var diary = target.Profile.Diaries.FirstOrDefault(d => d.Id == diaryId);
        if (diary is null || actor.PlayerData.Id == target.PlayerData.Id || !Allowed(actor, target)) return 20038001;
        if (diary.LikedPlayerIds.Remove(actor.PlayerData.Id)) return 20038003;
        int reset = int.Parse(TableReaderV2.Parse<ConfigTable>().Single(x => x.Key == "DailyResetTimestamp").Value);
        long period = (long)Math.Floor((now - reset) / 86400d);
        if (actor.Profile.LikePeriod != period) { actor.Profile.LikePeriod = period; actor.Profile.LikesGiven = 0; }
        if (actor.Profile.LikesGiven >= ProfileShowcaseModule.Policy("DailyLikeLimit")) return 20038001;
        actor.Profile.LikesGiven++; diary.LikedPlayerIds.Add(actor.PlayerData.Id); return 20038002;
    }
    internal static int DeleteDiary(Player player, long id) { player.Profile.Diaries.RemoveAll(d => d.Id == id); return 0; }
    internal static int DeleteMessage(Player player, long diaryId, long messageId)
    {
        var diary = player.Profile.Diaries.FirstOrDefault(d => d.Id == diaryId);
        if (diary is null) return 5;
        diary.Msgs.RemoveAll(m => m.Id == messageId); return 0;
    }
    internal static int Ban(Player player, long diaryId)
    {
        var diary = player.Profile.Diaries.FirstOrDefault(d => d.Id == diaryId);
        if (diary is null) return 20038004;
        diary.IsBanMsg = true; return 0;
    }
    [RequestPacketHandler("ViewPersonalInfoRequest")]
    public static void ViewHandler(Session s, Packet.Request p)
    {
        var target = ProfileModule.Target(s, p.Deserialize<PersonalInfoRequest>().viewId);
        s.SendResponse(new ViewPersonalInfoResponse { Code = target is null ? 20037001 : 0, data = target is null ? null : ProfileModule.Detail(target, ProfileModule.Characters(s, target), s.player) }, p.Id);
    }
    [RequestPacketHandler("WriteDailyRequest")]
    public static void WriteHandler(Session s, Packet.Request p) { int code = Write(s.player, p.Deserialize<PersonalInfoRequest>().DailyContent, DateTimeOffset.UtcNow.ToUnixTimeSeconds()); if (code == 0) s.player.Save(); ProfileModule.Respond(s, p, code); }
    [RequestPacketHandler("GetDailysRequest")]
    public static void GetHandler(Session s, Packet.Request p)
    {
        var request = p.Deserialize<PersonalInfoRequest>(); var target = ProfileModule.Target(s, request.PlayerId);
        int size = ProfileShowcaseModule.Policy("PageSize");
        int code = target is null ? 20037001 : request.PageNum < 1 || request.PageNum > int.MaxValue / size ? 5 : !Allowed(s.player, target) ? 20037002 : 0;
        s.SendResponse(new GetDailysResponse { Code = code, DailyList = code == 0 ? target!.Profile.Diaries.OrderByDescending(d => d.Id).Skip((request.PageNum - 1) * size).Take(size).ToList() : [] }, p.Id);
    }
    [RequestPacketHandler("LeaveMsgRequest")]
    public static void LeaveHandler(Session s, Packet.Request p)
    {
        var request = p.Deserialize<PersonalInfoRequest>(); var target = ProfileModule.Target(s, request.PlayerId);
        int code = target is null ? 20037001 : Leave(s.player, target, request, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (code == 0) target!.Save(); ProfileModule.Respond(s, p, code);
    }
    [RequestPacketHandler("LookOverLeaveMsgRequest")]
    public static void MessagesHandler(Session s, Packet.Request p)
    {
        var request = p.Deserialize<PersonalInfoRequest>(); var target = ProfileModule.Target(s, request.PlayerId); var diary = target?.Profile.Diaries.FirstOrDefault(d => d.Id == request.DailyId);
        int code = target is null ? 20037001 : !Allowed(s.player, target) ? 20037002 : diary is null ? 5 : 0;
        s.SendResponse(new LookOverLeaveMsgResponse { Code = code, MsgArray = code == 0 ? diary!.Msgs.ToList() : [] }, p.Id);
    }
    [RequestPacketHandler("GiveLikeRequest")]
    public static void LikeHandler(Session s, Packet.Request p)
    {
        var request = p.Deserialize<PersonalInfoRequest>(); var target = ProfileModule.Target(s, request.PlayerId);
        int code = target is null ? 20038001 : Like(s.player, target, request.DailyId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (code is 20038002 or 20038003) { target!.Save(); s.player.Save(); }
        ProfileModule.Respond(s, p, code);
    }
    [RequestPacketHandler("DeleteDailyRequest")]
    public static void DeleteHandler(Session s, Packet.Request p) { int code = DeleteDiary(s.player, p.Deserialize<PersonalInfoRequest>().DailyId); if (code == 0) s.player.Save(); ProfileModule.Respond(s, p, code); }
    [RequestPacketHandler("DeleteLeaveMsgRequest")]
    public static void DeleteMessageHandler(Session s, Packet.Request p) { var r = p.Deserialize<PersonalInfoRequest>(); int code = DeleteMessage(s.player, r.DailyId, r.MsgId); if (code == 0) s.player.Save(); ProfileModule.Respond(s, p, code); }
    [RequestPacketHandler("BanMsgRequest")]
    public static void BanHandler(Session s, Packet.Request p) { int code = Ban(s.player, p.Deserialize<PersonalInfoRequest>().DailyId); if (code == 0) s.player.Save(); ProfileModule.Respond(s, p, code); }
}
