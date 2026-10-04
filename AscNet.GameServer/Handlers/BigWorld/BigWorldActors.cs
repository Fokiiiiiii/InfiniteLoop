using System.Runtime.CompilerServices;
using AscNet.Table.V2.share.statussyncfight.level.sceneconfig;
using AscNet.Common.Database;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Level-action actor effects (LoadNpc/UnloadNpc/LoadSceneObject/UnloadSceneObject/EnableActorInteractableComponent).
    // Every effect is persisted as a BigWorldActorOverride, so BuildRepLevel (enter snapshot) reproduces it on re-enter,
    // and replicated live to the client when the actor exists in the session's current level.
    // Wire (retail babylonia.pcap 1572/1574/1657/1797-1803): load = XRpcActorReplicate (server controller 15, uuid from the
    // server controller sequence, ControllerIncId = that sequence) + XRpcCommon RpcActorChangeController [level, uuid, 1,
    // true, false] (target 0, envelope level = actor level); unload = XRpcCommon XRpcRemoveActor [level, uuid] (target 0);
    // interactable = XRpcComponentAction RpcSetInteractableCmpEnableRequest [enable] target 1.
    internal static class BigWorldActors
    {
        internal const int NpcType = 1, SceneObjectType = 2; // EActorType

        // Per-session view of the replicated level: uuid of every live actor and the server controller's last sequence.
        private sealed class LevelView
        {
            public int LevelId;
            public int LastSequence;
            public readonly Dictionary<(int Type, int PlaceId), int> Uuids = new();
            public List<int>? TeamUuids; // uuid per team index after an in-world team sync; null = the snapshot's PlayerNpcUuid(index)
            // KillEnemyGroup members alive in this view: uuid -> (objective id, 1-based spawn index).
            public readonly Dictionary<int, (int ObjectiveId, int Index)> Groups = new();
        }

        private static readonly ConditionalWeakTable<Session, LevelView> Views = new();

        // Called by BuildRepLevel: the snapshot defines which uuids exist and where the server sequence continues.
        internal static void OnLevelSnapshot(Session session, int levelId, int lastSequence,
            IEnumerable<(int PlaceId, int Uuid)> sceneObjects, IEnumerable<(int PlaceId, int Uuid)> npcs)
        {
            LevelView view = Views.GetOrCreateValue(session);
            view.LevelId = levelId;
            view.LastSequence = lastSequence;
            view.TeamUuids = null;
            view.Uuids.Clear();
            view.Groups.Clear();
            foreach ((int placeId, int uuid) in sceneObjects) view.Uuids[(SceneObjectType, placeId)] = uuid;
            foreach ((int placeId, int uuid) in npcs) view.Uuids[(NpcType, placeId)] = uuid;
        }

        internal static bool TryGetUuid(Session session, int levelId, int actorType, int placeId, out int uuid)
        {
            uuid = 0;
            return Views.TryGetValue(session, out LevelView? view) && view.LevelId == levelId && view.Uuids.TryGetValue((actorType, placeId), out uuid);
        }

        internal static bool HasView(Session session, int levelId) => Views.TryGetValue(session, out LevelView? view) && view.LevelId == levelId;

        // Uuid of the player's team member at `index` as the client knows it.
        internal static int TeamNpcUuid(Session session, int levelId, int index) =>
            Views.TryGetValue(session, out LevelView? view) && view.LevelId == levelId && view.TeamUuids is { } uuids && index >= 0 && index < uuids.Count
                ? uuids[index] : BigWorldModule.PlayerNpcUuid(index);

        internal static void SetTeamUuids(Session session, int levelId, List<int> uuids)
        {
            if (Views.TryGetValue(session, out LevelView? view) && view.LevelId == levelId)
                view.TeamUuids = uuids;
        }

        // Reverse of TryGetUuid: the place id of a live actor of the replicated level.
        internal static bool TryGetPlaceId(Session session, int levelId, int actorType, int uuid, out int placeId)
        {
            placeId = 0;
            if (!Views.TryGetValue(session, out LevelView? view) || view.LevelId != levelId)
                return false;
            foreach (((int type, int place), int value) in view.Uuids)
                if (type == actorType && value == uuid)
                {
                    placeId = place;
                    return true;
                }
            return false;
        }

        internal static bool? LoadedOverride(BigWorldPlayerState state, int levelId, int actorType, int placeId) =>
            Find(state, levelId, actorType, placeId)?.Loaded;

        internal static bool? InteractableOverride(BigWorldPlayerState state, int levelId, int actorType, int placeId) =>
            Find(state, levelId, actorType, placeId)?.Interactable;

        // Client-simulated NPC movement (routes, ecology AI, follow): the controlling client reports each pose with
        // XRpcNpcPositionAndRotation and retail's enter snapshot carries the last one (babylonia.pcap enter: 100002 and the
        // follower 700004 are off their config placement). Kept in memory; written by the next player Save.
        // ponytail: not saved per message (a mover would double the Mongo writes); upgrade to Save() if idle-player NPC poses must survive a crash.
        internal static bool TrackNpcPose(Session s, int levelId, int uuid, BigWorldVector3 position, BigWorldVector4 rotation)
        {
            if (!Views.TryGetValue(s, out LevelView? view) || view.LevelId != levelId) return false;
            foreach (((int type, int placeId), int live) in view.Uuids)
            {
                if (type != NpcType || live != uuid) continue;
                BigWorldActorOverride o = Touch(s.player.BigWorldState, levelId, NpcType, placeId);
                o.Position = position;
                o.Rotation = rotation;
                return true;
            }
            return false;
        }

        internal static (BigWorldVector3 Position, BigWorldVector4 Rotation)? NpcPose(BigWorldPlayerState state, int levelId, int placeId) =>
            Find(state, levelId, NpcType, placeId) is { Position: { } p, Rotation: { } r } ? (p, r) : null;

        // Next server-controller uuid of the replicated level (retail replicate ControllerIncId = the uuid's sequence);
        // null when the session has no snapshot of that level.
        internal static (int Uuid, int Sequence)? AllocateServerUuid(Session s, int levelId)
        {
            if (!Views.TryGetValue(s, out LevelView? view) || view.LevelId != levelId)
                return null;
            int sequence = ++view.LastSequence;
            return (BigWorldModule.ServerUuid(sequence), sequence);
        }


        private static BigWorldActorOverride? Find(BigWorldPlayerState state, int levelId, int actorType, int placeId) =>
            state.ActorOverrides.Find(o => o.LevelId == levelId && o.ActorType == actorType && o.PlaceId == placeId);

        private static BigWorldActorOverride Touch(BigWorldPlayerState state, int levelId, int actorType, int placeId)
        {
            BigWorldActorOverride? found = Find(state, levelId, actorType, placeId);
            if (found is not null) return found;
            found = new BigWorldActorOverride { LevelId = levelId, ActorType = actorType, PlaceId = placeId };
            state.ActorOverrides.Add(found);
            return found;
        }

        internal static void LoadLevelNpcs(Session s, int levelId, IEnumerable<int> placeIds)
        {
            foreach (int placeId in placeIds)
            {
                AscNet.Table.V2.share.statussyncfight.level.sceneconfig.LevelNpcTable? row = BigWorldModule.LevelNpcOf(levelId, placeId);
                if (row is null)
                {
                    s.log.Warn($"BigWorld LoadNpc: level {levelId} has no NPC placeId {placeId}.");
                    continue;
                }
                Load(s, levelId, NpcType, placeId, (uuid, sequence) =>
                    BigWorldModule.BuildLevelNpcReplicate(s.player, levelId, row, uuid, BigWorldXRpc.ServerControllerId, sequence));
            }
        }

        internal static void LoadSceneObjects(Session s, int levelId, IEnumerable<int> placeIds)
        {
            foreach (int placeId in placeIds)
            {
                if (!BigWorldModule.SceneObjectsOf(levelId).TryGetValue(placeId, out var row))
                {
                    s.log.Warn($"BigWorld LoadSceneObject: level {levelId} has no scene object placeId {placeId}.");
                    continue;
                }
                Load(s, levelId, SceneObjectType, placeId, (uuid, sequence) =>
                    BigWorldModule.BuildSceneObjectReplicate(s.player, levelId, row, uuid, BigWorldXRpc.ServerControllerId, sequence));
            }
        }

        private static void Load(Session s, int levelId, int actorType, int placeId, Func<int, int, byte[]> build)
        {
            BigWorldPlayerState state = s.player.BigWorldState;
            Touch(state, levelId, actorType, placeId).Loaded = true;
            s.player.Save();
            if (!Views.TryGetValue(s, out LevelView? view) || view.LevelId != levelId || view.Uuids.ContainsKey((actorType, placeId)))
                return; // not in the replicated level (applied on its next enter) or already live
            int sequence = ++view.LastSequence;
            int uuid = BigWorldModule.ServerUuid(sequence);
            view.Uuids[(actorType, placeId)] = uuid;
            s.SendPush("XRpcActorReplicate", build(uuid, sequence));
            s.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcActorChangeController",
                BigWorldXRpc.Args(levelId, uuid, (int)BigWorldXRpc.PlayerControllerId, true, false), levelId, target: 0));
        }

        // Spawns one KillEnemyGroup member into the replicated level (same wire as LoadNpc). False when the level is not the session's
        // replicated one (the next level snapshot's sync spawns it) or the member is already alive.
        internal static bool SpawnGroupNpc(Session s, int levelId, int objectiveId, int index, Func<int, int, byte[]> build)
        {
            if (!Views.TryGetValue(s, out LevelView? view) || view.LevelId != levelId || view.Groups.ContainsValue((objectiveId, index)))
                return false;
            int sequence = ++view.LastSequence;
            int uuid = BigWorldModule.ServerUuid(sequence);
            view.Groups[uuid] = (objectiveId, index);
            s.SendPush("XRpcActorReplicate", build(uuid, sequence));
            s.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcActorChangeController",
                BigWorldXRpc.Args(levelId, uuid, (int)BigWorldXRpc.PlayerControllerId, true, false), levelId, target: 0));
            return true;
        }

        internal static bool TryGetGroupMember(Session s, int levelId, int uuid, out int objectiveId, out int index)
        {
            objectiveId = index = 0;
            if (!Views.TryGetValue(s, out LevelView? view) || view.LevelId != levelId || !view.Groups.TryGetValue(uuid, out (int ObjectiveId, int Index) member))
                return false;
            (objectiveId, index) = member;
            return true;
        }

        // The members of an objective still alive in the view, removed from the client (the objective ended without killing them).
        internal static void DespawnGroup(Session s, int objectiveId)
        {
            if (!Views.TryGetValue(s, out LevelView? view))
                return;
            foreach (int uuid in view.Groups.Where(g => g.Value.ObjectiveId == objectiveId).Select(g => g.Key).ToList())
            {
                view.Groups.Remove(uuid);
                s.SendPush("XRpcCommon", BigWorldXRpc.Common("XRpcRemoveActor", BigWorldXRpc.Args(view.LevelId, uuid), view.LevelId, target: 0));
            }
        }

        // A dead member leaves the view (its actor dies and is cleaned up by the client itself).
        internal static void ForgetGroupMember(Session s, int uuid)
        {
            if (Views.TryGetValue(s, out LevelView? view))
                view.Groups.Remove(uuid);
        }

        internal static void UnloadLevelNpcs(Session s, int levelId, IEnumerable<int> placeIds) => Unload(s, levelId, NpcType, placeIds);

        internal static void UnloadSceneObjects(Session s, int levelId, IEnumerable<int> placeIds) => Unload(s, levelId, SceneObjectType, placeIds);

        private static void Unload(Session s, int levelId, int actorType, IEnumerable<int> placeIds)
        {
            foreach (int placeId in placeIds)
            {
                Touch(s.player.BigWorldState, levelId, actorType, placeId).Loaded = false;
                s.player.Save();
                if (Views.TryGetValue(s, out LevelView? view) && view.LevelId == levelId && view.Uuids.Remove((actorType, placeId), out int uuid))
                    s.SendPush("XRpcCommon", BigWorldXRpc.Common("XRpcRemoveActor", BigWorldXRpc.Args(levelId, uuid), levelId, target: 0));
            }
        }

        // A touch-collected scene object (ECollectWay.TouchTrigger): the client plays the Die state after RpcSceneObjectCollectNotify
        // and destroys the actor itself (XSceneObjectStateComponent.ChangeState Die [DUMP48 0x1E65E90], retail sends no XRpcRemoveActor
        // after a collect, babylonia.pcap 1088), so only the server's view and the persisted load state change. A later LoadSceneObject
        // (the next run) replicates it again.
        internal static void Collected(Session s, int levelId, int placeId)
        {
            Touch(s.player.BigWorldState, levelId, SceneObjectType, placeId).Loaded = false;
            s.player.Save();
            if (Views.TryGetValue(s, out LevelView? view) && view.LevelId == levelId)
                view.Uuids.Remove((SceneObjectType, placeId));
        }

        internal static bool BeScannedOverride(BigWorldPlayerState state, int levelId, int placeId) =>
            Find(state, levelId, SceneObjectType, placeId)?.BeScanned == true;

        // RpcChangeActorScannedStateNotify [BeScanned] from the controlling client (XBeScannedComponent.ChangeActorScannedState
        // with needSync): the client already applied it locally; persist it so the next enter snapshot (RepBeScannedComponent
        // CurBeScanned) keeps the object revealed. Unknown uuids and non-scene-object actors are ignored.
        internal static void SetBeScanned(Session s, int levelId, int actorUuid, bool beScanned)
        {
            if (!TryGetPlaceId(s, levelId, SceneObjectType, actorUuid, out int placeId)
                || !BigWorldModule.SceneObjectsOf(levelId).TryGetValue(placeId, out LevelSceneObjectTable? so) || so.BeScanned != 1)
                return;
            Touch(s.player.BigWorldState, levelId, SceneObjectType, placeId).BeScanned = beScanned;
            s.player.Save();
        }

        // ActorType is EActorType (1 Npc, 2 SceneObject); other types have no interactable component.
        internal static void SetInteractable(Session s, int levelId, int actorType, int placeId, bool enable)
        {
            if (actorType is not (NpcType or SceneObjectType))
                throw new InvalidDataException($"EnableActorInteractableComponent: unsupported ActorType {actorType}.");
            Touch(s.player.BigWorldState, levelId, actorType, placeId).Interactable = enable;
            s.player.Save();
            if (TryGetUuid(s, levelId, actorType, placeId, out int uuid))
                s.SendPush("XRpcComponentAction", BigWorldXRpc.ComponentAction("RpcSetInteractableCmpEnableRequest",
                    BigWorldXRpc.Args(enable), levelId, uuid, target: BigWorldXRpc.PlayerControllerId));
        }

        // Current-level convenience overloads used by the hotfix proxy and Lua-facing callers.
        internal static void SetInteractable(Session s, int actorType, int placeId, bool enable) =>
            SetInteractable(s, BigWorldModule.CurrentLevelId(s.player.BigWorldState), actorType, placeId, enable);

        internal static bool IsInteractable(Session s, int actorType, int placeId)
        {
            int levelId = BigWorldModule.CurrentLevelId(s.player.BigWorldState);
            if (InteractableOverride(s.player.BigWorldState, levelId, actorType, placeId) is { } value)
                return value;
            return actorType switch
            {
                NpcType => BigWorldModule.LevelNpcOf(levelId, placeId)?.CanInteract == 1,
                SceneObjectType => BigWorldModule.SceneObjectsOf(levelId).TryGetValue(placeId, out var so) && so.Interactable == 1,
                _ => false,
            };
        }
    }
}
