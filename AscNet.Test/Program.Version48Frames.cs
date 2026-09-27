using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Handlers;
using AscNet.GameServer;
using AscNet.Common.Util;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.grade;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.item;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateVersion48Frames()
    {
        PacketFactory.LoadPacketHandlers();
        foreach (int id in new[] { 1411003, 1421003 })
        {
            AssertEqual(true, Character.IsOwnableCharacter((uint)id), $"4.8 frame {id} has complete acquisition tables");
            Character roster = CreateTestCharacterRoster(id, 1);
            AssertEqual(true, roster.Characters.Any(character => character.Id == id), $"4.8 frame {id} can be acquired");
        }

        const int frameId = 1421003;
        Character trainedRoster = CreateTestCharacterRoster(frameId, 1);
        trainedRoster.Uid = 48_142;
        CharacterData trained = RequiredCharacterData(trainedRoster, frameId);
        int initialQuality = trained.Quality;
        const int standardSkillId = 142301;
        int initialSkillLevel = trained.SkillList.Single(skill => skill.Id == standardSkillId).Level;
        int standardSkillMaxLevel = TableReaderV2.Parse<CharacterSkillUpgradeTable>()
            .Where(row => row.SkillId == standardSkillId).Max(row => row.Level);
        AssertEqual(true, initialSkillLevel < standardSkillMaxLevel,
            "Anabasis has standard skill progression to train");
        int initialStar = trained.Star;
        int trainingItemId = TableReaderV2.Parse<ItemTable>()
            .Single(row => row.ItemType == (int)ItemType.NormalConsumableItem).Id;
        var player = CreateDrawCompatibilityPlayer(trainedRoster.Uid);
        player.PlayerData.Level = 52;
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<Character> characterSaves,
            out RecordingMongoCollectionProxy<Inventory> inventorySaves))
        using (LoopbackSessionHarness harness = new(trainedRoster, player,
            CreateDrawCompatibilityInventory(trainedRoster.Uid, [new Item { Id = trainingItemId, Count = 1 }]), "v48-frame-training"))
        {
            const int packetId = 48_142;
            InvokeRegisteredRequestHandler(nameof(CharacterUseOneClickItemRequest), harness.Session, packetId,
                new CharacterUseOneClickItemRequest { CharacterId = frameId });
            AssertEqual(0L, harness.Session.inventory.Items.Single(item => item.Id == trainingItemId).Count,
                "Transition Device consumes one item");
            _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "training inventory push");
            CharacterData pushed = ReadPushPayload<NotifyCharacterDataList>(harness,
                nameof(NotifyCharacterDataList), "training character push").CharacterDataList.Single();
            AssertEqual(Character.characterLevelUpTemplates
                .Where(row => row.Type == TableReaderV2.Parse<CharacterTable>()
                    .First(frame => frame.Id == frameId).LevelUpTemplateId)
                .Max(row => row.Level), pushed.Level,
                "Transition Device reaches authored level cap independent of Commandant level");
            AssertEqual(TableReaderV2.Parse<CharacterGradeTable>().Where(row => row.CharacterId == frameId)
                .Max(row => row.Grade), pushed.Grade, "training reaches authored maximum grade");
            AssertEqual(initialQuality, pushed.Quality, "training does not evolve quality");
            AssertEqual(initialStar, pushed.Star, "training does not evolve stars");
            AssertEqual(0, pushed.EnhanceSkillList.Count, "training does not unlock Leap skills");
            AssertEqual(standardSkillMaxLevel,
                pushed.SkillList.Single(skill => skill.Id == standardSkillId).Level,
                "training upgrades eligible standard skills to their authored cap");
            _ = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "training task progress push");
            AssertEqual(0, ReadResponsePayload<CharacterUseOneClickItemResponse>(harness, packetId,
                nameof(CharacterUseOneClickItemResponse), "training response").Code, "training succeeds");
            AssertEqual(1, characterSaves.ReplaceOneCalls, "training saves frame");
            AssertEqual(1, inventorySaves.ReplaceOneCalls, "training saves spent item");
        }

        Character uniframeRoster = CreateTestCharacterRoster(1511003, 1);
        uniframeRoster.Uid = 48_143;
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<Character> rejectedSaves, out _))
        using (LoopbackSessionHarness harness = new(uniframeRoster, CreateDrawCompatibilityPlayer(uniframeRoster.Uid),
            CreateDrawCompatibilityInventory(uniframeRoster.Uid, [new Item { Id = trainingItemId, Count = 1 }]), "v48-uniframe-training"))
        {
            const int packetId = 48_143;
            InvokeRegisteredRequestHandler(nameof(CharacterUseOneClickItemRequest), harness.Session, packetId,
                new CharacterUseOneClickItemRequest { CharacterId = 1511003 });
            AssertEqual(20009021, ReadResponsePayload<CharacterUseOneClickItemResponse>(harness, packetId,
                nameof(CharacterUseOneClickItemResponse), "uniframe training rejection").Code,
                "Transition Device cannot train Uniframes");
            AssertEqual(1L, harness.Session.inventory.Items.Single(item => item.Id == trainingItemId).Count,
                "rejected training preserves item");
            AssertEqual(0, rejectedSaves.ReplaceOneCalls, "rejected training does not save frame");
        }
    }
}
