using System.Globalization;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.client.teamrecommend;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.partner;
using AscNet.Table.V2.share.teamrecommend;

namespace AscNet.GameServer.Handlers;

internal static class TeamRecommendProgress
{
    private static readonly string[] WeightKeys = ["CharacterOwn", "CharacterLevel", "CharacterSkill",
        "WeaponWear", "WeaponLevel", "WeaponResonance", "WeaponOverrun", "AwarenessWear",
        "AwarenessLevel", "AwarenessResonance", "AwarenessOverclock", "PartnerWear", "PartnerLevel"];
    private static readonly Lazy<Dictionary<string, int>> Weights = new(() =>
        TableReaderV2.Parse<TeamRecommendProgressWeightTable>()
            .ToDictionary(row => row.Key, row => row.Weight));

    internal static double? Calculate(Session session, int characterId, TeamRecommendTargetState target)
    {
        if (target.BaseCharacterId > 0 && (target.TeamCfgId > 0 || target.BaseFormationId > 0))
            return null;
        TeamRecommendBaseCharacterTable? cfg = target.BaseCharacterId > 0
            ? TableReaderV2.Parse<TeamRecommendBaseCharacterTable>()
                .Find(row => row.Id == target.BaseCharacterId && row.CharacterId == characterId)
            : target.TeamCfgId > 0 && target.BaseFormationId > 0
                ? ResolveFormation(characterId, target)
                : target.TeamCfgId > 0 && target.TargetFormation is not null
                    ? FromSnapshot(target.TargetFormation, characterId)
                    : null;
        // Snapshot targets already carry final character-skill ids; authored rows carry pool ids.
        bool finalSkillIds = target.BaseCharacterId <= 0 && target.BaseFormationId <= 0;
        if (cfg is null || characterId <= 0) return null;

        Dictionary<string, int> weights = Weights.Value;
        if (WeightKeys.Any(key => !weights.TryGetValue(key, out int weight) || weight < 0)) return null;
        // The installed table assigns zero to these two weights. The client's enhance-skill
        // visibility gate is not persisted; never grant nonzero character-skill credit blindly.
        if (weights["CharacterLevel"] != 0 || weights["CharacterSkill"] != 0) return null;

        double earned = 0, total = 0;
        void Add(string key, double fraction)
        {
            int weight = weights[key];
            total += weight;
            earned += weight * fraction;
        }

        bool owns = session.character.Characters.Any(row => row.Id == characterId);
        Add("CharacterOwn", owns ? 1 : 0);
        Add("CharacterLevel", 0);
        Add("CharacterSkill", 0);

        List<EquipTable> templates = TableReaderV2.Parse<EquipTable>();
        EquipTable? weaponTemplate = templates.Find(row => row.Id == cfg.WeaponId && row.Site == 0);
        if (cfg.WeaponId > 0)
        {
            if (weaponTemplate is null) return null;
            EquipData? weapon = Worn(session.character, characterId, 0, templates, out bool ambiguous);
            if (ambiguous) return null;
            bool worn = weapon?.TemplateId == cfg.WeaponId;
            Add("WeaponWear", worn ? 1 : 0);
            Add("WeaponLevel", worn && IsMaxEquip(weapon!) ? 1 : 0);

            int targetCount = 0, matched = 0;
            Dictionary<(int Type, int Skill), int> actual = new();
            if (worn && weaponTemplate.Star != 5)
                actual = Resonances(weapon!, characterId);
            for (int slot = 0; slot < 3; slot++)
            {
                int skill = cfg.WeaponResonanceSkillIds.ElementAtOrDefault(slot);
                int type = cfg.WeaponResonanceTypes.ElementAtOrDefault(slot);
                if (skill <= 0 || type <= 0) continue;
                targetCount++;
                if (!worn) continue;
                if (weaponTemplate.Star == 5)
                {
                    if (weapon!.ResonanceInfo?.Any(row => row.Slot == slot + 1) == true) matched++;
                }
                else if (actual.TryGetValue((type, skill), out int count) && count > 0)
                {
                    actual[(type, skill)] = count - 1;
                    matched++;
                }
            }
            if (targetCount > 0) Add("WeaponResonance", (double)matched / targetCount);

            if (cfg.WeaponOverrunChoseSuit > 0)
            {
                int suitId = cfg.WeaponOverrunChoseSuit;
                bool overrun = false;
                if (worn && weapon!.WeaponOverrunData?.ChoseSuit == suitId)
                {
                    // Suit 1/2 are the client default types; every other suit derives its
                    // compatible character type from the first authored suit equipment.
                    int? suitType = suitId is 1 or 2 ? suitId : templates
                        .Where(row => row.SuitId == suitId)
                        .Select(row => (int?)row.CharacterType).FirstOrDefault();
                    if (suitType is null) return null;
                    CharacterTable? characterTemplate = TableReaderV2.Parse<CharacterTable>()
                        .Find(row => row.Id == characterId);
                    if (characterTemplate is null) return null;
                    overrun = suitType == 0 || suitType == characterTemplate.Type;
                }
                Add("WeaponOverrun", overrun ? 1 : 0);
            }
        }

        int minAwakeStar = int.TryParse(TableReaderV2.Parse<ConfigTable>()
            .Find(row => row.Key == "MinEquipAwakeStar")?.Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int star) ? star : 0;
        if (minAwakeStar <= 0) return null;
        int wornCount = 0, levelCount = 0, resonanceCount = 0, awakeCount = 0;
        for (int site = 1; site <= 6; site++)
        {
            int templateId = cfg.EquipIds.ElementAtOrDefault(site - 1);
            EquipTable? template = templates.Find(row => row.Id == templateId && row.Site == site);
            if (template is null) return null;
            EquipData? equip = Worn(session.character, characterId, site, templates, out bool ambiguous);
            if (ambiguous) return null;
            bool worn = equip?.TemplateId == templateId;
            if (worn) wornCount++;
            if (worn && IsMaxEquip(equip!)) levelCount++;

            EquipResonanceTable? resonance = TableReaderV2.Parse<EquipResonanceTable>()
                .Find(row => row.Id == templateId);
            if (resonance is null) return null;
            bool canResonate = resonance.AttribPoolId.Any(id => id > 0)
                || resonance.CharacterSkillPoolId.Any(id => id > 0)
                || resonance.WeaponSkillPoolId.Any(id => id > 0);
            if (!canResonate) resonanceCount += 2;
            else if (worn)
            {
                Dictionary<(int Type, int Skill), int> actual = Resonances(equip!, characterId);
                for (int pos = 0; pos < 2; pos++)
                {
                    int index = (site - 1) * 2 + pos;
                    int type = cfg.EquipResonanceTypes.ElementAtOrDefault(index);
                    int skill = cfg.EquipSkillIds.ElementAtOrDefault(index);
                    if (skill <= 0 || type <= 0) continue;
                    if (type == (int)EquipResonanceType.CharacterSkill && !finalSkillIds)
                        skill = TableReaderV2.Parse<CharacterSkillPoolTable>()
                            .Find(row => row.Id == skill)?.SkillId ?? skill;
                    if (actual.TryGetValue((type, skill), out int count) && count > 0)
                    {
                        actual[(type, skill)] = count - 1;
                        resonanceCount++;
                    }
                }
            }

            if (template.Star < minAwakeStar) awakeCount += 2;
            else if (worn)
            {
                for (int pos = 1; pos <= 2; pos++)
                    if (equip!.AwakeSlotList?.Any(value =>
                        Convert.ToInt32((object)value, CultureInfo.InvariantCulture) == pos) == true)
                        awakeCount++;
            }
        }
        Add("AwarenessWear", wornCount / 6.0);
        Add("AwarenessLevel", levelCount / 6.0);
        Add("AwarenessResonance", resonanceCount / 12.0);
        Add("AwarenessOverclock", awakeCount / 12.0);

        if (cfg.PartnerId > 0)
        {
            PartnerData? partner = null;
            foreach (PartnerData entry in session.character.Partners)
            {
                if (entry.CharacterId != characterId) continue;
                if (partner is not null) return null;
                partner = entry;
            }
            bool carrying = partner?.TemplateId == cfg.PartnerId;
            Add("PartnerWear", carrying ? 1 : 0);
            PartnerBreakThroughTable? maxRow = null;
            foreach (PartnerBreakThroughTable row in TableReaderV2.Parse<PartnerBreakThroughTable>())
                if (row.PartnerId == cfg.PartnerId
                    && (maxRow is null || row.BreakTimes > maxRow.BreakTimes))
                    maxRow = row;
            if (maxRow is null) return null;
            int maxBreak = maxRow.BreakTimes;
            Add("PartnerLevel", carrying && partner!.BreakThrough >= maxBreak
                && partner.Level >= maxRow!.LevelLimit ? 1 : 0);
        }
        return total > 0 ? Math.Min(earned / total, 1) : null;
    }

