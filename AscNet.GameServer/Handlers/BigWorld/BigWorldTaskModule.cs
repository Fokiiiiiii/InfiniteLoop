using System.Runtime.CompilerServices;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using SyncTask = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask;
using AscNet.Table.V2.share.bigworld.common.course;
using AscNet.Table.V2.share.bigworld.common.task;
using AscNet.Table.V2.share.statussyncfight.level;
using AscNet.Table.V2.share.statussyncfight.quest;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // BigWorld (Babylonia) tasks: BigWorldTask.tab rows ride the main task channel (login TaskData, NotifyTask,
    // FinishTask/FinishMultiTaskRequest); the client merges them in XTaskManager (TotalTaskData post-processor).
    // Progress is derived from state owned by other slices; only claims are persisted here.
    internal static class BigWorldTaskModule
    {
        // CodeText 20026005 TaskManagerFinishTaskTaskNotFound "Mission not found".
        internal const int CodeTaskNotFound = 20026005;
        // CodeText 20026006 TaskManagerFinishTaskTaskAlreadyFinish "Mission completed".
        internal const int CodeTaskAlreadyFinish = 20026006;
        // CodeText 20026007 TaskManagerFinishTaskTaskNotAchieved "Mission reward not available for collection".
        internal const int CodeTaskNotAchieved = 20026007;

        private const int TaskStateActive = 1;
        private const int TaskStateAchieved = 3;
        private const int TaskStateFinish = 4;
        private const int TaskTypeDaily = 6;

        private static readonly Lazy<Dictionary<int, BigWorldTaskTable>> Tasks = new(() =>
            TableReaderV2.Parse<BigWorldTaskTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<int, BigWorldConditionTable>> Conditions = new(() =>
            TableReaderV2.Parse<BigWorldConditionTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<List<BigWorldCourseExploreTable>> Explores = new(() =>
            TableReaderV2.Parse<BigWorldCourseExploreTable>().ToList());
        private static readonly Lazy<Dictionary<int, int>> LevelPlayIdByLevel = new(() =>
            TableReaderV2.Parse<BigWorldLevelPlayTable>().GroupBy(row => row.LevelId).ToDictionary(group => group.Key, group => group.First().Id));
        private static readonly Lazy<Dictionary<int, int>> ObjectiveQuestIds = new(() =>
            TableReaderV2.Parse<DlcQuestObjectiveTable>().ToDictionary(row => row.Id, row => row.QuestId));
        // AscNet policy: 2021001 [count, firstResultId, ...] counts unlocked invite-quest endings in the id block that
        // starts at firstResultId and ends before the next 2021001 block start (1..6 = v1.5 endings, 7.. = v2.0).
        private static readonly Lazy<int[]> EndingBlockStarts = new(() =>
            Conditions.Value.Values.Where(row => row.Type == 2021001 && row.Params.Count > 1)
                .Select(row => row.Params[1]).Distinct().Order().ToArray());

        // Last progress sent per session so OnProgressChanged pushes only deltas.
        private static readonly ConditionalWeakTable<Session, Dictionary<int, (int Value, int State)>> Sent = new();

        internal static bool IsTask(int taskId) => Tasks.Value.ContainsKey(taskId);

        // Login TaskData / full NotifyTask rows; also resets the delta baseline.
        internal static List<SyncTask> BuildTasks(Session session)
        {
            List<SyncTask> tasks = Tasks.Value.Values.Select(task => Build(session.player, task)).ToList();
            Dictionary<int, (int, int)> sent = Sent.GetOrCreateValue(session);
            foreach (SyncTask task in tasks)
                sent[(int)task.Id] = (task.Schedule[0].Value, task.State);
            return tasks;
        }

        internal static List<SyncTask> BuildTasks(Session session, IEnumerable<int> taskIds) =>
            taskIds.Where(IsTask).Distinct().Select(id => Build(session.player, Tasks.Value[id])).ToList();

        // Core calls this after any state that feeds a BigWorld task counter changed.
        internal static void OnProgressChanged(Session session)
        {
            Dictionary<int, (int Value, int State)> sent = Sent.GetOrCreateValue(session);
            List<SyncTask> changed = [];
            foreach (BigWorldTaskTable task in Tasks.Value.Values)
            {
                SyncTask row = Build(session.player, task);
                (int, int) now = (row.Schedule[0].Value, row.State);
                if (sent.TryGetValue(task.Id, out (int, int) before) && before == now)
                    continue;
                sent[task.Id] = now;
                changed.Add(row);
            }
            if (changed.Count > 0)
                session.SendPush(new NotifyTask { Tasks = new() { Tasks = changed } });
        }

        // AscNet policy: a multi-claim is atomic — any unknown/claimed/unfinished id rejects the whole request, so the
        // client's isLoopReceive retry on NotDealTaskIds can never spin on a task that will not become claimable.
        internal static int Claim(Session session, IReadOnlyCollection<int> taskIds, List<RewardGoods> rewards)
        {
            int[] ids = taskIds.Distinct().ToArray();
            if (ids.Length == 0)
                return CodeTaskNotFound;
            foreach (int id in ids)
            {
                if (!Tasks.Value.TryGetValue(id, out BigWorldTaskTable? task))
                    return CodeTaskNotFound;
                int state = Build(session.player, task).State;
                if (state == TaskStateFinish)
                    return CodeTaskAlreadyFinish;
                if (state != TaskStateAchieved)
                    return CodeTaskNotAchieved;
            }
            long period = TaskModule.CurrentDailyResetPeriod(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            foreach (int id in ids)
                session.player.BigWorldState.ClaimedTaskPeriods[id] = period;
            foreach (int id in ids)
                rewards.AddRange(BigWorldModule.GrantReward(session, Tasks.Value[id].RewardId));
            session.player.Save();
            OnProgressChanged(session);
            return 0;
        }

        private static SyncTask Build(Player player, BigWorldTaskTable task)
        {
            int conditionId = task.Condition.FirstOrDefault();
            int target = Math.Max(1, task.Result);
            int value = Conditions.Value.TryGetValue(conditionId, out BigWorldConditionTable? condition)
                ? Math.Min(Progress(player, condition), target) : 0;
            int state = IsClaimed(player, task) ? TaskStateFinish : value >= target ? TaskStateAchieved : TaskStateActive;
            return new SyncTask
            {
                Id = (uint)task.Id,
                State = state,
                Schedule = [new() { Id = (uint)conditionId, Value = state == TaskStateFinish ? target : value }]
            };
        }

        private static bool IsClaimed(Player player, BigWorldTaskTable task) =>
            player.BigWorldState.ClaimedTaskPeriods.TryGetValue(task.Id, out long period)
            && (task.Type != TaskTypeDaily || period == TaskModule.CurrentDailyResetPeriod(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        // Counter per task/BigWorldCondition.Type. Sources are the owning slices' persisted state.
        private static int Progress(Player player, BigWorldConditionTable condition)
        {
            BigWorldPlayerState s = player.BigWorldState;
            List<int> p = condition.Params;
            IEnumerable<int> rest = p.Skip(1);
            int first = p.FirstOrDefault();
            return condition.Type switch
            {
                // AscNet policy: daily-login counter; a live session is today's login.
                10201 => 1,
                // Quest finished (DlcQuestInfo.FinishedQuests).
                1000001 => s.QuestData.FinishedQuests.Contains(first) ? 1 : 0,
                // All listed DlcQuestObjective ids finished.
                1000002 => p.All(id => ObjectiveFinished(s.QuestData, id)) ? 1 : 0,
                // AscNet policy: a level play is cleared once PlayerData.LevelPlayDatas has its entry; 3 stars = IsFullCleared.
                1010003 => rest.Count(level => LevelPlay(s, level) is not null),
                2030001 => rest.Count(level => LevelPlay(s, level)?.IsFullCleared == true),
                // Collected collectables. AscNet policy: each listed POI id expands to its whole BigWorldCourseExplore
                // (990074 [4,1,1001] = "All Treasure Chests" 10 = POIs 1001..1004); P2 is the collectable type.
                1020001 => ExpandPois(rest.Skip(1)).Sum(poi => BigWorldModule.CollectedByCourseGroup(player, poi)),
                // Kuroro Coffee stages: record exists = cleared; 2000003 [stars, ids] = ids with GetMaxStarReward >= stars.
                2000001 => s.SgCafe?.CafeStageList.ContainsKey(first) == true ? 1 : 0,
                2000003 => rest.Count(id => s.SgCafe?.CafeStageList.TryGetValue(id, out CafeStageRecord? r) == true && r!.GetMaxStarReward >= first),
                // Shopping street stages: passed record; 2010003 [stars, ids] = ids with RewardIndexRecord.Count >= stars.
                2010001 => s.SgStreet.PassedStageRecords.ContainsKey(first) ? 1 : 0,
                2010003 => rest.Count(id => s.SgStreet.PassedStageRecords.TryGetValue(id, out SgStreetPassedStageRecord? r) && r.RewardIndexRecord.Count >= first),
                // Dorm furniture / commander DIY part owned (2020001 [count, goodsId]).
                2020001 => (s.SgDorm?.FurnitureList.Count(item => item.CfgId == p.ElementAtOrDefault(1)) ?? 0)
                    + (s.CommanderFashionBags.Contains(p.ElementAtOrDefault(1)) ? 1 : 0),
                2021001 => EndingsInBlock(s, p.ElementAtOrDefault(1)),
                // Drone stage finished; 2031002 [stars, ...] = stages with FinishedStarTargets >= stars (AscNet policy: P2+ unused).
                2031001 => s.SgDroneStages.TryGetValue(first, out SgDroneStageInfo? drone) && drone.IsFinished ? 1 : 0,
                2031002 => s.SgDroneStages.Values.Count(stage => stage.FinishedStarTargets.Count >= first),
                // 17202 (990049 [7, 990006, 990010]) has no known source: never progresses.
                _ => 0
            };
        }

        private static bool ObjectiveFinished(Theatre5DlcQuestInfo quests, int objectiveId) =>
            ObjectiveQuestIds.Value.TryGetValue(objectiveId, out int questId)
            && (quests.FinishedQuests.Contains(questId)
                || quests.ActiveQuests.TryGetValue(questId, out Theatre5DlcQuest? quest) && quest.DynamicData?.FinishedObjectiveIds.Contains(objectiveId) == true);

        private static BigWorldLevelPlayData? LevelPlay(BigWorldPlayerState s, int levelId) =>
            LevelPlayIdByLevel.Value.TryGetValue(levelId, out int playId) ? s.LevelPlayDatas.GetValueOrDefault(playId) : null;

        private static IEnumerable<int> ExpandPois(IEnumerable<int> poiIds) =>
            poiIds.SelectMany(poi => Explores.Value.FirstOrDefault(explore => explore.PoiIds.Contains(poi))?.PoiIds ?? [poi]).Distinct();

        private static int EndingsInBlock(BigWorldPlayerState s, int blockStart)
        {
            int end = EndingBlockStarts.Value.FirstOrDefault(start => start > blockStart, int.MaxValue);
            return s.UnlockedInviteQuestResultIds.Distinct().Count(id => id >= blockStart && id < end);
        }
    }
}
