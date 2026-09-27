using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.miniactivity.musicplayer;
using AscNet.Table.V2.share.condition;
using MessagePack;
using MongoDB.Bson;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    /// <summary>
    /// AudioPlayer compatibility: login defaults, favorites, backgrounds, reset, caps,
    /// ownership rejection, and persistence rollback. Uses real MusicPlayer tables.
    /// </summary>
    private static void ValidateAudioPlayerCompatibility()
    {
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AudioPlayerModule");
        MethodInfo buildLogin = RequiredMethod(module, "BuildLoginData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(Player)]);
        MethodInfo addFav = RequiredMethod(module, "AddFavoriteSong", BindingFlags.Static | BindingFlags.Public, [typeof(Session), typeof(AscNet.GameServer.Packet.Request)]);
        MethodInfo removeFav = RequiredMethod(module, "RemoveFavoriteSong", BindingFlags.Static | BindingFlags.Public, [typeof(Session), typeof(AscNet.GameServer.Packet.Request)]);
        MethodInfo addBg = RequiredMethod(module, "AddBackgroundSongs", BindingFlags.Static | BindingFlags.Public, [typeof(Session), typeof(AscNet.GameServer.Packet.Request)]);
        MethodInfo removeBg = RequiredMethod(module, "RemoveBackgroundSong", BindingFlags.Static | BindingFlags.Public, [typeof(Session), typeof(AscNet.GameServer.Packet.Request)]);
        MethodInfo resetBg = RequiredMethod(module, "ResetBackgroundSongs", BindingFlags.Static | BindingFlags.Public, [typeof(Session), typeof(AscNet.GameServer.Packet.Request)]);
        AudioPlayerLoginData Login(Player player) =>
            (AudioPlayerLoginData)(buildLogin.Invoke(null, [player]) ?? throw new InvalidDataException("BuildLoginData returned null."));

        // Authoritative table anchors.
        var album = TableReaderV2.Parse<MusicPlayerAlbumTable>().ToList();
        var config = TableReaderV2.Parse<MusicPlayerConfigTable>().ToList();
        int defaultId = config.Single(c => c.Key == "DefaultBackgroundSongId").Values;
        int favMax = config.Single(c => c.Key == "FavoriteSongMaxCount").Values;
        int bgMax = config.Single(c => c.Key == "BackgroundSongMaxCount").Values;
        int[] testSongs = album
            .Where(row => (row.ConditionId is null or 0) && row.Id != defaultId)
            .Select(row => row.Id)
            .Take(2)
            .ToArray();
        if (testSongs.Length < 2)
            throw new InvalidDataException("MusicPlayerAlbum has fewer than two ungated non-default songs.");
        int songA = testSongs[0];
        int songB = testSongs[1];
        AssertEqual(true, songA != songB, "Album has two distinct songs");
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>()
            .ToDictionary(row => row.Id);
        MusicPlayerAlbumTable gatedSong = album.First(row =>
            row.ConditionId is int conditionId
            && conditions.TryGetValue(conditionId, out ConditionTable? condition)
            && condition.Type == 11117
            && condition.Params.Count > 0);
        int gatedBackgroundId = conditions[gatedSong.ConditionId!.Value].Params[0];


        void Invoke(MethodInfo method, Session session, object request)
        {
            Packet.Request req = new()
            {
                Id = 9001,
                Name = method.Name,
                Content = MessagePackSerializer.Serialize(request)
            };
            method.Invoke(null, [session, req]);
        }
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _);

        // ---- login defaults ----
        long uid = 47_200;
        using (LoopbackSessionHarness fresh = new(CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid, []), "audioplayer-login-test"))
        {
            AudioPlayerLoginData login = Login(fresh.Session.player);
            AssertEqual(0, login.FavoriteSongs.Count, "fresh login has no favorites");
            AssertEqual(1, login.BackgroundSongs.Count, "fresh login has default background");
            AssertEqual(defaultId, login.BackgroundSongs[0], "fresh login default background song id");
        }
        // ---- relogin handoff: persisted order is preserved exactly; default is not injected ----
        Player reloginPlayer = CreateDrawCompatibilityPlayer(uid + 10);
        reloginPlayer.BackgroundSongs = [songA, songB];
        Player reloadedReloginPlayer = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(
            reloginPlayer.ToBson());
        using (LoopbackSessionHarness relogin = new(
            CreateDrawCompatibilityCharacter(uid + 10),
            reloadedReloginPlayer,
            CreateDrawCompatibilityInventory(uid + 10, []),
            "audioplayer-relogin-test"))
        {
            relogin.Session.stage = CreateLoginAccountCompatibilityStage(uid + 10);
            MethodInfo buildNotifyLogin = RequiredMethod(
                RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
                "BuildNotifyLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);
            NotifyLogin notifyLogin = (NotifyLogin?)buildNotifyLogin.Invoke(null, [relogin.Session])
                ?? throw new InvalidDataException("AccountModule.BuildNotifyLogin returned nil.");
            AssertIntegerList([songA, songB],
                notifyLogin.AudioPlayerLoginData.BackgroundSongs.Select(id => (long)id).ToArray(),
                "NotifyLogin preserves persisted background song order");
        }


        // ---- favorites: add two distinct (oldest first on wire), remove one ----
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid, []), "audioplayer-fav-test"))
        {
            Invoke(addFav, h.Session, new AddAudioPlayerFavoriteSongRequest { SongId = songA });
            Invoke(addFav, h.Session, new AddAudioPlayerFavoriteSongRequest { SongId = songB });
            AssertEqual(2, h.Session.player.FavoriteSongs.Count, "two favorites persisted");
            AssertEqual(songA, h.Session.player.FavoriteSongs[0], "older favorite first on wire");
            AssertEqual(songB, h.Session.player.FavoriteSongs[1], "recent favorite last on wire");

            // Duplicate add is idempotent (no second entry).
            Invoke(addFav, h.Session, new AddAudioPlayerFavoriteSongRequest { SongId = songB });
            AssertEqual(2, h.Session.player.FavoriteSongs.Count, "duplicate favorite add idempotent");

            // Missing/invalid id rejected: no mutation.
            Invoke(addFav, h.Session, new AddAudioPlayerFavoriteSongRequest { SongId = 999_999 });
            AssertEqual(2, h.Session.player.FavoriteSongs.Count, "invalid favorite id not added");

            Invoke(removeFav, h.Session, new RemoveAudioPlayerFavoriteSongRequest { SongId = songA });
            AssertEqual(1, h.Session.player.FavoriteSongs.Count, "favorite removed");
            AssertEqual(songB, h.Session.player.FavoriteSongs[0], "remaining favorite");
        }
        // ---- scene-gated songs: owned background authorizes favorite and playlist persistence ----
        Player gatedPlayer = CreateDrawCompatibilityPlayer(uid + 20);
        gatedPlayer.UseBackgroundId = gatedBackgroundId;
        using (LoopbackSessionHarness gated = new(
            CreateDrawCompatibilityCharacter(uid + 20),
            gatedPlayer,
            CreateDrawCompatibilityInventory(uid + 20, []),
            "audioplayer-gated-song-test"))
        {
            Invoke(addFav, gated.Session, new AddAudioPlayerFavoriteSongRequest { SongId = gatedSong.Id });
            AssertEqual(0, ReadResponsePayload<AddAudioPlayerFavoriteSongResponse>(
                gated, 9001, nameof(AddAudioPlayerFavoriteSongResponse),
                "owned-scene gated favorite response").Code,
                "owned-scene gated favorite Code");
            Invoke(addBg, gated.Session, new AddAudioPlayerBackgroundSongRequest { SongIds = [gatedSong.Id] });
            AssertEqual(0, ReadResponsePayload<AddAudioPlayerBackgroundSongResponse>(
                gated, 9001, nameof(AddAudioPlayerBackgroundSongResponse),
                "owned-scene gated background response").Code,
                "owned-scene gated background Code");
            Player gatedReload = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(
                gatedPlayer.ToBson());
            AssertEqual(true, gatedReload.FavoriteSongs.Contains(gatedSong.Id),
                "owned-scene gated favorite persists");
            AssertEqual(true, Login(gatedReload).BackgroundSongs.Contains(gatedSong.Id),
                "owned-scene gated background persists across relogin");
        }

        Player unownedGatedPlayer = CreateDrawCompatibilityPlayer(uid + 21);
        using (LoopbackSessionHarness unownedGated = new(
            CreateDrawCompatibilityCharacter(uid + 21),
            unownedGatedPlayer,
            CreateDrawCompatibilityInventory(uid + 21, []),
            "audioplayer-unowned-gated-song-test"))
        {
            Invoke(addFav, unownedGated.Session,
                new AddAudioPlayerFavoriteSongRequest { SongId = gatedSong.Id });
            AssertEqual(true, ReadResponsePayload<AddAudioPlayerFavoriteSongResponse>(
                unownedGated, 9001, nameof(AddAudioPlayerFavoriteSongResponse),
                "unowned-scene gated favorite response").Code != 0,
                "unowned-scene gated favorite rejected");
        }


        // ---- backgrounds: ordered add, duplicate skip, remove, reset, cap ----
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid, []), "audioplayer-bg-test"))
        {
            _ = Login(h.Session.player);
            Invoke(addBg, h.Session, new AddAudioPlayerBackgroundSongRequest { SongIds = new() { songA, songB } });
            AssertEqual(3, h.Session.player.BackgroundSongs.Count, "default + two added");
            AssertEqual(defaultId, h.Session.player.BackgroundSongs[0], "default first on wire");
            AssertEqual(songA, h.Session.player.BackgroundSongs[1], "older background next on wire");
            AssertEqual(songB, h.Session.player.BackgroundSongs[2], "recent background last on wire");

            Invoke(addBg, h.Session, new AddAudioPlayerBackgroundSongRequest { SongIds = new() { songA, 999_999 } });
            AssertEqual(3, h.Session.player.BackgroundSongs.Count, "duplicate + invalid background skipped");

            Invoke(removeBg, h.Session, new RemoveAudioPlayerBackgroundSongRequest { SongId = songA });
            AssertEqual(2, h.Session.player.BackgroundSongs.Count, "background removed");

            Invoke(resetBg, h.Session, new ResetAudioPlayerBackgroundSongRequest());
            AssertEqual(1, h.Session.player.BackgroundSongs.Count, "reset keeps only default");
            AssertEqual(defaultId, h.Session.player.BackgroundSongs[0], "reset default song");

            List<int> many = album
                .Where(row => row.ConditionId is null or 0)
                .Select(row => row.Id)
                .Where(id => id != defaultId)
                .Take(bgMax + 1)
                .ToList();
            Invoke(addBg, h.Session, new AddAudioPlayerBackgroundSongRequest { SongIds = many });
            AssertEqual(Math.Min(bgMax, many.Count + 1), h.Session.player.BackgroundSongs.Count,
                "background list respects configured cap");
        }
        // ---- removing the final background is rejected and preserves the playlist ----
        Player finalBackgroundPlayer = CreateDrawCompatibilityPlayer(uid + 30);
        finalBackgroundPlayer.BackgroundSongs = [songA];
        using (LoopbackSessionHarness finalBackground = new(
            CreateDrawCompatibilityCharacter(uid + 30),
            finalBackgroundPlayer,
            CreateDrawCompatibilityInventory(uid + 30, []),
            "audioplayer-final-background-test"))
        {
            Invoke(removeBg, finalBackground.Session,
                new RemoveAudioPlayerBackgroundSongRequest { SongId = songA });
            AssertEqual(true, ReadResponsePayload<RemoveAudioPlayerBackgroundSongResponse>(
                finalBackground, 9001, nameof(RemoveAudioPlayerBackgroundSongResponse),
                "remove final background response").Code != 0,
                "remove final background rejected");
            AssertIntegerList([songA],
                finalBackgroundPlayer.BackgroundSongs.Select(id => (long)id).ToArray(),
                "remove final background preserves playlist");
        }


        // ---- persistence: mutation applied and SaveChecked before Code response ----
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid, []), "audioplayer-save-test"))
        {
            int before = h.Session.player.FavoriteSongs.Count;
            Invoke(addFav, h.Session, new AddAudioPlayerFavoriteSongRequest { SongId = songA });
            AssertEqual(before + 1, h.Session.player.FavoriteSongs.Count, "favorite persisted before save");
        }

        Player failedPlayer = CreateDrawCompatibilityPlayer(uid + 1);
        using (MongoCollectionOverride failedMongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> failedSaves, out _, out _))
        using (LoopbackSessionHarness failed = new(
            CreateDrawCompatibilityCharacter(uid + 1),
            failedPlayer,
            CreateDrawCompatibilityInventory(uid + 1, []),
            "audioplayer-save-failure"))
        {
            failedSaves.ThrowOnReplaceOne = true;
            Invoke(addFav, failed.Session, new AddAudioPlayerFavoriteSongRequest { SongId = songA });
            AddAudioPlayerFavoriteSongResponse response = ReadResponsePayload<AddAudioPlayerFavoriteSongResponse>(
                failed, 9001, nameof(AddAudioPlayerFavoriteSongResponse), "AudioPlayer persistence failure response");
            AssertEqual(true, response.Code != 0, "AudioPlayer persistence failure Code");
            AssertEqual(0, failedPlayer.FavoriteSongs.Count, "AudioPlayer persistence failure rolls back favorites");
        }
    }

    // Hook for parent integration; no Program.cs edit.
    internal static void RunAudioPlayerCompatibility() => ValidateAudioPlayerCompatibility();
}