    private static TeamRecommendBaseCharacterTable? ResolveFormation(int characterId, TeamRecommendTargetState target)
    {
        TeamRecommendBaseFormationTable? formation = TableReaderV2.Parse<TeamRecommendBaseFormationTable>()
            .Find(row => row.Id == target.BaseFormationId && row.FormationId == target.TeamCfgId);
        if (formation is null) return null;
        return TableReaderV2.Parse<TeamRecommendBaseCharacterTable>()
            .Find(row => row.CharacterId == characterId && formation.BaseCharacterIds.Contains(row.Id));
    }

    private static TeamRecommendBaseCharacterTable? FromSnapshot(TeamRecommendFormationData formation, int characterId)
    {
        TeamRecommendCharacterData? row = formation.CharacterDatas.Find(data => data.CharacterId == characterId);
        if (row is null) return null;
        List<int> weaponTypes = [0, 0, 0], weaponSkills = [0, 0, 0];
        foreach (TeamRecommendResonanceData resonance in row.WeaponResonanceDatas)
            if (resonance.Slot is >= 1 and <= 3)
                (weaponTypes[resonance.Slot - 1], weaponSkills[resonance.Slot - 1]) = (resonance.Type, resonance.TemplateId);
        return new TeamRecommendBaseCharacterTable
        {
            CharacterId = row.CharacterId,
            WeaponId = row.WeaponId,
            WeaponOverrunChoseSuit = row.WeaponOverrunChoseSuit,
            WeaponResonanceTypes = weaponTypes,
            WeaponResonanceSkillIds = weaponSkills,
            EquipIds = [.. row.EquipIds],
            // Snapshot rows are emitted site-major (site 1 slot 1, site 1 slot 2, ...).
            EquipResonanceTypes = row.EquipResonanceDatas.Select(data => data.Type).ToList(),
            EquipSkillIds = row.EquipResonanceDatas.Select(data => data.TemplateId).ToList(),
            PartnerId = row.PartnerId
        };
    }

