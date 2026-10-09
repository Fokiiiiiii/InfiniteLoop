using System.Buffers;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.statussyncfight.level;
using AscNet.Table.V2.share.statussyncfight.npc.trialnpc;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Team / instance-level actions: AddTrialNpcToTeam 1000, RemoveTrialNpcFromTeam 18000, RequestEnterInstLevel 18001,
    // RequestLeaveInstLevel 18002, SwitchLevel 19004, FinishInstLevel 6000, SettleInstLevel 19005 (server half),
    // CompleteLevelPlay 3001, SendChatMessage 19006.
    internal static partial class BigWorldActionHandlers
    {
        private static void RegisterTeamInstance(Dictionary<int, Action<ActionEnv>> t)
        {
            t[1000] = AddTrialNpcToTeam;
            t[18000] = RemoveTrialNpcFromTeam;
            t[18001] = e => EnterLevel(e, e.Params.Value<int>("InstLevelId"));
            t[19004] = e => EnterLevel(e, e.Params.Value<int>("NextLevelId"));
            t[18002] = RequestLeaveInstLevel;
            t[6000] = FinishInstLevel;
            t[19005] = SettleInstLevel;
            t[3001] = CompleteLevelPlay;
            // 19006 SendChatMessage [ShortMessageId]: the short message arrives in the phone (BigWorldMessage record +
            // NotifyBigWorldNotReadMessage), the same path quest AutoSend messages use. Re-activating is not an error.
            t[19006] = e => BigWorldArchiveModule.ActivateMessage(e.Session, e.Params.Value<int>("ShortMessageId"));
        }

        #region Trial team

        // 1000 AddTrialNpcToTeam [TrialNpcIds, AddMode (1 Cover / 2 Append), CurNpcPos]. Server effect: the persisted trial
        // team (BigWorldTrialTeam) + RpcAddTrialNpcToTeam [LevelId, AddMode, FightTeamNpcData] (dump.cs D:705971).
        private static void AddTrialNpcToTeam(ActionEnv e)
        {
            List<int> ids = e.Params["TrialNpcIds"]!.Values<int>().ToList();
            int mode = e.Params.Value<int>("AddMode");
            if (ids.Count == 0 || mode is not (BigWorldTrialTeam.Cover or BigWorldTrialTeam.Append))
                throw new InvalidDataException($"AddTrialNpcToTeam: {ids.Count} trial ids, AddMode {mode}.");
            BigWorldTrialTeam.Add(e.Session, e.LevelId, ids, mode, e.Params.Value<int>("CurNpcPos"), (e.Context as QuestActionContext)?.QuestId);
        }

        // 18000 RemoveTrialNpcFromTeam: the real team returns; RpcRemoveTrialNpcFromTeam [LevelId, FightTeamNpcData].
        private static void RemoveTrialNpcFromTeam(ActionEnv e) => BigWorldTrialTeam.Remove(e.Session, e.LevelId);

        #endregion

        #region Level transfer

        // 18001 RequestEnterInstLevel / 19004 SwitchLevel [level, Position, Rotation]: the same in-world transfer as
        // EnterInstLevelRequest (BigWorldModule.TransferLevel), then the client is moved with RpcPlayerSwitchLevelNotify.
        private static void EnterLevel(ActionEnv e, int levelId)
        {
            Session session = e.Session;
            // Already there (list replayed after a relog): nothing to transfer.
            if (levelId == BigWorldModule.CurrentLevelId(session.player.BigWorldState))
                return;
            int from = BigWorldModule.CurrentLevelId(session.player.BigWorldState);
            Theatre5DlcVector3? pos = Vec(e.Params["Position"]), rot = Vec(e.Params["Rotation"]);
            int code = BigWorldModule.TransferLevel(session, 0, levelId, pos, rot, out bool newLevel);
            if (code != 0)
                throw new InvalidDataException($"Level action {e.ActionType}: transfer to level {levelId} failed with code {code}.");
            BigWorldLevelSwitch.Send(session, from, levelId, pos, rot);
            BigWorldModule.AfterLevelTransfer(session, levelId, newLevel);
        }

        // 18002 RequestLeaveInstLevel [SaveOnExit, UseExitTargetLevelParams, WorldId, LevelId, TargetPos, TargetRot]
        // (client XFight.RequestLeaveInstanceLevel(resetSaveDataExit, TargetLevelParams{WorldId, LevelId})). Without exit
        // params the player returns to the open-world pose saved when the instance was entered.
        private static void RequestLeaveInstLevel(ActionEnv e)
        {
            Session session = e.Session;
            BigWorldPlayerState state = session.player.BigWorldState;
            bool useTarget = e.Params.Value<bool?>("UseExitTargetLevelParams") == true;
            int from = BigWorldModule.CurrentLevelId(state);
            // No instance (list replayed after the instance was left): nothing to leave.
            if (state.InstLevelId != 0)
            {
                // AscNet policy: SaveOnExit maps to LevelSaveOption SaveExit, otherwise NoSaveExit.
                int code = BigWorldModule.LeaveInstanceLevel(session, e.Params.Value<bool?>("SaveOnExit") == true ? 1 : 2);
                if (code != 0)
                    throw new InvalidDataException($"RequestLeaveInstLevel failed with code {code}.");
            }
            else if (!useTarget)
                return;

            Theatre5DlcVector3? pos = null, rot = null;
            bool newLevel = false;
            int target = BigWorldModule.CurrentLevelId(state);
            if (useTarget)
            {
                target = e.Params.Value<int>("LevelId");
                pos = Vec(e.Params["TargetPos"]);
                rot = Vec(e.Params["TargetRot"]);
                if (target != BigWorldModule.CurrentLevelId(state))
                {
                    int code = BigWorldModule.TransferLevel(session, e.Params.Value<int>("WorldId"), target, pos, rot, out newLevel);
                    if (code != 0)
                        throw new InvalidDataException($"RequestLeaveInstLevel: exit level {target} failed with code {code}.");
                }
                else
                {
                    state.LastPosition = new BigWorldVector3 { X = pos!.X, Y = pos.Y, Z = pos.Z };
                    state.LastRotation = null;
                    state.LastRotationY = rot?.Y;
                    session.player.Save();
                }
            }
            else
            {
                (pos, rot) = BigWorldLevelSwitch.OpenWorldPose(state);
            }
            BigWorldLevelSwitch.Send(session, from, target, pos, rot);
            BigWorldModule.AfterLevelTransfer(session, target, newLevel);
        }

        #endregion

        #region Instance result

        // 6000 FinishInstLevel [UseSwitchQuestVar, QuestVarSwitchDataList]: the instance counts as completed once per
        // visit (history in InstLevelFinishedCounts) and InstanceComplete objectives advance; a switch list first copies /
        // calculates quest vars into other quests (XQuestVarCalculateData: Left ref (target quest) = Left op Right).
        private static void FinishInstLevel(ActionEnv e)
        {
            BigWorldPlayerState state = e.Session.player.BigWorldState;
            if (e.Params.Value<bool?>("UseSwitchQuestVar") == true)
                foreach (JToken data in (JArray)e.Params["QuestVarSwitchDataList"]!)
                    SwitchQuestVar(e, (JObject)data);
            // Instance already left (list replayed) - its completion was recorded then.
            if (state.InstLevelId != 0)
                CompleteInstance(e.Session);
        }

        internal static void CompleteInstance(Session session)
        {
            BigWorldPlayerState state = session.player.BigWorldState;
            if (state.InstLevelFinished)
                return;
            int level = state.InstLevelId;
            state.InstLevelFinished = true;
            state.InstLevelFinishedCounts[level] = state.InstLevelFinishedCounts.GetValueOrDefault(level) + 1;
            session.player.Save();
            BigWorldQuestRuntime.OnInstanceComplete(session, level);
        }

        private static void SwitchQuestVar(ActionEnv e, JObject data)
        {
            int targetQuest = data.Value<int>("CalTargetQuestId");
            JObject left = (JObject)data["LeftQuestVarToken"]!, right = (JObject)data["RightQuestVarToken"]!;
            if (left["Literal"] is not null)
                throw new InvalidDataException("QuestVarSwitchData: the left token must be a var reference.");
            int type = left.Value<int>("VarType");
            string key = left.Value<string>("StrKey")!;
            double rhs = right["Literal"] is JObject literal
                ? literal["Value"] is { Type: JTokenType.Boolean } flag ? (flag.Value<bool>() ? 1 : 0) : literal.Value<double>("Value")
                : Number(BigWorldQuestRuntime.GetVar(e.Session.player, ((QuestActionContext)RequireQuest(e)).QuestId, right.Value<int>("VarType"), right.Value<string>("StrKey")!));
            double lhs = Number(BigWorldQuestRuntime.GetVar(e.Session.player, targetQuest, type, key));
            double result = data.Value<int>("MathOp") switch
            {
                1 => lhs + rhs,
                2 => lhs - rhs,
                3 => lhs * rhs,
                4 => rhs == 0 ? throw new InvalidDataException("QuestVarSwitchData division by zero.") : lhs / rhs,
                5 => rhs, // EVarMathOp.Copy
                var op => throw new InvalidDataException($"QuestVarSwitchData MathOp {op} is not supported."),
            };
            object stored = type switch { 1 => (int)result, 2 => (float)result, 3 => result != 0, _ => throw new InvalidDataException($"QuestVarSwitchData VarType {type}.") };
            BigWorldQuestRuntime.SetVar(e.Session, targetQuest, type, key, stored);
        }

        private static double Number(object? value) => value switch { null => 0, bool b => b ? 1 : 0, _ => Convert.ToDouble(value) };

        // InputVars positions of a Jumper settle (all shipped configs): Objective1..3 (int objective ids), Score (int),
        // Time (float seconds), IsWin (bool), DeathCount, GoldCount, Star (int), TriggerJudge, TriggerHideRoad (bool).
        // These are the fields of JumperLevelSettleData (dump.cs D:743909).
        private const int JumperSettle = 1;

        // 19005 SettleInstLevel, server half: build the settle from the quest's vars and persist it as the level's record
        // (XCode BigWorldInstLevelSettleDataIsNull = the game server refuses an empty record). A win completes the
        // instance when FinishInstLevel did not already. The client half opens the settle UI itself.
        private static void SettleInstLevel(ActionEnv e)
        {
            int settleType = e.Params.Value<int>("SettleType");
            if (settleType != JumperSettle)
                throw new InvalidDataException($"SettleInstLevel: SettleType {settleType} has no InputVars layout (only Jumper is configured).");
            int questId = RequireQuest(e).QuestId;
            List<string> keys = e.Params["InputVars"]!.Values<string>().ToList()!;
            if (keys.Count != 11)
                throw new InvalidDataException($"SettleInstLevel Jumper needs 11 InputVars, got {keys.Count}.");
            Player player = e.Session.player;
            int Int(int i) => (int)Number(BigWorldQuestRuntime.GetVar(player, questId, 1, keys[i]));
            bool Flag(int i) => Number(BigWorldQuestRuntime.GetVar(player, questId, 3, keys[i])) != 0;
            BigWorldPlayerState state = player.BigWorldState;
            int level = state.InstLevelId != 0 ? state.InstLevelId : e.LevelId;
            int score = Int(3);
            state.InstSettleRecords.TryGetValue(level, out BigWorldInstSettleRecord? previous);
            state.InstSettleRecords[level] = new BigWorldInstSettleRecord
            {
                SettleType = settleType,
                Theme = e.Params.Value<int>("Theme"),
                QuestId = questId,
                ObjectiveIds = [Int(0), Int(1), Int(2)],
                Score = score,
                BestScore = Math.Max(score, previous?.BestScore ?? 0),
                PlayTime = (float)Number(BigWorldQuestRuntime.GetVar(player, questId, 2, keys[4])),
                IsWin = Flag(5),
                DeathCount = Int(6),
                GoldCount = Int(7),
                StarCount = Int(8),
                IsTriggerJudge = Flag(9),
                IsTriggerHideRoad = Flag(10),
            };
            player.Save();
            if (state.InstSettleRecords[level].IsWin && state.InstLevelId != 0)
                CompleteInstance(e.Session);
        }

        // 3001 CompleteLevelPlay [IsFullyClearedVarRef]: the BigWorldLevelPlay of the current level gets its
        // PlayerData.LevelPlayDatas entry (cleared); IsFullCleared = the referenced quest bool var.
        // AscNet policy: a full clear is never downgraded by a later plain clear.
        private static void CompleteLevelPlay(ActionEnv e)
        {
            JObject reference = (JObject)e.Params["IsFullyClearedVarRef"]!;
            bool full = Number(BigWorldQuestRuntime.GetVar(e.Session.player, RequireQuest(e).QuestId, reference.Value<int>("VarType"), reference.Value<string>("StrKey")!)) != 0;
            BigWorldPlayerState state = e.Session.player.BigWorldState;
            int level = state.InstLevelId != 0 ? state.InstLevelId : e.LevelId;
            int playId = TableReaderV2.Parse<BigWorldLevelPlayTable>().FirstOrDefault(row => row.LevelId == level)?.Id
                ?? throw new InvalidDataException($"CompleteLevelPlay: level {level} has no BigWorldLevelPlay row.");
            if (!state.LevelPlayDatas.TryGetValue(playId, out BigWorldLevelPlayData? data))
                state.LevelPlayDatas[playId] = data = new BigWorldLevelPlayData();
            data.IsFullCleared |= full;
            e.Session.player.Save();
            // The client's drama gate (XTableDramaDecisionLevelCondition CheckLevelPlayCompleted) reads its in-memory
            // XBigWorldInstanceModel, filled at login and by this notify; without it Path II needs a relog.
            e.Session.SendPush(new NotifyBigWorldLevelPlayDataChange { PlayId = playId, LevelPlayData = new BigWorldLevelPlayDataDto { IsFullCleared = data.IsFullCleared } });
            BigWorldTaskModule.OnProgressChanged(e.Session);
        }

        private static Theatre5DlcVector3? Vec(JToken? token) => token is JArray { Count: 3 } a
            ? new Theatre5DlcVector3 { X = a[0].Value<float>(), Y = a[1].Value<float>(), Z = a[2].Value<float>() }
            : null;

        #endregion
    }

    // Player-controlled trial characters (TrialNpc -> TrialPlayerNpc | TrialPlayerSelfNpc tables, imported from the
    // installed client). State: BigWorldPlayerState.TrialNpcIds/TrialAddMode/TrialCurNpcPos; the effective team is what
    // BigWorldModule.BuildNpcList returns everywhere (enter snapshot, RepFight, SwitchPlayerNpc).
    internal static class BigWorldTrialTeam
    {
        internal const int Cover = 1, Append = 2; // ETrialNpcAddMode

        private static readonly Lazy<Dictionary<int, TrialNpcTable>> Trials = new(() => TableReaderV2.Parse<TrialNpcTable>().ToDictionary(r => r.Id));
        private static readonly Lazy<Dictionary<int, TrialPlayerNpcTable>> Npcs = new(() => TableReaderV2.Parse<TrialPlayerNpcTable>().ToDictionary(r => r.Id));
        private static readonly Lazy<Dictionary<int, TrialPlayerSelfNpcTable>> SelfNpcs = new(() => TableReaderV2.Parse<TrialPlayerSelfNpcTable>().ToDictionary(r => r.Id));

        // Real team with the trial members merged in (Cover replaces the real team, Append follows it).
        internal static List<Theatre5WorldNpcData> Apply(Player player, List<Theatre5WorldNpcData> real)
        {
            BigWorldPlayerState state = player.BigWorldState;
            if (state.TrialNpcIds.Count == 0)
                return real;
            List<Theatre5WorldNpcData> team = state.TrialAddMode == Cover ? [] : real;
            foreach (int trialId in state.TrialNpcIds)
                team.Add(Build(player, trialId, team.Count));
            return team;
        }

        private static Theatre5WorldNpcData Build(Player player, int trialId, int pos)
        {
            if (!Trials.Value.TryGetValue(trialId, out TrialNpcTable? trial))
                throw new InvalidDataException($"TrialNpc {trialId} does not exist.");
            if (trial.IsPlayerSelf != 1)
            {
                TrialPlayerNpcTable row = Npcs.Value.TryGetValue(trialId, out TrialPlayerNpcTable? found) ? found
                    : throw new InvalidDataException($"TrialPlayerNpc {trialId} does not exist.");
                return new Theatre5WorldNpcData
                {
                    Id = row.NpcId, Level = 1, Pos = pos, TrialId = trialId,
                    Character = new Theatre5DlcCharacterData { Id = row.CharacterId, FashionId = row.FashionId },
                    AttribsData = null, // nil like the real team (BuildWorldNpcList): an empty blob overruns XAttrib.Deserialize when a drama clones the player NPC
                };
            }
            // The commandant with the trial's fixed outfit; gender picks the male/female half of the row.
            TrialPlayerSelfNpcTable self = SelfNpcs.Value.TryGetValue(trialId, out TrialPlayerSelfNpcTable? s) ? s
                : throw new InvalidDataException($"TrialPlayerSelfNpc {trialId} does not exist.");
            bool male = BigWorldCharacterModule.GetGender(player) != 2;
            List<int> parts = male ? self.MalePartIds : self.FemalePartIds, colours = male ? self.MalePartColorIds : self.FemalePartColorIds;
            return new Theatre5WorldNpcData
            {
                Id = male ? self.MaleNpcId : self.FemaleNpcId, Level = 1, Pos = pos, TrialId = trialId, IsPlayerSelf = true,
                Gender = male ? 1 : 2,
                Character = new Theatre5DlcCharacterData
                {
                    Id = BigWorldModule.ConfigInt(male ? "PlayerMaleCharacterId" : "PlayerFemaleCharacterId"),
                    FashionId = male ? self.MaleFashionId : self.FemaleFashionId,
                    FashionColorId = male ? self.MaleFashionColorId : self.FemaleFashionColorId,
                },
                PartData = new Theatre5WorldNpcPartData
                {
                    PartList = parts.Where(p => p > 0).Select((p, i) => new Theatre5BigWorldCommanderFashion { PartId = p, ColourId = colours.ElementAtOrDefault(i) }).ToList(),
                },
                AttribsData = null,
            };
        }

        internal static void Add(Session session, int levelId, List<int> ids, int mode, int curNpcPos, int? questId)
        {
            Player player = session.player;
            BigWorldPlayerState state = player.BigWorldState;
            int before = BigWorldModule.BuildNpcList(player).Count;
            if (state.TrialNpcIds.Count == 0)
                state.TrialSavedCurNpcPos = state.CurNpcPos;
            if (mode == Cover)
            {
                state.TrialNpcIds = ids;
                state.TrialAddMode = Cover;
            }
            else
            {
                state.TrialNpcIds.AddRange(ids);
                state.TrialAddMode = state.TrialAddMode == Cover ? Cover : Append;
            }
            List<Theatre5WorldNpcData> team = BigWorldModule.BuildNpcList(player);
            state.CurNpcPos = Math.Clamp(curNpcPos, 0, team.Count - 1);
            // The native quest record (XDlcQuestDynamicData) carries the trial team too; it is what the client reads back.
            if (questId is { } id && state.QuestData.ActiveQuests.GetValueOrDefault(id)?.DynamicData is { } dynamic)
            {
                dynamic.TrialCharacterIdList = [.. state.TrialNpcIds];
                dynamic.TrialCharacterAddMode = state.TrialAddMode;
                dynamic.TrialCurCharacterPos = state.CurNpcPos;
            }
            player.Save();
            // The client replaces its whole team on Cover, otherwise only the appended members are new to it.
            Send(session, levelId, "RpcAddTrialNpcToTeam", mode, team, mode == Cover ? 0 : before);
        }

        internal static void Remove(Session session, int levelId)
        {
            Player player = session.player;
            if (player.BigWorldState.TrialNpcIds.Count == 0)
                return; // no trial team (list replayed after it was removed)
            bool covered = Discard(player);
            List<Theatre5WorldNpcData> team = BigWorldModule.BuildNpcList(player);
            // A covered real team was removed from the client and comes back as new actors.
            Send(session, levelId, "RpcRemoveTrialNpcFromTeam", null, team, covered ? 0 : team.Count);
        }

        // Drops the trial team and brings the real team's CurNpcPos back; true when the trial team had covered the real one.
        // Also the exit of the instance that owns it (BigWorldModule.LeaveInstance): the level switch that follows
        // replicates the real team, so no RpcRemoveTrialNpcFromTeam is needed.
        internal static bool Discard(Player player)
        {
            BigWorldPlayerState state = player.BigWorldState;
            bool covered = state.TrialAddMode == Cover;
            state.TrialNpcIds = [];
            state.TrialAddMode = 0;
            state.CurNpcPos = Math.Clamp(state.TrialSavedCurNpcPos, 0, Math.Max(0, BigWorldModule.BuildNpcList(player).Count - 1));
            foreach (Theatre5DlcQuestDynamicData dynamic in state.QuestData.ActiveQuests.Values.Select(q => q.DynamicData).OfType<Theatre5DlcQuestDynamicData>())
            {
                dynamic.TrialCharacterIdList = [];
                dynamic.TrialCharacterAddMode = 0;
                dynamic.TrialCurCharacterPos = 0;
            }
            player.Save();
            return covered;
        }

        // RpcAddTrialNpcToTeam [LevelId, AddMode, FightTeamNpcData] / RpcRemoveTrialNpcFromTeam [LevelId, FightTeamNpcData];
        // FightTeamNpcData [CurNpcPos, NpcDataList, XRpcActorReplicateList[[replicates]]] where the replicates are the XNpc
        // actors new to the client (retail team change 1276: one server-controlled XNpc, uuid = the server sequence).
        // Every replicate is backstage, also the controlled member: XController.SyncTeamDataInternal [DUMP48 0x1B06FC0] hands
        // control over with SwitchPlayerNpcInternal (SwitchIn un-backstages the new NPC, SwitchOut the old one, then
        // OnPlayerNpcChangedEvent) only while CurNpc != PlayerNpc, and XController.AddPlayerNpc [0x1B003C0] makes a
        // non-backstage player NPC the PlayerNpc on arrival. A non-backstage controlled replicate therefore skipped the
        // switch: followers (4033 cannon: XNpcNodeLockFollowModeImpl) kept the destroyed old NPC (NRE every tick) and
        // gameplay listeners (XGameplayHeavyArtilleryFire.OnPlayerNpcChanged: aim input map) never ran.
        private static void Send(Session session, int levelId, string rpc, int? mode, List<Theatre5WorldNpcData> team, int firstNew)
        {
            Player player = session.player;
            int cur = player.BigWorldState.CurNpcPos;
            // The client's uuid per team index: kept members keep theirs, the new replicates take the next server uuids.
            List<int> uuids = [.. Enumerable.Range(0, firstNew).Select(i => BigWorldActors.TeamNpcUuid(session, levelId, i))];
            List<byte[]> replicates = [];
            for (int i = firstNew; i < team.Count; i++)
            {
                if (BigWorldActors.AllocateServerUuid(session, levelId) is not { } id)
                    break; // level not replicated yet: the enter snapshot carries the team
                uuids.Add(id.Uuid);
                replicates.Add(BuildReplicate(player, team[i], levelId, id));
            }
            // The controlled member's pose reports are matched by these uuids (BigWorldModule.PersistNpcPosition).
            if (uuids.Count == team.Count)
                BigWorldActors.SetTeamUuids(session, levelId, uuids);
            byte[] args = BigWorldModule.Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(mode is null ? 2 : 3);
                BigWorldXRpc.WriteInt(ref w, levelId);
                if (mode is { } m) BigWorldXRpc.WriteInt(ref w, m);
                w.WriteRaw(BuildFightTeamNpcData(cur, team, replicates));
            });
            session.SendPush("XRpcCommon", BigWorldXRpc.Common(rpc, args, 0));
        }

        // One server-controlled backstage XNpc replicate of a team member new to the client.
        internal static byte[] BuildReplicate(Player player, Theatre5WorldNpcData npc, int levelId, (int Uuid, int Sequence) id) =>
            BigWorldModule.Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(10);
                w.Write("XNpc");
                w.Write(BigWorldModule.BuildRepNpc(npc, isBackstage: true));
                w.Write(BigWorldXRpc.ServerControllerId);
                w.Write(false);
                BigWorldXRpc.WriteInt(ref w, levelId);
                BigWorldXRpc.WriteInt(ref w, id.Uuid);
                w.Write(0);
                w.Write(BigWorldModule.BuildNpcComponents(player, npc, id.Uuid, isBackstage: true));
                BigWorldXRpc.WriteInt(ref w, id.Sequence);
                w.Write(0);
            });

        // XFightTeamNpcData [CurNpcPos, NpcDataList, XRpcActorReplicateList[[replicates]]].
        internal static byte[] BuildFightTeamNpcData(int cur, List<Theatre5WorldNpcData> team, List<byte[]> replicates) =>
            BigWorldModule.Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(3);
                BigWorldXRpc.WriteInt(ref w, cur);
                w.WriteRaw(MessagePackSerializer.Serialize(team));
                w.WriteArrayHeader(1);
                w.WriteArrayHeader(replicates.Count);
                foreach (byte[] replicate in replicates)
                    w.WriteRaw(replicate);
            });
    }

    // RpcPlayerSwitchLevelNotify [ControllerId, LevelId, NpcTravelInfos, LevelData, RpcControllerLeaveLevel,
    // RpcControllerEnterLevel] (dump.cs D:764600). Client contract [DUMP48]:
    //  - XFight.HandlePlayerSwitchLevelNotify 0x1F5D3E0 acts only when Leave.LevelId == the client's current level id AND
    //    Leave.ControllerId == its current controller id (else it just routes OnPlayerEnter/LeaveLevel for other
    //    controllers); notify.ControllerId is never read. It stores NpcTravelInfos (XFight +0xD8) and switches the fight
    //    state machine to SwitchLevel(3) when notify.LevelId != current, ReenterLevel(6) when equal.
    //  - OnEnterSwitchLevel 0x1F610A0 / OnEnterReenterLevel 0x1F60920 run XFight.InitLevelByProto(LevelData) (0x1F5E000, the
    //    same call as world entry's EnterFight 0x1F5C4D5), then LoadLevel(2).
    //  - XLevel.OnEnterLevel 0x1F9BFE0 consults NpcTravelInfos only when the controller has no PlayerNpc yet
    //    (GetLocalPlayerNpcTravelInfoList 0x1F5CF30): non-empty -> XController.PostInit(infos) spawns the team from them,
    //    empty -> PostInit() spawns it from the client's own team data. A LevelData that already replicates the team
    //    (PlayerNpc set by XNpc.PostInit) skips both, so the empty list is valid and the team pose comes from LevelData.
    //  - XFight.OnEnterLevel 0x1F5FB10 then sends RpcPlayerEnterLevelComplete (RequestServer), the trigger for the deferred
    //    level-start pushes.
    // The earlier shape (team only as travel infos, level without team actors) hit the NRE in XSceneRegion.CheckPointInSceneRegions
    // when XFightRLManager.LoadCamera ran before OnEnterLevel; replicating the team in LevelData avoids it (live-confirmed).
    internal static class BigWorldLevelSwitch
    {
        internal static void Send(Session session, int fromLevel, int toLevel, Theatre5DlcVector3? pos, Theatre5DlcVector3? rot)
        {
            Player player = session.player;
            List<Theatre5WorldNpcData> team = BigWorldModule.BuildNpcList(player);
            (BigWorldVector3, double?)? pose = pos is null ? null : (new BigWorldVector3 { X = pos.X, Y = pos.Y, Z = pos.Z }, (double?)rot?.Y);
            byte[] levelData = BigWorldModule.BuildRepLevel(session, toLevel, team, pose);
            // The client's system-function counters outlive the switch: hand back what the left level's quests applied first.
            BigWorldQuestRuntime.OnLevelSwitching(session, fromLevel);
            byte[] args = BigWorldModule.Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(6);
                w.Write((int)BigWorldXRpc.PlayerControllerId);
                BigWorldXRpc.WriteInt(ref w, toLevel);
                w.WriteArrayHeader(0); // NpcTravelInfos: the team is in LevelData
                w.Write(levelData);
                w.WriteArrayHeader(2); BigWorldXRpc.WriteInt(ref w, fromLevel); w.Write((int)BigWorldXRpc.PlayerControllerId);
                w.WriteArrayHeader(2); BigWorldXRpc.WriteInt(ref w, toLevel); w.Write((int)BigWorldXRpc.PlayerControllerId);
            });
            session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcPlayerSwitchLevelNotify", args, 0));
            // Level-start pushes (RL-object check, combat state, begin update) follow the client's RpcPlayerEnterLevelComplete.
            session.PendingBigWorldLoadCompleteXRpc = true;
        }

        // Pose the player returns to when an instance is left: the saved open-world pose, else the level's default spawn.
        internal static (Theatre5DlcVector3? Pos, Theatre5DlcVector3? Rot) OpenWorldPose(BigWorldPlayerState state)
        {
            if (state.LastPosition is { } p)
                return (new Theatre5DlcVector3 { X = (float)p.X, Y = (float)p.Y, Z = (float)p.Z },
                    new Theatre5DlcVector3 { Y = (float)(state.LastRotationY ?? 0D) });
            LevelTable level = BigWorldModule.Levels.Value[state.LastLevelId];
            return (new Theatre5DlcVector3 { X = (float)level.PositionX, Y = (float)level.PositionY, Z = (float)level.PositionZ },
                new Theatre5DlcVector3 { Y = (float)level.RotationY });
        }
    }
}
