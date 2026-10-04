using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using MessagePack;

namespace AscNet.Test
{
    internal partial class Program
    {
        // --big-world-interaction-only: RpcPlayerInteractRequest. Oracle: retail babylonia.pcap requests 440/220/384 (elevator options:
        // Start -> XRpcTeleportResetOnGroundRequest -> ClientExecute(ActorInteract ctx, [0,0]) -> client Finish -> Finish) and 635 (NPC without actions).
        private static void ValidateBigWorldInteraction()
        {
            const long playerId = 99_921;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
                CreateDrawCompatibilityInventory(playerId, []), "big-world-interaction");
            BigWorldCoreClient client = new(harness);

            BigWorldEnterWorldResponse enter = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest());
            AssertEqual(0, enter.Code, "enter");
            object?[] level = MessagePackSerializer.Deserialize<object?[]>(enter.EnterResultData!.LevelData!);
            int playerNpcUuid = (int)BwInt(((object?[])((object?[])level[2]!)[0]!).Cast<object?[]>()
                .First(r => (string)r[0]! == "XNpc" && BwInt(r[9]) == playerId)[5]);

            object[] Interact(int uuid, int placeId, int type, int optionId, int levelId = 4001, int launcher = -1) => new object[] { "RpcPlayerInteractRequest",
                MessagePackSerializer.Serialize(new object[] { (int)playerId, 1, launcher == -1 ? playerNpcUuid : launcher, uuid, placeId, type, levelId, optionId }), (byte)15, (byte)1, levelId };
            List<object?[]> Rpcs() => client.Pushes.Where(push => push.Name.StartsWith("XRpc")).Select(push => MessagePackSerializer.Deserialize<object?[]>(push.Content)).ToList();
            string[] Names() => Rpcs().Select(rpc => (string)rpc[0]!).ToArray();
            object?[] Push(string rpc) => Rpcs().Single(p => (string)p[0]! == rpc);
            // RpcLevelActionClientExecute map {ActionListId, LaunchContext, StartIndex, EndIndex}: LaunchContext is the union type name string
            // followed by its field map (no array header; retail pcap 1171).
            (int ListId, int Start, int End, string ContextType, IDictionary<object, object> Context, byte[] ContextBytes) ParseExecute(byte[] args)
            {
                MessagePackReader reader = new(args);
                int list = 0, start = -1, end = -1;
                string type = "";
                IDictionary<object, object> fields = new Dictionary<object, object>();
                byte[] raw = [];
                for (int i = reader.ReadMapHeader(); i > 0; i--)
                {
                    switch (reader.ReadString())
                    {
                        case "ActionListId": list = reader.ReadInt32(); break;
                        case "StartIndex": start = reader.ReadInt32(); break;
                        case "EndIndex": end = reader.ReadInt32(); break;
                        case "LaunchContext":
                            long from = reader.Consumed;
                            type = reader.ReadString()!;
                            fields = (IDictionary<object, object>)MessagePackSerializer.Deserialize<object>(ref reader, Packet.InboundOptions)!;
                            raw = args[(int)from..(int)reader.Consumed];
                            break;
                        default: throw new InvalidDataException("unexpected ClientExecute key");
                    }
                }
                return (list, start, end, type, fields, raw);
            }
            object?[] FinishFor(byte[] executeArgs)
            {
                var execute = ParseExecute(executeArgs);
                System.Buffers.ArrayBufferWriter<byte> buffer = new();
                MessagePackWriter writer = new(buffer);
                writer.WriteMapHeader(4);
                writer.Write("ActionListId"); writer.Write(execute.ListId);
                writer.Write("StartIndex"); writer.Write(execute.Start);
                writer.Write("EndIndex"); writer.Write(execute.End);
                writer.Write("Context"); writer.WriteRaw(execute.ContextBytes);
                writer.Flush();
                return ["RpcLevelActionClientExecuteFinish", buffer.WrittenMemory.ToArray(), (byte)15, (byte)1, 0];
            }

