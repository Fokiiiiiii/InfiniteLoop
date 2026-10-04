using System.Buffers;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using MessagePack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // WorldState slice: player/NPC pose, teleport, system-function gates, actor AI, level play timer, clear place, silent
    // photo and the NPC follow / guide / tip-icon state. Quest-scoped state lives in the quest's native DlcQuestDynamicData
    // (FuncEntryDisableDict, SystemFuncControlSaveData, LevelActorSaves -> XNpcSaveData); the client does not read the
    // system-function ones back (see SetSystemFuncEntryEnableBatch), the server replays them.
    internal static partial class BigWorldActionHandlers
    {
        private const int PoseTypePosition = 1, PoseTypePosAndRot = 3; // ELevelSetNpcPoseType (dump.cs D:819956)
        private const int PlayTimerSequence = 3; // XLevelPlayTimer's sequence in the level snapshot (BuildRepLevel)

        internal static void RegisterWorldState(Dictionary<int, Action<ActionEnv>> t)
        {
            t[3003] = ControlSystemFunction;
            t[3004] = ClearPlace;
            t[6001] = FixSetPlayerPosition;
            t[6003] = FixSetNpcPosition;
            t[14001] = NpcRelativeFollowMove;
            t[14003] = NpcStopFollow;
            t[14004] = NpcGuideMoveTo;
            t[14008] = NpcTetherFollowMove;
            t[14010] = NpcToggleFollowPauseState;
            t[16003] = PauseLevelPlayTimer;
            t[18004] = RestorePlace;
            t[19000] = SetNpcQuestTipIconActive;
            t[19007] = SetSystemFuncEntryEnableBatch;
            t[19013] = SetActorAIEnabled;
            t[20000] = TeleportPlayer;
            t[20002] = TakePhotoSilent;
        }

        #region Readers used by the enter snapshot

        // SetActorAIEnabled state; absent = enabled. Read by the level-NPC replicate (RepActor.IsAI).
        internal static bool ActorAiEnabled(BigWorldPlayerState state, int levelId, int actorType, int placeId) =>
            state.ActorAiOverrides.Find(o => o.LevelId == levelId && o.ActorType == actorType && o.PlaceId == placeId)?.Enabled ?? true;

        // SetNpcQuestTipIconActive state: RepNpc.TipIconQuestId of a level NPC (first active quest that lit its icon).
        internal static int NpcTipIconQuestId(BigWorldPlayerState state, int levelId, int placeId) =>
            state.QuestData.ActiveQuests.Values.OrderBy(q => q.QuestId)
                .Select(q => q.DynamicData?.LevelActorSaves.GetValueOrDefault(levelId)?.NpcSaveDatas.GetValueOrDefault(placeId)?.TipIconQuestId ?? 0)
                .FirstOrDefault(id => id != 0);

        // Saved follow mode of a level NPC (first active quest with one): written into the NPC's XNpcMoveComponent snapshot.
        // Falls back to the player-level follow record, which outlives the quest that started the follow.
        internal static Theatre5NpcSaveData? NpcFollowSave(BigWorldPlayerState state, int levelId, int placeId) =>
            state.QuestData.ActiveQuests.Values.OrderBy(q => q.QuestId)
                .Select(q => q.DynamicData?.LevelActorSaves.GetValueOrDefault(levelId)?.NpcSaveDatas.GetValueOrDefault(placeId))
                .FirstOrDefault(save => save is { RelativeFollowModeSaveData: not null } or { DirectlyFollowModeSaveData: not null } or { NodeLockFollowModeSaveData: not null } or { TetherFollowModeSaveData: not null })
            ?? (state.NpcFollowExtras.Find(x => x.LevelId == levelId && x.PlaceId == placeId) is { } extra && (extra.RelativeFollow is not null || extra.Tether is not null)
                ? new Theatre5NpcSaveData { RelativeFollowModeSaveData = extra.RelativeFollow, TetherFollowModeSaveData = extra.Tether } : null);

        // Saved 14004 guide move of a level NPC (first active quest with one): written as XRepNpcGuideMoveController.
        internal static Theatre5NpcGuideMoveSaveData? NpcGuideSave(BigWorldPlayerState state, int levelId, int placeId) =>
            state.QuestData.ActiveQuests.Values.OrderBy(q => q.QuestId)
                .Select(q => q.DynamicData?.LevelActorSaves.GetValueOrDefault(levelId)?.NpcSaveDatas.GetValueOrDefault(placeId)?.GuideMoveSaveData)
                .FirstOrDefault(g => g is not null);

        // 14010 pause flag of the NPC's follow (XRepNpcFollowMoveController.PausedCount).
        internal static bool NpcFollowPaused(BigWorldPlayerState state, int levelId, int placeId) =>
            state.NpcFollowExtras.Find(x => x.LevelId == levelId && x.PlaceId == placeId)?.Paused ?? false;

        #endregion

        #region Helpers

        private static float[] Vec(JToken? token, string name) =>
            token is JArray { Count: 3 } array ? array.Select(v => v.Value<float>()).ToArray() : throw new InvalidDataException($"Level action field {name} is not a Vector3.");

        private static float[]? OptionalVec(JToken? token, string name) => token is null or { Type: JTokenType.Null } ? null : Vec(token, name);

        private static Theatre5DlcQuestDynamicData RequireDynamic(ActionEnv e)
        {
            QuestActionContext quest = RequireQuest(e);
            return e.Session.player.BigWorldState.QuestData.ActiveQuests.GetValueOrDefault(quest.QuestId)?.DynamicData
                ?? throw new InvalidDataException($"Level action {e.ActionType}: quest {quest.QuestId} is not active.");
        }

        private static void Push(ActionEnv e, string rpcName, byte[] args) =>
            e.Session.SendPush("XRpcCommon", BigWorldXRpc.Common(rpcName, args, 0));

        // The player's persisted pose: the open-world pose, or the instance pose while inside an instance.
        private static void PersistPlayerPose(ActionEnv e, float[] position, float[]? euler)
        {
            BigWorldPlayerState state = e.Session.player.BigWorldState;
            if (state.InstLevelId != 0)
            {
                if (e.LevelId != state.InstLevelId)
                    return;
                state.InstPosition = new BigWorldVector3 { X = position[0], Y = position[1], Z = position[2] };
                if (euler is not null)
                    state.InstRotationY = BigWorldModule.NormalizeYaw(euler[1]);
                e.Session.player.Save();
                return;
            }
            if (e.LevelId != state.LastLevelId)
                return;
            state.LastPosition = new BigWorldVector3 { X = position[0], Y = position[1], Z = position[2] };
            if (euler is not null)
            {
                state.LastRotation = null;
                state.LastRotationY = BigWorldModule.NormalizeYaw(euler[1]);
            }
            e.Session.player.Save();
        }

        // RpcLevelSetNpcPoseRequest [NpcId (actor uuid), Position, Rotation quaternion, PoseChangeType, ResetNpcState] is an
        // ActorAction on XLevelProcess (uuid 1); dump.cs D:743257 / D:742603.
        private static void SendNpcPose(ActionEnv e, int npcUuid, float[] position, float[]? euler)
        {
            float[] rotation = euler is null ? [0, 0, 0, 1] : BigWorldModule.EulerToQuaternion(euler[0], euler[1], euler[2]);
            e.Session.SendPush("XRpcActorAction", BigWorldXRpc.ActorAction("RpcLevelSetNpcPoseRequest",
                BigWorldXRpc.Args(npcUuid, position, rotation, euler is null ? PoseTypePosition : PoseTypePosAndRot, false),
                e.LevelId, BigWorldModule.LevelProcessUuid));
        }

        #endregion

        #region Pose / teleport

        // 6001 FixSetPlayerPosition [Position, Rotation?]: relog fix that puts the player NPC at the configured pose.
        private static void FixSetPlayerPosition(ActionEnv e)
        {
            float[] position = Vec(e.Params["Position"], "Position");
            float[]? euler = OptionalVec(e.Params["Rotation"], "Rotation");
            PersistPlayerPose(e, position, euler);
            SendNpcPose(e, BigWorldActors.TeamNpcUuid(e.Session, e.LevelId, e.Session.player.BigWorldState.CurNpcPos), position, euler);
        }

        // 6003 FixSetNpcPosition [Position, Rotation?, PlaceId]: pose of a live level NPC. An NPC that is not loaded has
        // nothing to move (its replicate carries the table placement).
        private static void FixSetNpcPosition(ActionEnv e)
        {
            if (BigWorldActors.TryGetUuid(e.Session, e.LevelId, BigWorldActors.NpcType, e.Params.Value<int>("PlaceId"), out int uuid))
                SendNpcPose(e, uuid, Vec(e.Params["Position"], "Position"), OptionalVec(e.Params["Rotation"], "Rotation"));
        }

        // 20000 TeleportPlayer server half: the client half moves the avatar (black screen, streaming, camera); the server
        // records the destination so a relog resumes there.
        private static void TeleportPlayer(ActionEnv e) =>
            PersistPlayerPose(e, Vec(e.Params["Position"], "Position"), OptionalVec(e.Params["Rotation"], "Rotation"));

        #endregion

        #region System functions

        // 19007 SetSystemFuncEntryEnableBatch [EnableList, DisableList]: the quest's FuncEntryDisableDict counts disables per
        // ESystemFunctionType (disable +1, enable -1, absent = no-op) [DUMP48 XSystemFuncManager.SetFuncEntryEnableInternal
        // 0x1F1F9A0: counted per type; SetFuncEntryEnableBatch 0x1F1F4A0 applies EnableList then DisableList; a function is
        // enabled while its count <= 0 (CheckSystemFuncEntryEnable 0x1F1D5B0)]. RpcSetSystemFuncEntryEnableBatchRequest
        // [EnableList, DisableList] (handler Run 0x1F112B0). The client keeps the counts only in memory (XSystemFuncManager
        // +0x28, never restored from the quest save: XQuest's own dict fields are touched only by .ctor/CleanUp), so the
        // server replays them in the quest's level (BigWorldQuestRuntime.OnPlayerEnterLevelComplete) and undoes them before a
        // level switch (BigWorldQuestRuntime.OnLevelSwitching).
        private static void SetSystemFuncEntryEnableBatch(ActionEnv e)
        {
            List<int> enable = e.Params["EnableList"]!.Values<int>().ToList(), disable = e.Params["DisableList"]!.Values<int>().ToList();
            Dictionary<int, int> counts = RequireDynamic(e).FuncEntryDisableDict;
            foreach (int type in enable)
                if (counts.TryGetValue(type, out int count))
                {
                    if (count <= 1) counts.Remove(type);
                    else counts[type] = count - 1;
                }
            foreach (int type in disable)
                counts[type] = counts.GetValueOrDefault(type) + 1;
            List<int> enabled = RequireDynamic(e).FuncEntryEnabled;
            enabled.RemoveAll(disable.Contains);
            enabled.AddRange(enable.Where(type => !enabled.Contains(type)));
            e.Session.player.Save();
            BigWorldQuestRuntime.SendFuncEntry(e.Session, enable, disable);
        }

        // 3003 ControlSystemFunction* [SystemFunctionType, IsLimit | LimitType]: stored as the quest's
        // SystemFuncControlSaveData (DlcSystemFunctionControlSave) and sent as RpcControlSystemFunctionRequest
        // {Data: {type: XConfigLevelActionParams}}. The client sets (not counts) the control per type and keeps it in
        // XSystemFuncManager._systemFunCtrlArgsDic in memory [DUMP48 handler Run 0x1F10B50 -> ControlSystemFunctionByConfig
        // 0x1F1D680]; the params union is MessagePackBaseFormatterString (string key + inline body), the same base as the
        // retail-verified XLevelLogicLaunchContext union [DUMP48 XConfigLevelActionParamsFormatter : MessagePackBaseFormatterString].
        private static void ControlSystemFunction(ActionEnv e)
        {
            int type = e.Params.Value<int>("SystemFunctionType");
            List<Theatre5DlcSystemFunctionControlSave> saves = RequireDynamic(e).SystemFuncControlSaveData;
            saves.RemoveAll(s => s.SystemFunctionType == type);
            saves.Add(new Theatre5DlcSystemFunctionControlSave
            {
                SystemFunctionType = type,
                LimitType = e.Params.Value<int?>("LimitType") ?? 0,
                IsLimit = e.Params.Value<bool?>("IsLimit") ?? false,
            });
            e.Session.player.Save();
            Push(e, "RpcControlSystemFunctionRequest", ControlSystemFunctionArgs(e.Params));
        }

        // The control params class per function (the four XConfigLevelActionParamsControlSystemFunction* subclasses and the
        // field each carries) as the quest tables use them; the save keeps only type/LimitType/IsLimit.
        internal static JObject? ControlParams(Theatre5DlcSystemFunctionControlSave save) => save.SystemFunctionType switch
        {
            2 => new JObject { ["$type"] = "XConfigLevelActionParamsControlSystemFunctionTask", ["LimitType"] = save.LimitType, ["SystemFunctionType"] = 2 },
            3 => new JObject { ["$type"] = "XConfigLevelActionParamsControlSystemFunctionMap", ["IsLimit"] = save.IsLimit, ["SystemFunctionType"] = 3 },
            4 => new JObject { ["$type"] = "XConfigLevelActionParamsControlSystemFunctionShortMsg", ["IsLimit"] = save.IsLimit, ["SystemFunctionType"] = 4 },
            14 => new JObject { ["$type"] = "XConfigLevelActionParamsControlSystemFunctionFirstPerson", ["IsLimit"] = save.IsLimit, ["SystemFunctionType"] = 14 },
            _ => null,
        };

        internal static byte[] ControlSystemFunctionArgs(JObject p)
        {
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            writer.WriteArrayHeader(1);
            writer.WriteMapHeader(1);
            BigWorldXRpc.WriteInt(ref writer, p.Value<int>("SystemFunctionType"));
            writer.Write(p.Value<string>("$type"));
            JProperty[] fields = p.Properties().Where(f => f.Name != "$type").ToArray();
            writer.WriteMapHeader(fields.Length);
            foreach (JProperty field in fields)
            {
                writer.Write(field.Name);
                WriteJson(ref writer, field.Value);
            }
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        private static void WriteJson(ref MessagePackWriter writer, JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Integer: BigWorldXRpc.WriteInt(ref writer, token.Value<long>()); break;
                case JTokenType.Float: writer.Write(token.Value<float>()); break;
                case JTokenType.Boolean: writer.Write(token.Value<bool>()); break;
                case JTokenType.String: writer.Write(token.Value<string>()); break;
                case JTokenType.Null: writer.WriteNil(); break;
                case JTokenType.Array:
                    writer.WriteArrayHeader(token.Count());
                    foreach (JToken item in token) WriteJson(ref writer, item);
                    break;
                default: throw new InvalidDataException($"Unsupported level action param type {token.Type}.");
            }
        }

        #endregion

        #region Actor AI / play timer / photo

        // 19013 SetActorAIEnabled [PlaceID, ActorType, Enabled]: no BigWorld RPC exists (dump.cs, EN Lua and both captures
        // carry none), so the flag is persisted and the actor's replicate states it (XRpcActorReplicate.IsAI, D:55332);
        // an already-live actor changes on its next replicate.
        private static void SetActorAIEnabled(ActionEnv e)
        {
            int actorType = e.Params.Value<int>("ActorType"), placeId = e.Params.Value<int>("PlaceID");
            List<BigWorldActorAiOverride> overrides = e.Session.player.BigWorldState.ActorAiOverrides;
            BigWorldActorAiOverride? found = overrides.Find(o => o.LevelId == e.LevelId && o.ActorType == actorType && o.PlaceId == placeId);
            if (found is null)
                overrides.Add(found = new BigWorldActorAiOverride { LevelId = e.LevelId, ActorType = actorType, PlaceId = placeId });
            found.Enabled = e.Params.Value<bool>("Enabled");
            e.Session.player.Save();
        }

        // 16003 PauseLevelPlayTimer [IsPause]: RpcLevelPlayTimerPauseNotify / RpcLevelPlayTimerResumeNotify are ActorActions
        // on the replicated XLevelPlayTimer (D:742879/742891); the client ignores them unless the timer runs/is paused.
        private static void PauseLevelPlayTimer(ActionEnv e)
        {
            e.Session.SendPush("XRpcActorAction", BigWorldXRpc.ActorAction(
                e.Params.Value<bool>("IsPause") ? "RpcLevelPlayTimerPauseNotify" : "RpcLevelPlayTimerResumeNotify",
                BigWorldXRpc.Args(), e.LevelId, BigWorldModule.ServerUuid(PlayTimerSequence)));
            BigWorldQuestRuntime.OnPauseTimerAction(e.Session, e.Params.Value<bool>("IsPause"));
        }

        // 20002 TakePhotoSilent [PhotoKey, SpotId, Fov]. The client answers a server RpcTakePhotoSilentRequest {ShotId,
        // ShotPos, ShotRot, Fov} (D:14057), but ShotPos/ShotRot come from level "spots" (XDlcCSharpFuncs:GetSpot) that no
        // exported table carries, so the request is recorded (ShotId = position in the list) and not pushed. GAP.
        private static void TakePhotoSilent(ActionEnv e)
        {
            e.Session.player.BigWorldState.SilentPhotoRequests.Add(new BigWorldSilentPhotoRequest
            {
                LevelId = e.LevelId,
                QuestId = RequireQuest(e).QuestId,
                PhotoKey = e.Params.Value<int>("PhotoKey"),
                SpotId = e.Params.Value<int>("SpotId"),
                Fov = e.Params.Value<float>("Fov"),
            });
            e.Session.player.Save();
        }

        #endregion

        #region Clear place

        // DlcQuest.ClearPlaceArgs[ClearId]. SpecificActors lists EActorType -> place ids (minus ExcludedActors); the
        // SpecificLevelConfigGroupFolder variant names an editor folder whose members no exported table lists (GAP: recorded,
        // no actors resolved).
        private static List<(int Type, int PlaceId)> ClearActors(int questId, string clearId, Session session)
        {
            JObject? args = BigWorldQuestModule.Quests.Value.TryGetValue(questId, out var quest) && !string.IsNullOrWhiteSpace(quest.Config)
                ? JObject.Parse(quest.Config)["ClearPlaceArgs"]?[clearId] as JObject : null;
            if (args is null)
                throw new InvalidDataException($"Quest {questId} has no ClearPlaceArgs '{clearId}'.");
            if (args.Value<string>("$type") != "XConfigClearPlaceArgsSpecificActors")
            {
                session.log.Warn($"BigWorld ClearPlace '{clearId}': {args.Value<string>("$type")} members are not in any exported table.");
                return [];
            }
            HashSet<(int, int)> excluded = (args["ExcludedActors"] as JObject)?.Properties()
                .SelectMany(p => p.Value.Values<int>().Select(id => (int.Parse(p.Name), id))).ToHashSet() ?? [];
            return ((JObject)args["ActorPlaceIds"]!).Properties()
                .SelectMany(p => p.Value.Values<int>().Select(id => (int.Parse(p.Name), id)))
                .Where(actor => !excluded.Contains(actor)).ToList();
        }

        // 3004 ClearPlace [ClearId]: unloads the clear's actors (persisted as actor overrides, so the enter snapshot omits
        // them) and records the clear with each actor's previous load state for RestorePlace.
        private static void ClearPlace(ActionEnv e)
        {
            int questId = RequireQuest(e).QuestId;
            string clearId = e.Params.Value<string>("ClearId")!;
            BigWorldPlayerState state = e.Session.player.BigWorldState;
            if (state.ExecutedClearPlaces.Any(r => r.LevelId == e.LevelId && r.QuestId == questId && r.ClearId == clearId))
                return;
            List<(int Type, int PlaceId)> actors = ClearActors(questId, clearId, e.Session).Where(a => a.Type is BigWorldActors.NpcType or BigWorldActors.SceneObjectType).ToList();
            state.ExecutedClearPlaces.Add(new BigWorldClearPlaceRecord
            {
                LevelId = e.LevelId,
                QuestId = questId,
                ClearId = clearId,
                Actors = actors.Select(a => new BigWorldClearedActor
                {
                    ActorType = a.Type,
                    PlaceId = a.PlaceId,
                    PriorLoaded = BigWorldActors.LoadedOverride(state, e.LevelId, a.Type, a.PlaceId),
                }).ToList(),
            });
            BigWorldActors.UnloadLevelNpcs(e.Session, e.LevelId, actors.Where(a => a.Type == BigWorldActors.NpcType).Select(a => a.PlaceId).ToList());
            BigWorldActors.UnloadSceneObjects(e.Session, e.LevelId, actors.Where(a => a.Type == BigWorldActors.SceneObjectType).Select(a => a.PlaceId).ToList());
            e.Session.player.Save();
        }

        // 18004 RestorePlace [ClearId]: back to the state before the clear (previous override, else the table default).
        private static void RestorePlace(ActionEnv e)
        {
            int questId = RequireQuest(e).QuestId;
            string clearId = e.Params.Value<string>("ClearId")!;
            BigWorldPlayerState state = e.Session.player.BigWorldState;
            BigWorldClearPlaceRecord? record = state.ExecutedClearPlaces.Find(r => r.QuestId == questId && r.ClearId == clearId);
            if (record is null)
                return;
            state.ExecutedClearPlaces.Remove(record);
            List<int> npcs = [], sceneObjects = [];
            foreach (BigWorldClearedActor actor in record.Actors)
            {
                bool load = actor.PriorLoaded ?? (actor.ActorType == BigWorldActors.NpcType
                    ? BigWorldModule.LevelNpcOf(record.LevelId, actor.PlaceId) is { IsOnlyClient: 0, DeferLoading: 0 }
                    : BigWorldModule.SceneObjectsOf(record.LevelId).TryGetValue(actor.PlaceId, out var so) && so.IsOnlyClient == 0 && so.DeferLoading == 0);
                if (load) (actor.ActorType == BigWorldActors.NpcType ? npcs : sceneObjects).Add(actor.PlaceId);
            }
            BigWorldActors.LoadLevelNpcs(e.Session, record.LevelId, npcs);
            BigWorldActors.LoadSceneObjects(e.Session, record.LevelId, sceneObjects);
            e.Session.player.Save();
        }

        #endregion

        #region NPC follow / guide / tip icon

        // XNpcSaveData of a level NPC in the quest's LevelActorSaves (dump.cs D:70709): the client restores follow, guide and
        // tip-icon state from it on enter; NPC AI runs on the client, so the server half only keeps this state.
        private static Theatre5NpcSaveData NpcSave(ActionEnv e, int placeId)
        {
            Theatre5DlcQuestDynamicData dynamic = RequireDynamic(e);
            if (!dynamic.LevelActorSaves.TryGetValue(e.LevelId, out Theatre5LevelActorSaveData? actors))
                dynamic.LevelActorSaves[e.LevelId] = actors = new();
            if (!actors.NpcSaveDatas.TryGetValue(placeId, out Theatre5NpcSaveData? save))
                actors.NpcSaveDatas[placeId] = save = new();
            return save;
        }

        private static BigWorldNpcFollowExtra FollowExtra(ActionEnv e, int placeId)
        {
            List<BigWorldNpcFollowExtra> extras = e.Session.player.BigWorldState.NpcFollowExtras;
            BigWorldNpcFollowExtra? found = extras.Find(x => x.LevelId == e.LevelId && x.PlaceId == placeId);
            if (found is null)
                extras.Add(found = new BigWorldNpcFollowExtra { LevelId = e.LevelId, QuestId = RequireQuest(e).QuestId, PlaceId = placeId });
            return found;
        }

        // Follow modes are exclusive on an NPC [INFERENCE: one follow mode per XNpc], so starting one drops the others.
        private static void ClearFollow(Theatre5NpcSaveData save)
        {
            save.RelativeFollowModeSaveData = null;
            save.DirectlyFollowModeSaveData = null;
            save.NodeLockFollowModeSaveData = null;
            save.TetherFollowModeSaveData = null;
        }

        // 14001 NpcRelativeFollowMove -> XNpcRelativeFollowModeSaveData.
        private static void NpcRelativeFollowMove(ActionEnv e)
        {
            int placeId = e.Params.Value<int>("NpcPlaceId");
            Theatre5NpcSaveData save = NpcSave(e, placeId);
            ClearFollow(save);
            BigWorldNpcFollowExtra followExtra = FollowExtra(e, placeId);
            followExtra.Tether = null;
            followExtra.RelativeFollow = save.RelativeFollowModeSaveData = new Theatre5NpcRelativeFollowModeSaveData
            {
                IsForceTeleportEnabled = e.Params.Value<bool>("EnableForceTeleport"),
                ForceTeleportRange = e.Params.Value<float>("TeleportRange"),
                IsFollowPlayer = e.Params.Value<bool>("IsFollowPlayer"),
                FollowTargetNpcPlaceId = e.Params.Value<int>("FollowTargetNpcPlaceId"),
                TargetAngle = e.Params.Value<float>("TargetAngle"),
                TargetRadius = e.Params.Value<float>("TargetRadius"),
                ChaseRadius = e.Params.Value<float>("ChaseRadius"),
                MaxIdleLagDistance = e.Params.Value<float>("MaxIdleLagDistance"),
                IdleLookAtTargetDelayTime = e.Params.Value<float>("IdleLookAtTargetDelayTime"),
                UseNavMesh = e.Params.Value<bool>("UseNavMesh"),
                NormalFollowRadius = e.Params.Value<float>("NormalFollowRadius"),
                StartFollowDelayTime = e.Params.Value<float>("StartFollowDelayTime"),
            };
            e.Session.player.Save();
        }

        // 14003 NpcStopFollow [NpcPlaceId]: no follow mode (native or tether) remains.
        private static void NpcStopFollow(ActionEnv e)
        {
            int placeId = e.Params.Value<int>("NpcPlaceId");
            ClearFollow(NpcSave(e, placeId));
            e.Session.player.BigWorldState.NpcFollowExtras.RemoveAll(x => x.LevelId == e.LevelId && x.PlaceId == placeId);
            e.Session.player.Save();
        }

        // 14004 NpcGuideMoveTo -> XNpcGuideMoveSaveData (WaitComplete belongs to the client run, not the save).
        private static void NpcGuideMoveTo(ActionEnv e)
        {
            float[] target = Vec(e.Params["TargetPosition"], "TargetPosition");
            NpcSave(e, e.Params.Value<int>("NpcPlaceId")).GuideMoveSaveData = new Theatre5NpcGuideMoveSaveData
            {
                TargetPosition = new Theatre5DlcVector3 { X = target[0], Y = target[1], Z = target[2] },
                UseNavMesh = e.Params.Value<bool>("UseNavMesh"),
                OutOfRouteRange = e.Params.Value<float>("OutOfRouteRange"),
                StartGuideRange = e.Params.Value<float>("StartGuideRange"),
                ReachTargetPositionRange = e.Params.Value<float>("ReachTargetPositionRange"),
                WaitDramaCaptionName = e.Params.Value<string>("WaitDramaCaptionName") ?? "",
                WaitDramaCaptionPlayProbability = e.Params.Value<float>("WaitDramaCaptionPlayProbability"),
                IdleTurningDelayTime = e.Params.Value<float>("IdleTurningDelayTime"),
                GuideMoveTypeMatchesTarget = e.Params.Value<bool>("GuideMoveTypeMatchesTarget"),
                GuideMoveType = e.Params.Value<int>("GuideMoveType"),
            };
            e.Session.player.Save();
        }

        // 14008 NpcTetherFollowMove -> XNpcTetherFollowModeSaveData (replicated as ENpcFollowMode.TetherFollowNpc); replaces any other follow mode.
        private static void NpcTetherFollowMove(ActionEnv e)
        {
            int placeId = e.Params.Value<int>("NpcPlaceId");
            Theatre5NpcSaveData save = NpcSave(e, placeId);
            ClearFollow(save);
            float[] offset = Vec(e.Params["TargetPosOffset"], "TargetPosOffset");
            BigWorldNpcFollowExtra extra = FollowExtra(e, placeId);
            extra.RelativeFollow = null;
            extra.Paused = false;
            extra.Tether = save.TetherFollowModeSaveData = new Theatre5NpcTetherFollowModeSaveData
            {
                IsForceTeleportEnabled = e.Params.Value<bool>("EnableForceTeleport"),
                ForceTeleportRange = e.Params.Value<float>("TeleportRange"),
                IsPlayerBecomeFollower = e.Params.Value<bool>("IsPlayerBecomeFollower"),
                IsFollowPlayer = e.Params.Value<bool>("IsFollowPlayer"),
                FollowTargetNpcPlaceId = e.Params.Value<int>("FollowTargetNpcPlaceId"),
                LeadExpRotRadius = e.Params.Value<float>("LeadExpRotRadius"),
                ExpReachTime = e.Params.Value<float>("ExpReachTime"),
                TargetPosOffset = new Theatre5DlcVector3 { X = offset[0], Y = offset[1], Z = offset[2] },
                NpcAnimAlignRadius = e.Params.Value<float>("NpcAnimAlignRadius"),
                ChaseRadius = e.Params.Value<float>("ChaseRadius"),
                HandType = e.Params.Value<int>("HandType"),
            };
            e.Session.player.Save();
        }

        // 14010 NpcToggleFollowPauseState [NpcPlaceId, IsPause].
        private static void NpcToggleFollowPauseState(ActionEnv e)
        {
            FollowExtra(e, e.Params.Value<int>("NpcPlaceId")).Paused = e.Params.Value<bool>("IsPause");
            e.Session.player.Save();
        }

        // 19000 SetNpcQuestTipIconActive [PlaceId, Active]: XNpc.SetNpcQuestTipIconActive(questId, active) uses the launching
        // quest; saved as XNpcSaveData.TipIconQuestId and replicated in RepNpc.TipIconQuestId (D:759854).
        private static void SetNpcQuestTipIconActive(ActionEnv e)
        {
            int questId = RequireQuest(e).QuestId;
            NpcSave(e, e.Params.Value<int>("PlaceId")).TipIconQuestId = e.Params.Value<bool>("Active") ? questId : 0;
            e.Session.player.Save();
        }

        #endregion
    }
}
