using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.miniactivity.fangkuai;

namespace AscNet.GameServer.Handlers;

internal static partial class TaskModule
{
    private static readonly Lazy<IReadOnlyList<FangKuaiActivityTable>> FangKuaiTaskActivities = new(() =>
        TableReaderV2.Parse<FangKuaiActivityTable>()
            .Where(activity => activity.TimeId > 0 && activity.TaskTimeLimitIds.Count > 0).ToArray());
    private static readonly Lazy<IReadOnlyList<TaskTimeLimitTable>> FangKuaiTaskGroups = new(() =>
    {
        HashSet<int> ids = FangKuaiTaskActivities.Value.SelectMany(activity => activity.TaskTimeLimitIds).ToHashSet();
        return TableReaderV2.Parse<TaskTimeLimitTable>().Where(group => ids.Contains(group.Id)).ToArray();
    });
    private static readonly Lazy<IReadOnlyDictionary<int, ConditionTable>> FangKuaiConditions = new(() =>
    {
        HashSet<int> ids = FangKuaiTaskGroups.Value.SelectMany(group => group.TaskId).ToHashSet();
        HashSet<int> conditionIds = TaskRowsById.Value.Values.Where(task => ids.Contains(task.Id))
            .Select(task => task.Condition).ToHashSet();
        return TableReaderV2.Parse<ConditionTable>().Where(condition => conditionIds.Contains(condition.Id))
            .ToDictionary(condition => condition.Id);
    });

    internal static bool IsFangKuaiTask(TaskTable task) => task.Type == 11 &&
        FangKuaiTaskGroups.Value.Any(group => group.TaskId.Contains(task.Id));

    private static List<MissionTaskProgress> BuildFangKuaiTaskProgress(Session session, Func<int, bool>? claimed = null)
    {
        claimed ??= session.player.MissionProgress.ClaimedTaskIds.Contains;
        DateTimeOffset now = FangKuaiModule.Clock();
        List<MissionTaskProgress> progress = [];
        foreach (FangKuaiActivityTable activity in FangKuaiTaskActivities.Value)
        {
            if (!ActivityScheduleService.IsOpen(activity.TimeId, now))
                continue;
            foreach (TaskTimeLimitTable group in FangKuaiTaskGroups.Value)
            {
                if (!activity.TaskTimeLimitIds.Contains(group.Id) || group.TimeId is not int timeId
                    || !ActivityScheduleService.IsOpen(timeId, now))
                    continue;
                foreach (int id in group.TaskId)
                {
                    if (!TaskRowsById.Value.TryGetValue(id, out TaskTable? task) || !IsFangKuaiTask(task)
                        || !IsTaskActive(task, now) || !FangKuaiConditions.Value.TryGetValue(task.Condition, out ConditionTable? condition))
                        continue;
                    int target = task.Result ?? 1;
                    int value = 0;
                    if (session.player.FangKuai.ActivityId == activity.Id)
                    {
                        if (condition.Type == 107001 && condition.Params.Count == 2
                            && condition.Params[0] == 1 && target == 1)
                            value = session.player.FangKuai.PlayedStageIds.Contains(condition.Params[1]) ? 1 : 0;
                        else if (condition.Type == 107003 && condition.Params.Count > 1
                            && condition.Params[0] == target && target > 0)
                        {
                            long total = 0;
                            foreach (int stageId in condition.Params.Skip(1))
                            {
                                total += Math.Clamp(session.player.FangKuai.TotalScoresByStage.GetValueOrDefault(stageId),
                                    0, (long)target - total);
                                if (total >= target) break;
                            }
                            value = (int)total;
                        }
                    }
                    progress.Add(new MissionTaskProgress(task.Id, condition.Id, Math.Min(value, target),
                        claimed(task.Id) ? TaskStateFinish : value >= target ? TaskStateAchieved : TaskStateActive));
                }
            }
        }
        return progress;
    }

    internal static void SendFangKuaiTaskSync(Session session)
    {
        List<MissionTaskProgress> progress = BuildFangKuaiTaskProgress(session);
        if (progress.Count > 0)
            session.SendPush(new NotifyTask { Tasks = new() { Tasks = progress.Select(ToSyncTask).ToList() } });
    }
}
