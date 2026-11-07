using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.movie;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateMiscPlayerCompatibility()
    {
        const long playerId = 99_490;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, [new Item { Id = Inventory.ActionPoint, Count = 4_000 }]);
        Character roster = CreateDrawCompatibilityCharacter(playerId);
        roster.Characters = [new CharacterData { Id = 1_021_001, Level = 1 }];
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness harness = new(roster, player, inventory, "misc-player");
        int id = 99_490_000;
        T Call<T>(string name, object request) where T : class
        {
            InvokeRegisteredRequestHandler(name + "Request", harness.Session, ++id, request);
            return ReadResponsePayload<T>(harness, id, name + "Response", name, maxPacketsToRead: 16);
        }

        // CD keys: no server-owned catalog exists, every key fails closed with CdKeyInvalid.
        foreach (string key in new[] { "", "ABCDEFGH", "abcdefgh" })
            AssertEqual(20054001, Call<UseCdKeyResponse>("UseCdKey", new UseCdKeyRequest { Id = key }).Code, $"cd key '{key}' rejected");

        // Movie options: only MovieRecordOptions rows/options record; persisted per movie; idempotent.
        MovieRecordOptionsTable[] movies = TableReaderV2.Parse<MovieRecordOptionsTable>().Where(x => x.Options.Count >= 2).Take(2).ToArray();
        AssertEqual(0, Call<QueryMovieOptionsResponse>("QueryMovieOptions", new QueryMovieOptionsRequest { MovieId = movies[0].Id }).MovieList.Count, "fresh movie has no options");
        AssertEqual(20241002, Call<QueryMovieOptionsResponse>("QueryMovieOptions", new QueryMovieOptionsRequest { MovieId = "no-such-movie" }).Code, "unknown movie query");
        AssertEqual(20241002, Call<UpdateMovieOptionsResponse>("UpdateMovieOptions", new UpdateMovieOptionsRequest { MovieId = "no-such-movie", OptionId = 1 }).Code, "unknown movie update");
        AssertEqual(20241003, Call<UpdateMovieOptionsResponse>("UpdateMovieOptions", new UpdateMovieOptionsRequest { MovieId = movies[0].Id, OptionId = int.MaxValue }).Code, "unlisted option");
        AssertEqual(0, player.MovieOptions.Count, "rejected updates persist nothing");
        AssertEqual(0, Call<UpdateMovieOptionsResponse>("UpdateMovieOptions", new UpdateMovieOptionsRequest { MovieId = movies[0].Id, OptionId = movies[0].Options[1] }).Code, "option recorded");
        AssertEqual(0, Call<UpdateMovieOptionsResponse>("UpdateMovieOptions", new UpdateMovieOptionsRequest { MovieId = movies[0].Id, OptionId = movies[0].Options[1] }).Code, "option record idempotent");
        AssertEqual(0, Call<UpdateMovieOptionsResponse>("UpdateMovieOptions", new UpdateMovieOptionsRequest { MovieId = movies[1].Id, OptionId = movies[1].Options[0] }).Code, "second movie recorded");
        Player reloaded = BsonSerializer.Deserialize<Player>(player.ToBson());
        harness.Session.player = reloaded;
        QueryMovieOptionsResponse queried = Call<QueryMovieOptionsResponse>("QueryMovieOptions", new QueryMovieOptionsRequest { MovieId = movies[0].Id });
        AssertEqual(true, queried.MovieList.SequenceEqual([movies[0].Options[1]]), "movie A options survive reload exactly once");
        AssertEqual(true, Call<QueryMovieOptionsResponse>("QueryMovieOptions", new QueryMovieOptionsRequest { MovieId = movies[1].Id }).MovieList.SequenceEqual([movies[1].Options[0]]), "movie B options independent");
        player = reloaded;

        // Auto fight: duration/cost/limit from Stage + AutoFight; rewards once after CompleteTime.
        StageTable[] stages = TableReaderV2.Parse<StageTable>()
            .Where(s => s.AutoFightId is > 0 && s.RequireActionPoint is > 0 && (s.TeamExp ?? 0) > 0
                && (s.RobotId?.All(r => r <= 0) ?? true)
                && TableReaderV2.Parse<AutoFightTable>().Any(a => a.Id == s.AutoFightId && a.Limit >= 2)
                && MiscPlayerModule.ResolveGoods(s, 80).Count > 0)
            .GroupBy(s => (s.RequireActionPoint, s.TeamExp)).Select(g => g.First()).Take(2).ToArray();
        AssertEqual(2, stages.Length, "two distinct authored auto-fight stages");
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(1_800_000_000);
        Func<DateTimeOffset> savedClock = MiscPlayerModule.Clock;
        MiscPlayerModule.Clock = () => now;
        try
        {
            StageTable a = stages[0], b = stages[1];
            AssertEqual(20003103, Call<StartAutoFightResponse>("StartAutoFight", new StartAutoFightRequest { StageId = int.MaxValue, Times = 1 }).Code, "unknown stage invalid");
            AssertEqual(20003104, Call<StartAutoFightResponse>("StartAutoFight", new StartAutoFightRequest { StageId = a.StageId, Times = 1 }).Code, "unpassed stage rejected");
            long[] recordTimes = [60, 90];
            for (int i = 0; i < 2; i++)
                harness.Session.stage.AddStage(new StageDatum
                {
                    StageId = stages[i].StageId, Passed = true, LastRecordTime = recordTimes[i], LastCardIds = [1_021_001]
                });
            AssertEqual(20003107, Call<StartAutoFightResponse>("StartAutoFight", new StartAutoFightRequest { StageId = a.StageId, Times = 0 }).Code, "zero times rejected");
            int limit = TableReaderV2.Parse<AutoFightTable>().Single(x => x.Id == a.AutoFightId).Limit;
            int cap = a.MaxChallengeNums is > 0 ? Math.Min(a.MaxChallengeNums.Value, limit > 0 ? limit : int.MaxValue) : limit;
            AssertEqual(20003107, Call<StartAutoFightResponse>("StartAutoFight", new StartAutoFightRequest { StageId = a.StageId, Times = cap + 1 }).Code, "over limit rejected");
            AssertEqual(0, player.AutoFightRecords.Count, "rejections persist nothing");

            long ap0 = inventory.Items.Single(x => x.Id == Inventory.ActionPoint).Count;
            StartAutoFightResponse startA = Call<StartAutoFightResponse>("StartAutoFight", new StartAutoFightRequest { StageId = a.StageId, Times = 1 });
            AssertEqual(0, startA.Code, "start A");
            AssertEqual(now.ToUnixTimeSeconds() + 60, startA.Record!.CompleteTime, "A duration = stage record time x times");
            AssertEqual(ap0 - a.RequireActionPoint!.Value, inventory.Items.Single(x => x.Id == Inventory.ActionPoint).Count, "A AP cost from stage");
            AssertEqual(20003106, Call<StartAutoFightResponse>("StartAutoFight", new StartAutoFightRequest { StageId = a.StageId, Times = 1 }).Code, "same stage already running");
            StartAutoFightResponse startB = Call<StartAutoFightResponse>("StartAutoFight", new StartAutoFightRequest { StageId = b.StageId, Times = 2 });
            AssertEqual(0, startB.Code, "start B");
            AssertEqual(now.ToUnixTimeSeconds() + 180, startB.Record!.CompleteTime, "B duration scales with times");

            // Records survive a BSON reload and appear in the login payload.
            player = BsonSerializer.Deserialize<Player>(player.ToBson());
            harness.Session.player = player;
            AssertEqual(2, MiscPlayerModule.BuildLoginRecords(player).Count, "login carries both records");

            AssertEqual(20003109, Call<ObtainAutoFightRewardsResponse>("ObtainAutoFightRewards", new ObtainAutoFightRewardsRequest { Index = 5, StageId = a.StageId }).Code, "bad index");
            AssertEqual(20003110, Call<ObtainAutoFightRewardsResponse>("ObtainAutoFightRewards", new ObtainAutoFightRewardsRequest { Index = 0, StageId = b.StageId }).Code, "stage mismatch");
            AssertEqual(20003111, Call<ObtainAutoFightRewardsResponse>("ObtainAutoFightRewards", new ObtainAutoFightRewardsRequest { Index = 0, StageId = a.StageId }).Code, "not complete");
            AssertEqual(2, player.AutoFightRecords.Count, "early obtain keeps records");

            now = now.AddSeconds(60);
            int goodsA = MiscPlayerModule.ResolveGoods(a, 80).Count;
            ObtainAutoFightRewardsResponse obtainA = Call<ObtainAutoFightRewardsResponse>("ObtainAutoFightRewards", new ObtainAutoFightRewardsRequest { Index = 0, StageId = a.StageId });
            AssertEqual(0, obtainA.Code, "obtain A");
            AssertEqual(a.TeamExp!.Value, obtainA.TeamExp, "A team exp from stage");
            AssertEqual(goodsA, obtainA.Rewards.Count, "A rewards from drop table");
            AssertEqual(1, player.AutoFightRecords.Count, "A record consumed");
            AssertEqual(20003109, Call<ObtainAutoFightRewardsResponse>("ObtainAutoFightRewards", new ObtainAutoFightRewardsRequest { Index = 1, StageId = a.StageId }).Code, "A not obtainable twice");
            AssertEqual(20003111, Call<ObtainAutoFightRewardsResponse>("ObtainAutoFightRewards", new ObtainAutoFightRewardsRequest { Index = 0, StageId = b.StageId }).Code, "B still running at 60s of 180s");

            now = now.AddSeconds(120);
            int goodsB = MiscPlayerModule.ResolveGoods(b, 80).Count;
            ObtainAutoFightRewardsResponse obtainB = Call<ObtainAutoFightRewardsResponse>("ObtainAutoFightRewards", new ObtainAutoFightRewardsRequest { Index = 0, StageId = b.StageId });
            AssertEqual(0, obtainB.Code, "obtain B");
            AssertEqual(b.TeamExp!.Value * 2, obtainB.TeamExp, "B team exp scales with times");
            AssertEqual(goodsB * 2, obtainB.Rewards.Count, "B rewards scale with times");
            AssertEqual(0, player.AutoFightRecords.Count, "all records consumed");
        }
        finally
        {
            MiscPlayerModule.Clock = savedClock;
        }
    }
}
