using AscNet.Common.Util;
using AscNet.Table.V2.share.activity;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.fuben.simulatetrain;
using AscNet.Table.V2.share.miniactivity.dyemerge;
using AscNet.Table.V2.share.samecolorgame;
using AscNet.Table.V2.share.theatre;
using AscNet.Table.V2.share.theatre3;
using AscNet.Table.V2.share.theatre4;
using AscNet.Table.V2.share.miniactivity.fangkuai;
using AscNet.Table.V2.share.fuben.transfinitetower;
using AscNet.Table.V2.share.punishaar;

namespace AscNet.GameServer.Game;

public readonly record struct ActivityScheduleEntry(long Id, long StartTime, long EndTime, string Source)
{
    public bool IsOpen(DateTimeOffset now)
    {
        long unixTime = now.ToUnixTimeSeconds();
        return (StartTime == 0 || unixTime >= StartTime)
            && (EndTime == 0 || unixTime < EndTime);
    }
}

/// <summary>Event availability derived from version tables, public notices, and documented local mode policies.</summary>
public static class ActivityScheduleService
{
    private const long UnboundedEnd = 32503680000; // 3000-01-01 UTC; see the Theatre5 note below.

    private static readonly Lazy<IReadOnlyList<ActivityScheduleEntry>> Entries = new(() =>
        TableReaderV2.Parse<Theatre3ActivityTable>()
            .Where(row => row.TimeId > 0)
            .Select(row => new ActivityScheduleEntry(row.TimeId, 0, 0,
                $"local-policy:Theatre3:permanent-mode:Theatre3Activity:Id={row.Id}:TimeId={row.TimeId}"))
            // AscNet policy: Awakening Tundra remains available as a permanent roguelike.
            .Concat(TableReaderV2.Parse<Theatre4ActivityTable>()
                .Where(row => row.TimeId > 0)
                .Select(row => new ActivityScheduleEntry(row.TimeId, 0, 0,
                    $"local-policy:Theatre4:permanent-mode:Theatre4Activity:Id={row.Id}:TimeId={row.TimeId}")))
            .Concat(TheatreDecorationEntries())
            // AscNet policy: Circuit Connect is a permanent mode. Its client manager opens the mode
            // from the authored activity row's positive TimerId without a calendar bound, and no
            // authoritative retail window exists for AscNet; the authored table is the only source.
            .Concat(TableReaderV2.Parse<SameColorGameActivityTable>()
                .Where(row => row.TimerId is > 0)
                .Select(row => new ActivityScheduleEntry(row.TimerId!.Value, 0, 0,
                    $"local-policy:SameColorGame:permanent-mode:SameColorGameActivity:Id={row.Id}:"
                    + $"TimerId={row.TimerId}:user-approved")))
            // Godfall's PvP client requires a positive end. 3000-01-01 UTC stays within the
            // Windows _localtime64 range even after a local-time-zone adjustment.
            // https://learn.microsoft.com/cpp/c-runtime-library/reference/localtime-localtime32-localtime64
            .Concat(new[] { 34, 35, 46401 }.Select(timeId => new ActivityScheduleEntry(timeId, 0,
                UnboundedEnd,
                $"feature-window:Theatre5:unbounded-calendar:user-approved:TimeId={timeId}")))
            .Concat(TableReaderV2.Parse<ActivityScheduleTable>()
                .Select(row => new ActivityScheduleEntry(row.Id, row.StartTime, row.EndTime, row.Source)))
            .Concat(SimulateTrainWindowEntries())
            // Policy rows follow every authoritative source: DistinctBy keeps the first, so an
            // official window for any of these TimeIds always wins.
            .Concat(FangKuaiChapterEntries())
            .Concat(PunishaarStageEntries())
            .Concat(TransfiniteTowerEntries())
            // AscNet policy (user-approved, not retail parity): the separate self-choice lottery
            // has no authoritative calendar. Its client requires a positive end time.
            .Concat(new[] { new ActivityScheduleEntry(49501, 0, UnboundedEnd,
                "local-policy:missing-calendar:unbounded-calendar:"
                + "LottoPrimary:Id=5(3.7 Choice self-choice coating lottery):TimeId=49501:user-approved") })
            .DistinctBy(row => row.Id)
            .OrderBy(row => row.Id)
            .ToArray());

    public static IReadOnlyList<ActivityScheduleEntry> All => Entries.Value;

    public static bool IsOpen(long timeId, DateTimeOffset now) =>
        TryGet(timeId, out ActivityScheduleEntry entry) && entry.IsOpen(now);

    public static bool TryGet(long timeId, out ActivityScheduleEntry entry)
    {
        entry = Entries.Value.FirstOrDefault(row => row.Id == timeId);
        return entry.Id != 0;
    }

