using System.Text.RegularExpressions;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers.BigWorld;
using AscNet.Table.V2.share.bigworld.common.condition;
using AscNet.Table.V2.share.statussyncfight.level.sceneconfig;
using AscNet.Table.V2.share.statussyncfight.quest;
using Newtonsoft.Json.Linq;

namespace AscNet.Test
{
    internal partial class Program
    {
        // --big-world-quest-coverage-only: static audit of every Babylonia quest mechanic against what the server implements.
        // Inputs: all DlcQuest / DlcQuestStep / DlcQuestObjective rows (columns and Config JSON), every LevelInteractOption CompleteActionList,
        // the BigWorldConditions the quests use, and the XRpc surface of the 4.8 client (Fixtures/BigWorld/client_xrpcs.tsv, exported from the
        // dump by Scripts/export_bigworld_client_rpcs.py). "Consumed" is decided from the server source (comment-stripped, transitive through the
        // handler's helper methods), so a field nobody reads fails here instead of in a live playtest. Gaps that cannot be closed from the
        // dump/tables are listed in BigWorldQuestCoverage.KnownGaps with the evidence that would settle them; an unknown gap fails the selector,
        // and a known gap that disappears fails it too (remove the entry).
        private static void ValidateBigWorldQuestCoverage()
        {
            BigWorldQuestCoverage coverage = new();
            List<BigWorldQuestCoverage.Gap> gaps = coverage.Run();
            foreach (IGrouping<string, BigWorldQuestCoverage.Gap> group in gaps.GroupBy(g => g.Category).OrderBy(g => g.Key))
                Console.WriteLine($"  {group.Key}: {group.Count()} gap(s){(group.All(g => BigWorldQuestCoverage.KnownGaps.ContainsKey(g.Key)) ? " (all known)" : "")}");
            Console.WriteLine($"big world quest coverage: {coverage.Checked} items checked, {gaps.Count} gap(s), {gaps.Count(g => BigWorldQuestCoverage.KnownGaps.ContainsKey(g.Key))} known");
            foreach (BigWorldQuestCoverage.Gap gap in gaps.Where(g => BigWorldQuestCoverage.KnownGaps.ContainsKey(g.Key)))
                Console.WriteLine($"  KNOWN {gap.Key}: {gap.Detail}");
            AssertEqual("", string.Join("\n", gaps.Where(g => !BigWorldQuestCoverage.KnownGaps.ContainsKey(g.Key)).Select(g => $"{g.Key}: {g.Detail}")), "unimplemented or unconsumed quest mechanics (fix, or add to KnownGaps with evidence)");
            AssertEqual("", string.Join(",", BigWorldQuestCoverage.KnownGaps.Keys.Where(k => gaps.All(g => g.Key != k))), "known gaps that are closed now (remove from KnownGaps)");
            Console.WriteLine("big world quest coverage: ok");
        }

        internal sealed class BigWorldQuestCoverage
        {
            internal sealed record Gap(string Category, string Item, string Detail)
            {
                internal string Key => $"{Category}|{Item}";
            }

            // Gap key -> what is missing and the evidence that would settle it. Empty when the server is complete.
            internal static readonly Dictionary<string, string> KnownGaps = new()
            {
                ["level-condition-policy|XConfigLevelConditionParamsCheckNpcInRange"] = "Evaluates false (4 FixProcessors, inverted: the fix re-sets the NPC to the very position tested). Benign/idempotent; "
                    + "a real check needs the tracked NPC pose (BigWorldActors.NpcPose) - safe to implement, left as policy.",
                ["action-param-unread|12000:AutoUnloadWhiteList"] = "LoadNpc.AutoUnloadWhiteList (157 uses, 155 empty, 1 non-empty): the server ignores it. The 4.8 dump only lists the field "
                    + "(XConfigLevelActionParamsLoadNpc +0x18); whether the engine auto-unloads what a quest action loaded when the objective/step/quest ends "
                    + "(and the list exempts actors) is not recoverable from the dump. Needs: a retail capture of a quest end that loaded an NPC with no later UnloadNpc.",
                ["action-param-unread|12001:AutoUnloadWhiteList"] = "LoadSceneObject.AutoUnloadWhiteList (159 uses): same open question as LoadNpc.AutoUnloadWhiteList (scene objects).",
                ["config-key-unconsumed|DlcQuest.Config.FolderId"] = "ClearPlaceArgs of type XConfigClearPlaceArgsSpecificLevelConfigGroupFolder (quest 4043 clears 路线 and NPC): the folder's members "
                    + "(editor folder 3000004 / 3000002 of config group 700302) are in no exported table, so ClearPlace resolves no actors for them. Needs: the level editor's folder "
                    + "membership export (scene bundle skygarden config group 700302), or a retail capture of 4043's ClearPlace pushes.",
                ["config-key-unconsumed|DlcQuest.Config.LevelConfigGroupId"] = "Same as FolderId (SpecificLevelConfigGroupFolder args, quest 4043).",
                ["config-key-unconsumed|DlcQuest.Config.NPC"] = "Quest 4043's ClearPlaceArgs entry named 'NPC' (a ClearId, not a field): resolves like FolderId.",
                ["config-key-unconsumed|DlcQuest.Config.ExcludedLevelPlays"] = "ClearPlaceArgs.ExcludedLevelPlays (3025, 4043): level plays the clear spares; no exported table maps a LevelPlay to its actors.",
                ["column-unconsumed|DlcQuestStep.IsFixedSaveBegin"] = "Step 402100 only: marks where the engine's fixed save of the instance begins (XTableDlcQuestStep +0x64). Needs: retail instance-save behaviour of Level 4021.",
                ["column-unconsumed|DlcQuestStep.IsFixedSaveEnd"] = "Step 402100 only (XTableDlcQuestStep +0x65): where the fixed save ends. Same evidence as IsFixedSaveBegin.",
                ["column-unconsumed|DlcQuestStep.OccupiableObjectIds"] = "5 steps list EActorType-less object ids (110 x4, 103) a step may occupy (XTableDlcQuestStep.OccupiableObjectIds +0x70, ELevelActionType "
                    + "FixReturnQuestOccupiedObjects 6002 returns them). The occupy mechanism has no server code and no capture shows it.",
                ["column-unconsumed|DlcQuestObjective.IsSuccessOrFailObjective"] = "14 jumper settle objectives (5001-5007 xx301/302/401/402): objective counts as a win/fail marker. "
                    + "XTableDlcQuestObjective +0x3D has no decoded consumer; the settle result comes from SettleInstLevel vars. Needs: client XQuestObjective reading of the flag (disasm xref) or a capture.",
                ["column-unconsumed|DlcQuestObjective.AutoSend"] = "ReadShortMessage.AutoSend (35 rows 1, 2 rows 0): the server delivers the message when the objective opens regardless (AscNet policy in EnterDone). "
                    + "Needs: retail behaviour of the AutoSend=0 rows (invite quests 2009/2015?) - delivered by a different trigger or not at all.",
                ["column-unconsumed|DlcQuestObjective.ItemsDeliverType"] = "DeliverItems.ItemsDeliverType Normal 1 / Manual 2 (3 rows): the server demands the exact RequiredItemInfoDict for both. "
                    + "Needs: a Manual-type RpcQuestDeliverItemsRequest capture (the one live dump holds a single request) to see whether the player may pick counts.",
            };

