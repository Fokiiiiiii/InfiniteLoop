using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben.cerberusgame;
using AscNet.Table.V2.share.task;

namespace AscNet.GameServer.Handlers;

internal static partial class TaskModule
{
    private static readonly Lazy<IReadOnlyDictionary<int, ConditionTable>> CerberusGameTaskConditions = new(() =>
        TableReaderV2.Parse<ConditionTable>().Where(condition => condition.Type == 15201
            && condition.Params.Count >= 2 && condition.Params[0] == 1
            && condition.Params.Skip(1).All(stageId => stageId > 0 && CerberusGameModule.IsStage((uint)stageId)))
            .ToDictionary(condition => condition.Id));

    private static readonly Lazy<IReadOnlyList<TaskTimeLimitTable>> CerberusGameTaskGroups = new(() =>
    {
        // Cerberus has no task-group column: only shared event windows identify its current rerun tasks.
        // Historical mainline tasks referencing the same stages do not belong to these windows.
        HashSet<int> timeIds = TableReaderV2.Parse<CerberusGameChapterTable>().Select(chapter => chapter.TimeId)
            .Concat(TableReaderV2.Parse<CerberusGameActivityTable>().Select(activity => activity.TimeId ?? 0))
            .Where(timeId => timeId > 0).ToHashSet();
        return TableReaderV2.Parse<TaskTimeLimitTable>().Where(group => group.TimeId is int timeId && timeIds.Contains(timeId))
            .ToArray();
    });

    internal static bool IsCerberusGameTask(TaskTable task) => task.Type == 11
        && CerberusGameTaskConditions.Value.ContainsKey(task.Condition)
        && CerberusGameTaskGroups.Value.Any(group => group.TaskId.Contains(task.Id));

    private static List<MissionTaskProgress> BuildCerberusGameTaskProgress(Session session, Func<int, bool>? claimed = null)
    {
        claimed ??= session.player.MissionProgress.ClaimedTaskIds.Contains;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<MissionTaskProgress> progress = [];
        foreach (TaskTimeLimitTable group in CerberusGameTaskGroups.Value)
        {
            if (group.TimeId is not int timeId || !ActivityScheduleService.IsOpen(timeId, now)) continue;
            foreach (int taskId in group.TaskId)
            {
                if (!TaskRowsById.Value.TryGetValue(taskId, out TaskTable? task) || !IsCerberusGameTask(task)
                    || !IsTaskActive(task, now)) continue;
                ConditionTable condition = CerberusGameTaskConditions.Value[task.Condition];
                int target = Math.Max(1, task.Result ?? 1);
                int value = Math.Min(target, Math.Max(session.player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id),
                    condition.Params.Skip(1).Any(stageId => HasPassedStage(session, stageId)) ? 1 : 0));
                bool prerequisite = task.ShowAfterTaskId is not > 0 || claimed(task.ShowAfterTaskId.Value);
                progress.Add(new MissionTaskProgress(task.Id, condition.Id, value,
                    claimed(task.Id) ? TaskStateFinish : prerequisite && value >= target ? TaskStateAchieved : TaskStateActive));
            }
        }
        return progress.DistinctBy(task => task.TaskId).ToList();
    }

    internal static void SendCerberusGameTaskSync(Session session)
    {
        List<MissionTaskProgress> progress = BuildCerberusGameTaskProgress(session);
        if (progress.Count > 0)
            session.SendPush(new NotifyTask { Tasks = new() { Tasks = progress.Select(ToSyncTask).ToList() } });
    }
}
