using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.statussyncfight.level.sceneconfig;
using MessagePack;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Big-map teleport: RpcPlayerSwitchLevelRequest.
    internal static partial class BigWorldModule
    {
        // levelId -> pinId -> native XTableLevelMapPinNew (Level.ConfigGroups + DeferredConfigGroups of the sector bundle).
        private static readonly Lazy<Dictionary<int, Dictionary<int, LevelMapPinTable>>> MapPinsByLevel = new(() =>
        {
            ILookup<(string, int), LevelMapPinTable> byGroup = TableReaderV2.Parse<LevelMapPinTable>().ToLookup(row => (row.Sector, row.ConfigGroupId));
            return Levels.Value.Values.ToDictionary(
                level => level.Id,
                level => level.ConfigGroups.Concat(level.DeferredConfigGroups).SelectMany(groupId => byGroup[GroupKey(level, groupId)])
                    .GroupBy(row => row.PinId)
                    .ToDictionary(group => group.Key, group => group.First()));
        });

        // RpcPlayerSwitchLevelRequest [ControllerId, LevelId, TargetLevelId, Position, Rotation(quaternion), MapPinLevelId, MapPinId]
        // (dump.cs D:822889). Sent by XFight.SwitchLevelInternal [DUMP48 0x1F64DA0] when XFight.Teleport(mapPinLevelId, mapPinId)
        // targets another level than the client's current one; the client then waits for RpcPlayerSwitchLevelNotify. A teleport
        // inside the current level never reaches the server (XFight.Teleport moves the NPC locally, 0x1F65650). Returns the
        // response code; `afterResponse` is the level switch to push once the response is out.
        private static int HandlePlayerSwitchLevel(Session session, byte[] argsPayload, out Action? afterResponse)
        {
            afterResponse = null;
            object?[] args = MessagePackSerializer.Deserialize<object?[]>(argsPayload, Packet.InboundOptions);
            if (args.Length < 7 || !TryVector(args[3], 3, out double[] pos) || !TryVector(args[4], 4, out double[] quat))
                return CodeInvalidRequest;
            int level = BigWorldXRpc.ToInt(args[1]), target = BigWorldXRpc.ToInt(args[2]);
            int pinLevel = BigWorldXRpc.ToInt(args[5]), pinId = BigWorldXRpc.ToInt(args[6]);
            BigWorldPlayerState state = session.player.BigWorldState;
            int current = CurrentLevelId(state);
            if (session.BigWorldWorldId == 0)
                return CodeNotInWorld;
            if (level != current
                || !MapPinsByLevel.Value.TryGetValue(pinLevel, out Dictionary<int, LevelMapPinTable>? pins) || !pins.TryGetValue(pinId, out LevelMapPinTable? pin)
                || pin.TeleportEnable != 1 || target != (pin.TeleportLevelId != 0 ? pin.TeleportLevelId : pinLevel))
                return CodeInvalidRequest;
            // AscNet policy [INF]: XBWMapPinData.IsActive/CheckTeleporterActive. NPC pins are always active; a scene-object pin is
            // active when its object is no teleporter component, is active by default, or was activated (TeleporterData).
            // The pin's ConditionId display gate is not evaluated.
            bool active = pin.NpcPlaceId != 0
                || (pin.SceneObjectPlaceId != 0 && (!SceneObjectsOf(pinLevel).TryGetValue(pin.SceneObjectPlaceId, out LevelSceneObjectTable? so)
                    || so.IsTeleporter != 1 || so.TeleporterDefaultActive == 1
                    || state.TeleporterData.TryGetValue(pinLevel, out List<int>? placeIds) && placeIds.Contains(pin.SceneObjectPlaceId)));
            if (!active)
                return CodeInvalidRequest;
            if (target == current)
                return 0;
            Theatre5DlcVector3 position = new() { X = (float)pos[0], Y = (float)pos[1], Z = (float)pos[2] };
            // Unity quaternion yaw (degrees), the unit of Level.RotationY and the persisted rotation.
            Theatre5DlcVector3 rotation = new() { Y = (float)(Math.Atan2(2 * (quat[3] * quat[1] + quat[0] * quat[2]), 1 - 2 * (quat[1] * quat[1] + quat[0] * quat[0])) * 180 / Math.PI) };
            int code = TransferLevel(session, 0, target, position, rotation, out bool newLevel);
            if (code != 0)
                return code;
            afterResponse = () =>
            {
                BigWorldLevelSwitch.Send(session, current, target, position, rotation);
                AfterLevelTransfer(session, target, newLevel);
            };
            return 0;
        }

        private static bool TryVector(object? value, int length, out double[] components)
        {
            double[] result = components = new double[length];
            return value is object?[] items && items.Length >= length && Enumerable.Range(0, length).All(i => BigWorldXRpc.TryToDouble(items[i], out result[i]));
        }
    }
}