    internal static EquipData? Worn(AscNet.Common.Database.Character character, int characterId, int site,
        List<EquipTable> templates, out bool ambiguous)
    {
        EquipData? result = null;
        ambiguous = false;
        foreach (EquipData equip in character.Equips)
        {
            if (equip.CharacterId != characterId || equip.IsRecycle) continue;
            if (templates.Find(row => row.Id == equip.TemplateId)?.Site != site) continue;
            if (result is not null) { ambiguous = true; return null; }
            result = equip;
        }
        return result;
    }

    private static bool IsMaxEquip(EquipData equip)
    {
        EquipBreakThroughTable? maxRow = null;
        foreach (EquipBreakThroughTable row in TableReaderV2.Parse<EquipBreakThroughTable>())
            if (row.EquipId == equip.TemplateId && (maxRow is null || row.Times > maxRow.Times))
                maxRow = row;
        return maxRow is not null && equip.Breakthrough >= maxRow.Times && equip.Level >= maxRow.LevelLimit;
    }

    private static Dictionary<(int Type, int Skill), int> Resonances(EquipData equip, int characterId)
    {
        Dictionary<(int Type, int Skill), int> counts = new();
        foreach (ResonanceInfo row in equip.ResonanceInfo ?? [])
        {
            if (row.TemplateId <= 0 || row.CharacterId > 0 && row.CharacterId != characterId) continue;
            var key = ((int)row.Type, row.TemplateId);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts;
    }
}