            // Objective type -> the server path that completes it (checked against ClientDetected and exercised end-to-end by the sweep).
            private static readonly Dictionary<int, string> ObjectivePaths = new()
            {
                [1] = "server: IsSatisfied(CheckRim) on OnConditionsChanged/Reevaluate",
                [2] = "client RpcDramaFinishNotify -> OnDramaFinish",
                [3] = "server: OnLevelEntered closes EnterLevel",
                [4] = "server: FinishInstLevel / SettleInstLevel -> OnInstanceComplete, LeaveInstLevel -> OnInstanceLeft",
                [5] = "client RpcPlayerInteractRequest -> OnInteract",
                [6] = "client BigWorldMessageReadRecordRequest -> OnMessageFinished",
                [7] = "client RpcQuestObjectiveCompleteRequest (ClientDetected)",
                [8] = "client RpcUiClosedNotify -> OnUiClosed",
                [9] = "client RpcTakePhotoCompleteNotify -> OnTakePhoto",
                [10] = "server: XLevelPlayTimer expiry -> OnTimerElapsed",
                [11] = "server: IsSatisfied(CheckIntVar) on SetVar",
                [12] = "client RpcNarrativeCompleteNotify -> OnNarrativeComplete",
                [13] = "client RpcPlayerInteractRequest on the scene object -> OnSceneObjectCollected",
                [14] = "client RpcQuestDeliverItemsRequest -> OnDeliverItems",
                [15] = "server: group spawn + XRpcNpcDie deaths (CorruptedSpawn)",
                [16] = "client XRpcNpcDie -> HandleNpcDeath",
                [18] = "client RpcQuestObjectiveCompleteRequest (ClientDetected)",
            };

            // Table columns the client reads itself (the server only stores or forwards the row): column -> evidence. A column the server neither
            // reads nor lists here is a gap. Evidence tags: [DUMP48] XTable field of the 4.8 dump read by the named client class, [LUA] 4.8 Lua.
            private static readonly Dictionary<string, string> ClientColumns = new()
            {
                ["DlcQuest.NameTextId"] = "[DUMP48] text id shown by XBigWorldQuestAgency UI",
                ["DlcQuest.DescTextId"] = "[DUMP48] text id shown by the quest UI",
                ["DlcQuest.FinishTipTextId"] = "[DUMP48] text id of the finish tip",
                ["DlcQuest.SystemUiStyleId"] = "[DUMP48] UI style of the quest tracker (client table DlcQuestTipStyle)",
                ["DlcQuest.IsDefaultTrack"] = "[DUMP48] client auto-tracks the quest",
                ["DlcQuest.TrackPriority"] = "[DUMP48] client tracker ordering",
                ["DlcQuest.PopViewType"] = "[DUMP48] client popup style on receive/finish",
                ["DlcQuest.IsManualPop"] = "[DUMP48] client popup trigger",
                ["DlcQuest.ShieldPopViewType"] = "[LUA] XBigWorldQuestModel QuestViewShieldTypeList: client popup shielding",
                ["DlcQuest.FirstStatusBarPlay"] = "[DUMP48] client status bar animation",
                ["DlcQuest.QuestIcon"] = "[DUMP48] client icon path",
                ["DlcQuest.QuestBanner"] = "[DUMP48] client banner path",
                ["DlcQuestStep.StepTextId"] = "[DUMP48] step text shown by the client tracker",
                ["DlcQuestStep.LocationTextId"] = "[DUMP48] step location text",
                ["DlcQuestObjective.TitleTextId"] = "[DUMP48] objective text",
                ["DlcQuestObjective.DescriptionTextId"] = "[DUMP48] objective text",
                ["DlcQuestObjective.SkipUiRefreshEffect"] = "[DUMP48] client tracker effect flag",
                ["DlcQuestObjective.DeliverTitleTextId"] = "[DUMP48] UiBigWorldPopupDelivery text (client opens the popup itself)",
                ["DlcQuestObjective.DeliverDescTextId"] = "[DUMP48] UiBigWorldPopupDelivery text",
                ["DlcQuestObjective.DeliverSubTitleTextId"] = "[DUMP48] UiBigWorldPopupDelivery text",
                ["DlcQuestObjective.DeliverBehaviorDescTextId"] = "[DUMP48] UiBigWorldPopupDelivery text",
                ["DlcQuestObjective.DeliverBtnTextId"] = "[DUMP48] UiBigWorldPopupDelivery text",
                ["DlcQuestObjective.ReachDistance"] = "[DUMP48] XQuestObjectiveReachTargetPosition: the client detects arrival (ClientDetected 7)",
                ["DlcQuestObjective.IsNpcReach"] = "[DUMP48] XQuestObjectiveReachTargetPosition: reach by an NPC instead of the player (client detected)",
                ["DlcQuestObjective.CanJumpToEnter"] = "[DUMP48] EnterLevel objective: the client's map offers a jump button",
                ["DlcQuestObjective.IsRecordDecisionSelect"] = "[DUMP48] XQuestObjectiveDramaPlayFinish: the client drama player records decision clips",
                ["DlcQuestObjective.SearchInfoId"] = "[DUMP48] XQuestObjectiveScanPlusSearchComplete: the client's XScanPlusAbility search state for this id completes the objective (ClientDetected 18)",
                ["DlcQuestObjective.PlayerNpcTrialId"] = "[DUMP48] XTableQuestObjectiveDramaPlayFinish: the client picks the drama's player NPC",
            };

