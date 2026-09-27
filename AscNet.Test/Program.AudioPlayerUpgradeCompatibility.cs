using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.miniactivity.musicplayer;
using AscNet.Table.V2.share.condition;
using MongoDB.Bson;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    internal static void RunAudioPlayerUpgradeCompatibility()
    {
        MethodInfo buildNotify = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildNotifyLogin", BindingFlags.NonPublic | BindingFlags.Static, [typeof(Session)]);
        int defaultId = TableReaderV2.Parse<MusicPlayerConfigTable>()
            .Single(row => row.Key == "DefaultBackgroundSongId").Values;
        int[] extras = TableReaderV2.Parse<MusicPlayerAlbumTable>()
            .Where(row => row.ConditionId is null or 0 && row.Id != defaultId)
            .Select(row => row.Id).Take(3).ToArray();
        if (extras.Length < 3) throw new InvalidDataException("MusicPlayerAlbum needs three ungated non-default songs.");
        int a = extras[0], b = extras[1], c = extras[2];
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>()
            .ToDictionary(row => row.Id);
        int foreign = TableReaderV2.Parse<MusicPlayerAlbumTable>().First(row =>
            row.ConditionId is int id && conditions.TryGetValue(id, out ConditionTable? condition)
            && condition.Type == 11117 && condition.Params.Count > 0).Id;

        const long uid = 48_700;
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.BackgroundSongs = [defaultId, a, b, c]; // UI renders [c,b,a,default].
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> saves, out _, out _);
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(uid), player,
            CreateDrawCompatibilityInventory(uid, []), "music-move"))
        {
            void Move(int index, int moveType, int id, bool success, int[] expected, string label)
            {
                InvokeRegisteredRequestHandler(nameof(MoveAudioPlayerBackgroundSongRequest),
                    h.Session, 9001, new MoveAudioPlayerBackgroundSongRequest
                    {
                        Index = index, MoveType = moveType, SongId = id
                    });
                MoveAudioPlayerBackgroundSongResponse result = ReadResponsePayload<MoveAudioPlayerBackgroundSongResponse>(
                    h, 9001, nameof(MoveAudioPlayerBackgroundSongResponse), label);
                AssertEqual(success, result.Code == 0, label + " response Code");
                AssertIntegerList(expected.Select(x => (long)x).ToArray(),
                    player.BackgroundSongs.Select(x => (long)x).ToArray(), label + " durable wire order");
            }
            Move(1, 1, a, true, [defaultId, b, c, a], "move third UI song to top");
            Move(2, 2, c, true, [defaultId, c, b, a], "move second UI song down");
            AssertEqual(2, saves.ReplaceOneCalls, "both successful moves persisted");
            int[] unchanged = [defaultId, c, b, a];
            Move(-1, 1, a, false, unchanged, "negative index");
            Move(4, 1, a, false, unchanged, "past-end index");
            Move(0, 2, defaultId, false, unchanged, "bottom boundary");
            Move(3, 1, a, false, unchanged, "top boundary");
            Move(2, 3, b, false, unchanged, "invalid move type");
            Move(2, 2, foreign, false, unchanged, "foreign song id");
            // A foreign, gated song in a stale playlist still cannot be reordered by a player without ownership.
            player.BackgroundSongs[2] = foreign;
            Move(2, 2, foreign, false, [defaultId, c, foreign, a], "unowned playlist song");
            player.BackgroundSongs[2] = b;
            AssertEqual(2, saves.ReplaceOneCalls, "invalid moves never persist");
            saves.ThrowOnReplaceOne = true;
            Move(2, 2, b, false, unchanged, "failed move save rolls back");
            saves.ThrowOnReplaceOne = false;
        }
        Player reloaded = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(
            saves.LastSuccessfulReplacementBson ?? throw new InvalidDataException("Move did not persist playlist."));
        using (LoopbackSessionHarness relog = new(CreateDrawCompatibilityCharacter(uid), reloaded,
            CreateDrawCompatibilityInventory(uid, []), "music-move-relog"))
        {
            relog.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            NotifyLogin login = (NotifyLogin?)buildNotify.Invoke(null, [relog.Session])
                ?? throw new InvalidDataException("BuildNotifyLogin returned null.");
            AssertIntegerList([defaultId, c, b, a],
                login.AudioPlayerLoginData.BackgroundSongs.Select(x => (long)x).ToArray(),
                "relogin retains both playlist transitions in reversed client wire order");
        }
    }
}
