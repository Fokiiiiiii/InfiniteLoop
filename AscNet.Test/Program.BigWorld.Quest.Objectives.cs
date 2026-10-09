using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers.BigWorld;
using AscNet.Table.V2.share.statussyncfight.quest;
using MessagePack;
using MongoDB.Bson.Serialization;

namespace AscNet.Test
{
    internal partial class Program
    {
        // Server-owned objective types: OnLevelTimeOut (10) timer, DeliverItems (14), KillEnemyGroup (15), KillEnemy (16),
        // ScanPlusSearchComplete (18). Timers are driven through the replaceable scheduler and clock (no real sleeps).
        private static void ValidateBigWorldQuestObjectives()
        {
            Type runtime = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BigWorld.BigWorldQuestRuntime");
            Type actions = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BigWorld.BigWorldLevelActions");
            Type actors = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BigWorld.BigWorldActors");
            const BindingFlags any = BindingFlags.Static | BindingFlags.NonPublic;
            object? Rt(string name, params object[] args)
            {
                try { return runtime.GetMethod(name, any)!.Invoke(null, args); }
                catch (TargetInvocationException exception) { throw exception.InnerException!; }
            }
            DateTime clock = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            List<(TimeSpan Delay, Action Callback)> scheduled = [];
            FieldInfo schedule = actions.GetField("Schedule", any)!, now = runtime.GetField("Now", any)!;
            object oldSchedule = schedule.GetValue(null)!, oldNow = now.GetValue(null)!;
            schedule.SetValue(null, (Action<TimeSpan, Action>)((delay, callback) => scheduled.Add((delay, callback))));
            now.SetValue(null, (Func<DateTime>)(() => clock));
            try
            {
                const long playerId = 99_814;
                using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var playerSaves, out _, out _);
                Player player = CreateDrawCompatibilityPlayer(playerId);
                BigWorldPlayerState S = player.BigWorldState;
                using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, CreateDrawCompatibilityInventory(playerId, []), "big-world-quest-objectives");
                harness.Session.BigWorldWorldId = 400;
                int packetId = 0;
                List<(string Rpc, object?[] Args, int Level)> rpcs = [];
                void Drain(LoopbackSessionHarness h, List<(string, object?[], int)> into)
                {
                    while (h.TryReadAvailablePacket("drain", out Packet p))
                        if (p.Type == Packet.ContentType.Push && MessagePackSerializer.Deserialize<Packet.Push>(p.Content) is { Name: "XRpcCommon" or "XRpcActorAction" } push)
                        {
                            object[] envelope = MessagePackSerializer.Deserialize<object[]>(push.Content);
                            into.Add(((string)envelope[0], DecodeRpcArgs((byte[])envelope[1]), Convert.ToInt32(envelope[4])));
                        }
                }
                // Pushes that are not XRpc (NotifyDlcQuestItemUpdate) are counted separately.
                List<string> plain = [];
                List<byte[]> executes = []; // raw RpcLevelActionClientExecute args, for the client's Finish echo
                List<byte[]> replicates = []; // XRpcActorReplicate pushes
                void Take(Packet p)
                {
                    if (p.Type != Packet.ContentType.Push)
                        return;
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(p.Content);
                    if (push.Name is "XRpcCommon" or "XRpcActorAction")
                    {
                        object[] envelope = MessagePackSerializer.Deserialize<object[]>(push.Content);
                        rpcs.Add(((string)envelope[0], DecodeRpcArgs((byte[])envelope[1]), Convert.ToInt32(envelope[4])));
                        if ((string)envelope[0] == "RpcLevelActionClientExecute") executes.Add((byte[])envelope[1]);
                    }
                    else
                    {
                        plain.Add(push.Name);
                        if (push.Name == "XRpcActorReplicate") replicates.Add(push.Content);
                    }
                }
                void DrainAll()
                {
                    while (harness.TryReadAvailablePacket("drain", out Packet p))
                        Take(p);
                }
                void Request(string name, object request)
                {
                    InvokeRegisteredRequestHandler(name, harness.Session, ++packetId, request);
                    for (Packet p = harness.ReadPacket(name); ; p = harness.ReadPacket(name))
                    {
                        Take(p);
                        if (p.Type != Packet.ContentType.Push) break;
                    }
                    Thread.Sleep(20);
                    DrainAll();
                    Rt("Tick", harness.Session);
                    Thread.Sleep(20);
                    DrainAll();
                }
                // Push-only reads need the loopback to flush.
                void Settle() { Thread.Sleep(40); DrainAll(); }
                Player Reload() => BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson!);
                byte[] Args(params object[] values) => MessagePackSerializer.Serialize(values);
                Theatre5DlcQuestStepObjective Obj(int questId, int stepId, int id) => S.QuestData.ActiveQuests[questId].DynamicData!.Steps[stepId].Objectives[id];
                int Count(string rpc, Func<object?[], bool>? where = null) => rpcs.Count(r => r.Rpc == rpc && (where?.Invoke(r.Args) ?? true));
                // Puts the objective InProgress (earlier objectives of its step finished, later ones unopened).
                Theatre5DlcQuestStepObjective Install(int questId, int objectiveId)
                {
                    DlcQuestObjectiveTable cfg = BigWorldObjectiveTable(objectiveId);
                    if (!S.QuestData.ActiveQuests.TryGetValue(questId, out Theatre5DlcQuest? quest))
                        S.QuestData.ActiveQuests[questId] = quest = new Theatre5DlcQuest { QuestId = questId, DynamicData = new() { QuestState = 2 }, StaticData = new() };
                    Theatre5DlcQuestStep step = quest.DynamicData!.Steps[cfg.StepId] = new Theatre5DlcQuestStep { StepId = cfg.StepId, StepState = 1 };
                    foreach (DlcQuestObjectiveTable o in TableReaderV2.Parse<DlcQuestObjectiveTable>().Where(o => o.StepId == cfg.StepId).OrderBy(o => Convert.ToInt32(o.Order)).ThenBy(o => o.Id))
                    {
                        int state = Convert.ToInt32(o.Order) < Convert.ToInt32(cfg.Order) ? 6 : o.Id == objectiveId ? 3 : 0;
                        step.Objectives[o.Id] = new Theatre5DlcQuestStepObjective { Id = o.Id, ObjectiveState = state };
                        if (state == 6) quest.DynamicData.FinishedObjectiveIds.Add(o.Id);
                    }
                    return step.Objectives[objectiveId];
                }

