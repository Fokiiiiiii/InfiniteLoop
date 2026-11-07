using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.experiment;
using AscNet.Table.V2.share.reward;
using MessagePack;
namespace AscNet.GameServer.Handlers;
[MessagePackObject(true)] public sealed class ExperimentStarRewardRequest { public int Id { get; set; } public int StarNum { get; set; } }
[MessagePackObject(true)] public sealed class ExperimentStarRewardResponse { public int Code { get; set; } public List<RewardGoods> RewardList { get; set; } = new(); }
internal static class ExperimentClaimsModule
{
    [RequestPacketHandler("ExperimentStarRewardRequest")]
    public static void Claim(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<ExperimentStarRewardRequest>(); var response = new ExperimentStarRewardResponse();
        var levels = TableReaderV2.Parse<ExperimentLevelTable>();
        var rows = TableReaderV2.Parse<ExperimentClaimTable>().Where(x => x.LevelId == request.Id).ToList();
        ExperimentClaimTable? row = rows.FirstOrDefault(x => x.StarNum == request.StarNum);
        if (!levels.Any(x => x.Id == request.Id)) response.Code = 20051002;
        else if (rows.Count == 0) response.Code = 20051004;
        else if (row is null) response.Code = 20051007;
        else if (session.stage.ExperimentStarClaims.TryGetValue(request.Id, out var claimed) && claimed.Contains(request.StarNum)) response.Code = 20051008;
        else if (!session.stage.Stages.TryGetValue(row.StageId, out StageDatum? stage) || !stage.Passed) response.Code = 20051006;
        else if (TreasureModule.CountChapterStars(session, [row.StageId]) < request.StarNum) response.Code = 20051009;
        else
        {
            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(row.RewardId);
            if (goods.Count == 0) response.Code = 20051005;
            else
            {
                RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist([new RewardGrant($"experiment-star:{request.Id}:{request.StarNum}", goods)], session);
                if (!session.stage.ExperimentStarClaims.TryGetValue(request.Id, out var stars)) session.stage.ExperimentStarClaims[request.Id] = stars = new();
                stars.Add(request.StarNum); session.stage.SaveChecked(); result.SendPushes(session); response.RewardList = result.RewardGoods;
            }
        }
        session.SendResponse(response, packet.Id);
    }
}
