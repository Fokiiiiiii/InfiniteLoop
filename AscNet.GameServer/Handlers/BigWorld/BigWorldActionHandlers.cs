using AscNet.Common.MsgPack;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Server implementations of native level actions, keyed by ELevelActionType. Every ServerOnly / ServerThenClient
    // type of LevelActionExecMode.tsv is registered (ServerThenClient handlers are the server half only; the runner
    // dispatches the client half). Registration is split by concern over the partial class files.
    internal static partial class BigWorldActionHandlers
    {
        internal static readonly Dictionary<int, Action<ActionEnv>> Table = Build();

        private static Dictionary<int, Action<ActionEnv>> Build()
        {
            Dictionary<int, Action<ActionEnv>> table = new();
            RegisterCore(table);
            RegisterTeamInstance(table);
            RegisterWorldState(table);
            return table;
        }

        private static void RegisterCore(Dictionary<int, Action<ActionEnv>> t)
        {
            t[1001] = ActivateTeleporter;
            t[3000] = CalculateVar;
            t[5001] = EnableActorInteractable;
            t[12000] = e => BigWorldActors.LoadLevelNpcs(e.Session, e.LevelId, PlaceIds(e));
            t[12001] = e => BigWorldActors.LoadSceneObjects(e.Session, e.LevelId, PlaceIds(e));
            t[19002] = SetVar;
            t[19010] = e => BigWorldQuestRuntime.SetNavPointActive(e.Session, e.Params.Value<int>("QuestObjectiveId"),
                e.Params.Value<int>("NavPointId"), e.Params.Value<bool>("Active"));
            t[21000] = e => BigWorldQuestRuntime.UnderTakeSelfQuest(e.Session, RequireQuest(e).QuestId);
            t[21001] = e => BigWorldActors.UnloadLevelNpcs(e.Session, e.LevelId, PlaceIds(e));
            t[21002] = e => BigWorldActors.UnloadSceneObjects(e.Session, e.LevelId, PlaceIds(e));
            t[21003] = e => BigWorldQuestRuntime.UnlockBranchQuestResult(e.Session, e.Params.Value<int>("BranchQuestResultId"),
                e.Params.Value<int?>("QuestId"));
        }

        private static IEnumerable<int> PlaceIds(ActionEnv e) => e.Params["PlaceIdList"]!.Values<int>();

        private static QuestActionContext RequireQuest(ActionEnv e) => e.Context as QuestActionContext
            ?? throw new InvalidDataException($"Level action {e.ActionType} needs a quest context, got {e.Context.GetType().Name}.");

        // 5001 EnableActorInteractableComponent [PlaceId, ActorType, Enable]; retail: RpcSetInteractableCmpEnableRequest.
        private static void EnableActorInteractable(ActionEnv e) =>
            BigWorldActors.SetInteractable(e.Session, e.LevelId, e.Params.Value<int>("ActorType"), e.Params.Value<int>("PlaceId"), e.Params.Value<bool>("Enable"));

        // 1001 ActivateTeleporter: persisted activation + NotifyBigWorldActivateTeleporter (BigWorldModule) and, when the
        // scene object is live in the level, its RpcActiveTeleporterNotify (retail 1574/1575 order: notify, component push).
        private static void ActivateTeleporter(ActionEnv e)
        {
            foreach (int placeId in e.Params["SceneObjectPlaceIdList"]!.Values<int>())
                if (BigWorldModule.ActivateTeleporter(e.Session, e.LevelId, placeId)
                    && BigWorldActors.TryGetUuid(e.Session, e.LevelId, BigWorldActors.SceneObjectType, placeId, out int uuid))
                    e.Session.SendPush("XRpcComponentAction", BigWorldXRpc.ComponentAction("RpcActiveTeleporterNotify", BigWorldXRpc.Args(), e.LevelId, uuid));
        }

        private const int IntVar = 1, FloatVar = 2, BoolVar = 3; // EVarType

        // 19002 SetVar [Key, VarType, Bool/Int/FloatValue] -> the quest's var block (QuestRuntime persists it).
        private static void SetVar(ActionEnv e)
        {
            int varType = e.Params.Value<int>("VarType");
            object value = varType switch
            {
                IntVar => e.Params.Value<int?>("IntValue") ?? throw new InvalidDataException("SetVar Int without IntValue."),
                FloatVar => e.Params.Value<float?>("FloatValue") ?? throw new InvalidDataException("SetVar Float without FloatValue."),
                BoolVar => e.Params.Value<bool?>("BoolValue") ?? throw new InvalidDataException("SetVar Bool without BoolValue."),
                _ => throw new InvalidDataException($"SetVar VarType {varType} is not supported (Vector vars are not editable in configs)."),
            };
            BigWorldQuestRuntime.SetVar(e.Session, RequireQuest(e).QuestId, varType, e.Params.Value<string>("Key")!, value);
        }

        // 3000 CalculateVar: Result = Left <MathOp> Right (EVarMathOp 1 + 2 - 3 x 4 /); operands are var refs or literals,
        // unset vars read as the type default (XVarBlock.GetValueOrDefault); the result is stored in the ResultVarToken's type.
        private static void CalculateVar(ActionEnv e)
        {
            int questId = RequireQuest(e).QuestId;
            double left = Operand(e, questId, (JObject)e.Params["LeftVarToken"]!), right = Operand(e, questId, (JObject)e.Params["RightVarToken"]!);
            double result = e.Params.Value<int>("MathOp") switch
            {
                1 => left + right,
                2 => left - right,
                3 => left * right,
                4 => right == 0 ? throw new InvalidDataException("CalculateVar division by zero.") : left / right,
                var op => throw new InvalidDataException($"CalculateVar MathOp {op} is not supported."),
            };
            JObject target = (JObject)e.Params["ResultVarToken"]!;
            int type = target.Value<int>("VarType");
            object stored = type switch
            {
                IntVar => (int)result,
                FloatVar => (float)result,
                BoolVar => result != 0,
                _ => throw new InvalidDataException($"CalculateVar result VarType {type} is not supported."),
            };
            BigWorldQuestRuntime.SetVar(e.Session, questId, type, target.Value<string>("StrKey")!, stored);
        }

        private static double Operand(ActionEnv e, int questId, JObject token)
        {
            if (token["Literal"] is JObject literal)
                return literal["Value"] is { Type: JTokenType.Boolean } flag ? (flag.Value<bool>() ? 1 : 0) : literal.Value<double>("Value");
            object? value = BigWorldQuestRuntime.GetVar(e.Session.player, questId, token.Value<int>("VarType"), token.Value<string>("StrKey")!);
            return value switch { null => 0, bool b => b ? 1 : 0, _ => Convert.ToDouble(value) };
        }
    }
}
