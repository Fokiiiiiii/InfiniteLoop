using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.statussyncfight.quest;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Server-owned objective types that only completed by client report (or never): OnLevelTimeOut (10), DeliverItems (14),
    // KillEnemy (16). KillEnemyGroup (15) and ScanPlusSearchComplete (18) are client-detected, see OnClientComplete.
    internal static partial class BigWorldQuestRuntime
    {
        // CodeText ids (share/text/CodeText.tsv).
        private const int CodeObjectiveNotFound = 25100004, CodeNotEnoughItem = 25100027, CodeNotItemInBag = 25100028,
            CodeNotDeliverItemQuest = 25100037, CodeDeliverTypeNotMatch = 25100038, CodeDeliverCountNotMatch = 25100039, CodeObjectiveStateWrong = 25100045;

        private static double D(object? value) => value is null ? 0 : Convert.ToDouble(value);

        // Objectives with a LevelId only run while the player is in that level (0 = any).
        private static bool InObjectiveLevel(Session session, DlcQuestObjectiveTable cfg) =>
            I(cfg.LevelId) == 0 || I(cfg.LevelId) == BigWorldModule.CurrentLevelId(session.player.BigWorldState);

        #region OnLevelTimeOut (10): the level's XLevelPlayTimer

        // Wire [DUMP dump.cs D:743050ff, D:743777ff]: the timer is the replicated actor XLevelPlayTimer (EActorType 8), created by the
        // level snapshot (uuid = server sequence 3, BuildRepLevel). The server drives it with actor actions
        // RpcLevelPlayTimerStartNotify [LimitTime, IsCountDown, ImminentEndTimeS] / Pause / Resume / Stop ([] each); the client's
        // XLevelPlayTimer.HandleStart begins its HUD countdown. No capture contains a timer objective: the push order is [INF].
        private const int PlayTimerSequence = 3;

        // Replaceable clock (tests); the expiry callback uses BigWorldLevelActions.Schedule.
        internal static Func<DateTime> Now = () => DateTime.UtcNow;

        private sealed class PlayTimer
        {
            public int QuestId, ObjectiveId, LevelId, Token;
            public float Limit;
            public double Elapsed; // seconds accumulated before Since
            public DateTime? Since; // running
            public bool ActionPaused;
            public Theatre5DlcQuestStepObjective Objective = null!;
        }

        private static double ElapsedOf(PlayTimer t) => t.Elapsed + (t.Since is { } since ? Math.Max(0, (Now() - since).TotalSeconds) : 0);

        private static void TimerPush(Session session, PlayTimer t, string rpc, params object?[] args) =>
            session.SendPush("XRpcActorAction", BigWorldXRpc.ActorAction(rpc, BigWorldXRpc.Args(args), t.LevelId, BigWorldModule.ServerUuid(PlayTimerSequence)));

        private static void PersistTimer(Session session, PlayTimer t)
        {
            t.Objective.TimerElapsedMs = (int)Math.Round(ElapsedOf(t) * 1000);
            session.player.Save();
        }

        // Starts the level's timer for the first InProgress OnLevelTimeOut objective of the current level that should run: AutoStart
        // ones, and ones started before (persisted, or started by PauseLevelPlayTimer(false)). Needs the XRpc channel and a client
        // that finished loading the level (the timer actor exists there from the snapshot).
        // AscNet policy: one XLevelPlayTimer per level, so a second timer objective waits until the first closes.
        private static void SyncTimer(Session session)
        {
            Rt rt = Of(session);
            if (rt.Silent || rt.Timer is not null || !rt.LevelLoaded || session.BigWorldWorldId == 0)
                return;
            foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in InProgress(session))
            {
                if (TypeOf(cfg) != TypeOnLevelTimeOut || !InObjectiveLevel(session, cfg) || !(I(cfg.AutoStart) == 1 || objective.TimerStarted))
                    continue;
                objective.TimerStarted = true;
                Rt scope = Of(session);
                scope.Changes.Touched.Add(quest.QuestId);
                PlayTimer timer = new()
                {
                    QuestId = quest.QuestId, ObjectiveId = cfg.Id, LevelId = BigWorldModule.CurrentLevelId(session.player.BigWorldState),
                    Limit = (float)D(cfg.Time), Elapsed = objective.TimerElapsedMs / 1000D, Objective = objective
                };
                rt.Timer = timer;
                // A restored timer counts down its remaining time (RepLevelPlayTimer has no elapsed field).
                TimerPush(session, timer, "RpcLevelPlayTimerStartNotify", (float)Math.Max(0, timer.Limit - timer.Elapsed), I(cfg.IsCountDown) == 1, (float)D(cfg.ImminentEndTime));
                ReconcileTimer(session, timer);
                return;
            }
        }

        // Runs while neither the client's fight pause count nor PauseLevelPlayTimer holds it.
        private static void ReconcileTimer(Session session, PlayTimer t)
        {
            bool run = !Of(session).FightPaused && !t.ActionPaused;
            if (run && t.Since is null)
            {
                t.Since = Now();
                int token = ++t.Token;
                Rt rt = Of(session);
                BigWorldLevelActions.Schedule(TimeSpan.FromSeconds(Math.Max(0, t.Limit - t.Elapsed)), () => OnTimerElapsed(session, t, token));
            }
            else if (!run && t.Since is not null)
            {
                t.Elapsed = ElapsedOf(t);
                t.Since = null;
                t.Token++;
                PersistTimer(session, t);
            }
        }

        private static void OnTimerElapsed(Session session, PlayTimer t, int token)
        {
            lock (Session.GetPlayerOperationLock(session.player.PlayerData.Id))
            {
                if (Of(session).Timer != t || t.Token != token || t.Since is null)
                    return;
                if (ElapsedOf(t) < t.Limit - 0.001)
                {
                    // Woke early: wait out the remainder.
                    t.Elapsed = ElapsedOf(t);
                    t.Since = null;
                    ReconcileTimer(session, t);
                    return;
                }
                Scoped(session, false, () =>
                {
                    foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, _) in InProgress(session))
                        if (cfg.Id == t.ObjectiveId)
                        {
                            Close(session, quest, cfg);
                            return;
                        }
                    Of(session).Timer = null;
                });
                Tick(session);
            }
        }

        // The objective left InProgress (finished by the timer, the client's report or the step): the timer goes, its record with it.
        private static void StopTimer(Session session, DlcQuestObjectiveTable cfg)
        {
            if (TypeOf(cfg) != TypeOnLevelTimeOut)
                return;
            Rt rt = Of(session);
            if (rt.Timer is { } t && t.ObjectiveId == cfg.Id)
            {
                rt.Timer = null;
                t.Token++;
                if (rt.LevelLoaded && !rt.Silent)
                    TimerPush(session, t, "RpcLevelPlayTimerStopNotify");
            }
            if (Data(session).ActiveQuests.GetValueOrDefault(cfg.QuestId)?.DynamicData?.Steps.GetValueOrDefault(cfg.StepId)?.Objectives.GetValueOrDefault(cfg.Id) is { } objective)
            {
                objective.TimerStarted = false;
                objective.TimerElapsedMs = 0;
            }
        }

        // The player left the timer's level (instance leave, level switch, world leave): the callback is cancelled and the elapsed time
        // persisted; the timer resumes with the remaining time when the level is loaded again (SyncTimer). No Stop push: the timer
        // actor goes with its level.
        private static void SuspendTimer(Session session)
        {
            Rt rt = Of(session);
            if (rt.Timer is not { } t)
                return;
            t.Elapsed = ElapsedOf(t);
            t.Since = null;
            t.Token++;
            rt.Timer = null;
            PersistTimer(session, t);
        }

        // RpcPauseFight/RpcResumeFight: the client's pause count > 0 holds the timer (BigWorldLevelActions.OnPauseCount).
        internal static void OnFightPause(Session session, bool paused)
        {
            Rt rt = Of(session);
            rt.FightPaused = paused;
            if (rt.Timer is { } t)
                ReconcileTimer(session, t);
        }

        // Level action PauseLevelPlayTimer [IsPause] (its client push is sent by the action handler).
        // AscNet policy [INF]: no ELevelActionType starts a level play timer (XFightScriptProxy.StartLevelPlayTimer is Lua-only), so a
        // resume for an InProgress objective of this level whose timer never started (AutoStart 0) starts it.
        internal static void OnPauseTimerAction(Session session, bool isPause) =>
            Scoped(session, false, () =>
            {
                if (Of(session).Timer is { } t)
                {
                    t.ActionPaused = isPause;
                    ReconcileTimer(session, t);
                    return;
                }
                if (isPause)
                    return;
                foreach ((_, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in InProgress(session))
                    if (TypeOf(cfg) == TypeOnLevelTimeOut && InObjectiveLevel(session, cfg) && !objective.TimerStarted)
                    {
                        objective.TimerStarted = true;
                        break;
                    }
                SyncTimer(session);
            });

        #endregion

        #region DeliverItems (14)

        // RpcQuestDeliverItemsRequest [QuestObjectiveId, DeliverItemDict{itemId: count}] (dump.cs D:767632; XQuestManager
        // .HandleQuestDeliverItemsRequest). The client opens UiBigWorldPopupDelivery and sends the dictionary its player confirmed.
        // The server detects completion: the delivered dictionary must equal RequiredItemInfoDict (both ItemsDeliverType Normal 1 and
        // Manual 2), the quest item bag must hold it; it is consumed and the objective closes. DeliverItems has no Rep*Progress
        // (D:766407), so no ProgressNotify exists. A rejected request answers RpcQuestDeliverItemsFailedNotify [XCode] (D:767654,
        // XQuestManager.HandleQuestDeliverItemsFailedNotify); the XCodes are the DlcQuestDeliverItem* / DlcQuestNotEnoughItem ones [INF pairing].
        internal static void OnDeliverItems(Session session, int objectiveId, IReadOnlyDictionary<int, int> delivered) =>
            Scoped(session, false, () =>
            {
                if (!BigWorldQuestModule.Objectives.Value.TryGetValue(objectiveId, out DlcQuestObjectiveTable? cfg))
                {
                    Push(session, "RpcQuestDeliverItemsFailedNotify", CodeObjectiveNotFound);
                    return;
                }
                if (TypeOf(cfg) != TypeDeliverItems)
                {
                    Push(session, "RpcQuestDeliverItemsFailedNotify", CodeNotDeliverItemQuest);
                    return;
                }
                var open = InProgress(session).FirstOrDefault(o => o.Cfg.Id == objectiveId);
                if (open.Quest is null)
                {
                    Push(session, "RpcQuestDeliverItemsFailedNotify", CodeObjectiveStateWrong);
                    return;
                }
                Dictionary<int, int> required = RequiredItems(cfg);
                Dictionary<int, int> bag = session.player.BigWorldState.DlcQuestBag;
                int? failure = delivered.Any(kv => !required.ContainsKey(kv.Key)) ? CodeDeliverTypeNotMatch
                    : required.Any(kv => delivered.GetValueOrDefault(kv.Key) != kv.Value) ? CodeDeliverCountNotMatch
                    : required.Any(kv => !bag.ContainsKey(kv.Key) || bag[kv.Key] <= 0) ? CodeNotItemInBag
                    : required.Any(kv => bag[kv.Key] < kv.Value) ? CodeNotEnoughItem
                    : null;
                if (failure is { } code)
                {
                    Push(session, "RpcQuestDeliverItemsFailedNotify", code);
                    return;
                }
                NotifyDlcQuestItemUpdate update = new();
                foreach ((int itemId, int count) in required)
                {
                    bag[itemId] -= count;
                    update.DlcQuestItemChangeDict[itemId] = new DlcQuestItem { ItemId = itemId, Count = bag[itemId] };
                }
                Of(session).Changes.Touched.Add(open.Quest.QuestId);
                session.SendPush(update);
                Close(session, open.Quest, cfg);
            });

        #endregion

        #region KillEnemy (16)

        // XRpcNpcDieRequest [NpcId, KillerId, MagicId] (D:760330, actor = the NPC), XRpcNpcDie / XRpcNpcDeath [KillerId, MagicId]
        // (D:760354/760376, actor = the NPC) are the client's kill reports [INF: which one the 4.8 client sends is uncaptured, all three
        // are accepted]. A dead level NPC whose place id is in ExistsEnemyList counts once; progress is the ProgressNotify
        // {KilledEnemies: [placeIds]} blob (BigWorldQuestModule.Progress); the objective closes when every listed NPC is dead.
        internal static void HandleNpcDeath(Session session, string rpcName, byte[] args, int actorUuid)
        {
            if (rpcName is not ("XRpcNpcDieRequest" or "XRpcNpcDie" or "XRpcNpcDeath"))
                return;
            int uuid = rpcName == "XRpcNpcDieRequest" && BigWorldQuestModule.Decode(args) is object?[] { Length: >= 1 } request && request[0] is not null
                ? BigWorldXRpc.ToInt(request[0]) : actorUuid;
            int levelId = BigWorldModule.CurrentLevelId(session.player.BigWorldState);
            if (BigWorldActors.TryGetGroupMember(session, levelId, uuid, out int groupObjective, out int memberIndex))
            {
                OnGroupMemberDeath(session, uuid, groupObjective, memberIndex);
                return;
            }
            if (!BigWorldActors.TryGetPlaceId(session, levelId, BigWorldActors.NpcType, uuid, out int placeId))
                return;
            Scoped(session, false, () =>
            {
                foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in InProgress(session))
                {
                    if (TypeOf(cfg) != TypeKillEnemy || I(cfg.LevelId) != levelId || !(cfg.ExistsEnemyList ?? []).Any(v => I(v) == placeId)
                        || objective.KilledEnemies.Contains(placeId))
                        continue;
                    objective.KilledEnemies.Add(placeId);
                    Of(session).Changes.Touched.Add(quest.QuestId);
                    PushProgress(session, quest, cfg, objective);
                    if (IsSatisfied(session, quest, cfg))
                        Close(session, quest, cfg);
                }
            });
        }

        #endregion

        #region KillEnemyGroup (15)

        // [DUMP48] XQuestObjectiveKillEnemyGroup only has PostInit/CleanUp (no spawn, no DeserializeProgress override, no completion
        // request): the group logic is server-only code, so the server spawns the group and counts its deaths. Count members of
        // LevelNpcBase NpcBaseId stand within GroupRadius of GroupCenterPos (AscNet policy [INF]: retail's placement is uncaptured; member
        // i sits on a golden-angle spiral, uniform over the disc) facing InitialFacePoint (InitialOrientationType 1 ToCenterPoint) or
        // the master player (2 ToMasterPlayer) [DUMP48 EEnemyInitialOrientationType]. Members are replicated like LoadNpc (BuildEnemyReplicate)
        // and simulated by the player's client; KilledEnemies holds the dead members' 1-based spawn indices (a relog respawns the
        // rest), and the objective closes at Count deaths. There is no client progress for the type, so no ProgressNotify is sent.
        private const int FaceCenterPoint = 1, FaceMasterPlayer = 2;
        // AscNet policy [INF]: a slot whose spot lies within this many metres of a scene box (rock, cliff, wall) is rejected. Live 4033 wave 2
        // (40330301): slot 10 sat at the foot of the boundary rock (scenecollider 40 of Scene_Main), under the hill the player's shells hit
        // at y 8.8 instead of the plateau's 3.28; it was never shot and the objective stayed at 9/10. The spiral can cross hills, the
        // plateau lies around the centre, so a rejected slot retries at 7/8, 6/8 ... of its radius, each time at a new golden-angle turn.
        private const double GroupSolidMargin = 3;

        private static ((double X, double Y, double Z) Position, double Yaw) GroupMember(Session session, DlcQuestObjectiveTable cfg, int index)
        {
            double[] center = (cfg.GroupCenterPos ?? []).Select(v => Convert.ToDouble(v)).ToArray();
            int levelId = I(cfg.LevelId), count = Math.Max(1, I(cfg.Count));
            double golden = Math.PI * (3 - Math.Sqrt(5)), full = D(cfg.GroupRadius) * Math.Sqrt((index - 0.5) / count);
            (double X, double Y, double Z) at = default;
            for (int attempt = 0; attempt <= 8; attempt++)
            {
                double angle = (index + attempt * count) * golden, radius = full * (8 - attempt) / 8;
                at = (center[0] + radius * Math.Sin(angle), center[1], center[2] + radius * Math.Cos(angle));
                if (!BigWorldModule.InSceneSolid(levelId, at.X, at.Y + 1, at.Z, GroupSolidMargin))
                    break;
            }
            double[]? face = I(cfg.InitialOrientationType) switch
            {
                FaceCenterPoint => (cfg.InitialFacePoint ?? []).Select(v => Convert.ToDouble(v)).ToArray(),
                FaceMasterPlayer => BigWorldModule.CurrentPose(session.player.BigWorldState) is { Position: { } p } ? [p.X, p.Y, p.Z] : null,
                _ => null,
            };
            return (at, face is null ? 0 : Math.Atan2(face[0] - at.X, face[2] - at.Z) * 180 / Math.PI);
        }

        // Spawns the alive members of every open KillEnemyGroup of the player's level once the client has loaded it (objective opened
        // in the loaded level, or resumed by a relog / level switch).
        private static void SyncEnemyGroups(Session session)
        {
            Rt rt = Of(session);
            if (rt.Silent || !rt.LevelLoaded || session.BigWorldWorldId == 0)
                return;
            int levelId = BigWorldModule.CurrentLevelId(session.player.BigWorldState);
            foreach ((_, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in InProgress(session))
            {
                if (TypeOf(cfg) != TypeKillEnemyGroup || I(cfg.LevelId) != levelId)
                    continue;
                for (int index = 1; index <= I(cfg.Count); index++)
                {
                    if (objective.KilledEnemies.Contains(index))
                        continue;
                    ((double X, double Y, double Z) position, double yaw) = GroupMember(session, cfg, index);
                    BigWorldActors.SpawnGroupNpc(session, levelId, cfg.Id, index, (uuid, sequence) =>
                        BigWorldModule.BuildEnemyReplicate(levelId, I(cfg.NpcBaseId), position, yaw, uuid, sequence));
                }
            }
        }

        private static void OnGroupMemberDeath(Session session, int uuid, int objectiveId, int index) =>
            Scoped(session, false, () =>
            {
                BigWorldActors.ForgetGroupMember(session, uuid);
                foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in InProgress(session))
                {
                    if (cfg.Id != objectiveId || TypeOf(cfg) != TypeKillEnemyGroup || objective.KilledEnemies.Contains(index))
                        continue;
                    objective.KilledEnemies.Add(index);
                    Of(session).Changes.Touched.Add(quest.QuestId);
                    if (IsSatisfied(session, quest, cfg))
                        Close(session, quest, cfg);
                }
            });

        #endregion
    }
}
