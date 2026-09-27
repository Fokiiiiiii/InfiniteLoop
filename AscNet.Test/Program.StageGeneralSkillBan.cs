using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.character;
using System.Reflection;
using AscNet.GameServer;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.fuben;

namespace AscNet.Test;

internal partial class Program
{
    /// <summary>
    /// 4.8 Stage.IsBanGeneralSkill (xfuben/XFubenAgency.lua:1062): a banned stage must not
    /// receive the selected general-skill fight event, while the adjacent unbanned stage does.
    /// Self-contained; wired by the integration owner.
    /// </summary>
    private static void ValidateStageGeneralSkillBanCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        const long playerId = 48_801;
        const uint bannedStageId = 10420118; // new in 4.8 Stage.json with IsBanGeneralSkill
        const uint allowedStageId = 10420117;
        List<StageTable> stages = TableReaderV2.Parse<StageTable>();
        AssertEqual(true, Convert.ToInt32(stages.Single(s => (uint)s.StageId == bannedStageId).IsBanGeneralSkill) != 0,
            "Stage.tsv 4.8 banned general-skill stage");
        AssertEqual(0, Convert.ToInt32(stages.Single(s => (uint)s.StageId == allowedStageId).IsBanGeneralSkill),
            "Stage.tsv adjacent stage allows general skill");
        // Same authored selectable fixture as ValidateVersion47GeneralSkillPreFightCompatibility:
        // a general skill with a fight event, satisfied by a deployed character owning its required skill.
        CharacterGeneralSkillTable skill = TableReaderV2.Parse<CharacterGeneralSkillTable>()
            .First(row => row.FightEventId > 0 && row.SkillId.Any(id => id > 0));
        int requiredSkillIndex = skill.SkillId.FindIndex(id => id > 0);
        int requiredSkillId = skill.SkillId[requiredSkillIndex];
        int requiredSkillLevel = skill.SkillLevel[requiredSkillIndex];
        List<CharacterSkillGroupTable> skillGroups = TableReaderV2.Parse<CharacterSkillGroupTable>();
        CharacterSkillTable skillRow = TableReaderV2.Parse<CharacterSkillTable>()
            .First(row => row.SkillGroupId.Any(groupId =>
                skillGroups.Find(group => group.Id == groupId)?.SkillId.Contains(requiredSkillId) == true));
        int eventId = (int)RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.FightModule"),
            "GeneralSkillFightEventId", BindingFlags.Static | BindingFlags.NonPublic, [typeof(int)])
            .Invoke(null, [skill.Id])!;
        AssertEqual(true, eventId > 0, $"GeneralSkill {skill.Id} fight event");

        AscNet.Common.Database.Character roster = CreateDrawCompatibilityCharacter(playerId);
        roster.Characters =
        [
            new CharacterData
            {
                Id = (uint)skillRow.CharacterId,
                Level = 80,
                SkillList = [new CharacterSkill { Id = (uint)requiredSkillId, Level = requiredSkillLevel }]
            }
        ];
        using LoopbackSessionHarness harness = new(
            roster, CreateDrawCompatibilityPlayer(playerId),
            CreateDrawCompatibilityInventory(playerId, []), "stage-general-skill-ban");
        bool HasEvent(uint stageId, int packetId)
        {
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, packetId, new PreFightRequest
            {
                PreFightData = new() { StageId = stageId, CardIds = [(uint)skillRow.CharacterId], GeneralSkill = skill.Id }
            });
            PreFightResponse response = ReadResponsePayload<PreFightResponse>(
                harness, packetId, nameof(PreFightResponse), $"GeneralSkill stage {stageId} PreFight");
            AssertEqual(0, response.Code, $"GeneralSkill stage {stageId} PreFight code");
            return response.FightData.EventIds.Select(Convert.ToInt32).Contains(eventId);
        }
        AssertEqual(true, HasEvent(allowedStageId, 48_811), "Unbanned stage appends general-skill event");
        AssertEqual(false, HasEvent(bannedStageId, 48_812), "IsBanGeneralSkill stage drops general-skill event");
    }
}