                // ---- OnLevelTimeOut (10): 5001/50010302, level 4008, AutoStart, IsCountDown, Time 90, ImminentEndTime 10 ----
                S.LastLevelId = 4008;
                Rt("OnPlayerEnterLevelComplete", harness.Session); // level loaded, nothing open yet
                S.QuestData.ActiveQuests[5001] = new Theatre5DlcQuest { QuestId = 5001, DynamicData = new() { QuestState = 2 }, StaticData = new() };
                Rt("Scoped", harness.Session, false, (Action)(() => Rt("OpenStep", harness.Session, S.QuestData.ActiveQuests[5001], 500103, false)));
                Settle();
                Theatre5DlcQuestStepObjective timer = Obj(5001, 500103, 50010302);
                AssertEqual(3, timer.ObjectiveState, "timer objective InProgress");
                var start = rpcs.Single(r => r.Rpc == "RpcLevelPlayTimerStartNotify");
                AssertEqual("90,True,10,4008", $"{start.Args[0]:0},{start.Args[1]},{start.Args[2]:0},{start.Level}", "AutoStart pushes the timer start on objective InProgress");
                AssertEqual((true, 1, 90D), (timer.TimerStarted, scheduled.Count, scheduled[^1].Delay.TotalSeconds), "expiry scheduled at the config Time");
                Action first = scheduled[^1].Callback;

                // Pause holds it: 30s run, RpcPauseFight count 1, callbacks of the old schedule do nothing.
                clock += TimeSpan.FromSeconds(30);
                actions.GetMethod("OnPauseCount", any)!.Invoke(null, [harness.Session, 1]);
                AssertEqual(30_000, timer.TimerElapsedMs, "elapsed persisted at pause");
                clock += TimeSpan.FromSeconds(500);
                first();
                AssertEqual(3, timer.ObjectiveState, "paused timer does not expire");
                // Resume: the remaining 60s are scheduled again; PauseLevelPlayTimer(true) also holds it.
                int before = scheduled.Count;
                actions.GetMethod("OnPauseCount", any)!.Invoke(null, [harness.Session, 0]);
                AssertEqual((before + 1, 60D), (scheduled.Count, scheduled[^1].Delay.TotalSeconds), "resume schedules the remaining time");
                clock += TimeSpan.FromSeconds(10);
                Rt("OnPauseTimerAction", harness.Session, true);
                AssertEqual(40_000, timer.TimerElapsedMs, "action pause persists elapsed");
                clock += TimeSpan.FromSeconds(500);
                scheduled[^1].Callback();
                AssertEqual(3, timer.ObjectiveState, "action-paused timer does not expire");
                Rt("OnPauseTimerAction", harness.Session, false);
                AssertEqual(50D, scheduled[^1].Delay.TotalSeconds, "action resume schedules the remaining time");
                clock += TimeSpan.FromSeconds(20);

