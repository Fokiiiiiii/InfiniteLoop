using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.reward;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateDateALiveTaskProgressCompatibility()
    {
        TaskTable[] tasks = TableReaderV2.Parse<TaskTable>().Where(row => row.Type == 114).OrderBy(row => row.Priority).ToArray();
        AssertEqual(2, tasks.Length, "Date A Live source task count");
        ConditionTable[] conditions = tasks.Select(task => TableReaderV2.Parse<ConditionTable>().Single(row => row.Id == task.Condition)).ToArray();
        AssertEqual(true, conditions.All(row => row.Type == 27002 && row.Params.Count >= 3 && row.Params[1] > 0),
            "Date A Live authored draw-character predicates");
        AssertEqual(true, conditions.Select(row => row.Params[0]).Distinct().Count() == 1,
            "Both authored tasks reward the same character");
        int characterId = conditions[0].Params[0];
        int drawId = conditions[0].Params[2];
        int sharedDrawId = conditions[0].Params.Skip(2).First(id => id != drawId);
        AssertEqual(true, conditions.All(row => row.Params.Skip(2).Contains(drawId) && row.Params.Skip(2).Contains(sharedDrawId)),
            "Both authored tasks allow the shared draw pools");
        int ineligibleDrawId = TableReaderV2.Parse<ConditionTable>()
            .Where(row => row.Type == 27002 && row.Params.Count >= 3)
            .SelectMany(row => row.Params.Skip(2))
            .First(id => conditions.All(row => !row.Params.Skip(2).Contains(id)));
        int otherCharacterId = TableReaderV2.Parse<ConditionTable>()
            .Where(row => row.Type == 27002 && row.Params.Count >= 3)
            .Select(row => row.Params[0]).First(id => id != characterId);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        const long uid = 99_785;
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid),
            CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid, []), "date-alive-task-progress");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule");
        MethodInfo record = RequiredMethod(module, "RecordQualifiedDrawCharacterProgress", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(AscNet.GameServer.Session), typeof(int), typeof(IEnumerable<int>), typeof(bool)]);
        void Acquire(int pool, int role) => record.Invoke(null, [harness.Session, pool, new[] { role }, true]);
        void Check(int index, int state)
        {
            var task = BuildTaskData(harness.Session).Single(row => row.Id == tasks[index].Id);
            AssertEqual(state, task.State, $"Kurumi task {index} state");
            AssertEqual(state == 3 || state == 4 ? 1 : 0, task.Schedule.Single().Value, $"Kurumi task {index} schedule");
        }
        Check(0, 1);
        Check(1, 1);
        Acquire(ineligibleDrawId, characterId);
        Acquire(drawId, otherCharacterId);
        Check(0, 1);
        Check(1, 1);
        Acquire(drawId, characterId);
        Check(0, 3);
        Check(1, 1);
        InvokeRegisteredRequestHandler(nameof(FinishMultiTaskRequest), harness.Session, 19_784,
            new FinishMultiTaskRequest { TaskIds = [tasks[0].Id, tasks[1].Id, tasks[0].Id] });
        FinishMultiTaskResponse partial = (FinishMultiTaskResponse)ReadResponsePayload(harness, 19_784,
            nameof(FinishMultiTaskResponse), "Date A Live partial task claim", typeof(FinishMultiTaskResponse), maxPacketsToRead: 64);
        AssertEqual(0, partial.Code, "Partial task batch remains valid");
        AssertEqual(true, partial.SuccessTaskIds.SequenceEqual([tasks[0].Id]), "Achieved task claims exactly once");
        AssertEqual(true, partial.NotDealTaskIds.SequenceEqual([tasks[1].Id]), "Unachieved task is not claimed");
        AssertEqual(true, harness.Session.character.Characters.Any(row => row.Id == characterId),
            "First authored task reward grants the character");
        RewardGoods granted = partial.RewardGoodsList.Single();
        AssertEqual($"0:{characterId}:1:{(int)RewardType.Character}:0",
            $"{granted.Id}:{granted.TemplateId}:{granted.Count}:{granted.RewardType}:{granted.ConvertFrom}",
            "New linkage character uses retail draw reward shape");
        harness.Session.player = BsonSerializer.Deserialize<Player>(harness.Session.player.ToBson());
        Check(0, 4);
        Check(1, 1);
        Acquire(sharedDrawId, characterId);
        Check(1, 3);
        InvokeRegisteredRequestHandler(nameof(FinishMultiTaskRequest), harness.Session, 19_785,
            new FinishMultiTaskRequest { TaskIds = [tasks[0].Id, tasks[1].Id] });
        FinishMultiTaskResponse claim = (FinishMultiTaskResponse)ReadResponsePayload(harness, 19_785,
            nameof(FinishMultiTaskResponse), "Date A Live task claim", typeof(FinishMultiTaskResponse), maxPacketsToRead: 64);
        AssertEqual(0, claim.Code, "Date A Live multi-claim responds successfully");
        AssertEqual(true, claim.SuccessTaskIds.SequenceEqual([tasks[1].Id]), "Second earned task claims once");
        AssertEqual(true, claim.NotDealTaskIds.SequenceEqual([tasks[0].Id]), "Already claimed task remains rejected");
        CharacterTable kurumi = TableReaderV2.Parse<CharacterTable>().Single(row => row.Id == characterId);
        int shardCount = Character.GetMinCharacterFragment(characterId)?.DecomposeCount ?? 18;
        RewardGoods shard = claim.RewardGoodsList.Single();
        AssertEqual($"0:{kurumi.ItemId}:{shardCount}:{(int)RewardType.Item}:{characterId}",
            $"{shard.Id}:{shard.TemplateId}:{shard.Count}:{shard.RewardType}:{shard.ConvertFrom}",
            "Duplicate linkage character converts to shard in draw reward shape");
        AssertEqual(shardCount, harness.Session.inventory.Items.Single(item => item.Id == kurumi.ItemId).Count,
            "Duplicate linkage character shard lands in inventory");
        harness.Session.player = BsonSerializer.Deserialize<Player>(harness.Session.player.ToBson());
        harness.Session.inventory = BsonSerializer.Deserialize<Inventory>(harness.Session.inventory.ToBson());
        harness.Session.character = BsonSerializer.Deserialize<Character>(harness.Session.character.ToBson());
        Check(0, 4);
        Check(1, 4);
        byte[] beforeInventory = harness.Session.inventory.ToBson();
        byte[] beforeCharacter = harness.Session.character.ToBson();
        InvokeRegisteredRequestHandler(nameof(FinishMultiTaskRequest), harness.Session, 19_786,
            new FinishMultiTaskRequest { TaskIds = [tasks[0].Id, tasks[1].Id] });
        FinishMultiTaskResponse retry = (FinishMultiTaskResponse)ReadResponsePayload(harness, 19_786,
            nameof(FinishMultiTaskResponse), "Date A Live relogged retry", typeof(FinishMultiTaskResponse), maxPacketsToRead: 64);
        AssertEqual(0, retry.SuccessTaskIds.Count, "Claim retry grants nothing");
        AssertEqual(2, retry.NotDealTaskIds.Count, "Claim retry rejects both completed tasks");
        AssertEqual(true, beforeInventory.SequenceEqual(harness.Session.inventory.ToBson()), "Retry does not duplicate inventory reward");
        AssertEqual(true, beforeCharacter.SequenceEqual(harness.Session.character.ToBson()), "Retry does not duplicate character reward");
        TaskTable ordinary = TableReaderV2.Parse<TaskTable>().First(task => task.Suffix == "Dormitory"
            && task.ShowAfterTaskId is not > 0 && task.RewardId > 0 && task.Type != 12 && string.IsNullOrWhiteSpace(task.EndTime)
            && TableReaderV2.Parse<ConditionTable>().Any(row => row.Id == task.Condition && row.Type is >= 29000 and < 29100));
        harness.Session.player.MissionProgress.ConditionCounters[ordinary.Condition] = ordinary.Result ?? 1;
        InvokeRegisteredRequestHandler(nameof(FinishTaskRequest), harness.Session, 19_787, new FinishTaskRequest { TaskId = ordinary.Id });
        FinishTaskResponse ordinaryClaim = (FinishTaskResponse)ReadResponsePayload(harness, 19_787,
            nameof(FinishTaskResponse), "Ordinary task claim", typeof(FinishTaskResponse), maxPacketsToRead: 64);
        AssertEqual(0, ordinaryClaim.Code, "Ordinary task claim code");
        AssertEqual(string.Join(",", TableReaderV2.Parse<RewardTable>().First(row => row.Id == ordinary.RewardId).SubIds.Order()),
            string.Join(",", ordinaryClaim.RewardGoodsList.Select(row => row.Id).Order()), "Ordinary task keeps RewardGoods row ids");
    }
}
