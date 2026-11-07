using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.extrachapter;
using AscNet.Table.V2.share.fuben.shortstory;
using AscNet.Table.V2.share.reward;
using MessagePack;
namespace AscNet.GameServer.Handlers;
[MessagePackObject(true)] public sealed class ChapterExtraTreasureRewardRequest { public int TreasureId { get; set; } }
[MessagePackObject(true)] public sealed class ChapterExtraTreasureRewardResponse { public int Code { get; set; } public List<RewardGoods> RewardGoods { get; set; } = new(); }
[MessagePackObject(true)] public sealed class ShortStoryTreasureRewardRequest { public int TreasureId { get; set; } }
[MessagePackObject(true)] public sealed class ShortStoryTreasureRewardResponse { public int Code { get; set; } public List<RewardGoods> RewardGoods { get; set; } = new(); }
internal static class StageChapterClaimsModule
{
    [RequestPacketHandler("ChapterExtraTreasureRewardRequest")]
    public static void Extra(Session session, Packet.Request packet)
    {
        int id = packet.Deserialize<ChapterExtraTreasureRewardRequest>().TreasureId;
        ChapterExtraClaimTable? row = TableReaderV2.Parse<ChapterExtraClaimTable>().FirstOrDefault(x => x.TreasureId == id);
        var result = Claim(session, id, row?.RequireStar, row?.RewardId, row?.StageIds, session.player.ExtraChapterTreasures, $"extra-chapter-treasure:{id}");
        session.SendResponse(new ChapterExtraTreasureRewardResponse { Code = result.Code, RewardGoods = result.Goods }, packet.Id);
    }
    [RequestPacketHandler("ShortStoryTreasureRewardRequest")]
    public static void Short(Session session, Packet.Request packet)
    {
        int id = packet.Deserialize<ShortStoryTreasureRewardRequest>().TreasureId;
        ShortStoryClaimTable? row = TableReaderV2.Parse<ShortStoryClaimTable>().FirstOrDefault(x => x.TreasureId == id);
        var result = Claim(session, id, row?.RequireStar, row?.RewardId, row?.StageIds, session.player.ShortStoryTreasures, $"short-story-treasure:{id}");
        session.SendResponse(new ShortStoryTreasureRewardResponse { Code = result.Code, RewardGoods = result.Goods }, packet.Id);
    }
    private static (int Code, List<RewardGoods> Goods) Claim(Session session, int id, int? stars, int? reward, IEnumerable<int>? stages, List<int> claimed, string claimKey)
    {
        if (stars is null || reward is null || stages is null) return (20003008, new());
        if (claimed.Contains(id)) return (20003010, new());
        if (TreasureModule.CountChapterStars(session, stages) < stars.Value) return (20003009, new());
        List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(reward.Value);
        if (goods.Count == 0) return (20003008, new());
        RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist([new RewardGrant(claimKey, goods)], session);
        claimed.Add(id); session.player.SaveChecked(); result.SendPushes(session);
        return (0, result.RewardGoods);
    }
}
