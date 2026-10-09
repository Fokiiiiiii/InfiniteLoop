using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers.BigWorld;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.Test
{
    internal partial class Program
    {
        // WorldState slice of the level-action handlers: pose/teleport, system functions, AI flag, play timer, clear place,
        // silent photo, NPC follow / guide / tip icon. Level 6001 (F.O.S. 1) hosts the quest 3025 clear-place actors.
        private static void ValidateBigWorldActionsWorldState()
        {
            const long playerId = 99_941;
            const int levelId = 6001, questId = 3025, otherQuestId = 2002;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            foreach (int id in new[] { questId, otherQuestId })
                player.BigWorldState.QuestData.ActiveQuests[id] = new Theatre5DlcQuest { QuestId = id, DynamicData = new Theatre5DlcQuestDynamicData { QuestState = 2 } };
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
                CreateDrawCompatibilityInventory(playerId, []), "big-world-actions-world-state");
            BigWorldCoreClient client = new(harness);
            Session session = harness.Session;
            player.BigWorldState.LastWorldId = 400;
            AssertEqual(0, client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest()).Code, "enter");
            AssertEqual(0, client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = levelId }).Code, "enter level 6001");
            AssertEqual((levelId, 0), (player.BigWorldState.LastLevelId, player.BigWorldState.InstLevelId), "open-world level 6001");

            // ---- helpers
            List<(string Envelope, object?[] Rpc)> Drain()
            {
                List<(string, object?[])> pushes = [];
                for (int idle = 0; idle < 3; idle++)
                {
                    while (harness.TryReadAvailablePacket("world-state drain", out Packet packet))
                    {
                        idle = 0;
                        if (packet.Type != Packet.ContentType.Push) continue;
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        pushes.Add((push.Name, MessagePackSerializer.Deserialize<object?[]>(push.Content)));
                    }
                    Thread.Sleep(25);
                }
                return pushes;
            }
            Drain();
            void Run(int type, string paramsJson, int quest = questId)
            {
                JArray actions = new(new JObject { ["ActionType"] = type, ["Params"] = JObject.Parse(paramsJson) });
                lock (Session.GetPlayerOperationLock(playerId))
                    BigWorldLevelActions.Run(session, actions, new QuestActionContext(quest, quest * 10000 + 1, 2), () => { });
            }
            // Pushes of one action minus the ClientExecute of ServerThenClient types.
            List<(string Envelope, object?[] Rpc)> Pushes(int type, string paramsJson, int quest = questId)
            {
                Drain();
                Run(type, paramsJson, quest);
                return Drain().Where(p => (string)p.Rpc[0]! != "RpcLevelActionClientExecute").ToList();
            }
            object?[] Args((string Envelope, object?[] Rpc) push) => MessagePackSerializer.Deserialize<object?[]>((byte[])push.Rpc[1]!);
            double[] Floats(object? value) => ((object?[])value!).Select(Convert.ToDouble).ToArray();
            void AssertNear(double[] expected, double[] actual, string name)
            {
                AssertEqual(expected.Length, actual.Length, name + " length");
                for (int i = 0; i < expected.Length; i++)
                    if (Math.Abs(expected[i] - actual[i]) > 1e-4) throw new Exception($"{name}[{i}]: expected {expected[i]}, got {actual[i]}");
            }
            Theatre5DlcQuestDynamicData Dyn(int quest = questId) => player.BigWorldState.QuestData.ActiveQuests[quest].DynamicData!;

            // ---- 6001 FixSetPlayerPosition: pose RPC to XLevelProcess for the active player NPC + persisted open-world pose.
            int playerNpcUuid = ((BigWorldModule.ManagerSequenceCount + player.BigWorldState.CurNpcPos + 1) << 4) | 15;
            var fix = Pushes(6001, """{"Position":[10,2,3],"Rotation":[0,90,0]}""");
            AssertEqual(1, fix.Count, "6001 pushes");
            AssertEqual(("XRpcActorAction", "RpcLevelSetNpcPoseRequest", 1L, (long)levelId, 1L), (fix[0].Envelope, (string)fix[0].Rpc[0]!, BwInt(fix[0].Rpc[2]), BwInt(fix[0].Rpc[4]), BwInt(fix[0].Rpc[5])), "pose RPC envelope: target 1, level, XLevelProcess");
            object?[] pose = Args(fix[0]);
            AssertEqual(((long)playerNpcUuid, 3L, false), (BwInt(pose[0]), BwInt(pose[3]), (bool)pose[4]!), "pose RPC npc / PosAndRot / no state reset");
            AssertNear([10, 2, 3], Floats(pose[1]), "pose position");
            AssertNear([0, Math.Sin(Math.PI / 4), 0, Math.Cos(Math.PI / 4)], Floats(pose[2]), "pose rotation yaw 90");
            AssertEqual((10D, 2D, 3D, 90D), (player.BigWorldState.LastPosition!.X, player.BigWorldState.LastPosition.Y, player.BigWorldState.LastPosition.Z, player.BigWorldState.LastRotationY!.Value), "persisted pose A");
            var fixB = Pushes(6001, """{"Position":[4,5,6],"Rotation":null}""");
            AssertEqual(1L, BwInt(Args(fixB[0])[3]), "no rotation -> Position only");
            AssertEqual((4D, 5D, 6D, 90D), (player.BigWorldState.LastPosition!.X, player.BigWorldState.LastPosition.Y, player.BigWorldState.LastPosition.Z, player.BigWorldState.LastRotationY!.Value), "persisted pose B keeps the yaw");

            // ---- 20000 TeleportPlayer server half: state only (the client half moves the avatar).
            AssertEqual(0, Pushes(20000, """{"Position":[9.5,0.75,21.5],"Rotation":[0,-91.25,0],"WithBlackScreen":false,"ScreenEffectId":0,"BlackScreenEnterDuration":0.0,"BlackScreenExitDuration":0.0,"ShowEffect":false,"ResetCamera":true}""").Count, "teleport server half pushes nothing");
            AssertEqual((9.5D, 268.75D), (player.BigWorldState.LastPosition!.X, player.BigWorldState.LastRotationY!.Value), "teleport A persisted");
            Pushes(20000, """{"Position":[10.75,1.25,13.5],"Rotation":[0,83.5,0],"WithBlackScreen":false,"ScreenEffectId":0,"BlackScreenEnterDuration":0.0,"BlackScreenExitDuration":0.0,"ShowEffect":false,"ResetCamera":false}""");
            AssertEqual((10.75D, 83.5D), (player.BigWorldState.LastPosition!.X, player.BigWorldState.LastRotationY!.Value), "teleport B persisted");

            // ---- 6003 FixSetNpcPosition: live level NPCs only.
            var livePlaces = TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.level.sceneconfig.LevelNpcTable>()
                .Select(row => row.PlaceId).Distinct()
                .Where(place => BigWorldActors.TryGetUuid(session, levelId, BigWorldActors.NpcType, place, out _)).Take(2).ToList();
            AssertEqual(2, livePlaces.Count, "level 6001 has live level NPCs");
            for (int i = 0; i < 2; i++)
            {
                BigWorldActors.TryGetUuid(session, levelId, BigWorldActors.NpcType, livePlaces[i], out int npcUuid);
                var npcPose = Pushes(6003, $$"""{"Position":[{{i + 1}},20,30],"Rotation":{{(i == 0 ? "null" : "[0,180,0]")}},"PlaceId":{{livePlaces[i]}}}""");
                AssertEqual(1, npcPose.Count, "6003 pushes");
                object?[] npcArgs = Args(npcPose[0]);
                AssertEqual(((long)npcUuid, i == 0 ? 1L : 3L, 1L), (BwInt(npcArgs[0]), BwInt(npcArgs[3]), BwInt(npcPose[0].Rpc[5])), "6003 targets the NPC uuid on XLevelProcess with the matching pose type");
                AssertNear([i + 1, 20, 30], Floats(npcArgs[1]), "6003 position");
            }
            AssertEqual(0, Pushes(6003, """{"Position":[1,2,3],"Rotation":null,"PlaceId":987654321}""").Count, "6003 on an unloaded NPC pushes nothing");

            // ---- 16003 PauseLevelPlayTimer: Pause / Resume notify on the replicated play timer (sequence 3).
            foreach ((bool pause, string name) in new[] { (true, "RpcLevelPlayTimerPauseNotify"), (false, "RpcLevelPlayTimerResumeNotify") })
            {
                var timer = Pushes(16003, $$"""{"IsPause":{{(pause ? "true" : "false")}}}""");
                AssertEqual(("XRpcActorAction", name, (long)BigWorldModule.ServerUuid(3)), (timer[0].Envelope, (string)timer[0].Rpc[0]!, BwInt(timer[0].Rpc[5])), $"timer {name}");
            }

            // ---- 19007 SetSystemFuncEntryEnableBatch: counted per quest + RpcSetSystemFuncEntryEnableBatchRequest.
            var batchA = Pushes(19007, """{"EnableList":[],"DisableList":[5]}""");
            AssertEqual(("XRpcCommon", "RpcSetSystemFuncEntryEnableBatchRequest", 1L), (batchA[0].Envelope, (string)batchA[0].Rpc[0]!, BwInt(batchA[0].Rpc[2])), "batch envelope");
            object?[] batchArgs = Args(batchA[0]);
            AssertEqual(("", "5"), (string.Join(",", ((object?[])batchArgs[0]!).Select(BwInt)), string.Join(",", ((object?[])batchArgs[1]!).Select(BwInt))), "batch A args [Enable, Disable]");
            Pushes(19007, """{"EnableList":[],"DisableList":[5,9]}""");
            AssertEqual("5:2,9:1", string.Join(",", Dyn().FuncEntryDisableDict.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}")), "disabled twice / once");
            Pushes(19007, """{"EnableList":[5,9],"DisableList":[]}""");
            AssertEqual("5:1", string.Join(",", Dyn().FuncEntryDisableDict.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}")), "enable decrements, removes at zero");
            AssertEqual(0, Dyn(otherQuestId).FuncEntryDisableDict.Count, "another quest's dict untouched");

            // ---- 3003 ControlSystemFunction*: SystemFuncControlSaveData + RpcControlSystemFunctionRequest {type: union}.
            var controlMap = Pushes(3003, """{"$type":"XConfigLevelActionParamsControlSystemFunctionMap","IsLimit":true,"SystemFunctionType":3}""");
            MessagePackReader reader = new((byte[])controlMap[0].Rpc[1]!);
            AssertEqual((1, 1, 3), (reader.ReadArrayHeader(), reader.ReadMapHeader(), reader.ReadInt32()), "control Data dictionary header");
            AssertEqual("XConfigLevelActionParamsControlSystemFunctionMap", reader.ReadString()!, "control union type name");
            Dictionary<string, object?> fields = [];
            for (int n = reader.ReadMapHeader(); n > 0; n--)
                fields[reader.ReadString()!] = reader.NextMessagePackType == MessagePackType.Boolean ? reader.ReadBoolean() : reader.ReadInt32();
            AssertEqual((true, 3), ((bool)fields["IsLimit"]!, (int)fields["SystemFunctionType"]!), "control map fields");
            Pushes(3003, """{"$type":"XConfigLevelActionParamsControlSystemFunctionTask","LimitType":1,"SystemFunctionType":2}""");
            Pushes(3003, """{"$type":"XConfigLevelActionParamsControlSystemFunctionMap","IsLimit":false,"SystemFunctionType":3}""");
            AssertEqual("2:1:False,3:0:False", string.Join(",", Dyn().SystemFuncControlSaveData.OrderBy(s => s.SystemFunctionType).Select(s => $"{s.SystemFunctionType}:{s.LimitType}:{s.IsLimit}")), "control saves replace per function type");

            // ---- 19013 SetActorAIEnabled -> RepActor.IsAI of the NPC replicate; 19000 tip icon -> RepNpc.TipIconQuestId.
            int place = livePlaces[0];
            var row = BigWorldModule.LevelNpcOf(levelId, place)!;
            object?[] Replicate() => MessagePackSerializer.Deserialize<object?[]>(BigWorldModule.BuildLevelNpcReplicate(player, levelId, row, 0x1234F, 1, 0));
            long TipIcon() => BwInt(MessagePackSerializer.Deserialize<object?[]>((byte[])Replicate()[1]!)[18]);
            AssertEqual((true, 0L), ((bool)Replicate()[3]!, TipIcon()), "default: AI on, no tip icon");
            Run(19013, $$"""{"PlaceID":{{place}},"ActorType":1,"Enabled":false}""");
            AssertEqual(false, (bool)Replicate()[3]!, "AI disabled in the replicate");
            Run(19013, $$"""{"PlaceID":{{place}},"ActorType":1,"Enabled":true}""");
            AssertEqual(true, (bool)Replicate()[3]!, "AI re-enabled");
            Run(19000, $$"""{"PlaceId":{{place}},"Active":true}""");
            AssertEqual((long)questId, TipIcon(), "tip icon = launching quest");
            Run(19000, $$"""{"PlaceId":{{place}},"Active":false}""");
            AssertEqual(0L, TipIcon(), "tip icon off");
            Run(19000, $$"""{"PlaceId":{{place}},"Active":true}""", otherQuestId);
            AssertEqual((long)otherQuestId, TipIcon(), "tip icon = the other launching quest");

            // ---- 14001/14003/14004/14008/14010: quest-native XNpcSaveData + tether extras.
            Theatre5NpcSaveData Save(int placeId) => Dyn().LevelActorSaves[levelId].NpcSaveDatas[placeId];
            Run(14001, """{"NpcPlaceId":700004,"IsFollowPlayer":true,"FollowTargetNpcPlaceId":90,"TargetAngle":90.0,"TargetRadius":1.0,"ChaseRadius":3.0,"NormalFollowRadius":1.0,"MaxIdleLagDistance":4.0,"IdleLookAtTargetDelayTime":4.0,"UseNavMesh":true,"StartFollowDelayTime":0.0,"EnableForceTeleport":true,"TeleportRange":10.0}""");
            Run(14001, """{"NpcPlaceId":900002,"IsFollowPlayer":true,"FollowTargetNpcPlaceId":0,"TargetAngle":45.0,"TargetRadius":1.0,"ChaseRadius":2.0,"NormalFollowRadius":0.5,"MaxIdleLagDistance":1.5,"IdleLookAtTargetDelayTime":1.0,"UseNavMesh":false,"StartFollowDelayTime":0.0,"EnableForceTeleport":false,"TeleportRange":0.0}""");
            AssertEqual((90, 3F, 10F, true), (Save(700004).RelativeFollowModeSaveData!.FollowTargetNpcPlaceId, Save(700004).RelativeFollowModeSaveData!.ChaseRadius, Save(700004).RelativeFollowModeSaveData!.ForceTeleportRange, Save(700004).RelativeFollowModeSaveData!.UseNavMesh!.Value), "relative follow NPC A");
            AssertEqual((0, 45F, false), (Save(900002).RelativeFollowModeSaveData!.FollowTargetNpcPlaceId, Save(900002).RelativeFollowModeSaveData!.TargetAngle, Save(900002).RelativeFollowModeSaveData!.UseNavMesh!.Value), "relative follow NPC B");
            // The saved follow mode reaches the client in the NPC's XNpcMoveComponent (XRepNpcFollowMoveController), so a relog /
            // re-enter replicates a following NPC (live: Lucia stood still after relogging). 3800004/3800006: level NPCs of 6001.
            object?[]? Follow(int placeId)
            {
                object?[] replicate = MessagePackSerializer.Deserialize<object?[]>(BigWorldModule.BuildLevelNpcReplicate(player, levelId, BigWorldModule.LevelNpcOf(levelId, placeId)!, 0x7F, 1, 0));
                object?[] move = MessagePackSerializer.Deserialize<object?[]>((byte[])replicate[7]!).Cast<object?[]>().Single(c => (string)c[1]! == "XNpcMoveComponent");
                return (object?[]?)MessagePackSerializer.Deserialize<object?[]>((byte[])move[0]!)[2];
            }
            Run(14001, """{"NpcPlaceId":3800004,"IsFollowPlayer":true,"FollowTargetNpcPlaceId":90,"TargetAngle":90.0,"TargetRadius":1.0,"ChaseRadius":3.0,"NormalFollowRadius":1.0,"MaxIdleLagDistance":4.0,"IdleLookAtTargetDelayTime":4.0,"UseNavMesh":true,"StartFollowDelayTime":0.0,"EnableForceTeleport":true,"TeleportRange":10.0}""");
            Run(14001, """{"NpcPlaceId":3800006,"IsFollowPlayer":true,"FollowTargetNpcPlaceId":0,"TargetAngle":45.0,"TargetRadius":1.0,"ChaseRadius":2.0,"NormalFollowRadius":0.5,"MaxIdleLagDistance":1.5,"IdleLookAtTargetDelayTime":1.0,"UseNavMesh":false,"StartFollowDelayTime":0.0,"EnableForceTeleport":false,"TeleportRange":0.0}""");
            object?[] followA = Follow(3800004)!, followB = Follow(3800006)!;
            object?[] paramsA = MessagePackSerializer.Deserialize<object?[]>((byte[])followA[1]!), paramsB = MessagePackSerializer.Deserialize<object?[]>((byte[])followB[1]!);
            AssertEqual((2L, true, 10D, 10, 90L, 3D), (BwInt(followA[0]), (bool)followA[2]!, Convert.ToDouble(followA[3]), paramsA.Length, BwInt(paramsA[1]), Convert.ToDouble(paramsA[4])), "NPC A replicates its relative follow");
            AssertEqual((2L, false, 45D, false), (BwInt(followB[0]), (bool)followB[2]!, Convert.ToDouble(paramsB[2]), (bool)paramsB[7]!), "NPC B replicates its own follow");
            Run(14003, """{"NpcPlaceId":3800004}""");
            AssertEqual((true, false), (Follow(3800004) is null, Follow(3800006) is null), "stopped NPC replicates without follow; the other keeps it");
            // The follow outlives the quest that started it (live: 1001 finished with Lucia 900014 following; she stood still).
            var finishedQuest = player.BigWorldState.QuestData.ActiveQuests[questId];
            player.BigWorldState.QuestData.ActiveQuests.Remove(questId);
            AssertEqual((true, 2L), (Follow(3800004) is null, BwInt(Follow(3800006)![0])), "follow kept after the quest finished");
            player.BigWorldState.QuestData.ActiveQuests[questId] = finishedQuest;
            Run(14004, """{"WaitComplete":false,"NpcPlaceId":600011,"TargetPosition":[652.5,193.75,1290.25],"UseNavMesh":true,"OutOfRouteRange":11.0,"StartGuideRange":10.0,"ReachTargetPositionRange":3.0,"WaitDramaCaptionName":null,"WaitDramaCaptionPlayProbability":0.0,"IdleTurningDelayTime":0.5,"GuideMoveTypeMatchesTarget":false,"GuideMoveType":1}""");
            AssertEqual((652.5F, 11F, 1), (Save(600011).GuideMoveSaveData!.TargetPosition!.X, Save(600011).GuideMoveSaveData!.OutOfRouteRange, Save(600011).GuideMoveSaveData!.GuideMoveType), "guide move saved");
            Run(14008, """{"IsPlayerBecomeFollower":false,"NpcPlaceId":700004,"IsFollowPlayer":true,"FollowTargetNpcPlaceId":0,"LeadExpRotRadius":1.5,"ExpReachTime":0.2,"TargetPosOffset":[-0.5,0.0,-0.25],"NpcAnimAlignRadius":1.2,"ChaseRadius":1.0,"EnableForceTeleport":true,"TeleportRange":3.0,"HandType":2,"CloneTrialNpcId":0}""");
            AssertEqual((true, 2), (Save(700004).RelativeFollowModeSaveData is null, Save(700004).TetherFollowModeSaveData!.HandType), "tether replaces the native follow mode");
            Run(14010, """{"NpcPlaceId":700004,"IsPause":true}""");
            AssertEqual(true, player.BigWorldState.NpcFollowExtras.Single(x => x.PlaceId == 700004).Paused, "follow paused");
            Run(14010, """{"NpcPlaceId":700004,"IsPause":false}""");
            AssertEqual(false, player.BigWorldState.NpcFollowExtras.Single(x => x.PlaceId == 700004).Paused, "follow resumed");
            // Tether replicates as ENpcFollowMode 4 + XRepNpcTetherFollowMode with the configured params (dump48 D:818193), kept after the quest ends, PausedCount follows 14010.
            Run(14008, """{"IsPlayerBecomeFollower":false,"NpcPlaceId":3800004,"IsFollowPlayer":true,"FollowTargetNpcPlaceId":0,"LeadExpRotRadius":1.5,"ExpReachTime":0.2,"TargetPosOffset":[-0.5,0.0,-0.25],"NpcAnimAlignRadius":1.25,"ChaseRadius":1.0,"EnableForceTeleport":true,"TeleportRange":3.0,"HandType":2,"CloneTrialNpcId":0}""");
            Run(14008, """{"IsPlayerBecomeFollower":true,"NpcPlaceId":3800006,"IsFollowPlayer":false,"FollowTargetNpcPlaceId":7,"LeadExpRotRadius":2.5,"ExpReachTime":0.4,"TargetPosOffset":[1.0,0.0,0.5],"NpcAnimAlignRadius":2.0,"ChaseRadius":3.0,"EnableForceTeleport":false,"TeleportRange":0.0,"HandType":1,"CloneTrialNpcId":0}""");
            object?[] tetherA = Follow(3800004)!, tetherB = Follow(3800006)!;
            object?[] tpA = MessagePackSerializer.Deserialize<object?[]>((byte[])tetherA[1]!), tpB = MessagePackSerializer.Deserialize<object?[]>((byte[])tetherB[1]!);
            AssertEqual((4L, true, 3D, 0L, false, true, 1.5D, 1.25D, 2L), (BwInt(tetherA[0]), (bool)tetherA[2]!, Convert.ToDouble(tetherA[3]), BwInt(tetherA[4]), (bool)tpA[0]!, (bool)tpA[1]!, Convert.ToDouble(tpA[3]), Convert.ToDouble(tpA[6]), BwInt(tpA[8])), "NPC A replicates its tether follow");
            AssertEqual((4L, false, true, 7L, 2.5D, 3D, 1L, -0.5D), (BwInt(tetherB[0]), (bool)tetherB[2]!, (bool)tpB[0]!, BwInt(tpB[2]), Convert.ToDouble(tpB[3]), Convert.ToDouble(tpB[7]), BwInt(tpB[8]), Convert.ToDouble(((object?[])tpA[5]!)[0])), "NPC B replicates its own tether follow");
            Run(14010, """{"NpcPlaceId":3800004,"IsPause":true}""");
            AssertEqual((1L, 0L), (BwInt(Follow(3800004)![4]), BwInt(Follow(3800006)![4])), "pause replicates as PausedCount for the paused NPC only");
            var tetherQuest = player.BigWorldState.QuestData.ActiveQuests[questId];
            player.BigWorldState.QuestData.ActiveQuests.Remove(questId);
            AssertEqual(4L, BwInt(Follow(3800004)![0]), "tether kept after the quest finished");
            player.BigWorldState.QuestData.ActiveQuests[questId] = tetherQuest;
            Run(14003, """{"NpcPlaceId":3800004}""");
            AssertEqual((true, false), (Follow(3800004) is null, Follow(3800006) is null), "stop clears the tether of that NPC only");
            Run(14003, """{"NpcPlaceId":700004}""");
            AssertEqual((true, 0, true), (Save(700004).RelativeFollowModeSaveData is null, player.BigWorldState.NpcFollowExtras.Count(x => x.PlaceId == 700004), Save(900002).RelativeFollowModeSaveData is not null), "stop follow clears NPC A only");
            // 14004 NpcGuideMoveTo also replicates (XRepMoveComponent key 3 = XRepNpcGuideMoveController): the live client only runs the action
            // once, so a relog mid-objective (Selena, quest 7001 "Go to the event venue") must restart the walk from the snapshot.
            object?[]? Guide(int placeId) => (object?[]?)MessagePackSerializer.Deserialize<object?[]>((byte[])MessagePackSerializer.Deserialize<object?[]>((byte[])
                MessagePackSerializer.Deserialize<object?[]>(BigWorldModule.BuildLevelNpcReplicate(player, levelId, BigWorldModule.LevelNpcOf(levelId, placeId)!, 0x7F, 1, 0))[7]!)
                .Cast<object?[]>().Single(c => (string)c[1]! == "XNpcMoveComponent")[0]!)[3];
            AssertEqual((true, true), (Guide(3800004) is null, Guide(3800006) is null), "no guide before 14004");
            Run(14004, """{"WaitComplete":false,"NpcPlaceId":3800004,"TargetPosition":[543.5,192.25,1002.0],"UseNavMesh":true,"OutOfRouteRange":5.0,"StartGuideRange":4.0,"ReachTargetPositionRange":4.0,"WaitDramaCaptionName":null,"WaitDramaCaptionPlayProbability":0.0,"IdleTurningDelayTime":4.0,"GuideMoveTypeMatchesTarget":false,"GuideMoveType":0}""");
            Run(14004, """{"WaitComplete":true,"NpcPlaceId":3800006,"TargetPosition":[-12.5,3.0,77.75],"UseNavMesh":false,"OutOfRouteRange":11.0,"StartGuideRange":10.0,"ReachTargetPositionRange":3.0,"WaitDramaCaptionName":"Wait01","WaitDramaCaptionPlayProbability":0.5,"IdleTurningDelayTime":0.5,"GuideMoveTypeMatchesTarget":true,"GuideMoveType":1}""");
            object?[] guideA = Guide(3800004)!, guideB = Guide(3800006)!;
            AssertEqual((10, 543.5D, 1002D, true, 5D, 4D, true, 0L), (guideA.Length, Convert.ToDouble(((object?[])guideA[0]!)[0]), Convert.ToDouble(((object?[])guideA[0]!)[2]), (bool)guideA[1]!, Convert.ToDouble(guideA[2]),
                Convert.ToDouble(guideA[4]), guideA[5] is null, BwInt(guideA[9])), "NPC A replicates its guide move");
            AssertEqual((-12.5D, false, 10D, "Wait01", 0.5D, true, 1L), (Convert.ToDouble(((object?[])guideB[0]!)[0]), (bool)guideB[1]!, Convert.ToDouble(guideB[3]), (string)guideB[5]!, Convert.ToDouble(guideB[6]),
                (bool)guideB[8]!, BwInt(guideB[9])), "NPC B replicates its own guide move");
            var guideQuest = player.BigWorldState.QuestData.ActiveQuests[questId];
            player.BigWorldState.QuestData.ActiveQuests.Remove(questId);
            AssertEqual(true, Guide(3800004) is null, "guide leaves with its quest");
            player.BigWorldState.QuestData.ActiveQuests[questId] = guideQuest;

            // ---- 20002 TakePhotoSilent: request ledger.
            Run(20002, """{"PhotoKey":1,"SpotId":1500001,"Fov":50.0}""");
            Run(20002, """{"PhotoKey":2,"SpotId":1500002,"Fov":35.5}""");
            AssertEqual("1:1500001:50,2:1500002:35.5", string.Join(",", player.BigWorldState.SilentPhotoRequests.Select(r => $"{r.PhotoKey}:{r.SpotId}:{r.Fov}")), "silent photo requests");

            // ---- 3004 ClearPlace / 18004 RestorePlace (quest 3025 ClearPlaceArgs SpecificActors).
            const string clearA = "清场-3025", clearB = "清场-3025(3)";
            BigWorldActors.TryGetUuid(session, levelId, BigWorldActors.NpcType, 3200013, out int clearedUuid);
            AssertEqual(true, clearedUuid != 0, "NPC 3200013 is live before the clear");
            var cleared = Pushes(3004, $$"""{"ClearId":"{{clearA}}"}""");
            AssertEqual(("XRpcCommon", "XRpcRemoveActor", (long)clearedUuid), (cleared.Single().Envelope, (string)cleared.Single().Rpc[0]!, BwInt(Args(cleared.Single())[1])), "clear unloads the live actor");
            AssertEqual(false, BigWorldActors.TryGetUuid(session, levelId, BigWorldActors.NpcType, 3200013, out _), "cleared actor is gone");
            AssertEqual(false, BigWorldActors.LoadedOverride(player.BigWorldState, levelId, BigWorldActors.NpcType, 3200013), "clear persisted as actor override");
            AssertEqual(0, Pushes(3004, $$"""{"ClearId":"{{clearA}}"}""").Count, "second clear of the same id is idempotent");
            Run(3004, $$"""{"ClearId":"{{clearB}}"}""");
            AssertEqual(9, player.BigWorldState.ExecutedClearPlaces.Single(r => r.ClearId == clearB).Actors.Count, "second clear resolves its 8 NPCs + 1 scene object");
            var restored = Pushes(18004, $$"""{"ClearId":"{{clearA}}"}""");
            AssertEqual("XRpcActorReplicate,RpcActorChangeController", string.Join(",", restored.Select(p => p.Envelope == "XRpcActorReplicate" ? "XRpcActorReplicate" : (string)p.Rpc[0]!)), "restore replicates the actor again");
            AssertEqual(true, BigWorldActors.TryGetUuid(session, levelId, BigWorldActors.NpcType, 3200013, out _), "restored actor is live");
            AssertEqual(1, player.BigWorldState.ExecutedClearPlaces.Count, "restore removes only its record");
            Console.WriteLine("BigWorld actions world-state validation passed.");
        }
    }
}
