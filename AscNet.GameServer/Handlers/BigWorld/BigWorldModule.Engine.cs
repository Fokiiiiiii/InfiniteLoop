using System.Buffers;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.statussyncfight;
using AscNet.Table.V2.share.statussyncfight.level.sceneconfig;
using MessagePack;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Engine mode (temporary experiment switch, see README "BigWorld engine mode"):
    //   ASCNET_BIGWORLD_ENGINE=offline    -> WorldData.Online=false, no FightData/LevelData (client-hosted engine).
    //   ASCNET_BIGWORLD_ENGINE=online-min -> (default) Online=true/IsSingleOnline=true with a generated RepFight and
    //                                        minimal RepLevel; AscNet answers the StatusSync XRpc channel.
    internal static partial class BigWorldModule
    {
        private const string EngineVariable = "ASCNET_BIGWORLD_ENGINE";
        // RepController ids (retail LevelData: player controller 1 is master, server controller 15).
        private const int MasterControllerId = BigWorldXRpc.PlayerControllerId;

        internal static bool IsOnline => ResolveEngineMode() == "online-min";

        private static string ResolveEngineMode()
        {
            string? value = Environment.GetEnvironmentVariable(EngineVariable);
            if (string.IsNullOrWhiteSpace(value) || value.Equals("online-min", StringComparison.OrdinalIgnoreCase))
                return "online-min";
            if (value.Equals("offline", StringComparison.OrdinalIgnoreCase))
                return "offline";
            throw new InvalidOperationException($"{EngineVariable}={value} is not supported; use 'offline' or 'online-min'.");
        }

        private static readonly Lazy<List<FightConfigTable>> FightConfig = new(() => TableReaderV2.Parse<FightConfigTable>());

        #region EnterResultData

        private static BigWorldEnterResultData BuildEnterResultData(Session session, int worldId, int levelId,
            Theatre5DlcVector3? targetPos = null, Theatre5DlcVector3? targetRot = null)
        {
            Player player = session.player;
            BigWorldPlayerState state = player.BigWorldState;
            int playerId = checked((int)player.PlayerData.Id);
            bool online = IsOnline;
            List<Theatre5WorldNpcData> npcs = BuildNpcList(player);
            // The client indexes its team by CurNpcPos (XController.CurNpc): a stale position outside the team throws there.
            state.CurNpcPos = Math.Clamp(state.CurNpcPos, 0, Math.Max(0, npcs.Count - 1));

            bool openWorldPose = levelId == state.LastLevelId && state.InstLevelId == 0 && state.LastPosition is not null;
            // A resumed instance spawns at its saved pose (an explicit transfer target wins).
            if (levelId == state.InstLevelId && targetPos is null)
            {
                targetPos = ToDlcVector(state.InstPosition);
                targetRot = state.InstRotationY is double yaw ? new Theatre5DlcVector3 { Y = (float)yaw } : null;
            }
            Theatre5WorldData world = new()
            {
                Online = online,
                RoomId = null!,
                WorldId = worldId,
                LevelId = levelId,
                WorldType = BigWorlds.Value[worldId].WorldType,
                IsSingleOnline = true,
                QuestData = BigWorldQuestModule.BuildWorldQuestData(session),
                Players =
                [
                    new Theatre5WorldPlayerData
                    {
                        Id = playerId,
                        Master = true,
                        CurNpcPos = state.CurNpcPos,
                        Name = player.PlayerData.Name,
                        NpcList = npcs,
                        BornData = new Theatre5WorldPlayerBornData
                        {
                            Position = openWorldPose ? ToDlcVector(state.LastPosition) : targetPos,
                            EulerAngles = openWorldPose
                                ? new Theatre5DlcVector3 { Y = (float)(GetRotationY(state) ?? 0D) }
                                : targetRot,
                            LastLevelId = state.LastLevelId,
                            LastWorldId = state.LastWorldId
                        }
                    }
                ]
            };

            session.BigWorldFightStartedAt = DateTime.UtcNow;
            return new BigWorldEnterResultData
            {
                WorldData = world,
                FightData = online ? BuildRepFight(player, world) : null,
                LevelData = online ? BuildRepLevel(session, levelId, npcs) : null
            };
        }

        // The commandant NPC (BigWorldConfig PlayerMale/FemaleCharacterId) is the player's own avatar (retail IsPlayerSelf).
        internal static List<Theatre5WorldNpcData> BuildNpcList(Player player)
        {
            HashSet<int> commandantIds = [ConfigInt("PlayerMaleCharacterId"), ConfigInt("PlayerFemaleCharacterId")];
            List<Theatre5WorldNpcData> npcs = BigWorldCharacterModule.BuildWorldNpcList(player);
            foreach (Theatre5WorldNpcData npc in npcs)
                npc.IsPlayerSelf = npc.Character is { } character && commandantIds.Contains(character.Id);
            return BigWorldTrialTeam.Apply(player, npcs);
        }

        // AscNet policy: actor UUIDs follow the retail layout (sequence << 4 | server controller 15; XLevelProcess is 1).
        // Level managers take sequences 1..4, player team NPCs the sequences after them.
        internal const int ManagerSequenceCount = 4, LevelProcessUuid = 1;
        private static readonly byte[] EmptyComponentList = [0x90];
        internal static int ServerUuid(int sequence) => (sequence << 4) | BigWorldXRpc.ServerControllerId;
        internal static int PlayerNpcUuid(int index) => ServerUuid(ManagerSequenceCount + index + 1);
        // XGameplayManager's gameplay actor (manager sequence 4): parent of the scene objects the gameplay creates.
        internal static int GameplayUuid => ServerUuid(ManagerSequenceCount);

        // RepFight, installed-client layout (retail FightData has 11 keys; dump.cs D:764454 has the older 8-key layout
        // Time, IsFinished, IsSettled, FloatConfigDict, ResultData, TrialCurNpcPos, TrialNpcAddMode, InitialQuests).
        // Retail inserts one bool after IsSettled and appends two list tuples; AscNet emits their empty defaults.
        private static byte[] BuildRepFight(Player player, Theatre5WorldData world)
        {
            int playerId = world.Players[0].Id;
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            writer.WriteArrayHeader(11);
            writer.Write(0F); // Time: the snapshot is the fight start.
            writer.Write(false); // IsFinished
            writer.Write(false); // IsSettled
            writer.Write(false); // installed-client key 3 (absent from dump.cs), default
            // FloatConfigDict: FightConfig float rows for the EGravityType values (Jump, FreeFall) -> "<Type>Gravity".
            List<FightConfigTable> gravity = FightConfig.Value
                .Where(row => row.Type == "float" && (row.Key == "JumpGravity" || row.Key == "FreeFallGravity"))
                .ToList();
            writer.WriteMapHeader(gravity.Count);
            foreach (FightConfigTable row in gravity)
            {
                writer.Write(row.Key);
                writer.Write(float.Parse(row.Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            writer.WriteRaw(MessagePackSerializer.Serialize(new Theatre5DlcFightResultData
            {
                WorldData = world,
                PlayerData = { [playerId] = new Theatre5DlcFightResultPlayerData { PlayerId = playerId } }
            }));
            // TrialCurNpcPos: XController.InitCurNpcPos [DUMP48 0x1B033F0] takes the controlled position from here when
            // TrialNpcAddMode != 0 (else from WorldData CurNpcPos); a position outside the team made CurNpc throw KeyNotFound
            // in the client (live 20:05 log), so it is always the controlled member's index.
            writer.Write(player.BigWorldState.TrialAddMode == 0 ? 0 : player.BigWorldState.CurNpcPos);
            writer.Write(player.BigWorldState.TrialAddMode); // TrialNpcAddMode: ETrialNpcAddMode
            BigWorldXRpc.WriteValue(ref writer, BigWorldQuestModule.BuildRepInitialQuests(player));
            writer.WriteArrayHeader(3);
            for (int i = 0; i < 3; i++)
                writer.WriteArrayHeader(0);
            writer.WriteArrayHeader(2);
            for (int i = 0; i < 2; i++)
                writer.WriteArrayHeader(0);
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        // XNpc replication: RepComponent list = [snapshot bin | nil, ComponentName] in engine order. XActor.DeserializeOther
        // creates each named component and deserializes a non-nil snapshot; components whose formatter rejects nil must
        // carry one. Sources per AGENTS.md "BigWorld engine-default exception":
        //   state  - XNpcMoveComponent (saved position/facing)
        //   table  - XAttribComponent (DlcWorldAttrib row = Npc.AttribId), XFightScriptComponent (Npc.ScriptId),
        //            XPartComponent (Part rows)
        //   engine default - every other snapshot below: identical for every fresh spawn of a player-controlled XNpc in
        //            retail replication; backstage team members carry only the components before the first ActiveOnly
        //            entry (retail babylonia.pcap 979 entry and 1276 team sync: 16 components ending at XNpcAudioComponent).
        //            Listing the rest with nil crashes the client's replicate path (XVarComponent rejects nil).
        private static readonly (string Name, string? DefaultHex, bool ActiveOnly)[] NpcComponents =
        [
            ("XNpcFSMComponent", "92c40993ff00c404930002c001", false),
            ("XNpcTimeComponent", "94ca00000000ca00000000ca3f80000090", false),
            ("XAttribComponent", null, false),
            ("XNpcMoveComponent", null, false),
            // table: joint-hide counts from Npc.BornMagic (BuildModelSnapshot), not a constant.
            ("XNpcModelComponent", null, false),
            ("XEffectComponent", null, false),
            ("XBehaviorComponent", "960090808090948201010000000202000000800000", false),
            // ponytail: born-magic buffs (Npc.BornMagic) are not replicated; the engine starts with no buffs.
            ("XBuffComponent", "929000", false),
            ("XOnAirComponent", "93c200ca00000000", false),
            ("XFightScriptComponent", null, false),
            ("XNpcAnimatorComponent", null, false),
            ("XNpcColliderComponent", "91c2", false),
            ("XNpcNodeComponent", null, false),
            ("XNpcTriggerComponent", null, false),
            ("XNpcDeathComponent", "920000", false),
            ("XNpcAudioComponent", "91ca3f800000", false),
            ("XVarComponent", "93908080", true),
            ("XPartComponent", null, true),
            ("XThreatComponent", "95909090ca00000000c2", true),
            ("XParalysisComponent", null, false),
            ("XFocusComponent", null, false),
            ("XSkillActionComponent", "9200c0", true),
            ("XSkillComponent", "9190", true),
            ("XNpcSkillComponent", "9190", true),
            ("XHitComponent", "9ec2c2c2c2c404930000c0c404930000c092ca00000000ca0000000093ca00000000ca00000000ca00000000c200008201800280c290", true),
            ("XProtectorComponent", "93908080", true),
            ("XNpcResilienceComponent", "9294c200ca000000000094c200ca0000000000", true),
            ("XNpcRebootComponent", "95000090c2ff", true),
            ("XNpcInputControlComponent", null, false),
            ("XNpcSearchTargetComponent", "920101", true),
            ("XNpcLockTargetComponent", null, false),
            ("XInputHandleComponent", null, false),
            ("XNpcFightInfoRecorderComponent", "9700ca00000000ca0000000000000000", true),
        ];
        private static readonly int BackstageComponentCount = Array.FindIndex(NpcComponents, c => c.ActiveOnly);
        private static readonly Lazy<Dictionary<int, AscNet.Table.V2.share.statussyncfight.npc.NpcTable>> DlcNpcs = new(() =>
            TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.npc.NpcTable>().ToDictionary(r => r.Id));
        private static readonly Lazy<Dictionary<int, AscNet.Table.V2.share.statussyncfight.MagicTable>> FightMagics = new(() =>
            TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.MagicTable>().ToDictionary(r => r.Id));
        private static readonly Lazy<Dictionary<int, int>> BuffMagics = new(() =>
            TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.BuffTable>().ToDictionary(r => r.Id, r => r.Magic));

        // XRepNpcModelComponent.JointHideCountDict: hidden model joints -> hide count. HideJoint magics bump the count; the
        // engine runs them when Npc.BornMagic AddBuff magics add their buffs (Buff.Magic on add). Empty -> engine dict never
        // created -> [nil] (commandant). Retail parity: 3001 {Engine01,Engine02}, 3029 {SGHide}, 3031 {Chain}.
        // Level-instance DisableJointList (client ConfigGroup key 30; e.g. retail place 700004 EngineLeg) is not in LevelNpc.tsv.
        internal static Dictionary<string, int> HiddenJoints(AscNet.Table.V2.share.statussyncfight.npc.NpcTable config)
        {
            Dictionary<string, int> hidden = [];
            void Hide(int magicId)
            {
                if (!FightMagics.Value.TryGetValue(magicId, out var magic)) return;
                if (magic.Name == "HideJoint") hidden[magic.Level] = hidden.GetValueOrDefault(magic.Level) + 1;
            }
            foreach (int born in config.BornMagic.Where(id => id != 0).Distinct())
            {
                if (!FightMagics.Value.TryGetValue(born, out var magic)) continue;
                if (magic.Name == "AddBuff" && BuffMagics.Value.TryGetValue(int.Parse(magic.Level), out int buffMagic)) Hide(buffMagic);
                else Hide(born);
            }
            return hidden;
        }

        internal static byte[] BuildModelSnapshot(AscNet.Table.V2.share.statussyncfight.npc.NpcTable config) => Pack((ref MessagePackWriter w) =>
        {
            Dictionary<string, int> hidden = HiddenJoints(config);
            w.WriteArrayHeader(1);
            if (hidden.Count == 0) { w.WriteNil(); w.Flush(); return; }
            w.WriteMapHeader(hidden.Count);
            foreach ((string joint, int count) in hidden) { w.Write(joint); w.Write(count); }
            w.Flush();
        });
        private static readonly Lazy<ILookup<int, AscNet.Table.V2.share.statussyncfight.part.PartTable>> DlcParts = new(() =>
            TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.part.PartTable>().ToLookup(r => r.Npc));
        private static readonly Lazy<Dictionary<int, AscNet.Table.V2.share.dlcworld.DlcWorldAttribTable>> DlcAttribs = new(() =>
            TableReaderV2.Parse<AscNet.Table.V2.share.dlcworld.DlcWorldAttribTable>().ToDictionary(r => r.Id));
        // DlcWorldAttrib's shipped column order is ENpcAttrib order (148 attributes, verified against XDlcFightEnum.lua).
        private static readonly Lazy<(string Name, System.Reflection.PropertyInfo? Property)[]> AttribColumns = new(() =>
            File.ReadLines(JsonSnapshot.ResolvePath(AscNet.Table.V2.share.dlcworld.DlcWorldAttribTable.File)).First()
                .Split('\t').Skip(1)
                .Select(name => (name, typeof(AscNet.Table.V2.share.dlcworld.DlcWorldAttribTable).GetProperty(name)))
                .ToArray());
        private static readonly HashSet<int> CommandantNpcIds = TableReaderV2
            .Parse<AscNet.Table.V2.share.bigworld.common.character.BigWorldCharacterTable>()
            .Where(r => r.IsCommandant == 1).Select(r => r.NpcId).ToHashSet();

        // pose: where the team stands (level switch target); null = the saved open-world pose.
        internal static byte[] BuildNpcComponents(Player player, Theatre5WorldNpcData npc, int actorUuid, bool isBackstage,
            (BigWorldVector3 Position, double? RotationY)? pose = null)
        {
            AscNet.Table.V2.share.statussyncfight.npc.NpcTable config = DlcNpcs.Value.TryGetValue(npc.Id, out var row)
                ? row : throw new InvalidDataException($"BigWorld NPC {npc.Id} has no statussyncfight Npc row.");
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            // NPCs with NeedFightLogicType 1 (the commandants 3004/3005) run fight logic even backstage: XNpcMoveComponent
            // .ControllerMove and XNpc.SwitchOut -> AbortSkill call XNpcFSMComponent.IsNpcDoSkill, which dereferences
            // XSkillActionComponent [DUMP48 0x1D84540 get_NeedFightLogic 0x1D6C680; live log: actor NRE every frame +
            // RpcSwitchPlayerNpc NRE in SwitchOut]. They keep the full component list; other members use the 16-component
            // backstage layout (retail 979/1276).
            int count = isBackstage && config.NeedFightLogicType != 1 ? BackstageComponentCount : NpcComponents.Length;
            writer.WriteArrayHeader(count);
            foreach ((string name, string? defaultHex, _) in NpcComponents.Take(count))
            {
                writer.WriteArrayHeader(2);
                byte[]? snapshot = name switch
                {
                    "XAttribComponent" => BuildAttribSnapshot(config.AttribId),
                    "XNpcMoveComponent" => BuildMoveSnapshot(pose ?? CurrentPose(player.BigWorldState)),
                    "XFightScriptComponent" => BuildFightScriptSnapshot(config.ScriptId),
                    "XNpcAnimatorComponent" => BuildAnimatorSnapshot(CommandantNpcIds.Contains(npc.Id)),
                    "XPartComponent" => BuildPartSnapshot(config, actorUuid),
                    "XNpcModelComponent" => BuildModelSnapshot(config),
                    _ => defaultHex is null ? null : Convert.FromHexString(defaultHex),
                };
                if (snapshot is null) writer.WriteNil(); else writer.Write(snapshot);
                writer.Write(name);
            }
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        // The team's saved pose in its current level: the instance pose inside an instance (a resumed instance otherwise put
        // the team at open-world coordinates: live 4008 spawned in the sky), else the open-world pose.
        internal static (BigWorldVector3 Position, double? RotationY)? CurrentPose(BigWorldPlayerState state) =>
            state.InstLevelId != 0
                ? state.InstPosition is { } inst ? (inst, state.InstRotationY) : null
                : state.LastPosition is { } last ? (last, state.LastRotationY) : null;

        internal delegate void PackBody(ref MessagePackWriter writer);

        internal static byte[] Pack(PackBody body)
        {
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            body(ref writer);
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        // [Position, Rotation quaternion, nil, nil, 0.0]; nil snapshot before the first saved position (BornData spawns).
        private static byte[]? BuildMoveSnapshot((BigWorldVector3 Position, double? RotationY)? pose)
        {
            if (pose is not { } value)
                return null;
            (BigWorldVector3 pos, double? rotationY) = value;
            double halfYaw = (rotationY ?? 0) * Math.PI / 360;
            return Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(5);
                w.WriteArrayHeader(3);
                w.Write((float)pos.X); w.Write((float)pos.Y); w.Write((float)pos.Z);
                w.WriteArrayHeader(4);
                w.Write(0f); w.Write((float)Math.Sin(halfYaw)); w.Write(0f); w.Write((float)Math.Cos(halfYaw));
                w.WriteNil(); w.WriteNil(); w.Write(0f);
                w.Flush();
            });
        }

        // [{3: {ScriptId: [ScriptId, 3, nil]}}, {3: {ScriptId: true}}]; slot key 3 is the engine's script slot (engine
        // default); no script -> [{}, {}] as retail sends for script-less NPCs.
        private static byte[] BuildFightScriptSnapshot(int scriptId) => Pack((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(2);
            if (scriptId <= 0) { w.WriteMapHeader(0); w.WriteMapHeader(0); w.Flush(); return; }
            w.WriteMapHeader(1); w.Write(3); w.WriteMapHeader(1); w.Write(scriptId);
            w.WriteArrayHeader(3); w.Write(scriptId); w.Write(3); w.WriteNil();
            w.WriteMapHeader(1); w.Write(3); w.WriteMapHeader(1); w.Write(scriptId); w.Write(true);
            w.Flush();
        });

        // [0, "Stand1", 0.0, {speed scales}] - engine defaults; commandants also carry the first-person scales.
        private static byte[] BuildAnimatorSnapshot(bool commandant) => Pack((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(4);
            w.Write(0); w.Write("Stand1"); w.Write(0f);
            string[] scales = commandant
                ? ["WalkSpeed", "RunSpeed", "FPRunSpeed", "FPWalkSpeed", "FPSprintSpeed"]
                : ["WalkSpeed", "RunSpeed"];
            w.WriteMapHeader(scales.Length);
            foreach (string scale in scales) { w.Write(scale); w.Write(1f); }
            w.Flush();
        });

        // [[[PartId, PartUuid, []] ...], [], []]. Part UUID = actor UUID << 8 | (index + 1) (retail: 991 -> 253697).
        // Part rows are keyed by NPC id; the commandant has none and retail uses the rows keyed by its AttribId
        // (1002 -> parts 0 and 100201) - [INFERENCE], same fallback applied to every NPC without own rows.
        private static byte[] BuildPartSnapshot(AscNet.Table.V2.share.statussyncfight.npc.NpcTable npc, int actorUuid)
        {
            var rows = DlcParts.Value[npc.Id].Any() ? DlcParts.Value[npc.Id] : DlcParts.Value[npc.AttribId];
            List<int> partIds = rows.Select(r => r.PartId).ToList();
            return Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(3);
                w.WriteArrayHeader(partIds.Count);
                for (int i = 0; i < partIds.Count; i++)
                {
                    w.WriteArrayHeader(3); w.Write(partIds[i]); w.Write((actorUuid << 8) | (i + 1)); w.WriteArrayHeader(0);
                }
                w.WriteArrayHeader(0); w.WriteArrayHeader(0);
                w.Flush();
            });
        }

        // XAttribComponent: raw binary, one 21-byte record per ENpcAttrib (16 zero bytes, int32 LE value, AllowNegative
        // byte), then one int32 per ENpcEnergy (XEnergy.SIZE = 4). Values come from the DlcWorldAttrib row: integer
        // columns raw, float columns as round(value * 1000) (verified on all 105 records of the retail 4.5 snapshot
        // against row 1002).
        private const string AttribFlags105 = // AllowNegative for ENpcAttrib 0..104 as retail replicates it (engine template)
            "011111111111111111111111111110000111111100000111000000000000110000011111111111111111110110111111101111111";
        // Attributes added after the 4.5 capture: non-negative per the client's AttrNonnegative set
        // (matrix/xmanager/xdlchuntattrmanager.lua); every other new attribute allows negatives.
        private static readonly HashSet<string> NewNonNegativeAttribs = ["HeavyEnergy"];
        // ENpcEnergy order (dump.cs D:678246, 12 entries) plus HeavyEnergy, the only energy-type attribute 4.8 added
        // [INFERENCE: appended]. Initial energy = attribute value for the entries retail spawns full, else 0 (engine
        // default observed in the 4.5 snapshot: Life, Dodge, Jump and custom groups full; the rest empty).
        private static readonly (int Attrib, bool StartsFull)[] NpcEnergies =
        [
            (0, true), (40, false), (41, false), (44, true), (45, true), (48, true), (49, true), (50, true), (51, true),
            (86, false), (89, false), (97, false), (137, false),
        ];
        private static byte[] BuildAttribSnapshot(int attribId)
        {
            var row = DlcAttribs.Value.TryGetValue(attribId, out var found)
                ? found : throw new InvalidDataException($"DlcWorldAttrib {attribId} does not exist.");
            var columns = AttribColumns.Value;
            byte[] block = new byte[columns.Length * 21 + NpcEnergies.Length * 4];
            int[] values = new int[columns.Length];
            for (int i = 0; i < columns.Length; i++)
            {
                object? raw = columns[i].Property?.GetValue(row);
                values[i] = raw is double or float
                    ? (int)Math.Round(Convert.ToDouble(raw) * 1000, MidpointRounding.AwayFromZero)
                    : Convert.ToInt32(raw ?? 0);
                BitConverter.TryWriteBytes(block.AsSpan(i * 21 + 16), values[i]);
                block[i * 21 + 20] = (byte)(i < AttribFlags105.Length ? AttribFlags105[i] - '0' : NewNonNegativeAttribs.Contains(columns[i].Name) ? 0 : 1);
            }
            int tail = columns.Length * 21;
            for (int i = 0; i < NpcEnergies.Length; i++)
                BitConverter.TryWriteBytes(block.AsSpan(tail + i * 4), NpcEnergies[i].StartsFull ? values[NpcEnergies[i].Attrib] : 0);
            return block;
        }

        // XNpcTravelInfo.Energies: the initial energy record of the NPC's attribute row (tail of its XAttribComponent snapshot).
        internal static List<int> InitialEnergies(int npcId)
        {
            byte[] block = BuildAttribSnapshot(DlcNpcs.Value[npcId].AttribId);
            int tail = block.Length - NpcEnergies.Length * 4;
            return Enumerable.Range(0, NpcEnergies.Length).Select(i => BitConverter.ToInt32(block, tail + i * 4)).ToList();
        }

        // RepLevel (dump.cs D:764482): [LevelId, [player RepController], XRpcActorReplicateList, ServerControllerData,
        // MasterControllerId] + the installed client's level-module list (retail key 5). Replicates follow retail creation
        // order: XSceneObjectManager and its scene objects, the other level managers (XLevel.PostEnterLevel resolves its
        // scene config groups through them), then the player's team NPCs.
        // Level NPCs follow the scene objects (default-loaded NPCs only; deferred ones need the script runtime).
        // pose: team position for a level switch (the switch target); null = saved open-world pose (world entry).
        internal static byte[] BuildRepLevel(Session session, int levelId, List<Theatre5WorldNpcData> npcs,
            (BigWorldVector3 Position, double? RotationY)? pose = null)
        {
            Player player = session.player;
            int playerId = checked((int)player.PlayerData.Id);
            List<LevelSceneObjectTable> sceneObjects = ReplicatedSceneObjects(player, levelId);
            List<LevelNpcTable> levelNpcs = ReplicatedLevelNpcs(player, levelId);
            int SceneObjectUuid(int index) => ServerUuid(ManagerSequenceCount + npcs.Count + index + 1);
            int LevelNpcUuid(int index) => ServerUuid(ManagerSequenceCount + npcs.Count + sceneObjects.Count + index + 1);
            int lastSequence = ManagerSequenceCount + npcs.Count + sceneObjects.Count + levelNpcs.Count;
            BigWorldActors.OnLevelSnapshot(session, levelId, lastSequence,
                sceneObjects.Select((so, i) => (so.PlaceId, SceneObjectUuid(i))),
                levelNpcs.Select((npc, i) => (npc.PlaceId, LevelNpcUuid(i))));
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            writer.WriteArrayHeader(6);
            BigWorldXRpc.WriteInt(ref writer, levelId);
            writer.WriteArrayHeader(1);
            WriteRepController(ref writer, levelId, isMaster: true, BigWorldXRpc.PlayerControllerId, playerId, incId: 0, player.PlayerData.Name);

            writer.WriteArrayHeader(1); // XRpcActorReplicateList [RpcActorReplicateList]
            string? gameplay = GameplayActors.Value.GetValueOrDefault(levelId);
            writer.WriteArrayHeader(4 + (gameplay is null ? 0 : 1) + sceneObjects.Count + levelNpcs.Count + npcs.Count);
            int sceneObjectManager = ServerUuid(1), gameplayManager = ServerUuid(2), playTimer = ServerUuid(3), gameplayUuid = GameplayUuid;
            // XSceneObjectManager [SceneObjectUuids]
            WriteManagerReplicate(ref writer, "XSceneObjectManager", levelId, sceneObjectManager, lastSequence,
                Pack((ref MessagePackWriter w) =>
                {
                    w.WriteArrayHeader(1);
                    w.WriteArrayHeader(sceneObjects.Count);
                    for (int i = 0; i < sceneObjects.Count; i++)
                        w.Write(SceneObjectUuid(i));
                }), EmptyComponentList);
            for (int i = 0; i < sceneObjects.Count; i++)
                writer.WriteRaw(BuildSceneObjectReplicate(player, levelId, sceneObjects[i], SceneObjectUuid(i), BigWorldXRpc.PlayerControllerId, 0));
            for (int i = 0; i < levelNpcs.Count; i++)
                writer.WriteRaw(BuildLevelNpcReplicate(player, levelId, levelNpcs[i], LevelNpcUuid(i), BigWorldXRpc.PlayerControllerId, 0));
            // XGameplayManager [[GameplayUuids]]
            WriteManagerReplicate(ref writer, "XGameplayManager", levelId, gameplayManager, lastSequence,
                Pack((ref MessagePackWriter w) =>
                {
                    w.WriteArrayHeader(1);
                    w.WriteArrayHeader(gameplay is null ? 0 : 1);
                    if (gameplay is not null) w.Write(gameplayUuid);
                }), EmptyComponentList);
            // XLevelPlayTimer [Time, State, IsPaused, Elapsed] - engine start-up state.
            WriteManagerReplicate(ref writer, "XLevelPlayTimer", levelId, playTimer, lastSequence,
                Convert.FromHexString("94ca0000000000c2ca00000000"), EmptyComponentList);
            // XLevelProcess [LevelId, SceneObjectManagerUuid, (empty level-process state x9), GameplayManagerUuid, PlayTimerUuid]
            WriteManagerReplicate(ref writer, "XLevelProcess", levelId, LevelProcessUuid, lastSequence,
                Pack((ref MessagePackWriter w) =>
                {
                    w.WriteArrayHeader(13);
                    w.Write(levelId); w.Write(sceneObjectManager);
                    w.WriteMapHeader(0); w.WriteMapHeader(0); w.WriteMapHeader(0); w.WriteMapHeader(0);
                    w.WriteArrayHeader(0); w.WriteMapHeader(0); w.WriteMapHeader(0); w.Write(0); w.WriteMapHeader(0);
                    w.Write(gameplayManager); w.Write(playTimer);
                }),
                // [Effect nil, Var [[],{},{}], LevelAudio [1.0]] - engine start-up state.
                Convert.FromHexString("9392c0b058456666656374436f6d706f6e656e7492c40493908080ad58566172436f6d706f6e656e7492c40691ca3f800000b4584c6576656c417564696f436f6d706f6e656e74"));
            // Serialization is AGameplayBigWorldActor's (no subclass override): nil content, no components, as retail's
            // XGameplayBigWorldMain in 4001. The client-side gameplay builds its own content: XGameplayDormitory creates the
            // photo wall / frame wall / photos / adorns / goods itself as local-only scene objects (placeId 0) from
            // XSkyGardenDormAgency:OnFightGetGamePlayData [DUMP48 XSceneObjectGenerator.Generate isOnlyLocal=1 at
            // 0x1BFA554, 0x1BF9DDA, 0x1BFAADF, 0x1BFB389, 0x1BFB923], so the server replicates none of them.
            if (gameplay is not null)
                WriteManagerReplicate(ref writer, gameplay, levelId, gameplayUuid, lastSequence, null, EmptyComponentList);

            for (int index = 0; index < npcs.Count; index++)
            {
                writer.WriteArrayHeader(10);
                writer.Write("XNpc");
                writer.Write(BuildRepNpc(npcs[index], isBackstage: index != player.BigWorldState.CurNpcPos));
                writer.Write(BigWorldXRpc.PlayerControllerId);
                writer.Write(false); // IsAI
                BigWorldXRpc.WriteInt(ref writer, levelId);
                BigWorldXRpc.WriteInt(ref writer, PlayerNpcUuid(index));
                writer.Write(0); // ParentUUID
                writer.Write(BuildNpcComponents(player, npcs[index], PlayerNpcUuid(index), isBackstage: index != player.BigWorldState.CurNpcPos, pose));
                writer.Write(0); // ControllerIncId
                BigWorldXRpc.WriteInt(ref writer, playerId);
            }

            WriteRepController(ref writer, levelId, isMaster: false, BigWorldXRpc.ServerControllerId, playerId: 0, incId: lastSequence, name: null);
            writer.Write(MasterControllerId);
            // Level modules: XLevelClearPlaceModule with no executed clear-place records (AscNet does not run level actions).
            writer.WriteArrayHeader(1);
            writer.WriteArrayHeader(2);
            writer.Write("XLevelClearPlaceModule");
            writer.Write(MessagePackSerializer.Serialize(new Dictionary<string, int[]> { ["ExecutedClearPlaceRecords"] = [] }));
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        // XGameplay*.NeedCreate(levelId): each BigWorld gameplay actor exists only in the level(s) its config names
        // (GameplayLevelId / GameplayLevelIdsStr). Retail 4001 replicates XGameplayBigWorldMain; the lounge (4003) needs
        // XGameplayDormitory, whose client half loads the room skin (DormitorySkins.LevelGroupId). The configs name
        // disjoint levels, so a level has at most one (manager sequence 4).
        private static readonly Lazy<Dictionary<int, string>> GameplayActors = new(() =>
        {
            (string Actor, IEnumerable<(string Key, string Value)> Rows)[] configs =
            [
                ("XGameplayBigWorldMain", TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.gameplay.bigworldmain.BigWorldMainConfigTable>().Select(r => (r.Key, $"{r.Value}"))),
                ("XGameplayDormitory", TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.gameplay.dormitory.DormitoryConfigTable>().Select(r => (r.Key, $"{r.Value}"))),
                ("XGameplayCafe", TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.gameplay.cafe.ConfigTable>().Select(r => (r.Key, $"{r.Value}"))),
                ("XGameplayShoppingStreet", TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.gameplay.sgstreet.SgStreetConfigTable>().Select(r => (r.Key, $"{r.Value}"))),
                ("XGameplayUAV", TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.gameplay.sguav.SkyGardenUAVConfigTable>().Select(r => (r.Key, $"{r.Value}"))),
                ("XGameplayHeavyArtilleryFire", TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.gameplay.sgheavyartilleryfire.SgHeavyArtilleryFireConfigTable>().Select(r => (r.Key, $"{r.Value}"))),
                ("XGameplayShoppingStreetShow", TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.gameplay.sgstreetshow.SgStreetShowConfigTable>().Select(r => (r.Key, $"{r.Value}"))),
            ];
            Dictionary<int, string> byLevel = new();
            foreach ((string actor, IEnumerable<(string Key, string Value)> rows) in configs)
                foreach ((string key, string value) in rows.Where(r => r.Key is "GameplayLevelId" or "GameplayLevelIdsStr"))
                    foreach (string level in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
                        if (!byLevel.TryAdd(int.Parse(level), actor))
                            throw new InvalidDataException($"Level {level} has two BigWorld gameplay actors ({byLevel[int.Parse(level)]}, {actor}).");
            return byLevel;
        });

        // Server-controlled replicate: [Name, Content, ControllerId 15, IsAI, LevelId, UUID, ParentUUID, Components,
        // ControllerIncId (server controller's last sequence), PlayerId 0].
        private static void WriteManagerReplicate(ref MessagePackWriter writer, string name, int levelId, int uuid, int incId, byte[]? content, byte[] components)
        {
            writer.WriteArrayHeader(10);
            writer.Write(name);
            if (content is null) writer.WriteNil(); else writer.Write(content);
            writer.Write(BigWorldXRpc.ServerControllerId);
            writer.Write(true);
            BigWorldXRpc.WriteInt(ref writer, levelId);
            BigWorldXRpc.WriteInt(ref writer, uuid);
            writer.Write(0);
            writer.Write(components);
            writer.Write(incId);
            writer.Write(0);
        }

        private static readonly Lazy<Dictionary<int, AscNet.Table.V2.share.statussyncfight.sceneobject.SceneObjectBaseTable>> SceneObjectBases = new(() =>
            TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.sceneobject.SceneObjectBaseTable>().ToDictionary(r => r.Id));
        private static readonly Lazy<HashSet<int>> BasesWithActions = new(() =>
            TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.sceneobject.SceneObjectActionTable>().Select(r => r.SceneObjectBase).ToHashSet());

        // Server-owned scene objects of the level: its config groups' objects that the client does not create itself
        // (IsOnlyClient; records whose IsOnlyClient could not be recovered (-1) are not replicated), minus collected
        // collectables. Initially loaded: not DeferLoading; level actions (LoadSceneObject/UnloadSceneObject, persisted as
        // actor overrides) override that default. Reproduces the retail level 4001 replication for a fresh player.
        private static List<LevelSceneObjectTable> ReplicatedSceneObjects(Player player, int levelId)
        {
            HashSet<int> collected = player.BigWorldState.ClaimedSceneObjects.Where(c => c.LevelId == levelId).Select(c => c.PlaceId).ToHashSet();
            return SceneObjectsOf(levelId).Values
                .Where(so => so.IsOnlyClient == 0 && !(so.CollectRewardId > 0 && collected.Contains(so.PlaceId))
                    && (BigWorldActors.LoadedOverride(player.BigWorldState, levelId, BigWorldActors.SceneObjectType, so.PlaceId) ?? so.DeferLoading == 0))
                .ToList();
        }

        // XSceneObject RepComponent list in engine order. Presence follows the object's config (teleporter, interactable,
        // collectable, space audio) and its base's actions; snapshots are the model (SceneObjectBase.Model), the placement
        // (config position/rotation) and engine start-up state.
        private static byte[] BuildSceneObjectComponents(LevelSceneObjectTable so, bool interactable, bool beScanned)
        {
            var baseRow = SceneObjectBases.Value[so.BaseId];
            bool teleporter = so.IsTeleporter == 1;
            List<(string Name, byte[]? Snapshot)> components =
            [
                ("XTimeComponent", Convert.FromHexString("94ca00000000ca00000000ca3f80000090")),
                ("XSceneObjectNodeComponent", null),
                // [State, Animation ("Born"; teleporters drive their own), Time, Params]
                ("XSceneObjectAnimatorComponent", Pack((ref MessagePackWriter w) =>
                {
                    w.WriteArrayHeader(4); w.Write(0);
                    if (teleporter) w.WriteNil(); else w.Write("Born");
                    w.Write(0f); w.WriteMapHeader(0);
                })),
                ("XSceneObjectModelComponent", Pack((ref MessagePackWriter w) => { w.WriteArrayHeader(2); w.Write(baseRow.Model); w.Write(true); })),
                ("XSceneObjectMoveComponent", BuildSceneObjectMoveSnapshot(so)),
                ("XSceneObjectColliderComponent", null),
                ("XEffectComponent", null),
                ("XFightScriptComponent", BuildFightScriptSnapshot(baseRow.ScriptId ?? 0)),
                teleporter ? ("XSceneObjectTeleporterComponent", null) : ("XSceneObjectStateComponent", Convert.FromHexString("9101")),
            ];
            if (BasesWithActions.Value.Contains(so.BaseId))
                components.Add(("XSceneObjectActionComponent", Convert.FromHexString("9100")));
            components.Add(("XSceneObjectTriggerComponent", null));
            if (so.Interactable == 1)
                components.Add(("XSceneObjectInteractableComponent", Convert.FromHexString(interactable ? "91c3" : "91c2")));
            if (so.CollectWay > 0) // the config's CollectableComponent: reward chests and reward-less jumper coins (CollectWay 2)
                components.Add(("XSceneObjectCollectableComponent", null));
            // RepBeScannedComponent [CurBeScanned]. Presence follows the config's BeScannedComponent: every retail 4001 chest
            // carries the treasure-box variant ([false]) and no other 4001 object has one (LevelSceneObject.BeScanned matches
            // all 59 rows); other objects use XSceneObjectBeScannedComponent with the same base-class snapshot. Position after
            // Collectable is AscNet policy (no retail sample of the object variant; the client adds components by name).
            if (so.BeScanned == 1)
                components.Add((so.CollectRewardId > 0 ? "XSceneTreasureBoxBeScannedComponent" : "XSceneObjectBeScannedComponent",
                    Convert.FromHexString(beScanned ? "91c3" : "91c2")));
            if (so.SpaceAudio == 1)
                components.Add(("XSpaceAudioComponent", null));
            return Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(components.Count);
                foreach ((string name, byte[]? snapshot) in components)
                {
                    w.WriteArrayHeader(2);
                    if (snapshot is null) w.WriteNil(); else w.Write(snapshot);
                    w.Write(name);
                }
            });
        }

        // Euler (Unity order Z, X, Y: yaw * pitch * roll) -> quaternion [X, Y, Z, W] in float32 arithmetic like the engine,
        // with correctly rounded sin/cos (MathF is 1 ulp off) and signed zeros normalised to +0 as retail sends them
        // (bit-identical to all 50 retail level 4001 scene objects; double arithmetic matched only 32).
        internal static float[] EulerToQuaternion(double x, double y, double z)
        {
            const float halfDegree = MathF.PI / 180f * 0.5f;
            float hy = (float)y * halfDegree, hp = (float)x * halfDegree, hr = (float)z * halfDegree;
            static float Cos(float a) => (float)Math.Cos(a);
            static float Sin(float a) => (float)Math.Sin(a);
            float cy = Cos(hy), sy = Sin(hy), cp = Cos(hp), sp = Sin(hp), cr = Cos(hr), sr = Sin(hr);
            return
            [
                cy * sp * cr + sy * cp * sr + 0f,
                sy * cp * cr - cy * sp * sr + 0f,
                cy * cp * sr - sy * sp * cr + 0f,
                cy * cp * cr + sy * sp * sr + 0f,
            ];
        }

        // [Position, Rotation, 0, false, false, SpawnPosition, SpawnRotation, -1, -1, 0.0] from the config placement.
        private static byte[] BuildSceneObjectMoveSnapshot(LevelSceneObjectTable so)
        {
            float[] q = EulerToQuaternion(so.RotX, so.RotY, so.RotZ);
            return Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(10);
                for (int copy = 0; copy < 2; copy++)
                {
                    w.WriteArrayHeader(3); w.Write((float)so.PosX); w.Write((float)so.PosY); w.Write((float)so.PosZ);
                    w.WriteArrayHeader(4); w.Write(q[0]); w.Write(q[1]); w.Write(q[2]); w.Write(q[3]);
                    if (copy == 0) { w.Write(0); w.Write(false); w.Write(false); }
                }
                w.Write(-1); w.Write(-1); w.Write(0f);
            });
        }

        private static readonly Lazy<ILookup<(string, int), LevelNpcTable>> LevelNpcsByGroup = new(() =>
            TableReaderV2.Parse<LevelNpcTable>().ToLookup(r => (r.Sector, r.ConfigGroupId)));
        private static readonly Lazy<Dictionary<int, AscNet.Table.V2.share.statussyncfight.npc.LevelNpcBaseTable>> LevelNpcBases = new(() =>
            TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.npc.LevelNpcBaseTable>().ToDictionary(r => r.Id));

        // NPCs of the level's config groups the client does not create itself (IsOnlyClient). Initially loaded: not deferred.
        // Level actions (LoadNpc/UnloadNpc, persisted as actor overrides) override the table default either way.
        private static List<LevelNpcTable> ReplicatedLevelNpcs(Player player, int levelId)
        {
            if (!Levels.Value.TryGetValue(levelId, out AscNet.Table.V2.share.statussyncfight.level.LevelTable? level))
                return [];
            return level.ConfigGroups.SelectMany(groupId => LevelNpcsByGroup.Value[GroupKey(level, groupId)])
                .GroupBy(npc => npc.PlaceId).Select(group => group.First())
                .Where(npc => npc.IsOnlyClient == 0
                    && (BigWorldActors.LoadedOverride(player.BigWorldState, levelId, BigWorldActors.NpcType, npc.PlaceId) ?? npc.DeferLoading == 0))
                .ToList();
        }

        internal static LevelNpcTable? LevelNpcOf(int levelId, int placeId) =>
            Levels.Value.TryGetValue(levelId, out AscNet.Table.V2.share.statussyncfight.level.LevelTable? level)
                ? level.ConfigGroups.SelectMany(groupId => LevelNpcsByGroup.Value[GroupKey(level, groupId)]).FirstOrDefault(npc => npc.PlaceId == placeId)
                : null;

        // Server-controlled XRpcActorReplicate / enter-snapshot entry of a level NPC:
        // [Name, Content, ControllerId, IsAI, LevelId, UUID, ParentUUID, Components, ControllerIncId, PlayerId].
        // Enter snapshot: player-controlled replica (controller 1, IsAI, inc 0). Level-action LoadNpc: server controller 15,
        // ControllerIncId = the uuid's sequence, followed by RpcActorChangeController to 1 (retail 1574/1572).
        internal static byte[] BuildLevelNpcReplicate(Player player, int levelId, LevelNpcTable npc, int uuid, int controllerId, int incId)
        {
            var levelBase = LevelNpcBases.Value[npc.BaseId];
            var fightNpc = FightNpcOf(levelBase, $"Level NPC {npc.PlaceId}");
            bool interactable = BigWorldActors.InteractableOverride(player.BigWorldState, levelId, BigWorldActors.NpcType, npc.PlaceId) ?? true;
            (BigWorldVector3 Position, BigWorldVector4 Rotation)? pose = BigWorldActors.NpcPose(player.BigWorldState, levelId, npc.PlaceId);
            Placement placement = pose is { } tracked
                ? new([(float)tracked.Rotation.X, (float)tracked.Rotation.Y, (float)tracked.Rotation.Z, (float)tracked.Rotation.W],
                    tracked.Position.X, tracked.Position.Y, tracked.Position.Z, npc.ScriptId, npc.DefaultAction, npc.DisableGravity == 1, npc.CanInteract == 1)
                : new(EulerToQuaternion(npc.RotX, npc.RotY, npc.RotZ), npc.PosX, npc.PosY, npc.PosZ, npc.ScriptId, npc.DefaultAction, npc.DisableGravity == 1, npc.CanInteract == 1);
            return Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(10);
                w.Write("XNpc");
                w.Write(BuildLevelRepNpc(npc, levelBase, BigWorldActionHandlers.NpcTipIconQuestId(player.BigWorldState, levelId, npc.PlaceId)));
                w.Write(controllerId);
                w.Write(BigWorldActionHandlers.ActorAiEnabled(player.BigWorldState, levelId, BigWorldActors.NpcType, npc.PlaceId)); // IsAI (SetActorAIEnabled)
                BigWorldXRpc.WriteInt(ref w, levelId);
                BigWorldXRpc.WriteInt(ref w, uuid);
                w.Write(0);
                w.Write(BuildLevelNpcComponents(uuid, placement, fightNpc, interactable, idle: false,
                    BigWorldActionHandlers.NpcFollowSave(player.BigWorldState, levelId, npc.PlaceId),
                    BigWorldActionHandlers.NpcFollowPaused(player.BigWorldState, levelId, npc.PlaceId),
                    BigWorldActionHandlers.NpcGuideSave(player.BigWorldState, levelId, npc.PlaceId)));
                w.Write(incId);
                w.Write(0);
            });
        }

        private static AscNet.Table.V2.share.statussyncfight.npc.NpcTable FightNpcOf(AscNet.Table.V2.share.statussyncfight.npc.LevelNpcBaseTable levelBase, string what) =>
            DlcNpcs.Value.TryGetValue(levelBase.FightNpcId, out var found)
                ? found : throw new InvalidDataException($"{what}: FightNpcId {levelBase.FightNpcId} has no Npc row.");

        // Static BoxColliders (rocks, cliffs, boundary walls) of a level's scene, keyed (Sector, scene file = Level.Prefab's name):
        // the solid geometry the status-sync simulation collides with [DUMP48 share/statussyncfight/scenecollider]. Terrain is not in it.
        private static readonly Lazy<ILookup<(string, string), AscNet.Table.V2.share.statussyncfight.scenecollider.SceneColliderTable>> SceneBoxes = new(() =>
            TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.scenecollider.SceneColliderTable>().ToLookup(r => (r.Sector, r.Scene)));

        // True when the world point lies within `margin` of any static box of the level's scene (the box's half extents are Size * Scale / 2,
        // rotated by its quaternion). A character spawned there stands in rock or in the foot of a cliff and cannot be hit.
        internal static bool InSceneSolid(int levelId, double x, double y, double z, double margin)
        {
            var level = Levels.Value[levelId];
            foreach (var box in SceneBoxes.Value[((level.SectorName ?? string.Empty).ToLowerInvariant(), Path.GetFileNameWithoutExtension(level.Prefab))])
            {
                // Offset into box space: rotate by the inverse (conjugate) quaternion, v + w t + q x t with t = 2 q x v.
                double vx = x - box.PosX, vy = y - box.PosY, vz = z - box.PosZ, qx = -box.QuatX, qy = -box.QuatY, qz = -box.QuatZ;
                double tx = 2 * (qy * vz - qz * vy), ty = 2 * (qz * vx - qx * vz), tz = 2 * (qx * vy - qy * vx);
                double lx = vx + box.QuatW * tx + (qy * tz - qz * ty), ly = vy + box.QuatW * ty + (qz * tx - qx * tz), lz = vz + box.QuatW * tz + (qx * ty - qy * tx);
                if (Math.Abs(lx) <= Math.Abs(box.SizeX * box.ScaleX) / 2 + margin && Math.Abs(ly) <= Math.Abs(box.SizeY * box.ScaleY) / 2 + margin
                    && Math.Abs(lz) <= Math.Abs(box.SizeZ * box.ScaleZ) / 2 + margin)
                    return true;
            }
            return false;
        }

        // ENpcCampType.Camp2: the monster camp of level-spawned fighters (Level_90005_Logic.lua GenerateNpc(monsterId, Camp2) next to the
        // player's Camp1; ENpcCampType values are bit flags, Camp1 = 1, Camp2 = 2). The shipped NpcCampRelationship table is empty, so
        // XNpcUtility.CheckTargetType treats every camp sharing no bit with the player's as enemy [DUMP48 0x1D9D100].
        private const int EnemyCamp = 2;

        // A KillEnemyGroup member (DlcQuestObjective NpcBaseId = LevelNpcBase.Id): no LevelNpc placement, so no level instance id, script
        // override, default action or interaction; hostile camp; the template's fight layout and idle FSM. Server controller like a
        // LoadNpc replicate (the caller follows with RpcActorChangeController to the player's client, which simulates it).
        internal static byte[] BuildEnemyReplicate(int levelId, int levelBaseId, (double X, double Y, double Z) position, double yawDegrees, int uuid, int incId)
        {
            var levelBase = LevelNpcBases.Value[levelBaseId];
            var fightNpc = FightNpcOf(levelBase, $"Enemy NPC base {levelBaseId}");
            Placement placement = new(EulerToQuaternion(0, yawDegrees, 0), position.X, position.Y, position.Z, 0, null, false, false);
            return Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(10);
                w.Write("XNpc");
                w.Write(BuildRepNpc(null, levelBase.FightNpcId, levelBase.Id, 0, EnemyCamp, isPlayer: false, isBackstage: false, levelBase.RolePartModelIds, actorSignificance: 1));
                w.Write(BigWorldXRpc.ServerControllerId);
                w.Write(true); // IsAI
                BigWorldXRpc.WriteInt(ref w, levelId);
                BigWorldXRpc.WriteInt(ref w, uuid);
                w.Write(0);
                w.Write(BuildLevelNpcComponents(uuid, placement, fightNpc, interactable: false, idle: true, null, false, null));
                w.Write(incId);
                w.Write(0);
            });
        }

        internal static byte[] BuildSceneObjectReplicate(Player player, int levelId, LevelSceneObjectTable so, int uuid, int controllerId, int incId)
        {
            HashSet<int> activatedTeleporters = player.BigWorldState.TeleporterData.TryGetValue(levelId, out List<int>? activated) ? [.. activated] : [];
            bool interactable = BigWorldActors.InteractableOverride(player.BigWorldState, levelId, BigWorldActors.SceneObjectType, so.PlaceId) ?? true;
            return Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(10);
                w.Write("XSceneObject");
                // [BaseId, PlaceId, -1, Flags, 1]; flag 2 marks an activated teleporter (engine defaults otherwise).
                w.Write(Pack((ref MessagePackWriter c) =>
                {
                    c.WriteArrayHeader(5);
                    c.Write(so.BaseId); c.Write(so.PlaceId); c.Write(-1);
                    if (so.IsTeleporter == 1 && activatedTeleporters.Contains(so.PlaceId)) { c.WriteArrayHeader(1); c.Write(2); }
                    else c.WriteArrayHeader(0);
                    c.Write(1);
                }));
                w.Write(controllerId);
                w.Write(true);
                BigWorldXRpc.WriteInt(ref w, levelId);
                BigWorldXRpc.WriteInt(ref w, uuid);
                w.Write(0);
                w.Write(BuildSceneObjectComponents(so, interactable, BigWorldActors.BeScannedOverride(player.BigWorldState, levelId, so.PlaceId)));
                w.Write(incId);
                w.Write(0);
            });
        }

        // Level-placed XNpc RepComponent list (retail order, 18 components). Table/config: Attrib (Npc.AttribId of the base's
        // FightNpcId), Move (placement), Animator animation (DefaultAction), FightScript (config ScriptId, else Npc.ScriptId),
        // Interactable (CanInteract). The rest are engine start-up state identical for every retail level NPC.
        // XRepMoveComponent key 2 = XRepNpcFollowMoveController [FollowMode (ENpcFollowMode), FollowModeParams (msgpack of
        // XRepNpc{Directly,Relative,NodeLock}FollowMode), IsForceTeleportEnabled, ForceTeleportRange] (dump.cs D:759904..). Level
        // actions 14001/14004/.. persist the mode as XNpcSaveData; without it here a relog re-replicates the NPC standing still.
        private static void WriteFollowController(ref MessagePackWriter w, Theatre5NpcSaveData? follow, bool paused)
        {
            // XRepNpcFollowMoveController keys 0..4; key 4 PausedCount = 14010 pause, tether = FollowMode 4 + XRepNpcTetherFollowMode [DUMP48 D:818069/818193].
            if (follow?.TetherFollowModeSaveData is { } t)
            {
                w.WriteArrayHeader(5); w.Write(4);
                w.Write(Pack((ref MessagePackWriter p) =>
                {
                    p.WriteArrayHeader(11);
                    p.Write(t.IsPlayerBecomeFollower ?? false); p.Write(t.IsFollowPlayer ?? false); p.Write(t.FollowTargetNpcPlaceId);
                    p.Write(t.LeadExpRotRadius); p.Write(t.ExpReachTime);
                    p.WriteArrayHeader(3); p.Write(t.TargetPosOffset?.X ?? 0f); p.Write(t.TargetPosOffset?.Y ?? 0f); p.Write(t.TargetPosOffset?.Z ?? 0f);
                    p.Write(t.NpcAnimAlignRadius); p.Write(t.ChaseRadius); p.Write(t.HandType); p.WriteNil(); p.WriteNil();
                }));
                w.Write(t.IsForceTeleportEnabled ?? false); w.Write(t.ForceTeleportRange); w.Write(paused ? 1 : 0);
            }
            else if (follow?.RelativeFollowModeSaveData is { } r)
            {
                w.WriteArrayHeader(5); w.Write(2);
                w.Write(Pack((ref MessagePackWriter p) =>
                {
                    p.WriteArrayHeader(10);
                    p.Write(r.IsFollowPlayer ?? false); p.Write(r.FollowTargetNpcPlaceId); p.Write(r.TargetAngle); p.Write(r.TargetRadius);
                    p.Write(r.ChaseRadius); p.Write(r.MaxIdleLagDistance); p.Write(r.IdleLookAtTargetDelayTime); p.Write(r.UseNavMesh ?? false);
                    p.Write(r.NormalFollowRadius); p.Write(r.StartFollowDelayTime);
                }));
                w.Write(r.IsForceTeleportEnabled ?? false); w.Write(r.ForceTeleportRange); w.Write(paused ? 1 : 0);
            }
            else if (follow?.DirectlyFollowModeSaveData is { } d)
            {
                w.WriteArrayHeader(5); w.Write(1);
                w.Write(Pack((ref MessagePackWriter p) =>
                {
                    p.WriteArrayHeader(7);
                    p.Write(d.IsFollowPlayer ?? false); p.Write(d.FollowTargetNpcPlaceId); p.Write(d.MaxIdleRange); p.Write(d.StartFollowRange);
                    p.Write(d.UseNavMesh ?? false); p.Write(d.IdleLookAtTarget ?? false); p.Write(d.ExpectedMovingRotateAngularSpeed);
                }));
                w.Write(false); w.Write(0f); w.Write(paused ? 1 : 0);
            }
            else if (follow?.NodeLockFollowModeSaveData is { } n)
            {
                w.WriteArrayHeader(5); w.Write(3);
                w.Write(Pack((ref MessagePackWriter p) =>
                {
                    p.WriteArrayHeader(4);
                    p.Write(n.IsFollowMasterPlayer ?? false); p.Write(n.FollowTargetNpcPlaceId); p.Write(n.LockJointName);
                    p.WriteArrayHeader(3); p.Write(n.PosOffset?.X ?? 0f); p.Write(n.PosOffset?.Y ?? 0f); p.Write(n.PosOffset?.Z ?? 0f);
                }));
                w.Write(false); w.Write(0f); w.Write(paused ? 1 : 0);
            }
            else
                w.WriteNil();
        }

        // XRepMoveComponent key 3 = XRepNpcGuideMoveController [TargetPosition XFloat3, UseNavMesh, OutOfRouteRange, StartGuideRange,
        // ReachTargetPositionRange, WaitDramaCaptionName, WaitDramaCaptionPlayProbability, IdleTurningDelayTime,
        // GuideMoveTypeMatchesTarget, GuideMoveType] [DUMP48 D:818233]; XNpcMoveComponent.StartGuideByRep restarts the walk on replicate.
        // Level action 14004 only runs on the live client (ServerThenClient), so a relog needs the saved guide here or the NPC stands still.
        private static void WriteGuideController(ref MessagePackWriter w, Theatre5NpcGuideMoveSaveData? g)
        {
            if (g is null) { w.WriteNil(); return; }
            w.WriteArrayHeader(10);
            w.WriteArrayHeader(3); w.Write(g.TargetPosition?.X ?? 0f); w.Write(g.TargetPosition?.Y ?? 0f); w.Write(g.TargetPosition?.Z ?? 0f);
            w.Write(g.UseNavMesh ?? false); w.Write(g.OutOfRouteRange); w.Write(g.StartGuideRange); w.Write(g.ReachTargetPositionRange);
            if (string.IsNullOrEmpty(g.WaitDramaCaptionName)) w.WriteNil(); else w.Write(g.WaitDramaCaptionName);
            w.Write(g.WaitDramaCaptionPlayProbability); w.Write(g.IdleTurningDelayTime); w.Write(g.GuideMoveTypeMatchesTarget ?? false); w.Write(g.GuideMoveType);
        }

        private readonly record struct Placement(float[] Rotation, double X, double Y, double Z, int ScriptId, string? DefaultAction, bool DisableGravity, bool CanInteract);

        // XNpc.Init [DUMP48 0x1D59DC0]: the 16 common components, then for NeedFightLogic (Npc.NeedFightLogicType 1 = ENpcNeedFightLogicType.Need)
        // XVar and the fight set Part, Threat, Paralysis, Focus, SkillAction, Skill, NpcSkill, Hit, Protector, Resilience, Reboot (XAI only
        // for a level instance with an AI config; InputControl/SearchTarget/LockTarget/InputHandle/FightInfoRecorder are IsPlayer-only),
        // then Interactable (instance CanInteract) and Perform (every non-player NPC in BigWorld). Without the fight set a fight NPC
        // throws in XNpcMoveComponent.ControllerMove -> XNpcFSMComponent.IsNpcDoSkill every frame (live 4033 cannon, place 2) and
        // cannot cast skills. The fight set's snapshots are the engine defaults of NpcComponents (identical for a fresh spawn).
        private static readonly string[] FightLogicComponents =
        [
            "XVarComponent", "XPartComponent", "XThreatComponent", "XParalysisComponent", "XFocusComponent", "XSkillActionComponent",
            "XSkillComponent", "XNpcSkillComponent", "XHitComponent", "XProtectorComponent", "XNpcResilienceComponent", "XNpcRebootComponent",
        ];

        // idle: spawned without a level placement (no Perform default action), so the action FSM starts in Idle (the player's engine
        // default snapshot) instead of the placed NPCs' Perform state.
        private static byte[] BuildLevelNpcComponents(int uuid, Placement at, AscNet.Table.V2.share.statussyncfight.npc.NpcTable fightNpc, bool interactable,
            bool idle, Theatre5NpcSaveData? follow = null, bool followPaused = false, Theatre5NpcGuideMoveSaveData? guide = null)
        {
            float[] q = at.Rotation;
            List<(string Name, byte[]? Snapshot)> components =
            [
                ("XNpcFSMComponent", Convert.FromHexString(idle ? NpcComponents[0].DefaultHex! : "92c40e93ff0dc409930002c404930002c001")),
                ("XNpcTimeComponent", Convert.FromHexString("94ca00000000ca00000000ca3f80000090")),
                ("XAttribComponent", BuildAttribSnapshot(fightNpc.AttribId)),
                ("XNpcMoveComponent", Pack((ref MessagePackWriter w) =>
                {
                    w.WriteArrayHeader(5);
                    w.WriteArrayHeader(3); w.Write((float)at.X); w.Write((float)at.Y); w.Write((float)at.Z);
                    w.WriteArrayHeader(4); w.Write(q[0]); w.Write(q[1]); w.Write(q[2]); w.Write(q[3]);
                    WriteFollowController(ref w, follow, followPaused);
                    WriteGuideController(ref w, guide);
                    w.Write(0f);
                })),
                ("XNpcModelComponent", BuildModelSnapshot(fightNpc)),
                ("XEffectComponent", null),
                ("XBehaviorComponent", Convert.FromHexString("960090808090948201010000000202000000800000")),
                ("XBuffComponent", Convert.FromHexString("929000")),
                ("XOnAirComponent", Convert.FromHexString("93c200cabf800000")),
                ("XFightScriptComponent", BuildFightScriptSnapshot(at.ScriptId > 0 ? at.ScriptId : fightNpc.ScriptId)),
                // ponytail: speed-scale keys inferred from one sample (the gravity-disabled NPC lacks RunSpeed).
                ("XNpcAnimatorComponent", Pack((ref MessagePackWriter w) =>
                {
                    w.WriteArrayHeader(4); w.Write(0);
                    if (string.IsNullOrEmpty(at.DefaultAction)) w.WriteNil(); else w.Write(at.DefaultAction);
                    w.Write(0f);
                    w.WriteMapHeader(at.DisableGravity ? 1 : 2);
                    w.Write("WalkSpeed"); w.Write(1f);
                    if (!at.DisableGravity) { w.Write("RunSpeed"); w.Write(1f); }
                })),
                ("XNpcColliderComponent", Convert.FromHexString("91c2")),
                ("XNpcNodeComponent", null),
                ("XNpcTriggerComponent", null),
                ("XNpcDeathComponent", Convert.FromHexString("920000")),
                ("XNpcAudioComponent", Convert.FromHexString("91ca3f800000")),
            ];
            if (fightNpc.NeedFightLogicType == 1)
                foreach (string name in FightLogicComponents)
                {
                    (string _, string? defaultHex, bool _) = NpcComponents.Single(c => c.Name == name);
                    components.Add((name, name == "XPartComponent" ? BuildPartSnapshot(fightNpc, uuid) : defaultHex is null ? null : Convert.FromHexString(defaultHex)));
                }
            if (at.CanInteract)
                components.Add(("XNpcInteractableComponent", Convert.FromHexString(interactable ? "91c3" : "91c2")));
            components.Add(("XNpcPerformComponent", null));
            return Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(components.Count);
                foreach ((string name, byte[]? snapshot) in components)
                {
                    w.WriteArrayHeader(2);
                    if (snapshot is null) w.WriteNil(); else w.Write(snapshot);
                    w.Write(name);
                }
            });
        }

        // RepController (dump.cs D:764398) [LevelId, IsMaster, Id, PlayerId, IncId, Alive, PlayerName].
        private static void WriteRepController(ref MessagePackWriter writer, int levelId, bool isMaster, int id, int playerId, int incId, string? name)
        {
            writer.WriteArrayHeader(7);
            BigWorldXRpc.WriteInt(ref writer, levelId);
            writer.Write(isMaster);
            BigWorldXRpc.WriteInt(ref writer, id);
            BigWorldXRpc.WriteInt(ref writer, playerId);
            BigWorldXRpc.WriteInt(ref writer, incId);
            writer.Write(true);
            if (name is null)
                writer.WriteNil();
            else
                writer.Write(name);
        }

        private const int NpcFlagCount = 92, CameraCollisionTypeCount = 2;

        // XRepNpc (dump.cs D:16124, 23 keys). Player team NPC: NpcData, player camp 1. Level-placed NPC (retail): no NpcData,
        // template = LevelNpcBase.FightNpcId, level base/instance ids, neutral camp 4, role part models from LevelNpcBase.
        internal static byte[] BuildRepNpc(Theatre5WorldNpcData npc, bool isBackstage) =>
            BuildRepNpc(npc, npc.Id, 0, 0, camp: 1, isPlayer: true, isBackstage, rolePartModels: null, actorSignificance: 1);

        private static byte[] BuildLevelRepNpc(LevelNpcTable npc, AscNet.Table.V2.share.statussyncfight.npc.LevelNpcBaseTable levelBase, int tipIconQuestId) =>
            BuildRepNpc(null, levelBase.FightNpcId, npc.BaseId, npc.PlaceId, camp: 4, isPlayer: false, isBackstage: false,
                levelBase.RolePartModelIds, npc.ActorSignificance, tipIconQuestId);

        private static byte[] BuildRepNpc(Theatre5WorldNpcData? npcData, int templateId, int levelBaseId, int levelInstId, int camp,
            bool isPlayer, bool isBackstage, IReadOnlyList<string>? rolePartModels, int actorSignificance, int tipIconQuestId = 0)
        {
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            writer.WriteArrayHeader(23);
            if (npcData is null) writer.WriteNil(); else writer.WriteRaw(MessagePackSerializer.Serialize(npcData)); // NpcData
            BigWorldXRpc.WriteInt(ref writer, templateId); // TemplateId
            writer.Write(levelBaseId); // LevelBaseTableId
            writer.Write(levelInstId); // LevelInstTableId
            writer.Write(camp); // Camp
            writer.Write(isPlayer); // IsPlayer
            writer.Write(isBackstage); // IsBackState: team members other than CurNpcPos wait backstage
            // Flags: one counter per ENpcFlag (ENpcFlag.End = 92 in the installed XDlcFightEnum.lua and dump.cs), all clear
            // at spawn; XNpc.CheckFlag indexes it directly.
            writer.WriteArrayHeader(NpcFlagCount);
            for (int i = 0; i < NpcFlagCount; i++)
                writer.Write(0);
            writer.WriteArrayHeader(0); // WeaponEquipIds
            writer.WriteArrayHeader(0); // SpearEquipIds
            // CameraCollisionEnabledArr: one bool per ECameraCollisionType (2), enabled at spawn (engine default).
            writer.WriteArrayHeader(CameraCollisionTypeCount);
            for (int i = 0; i < CameraCollisionTypeCount; i++)
                writer.Write(true);
            writer.Write(0); // CameraConfigId: Level default camera
            // HideFlags: ENpcHideFlags.Backstage (2, dump48 D:814003) for team members waiting backstage (retail
            // babylonia.pcap 979); without it they render visible in their unanimated bind pose.
            writer.Write(isBackstage ? 2 : 0);
            // RolePartModelData: {PartModelId: nil} per LevelNpcBase.RolePartModelIds, nil without parts.
            List<string> parts = rolePartModels?.Where(part => !string.IsNullOrEmpty(part)).ToList() ?? [];
            if (parts.Count == 0) writer.WriteNil();
            else
            {
                writer.WriteMapHeader(parts.Count);
                foreach (string part in parts) { writer.Write(part); writer.WriteNil(); }
            }
            writer.Write(1); // GravityType: EGravityType.FreeFall (spawned airborne until grounded)
            writer.Write(FightFloat("JumpGravity"));
            writer.Write(FightFloat("FreeFallGravity"));
            writer.Write(false); // CtrlBackstageByStreaming
            writer.Write(tipIconQuestId); // TipIconQuestId (level NPCs: SetNpcQuestTipIconActive state)
            writer.Write(actorSignificance); // ActorSignificance (player: Level1; level NPCs: config)
            writer.Write(0); // KillerNpcUUID
            writer.Write(0); // KillerMagicId
            writer.WriteArrayHeader(0); // SpecialStates
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        private static float FightFloat(string key) =>
            float.Parse(FightConfig.Value.Single(row => row.Key == key).Value, System.Globalization.CultureInfo.InvariantCulture);

        #endregion

        #region LoadComplete bootstrap

        internal static void SendPendingStartFightNotify(Session session)
        {
            if (!session.PendingBigWorldStartFightNotify)
                return;
            session.PendingBigWorldStartFightNotify = false;
            session.SendPush(new StartFightNotify());
        }

        // Online-mode server engine bootstrap after LoadCompleteResponse (retail order: ecology state, RL-object check,
        // [quest nav points], combat state, begin update).
        internal static void SendPendingLoadCompleteXRpc(Session session)
        {
            if (!session.PendingBigWorldLoadCompleteXRpc)
                return;
            session.PendingBigWorldLoadCompleteXRpc = false;
            int levelId = CurrentLevelId(session.player.BigWorldState);
            int playerId = checked((int)session.player.PlayerData.Id);
            // No ecology-construct clear state is persisted: both dictionaries are empty.
            session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcEcologyConstructClearStateUpdateNotify",
                BigWorldXRpc.Args(new Dictionary<int, int>(), new Dictionary<int, int>()), 0));
            session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcBeginCheckRLObjectCompleted", BigWorldXRpc.Args(), levelId));
            // The world is entered out of combat (no enemies are simulated by AscNet).
            session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcSetCombatState",
                MessagePackSerializer.Serialize(new Dictionary<string, object> { ["PlayerId"] = playerId, ["IsInCombat"] = false }), levelId));
            float elapsed = (float)Math.Max(0D, (DateTime.UtcNow - session.BigWorldFightStartedAt).TotalSeconds);
            session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcBeginUpdateLevel", BigWorldXRpc.Args(elapsed), levelId));
        }

        #endregion

        #region XRpc

        [RequestPacketHandler("XRpcCommon")]
        public static void XRpcCommonRequestHandler(Session session, Packet.Request packet)
        {
            if (!BigWorldXRpc.TryRead(packet.Content, out string rpcName, out byte[] args, out int levelId, out _))
            {
                session.SendResponse(new XRpcCommonResponse { Code = CodeInvalidRequest }, packet.Id);
                return;
            }
            // A server-run level switch (RpcPlayerSwitchLevelNotify) has no LoadComplete: its level-start pushes go out when
            // the client reports the new level loaded; without them the client never leaves the loading screen.
            if (rpcName == "RpcPlayerEnterLevelComplete")
                SendPendingLoadCompleteXRpc(session);
            // RpcResumeFight releases the suspended action lists AFTER its response (retail 1777 follows resp 719).
            int? resumeCount = null;
            Action? afterResponse = null;
            int code = rpcName switch
            {
                "RpcPlayerInteractRequest" => HandlePlayerInteract(session, args),
                "RpcSwitchPlayerNpcRequest" => HandleSwitchPlayerNpc(session, args),
                "RpcPlayerSwitchLevelRequest" => HandlePlayerSwitchLevel(session, args, out afterResponse),
                "RpcLevelActionClientExecuteFinish" => BigWorldLevelActions.HandleClientExecuteFinish(session, args),
                "RpcPauseFight" => HandlePauseCount(session, args, out _, pause: true),
                "RpcResumeFight" => HandlePauseCount(session, args, out resumeCount, pause: false),
                _ => BigWorldQuestModule.TryHandleXRpc(session, rpcName, args, levelId) ? 0 : AcknowledgeEngineRpc(session, rpcName)
            };
            session.SendResponse(new XRpcCommonResponse { Code = code }, packet.Id);
            if (resumeCount is { } count)
                BigWorldLevelActions.OnPauseCount(session, count);
            afterResponse?.Invoke();
            BigWorldQuestRuntime.Tick(session);
        }

        // RpcPauseFight/RpcResumeFight [PauseCount]: the client's pause count after the change. Pause applies at once;
        // Resume is applied by the caller after the response.
        private static int HandlePauseCount(Session session, byte[] args, out int? resumeCount, bool pause)
        {
            resumeCount = null;
            object?[] values = MessagePackSerializer.Deserialize<object?[]>(args, Packet.InboundOptions);
            if (values.Length < 1)
                return CodeInvalidRequest;
            int count = BigWorldXRpc.ToInt(values[0]);
            if (pause) BigWorldLevelActions.OnPauseCount(session, count);
            else resumeCount = count;
            return 0;
        }

        [RequestPacketHandler("XRpcComponentAction")]
        public static void XRpcComponentActionRequestHandler(Session session, Packet.Request packet)
        {
            if (!BigWorldXRpc.TryRead(packet.Content, out string rpcName, out byte[] args, out int levelId, out int actorUuid))
            {
                session.SendResponse(new XRpcComponentActionResponse { Code = CodeInvalidRequest }, packet.Id);
                return;
            }
            if (rpcName == "XRpcNpcPositionAndRotation")
                PersistNpcPosition(session, args, levelId, actorUuid);
            else if (rpcName == "RpcChangeActorScannedStateNotify")
                PersistBeScanned(session, args, levelId, actorUuid);
            else if (rpcName == "RpcTriggerInteractNotify")
                HandleTriggerInteractNotify(session, args, levelId, actorUuid);
            else
                AcknowledgeEngineRpc(session, rpcName);
            session.SendResponse(new XRpcComponentActionResponse(), packet.Id);
            BigWorldQuestRuntime.Tick(session);
        }

        // RpcChangeActorScannedStateNotify [BeScanned] (RpcChangeActorScannedStateNotify.Key 0).
        private static void PersistBeScanned(Session session, byte[] argsPayload, int levelId, int actorUuid)
        {
            object?[] args = MessagePackSerializer.Deserialize<object?[]>(argsPayload, Packet.InboundOptions);
            if (args.Length >= 1 && args[0] is bool beScanned)
                BigWorldActors.SetBeScanned(session, levelId, actorUuid, beScanned);
        }

        // Client actor-level XRpcs (XRpcNpcSetActiveNotify, XRpcTeleportResetOnGround ...): engine state the client
        // simulates; retail answers each with XRpcActorActionResponse {Code 0} (babylonia.pcap 221/222/385).
        [RequestPacketHandler("XRpcActorAction")]
        public static void XRpcActorActionRequestHandler(Session session, Packet.Request packet)
        {
            if (!BigWorldXRpc.TryRead(packet.Content, out string rpcName, out byte[] args, out _, out int actorUuid))
            {
                session.SendResponse(new XRpcActorActionResponse { Code = CodeInvalidRequest }, packet.Id);
                return;
            }
            BigWorldQuestRuntime.HandleNpcDeath(session, rpcName, args, actorUuid);
            AcknowledgeEngineRpc(session, rpcName);
            session.SendResponse(new XRpcActorActionResponse(), packet.Id);
        }

        // Client-created actors (XRpcActorReplicate [Type, Rep, ControllerId, IsAI, LevelId, UUID, ParentUUID, Components,
        // ControllerIncId, PlayerId], dump48 TypeDefIndex 1034): the controlling client replicates actors it spawns itself,
        // e.g. the heavy-artillery shells (XMissile) in 4033. The client simulates them alone (single player), so the server
        // validates the envelope against the session and acknowledges with XRpcActorReplicateResponse {Code}; nothing
        // is persisted or relayed. AscNet policy [INF]: no retail capture shows this request (babylonia.pcap has no shots).
        [RequestPacketHandler("XRpcActorReplicate")]
        public static void XRpcActorReplicateRequestHandler(Session session, Packet.Request packet)
        {
            object?[]? rep = null;
            try { rep = MessagePackSerializer.Deserialize<object?[]>(packet.Content, Packet.InboundOptions); }
            catch (MessagePackSerializationException) { }
            bool valid = rep is { Length: >= 10 } && rep[0] is string { Length: > 0 }
                && session.BigWorldWorldId != 0
                && BigWorldXRpc.TryToDouble(rep[2], out double controller) && (int)controller == BigWorldXRpc.PlayerControllerId
                && BigWorldXRpc.TryToDouble(rep[4], out double level) && (int)level == CurrentLevelId(session.player.BigWorldState);
            session.SendResponse(new XRpcActorReplicateResponse { Code = valid ? 0 : CodeInvalidRequest }, packet.Id);
        }

        // Client-simulated combat/engine RPCs (RpcPauseFight, RepBuff, RpcPlayerEnterLevelComplete ...) carry no state
        // AscNet persists; retail acknowledges every client XRpc with Code 0.
        private static int AcknowledgeEngineRpc(Session session, string rpcName)
        {
            session.log.Debug($"BigWorld XRpc {rpcName} acknowledged (client-simulated).");
            return 0;
        }

        // RpcSwitchPlayerNpcRequest [LevelId, CharacterId, NpcId, IsTrial] -> RpcSwitchPlayerNpc [LevelId, Pos].
        private static int HandleSwitchPlayerNpc(Session session, byte[] argsPayload)
        {
            object?[] args = MessagePackSerializer.Deserialize<object?[]>(argsPayload, Packet.InboundOptions);
            if (args.Length < 3)
                return CodeInvalidRequest;
            int levelId = BigWorldXRpc.ToInt(args[0]);
            int characterId = BigWorldXRpc.ToInt(args[1]);
            List<Theatre5WorldNpcData> npcs = BuildNpcList(session.player);
            int pos = npcs.FindIndex(npc => npc.Character?.Id == characterId);
            if (pos < 0 || levelId != CurrentLevelId(session.player.BigWorldState))
                return CodeInvalidRequest;
            session.player.BigWorldState.CurNpcPos = pos;
            session.player.Save();
            session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcSwitchPlayerNpc", BigWorldXRpc.Args(levelId, pos), levelId));
            return 0;
        }

        // XRpcNpcPositionAndRotation [X, Y, Z, QX, QY, QZ, QW, PosSyncType, RotSyncType, MoveType, OnAirType, ...] [DUMP48
        // XRpcNpcPositionAndRotation]. Only the controlled team member on the ground is saved: an airborne report (OnAirType
        // != OnGround; live: a character that fell through a missing floor reported y=-62) or a backstage member's pose would
        // be what a relog respawns at; the previous grounded pose (else the level entry pose) stays.
        private static void PersistNpcPosition(Session session, byte[] argsPayload, int levelId, int actorUuid)
        {
            BigWorldPlayerState state = session.player.BigWorldState;
            object?[] args = MessagePackSerializer.Deserialize<object?[]>(argsPayload, Packet.InboundOptions);
            double[] values = new double[7];
            bool numeric = args.Length >= 7 && Enumerable.Range(0, 7).All(i => BigWorldXRpc.TryToDouble(args[i], out values[i]));
            bool playerNpc = Enumerable.Range(0, BuildNpcList(session.player).Count).Any(index => BigWorldActors.TeamNpcUuid(session, levelId, index) == actorUuid);
            if (numeric && !playerNpc)
            {
                BigWorldActors.TrackNpcPose(session, levelId, actorUuid, new BigWorldVector3 { X = values[0], Y = values[1], Z = values[2] },
                    new BigWorldVector4 { X = values[3], Y = values[4], Z = values[5], W = values[6] });
                return;
            }
            if (!numeric || !playerNpc || actorUuid != BigWorldActors.TeamNpcUuid(session, levelId, state.CurNpcPos)
                || (args.Length > 10 && BigWorldXRpc.ToInt(args[10]) != 0))
                return;
            // AscNet policy [INF]: the pose inside an instance is kept apart (InstPosition) so a relog resumes the instance
            // while leaving it still returns to the saved open-world pose.
            if (state.InstLevelId != 0)
            {
                if (levelId != state.InstLevelId)
                    return;
                state.InstPosition = new BigWorldVector3 { X = values[0], Y = values[1], Z = values[2] };
                state.InstRotationY = QuaternionYaw(values[3], values[4], values[5], values[6]);
                session.player.Save();
                return;
            }
            if (levelId != state.LastLevelId)
                return;
            state.LastPosition = new BigWorldVector3 { X = values[0], Y = values[1], Z = values[2] };
            state.LastRotation = new BigWorldVector4 { X = values[3], Y = values[4], Z = values[5], W = values[6] };
            state.LastRotationY = QuaternionYaw(values[3], values[4], values[5], values[6]);
            session.player.Save();
        }

        #endregion
    }
}
