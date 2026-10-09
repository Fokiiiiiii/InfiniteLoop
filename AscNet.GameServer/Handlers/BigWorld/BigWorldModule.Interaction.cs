using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.statussyncfight.level.sceneconfig;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // RpcPlayerInteractRequest: collectables, interactive scene objects with option action lists (elevators) and level NPCs.
    internal static partial class BigWorldModule
    {
        private const int NpcActorType = 1;         // EActorType.Npc
        private const int SceneObjectActorType = 2; // EActorType.SceneObject
        private const int TeleportPlayerActionType = 20000; // ELevelActionType.TeleportPlayer

        // Native option config (XTableInteractOption of every level NPC / scene object InteractableComponent, imported by
        // Scripts/import_bigworld_scene_objects_4_7.py): levelId -> (actorType, placeId) -> optionId -> option.
        // Level.ConfigGroups and DeferredConfigGroups both belong to the level (deferred ones are loaded by quest actions).
        private static readonly Lazy<Dictionary<int, Dictionary<(int ActorType, int PlaceId), Dictionary<int, LevelInteractOptionTable>>>> InteractOptionsByLevel = new(() =>
        {
            ILookup<(string, int), LevelInteractOptionTable> byGroup = TableReaderV2.Parse<LevelInteractOptionTable>().ToLookup(row => (row.Sector, row.ConfigGroupId));
            return Levels.Value.Values.ToDictionary(
                level => level.Id,
                level => level.ConfigGroups.Concat(level.DeferredConfigGroups).SelectMany(groupId => byGroup[GroupKey(level, groupId)])
                    .GroupBy(row => (row.ActorType, row.PlaceId))
                    .ToDictionary(group => group.Key, group => group.GroupBy(row => row.OptionId).ToDictionary(o => o.Key, o => o.First())));
        });

        // The interact options a level actor accepts (null = none configured: any option id is taken).
        internal static IReadOnlyDictionary<int, LevelInteractOptionTable>? InteractOptionsOf(int levelId, int actorType, int placeId) =>
            InteractOptionsByLevel.Value.GetValueOrDefault(levelId)?.GetValueOrDefault((actorType, placeId));

        // Parsed CompleteActionList per option row (JSON of the row's Config column).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<LevelInteractOptionTable, JArray> OptionActions = new();

        private static JArray CompleteActionList(LevelInteractOptionTable option) => OptionActions.GetOrAdd(option, row =>
            string.IsNullOrEmpty(row.Config) ? [] : JObject.Parse(row.Config)["CompleteActionList"] as JArray ?? []);

        private static bool LevelHasNpc(int levelId, int placeId) =>
            Levels.Value.TryGetValue(levelId, out AscNet.Table.V2.share.statussyncfight.level.LevelTable? level)
            && level.ConfigGroups.Concat(level.DeferredConfigGroups).Any(groupId => LevelNpcsByGroup.Value[GroupKey(level, groupId)].Any(npc => npc.PlaceId == placeId));

        // RpcPlayerInteractRequest [PlayerId, PlayerControllerId, LauncherUUID, TargetUUID, TargetPlaceId,
        // TargetType:EActorType, LevelId, OptionId] (dump.cs D:735988).
        // Retail order: [collect reward push] -> RpcNpcInteractStartNotify -> [XRpcActorAction TeleportResetOnGround ->
        // RpcLevelActionClientExecute (option actions, client Finish)] -> RpcNpcInteractFinishNotify -> quest OnInteract.
        // AscNet policy: retail answers the request between Start and the action list; here the response follows every push
        // (the client handles pushes independently of the response).
        private static int HandlePlayerInteract(Session session, byte[] argsPayload)
        {
            object?[] args;
            int playerId, launcherController, launcherUuid, targetUuid, placeId, targetType, levelId, optionId;
            try
            {
                args = MessagePackSerializer.Deserialize<object?[]>(argsPayload, Packet.InboundOptions);
                if (args.Length < 8)
                    return CodeInvalidRequest;
                playerId = BigWorldXRpc.ToInt(args[0]);
                launcherController = BigWorldXRpc.ToInt(args[1]);
                launcherUuid = BigWorldXRpc.ToInt(args[2]);
                targetUuid = BigWorldXRpc.ToInt(args[3]);
                placeId = BigWorldXRpc.ToInt(args[4]);
                targetType = BigWorldXRpc.ToInt(args[5]);
                levelId = BigWorldXRpc.ToInt(args[6]);
                optionId = BigWorldXRpc.ToInt(args[7]);
            }
            catch (MessagePackSerializationException)
            {
                return CodeInvalidRequest;
            }
            if (playerId != session.player.PlayerData.Id || levelId != CurrentLevelId(session.player.BigWorldState))
                return CodeInvalidRequest;
            // The launcher is one of the player's own NPC actors.
            if (!Enumerable.Range(0, BuildNpcList(session.player).Count).Any(index => BigWorldActors.TeamNpcUuid(session, levelId, index) == launcherUuid))
                return CodeInvalidRequest;

            LevelSceneObjectTable? sceneObject = null;
            if (targetType == SceneObjectActorType)
            {
                if (!SceneObjectsOf(levelId).TryGetValue(placeId, out sceneObject))
                    return CodeInvalidRequest;
            }
            else if (targetType != NpcActorType || !LevelHasNpc(levelId, placeId))
                return CodeInvalidRequest;
            if (!BigWorldActors.IsInteractable(session, targetType, placeId))
                return CodeInvalidRequest;

            // An actor with configured options only accepts one of them; one without (chests, plain teleporters) takes any.
            LevelInteractOptionTable? option = null;
            if (InteractOptionsByLevel.Value[levelId].TryGetValue((targetType, placeId), out Dictionary<int, LevelInteractOptionTable>? options)
                && !options.TryGetValue(optionId, out option))
                return CodeInvalidRequest;

            if (sceneObject is not null)
            {
                List<RewardGoods>? rewards = CollectSceneObject(session, levelId, placeId, targetUuid);
                if (rewards is not null)
                    session.SendPush("XRpcComponentAction", BigWorldXRpc.ComponentAction("RpcSceneObjectCollectNotify",
                        BigWorldXRpc.Args(rewards), levelId, targetUuid));
                // CollectSceneObject objectives count the interaction itself: a reward claimed in an earlier visit still completes them.
                BigWorldQuestRuntime.OnSceneObjectCollected(session, levelId, placeId);
                if (sceneObject.IsTeleporter == 1 && ActivateTeleporter(session, levelId, placeId))
                    session.SendPush("XRpcComponentAction", BigWorldXRpc.ComponentAction("RpcActiveTeleporterNotify",
                        BigWorldXRpc.Args(), levelId, targetUuid));
            }

            // RpcNpcInteractStartNotify [TargetUUID, TargetPlaceId, TargetType, OptionId]; retail sends both without a level id.
            session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcNpcInteractStartNotify",
                BigWorldXRpc.Args(targetUuid, placeId, targetType, optionId), 0));
            void Finish()
            {
                session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcNpcInteractFinishNotify", BigWorldXRpc.Args(), 0));
                BigWorldQuestRuntime.OnInteract(session, targetType, placeId, optionId);
            }

            JArray actions = option is null ? [] : CompleteActionList(option);
            if (actions.Count == 0)
            {
                Finish();
                return 0;
            }
            // Retail resets the player NPC onto the ground before every teleporting option (elevators: 3 of 3 captured);
            // AscNet [INF]: the rule is "the list contains TeleportPlayer".
            if (actions.Any(action => (int?)action["ActionType"] == TeleportPlayerActionType))
                session.SendPush("XRpcActorAction", BigWorldXRpc.ActorAction("XRpcTeleportResetOnGroundRequest", BigWorldXRpc.Args(), levelId, launcherUuid));
            BigWorldLevelActions.Run(session, actions,
                new ActorInteractActionContext(launcherUuid, launcherController, targetType, placeId, levelId, optionId), Finish);
            return 0;
        }

        private const int TouchTriggerCollectWay = 2; // ECollectWay.TouchTrigger
        private const int TriggerStateEnter = 1;      // ETriggerState.Enter

        // RpcTriggerInteractNotify [TouchType:ETriggerTouchType, SourceActorId, TriggerId, TriggerState, SourceControllerId] (dump.cs
        // RpcTriggerInteractNotify): the client simulates the XTriggerComponent of the envelope's scene object and reports each
        // enter/exit of its controlled NPC (live: Space Leap cogs, [2, npc, 1, 1|2, 1] on actor <cog uuid>). The server decides what the
        // touch means: a reward-less TouchTrigger collectable (jumper coin) is collected once (RpcSceneObjectCollectNotify -> the client's
        // XSceneObjectCollectableComponent plays Die and destroys it, XSceneObjectStateComponent.ChangeState [DUMP48 0x1E4A0E0/0x1E65E90]),
        // then the step statistics count the collect / trigger enter (BigWorldQuestRuntime.Statistics).
        private static void HandleTriggerInteractNotify(Session session, byte[] argsPayload, int levelId, int actorUuid)
        {
            object?[] args;
            try { args = MessagePackSerializer.Deserialize<object?[]>(argsPayload, Packet.InboundOptions); }
            catch (MessagePackSerializationException) { return; }
            BigWorldPlayerState state = session.player.BigWorldState;
            if (args.Length < 5 || BigWorldXRpc.ToInt(args[3]) != TriggerStateEnter || levelId != CurrentLevelId(state)
                || BigWorldXRpc.ToInt(args[1]) != BigWorldActors.TeamNpcUuid(session, levelId, state.CurNpcPos)
                || !BigWorldActors.TryGetPlaceId(session, levelId, BigWorldActors.SceneObjectType, actorUuid, out int placeId)
                || !SceneObjectsOf(levelId).TryGetValue(placeId, out LevelSceneObjectTable? sceneObject))
                return;
            if (sceneObject.CollectWay == TouchTriggerCollectWay && sceneObject.CollectRewardId <= 0)
            {
                BigWorldActors.Collected(session, levelId, placeId);
                session.SendPush("XRpcComponentAction", BigWorldXRpc.ComponentAction("RpcSceneObjectCollectNotify",
                    BigWorldXRpc.Args(new List<RewardGoods>()), levelId, actorUuid));
                BigWorldQuestRuntime.OnSceneObjectBeCollected(session, levelId, sceneObject.CollectableType);
                BigWorldQuestRuntime.OnSceneObjectCollected(session, levelId, placeId);
            }
            BigWorldQuestRuntime.OnPlayerEnterTrigger(session, levelId, placeId, BigWorldXRpc.ToInt(args[2]));
        }
    }
}
