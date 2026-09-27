using System;
using System.Linq;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.partner;
using AscNet.Table.V2.share.teamrecommend;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateTeamRecommendCompatibility()
    {
        TeamRecommendCharacterTargetTable single = TableReaderV2.Parse<TeamRecommendCharacterTargetTable>()
            .First(row => row.BaseCharacterIds.Count >= 2);
        TeamRecommendBaseFormationTable formation = TableReaderV2.Parse<TeamRecommendBaseFormationTable>()
            .First(row => row.BaseCharacterIds.Count == 3);
        int formationCharacter = TableReaderV2.Parse<TeamRecommendBaseCharacterTable>()
            .Single(row => row.Id == formation.BaseCharacterIds[0]).CharacterId;
        AscNet.Common.Database.Character character = new()
        {
            Uid = 91_800,
            Characters = [new CharacterData { Id = (uint)single.CharacterId },
                new CharacterData { Id = (uint)formationCharacter }],
            Equips = [], Fashions = [], Partners = []
        };
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> saves, out _);
        using LoopbackSessionHarness harness = new(character, sessionId: "team-recommend-test");
        int before = saves.ReplaceOneCalls;

        InvokeRegisteredRequestHandler(nameof(TeamRecommendSetTargetRequest), harness.Session, 18_001,
            new TeamRecommendSetTargetRequest { CharacterId = single.CharacterId, BaseCfgId = single.BaseCharacterIds[0] });
        TeamRecommendTargetResponse selected = (TeamRecommendTargetResponse)ReadResponsePayload(harness,
            18_001, "TeamRecommendSetTargetResponse", "single target", typeof(TeamRecommendTargetResponse));
        AssertEqual(0, selected.Code, "single target set");
        AssertEqual(single.BaseCharacterIds[0], selected.Target!.BaseCharacterId, "authored single target ID");
        character.TeamRecommendFinishEvents.Add(single.CharacterId);
        InvokeRegisteredRequestHandler(nameof(TeamRecommendSetTargetRequest), harness.Session, 18_008,
            new TeamRecommendSetTargetRequest { CharacterId = single.CharacterId, BaseCfgId = single.BaseCharacterIds[0] });
        TeamRecommendTargetResponse repeated = (TeamRecommendTargetResponse)ReadResponsePayload(harness,
            18_008, "TeamRecommendSetTargetResponse", "repeated target", typeof(TeamRecommendTargetResponse));
        AssertEqual(0, repeated.Code, "same goal is idempotent");
        AssertEqual(true, character.TeamRecommendFinishEvents.Contains(single.CharacterId),
            "same goal does not retrigger threshold event");
        AssertEqual(before + 1, saves.ReplaceOneCalls, "same goal does not rewrite account");

        InvokeRegisteredRequestHandler(nameof(TeamRecommendSetFormationTargetRequest), harness.Session, 18_002,
            new TeamRecommendSetFormationTargetRequest { CharacterId = formationCharacter,
                TeamCfgId = formation.FormationId, SourceType = 1, SourceId = formation.Id });
        TeamRecommendTargetResponse team = (TeamRecommendTargetResponse)ReadResponsePayload(harness,
            18_002, "TeamRecommendSetFormationTargetResponse", "configured formation target", typeof(TeamRecommendTargetResponse));
        AssertEqual(0, team.Code, "configured formation set");
        AssertEqual(formation.Id, team.Target!.BaseFormationId, "authored formation selection");
        RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TeamRecommendModule"),
            "SendLoginState", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            [typeof(Session)]).Invoke(null, [harness.Session]);
        NotifyTeamRecommendTargetData loginState = ReadPushPayload<NotifyTeamRecommendTargetData>(
            harness, nameof(NotifyTeamRecommendTargetData), "recommendation login state");
        AssertEqual(2, loginState.CharacterTargets.CharacterTargets.Count, "relogin target notification");
        AssertEqual(before + 2, saves.ReplaceOneCalls, "two target changes saved");
        InvokeRegisteredRequestHandler("TeamRecommendGetAllTargetsRequest", harness.Session, 18_003, null);
        TeamRecommendGetAllTargetsResponse all = (TeamRecommendGetAllTargetsResponse)ReadResponsePayload(harness,
            18_003, "TeamRecommendGetAllTargetsResponse", "all target state", typeof(TeamRecommendGetAllTargetsResponse));
        AssertEqual(2, all.CharacterTargets.CharacterTargets.Count, "two independent targets visible");
        AssertEqual(formation.Id, all.CharacterTargets.CharacterTargets[formationCharacter].BaseFormationId,
            "formation visible in debug target query");
        AscNet.Common.Database.Character relog = BsonSerializer.Deserialize<AscNet.Common.Database.Character>(character.ToBson());
        AssertEqual(2, relog.TeamRecommendTargets.Count, "targets survive BSON round trip");

        InvokeRegisteredRequestHandler(nameof(TeamRecommendSetTargetRequest), harness.Session, 18_004,
            new TeamRecommendSetTargetRequest { CharacterId = single.CharacterId, BaseCfgId = formation.BaseCharacterIds[0] });
        TeamRecommendTargetResponse rejected = (TeamRecommendTargetResponse)ReadResponsePayload(harness,
            18_004, "TeamRecommendSetTargetResponse", "foreign scheme rejected", typeof(TeamRecommendTargetResponse));
        AssertEqual(true, rejected.Code != 0, "foreign scheme not selectable");
        AssertEqual(single.BaseCharacterIds[0], character.TeamRecommendTargets[single.CharacterId].BaseCharacterId,
            "invalid request leaves existing target intact");

        InvokeRegisteredRequestHandler(nameof(TeamRecommendSetFormationTargetRequest), harness.Session, 18_005,
            new TeamRecommendSetFormationTargetRequest { CharacterId = formationCharacter,
                TeamCfgId = formation.FormationId, SourceType = 2, SourceId = formationCharacter });
        TeamRecommendTargetResponse unsupported = (TeamRecommendTargetResponse)ReadResponsePayload(harness,
            18_005, "TeamRecommendSetFormationTargetResponse", "unverified global source rejected", typeof(TeamRecommendTargetResponse));
        AssertEqual(true, unsupported.Code != 0, "global snapshot is not fabricated");

        InvokeRegisteredRequestHandler("TeamRecommendFinishTargetRequest", harness.Session, 18_009,
            new TeamRecommendCharacterRequest { CharacterId = single.CharacterId });
        TeamRecommendResponse unfinished = (TeamRecommendResponse)ReadResponsePayload(harness,
            18_009, "TeamRecommendFinishTargetResponse", "unearned finish rejected", typeof(TeamRecommendResponse));
        AssertEqual(true, unfinished.Code != 0, "unearned completion cannot clear target");
        AssertEqual(true, character.TeamRecommendTargets.ContainsKey(single.CharacterId),
            "target persists after rejected completion");
        InvokeRegisteredRequestHandler("TeamRecommendFinishTargetEventRequest", harness.Session, 18_010,
            new TeamRecommendCharacterRequest { CharacterId = formationCharacter });
        TeamRecommendResponse belowThreshold = (TeamRecommendResponse)ReadResponsePayload(harness,
            18_010, "TeamRecommendFinishTargetEventResponse", "below-threshold event", typeof(TeamRecommendResponse));
        AssertEqual(true, belowThreshold.Code != 0, "unfinished formation cannot claim progress event");
        AssertEqual(false, character.TeamRecommendFinishEvents.Contains(formationCharacter),
            "unearned threshold cannot be persisted");
        TeamRecommendBaseCharacterTable formationMember = TableReaderV2.Parse<TeamRecommendBaseCharacterTable>()
            .Single(row => row.Id == formation.BaseCharacterIds[0]);
        character.Equips.Add(new EquipData
        {
            Id = 1, TemplateId = (uint)formationMember.WeaponId, CharacterId = formationCharacter
        });
        for (int site = 0; site < 6; site++)
            character.Equips.Add(new EquipData
            {
                Id = (uint)(site + 2), TemplateId = (uint)formationMember.EquipIds[site],
                CharacterId = formationCharacter
            });
        character.Partners.Add(new PartnerData
        {
            Id = 1, TemplateId = formationMember.PartnerId, CharacterId = formationCharacter
        });
        int savesBeforeMilestone = saves.ReplaceOneCalls;
        InvokeRegisteredRequestHandler("TeamRecommendFinishTargetEventRequest", harness.Session, 18_011,
            new TeamRecommendCharacterRequest { CharacterId = formationCharacter });
        TeamRecommendResponse earned = (TeamRecommendResponse)ReadResponsePayload(harness,
            18_011, "TeamRecommendFinishTargetEventResponse", "earned 50-percent milestone",
            typeof(TeamRecommendResponse));
        AssertEqual(0, earned.Code, "equipped authored build crosses milestone");
        AssertEqual(true, character.TeamRecommendFinishEvents.Contains(formationCharacter), "milestone persisted");
        AssertEqual(savesBeforeMilestone + 1, saves.ReplaceOneCalls, "milestone saved exactly once");
        InvokeRegisteredRequestHandler("TeamRecommendFinishTargetEventRequest", harness.Session, 18_012,
            new TeamRecommendCharacterRequest { CharacterId = formationCharacter });
        TeamRecommendResponse retried = (TeamRecommendResponse)ReadResponsePayload(harness,
            18_012, "TeamRecommendFinishTargetEventResponse", "retry milestone", typeof(TeamRecommendResponse));
        AssertEqual(0, retried.Code, "milestone retry succeeds");
        AssertEqual(savesBeforeMilestone + 1, saves.ReplaceOneCalls, "milestone retry does not resave");
        InvokeRegisteredRequestHandler("TeamRecommendDeleteTargetRequest", harness.Session, 18_006,
            new TeamRecommendCharacterRequest { CharacterId = single.CharacterId });
        TeamRecommendResponse deleted = (TeamRecommendResponse)ReadResponsePayload(harness,
            18_006, "TeamRecommendDeleteTargetResponse", "target deletion", typeof(TeamRecommendResponse));
        AssertEqual(0, deleted.Code, "existing target deleted");
        AssertEqual(false, character.TeamRecommendTargets.ContainsKey(single.CharacterId), "only selected target removed");
        AssertEqual(true, character.TeamRecommendTargets.ContainsKey(formationCharacter), "other target preserved");
        foreach (int id in Enumerable.Range(1, 5))
            character.TeamRecommendTargets[2_000_000 + id] = new TeamRecommendTargetState { BaseCharacterId = id };
        InvokeRegisteredRequestHandler(nameof(TeamRecommendSetTargetRequest), harness.Session, 18_007,
            new TeamRecommendSetTargetRequest { CharacterId = single.CharacterId, BaseCfgId = single.BaseCharacterIds[1] });
        TeamRecommendTargetResponse capped = (TeamRecommendTargetResponse)ReadResponsePayload(harness,
            18_007, "TeamRecommendSetTargetResponse", "target limit", typeof(TeamRecommendTargetResponse));
        AssertEqual(true, capped.Code != 0, "six-target cap rejects new character goal");
        AssertEqual(false, character.TeamRecommendTargets.ContainsKey(single.CharacterId), "cap does not insert rejected goal");
        EquipData weapon = character.Equips.Single(equip => equip.Id == 1);
        EquipBreakThroughTable weaponMax = TableReaderV2.Parse<EquipBreakThroughTable>()
            .Where(row => row.EquipId == weapon.TemplateId).MaxBy(row => row.Times)!;
        weapon.Breakthrough = weaponMax.Times;
        weapon.Level = weaponMax.LevelLimit;
        weapon.WeaponOverrunData.ChoseSuit = formationMember.WeaponOverrunChoseSuit;
        for (int slot = 0; slot < formationMember.WeaponResonanceSkillIds.Count; slot++)
        {
            int skill = formationMember.WeaponResonanceSkillIds[slot];
            if (skill <= 0) continue;
            weapon.ResonanceInfo.Add(new ResonanceInfo
            {
                Slot = slot + 1, Type = (EquipResonanceType)formationMember.WeaponResonanceTypes[slot],
                CharacterId = formationCharacter, TemplateId = skill
            });
        }
        for (int site = 1; site <= 6; site++)
        {
            EquipData equip = character.Equips.Single(row => row.Id == site + 1);
            EquipBreakThroughTable max = TableReaderV2.Parse<EquipBreakThroughTable>()
                .Where(row => row.EquipId == equip.TemplateId).MaxBy(row => row.Times)!;
            equip.Breakthrough = max.Times;
            equip.Level = max.LevelLimit;
            equip.AwakeSlotList = [1, 2];
            for (int slot = 0; slot < 2; slot++)
            {
                int index = (site - 1) * 2 + slot;
                int type = formationMember.EquipResonanceTypes[index];
                int skill = formationMember.EquipSkillIds[index];
                if (type == (int)EquipResonanceType.CharacterSkill)
                    skill = TableReaderV2.Parse<CharacterSkillPoolTable>()
                        .Find(row => row.Id == skill)?.SkillId ?? skill;
                equip.ResonanceInfo.Add(new ResonanceInfo
                {
                    Slot = slot + 1, Type = (EquipResonanceType)type,
                    CharacterId = type == (int)EquipResonanceType.Attrib ? 0 : formationCharacter,
                    TemplateId = skill
                });
            }
        }
        PartnerData carried = character.Partners.Single(row => row.CharacterId == formationCharacter);
        PartnerBreakThroughTable partnerMax = TableReaderV2.Parse<PartnerBreakThroughTable>()
            .Where(row => row.PartnerId == carried.TemplateId).MaxBy(row => row.BreakTimes)!;
        carried.BreakThrough = partnerMax.BreakTimes;
        carried.Level = partnerMax.LevelLimit;

        saves.ThrowOnReplaceOne = true;
        bool saveFailed = false;
        try
        {
            InvokeRegisteredRequestHandler("TeamRecommendFinishTargetRequest", harness.Session, 18_013,
                new TeamRecommendCharacterRequest { CharacterId = formationCharacter });
        }
        catch (InvalidDataException) { saveFailed = true; }
        finally { saves.ThrowOnReplaceOne = false; }
        AssertEqual(true, saveFailed, "complete target removal requires acknowledged durable save");
        AssertEqual(true, character.TeamRecommendTargets.ContainsKey(formationCharacter), "failed save rolls back target");
        AssertEqual(true, character.TeamRecommendFinishEvents.Contains(formationCharacter),
            "failed save preserves earned event");

        InvokeRegisteredRequestHandler("TeamRecommendFinishTargetRequest", harness.Session, 18_014,
            new TeamRecommendCharacterRequest { CharacterId = formationCharacter });
        TeamRecommendResponse finished = (TeamRecommendResponse)ReadResponsePayload(harness,
            18_014, "TeamRecommendFinishTargetResponse", "completed authored target",
            typeof(TeamRecommendResponse));
        AssertEqual(0, finished.Code, "fully completed configured goal succeeds");
        AssertEqual(false, character.TeamRecommendTargets.ContainsKey(formationCharacter),
            "completed goal removed from durable state");
        AssertEqual(false, character.TeamRecommendFinishEvents.Contains(formationCharacter),
            "completion clears per-goal threshold receipt");
        AscNet.Common.Database.Character finishedRelog =
            BsonSerializer.Deserialize<AscNet.Common.Database.Character>(character.ToBson());
        AssertEqual(false, finishedRelog.TeamRecommendTargets.ContainsKey(formationCharacter),
            "completed goal remains removed on relog");
    }

    private static void ValidateTeamRecommendGlobalStandings()
    {
        TeamRecommendBaseFormationTable roster = TableReaderV2.Parse<TeamRecommendBaseFormationTable>()
            .First(row => row.BaseCharacterIds.Count == 3);
        TeamRecommendFormationTable window = TableReaderV2.Parse<TeamRecommendFormationTable>()
            .Single(row => row.Id == roster.FormationId);
        TeamRecommendBaseCharacterTable[] members = roster.BaseCharacterIds.Select(id =>
            TableReaderV2.Parse<TeamRecommendBaseCharacterTable>().Single(row => row.Id == id)).ToArray();
        uint nextEquip = 1;
        AscNet.Common.Database.Character Account(long uid, int qualityStar, bool partner = false, int? weaponOverride = null)
        {
            AscNet.Common.Database.Character account = new() { Uid = uid, Characters = [], Equips = [], Fashions = [], Partners = [] };
            foreach (TeamRecommendBaseCharacterTable member in members)
            {
                account.Characters.Add(new CharacterData { Id = (uint)member.CharacterId, Level = 80,
                    Quality = qualityStar / 1000, Star = qualityStar % 1000 });
                foreach (int template in new[] { weaponOverride ?? member.WeaponId }.Concat(member.EquipIds.Take(6)))
                    account.Equips.Add(new EquipData { Id = nextEquip++, TemplateId = (uint)template, CharacterId = member.CharacterId });
                if (partner && member.PartnerId > 0)
                    account.Partners.Add(new PartnerData { Id = (int)nextEquip++, TemplateId = member.PartnerId, CharacterId = member.CharacterId });
            }
            return account;
        }
        AscNet.Common.Database.Character self = Account(92_002, window.MinCharacterQualityStar, partner: true);
        AscNet.Common.Database.Character lowerUidTwin = Account(92_001, window.MinCharacterQualityStar);
        AscNet.Common.Database.Character leader = Account(92_003, window.MaxCharacterQualityStar);
        AscNet.Common.Database.Character outOfWindow = Account(92_000, window.MaxCharacterQualityStar + 1);
        AscNet.Common.Database.Character dangling = Account(91_999, window.MaxCharacterQualityStar, weaponOverride: 999_999_999);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characters, out _);
        using LoopbackSessionHarness harness = new(self, sessionId: "team-recommend-global");
        int entry = roster.CharacterId, packetId = 18_100;
        TeamRecommendFormationResponse Query(params int[] teamCfgIds)
        {
            InvokeRegisteredRequestHandler("TeamRecommendFormationRequest", harness.Session, ++packetId,
                new TeamRecommendFormationRequest { CharacterId = entry, TeamCfgIds = [.. teamCfgIds] });
            return (TeamRecommendFormationResponse)ReadResponsePayload(harness, packetId,
                "TeamRecommendFormationResponse", "global standings", typeof(TeamRecommendFormationResponse));
        }

        characters.FindResults = [];
        TeamRecommendFormationResponse empty = Query(roster.FormationId);
        AssertEqual(0, empty.Code, "empty server succeeds");
        AssertEqual(0, empty.TeamRecommendFormations.Count, "empty server has no fabricated standings");
        AssertEqual(true, Query(-1).Code != 0, "unknown team config rejected");
        // xuiteamrecommendmain.lua RequestAllTeamFormationList (first open/guide): every authored
        // TeamRecommendFormation Id, sorted. Old stub answered Code 1; this must succeed with an empty cache.
        int[] firstOpen = TableReaderV2.Parse<TeamRecommendFormationTable>().Select(row => row.Id).Order().ToArray();
        TeamRecommendFormationResponse guide = Query(firstOpen);
        AssertEqual(0, guide.Code, "first-open all-formation request succeeds");
        AssertEqual(0, guide.TeamRecommendFormations.Count, "first-open empty server returns empty fallback map");

        characters.FindResults = [dangling, outOfWindow, self, lowerUidTwin, leader];
        TeamRecommendFormationData top = Query(roster.FormationId).TeamRecommendFormations[roster.FormationId];
        AssertEqual(3, top.CharacterDatas.Count, "full authored roster");
        AssertEqual(window.MaxCharacterQualityStar, top.CharacterDatas[0].CharacterQualityStar,
            "highest in-window quality-star ranks first; out-of-window and dangling gear excluded");
        AssertEqual(members[0].WeaponId, top.CharacterDatas[0].WeaponId, "snapshot shows persisted weapon template");
        AssertEqual(12, top.CharacterDatas[0].EquipResonanceDatas.Count, "twelve awareness resonance slots");

        leader.Equips.First(equip => equip.TemplateId == (uint)members[0].WeaponId).IsRecycle = true;
        TeamRecommendFormationData reloaded = Query(roster.FormationId).TeamRecommendFormations[roster.FormationId];
        AssertEqual(window.MinCharacterQualityStar, reloaded.CharacterDatas[0].CharacterQualityStar,
            "changed loadout drops the former leader");
        AssertEqual(0, reloaded.CharacterDatas.Sum(row => row.PartnerId), "equal builds tie-break to lowest UID (no partner twin)");

        TeamRecommendFormationData spoof = BsonSerializer.Deserialize<TeamRecommendFormationData>(reloaded.ToBson());
        spoof.CharacterDatas[0].CharacterQualityStar = window.MaxCharacterQualityStar;
        InvokeRegisteredRequestHandler(nameof(TeamRecommendSetFormationTargetRequest), harness.Session, ++packetId,
            new TeamRecommendSetFormationTargetRequest { CharacterId = entry, TeamCfgId = roster.FormationId,
                SourceType = 2, SourceId = entry, TargetFormation = spoof });
        AssertEqual(true, ((TeamRecommendTargetResponse)ReadResponsePayload(harness, packetId,
            "TeamRecommendSetFormationTargetResponse", "spoofed snapshot", typeof(TeamRecommendTargetResponse))).Code != 0,
            "client-edited snapshot rejected");
        AssertEqual(false, self.TeamRecommendTargets.ContainsKey(entry), "spoof not persisted");

        TeamRecommendFormationData echo = BsonSerializer.Deserialize<TeamRecommendFormationData>(reloaded.ToBson());
        echo.CharacterDatas.Reverse();
        InvokeRegisteredRequestHandler(nameof(TeamRecommendSetFormationTargetRequest), harness.Session, ++packetId,
            new TeamRecommendSetFormationTargetRequest { CharacterId = entry, TeamCfgId = roster.FormationId,
                SourceType = 2, SourceId = entry, TargetFormation = echo });
        TeamRecommendTargetResponse chosen = (TeamRecommendTargetResponse)ReadResponsePayload(harness, packetId,
            "TeamRecommendSetFormationTargetResponse", "top snapshot target", typeof(TeamRecommendTargetResponse));
        AssertEqual(0, chosen.Code, "current top snapshot selectable");
        AscNet.Common.Database.Character relog = BsonSerializer.Deserialize<AscNet.Common.Database.Character>(self.ToBson());
        AssertEqual(3, relog.TeamRecommendTargets[entry].TargetFormation!.CharacterDatas.Count, "snapshot target survives relog");
        AssertEqual(0, relog.TeamRecommendTargets[entry].BaseFormationId, "snapshot target is not an authored formation");
    }
}
