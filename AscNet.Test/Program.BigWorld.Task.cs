using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.statussyncfight.level;
using AscNet.Table.V2.share.statussyncfight.level.sceneconfig;
using MongoDB.Bson.Serialization;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static void ValidateBigWorldTask()
        {
            Type taskModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule");
            Type bwTask = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BigWorld.BigWorldTaskModule");
            const long playerId = 99_812;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var playerSaves, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, inventory, "big-world-task");
            int packetId = 0;
            FinishMultiTaskResponseProbe Claim(params int[] ids)
            {
                InvokeRegisteredRequestHandler("FinishMultiTaskRequest", harness.Session, ++packetId, new Dictionary<string, object> { ["TaskIds"] = ids.ToList() });
                return ReadResponsePayload<FinishMultiTaskResponseProbe>(harness, packetId, "FinishMultiTaskResponse", "FinishMultiTaskRequest", 20);
            }
            Dictionary<uint, LoginTask> Login() =>
                ((List<LoginTask>)taskModule.GetMethod("BuildTaskData", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [harness.Session])!)
                    .ToDictionary(task => task.Id);
            NotifyTask ProgressChanged()
            {
                bwTask.GetMethod("OnProgressChanged", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [harness.Session]);
                return ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "bw task delta");
            }

            // Supply chests of Explore 1 (POIs 101/102/103) in levels whose ConfigGroups hold them.
            // Group ids are per sector bundle: key rows by (Sector, ConfigGroupId) like the server.
            Dictionary<(string, int), int> levelByGroup = TableReaderV2.Parse<LevelTable>()
                .SelectMany(level => level.ConfigGroups.Select(group => ((level.SectorName.ToLowerInvariant(), group), level.Id)))
                .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().Id);
            List<BigWorldClaimedSceneObject> chests = TableReaderV2.Parse<LevelSceneObjectTable>()
                .Where(row => row.CollectRewardId > 0 && row.CourseGroupId is 101 or 102 or 103 && levelByGroup.ContainsKey((row.Sector, row.ConfigGroupId)))
                .Select((row, i) => new BigWorldClaimedSceneObject { LevelId = levelByGroup[(row.Sector, row.ConfigGroupId)], PlaceId = row.PlaceId, Uuid = i + 1 })
                .ToList();
            AssertEqual(true, chests.Count >= 19, "explore-1 chest rows available");

            Dictionary<uint, LoginTask> login = Login();
            AssertEqual((0, 1), (login[990007].Schedule[0].Value, login[990007].State), "login 990007 before collect");
            AssertEqual(990007u, login[990007].Schedule[0].Id, "schedule id = BigWorldCondition id");
            AssertEqual((1, 3), (login[999999].Schedule[0].Value, login[999999].State), "daily login task achieved");

            player.BigWorldState.ClaimedSceneObjects.AddRange(chests.Take(2));
            NotifyTask delta = ProgressChanged();
            AssertEqual("990007,990008,990009,990010", string.Join(",", delta.Tasks.Tasks.Select(t => t.Id).Order()), "delta holds only chest tasks");
            AssertEqual(2, delta.Tasks.Tasks.Single(t => t.Id == 990007).Schedule[0].Value, "2 chests");
            AssertEqual(20026007, Claim(990007).Code, "unfinished claim rejected");

            player.BigWorldState.ClaimedSceneObjects.AddRange(chests.Skip(2).Take(4));
            delta = ProgressChanged();
            SyncProbe(delta, 990007, 5, 3);
            SyncProbe(delta, 990008, 6, 1);
            bwTask.GetMethod("OnProgressChanged", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [harness.Session]);
            if (harness.TryReadAvailablePacket("no-change delta", out _))
                throw new InvalidDataException("OnProgressChanged pushed without a change.");

            AssertEqual(20026007, Claim(990007, 990008).Code, "mixed claim atomic reject");
            AssertEqual(false, player.BigWorldState.ClaimedTaskPeriods.ContainsKey(990007), "atomic reject leaves no claim");
            FinishMultiTaskResponseProbe ok = Claim(990007);
            AssertEqual((0, "990007"), (ok.Code, string.Join(",", ok.SuccessTaskIds)), "claim ok");
            AssertEqual(true, ok.RewardGoodsList.Count > 0, "claim granted BigWorldReward goods");
            AssertEqual(20026006, Claim(990007).Code, "second claim rejected");
            AssertEqual(20026005, Claim(123, 990007).Code, "unknown id in BigWorld claim rejected");

            // Quest-derived counter: 990099 = quest 7003 finished.
            AssertEqual(1, Login()[990099].State, "990099 active before quest");
            player.BigWorldState.QuestData.FinishedQuests.Add(7003);
            AssertEqual(3, Login()[990099].State, "990099 achieved after quest");

            player.Save();
            Player reloaded = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson!);
            AssertEqual(true, reloaded.BigWorldState.ClaimedTaskPeriods.ContainsKey(990007), "claim persisted");
            harness.Session.player = reloaded;
            login = Login();
            AssertEqual((5, 4), (login[990007].Schedule[0].Value, login[990007].State), "reloaded 990007 finished");
            AssertEqual((6, 1), (login[990008].Schedule[0].Value, login[990008].State), "reloaded 990008 progress");
            Console.WriteLine("BigWorld task validation passed.");
        }

        private static void SyncProbe(NotifyTask delta, uint id, int value, int state)
        {
            var task = delta.Tasks.Tasks.Single(t => t.Id == id);
            AssertEqual((value, state), (task.Schedule[0].Value, task.State), $"delta {id}");
        }

        [MessagePack.MessagePackObject(true)]
        public class FinishMultiTaskResponseProbe
        {
            public int Code { get; set; }
            public List<RewardGoods> RewardGoodsList { get; set; } = new();
            public List<int> SuccessTaskIds { get; set; } = new();
            public List<int> NotDealTaskIds { get; set; } = new();
        }
    }
}