            // Config JSON keys (objective/step/quest) handled by the client: key -> evidence.
            private static readonly Dictionary<string, string> ClientConfigKeys = new()
            {
                ["RecordDecisionClipSelect"] = "[DUMP48] XTableQuestObjectiveDramaPlayFinish.RecordDecisionClipSelect: client drama player decision clips",
                ["IntKey"] = "[DATA] 162 of 162 XConfigQuestVarRefToken.IntKey values are 0 (alternative var key, unused)",
                ["TipDescId"] = "[DATA] null in 51 of 51 condition entries (XConfigLevelCondition UI tip)",
                ["CondType"] = "[DUMP48] ELevelConditionType mirrors the Params $type class that selects the evaluator",
                ["StatisticType"] = "[DUMP48] mirrors the XConfigQuestStatisticItem* $type that selects the rule",
                ["FixTiming"] = "[DATA] 20 of 20 equal the FixProcessors dictionary key the server already reads",
                ["PhotoFilterId"] = "[DATA] 0 in 27 of 27 (FinalShot.PhotoFilterId; the objective column PhotoFilterId is the filter the server checks)",
                ["FinalShot"] = "[DUMP48] XTableQuestObjectiveTakePhotoComplete.FinalShot: client photo mode final-shot rule",
            };

            // Params keys of ServerThenClient (3) action types that only the client half reads (the server half runs XLevelAction<T> server side):
            // "type:key" -> evidence ([DUMP48] XConfigLevelActionParams<T> field read by XLevelAction<T>'s client execute).
            private static readonly Dictionary<string, string> ClientHalfKeys = new()
            {
                ["3003:IsLimit"] = "[DUMP48] XLevelActionControlSystemFunction client half applies the function control",
                ["3003:LimitType"] = "[DUMP48] XLevelActionControlSystemFunction client half applies the function control",
                ["20000:WithBlackScreen"] = "[DUMP48] XLevelActionTeleportPlayer client half: black screen (the runner blocks on it)",
                ["20000:ScreenEffectId"] = "[DUMP48] XLevelActionTeleportPlayer client half: screen effect",
                ["20000:BlackScreenEnterDuration"] = "[DUMP48] XLevelActionTeleportPlayer client half",
                ["20000:BlackScreenExitDuration"] = "[DUMP48] XLevelActionTeleportPlayer client half",
                ["20000:ShowEffect"] = "[DUMP48] XLevelActionTeleportPlayer client half",
                ["20000:ResetCamera"] = "[DUMP48] XLevelActionTeleportPlayer client half",
                ["14001:IsFollowPlayer"] = "[DUMP48] XLevelActionNpcRelativeFollowMove client half (the NPC controller's follow component)",
                ["14001:FollowTargetNpcPlaceId"] = "[DUMP48] client half", ["14001:TargetAngle"] = "[DUMP48] client half", ["14001:TargetRadius"] = "[DUMP48] client half",
                ["14001:ChaseRadius"] = "[DUMP48] client half", ["14001:NormalFollowRadius"] = "[DUMP48] client half", ["14001:MaxIdleLagDistance"] = "[DUMP48] client half",
                ["14001:IdleLookAtTargetDelayTime"] = "[DUMP48] client half", ["14001:UseNavMesh"] = "[DUMP48] client half", ["14001:StartFollowDelayTime"] = "[DUMP48] client half",
                ["14001:EnableForceTeleport"] = "[DUMP48] client half", ["14001:TeleportRange"] = "[DUMP48] client half",
                ["14004:WaitComplete"] = "[DUMP48] XLevelActionNpcGuideMoveTo client half (the runner blocks on it)", ["14004:UseNavMesh"] = "[DUMP48] client half",
                ["14004:OutOfRouteRange"] = "[DUMP48] client half", ["14004:StartGuideRange"] = "[DUMP48] client half", ["14004:ReachTargetPositionRange"] = "[DUMP48] client half",
                ["14004:WaitDramaCaptionName"] = "[DUMP48] client half", ["14004:WaitDramaCaptionPlayProbability"] = "[DUMP48] client half", ["14004:IdleTurningDelayTime"] = "[DUMP48] client half",
                ["14004:GuideMoveTypeMatchesTarget"] = "[DUMP48] client half", ["14004:GuideMoveType"] = "[DUMP48] client half",
                ["14008:IsPlayerBecomeFollower"] = "[DUMP48] XLevelActionNpcTetherFollowMove client half", ["14008:IsFollowPlayer"] = "[DUMP48] client half",
                ["14008:FollowTargetNpcPlaceId"] = "[DUMP48] client half", ["14008:LeadExpRotRadius"] = "[DUMP48] client half", ["14008:ExpReachTime"] = "[DUMP48] client half",
                ["14008:NpcAnimAlignRadius"] = "[DUMP48] client half", ["14008:ChaseRadius"] = "[DUMP48] client half", ["14008:EnableForceTeleport"] = "[DUMP48] client half",
                ["14008:TeleportRange"] = "[DUMP48] client half", ["14008:HandType"] = "[DUMP48] client half", ["14008:CloneTrialNpcId"] = "[DUMP48] client half",
                ["19005:SettleType"] = "[DUMP48] XLevelActionSettleInstLevel client half opens the settle UI", ["19005:Theme"] = "[DUMP48] client half", ["19005:InputVars"] = "[DUMP48] client half",
                ["19000:Active"] = "[DUMP48] XLevelActionSetNpcQuestTipIconActive client half",
            };

