using System.Buffers;
using MessagePack;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // StatusSync XRpc envelopes (dump.cs XRpcBase/XRpcCommon/XRpcActorAction D:55170-55450).
    // Server->client: TargetControllerId = 1 (player controller), ControllerId = 15 (server controller);
    // client->server swaps them. Retail writes integers in their smallest MessagePack encoding.
    internal static class BigWorldXRpc
    {
        internal const byte PlayerControllerId = 1;
        internal const byte ServerControllerId = 15;

        // XRpcCommon [Name, Content, TargetControllerId, ControllerId, LevelId]. Retail targets: player controller 1 for
        // quest/nav/level-action pushes (envelope level 0), 0 (all controllers) for ProgressNotify, XRpcRemoveActor and
        // RpcActorChangeController (level = the actor's level).
        internal static byte[] Common(string rpcName, byte[] args, int levelId, byte target = PlayerControllerId)
        {
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            writer.WriteArrayHeader(5);
            writer.Write(rpcName);
            writer.Write(args);
            writer.Write(target);
            writer.Write(ServerControllerId);
            WriteInt(ref writer, levelId);
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        // XRpcComponentAction [Name, Content, TargetControllerId, ControllerId, LevelId, ActorUUID].
        // Retail: RpcSceneObjectCollectNotify/RpcActiveTeleporterNotify target 0 (all controllers),
        // RpcSetInteractableCmpEnableRequest targets the player controller 1.
        internal static byte[] ComponentAction(string rpcName, byte[] args, int levelId, int actorUuid, byte target = 0) =>
            Actor(rpcName, args, levelId, actorUuid, target);

        // XRpcActorAction, same layout (retail XRpcTeleportResetOnGroundRequest: target 1, level = current level).
        internal static byte[] ActorAction(string rpcName, byte[] args, int levelId, int actorUuid, byte target = PlayerControllerId) =>
            Actor(rpcName, args, levelId, actorUuid, target);

        private static byte[] Actor(string rpcName, byte[] args, int levelId, int actorUuid, byte target)
        {
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            writer.WriteArrayHeader(6);
            writer.Write(rpcName);
            writer.Write(args);
            writer.Write(target);
            writer.Write(ServerControllerId);
            WriteInt(ref writer, levelId);
            WriteInt(ref writer, actorUuid);
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        // Positional Rpc argument array: ints in smallest encoding, floats as float32, other values via MessagePack.
        internal static byte[] Args(params object?[] args)
        {
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            writer.WriteArrayHeader(args.Length);
            foreach (object? arg in args)
                WriteValue(ref writer, arg);
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        // Native XVarModifyDictionary<string,T>.Serialize [DUMP48 0x3C313B0 / XBinaryTool 0x2D21210, 0x2D21FB0]: concatenated
        // MessagePack map {key: value} of the set operations, then an array of removed keys (here always empty).
        internal static byte[] VarModifySet(string key, object value)
        {
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            writer.WriteMapHeader(1);
            writer.Write(key);
            WriteValue(ref writer, value);
            writer.WriteArrayHeader(0);
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        internal static void WriteValue(ref MessagePackWriter writer, object? value)
        {
            switch (value)
            {
                case null:
                    writer.WriteNil();
                    break;
                case int i:
                    WriteInt(ref writer, i);
                    break;
                case float f:
                    writer.Write(f);
                    break;
                case double d:
                    writer.Write((float)d);
                    break;
                case bool b:
                    writer.Write(b);
                    break;
                case string s:
                    writer.Write(s);
                    break;
                case byte[] bytes:
                    writer.Write(bytes);
                    break;
                case System.Collections.IList list:
                    writer.WriteArrayHeader(list.Count);
                    foreach (object? item in list)
                        WriteValue(ref writer, item);
                    break;
                default:
                    writer.WriteRaw(MessagePackSerializer.Serialize(value.GetType(), value));
                    break;
            }
        }

        internal static void WriteInt(ref MessagePackWriter writer, long value)
        {
            if (value < 0)
                writer.Write(value);
            else if (value <= byte.MaxValue)
                writer.Write((byte)value);
            else if (value <= ushort.MaxValue)
                writer.Write((ushort)value);
            else if (value <= uint.MaxValue)
                writer.Write((uint)value);
            else
                writer.Write(value);
        }

        // Inbound envelope: [Name, Content, TargetControllerId, ControllerId, LevelId, (ActorUUID)].
        internal static bool TryRead(byte[] payload, out string rpcName, out byte[] args, out int levelId, out int actorUuid)
        {
            rpcName = string.Empty;
            args = [];
            levelId = 0;
            actorUuid = 0;
            object?[] rpc;
            try
            {
                rpc = MessagePackSerializer.Deserialize<object?[]>(payload, Packet.InboundOptions);
            }
            catch (MessagePackSerializationException)
            {
                return false;
            }
            if (rpc.Length < 5 || rpc[0] is not string name || rpc[1] is not byte[] content)
                return false;
            rpcName = name;
            args = content;
            levelId = ToInt(rpc[4]);
            actorUuid = rpc.Length > 5 ? ToInt(rpc[5]) : 0;
            return true;
        }

        internal static int ToInt(object? value) => value switch
        {
            byte v => v,
            sbyte v => v,
            short v => v,
            ushort v => v,
            int v => v,
            uint v when v <= int.MaxValue => (int)v,
            long v when v is >= int.MinValue and <= int.MaxValue => (int)v,
            ulong v when v <= int.MaxValue => (int)v,
            _ => throw new MessagePackSerializationException($"Expected XRpc int32 value, got {value?.GetType().Name ?? "null"}.")
        };

        internal static bool TryToDouble(object? value, out double result)
        {
            switch (value)
            {
                case float f: result = f; return true;
                case double d: result = d; return true;
                case byte or sbyte or short or ushort or int or uint or long or ulong:
                    result = Convert.ToDouble(value);
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }
    }
}
