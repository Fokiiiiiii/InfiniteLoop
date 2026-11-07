using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.photomode;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test
{
    internal partial class Program
    {
        // Random home scenes (Switch/Add/Remove/Edit RandomBackground*, SwitchRandomFashion) and
        // FashionSuitSetRequest. Scene ids, owner character/fashions and the pool limit come from tables.
        private static void ValidateWardrobePhotoCompatibility()
        {
            using MongoCollectionOverride noOpStages = MongoCollectionOverride.InstallNoOpStageCollection();
            const long playerId = 99_611;
            int packetId = 19_611;

            List<int> scenes = TableReaderV2.Parse<BackgroundTable>()
                .Where(row => row.Id > 0 && row.IsFree == 0)
                .Select(row => row.Id).Order().ToList();
            if (scenes.Count < 5)
                throw new InvalidDataException("Background.tsv needs five non-free scenes.");
            int sceneA = scenes[0], sceneB = scenes[1], sceneC = scenes[2], lockedScene = scenes[3];
            int unknownScene = TableReaderV2.Parse<BackgroundTable>().Max(row => row.Id) + 1;
            int limit = int.Parse(TableReaderV2.Parse<ConfigTable>().Single(r => r.Key == "RandomBackgroundCountLimit").Value);

            List<FashionTable> fashionRows = TableReaderV2.Parse<FashionTable>();
            IGrouping<int, FashionTable> group = fashionRows.GroupBy(r => r.CharacterId)
                .Where(g => g.Count() >= 2).OrderBy(g => g.Key).First();
            FashionTable[] rows = group.OrderBy(r => r.Id).Take(2).ToArray();
            FashionTable foreignRow = fashionRows.Where(r => r.CharacterId != group.Key).OrderBy(r => r.Id).First();

            CharacterData chief = CreateLoginAccountCompatibilityCharacter((uint)group.Key, (uint)rows[0].Id);
            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            character.Characters = [chief];
            character.Fashions =
            [
                new() { Id = rows[0].Id }, new() { Id = rows[1].Id }, new() { Id = foreignRow.Id }
            ];
            character.WeaponFashions =
            [
                new() { Id = 9001 },
                new() { Id = 9002, ExpireTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10 }
            ];
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            player.PlayerData.DisplayCharIdList = [group.Key];
            player.OwnedBackgroundIds = [sceneA, sceneB, sceneC];

            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> playerCollection,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characterCollection,
                out _);
            using LoopbackSessionHarness harness = new(
                character, player, CreateDrawCompatibilityInventory(playerId, []), "wardrobe-photo-compat");

            TResp Call<TReq, TResp>(string req, TReq body)
            {
                int id = packetId++;
                InvokeRequestHandler(harness, req, id, body);
                return ReadResponsePayload<TResp>(harness.ReadPacket(req), req.Replace("Request", "Response"));
            }
            int Switch(bool on, int id) => Call<SwitchRandomBackgroundRequest, SwitchRandomBackgroundResponse>(
                nameof(SwitchRandomBackgroundRequest), new() { IsRandomBackground = on, CurRandomBackgroundId = id }).Code;
            int Add(int id) => Call<AddRandomBackgroundRequest, AddRandomBackgroundResponse>(
                nameof(AddRandomBackgroundRequest), new() { BackgroundId = id }).Code;
            int Remove(int id) => Call<RemoveRandomBackgroundRequest, RemoveRandomBackgroundResponse>(
                nameof(RemoveRandomBackgroundRequest), new() { BackgroundId = id }).Code;
            int Fashion(bool on) => Call<SwitchRandomFashionRequest, SwitchRandomFashionResponse>(
                nameof(SwitchRandomFashionRequest), new() { IsBackgroundRandomFashion = on }).Code;
            int Edit(RandomBackgroundData? data) => Call<EditRandomBackGroundFashionRequest, EditRandomBackGroundFashionResponse>(
                nameof(EditRandomBackGroundFashionRequest), new() { EditRandomBackground = data }).Code;
            AscNet.Common.Database.Player Reload() =>
                BsonSerializer.Deserialize<AscNet.Common.Database.Player>(player.ToBsonDocument());

            // Disabled feature boundaries.
            AssertEqual(20244006, Add(sceneA), "Add while random scenes off");
            AssertEqual(20244011, Remove(sceneA), "Remove while random scenes off");
            AssertEqual(20244001, Fashion(true), "SwitchRandomFashion while random scenes off");
            AssertEqual(20244003, Switch(false, sceneA), "Close while already closed");
            AssertEqual(20244007, Switch(true, unknownScene), "Open with unknown scene");
            AssertEqual(20244008, Switch(true, lockedScene), "Open with locked scene");
            AssertEqual(false, player.RandomBackground.IsRandomBackground, "rejected open leaves state off");

            // Open seeds the pool with the current scene and the chief assistant's worn coating.
            int openId = packetId++;
            InvokeRequestHandler(harness, nameof(SwitchRandomBackgroundRequest), openId,
                new SwitchRandomBackgroundRequest { IsRandomBackground = true, CurRandomBackgroundId = sceneA });
            SwitchRandomBackgroundResponse opened = ReadResponsePayload<SwitchRandomBackgroundResponse>(
                harness.ReadPacket("open"), nameof(SwitchRandomBackgroundResponse));
            AssertEqual(0, opened.Code, "open code");
            AssertEqual(sceneA, opened.AddRandomBackground!.BackgroundId, "open seeded scene");
            AssertEqual(group.Key, opened.AddRandomBackground.RandomChars.Single().CharId, "seed chief id");
            AssertEqual(true, opened.AddRandomBackground.RandomChars.Single().IsRandom, "seed chief random");
            AssertIntegerList([rows[0].Id], opened.AddRandomBackground.RandomChars.Single().RandomFashions.Select(f => (long)f).ToArray(), "seed chief fashion");
            AssertEqual(20244002, Switch(true, sceneA), "open twice");

            // Add boundaries and success with a distinct scene.
            AssertEqual(20244009, Add(sceneA), "add duplicate");
            AssertEqual(20244008, Add(lockedScene), "add locked");
            AssertEqual(20244007, Add(unknownScene), "add unknown");
            AscNet.Common.Database.Player afterAdd;
            AssertEqual(0, Add(sceneB), "add B");
            AssertEqual(0, Add(sceneC), "add C");
            afterAdd = Reload();
            AssertIntegerList([sceneA, sceneB, sceneC], afterAdd.RandomBackground.Pool.Select(e => (long)e.BackgroundId).ToArray(), "pool persists after BSON reload");

            // Fashion toggle + edit.
            AssertEqual(0, Fashion(true), "fashion on");
            AssertEqual(0, Fashion(true), "fashion on idempotent");
            RandomBackgroundData Edited(int bg, bool random, params int[] fashions) => new()
            {
                BackgroundId = bg,
                RandomChars = [new() { CharId = group.Key, IsRandom = random, RandomFashions = [.. fashions] }]
            };
            AssertEqual(20012001, Edit(Edited(sceneA, true, foreignRow.Id)), "edit fashion of another character");
            AssertEqual(20012001, Edit(Edited(sceneA, false, rows[0].Id)), "edit without a random assistant");
            AssertEqual(20012001, Edit(Edited(sceneA, true)), "edit assistant with no fashion");
            AssertEqual(20012001, Edit(Edited(lockedScene, true, rows[0].Id)), "edit scene outside pool");
            AssertEqual(20012001, Edit(new() { BackgroundId = sceneA, RandomChars = null! }), "edit nil chars");
            AssertEqual(20012001, Edit(null), "edit nil data");
            AssertEqual(0, Edit(Edited(sceneA, true, rows[0].Id, rows[1].Id)), "edit two fashions");
            AssertEqual(0, Edit(Edited(sceneB, true, rows[1].Id)), "edit one fashion");
            afterAdd = Reload();
            AssertIntegerList([rows[0].Id, rows[1].Id], afterAdd.RandomBackground.Pool.Single(e => e.BackgroundId == sceneA).Chars.Single().Fashions.Select(f => (long)f).ToArray(), "scene A fashions persisted");
            AssertIntegerList([rows[1].Id], afterAdd.RandomBackground.Pool.Single(e => e.BackgroundId == sceneB).Chars.Single().Fashions.Select(f => (long)f).ToArray(), "scene B fashions persisted");
            AssertEqual(true, afterAdd.RandomBackground.IsBackgroundRandomFashion, "fashion flag persisted");
            AssertEqual(0, Fashion(false), "fashion off");
            AssertEqual(20012001, Edit(Edited(sceneA, true, rows[0].Id)), "edit while fashion random off");

            afterAdd = Reload();
            // Login payload carries the typed pool the client reads.
            RandomBackgroundLoginData login = MessagePackSerializer.Deserialize<RandomBackgroundLoginData>(
                MessagePackSerializer.Serialize(WardrobePhotoModule.BuildRandomBackgroundLoginData(afterAdd)));
            AssertEqual(true, login.IsRandomBackground, "login IsRandomBackground");
            AssertEqual(false, login.IsBackgroundRandomFashion, "login IsBackgroundRandomFashion");
            AssertIntegerList([sceneA, sceneB, sceneC], login.RandomBackgroundPool.Select(e => (long)e.BackgroundId).ToArray(), "login pool ids");
            AssertIntegerList([rows[1].Id], login.RandomBackgroundPool[1].RandomChars.Single().RandomFashions.Select(f => (long)f).ToArray(), "login pool fashions");

            // Remove boundaries.
            AssertEqual(20244012, Remove(unknownScene), "remove unknown");
            AssertEqual(20244013, Remove(lockedScene), "remove locked");
            AssertEqual(0, Remove(sceneC), "remove C");
            AssertEqual(20244014, Remove(sceneC), "remove C twice");
            AssertEqual(0, Remove(sceneB), "remove B");
            AssertEqual(20244015, Remove(sceneA), "remove last scene");

            // Limit comes from Config.tsv.
            AscNet.Common.Database.RandomBackgroundState full = player.RandomBackground.Clone();
            player.RandomBackground.Pool.AddRange(Enumerable.Range(1, limit - 1).Select(i => new AscNet.Common.Database.RandomBackgroundEntry { BackgroundId = -i }));
            AssertEqual(20244010, Add(sceneB), "add at pool limit");
            player.RandomBackground = full;

            // Close adopts the last random scene as the current scene.
            AssertEqual(20244004, Switch(false, unknownScene), "close unknown scene");
            AssertEqual(20244005, Switch(false, lockedScene), "close locked scene");
            AssertEqual(0, Switch(false, sceneC), "close");
            afterAdd = Reload();
            AssertEqual(false, afterAdd.RandomBackground.IsRandomBackground, "closed persisted");
            AssertEqual(sceneC, afterAdd.UseBackgroundId, "closed scene becomes current scene");
            AssertEqual(0, Switch(true, sceneB), "reopen keeps pool");
            AssertEqual(1, Reload().RandomBackground.Pool.Count, "reopen does not reseed");

            // FashionSuitSetRequest: bind weapon coating to an owned coating.
            int Set(int cloth, int weapon) => Call<FashionSuitSetRequest, FashionSuitSetResponse>(
                nameof(FashionSuitSetRequest), new() { ClothFashionId = cloth, WeaponFashionId = weapon }).Code;
            int ReadSyncAndSet(int cloth, int weapon)
            {
                int id = packetId++;
                InvokeRequestHandler(harness, nameof(FashionSuitSetRequest), id, new FashionSuitSetRequest { ClothFashionId = cloth, WeaponFashionId = weapon });
                FashionSyncNotify push = ReadPushPayload<FashionSyncNotify>(harness, nameof(FashionSyncNotify), "suit set push");
                AssertEqual(cloth, (int)push.FashionList.Single().Id, "suit set push fashion");
                AssertEqual(weapon, (int)push.FashionList.Single().WeaponFashionId, "suit set push weapon");
                return ReadResponsePayload<FashionSuitSetResponse>(harness.ReadPacket("suit set"), nameof(FashionSuitSetResponse)).Code;
            }
            AssertEqual(0, ReadSyncAndSet(rows[0].Id, 9001), "set first");
            AssertEqual(0, ReadSyncAndSet(rows[1].Id, 0), "set second");
            AssertEqual(20012001, Set(0, 0), "set zero fashion");
            AssertEqual(20012001, Set(rows[0].Id, 9002), "set expired weapon fashion");
            AssertEqual(20012001, Set(rows[0].Id, 9003), "set unowned weapon fashion");
            AssertEqual(20012001, Set(rows[0].Id, -1), "set negative weapon fashion");
            AssertEqual(20012001, Set(fashionRows.Max(r => r.Id) + 1, 0), "set unknown fashion");
            AssertEqual(20009001, Set(foreignRow.Id, 0), "set fashion of unowned character");
            AscNet.Common.Database.Character reloaded = BsonSerializer.Deserialize<AscNet.Common.Database.Character>(character.ToBsonDocument());
            AssertEqual(9001L, reloaded.Fashions.Single(f => f.Id == rows[0].Id).WeaponFashionId, "weapon binding persisted");
            AssertEqual(0L, reloaded.Fashions.Single(f => f.Id == rows[1].Id).WeaponFashionId, "second binding persisted");
            AssertEqual(false, reloaded.Fashions.Single(f => f.Id == rows[0].Id).IsRandom, "random flag untouched");
            int saves = characterCollection.ReplaceOneCalls;
            AssertEqual(0, ReadSyncAndSet(rows[0].Id, 9001), "set idempotent");
            AssertEqual(saves, characterCollection.ReplaceOneCalls, "idempotent set does not save");

            // Share/photo requests are fire-and-forget: no response, no state change.
            foreach (string name in new[] { "ShareBackgroundRequest", "PhotoBackgroundRequest" })
            {
                InvokeRequestHandler(harness, name, packetId++, new object());
                if (harness.TryReadAvailablePacket($"{name} unexpected packet", out Packet extra))
                    throw new InvalidDataException($"{name}: unexpected {extra.Type} packet.");
            }
        }
    }
}