            // One ELevelActionType with several Params subclasses: type -> class name prefix [DUMP48 XConfigLevelActionParamsControlSystemFunction + Task/Map/ShortMsg/FirstPerson].
            private static readonly Dictionary<int, string> ParamsFamilies = new() { [3003] = "XConfigLevelActionParamsControlSystemFunction" };

            private static readonly string Root = FindRoot();
            private readonly List<Gap> gaps = [];
            internal int Checked;

            private static string FindRoot()
            {
                for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "AscNet.sln")))
                        return dir.FullName;
                throw new DirectoryNotFoundException("AscNet.sln not found above the test binary.");
            }

            private void Fail(string category, string item, string detail) => gaps.Add(new Gap(category, item, detail));

            private static string StripComments(string text) => Regex.Replace(text, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

            private static int I(object? value) => value is null ? 0 : Convert.ToInt32(value);

            private static readonly Lazy<Dictionary<string, string>> Sources = new(() =>
                Directory.EnumerateFiles(Path.Combine(Root, "AscNet.GameServer"), "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    .ToDictionary(f => Path.GetRelativePath(Root, f), f => StripComments(File.ReadAllText(f))));

            private static IEnumerable<string> BigWorldSources => Sources.Value.Where(kv => kv.Key.Contains("Handlers/BigWorld/")).Select(kv => kv.Value);

            private static bool Mentions(string source, string name) => Regex.IsMatch(source, $@"(\.\s*{Regex.Escape(name)}\b)|(""{Regex.Escape(name)}"")|(\b{Regex.Escape(name)}\s*=[^=])");

            // method name -> bodies (every static/instance method of the BigWorld sources, brace matched).
            private static readonly Lazy<Dictionary<string, List<string>>> Methods = new(() =>
            {
                Dictionary<string, List<string>> methods = [];
                Regex header = new(@"(?:private|internal|public)\s+(?:static\s+)?[\w<>\[\],\.\?\(\) ]+?\s+(\w+)\s*\([^;{}]*\)\s*(?:=>|\{)", RegexOptions.Compiled);
                foreach (string source in BigWorldSources)
                    foreach (Match m in header.Matches(source))
                    {
                        int start = m.Index + m.Length - 1, end = start;
                        if (source[start] == '{')
                        {
                            for (int depth = 0; end < source.Length; end++)
                            {
                                if (source[end] == '{') depth++;
                                else if (source[end] == '}' && --depth == 0) break;
                            }
                        }
                        else
                            end = source.IndexOf(';', start);
                        if (end < 0)
                            continue;
                        if (!methods.TryGetValue(m.Groups[1].Value, out List<string>? list))
                            methods[m.Groups[1].Value] = list = [];
                        list.Add(source[start..Math.Min(end + 1, source.Length)]);
                    }
                return methods;
            });

            // action type -> (registration text) for the server handler table (t[1001] = ActivateTeleporter;).
            private static readonly Lazy<Dictionary<int, string>> HandlerText = new(() =>
            {
                Dictionary<int, string> map = [];
                foreach (string source in BigWorldSources)
                    foreach (Match m in Regex.Matches(source, @"\bt\[(\d+)\]\s*=\s*"))
                    {
                        int end = m.Index + m.Length, depth = 0;
                        for (; end < source.Length; end++)
                        {
                            if (source[end] is '(' or '{') depth++;
                            else if (source[end] is ')' or '}') depth--;
                            else if (source[end] == ';' && depth <= 0) break;
                        }
                        map[int.Parse(m.Groups[1].Value)] = source[(m.Index + m.Length)..end];
                    }
                return map;
            });

            // String literals reachable from a handler: its registration text and, transitively, the bodies of the helper methods it calls.
            private static HashSet<string> LiteralsOf(string text)
            {
                HashSet<string> literals = [], seen = [];
                Queue<(string Text, int Depth)> queue = new([(text, 0)]);
                while (queue.TryDequeue(out (string Text, int Depth) item))
                {
                    foreach (Match m in Regex.Matches(item.Text, "\"([A-Za-z_][A-Za-z0-9_]*)\""))
                        literals.Add(m.Groups[1].Value);
                    if (item.Depth >= 5)
                        continue;
                    foreach (Match m in Regex.Matches(item.Text, @"\b([A-Za-z_]\w*)\s*[\(,\)]|=>\s*([A-Za-z_]\w*)\b|^\s*([A-Za-z_]\w*)\s*$"))
                        foreach (string name in m.Groups.Cast<Group>().Skip(1).Where(g => g.Success).Select(g => g.Value))
                            if (Methods.Value.TryGetValue(name, out List<string>? bodies) && seen.Add(name))
                                foreach (string body in bodies)
                                    queue.Enqueue((body, item.Depth + 1));
                }
                return literals;
            }

            internal List<Gap> Run()
            {
                CheckActions();
                CheckObjectiveTypes();
                CheckColumns();
                CheckConditions();
                CheckPlaceReferences();
                CheckRpcs();
                return gaps;
            }

            #region Level actions

            // (action type, $type, params, where) of every action list: objective Enter/Exit/FixProcessors and LevelInteractOption.CompleteActionList.
            private static IEnumerable<(int Type, string ParamsType, JObject Params, string Where, int Level)> AllActions()
            {
                foreach (DlcQuestObjectiveTable o in TableReaderV2.Parse<DlcQuestObjectiveTable>())
                {
                    if (string.IsNullOrWhiteSpace(o.Config))
                        continue;
                    JObject config = JObject.Parse(o.Config);
                    IEnumerable<JToken> lists = new[] { config["EnterActions"], config["ExitActions"] }.Where(t => t is not null)!
                        .Concat((config["FixProcessors"] as JObject)?.Properties().SelectMany(p => p.Value.Select(fix => fix["Actions"])).Where(t => t is not null)! ?? []);
                    foreach (JToken list in lists)
                        foreach (JToken action in list)
                            yield return (action.Value<int>("ActionType"), action["Params"]?.Value<string>("$type") ?? "", (JObject)action["Params"]!, $"objective {o.Id}", I(o.LevelId));
                }
                foreach (LevelInteractOptionTable option in TableReaderV2.Parse<LevelInteractOptionTable>())
                {
                    if (string.IsNullOrWhiteSpace(option.Config) || JObject.Parse(option.Config)["CompleteActionList"] is not JArray list)
                        continue;
                    foreach (JToken action in list)
                        yield return (action.Value<int>("ActionType"), action["Params"]?.Value<string>("$type") ?? "", (JObject)action["Params"]!, $"option {option.Sector}/{option.ConfigGroupId}/{option.PlaceId}/{option.OptionId}", 0);
                }
            }

            private void CheckActions()
            {
                Dictionary<int, LevelActionExecModeTable> modes = TableReaderV2.Parse<LevelActionExecModeTable>().ToDictionary(r => r.ActionType);
                foreach (IGrouping<int, (int Type, string ParamsType, JObject Params, string Where, int Level)> group in AllActions().GroupBy(a => a.Type).OrderBy(g => g.Key))
                {
                    Checked++;
                    string example = group.First().Where;
                    if (!modes.TryGetValue(group.Key, out LevelActionExecModeTable? mode))
                    {
                        Fail("action-no-execmode", $"{group.Key}", $"{group.Count()} uses, e.g. {example}");
                        continue;
                    }
                    string? mismatch = group.Select(a => a.ParamsType).Distinct().FirstOrDefault(t => t != mode.Params && !(ParamsFamilies.TryGetValue(group.Key, out string? family) && t.StartsWith(family)));
                    if (mismatch is not null)
                        Fail("action-params-class", $"{group.Key}", $"config uses {mismatch}, ExecMode table says {mode.Params}");
                    // ExecMode 1 ServerOnly / 3 ServerThenClient need a server handler; 0 (Any) is only WaitTime; 2 ClientOnly is the client's.
                    if (mode.ExecMode is 1 or 3 && !BigWorldActionHandlers.Table.ContainsKey(group.Key))
                        Fail("action-no-handler", $"{group.Key}", $"{mode.Params} ExecMode {mode.ExecMode}: {group.Count()} uses, e.g. {example}");
                    if (mode.ExecMode == 0 && group.Key != 23000)
                        Fail("action-any-mode", $"{group.Key}", $"{mode.Params} has ExecMode Any but only WaitTime has a runner path");
                    if (mode.ExecMode is not (1 or 3) || !HandlerText.Value.TryGetValue(group.Key, out string? handler))
                        continue;
                    // Every config key of a server-run action must be read by the server (or, for ServerThenClient, belong to the client half).
                    HashSet<string> read = LiteralsOf(handler);
                    foreach (IGrouping<string, string> key in group.SelectMany(a => a.Params.Properties().Select(p => p.Name)).Where(k => k != "$type").GroupBy(k => k))
                    {
                        Checked++;
                        if (!read.Contains(key.Key) && !(mode.ExecMode == 3 && ClientHalfKeys.ContainsKey($"{group.Key}:{key.Key}")))
                            Fail("action-param-unread", $"{group.Key}:{key.Key}", $"{mode.Params}.{key.Key}: {key.Count()} uses, ExecMode {mode.ExecMode}, e.g. {example}");
                    }
                }
            }

            #endregion

            #region Objective types

            private void CheckObjectiveTypes()
            {
                int[] clientDetected = (int[])typeof(BigWorldQuestRuntime).GetField("ClientDetected", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
                foreach (IGrouping<int, DlcQuestObjectiveTable> group in TableReaderV2.Parse<DlcQuestObjectiveTable>().GroupBy(o => I(o.ObjectiveType)).OrderBy(g => g.Key))
                {
                    Checked++;
                    if (!ObjectivePaths.TryGetValue(group.Key, out string? path))
                        Fail("objective-type", $"{group.Key}", $"{group.Count()} objectives (e.g. {group.First().Id}) have no completion path");
                    else if (path.EndsWith("(ClientDetected)") && !clientDetected.Contains(group.Key))
                        Fail("objective-detect", $"{group.Key}", $"path says '{path}' but RpcQuestObjectiveCompleteRequest is not accepted for it");
                }
            }

            #endregion

            #region Columns and Config keys

            private void CheckColumns()
            {
                CheckTable("DlcQuest", TableReaderV2.Parse<DlcQuestTable>().Select(r => (r.Id, (object)r, r.Config)));
                CheckTable("DlcQuestStep", TableReaderV2.Parse<DlcQuestStepTable>().Select(r => (r.Id, (object)r, r.Config)));
                CheckTable("DlcQuestObjective", TableReaderV2.Parse<DlcQuestObjectiveTable>().Select(r => (r.Id, (object)r, r.Config)));
            }

            private void CheckTable(string table, IEnumerable<(int Id, object Row, string? Config)> rows)
            {
                List<(int Id, object Row, string? Config)> all = rows.ToList();
                foreach (System.Reflection.PropertyInfo property in all[0].Row.GetType().GetProperties().Where(p => p.Name is not ("Config" or "Id")))
                {
                    List<int> used = all.Where(r => property.GetValue(r.Row) is { } value && IsSet(value)).Select(r => r.Id).ToList();
                    if (used.Count == 0)
                        continue;
                    Checked++;
                    if (!BigWorldSources.Any(s => Mentions(s, property.Name)) && !ClientColumns.ContainsKey($"{table}.{property.Name}"))
                        Fail("column-unconsumed", $"{table}.{property.Name}", $"{used.Count} non-empty rows (e.g. {string.Join(",", used.Take(3))}) read by no server code and not a known client-only column");
                }
                // Config JSON: every key at any depth (action lists excluded: CheckActions) must be read by the server or be a client key.
                Dictionary<string, List<int>> keys = [];
                foreach ((int id, _, string? config) in all.Where(r => !string.IsNullOrWhiteSpace(r.Config)))
                    foreach (string key in ConfigKeys(JToken.Parse(config!)))
                    {
                        if (!keys.TryGetValue(key, out List<int>? ids))
                            keys[key] = ids = [];
                        ids.Add(id);
                    }
                foreach ((string key, List<int> ids) in keys.OrderBy(k => k.Key))
                {
                    Checked++;
                    if (!BigWorldSources.Any(s => s.Contains($"\"{key}\"")) && !ClientConfigKeys.ContainsKey(key))
                        Fail("config-key-unconsumed", $"{table}.Config.{key}", $"{ids.Count} rows (e.g. {string.Join(",", ids.Take(3))}) carry it; no server code reads it and it is not a known client key");
                }
            }

            private static bool IsSet(object value) => value switch
            {
                string s => s.Length > 0,
                bool b => b,
                System.Collections.IEnumerable list => list.Cast<object?>().Any(v => v is not null && IsSet(v)),
                _ => Convert.ToDouble(value) != 0,
            };

            // Property names under a Config document, skipping action parameter blobs (checked per action type) and non-identifier keys (ids as keys).
            private static IEnumerable<string> ConfigKeys(JToken token)
            {
                switch (token)
                {
                    case JObject o:
                        foreach (JProperty p in o.Properties())
                        {
                            if (p.Name is "$type" or "Params" || !Regex.IsMatch(p.Name, "^[A-Za-z_]"))
                            {
                                foreach (string nested in ConfigKeys(p.Value)) yield return nested;
                                continue;
                            }
                            yield return p.Name;
                            if (p.Name is not ("EnterActions" or "ExitActions" or "Actions"))
                                foreach (string nested in ConfigKeys(p.Value)) yield return nested;
                        }
                        break;
                    case JArray a:
                        foreach (JToken item in a)
                            foreach (string nested in ConfigKeys(item)) yield return nested;
                        break;
                }
            }

            #endregion

            #region Conditions

            private void CheckConditions()
            {
                // XConfigLevelCondition* types in step PreLevelConditionGroup and FixProcessors ConditionGroup must be evaluated by CheckCondition.
                string runtime = File.ReadAllText(Path.Combine(Root, "AscNet.GameServer", "Handlers", "BigWorld", "BigWorldQuestRuntime.cs"));
                Dictionary<string, List<string>> levelConditions = [];
                void Collect(string? config, string where)
                {
                    if (string.IsNullOrWhiteSpace(config))
                        return;
                    foreach (JToken p in JObject.Parse(config).DescendantsAndSelf().OfType<JObject>().Where(o => o["$type"]?.Value<string>() is { } t && t.StartsWith("XConfigLevelConditionParams")))
                    {
                        string type = p.Value<string>("$type")!;
                        if (!levelConditions.TryGetValue(type, out List<string>? list))
                            levelConditions[type] = list = [];
                        list.Add(where);
                    }
                }
                foreach (DlcQuestStepTable step in TableReaderV2.Parse<DlcQuestStepTable>())
                    Collect(step.Config, $"step {step.Id}");
                foreach (DlcQuestObjectiveTable o in TableReaderV2.Parse<DlcQuestObjectiveTable>())
                    Collect(o.Config, $"objective {o.Id}");
                foreach ((string type, List<string> where) in levelConditions.OrderBy(k => k.Key))
                {
                    Checked++;
                    Match branch = Regex.Match(runtime, $"case \"{type}\":(?<body>.*?)(?=case \"|default:)", RegexOptions.Singleline);
                    if (!branch.Success)
                        Fail("level-condition-unsupported", type, $"{where.Count} uses (e.g. {where[0]}) fall to the default branch (false)");
                    else if (Regex.IsMatch(branch.Groups["body"].Value.Trim(), @"return false;$") && branch.Groups["body"].Value.Contains("AscNet policy"))
                        Fail("level-condition-policy", type, $"{where.Count} uses (e.g. {where[0]}): evaluated by an AscNet policy constant, not the real condition");
                }

                // BigWorldCondition ids used by quests (objective ConditionIds, DlcQuest.Condition) and the formulas they expand to: type handled?
                Dictionary<int, BigWorldConditionTable> table = TableReaderV2.Parse<BigWorldConditionTable>().ToDictionary(c => c.Id);
                HashSet<int> used = [];
                void Expand(int id)
                {
                    if (id == 0 || !used.Add(id) || !table.TryGetValue(id, out BigWorldConditionTable? row) || row.Type != 0)
                        return;
                    foreach (Match m in Regex.Matches(row.Formula ?? "", @"\d+"))
                        Expand(int.Parse(m.Value));
                }
                foreach (DlcQuestObjectiveTable o in TableReaderV2.Parse<DlcQuestObjectiveTable>())
                    foreach (int id in o.ConditionIds ?? [])
                        Expand(id);
                foreach (DlcQuestTable q in TableReaderV2.Parse<DlcQuestTable>())
                    Expand(q.Condition);
                string service = Sources.Value.Single(kv => kv.Key.EndsWith("BigWorldConditionService.cs")).Value;
                HashSet<int> handled = Regex.Matches(service, @"case (\d+):").Select(m => int.Parse(m.Groups[1].Value))
                    .Concat(Sources.Value.Values.SelectMany(s => Regex.Matches(s, @"BigWorldConditionService\.Register\((\d+)").Select(m => int.Parse(m.Groups[1].Value)))).ToHashSet();
                // Registered types given as named constants.
                foreach (string source in Sources.Value.Values)
                    foreach (Match m in Regex.Matches(source, @"BigWorldConditionService\.Register\((\w+)"))
                        if (!int.TryParse(m.Groups[1].Value, out _) && Regex.Match(source, $@"\b{m.Groups[1].Value}\s*=\s*(\d+)") is { Success: true } constant)
                            handled.Add(int.Parse(constant.Groups[1].Value));
                foreach (IGrouping<int, BigWorldConditionTable> group in used.Where(table.ContainsKey).Select(id => table[id]).Where(r => r.Type != 0).GroupBy(r => r.Type).OrderBy(g => g.Key))
                {
                    Checked++;
                    if (!handled.Contains(group.Key))
                        Fail("condition-type", $"{group.Key}", $"{group.Count()} conditions used by quests (e.g. {group.First().Id}) have no evaluator (always false)");
                }
                foreach (int id in used.Where(id => !table.ContainsKey(id)))
                    Fail("condition-id", $"{id}", "quest references a BigWorldCondition row that does not exist");
            }

            #endregion

            #region Place references

            // Every actor a quest loads, unloads, enables or targets exists in the objective's level (LevelNpc / LevelSceneObject of its config groups).
            private void CheckPlaceReferences()
            {
                bool NpcExists(int level, int place) => level <= 0 || BigWorldModule.LevelNpcOf(level, place) is not null;
                bool ObjectExists(int level, int place) => level <= 0 || BigWorldModule.SceneObjectsOf(level).ContainsKey(place);
                foreach (DlcQuestObjectiveTable o in TableReaderV2.Parse<DlcQuestObjectiveTable>())
                {
                    if (string.IsNullOrWhiteSpace(o.Config))
                        continue;
                    JObject config = JObject.Parse(o.Config);
                    int level = I(o.LevelId);
                    foreach (JToken action in new[] { "EnterActions", "ExitActions" }.SelectMany(k => config[k] as JArray ?? [])
                        .Concat((config["FixProcessors"] as JObject)?.Properties().SelectMany(p => p.Value.SelectMany(fix => fix["Actions"] ?? new JArray())) ?? []))
                    {
                        int type = action.Value<int>("ActionType");
                        JToken p = action["Params"]!;
                        void Npc(int place, string what) { Checked++; if (!NpcExists(level, place)) Fail("place-missing", $"npc:{level}:{place}", $"{what} (objective {o.Id}) names NPC place {place}, level {level} has no such LevelNpc"); }
                        void Obj(int place, string what) { Checked++; if (!ObjectExists(level, place)) Fail("place-missing", $"object:{level}:{place}", $"{what} (objective {o.Id}) names scene object {place}, level {level} has none"); }
                        switch (type)
                        {
                            case 12000 or 21001: foreach (int place in p["PlaceIdList"]!.Values<int>()) Npc(place, $"action {type}"); break;
                            case 12001 or 21002: foreach (int place in p["PlaceIdList"]!.Values<int>()) Obj(place, $"action {type}"); break;
                            case 5001: if (p.Value<int>("ActorType") == 1) Npc(p.Value<int>("PlaceId"), "EnableActorInteractable"); else Obj(p.Value<int>("PlaceId"), "EnableActorInteractable"); break;
                            case 14000 or 14001 or 14003 or 14004 or 14008 or 14009 or 14010: Npc(p.Value<int>("NpcPlaceId"), $"action {type}"); break;
                            case 6003: Npc(p.Value<int>("PlaceId"), "FixSetNpcPosition"); break;
                        }
                    }
                    if (I(o.ObjectiveType) == 5 && config["TargetArgs"] is JObject targets)
                        foreach (JProperty type in targets.Properties())
                            foreach (JProperty place in ((JObject)type.Value).Properties())
                            {
                                Checked++;
                                bool exists = type.Name == "1" ? NpcExists(level, int.Parse(place.Name)) : ObjectExists(level, int.Parse(place.Name));
                                if (!exists)
                                    Fail("place-missing", $"target:{level}:{type.Name}:{place.Name}", $"interact objective {o.Id} targets actor type {type.Name} place {place.Name}, level {level} has none");
                            }
                    if (I(o.ObjectiveType) == 16)
                        foreach (object place in o.ExistsEnemyList ?? [])
                        {
                            Checked++;
                            if (!NpcExists(level, I(place)))
                                Fail("place-missing", $"enemy:{level}:{I(place)}", $"kill objective {o.Id} lists enemy place {I(place)}, level {level} has no such LevelNpc");
                        }
                }
            }

            #endregion

            #region XRpc

            // XRpc names the client sends in BigWorld: dump protocols without a client handler plus the names live dumps showed. Each is handled by
            // name in the server or acknowledged on purpose (engine state the client simulates; retail acks every client XRpc with Code 0).
            private static readonly string[] ObservedClientRpcs =
            [
                "RpcSetIgnoreActorCollisionRequest", "XRpcDisableNpcLookAt", "XRpcNpcEnableNpcLookAt", "XRpcNpcSetActiveNotify", "XRpcNpcSetCamp",
                "XRpcTeleportResetOnGround", "RpcDramaFinishNotify", "RpcEcologyConstructClearStateRequest", "RpcLevelActionClientExecuteFinish",
                "RpcNarrativeCompleteNotify", "RpcPauseFight", "RpcPlayerEnterLevelComplete", "RpcPlayerInteractRequest", "RpcPlayerSwitchLevelRequest",
                "RpcQuestDeliverItemsRequest", "RpcQuestObjectiveCompleteRequest", "RpcQuestPopupClosedNotify", "RpcRLObjectLoadCompleted", "RpcResumeFight",
                "RpcSetNavPointActive", "RpcShortMessageReadComplete", "RpcSwitchPlayerNpcRequest", "RpcSwitchPlayerSelfNpcIn", "RpcSwitchPlayerSelfNpcOut",
                "RpcTakePhotoCompleteNotify", "RpcUiClosedNotify", "RepBuff", "RepThreatComponent", "RpcAddAttribAdditive", "RpcChangeActorScannedStateNotify",
                "RpcRemoveBuff", "RpcSceneObjectSetActive", "RpcSceneObjectSetActiveRequest", "RpcSetInteractableCmpEnableNotify", "RpcTriggerInteractNotify",
                "XRpcNpcPositionAndRotation", "XRpcNpcDie", "XRpcNpcDieRequest", "XRpcNpcDeath",
            ];

            // Names deliberately left to the generic acknowledgement, grouped by reason.
            private static readonly (string Pattern, string Reason)[] AckedRpcs =
            [
                (@"^Rpc(Gameplay|Mouse|Heavy|MultiParry|Drop)\w*$", "other minigames (not Babylonia quests): never sent inside BigWorld [DUMP48 XGameplay* handlers]"),
                (@"^(Rpc|XRpc)(SetIgnoreActorCollision\w*|DisableNpcLookAt|NpcEnableNpcLookAt|NpcSetActiveNotify|NpcSetCamp|TeleportResetOnGround\w*|NpcPositionAndRotation|PauseFight|ResumeFight|EcologyConstructClearState\w*|Add(Attrib|Time)\w*|AddBuff|RemoveBuff|NewMissile|NewSkillBall\w*|PartChangeAttrib|SyncFreeAimTarget|CastSkillAction\w*|BindNpcEffect|UnBindNpcEffect|BindSceneObjectEffect|UnBindSceneObjectEffect|SetSystemFuncEntryEnableRequest|LevelSwitchSceneTimeline|OpenInstLevelSettleUiRequest|KillScreenEffectRequest|ResetCameraRequest|TakePhotoSilent|TakePhotoSilentCompleteNotify|RegisterVarSyncNote|UnregisterVarSyncNote)$", "engine state the client simulates (combat, effects, camera); retail acks with Code 0 [CAP babylonia.pcap]"),
                (@"^XRpcNpc(OnAir|SpecialState|SwitchHitAction)$", "NPC engine state the controlling client simulates (no client handler: the client is the sender); retail acks Code 0"),
                (@"^(RepBuff|RepThreatComponent|RpcSceneObjectSetActive|RpcSceneObjectSetActiveRequest|RpcSetInteractableCmpEnableNotify|RpcSwitchPlayerSelfNpcIn|RpcSwitchPlayerSelfNpcOut|RpcSetNavPointActive|RpcShortMessageReadComplete|RpcAddTrialNpcToTeamRequest|RpcRemoveTrialNpcFromTeamRequest)$", "client echoes of server-owned state or one-way notifications the server already holds [CAP live dumps]"),
            ];

            private void CheckRpcs()
            {
                string source = string.Join("\n", BigWorldSources);
                Dictionary<string, (string Envelope, string Kind)> protocols = File.ReadAllLines(Path.Combine(Root, "AscNet.Test", "Fixtures", "BigWorld", "client_xrpcs.tsv"))
                    .Select(l => l.Split('\t')).ToDictionary(c => c[1], c => (c[0], c[2]));
                IEnumerable<string> clientSent = ObservedClientRpcs.Concat(protocols.Where(p => p.Value.Kind == "sent" && !p.Key.StartsWith("Rep")).Select(p => p.Key)).Distinct().Order();
                foreach (string name in clientSent)
                {
                    Checked++;
                    bool byName = source.Contains($"\"{name}\"");
                    bool acked = AckedRpcs.Any(a => Regex.IsMatch(name, a.Pattern));
                    if (!byName && !acked)
                        Fail("client-rpc", name, "sent by the client (dump/live), neither handled by name nor listed as deliberately acknowledged");
                    if (!protocols.ContainsKey(name) && !name.StartsWith("Rep"))
                        Fail("client-rpc-unknown", name, "listed as observed but absent from the 4.8 dump protocols");
                }
                // The client-sent XRpc envelopes are top-level requests: every one with a response class in the 4.8 dump
                // (XRpcCommonResponse, XRpcActorReplicateResponse, XRpcActorActionResponse, XRpcComponentActionResponse;
                // dump48 TypeDefIndex 1037-1040) needs a request handler (live: XRpcActorReplicate for Fangs shells had none).
                foreach (string envelope in new[] { "XRpcCommon", "XRpcActorReplicate", "XRpcActorAction", "XRpcComponentAction" })
                {
                    Checked++;
                    if (!source.Contains($"[RequestPacketHandler(\"{envelope}\")]"))
                        Fail("client-envelope", envelope, "client-sent XRpc envelope request has no RequestPacketHandler");
                }
                // Every RPC the server names must exist in the dump; pushed ones must have a client handler.
                foreach (Match m in Regex.Matches(source, @"""((?:X)?Rpc\w+)"""))
                {
                    string name = m.Groups[1].Value;
                    if (name is "XRpcCommon" or "XRpcActorAction" or "XRpcComponentAction" or "XRpcActorReplicate")
                        continue;
                    Checked++;
                    if (!protocols.ContainsKey(name))
                        Fail("server-rpc-unknown", name, "named by server code but absent from the 4.8 client protocols (typo or invented RPC)");
                }
                foreach (Match m in Regex.Matches(source, @"(?:Common|ActorAction|ComponentAction)\(\s*""((?:X)?Rpc\w+)""|(?:Push|TimerPush)\([^;""]*""((?:X)?Rpc\w+)"""))
                {
                    string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    Checked++;
                    if (protocols.TryGetValue(name, out var protocol) && protocol.Kind != "handled")
                        Fail("server-push-unhandled", name, "pushed by the server but the 4.8 client has no handler for it");
                }
            }

            #endregion
        }
    }
}