    private static IEnumerable<ActivityScheduleEntry> TheatreDecorationEntries()
    {
        // Only the approved decoration calendars are permanent; progression and costs remain intact.
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>()
            .ToDictionary(row => row.Id);
        Stack<int> pending = new(TableReaderV2.Parse<TheatreDecorationTable>()
            .Where(row => row.DecorationId is 20003 or 20004 or 20005)
            .Select(row => row.ConditionId.GetValueOrDefault())
            .Where(conditionId => conditionId > 0));
        HashSet<int> seen = [];
        while (pending.TryPop(out int conditionId))
        {
            if (!seen.Add(conditionId))
                continue;
            ConditionTable condition = conditions[conditionId];
            if (!string.IsNullOrWhiteSpace(condition.Formula))
            {
                foreach (System.Text.RegularExpressions.Match reference in
                    System.Text.RegularExpressions.Regex.Matches(condition.Formula, @"\d+"))
                    pending.Push(int.Parse(reference.Value));
            }
            else if (condition.Type == 23001 && condition.Params.Count > 0
                && condition.Params[0] is 803 or 804 or 805)
            {
                int timeId = condition.Params[0];
                yield return new ActivityScheduleEntry(timeId, 0, 0,
                    $"feature-window:Theatre:permanent-decoration-release:user-approved:"
                    + $"TheatreDecoration:DecorationId=20003,20004,20005:Condition:Id={conditionId}:TimeId={timeId}");
            }
        }
    }

    /// <summary>
    /// AscNet policy (user-approved): FangKuai chapter stage-group TimeIds with no authored window
    /// inherit their parent FangKuaiActivity window from ActivitySchedule.tsv; a parent without a
    /// window leaves the chapter permanent. Stage PreStageId progression still gates entry.
    /// </summary>
    private static IEnumerable<ActivityScheduleEntry> FangKuaiChapterEntries()
    {
        var official = TableReaderV2.Parse<ActivityScheduleTable>().ToDictionary(row => (long)row.Id);
        Dictionary<int, FangKuaiChapterTable> chapters = TableReaderV2.Parse<FangKuaiChapterTable>().ToDictionary(row => row.Id);
        Dictionary<int, FangKuaiStageGroupTable> groups = TableReaderV2.Parse<FangKuaiStageGroupTable>().ToDictionary(row => row.Id);
        foreach (FangKuaiActivityTable activity in TableReaderV2.Parse<FangKuaiActivityTable>().Where(row => row.TimeId > 0))
        {
            bool inherited = official.TryGetValue(activity.TimeId, out ActivityScheduleTable? parent);
            foreach (int timeId in activity.ChapterIds
                .SelectMany(id => chapters.TryGetValue(id, out FangKuaiChapterTable? chapter) ? chapter.StageGroupIds : [])
                .Select(id => groups.TryGetValue(id, out FangKuaiStageGroupTable? group) ? group.TimeId : 0)
                .Where(timeId => timeId > 0 && timeId != activity.TimeId).Distinct())
                yield return new ActivityScheduleEntry(timeId, parent?.StartTime ?? 0, parent?.EndTime ?? 0,
                    $"local-policy:FangKuai:chapter-{(inherited ? "inherits-parent-window" : "permanent")}:"
                    + $"FangKuaiActivity:Id={activity.Id}:ParentTimeId={activity.TimeId}:TimeId={timeId}:user-approved");
        }
    }

    private static ActivityScheduleEntry InheritParent(Dictionary<long, ActivityScheduleTable> official,
        long parentTimeId, long timeId, string mode, string row)
    {
        bool inherited = official.TryGetValue(parentTimeId, out ActivityScheduleTable? parent);
        return new ActivityScheduleEntry(timeId, parent?.StartTime ?? 0, parent?.EndTime ?? 0,
            $"local-policy:{mode}-{(inherited ? "inherits-parent-window" : "permanent")}:"
            + $"{row}:ParentTimeId={parentTimeId}:TimeId={timeId}:user-approved");
    }

    /// <summary>
    /// AscNet policy (user-approved): Circuit Calculus stage TimeIds with no authored window
    /// inherit their PunishaarActivity parent window. PreStageId progression still gates entry.
    /// </summary>
    private static IEnumerable<ActivityScheduleEntry> PunishaarStageEntries()
    {
        var official = TableReaderV2.Parse<ActivityScheduleTable>().ToDictionary(row => (long)row.Id);
        PunishaarStageGroupTable[] stages = TableReaderV2.Parse<PunishaarStageGroupTable>().ToArray();
        foreach (PunishaarActivityTable activity in TableReaderV2.Parse<PunishaarActivityTable>().Where(row => row.TimeId > 0))
            foreach (int timeId in stages.Where(stage => stage.GroupId == activity.StageGroup)
                .Select(stage => stage.TimeId).Where(timeId => timeId > 0 && timeId != activity.TimeId).Distinct())
                yield return InheritParent(official, activity.TimeId, timeId, "Punishaar:stage",
                    $"PunishaarActivity:Id={activity.Id}");
    }

