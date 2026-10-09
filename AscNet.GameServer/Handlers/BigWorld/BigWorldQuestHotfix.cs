using AscNet.Common.Util;
using AscNet.Table.V2.share.statussyncfight.quest;
using AscNet.Common.Database;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Server side of the client's XDlcQuestHotfixManager: retail ships 4 quest-objective hotfix scripts (stuck
    // interactions fixed by flipping an actor's interactable flag when the objective goes InProgress). The scripts are
    // imported as data (Scripts/import_bigworld_quest_hotfixes.py -> QuestObjectiveHotfix.tsv) and applied here.
    // Each script runs once per player (BigWorldState.ExecutedHotfixScriptIds), as the retail server records ids.
    internal static class BigWorldQuestHotfix
    {
        // Native XFightScriptProxy hook order: Enter(1) ScriptEnter(2) InProgress(3) Exit(4) ScriptExit(5).
        // Only OnStateInProgressFunc has a body in the shipped scripts.
        private const int InProgress = 3;

        private static readonly Lazy<ILookup<int, QuestObjectiveHotfixTable>> ByObjective = new(() =>
            TableReaderV2.Parse<QuestObjectiveHotfixTable>().ToLookup(row => row.ObjectiveId));

        internal static void OnObjectiveState(Session session, int objectiveId, int state)
        {
            if (state != InProgress) return;
            QuestObjectiveHotfixTable[] rows = ByObjective.Value[objectiveId].ToArray();
            if (rows.Length == 0) return;
            List<int> executed = session.player.BigWorldState.ExecutedHotfixScriptIds;
            foreach (var script in rows.GroupBy(row => row.ScriptId))
            {
                if (executed.Contains(script.Key)) continue;
                foreach (QuestObjectiveHotfixTable row in script)
                {
                    int actorType = row.TargetType switch
                    {
                        "Npc" => 1,
                        "SceneObject" => 2,
                        _ => throw new InvalidDataException($"QuestObjectiveHotfix {row.Id}: unknown TargetType '{row.TargetType}'.")
                    };
                    bool enable = row.Enable != 0;
                    // The scripts guard every call with IsInteractable: apply only when the flag differs.
                    if (BigWorldActors.IsInteractable(session, actorType, row.PlaceId) != enable)
                        BigWorldActors.SetInteractable(session, actorType, row.PlaceId, enable);
                }
                executed.Add(script.Key);
                session.player.Save();
            }
        }
    }
}