            // Quest 2002 (Ready on entry): objectives before 2002034 done, so 2002034 (InteractComplete on scene object 100066) is in progress.
            List<int> questOrder = TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.quest.DlcQuestObjectiveTable>().Where(o => o.StepId == 200201)
                .OrderBy(o => Convert.ToInt32(o.Order)).Select(o => o.Id).ToList();
            void FinishObjectivesBefore(int objectiveId, int fromObjectiveId = 0) => AssertEqual(0, client.Call<DlcQuestUpdateResponse>("DlcQuestUpdateRequest", new DlcQuestUpdateRequest
            {
                QuestStepObjectiveList = questOrder.SkipWhile(id => id != fromObjectiveId && fromObjectiveId != 0).TakeWhile(id => id != objectiveId).Select(id => new Theatre5DlcQuestStepObjective { Id = id, ObjectiveState = 6 }).ToList()
            }).Code, $"objectives before {objectiveId} finished");
            FinishObjectivesBefore(2002034);

            // ---- Elevator scene objects (option 1 = TeleportPlayer): Start, ActorAction, ClientExecute, then Finish only after the client Finish.
            foreach ((int placeId, int uuid) in new[] { (100066, 61), (100123, 62) })
            {
                AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Interact(uuid, placeId, 2, 1)).Code, $"elevator {placeId} interact");
                AssertEqual("RpcNpcInteractStartNotify,XRpcTeleportResetOnGroundRequest,RpcLevelActionClientExecute", string.Join(",", Names()), $"elevator {placeId} push order");
                object?[] start = MessagePackSerializer.Deserialize<object?[]>((byte[])Push("RpcNpcInteractStartNotify")[1]!);
                AssertEqual((uuid, placeId, 2, 1), ((int)BwInt(start[0]), (int)BwInt(start[1]), (int)BwInt(start[2]), (int)BwInt(start[3])), $"elevator {placeId} Start args");
                object?[] reset = Push("XRpcTeleportResetOnGroundRequest");
                AssertEqual((4001L, (long)playerNpcUuid, 1L), (BwInt(reset[4]), BwInt(reset[5]), BwInt(reset[2])), $"elevator {placeId} reset on ground: level, player uuid, target controller");
                var execute = ParseExecute((byte[])Push("RpcLevelActionClientExecute")[1]!);
                AssertEqual(true, execute.ContextType.EndsWith("LaunchActorInteractContext"), $"elevator {placeId} launch context type");
                AssertEqual((playerNpcUuid, 1, 2, placeId, 4001, 1), ((int)BwInt(execute.Context["LauncherUUID"]), (int)BwInt(execute.Context["LauncherControllerId"]), (int)BwInt(execute.Context["ActorType"]),
                    (int)BwInt(execute.Context["ActorPlaceId"]), (int)BwInt(execute.Context["LevelId"]), (int)BwInt(execute.Context["OptionId"])), $"elevator {placeId} ActorInteract context");
                // Retail: the lone ServerThenClient TeleportPlayer at index 0 is segment [0,0] (inclusive end).
                AssertEqual((0, 0), (execute.Start, execute.End), $"elevator {placeId} client segment");

                object?[] finish = FinishFor((byte[])Push("RpcLevelActionClientExecute")[1]!);
                client.Call<XRpcCommonResponse>("XRpcCommon", finish);
                // 2002034 targets scene object 100066 only: its interaction progresses the quest after Finish; the other elevator does not.
                AssertEqual(placeId == 100066 ? "RpcNpcInteractFinishNotify,RpcQuestObjectiveProgressNotify" : "RpcNpcInteractFinishNotify",
                    string.Join(",", Names().Where(n => n is "RpcNpcInteractFinishNotify" or "RpcQuestObjectiveProgressNotify")), $"elevator {placeId} Finish after the client Finish, then quest progress");
                AssertEqual("RpcNpcInteractFinishNotify", Names().First(), $"elevator {placeId} Finish is first after the client Finish");
            }

            // ---- NPC without option actions: Start + Finish at once, nothing else.
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Interact(70, 100318, 1, 1)).Code, "plain NPC interact");
            AssertEqual("RpcNpcInteractStartNotify,RpcNpcInteractFinishNotify", string.Join(",", Names()), "plain NPC Start+Finish");

            // ---- NPC whose option plays a drama (client-only action, no teleport): no reset-on-ground; Finish after the client Finish.
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Interact(71, 100002, 1, 1)).Code, "drama NPC interact");
            AssertEqual("RpcNpcInteractStartNotify,RpcLevelActionClientExecute", string.Join(",", Names()), "drama NPC Start, ClientExecute");
            client.Call<XRpcCommonResponse>("XRpcCommon", FinishFor((byte[])Push("RpcLevelActionClientExecute")[1]!));
            AssertEqual("RpcNpcInteractFinishNotify", string.Join(",", Names()), "drama NPC Finish after the client Finish");

            // ---- Quest NPC 700003 (2002036 InteractComplete): Start + Finish, then quest progress (retail request 635).
            FinishObjectivesBefore(2002036, fromObjectiveId: 2002046); // 2002034 already closed by the elevator
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Interact(1103, 700003, 1, 1)).Code, "quest NPC interact");
            string[] npcNames = Names();
            AssertEqual("RpcNpcInteractStartNotify,RpcNpcInteractFinishNotify,RpcQuestObjectiveProgressNotify", string.Join(",", npcNames.Take(3)), "quest NPC Start, Finish, progress");

            // ---- Collectable still grants exactly once.
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Interact(95, 100017, 2, 1)).Code, "chest interact");
            AssertEqual("RpcSceneObjectCollectNotify,RpcNpcInteractStartNotify,RpcNpcInteractFinishNotify", string.Join(",", Names()), "chest push order");
            AssertEqual(1, client.Pushed<NotifyBigWorldBoxData>(nameof(NotifyBigWorldBoxData)).BoxRewardedCnt, "chest counted");
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Interact(95, 100017, 2, 1)).Code, "chest interact again");
            AssertEqual("RpcNpcInteractStartNotify,RpcNpcInteractFinishNotify", string.Join(",", Names()), "second chest interact grants nothing");

            // ---- Invalid requests are rejected without pushes.
            foreach ((string what, object[] request) in new (string, object[])[]
            {
                ("unknown option", Interact(61, 100066, 2, 99)),
                ("unknown scene object", Interact(61, 123_456, 2, 1)),
                ("unknown npc", Interact(61, 123_456, 1, 1)),
                ("scene object placeId under the npc type", Interact(61, 100066, 1, 1)),
                ("unknown actor type", Interact(61, 100066, 7, 1)),
                ("wrong level", Interact(61, 100066, 2, 1, levelId: 4003)),
                ("foreign launcher", Interact(61, 100066, 2, 1, launcher: 12_345)),
            })
            {
                AssertEqual(4, client.Call<XRpcCommonResponse>("XRpcCommon", request).Code, $"{what} rejected");
                AssertEqual(0, client.Pushes.Count, $"{what}: no pushes");
            }
            // ---- Big-map teleport (RpcPlayerSwitchLevelRequest [Controller, Level, TargetLevel, Pos, Rot, PinLevel, PinId]).
            // Pin 100008 (4001, plain scene object 100010) teleports to 4003; pin 100010 (4001) is bound to teleporter 100121 and
            // targets its own level (TeleportLevelId 0).
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Interact(95, 100017, 2, 1)).Code, "(reset pushes)");
            BigWorldPlayerState bw = harness.Session.player.BigWorldState;
            object[] Teleport(int level, int target, double[] pos, double yaw, int pinLevel, int pinId) => new object[] { "RpcPlayerSwitchLevelRequest",
                MessagePackSerializer.Serialize(new object[] { 1, level, target, pos.Select(v => (float)v).ToArray(),
                    new[] { 0f, (float)Math.Sin(yaw * Math.PI / 360), 0f, (float)Math.Cos(yaw * Math.PI / 360) }, pinLevel, pinId }), (byte)15, (byte)1, level };
            void Rejected(string what, object[] request)
            {
                (int levelBefore, double? xBefore) = (bw.LastLevelId, bw.LastPosition?.X);
                AssertEqual(4, client.Call<XRpcCommonResponse>("XRpcCommon", request).Code, $"{what} rejected");
                AssertEqual((0, levelBefore, xBefore), (client.Pushes.Count, bw.LastLevelId, bw.LastPosition?.X), $"{what}: no switch, state kept");
            }
            Rejected("inactive teleporter pin", Teleport(4001, 4001, [594.03, 150.83, 1295.72], -146, 4001, 100010));
            Rejected("current level mismatch", Teleport(4003, 4003, [18.43, 1.144, 17.55], -95, 4001, 100008));
            Rejected("target is not the pin's", Teleport(4001, 4005, [18.43, 1.144, 17.55], -95, 4001, 100008));
            Rejected("unknown pin", Teleport(4001, 4003, [18.43, 1.144, 17.55], -95, 4001, 123_456));
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Teleport(4001, 4003, [18.43, 1.144, 17.55], -95, 4001, 100008)).Code, "teleport to the lounge");
            AssertEqual(true, client.Pushes.First().Name == "XRpcCommon" && (string)MessagePackSerializer.Deserialize<object?[]>(client.Pushes[0].Content)[0]! == "RpcPlayerSwitchLevelNotify", "lounge switch notify first");
            AssertEqual((4003, 18.43, 17.55, true), (bw.LastLevelId, Math.Round(bw.LastPosition!.X, 2), Math.Round(bw.LastPosition.Z, 2), Math.Abs(bw.LastRotationY!.Value + 95) < 0.1), "lounge pose persisted");
            Rejected("inactive teleporter pin from the lounge", Teleport(4003, 4001, [594.03, 150.83, 1295.72], -146, 4001, 100010));
            bw.TeleporterData[4001] = [100121];
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Teleport(4003, 4001, [594.03, 150.83, 1295.72], -146, 4001, 100010)).Code, "teleport to activated teleporter");
            object?[] back = MessagePackSerializer.Deserialize<object?[]>(client.Pushes[0].Content);
            AssertEqual(("RpcPlayerSwitchLevelNotify", 4003L, 4001L), ((string)back[0]!, BwInt(((object?[])MessagePackSerializer.Deserialize<object?[]>((byte[])back[1]!)[4]!)[0]), BwInt(((object?[])MessagePackSerializer.Deserialize<object?[]>((byte[])back[1]!)[5]!)[0])), "switch back leaves 4003, enters 4001");
            AssertEqual((4001, 594.03, 1295.72, true), (bw.LastLevelId, Math.Round(bw.LastPosition!.X, 2), Math.Round(bw.LastPosition.Z, 2), Math.Abs(bw.LastRotationY!.Value + 146) < 0.1), "teleporter pose persisted");
            // Same-level teleports are executed by the client locally: acknowledged, nothing pushed or moved.
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Teleport(4001, 4001, [594.03, 150.83, 1295.72], -146, 4001, 100010)).Code, "same-level teleport");
            AssertEqual(0, client.Pushes.Count, "same-level teleport: no pushes");

            // ---- Space Leap (instance 4008, quest 5001): cogs are touch-collected (RpcTriggerInteractNotify) and scored by the step's
            // StatisticModule (collectable type 2000 -> Score +20 and GoldCount +1, 2001 -> Score +100); a cog is collected once;
            // the death zone trigger (host 300026) counts DeathCount; 50010201 (Score >= 300) completes and counts a Star.
            AssertEqual(0, client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = 4008 }).Code, "enter Space Leap");
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", new object[] { "RpcPlayerEnterLevelComplete", MessagePackSerializer.Serialize(Array.Empty<object>()), (byte)15, (byte)1, 4008 }).Code, "Space Leap loaded");
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Interact(1, 300001, 1, 1, levelId: 4008)).Code, "start the run");
            var runtime = AscNet.GameServer.Handlers.BigWorld.BigWorldQuestRuntime.GetVar;
            int Var(string key) => (int)(runtime(harness.Session.player, 5001, 1, key) ?? -1);
            int CogUuid(int placeId) => AscNet.GameServer.Handlers.BigWorld.BigWorldActors.TryGetUuid(harness.Session, 4008, 2, placeId, out int uuid) ? uuid : -1;
            object[] Touch(int uuid, int state = 1) => new object[] { "RpcTriggerInteractNotify", MessagePackSerializer.Serialize(new object[] { 2, playerNpcUuid, 1, state, 1 }), (byte)0, (byte)1, 4008, uuid };
            AssertEqual((0, 0, 0, 0, true), (Var("Score"), Var("GoldCount"), Var("DeathCount"), Var("Star"), CogUuid(300012) > 0 && CogUuid(300037) > 0), "run started: vars zero, cogs loaded");
            int normal = CogUuid(300012), large = CogUuid(300037);
            AssertEqual(0, client.Call<XRpcComponentActionResponse>("XRpcComponentAction", Touch(normal)).Code, "touch a cog");
            AssertEqual(("RpcSceneObjectCollectNotify", normal, 20, 1), (Names().Single(n => n == "RpcSceneObjectCollectNotify"),
                (int)BwInt(Rpcs().Single(p => (string)p[0]! == "RpcSceneObjectCollectNotify")[5]), Var("Score"), Var("GoldCount")), "normal cog: collected, +20 Score, +1 GoldCount");
            AssertEqual(true, Rpcs().Any(p => (string)p[0]! == "RpcQuestSyncVars" && BwInt(MessagePackSerializer.Deserialize<object?[]>((byte[])p[1]!)[0]) == 5001), "vars synced to the client");
            client.Call<XRpcComponentActionResponse>("XRpcComponentAction", Touch(normal));
            string[] repeatEnter = Names();
            client.Call<XRpcComponentActionResponse>("XRpcComponentAction", Touch(normal, 2));
            AssertEqual((20, 1, "", ""), (Var("Score"), Var("GoldCount"), string.Join(",", repeatEnter), string.Join(",", Names())), "repeat touch and exit add nothing and push nothing");
            AssertEqual(false, AscNet.GameServer.Handlers.BigWorld.BigWorldActors.LoadedOverride(harness.Session.player.BigWorldState, 4008, 2, 300012), "collected cog persisted as unloaded");
            client.Call<XRpcComponentActionResponse>("XRpcComponentAction", Touch(large));
            AssertEqual((120, 1), (Var("Score"), Var("GoldCount")), "large cog: +100 Score, no GoldCount");
            foreach (int placeId in Enumerable.Range(300013, 13))
                client.Call<XRpcComponentActionResponse>("XRpcComponentAction", Touch(CogUuid(placeId)));
            Theatre5DlcQuestStepObjective star1 = harness.Session.player.BigWorldState.QuestData.ActiveQuests[5001].DynamicData!.Steps[500102].Objectives[50010201];
            AssertEqual((380, 14, 6, 1), (Var("Score"), Var("GoldCount"), star1.ObjectiveState, Var("Star")), "Score >= 300 completes 50010201 and counts a Star");
            client.Call<XRpcComponentActionResponse>("XRpcComponentAction", Touch(CogUuid(300026)));
            client.Call<XRpcComponentActionResponse>("XRpcComponentAction", Touch(CogUuid(300026)));
            AssertEqual(2, Var("DeathCount"), "every death zone entry counts");
            // Reaching the goal sets DoSettle = 1; the end step's CheckIntVar (EIntCheckType 2 = >=, DoSettle >= 1) must then close
            // and run its exit list (settle; the client half finishes the quest). Live: it stayed InProgress because CheckType 2
            // was read as ">", so Path I never finished and Path II never unlocked.
            // The Drama_5001_001 option 1002 (Path II) is gated on CheckLevelPlayCompleted 4001001, option 1003 (Path III) on 4001002.
            AssertEqual(false, harness.Session.player.BigWorldState.LevelPlayDatas.ContainsKey(4001001), "Path II still locked before Path I is settled");
            AscNet.GameServer.Handlers.BigWorld.BigWorldQuestRuntime.OnClientComplete(harness.Session, 5001, 50010301);
            // The exit list has a WaitTime: let the deferred remainder run.
            var steps = harness.Session.player.BigWorldState.QuestData.ActiveQuests[5001].DynamicData!.Steps;
            for (int i = 0; i < 40 && !steps.ContainsKey(500104); i++)
            {
                Thread.Sleep(25);
                AscNet.GameServer.Handlers.BigWorld.BigWorldQuestRuntime.Tick(harness.Session);
            }
            var settle = steps[500104].Objectives[50010402];
            AssertEqual((1, true), (Var("DoSettle"), settle.ObjectiveState > 3), "goal reached: DoSettle >= 1 closes the settle objective");
            // Live: Path II stayed locked after a cleared Path I because the settle objective's ObjectiveEnter fixes (FullyClear,
            // CompleteLevelPlay) only ran on a level reload; they run as the objective opens. The run scored 3 stars: full clear.
            AssertEqual((true, true, false), (harness.Session.player.BigWorldState.LevelPlayDatas.TryGetValue(4001001, out BigWorldLevelPlayData? pathI), pathI?.IsFullCleared ?? false,
                harness.Session.player.BigWorldState.LevelPlayDatas.ContainsKey(4001002)), "settling Path I clears level play 4001001 (unlocks Path II), not 4001002");

            // ---- Re-entering Path I after a finished run (the user's sequence). Live: the old run's settle list was still pending when the
            // player left; the next entry relaunched that run, its exit list unloaded the cogs, settled and finished quest 5001 the moment
            // the level loaded, and the finished quest left Path I without a start. A visit must start a fresh run.
            BigWorldPlayerState sl = harness.Session.player.BigWorldState;
            Theatre5DlcQuestInfo quests = sl.QuestData;
            var questRuntime = AscNet.GameServer.Handlers.BigWorld.BigWorldQuestRuntime.Tick;
            void FinishRun()
            {
                // Pushes are collected per call: an inert request pumps the ones the deferred lists emitted, then the client half is acked.
                for (int i = 0; i < 60 && !quests.FinishedQuests.Contains(5001); i++)
                {
                    Thread.Sleep(25);
                    questRuntime(harness.Session);
                    client.Call<XRpcCommonResponse>("XRpcCommon", new object[] { "RpcRLObjectLoadCompleted", MessagePackSerializer.Serialize(new object[] { 1 }), (byte)15, (byte)1, 0 });
                    foreach (object?[] execute in Rpcs().Where(p => (string)p[0]! == "RpcLevelActionClientExecute").ToList())
                        client.Call<XRpcCommonResponse>("XRpcCommon", FinishFor((byte[])execute[1]!));
                }
            }
            Theatre5DlcVector3 LuaStart = new() { X = 243.7F, Y = 37F, Z = 322.7F };
            void EnterPathI()
            {
                client.Pushes.Clear();
                AssertEqual(0, client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = 4008, TargetPos = LuaStart }).Code, "enter Path I");
                AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", new object[] { "RpcPlayerEnterLevelComplete", MessagePackSerializer.Serialize(Array.Empty<object>()), (byte)15, (byte)1, 4008 }).Code, "Path I loaded");
                Thread.Sleep(50);
                questRuntime(harness.Session);
            }
            void AssertFreshRun(string label, double y = 37.0)
            {
                Theatre5DlcQuestDynamicData run = quests.ActiveQuests[5001].DynamicData!;
                AssertEqual((4008, 2, "500101", 3, 0), (sl.InstLevelId, run.QuestState, string.Join(",", run.Steps.Keys), run.Steps[500101].Objectives[50010101].ObjectiveState,
                    run.FinishedObjectiveIds.Count), $"{label}: fresh run at the start step");
                AssertEqual((-1, -1, -1, (bool?)null, 243.7, y, 322.7), (Var("Score"), Var("DoSettle"), Var("DeathCount"),
                    AscNet.GameServer.Handlers.BigWorld.BigWorldActors.LoadedOverride(sl, 4008, 2, 300012),
                    Math.Round(sl.InstPosition!.X, 1), Math.Round(sl.InstPosition.Y, 1), Math.Round(sl.InstPosition.Z, 1)), $"{label}: no vars, the old run's cog unloads dropped, entry pose");
            }

            // Path I run 1 stopped at the goal: its settle list is pending (the client half never ran) when the player leaves.
            int countBefore = sl.InstLevelFinishedCounts.GetValueOrDefault(4008);
            AssertEqual(0, client.Call<LeaveInstLevelResponse>("LeaveInstLevelRequest", new LeaveInstLevelRequest()).Code, "leave Path I mid-settle");
            EnterPathI();
            AssertFreshRun("after leaving mid-settle");
            AssertEqual(false, Rpcs().Any(p => (string)p[0]! == "RpcLevelActionClientExecute"), "the old run's settle list did not run on re-entry");
            AssertEqual(countBefore, sl.InstLevelFinishedCounts.GetValueOrDefault(4008), "re-entry does not finish the level again");

            // Run 2 to the goal: it settles and finishes normally (the settle record keeps the best score).
            int bestBefore = sl.InstSettleRecords.TryGetValue(4008, out BigWorldInstSettleRecord? firstRecord) ? firstRecord.BestScore : 0;
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Interact(1, 300001, 1, 1, levelId: 4008)).Code, "start run 2");
            Thread.Sleep(50);
            questRuntime(harness.Session);
            AssertEqual((0, 0), (Var("Score"), Var("DeathCount")), "run 2 vars start at zero");
            AscNet.GameServer.Handlers.BigWorld.BigWorldQuestRuntime.OnClientComplete(harness.Session, 5001, 50010301);
            FinishRun();
            BigWorldInstSettleRecord run2 = sl.InstSettleRecords[4008];
            AssertEqual((true, false, countBefore + 1, true, Math.Max(bestBefore, run2.Score)), (quests.FinishedQuests.Contains(5001), quests.ActiveQuests.ContainsKey(5001),
                sl.InstLevelFinishedCounts.GetValueOrDefault(4008), run2.IsWin, run2.BestScore), "run 2 settled and finished quest 5001");

            // The settle UI's restart button (DlcReChallengeInstRequest): a fresh run at the level's start pose without leaving.
            client.Pushes.Clear();
            AssertEqual(0, client.Call<DlcReChallengeInstResponse>("DlcReChallengeInstRequest", null).Code, "re-challenge");
            object?[] reenterArgs = MessagePackSerializer.Deserialize<object?[]>((byte[])Rpcs().Single(p => (string)p[0]! == "RpcPlayerSwitchLevelNotify")[1]!);
            AssertEqual((4008L, 4008L, 4008L, false, 243.7, 36.6, 322.7), (BwInt(reenterArgs[1]), BwInt(((object?[])reenterArgs[4]!)[0]), BwInt(((object?[])reenterArgs[5]!)[0]), sl.InstLevelFinished,
                Math.Round(sl.InstPosition!.X, 1), Math.Round(sl.InstPosition.Y, 1), Math.Round(sl.InstPosition.Z, 1)), "re-challenge reloads the level at its start pose");
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", new object[] { "RpcPlayerEnterLevelComplete", MessagePackSerializer.Serialize(Array.Empty<object>()), (byte)15, (byte)1, 4008 }).Code, "re-challenge loaded");
            Thread.Sleep(50);
            questRuntime(harness.Session);
            AssertFreshRun("after re-challenge", 36.6);
            AssertEqual(countBefore + 1, sl.InstLevelFinishedCounts.GetValueOrDefault(4008), "re-challenge keeps the finish history");
            Console.WriteLine("BigWorld interaction validation passed.");
        }
    }
}
