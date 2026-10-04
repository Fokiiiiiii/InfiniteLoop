using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.bigworld.common;
using AscNet.Table.V2.share.bigworld.common.character;
using AscNet.Table.V2.share.bigworld.common.commanderdiy;
using AscNet.Table.V2.share.bigworld.common.fashion;
using AscNet.Table.V2.share.fashion;
using MessagePack;

namespace AscNet.GameServer.Handlers.BigWorld
{
    #region DTOs
    [MessagePackObject(true)]
    public class BigWorldTeamChangeRequest
    {
        public BigWorldTeamData? ChangeTeam { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldTeamChangeResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldTeamIndexChangeRequest
    {
        public int CurrentTeamId { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldTeamIndexChangeResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldTeamNotify
    {
        public List<BigWorldTeamData> Teams { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class BigWorldCharacterFashionUseRequest
    {
        public int FashionId { get; set; }
        public int FashionColorId { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldCharacterFashionUseResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldCharacterSetHeadInfoRequest
    {
        public int TemplateId { get; set; }
        public BigWorldCharacterHeadInfo? CharacterHeadInfo { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldCharacterSetHeadInfoResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldCommanderFashionUpdateRequest
    {
        public int OutfitType { get; set; }
        public int Gender { get; set; }
        public Dictionary<int, Theatre5BigWorldCommanderFashion>? CommanderFashionList { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldCommanderFashionUpdateResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldShowRewardFinishRequest
    {
    }

    [MessagePackObject(true)]
    public class BigWorldShowRewardFinishResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyBigWorldCommanderFashionBagUpdate
    {
        public List<int> DlcFashionBags { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class NotifyBigWorldShowReward
    {
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }
    #endregion

    // Teams, BigWorld character fashion/head, commander DIY (xbigworldcharacter, xbigworldcommanderdiy).
    internal static class BigWorldCharacterModule
    {
        // CodeText ids.
        private const int DlcCharacterTemplateNotFound = 25000003;          // character settings not found
        private const int DlcCharacterGetCharacterByIdNotFound = 25000006;  // character does not exist
        private const int DlcCharacterAlreadyFighting = 25000007;           // character already deployed (duplicate)
        private const int DlcConditionNotFinish = 25004034;                 // conditions not met
        private const int DlcFashionPartIdNotFound = 25200002;              // invalid outfit id
        private const int DlcFashionPartIdNotMatchType = 25200003;          // outfit cannot be equipped on this part
        private const int DlcFashionPartIdNotOwn = 25200004;                // outfit not owned
        private const int DlcFashionResIdNotFound = 25200005;               // outfit res missing
        private const int DlcFashionNotSupportChangeColor = 25200006;       // outfit does not support colour changes
        private const int DlcFashionNotExistColor = 25200007;               // outfit colour does not exist
        private const int DlcLackMustWearPart = 25200008;                   // missing required slot
        private const int DlcFashionFashionListParamError = 25200101;       // fashion list param error
        private const int DlcFashionTypeIdNotFound = 25200103;              // invalid outfit part type
        private const int DlcFashionSexualParamError = 25200104;            // gender param error
        private const int BigWorldDIYOutfitTypeNotFound = 25201001;         // costume preset not found
        private const int BigWorldDIYOutfitCannotEquip = 25201002;          // costume cannot be equipped in preset
        private const int BigWorldDIYFashionPartIdIncompatible = 25201003;  // incompatible costume
        private const int BigWorldFashionConfigNotFound = 25202001;         // BW coating config not found
        private const int BigWorldFashionColorIdInvalid = 25202002;         // BW coating variant invalid
        private const int BigWorldFashionColorIdUnowned = 25202003;         // BW coating variant not owned
        private const int FashionIsUnOwned = 20010002;                      // coating not owned
        private const int FashionCharacterDoNotMatch = 20010005;            // coating character mismatch
        private const int CharacterLiberateLvNotEnough = 20034022;          // ConditionManagerCharacterLiberateLvNotEnough

        private const int TeamCount = 3;        // team/XBWTeamId.lua Common 1..3
        private const int FullTeamEntityCount = 3; // XBigWorldCharacterAgency:7
        private const int HeadTypeDefault = 0, HeadTypeLiberation = 1, HeadTypeFashion = 2; // XFashionConfigs.HeadPortraitType
        private const int LiberationHigher = 4; // XEnumConst.CHARACTER.GrowUpLevel.Higher (as CharacterModule)

        private static readonly Lazy<Dictionary<int, BigWorldCharacterTable>> Characters = new(() =>
            TableReaderV2.Parse<BigWorldCharacterTable>().ToDictionary(r => r.Id));
        private static readonly Lazy<Dictionary<int, BigWorldFashionTable>> BwFashions = new(() =>
            TableReaderV2.Parse<BigWorldFashionTable>().ToDictionary(r => r.Id));
        private static readonly Lazy<Dictionary<int, int>> MainFashionCharacter = new(() =>
            TableReaderV2.Parse<FashionTable>().ToDictionary(r => r.Id, r => r.CharacterId));
        private static readonly Lazy<Dictionary<int, BigWorldDIYPartTable>> Parts = new(() =>
            TableReaderV2.Parse<BigWorldDIYPartTable>().ToDictionary(r => r.Id));
        private static readonly Lazy<Dictionary<int, BigWorldDIYTypeTable>> Types = new(() =>
            TableReaderV2.Parse<BigWorldDIYTypeTable>().ToDictionary(r => r.TypeId));
        private static readonly Lazy<Dictionary<int, BigWorldDIYOutfitTable>> Outfits = new(() =>
            TableReaderV2.Parse<BigWorldDIYOutfitTable>().ToDictionary(r => r.Id));
        private static readonly Lazy<Dictionary<int, BigWorldDIYResTable>> Res = new(() =>
            TableReaderV2.Parse<BigWorldDIYResTable>().ToDictionary(r => r.Id));
        private static readonly Lazy<Dictionary<int, List<int>>> ColorGroups = new(() =>
            TableReaderV2.Parse<BigWorldDIYColorGroupTable>().ToDictionary(r => r.GroupId, r => r.ColorId ?? new()));
        private static readonly Lazy<int[]> DefaultTeam = new(() =>
        {
            Dictionary<string, string> config = TableReaderV2.Parse<BigWorldConfigTable>().ToDictionary(r => r.Key, r => r.Value ?? "0");
            return Enumerable.Range(1, FullTeamEntityCount).Select(pos => int.Parse(config[$"DefaultTeamPos{pos}"])).ToArray();
        });

        internal static void RegisterConditions()
        {
            // XBigWorldCommanderDIYAgency:CheckPartUnlockCondition: owned count of Params[2..] >= Params[1].
            BigWorldConditionService.Register(23102, (player, p) =>
            {
                // Unseeded players own exactly the BigWorldDIYBagInit parts.
                IEnumerable<int> bag = player.BigWorldState.CommanderGender == 0
                    ? TableReaderV2.Parse<BigWorldDIYBagInitTable>().Select(r => r.PartId)
                    : player.BigWorldState.CommanderFashionBags;
                HashSet<int> owned = bag.ToHashSet();
                return p.Count > 0 && p.Skip(1).Count(id => id > 0 && owned.Contains(id)) >= p[0];
            });
        }

        #region Shared API (Core)
        internal static void FillPlayerData(Player player, BigWorldPlayerData data)
        {
            Ensure(player);
            BigWorldPlayerState s = player.BigWorldState;
            data.Gender = s.CommanderGender;
            data.CurCommanderOutfitType = s.CurCommanderOutfitType;
            data.CommanderFashionOutfits = s.CommanderFashionOutfits.ToDictionary(o => o.Key, o => new BigWorldCommanderOutfit
            {
                WearFashionDict = o.Value.ToDictionary(p => p.TypeId, p => new Theatre5BigWorldCommanderFashion { PartId = p.PartId, ColourId = p.ColourId })
            });
            data.CommanderFashionBags = s.CommanderFashionBags.ToList();
            data.CharacterInitialized = s.CharacterInitialized;
            data.CurrentTeamId = s.CurrentTeamId;
            data.TeamDict = s.Teams.ToDictionary(t => t.Key, t => ToTeamData(t.Key, t.Value));
            data.CharacterWearFashionDict = s.CharacterFashions.ToDictionary(f => f.Key, f => new BigWorldCharacterWearFashion
            {
                Character = f.Key,
                FashionId = f.Value.FashionId,
                FashionColorId = f.Value.FashionColorId,
                DlcCharacterHeadInfo = f.Value.HeadFashionId > 0
                    ? new BigWorldCharacterHeadInfo { HeadFashionId = f.Value.HeadFashionId, HeadFashionType = f.Value.HeadFashionType }
                    : null
            });
        }

        internal static int GetGender(Player player)
        {
            Ensure(player);
            return player.BigWorldState.CommanderGender;
        }

        internal static List<int> GetCommanderFashionBags(Player player)
        {
            Ensure(player);
            return player.BigWorldState.CommanderFashionBags.ToList();
        }

        // WorldData.Players[0].NpcList: current team by Pos; commandant carries gender + current DIY outfit.
        internal static List<Theatre5WorldNpcData> BuildWorldNpcList(Player player)
        {
            Ensure(player);
            BigWorldPlayerState s = player.BigWorldState;
            List<BigWorldTeamMemberState> team = s.Teams.GetValueOrDefault(s.CurrentTeamId) ?? new();
            return team.OrderBy(m => m.Pos).Where(m => Characters.Value.ContainsKey(m.CharacterId)).Select(m =>
            {
                BigWorldCharacterTable row = Characters.Value[m.CharacterId];
                BigWorldCharacterFashionState? wear = s.CharacterFashions.GetValueOrDefault(m.CharacterId);
                bool commandant = row.IsCommandant == 1;
                return new Theatre5WorldNpcData
                {
                    Id = row.NpcId,
                    // AscNet policy: BigWorld stats come from BigWorldCharacter.AttribId, not main-game growth; level fixed at 1.
                    Level = 1,
                    Pos = m.Pos,
                    Gender = commandant ? s.CommanderGender : 0,
                    Character = new Theatre5DlcCharacterData
                    {
                        Id = m.CharacterId,
                        FashionId = wear?.FashionId ?? row.DefaultFashionId,
                        FashionColorId = wear?.FashionColorId ?? 0
                    },
                    PartData = commandant
                        ? new Theatre5WorldNpcPartData
                        {
                            PartList = (s.CommanderFashionOutfits.GetValueOrDefault(s.CurCommanderOutfitType) ?? new())
                                .Select(p => new Theatre5BigWorldCommanderFashion { PartId = p.PartId, ColourId = p.ColourId }).ToList()
                        }
                        : null,
                    // nil like retail: the engine builds attributes from tables. An empty blob makes XAttrib.Deserialize
                    // overrun when a drama clones the player NPC (XDrama.ClonePlayerNpc), locking the player mid-dialogue.
                    AttribsData = null
                };
            }).ToList();
        }

        // Applies DIY-part goods (29xxxxxx) returned unapplied by BigWorldRewardService.Grant.
        internal static void ApplySliceRewards(Session session, IReadOnlyList<RewardGoods> goods)
        {
            Ensure(session.player);
            BigWorldPlayerState s = session.player.BigWorldState;
            bool bagChanged = false;
            foreach (RewardGoods g in goods)
            {
                if (Parts.Value.ContainsKey(g.TemplateId) && !s.CommanderFashionBags.Contains(g.TemplateId))
                {
                    s.CommanderFashionBags.Add(g.TemplateId);
                    bagChanged = true;
                }
            }
            // AscNet policy: rewards granted before the opening DIY completes are held for NotifyBigWorldShowReward
            // (client shows them after the DIY/opening flow and acks with BigWorldShowRewardFinishRequest).
            bool queued = !s.CharacterInitialized && goods.Count > 0;
            if (queued)
                s.PendingShowRewards.AddRange(goods);
            if (!bagChanged && !queued)
                return;
            session.player.Save();
            if (bagChanged)
                session.SendPush(new NotifyBigWorldCommanderFashionBagUpdate { DlcFashionBags = s.CommanderFashionBags.ToList() });
            if (queued)
                session.SendPush(new NotifyBigWorldShowReward { RewardGoodsList = s.PendingShowRewards.ToList() });
        }
        #endregion

        #region Handlers
        [RequestPacketHandler("BigWorldTeamChangeRequest")]
        public static void BigWorldTeamChangeRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldTeamChangeRequest req = packet.Deserialize<BigWorldTeamChangeRequest>();
            EnsureAndNotify(session);
            List<Theatre5WorldNpcData> before = BigWorldModule.BuildNpcList(session.player);
            int code = ValidateTeam(session, req.ChangeTeam);
            if (code == 0)
            {
                BigWorldTeamData team = req.ChangeTeam!;
                session.player.BigWorldState.Teams[team.TeamId] = team.CharacterList
                    .OrderBy(c => c.Pos).Select(c => new BigWorldTeamMemberState { CharacterId = c.CharacterId, Pos = c.Pos }).ToList();
                session.player.Save();
            }
            session.SendResponse(new BigWorldTeamChangeResponse { Code = code }, packet.Id);
            if (code == 0 && req.ChangeTeam!.TeamId == session.player.BigWorldState.CurrentTeamId)
                SyncTeamData(session, before);
        }

        [RequestPacketHandler("BigWorldTeamIndexChangeRequest")]
        public static void BigWorldTeamIndexChangeRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldTeamIndexChangeRequest req = packet.Deserialize<BigWorldTeamIndexChangeRequest>();
            EnsureAndNotify(session);
            BigWorldPlayerState s = session.player.BigWorldState;
            List<Theatre5WorldNpcData> before = BigWorldModule.BuildNpcList(session.player);
            int code = req.CurrentTeamId is < 1 or > TeamCount ? DlcConditionNotFinish
                // Client blocks empty current teams (Agency:418-424).
                : s.Teams.GetValueOrDefault(req.CurrentTeamId) is not { Count: > 0 } ? DlcCharacterGetCharacterByIdNotFound
                : 0;
            bool switched = code == 0 && s.CurrentTeamId != req.CurrentTeamId;
            if (switched)
            {
                s.CurrentTeamId = req.CurrentTeamId;
                session.player.Save();
            }
            session.SendResponse(new BigWorldTeamIndexChangeResponse { Code = code }, packet.Id);
            if (switched)
                SyncTeamData(session, before);
        }

        // RpcSyncTeamData [LevelId, CurNpcPos, NpcDataList, XFightTeamNpcData] (4.8 XController.HandleSyncTeamData 0x1B03330 ->
        // SyncTeamDataInternal 0x1B06FC0, disassembled): every NpcDataList entry is matched against the client's player NPCs by
        // (Character.Id, NpcData.Id); matches are Reload()ed in place at their new Pos, NPCs without a match are destroyed by the
        // client itself (no XRpcRemoveActor), and the XFightTeamNpcData replicate list is dispatched for the entries without a
        // match, each then taken over by the local controller. CurNpcPos is read from XFightTeamNpcData (a Pos in the list, else
        // the client keeps its current character). Retail 1276 shape; replicates are server-controlled XNpc like AddTrialNpcToTeam.
        // AscNet policy [INF]: the controlled character stays controlled when it is still in the team, else CurNpcPos is clamped.
        // Outside the world, or before the level snapshot, nothing is sent: the enter snapshot carries the team.
        private static void SyncTeamData(Session session, List<Theatre5WorldNpcData> before)
        {
            Player player = session.player;
            BigWorldPlayerState s = player.BigWorldState;
            int levelId = BigWorldModule.CurrentLevelId(s);
            List<Theatre5WorldNpcData> team = BigWorldModule.BuildNpcList(player);
            if (session.BigWorldWorldId == 0 || team.Count == 0 || !BigWorldActors.HasView(session, levelId))
                return;
            int controlled = before.ElementAtOrDefault(s.CurNpcPos)?.Character?.Id ?? 0;
            int cur = team.FindIndex(npc => npc.Character?.Id == controlled);
            s.CurNpcPos = cur >= 0 ? cur : Math.Clamp(s.CurNpcPos, 0, team.Count - 1);
            cur = s.CurNpcPos;
            player.Save();

            List<int> uuids = [];
            List<byte[]> replicates = [];
            for (int i = 0; i < team.Count; i++)
            {
                int known = before.FindIndex(old => old.Character?.Id == team[i].Character?.Id && old.Id == team[i].Id);
                if (known >= 0)
                {
                    uuids.Add(BigWorldActors.TeamNpcUuid(session, levelId, known));
                    continue;
                }
                (int Uuid, int Sequence) id = BigWorldActors.AllocateServerUuid(session, levelId)!.Value;
                uuids.Add(id.Uuid);
                replicates.Add(BigWorldTrialTeam.BuildReplicate(player, team[i], levelId, id));
            }
            BigWorldActors.SetTeamUuids(session, levelId, uuids);
            byte[] args = BigWorldModule.Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(4);
                BigWorldXRpc.WriteInt(ref w, levelId);
                BigWorldXRpc.WriteInt(ref w, cur);
                w.WriteRaw(MessagePackSerializer.Serialize(team));
                w.WriteRaw(BigWorldTrialTeam.BuildFightTeamNpcData(cur, team, replicates));
            });
            session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcSyncTeamData", args, 0));
        }

        [RequestPacketHandler("BigWorldCharacterFashionUseRequest")]
        public static void BigWorldCharacterFashionUseRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldCharacterFashionUseRequest req = packet.Deserialize<BigWorldCharacterFashionUseRequest>();
            EnsureAndNotify(session);
            int characterId = ResolveFashionCharacter(req.FashionId);
            int code = !BwFashions.Value.TryGetValue(req.FashionId, out BigWorldFashionTable? fashion) ? BigWorldFashionConfigNotFound
                : characterId == 0 ? DlcCharacterGetCharacterByIdNotFound
                : !FashionUnlocked(session, characterId, req.FashionId) ? FashionIsUnOwned
                : req.FashionColorId != 0 && !(fashion.FashionColorIds ?? new()).Contains(req.FashionColorId) ? BigWorldFashionColorIdInvalid
                : req.FashionColorId != 0 && !(session.character.FashionColors.GetValueOrDefault(req.FashionId)?.Contains(req.FashionColorId) ?? false) ? BigWorldFashionColorIdUnowned
                : 0;
            if (code == 0)
            {
                BigWorldCharacterFashionState wear = Wear(session.player, characterId);
                wear.FashionId = req.FashionId;
                wear.FashionColorId = req.FashionColorId;
                session.player.Save();
            }
            session.SendResponse(new BigWorldCharacterFashionUseResponse { Code = code }, packet.Id);
        }

        // XBigWorldCharacterAgency:CheckHeadUnlock.
        [RequestPacketHandler("BigWorldCharacterSetHeadInfoRequest")]
        public static void BigWorldCharacterSetHeadInfoRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldCharacterSetHeadInfoRequest req = packet.Deserialize<BigWorldCharacterSetHeadInfoRequest>();
            EnsureAndNotify(session);
            BigWorldCharacterHeadInfo? head = req.CharacterHeadInfo;
            int code = !Characters.Value.ContainsKey(req.TemplateId) ? DlcCharacterGetCharacterByIdNotFound
                : head is null || head.HeadFashionType is < HeadTypeDefault or > HeadTypeFashion ? DlcConditionNotFinish
                : ResolveFashionCharacter(head.HeadFashionId) != req.TemplateId ? FashionCharacterDoNotMatch
                : !FashionUnlocked(session, req.TemplateId, head.HeadFashionId) ? FashionIsUnOwned
                : head.HeadFashionType == HeadTypeLiberation
                    && Character.GetLiberateLevel((uint)req.TemplateId, session.player.GatherRewards) < LiberationHigher ? CharacterLiberateLvNotEnough
                : 0;
            if (code == 0)
            {
                BigWorldCharacterFashionState wear = Wear(session.player, req.TemplateId);
                wear.HeadFashionId = head!.HeadFashionId;
                wear.HeadFashionType = head.HeadFashionType;
                session.player.Save();
            }
            session.SendResponse(new BigWorldCharacterSetHeadInfoResponse { Code = code }, packet.Id);
        }

        [RequestPacketHandler("BigWorldCommanderFashionUpdateRequest")]
        public static void BigWorldCommanderFashionUpdateRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldCommanderFashionUpdateRequest req = packet.Deserialize<BigWorldCommanderFashionUpdateRequest>();
            EnsureAndNotify(session);
            int code = ValidateOutfit(session.player, req);
            if (code == 0)
            {
                BigWorldPlayerState s = session.player.BigWorldState;
                s.CommanderGender = req.Gender;
                s.CurCommanderOutfitType = req.OutfitType;
                s.CommanderFashionOutfits[req.OutfitType] = req.CommanderFashionList!
                    .OrderBy(p => p.Key)
                    .Select(p => new BigWorldCommanderPartState { TypeId = p.Key, PartId = p.Value.PartId, ColourId = p.Value.ColourId })
                    .ToList();
                s.CharacterInitialized = true;
                session.player.Save();
            }
            session.SendResponse(new BigWorldCommanderFashionUpdateResponse { Code = code }, packet.Id);
            if (code == 0)
                SyncPlayerSelfDiy(session);
        }

        // RpcSyncPlayerSelfDIY [LevelId, NpcData, RpcActorReplicateList, PlayerSelfDIYData] re-dresses the live commandant.
        // Client 4.8 XController.HandleSyncPlayerSelfDIY (GameAssembly RVA 0x1B02B50, disassembled; local://bw-diy-sync.md):
        // a nil replicate list reloads the existing player-self NPC in place (PlayerSelfNpc.Reload(NpcData), part models looked
        // up from NpcData.PartData) and patches the team's IsPlayerSelf entry from PlayerSelfDIYData; a non-nil list destroys and
        // re-creates the actor (both earlier attempts took that branch and broke the character). No capture holds the RPC.
        // PlayerSelfDIYData = [CharacterId, Gender, NpcId, FashionId, FashionColorId, PartData].
        private static void SyncPlayerSelfDiy(Session session)
        {
            if (session.BigWorldWorldId == 0)
                return; // outside the world: the next enter snapshot carries the outfit
            Player player = session.player;
            Theatre5WorldNpcData? self = BigWorldModule.BuildNpcList(player).FirstOrDefault(npc => npc.IsPlayerSelf);
            if (self?.Character is null || self.PartData is not { PartList.Count: > 0 })
                return; // commandant not in the team: nothing in the world wears the outfit
            byte[] args = BigWorldModule.Pack((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(4);
                BigWorldXRpc.WriteInt(ref w, BigWorldModule.CurrentLevelId(player.BigWorldState));
                w.WriteRaw(MessagePackSerializer.Serialize(self));
                w.WriteNil(); // RpcActorReplicateList: nil = reload in place
                w.WriteArrayHeader(6);
                BigWorldXRpc.WriteInt(ref w, self.Character.Id);
                BigWorldXRpc.WriteInt(ref w, self.Gender);
                BigWorldXRpc.WriteInt(ref w, self.Id);
                BigWorldXRpc.WriteInt(ref w, self.Character.FashionId);
                BigWorldXRpc.WriteInt(ref w, self.Character.FashionColorId);
                w.WriteRaw(MessagePackSerializer.Serialize(self.PartData));
            });
            session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcSyncPlayerSelfDIY", args, 0));
        }

        [RequestPacketHandler("BigWorldShowRewardFinishRequest")]
        public static void BigWorldShowRewardFinishRequestHandler(Session session, Packet.Request packet)
        {
            // AscNet policy: the client clears its queue before sending; the server drops the persisted pending list.
            BigWorldPlayerState s = session.player.BigWorldState;
            if (s.PendingShowRewards.Count > 0)
            {
                s.PendingShowRewards.Clear();
                session.player.Save();
            }
            session.SendResponse(new BigWorldShowRewardFinishResponse(), packet.Id);
        }
        #endregion

        #region Validation
        private static int ValidateTeam(Session session, BigWorldTeamData? team)
        {
            if (team is null || team.TeamId is < 1 or > TeamCount || team.CharacterList is null)
                return DlcConditionNotFinish;
            if (team.CharacterList.Count == 0 && team.TeamId == session.player.BigWorldState.CurrentTeamId)
                return DlcCharacterGetCharacterByIdNotFound;
            HashSet<int> positions = new(), ids = new();
            foreach (BigWorldTeamCharacter c in team.CharacterList)
            {
                if (c.Pos is < 0 or >= FullTeamEntityCount || !positions.Add(c.Pos))
                    return DlcConditionNotFinish;
                if (!Characters.Value.TryGetValue(c.CharacterId, out BigWorldCharacterTable? row))
                    return DlcCharacterTemplateNotFound;
                if (!ids.Add(c.CharacterId))
                    return DlcCharacterAlreadyFighting;
                // AscNet policy: BigWorldCharacter.Condition (main-game "own character X") is checked as main-game ownership.
                if (row.IsCommandant != 1 && !session.character.Characters.Any(owned => owned.Id == (uint)c.CharacterId))
                    return DlcCharacterGetCharacterByIdNotFound;
            }
            return 0;
        }

        // Check order is AscNet policy; every code is a retail CodeText key.
        private static int ValidateOutfit(Player player, BigWorldCommanderFashionUpdateRequest req)
        {
            if (!Outfits.Value.TryGetValue(req.OutfitType, out BigWorldDIYOutfitTable? outfit))
                return BigWorldDIYOutfitTypeNotFound;
            if (req.Gender is not (1 or 2))
                return DlcFashionSexualParamError;
            if (req.CommanderFashionList is not { Count: > 0 } list)
                return DlcFashionFashionListParamError;
            List<int> bag = player.BigWorldState.CommanderFashionBags;
            List<int> allowed = outfit.AllowFashionIds ?? new();
            foreach ((int typeId, Theatre5BigWorldCommanderFashion wear) in list)
            {
                if (!Types.Value.ContainsKey(typeId))
                    return DlcFashionTypeIdNotFound;
                if (wear is null || !Parts.Value.TryGetValue(wear.PartId, out BigWorldDIYPartTable? part))
                    return DlcFashionPartIdNotFound;
                if (part.TypeId != typeId)
                    return DlcFashionPartIdNotMatchType;
                if (!bag.Contains(wear.PartId))
                    return DlcFashionPartIdNotOwn;
            }
            // Types covered by a worn suit's IncompatibleType count as satisfied (Control:GetDIYInfo drops them).
            HashSet<int> covered = list.Values.SelectMany(w => Parts.Value[w.PartId].IncompatibleType ?? new()).ToHashSet();
            if (Types.Value.Values.Any(t => t.IsRequired == 1 && !list.ContainsKey(t.TypeId) && !covered.Contains(t.TypeId)))
                return DlcLackMustWearPart;
            // IncompatiblePartIds is empty in 4.7 data (column not generated); IncompatibleType covers suits.
            if (list.Any(w => (Parts.Value[w.Value.PartId].IncompatibleType ?? new()).Any(t => t > 0 && t != w.Key && list.ContainsKey(t))))
                return BigWorldDIYFashionPartIdIncompatible;
            foreach ((int typeId, Theatre5BigWorldCommanderFashion wear) in list)
            {
                BigWorldDIYPartTable part = Parts.Value[wear.PartId];
                // Suits resolve their outfit fashion through their component Parts.
                foreach (int partId in (part.Parts ?? new()).Where(id => id > 0).Prepend(part.Id))
                {
                    if (!Parts.Value.TryGetValue(partId, out BigWorldDIYPartTable? component)
                        || !TryGetRes(component, req.Gender, out BigWorldDIYResTable? res))
                        return DlcFashionResIdNotFound;
                    if (res.FashionId != 0 && !allowed.Contains(res.FashionId))
                        return BigWorldDIYOutfitCannotEquip;
                }
                TryGetRes(part, req.Gender, out BigWorldDIYResTable? ownRes);
                if (wear.ColourId != 0)
                {
                    if (ownRes!.ColorGroupId == 0)
                        return DlcFashionNotSupportChangeColor;
                    if (!ColorGroups.Value.TryGetValue(ownRes.ColorGroupId, out List<int>? colors) || !colors.Contains(wear.ColourId))
                        return DlcFashionNotExistColor;
                }
            }
            return 0;
        }
        #endregion

        #region State
        // Seeds DIY (BigWorldDIYBagInit + BigWorldDIYOutfit.DefaultPartIds) and team 1 (BigWorldConfig DefaultTeamPos1..3).
        // Returns true when teams were seeded.
        private static bool Ensure(Player player)
        {
            BigWorldPlayerState s = player.BigWorldState;
            bool changed = false, teamsSeeded = false;
            if (s.CommanderGender == 0)
            {
                // AscNet policy: start from the main-game gender when set, else male (commandant row 2011001 = DefaultTeamPos1).
                s.CommanderGender = player.PlayerData.Gender is 1 or 2 ? (int)player.PlayerData.Gender : 1;
                s.CommanderFashionBags = TableReaderV2.Parse<BigWorldDIYBagInitTable>().OrderBy(r => r.Id).Select(r => r.PartId).Distinct().ToList();
                s.CommanderFashionOutfits = Outfits.Value.Values.ToDictionary(o => o.Id, o => (o.DefaultPartIds ?? new())
                    .Where(id => Parts.Value.ContainsKey(id))
                    .Select(id =>
                    {
                        BigWorldDIYPartTable part = Parts.Value[id];
                        return new BigWorldCommanderPartState
                        {
                            TypeId = part.TypeId,
                            PartId = id,
                            ColourId = TryGetRes(part, s.CommanderGender, out BigWorldDIYResTable? res) ? res.DefaultColorId : 0
                        };
                    }).ToList());
                // AscNet policy: the lowest outfit id is current before the player's first DIY save.
                s.CurCommanderOutfitType = Outfits.Value.Keys.Min();
                changed = true;
            }
            if (s.CurrentTeamId == 0)
            {
                s.CurrentTeamId = 1;
                s.Teams[1] = DefaultTeam.Value.Select((id, pos) => new BigWorldTeamMemberState { CharacterId = id, Pos = pos })
                    .Where(m => m.CharacterId > 0).ToList();
                changed = teamsSeeded = true;
            }
            if (changed)
                player.Save();
            return teamsSeeded;
        }

        private static void EnsureAndNotify(Session session)
        {
            if (Ensure(session.player))
                session.SendPush(new BigWorldTeamNotify
                {
                    Teams = session.player.BigWorldState.Teams.Select(t => ToTeamData(t.Key, t.Value)).ToList()
                });
        }

        private static BigWorldTeamData ToTeamData(int teamId, List<BigWorldTeamMemberState> members) => new()
        {
            TeamId = teamId,
            CharacterList = members.Select(m => new BigWorldTeamCharacter { CharacterId = m.CharacterId, Pos = m.Pos }).ToList()
        };

        private static BigWorldCharacterFashionState Wear(Player player, int characterId)
        {
            Dictionary<int, BigWorldCharacterFashionState> dict = player.BigWorldState.CharacterFashions;
            if (!dict.TryGetValue(characterId, out BigWorldCharacterFashionState? wear))
                dict[characterId] = wear = new BigWorldCharacterFashionState { FashionId = Characters.Value[characterId].DefaultFashionId };
            return wear;
        }

        // BigWorld default fashion first (commandant 9999xxx has no main-game row), else main-game Fashion.CharacterId.
        private static int ResolveFashionCharacter(int fashionId)
        {
            BigWorldCharacterTable? byDefault = Characters.Value.Values.FirstOrDefault(r => r.DefaultFashionId == fashionId);
            if (byDefault is not null)
                return byDefault.Id;
            return MainFashionCharacter.Value.TryGetValue(fashionId, out int characterId) && Characters.Value.ContainsKey(characterId) ? characterId : 0;
        }

        // XBigWorldCharacterAgency:CheckFashionUnlock.
        private static bool FashionUnlocked(Session session, int characterId, int fashionId) =>
            Characters.Value[characterId].DefaultFashionId == fashionId
            || session.character.Fashions.Any(f => f.Id == fashionId && !f.IsLock);

        private static bool TryGetRes(BigWorldDIYPartTable part, int gender, out BigWorldDIYResTable res)
        {
            List<int> resIds = part.ResId ?? new();
            res = null!;
            return gender is 1 or 2 && gender <= resIds.Count && Res.Value.TryGetValue(resIds[gender - 1], out res!);
        }
        #endregion
    }
}
