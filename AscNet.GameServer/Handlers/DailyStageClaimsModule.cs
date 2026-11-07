using AscNet.Common.MsgPack;
using MessagePack;
namespace AscNet.GameServer.Handlers;
[MessagePackObject(true)] public sealed class ReceiveDailyRewardRequest { public int DailySectionId { get; set; } }
[MessagePackObject(true)] public sealed class ReceiveDailyRewardResponse { public int Code { get; set; } public List<RewardGoods> RewardGoodsList { get; set; } = new(); }
internal static class DailyStageClaimsModule
{
    [RequestPacketHandler("ReceiveDailyRewardRequest")]
    public static void Claim(Session session, Packet.Request packet)
    {
        _ = packet.Deserialize<ReceiveDailyRewardRequest>();
        // DailySection claim requirements/reward mapping are absent from installed daily tables.
        session.SendResponse(new ReceiveDailyRewardResponse { Code = 20020003 }, packet.Id);
    }
}
