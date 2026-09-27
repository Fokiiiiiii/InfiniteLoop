using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table;
using AscNet.Table.V2.share.dormitory;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    // 4.8 Kurumi 1411003 / Adelyde 1421003 trust + dorm rows, exercised through registered handlers.
    private static void ValidateRosterLife48Compatibility()
    {
        const long playerId = 48_140;
        const uint kurumi = 1_411_003, adelyde = 1_421_003;
        const int kurumiGift = 40_644;

        Character roster = CreateDrawCompatibilityCharacter(playerId);
        roster.AddCharacter(kurumi);
        roster.AddCharacter(adelyde);
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, [new Item { Id = kurumiGift, Count = 4 }]);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves,
            out RecordingMongoCollectionProxy<Character> characterSaves,
            out _);
        using LoopbackSessionHarness harness = new(roster, player, inventory, "roster-life-48");
        System.Reflection.MethodInfo dormLogin = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.DormModule"), "BuildLoginData",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public, [typeof(AscNet.GameServer.Session)]);
        void Drain() { while (harness.TryReadAvailablePacket("roster-life-48 drain", out _)) { } }

        // Trust: Kurumi's authored gift is favorite (60) for her, plain (30) for Adelyde; Lv1 needs 100.
        InvokeRegisteredRequestHandler(nameof(CharacterSendGiftRequest), harness.Session, 48_001,
            new CharacterSendGiftRequest { TemplateId = (int)kurumi, GiftItems = new() { [kurumiGift] = 2 } });
        Drain();
        InvokeRegisteredRequestHandler(nameof(CharacterSendGiftRequest), harness.Session, 48_002,
            new CharacterSendGiftRequest { TemplateId = (int)adelyde, GiftItems = new() { [kurumiGift] = 2 } });
        Drain();
        CharacterData savedKurumi = characterSaves.LastReplacement!.Characters.Single(c => c.Id == kurumi);
        CharacterData savedAdelyde = characterSaves.LastReplacement!.Characters.Single(c => c.Id == adelyde);
        AssertEqual((2L, 20L), ((long)savedKurumi.TrustLv, (long)savedKurumi.TrustExp), "4.8 Kurumi favorite gift persisted trust");
        AssertEqual((1L, 60L), ((long)savedAdelyde.TrustLv, (long)savedAdelyde.TrustExp), "4.8 Adelyde non-favorite gift persisted trust");

        // Dorm: login seeds authored fondle counts; placement picks authored recovery tiers per attribute slot.
        dormLogin.Invoke(null, [harness.Session]);
        AssertEqual(true, player.Dorm.Characters.Where(c => c.CharacterId is kurumi or adelyde).All(c => c.LeftFondleCount == 3),
            "4.8 new characters seeded with authored fondle MaxCount");
        DormitoryTable room = TableReaderV2.Parse<DormitoryTable>().First(row => row.IsFree == 1 && row.CharCapacity >= 2
            && player.Dorm.Rooms.Any(saved => saved.Id == row.Id));
        player.Dorm.Furniture.RemoveAll(f => f.DormitoryId == room.Id);
        // Beauty 0 / Comfort 3500 / Utility 6500: meets Adelyde's top tier (470) but only Kurumi's total-3500 tier (463).
        player.Dorm.Furniture.Add(new PlayerDormFurniture { Id = 48_999_001, ConfigId = 1, DormitoryId = room.Id, AttrList = [0, 3_500, 6_500], BaseAttrList = [0, 0, 0] });
        InvokeRegisteredRequestHandler(nameof(DormPutCharacterRequest), harness.Session, 48_003,
            new DormPutCharacterRequest { DormitoryId = room.Id, CharacterIds = [kurumi, adelyde] });
        Drain();
        Player saved = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
            ?? throw new InvalidDataException("4.8 dorm placement was not saved."));
        PlayerDormCharacter Saved(uint id) => saved.Dorm.Characters.Single(c => c.CharacterId == id);
        AssertEqual((100, 400), (Saved(kurumi).MoodSpeed, Saved(kurumi).VitalitySpeed), "4.8 Kurumi recovery tier 463");
        AssertEqual((500, 600), (Saved(adelyde).MoodSpeed, Saved(adelyde).VitalitySpeed), "4.8 Adelyde recovery tier 470");

        // Placed characters draw events from their own authored pools.
        player.Dorm.EventNextRefreshTime = 0;
        dormLogin.Invoke(null, [harness.Session]);
        saved = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson!);
        AssertEqual(true, Saved(kurumi).EventList.Single().EventId is >= 9201 and <= 9208, "4.8 Kurumi authored dorm event");
        AssertEqual(true, Saved(adelyde).EventList.Single().EventId is >= 9301 and <= 9308, "4.8 Adelyde authored dorm event");

        // Fondle uses the authored row: count drops and mood rises within [Lower, Upper] = [600, 1000].
        int moodBefore = player.Dorm.Characters.Single(c => c.CharacterId == adelyde).Mood;
        InvokeRegisteredRequestHandler(nameof(DormDoFondleRequest), harness.Session, 48_004,
            new DormDoFondleRequest { CharacterId = adelyde, FondleType = 1 });
        Drain();
        saved = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson!);
        int gained = Saved(adelyde).Mood - moodBefore;
        AssertEqual(true, Saved(adelyde).LeftFondleCount == 2 && (gained is >= 600 and <= 1000 || Saved(adelyde).Mood == 10_000),
            "4.8 Adelyde fondle persisted");
    }
}
