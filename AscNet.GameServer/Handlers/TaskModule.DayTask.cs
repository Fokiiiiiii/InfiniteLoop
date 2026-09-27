using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.task;

namespace AscNet.GameServer.Handlers;

internal static partial class TaskModule
{
    // Source TaskTimeLimit.DayTaskId Type-11 event dailies whose conditions the server can evaluate:
    // 10201 daily login, 11202 daily item spend [target, itemId], 11203 daily balance [itemId, target].
    private sealed record DayTask(TaskTable Task, ConditionTable Condition, int[] TimeIds);

    private static readonly Lazy<IReadOnlyDictionary<int, DayTask>> DayTasks = new(() =>
    {
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        return TableReaderV2.Parse<TaskTimeLimitTable>().Where(limit => limit.TimeId is > 0)
            .SelectMany(limit => limit.DayTaskId.Select(id => (id, TimeId: limit.TimeId!.Value)))
            .GroupBy(entry => entry.id)
            .Select(group => (Task: TaskRowsById.Value.GetValueOrDefault(group.Key), TimeIds: group.Select(entry => entry.TimeId).Distinct().ToArray()))
            .Where(entry => entry.Task is { Type: 11 } task && !CurrentTaskIds.Value.Contains(task.Id)
                && !IsEnvelopeTask(task) && !IsFangKuaiTask(task)
                && conditions.TryGetValue(task.Condition, out ConditionTable? condition)
                && condition.Type is 10201 or 11202 or 11203)
            .ToDictionary(entry => entry.Task!.Id, entry => new DayTask(entry.Task!, conditions[entry.Task!.Condition], entry.TimeIds));
    });

    internal static bool IsDayTask(int taskId) => DayTasks.Value.ContainsKey(taskId);

    private static bool IsDayTaskOpen(DayTask day, DateTimeOffset now) =>
        IsTaskActive(day.Task, now) && day.TimeIds.Any(timeId => ActivityScheduleService.IsOpen(timeId, now));

    private static string DayTaskClaimKey(int taskId, long dailyResetDay) => $"day-task:{taskId}:{dailyResetDay}";

    private static List<MissionTaskProgress> BuildDayTaskProgress(Session session, Func<int, bool>? claimed = null, Func<int, long>? balance = null)
    {
        claimed ??= session.player.MissionProgress.ClaimedTaskIds.Contains;
        balance ??= itemId => session.inventory.Items.Where(item => item.Id == itemId).Sum(item => (long)item.Count);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<MissionTaskProgress> progress = [];
        foreach (DayTask day in DayTasks.Value.Values)
        {
            if (!IsDayTaskOpen(day, now))
                continue;
            List<int> parameters = day.Condition.Params;
            long value = day.Condition.Type switch
            {
                // AscNet policy: an authenticated request inside the business day is that day's login.
                10201 => 1,
                11202 => session.player.MissionProgress.DayTaskConditionCounters.GetValueOrDefault(day.Condition.Id),
                11203 when parameters.Count == 2 => balance(parameters[0]),
                _ => 0
            };
            int target = day.Task.Result ?? 1;
            int shown = (int)Math.Clamp(value, 0, target);
            progress.Add(new MissionTaskProgress(day.Task.Id, day.Condition.Id, shown,
                claimed(day.Task.Id) ? TaskStateFinish : shown >= target ? TaskStateAchieved : TaskStateActive));
        }
        return progress;
    }

    // Adds today's spend to open day tasks; caller persists and restores the returned snapshot on failure.
    private static Dictionary<int, int?> AddDayTaskSpend(Session session, Func<ConditionTable, int> amount)
    {
        Dictionary<int, int> counters = session.player.MissionProgress.DayTaskConditionCounters;
        Dictionary<int, int?> previous = [];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (DayTask day in DayTasks.Value.Values)
        {
            if (day.Condition.Type != 11202 || previous.ContainsKey(day.Condition.Id) || !IsDayTaskOpen(day, now))
                continue;
            int added = amount(day.Condition);
            if (added <= 0)
                continue;
            previous[day.Condition.Id] = counters.TryGetValue(day.Condition.Id, out int old) ? old : null;
            counters[day.Condition.Id] = (int)Math.Min(int.MaxValue, (long)old + added);
        }
        return previous;
    }

    private static void RestoreDayTaskCounters(Session session, Dictionary<int, int?> previous)
    {
        foreach ((int conditionId, int? value) in previous)
            if (value.HasValue) session.player.MissionProgress.DayTaskConditionCounters[conditionId] = value.Value;
            else session.player.MissionProgress.DayTaskConditionCounters.Remove(conditionId);
    }

    private static void ResetDayTasks(Session session)
    {
        session.player.MissionProgress.ClaimedTaskIds.RemoveAll(IsDayTask);
        session.player.MissionProgress.DayTaskConditionCounters.Clear();
    }
}
