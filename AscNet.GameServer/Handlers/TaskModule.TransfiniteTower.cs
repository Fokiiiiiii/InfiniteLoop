using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.task;

namespace AscNet.GameServer.Handlers;

// Overclock Simulation tasks (TransfiniteTowerActivity.TaskTimeLimitId, e.g. 763): progress derives from committed tower floors.
internal static partial class TaskModule
{
    private const int TransfiniteTowerFloorCondition = 15201;
    private static readonly Lazy<IReadOnlyList<(TaskTimeLimitTable Group, int ActivityTimeId)>> TransfiniteTowerTaskGroups = new(() =>
    {
        Dictionary<int, TaskTimeLimitTable> groups = TableReaderV2.Parse<TaskTimeLimitTable>().ToDictionary(x => x.Id);
        return TransfiniteTowerModule.TaskGroups().Where(x => groups.ContainsKey(x.TaskTimeLimitId))
            .Select(x => (groups[x.TaskTimeLimitId], x.TimeId)).ToArray();
    });
    private static readonly Lazy<IReadOnlyDictionary<int, ConditionTable>> TransfiniteTowerConditions = new(() =>
        TableReaderV2.Parse<ConditionTable>().Where(x => x.Type == TransfiniteTowerFloorCondition).ToDictionary(x => x.Id));

    internal static bool IsTransfiniteTowerTask(TaskTable task) => task.Type == 11
        && TransfiniteTowerTaskGroups.Value.Any(x => x.Group.TaskId.Contains(task.Id));

    private static List<MissionTaskProgress> BuildTransfiniteTowerTaskProgress(Session session, Func<int, bool>? claimed = null)
    {
        claimed ??= session.player.MissionProgress.ClaimedTaskIds.Contains;
        // The tower clock seam gates the tower-owned activity/task windows below too, so a frozen tower clock
        // stays consistent with the entry, settle and ranking paths (the wall clock would suppress the sync).
        DateTimeOffset now = TransfiniteTowerModule.Clock();
        List<MissionTaskProgress> progress = [];
        foreach ((TaskTimeLimitTable group, int activityTimeId) in TransfiniteTowerTaskGroups.Value)
        {
            if (!ActivityScheduleService.IsOpen(activityTimeId, now) || group.TimeId is int timeId && !ActivityScheduleService.IsOpen(timeId, now))
                continue;
            foreach (int id in group.TaskId)
            {
                if (!TaskRowsById.Value.TryGetValue(id, out TaskTable? task) || task.Type != 11 || !IsTaskActive(task, now)
                    || !TransfiniteTowerConditions.Value.TryGetValue(task.Condition, out ConditionTable? condition)
                    || condition.Params.Count < 2 || condition.Params[0] != 1)
                    continue;
                int value = TransfiniteTowerModule.IsFloorCleared(session.player, condition.Params[1]) ? 1 : 0;
                int target = Math.Max(1, task.Result ?? 1);
                progress.Add(new MissionTaskProgress(task.Id, condition.Id, Math.Min(value, target),
                    claimed(task.Id) ? TaskStateFinish : value >= target ? TaskStateAchieved : TaskStateActive));
            }
        }
        return progress;
    }

    internal static void SendTransfiniteTowerTaskSync(Session session)
    {
        List<MissionTaskProgress> progress = BuildTransfiniteTowerTaskProgress(session);
        if (progress.Count > 0)
            session.SendPush(new NotifyTask { Tasks = new() { Tasks = progress.Select(ToSyncTask).ToList() } });
    }
}
