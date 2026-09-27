using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.punishaar;
using AscNet.Table.V2.share.task;

namespace AscNet.GameServer.Handlers;

internal static partial class TaskModule
{
    private static readonly Lazy<IReadOnlyList<PunishaarActivityTable>> PunishaarTaskActivities = new(() =>
        TableReaderV2.Parse<PunishaarActivityTable>().Where(a => a.TimeId > 0 && a.TaskGroupIds.Count > 0).ToArray());
    private static readonly Lazy<IReadOnlyList<TaskTimeLimitTable>> PunishaarTaskGroups = new(() =>
    {
        HashSet<int> ids = PunishaarTaskActivities.Value.SelectMany(a => a.TaskGroupIds).ToHashSet();
        return TableReaderV2.Parse<TaskTimeLimitTable>().Where(group => ids.Contains(group.Id)).ToArray();
    });
    private static readonly Lazy<IReadOnlyDictionary<int, ConditionTable>> PunishaarConditions = new(() =>
    {
        HashSet<int> ids = PunishaarTaskGroups.Value.SelectMany(group => group.TaskId).ToHashSet();
        HashSet<int> conditionIds = TaskRowsById.Value.Values.Where(task => ids.Contains(task.Id)).Select(task => task.Condition).ToHashSet();
        return TableReaderV2.Parse<ConditionTable>().Where(c => conditionIds.Contains(c.Id)).ToDictionary(c => c.Id);
    });

    internal static bool IsPunishaarTask(TaskTable task) => task.Type == 11 &&
        PunishaarTaskGroups.Value.Any(group => group.TaskId.Contains(task.Id));

    // 142001 [stageId]: stage cleared. 142002 [stageId, nodeType, N]: won nodes of that type in the best single run
    // (AscNet policy; endless rounds accumulate within a run).
    private static List<MissionTaskProgress> BuildPunishaarTaskProgress(Session session, Func<int, bool>? claimed = null)
    {
        claimed ??= session.player.MissionProgress.ClaimedTaskIds.Contains;
        // Same clock seam as the module, so a frozen activity window drives the task projection too.
        DateTimeOffset now = PunishaarModule.Clock();
        List<MissionTaskProgress> progress = [];
        foreach (PunishaarActivityTable activity in PunishaarTaskActivities.Value)
        {
            if (!ActivityScheduleService.IsOpen(activity.TimeId, now)) continue;
            bool own = session.player.Punishaar.ActivityId == activity.Id;
            foreach (TaskTimeLimitTable group in PunishaarTaskGroups.Value)
            {
                if (!activity.TaskGroupIds.Contains(group.Id) || group.TimeId is not int timeId || !ActivityScheduleService.IsOpen(timeId, now))
                    continue;
                foreach (int id in group.TaskId)
                {
                    if (!TaskRowsById.Value.TryGetValue(id, out TaskTable? task) || !IsPunishaarTask(task) || !IsTaskActive(task, now)
                        || !PunishaarConditions.Value.TryGetValue(task.Condition, out ConditionTable? condition))
                        continue;
                    int target = task.Result ?? 1;
                    int value = !own ? 0 : condition.Type switch
                    {
                        142001 when condition.Params.Count >= 1 => session.player.Punishaar.PassedStageIds.Contains(condition.Params[0]) ? 1 : 0,
                        142002 when condition.Params.Count >= 2 =>
                            session.player.Punishaar.BestWonNodeCounts.GetValueOrDefault(condition.Params[0] * 10 + condition.Params[1]),
                        _ => 0
                    };
                    progress.Add(new MissionTaskProgress(task.Id, condition.Id, Math.Min(value, target),
                        claimed(task.Id) ? TaskStateFinish : value >= target ? TaskStateAchieved : TaskStateActive));
                }
            }
        }
        return progress;
    }

    internal static void SendPunishaarTaskSync(Session session)
    {
        List<MissionTaskProgress> progress = BuildPunishaarTaskProgress(session);
        if (progress.Count > 0)
            session.SendPush(new NotifyTask { Tasks = new() { Tasks = progress.Select(ToSyncTask).ToList() } });
    }
}
