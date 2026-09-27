using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.miniactivity.envelope;
using AscNet.Table.V2.share.task;

namespace AscNet.GameServer.Handlers;

internal static partial class TaskModule
{
    private static readonly Lazy<IReadOnlyDictionary<int, ConditionTable>> EnvelopeConditions = new(() =>
        TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id));

    internal static bool IsEnvelopeTask(TaskTable task) =>
        TableReaderV2.Parse<EnvelopeActivityTable>().Any(activity =>
            task.GroupId == activity.TaskGroup || task.GroupId == activity.TaskDailyGroup);

    internal static int EvaluateEnvelopeTask(Session session, TaskTable task, ConditionTable condition,
        Func<int, long>? balance = null) => condition.Type switch
    {
        10202 => session.player.PlayerData.NewPlayerTaskActiveDay > 0 ? 1 : 0,
        11203 => (int)Math.Clamp(balance?.Invoke(Inventory.DailyActiveness)
            ?? session.inventory.Items.FirstOrDefault(item => item.Id == Inventory.DailyActiveness)?.Count ?? 0,
            0, int.MaxValue),
        141001 => session.player.Envelope.OpenedCharacterIds.Distinct().Count(),
        141002 => session.player.Envelope.AvgWatchedCharacterIds.Distinct().Count(),
        141003 => condition.Params.Count > 0 && condition.Params[0] > 0
            && session.player.Envelope.InstrumentBindings.Values.Count(characterId => characterId > 0)
                >= condition.Params[0] ? 1 : 0,
        _ => 0
    };

    private static List<MissionTaskProgress> BuildEnvelopeTaskProgress(Session session,
        Func<int, bool>? claimed = null, Func<int, long>? balance = null, DateTimeOffset? clock = null)
    {
        DateTimeOffset now = clock ?? Version47EventModule.Clock();
        // Login builds its task snapshot before it sends the Envelope activity notification.
        Version47EventModule.BuildEnvelopeNotify(session.player, now);
        EnvelopeActivityTable? activity = TableReaderV2.Parse<EnvelopeActivityTable>()
            .FirstOrDefault(row => row.Id == session.player.Envelope.ActivityId
                && row.TimeId > 0 && ActivityScheduleService.IsOpen(row.TimeId, now));
        if (activity is null)
            return [];
        claimed ??= session.player.MissionProgress.ClaimedTaskIds.Contains;
        return TableReaderV2.Parse<TaskTable>()
            .Where(task => (task.GroupId == activity.TaskGroup || task.GroupId == activity.TaskDailyGroup)
                && IsTaskActive(task, now) && EnvelopeConditions.Value.ContainsKey(task.Condition))
            .Select(task =>
            {
                int value = EvaluateEnvelopeTask(session, task, EnvelopeConditions.Value[task.Condition], balance);
                int target = task.Result ?? 1;
                return new MissionTaskProgress(task.Id, task.Condition, Math.Min(value, target),
                    claimed(task.Id) ? TaskStateFinish : value >= target ? TaskStateAchieved : TaskStateActive);
            }).ToList();
    }
    internal static void SendEnvelopeTaskSync(Session session, DateTimeOffset now)
    {
        List<MissionTaskProgress> progress = BuildEnvelopeTaskProgress(session, clock: now);
        if (progress.Count > 0)
            session.SendPush(new AscNet.Common.MsgPack.NotifyTask
            {
                Tasks = new() { Tasks = progress.Select(ToSyncTask).ToList() }
            });
    }

    internal static string EnvelopeTaskClaimKey(TaskTable task, long dailyResetDay) =>
        task.Type == 112 ? $"envelope-task:{task.Id}:{dailyResetDay}" : $"envelope-task:{task.Id}";

    // AscNet policy: before the daily rollover clears daily claims, remember the closing period's
    // earned-but-unclaimed daily tasks under that period's own claim key. Evaluated at the period's
    // last second, so a period outside the authored event window captures nothing.
    internal static void CaptureEnvelopeTaskReissues(Session session, long closingDay)
    {
        if (closingDay < 0)
            return;
        DateTimeOffset closing = DateTimeOffset.FromUnixTimeSeconds((closingDay + 1) * 86_400 - 1);
        Dictionary<int, TaskTable> daily = TableReaderV2.Parse<TaskTable>()
            .Where(task => task.Type == 112 && IsEnvelopeTask(task)).ToDictionary(task => task.Id);
        foreach (MissionTaskProgress row in BuildEnvelopeTaskProgress(session, clock: closing))
            if (row.State == TaskStateAchieved && daily.TryGetValue(row.TaskId, out TaskTable? task))
                session.player.Envelope.PendingTaskReissues[EnvelopeTaskClaimKey(task, closingDay)] = task.Id;
    }

}
