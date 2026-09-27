using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.equip;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateEquipmentSlotNotifications()
    {
        var equipment = TableReaderV2.Parse<EquipTable>().ToDictionary(row => row.Id);
        var owners = TableReaderV2.Parse<CharacterTable>()
            .Where(row => equipment.TryGetValue(row.EquipId, out var weapon)
                && weapon.Site == 0 && weapon.CharacterId == 0 && Character.IsOwnableEquipTemplate(weapon))
            .GroupBy(row => row.EquipType).First(group => group.Count() >= 3).Take(3).ToArray();
        const long uid = 998_481;
        Character character = new()
        {
            Uid = uid,
            Characters = owners.Select(row => new CharacterData { Id = (uint)row.Id }).ToList(),
            Equips =
            [
                new() { Id = 1, TemplateId = (uint)owners[0].EquipId, CharacterId = owners[0].Id },
                new() { Id = 2, TemplateId = (uint)owners[1].EquipId, CharacterId = owners[1].Id },
                new() { Id = 3, TemplateId = (uint)owners[0].EquipId }
            ]
        };
        Player player = CreateDrawCompatibilityPlayer(uid);
        int teamMaxPosition = TableReaderV2.Parse<TeamConfigTable>()
            .ToDictionary(row => row.Key, row => row.Value)["TeamMaxPos"];
        TeamPrefabData presetOntoWeaponlessCharacter = new()
        {
            TeamType = TeamKind.Prefab,
            TeamId = 1,
            CaptainPos = 1,
            FirstFightPos = 1,
            TeamName = "Weaponless Transfer",
            TeamData = Enumerable.Range(1, teamMaxPosition)
                .ToDictionary(position => position, position => position == 1 ? owners[2].Id : 0),
            EquipData = new()
            {
                [1] = new TeamPrefabEquipData
                {
                    EquipDataDict = new() { [0] = new TeamPrefabEquipEntry { EquipId = 2 } }
                }
            }
        };
        player.TeamPrefabs = [presetOntoWeaponlessCharacter];
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<Character> saved, out _);
        using LoopbackSessionHarness harness = new(character, player, CreateDrawCompatibilityInventory(uid, []));
        foreach (int selected in new[] { 3, 2 })
        {
            InvokeRequestHandler(harness, nameof(EquipPutOnRequest), 848_100 + selected,
                new EquipPutOnRequest { CharacterId = owners[0].Id, EquipId = selected, Site = 0 });
            NotifyEquipDataList push = ReadPushPayload<NotifyEquipDataList>(
                harness, nameof(NotifyEquipDataList), "weapon slot snapshot before acknowledgement");
            AssertEqual(owners[0].Id, push.EquipDataList.Single(row => row.Id == selected).CharacterId,
                "replacement weapon is in the slot snapshot");
            AssertEqual(0, ReadResponsePayload<EquipPutOnResponse>(harness, 848_100 + selected,
                nameof(EquipPutOnResponse), "weapon put-on").Code, "weapon put-on succeeds");
            if (selected == 2)
                AssertEqual(owners[1].Id, push.EquipDataList.Single(row => row.Id == 3).CharacterId,
                    "cross-character transfer pushes the displaced weapon onto its previous owner");
            Character reloaded = BsonSerializer.Deserialize<Character>(saved.LastSuccessfulReplacementBson!);
            AssertEqual(1, reloaded.Equips.Count(row => row.CharacterId == owners[0].Id),
                "the receiving character retains one persisted weapon");
            AssertEqual(1, reloaded.Equips.Count(row => row.CharacterId == owners[1].Id),
                "the donor character retains one persisted weapon");
            AssertEqual(0, reloaded.Equips.Count(row => row.CharacterId == owners[2].Id),
                "the weaponless character owns no persisted weapon");
        }

        // Equip 2 is worn by owners[0] and owners[2] never had a weapon of that slot, so moving it there
        // has no replacement weapon to hand back: both request paths must reject before mutating.
        int savesBeforeRejection = saved.ReplaceOneCalls;
        string stateBeforeRejection = Convert.ToHexString(character.ToBson());
        InvokeRequestHandler(harness, nameof(EquipPutOnRequest), 848_120,
            new EquipPutOnRequest { CharacterId = owners[2].Id, EquipId = 2, Site = 0 });
        AssertEqual(20021012, ReadResponsePayload<EquipPutOnResponse>(harness, 848_120,
            nameof(EquipPutOnResponse), "weapon transfer onto a weaponless character").Code,
            "a worn weapon cannot move to a character with no replacement weapon");
        AssertEqual(owners[0].Id, character.Equips.Single(row => row.Id == 2).CharacterId,
            "rejected transfer leaves the weapon with its previous owner");
        AssertEqual(0, character.Equips.Count(row => row.CharacterId == owners[2].Id),
            "rejected transfer equips nothing on the weaponless character");
        AssertEqual(savesBeforeRejection, saved.ReplaceOneCalls,
            "rejected transfer does not persist Character");
        AssertEqual(stateBeforeRejection, Convert.ToHexString(character.ToBson()),
            "rejected transfer mutates no equipment assignment");
        AssertNoPendingPacket(harness, "rejected weapon transfer");

        // A spare weapon on the donor does not make the transfer safe: the pushed snapshot clears the
        // slot the weapon left and never reinstalls the spare, so the same rejection has to hold.
        character.Equips.Add(new() { Id = 4, TemplateId = (uint)owners[0].EquipId, CharacterId = owners[0].Id });
        savesBeforeRejection = saved.ReplaceOneCalls;
        stateBeforeRejection = Convert.ToHexString(character.ToBson());
        InvokeRequestHandler(harness, nameof(EquipPutOnRequest), 848_125,
            new EquipPutOnRequest { CharacterId = owners[2].Id, EquipId = 4, Site = 0 });
        AssertEqual(20021012, ReadResponsePayload<EquipPutOnResponse>(harness, 848_125,
            nameof(EquipPutOnResponse), "spare weapon transfer onto a weaponless character").Code,
            "a spare weapon does not make the previous owner a valid donor");
        AssertEqual(owners[0].Id, character.Equips.Single(row => row.Id == 4).CharacterId,
            "rejected spare transfer leaves the spare weapon with its previous owner");
        AssertEqual(2, character.Equips.Count(row => row.CharacterId == owners[0].Id),
            "rejected spare transfer keeps both donor weapons");
        AssertEqual(savesBeforeRejection, saved.ReplaceOneCalls,
            "rejected spare transfer does not persist Character");
        AssertEqual(stateBeforeRejection, Convert.ToHexString(character.ToBson()),
            "rejected spare transfer mutates no equipment assignment");
        AssertNoPendingPacket(harness, "rejected spare-donor transfer");

        savesBeforeRejection = saved.ReplaceOneCalls;
        stateBeforeRejection = Convert.ToHexString(character.ToBson());
        InvokeRequestHandler(harness, nameof(TeamPrefabApplyRequest), 848_130,
            new TeamPrefabApplyRequest { TeamId = presetOntoWeaponlessCharacter.TeamId });
        AssertEqual(20004003, ReadResponsePayload<TeamPrefabApplyRequestResponse>(harness, 848_130,
            nameof(TeamPrefabApplyRequestResponse), "preset applying a worn weapon onto a weaponless character").Code,
            "a preset cannot move a worn weapon onto a character with no replacement weapon");
        AssertEqual(owners[0].Id, character.Equips.Single(row => row.Id == 2).CharacterId,
            "rejected preset leaves the weapon with its previous owner");
        AssertEqual(0, character.Equips.Count(row => row.CharacterId == owners[2].Id),
            "rejected preset equips nothing on the weaponless character");
        AssertEqual(savesBeforeRejection, saved.ReplaceOneCalls,
            "rejected preset does not persist Character");
        AssertEqual(stateBeforeRejection, Convert.ToHexString(character.ToBson()),
            "rejected preset mutates no equipment assignment");
        AssertNoPendingPacket(harness, "rejected preset apply");

        InvokeRequestHandler(harness, nameof(EquipTakeOffRequest), 848_110,
            new EquipTakeOffRequest { EquipIds = [2] });
        AssertEqual(20021012, ReadResponsePayload<EquipTakeOffResponse>(harness, 848_110,
            nameof(EquipTakeOffResponse), "weapon cannot be removed without replacement").Code,
            "weapon removal preserves preview invariant");
        AssertEqual(owners[0].Id, character.Equips.Single(row => row.Id == 2).CharacterId,
            "rejected removal leaves the weapon equipped");
    }

    private static void AssertNoPendingPacket(LoopbackSessionHarness harness, string name)
    {
        if (harness.TryReadAvailablePacket($"{name} unexpected packet", out var extra))
            throw new InvalidDataException($"{name}: unexpected extra {extra.Type} packet.");
    }
}
