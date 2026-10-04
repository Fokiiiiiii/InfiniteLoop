using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers.BigWorld;
using AscNet.Table.V2.share.statussyncfight.quest;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.Test
{
    internal partial class Program
    {
        // --big-world-actions-only: native level-action runner. Oracles: retail babylonia.pcap 1171/1804 (ClientExecute bytes),
        // 0446/0724 (Finish bytes), 1572-1575/1657/1797-1803 (push orderings of objectives 2002048 exit, 2002037 enter,
        // 20030101 enter), 319-719 (pause counts).
        private static void ValidateBigWorldActions()
        {
            ValidateBigWorldActionWire();
            ValidateBigWorldActionRunner();
            ValidateBigWorldActionsTeamInstance();
            ValidateBigWorldActionsWorldState();
            ValidateBigWorldActionHandlerCoverage();
            Console.WriteLine("BigWorld actions validation passed.");
        }

        // Every ServerOnly / ServerThenClient action type of LevelActionExecMode.tsv has a registered server handler, and every
        // action type any runnable list uses (quest objectives, level interact options) has an ExecMode row: the runner
        // throws mid-interaction otherwise (live: OpenNarrativeUI on the lounge Data Screen).
        private static void ValidateBigWorldActionHandlerCoverage()
        {
            var modes = TableReaderV2.Parse<LevelActionExecModeTable>().ToList();
            var missing = modes.Where(row => row.ExecMode is 1 or 3)
                .Where(row => !BigWorldActionHandlers.Table.ContainsKey(row.ActionType)).Select(row => row.ActionType).ToList();
            AssertEqual("", string.Join(",", missing), "server action types without a handler");
            HashSet<int> known = modes.Select(row => row.ActionType).ToHashSet();
            IEnumerable<string> configs = TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.quest.DlcQuestObjectiveTable>().Select(row => row.Config ?? "")
                .Concat(TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.level.sceneconfig.LevelInteractOptionTable>().Select(row => row.Config ?? ""));
            var unknown = configs.SelectMany(config => System.Text.RegularExpressions.Regex.Matches(config, "\"ActionType\":(\\d+)").Select(m => int.Parse(m.Groups[1].Value)))
                .Where(type => !known.Contains(type)).Distinct().Order().ToList();
            AssertEqual("", string.Join(",", unknown), "configured action types without an ExecMode");
        }

        private static void ValidateBigWorldActionWire()
        {
            static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
            // Retail bytes: quest context (list 9, action 4), ActorInteract context (list 1, unit range), and both Finish echoes.
            QuestActionContext quest = new(2003, 20030101, 2);
            ActorInteractActionContext actor = new(975, 1, 2, 100123, 4001, 1);
            AssertEqual("84ac416374696f6e4c697374496409ad4c61756e6368436f6e74657874bd584c6576656c4c6f6769634c61756e63685175657374436f6e7465787483a751756573744964cd07d3ab4f626a6563746976654964ce0131a295ae4f626a656374697665537461746502aa5374617274496e64657804a8456e64496e64657804",
                Hex(BigWorldLevelActions.BuildExecuteMessage(9, 4, 4, quest)), "ClientExecute quest context bytes");
            AssertEqual("84ac416374696f6e4c697374496401ad4c61756e6368436f6e74657874d925584c6576656c4c6f6769634c61756e63684163746f72496e746572616374436f6e7465787486ac4c61756e6368657255554944cd03cfb44c61756e63686572436f6e74726f6c6c6572496401a94163746f725479706502ac4163746f72506c6163654964ce0001871ba74c6576656c4964cd0fa1a84f7074696f6e496401aa5374617274496e64657800a8456e64496e64657800",
                Hex(BigWorldLevelActions.BuildExecuteMessage(1, 0, 0, actor)), "ClientExecute actor context bytes");
            AssertEqual("84ac416374696f6e4c697374496409aa5374617274496e64657804a8456e64496e64657804a7436f6e74657874bd584c6576656c4c6f6769634c61756e63685175657374436f6e7465787483a751756573744964cd07d3ab4f626a6563746976654964ce0131a295ae4f626a656374697665537461746502",
                Hex(BigWorldLevelActions.BuildFinishMessage(9, 4, 4, quest)), "Finish quest context bytes");
        }

        private static void ValidateBigWorldActionRunner()
        {
            const long playerId = 99_931;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            // Quest 2003 Ready with its first step already open: UnderTakeSelfQuest only flips the quest to InProgress.
            player.BigWorldState.QuestData.ActiveQuests[2003] = new Theatre5DlcQuest
            {
                QuestId = 2003,
                DynamicData = new Theatre5DlcQuestDynamicData
                {
                    QuestState = 1,
                    Steps = { [200301] = new Theatre5DlcQuestStep { StepId = 200301, StepState = 1 } },
                },
            };
            player.BigWorldState.QuestData.ReadyQuestIds.Add(2003);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
                CreateDrawCompatibilityInventory(playerId, []), "big-world-actions");
            BigWorldCoreClient client = new(harness);
            Session session = harness.Session;
            BigWorldEnterWorldResponse enter = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest());
            AssertEqual(0, enter.Code, "enter");
            object?[] level = MessagePackSerializer.Deserialize<object?[]>(enter.EnterResultData!.LevelData!);
            int lastSequence = (int)BwInt(((object?[])level[3]!)[4]);

            Dictionary<int, DlcQuestObjectiveTable> objectives = TableReaderV2.Parse<DlcQuestObjectiveTable>().ToDictionary(row => row.Id);
            JArray Actions(int objectiveId, string list) => (JArray)JObject.Parse(objectives[objectiveId].Config)[list]!;

            // ---- helpers over the session's outbound stream
            List<(string Label, byte[] Content, string Envelope)> inbox = [];
            (string Label, byte[] Content, string Envelope) ToPush(Packet packet)
            {
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                string label = push.Name == "XRpcCommon" ? (string)MessagePackSerializer.Deserialize<object?[]>(push.Content)[0]! : push.Name;
                if (push.Name == "XRpcComponentAction") label = "Component:" + (string)MessagePackSerializer.Deserialize<object?[]>(push.Content)[0]!;
                return (label, push.Content, push.Name);
            }
            List<(string Label, byte[] Content, string Envelope)> Drain()
            {
                List<(string, byte[], string)> pushes = [.. inbox];
                inbox.Clear();
                // Loopback delivery is asynchronous: settle after three idle polls (BigWorldCoreClient.DrainTrailing pattern).
                for (int idle = 0; idle < 3; idle++)
                {
                    while (harness.TryReadAvailablePacket("actions drain", out Packet packet))
                    {
                        idle = 0;
                        if (packet.Type == Packet.ContentType.Push) pushes.Add(ToPush(packet));
                    }
                    Thread.Sleep(15);
                }
                return pushes;
            }
            // Sends an XRpcCommon request; pushes that precede the response land in `inbox`. Returns the response code.
            int packetId = 50_000;
            int SendRpc(string rpc, byte[] args)
            {
                InvokeRegisteredRequestHandler("XRpcCommon", session, ++packetId, new object[] { rpc, args, (byte)15, (byte)1, 0 });
                while (true)
                {
                    Packet packet = harness.ReadPacket(rpc);
                    if (packet.Type == Packet.ContentType.Push) { inbox.Add(ToPush(packet)); continue; }
                    return MessagePackSerializer.Deserialize<XRpcCommonResponse>(MessagePackSerializer.Deserialize<Packet.Response>(packet.Content).Content).Code;
                }
            }
            string Labels(List<(string Label, byte[] Content, string Envelope)> pushes) => string.Join(",", pushes.Select(p => p.Label));
            int completed = 0;
            void Run(JArray actions, LevelActionContext context)
            {
                lock (Session.GetPlayerOperationLock(playerId))
                    BigWorldLevelActions.Run(session, actions, context, () => completed++);
            }
            (int Id, int Start, int End, string Context, Dictionary<string, int> Fields) ParseExecute((string Label, byte[] Content, string Envelope) push)
            {
                MessagePackReader reader = new((byte[])MessagePackSerializer.Deserialize<object?[]>(push.Content)[1]!);
                (int id, int start, int end, string name, Dictionary<string, int> fields) = (0, 0, 0, "", new Dictionary<string, int>());
                for (int pairs = reader.ReadMapHeader(); pairs > 0; pairs--)
                    switch (reader.ReadString())
                    {
                        case "ActionListId": id = reader.ReadInt32(); break;
                        case "StartIndex": start = reader.ReadInt32(); break;
                        case "EndIndex": end = reader.ReadInt32(); break;
                        case "LaunchContext":
                            name = reader.ReadString()!;
                            for (int f = reader.ReadMapHeader(); f > 0; f--) fields[reader.ReadString()!] = reader.ReadInt32();
                            break;
                    }
                return (id, start, end, name, fields);
            }
            int CallFinish(byte[] message) => SendRpc("RpcLevelActionClientExecuteFinish", message);
            object?[] Rpc((string Label, byte[] Content, string Envelope) push) => MessagePackSerializer.Deserialize<object?[]>(push.Content);
            object?[] Args((string Label, byte[] Content, string Envelope) push) => MessagePackSerializer.Deserialize<object?[]>((byte[])Rpc(push)[1]!);

            // ---- Sample A: 2002048 ExitActions [LoadNpc 700003, PlayDramaCaption(WaitComplete=false), ActivateTeleporter 100030].
            // Retail: Replicate+ChangeController, ClientExecute, teleporter pushes (NON-blocking client run), Finish later.
            QuestActionContext exit48 = new(2002, 2002048, 5);
            Run(Actions(2002048, "ExitActions"), exit48);
            var a = Drain();
            AssertEqual("XRpcActorReplicate,RpcActorChangeController,RpcLevelActionClientExecute,NotifyBigWorldActivateTeleporter,Component:RpcActiveTeleporterNotify", Labels(a), "2002048 exit push order");
            AssertEqual(0, completed, "2002048 not complete before the client Finish");
            object?[] replicate = MessagePackSerializer.Deserialize<object?[]>(a[0].Content);
            int npcUuid = (int)BwInt(replicate[5]);
            AssertEqual(("XNpc", 15L, true, lastSequence + 1, lastSequence + 1L), ((string)replicate[0]!, BwInt(replicate[2]), (bool)replicate[3]!, npcUuid >> 4, BwInt(replicate[8])), "loaded NPC: server controller, sequence continues the snapshot");
            AssertEqual(700003L, BwInt(MessagePackSerializer.Deserialize<object?[]>((byte[])replicate[1]!)[3]), "loaded NPC place id");
            object?[] change = Rpc(a[1]);
            AssertEqual((0L, 15L, 4001L), (BwInt(change[2]), BwInt(change[3]), BwInt(change[4])), "ChangeController envelope target 0 level 4001");
            AssertEqual((4001L, (long)npcUuid, 1L, true, false), (BwInt(Args(a[1])[0]), BwInt(Args(a[1])[1]), BwInt(Args(a[1])[2]), (bool)Args(a[1])[3]!, (bool)Args(a[1])[4]!), "ChangeController args");
            var exec48 = ParseExecute(a[2]);
            AssertEqual((1, 1, "XLevelLogicLaunchQuestContext", "2002,2002048,5"), (exec48.Start, exec48.End, exec48.Context, string.Join(",", exec48.Fields.Values)), "2002048 client segment [1,1] + QuestContext");
            AssertEqual((1L, 15L, 0L), (BwInt(Rpc(a[2])[2]), BwInt(Rpc(a[2])[3]), BwInt(Rpc(a[2])[4])), "ClientExecute envelope target 1 level 0");
            AssertEqual(0L, BwInt(Rpc(a[4])[2]), "teleporter component push targets all controllers");

            // Finish validation: wrong list id, wrong range and wrong context are rejected, the right one completes once.
            AssertEqual(4, CallFinish(BigWorldLevelActions.BuildFinishMessage(exec48.Id + 50, 1, 1, exit48)), "wrong ActionListId rejected");
            AssertEqual(4, CallFinish(BigWorldLevelActions.BuildFinishMessage(exec48.Id, 0, 1, exit48)), "wrong range rejected");
            AssertEqual(4, CallFinish(BigWorldLevelActions.BuildFinishMessage(exec48.Id, 1, 1, new QuestActionContext(2002, 2002047, 5))), "wrong context rejected");
            AssertEqual(0, completed, "rejected Finishes do not complete the list");
            AssertEqual(0, CallFinish(BigWorldLevelActions.BuildFinishMessage(exec48.Id, 1, 1, exit48)), "Finish accepted");
            AssertEqual(1, completed, "completion callback ran once");
            AssertEqual(4, CallFinish(BigWorldLevelActions.BuildFinishMessage(exec48.Id, 1, 1, exit48)), "duplicate Finish rejected");
            AssertEqual(1, completed, "duplicate Finish does not complete again");

            // ---- Sample B: 2002037 EnterActions [PlayScreenEffect IsWaitEnter=true, UnloadNpc 700003]: BLOCKING run.
            QuestActionContext enter37 = new(2002, 2002037, 2);
            Run(Actions(2002037, "EnterActions"), enter37);
            var b = Drain();
            AssertEqual("RpcLevelActionClientExecute", Labels(b), "2002037 enter: only the client segment before the Finish");
            var exec37 = ParseExecute(b[0]);
            AssertEqual((exec48.Id + 1, 0, 0), (exec37.Id, exec37.Start, exec37.End), "ActionListId increments; segment [0,0]");
            AssertEqual(0, CallFinish(BigWorldLevelActions.BuildFinishMessage(exec37.Id, 0, 0, enter37)), "2002037 Finish");
            var b2 = Drain();
            AssertEqual("XRpcRemoveActor", Labels(b2), "UnloadNpc only after the Finish");
            AssertEqual((0L, 15L, 4001L, 4001L, (long)npcUuid), (BwInt(Rpc(b2[0])[2]), BwInt(Rpc(b2[0])[3]), BwInt(Rpc(b2[0])[4]), BwInt(Args(b2[0])[0]), BwInt(Args(b2[0])[1])), "RemoveActor target 0, level, uuid of the loaded NPC");
            AssertEqual(2, completed, "2002037 completed after the Finish");

            // ---- Sample C: 20030101 EnterActions [UnderTakeSelfQuest, LoadNpc x3, EnableInteractable false x2, PlayDramaCaption].
            QuestActionContext enter301 = new(2003, 20030101, 2);
            Run(Actions(20030101, "EnterActions"), enter301);
            var c = Drain();
            AssertEqual("RpcQuestUpdate,XRpcActorReplicate,RpcActorChangeController,XRpcActorReplicate,RpcActorChangeController,XRpcActorReplicate,RpcActorChangeController,Component:RpcSetInteractableCmpEnableRequest,Component:RpcSetInteractableCmpEnableRequest,RpcLevelActionClientExecute",
                Labels(c.Where(p => p.Label != "NotifyBigWorldNotReadMessage").ToList()), "20030101 enter push order");
            List<int> uuids = c.Where(p => p.Label == "XRpcActorReplicate").Select(p => (int)BwInt(MessagePackSerializer.Deserialize<object?[]>(p.Content)[5])).ToList();
            AssertEqual((npcUuid >> 4) + 1, uuids[0] >> 4, "three distinct sequential uuids continue after the previous load");
            AssertEqual(3, uuids.Distinct().Count(), "distinct uuids");
            var interactables = c.Where(p => p.Label == "Component:RpcSetInteractableCmpEnableRequest").ToList();
            AssertEqual((1L, 15L, 4001L, false, false), (BwInt(Rpc(interactables[0])[2]), BwInt(Rpc(interactables[0])[3]), BwInt(Rpc(interactables[0])[4]), (bool)Args(interactables[0])[0]!, (bool)Args(interactables[1])[0]!), "SetInteractable target 1, Enable=false");
            AssertEqual(true, new[] { BwInt(Rpc(interactables[0])[5]), BwInt(Rpc(interactables[1])[5]) }.All(uuid => uuids.Contains((int)uuid)), "SetInteractable addresses the loaded actors");
            var exec301 = ParseExecute(c[^1]);
            AssertEqual((4, 4, "2003,20030101,2"), (exec301.Start, exec301.End, string.Join(",", exec301.Fields.Values)), "20030101 client segment [4,4]");
            CallFinish(BigWorldLevelActions.BuildFinishMessage(exec301.Id, 4, 4, enter301));
            AssertEqual(3, completed, "20030101 completed");
            AssertEqual(2, player.BigWorldState.QuestData.ActiveQuests[2003].DynamicData!.QuestState, "UnderTakeSelfQuest ran (quest InProgress)");
            Drain();

            // ---- Sample D: 2002014 ExitActions [UnderTake, PlayDramaCaption(non-blocking), LoadNpc x3, NpcRelativeFollowMove(ServerThenClient)].
            // The client keys running lists by ActionListId: segment [3,3] must wait for [1,1]'s Finish even though the caption
            // does not block (live client threw "same key" and never finished [3,3], stalling 2002014 at ScriptExit).
            QuestActionContext exit14 = new(2002, 2002014, 5);
            Run(Actions(2002014, "ExitActions"), exit14);
            var d = Drain().Where(p => p.Label is "RpcLevelActionClientExecute" or "XRpcActorReplicate").ToList();
            AssertEqual("RpcLevelActionClientExecute,XRpcActorReplicate,XRpcActorReplicate,XRpcActorReplicate", Labels(d), "2002014: caption segment, server loads run, follow segment held");
            var exec14 = ParseExecute(d[0]);
            AssertEqual((1, 1), (exec14.Start, exec14.End), "first segment [1,1]");
            AssertEqual(0, CallFinish(BigWorldLevelActions.BuildFinishMessage(exec14.Id, 1, 1, exit14)), "caption Finish");
            var d2 = Drain().Where(p => p.Label == "RpcLevelActionClientExecute").ToList();
            AssertEqual((1, 3, 3, exec14.Id), (d2.Count, ParseExecute(d2[0]).Start, ParseExecute(d2[0]).End, ParseExecute(d2[0]).Id), "follow segment [3,3] sent after the previous Finish");
            AssertEqual(3, completed, "2002014 not complete before the follow Finish");
            AssertEqual(0, CallFinish(BigWorldLevelActions.BuildFinishMessage(exec14.Id, 3, 3, exit14)), "follow Finish");
            AssertEqual(4, completed, "2002014 completed");
            Drain();

            // ---- WaitTime delays the walk; two distinct delays.
            List<(TimeSpan Delay, Action Fire)> timers = [];
            BigWorldLevelActions.Schedule = (delay, fire) => timers.Add((delay, fire));
            try
            {
                foreach (double seconds in new[] { 6D, 2.5D })
                {
                    timers.Clear();
                    Run(JArray.Parse($"[{{\"ActionType\":23000,\"Params\":{{\"TimeSeconds\":{seconds}}}}},{{\"ActionType\":21001,\"Params\":{{\"PlaceIdList\":[100002]}}}}]"), enter301);
                    AssertEqual((1, TimeSpan.FromSeconds(seconds), ""), (timers.Count, timers[0].Delay, Labels(Drain())), $"WaitTime {seconds}s: timer armed, nothing pushed yet");
                    int before = completed;
                    timers[0].Fire();
                    AssertEqual((before + 1, seconds == 6D ? "XRpcRemoveActor" : ""), (completed, Labels(Drain())), $"WaitTime {seconds}s: walk resumes on the timer");
                }
            }
            finally { BigWorldLevelActions.Schedule = (delay, fire) => Task.Delay(delay).ContinueWith(_ => fire()); }

            // ---- Pause gating: paused lists do not execute until RpcResumeFight has been answered.
            foreach ((int pauseCount, int placeId) in new[] { (1, 500001), (2, 500002) })
            {
                AssertEqual(0, SendRpc("RpcPauseFight", MessagePackSerializer.Serialize(new object[] { pauseCount })), $"pause {pauseCount} response");
                int before = completed;
                Run(JArray.Parse($"[{{\"ActionType\":21001,\"Params\":{{\"PlaceIdList\":[{placeId}]}}}}]"), enter301);
                AssertEqual((before, ""), (completed, Labels(Drain())), $"pause {pauseCount}: list gated");
                if (pauseCount == 2)
                {
                    AssertEqual(0, SendRpc("RpcResumeFight", MessagePackSerializer.Serialize(new object[] { 1 })), "resume to count 1 response");
                    AssertEqual((before, ""), (completed, Labels(Drain())), "still paused at count 1");
                }
                AssertEqual(0, SendRpc("RpcResumeFight", MessagePackSerializer.Serialize(new object[] { 0 })), $"resume {pauseCount} response");
                AssertEqual(0, inbox.Count, $"resume {pauseCount}: the response precedes the released actions");
                AssertEqual((before + 1, "XRpcRemoveActor"), (completed, Labels(Drain())), $"pause {pauseCount}: released after resume");
            }

            // ---- Persistence: re-enter reflects loads/unloads/interactable overrides.
            AssertEqual(true, new[] { 700003, 100002, 500001, 500002 }.All(place => player.BigWorldState.ActorOverrides.Any(o => o.PlaceId == place && o.Loaded == false)), "overrides recorded");
            InvokeRegisteredRequestHandler("LeaveWorldRequest", session, 50_100, new Dictionary<string, object>());
            _ = harness.ReadPacket("leave response");
            Drain();
            BigWorldEnterWorldResponse again = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest());
            AssertEqual(0, again.Code, "re-enter");
            object?[] level2 = MessagePackSerializer.Deserialize<object?[]>(again.EnterResultData!.LevelData!);
            Dictionary<long, object?[]> levelNpcs = ((object?[])((object?[])level2[2]!)[0]!).Cast<object?[]>()
                .Where(r => (string)r[0]! == "XNpc" && BwInt(r[9]) == 0)
                .ToDictionary(r => BwInt(MessagePackSerializer.Deserialize<object?[]>((byte[])r[1]!)[3]));
            AssertEqual(true, levelNpcs.ContainsKey(500003), "quest-loaded NPC 500003 is in the re-enter snapshot");
            AssertEqual(false, levelNpcs.ContainsKey(100002), "unloaded default NPC 100002 is omitted from the re-enter snapshot");
            AssertEqual(false, levelNpcs.ContainsKey(500001) || levelNpcs.ContainsKey(500002) || levelNpcs.ContainsKey(700003), "unloaded quest NPCs omitted");
            AssertEqual(levelNpcs.Count, ((object?[])((object?[])level2[2]!)[0]!).Cast<object?[]>().Where(r => (string)r[0]! == "XNpc" && BwInt(r[9]) == 0).Select(r => BwInt(r[5])).Distinct().Count(), "snapshot uuids distinct");
            // Interactable override: load 500001 back and disable it -> its snapshot component is [false]; 500003 stays [true].
            lock (Session.GetPlayerOperationLock(playerId))
            {
                BigWorldActors.LoadLevelNpcs(session, 4001, [500001]);
                BigWorldActors.SetInteractable(session, 4001, BigWorldActors.NpcType, 500001, false);
            }
            Drain();
            AssertEqual((false, true), (BigWorldActors.IsInteractable(session, BigWorldActors.NpcType, 500001), BigWorldActors.IsInteractable(session, BigWorldActors.NpcType, 500003)), "IsInteractable reflects overrides");
            InvokeRegisteredRequestHandler("LeaveWorldRequest", session, 50_101, new Dictionary<string, object>());
            _ = harness.ReadPacket("leave response 2");
            Drain();
            BigWorldEnterWorldResponse third = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest());
            string InteractableSnapshot(object?[][] replicates, int place) => Convert.ToHexString(MessagePackSerializer.Deserialize<object?[]>((byte[])replicates
                .Single(r => (string)r[0]! == "XNpc" && BwInt(r[9]) == 0 && BwInt(MessagePackSerializer.Deserialize<object?[]>((byte[])r[1]!)[3]) == place)[7]!)
                .Cast<object?[]>().Single(component => (string)component[1]! == "XNpcInteractableComponent")[0] as byte[] ?? []);
            object?[][] replicates3 = ((object?[])((object?[])MessagePackSerializer.Deserialize<object?[]>(third.EnterResultData!.LevelData!)[2]!)[0]!).Cast<object?[]>().ToArray();
            AssertEqual(("91C2", "91C3"), (InteractableSnapshot(replicates3, 500001), InteractableSnapshot(replicates3, 500003)), "interactable overrides in the enter snapshot");
        }
    }
}
