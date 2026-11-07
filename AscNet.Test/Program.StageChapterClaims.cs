using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.fuben.extrachapter;
using AscNet.Table.V2.share.fuben.shortstory;
using AscNet.Table.V2.share.fuben.experiment;
using AscNet.Table.V2.share.reward;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;
internal partial class Program
{
    private static void ValidateStageChapterRewardClaimsCompatibility()
    {
        var oldPlayers = Player.collection; var oldInventory = Inventory.collection;
        var oldCharacters = Character.collection; var oldStages = Stage.collection;
        var client = AscNet.Common.Common.db.Client;
        string databaseName = "ascnet_chapter_claim_" + Guid.NewGuid().ToString("N");
        var db = client.GetDatabase(databaseName);
        Player.collection = db.GetCollection<Player>("players");
        Inventory.collection = db.GetCollection<Inventory>("inventories");
        Character.collection = db.GetCollection<Character>("characters");
        Stage.collection = db.GetCollection<Stage>("stages");
        LoopbackSessionHarness? harness = null;
        try
        {
            PacketFactory.LoadPacketHandlers();
            long uid = 1800000201;
            var player = CreateDrawCompatibilityPlayer(uid);
            var inventory = CreateDrawCompatibilityInventory(uid, []);
            var character = CreateDrawCompatibilityCharacter(uid);
            var stage = new Stage { Uid = uid, Stages = new() };
            Player.collection.InsertOne(player); Inventory.collection.InsertOne(inventory);
            Character.collection.InsertOne(character); Stage.collection.InsertOne(stage);
            harness = new LoopbackSessionHarness(character, player, inventory, "chapter-claims-" + Guid.NewGuid().ToString("N"), startClientLoop: false);
            harness.Session.stage = stage;
            var session = harness.Session;
            JObject Rpc(string name, object request) => GuildRpc(harness, name + "Request", request);
            void Code(JObject response, int expected, string label) => GuildAssert(response.Value<int>("Code") == expected, label + ": " + response);
            void Reload()
            {
                session.player = Player.collection.Find(p => p.PlayerData.Id == uid).Single();
                session.inventory = Inventory.collection.Find(p => p.Uid == uid).Single();
                session.character = Character.collection.Find(p => p.Uid == uid).Single();
                session.stage = Stage.collection.Find(p => p.Uid == uid).Single();
            }
            Dictionary<int, long> Balances() => session.inventory.Items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.Sum(i => i.Count));
            void Unchanged(Dictionary<int, long> before, string label)
            {
                var after = Balances();
                GuildAssert(before.Keys.Union(after.Keys).All(id => before.GetValueOrDefault(id) == after.GetValueOrDefault(id)), label);
            }
            var rewards = TableReaderV2.Parse<RewardTable>().ToDictionary(r => r.Id);
            var goods = TableReaderV2.Parse<RewardGoodsTable>().ToDictionary(r => r.Id);
            void Granted(JObject response, string key, int rewardId, Dictionary<int, long> before)
            {
                var expected = rewards[rewardId].SubIds.Where(id => id > 0).Select(id => goods[id]).ToList();
                var actual = response[key]!.Children().ToList();
                GuildAssert(actual.Count == expected.Count && expected.All(g => actual.Any(a => a.Value<int>("TemplateId") == g.TemplateId && a.Value<long>("Count") == g.Count)), "authored response reward join");
                var after = Balances();
                foreach (var group in expected.Where(g => actual.Any(a => a.Value<int>("TemplateId") == g.TemplateId && a.Value<int>("RewardType") == 1)).GroupBy(g => g.TemplateId))
                    GuildAssert(after.GetValueOrDefault(group.Key) - before.GetValueOrDefault(group.Key) == group.Sum(g => (long)g.Count), "persisted inventory receives authored reward");
                foreach (var reward in actual.Where(a => a.Value<int>("RewardType") == 14))
                    GuildAssert(session.character.ScoreTitles.Single(t => t.Id == reward.Value<uint>("TemplateId")).Score == reward.Value<int>("Count"), "experiment collection score persists in character document");
            }
            void Progress(IEnumerable<int> ids, int stars)
            {
                session.stage.Stages.Clear();
                foreach (int id in ids.Where(id => id > 0).Distinct())
                {
                    int count = Math.Min(3, stars); stars -= count;
                    session.stage.Stages[id] = new StageDatum { StageId = id, Passed = count > 0, StarsMark = (1L << count) - 1 };
                }
                GuildAssert(stars == 0, "chosen authored chapter supports threshold");
                session.stage.Save();
            }
            void Chapter(string name, IEnumerable<(int Id, int Stars, int Reward, IEnumerable<int> Stages)> rows, bool extra)
            {
                Code(Rpc(name, new { TreasureId = 0 }), 20003008, name + " invalid id");
                foreach (var row in rows.Take(2))
                {
                    Progress(row.Stages, row.Stars - 1);
                    var before = Balances();
                    Code(Rpc(name, new { TreasureId = row.Id }), 20003009, name + " insufficient stars");
                    Unchanged(before, "failure grants nothing");
                    Progress(row.Stages, row.Stars);
                    var response = Rpc(name, new { TreasureId = row.Id }); Code(response, 0, name + " success");
                    Reload(); Granted(response, "RewardGoods", row.Reward, before);
                    GuildAssert((extra ? session.player.ExtraChapterTreasures : session.player.ShortStoryTreasures).Contains(row.Id), "claimed treasure persists after reload");
                    before = Balances();
                    Code(Rpc(name, new { TreasureId = row.Id }), 20003010, name + " duplicate after reload");
                    Unchanged(before, "duplicate grants nothing");
                    // Recover the window where rewards committed but the separate claim-list write did not.
                    (extra ? session.player.ExtraChapterTreasures : session.player.ShortStoryTreasures).Remove(row.Id);
                    session.player.SaveChecked(); Reload();
                    Code(Rpc(name, new { TreasureId = row.Id }), 0, name + " reward receipt recovery");
                    Reload(); Unchanged(before, "receipt recovery does not regrant inventory");
                    GuildAssert((extra ? session.player.ExtraChapterTreasures : session.player.ShortStoryTreasures).Contains(row.Id), "receipt recovery repairs claimed list");
                }
            }
            Chapter("ChapterExtraTreasureReward", TableReaderV2.Parse<ChapterExtraClaimTable>().Where(r => r.RequireStar <= r.StageIds.Count(id => id > 0) * 3)
                .Select(r => (r.TreasureId, r.RequireStar, r.RewardId, (IEnumerable<int>)r.StageIds)), true);
            Chapter("ShortStoryTreasureReward", TableReaderV2.Parse<ShortStoryClaimTable>().Where(r => r.RequireStar <= r.StageIds.Count(id => id > 0) * 3)
                .Select(r => (r.TreasureId, r.RequireStar, r.RewardId, (IEnumerable<int>)r.StageIds)), false);
            var experiments = TableReaderV2.Parse<ExperimentClaimTable>().OrderBy(r => r.StarNum).ToList();
            var first = experiments.First();
            Code(Rpc("ExperimentStarReward", new { Id = 0, StarNum = first.StarNum }), 20051002, "invalid experiment");
            Code(Rpc("ExperimentStarReward", new { Id = first.LevelId, StarNum = 0 }), 20051007, "invalid threshold");
            session.stage.Stages.Clear(); session.stage.Save();
            Code(Rpc("ExperimentStarReward", new { Id = first.LevelId, StarNum = first.StarNum }), 20051006, "uncleared experiment");
            Type account = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
            var projection = RequiredMethod(account, "BuildExperimentPayload", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Stage)]);
            var update = RequiredMethod(account, "PushNewlyFinishedExperiments", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session), typeof(long)]);
            foreach (var row in experiments.Take(2))
            {
                Progress([row.StageId], row.StarNum - 1);
                session.stage.Stages[row.StageId].Passed = true; session.stage.Save();
                var before = Balances();
                Code(Rpc("ExperimentStarReward", new { Id = row.LevelId, StarNum = row.StarNum }), 20051009, "insufficient experiment stars");
                Unchanged(before, "experiment failure grants nothing");
                Progress([row.StageId], row.StarNum);
                var beforeClaimLogin = JObject.FromObject(projection.Invoke(null, [session.stage])!);
                var info = beforeClaimLogin["ExperimentInfos"]!.Single(i => i.Value<int>("Id") == row.LevelId);
                GuildAssert(info.Value<long>("StarsMark") == (1L << row.StarNum) - 1, "unclaimed progression is visible at login");
                update.Invoke(null, [session, (long)row.StageId]);
                var pushed = ReadPushPayload<Dictionary<string, object>>(harness, "NotifyUpdateExperimentId", "live experiment stars");
                var pushedInfo = JObject.FromObject(pushed)["Info"]!;
                GuildAssert(pushedInfo.Value<int>("Id") == row.LevelId && pushedInfo.Value<long>("StarsMark") == (1L << row.StarNum) - 1, "live star improvements unblock client claim gate");
                var response = Rpc("ExperimentStarReward", new { Id = row.LevelId, StarNum = row.StarNum });
                Code(response, 0, "experiment success"); Reload();
                Granted(response, "RewardList", row.RewardId, before);
                GuildAssert(session.stage.ExperimentStarClaims[row.LevelId].Contains(row.StarNum), "experiment claim persists");
                var login = JObject.FromObject(projection.Invoke(null, [session.stage])!);
                GuildAssert(login["ExperimentInfos"]!.Single(i => i.Value<int>("Id") == row.LevelId)["StarList"]!.Values<int>().Contains(row.StarNum), "claimed threshold visible after relog");
                before = Balances();
                Code(Rpc("ExperimentStarReward", new { Id = row.LevelId, StarNum = row.StarNum }), 20051008, "duplicate experiment claim after reload");
                Unchanged(before, "experiment retry grants nothing");
                int scoreBeforeRetry = session.character.ScoreTitles.Sum(t => t.Score);
                session.stage.ExperimentStarClaims[row.LevelId].Remove(row.StarNum); session.stage.SaveChecked(); Reload();
                Code(Rpc("ExperimentStarReward", new { Id = row.LevelId, StarNum = row.StarNum }), 0, "experiment reward receipt recovery");
                Reload(); Unchanged(before, "experiment receipt recovery does not grant inventory");
                GuildAssert(session.character.ScoreTitles.Sum(t => t.Score) == scoreBeforeRetry, "receipt recovery does not increment collection score twice");
                GuildAssert(session.stage.ExperimentStarClaims[row.LevelId].Contains(row.StarNum), "receipt recovery repairs experiment claim list");
            }
        }
        finally
        {
            harness?.Dispose();
            Player.collection = oldPlayers; Inventory.collection = oldInventory;
            Character.collection = oldCharacters; Stage.collection = oldStages;
            client.DropDatabase(databaseName);
        }
    }
}
