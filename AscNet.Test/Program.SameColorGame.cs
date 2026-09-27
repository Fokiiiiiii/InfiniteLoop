using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.samecolorgame;
using AscNet.Table.V2.share.task;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Globalization;
using System.Reflection;
using TaskConditionTable = AscNet.Table.V2.share.task.ConditionTable;

namespace AscNet.Test;

/// <summary>
/// Circuit Connect / SameColorGame (activity 7) focused contract harness.
///
/// Authority and boundaries:
/// - Requests carry the exact client key set the EN producer builds
///   (XSameColorGameActivityManager.Request*), so the registered handler sees the named-key map a real
///   client sends. The client converts one-based UI coordinates to zero-based wire coordinates; this
///   harness speaks the wire form.
/// - Responses and pushes are asserted through their MessagePack JSON by field name, which is exactly
///   what the EN consumers (XSCBattleManager, XSCBossManager, XSameColorGameActivityManager,
///   XSCBoss) read. No test asserts C# DTO internals, and no expected number mirrors a module
///   constant: values come from the authoritative table row that produced them or from the request.
/// - Failure codes are the authored EN client values (bytes/share/text/CodeText.json keys
///   20143001-20143034), restated here so this suite is an independent oracle. AscNet policy mapping:
///   no active run 20143002; coordinates 20143003; empty cell 20143004; illegal or no-match swap
///   20143005; unknown boss 20143008; locked boss 20143009; unknown role 20143011; locked role
///   20143012; locked skill 20143013; no steps 20143014; unknown skill 20143015; cooldown 20143016;
///   skill parameter 20143017; energy 20143019; nothing to cancel 20143020; not a timed stage
///   20143026; summon swap 20143034.
/// - Board generation, RNG and scoring are AscNet policy (retail server authority is not
///   recoverable), so this suite never asserts a captured board, score or action content. It asserts
///   client-visible invariants: board shape, round accounting, cumulative score, skill-family
///   geometry, persistence and the task/reward chain.
/// - Persisted-state fixtures use only public model fields (records, total score, board and energy on
///   the current run) so every scenario stays deterministic without touching module internals.
/// </summary>
internal static partial class Program
{
    private const int CircuitCodeInternal = 20143001;
    private const int CircuitCodeNoRun = 20143002;
    private const int CircuitCodeCoordinate = 20143003;
    private const int CircuitCodeSwapFailed = 20143005;
    private const int CircuitCodeUnknownBoss = 20143008;
    private const int CircuitCodeLockedBoss = 20143009;
    private const int CircuitCodeUnknownRole = 20143011;
    private const int CircuitCodeLockedRole = 20143012;
    private const int CircuitCodeLockedSkill = 20143013;
    private const int CircuitCodeUnknownSkill = 20143015;
    private const int CircuitCodeCooldown = 20143016;
    private const int CircuitCodeSkillParam = 20143017;
    private const int CircuitCodeCannotCancel = 20143020;
    private const int CircuitCodeNotTimed = 20143026;
    private const int CircuitCodeCannotSwapSummon = 20143034;

    private const string CircuitEnterRequest = "SameColorGameEnterStageRequest";
    private const string CircuitSwapRequest = "SameColorGameSwapItemRequest";
    private const string CircuitUseItemRequest = "SameColorGameUseItemRequest";
    private const string CircuitCancelUseItemRequest = "SameColorGameCancelUseItemRequest";
    private const string CircuitGiveUpRequest = "SameColorGameGiveUpRequest";
    private const string CircuitPauseResumeRequest = "SameColorGamePauseResumeRequest";
    private const string CircuitCountDownRequest = "SameColorGameCountDownRequest";
    private const string CircuitOpenRankRequest = "SameColorGameOpenRankRequest";
    private const string CircuitLoginPush = "NotifySameColorGameData";
    private const string CircuitTaskPush = "NotifyTask";

    private static void ValidateSameColorGameCompatibility()
    {
        CircuitValidateAuthoredClosure();
        CircuitValidateEntryGates();
        CircuitValidateSwapRules();
        CircuitValidateSkillFamilies();
        CircuitValidateBossFamilies();
        CircuitValidateScoreAuthority();
        CircuitValidateTerminalRunAndGiveUp();
        CircuitValidateControlAndRank();
        CircuitValidateRelogin();
        CircuitValidateTaskProgressionAndClaims();
        CircuitValidateShopAcquisition();
        CircuitValidateOriginalClientWire();
    }

    // ---------------------------------------------------------------------------------------------
    // Bounded export of one deterministic Circuit flow as real server packets, for the original EN Lua
    // action/request callback replay. Opt-in (ASCNET_CIRCUIT_WIRE_TRANSCRIPT=<directory>) and never a
    // runtime input: the probe reads only what this harness actually sent and received.
    // ---------------------------------------------------------------------------------------------
    private static void CircuitValidateOriginalClientWire()
    {
        string? directory = Environment.GetEnvironmentVariable("ASCNET_CIRCUIT_WIRE_TRANSCRIPT");
        if (string.IsNullOrWhiteSpace(directory))
            return;

        Directory.CreateDirectory(directory);
        CircuitWire.Begin();
        using (CircuitCase test = new(88_510, "circuit-wire-transcript"))
        {
            test.SeedUnlockedBosses();
            // Real uninterrupted flow: enter on the served board, earn the authored skill energy with
            // legal swaps (a rejected candidate is ordinary client traffic the client's fail callback
            // handles), cast the authored skill, then give up. No fixture board or energy mutation is
            // interleaved with the recorded packets, so the transcript stays a coherent client session.
            foreach (SameColorGameRoleTable role in TableReaderV2.Parse<SameColorGameRoleTable>().OrderBy(row => Convert.ToInt32(row.Id)))
            {
                int roleId = Convert.ToInt32(role.Id);
                int groupId = Convert.ToInt32(role.SkillId);
                SameColorGameSkillTable skill = CircuitSkill(Convert.ToInt32(CircuitGroup(groupId).SkillId));
                int cost = Convert.ToInt32(skill.EnergyCost);
                int maxRound = Convert.ToInt32(CircuitBoss(roleId).MaxRound);
                test.Success(CircuitEnterRequest, CircuitEnterBody(roleId, roleId, null), $"Circuit wire role {roleId} entry");
                for (int run = 0; run < 3 && test.Run().Energy < cost; run++)
                {
                    if (run > 0)
                        test.Success(CircuitEnterRequest, CircuitEnterBody(roleId, roleId, null),
                            $"Circuit wire role {roleId} extra energy run {run}");
                    for (int move = 1; move < maxRound && test.Run().Energy < cost; move++)
                    {
                        ((int X, int Y) Source, (int X, int Y) Dest)? swap = CircuitBestMatchSwap(test);
                        if (swap is null)
                            break;
                        test.Success(CircuitSwapRequest,
                            CircuitSwapBody(swap.Value.Source.X, swap.Value.Source.Y, swap.Value.Dest.X, swap.Value.Dest.Y),
                            $"Circuit wire role {roleId} energy move {move}");
                    }
                }
                AssertEqual(true, test.Run().Energy >= cost, $"Circuit wire role {roleId} earned the authored skill energy");
                (int ItemId, int X, int Y) target = Convert.ToInt32(skill.Type) is 112 or 114
                    ? CircuitColourPopupTarget(role, CircuitBallColour(Convert.ToInt32(role.BallId[0])))
                    : test.TargetAt(0, 0);
                test.Success(CircuitUseItemRequest,
                    CircuitUseBody(groupId, Convert.ToInt32(CircuitGroup(groupId).SkillId), CircuitTargets(target)),
                    $"Circuit wire role {roleId} skill");
                test.Success(CircuitGiveUpRequest, new Dictionary<string, object?> { ["BossId"] = test.Run().BossId },
                    $"Circuit wire role {roleId} give-up");
            }
        }
        CircuitWire.Write(Path.Combine(directory, "circuit-wire.lua"));
        CircuitWire.End();
    }

    /// <summary>
    /// Records the exact request/response/push sequence of the enabled flow as a Lua chunk so the
    /// original-client probe can replay it without a MessagePack decoder.
    /// </summary>
    private static class CircuitWire
    {
        private static readonly List<string> Events = new();
        private static bool enabled;

        public static void Begin()
        {
            Events.Clear();
            enabled = true;
        }

        public static void End() => enabled = false;

        public static void Record(string requestName, object? body, JObject response,
            IReadOnlyList<(string Name, JObject Body)> pushes)
        {
            if (!enabled)
                return;
            string pushList = string.Join(", ", pushes.Select(push =>
                $"{{Name={Literal(push.Name)}, Body={Literal(push.Body)}}}"));
            Events.Add($"{{Kind=\"Request\", Name={Literal(requestName)}, Body={LiteralRequest(body)}, "
                + $"Response={Literal(response)}, Pushes={{ {pushList} }}}}");
        }

        public static void Write(string path) =>
            File.WriteAllText(path, "return {\n" + string.Join(",\n", Events) + "\n}\n", new UTF8Encoding(false));

        private static string LiteralRequest(object? body)
        {
            if (body is null)
                return "nil";
            byte[] serialized = MessagePackSerialize(body.GetType(), body);
            return Literal(JToken.Parse(MessagePackSerializer.ConvertToJson(serialized)));
        }

        private static string Literal(JToken? token) => token switch
        {
            null => "nil",
            JObject value => "{" + string.Join(", ", value.Properties()
                .Select(property => $"[{Literal(property.Name)}]={Literal(property.Value)}")) + "}",
            JArray value => "{" + string.Join(", ", value.Select(Literal)) + "}",
            JValue value => value.Type switch
            {
                JTokenType.Null => "nil",
                JTokenType.Boolean => value.Value<bool>() ? "true" : "false",
                JTokenType.String => Literal(value.Value<string>() ?? string.Empty),
                _ => Convert.ToString(value.Value, CultureInfo.InvariantCulture)
                    ?? throw new InvalidDataException("Circuit wire transcript cannot format a numeric value.")
            },
            _ => throw new InvalidDataException($"Circuit wire transcript cannot format {token.Type}.")
        };

        private static string Literal(string text) =>
            "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
    }

