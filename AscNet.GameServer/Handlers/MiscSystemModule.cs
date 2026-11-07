using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.functional;
using MessagePack;

namespace AscNet.GameServer.Handlers;

#pragma warning disable CS8618
[MessagePackObject(true)] public sealed class DormitoryDataRequest { }
// Fields are the ones XRpc.NotifyDormitoryData marks "from DormitoryDataRequest".
[MessagePackObject(true)]
public sealed class DormitoryDataResponse
{
    public int Code;
    public List<NotifyDormitoryData.NotifyDormitoryDataDormitory> DormitoryList = [];
    public List<NotifyDormitoryData.NotifyDormitoryDataFurniture> FurnitureList = [];
    public List<NotifyDormitoryData.NotifyDormitoryDataCharacter> CharacterList = [];
}

[MessagePackObject(true)] public sealed class FunctionalEntranceRedPointNumUpdateRequest { public int FunctionId; }
[MessagePackObject(true)] public sealed class FunctionalEntranceRedPointNumUpdateResponse { public int Code; }

[MessagePackObject(true)] public sealed class LimitedLoginVerifyRequest { public int QuizIdx; public int Answer; public int UseTime; }
[MessagePackObject(true)] public sealed class LimitedLoginVerifyResponse { public int Code; public bool IsCorrect; }
#pragma warning restore CS8618

internal static class MiscSystemModule
{
    private const int FunctionalEntranceConfigNotFound = 20421001;
    private const int LimitedLoginQuizIsNull = 20138001;

    [RequestPacketHandler("DormitoryDataRequest")]
    public static void DormitoryDataRequestHandler(Session session, Packet.Request packet)
    {
        NotifyDormitoryData data = DormModule.BuildLoginData(session);
        session.SendResponse(new DormitoryDataResponse
        {
            DormitoryList = data.DormitoryList,
            FurnitureList = data.FurnitureList,
            CharacterList = data.CharacterList
        }, packet.Id);
    }

    internal static int RedPointNum(int functionId) =>
        TableReaderV2.Parse<FunctionalEntranceConfigTable>().FirstOrDefault(row => row.FunctionId == functionId)?.RedPointNum ?? 0;

    // The client only issues this when the config RedPointNum > 0 and applies that same number locally.
    [RequestPacketHandler("FunctionalEntranceRedPointNumUpdateRequest")]
    public static void FunctionalEntranceRedPointNumUpdateRequestHandler(Session session, Packet.Request packet)
    {
        FunctionalEntranceRedPointNumUpdateRequest request = packet.Deserialize<FunctionalEntranceRedPointNumUpdateRequest>();
        int num = RedPointNum(request.FunctionId);
        if (num <= 0)
        {
            session.SendResponse(new FunctionalEntranceRedPointNumUpdateResponse { Code = FunctionalEntranceConfigNotFound }, packet.Id);
            return;
        }
        if (session.player.FunctionalEntranceRedPoints.GetValueOrDefault(request.FunctionId) != num)
        {
            session.player.FunctionalEntranceRedPoints[request.FunctionId] = num;
            session.player.Save();
        }
        session.SendResponse(new FunctionalEntranceRedPointNumUpdateResponse(), packet.Id);
    }

    // The quiz bank (LimitedLoginQuiz/LimitedLoginConst) is a server-only table absent from every client
    // dump, and NotifyLogin.LimitedLoginData is never populated, so no quiz can be pending: fail closed.
    [RequestPacketHandler("LimitedLoginVerifyRequest")]
    public static void LimitedLoginVerifyRequestHandler(Session session, Packet.Request packet)
    {
        packet.Deserialize<LimitedLoginVerifyRequest>();
        session.SendResponse(new LimitedLoginVerifyResponse { Code = LimitedLoginQuizIsNull }, packet.Id);
    }
}
