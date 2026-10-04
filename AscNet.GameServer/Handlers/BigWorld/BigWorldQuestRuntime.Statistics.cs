using System.Collections.Concurrent;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.statussyncfight.quest;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Native step statistics (DlcQuestStep.Config.StatisticModule.Items, the 13 jumper steps of quests 5001-5007). A running step's items
    // turn engine events into quest vars (StatisticType 1 SceneObjectBeCollected, 2 LevelPlayCurTime, 3 QuestObjectiveComplete,
    // 4 PlayerEnterTrigger; XConfigQuestStatisticItem* [DUMP48 dump.cs XConfigQuestStatisticItemSceneObjectBeCollected]). The config
    // carries the whole rule (collectable type -> var, AddValue, PreventNegativeResult), so the 4.7 Lua score dictionaries are not used.
    internal static partial class BigWorldQuestRuntime
    {
        private static readonly ConcurrentDictionary<int, JArray> StatisticItemsByStep = new();

        private static JArray StatisticItems(int stepId) => StatisticItemsByStep.GetOrAdd(stepId, id =>
            StepConfig(BigWorldQuestModule.Steps.Value[id])["StatisticModule"]?["Items"] as JArray ?? []);

        private static List<(Theatre5DlcQuest Quest, JObject Item)> RunningStatistics(Session session, int levelId, string itemType) =>
            Data(session).ActiveQuests.Values.OrderBy(q => q.QuestId).Where(q => q.DynamicData is not null)
                .SelectMany(q => q.DynamicData!.Steps.Values.Where(s => s.StepState == StepInProgress && I(BigWorldQuestModule.Steps.Value[s.StepId].LevelId) == levelId)
                    .OrderBy(s => s.StepId)
                    .SelectMany(s => StatisticItems(s.StepId).OfType<JObject>().Where(item => (string?)item["$type"] == "XConfigQuestStatisticItem" + itemType)
                        .Select(item => (q, item))))
                .ToList();

        // Int/float vars add AddValue (PreventNegativeResult floors at 0); a bool var (TriggerJudge on an enter-trigger item) is set when
        // AddValue is positive. AscNet policy [INF]: the native bool-var behaviour of an Add item is not in the dump; an unset var counts from 0.
        private static void ApplyStatistic(Session session, int questId, JObject item)
        {
            int add = item["AddValue"]?["Value"]?.Value<int>() ?? 0;
            int varType = item["VarRef"]!["VarType"]!.Value<int>();
            string key = item["VarRef"]!["StrKey"]!.Value<string>()!;
            object value = varType switch
            {
                1 => AddInt((int?)GetVar(session.player, questId, 1, key) ?? 0, add, item["PreventNegativeResult"]?.Value<bool>() == true),
                2 => (float?)GetVar(session.player, questId, 2, key) + add ?? add,
                3 => add > 0,
                _ => throw new InvalidDataException($"Statistic var {key} has unsupported VarType {varType}.")
            };
            SetVar(session, questId, varType, key, value);
        }

        private static int AddInt(int current, int add, bool preventNegative) => preventNegative ? Math.Max(0, current + add) : current + add;

        // A collectable of `collectableType` was collected in `levelId` (RpcSceneObjectCollectNotify sent).
        internal static void OnSceneObjectBeCollected(Session session, int levelId, int collectableType) =>
            Scoped(session, false, () =>
            {
                foreach ((Theatre5DlcQuest quest, JObject item) in RunningStatistics(session, levelId, "SceneObjectBeCollected"))
                    if (item["CollectableType"]?.Value<int>() == collectableType)
                        ApplyStatistic(session, quest.QuestId, item);
            });

        // The player's controlled NPC entered trigger `triggerId` of scene object `hostPlaceId` (RpcTriggerInteractNotify Enter).
        internal static void OnPlayerEnterTrigger(Session session, int levelId, int hostPlaceId, int triggerId) =>
            Scoped(session, false, () =>
            {
                foreach ((Theatre5DlcQuest quest, JObject item) in RunningStatistics(session, levelId, "PlayerEnterTrigger"))
                    if (item["TriggerHostActorPlaceId"]?.Value<int>() == hostPlaceId && item["TriggerId"]?.Value<int>() == triggerId)
                        ApplyStatistic(session, quest.QuestId, item);
            });

        // An objective closes: QuestObjectiveComplete items count it, and LevelPlayCurTime items store the level play time (the level's
        // XLevelPlayTimer, seconds) before the objective's ExitActions read it (50010301 settles Score with Time).
        // AscNet policy [INF]: Time is sampled at objective completion instead of every tick (no client reads it earlier than settle).
        private static void OnObjectiveStatistics(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg)
        {
            int levelId = I(BigWorldQuestModule.Steps.Value[cfg.StepId].LevelId);
            foreach ((Theatre5DlcQuest owner, JObject item) in RunningStatistics(session, levelId, "QuestObjectiveComplete"))
                if (item["QuestObjectiveId"]?.Value<int>() == cfg.Id)
                    ApplyStatistic(session, owner.QuestId, item);
            if (Of(session).Timer is { } timer && timer.QuestId == quest.QuestId)
                foreach ((Theatre5DlcQuest owner, JObject item) in RunningStatistics(session, levelId, "LevelPlayCurTime"))
                    SetVar(session, owner.QuestId, 2, item["VarRef"]!["StrKey"]!.Value<string>()!, (float)ElapsedOf(timer));
        }
    }
}