    /// <summary>
    /// AscNet policy (user-approved): Overclock Simulation chapter/character unlock TimeIds with no
    /// authored window inherit the TransfiniteTowerActivity parent window. The rank-reward TimeId
    /// opens at the parent's end for the authored RankRewardExpireInterval; with no bounded parent
    /// end there is no ranking close, so no reward window is emitted.
    /// </summary>
    private static IEnumerable<ActivityScheduleEntry> TransfiniteTowerEntries()
    {
        var official = TableReaderV2.Parse<ActivityScheduleTable>().ToDictionary(row => (long)row.Id);
        Dictionary<int, TransfiniteTowerChapterTable> chapters = TableReaderV2.Parse<TransfiniteTowerChapterTable>()
            .ToDictionary(row => row.Id);
        int[] characterTimeIds = TableReaderV2.Parse<TransfiniteTowerCharacterTable>()
            .Select(row => row.UnLockTimeId).ToArray();
        long expireInterval = long.Parse(TableReaderV2.Parse<TransfiniteTowerConfigTable>()
            .Single(row => row.Key == "RankRewardExpireInterval").Values[0]);
        foreach (TransfiniteTowerActivityTable activity in TableReaderV2.Parse<TransfiniteTowerActivityTable>()
            .Where(row => row.TimeId > 0))
        {
            string row = $"TransfiniteTowerActivity:Id={activity.Id}";
            foreach (int timeId in activity.ChapterIds
                .Select(id => chapters.TryGetValue(id, out TransfiniteTowerChapterTable? chapter) ? chapter.UnLockTimeId : 0)
                .Concat(characterTimeIds)
                .Where(timeId => timeId > 0 && timeId != activity.TimeId).Distinct())
                yield return InheritParent(official, activity.TimeId, timeId, "TransfiniteTower:unlock", row);
            if (activity.RankRewardTimeId > 0 && official.TryGetValue(activity.TimeId, out ActivityScheduleTable? parent)
                && parent.EndTime > 0)
                yield return new ActivityScheduleEntry(activity.RankRewardTimeId, parent.EndTime,
                    parent.EndTime + expireInterval,
                    $"local-policy:TransfiniteTower:rank-reward-after-parent-end:{row}:ParentTimeId={activity.TimeId}:"
                    + $"RankRewardExpireInterval={expireInterval}:TimeId={activity.RankRewardTimeId}:user-approved");
        }
    }

    /// <summary>
    /// Simulated Trial practice bosses are a permanent catalog: their TimeId/ImpasseTimeId columns
    /// are retail rotation metadata with no authoritative AscNet schedule, so every id the monster
    /// table publishes stays available at all times (retail opened each from a rotation date).
    /// </summary>
    private static IEnumerable<ActivityScheduleEntry> SimulateTrainWindowEntries() =>
        TableReaderV2.Parse<SimulateTrainMonsterTable>()
            .SelectMany(row => new[]
            {
                (TimeId: row.TimeId, Column: "TimeId"),
                (TimeId: row.ImpasseTimeId, Column: "ImpasseTimeId")
            })
            .Where(entry => entry.TimeId > 0)
            .DistinctBy(entry => entry.TimeId)
            .OrderBy(entry => entry.TimeId)
            .Select(entry => new ActivityScheduleEntry(entry.TimeId, 0, 0,
                $"local-policy:SimulateTrain:permanent-boss-practice:SimulateTrainMonster:"
                + $"{entry.Column}={entry.TimeId}:user-approved"));

    /// <summary>
    /// Maps an ordinary event stage back to its activity TimeId from the fuben/miniactivity
    /// tables that enumerate stages alongside a TimeId and are not otherwise gated by a
    /// PreFight module. Stages absent from the index (permanent mainline, normal stages,
    /// module-gated event stages) return null and are left to their own gates.
    /// </summary>
    private static readonly Lazy<Dictionary<int, int>> StageTimeIds = new(BuildStageTimeIds);

    public static int? StageTimeId(int stageId) =>
        StageTimeIds.Value.TryGetValue(stageId, out int timeId) ? timeId : null;

    private static Dictionary<int, int> BuildStageTimeIds()
    {
        Dictionary<int, int> stageToTimeId = new();

        void AddStage(int stageId, int? timeId)
        {
            if (stageId > 0 && timeId is > 0)
                stageToTimeId.TryAdd(stageId, timeId.Value);
        }


        foreach (DyeMergeChapterTable chapter in TableReaderV2.Parse<DyeMergeChapterTable>())
            foreach (int stageId in chapter.StageIds)
                AddStage(stageId, chapter.TimeId);


        return stageToTimeId;
    }
}
