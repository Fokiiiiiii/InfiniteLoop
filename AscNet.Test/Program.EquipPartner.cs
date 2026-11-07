using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.partner;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateEquipPartnerCompatibility()
    {
        const long playerId = 99_650;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
        Character roster = CreateDrawCompatibilityCharacter(playerId);
        roster.Characters = [new CharacterData { Id = 1011002, Level = 1 }, new CharacterData { Id = 1021001, Level = 1 }];
        roster.Equips = [];
        roster.Partners = [];
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness harness = new(roster, player, inventory, "equip-partner");
        int id = 99_650_000;
        T Call<T>(string name, object request) where T : class
        {
            InvokeRegisteredRequestHandler(name + "Request", harness.Session, ++id, request);
            return ReadResponsePayload<T>(harness, id, name + "Response", name, maxPacketsToRead: 8);
        }
        Character Reload() => BsonSerializer.Deserialize<Character>(harness.Session.character.ToBson());

        // ---- EquipUpdateRecycleRequest ----
        EquipData NewChip(int level = 1)
        {
            EquipTable row = TableReaderV2.Parse<EquipTable>()
                .First(row => row.Site > 0 && row.Star <= 5 && row.Type != 99 && Character.IsOwnableEquipTemplate(row));
            return roster.AddEquip((uint)row.Id, level: level)!;
        }
        EquipUpdateRecycleResponse SetRecycle(uint equipId, bool isRecycle) =>
            Call<EquipUpdateRecycleResponse>("EquipUpdateRecycle", new EquipUpdateRecycleRequest { EquipId = (int)equipId, IsRecycle = isRecycle });

        EquipData chipA = NewChip();
        EquipData chipB = NewChip();
        EquipData lockedChip = NewChip();
        lockedChip.IsLock = true;
        EquipData leveledChip = NewChip(level: 2);
        EquipData wornChip = NewChip();
        wornChip.CharacterId = 1011002;
        EquipData groupedChip = NewChip();
        player.EquipChipGroups.Add(new EquipChipGroupData { GroupId = 1, Name = "g", ChipIdList = [(int)groupedChip.Id] });
        AssertEqual(20021012, SetRecycle(9_999_999, true).Code, "recycle mark on unknown equip");
        foreach ((EquipData chip, string label) in new[] { (lockedChip, "locked"), (leveledChip, "leveled"), (wornChip, "worn"), (groupedChip, "in chip group") })
        {
            AssertEqual(20021019, SetRecycle(chip.Id, true).Code, $"{label} chip cannot be marked");
            AssertEqual(false, chip.IsRecycle, $"{label} chip stays unmarked");
        }
        AssertEqual(0, SetRecycle(chipA.Id, true).Code, "recycle mark set");
        AssertEqual(true, chipA.IsRecycle, "mark applied");
        AssertEqual(false, chipB.IsRecycle, "other chip untouched");
        AssertEqual(0, SetRecycle(chipA.Id, true).Code, "recycle mark idempotent");
        Character reloaded = Reload();
        AssertEqual(false, reloaded.NormalizeEquipsForCurrentTables(), "login normalization keeps a user recycle mark");
        AssertEqual(true, reloaded.Equips.Single(equip => equip.Id == chipA.Id).IsRecycle, "mark survives BSON reload and login");
        AssertEqual(0, SetRecycle(chipB.Id, true).Code, "second chip marked");
        AssertEqual(0, SetRecycle(chipA.Id, false).Code, "recycle mark cleared");
        AssertEqual(false, Reload().Equips.Single(equip => equip.Id == chipA.Id).IsRecycle, "cleared mark persists");
        AssertEqual(true, Reload().Equips.Single(equip => equip.Id == chipB.Id).IsRecycle, "other chip keeps its mark");
        Call<EquipUpdateLockResponse>("EquipUpdateLock", new EquipUpdateLockRequest { EquipId = (int)chipB.Id, IsLock = true });
        AssertEqual(false, chipB.IsRecycle, "locking clears the recycle mark like XEquip:SetLock");

        // ---- EquipTransformChipRequest ----
        int useCount = int.Parse(TableReaderV2.Parse<ConfigTable>().Single(row => row.Key == "EquipTransformChipUseCount").Value);
        EquipSuitTable suit = TableReaderV2.Parse<EquipSuitTable>()
            .First(row => row.EquipIds.Count(templateId => templateId > 0) >= useCount + 1
                && row.EquipIds.Take(useCount + 1).All(templateId => templateId > 0));
        EquipSuitTable otherSuit = TableReaderV2.Parse<EquipSuitTable>()
            .First(row => row.Id != suit.Id && row.EquipIds[0] > 0);
        Type drawManager = typeof(Session).Assembly.GetType("AscNet.GameServer.Game.DrawManager")!;
        BindingFlags nonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo isActive = drawManager.GetMethod("IsActive", nonPublicStatic, [typeof(DrawGroupInfo)])!;
        DrawGroupInfo activeGroup = ((DrawGroupInfo[])drawManager.GetField("GroupTemplates", nonPublicStatic)!.GetValue(null)!)
            .First(group => (bool)isActive.Invoke(null, [group])!);
        EquipData[] Materials(int level, params int[] sites) => sites
            .Select(site => roster.AddEquip((uint)suit.EquipIds[site - 1], level: level)!).ToArray();
        EquipTransformChipResponse Transform(int suitId, int site, List<int>? useIds) =>
            Call<EquipTransformChipResponse>("EquipTransformChip", new EquipTransformChipRequest { SuitId = suitId, Site = site, UseIdList = useIds });
        List<int> Ids(IEnumerable<EquipData> equips) => equips.Select(equip => (int)equip.Id).ToList();

        EquipData[] forSite1 = Materials(1, 2, 3, 4);
        AssertEqual(20021049, Transform(suit.Id, 1, Ids(forSite1)).Code, "suit not advertised by an active draw group");
        activeGroup.TransformSuitList.Add(suit.Id);
        try
        {
            int equipCount = roster.Equips.Count;
            AssertEqual(20021049, Transform(otherSuit.Id, 1, Ids(forSite1)).Code, "other suit still not refittable");
            AssertEqual(20021050, Transform(suit.Id, 0, Ids(forSite1)).Code, "site below range");
            AssertEqual(20021050, Transform(suit.Id, suit.EquipIds.Count + 1, Ids(forSite1)).Code, "site above range");
            AssertEqual(20021051, Transform(suit.Id, 1, null).Code, "nil UseIdList (empty Lua table)");
            AssertEqual(20021051, Transform(suit.Id, 1, Ids(forSite1.Take(useCount - 1))).Code, "too few materials");
            AssertEqual(20021051, Transform(suit.Id, 1, Ids(forSite1).Select((value, index) => index == 0 ? value : Ids(forSite1)[0]).ToList()).Code, "duplicate materials");
            AssertEqual(20021004, Transform(suit.Id, 1, [.. Ids(forSite1.Take(useCount - 1)), 9_999_999]).Code, "unowned material");
            EquipData sameSite = Materials(1, 1)[0];
            AssertEqual(20021052, Transform(suit.Id, 1, [.. Ids(forSite1.Take(useCount - 1)), (int)sameSite.Id]).Code, "material on the target site");
            EquipData foreign = roster.AddEquip((uint)otherSuit.EquipIds[0], level: 1)!;
            AssertEqual(20021052, Transform(suit.Id, 2, [.. Ids(forSite1.Skip(1).Take(useCount - 1)), (int)foreign.Id]).Code, "material of another suit");
            forSite1[0].IsLock = true;
            AssertEqual(20021025, Transform(suit.Id, 1, Ids(forSite1)).Code, "locked material");
            forSite1[0].IsLock = false;
            forSite1[0].CharacterId = 1011002;
            AssertEqual(20021019, Transform(suit.Id, 1, Ids(forSite1)).Code, "worn material");
            forSite1[0].CharacterId = 0;
            AssertEqual(equipCount + 2, roster.Equips.Count, "rejections persist nothing");

            int mainLevel = Math.Min(3, Character.ResolveEquipBreakThrough((uint)suit.EquipIds[0], 0)!.LevelLimit);
            forSite1[0].Level = mainLevel;
            forSite1[0].Exp = 7;
            List<int> consumed = Ids(forSite1);
            int before = roster.Equips.Count;
            InvokeRegisteredRequestHandler("EquipTransformChipRequest", harness.Session, ++id, new EquipTransformChipRequest { SuitId = suit.Id, Site = 1, UseIdList = consumed });
            NotifyEquipDataList push = ReadPushPayload<NotifyEquipDataList>(harness, nameof(NotifyEquipDataList), "transform push", maxPacketsToRead: 8);
            EquipTransformChipResponse made = ReadResponsePayload<EquipTransformChipResponse>(harness, id, "EquipTransformChipResponse", "transform", maxPacketsToRead: 4);
            AssertEqual(0, made.Code, "transform succeeds");
            AssertEqual((uint)suit.EquipIds[0], made.EquipData!.TemplateId, "result is the suit's site-1 chip");
            AssertEqual(mainLevel, made.EquipData.Level, "result inherits the main material level");
            AssertEqual(7, made.EquipData.Exp, "result inherits the main material exp");
            AssertEqual(before - useCount + 1, roster.Equips.Count, "materials consumed, one chip created");
            AssertEqual(true, push.DeletedEquipIdList.Select(value => (int)value).Order().SequenceEqual(consumed.Order()), "push deletes the materials");
            AssertEqual(made.EquipData.Id, push.EquipDataList.Single().Id, "push carries the new chip");
            Character afterReload = Reload();
            AssertEqual(true, afterReload.Equips.Any(equip => equip.Id == made.EquipData.Id && equip.TemplateId == made.EquipData.TemplateId), "result survives reload");
            AssertEqual(false, afterReload.Equips.Any(equip => consumed.Contains((int)equip.Id)), "materials stay consumed after reload");
            AssertEqual(20021004, Transform(suit.Id, 1, consumed).Code, "retry after success finds no materials");

            EquipData[] forSite2 = Materials(1, 1, 3, 4);
            forSite2[0].Level = 1;
            EquipTransformChipResponse second = Transform(suit.Id, 2, Ids(forSite2));
            AssertEqual(0, second.Code, "second transform succeeds");
            AssertEqual((uint)suit.EquipIds[1], second.EquipData!.TemplateId, "target template follows the requested site");
            AssertEqual(1, second.EquipData.Level, "level follows its own main material");
            AssertEqual(true, made.EquipData.TemplateId != second.EquipData.TemplateId, "two sites give two templates");
            AssertEqual(0, inventory.Items.Count, "transform spends no items");
        }
        finally
        {
            activeGroup.TransformSuitList.Remove(suit.Id);
        }

        // ---- PartnerChangeNameRequest ----
        List<(PartnerTable Template, int Quality)> skilled = [];
        foreach (PartnerTable template in TableReaderV2.Parse<PartnerTable>())
        {
            PartnerData probe = PartnerModule.CreatePartner(1, template.Id);
            PartnerQualityTable? quality = TableReaderV2.Parse<PartnerQualityTable>()
                .Find(row => row.PartnerId == template.Id && row.Quality == probe.Quality);
            if (quality is { SkillColumnCount: >= 1 } && probe.SkillList.Any(skill => skill.Type == 2) && probe.UnlockSkillGroup.Count > 0)
                skilled.Add((template, quality.SkillColumnCount));
        }
        AssertEqual(true, skilled.Count >= 2, "two partner templates with passive slots");
        PartnerData partnerA = PartnerModule.CreatePartner(1, skilled[0].Template.Id);
        PartnerData partnerB = PartnerModule.CreatePartner(2, skilled[1].Template.Id);
        roster.Partners = [partnerA, partnerB];

        int nameLimit = int.Parse(TableReaderV2.Parse<ConfigTable>().Single(row => row.Key == "PartnerNameMaxLength").Value);
        PartnerChangeNameResponse Rename(int partnerId, string? name) =>
            Call<PartnerChangeNameResponse>("PartnerChangeName", new PartnerChangeNameRequest { PartnerId = partnerId, Name = name });
        AssertEqual(20114003, Rename(99, "x").Code, "rename unknown partner");
        AssertEqual(20114006, Rename(partnerA.Id, null).Code, "rename nil name");
        AssertEqual(20114006, Rename(partnerA.Id, "   ").Code, "rename blank name");
        AssertEqual(20114004, Rename(partnerA.Id, new string('a', nameLimit + 1)).Code, "rename one past the config limit");
        AssertEqual(20114007, Rename(partnerA.Id, "bad\nname").Code, "rename with control character");
        AssertEqual(true, string.IsNullOrEmpty(partnerA.Name), "rejected renames persist nothing");
        AssertEqual(0, Rename(partnerA.Id, "  " + new string('a', nameLimit) + "  ").Code, "rename at the config limit, trimmed like the client");
        AssertEqual(new string('a', nameLimit), partnerA.Name, "trimmed name stored");
        AssertEqual(20114005, Rename(partnerA.Id, new string('a', nameLimit)).Code, "unchanged name");
        AssertEqual(0, Rename(partnerB.Id, "Bee").Code, "second partner renamed");
        AssertEqual("Bee", Reload().Partners!.Single(partner => partner.Id == partnerB.Id).Name, "name survives reload");
        AssertEqual(new string('a', nameLimit), Reload().Partners!.Single(partner => partner.Id == partnerA.Id).Name, "other partner keeps its name");

        // ---- PartnerMultiCarryAndSkillRequest ----
        PartnerMultiCarryAndSkillResponse Multi(params PartnerMultiCarryAndSkillEntry[]? entries) =>
            Call<PartnerMultiCarryAndSkillResponse>("PartnerMultiCarryAndSkill",
                new PartnerMultiCarryAndSkillRequest { PartnerMultiCarryAndSkillData = entries?.ToList() });
        Dictionary<int, List<int>> SkillData(PartnerData partner, bool withPassive)
        {
            int groupId = partner.UnlockSkillGroup[0];
            Dictionary<int, List<int>> data = new()
            {
                [1] = [TableReaderV2.Parse<PartnerMainSkillGroupTable>().First(group => group.Id == groupId).SkillId[0]]
            };
            if (withPassive)
                data[2] = [partner.SkillList.First(skill => skill.Type == 2).Id];
            return data;
        }
        HashSet<int> WornPassives(PartnerData partner) => partner.SkillList.Where(skill => skill.Type == 2 && skill.IsWear).Select(skill => skill.Id).ToHashSet();
        const int c1 = 1011002, c2 = 1021001;
        foreach (PartnerData partner in roster.Partners)
            foreach (PartnerSkillData skill in partner.SkillList.Where(skill => skill.Type == 2))
                skill.IsWear = false;

        AssertEqual(20114045, Multi(new PartnerMultiCarryAndSkillEntry { CharacterId = 5, Id = partnerA.Id }).Code, "unowned character");
        AssertEqual(20114003, Multi(new PartnerMultiCarryAndSkillEntry { CharacterId = c1, Id = 99 }).Code, "unknown partner");
        AssertEqual(20114040, Multi(new PartnerMultiCarryAndSkillEntry { CharacterId = c1, Id = partnerA.Id }, new PartnerMultiCarryAndSkillEntry { CharacterId = c2, Id = partnerA.Id }).Code, "partner on two characters");
        AssertEqual(20114040, Multi(new PartnerMultiCarryAndSkillEntry { CharacterId = c1, Id = partnerA.Id }, new PartnerMultiCarryAndSkillEntry { CharacterId = c1, Id = partnerB.Id }).Code, "character twice");
        AssertEqual(20114026, Multi(new PartnerMultiCarryAndSkillEntry { CharacterId = c1, Id = partnerA.Id, SkillData = new() { [1] = [int.MaxValue] } }).Code, "unknown main skill");
        int passiveId = partnerA.SkillList.First(skill => skill.Type == 2).Id;
        Dictionary<int, List<int>> overfull = SkillData(partnerA, false);
        overfull[2] = Enumerable.Repeat(passiveId, skilled[0].Quality + 1).ToList();
        AssertEqual(20114030, Multi(new PartnerMultiCarryAndSkillEntry { CharacterId = c1, Id = partnerA.Id, SkillData = overfull }).Code, "more passives than the quality allows");
        AssertEqual(0, partnerA.CharacterId + partnerB.CharacterId, "rejections carry nothing");
        AssertEqual(0, Multi().Code, "nil entry list (empty Lua table) is a no-op");
        AssertEqual(0, Multi(new PartnerMultiCarryAndSkillEntry { CharacterId = 0, Id = partnerA.Id }).Code, "slot without a character is skipped");
        AssertEqual(0, partnerA.CharacterId, "skipped slot carries nothing");

        InvokeRegisteredRequestHandler("PartnerMultiCarryAndSkillRequest", harness.Session, ++id, new PartnerMultiCarryAndSkillRequest
        {
            PartnerMultiCarryAndSkillData =
            [
                new PartnerMultiCarryAndSkillEntry { CharacterId = c1, Id = partnerA.Id, SkillData = SkillData(partnerA, true) },
                new PartnerMultiCarryAndSkillEntry { CharacterId = c2, Id = partnerB.Id, SkillData = null }
            ]
        });
        NotifyPartnerDataList multiPush = ReadPushPayload<NotifyPartnerDataList>(harness, nameof(NotifyPartnerDataList), "multi push");
        AssertEqual(0, ReadResponsePayload<PartnerMultiCarryAndSkillResponse>(harness, id, "PartnerMultiCarryAndSkillResponse", "multi", 4).Code, "multi carry and skill applied");
        AssertEqual(c1, partnerA.CharacterId, "partner A carried by character 1");
        AssertEqual(c2, partnerB.CharacterId, "partner B carried by character 2 without skill data");
        AssertEqual(true, WornPassives(partnerA).SequenceEqual([passiveId]), "requested passive worn");
        AssertEqual(0, WornPassives(partnerB).Count, "empty skill data leaves skills untouched");
        AssertEqual(true, multiPush.PartnerDataList.Select(partner => partner.Id).Order().SequenceEqual([partnerA.Id, partnerB.Id]), "push carries both partners");
        Character multiReload = Reload();
        AssertEqual(c1, multiReload.Partners!.Single(partner => partner.Id == partnerA.Id).CharacterId, "carry survives reload");
        AssertEqual(true, WornPassives(multiReload.Partners!.Single(partner => partner.Id == partnerA.Id)).SequenceEqual([passiveId]), "skills survive reload");

        AssertEqual(0, Multi(
            new PartnerMultiCarryAndSkillEntry { CharacterId = c1, Id = partnerB.Id, SkillData = SkillData(partnerB, false) },
            new PartnerMultiCarryAndSkillEntry { CharacterId = c2, Id = 0 }).Code, "second layout applied");
        AssertEqual(0, partnerA.CharacterId, "displaced partner released");
        AssertEqual(c1, partnerB.CharacterId, "partner B moved to character 1");
        AssertEqual(0, WornPassives(partnerB).Count, "second input wears no passive");
        AssertEqual(true, WornPassives(partnerA).SequenceEqual([passiveId]), "released partner keeps its skills");
        AssertEqual(0, Multi(
            new PartnerMultiCarryAndSkillEntry { CharacterId = c1, Id = partnerB.Id, SkillData = SkillData(partnerB, false) },
            new PartnerMultiCarryAndSkillEntry { CharacterId = c2, Id = 0 }).Code, "same layout is idempotent");
        AssertEqual(c1, partnerB.CharacterId, "idempotent carry unchanged");
    }
}