                // Cancel on leave: elapsed is persisted (60s), the pending callback is dead.
                Action pending = scheduled[^1].Callback;
                Rt("OnPlayerLeaveLevel", harness.Session);
                AssertEqual(60_000, timer.TimerElapsedMs, "leave persists elapsed");
                clock += TimeSpan.FromSeconds(500);
                pending();
                AssertEqual(3, timer.ObjectiveState, "callback after leave is cancelled");

                // Relog restore: remaining 30s counted down, then expiry completes the objective exactly once.
                Player relogged = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson!);
                AssertEqual((true, 60_000), (relogged.BigWorldState.QuestData.ActiveQuests[5001].DynamicData!.Steps[500103].Objectives[50010302].TimerStarted,
                    relogged.BigWorldState.QuestData.ActiveQuests[5001].DynamicData!.Steps[500103].Objectives[50010302].TimerElapsedMs), "timer record persisted");
                using LoopbackSessionHarness relog = new(CreateDrawCompatibilityCharacter(playerId), relogged, CreateDrawCompatibilityInventory(playerId, []), "big-world-quest-objectives-relog");
                relog.Session.BigWorldWorldId = 400;
                Rt("OnPlayerEnterLevelComplete", relog.Session);
                List<(string, object?[], int)> relogRpcs = [];
                Thread.Sleep(40);
                Drain(relog, relogRpcs);
                var restored = relogRpcs.Single(r => r.Item1 == "RpcLevelPlayTimerStartNotify");
                AssertEqual("30,True", $"{restored.Item2[0]:0},{restored.Item2[1]}", "restored timer counts down the remaining time");
                AssertEqual(30D, scheduled[^1].Delay.TotalSeconds, "restored expiry scheduled");
                Theatre5DlcQuestStepObjective restoredObjective = relogged.BigWorldState.QuestData.ActiveQuests[5001].DynamicData!.Steps[500103].Objectives[50010302];
                clock += TimeSpan.FromSeconds(30);
                scheduled[^1].Callback();
                Thread.Sleep(40);
                Drain(relog, relogRpcs);
                AssertEqual(true, restoredObjective.ObjectiveState >= 4, "expiry completes the objective");
                AssertEqual((1, false, 0), (relogRpcs.Count(r => r.Item1 == "RpcLevelPlayTimerStopNotify"), restoredObjective.TimerStarted, restoredObjective.TimerElapsedMs), "timer stopped and record cleared");
                int closes = relogRpcs.Count(r => r.Item1 == "RpcQuestObjectiveUpdate" && Convert.ToInt32(r.Item2[1]) == 50010302 && Convert.ToInt32(r.Item2[2]) == 4);
                scheduled[^1].Callback();
                Rt("OnClientComplete", relog.Session, 5001, 50010302);
                Thread.Sleep(40);
                Drain(relog, relogRpcs);
                AssertEqual((1, 1), (closes, relogRpcs.Count(r => r.Item1 == "RpcQuestObjectiveUpdate" && Convert.ToInt32(r.Item2[1]) == 50010302 && Convert.ToInt32(r.Item2[2]) == 4)),
                    "expiry then the client's late report completes once");
                harness.Session.BigWorldWorldId = 400;
                rpcs.Clear();
                plain.Clear();
                Rt("OnPlayerLeaveLevel", harness.Session);

                // ---- DeliverItems (14): 2013/20130106 needs quest item 28000036 x1 ----
                S.QuestData.ActiveQuests.Remove(5001);
                Install(2013, 20130106);
                void Deliver(params (int Item, int Count)[] items) => Request("XRpcCommon", new object[]
                    { "RpcQuestDeliverItemsRequest", Args(20130106, items.ToDictionary(i => i.Item, i => i.Count)), 15, 1, 5001 });
                int Failed() => rpcs.Where(r => r.Rpc == "RpcQuestDeliverItemsFailedNotify").Select(r => Convert.ToInt32(r.Args[0])).LastOrDefault();
                Deliver((28000035, 1));
                AssertEqual((25100038, 3), (Failed(), Obj(2013, 201301, 20130106).ObjectiveState), "wrong item type rejected");
                Deliver((28000036, 2));
                AssertEqual((25100039, 3), (Failed(), Obj(2013, 201301, 20130106).ObjectiveState), "wrong count rejected");
                Deliver((28000036, 1));
                AssertEqual((25100028, 3), (Failed(), Obj(2013, 201301, 20130106).ObjectiveState), "item missing from the bag rejected");
                S.DlcQuestBag[28000036] = 1;
                rpcs.Clear();
                Deliver((28000036, 1));
                AssertEqual((0, true, 0), (Count("RpcQuestDeliverItemsFailedNotify"), Obj(2013, 201301, 20130106).ObjectiveState >= 4, S.DlcQuestBag[28000036]), "valid delivery consumes the item and closes");
                AssertEqual(true, plain.Contains("NotifyDlcQuestItemUpdate"), "bag update pushed");
                AssertEqual(0, Reload().BigWorldState.DlcQuestBag[28000036], "consumption persisted");
                Deliver((28000036, 1));
                AssertEqual(25100045, Failed(), "delivery to a closed objective rejected");

                // ---- DlcQuestObjective.ItemAction 1 grants ItemIds x ItemCounts as the objective completes ----
                // Frostheart Shadow 4032: drama 40320305 hands out 28000035 ("Malkuth" ID Code); deliver objective 40320315 consumes it.
                // Quest 1001's drama 10010113 hands out 28000002 (a second, distinct item and quest).
                S.QuestData.ActiveQuests.Remove(5001);
                foreach ((int questId, int objectiveId, string drama, int itemId) in new[] { (4032, 40320305, "Drama_1002_006", 28000035), (1001, 10010113, "Drama_1001_002", 28000002) })
                {
                    Install(questId, objectiveId);
                    S.DlcQuestBag.Remove(itemId);
                    rpcs.Clear();
                    plain.Clear();
                    Rt("OnDramaFinish", harness.Session, drama);
                    Settle();
                    AssertEqual((1, true, true), (S.DlcQuestBag.GetValueOrDefault(itemId), plain.Contains("NotifyDlcQuestItemUpdate"), plain.Contains("NotifyDlcQuestItemObtainDisplay")), $"{objectiveId} grants item {itemId} with both notifies");
                    AssertEqual(1, Reload().BigWorldState.DlcQuestBag[itemId], $"{objectiveId} grant persisted");
                    Rt("OnDramaFinish", harness.Session, drama);
                    AssertEqual(1, S.DlcQuestBag[itemId], $"{objectiveId} granted once");
                }
                Install(4032, 40320315);
                S.DlcQuestBag.Remove(28000035); // a save made before ItemAction was honoured: 40320305 finished, bag empty
                plain.Clear();
                Rt("OnPlayerEnterLevelComplete", harness.Session);
                Settle();
                AssertEqual((1, true), (S.DlcQuestBag.GetValueOrDefault(28000035), plain.Contains("NotifyDlcQuestItemUpdate")), "open deliver objective of a stuck save heals from the finished granting objective");
                Rt("OnPlayerEnterLevelComplete", harness.Session);
                AssertEqual(1, S.DlcQuestBag[28000035], "healing does not stack");
                rpcs.Clear();
                Request("XRpcCommon", new object[] { "RpcQuestDeliverItemsRequest", Args(40320315, new Dictionary<int, int> { [28000035] = 1 }), 15, 1, 4032 });
                AssertEqual((0, true, 0), (Count("RpcQuestDeliverItemsFailedNotify"), Obj(4032, 403203, 40320315).ObjectiveState >= 4, S.DlcQuestBag[28000035]), "Frostheart Shadow delivery consumes the granted item");

                // ---- no IsEndStep row (4012, 4034-4036): a save holding every step finished ends the quest when the level loads ----
                Install(4034, 40340101);
                S.QuestData.ActiveQuests[4034].DynamicData!.Steps[403401].StepState = 2;
                Rt("Scoped", harness.Session, false, (Action)(() => Rt("Reevaluate", harness.Session)));
                Settle();
                AssertEqual((false, true), (S.QuestData.ActiveQuests.ContainsKey(4034), S.QuestData.FinishedQuests.Contains(4034)), "quest without an end step finishes once all its steps are finished (Reevaluate heals saves)");

                // ---- KillEnemy (16): 2018/20180101 in level 6002, ExistsEnemyList 1,2,8,9 (level NPC place ids) ----
                S.LastLevelId = 6002;
                Install(2018, 20180101);
                actors.GetMethod("OnLevelSnapshot", any)!.Invoke(null, [harness.Session, 6002, 0, new List<(int, int)>(),
                    new List<(int, int)> { (1, 101), (2, 102), (3, 103), (8, 108), (9, 109) }]);
                void Die(int uuid, string rpc = "XRpcNpcDie") => Request("XRpcActorAction", new object[] { rpc, rpc == "XRpcNpcDieRequest" ? Args(uuid, 0, 0) : Args(0, 0), 15, 1, 6002, rpc == "XRpcNpcDieRequest" ? 0 : uuid });
                int Kills() => rpcs.Count(r => r.Rpc == "RpcQuestObjectiveProgressNotify" && Convert.ToInt32(r.Args[1]) == 20180101);
                string Killed() => string.Join(",", MessagePackSerializer.Deserialize<Dictionary<string, int[]>>((byte[])rpcs.Last(r => r.Rpc == "RpcQuestObjectiveProgressNotify" && Convert.ToInt32(r.Args[1]) == 20180101).Args[2])["KilledEnemies"]);
                rpcs.Clear();
                Die(103);
                Die(999);
                AssertEqual(0, Kills(), "unlisted NPC and unknown actor do not count");
                Die(101);
                Die(101);
                AssertEqual(("1", 1), (Killed(), Kills()), "kill counted once with the KilledEnemies blob");
                Die(102, "XRpcNpcDieRequest");
                AssertEqual("1,2", Killed(), "XRpcNpcDieRequest carries NpcId in its arguments");
                Die(108);
                AssertEqual(3, Obj(2018, 201801, 20180101).ObjectiveState, "still open with a listed NPC alive");
                Die(109);
                AssertEqual(("1,2,8,9", true), (string.Join(",", Obj(2018, 201801, 20180101).KilledEnemies.Order()), Obj(2018, 201801, 20180101).ObjectiveState >= 4), "last kill closes the objective");
                AssertEqual("1,2,8,9", string.Join(",", Reload().BigWorldState.QuestData.ActiveQuests[2018].DynamicData!.Steps[201801].Objectives[20180101].KilledEnemies.Order()), "kills persisted");

                // ---- ScanPlusSearchComplete (18) 3025/30250401 (level 6001): client-detected, checked against the objective's level ----
                foreach ((int questId, int objectiveId, int stepId, int levelId, string what) in new[] { (3025, 30250401, 302504, 6001, "ScanPlusSearch") })
                {
                    Install(questId, objectiveId);
                    S.LastLevelId = 4001;
                    Request("XRpcCommon", new object[] { "RpcQuestObjectiveCompleteRequest", MessagePackSerializer.Serialize(new Dictionary<string, int> { ["QuestId"] = questId, ["ObjectiveId"] = objectiveId }), 15, 1, 4001 });
                    AssertEqual(3, Obj(questId, stepId, objectiveId).ObjectiveState, $"{what} report from another level rejected");
                    Request("XRpcCommon", new object[] { "RpcQuestObjectiveCompleteRequest", MessagePackSerializer.Serialize(new Dictionary<string, int> { ["QuestId"] = questId + 1, ["ObjectiveId"] = objectiveId }), 15, 1, levelId });
                    S.LastLevelId = levelId;
                    AssertEqual(3, Obj(questId, stepId, objectiveId).ObjectiveState, $"{what} report with a foreign quest id rejected");
                    Request("XRpcCommon", new object[] { "RpcQuestObjectiveCompleteRequest", MessagePackSerializer.Serialize(new Dictionary<string, int> { ["QuestId"] = questId, ["ObjectiveId"] = objectiveId }), 15, 1, levelId });
                    AssertEqual(true, Obj(questId, stepId, objectiveId).ObjectiveState >= 4, $"{what} report in the objective's level completes it");
                }

                // ---- KillEnemyGroup (15): the 4.8 client class has no spawn or completion code, so the server spawns Count hostile members of
                // LevelNpcBase NpcBaseId around GroupCenterPos and counts their deaths. 4033's waves: 40330201 (Fusion Turret base) and 40330301
                // (Chubby Coo Coo base): different bases, templates, centers and radii.
                Dictionary<int, int> fightNpcOfBase = TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.npc.LevelNpcBaseTable>().ToDictionary(r => r.Id, r => r.FightNpcId);
                List<(int Template, int Base, string[] Components, double X, double Z, bool Ai, long Controller, long Camp)> Members()
                {
                    List<(int, int, string[], double, double, bool, long, long)> members = [];
                    foreach (byte[] bytes in replicates)
                    {
                        object?[] replicate = MessagePackSerializer.Deserialize<object?[]>(bytes), rep = MessagePackSerializer.Deserialize<object?[]>((byte[])replicate[1]!);
                        object?[] components = MessagePackSerializer.Deserialize<object?[]>((byte[])replicate[7]!);
                        object?[] move = MessagePackSerializer.Deserialize<object?[]>((byte[])components.Cast<object?[]>().Single(c => (string)c[1]! == "XNpcMoveComponent")[0]!);
                        object?[] position = (object?[])move[0]!;
                        members.Add(((int)BwInt(rep[1]), (int)BwInt(rep[2]), components.Cast<object?[]>().Select(c => (string)c[1]!).ToArray(), Convert.ToDouble(position[0]), Convert.ToDouble(position[2]),
                            (bool)replicate[3]!, BwInt(replicate[2]), BwInt(rep[4])));
                    }
                    return members;
                }
                void Kill(int uuid) => Request("XRpcActorAction", new object[] { "XRpcNpcDie", Args(0, 0), 15, 1, 4033, uuid });
                List<(int Template, int Base)> waves = [];
                foreach (int objectiveId in new[] { 40330201, 40330301 })
                {
                    DlcQuestObjectiveTable wave = BigWorldObjectiveTable(objectiveId);
                    int count = Convert.ToInt32(wave.Count), baseId = Convert.ToInt32(wave.NpcBaseId);
                    double[] center = wave.GroupCenterPos!.Select(v => Convert.ToDouble(v)).ToArray();
                    S.LastLevelId = 4033;
                    S.QuestData.ActiveQuests.Remove(4033);
                    Theatre5DlcQuestStepObjective open = Install(4033, objectiveId);
                    void Enter()
                    {
                        actors.GetMethod("OnLevelSnapshot", any)!.Invoke(null, [harness.Session, 4033, 8, new List<(int, int)>(), new List<(int, int)>()]);
                        rpcs.Clear();
                        replicates.Clear();
                        Rt("OnPlayerEnterLevelComplete", harness.Session);
                        Settle();
                    }
                    Enter();
                    var spawned = Members();
                    AssertEqual(count, spawned.Count, $"{objectiveId} spawns Count members");
                    AssertEqual(count, rpcs.Count(r => r.Rpc == "RpcActorChangeController"), $"{objectiveId} hands every member to the player's client");
                    AssertEqual((fightNpcOfBase[baseId], baseId, true, 15L, 2L), (spawned[0].Template, spawned[0].Base, spawned[0].Ai, spawned[0].Controller, spawned[0].Camp), $"{objectiveId} members: template from the base, hostile camp, AI, server controller");
                    AssertEqual(true, spawned.All(m => m.Template == spawned[0].Template && m.Base == baseId), $"{objectiveId} members share the wave's base");
                    AssertEqual(true, spawned.All(m => Math.Sqrt(Math.Pow(m.X - center[0], 2) + Math.Pow(m.Z - center[2], 2)) <= Convert.ToDouble(wave.GroupRadius) + 0.01), $"{objectiveId} members stand inside GroupRadius");
                    AssertEqual(count, spawned.Select(m => (m.X, m.Z)).Distinct().Count(), $"{objectiveId} members stand apart");
                    // Live 4033 wave 2 stalled at 9/10: its slot 10 stood at the foot of a boundary rock and could not be shot.
                    AssertEqual(0, spawned.Count(m => BigWorldModule.InSceneSolid(4033, m.X, center[1] + 1, m.Z, 2)), $"{objectiveId} no member stands in or beside a scene box");
                    string[] layout = spawned[0].Components;
                    AssertEqual((true, true, true, false, false), (layout.Contains("XSkillActionComponent"), layout.Contains("XHitComponent"), layout.Contains("XNpcPerformComponent"),
                        layout.Contains("XNpcInputControlComponent"), layout.Contains("XNpcSearchTargetComponent")), $"{objectiveId} members carry the fight layout of a non-player NPC");
                    waves.Add((spawned[0].Template, baseId));

                    Request("XRpcCommon", new object[] { "RpcQuestObjectiveCompleteRequest", MessagePackSerializer.Serialize(new Dictionary<string, int> { ["QuestId"] = 4033, ["ObjectiveId"] = objectiveId }), 15, 1, 4033 });
                    AssertEqual(3, open.ObjectiveState, $"{objectiveId}: the client cannot complete a server-counted group");
                    List<int> uuids = rpcs.Where(r => r.Rpc == "RpcActorChangeController").Select(r => Convert.ToInt32(r.Args[1])).ToList();
                    Kill(uuids[0]);
                    Kill(uuids[0]);
                    Kill(uuids[1]);
                    Kill(0x7777F);
                    AssertEqual((2, 3), (open.KilledEnemies.Count, open.ObjectiveState), $"{objectiveId}: deaths count once, unknown actors do not");
                    if (objectiveId == 40330201)
                    {
                        var firstSpawn = spawned;
                        Enter(); // a relog / level switch: only the living members come back
                        var resumed = Members();
                        AssertEqual(count - 2, resumed.Count, $"{objectiveId} respawns the survivors only");
                        AssertEqual(true, resumed.All(m => firstSpawn.Any(b => (b.X, b.Z) == (m.X, m.Z))) && !resumed.Any(m => (m.X, m.Z) == (firstSpawn[0].X, firstSpawn[0].Z) || (m.X, m.Z) == (firstSpawn[1].X, firstSpawn[1].Z)),
                            $"{objectiveId} survivors keep their spawn slots");
                        uuids = rpcs.Where(r => r.Rpc == "RpcActorChangeController").Select(r => Convert.ToInt32(r.Args[1])).ToList();
                    }
                    else uuids = uuids.Skip(2).ToList();
                    for (int i = 0; i < uuids.Count - 1; i++)
                        Kill(uuids[i]);
                    AssertEqual(3, open.ObjectiveState, $"{objectiveId} still open with a member alive");
                    Kill(uuids[^1]);
                    AssertEqual((count, true), (open.KilledEnemies.Count, open.ObjectiveState >= 4), $"{objectiveId} closes at the last death");
                    AssertEqual(count, Reload().BigWorldState.QuestData.ActiveQuests[4033].DynamicData!.Steps[wave.StepId].Objectives[objectiveId].KilledEnemies.Count, $"{objectiveId} deaths persisted");
                }
                AssertEqual(true, waves[0].Template != waves[1].Template && waves[0].Base != waves[1].Base, "the two waves replicate different enemy types");
                // Level-placed NPCs follow the template's NeedFightLogicType: 4033's CUB cannon (place 2, Npc 7002, Need) carries the fight
                // set (live: its absence threw in XNpcFSMComponent.IsNpcDoSkill every frame); the ecology NPC 100002 of 4001 (Npc 6040, follows
                // the world: retail replicated it without) does not.
                string[] PlacedLayout(int levelId, int placeId) => MessagePackSerializer.Deserialize<object?[]>((byte[])MessagePackSerializer.Deserialize<object?[]>(
                    BigWorldModule.BuildLevelNpcReplicate(player, levelId, BigWorldModule.LevelNpcOf(levelId, placeId)!, 0x7F, 1, 0))[7]!).Cast<object?[]>().Select(c => (string)c[1]!).ToArray();
                AssertEqual((true, true, false), (PlacedLayout(4033, 2).Contains("XSkillActionComponent"), PlacedLayout(4033, 2).Contains("XNpcPerformComponent"), PlacedLayout(4001, 100002).Contains("XSkillActionComponent")),
                    "placed fight NPC carries the fight set, placed logic-less NPC does not");

                // ---- NpcGuideMoveTo objectives (type 7 ReachTarget, IsNpcReach, Enter = [caption..., 14004]): 7001/70010105 "Go to the event venue with
                // Selena" (level 5001, NPC 1400001) and 4041/40410116 (level 4041, NPC 3400020). Live: after a relog Selena stood still beside the
                // player because only the live client run of 14004 started the walk and the NPC snapshot carried no guide controller.
                foreach ((int questId, int stepId, int objectiveId, int levelId, int npcPlace, float targetX, float targetZ, float reachRange) in new[]
                    { (7001, 700103, 70010105, 5001, 1400001, 543.55F, 1002.036F, 4F), (4041, 404101, 40410116, 4041, 3400020, 371.4168F, 162.26F, 2F) })
                {
                    S.LastLevelId = levelId;
                    Install(questId, objectiveId).ObjectiveState = 2; // ScriptEnter: the objective just opened, its Enter list is pending
                    rpcs.Clear();
                    executes.Clear();
                    Rt("OnPlayerEnterLevelComplete", harness.Session);
                    Rt("Tick", harness.Session);
                    Settle();
                    Rt("Tick", harness.Session);
                    Settle();
                    AssertEqual(true, Count("RpcLevelActionClientExecute") > 0, $"{objectiveId} opens: client run pushed");
                    // The client finishes the run (nothing in it waits), then the Enter list completes and the objective is InProgress.
                    foreach (byte[] run in executes.ToList())
                    {
                        MessagePackReader reader = new(run);
                        int list = 0, from = 0, to = 0;
                        byte[] context = [];
                        for (int i = reader.ReadMapHeader(); i > 0; i--)
                            switch (reader.ReadString())
                            {
                                case "ActionListId": list = reader.ReadInt32(); break;
                                case "StartIndex": from = reader.ReadInt32(); break;
                                case "EndIndex": to = reader.ReadInt32(); break;
                                case "LaunchContext":
                                    long begin = reader.Consumed;
                                    reader.ReadString();
                                    MessagePackSerializer.Deserialize<object>(ref reader, Packet.InboundOptions);
                                    context = run[(int)begin..(int)reader.Consumed];
                                    break;
                            }
                        System.Buffers.ArrayBufferWriter<byte> buffer = new();
                        MessagePackWriter writer = new(buffer);
                        writer.WriteMapHeader(4);
                        writer.Write("ActionListId"); writer.Write(list);
                        writer.Write("StartIndex"); writer.Write(from);
                        writer.Write("EndIndex"); writer.Write(to);
                        writer.Write("Context"); writer.WriteRaw(context);
                        writer.Flush();
                        Request("XRpcCommon", new object[] { "RpcLevelActionClientExecuteFinish", buffer.WrittenMemory.ToArray(), 15, 1, levelId });
                    }
                    executes.Clear();
                    AssertEqual(3, Obj(questId, stepId, objectiveId).ObjectiveState, $"{objectiveId} opens: InProgress after the client run finished");
                    object?[]? Guide(Player p) => (object?[]?)MessagePackSerializer.Deserialize<object?[]>((byte[])MessagePackSerializer.Deserialize<object?[]>((byte[])
                        MessagePackSerializer.Deserialize<object?[]>(BigWorldModule.BuildLevelNpcReplicate(p, levelId, BigWorldModule.LevelNpcOf(levelId, npcPlace)!, 0x7F, 1, 0))[7]!)
                        .Cast<object?[]>().Single(c => (string)c[1]! == "XNpcMoveComponent")[0]!)[3];
                    // The relogged player (BSON round trip) replicates the NPC with the guide controller of the open objective.
                    foreach ((Player who, string tag) in new[] { (player, "live"), (Reload(), "relogged") })
                    {
                        object?[] guide = Guide(who)!;
                        object?[] target = (object?[])guide[0]!;
                        AssertEqual((targetX, targetZ, true, reachRange), (Convert.ToSingle(target[0]), Convert.ToSingle(target[2]), (bool)guide[1]!, Convert.ToSingle(guide[4])), $"{objectiveId} {tag}: NPC {npcPlace} replicates its guide move");
                    }
                    Request("XRpcCommon", new object[] { "RpcQuestObjectiveCompleteRequest", MessagePackSerializer.Serialize(new Dictionary<string, int> { ["QuestId"] = questId, ["ObjectiveId"] = objectiveId }), 15, 1, levelId });
                    AssertEqual(true, Obj(questId, stepId, objectiveId).ObjectiveState >= 4, $"{objectiveId} client reach report completes it");
                }
            }
            finally
            {
                schedule.SetValue(null, oldSchedule);
                now.SetValue(null, oldNow);
            }
        }

        private static DlcQuestObjectiveTable BigWorldObjectiveTable(int id) => TableReaderV2.Parse<DlcQuestObjectiveTable>().Single(o => o.Id == id);
    }
}
