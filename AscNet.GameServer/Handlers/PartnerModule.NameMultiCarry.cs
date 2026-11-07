using System.Globalization;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.partner;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    [MessagePackObject(true)]
    public class PartnerChangeNameRequest
    {
        public int PartnerId;
        public string? Name;
    }

    [MessagePackObject(true)]
    public class PartnerChangeNameResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class PartnerMultiCarryAndSkillEntry
    {
        public int CharacterId;
        public int Id;
        // Main skill list under key 1, passive list under key 2; the client sends nil for an empty table.
        public Dictionary<int, List<int>>? SkillData;
    }

    [MessagePackObject(true)]
    public class PartnerMultiCarryAndSkillRequest
    {
        public List<PartnerMultiCarryAndSkillEntry>? PartnerMultiCarryAndSkillData;
    }

    [MessagePackObject(true)]
    public class PartnerMultiCarryAndSkillResponse
    {
        public int Code;
    }

    internal partial class PartnerModule
    {
        private const int PartnerGetPartnerNotFound = 20114003;
        private const int PartnerChangeNameIsTooLong = 20114004;
        private const int PartnerChangeNameIsNotChange = 20114005;
        private const int PartnerChangeNameIsNull = 20114006;
        private const int PartnerChangeNameIsInvalid = 20114007;
        private const int PartnerSkillIdIsNotActivate = 20114026;
        private const int PartnerSkillPosHaveNotUnlock = 20114030;
        private const int PartnerCarryRepeat = 20114040;
        private const int PartnerCanNotSetWhenCharacterIsNull = 20114045;

        [RequestPacketHandler("PartnerChangeNameRequest")]
        public static void PartnerChangeNameRequestHandler(Session session, Packet.Request packet)
        {
            PartnerChangeNameRequest request = packet.Deserialize<PartnerChangeNameRequest>();
            PartnerData? partner = FindPartner(session, request.PartnerId);
            string name = request.Name?.Trim() ?? "";
            ConfigTable? limitRow = TableReaderV2.Parse<ConfigTable>()
                .Find(row => row.Key == "PartnerNameMaxLength");

            int code = 0;
            if (partner is null)
                code = PartnerGetPartnerNotFound;
            else if (name.Length == 0)
                code = PartnerChangeNameIsNull;
            else if (limitRow is null
                || !int.TryParse(limitRow.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxLength)
                || name.EnumerateRunes().Count() > maxLength)
                code = PartnerChangeNameIsTooLong;
            else if (name.Any(char.IsControl))
                code = PartnerChangeNameIsInvalid;
            else if (name == partner.Name)
                code = PartnerChangeNameIsNotChange;

            if (code != 0)
            {
                session.SendResponse(new PartnerChangeNameResponse { Code = code }, packet.Id);
                return;
            }

            partner!.Name = name;
            session.character.Save();
            SendPartnerUpdate(session, partner);
            session.SendResponse(new PartnerChangeNameResponse(), packet.Id);
        }

        [RequestPacketHandler("PartnerMultiCarryAndSkillRequest")]
        public static void PartnerMultiCarryAndSkillRequestHandler(Session session, Packet.Request packet)
        {
            PartnerMultiCarryAndSkillRequest request = packet.Deserialize<PartnerMultiCarryAndSkillRequest>();
            void Fail(int code) => session.SendResponse(new PartnerMultiCarryAndSkillResponse { Code = code }, packet.Id);

            // Slots without a character carry nothing to apply.
            List<PartnerMultiCarryAndSkillEntry> entries = (request.PartnerMultiCarryAndSkillData ?? [])
                .Where(entry => entry is { CharacterId: not 0 })
                .ToList();
            List<PartnerTable> partnerTables = TableReaderV2.Parse<PartnerTable>();
            HashSet<int> characterIds = [];
            HashSet<int> partnerIds = [];
            List<(int CharacterId, PartnerData Partner, Dictionary<int, List<int>>? SkillData)> plan = [];
            foreach (PartnerMultiCarryAndSkillEntry entry in entries)
            {
                if (!session.character.Characters.Any(character => character.Id == entry.CharacterId))
                {
                    Fail(PartnerCanNotSetWhenCharacterIsNull);
                    return;
                }
                if (!characterIds.Add(entry.CharacterId) || entry.Id != 0 && !partnerIds.Add(entry.Id))
                {
                    Fail(PartnerCarryRepeat);
                    return;
                }
                if (entry.Id == 0)
                    continue;

                PartnerData? partner = FindPartner(session, entry.Id);
                if (partner is null || !partnerTables.Any(row => row.Id == partner.TemplateId))
                {
                    Fail(PartnerGetPartnerNotFound);
                    return;
                }
                // An empty preset (nil on the wire) carries without touching skills.
                if (entry.SkillData is { Count: > 0 }
                    && !FightModule.IsValidPartnerSkillData(partner, entry.SkillData, out FightModule.TeamPrefabValidationFailure failure))
                {
                    Fail(failure == FightModule.TeamPrefabValidationFailure.TooManyPartnerPassiveSkills
                        ? PartnerSkillPosHaveNotUnlock
                        : PartnerSkillIdIsNotActivate);
                    return;
                }
                plan.Add((entry.CharacterId, partner, entry.SkillData));
            }

            Dictionary<int, int> desiredPartnerByCharacter = plan.ToDictionary(item => item.CharacterId, item => item.Partner.Id);
            HashSet<int> affected = [];
            bool carryChanged = false;
            foreach (PartnerData partner in session.character.Partners ?? [])
            {
                if (partner.CharacterId <= 0
                    || !characterIds.Contains(partner.CharacterId)
                    || desiredPartnerByCharacter.GetValueOrDefault(partner.CharacterId) == partner.Id)
                    continue;

                partner.CharacterId = 0;
                session.character.NormalizePartnerMainSkillForCarrier(partner);
                affected.Add(partner.Id);
                carryChanged = true;
            }
            foreach ((int characterId, PartnerData partner, Dictionary<int, List<int>>? skillData) in plan)
            {
                if (partner.CharacterId != characterId)
                {
                    partner.CharacterId = characterId;
                    carryChanged = true;
                }
                if (skillData is { Count: > 0 })
                    FightModule.ApplyTeamPrefabPartnerSkills(session.character, partner, skillData);
                else
                    session.character.NormalizePartnerMainSkillForCarrier(partner);
                affected.Add(partner.Id);
            }

            session.character.Save();
            if (affected.Count > 0)
            {
                session.SendPush(new NotifyPartnerDataList
                {
                    PartnerDataList = session.character.Partners!
                        .Where(partner => affected.Contains(partner.Id))
                        .OrderBy(partner => partner.Id)
                        .ToList(),
                    OperateTypes = carryChanged ? [2, 3] : [2]
                });
            }
            session.SendResponse(new PartnerMultiCarryAndSkillResponse(), packet.Id);
        }
    }
}