    // ---------------------------------------------------------------------------------------------
    // Authored closure: mode content and its task/reward chain must resolve from tables only.
    // ---------------------------------------------------------------------------------------------
    private static void CircuitValidateAuthoredClosure()
    {
        List<SameColorGameActivityTable> activities = TableReaderV2.Parse<SameColorGameActivityTable>();
        List<SameColorGameActivityTable> current = activities.Where(row => Convert.ToInt32(row.TimerId) > 0).ToList();
        AssertEqual(1, current.Count, "Circuit has exactly one current activity row (client selects the positive TimerId)");

        List<TaskTimeLimitTable> circuitGroups = TableReaderV2.Parse<TaskTimeLimitTable>()
            .Where(row => Convert.ToInt32(row.TimeId) == 907 && row.TaskId.Count > 0)
            .OrderBy(row => Convert.ToInt32(row.Id))
            .ToList();
        AssertEqual(2, circuitGroups.Count, "Circuit task groups (404/405) both reference TimeId 907");
        List<int> circuitTaskIds = circuitGroups.SelectMany(row => row.TaskId).Select(Convert.ToInt32).Order().ToList();
        AssertEqual(true, circuitTaskIds.Count > 0, "Circuit task groups expose task ids");

        Dictionary<int, TaskTable> tasks = TableReaderV2.Parse<TaskTable>().ToDictionary(row => Convert.ToInt32(row.Id));
        Dictionary<int, RewardTable> rewards = TableReaderV2.Parse<RewardTable>().ToDictionary(row => Convert.ToInt32(row.Id));
        Dictionary<int, RewardGoodsTable> rewardGoods = TableReaderV2.Parse<RewardGoodsTable>().ToDictionary(row => Convert.ToInt32(row.Id));
        Dictionary<int, TaskConditionTable> conditions = TableReaderV2.Parse<TaskConditionTable>().ToDictionary(row => Convert.ToInt32(row.Id));
        List<SameColorGameBossTable> bosses = TableReaderV2.Parse<SameColorGameBossTable>().OrderBy(row => Convert.ToInt32(row.Id)).ToList();

        int gradeTasks = 0;
        int scoreTasks = 0;
        foreach (int taskId in circuitTaskIds)
        {
            TaskTable task = tasks[taskId];
            AssertEqual(3002, Convert.ToInt32(task.SourceGroupId), $"Circuit task {taskId} belongs to SourceGroupId 3002");
            _ = rewards[Convert.ToInt32(task.RewardId)];
            TaskConditionTable condition = conditions[Convert.ToInt32(task.Condition)];
            switch (Convert.ToInt32(condition.Type))
            {
                case 69005:
                    gradeTasks++;
                    int bossId = Convert.ToInt32(condition.Params[0]);
                    int grade = Convert.ToInt32(condition.Params[1]);
                    AssertEqual(true, bosses.Any(boss => Convert.ToInt32(boss.Id) == bossId), $"Circuit grade task {taskId} names an authored boss");
                    AssertEqual(true, TableReaderV2.Parse<SameColorGameBossGradeTable>()
                            .Any(row => Convert.ToInt32(row.BossId) == bossId && Convert.ToInt32(row.Grade) == grade),
                        $"Circuit grade task {taskId} names an authored boss grade");
                    break;
                case 69002:
                    scoreTasks++;
                    AssertEqual(true, Convert.ToInt32(condition.Params[0]) > 0, $"Circuit score task {taskId} has a positive threshold");
                    break;
                default:
                    throw new InvalidDataException($"Circuit task {taskId} uses unexpected condition type {condition.Type}.");
            }
        }
        AssertEqual(bosses.Count, gradeTasks, "Every authored Circuit boss has one grade task");
        AssertEqual(true, scoreTasks >= 2, "Circuit exposes multiple cumulative score tasks");

        // Reward closure: the authored goods behind every grade task must resolve.
        foreach (int taskId in circuitTaskIds.Take(gradeTasks))
        {
            List<(int TemplateId, long Count)> goods = CircuitExpectedGoods(tasks[taskId]);
            AssertEqual(true, goods.Count > 0, $"Circuit task {taskId} reward resolves to goods");
            AssertEqual(true, goods.All(entry => entry.TemplateId > 0 && entry.Count > 0),
                $"Circuit task {taskId} goods carry item and count");
        }

        // Every role's main skill group resolves, so the family coverage below is table-backed.
        List<SameColorGameSkillGroupTable> groups = TableReaderV2.Parse<SameColorGameSkillGroupTable>();
        Dictionary<int, SameColorGameSkillTable> skills = TableReaderV2.Parse<SameColorGameSkillTable>().ToDictionary(row => Convert.ToInt32(row.Id));
        List<SameColorGameRoleTable> roles = TableReaderV2.Parse<SameColorGameRoleTable>().OrderBy(row => Convert.ToInt32(row.Id)).ToList();
        AssertEqual(bosses.Count, roles.Count, "Circuit ships one role per boss");
        foreach (SameColorGameRoleTable role in roles)
        {
            int groupId = Convert.ToInt32(role.SkillId);
            SameColorGameSkillGroupTable group = groups.Single(row => Convert.ToInt32(row.Id) == groupId);
            SameColorGameSkillTable skill = skills[Convert.ToInt32(group.SkillId)];
            AssertEqual(true, Convert.ToInt32(skill.EnergyCost) > 0, $"Role {role.Id} main skill has an authored energy cost");
            AssertEqual(true, Convert.ToInt32(group.Cd) > 0, $"Role {role.Id} main group has an authored cooldown");
            AssertEqual(true, role.BallId.Count >= 2 && role.PropBallIds.Count > 0, $"Role {role.Id} has authored orbs and props");
            AssertEqual(true, Convert.ToInt32(role.Row) > 0 && Convert.ToInt32(role.Col) > 0, $"Role {role.Id} has an authored board size");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Entry gates, null extra skills, the MAP_INIT board and operations without an active run.
    // ---------------------------------------------------------------------------------------------
    private static void CircuitValidateEntryGates()
    {
        using CircuitCase test = new(88_501, "circuit-entry-gates");
        int activityId = CircuitCurrentActivityId();

        test.Reject(CircuitSwapRequest, CircuitSwapBody(0, 0, 1, 1), CircuitCodeNoRun, "Circuit swap without a run");
        test.Reject(CircuitUseItemRequest, CircuitUseBody(601, 601, CircuitTargets((1, 0, 0))), CircuitCodeNoRun, "Circuit skill without a run");
        test.Reject(CircuitCancelUseItemRequest, CircuitCancelBody(601, 601), CircuitCodeNoRun, "Circuit cancel without a run");

        test.Reject(CircuitEnterRequest, CircuitEnterBody(99, 1, null), CircuitCodeUnknownBoss, "Circuit unknown boss");
        test.Reject(CircuitEnterRequest, CircuitEnterBody(2, 1, null), CircuitCodeLockedBoss, "Circuit locked predecessor boss");
        test.Reject(CircuitEnterRequest, CircuitEnterBody(1, 99, null), CircuitCodeUnknownRole, "Circuit unknown role");
        test.Reject(CircuitEnterRequest, CircuitEnterBody(1, 2, null), CircuitCodeLockedRole, "Circuit locked role condition");
        test.Reject(CircuitEnterRequest, CircuitEnterBody(1, 1, [9999]), CircuitCodeUnknownSkill, "Circuit unknown extra skill group");
        int lockedRoleGroup = Convert.ToInt32(CircuitRole(2).SkillId);
        AssertEqual(true, lockedRoleGroup != Convert.ToInt32(CircuitRole(1).SkillId), "Locked-role fixture uses a distinct main group");
        test.Reject(CircuitEnterRequest, CircuitEnterBody(1, 1, [lockedRoleGroup]), CircuitCodeLockedSkill,
            "Circuit extra skill group owned by a locked role");

        foreach (int[]? skillIds in new[] { (int[]?)null, [] })
        {
            string label = skillIds is null ? "null" : "empty";
            JObject response = test.Success(CircuitEnterRequest, CircuitEnterBody(1, 1, skillIds), $"Circuit enter with {label} extra skills");
            JArray actions = CircuitActions(response);
            AssertEqual(1, CircuitActionType((JObject)actions[0]!), $"Circuit enter with {label} extra skills starts with MAP_INIT");
            JObject init = (JObject)actions[0]!;
            int rows = Convert.ToInt32(CircuitRole(1).Row);
            int cols = Convert.ToInt32(CircuitRole(1).Col);
            JArray cells = CircuitItems(init);
            AssertEqual(rows * cols, cells.Count, "Circuit MAP_INIT carries the complete board");
            AssertEqual(rows * cols,
                cells.Select(cell => (Convert.ToInt32(cell["PositionX"]), Convert.ToInt32(cell["PositionY"]))).Distinct().Count(),
                "Circuit MAP_INIT positions are unique");
            AssertEqual(true, cells.All(cell => CircuitJsonNull(cell["BuffUids"])
                    || (cell["BuffUids"] is JArray buffs && buffs.Count == 0)),
                "Circuit ordinary ball records carry no owned buff uids");
            AssertEqual(true, cells.All(cell =>
                    Convert.ToInt32(cell["ItemId"]) > 0
                    && Convert.ToInt32(cell["BallType"]) == 1
                    && Convert.ToInt32(cell["PositionX"]) >= 0 && Convert.ToInt32(cell["PositionX"]) < cols
                    && Convert.ToInt32(cell["PositionY"]) >= 0 && Convert.ToInt32(cell["PositionY"]) < rows),
                "Circuit MAP_INIT carries normal in-bounds orbs");

            CircuitBoard board = CircuitBoard.FromMapInit(init, rows, cols);
            AssertEqual(false, board.HasMatch(), "Circuit opening board has no pre-existing match");
            AssertEqual(true, board.HasLegalMove(), $"Circuit opening board with {label} extra skills has a legal move");

            SameColorGameRun run = test.Run();
            AssertEqual(1, run.BossId, $"Circuit run boss after {label} entry");
            AssertEqual(1, run.RoleId, $"Circuit run role after {label} entry");
            AssertEqual(1, run.CurRound, $"Circuit run round after {label} entry");
            AssertEqual(Convert.ToInt32(CircuitBoss(1).MaxRound), run.MaxRound, $"Circuit run max round after {label} entry");
            AssertEqual(0L, run.Score, $"Circuit run score after {label} entry");
            AssertEqual(activityId, test.State().ActivityId, $"Circuit run activity after {label} entry");
            AssertEqual(true, test.Players.LastSuccessfulReplacementBson is not null, "Circuit entry is persisted before acknowledgement");
        }

        // An unfinished run does not block the client's replay path: Enter simply replaces it.
        test.Success(CircuitEnterRequest, CircuitEnterBody(1, 1, null), "Circuit replay enter replaces the unfinished run");
        AssertEqual(1, test.Run().CurRound, "Circuit replay enter starts a fresh round");
        AssertEqual(0L, test.Run().Score, "Circuit replay enter starts a fresh score");
    }

    // ---------------------------------------------------------------------------------------------
    // Swap contract: coordinates, no-match policy, self-swap, summon immunity, match accounting.
    // ---------------------------------------------------------------------------------------------
    private static void CircuitValidateSwapRules()
    {
        using CircuitCase test = new(88_502, "circuit-swap-rules");
        test.Success(CircuitEnterRequest, CircuitEnterBody(1, 1, null), "Circuit swap fixture entry");

        SameColorGameRoleTable role = CircuitRole(1);
        CircuitBoard board = CircuitCraftedTripleBoard(role, out (int X, int Y) source, out (int X, int Y) dest);
        test.InstallBoard(board);
        AssertEqual(false, board.HasMatch(), "Circuit crafted fixture board has no pre-existing match");

        test.Reject(CircuitSwapRequest, CircuitSwapBody(8, 0, 7, 0), CircuitCodeCoordinate, "Circuit out-of-range swap");
        test.Reject(CircuitSwapRequest, CircuitSwapBody(-1, 0, 0, 0), CircuitCodeCoordinate, "Circuit negative coordinate swap");
        test.Reject(CircuitSwapRequest, CircuitSwapBody(0, 0, 0, 2), CircuitCodeCoordinate, "Circuit non-adjacent swap");
        test.Reject(CircuitSwapRequest, CircuitSwapBody(0, 0, 1, 1), CircuitCodeCoordinate, "Circuit diagonal swap");

        (int X, int Y) noMatchSource = (0, 0);
        (int X, int Y) noMatchDest = (1, 0);
        AssertEqual(false, board.CreatesMatch(noMatchSource, noMatchDest), "Circuit no-match fixture swap is genuinely matchless");
        int roundBeforeRejection = test.Run().CurRound;
        string boardBeforeRejection = test.Run().Board.ToJson();
        JObject rejected = test.Reject(CircuitSwapRequest,
            CircuitSwapBody(noMatchSource.X, noMatchSource.Y, noMatchDest.X, noMatchDest.Y),
            CircuitCodeSwapFailed, "Circuit no-match swap");
        AssertEqual(roundBeforeRejection, test.Run().CurRound, "Circuit no-match swap does not consume a round");
        AssertEqual(boardBeforeRejection, test.Run().Board.ToJson(), "Circuit no-match swap leaves the board unchanged");
        int boardItem = Convert.ToInt32(board.Cell(noMatchSource.X, noMatchSource.Y).ItemId);
        test.Reject(CircuitSwapRequest, CircuitSwapBody(noMatchSource.X, noMatchSource.Y, noMatchSource.X, noMatchSource.Y),
            CircuitCodeSwapFailed, "Circuit self-swap on an ordinary orb is not a prop activation");
        AssertEqual(boardItem, Convert.ToInt32(board.Cell(noMatchSource.X, noMatchSource.Y).ItemId), "Circuit rejected self-swap keeps the orb");

        // The crafted three-match is the accepted path: ITEM_SWAP, removals, per-move settlement.
        JObject swap = test.Success(CircuitSwapRequest, CircuitSwapBody(source.X, source.Y, dest.X, dest.Y), "Circuit match swap");
        JArray actions = CircuitActions(swap);
        AssertEqual(8, CircuitActionType((JObject)actions[0]!), "Circuit swap response starts with ITEM_SWAP");
        JObject swapAction = (JObject)actions[0]!;
        AssertEqual(source.X, Convert.ToInt32(swapAction["Source"]!["PositionX"]), "Circuit ITEM_SWAP echoes the source column");
        AssertEqual(source.Y, Convert.ToInt32(swapAction["Source"]!["PositionY"]), "Circuit ITEM_SWAP echoes the source row");
        AssertEqual(dest.X, Convert.ToInt32(swapAction["Destination"]!["PositionX"]), "Circuit ITEM_SWAP echoes the destination column");
        AssertEqual(dest.Y, Convert.ToInt32(swapAction["Destination"]!["PositionY"]), "Circuit ITEM_SWAP echoes the destination row");
        AssertEqual(1, Convert.ToInt32(swapAction["CurRound"]), "Circuit ITEM_SWAP reports the played round (capture: move N reports CurRound N)");
        AssertEqual(true, CircuitActionsOfType(actions, 2).Any(), "Circuit match swap emits ITEM_REMOVE");
        AssertEqual(false, CircuitActionsOfType(actions, 23).Any(), "Circuit straight three-match creates no prop");
        AssertEqual(true, CircuitActionsOfType(actions, 3).Any() || CircuitActionsOfType(actions, 4).Any(),
            "Circuit match swap refills the cleared cells");
        AssertEqual(false, test.Run().Board.Any(cell => cell.ItemId == 0), "Circuit settled board has no holes");
        JObject settlement = CircuitActionsOfType(actions, 7).Last();
        AssertEqual(0, Convert.ToInt32(settlement["IsLastRound"]), "Circuit first move is not terminal");
        AssertEqual(1, Convert.ToInt32(settlement["CurRound"]), "Circuit settlement reports the played round");
        AssertEqual(1, Convert.ToInt32(settlement["CurrentBossId"]), "Circuit settlement reports the boss");
        AssertEqual(true, Convert.ToInt64(settlement["CurrentScore"]) > 0, "Circuit matched move scores");
        AssertEqual(Convert.ToInt64(settlement["CurrentScore"]), Convert.ToInt64(settlement["TotalScore"]),
            "Circuit first settlement accumulates from zero");
        AssertEqual(Convert.ToInt64(settlement["TotalScore"]), test.Run().Score, "Circuit accepted swap persists the move score");
        AssertEqual(true, settlement["PassiveSkillMoreDamages"] is JArray,
            "Circuit settlement always carries the passive damage pair list the client iterates");
        AssertEqual(true, settlement["PassiveSkillMoreScoreFactor"] is JValue { Type: not JTokenType.Null } scoreFactor
                && Convert.ToDouble(scoreFactor.Value) >= 0,
            "Circuit settlement always carries the numeric passive score factor the client compares");
        AssertEqual(false, CircuitActionsOfType(actions, 17).Any(), "Circuit ordinary swap carries no skill cooldown action");

        // Boss 1 declares TriggerRound 1, so the first accepted move spawns its authored summons.
        List<JObject> bossSkills = CircuitActionsOfType(actions, 14).ToList();
        AssertEqual(true, bossSkills.Count > 0, "Circuit boss 1 releases its authored turn-1 skill");
        SameColorGameBossTable boss = CircuitBoss(1);
        AssertEqual(true, bossSkills.All(action => boss.ShowSkillIds.Select(Convert.ToInt32).Contains(Convert.ToInt32(action["BossSkillId"]))),
            "Circuit boss release only names authored boss skills");
        List<(int X, int Y)> summons = CircuitSummons(actions);
        AssertEqual(true, summons.Count > 0, "Circuit boss 1 turn-1 skill spawns summons");
        (int X, int Y) summon = summons[0];
        int neighbourX = summon.X > 0 ? summon.X - 1 : summon.X + 1;
        int roundBeforeSummonRejection = test.Run().CurRound;
        test.Reject(CircuitSwapRequest, CircuitSwapBody(summon.X, summon.Y, neighbourX, summon.Y),
            CircuitCodeCannotSwapSummon, "Circuit summon cannot be swapped");
        AssertEqual(roundBeforeSummonRejection, test.Run().CurRound, "Circuit rejected summon swap does not consume a round");

        // A failed persistence boundary must not leave a half-applied move in the live session.
        CircuitBoard failureBoard = CircuitCraftedTripleBoard(role, out (int X, int Y) failedSource, out (int X, int Y) failedDest);
        test.InstallBoard(failureBoard);
        string boardBeforeFailure = test.Run().Board.ToJson();
        long scoreBeforeFailure = test.Run().Score;
        test.Players.ThrowOnReplaceOne = true;
        try
        {
            test.Reject(CircuitSwapRequest, CircuitSwapBody(failedSource.X, failedSource.Y, failedDest.X, failedDest.Y),
                CircuitCodeInternal, "Circuit accepted swap with a failed player save");
        }
        finally
        {
            test.Players.ThrowOnReplaceOne = false;
        }
        AssertEqual(boardBeforeFailure, test.Run().Board.ToJson(), "Circuit failed save rolls the live board back");
        AssertEqual(scoreBeforeFailure, test.Run().Score, "Circuit failed save credits no live score");
        JObject retried = test.Success(CircuitSwapRequest, CircuitSwapBody(failedSource.X, failedSource.Y, failedDest.X, failedDest.Y),
            "Circuit retry after the failed save");
        AssertEqual(true, Convert.ToInt64(CircuitActionsOfType(CircuitActions(retried), 7).Last()["CurrentScore"]) > 0,
            "Circuit retry after a failed save reuses the preserved board");

        // A straight four creates the role's rapid-fire prop and refills the column a fixed prop splits.
        CircuitBoard fourBoard = CircuitCraftedFourBoard(role, out (int X, int Y) fourSource, out (int X, int Y) fourDest);
        test.InstallBoard(fourBoard);
        JObject fourMove = test.Success(CircuitSwapRequest, CircuitSwapBody(fourSource.X, fourSource.Y, fourDest.X, fourDest.Y),
            "Circuit straight four swap");
        int rapidFireBall = role.PropBallIds.Select(Convert.ToInt32).First(ball => CircuitBallPropType(ball) == 4);
        AssertEqual(true, CircuitActionsOfType(CircuitActions(fourMove), 23).SelectMany(CircuitItems)
                .Any(item => Convert.ToInt32(item["ItemId"]) == rapidFireBall),
            "Circuit straight four creates the role's rapid-fire prop");
        AssertEqual(false, test.Run().Board.Any(cell => cell.ItemId == 0),
            "Circuit straight-four refill leaves no holes around the fixed prop");
    }

    // ---------------------------------------------------------------------------------------------
    // All seven role/skill families on legal, deterministic persisted state.
    // ---------------------------------------------------------------------------------------------
    private static void CircuitValidateSkillFamilies()
    {
        using CircuitCase test = new(88_503, "circuit-skill-families");
        test.SeedUnlockedBosses();

        foreach (SameColorGameRoleTable role in TableReaderV2.Parse<SameColorGameRoleTable>().OrderBy(row => Convert.ToInt32(row.Id)))
        {
            int roleId = Convert.ToInt32(role.Id);
            test.Success(CircuitEnterRequest, CircuitEnterBody(roleId, roleId, null), $"Circuit role {roleId} entry");
            int groupId = Convert.ToInt32(role.SkillId);
            SameColorGameSkillGroupTable group = CircuitGroup(groupId);
            SameColorGameSkillTable skill = CircuitSkill(Convert.ToInt32(group.SkillId));
            int cost = Convert.ToInt32(skill.EnergyCost);
            int skillType = Convert.ToInt32(skill.Type);
            int colour = CircuitBallColour(Convert.ToInt32(role.BallId[0]));

            if (skillType == 113)
            {
                // Qu activates existing props: build one deterministically with an L-shaped prop match,
                // then prove the activation consumes that exact prop.
                CircuitBoard lBoard = CircuitCraftedBlastBoard(role, out (int X, int Y) lSource, out (int X, int Y) lDest);
                test.InstallBoard(lBoard);
                test.ResetEnergy();
                JObject propMove = test.Success(CircuitSwapRequest, CircuitSwapBody(lSource.X, lSource.Y, lDest.X, lDest.Y),
                    "Circuit role 4 prop fixture move");
                JArray propActions = CircuitActions(propMove);
                int firstRefill = propActions.IndexOf(propActions.First(action => CircuitActionType((JObject)action) == 4));
                List<(int ItemId, int X, int Y)> created = propActions.Take(firstRefill)
                    .OfType<JObject>()
                    .Where(action => CircuitActionType(action) == 23)
                    .SelectMany(CircuitItems)
                    .Select(item => (Convert.ToInt32(item["ItemId"]), Convert.ToInt32(item["PositionX"]), Convert.ToInt32(item["PositionY"])))
                    .ToList();
                AssertEqual(1, created.Count, "Circuit L-shaped match creates exactly one prop before its first refill");
                int createdPropBall = created[0].ItemId;
                SameColorGameRun settled = test.Run();
                List<(int X, int Y)> propCells = Enumerable.Range(0, settled.Board.Count)
                    .Where(index => settled.Board[index].BallType == 2 && settled.Board[index].ItemId == createdPropBall)
                    .Select(index => (X: index % settled.Cols, Y: index / settled.Cols))
                    .ToList();
                AssertEqual(true, propCells.Count > 0, "Circuit role 4 fixture prop survives the refill");
                (int X, int Y) propCell = propCells[0];
                int propBall = createdPropBall;
                AssertEqual(true, CircuitBallType(propBall) != 1, $"Circuit role 4 fixture prop is a prop ball ({propBall})");
                test.ResetEnergy();
                JObject activation = test.Success(CircuitUseItemRequest,
                    CircuitUseBody(groupId, Convert.ToInt32(group.SkillId), CircuitTargets((propBall, propCell.X, propCell.Y))),
                    "Circuit role 4 activate-all-props skill");
                AssertEqual(true, CircuitActionsOfType(CircuitActions(activation), 2)
                        .SelectMany(CircuitItems)
                        .Any(item => CircuitPosition(item) == propCell && Convert.ToInt32(item["ItemId"]) == propBall),
                    "Circuit activate-all-props consumes the authored prop on the board");
                JObject propRemoval = CircuitActionsOfType(CircuitActions(activation), 2)
                    .First(action => CircuitItems(action).Any(item => CircuitPosition(item) == propCell));
                AssertEqual(true, CircuitItems(propRemoval).Any(item => Convert.ToInt32(item["ItemType"]) != 0),
                    "Circuit role 4 prop activation carries a skill attribution marker");
                CircuitAssertSkillEnergy(test, skill, CircuitActions(activation), "Circuit role 4");
                AssertEqual(false, test.Run().Board.Any(cell => cell.ItemId == 0),
                    "Circuit role 4 skill leaves no holes after prop activation");
                continue;
            }

            // Invalid popup colour is rejected on a fresh run with valid energy, so the parameter check
            // (not the energy gate) is what the assertion exercises.
            if (skillType is 112 or 114)
            {
                test.ResetEnergy();
                test.Reject(CircuitUseItemRequest,
                    CircuitUseBody(groupId, Convert.ToInt32(group.SkillId), CircuitTargets((900000, -1, -1))),
                    CircuitCodeSkillParam, $"Circuit role {roleId} popup rejects a non-orb colour");
                test.Success(CircuitEnterRequest, CircuitEnterBody(roleId, roleId, null), $"Circuit role {roleId} recovery entry");
            }

            // A skill id that does not belong to the requested group must be rejected without touching
            // the selected skill: the following legal cast still succeeds at full energy.
            if (roleId == 1)
            {
                int foreignSkill = Convert.ToInt32(CircuitSkill(606).Id);
                test.InstallBoard(CircuitNoMatchBoard(role));
                test.ResetEnergy();
                test.Reject(CircuitUseItemRequest,
                    CircuitUseBody(groupId, foreignSkill, CircuitTargets(test.TargetAt(0, 0))),
                    null, "Circuit mismatched skill id for the requested group");
                AssertEqual(Convert.ToInt32(role.EnergyLimit), test.Run().Energy,
                    "Circuit mismatched skill id does not spend energy");
                AssertEqual(0, test.Run().Skills.Sum(state => state.LeftCd), "Circuit mismatched skill id does not start a cooldown");
            }

            test.InstallBoard(CircuitNoMatchBoard(role));
            test.ResetEnergy();
            (int ItemId, int X, int Y) castTarget = skillType is 112 or 114
                ? CircuitColourPopupTarget(role, colour)
                : test.TargetAt(0, 0);
            HashSet<(int X, int Y)>? dissolveCells = skillType == 114 ? test.BoardSnapshot().CellsOfColour(colour) : null;
            JObject response = test.Success(CircuitUseItemRequest,
                CircuitUseBody(groupId, Convert.ToInt32(group.SkillId), CircuitTargets(castTarget)),
                $"Circuit role {roleId} main skill");
            JArray actions = CircuitActions(response);
            AssertEqual(true, CircuitActionsOfType(actions, 16).Any(action =>
                    Convert.ToInt32(action["EnergyChange"]) == -cost && Convert.ToInt32(action["EnergyChangeType"]) == 1),
                $"Circuit role {roleId} skill charges the authored energy cost");
            AssertEqual(1, CircuitActionsOfType(actions, 17).Count(action =>
                    Convert.ToInt32(action["SkillId"]) == groupId && Convert.ToInt32(action["LeftCd"]) == Convert.ToInt32(group.Cd)),
                $"Circuit role {roleId} skill publishes the authored cooldown");
            CircuitAssertSkillEnergy(test, skill, actions, $"Circuit role {roleId}");
            test.Reject(CircuitUseItemRequest,
                CircuitUseBody(groupId, Convert.ToInt32(group.SkillId), CircuitTargets(castTarget)),
                CircuitCodeCooldown, $"Circuit role {roleId} repeated skill is on cooldown");

            switch (skillType)
            {
                case 117:
                {
                    List<JObject> transforms = CircuitActionsOfType(actions, 24).ToList();
                    int propCount = skill.SkillParams.Count;
                    AssertEqual(propCount, transforms.Count, "Circuit role 1 converts the authored number of orbs");
                    List<(int ItemId, int X, int Y)> destinations = transforms
                        .Select(action => action["Destination"]!)
                        .Select(item => (Convert.ToInt32(item["ItemId"]), Convert.ToInt32(item["PositionX"]), Convert.ToInt32(item["PositionY"])))
                        .ToList();
                    AssertEqual(propCount, destinations.Select(cell => (cell.X, cell.Y)).Distinct().Count(),
                        "Circuit role 1 transforms distinct orbs");
                    List<int> authoredProps = skill.SkillParams.Select(Convert.ToInt32).Distinct().ToList();
                    AssertEqual(propCount, authoredProps.Count, "Circuit role 1 authored prop set is distinct");
                    AssertEqual(true, destinations.All(cell => authoredProps.Contains(cell.ItemId)),
                        "Circuit role 1 converts orbs into the skill's authored SkillParams props (enhanced set), not the role's ordinary PropBallIds");
                    // EN XSCBattleManager:GetCurUsingSkillId attributes an action to the skill only when an
                    // entry carries a non-default ItemType, so skill effects must be marked.
                    AssertEqual(true, transforms.All(action => Convert.ToInt32(action["Destination"]!["ItemType"]) != 0),
                        "Circuit role 1 transform entries carry a skill attribution marker");

                    // A created prop is a real prop: a self-swap activates it instead of being rejected.
                    (int ItemId, int X, int Y) first = destinations[0];
                    JObject propActivation = test.Success(CircuitSwapRequest,
                        CircuitSwapBody(first.X, first.Y, first.X, first.Y), "Circuit role 1 prop self-activation");
                    AssertEqual(true, CircuitActionsOfType(CircuitActions(propActivation), 2)
                            .SelectMany(CircuitItems)
                            .Any(item => CircuitPosition(item) == (first.X, first.Y)),
                        "Circuit role 1 prop self-swap removes the prop");
                    break;
                }
                case 112:
                {
                    int window = CircuitChangeColorWindow(skill);
                    JObject followUp = test.MatchMove("Circuit role 2 conversion move").Response;
                    List<int> created = CircuitWindowCreations(actions, CircuitActions(followUp), window)
                        .Select(item => CircuitBallColour(Convert.ToInt32(item["ItemId"])))
                        .ToList();
                    AssertEqual(true, created.Count > 0, "Circuit role 2 conversion window produced drops");
                    AssertEqual(true, created.All(value => value == colour),
                        "Circuit role 2 conversion turns the next authored drops into the chosen orb");
                    break;
                }
                case 114:
                {
                    AssertEqual(true, dissolveCells is { Count: > 0 }, "Circuit role 3 fixture board holds orbs of the chosen colour");
                    HashSet<(int X, int Y)> expected = dissolveCells!;
                    // The skill's own dissolve is the first removal action; later removals in the same
                    // response are cascade matches and are deliberately not compared against the selection.
                    JObject directRemoval = CircuitActionsOfType(actions, 2).First();
                    HashSet<(int X, int Y)> removed = CircuitItems(directRemoval).Select(CircuitPosition).ToHashSet();
                    AssertEqual(true, removed.SetEquals(expected), "Circuit role 3 dissolves exactly the chosen colour");
                    AssertEqual(true, CircuitItems(directRemoval).Any(item => Convert.ToInt32(item["ItemType"]) != 0),
                        "Circuit role 3 dissolve carries a skill attribution marker");
                    int window = CircuitChangeColorWindow(skill);
                    JObject followUp = test.MatchMove("Circuit role 3 exclusion move").Response;
                    List<int> created = CircuitWindowCreations(actions, CircuitActions(followUp), window)
                        .Select(item => CircuitBallColour(Convert.ToInt32(item["ItemId"])))
                        .ToList();
                    AssertEqual(true, created.Count > 0, "Circuit role 3 exclusion window produced drops");
                    AssertEqual(true, created.All(value => value != colour), "Circuit role 3 excludes the chosen colour from drops");
                    break;
                }
                case 120:
                {
                    List<JObject> shuffles = CircuitActionsOfType(actions, 5)
                        .Concat(CircuitActionsOfType(actions, 27))
                        .ToList();
                    AssertEqual(true, shuffles.Count > 0, "Circuit role 5 publishes a board shuffle the client consumes");
                    CircuitBoard after = CircuitBoard.FromItems(CircuitItems(shuffles[0]), Convert.ToInt32(role.Row), Convert.ToInt32(role.Col));
                    AssertEqual(Convert.ToInt32(role.Row) * Convert.ToInt32(role.Col),
                        CircuitItems(shuffles[0]).Count, "Circuit role 5 shuffle republishes the complete board");
                    AssertEqual(false, after.HasMatch(), "Circuit role 5 shuffled board has no immediate match");
                    AssertEqual(true, after.HasLegalMove(), "Circuit role 5 shuffled board is playable");
                    // EN XUiPanelBoard:DoActionNewShuffleBall resolves the skill through
                    // GetCurUsingSkillId before showing the change-ball effect, so a skill-driven
                    // NEW_MAP_SHUFFLE must be attributable.
                    foreach (JObject shuffle in CircuitActionsOfType(actions, 27))
                    {
                        AssertEqual(true, CircuitItems(shuffle).Any(item => Convert.ToInt32(item["ItemType"]) != 0),
                            "Circuit NEW_MAP_SHUFFLE carries a skill attribution marker");
                    }
                    break;
                }
                case 115:
                {
                    int strikes = Math.Max(1, Convert.ToInt32(skill.SkillParam));
                    List<JObject> removals = CircuitActionsOfType(actions, 2).ToList();
                    List<JObject> lightning = removals
                        .Where(action => CircuitItems(action).Any(item => Convert.ToInt32(item["ItemType"]) == 4))
                        .ToList();
                    AssertEqual(true, lightning.Count > 0, "Circuit role 6 marks its lightning removals");
                    AssertEqual(true, lightning.All(action => CircuitItems(action).Count <= strikes * 9),
                        "Circuit role 6 never exceeds its authored strike area in the marked removal");
                    AssertEqual(true, lightning.Sum(action => CircuitItems(action).Count) >= strikes,
                        "Circuit role 6 lightning clears at least one orb per strike");
                    AssertEqual(true, removals.Count >= lightning.Count,
                        "Circuit role 6 cascade removals follow the marked strike action");
                    break;
                }
                case 118:
                {
                    int width = Math.Max(1, Convert.ToInt32(skill.SkillParam));
                    List<JObject> removals = CircuitActionsOfType(actions, 2).ToList();
                    AssertEqual(true, removals.Any(action => CircuitItems(action).Any(item => Convert.ToInt32(item["ItemType"]) == 5)),
                        "Circuit role 7 marks its wave removals");
                    AssertEqual(true, removals.Any(action => CircuitItems(action)
                            .GroupBy(item => Convert.ToInt32(item["PositionY"]))
                            .Any(row => row.Select(item => Convert.ToInt32(item["PositionX"])).Distinct().Count() >= width + 5)
                        || CircuitItems(action)
                            .GroupBy(item => Convert.ToInt32(item["PositionX"]))
                            .Any(column => column.Select(item => Convert.ToInt32(item["PositionY"])).Distinct().Count() >= width + 5)),
                        "Circuit role 7 wave covers a three-wide band across the board");
                    break;
                }
            }

            if (skillType is 113 or 114 or 115 or 118)
            {
                AssertEqual(false, test.Run().Board.Any(cell => cell.ItemId == 0),
                    $"Circuit role {roleId} skill refills every cell it cleared");
                AssertEqual(false, CircuitActionsOfType(actions, 2).Any()
                        && !CircuitActionsOfType(actions, 4).Any() && !CircuitActionsOfType(actions, 5).Any()
                        && !CircuitActionsOfType(actions, 27).Any(),
                    $"Circuit role {roleId} publishes refill or shuffle for its removals");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The authored 15-turn stage: round accounting, terminal settlement, records, give-up and replay.
    // ---------------------------------------------------------------------------------------------
    private static void CircuitValidateTerminalRunAndGiveUp()
    {
        using CircuitCase test = new(88_504, "circuit-terminal-run");
        SameColorGameBossTable boss = CircuitBoss(1);
        int maxRound = Convert.ToInt32(boss.MaxRound);
        AssertEqual(15, maxRound, "Circuit boss 1 keeps the authored 15-turn stage");

        SameColorGameBossGradeTable sGrade = TableReaderV2.Parse<SameColorGameBossGradeTable>()
            .Single(row => Convert.ToInt32(row.BossId) == 1 && Convert.ToInt32(row.Grade) == 11);
        TaskTimeLimitTable scoreGroup = TableReaderV2.Parse<TaskTimeLimitTable>().Single(row => Convert.ToInt32(row.Id) == 405);
        TaskTable firstScoreTask = TableReaderV2.Parse<TaskTable>().Single(row => Convert.ToInt32(row.Id) == scoreGroup.TaskId[0]);
        long firstScoreThreshold = Convert.ToInt64(TableReaderV2.Parse<TaskConditionTable>()
            .Single(row => Convert.ToInt32(row.Id) == Convert.ToInt32(firstScoreTask.Condition)).Params[0]);

        // Durable fixture: the boss already sits in S-grade range, so the authored grade task must stay
        // achieved after the run and the record maximum must keep the larger value.
        test.State().ActivityId = CircuitCurrentActivityId();
        test.State().BossRecords =
        [
            new SameColorGameBossRecord
            {
                BossId = 1,
                MaxPoint = Convert.ToInt32(sGrade.Damage),
                MaxCombo = 1,
                LastUseRoleId = 1
            }
        ];
        test.State().TotalScore = firstScoreThreshold - 1;
        long seededTotal = test.State().TotalScore;

        test.Success(CircuitEnterRequest, CircuitEnterBody(1, 1, null), "Circuit terminal fixture entry");
        long previousTotal = 0;
        for (int move = 1; move <= maxRound; move++)
        {
            (JObject Response, List<(string Name, JObject Body)> Pushes) exchange = test.MatchMove($"Circuit terminal move {move}");
            JObject settlement = CircuitActionsOfType(CircuitActions(exchange.Response), 7).Last();
            AssertEqual(move, Convert.ToInt32(settlement["CurRound"]),
                $"Circuit terminal move {move} reports the played round (capture: move N reports CurRound N)");
            long current = Convert.ToInt64(settlement["CurrentScore"]);
            long total = Convert.ToInt64(settlement["TotalScore"]);
            AssertEqual(true, current > 0, $"Circuit terminal move {move} scores");
            AssertEqual(previousTotal + current, total, $"Circuit terminal move {move} accumulates the run score");
            previousTotal = total;
            AssertEqual(move == maxRound ? 1 : 0, Convert.ToInt32(settlement["IsLastRound"]),
                $"Circuit terminal move {move} last-round flag");
            if (move < maxRound)
                AssertEqual(false, test.Run().Board.Any(cell => cell.ItemId == 0), $"Circuit terminal move {move} leaves no holes");
            AssertEqual(move == maxRound ? 1 : 0, exchange.Pushes.Count(push => push.Name == CircuitTaskPush),
                move == maxRound
                    ? "Circuit terminal move publishes its task update before the response"
                    : $"Circuit move {move} publishes no task update before settlement");
        }

        AssertEqual(true, previousTotal > 0, "Circuit terminal run produced a score");
        AssertEqual(seededTotal + previousTotal, test.State().TotalScore, "Circuit terminal settlement commits the run score once");
        SameColorGameBossRecord record = test.State().BossRecords.Single(row => row.BossId == 1);
        AssertEqual(Math.Max(Convert.ToInt64(sGrade.Damage), previousTotal), record.MaxPoint,
            "Circuit terminal settlement retains the larger boss maximum");
        AssertEqual(true, record.MaxCombo >= 1, "Circuit terminal settlement records a combo maximum");
        AssertEqual(1, record.LastUseRoleId, "Circuit terminal settlement records the last role");
        SameColorGameBossRecord durable = BsonSerializer.Deserialize<Player>(test.Players.LastSuccessfulReplacementBson!)
            .SameColorGame.BossRecords.Single(row => row.BossId == 1);
        AssertEqual(record.MaxPoint, durable.MaxPoint, "Circuit terminal score is durable before the response");
        AssertEqual(record.LastUseRoleId, durable.LastUseRoleId, "Circuit terminal role is durable before the response");

        // A move after the final round cannot continue the stage or credit anything.
        long committedTotal = test.State().TotalScore;
        long committedMax = test.State().BossRecords.Single(row => row.BossId == 1).MaxPoint;
        (JObject drained, _) = test.Exchange(CircuitSwapRequest, CircuitSwapBody(0, 0, 1, 0), "Circuit swap after the final round");
        AssertEqual(true, Convert.ToInt32(drained["Code"]) != 0, "Circuit cannot move after the final round");
        AssertEqual(committedTotal, test.State().TotalScore, "Circuit move after the final round credits nothing");
        AssertEqual(committedMax, test.State().BossRecords.Single(row => row.BossId == 1).MaxPoint,
            "Circuit move after the final round keeps the record");

        // Give-up never credits a score, and the client replays with a plain Enter afterwards.
        test.Success(CircuitEnterRequest, CircuitEnterBody(1, 1, null), "Circuit replay enter after settlement");
        test.MatchMove("Circuit give-up fixture move");
        AssertEqual(true, test.Run().Score > 0, "Circuit give-up fixture has an unfinished score");
        test.Success(CircuitGiveUpRequest, new Dictionary<string, object?> { ["BossId"] = 1 }, "Circuit give-up");
        AssertEqual(committedTotal, test.State().TotalScore, "Circuit give-up credits no total score");
        AssertEqual(committedMax, test.State().BossRecords.Single(row => row.BossId == 1).MaxPoint,
            "Circuit give-up credits no boss record");
        test.Success(CircuitEnterRequest, CircuitEnterBody(1, 1, null), "Circuit enter after give-up (client replay path)");
        AssertEqual(1, test.Run().CurRound, "Circuit enter after give-up starts a fresh round");
        AssertEqual(0L, test.Run().Score, "Circuit enter after give-up starts a fresh score");

        // An unfinished run replaced by Enter also credits nothing.
        test.MatchMove("Circuit replaced-run fixture move");
        long beforeReplace = test.State().TotalScore;
        test.Success(CircuitEnterRequest, CircuitEnterBody(1, 1, null), "Circuit unfinished run replacement");
        AssertEqual(beforeReplace, test.State().TotalScore, "Circuit unfinished run replacement credits nothing");
        AssertEqual(1, test.Run().CurRound, "Circuit unfinished run replacement resets the round");
        AssertEqual(0L, test.Run().Score, "Circuit unfinished run replacement resets the score");
    }

    // ---------------------------------------------------------------------------------------------
    // Stage controls and the ranking projection the client consumes.
    // ---------------------------------------------------------------------------------------------
    private static void CircuitValidateControlAndRank()
    {
        using CircuitCase test = new(88_505, "circuit-controls");
        test.Success(CircuitEnterRequest, CircuitEnterBody(1, 1, null), "Circuit control fixture entry");

        test.Reject(CircuitPauseResumeRequest, new Dictionary<string, object?>(), CircuitCodeNotTimed, "Circuit pause on a round stage");
        test.Reject(CircuitCountDownRequest, new Dictionary<string, object?>(), CircuitCodeNotTimed, "Circuit countdown on a round stage");
        SameColorGameRoleTable role = CircuitRole(1);
        test.Reject(CircuitCancelUseItemRequest, CircuitCancelBody(Convert.ToInt32(role.SkillId), Convert.ToInt32(role.SkillId)),
            CircuitCodeCannotCancel, "Circuit cancel without a prepared skill");
        AssertEqual(1, test.Run().CurRound, "Circuit rejected pause leaves the persisted round unchanged");
        AssertEqual(0L, test.Run().Score, "Circuit rejected pause leaves the persisted score unchanged");

        // A terminal settlement must become visible on the rank board: the durable projection is
        // flushed by the rank query itself, without the owner querying first.
        test.Run().MaxRound = test.Run().CurRound;
        test.MatchMove("Circuit rank fixture terminal move");
        test.Ranks.FindResults = [];
        test.Ranks.CountDocumentsResults.Enqueue(0L);
        JObject total = test.Success(CircuitOpenRankRequest, new Dictionary<string, object?> { ["BossId"] = 0 }, "Circuit total rank board");
        AssertEqual(JTokenType.Array, total["RankList"]!.Type, "Circuit total rank board publishes a row list");
        AssertEqual(JTokenType.Object, total["MyRankInfo"]!.Type, "Circuit total rank board always publishes the caller's row");
        AssertEqual(0, test.State().PendingRankUpdates.Count, "Circuit rank query flushes the pending projection");
        test.Ranks.FindResults = [];
        test.Ranks.CountDocumentsResults.Enqueue(0L);
        JObject boss = test.Success(CircuitOpenRankRequest, new Dictionary<string, object?> { ["BossId"] = 1 }, "Circuit boss rank board");
        AssertEqual(JTokenType.Array, boss["RankList"]!.Type, "Circuit boss rank board publishes a row list");
        AssertEqual(JTokenType.Object, boss["MyRankInfo"]!.Type, "Circuit boss rank board always publishes the caller's row");
        AssertEqual(0, test.State().PendingRankUpdates.Count, "Circuit boss rank query keeps no pending projection");
    }

    // ---------------------------------------------------------------------------------------------
    // BSON relogin: the login notification restores records and tasks, and never resumes a board.
    // ---------------------------------------------------------------------------------------------
    private static void CircuitValidateRelogin()
    {
        using CircuitCase test = new(88_506, "circuit-relogin");
        long playerId = test.Harness.Session.player.PlayerData.Id;
        test.State().ActivityId = CircuitCurrentActivityId();
        test.State().BossRecords =
        [
            new SameColorGameBossRecord { BossId = 3, MaxPoint = 654_321, MaxCombo = 17, LastUseRoleId = 5 },
            new SameColorGameBossRecord { BossId = 1, MaxPoint = 1_234, MaxCombo = 3, LastUseRoleId = 2 }
        ];
        test.State().TotalScore = 987_654;
        test.Success(CircuitEnterRequest, CircuitEnterBody(1, 1, null), "Circuit relogin fixture entry");
        test.MatchMove("Circuit relogin fixture move");
        byte[] persisted = test.Harness.Session.player.ToBson();

        Player restored = BsonSerializer.Deserialize<Player>(persisted);
        AssertEqual(987_654L, restored.SameColorGame.TotalScore, "Circuit total score survives player BSON");
        AssertEqual(2, restored.SameColorGame.BossRecords.Count, "Circuit boss records survive player BSON");
        AssertEqual(true, restored.SameColorGame.Run is not null, "Circuit unfinished run survives player BSON (durable, not published)");

        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), restored,
            CreateDrawCompatibilityInventory(playerId, []), sessionId: "circuit-relogin-login");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        MethodInfo doLogin = RequiredMethod(accountModule, "DoLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]);
        doLogin.Invoke(null, [harness.Session]);

        JObject? login = null;
        for (int packetIndex = 0; packetIndex < 192 && login is null; packetIndex++)
        {
            Packet packet = harness.ReadPacket($"Circuit login push {packetIndex + 1}");
            AssertEqual(Packet.ContentType.Push, packet.Type, "Circuit login stream carries pushes before the retail tail");
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
            if (push.Name == CircuitLoginPush)
                login = JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content));
        }
        AssertEqual(true, login is not null, "Circuit login publishes NotifySameColorGameData");
        AssertEqual(CircuitCurrentActivityId(), Convert.ToInt32(login!["ActivityId"]), "Circuit login reports the current activity");
        JArray records = (JArray)login!["BossRecords"]!;
        AssertEqual(2, records.Count, "Circuit login reports every persisted boss record");
        foreach (SameColorGameBossRecord expected in restored.SameColorGame.BossRecords)
        {
            JObject row = (JObject)records.Single(value => Convert.ToInt32(value["BossId"]) == expected.BossId)!;
            AssertEqual(expected.MaxPoint, Convert.ToInt64(row["MaxPoint"]), $"Circuit login max point for boss {expected.BossId}");
            AssertEqual(expected.MaxCombo, Convert.ToInt32(row["MaxCombo"]), $"Circuit login max combo for boss {expected.BossId}");
            AssertEqual(expected.LastUseRoleId, Convert.ToInt32(row["LastUseRoleId"]), $"Circuit login last role for boss {expected.BossId}");
        }
        AssertEqual(true, CircuitJsonNull(login!["Run"]), "Circuit login never resumes a mid-board run");
        AssertEqual(true, CircuitJsonNull(login!["Board"]), "Circuit login never republishes a board");

        // The restored client can start the next run normally.
        using (MongoCollectionOverride replayMongo = MongoCollectionOverride.InstallForSameColorGameCompatibility(
                   out _, out _, out _, out _))
        {
            using LoopbackSessionHarness replay = new(CreateDrawCompatibilityCharacter(playerId + 1),
                BsonSerializer.Deserialize<Player>(persisted), CreateDrawCompatibilityInventory(playerId + 1, []),
                "circuit-relogin-replay");
            InvokeRegisteredRequestHandler(CircuitEnterRequest, replay.Session, 88_606_001, CircuitEnterBody(1, 1, null));
            Packet replayResponse = replay.ReadPacket("Circuit relogin replay response");
            AssertEqual(Packet.ContentType.Response, replayResponse.Type, "Circuit relogin replay response packet");
            JObject payload = JObject.Parse(MessagePackSerializer.ConvertToJson(
                MessagePackSerializer.Deserialize<Packet.Response>(replayResponse.Content).Content));
            AssertEqual(0, Convert.ToInt32(payload["Code"]), "Circuit relogin replay enter succeeds on the restored player");
            AssertEqual(1, Convert.ToInt32(CircuitActions(payload)[0]!["ActionType"]), "Circuit relogin replay publishes a fresh board");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Task progression, single/multi claims and the reward-receipt partial-save recovery.
    // ---------------------------------------------------------------------------------------------
    private static void CircuitValidateTaskProgressionAndClaims()
    {
        using CircuitCase test = new(88_507, "circuit-tasks");
        List<SameColorGameBossGradeTable> grades = TableReaderV2.Parse<SameColorGameBossGradeTable>()
            .Where(row => Convert.ToInt32(row.Grade) == 11)
            .OrderBy(row => Convert.ToInt32(row.BossId))
            .ToList();
        TaskTimeLimitTable gradeGroup = TableReaderV2.Parse<TaskTimeLimitTable>().Single(row => Convert.ToInt32(row.Id) == 404);
        TaskTimeLimitTable scoreGroup = TableReaderV2.Parse<TaskTimeLimitTable>().Single(row => Convert.ToInt32(row.Id) == 405);
        List<int> gradeTaskIds = gradeGroup.TaskId.Select(Convert.ToInt32).ToList();
        List<int> scoreTaskIds = scoreGroup.TaskId.Select(Convert.ToInt32).ToList();
        AssertEqual(grades.Count, gradeTaskIds.Count, "Circuit grade task group covers every boss");

        // Every boss is already in S-grade range, so the whole grade group is achievable. The seeded
        // state carries the authored activity id because the module resets a state from another activity.
        test.State().ActivityId = CircuitCurrentActivityId();
        test.State().BossRecords = grades
            .Select(grade => new SameColorGameBossRecord
            {
                BossId = Convert.ToInt32(grade.BossId),
                MaxPoint = Convert.ToInt32(grade.Damage),
                MaxCombo = 1,
                LastUseRoleId = 1
            })
            .ToList();
        Dictionary<int, TaskTable> tasks = TableReaderV2.Parse<TaskTable>().ToDictionary(row => Convert.ToInt32(row.Id));
        Dictionary<int, TaskConditionTable> conditions = TableReaderV2.Parse<TaskConditionTable>().ToDictionary(row => Convert.ToInt32(row.Id));
        List<(int TaskId, long Threshold)> scoreThresholds = scoreTaskIds
            .Select(taskId => (TaskId: taskId, Threshold: Convert.ToInt64(conditions[Convert.ToInt32(tasks[taskId].Condition)].Params[0])))
            .OrderBy(entry => entry.Threshold)
            .ToList();
        test.State().TotalScore = scoreThresholds[1].Threshold;

        // Fixture shortcut: one authored-cost move on a stage whose max round is already the current
        // round, which drives the same terminal settlement path as the 15-turn stage.
        test.Success(CircuitEnterRequest, CircuitEnterBody(1, 1, null), "Circuit task fixture entry");
        test.Run().MaxRound = test.Run().CurRound;
        (JObject terminalResponse, List<(string Name, JObject Body)> terminalPushes) = test.MatchMove("Circuit task fixture terminal move");
        AssertEqual(1, Convert.ToInt32(CircuitActionsOfType(CircuitActions(terminalResponse), 7).Last()["IsLastRound"]),
            "Circuit task fixture move is terminal");

        JObject? taskPush = terminalPushes.Where(push => push.Name == CircuitTaskPush).Select(push => push.Body).LastOrDefault();
        AssertEqual(true, taskPush is not null, "Circuit terminal settlement publishes a task update before the response");
        Dictionary<int, JObject> pushed = CircuitTaskRows(taskPush!);
        AssertEqual(true, gradeTaskIds.All(pushed.ContainsKey), "Circuit task update carries the whole grade group");
        AssertEqual(true, scoreTaskIds.All(pushed.ContainsKey), "Circuit task update carries the whole score group");
        foreach (int taskId in gradeTaskIds)
        {
            JObject row = pushed[taskId];
            AssertEqual(3, Convert.ToInt32(row["State"]), $"Circuit grade task {taskId} is achieved");
            int grade = Convert.ToInt32(conditions[Convert.ToInt32(tasks[taskId].Condition)].Params[1]);
            AssertEqual(grade, CircuitScheduleValue(row, taskId), $"Circuit grade task {taskId} publishes the authored grade");
        }
        foreach (int taskId in scoreTaskIds)
        {
            JObject row = pushed[taskId];
            long threshold = scoreThresholds.Single(entry => entry.TaskId == taskId).Threshold;
            AssertEqual(test.State().TotalScore >= threshold ? 3 : 1, Convert.ToInt32(row["State"]),
                $"Circuit score task {taskId} state follows its authored threshold");
            AssertEqual(test.State().TotalScore, CircuitScheduleValue(row, taskId),
                $"Circuit score task {taskId} publishes the persisted total");
        }

        // Single claim: the authored reward lands once and the marker makes it non-repeatable.
        int gradeClaim = gradeTaskIds[0];
        Dictionary<int, long> before = test.Balances();
        List<(int TemplateId, long Count)> expectedGoods = CircuitExpectedGoods(tasks[gradeClaim]);
        JObject claim = test.Success("FinishTaskRequest", new Dictionary<string, object?> { ["TaskId"] = gradeClaim }, "Circuit grade task claim");
        CircuitAssertGoods(claim["RewardGoodsList"]!.OfType<JObject>().ToList(), expectedGoods, $"Circuit grade task {gradeClaim} claim goods");
        CircuitAssertBalanceDelta(test, before, expectedGoods, $"Circuit grade task {gradeClaim} claim");
        AssertEqual(true, test.Player().MissionProgress.ClaimedTaskIds.Contains(gradeClaim), "Circuit claim records the claim marker");
        AssertEqual(true, test.Players.LastSuccessfulReplacementBson is not null, "Circuit claim is durable");
        Dictionary<int, long> afterClaim = test.Balances();
        test.Reject("FinishTaskRequest", new Dictionary<string, object?> { ["TaskId"] = gradeClaim }, 20026006,
            "Circuit repeated claim");
        CircuitAssertBalanceDelta(test, afterClaim, [], "Circuit repeated claim pays nothing twice");

        // Multi claim: only achieved tasks succeed, the rest stay untouched.
        int achievedScore = scoreThresholds[1].TaskId;
        int unachievedScore = scoreThresholds[^1].TaskId;
        JObject multi = test.Success("FinishMultiTaskRequest",
            new Dictionary<string, object?> { ["TaskIds"] = new List<int> { achievedScore, unachievedScore } },
            "Circuit multi claim");
        AssertIntegerList([achievedScore], ((JArray)multi["SuccessTaskIds"]!).Select(value => Convert.ToInt64(value)).ToArray(),
            "Circuit multi claim reports the achieved task");
        AssertIntegerList([unachievedScore], ((JArray)multi["NotDealTaskIds"]!).Select(value => Convert.ToInt64(value)).ToArray(),
            "Circuit multi claim leaves the unachieved task alone");

        // Receipt partial save: the reward is durable, the marker is not, and the retry cannot pay twice.
        int receiptTask = gradeTaskIds[1];
        Dictionary<int, long> beforeReceipt = test.Balances();
        List<(int TemplateId, long Count)> receiptGoods = CircuitExpectedGoods(tasks[receiptTask]);
        test.Players.ThrowOnReplaceOne = true;
        try
        {
            _ = test.Reject("FinishTaskRequest", new Dictionary<string, object?> { ["TaskId"] = receiptTask }, null,
                "Circuit claim with a failed marker save");
        }
        finally
        {
            test.Players.ThrowOnReplaceOne = false;
        }
        AssertEqual(false, test.Player().MissionProgress.ClaimedTaskIds.Contains(receiptTask),
            "Circuit failed marker save leaves no committed in-memory claim");
        CircuitAssertBalanceDelta(test, beforeReceipt, receiptGoods, "Circuit receipt persists the authored reward before the marker");
        Dictionary<int, long> afterReceipt = test.Balances();
        JObject retry = test.Success("FinishTaskRequest", new Dictionary<string, object?> { ["TaskId"] = receiptTask }, "Circuit receipt retry");
        AssertEqual(true, retry["RewardGoodsList"] is JArray { Count: > 0 }, "Circuit receipt retry replays the authored acknowledgement");
        CircuitAssertBalanceDelta(test, afterReceipt, [], "Circuit receipt retry pays nothing twice");
        AssertEqual(true, test.Player().MissionProgress.ClaimedTaskIds.Contains(receiptTask), "Circuit receipt retry commits the marker");
    }

    // ---------------------------------------------------------------------------------------------
    // Shop 1291 through the shared shop transport (curated static catalog exception).
    // ---------------------------------------------------------------------------------------------
    private static void CircuitValidateShopAcquisition()
    {
        using CircuitCase test = new(88_508, "circuit-shop");
        JObject info = test.Success("GetShopInfoRequest", new Dictionary<string, object?> { ["Id"] = 1291 }, "Circuit shop info");
        JObject shop = (JObject)info["ClientShop"]!;
        AssertEqual(1291, Convert.ToInt32(shop["Id"]), "Circuit shop keeps its authored identity");
        JArray goods = (JArray)shop["GoodsList"]!;
        AssertEqual(true, goods.Count > 0, "Circuit shop exposes its offer list");

        JObject offer = (JObject)goods[0]!;
        JObject consume = (JObject)((JArray)offer["ConsumeList"]!)[0]!;
        int currencyId = Convert.ToInt32(consume["Id"]);
        long cost = Convert.ToInt64(consume["Count"]);
        AssertEqual(true, cost > 0, "Circuit shop offer has a positive cost");
        test.GrantItem(currencyId, cost);
        Dictionary<int, long> before = test.Balances();
        JObject buy = test.Success("BuyRequest",
            new Dictionary<string, object?> { ["ShopId"] = 1291, ["GoodsId"] = Convert.ToInt32(offer["Id"]), ["Count"] = 1 },
            "Circuit shop purchase");
        AssertEqual(true, ((JArray)buy["GoodList"]!).Count > 0, "Circuit shop purchase returns the granted goods");
        AssertEqual(before[currencyId] - cost, test.Balances().GetValueOrDefault(currencyId),
            "Circuit shop purchase debits the authored cost");
        AssertEqual(1, test.Player().ShopBuyTimes.GetValueOrDefault(Convert.ToUInt32(offer["Id"])),
            "Circuit shop purchase records the buy count");
        AssertEqual(true, test.Players.LastSuccessfulReplacementBson is not null, "Circuit shop purchase is durable");

        using CircuitCase poor = new(88_509, "circuit-shop-unaffordable");
        Dictionary<int, long> poorBefore = poor.Balances();
        poor.Reject("BuyRequest",
            new Dictionary<string, object?> { ["ShopId"] = 1291, ["GoodsId"] = Convert.ToInt32(offer["Id"]), ["Count"] = 1 },
            null, "Circuit shop purchase without currency");
        AssertEqual(poorBefore.Count, poor.Balances().Count, "Circuit unaffordable purchase grants no items");
    }

    // ---------------------------------------------------------------------------------------------
    // Every authored boss family: trigger rounds, summon spawns, adjacency kills and death effects.
    // One run per authored summon ball keeps the 15-round stage budget sufficient for every kill.
    // ---------------------------------------------------------------------------------------------
    private static void CircuitValidateBossFamilies()
    {
        Dictionary<int, SameColorGameBallTable> balls = CircuitBallRows();
        Dictionary<int, SameColorGameSkillTable> skills = CircuitSkillRows();
        Dictionary<int, SameColorGameBossSkillTable> bossSkills = CircuitBossSkillRows();

        foreach (SameColorGameBossTable boss in CircuitBossRows())
        {
            int bossId = Convert.ToInt32(boss.Id);
            int maxRound = Convert.ToInt32(boss.MaxRound);
            SameColorGameRoleTable role = CircuitRole(Convert.ToInt32(boss.RoleId));
            List<SameColorGameBossSkillTable> authored = boss.ShowSkillIds.Select(Convert.ToInt32)
                .Where(bossSkills.ContainsKey)
                .Select(id => bossSkills[id])
                .GroupBy(row => Convert.ToInt32(row.Id))
                .Select(group => group.First())
                .OrderBy(row => Convert.ToInt32(row.TriggerRound))
                .ToList();
            AssertEqual(true, authored.Count > 0, $"Circuit boss {bossId} has an authored boss-skill closure");
            List<int> summonBalls = authored.SelectMany(row => row.WeakBallIds.Select(Convert.ToInt32))
                .Where(balls.ContainsKey)
                .Distinct()
                .OrderBy(ballId => ballId)
                .ToList();
            AssertEqual(true, summonBalls.Count > 0, $"Circuit boss {bossId} has authored summons");

            foreach (int summonBallId in summonBalls)
            {
                SameColorGameBossSkillTable spawn = authored.First(row => row.WeakBallIds.Select(Convert.ToInt32).Contains(summonBallId));
                int authoredHits = Math.Max(1, Convert.ToInt32(balls[summonBallId].WeakHitTimes));
                AssertEqual(true, Convert.ToInt32(spawn.TriggerRound) + authoredHits <= maxRound,
                    $"Circuit boss {bossId} summon {summonBallId} fits inside the authored {maxRound}-round stage");

                using CircuitCase test = new(88_520 + bossId * 100 + summonBalls.IndexOf(summonBallId),
                    $"circuit-boss-{bossId}-{summonBallId}");
                test.SeedUnlockedBosses();
                test.Success(CircuitEnterRequest, CircuitEnterBody(bossId, Convert.ToInt32(role.Id), null),
                    $"Circuit boss {bossId} summon {summonBallId} entry");
                HashSet<int> observedTriggers = [];
                List<int> weakBuffUids = [];
                (int Uid, int X, int Y) tracked = (-1, -1, -1);

                while (tracked.Uid < 0)
                {
                    int round = test.Run().CurRound;
                    AssertEqual(true, round <= maxRound, $"Circuit boss {bossId} summon {summonBallId} reaches its authored trigger round");
                    CircuitBoard board = CircuitCraftedTripleBoard(role, out (int X, int Y) source, out (int X, int Y) dest);
                    test.InstallBoard(board);
                    JObject move = test.Success(CircuitSwapRequest, CircuitSwapBody(source.X, source.Y, dest.X, dest.Y),
                        $"Circuit boss {bossId} summon {summonBallId} advance move");
                    JArray actions = CircuitActions(move);
                    foreach (JObject release in CircuitActionsOfType(actions, 14))
                    {
                        int released = Convert.ToInt32(release["BossSkillId"]);
                        AssertEqual(true, authored.Any(row => Convert.ToInt32(row.Id) == released),
                            $"Circuit boss {bossId} releases only its authored boss skills ({released})");
                        observedTriggers.Add(released);
                    }
                    foreach (JToken spawned in CircuitActionsOfType(actions, 24)
                                 .Select(action => action["Destination"]!)
                                 .Where(destination => Convert.ToInt32(destination["BallType"]) == 3
                                     && Convert.ToInt32(destination["WeakHitTimes"]) > 0))
                    {
                        int spawnedBall = Convert.ToInt32(spawned["ItemId"]);
                        AssertEqual(true, balls.ContainsKey(spawnedBall), $"Circuit boss {bossId} spawns an authored summon ball ({spawnedBall})");
                        AssertEqual(Convert.ToInt32(balls[spawnedBall].WeakHitTimes), Convert.ToInt32(spawned["WeakHitTimes"]),
                            $"Circuit boss {bossId} summon {spawnedBall} spawns with the authored hit count");
                        foreach (int buffId in balls[spawnedBall].WeakBuffIds.Select(Convert.ToInt32))
                            AssertEqual(true, CircuitActionsOfType(actions, 12).Any(action => Convert.ToInt32(action["BuffId"]) == buffId),
                                $"Circuit boss {bossId} summon {spawnedBall} applies its authored weak buff {buffId}");
                        if (spawnedBall == summonBallId && tracked.Uid < 0)
                        {
                            tracked = (Convert.ToInt32(spawned["WeakUid"]), Convert.ToInt32(spawned["PositionX"]), Convert.ToInt32(spawned["PositionY"]));
                            List<int> weakBuffs = balls[spawnedBall].WeakBuffIds.Select(Convert.ToInt32).ToList();
                            weakBuffUids.AddRange(CircuitActionsOfType(actions, 12)
                                .Where(action => weakBuffs.Contains(Convert.ToInt32(action["BuffId"])))
                                .Select(action => Convert.ToInt32(action["BuffUid"])));
                        }
                    }
                }
                AssertEqual(true, observedTriggers.Contains(Convert.ToInt32(spawn.Id)),
                    $"Circuit boss {bossId} observed its authored trigger skill {spawn.Id}");

                int remainingHits = authoredHits;
                bool killed = false;
                List<int> ownedBuffUids = [];
                Dictionary<int, int> ownedBuffIds = new();
                Dictionary<int, int> buffRemovedRounds = new();
                int trackedSpawnRound = test.Run().CurRound;
                int lastRound = trackedSpawnRound;
                for (int hitIndex = 0; hitIndex <= authoredHits && !killed; hitIndex++)
                {
                    CircuitBoard hitBoard = CircuitSummonHitBoard(role, summonBallId, tracked.Uid, remainingHits,
                        out (int X, int Y) hitSource, out (int X, int Y) hitDest);
                    test.InstallBoard(hitBoard);
                    JObject hitMove = test.Success(CircuitSwapRequest, CircuitSwapBody(hitSource.X, hitSource.Y, hitDest.X, hitDest.Y),
                        $"Circuit boss {bossId} summon {summonBallId} adjacency hit {hitIndex + 1}");
                    JArray hitActions = CircuitActions(hitMove);
                    foreach (JObject swapAction in CircuitActionsOfType(hitActions, 8))
                        lastRound = Convert.ToInt32(swapAction["CurRound"]);
                    foreach (JObject removedBuff in CircuitActionsOfType(hitActions, 13))
                        buffRemovedRounds.TryAdd(Convert.ToInt32(removedBuff["BuffUid"]), lastRound);
                    JObject weakHit = CircuitActionsOfType(hitActions, 26)
                            .Where(action => Convert.ToInt32(action["Destination"]!["BallType"]) == 3
                                && Convert.ToInt32(action["Destination"]!["WeakUid"]) == tracked.Uid)
                            .LastOrDefault()
                        ?? throw new InvalidDataException(
                            $"Circuit boss {bossId} summon {summonBallId}: adjacency hit did not reach summon {tracked.Uid}.");
                    if (ownedBuffUids.Count == 0 && weakHit["Destination"]!["BuffUids"] is JArray ownedBuffs)
                    {
                        ownedBuffUids.AddRange(ownedBuffs.Select(value => Convert.ToInt32(value)));
                        foreach (SameColorGameBuffState buffState in test.Run().Buffs)
                            ownedBuffIds[buffState.Uid] = buffState.BuffId;
                    }
                    int observedHits = Convert.ToInt32(weakHit["Destination"]!["WeakHitTimes"]);
                    AssertEqual(true, observedHits < remainingHits,
                        $"Circuit boss {bossId} summon {summonBallId} hit count decreases (was {remainingHits}, now {observedHits})");
                    remainingHits = observedHits;
                    // A summon record is identified by BallType 3 plus its uid: ordinary orbs legitimately
                    // carry the default uid, so a uid-only scan would match them.
                    JObject? removal = CircuitActionsOfType(hitActions, 2)
                        .FirstOrDefault(action => CircuitItems(action).OfType<JObject>().Any(item =>
                            Convert.ToInt32(item["BallType"]) == 3 && Convert.ToInt32(item["WeakUid"]) == tracked.Uid));
                    if (removal is null)
                    {
                        AssertEqual(true, remainingHits > 0, $"Circuit boss {bossId} summon {summonBallId} leaves the board only at zero hits");
                        continue;
                    }

                    killed = true;
                    AssertEqual(0, remainingHits, $"Circuit boss {bossId} summon {summonBallId} is removed at the final observed zero hits");
                    JObject record = CircuitItems(removal).OfType<JObject>()
                        .First(item => Convert.ToInt32(item["BallType"]) == 3 && Convert.ToInt32(item["WeakUid"]) == tracked.Uid);
                    int row = Convert.ToInt32(record["PositionY"]);
                    int column = Convert.ToInt32(record["PositionX"]);
                    SameColorGameBallTable ball = balls[summonBallId];
                    AssertEqual(true, weakBuffUids.Count >= ball.WeakBuffIds.Count,
                        $"Circuit boss {bossId} summon {summonBallId} spawned its authored weak buff instances");
                    // Authored weak buffs tick per accepted move, so a stack may already have expired before
                    // an adjacency-only kill finishes; only a stack still alive at death must be removed here.
                    foreach (int buffUid in ownedBuffUids)
                    {
                        if (CircuitActionsOfType(hitActions, 13).Any(action => Convert.ToInt32(action["BuffUid"]) == buffUid))
                            continue;
                        AssertEqual(true, buffRemovedRounds.ContainsKey(buffUid),
                            $"Circuit boss {bossId} summon {summonBallId} weak buff {buffUid} is removed on death or already expired");
                        if (!ownedBuffIds.TryGetValue(buffUid, out int ownedBuffId))
                            continue;
                        SameColorGameBuffTable? buffRow = TableReaderV2.Parse<SameColorGameBuffTable>()
                            .FirstOrDefault(row => Convert.ToInt32(row.Id) == ownedBuffId);
                        if (buffRow is null || Convert.ToInt32(buffRow.Duration) <= 0)
                            continue;
                        AssertEqual(true, buffRemovedRounds[buffUid] - trackedSpawnRound <= Convert.ToInt32(buffRow.Duration),
                            $"Circuit boss {bossId} weak buff {ownedBuffId} expires inside its authored Duration");
                    }
                    if (ball.SkillId is int deathSkillId && skills.TryGetValue(deathSkillId, out SameColorGameSkillTable? deathSkill))
                    {
                        foreach (int buffId in deathSkill.BuffIds.Select(Convert.ToInt32))
                            AssertEqual(true, CircuitActionsOfType(hitActions, 12).Any(action => Convert.ToInt32(action["BuffId"]) == buffId),
                                $"Circuit boss {bossId} death skill {deathSkillId} grants its authored buff {buffId}");
                        if (Convert.ToInt32(deathSkill.Type) is 106 or 110)
                            AssertEqual(true, CircuitActionsOfType(hitActions, 2)
                                    .Any(action => CircuitItems(action).Count(item => Convert.ToInt32(item["PositionY"]) == row) >= 2),
                                $"Circuit boss {bossId} death skill {deathSkillId} clears the summon's row");
                        if (Convert.ToInt32(deathSkill.Type) is 107 or 110)
                            AssertEqual(true, CircuitActionsOfType(hitActions, 2)
                                    .Any(action => CircuitItems(action).Count(item => Convert.ToInt32(item["PositionX"]) == column) >= 2),
                                $"Circuit boss {bossId} death skill {deathSkillId} clears the summon's column");
                    }
                }
                AssertEqual(true, killed, $"Circuit boss {bossId} killed summon {summonBallId} within the authored stage");
            }

            CircuitValidateBossEarlyKill(bossId, summonBalls, balls, authored, bossSkills);
        }
    }

    /// <summary>
    /// Boss 6's weak buffs last the authored two turns, so an adjacency-only kill can outlive them. This
    /// case kills the shortest-lived summon inside that window: role 4's activate-all-props fires the
    /// role's star prop for the direct hit the adjacency path cannot deliver, so the still-active weak
    /// stack must be removed by the death itself.
    /// </summary>
    private static void CircuitValidateBossEarlyKill(int bossId, List<int> summonBalls,
        IReadOnlyDictionary<int, SameColorGameBallTable> balls,
        IReadOnlyList<SameColorGameBossSkillTable> authored,
        IReadOnlyDictionary<int, SameColorGameBossSkillTable> bossSkills)
    {
        if (bossId != 6 || summonBalls.Count == 0)
            return;

        int earlyBall = summonBalls.OrderBy(ballId => Convert.ToInt32(balls[ballId].WeakHitTimes)).First();
        int earlyHits = Math.Max(1, Convert.ToInt32(balls[earlyBall].WeakHitTimes));
        int earlyDuration = balls[earlyBall].WeakBuffIds.Select(Convert.ToInt32)
            .Select(buffId => TableReaderV2.Parse<SameColorGameBuffTable>()
                .Where(row => Convert.ToInt32(row.Id) == buffId)
                .Select(row => Convert.ToInt32(row.Duration))
                .DefaultIfEmpty(0)
                .First())
            .DefaultIfEmpty(0)
            .Max();
        if (earlyDuration <= 0 || earlyHits > earlyDuration + 1)
            return;

        SameColorGameRoleTable propRole = CircuitRole(4);
        int propGroupId = Convert.ToInt32(propRole.SkillId);
        SameColorGameSkillTable propSkill = CircuitSkill(Convert.ToInt32(CircuitGroup(propGroupId).SkillId));
        int starPropBall = propRole.PropBallIds.Select(Convert.ToInt32).First(ball => CircuitBallPropType(ball) == 1);
        SameColorGameBossSkillTable spawn = authored.First(row => row.WeakBallIds.Select(Convert.ToInt32).Contains(earlyBall));

        using CircuitCase test = new(88_591, "circuit-boss-6-early-kill");
        test.SeedUnlockedBosses();
        test.Success(CircuitEnterRequest, CircuitEnterBody(bossId, Convert.ToInt32(propRole.Id), null),
            "Circuit boss 6 early-kill entry");
        (int Uid, int X, int Y) tracked = (-1, -1, -1);
        List<int> ownedBuffUids = [];
        while (tracked.Uid < 0)
        {
            CircuitBoard board = CircuitCraftedTripleBoard(propRole, out (int X, int Y) source, out (int X, int Y) dest);
            test.InstallBoard(board);
            JObject move = test.Success(CircuitSwapRequest, CircuitSwapBody(source.X, source.Y, dest.X, dest.Y),
                "Circuit boss 6 early-kill advance move");
            JArray actions = CircuitActions(move);
            foreach (JToken spawned in CircuitActionsOfType(actions, 24)
                         .Select(action => action["Destination"]!)
                         .Where(destination => Convert.ToInt32(destination["BallType"]) == 3
                             && Convert.ToInt32(destination["WeakHitTimes"]) > 0
                             && Convert.ToInt32(destination["ItemId"]) == earlyBall))
                tracked = (Convert.ToInt32(spawned["WeakUid"]), Convert.ToInt32(spawned["PositionX"]), Convert.ToInt32(spawned["PositionY"]));
        }

        int remaining = earlyHits;
        bool killed = false;
        for (int step = 0; step < earlyHits + 1 && !killed; step++)
        {
            CircuitBoard hitBoard = CircuitSummonHitBoard(propRole, earlyBall, tracked.Uid, remaining,
                out (int X, int Y) hitSource, out (int X, int Y) hitDest);
            hitBoard.Set(0, 0, starPropBall, ballType: 2);
            test.InstallBoard(hitBoard);
            JObject hitMove = test.Success(CircuitSwapRequest, CircuitSwapBody(hitSource.X, hitSource.Y, hitDest.X, hitDest.Y),
                $"Circuit boss 6 early-kill adjacency hit {step + 1}");
            JArray hitActions = CircuitActions(hitMove);
            JObject weakHit = CircuitActionsOfType(hitActions, 26)
                    .Where(action => Convert.ToInt32(action["Destination"]!["BallType"]) == 3
                        && Convert.ToInt32(action["Destination"]!["WeakUid"]) == tracked.Uid)
                    .LastOrDefault()
                ?? throw new InvalidDataException("Circuit boss 6 early kill: adjacency hit did not reach the tracked summon.");
            if (ownedBuffUids.Count == 0 && weakHit["Destination"]!["BuffUids"] is JArray owned)
                ownedBuffUids.AddRange(owned.Select(value => Convert.ToInt32(value)));
            remaining = Convert.ToInt32(weakHit["Destination"]!["WeakHitTimes"]);
            killed = CircuitEarlyKillSettled(test, bossId, earlyBall, tracked.Uid, ownedBuffUids, hitActions, ref remaining, ref ownedBuffUids);
            if (killed)
                break;

            // Direct hit: activate-all-props fires the star prop, which strikes every summon once.
            test.ResetEnergy();
            JObject activation = test.Success(CircuitUseItemRequest,
                CircuitUseBody(propGroupId, Convert.ToInt32(propSkill.Id), CircuitTargets(test.TargetAt(0, 0))),
                "Circuit boss 6 early-kill direct hit");
            JArray activationActions = CircuitActions(activation);
            JObject? directHit = CircuitActionsOfType(activationActions, 26)
                .Where(action => Convert.ToInt32(action["Destination"]!["BallType"]) == 3
                    && Convert.ToInt32(action["Destination"]!["WeakUid"]) == tracked.Uid)
                .LastOrDefault();
            if (directHit is null)
                continue;
            remaining = Convert.ToInt32(directHit["Destination"]!["WeakHitTimes"]);
            killed = CircuitEarlyKillSettled(test, bossId, earlyBall, tracked.Uid, ownedBuffUids, activationActions, ref remaining, ref ownedBuffUids);
        }
        AssertEqual(true, killed, "Circuit boss 6 early kill finishes inside the authored buff window");
        AssertEqual(true, spawn.WeakBallIds.Select(Convert.ToInt32).Contains(earlyBall), "Circuit boss 6 early kill uses an authored spawn ball");
    }

    private static bool CircuitEarlyKillSettled(CircuitCase test, int bossId, int summonBallId, int weakUid,
        List<int> ownedBuffUids, JArray actions, ref int remaining, ref List<int> _)
    {
        if (remaining > 0)
            return false;
        JObject? removal = CircuitActionsOfType(actions, 2)
            .FirstOrDefault(action => CircuitItems(action).OfType<JObject>().Any(item =>
                Convert.ToInt32(item["BallType"]) == 3 && Convert.ToInt32(item["WeakUid"]) == weakUid));
        AssertEqual(true, removal is not null, "Circuit boss 6 early kill removes the summon at zero hits");
        AssertEqual(true, ownedBuffUids.Count > 0, "Circuit boss 6 early kill captured the summon's owned weak buff stack");
        foreach (int buffUid in ownedBuffUids)
            AssertEqual(true, CircuitActionsOfType(actions, 13).Any(action => Convert.ToInt32(action["BuffUid"]) == buffUid),
                $"Circuit boss 6 early death removes the still-active weak buff instance {buffUid}");
        AssertEqual(true, bossId == 6, "Circuit boss 6 early kill stays on the authored boss");
        AssertEqual(true, summonBallId > 0, "Circuit boss 6 early kill tracks an authored summon ball");
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Independent score oracle: raw removal values and settlements must follow the authored Ball,
    // Score, Combo and AttributeFactor rows, including the capture's 22000 raw / 2200 single triple.
    // ---------------------------------------------------------------------------------------------
    private static void CircuitValidateScoreAuthority()
    {
        using CircuitCase test = new(88_530, "circuit-score-authority");
        SameColorGameBossTable boss = CircuitBoss(1);
        SameColorGameRoleTable role = CircuitRole(Convert.ToInt32(boss.RoleId));
        int matchBall = Convert.ToInt32(role.BallId[0]);
        decimal attributeBonus = CircuitAttributeBonus(role, boss);
        List<int> comboCurve = CircuitComboCurve();
        test.Success(CircuitEnterRequest, CircuitEnterBody(1, 1, null), "Circuit score fixture entry");

        CircuitBoard triple = CircuitCraftedTripleBoard(role, out (int X, int Y) tripleSource, out (int X, int Y) tripleDest);
        test.InstallBoard(triple);
        JObject tripleMove = test.Success(CircuitSwapRequest,
            CircuitSwapBody(tripleSource.X, tripleSource.Y, tripleDest.X, tripleDest.Y), "Circuit score triple swap");
        JArray tripleActions = CircuitActions(tripleMove);
        long expectedTripleRaw = CircuitExpectedRaw(matchBall, 3, attributeBonus);
        AssertEqual(expectedTripleRaw, Convert.ToInt64(CircuitActionsOfType(tripleActions, 2).First()["CurrentScore"]),
            "Circuit plain triple raw score follows Ball.Score * ScoreCurve[3] * the authored attribute factor (capture: 22000)");
        if (CircuitActionsOfType(tripleActions, 2).Count() == 1)
        {
            long expectedTripleSettlement = (long)decimal.Truncate(expectedTripleRaw * comboCurve[0] / 1000m);
            AssertEqual(expectedTripleSettlement, Convert.ToInt64(CircuitActionsOfType(tripleActions, 7).Last()["CurrentScore"]),
                "Circuit single-group triple settlement keeps the capture pair (22000 raw -> 2200)");
            AssertEqual(expectedTripleSettlement, Convert.ToInt64(CircuitActionsOfType(tripleActions, 7).Last()["TotalScore"]),
                "Circuit first settlement starts the run total at the authored value");
        }
        CircuitAssertSettlement(tripleActions, comboCurve, "Circuit triple settlement");

        // Straight four: the swap extends a crafted three into a four, so the first removal group is
        // exactly the quad and its raw follows the authored Score curve.
        CircuitBoard quad = CircuitCraftedQuadBoard(role, out (int X, int Y) quadSource, out (int X, int Y) quadDest);
        test.InstallBoard(quad);
        JObject quadMove = test.Success(CircuitSwapRequest,
            CircuitSwapBody(quadSource.X, quadSource.Y, quadDest.X, quadDest.Y), "Circuit score quad swap");
        JArray quadActions = CircuitActions(quadMove);
        long expectedQuadRaw = CircuitExpectedRaw(matchBall, 4, attributeBonus);
        AssertEqual(expectedQuadRaw, Convert.ToInt64(CircuitActionsOfType(quadActions, 2).First()["CurrentScore"]),
            "Circuit straight four raw score follows the authored Score curve (capture: 33000)");
        CircuitAssertSettlement(quadActions, comboCurve, "Circuit quad settlement");
    }

    // ---------------------------------------------------------------------------------------------
    // Table accessors and JSON helpers.
    // ---------------------------------------------------------------------------------------------
    private static int CircuitCurrentActivityId() =>
        TableReaderV2.Parse<SameColorGameActivityTable>()
            .Where(row => Convert.ToInt32(row.TimerId) > 0)
            .Select(row => Convert.ToInt32(row.Id))
            .Single();

    private static List<SameColorGameBossTable> CircuitBossRows() =>
        TableReaderV2.Parse<SameColorGameBossTable>().OrderBy(row => Convert.ToInt32(row.Id)).ToList();

    private static Dictionary<int, SameColorGameBallTable> CircuitBallRows() =>
        TableReaderV2.Parse<SameColorGameBallTable>().GroupBy(row => Convert.ToInt32(row.Id))
            .ToDictionary(group => group.Key, group => group.First());

    private static Dictionary<int, SameColorGameSkillTable> CircuitSkillRows() =>
        TableReaderV2.Parse<SameColorGameSkillTable>().GroupBy(row => Convert.ToInt32(row.Id))
            .ToDictionary(group => group.Key, group => group.First());

    private static Dictionary<int, SameColorGameBossSkillTable> CircuitBossSkillRows() =>
        TableReaderV2.Parse<SameColorGameBossSkillTable>().GroupBy(row => Convert.ToInt32(row.Id))
            .ToDictionary(group => group.Key, group => group.First());

    private static List<int> CircuitScoreCurve() =>
        TableReaderV2.Parse<SameColorGameScoreTable>().OrderBy(row => Convert.ToInt32(row.Id))
            .Select(row => Convert.ToInt32(row.Score)).ToList();

    private static List<int> CircuitComboCurve() =>
        TableReaderV2.Parse<SameColorGameComboTable>().OrderBy(row => Convert.ToInt32(row.Id))
            .Select(row => Convert.ToInt32(row.Percent)).ToList();

    /// <summary>Authored attribute bonus: only when the boss is weak to the role's attribute.</summary>
    private static decimal CircuitAttributeBonus(SameColorGameRoleTable role, SameColorGameBossTable boss)
    {
        if (Convert.ToInt32(role.AttributeFactorId) <= 0)
            return 0m;
        SameColorGameAttributeFactorTable? factor = TableReaderV2.Parse<SameColorGameAttributeFactorTable>()
            .SingleOrDefault(row => Convert.ToInt32(row.Id) == Convert.ToInt32(role.AttributeFactorId));
        if (factor is null)
            return 0m;
        return Convert.ToInt32(factor.Type) == Convert.ToInt32(boss.AttributeType) ? Convert.ToDecimal(factor.Factor) : 0m;
    }

    private static long CircuitExpectedRaw(int ballId, int orbCount, decimal attributeBonus)
    {
        List<int> curve = CircuitScoreCurve();
        int score = Convert.ToInt32(CircuitBallRows()[ballId].Score);
        int orbValue = curve[Math.Clamp(orbCount, 1, curve.Count) - 1];
        return (long)decimal.Truncate(score * orbValue * (1m + attributeBonus));
    }

    /// <summary>
    /// Strongest legal swap for the served board, chosen with the same independent match oracle the
    /// fixtures use: the candidate whose swap matches the most cells (largest simultaneous clear and
    /// therefore the most authored combo energy). Read-only against the run's persisted board, so the
    /// original-client transcript can earn real energy without any fixture mutation.
    /// </summary>
    private static ((int X, int Y) Source, (int X, int Y) Dest)? CircuitBestMatchSwap(CircuitCase test)
    {
        CircuitBoard board = test.BoardSnapshot();
        ((int X, int Y) Source, (int X, int Y) Dest)? best = null;
        int bestMatched = 0;
        for (int y = 0; y < board.Rows; y++)
        for (int x = 0; x < board.Cols; x++)
        {
            foreach ((int X, int Y) dest in new[] { (x + 1, y), (x, y + 1) })
            {
                if (dest.X >= board.Cols || dest.Y >= board.Rows)
                    continue;
                CircuitBoard copy = board.Clone();
                copy.Swap((x, y), dest);
                int matched = copy.Matched().Count;
                if (matched > bestMatched)
                {
                    bestMatched = matched;
                    best = ((x, y), dest);
                }
            }
        }
        return best;
    }

    /// <summary>
    /// The settlement must equal the authored Combo percent applied to the response's own removal
    /// raws and group count. This catches raw score multiplication per removed cell.
    /// </summary>
    private static void CircuitAssertSettlement(JArray actions, IReadOnlyList<int> comboCurve, string name)
    {
        long raw = 0;
        int groups = 0;
        foreach (JObject removal in CircuitActionsOfType(actions, 2))
        {
            raw += Convert.ToInt64(removal["CurrentScore"]);
            groups += Convert.ToInt32(removal["CurrentCombo"]);
        }
        long expected = raw == 0
            ? 0
            : (long)decimal.Truncate(raw * comboCurve[Math.Clamp(groups, 1, comboCurve.Count) - 1] / 1000m);
        AssertEqual(expected, Convert.ToInt64(CircuitActionsOfType(actions, 7).Last()["CurrentScore"]),
            $"{name} follows the authored Combo curve applied to the response's own removals (raw {raw}, groups {groups})");
    }

    private static SameColorGameRoleTable CircuitRole(int roleId) =>
        TableReaderV2.Parse<SameColorGameRoleTable>().Single(row => Convert.ToInt32(row.Id) == roleId);

    private static SameColorGameBossTable CircuitBoss(int bossId) =>
        TableReaderV2.Parse<SameColorGameBossTable>().Single(row => Convert.ToInt32(row.Id) == bossId);

    private static SameColorGameSkillGroupTable CircuitGroup(int groupId) =>
        TableReaderV2.Parse<SameColorGameSkillGroupTable>().Single(row => Convert.ToInt32(row.Id) == groupId);

    private static SameColorGameSkillTable CircuitSkill(int skillId) =>
        TableReaderV2.Parse<SameColorGameSkillTable>().Single(row => Convert.ToInt32(row.Id) == skillId);

    private static int CircuitBallColour(int ballId) =>
        Convert.ToInt32(TableReaderV2.Parse<SameColorGameBallTable>().Single(row => Convert.ToInt32(row.Id) == ballId).Color);

    private static int CircuitBallType(int ballId) =>
        Convert.ToInt32(TableReaderV2.Parse<SameColorGameBallTable>().Single(row => Convert.ToInt32(row.Id) == ballId).Type);

    private static int CircuitBallPropType(int ballId) =>
        Convert.ToInt32(TableReaderV2.Parse<SameColorGameBallTable>().Single(row => Convert.ToInt32(row.Id) == ballId).PropType);

    private static int CircuitChangeColorWindow(SameColorGameSkillTable skill)
    {
        Dictionary<int, SameColorGameBuffTable> buffs = TableReaderV2.Parse<SameColorGameBuffTable>()
            .GroupBy(row => Convert.ToInt32(row.Id))
            .ToDictionary(group => group.Key, group => group.First());
        foreach (int buffId in skill.BuffIds.Select(Convert.ToInt32))
        {
            if (buffs.TryGetValue(buffId, out SameColorGameBuffTable? buff) && Convert.ToInt32(buff.ChangeColorCount) > 0)
                return Convert.ToInt32(buff.ChangeColorCount);
        }
        return 10;
    }

    private static Dictionary<string, object?> CircuitEnterBody(int bossId, int roleId, IReadOnlyList<int>? skillIds) => new()
    {
        ["BossId"] = bossId,
        ["RoleId"] = roleId,
        ["SkillIds"] = skillIds is null ? null : skillIds.ToList()
    };

    private static Dictionary<string, object?> CircuitSwapBody(int sourceX, int sourceY, int destX, int destY) => new()
    {
        ["Source"] = new Dictionary<string, object?> { ["PositionX"] = sourceX, ["PositionY"] = sourceY },
        ["Destination"] = new Dictionary<string, object?> { ["PositionX"] = destX, ["PositionY"] = destY }
    };

    private static Dictionary<string, object?> CircuitCancelBody(int groupId, int skillId) => new()
    {
        ["GroupId"] = groupId,
        ["ItemId"] = skillId
    };

    /// <summary>Client UseSkillItemParam: empty map, or Item1/Item2 zero-based targets.</summary>
    private static Dictionary<string, object?> CircuitUseBody(int groupId, int skillId, IReadOnlyList<(int ItemId, int X, int Y)>? targets)
    {
        Dictionary<string, object?> parameters = new();
        if (targets is not null)
        {
            for (int index = 0; index < targets.Count && index < 2; index++)
            {
                parameters[$"Item{index + 1}"] = new Dictionary<string, object?>
                {
                    ["ItemId"] = targets[index].ItemId,
                    ["PositionX"] = targets[index].X,
                    ["PositionY"] = targets[index].Y
                };
            }
        }
        return new Dictionary<string, object?>
        {
            ["GroupId"] = groupId,
            ["ItemId"] = skillId,
            ["UseSkillItemParam"] = parameters
        };
    }

    private static IReadOnlyList<(int ItemId, int X, int Y)> CircuitTargets((int ItemId, int X, int Y) target) => [target];

    private static (int ItemId, int X, int Y) CircuitColourPopupTarget(SameColorGameRoleTable role, int colour)
    {
        int ballId = role.BallId.Select(Convert.ToInt32).First(id => CircuitBallColour(id) == colour);
        return (ballId, -1, -1);
    }

    /// <summary>
    /// MessagePack JSON null arrives either as an absent property (C# null) or as a JValue whose type
    /// is JTokenType.Null, so shape checks must accept both.
    /// </summary>
    private static bool CircuitJsonNull(JToken? token) => token is null || token.Type == JTokenType.Null;

    private static JArray CircuitActions(JObject response) =>
        response["Actions"] as JArray ?? throw new InvalidDataException("Circuit response carries no Actions array.");

    private static int CircuitActionType(JObject action) => Convert.ToInt32(action["ActionType"]);

    private static IEnumerable<JObject> CircuitActionsOfType(JArray actions, int actionType) =>
        actions.OfType<JObject>().Where(action => CircuitActionType(action) == actionType);

    private static JArray CircuitItems(JObject action) => action["ItemList"] as JArray ?? [];

    private static (int X, int Y) CircuitPosition(JToken record) =>
        (Convert.ToInt32(record["PositionX"]), Convert.ToInt32(record["PositionY"]));

    private static List<(int X, int Y)> CircuitSummons(JArray actions) => CircuitActionsOfType(actions, 24)
        .Select(action => action["Destination"]!)
        .Where(destination => Convert.ToInt32(destination["BallType"]) == 3 && Convert.ToInt32(destination["WeakHitTimes"]) > 0)
        .Select(CircuitPosition)
        .ToList();

    private static Dictionary<int, JObject> CircuitTaskRows(JObject push)
    {
        Dictionary<int, JObject> rows = new();
        foreach (JObject row in (push["Tasks"]?["Tasks"] as JArray ?? []).OfType<JObject>())
            rows[Convert.ToInt32(row["Id"])] = row;
        return rows;
    }

    private static long CircuitScheduleValue(JObject row, int taskId)
    {
        JArray schedule = row["Schedule"] as JArray
            ?? throw new InvalidDataException($"Circuit task {taskId} carries no schedule array.");
        JObject entry = schedule.OfType<JObject>().Single(value => Convert.ToInt32(value["Id"]) == taskId);
        return Convert.ToInt64(entry["Value"]);
    }

    private static List<(int TemplateId, long Count)> CircuitExpectedGoods(TaskTable task)
    {
        Dictionary<int, RewardTable> rewards = TableReaderV2.Parse<RewardTable>().ToDictionary(row => Convert.ToInt32(row.Id));
        Dictionary<int, RewardGoodsTable> goods = TableReaderV2.Parse<RewardGoodsTable>().ToDictionary(row => Convert.ToInt32(row.Id));
        return rewards[Convert.ToInt32(task.RewardId)].SubIds
            .Select(Convert.ToInt32)
            .Where(id => id != 0)
            .Select(id => (TemplateId: Convert.ToInt32(goods[id].TemplateId), Count: Convert.ToInt64(goods[id].Count)))
            .ToList();
    }

    private static void CircuitAssertGoods(IReadOnlyList<JObject> actual, IReadOnlyList<(int TemplateId, long Count)> expected, string name)
    {
        AssertEqual(expected.Count, actual.Count, $"{name} count");
        foreach ((int templateId, long count) in expected)
        {
            AssertEqual(1, actual.Count(row => Convert.ToInt32(row["TemplateId"]) == templateId && Convert.ToInt64(row["Count"]) == count),
                $"{name} carries {templateId}x{count}");
        }
    }

    private static void CircuitAssertBalanceDelta(CircuitCase test, IReadOnlyDictionary<int, long> before,
        IReadOnlyList<(int TemplateId, long Count)> expected, string name)
    {
        Dictionary<int, long> after = test.Balances();
        foreach ((int templateId, long count) in expected)
        {
            AssertEqual(before.GetValueOrDefault(templateId) + count, after.GetValueOrDefault(templateId),
                $"{name} inventory {templateId}");
        }
        foreach (int itemId in after.Keys.Union(before.Keys))
        {
            if (expected.Any(goods => goods.TemplateId == itemId))
                continue;
            AssertEqual(before.GetValueOrDefault(itemId), after.GetValueOrDefault(itemId), $"{name} leaves inventory {itemId} unchanged");
        }
    }

    /// <summary>
    /// Energy after a skill use. The authored cost is charged first; removals the effect causes can add
    /// authored combo energy afterwards, so the final value is bounded by the authored limit instead of
    /// pinned to limit - cost. A skill whose effect removes nothing still settles at exactly limit - cost.
    /// </summary>
    /// <summary>
    /// The authored conversion/exclusion window counts the next falling orbs, so it spans every
    /// ITEM_CREATE_NEW record of the skill response and of the following move in order.
    /// </summary>
    private static List<JObject> CircuitWindowCreations(JArray castActions, JArray followUpActions, int window)
    {
        List<JObject> created = new();
        foreach (JArray actions in new[] { castActions, followUpActions })
        {
            foreach (JObject creation in CircuitActionsOfType(actions, 4))
            {
                foreach (JObject item in CircuitItems(creation).OfType<JObject>())
                {
                    if (created.Count >= window)
                        return created;
                    created.Add(item);
                }
            }
        }
        return created;
    }

    private static void CircuitAssertSkillEnergy(CircuitCase test, SameColorGameSkillTable skill, JArray actions, string name)
    {
        SameColorGameRun run = test.Run();
        int limit = Convert.ToInt32(CircuitRole(run.RoleId).EnergyLimit);
        int cost = Convert.ToInt32(skill.EnergyCost);
        List<JObject> energyChanges = CircuitActionsOfType(actions, 16).ToList();
        int costIndex = energyChanges.FindIndex(action =>
            Convert.ToInt32(action["EnergyChange"]) == -cost && Convert.ToInt32(action["EnergyChangeType"]) == 1);
        AssertEqual(true, costIndex >= 0, $"{name} publishes the authored skill cost");
        int gainIndex = energyChanges.FindIndex(action => Convert.ToInt32(action["EnergyChangeType"]) == 3);
        if (gainIndex >= 0)
            AssertEqual(true, costIndex < gainIndex, $"{name} charges the skill before any removal gain");
        AssertEqual(true, run.Energy <= limit, $"{name} energy never exceeds the authored limit");
        if (!CircuitActionsOfType(actions, 2).Any())
            AssertEqual(limit - cost, run.Energy, $"{name} energy settles at the authored cost when the effect removes nothing");
    }

    // ---------------------------------------------------------------------------------------------
    // Board fixture/oracle: independent match detector used to build legal deterministic inputs and to
    // verify published boards. It never computes scores and never mirrors engine internals.
    // ---------------------------------------------------------------------------------------------
    private sealed class CircuitBoard
    {
        private readonly int[] itemIds;
        private readonly int[] ballTypes;
        private readonly int[] weakHitTimes;
        private readonly int[] weakUids;

        public CircuitBoard(int rows, int cols)
        {
            Rows = rows;
            Cols = cols;
            itemIds = new int[rows * cols];
            ballTypes = new int[rows * cols];
            weakHitTimes = new int[rows * cols];
            weakUids = new int[rows * cols];
        }

        public int Rows { get; }
        public int Cols { get; }

        public (int ItemId, int BallType, int WeakHitTimes) Cell(int x, int y) =>
            (itemIds[y * Cols + x], ballTypes[y * Cols + x], weakHitTimes[y * Cols + x]);

        public void Set(int x, int y, int itemId, int ballType = 1, int hitTimes = 0, int weakUid = 0)
        {
            itemIds[y * Cols + x] = itemId;
            ballTypes[y * Cols + x] = ballType;
            weakHitTimes[y * Cols + x] = hitTimes;
            weakUids[y * Cols + x] = weakUid;
        }

        public static CircuitBoard FromMapInit(JObject init, int rows, int cols) => FromItems(CircuitItems(init), rows, cols);

        public static CircuitBoard FromItems(JArray records, int rows, int cols)
        {
            CircuitBoard board = new(rows, cols);
            foreach (JObject record in records.OfType<JObject>())
            {
                (int x, int y) = CircuitPosition(record);
                board.Set(x, y, Convert.ToInt32(record["ItemId"]), Convert.ToInt32(record["BallType"]),
                    Convert.ToInt32(record["WeakHitTimes"]), Convert.ToInt32(record["WeakUid"]));
            }
            return board;
        }

        public CircuitBoard Clone()
        {
            CircuitBoard copy = new(Rows, Cols);
            Array.Copy(itemIds, copy.itemIds, itemIds.Length);
            Array.Copy(ballTypes, copy.ballTypes, ballTypes.Length);
            Array.Copy(weakHitTimes, copy.weakHitTimes, weakHitTimes.Length);
            Array.Copy(weakUids, copy.weakUids, weakUids.Length);
            return copy;
        }

        public bool HasMatch() => Matched().Count > 0;

        public HashSet<(int X, int Y)> Matched()
        {
            HashSet<(int X, int Y)> matched = new();
            for (int y = 0; y < Rows; y++)
            {
                int x = 0;
                while (x < Cols)
                {
                    if (ballTypes[y * Cols + x] != 1)
                    {
                        x++;
                        continue;
                    }
                    int end = x + 1;
                    while (end < Cols && ballTypes[y * Cols + end] == 1 && itemIds[y * Cols + end] == itemIds[y * Cols + x])
                        end++;
                    if (end - x >= 3)
                        for (int offset = x; offset < end; offset++) matched.Add((offset, y));
                    x = end;
                }
            }
            for (int x = 0; x < Cols; x++)
            {
                int y = 0;
                while (y < Rows)
                {
                    if (ballTypes[y * Cols + x] != 1)
                    {
                        y++;
                        continue;
                    }
                    int end = y + 1;
                    while (end < Rows && ballTypes[end * Cols + x] == 1 && itemIds[end * Cols + x] == itemIds[y * Cols + x])
                        end++;
                    if (end - y >= 3)
                        for (int offset = y; offset < end; offset++) matched.Add((x, offset));
                    y = end;
                }
            }
            return matched;
        }

        public bool CreatesMatch((int X, int Y) source, (int X, int Y) dest)
        {
            CircuitBoard copy = Clone();
            copy.Swap(source, dest);
            return copy.HasMatch();
        }

        public void Swap((int X, int Y) a, (int X, int Y) b)
        {
            int first = a.Y * Cols + a.X;
            int second = b.Y * Cols + b.X;
            (itemIds[first], itemIds[second]) = (itemIds[second], itemIds[first]);
            (ballTypes[first], ballTypes[second]) = (ballTypes[second], ballTypes[first]);
            (weakHitTimes[first], weakHitTimes[second]) = (weakHitTimes[second], weakHitTimes[first]);
            (weakUids[first], weakUids[second]) = (weakUids[second], weakUids[first]);
        }

        public bool HasLegalMove()
        {
            for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
            {
                if ((x + 1) < Cols && CreatesMatch((x, y), (x + 1, y)))
                    return true;
                if ((y + 1) < Rows && CreatesMatch((x, y), (x, y + 1)))
                    return true;
            }
            return false;
        }

        public HashSet<(int X, int Y)> CellsOfColour(int colour)
        {
            HashSet<(int X, int Y)> cells = new();
            for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
            {
                int index = y * Cols + x;
                if (ballTypes[index] == 1 && CircuitBallColour(itemIds[index]) == colour)
                    cells.Add((x, y));
            }
            return cells;
        }

        public List<SameColorGameCell> ToRunBoard() => Enumerable.Range(0, Rows * Cols)
            .Select(index => new SameColorGameCell
            {
                ItemId = itemIds[index],
                BallType = ballTypes[index],
                WeakUid = weakUids[index],
                WeakHitTimes = weakHitTimes[index]
            })
            .ToList();
    }

    /// <summary>Zero-match opening used as a deterministic persisted-board fixture.</summary>
    private static CircuitBoard CircuitNoMatchBoard(SameColorGameRoleTable role)
    {
        int[] pattern = [0, 0, 1, 1, 2, 2, 3, 3];
        CircuitBoard board = new(Convert.ToInt32(role.Row), Convert.ToInt32(role.Col));
        for (int y = 0; y < board.Rows; y++)
        for (int x = 0; x < board.Cols; x++)
            board.Set(x, y, Convert.ToInt32(role.BallId[pattern[(x + 2 * y) % pattern.Length]]));
        if (board.HasMatch())
            throw new InvalidDataException("Circuit no-match fixture unexpectedly contains a match.");
        return board;
    }

    /// <summary>
    /// Fixture whose swap (source -> dest) forms a straight three-match: row 3 holds X,X,Y,X so
    /// swapping the Y out yields exactly three. Legal and match-free by construction.
    /// </summary>
    private static CircuitBoard CircuitCraftedTripleBoard(SameColorGameRoleTable role, out (int X, int Y) source, out (int X, int Y) dest)
    {
        CircuitBoard board = CircuitNoMatchBoard(role);
        int matchBall = Convert.ToInt32(role.BallId[0]);
        int otherBall = Convert.ToInt32(role.BallId[1]);
        const int row = 3;
        board.Set(0, row, otherBall);
        board.Set(1, row, matchBall);
        board.Set(2, row, matchBall);
        board.Set(3, row, otherBall);
        board.Set(4, row, matchBall);
        board.Set(5, row, otherBall);
        source = (3, row);
        dest = (4, row);
        if (board.HasMatch())
            throw new InvalidDataException("Circuit crafted triple fixture already contains a match.");
        if (!board.CreatesMatch(source, dest))
            throw new InvalidDataException("Circuit crafted triple fixture swap must create a straight three.");
        CircuitBoard swapped = board.Clone();
        swapped.Swap(source, dest);
        HashSet<(int X, int Y)> cleared = swapped.Matched();
        HashSet<(int X, int Y)> expectedCleared = [(1, row), (2, row), (3, row)];
        if (!cleared.SetEquals(expectedCleared))
            throw new InvalidDataException(
                $"Circuit crafted triple fixture swap must clear exactly the authored straight three, found {cleared.Count} matched cell(s).");
        return board;
    }

    /// <summary>
    /// Fixture whose swap creates an L-shaped three-match, the authored blast prop shape: column 2
    /// already holds two extra orbs below the row so the created run corners at (2,3).
    /// </summary>
    private static CircuitBoard CircuitCraftedBlastBoard(SameColorGameRoleTable role, out (int X, int Y) source, out (int X, int Y) dest)
    {
        CircuitBoard board = CircuitNoMatchBoard(role);
        int matchBall = Convert.ToInt32(role.BallId[0]);
        int otherBall = Convert.ToInt32(role.BallId[1]);
        const int row = 3;
        const int cornerX = 2;
        board.Set(0, row, matchBall);
        board.Set(1, row, matchBall);
        board.Set(cornerX, row, otherBall);
        board.Set(3, row, matchBall);
        board.Set(4, row, otherBall);
        board.Set(5, row, Convert.ToInt32(role.BallId[3]));
        board.Set(1, row + 1, otherBall);
        board.Set(3, row + 1, otherBall);
        board.Set(cornerX, row + 1, matchBall);
        board.Set(cornerX, row + 2, matchBall);
        if (row + 3 < board.Rows)
            board.Set(cornerX, row + 3, otherBall);
        source = (cornerX, row);
        dest = (3, row);
        if (board.HasMatch())
            throw new InvalidDataException("Circuit crafted blast fixture already contains a match.");
        CircuitBoard swapped = board.Clone();
        swapped.Swap(source, dest);
        HashSet<(int X, int Y)> expected = [(0, row), (1, row), (cornerX, row), (cornerX, row + 1), (cornerX, row + 2)];
        HashSet<(int X, int Y)> matched = swapped.Matched();
        if (!matched.SetEquals(expected))
            throw new InvalidDataException(
                $"Circuit blast fixture swap must create exactly the authored L shape, found {matched.Count} matched cell(s).");
        return board;
    }

    /// <summary>
    /// Fixture whose swap is an ordinary straight three (so the move is accepted) while the board also
    /// holds one deliberate straight four and a fixed prop below it. The cascade must create the
    /// rapid-fire prop for the four and refill the column that the fixed prop splits.
    /// </summary>
    private static CircuitBoard CircuitCraftedFourBoard(SameColorGameRoleTable role, out (int X, int Y) source, out (int X, int Y) dest)
    {
        CircuitBoard board = CircuitNoMatchBoard(role);
        int matchBall = Convert.ToInt32(role.BallId[0]);
        int otherBall = Convert.ToInt32(role.BallId[1]);
        int propBall = role.PropBallIds.Select(Convert.ToInt32).First(ball => CircuitBallPropType(ball) == 2);
        const int tripleRow = 3;
        board.Set(0, tripleRow, otherBall);
        board.Set(1, tripleRow, matchBall);
        board.Set(2, tripleRow, matchBall);
        board.Set(3, tripleRow, otherBall);
        board.Set(4, tripleRow, matchBall);
        board.Set(5, tripleRow, otherBall);
        const int fourRow = 6;
        board.Set(0, fourRow, otherBall);
        for (int x = 1; x <= 4; x++)
            board.Set(x, fourRow, matchBall);
        board.Set(5, fourRow, otherBall);
        board.Set(6, fourRow, Convert.ToInt32(role.BallId[2]));
        board.Set(7, fourRow, Convert.ToInt32(role.BallId[2]));
        board.Set(2, fourRow - 1, propBall, ballType: 2);
        source = (3, tripleRow);
        dest = (4, tripleRow);
        HashSet<(int X, int Y)> expected = [(1, fourRow), (2, fourRow), (3, fourRow), (4, fourRow)];
        HashSet<(int X, int Y)> matched = board.Matched();
        if (!matched.SetEquals(expected))
            throw new InvalidDataException(
                $"Circuit four fixture must hold exactly the authored straight four, found {matched.Count} matched cell(s).");
        return board;
    }

    /// <summary>
    /// Fixture whose swap extends a crafted three into a straight four, so the first removal group is
    /// exactly the quad. A pre-existing match is deliberate: the engine only resolves matches on a move.
    /// </summary>
    private static CircuitBoard CircuitCraftedQuadBoard(SameColorGameRoleTable role, out (int X, int Y) source, out (int X, int Y) dest)
    {
        CircuitBoard board = CircuitNoMatchBoard(role);
        int matchBall = Convert.ToInt32(role.BallId[0]);
        int otherBall = Convert.ToInt32(role.BallId[1]);
        const int row = 3;
        board.Set(0, row, otherBall);
        board.Set(1, row, matchBall);
        board.Set(2, row, matchBall);
        board.Set(3, row, matchBall);
        board.Set(4, row, otherBall);
        board.Set(5, row, matchBall);
        board.Set(6, row, otherBall);
        source = (5, row);
        dest = (4, row);
        HashSet<(int X, int Y)> expected = [(1, row), (2, row), (3, row)];
        HashSet<(int X, int Y)> matched = board.Matched();
        if (!matched.SetEquals(expected))
            throw new InvalidDataException(
                $"Circuit quad fixture must hold exactly the authored straight three before the swap, found {matched.Count} matched cell(s).");
        if (!board.CreatesMatch(source, dest))
            throw new InvalidDataException("Circuit quad fixture swap must extend the three into a straight four.");
        return board;
    }

    /// <summary>
    /// Fixture that places one tracked summon adjacent to the crafted straight three, so the next swap
    /// delivers exactly one engine-policy adjacency hit to that summon.
    /// </summary>
    private static CircuitBoard CircuitSummonHitBoard(SameColorGameRoleTable role, int summonBallId, int weakUid, int weakHitTimes,
        out (int X, int Y) source, out (int X, int Y) dest)
    {
        CircuitBoard board = CircuitCraftedTripleBoard(role, out source, out dest);
        const int summonX = 2;
        const int summonY = 2;
        board.Set(summonX, summonY, summonBallId, ballType: 3, hitTimes: weakHitTimes, weakUid: weakUid);
        if (board.HasMatch())
            throw new InvalidDataException("Circuit summon hit fixture already contains a match.");
        CircuitBoard swapped = board.Clone();
        swapped.Swap(source, dest);
        HashSet<(int X, int Y)> cleared = swapped.Matched();
        if (cleared.Count == 0)
            throw new InvalidDataException("Circuit summon hit fixture swap must clear a line.");
        if (!cleared.Any(cell => Math.Abs(cell.X - summonX) + Math.Abs(cell.Y - summonY) == 1))
            throw new InvalidDataException("Circuit summon hit fixture must place the summon orthogonally adjacent to a cleared cell.");
        return board;
    }

    // ---------------------------------------------------------------------------------------------
    // Case harness: real loopback session, real registered handlers, recording Mongo proxies.
    // ---------------------------------------------------------------------------------------------
    private sealed class CircuitCase : IDisposable
    {
        private readonly MongoCollectionOverride mongo;
        private int packetId = 88_600_000;

        public CircuitCase(long playerId, string sessionId)
        {
            mongo = MongoCollectionOverride.InstallForSameColorGameCompatibility(
                out RecordingMongoCollectionProxy<Player> players,
                out RecordingMongoCollectionProxy<Character> characters,
                out RecordingMongoCollectionProxy<Inventory> inventories,
                out RecordingMongoCollectionProxy<SameColorGameRankEntry> ranks);
            Players = players;
            Characters = characters;
            Inventories = inventories;
            Ranks = ranks;
            Harness = new LoopbackSessionHarness(CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId), CreateDrawCompatibilityInventory(playerId, []), sessionId);
            Harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
            Ranks.FindResults = [];
        }

        public LoopbackSessionHarness Harness { get; }
        public RecordingMongoCollectionProxy<Player> Players { get; }
        public RecordingMongoCollectionProxy<Character> Characters { get; }
        public RecordingMongoCollectionProxy<Inventory> Inventories { get; }
        public RecordingMongoCollectionProxy<SameColorGameRankEntry> Ranks { get; }

        public Player Player() => Harness.Session.player;

        public SameColorGameState State() => Harness.Session.player.SameColorGame;

        public SameColorGameRun Run() => State().Run ?? throw new InvalidDataException("Circuit run is not active.");

        public Dictionary<int, long> Balances() => Harness.Session.inventory.Items
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count));

        public void GrantItem(int itemId, long count)
        {
            Item? item = Harness.Session.inventory.Items.FirstOrDefault(row => row.Id == itemId);
            if (item is null)
                Harness.Session.inventory.Items.Add(new Item { Id = itemId, Count = count });
            else
                item.Count += count;
        }

        /// <summary>Deterministic predecessor fixture: every boss before the requested one already passed.</summary>
        public void SeedUnlockedBosses()
        {
            State().ActivityId = CircuitCurrentActivityId();
            State().BossRecords = TableReaderV2.Parse<SameColorGameBossTable>()
                .Where(boss => Convert.ToInt32(boss.PreBossId) > 0)
                .Select(boss => Convert.ToInt32(boss.PreBossId))
                .Distinct()
                .OrderBy(bossId => bossId)
                .Select(bossId => new SameColorGameBossRecord { BossId = bossId, MaxPoint = 1, MaxCombo = 1, LastUseRoleId = 1 })
                .ToList();
        }

        public void ResetEnergy()
        {
            SameColorGameRun run = Run();
            run.Energy = Convert.ToInt32(CircuitRole(run.RoleId).EnergyLimit);
        }

        public void InstallBoard(CircuitBoard board)
        {
            SameColorGameRun run = Run();
            AssertEqual(run.Rows, board.Rows, "Circuit fixture board rows");
            AssertEqual(run.Cols, board.Cols, "Circuit fixture board columns");
            run.Board = board.ToRunBoard();
        }

        public CircuitBoard BoardSnapshot()
        {
            SameColorGameRun run = Run();
            CircuitBoard board = new(run.Rows, run.Cols);
            for (int y = 0; y < run.Rows; y++)
            for (int x = 0; x < run.Cols; x++)
            {
                SameColorGameCell cell = run.Board[y * run.Cols + x];
                board.Set(x, y, cell.ItemId, cell.BallType, cell.WeakHitTimes, cell.WeakUid);
            }
            return board;
        }

        public int BoardCell((int X, int Y) cell) => Run().Board[cell.Y * Run().Cols + cell.X].ItemId;

        public (int ItemId, int X, int Y) TargetAt(int x, int y) => (BoardCell((x, y)), x, y);

        public (JObject Response, List<(string Name, JObject Body)> Pushes) Exchange(
            string requestName, object? body, string name, int maxPackets = 24)
        {
            List<(string Name, JObject Body)> pushes = new();
            InvokeRegisteredRequestHandler(requestName, Harness.Session, packetId++, body);
            for (int index = 0; index < maxPackets; index++)
            {
                Packet packet = Harness.ReadPacket($"{name} packet {index + 1}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    pushes.Add((push.Name, JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content))));
                    continue;
                }

                AssertEqual(Packet.ContentType.Response, packet.Type, $"{name} packet type");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                string expected = requestName.EndsWith("Request", StringComparison.Ordinal)
                    ? string.Concat(requestName.AsSpan(0, requestName.Length - "Request".Length), "Response")
                    : requestName;
                AssertEqual(expected, response.Name, $"{name} response name");
                JObject payload = JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
                CircuitWire.Record(requestName, body, payload, pushes);
                return (payload, pushes);
            }
            throw new InvalidDataException($"{name}: no response within {maxPackets} packets.");
        }

        public JObject Success(string requestName, object? body, string name)
        {
            (JObject response, _) = Exchange(requestName, body, name);
            AssertEqual(0, Convert.ToInt32(response["Code"]), $"{name} Code");
            return response;
        }

        public JObject Reject(string requestName, object? body, int? expectedCode, string name)
        {
            (JObject response, _) = Exchange(requestName, body, name);
            int code = Convert.ToInt32(response["Code"]);
            AssertEqual(true, code != 0, $"{name} is rejected");
            if (expectedCode is int expected)
                AssertEqual(expected, code, $"{name} Code");
            return response;
        }

        /// <summary>
        /// Sends deterministic adjacent swaps (rows then columns, right then down) until one is
        /// accepted. The board machine guarantees a legal move on every settled board, and a rejected
        /// candidate cannot consume a round, so the scan terminates without guessing the board.
        /// </summary>
        public (JObject Response, List<(string Name, JObject Body)> Pushes) MatchMove(string name)
        {
            SameColorGameRun run = Run();
            int lastCode = 0;
            for (int y = 0; y < run.Rows; y++)
            for (int x = 0; x < run.Cols; x++)
            {
                foreach ((int X, int Y) dest in new[] { (x + 1, y), (x, y + 1) })
                {
                    if (dest.X >= run.Cols || dest.Y >= run.Rows)
                        continue;
                    (JObject response, List<(string Name, JObject Body)> pushes) = Exchange(
                        CircuitSwapRequest, CircuitSwapBody(x, y, dest.X, dest.Y), name);
                    int code = Convert.ToInt32(response["Code"]);
                    if (code == 0)
                        return (response, pushes);
                    lastCode = code;
                }
            }
            throw new InvalidDataException($"{name}: no adjacent swap was accepted (last code {lastCode}).");
        }

        public void Dispose()
        {
            Harness.Dispose();
            mongo.Dispose();
        }
    }
}
