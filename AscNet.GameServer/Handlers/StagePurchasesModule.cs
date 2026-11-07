using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.prequel;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public sealed class BuyMainLineChallengeCountRequest { public int StageId { get; set; } }
[MessagePackObject(true)]
public sealed class BuyMainLineChallengeCountResponse { public int Code { get; set; } }
[MessagePackObject(true)]
public sealed class UnlockPrequelChallengeRequest
{
    public int CoverId { get; set; }
    public int ChallengeId { get; set; }
}
[MessagePackObject(true)]
public sealed class UnlockPrequelChallengeResponse { public int Code { get; set; } }

internal static class StagePurchasesModule
{
    [RequestPacketHandler("BuyMainLineChallengeCountRequest")]
    public static void Buy(Session session, Packet.Request packet)
    {
        int stageId = packet.Deserialize<BuyMainLineChallengeCountRequest>().StageId;
        StageTable? stage = TableReaderV2.Parse<StageTable>().FirstOrDefault(row => row.StageId == stageId);
        // EN's Stage table authors no BuyChallengeCount or BuyChallengeCost values. An absent
        // purchase definition is not permission to grant attempts or charge an invented price.
        session.SendResponse(new BuyMainLineChallengeCountResponse { Code = stage is null ? 20003161 : 20003020 }, packet.Id);
    }

    [RequestPacketHandler("UnlockPrequelChallengeRequest")]
    public static void Unlock(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<UnlockPrequelChallengeRequest>();
        CoverTable? cover = TableReaderV2.Parse<CoverTable>().FirstOrDefault(row => row.CoverId == request.CoverId);
        // Cover.tsv authors one ChallengeStage column, which the table generator emits as a scalar,
        // so the only valid one-based challenge index is 1.
        int code = cover is null ? 20003095
            : request.ChallengeId != 1 ? 20003096
            : 20003097;
        // The installed Cover schema has ChallengeStage but no ChallengeConsumeItem/Count.
        // Keep valid challenges locked rather than inventing the obsolete unlock price.
        session.SendResponse(new UnlockPrequelChallengeResponse { Code = code }, packet.Id);
    }
}
