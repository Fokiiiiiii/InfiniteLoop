using System.Collections.Concurrent;
using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Logging;
using AscNet.Table.V2.share.bigworld.common.condition;
using AscNet.Table.V2.share.statussyncfight.quest;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Server mirror of client XBigWorldCondition (xbigworldservice/common/XBigWorldCondition.lua):
    // Type 0 = Formula over condition ids with & and | (equal precedence, left to right, parentheses);
    // Type in [10000000, 99999999) = BigWorld handler by type; anything else = main-game condition.
    internal static class BigWorldConditionService
    {
        private const int QuestStateReady = 1;      // native EQuestState
        private const int QuestStateInProgress = 2;
        private const int QuestStateFinished = 3;
        private const int StepStateFinished = 2;    // native EQuestStepState

        private static readonly Logger log = new(typeof(BigWorldConditionService), LogLevel.DEBUG, LogLevel.DEBUG);
        private static readonly Lazy<Dictionary<int, BigWorldConditionTable>> Conditions = new(() =>
            TableReaderV2.Parse<BigWorldConditionTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<int, int>> QuestIdByStepId = new(() =>
            TableReaderV2.Parse<DlcQuestStepTable>().ToDictionary(row => row.Id, row => row.QuestId));
        private static readonly Lazy<Dictionary<int, int>> QuestIdByObjectiveId = new(() =>
            TableReaderV2.Parse<DlcQuestObjectiveTable>().ToDictionary(row => row.Id, row => row.QuestId));

        // Types whose state belongs to a SkyGarden/Character slice (cafe 10203xxx, street 10202xxx,
        // drone 10204001, DIY part 23102). The owning slices register their evaluators on first use.
        private static readonly ConcurrentDictionary<int, Func<Player, IReadOnlyList<int>, bool>> Registered = new();
        private static readonly ConcurrentDictionary<int, byte> LoggedUnsupported = new();

        static BigWorldConditionService()
        {
            SkyGardenCafeModule.RegisterConditions();
            SkyGardenDroneModule.RegisterConditions();
            SkyGardenStreetModule.RegisterConditions();
            BigWorldCharacterModule.RegisterConditions();
        }

        internal static void Register(int type, Func<Player, IReadOnlyList<int>, bool> check)
        {
            if (!Registered.TryAdd(type, check))
                throw new InvalidOperationException($"BigWorld condition type {type} is already registered.");
        }

        internal static bool Exists(int conditionId) => Conditions.Value.ContainsKey(conditionId);

        // Unknown condition ids fail (client falls back to the main-game table; no BigWorld caller relies on that).
        internal static bool Check(Player player, int conditionId)
        {
            if (conditionId == 0)
                return true;
            if (!Conditions.Value.TryGetValue(conditionId, out BigWorldConditionTable? row))
            {
                Unsupported($"condition id {conditionId}");
                return false;
            }
            IReadOnlyList<int> p = row.Params ?? [];
            int P(int index) => index < p.Count ? p[index] : 0;
            BigWorldPlayerState state = player.BigWorldState;

            // Registered slice types include main-game-range ids (DIY part 23102), not only 10000000+.
            if (Registered.TryGetValue(row.Type, out var registered))
                return registered(player, p);

            switch (row.Type)
            {
                case 0:
                    return EvaluateFormula(player, row.Formula ?? string.Empty);
                // Main-game XConditionManager types used by BigWorldCondition rows.
                case 10101:
                    return player.PlayerData.Level >= P(0);
                case 10187:
                    return (P(0) == 1) == (player.PlayerData.GuideData?.Contains(P(1)) ?? false);
                // XBigWorldQuestConstAgency ConditionCheck* handlers.
                case 10101001:
                    return QuestFinished(state, P(0));
                case 10101002:
                    return StepFinished(state, P(0));
                case 10101003:
                    return QuestIdByObjectiveId.Value.TryGetValue(P(0), out int objectiveQuestId)
                        && (QuestFinished(state, objectiveQuestId)
                            || state.QuestData.ActiveQuests.TryGetValue(objectiveQuestId, out var objectiveQuest)
                                && objectiveQuest.DynamicData?.FinishedObjectiveIds.Contains(P(0)) == true);
                case 10101005:
                    return !QuestFinished(state, P(0))
                        && (QuestState(state, P(0)) is QuestStateInProgress or QuestStateReady
                            || state.QuestData.ReadyQuestIds.Contains(P(0)));
                case 10101007:
                    return p.Skip(1).TakeWhile(id => id > 0).Count(state.UnlockedInviteQuestResultIds.Contains) >= P(0);
                // XBigWorldMapAgency handlers.
                case 10101004:
                    return P(0) > 0 && P(1) > 0
                        && state.TeleporterData.TryGetValue(P(0), out List<int>? places) && places.Contains(P(1));
                case 10101009:
                    return P(0) > 0 && state.EnteredLevelIds.Contains(P(0)) == (P(1) == 1);
                // XBigWorldAgency:CheckParamMarkedCondition (invalid param id passes, as on the client).
                case 10101008:
                    return P(0) <= 0 || state.CustomParamMarkData.Contains(P(0)) == (P(1) == 1);
                default:
                    // 10101006 (quest map pin) is derived from client-side pin data; slice types unregistered.
                    Unsupported($"type {row.Type} (condition {conditionId})");
                    return false;
            }
        }

        private static int QuestState(BigWorldPlayerState state, int questId) =>
            state.QuestData.ActiveQuests.TryGetValue(questId, out var quest) ? quest.DynamicData?.QuestState ?? 0 : 0;

        private static bool QuestFinished(BigWorldPlayerState state, int questId) =>
            questId > 0 && (state.QuestData.FinishedQuests.Contains(questId) || QuestState(state, questId) == QuestStateFinished);

        private static bool StepFinished(BigWorldPlayerState state, int stepId)
        {
            if (!QuestIdByStepId.Value.TryGetValue(stepId, out int questId))
                return false;
            if (QuestFinished(state, questId))
                return true;
            return state.QuestData.ActiveQuests.TryGetValue(questId, out var quest)
                && quest.DynamicData is { } data
                && data.QuestState != QuestStateReady
                && data.Steps.TryGetValue(stepId, out var step)
                && step.StepState == StepStateFinished;
        }

        private static void Unsupported(string what)
        {
            if (LoggedUnsupported.TryAdd(what.GetHashCode(), 0))
                log.Warn($"BigWorld condition unsupported: {what}; evaluating as false.");
        }

        // Client XConditionFormula + XBigWorldConditionArithmetic: operands are condition ids, '&'/'|' share one
        // precedence level (left-associative), parentheses group.
        internal static bool EvaluateFormula(Player player, string formula) =>
            EvaluateFormula(formula, id => Check(player, id));

        internal static bool EvaluateFormula(string formula, Func<int, bool> check)
        {
            int pos = 0;
            bool result = Expression();
            if (pos != formula.Length)
                throw new FormatException($"Invalid BigWorld condition formula '{formula}'.");
            return result;

            bool Expression()
            {
                bool value = Operand();
                while (pos < formula.Length && formula[pos] is '&' or '|')
                {
                    char op = formula[pos++];
                    bool right = Operand();
                    value = op == '&' ? value && right : value || right;
                }
                return value;
            }

            bool Operand()
            {
                if (pos < formula.Length && formula[pos] == '(')
                {
                    pos++;
                    bool inner = Expression();
                    if (pos >= formula.Length || formula[pos++] != ')')
                        throw new FormatException($"Unbalanced BigWorld condition formula '{formula}'.");
                    return inner;
                }
                int start = pos;
                while (pos < formula.Length && char.IsDigit(formula[pos]))
                    pos++;
                if (start == pos)
                    throw new FormatException($"Invalid BigWorld condition formula '{formula}'.");
                return check(int.Parse(formula.AsSpan(start, pos - start)));
            }
        }
    }
}
