using System.Buffers;
using System.Runtime.CompilerServices;
using AscNet.Table.V2.share.statussyncfight.quest;
using AscNet.Common.Util;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers.BigWorld
{
    internal abstract record LevelActionContext;

    // Quest objective list. ObjectiveState is the SCRIPT state retail sends: 2 (ScriptEnter) for EnterActions,
    // 5 (ScriptExit) for ExitActions (babylonia.pcap 1448/1573/1655/1777/1804).
    internal sealed record QuestActionContext(int QuestId, int ObjectiveId, int ObjectiveState) : LevelActionContext;

    internal sealed record ActorInteractActionContext(int LauncherUuid, int LauncherControllerId, int ActorType, int ActorPlaceId, int LevelId, int OptionId) : LevelActionContext;

    // One level-action handler call: the action's config Params (JObject with "$type"), the list's launch context and
    // the level the player is in.
    internal sealed record ActionEnv(Session Session, int ActionType, JObject Params, LevelActionContext Context, int LevelId);

    // Native per-objective / per-interaction level-action list runner (client XLevelActionManager mirror).
    //
    // Execution (retail babylonia.pcap; ELevelActionExecMode from the installed GameAssembly, LevelActionExecMode.tsv):
    //  - actions run in list order; ServerOnly actions run inline on the server.
    //  - maximal contiguous runs of client-involved actions (ClientOnly / ServerThenClient) go out as ONE
    //    RpcLevelActionClientExecute {ActionListId, LaunchContext, StartIndex, EndIndex}; the indexes are INCLUSIVE
    //    into the same config list (lone action at index 0 -> 0,0; caption at index 1 -> 1,1; index 4 -> 4,4).
    //    ServerThenClient server halves run before the run is sent. A ServerThenClient action after a waiting client
    //    action starts a new run (its server half must follow that wait).
    //  - a run BLOCKS the walk until its RpcLevelActionClientExecuteFinish when a client action in it waits for the
    //    player: dump.cs XLevelAction* classes with a completion callback or Tick: PlayDrama (OnDramaFinish),
    //    PlayDramaCaption/PlayDramaInterTitleDialog.WaitComplete, NpcNavigateTo/NpcGuideMoveTo.WaitComplete,
    //    PlayScreenEffect.IsWaitEnter, TeleportPlayer.WithBlackScreen. Every other client action finishes instantly
    //    on the client, so the walk continues (retail: caption WaitComplete=false -> next server action is pushed
    //    before the Finish arrives; PlayScreenEffect IsWaitEnter=true -> UnloadNpc only after the Finish).
    //  - the list completes when every action ran and every client run's Finish arrived; onComplete runs once.
    //  - ActionListId = per-player-session counter (XLevelActionManager._incId), consumed by every non-empty list.
    //  - WaitTime (ExecMode Any) delays the walk on a server timer.
    //  - RpcPauseFight/RpcResumeFight carry the client's pause count; while it is > 0 no walk step executes
    //    (retail 1777: the exit push is only sent after the RpcResumeFight response).
    internal static class BigWorldLevelActions
    {
        private const int AnyMode = 0, ServerOnlyMode = 1, ClientOnlyMode = 2, ServerThenClientMode = 3, WaitTimeType = 23000;

        private static readonly Lazy<Dictionary<int, int>> ExecModes = new(() =>
            TableReaderV2.Parse<LevelActionExecModeTable>().ToDictionary(row => row.ActionType, row => row.ExecMode));

        private sealed class ActionList
        {
            public int Id;
            public JArray Actions = null!;
            public LevelActionContext Context = null!;
            public Action OnComplete = null!;
            public int Next;
            public bool WalkDone, Done;
            public (int Start, int End)? Blocked;
            // The client keys running lists by ActionListId (XLevelActionManager._actionListDict): a second segment of the
            // same list arriving while one is running throws "same key" and is never finished. Next run waits for Pending.
            public bool AwaitingPending;
            public readonly List<(int Start, int End)> Pending = new();
            public int TimerToken;
        }

        private sealed class State
        {
            public int IncId;
            public int PauseCount;
            public readonly Dictionary<int, ActionList> Lists = new();
            public readonly List<ActionList> Deferred = new();
        }

        private static readonly ConditionalWeakTable<Session, State> States = new();

        // ponytail: server timers are Task.Delay; replaceable so tests can drive the clock deterministically.
        internal static Action<TimeSpan, Action> Schedule = (delay, callback) => Task.Delay(delay).ContinueWith(_ => callback());

        internal static void Reset(Session session) => States.Remove(session);

        internal static void Run(Session session, JArray actions, LevelActionContext context, Action onComplete)
        {
            if (actions.Count == 0)
            {
                onComplete();
                return;
            }
            State state = States.GetOrCreateValue(session);
            ActionList list = new() { Id = ++state.IncId, Actions = actions, Context = context, OnComplete = onComplete };
            state.Lists[list.Id] = list;
            Walk(session, state, list);
        }

        internal static void OnPauseCount(Session session, int pauseCount)
        {
            State state = States.GetOrCreateValue(session);
            state.PauseCount = Math.Max(0, pauseCount);
            BigWorldQuestRuntime.OnFightPause(session, state.PauseCount > 0);
            if (state.PauseCount > 0 || state.Deferred.Count == 0)
                return;
            List<ActionList> resume = [.. state.Deferred];
            state.Deferred.Clear();
            foreach (ActionList list in resume)
                Walk(session, state, list);
        }

        private static (int Type, JObject Params) Read(JArray actions, int index)
        {
            JObject action = (JObject)actions[index];
            return (action.Value<int>("ActionType"), action["Params"] as JObject ?? new JObject());
        }

        private static int ModeOf(int type) =>
            ExecModes.Value.TryGetValue(type, out int mode) ? mode : throw new InvalidDataException($"Level action type {type} has no ELevelActionExecMode.");

        // True when the client action only finishes after the player-visible effect ends (see class comment).
        internal static bool WaitsForClient(int type, JObject p) => type switch
        {
            16000 => true, // PlayDrama
            16001 or 16004 or 14000 or 14004 => p.Value<bool?>("WaitComplete") == true,
            16002 => p.Value<bool?>("IsWaitEnter") == true,
            20000 => p.Value<bool?>("WithBlackScreen") == true,
            _ => false,
        };

        private static void Walk(Session session, State state, ActionList list)
        {
            if (list.Done)
                return;
            if (state.PauseCount > 0)
            {
                if (!state.Deferred.Contains(list)) state.Deferred.Add(list);
                return;
            }
            try
            {
                while (list.Next < list.Actions.Count)
                {
                    int index = list.Next;
                    (int type, JObject p) = Read(list.Actions, index);
                    int mode = ModeOf(type);
                    if (mode == AnyMode)
                    {
                        if (type != WaitTimeType)
                            throw new InvalidDataException($"Level action type {type} has ExecMode Any but no server implementation.");
                        list.Next = index + 1;
                        double seconds = p.Value<double?>("TimeSeconds") ?? 0;
                        if (seconds <= 0)
                            continue;
                        int token = ++list.TimerToken;
                        Schedule(TimeSpan.FromSeconds(seconds), () => OnTimer(session, state, list, token));
                        return;
                    }
                    if (mode == ServerOnlyMode)
                    {
                        list.Next = index + 1;
                        Execute(session, list, type, p);
                        continue;
                    }
                    if (mode is not (ClientOnlyMode or ServerThenClientMode))
                        throw new InvalidDataException($"Level action type {type}: ExecMode {mode} (ClientThenServer) is not used by any config.");
                    if (list.Pending.Count > 0)
                    {
                        list.AwaitingPending = true;
                        return;
                    }

                    int end = index;
                    bool blocking = false;
                    for (; end < list.Actions.Count; end++)
                    {
                        (int t, JObject tp) = Read(list.Actions, end);
                        int m = ModeOf(t);
                        if (m is not (ClientOnlyMode or ServerThenClientMode) || (m == ServerThenClientMode && blocking))
                            break;
                        blocking |= WaitsForClient(t, tp);
                    }
                    list.Next = end;
                    for (int k = index; k < end; k++)
                    {
                        (int t, JObject tp) = Read(list.Actions, k);
                        if (ModeOf(t) == ServerThenClientMode)
                            Execute(session, list, t, tp);
                    }
                    list.Pending.Add((index, end - 1));
                    session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcLevelActionClientExecute",
                        BuildExecuteMessage(list.Id, index, end - 1, list.Context), 0));
                    if (blocking)
                    {
                        list.Blocked = (index, end - 1);
                        return;
                    }
                }
                list.WalkDone = true;
                TryComplete(state, list);
            }
            catch
            {
                list.Done = true;
                state.Lists.Remove(list.Id);
                throw;
            }
        }

        private static void OnTimer(Session session, State state, ActionList list, int token)
        {
            lock (Session.GetPlayerOperationLock(session.player.PlayerData.Id))
            {
                if (list.Done || list.TimerToken != token || !States.TryGetValue(session, out State? current) || current != state)
                    return;
                Walk(session, state, list);
            }
        }

        private static void TryComplete(State state, ActionList list)
        {
            if (list.Done || !list.WalkDone || list.Pending.Count > 0)
                return;
            list.Done = true;
            state.Lists.Remove(list.Id);
            list.OnComplete();
        }

        private static void Execute(Session session, ActionList list, int type, JObject p)
        {
            if (!BigWorldActionHandlers.Table.TryGetValue(type, out Action<ActionEnv>? handler))
                throw new NotSupportedException($"Level action type {type} has no server handler.");
            handler(new ActionEnv(session, type, p, list.Context, TargetLevel(session, list.Context)));
        }

        // A quest objective's actions act on the objective's configured level (DlcQuestObjective.LevelId), not wherever the
        // player is: 10010119's ExitActions switch to instance 4026 and then unload District A NPCs, and 10010120's
        // EnterActions (run after the switch) load the projector 900001 back in District A. Off-level loads/unloads are
        // persisted and applied on that level's next enter. Objectives without a level and interactions use the current level.
        private static int TargetLevel(Session session, LevelActionContext context) =>
            context is QuestActionContext quest
                && BigWorldQuestModule.Objectives.Value.TryGetValue(quest.ObjectiveId, out var cfg)
                && cfg.LevelId is > 0 and var level
                ? level
                : BigWorldModule.CurrentLevelId(session.player.BigWorldState);

        // RpcLevelActionClientExecuteFinish {ActionListId, StartIndex, EndIndex, Context}. Returns the XRpc response code.
        internal static int HandleClientExecuteFinish(Session session, byte[] args)
        {
            if (!TryParseClientMessage(args, out int listId, out int start, out int end, out string? contextName, out Dictionary<string, int>? contextFields)
                || !States.TryGetValue(session, out State? state)
                || !state.Lists.TryGetValue(listId, out ActionList? list)
                || !list.Pending.Contains((start, end)))
                return BigWorldModule.CodeInvalidRequest;
            (string name, (string, int)[] fields) = ContextFields(list.Context);
            if (contextName != name || contextFields!.Count != fields.Length || fields.Any(f => !contextFields.TryGetValue(f.Item1, out int v) || v != f.Item2))
                return BigWorldModule.CodeInvalidRequest;
            list.Pending.Remove((start, end));
            if (list.Blocked == (start, end))
            {
                list.Blocked = null;
                Walk(session, state, list);
            }
            else if (list.AwaitingPending && list.Pending.Count == 0)
            {
                list.AwaitingPending = false;
                Walk(session, state, list);
            }
            else
                TryComplete(state, list);
            return 0;
        }

        // Union context: type name string followed inline by the field map (retail writes no array header).
        private static (string Name, (string, int)[] Fields) ContextFields(LevelActionContext context) => context switch
        {
            QuestActionContext q => ("XLevelLogicLaunchQuestContext", [("QuestId", q.QuestId), ("ObjectiveId", q.ObjectiveId), ("ObjectiveState", q.ObjectiveState)]),
            ActorInteractActionContext a => ("XLevelLogicLaunchActorInteractContext",
                [("LauncherUUID", a.LauncherUuid), ("LauncherControllerId", a.LauncherControllerId), ("ActorType", a.ActorType),
                 ("ActorPlaceId", a.ActorPlaceId), ("LevelId", a.LevelId), ("OptionId", a.OptionId)]),
            _ => throw new NotSupportedException($"Unknown level action context {context.GetType().Name}."),
        };

        // RpcLevelActionClientExecute: pairs ActionListId, LaunchContext, StartIndex, EndIndex (retail order).
        internal static byte[] BuildExecuteMessage(int listId, int start, int end, LevelActionContext context)
        {
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            writer.WriteMapHeader(4);
            writer.Write("ActionListId"); BigWorldXRpc.WriteInt(ref writer, listId);
            writer.Write("LaunchContext"); WriteContext(ref writer, context);
            writer.Write("StartIndex"); BigWorldXRpc.WriteInt(ref writer, start);
            writer.Write("EndIndex"); BigWorldXRpc.WriteInt(ref writer, end);
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        // RpcLevelActionClientExecuteFinish (client -> server): pairs ActionListId, StartIndex, EndIndex, Context.
        internal static byte[] BuildFinishMessage(int listId, int start, int end, LevelActionContext context)
        {
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            writer.WriteMapHeader(4);
            writer.Write("ActionListId"); BigWorldXRpc.WriteInt(ref writer, listId);
            writer.Write("StartIndex"); BigWorldXRpc.WriteInt(ref writer, start);
            writer.Write("EndIndex"); BigWorldXRpc.WriteInt(ref writer, end);
            writer.Write("Context"); WriteContext(ref writer, context);
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        private static void WriteContext(ref MessagePackWriter writer, LevelActionContext context)
        {
            (string name, (string, int)[] fields) = ContextFields(context);
            writer.Write(name);
            writer.WriteMapHeader(fields.Length);
            foreach ((string key, int value) in fields) { writer.Write(key); BigWorldXRpc.WriteInt(ref writer, value); }
        }

        private static bool TryParseClientMessage(byte[] args, out int listId, out int start, out int end, out string? contextName, out Dictionary<string, int>? fields)
        {
            listId = start = end = 0;
            contextName = null;
            fields = null;
            try
            {
                MessagePackReader reader = new(args);
                bool haveList = false, haveStart = false, haveEnd = false;
                int count = reader.ReadMapHeader();
                for (int i = 0; i < count; i++)
                {
                    switch (reader.ReadString())
                    {
                        case "ActionListId": listId = reader.ReadInt32(); haveList = true; break;
                        case "StartIndex": start = reader.ReadInt32(); haveStart = true; break;
                        case "EndIndex": end = reader.ReadInt32(); haveEnd = true; break;
                        case "Context" or "LaunchContext":
                            contextName = reader.ReadString();
                            fields = new Dictionary<string, int>();
                            for (int f = reader.ReadMapHeader(); f > 0; f--)
                                fields[reader.ReadString()!] = reader.ReadInt32();
                            break;
                        default: reader.Skip(); break;
                    }
                }
                return haveList && haveStart && haveEnd && fields is not null;
            }
            catch (Exception ex) when (ex is MessagePackSerializationException or EndOfStreamException or InvalidOperationException)
            {
                return false;
            }
        }
    }
}
