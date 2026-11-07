using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.task;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public class GetWeeklyActivenessRewardResponse
{
    public int Code { get; set; }
    public List<RewardGoods> RewardGoodsList { get; set; } = new();
    public List<int> WeeklyTaskActivenessProgress { get; set; } = new();
}

[MessagePackObject(true)]
public class SetNewPlayerTaskActiveUiRequest
{
    public int Group { get; set; }
}

[MessagePackObject(true)]
public class SetNewPlayerTaskActiveUiResponse
{
    public int Code { get; set; }
    public int Result { get; set; }
}

internal static partial class TaskModule
{
    public static List<int> BuildFinishedTaskIds(Session session)
    {
        EnsureMissionResets(session);
        return session.stage.FinishedTasks.Concat(session.player.MissionProgress.ClaimedTaskIds).Distinct().ToList();
    }

    [RequestPacketHandler("GetWeeklyActivenessRewardRequest")]
    public static void GetWeeklyActivenessRewardRequestHandler(Session session, Packet.Request packet)
    {
        EnsureMissionResets(session);
        var response = new GetWeeklyActivenessRewardResponse();
        var table = TableReaderV2.Parse<CurrentTaskActivenessTable>().FirstOrDefault(row => row.Type == 5);
        var claimed = session.player.MissionProgress.WeeklyTaskActivenessProgress;
        var weeklyIds = TableReaderV2.Parse<TaskTable>().Where(row => row.Tag == 1).Select(row => row.Id).ToHashSet();
        int points = BuildFinishedTaskIds(session).Count(weeklyIds.Contains);
        var eligible = table?.Activeness.Select((value, index) => (value, index))
            .Where(entry => entry.value <= points && !claimed.Contains(entry.value)).ToList();
        if (table is null)
            response.Code = 20026031;
        else if (eligible is null || eligible.Count == 0)
            response.Code = table.Activeness.All(claimed.Contains) ? 20026032 : 20026034;
        else if (eligible.Any(entry => entry.index >= table.RewardId.Count || RewardHandler.GetRewardGoods(table.RewardId[entry.index]).Count == 0))
            response.Code = 20026035;
        else
        {
            var applications = eligible.Select(entry => RewardHandler.ApplyRewards(RewardHandler.GetRewardGoods(table.RewardId[entry.index]), session)).ToList();
            claimed.AddRange(eligible.Select(entry => entry.value));
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            foreach (var application in applications) application.SendPushes(session);
            response.RewardGoodsList = applications.SelectMany(application => application.RewardGoods).ToList();
        }
        response.WeeklyTaskActivenessProgress = claimed.ToList();
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("SetNewPlayerTaskActiveUiRequest")]
    public static void SetNewPlayerTaskActiveUiRequestHandler(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<SetNewPlayerTaskActiveUiRequest>();
        bool valid = TableReaderV2.Parse<NewPlayerTaskGroupTable>().Any(row => row.Id == request.Group);
        if (valid)
        {
            session.player.PlayerData.NewPlayerTaskActiveUi |= 1 << request.Group;
            session.player.Save();
        }
        session.SendResponse(new SetNewPlayerTaskActiveUiResponse
        {
            Code = valid ? 0 : 20026004,
            Result = session.player.PlayerData.NewPlayerTaskActiveUi
        }, packet.Id);
    }
}
