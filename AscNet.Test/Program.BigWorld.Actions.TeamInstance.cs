using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers.BigWorld;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.Test
{
    internal partial class Program
    {
        // Team / instance level actions driven through BigWorldLevelActions.Run. Oracles: retail babylonia.pcap 1276
        // (RpcSyncTeamData team shape: [LevelId, CurNpcPos, NpcDataList, [CurNpcPos, NpcDataList, [[XNpc replicate]]]]),
        // TrialNpc/TrialPlayerNpc/TrialPlayerSelfNpc client tables, JumperLevelSettleData field order (dump.cs D:743909).
        private static void ValidateBigWorldActionsTeamInstance()
        {
            const long playerId = 99_941;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
                CreateDrawCompatibilityInventory(playerId, []), "big-world-actions-team");
            BigWorldCoreClient client = new(harness);
            Session session = harness.Session;
            BigWorldPlayerState state = player.BigWorldState;
            BigWorldEnterWorldResponse enter = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest());
            AssertEqual(0, enter.Code, "enter");
            int realTeam = enter.EnterResultData!.WorldData.Players[0].NpcList.Count;
            int lastSequence = (int)BwInt(((object?[])MessagePackSerializer.Deserialize<object?[]>(enter.EnterResultData.LevelData!)[3]!)[4]);

            List<(string Label, byte[] Content)> Drain()
            {
                List<(string, byte[])> pushes = [];
                // Loopback delivery is asynchronous: stop after three idle polls.
                for (int idle = 0; idle < 3; idle++)
                {
                    while (harness.TryReadAvailablePacket("actions team drain", out Packet packet))
                    {
                        idle = 0;
                        if (packet.Type != Packet.ContentType.Push) continue;
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        pushes.Add((push.Name == "XRpcCommon" ? (string)MessagePackSerializer.Deserialize<object?[]>(push.Content)[0]! : push.Name, push.Content));
                    }
                    Thread.Sleep(30);
                }
                return pushes;
            }
            void Run(string actions, LevelActionContext? context = null)
            {
                lock (Session.GetPlayerOperationLock(playerId))
                    BigWorldLevelActions.Run(session, JArray.Parse(actions), context ?? new QuestActionContext(2003, 20030101, 2), () => { });
            }
            object?[] Args((string Label, byte[] Content) push) => MessagePackSerializer.Deserialize<object?[]>((byte[])MessagePackSerializer.Deserialize<object?[]>(push.Content)[1]!);
            static IDictionary<object, object> Map(object? value) => (IDictionary<object, object>)value!;
            static string Labels(List<(string Label, byte[] Content)> pushes) => string.Join(",", pushes.Select(p => p.Label));
            static string Add(string ids, int mode, int pos) =>
                $$$"""[{"ActionType":1000,"Params":{"$type":"XConfigLevelActionParamsAddTrialNpcToTeam","TrialNpcIds":[{{{ids}}}],"AddMode":{{{mode}}},"CurNpcPos":{{{pos}}}}}]""";
            const string Remove = """[{"ActionType":18000,"Params":{"$type":"XConfigLevelActionParamsRemoveTrialNpcFromTeam"}}]""";

            // ---- Trial team. Cover TrialNpc 3020 (TrialPlayerNpc: Npc 1060, character 1021006) then Append 2015 (Npc 3029).
            Run(Add("3020", 1, 0));
            var pushes = Drain();
            AssertEqual("RpcAddTrialNpcToTeam", Labels(pushes), "cover push");
            object?[] add = Args(pushes[0]);
            object?[] fightTeam = (object?[])add[2]!;
            object?[] npcs = (object?[])fightTeam[1]!;
            AssertEqual((4001L, 1L, 0L, 1), (BwInt(add[0]), BwInt(add[1]), BwInt(fightTeam[0]), npcs.Length), "cover args: level, mode, CurNpcPos, only the trial member");
            AssertEqual((1060L, 3020L, 1021006L), (BwInt(Map(npcs[0])["Id"]), BwInt(Map(npcs[0])["TrialId"]), BwInt(Map(Map(npcs[0])["Character"])["Id"])), "TrialPlayerNpc 3020 member");
            object?[] replicates = (object?[])((object?[])fightTeam[2]!)[0]!;
            AssertEqual(1, replicates.Length, "one new XNpc replicate");
            object?[] replicate = (object?[])replicates[0]!;
            AssertEqual(("XNpc", 15L, lastSequence + 1L, lastSequence + 1L, 0L), ((string)replicate[0]!, BwInt(replicate[2]), BwInt(replicate[5]) >> 4, BwInt(replicate[8]), BwInt(replicate[9])),
                "replicate is server-controlled with the next server sequence (retail 1276)");
            // The controlled trial member arrives backstage: the client only hands control over (SwitchIn/SwitchOut, OnPlayerNpcChangedEvent
            // for followers and gameplay listeners) while CurNpc != PlayerNpc, and a non-backstage player NPC becomes PlayerNpc on arrival.
            object?[] repNpc = MessagePackSerializer.Deserialize<object?[]>((byte[])replicate[1]!);
            AssertEqual((true, 2L), ((bool)repNpc[6]!, BwInt(repNpc[12])), "cover replicate of the controlled member is backstage (IsBackState, HideFlags Backstage)");
            AssertEqual("3020", string.Join(",", state.TrialNpcIds), "persisted trial ids");

            Run(Add("2015", 2, 1));
            pushes = Drain();
            add = Args(pushes[0]);
            fightTeam = (object?[])add[2]!;
            npcs = (object?[])fightTeam[1]!;
            AssertEqual((2L, 1L, "1060,3029", 1), (BwInt(add[1]), BwInt(fightTeam[0]), string.Join(",", npcs.Select(n => BwInt(Map(n)["Id"]))), ((object?[])((object?[])fightTeam[2]!)[0]!).Length),
                "append keeps the covered trial team and replicates only the new member");
            AssertEqual(1, state.CurNpcPos, "CurNpcPos follows the action");

            // The enter snapshot reflects the trial team (BuildNpcList + RepFight trial fields).
            var data = client.Call<BigWorldGetEnterWorldDataResponse>("BigWorldGetEnterWorldDataRequest", null).EnterResultData!;
            AssertEqual("1060,3029", string.Join(",", data.WorldData.Players[0].NpcList.Select(n => n.Id)), "enter snapshot team");
            object?[] fight = MessagePackSerializer.Deserialize<object?[]>(data.FightData!);
            AssertEqual((1L, 1L), (BwInt(fight[6]), BwInt(fight[7])), "RepFight TrialCurNpcPos / TrialNpcAddMode");

            Run(Remove);
            pushes = Drain();
            object?[] removed = Args(pushes[0]);
            AssertEqual("RpcRemoveTrialNpcFromTeam", Labels(pushes), "remove push");
            fightTeam = (object?[])removed[1]!;
            AssertEqual((realTeam, realTeam, 0), (((object?[])fightTeam[1]!).Length, ((object?[])((object?[])fightTeam[2]!)[0]!).Length, state.TrialNpcIds.Count),
                "covered real team returns as new actors");

            // Append-only trial: the real team stays on the client, so removing replicates nothing.
            Run(Add("3", 2, 0));
            Drain();
            Run(Remove);
            removed = Args(Drain()[0]);
            AssertEqual((realTeam, 0), (((object?[])((object?[])removed[1]!)[1]!).Length, ((object?[])((object?[])((object?[])removed[1]!)[2]!)[0]!).Length), "append-only remove replicates nothing");

            // Player-self trial members: commandant with the trial's fixed outfit (TrialPlayerSelfNpc), gender picks the half.
            Run(Add("4", 1, 0));
            Dictionary<object, object> self4 = (Dictionary<object, object>)Map(((object?[])((object?[])Args(Drain()[0])[2]!)[1]!)[0]);
            Run(Add("999910301", 1, 0));
            Dictionary<object, object> self9 = (Dictionary<object, object>)Map(((object?[])((object?[])Args(Drain()[0])[2]!)[1]!)[0]);
            AssertEqual((true, 3, 9999102L), ((bool)self4["IsPlayerSelf"], ((object?[])Map(self4["PartData"])["PartList"]!).Length, BwInt(Map(self4["Character"])["FashionId"])), "TrialPlayerSelfNpc 4");
            AssertEqual((true, 4, 9999103L), ((bool)self9["IsPlayerSelf"], ((object?[])Map(self9["PartData"])["PartList"]!).Length, BwInt(Map(self9["Character"])["FashionId"])), "TrialPlayerSelfNpc 999910301");
            Run(Remove);
            Drain();

            // ---- Level transfer. Enter instance 4008 (RequestEnterInstLevel) -> same state as EnterInstLevelRequest + switch push.
            const string Rotation = "null";
            string Enter(int action, string key, int level, string pos) =>
                $$$"""[{"ActionType":{{{action}}},"Params":{"{{{key}}}":{{{level}}},"Position":{{{pos}}},"Rotation":{{{Rotation}}}}}]""";
            Run(Enter(18001, "InstLevelId", 4008, "[1.5,2.5,3.5]"));
            pushes = Drain();
            // Entering 4008 also (re)starts its level-bound jumper quest 5001 (TransferLevel, before the switch like the first entry of
            // EnterInstLevelRequest); the undertake follows the level (OnLevelEntered).
            int switchAt = pushes.FindIndex(p => p.Label == "RpcPlayerSwitchLevelNotify");
            AssertEqual(("RpcActivateQuests", "NotifyNewEnteredBigWorldLevelId,RpcQuestUpdate"), (pushes[0].Label, string.Join(",", pushes.Skip(switchAt + 1).Take(2).Select(p => p.Label))), "enter instance pushes");
            pushes = pushes.Skip(switchAt).ToList();
            object?[] switched = Args(pushes[0]);
            AssertEqual((1L, 4008L, 0, 4001L, 4008L), (BwInt(switched[0]), BwInt(switched[1]), ((object?[])switched[2]!).Length,
                BwInt(((object?[])switched[4]!)[0]), BwInt(((object?[])switched[5]!)[0])), "switch notify: controller, level, no travellers, leave/enter levels");
            // LevelData is the world-entry RepLevel of the target with the team replicated at the switch target (the client
            // needs the player NPC in the level when its camera loads).
            object?[] switchLevel = MessagePackSerializer.Deserialize<object?[]>((byte[])switched[3]!);
            List<object?[]> teamReplicates = ((object?[])((object?[])switchLevel[2]!)[0]!).Cast<object?[]>().Where(r => (string)r[0]! == "XNpc" && BwInt(r[9]) != 0).ToList();
            object?[] moveComponent = MessagePackSerializer.Deserialize<object?[]>((byte[])teamReplicates[0][7]!).Cast<object?[]>().Single(c => (string)c[1]! == "XNpcMoveComponent");
            object?[] movePosition = (object?[])MessagePackSerializer.Deserialize<object?[]>((byte[])moveComponent[0]!)[0]!;
            AssertEqual((4008L, realTeam, 1.5, 2.5, 3.5), (BwInt(switchLevel[0]), teamReplicates.Count,
                Convert.ToDouble(movePosition[0]), Convert.ToDouble(movePosition[1]), Convert.ToDouble(movePosition[2])), "LevelData: target level, team replicated at the switch position");
            AssertEqual(4008, state.InstLevelId, "instance level persisted");
            // A switched level has no LoadComplete: RpcPlayerEnterLevelComplete releases the level-start pushes for the new
            // level (live: without them the client hung loading the immersion projector level 4026). Only once.
            byte[] noArgs = MessagePackSerializer.Serialize(new object[] { 1 });
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", new object[] { "RpcPlayerEnterLevelComplete", noArgs, (byte)15, (byte)1, 0 }).Code, "switch enter complete");
            List<(string Rpc, long Level)> starts = client.Pushes.Where(p => p.Name == "XRpcCommon")
                .Select(p => MessagePackSerializer.Deserialize<object?[]>(p.Content)).Select(e => ((string)e[0]!, BwInt(e[4])))
                .Where(r => r.Item1 is "RpcBeginCheckRLObjectCompleted" or "RpcSetCombatState" or "RpcBeginUpdateLevel").ToList();
            AssertEqual("RpcBeginCheckRLObjectCompleted:4008,RpcSetCombatState:4008,RpcBeginUpdateLevel:4008", string.Join(",", starts.Select(s => $"{s.Rpc}:{s.Level}")), "level-start pushes after switch");
            client.Call<XRpcCommonResponse>("XRpcCommon", new object[] { "RpcPlayerEnterLevelComplete", noArgs, (byte)15, (byte)1, 0 });
            AssertEqual(false, client.Pushes.Any(p => p.Name == "XRpcCommon" && (string)MessagePackSerializer.Deserialize<object?[]>(p.Content)[0]! == "RpcBeginUpdateLevel"), "level-start pushes sent once");
            Drain();

            // Quest actions target the objective's level even while the player is elsewhere (live: 10010120's EnterActions ran
            // after 10010119's switch into 4026 and loaded the District A projector 900001 into 4026, so it never reappeared).
            // The player is in instance 4008 here; both objectives are District A (4001) objectives.
            Run("""[{"ActionType":12001,"Params":{"$type":"XConfigLevelActionParamsLoadSceneObject","PlaceIdList":[900001]}}]""", new QuestActionContext(1001, 10010120, 2));
            Run("""[{"ActionType":21001,"Params":{"$type":"XConfigLevelActionParamsUnloadNpc","PlaceIdList":[900002]}}]""", new QuestActionContext(1001, 10010119, 5));
            AssertEqual((true, false, (bool?)null, (bool?)null), (BigWorldActors.LoadedOverride(state, 4001, BigWorldActors.SceneObjectType, 900001),
                BigWorldActors.LoadedOverride(state, 4001, BigWorldActors.NpcType, 900002),
                BigWorldActors.LoadedOverride(state, 4008, BigWorldActors.SceneObjectType, 900001),
                BigWorldActors.LoadedOverride(state, 4008, BigWorldActors.NpcType, 900002)), "off-level quest actions persist on the objective's level");
            Drain();

            // FinishInstLevel: once per visit; the history counts the level.
            Run("""[{"ActionType":6000,"Params":{"$type":"XConfigLevelActionFinishInstLevel"}}]""");
            Run("""[{"ActionType":6000,"Params":{"$type":"XConfigLevelActionFinishInstLevel"}}]""");
            AssertEqual(1, state.InstLevelFinishedCounts.GetValueOrDefault(4008), "FinishInstLevel counted once per visit");

            // Quest var switch (native XQuestVarCalculateData Copy): quest 2009's var := this quest's var.
            state.QuestData.ActiveQuests[4038] = new Theatre5DlcQuest { QuestId = 4038, DynamicData = new Theatre5DlcQuestDynamicData { QuestState = 1, VarBlockData = new Theatre5DlcVarBlockData { IntDict = { ["4038"] = 7 } } } };
            state.QuestData.ActiveQuests[2009] = new Theatre5DlcQuest { QuestId = 2009, DynamicData = new Theatre5DlcQuestDynamicData { QuestState = 1, VarBlockData = new Theatre5DlcVarBlockData { IntDict = { ["2009"] = 0 } } } };
            const string Switch = """[{"ActionType":6000,"Params":{"$type":"XConfigLevelActionFinishInstLevel","UseSwitchQuestVar":true,"QuestVarSwitchDataList":[{"CalTargetQuestId":2009,"MathOp":5,"LeftQuestVarToken":{"$type":"XConfigQuestVarRefToken","VarType":1,"StrKey":"2009","IntKey":0},"RightQuestVarToken":{"$type":"XConfigQuestVarRefToken","VarType":1,"StrKey":"4038","IntKey":0}}]}}]""";
            Run(Switch, new QuestActionContext(4038, 40380701, 5));
            AssertEqual(7, state.QuestData.ActiveQuests[2009].DynamicData!.VarBlockData!.IntDict["2009"], "var switch copies 7");
            state.QuestData.ActiveQuests[4038].DynamicData!.VarBlockData!.IntDict["4038"] = 3;
            Run(Switch, new QuestActionContext(4038, 40380701, 5));
            AssertEqual(3, state.QuestData.ActiveQuests[2009].DynamicData!.VarBlockData!.IntDict["2009"], "var switch copies 3");
            Drain();

            // Leave without exit params -> back to the saved open-world level; then a level switch and an exit-target leave.
            Run("""[{"ActionType":18002,"Params":{"$type":"XConfigLevelActionParamsRequestLeaveInstLevel","SaveOnExit":false}}]""");
            pushes = Drain();
            switched = Args(pushes[0]);
            AssertEqual((0, 4001L, 4008L, false), (state.InstLevelId, BwInt(switched[1]), BwInt(((object?[])switched[4]!)[0]), state.InstLevelFinished), "leave returns to the open world level");
            Run(Enter(19004, "NextLevelId", 4003, "[12.5,1.25,13.5]"));
            AssertEqual((4003, "12.5"), (state.LastLevelId, state.LastPosition!.X.ToString(System.Globalization.CultureInfo.InvariantCulture)), "SwitchLevel moves the open-world pose");
            Drain();
            Run("""[{"ActionType":18002,"Params":{"$type":"XConfigLevelActionParamsRequestLeaveInstLevel","SaveOnExit":true,"UseExitTargetLevelParams":true,"WorldId":0,"LevelId":4001,"TargetPos":[5,6,7],"TargetRot":[0,90,0]}}]""");
            switched = Args(Drain()[0]);
            AssertEqual((4001, 6.0, 4003L, 4001L), (state.LastLevelId, state.LastPosition!.Y, BwInt(((object?[])switched[4]!)[0]), BwInt(switched[1])), "exit target params send the player to LevelId/TargetPos");

            // ---- Settle + level play (quest 5002 = Space Leap level play 4001002 in instance 4009).
            JObject Jumper(int score, bool win) => new()
            {
                ["ActionType"] = 19005,
                ["Params"] = new JObject
                {
                    ["$type"] = "XConfigLevelActionParamsSettleInstLevel", ["SettleType"] = 1, ["Theme"] = 1,
                    ["InputVars"] = new JArray("Objective1", "Objective2", "Objective3", "Score", "Time", "IsWin", "DeathCount", "GoldCount", "Star", "TriggerJudge", "TriggerHideRoad"),
                },
            };
            void Vars(int quest, int score, bool win) => state.QuestData.ActiveQuests[quest] = new Theatre5DlcQuest
            {
                QuestId = quest,
                DynamicData = new Theatre5DlcQuestDynamicData
                {
                    QuestState = 1,
                    VarBlockData = new Theatre5DlcVarBlockData
                    {
                        IntDict = { ["Objective1"] = 50020201, ["Objective2"] = 50020202, ["Objective3"] = 50020203, ["Score"] = score, ["DeathCount"] = 2, ["GoldCount"] = 9, ["Star"] = 3 },
                        FloatDict = { ["Time"] = 33.5f },
                        BoolDict = { ["IsWin"] = win, ["TriggerJudge"] = false, ["TriggerHideRoad"] = true, ["FullyClear"] = win },
                    },
                },
            };
            Run(Enter(18001, "InstLevelId", 4009, "[0,0,0]"));
            Drain();
            Vars(5002, 120, true);
            Run(new JArray(Jumper(120, true)).ToString(), new QuestActionContext(5002, 50020402, 5));
            BigWorldInstSettleRecord record = state.InstSettleRecords[4009];
            AssertEqual((120, 120, 33.5f, true, 9, 3, "50020201,50020202,50020203", true, 1), (record.Score, record.BestScore, record.PlayTime, record.IsWin, record.GoldCount, record.StarCount,
                string.Join(",", record.ObjectiveIds), record.IsTriggerHideRoad, state.InstLevelFinishedCounts.GetValueOrDefault(4009)), "settle record from quest vars; a win completes the instance");
            Vars(5002, 80, false);
            Run(new JArray(Jumper(80, false)).ToString(), new QuestActionContext(5002, 50020402, 5));
            record = state.InstSettleRecords[4009];
            AssertEqual((80, 120, false, 1), (record.Score, record.BestScore, record.IsWin, state.InstLevelFinishedCounts.GetValueOrDefault(4009)), "a lost settle keeps the best score and does not complete again");
            Drain();

            const string CompletePlay = """[{"ActionType":3001,"Params":{"$type":"XConfigLevelActionParamsCompleteLevelPlay","IsFullyClearedVarRef":{"VarType":3,"StrKey":"FullyClear","IntKey":0}}}]""";
            Vars(5002, 120, true);
            Run(CompletePlay, new QuestActionContext(5002, 50020301, 5));
            AssertEqual(true, state.LevelPlayDatas[4001002].IsFullCleared, "CompleteLevelPlay full clear on level play 4001002");
            NotifyBigWorldLevelPlayDataChange played = MessagePackSerializer.Deserialize<NotifyBigWorldLevelPlayDataChange>(Drain().Single(p => p.Label == nameof(NotifyBigWorldLevelPlayDataChange)).Content);
            AssertEqual((4001002, true), (played.PlayId, played.LevelPlayData.IsFullCleared), "CompleteLevelPlay pushes the level play data the client's drama gate reads");
            Run(Enter(18001, "InstLevelId", 4008, "[0,0,0]"));
            Vars(5001, 10, false);
            Run(CompletePlay, new QuestActionContext(5001, 50010301, 5));
            AssertEqual((true, false), (state.LevelPlayDatas.ContainsKey(4001001), state.LevelPlayDatas[4001001].IsFullCleared), "plain clear on level play 4001001");
            Drain();

            // ---- SendChatMessage -> NotifyBigWorldNotReadMessage (activating twice pushes once).
            Run("""[{"ActionType":19006,"Params":{"$type":"XConfigLevelActionParamsSendChatMessage","ShortMessageId":201405}}]""");
            pushes = Drain();
            AssertEqual("NotifyBigWorldNotReadMessage", Labels(pushes), "short message push");
            AssertEqual(201405, MessagePackSerializer.Deserialize<NotifyBigWorldNotReadMessage>(pushes[0].Content).MessageId, "short message id");
            Run("""[{"ActionType":19006,"Params":{"$type":"XConfigLevelActionParamsSendChatMessage","ShortMessageId":201405}}]""");
            AssertEqual("", Labels(Drain()), "already active message is not re-pushed");
            Run("""[{"ActionType":19006,"Params":{"$type":"XConfigLevelActionParamsSendChatMessage","ShortMessageId":1001}}]""");
            AssertEqual(1001, MessagePackSerializer.Deserialize<NotifyBigWorldNotReadMessage>(Drain().Single().Content).MessageId, "second short message id");
        }
    }
}
