using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben.cerberusgame;
using AscNet.Table.V2.share.task;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateCerberusGameTasks()
    {
        HashSet<int> timeIds = TableReaderV2.Parse<CerberusGameChapterTable>().Select(row => row.TimeId)
            .Concat(TableReaderV2.Parse<CerberusGameActivityTable>().Select(row => row.TimeId ?? 0)).Where(id => id > 0).ToHashSet();
        var conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        var tasks = TableReaderV2.Parse<TaskTable>().ToDictionary(row => row.Id);
        var group = TableReaderV2.Parse<TaskTimeLimitTable>().First(row => row.TimeId is int timeId && timeIds.Contains(timeId)
            && ActivityScheduleService.IsOpen(timeId, DateTimeOffset.UtcNow)
            && row.TaskId.Any(id => tasks.TryGetValue(id, out var task) && task.Type == 11
                && conditions.TryGetValue(task.Condition, out var condition) && condition.Type == 15201
                && condition.Params.Count >= 2 && condition.Params[0] == 1
                && condition.Params.Skip(1).All(stage => stage > 0 && CerberusGameModule.IsStage((uint)stage))));
        var task = group.TaskId.Select(id => tasks[id]).First(row => conditions.TryGetValue(row.Condition, out var condition)
            && condition.Type == 15201 && condition.Params.Count >= 2
            && condition.Params.Skip(1).All(stage => stage > 0 && CerberusGameModule.IsStage((uint)stage)));
        int stageId = conditions[task.Condition].Params[1];
        const long uid = 98_890;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(out _, out _, out _, out _);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, []), "cerberus-game-tasks");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        harness.Session.stage.Stages.Clear();
        LoginTask Row() => BuildTaskData(harness.Session).Single(row => row.Id == task.Id);
        int packetId = 98_891;
        FinishTaskResponse Claim()
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(FinishTaskRequest), harness.Session, id, new FinishTaskRequest { TaskId = task.Id });
            return ReadResponsePayload<FinishTaskResponse>(harness, id, nameof(FinishTaskResponse), "Cerberus authored task", maxPacketsToRead: 64);
        }
        AssertEqual(0, Row().Schedule.Single().Value, "Cerberus task initially incomplete");
        AssertEqual(true, Claim().Code != 0, "Cerberus task rejects unearned claim");
        harness.Session.stage.AddStage(new StageDatum { StageId = stageId, Passed = true });
        harness.Session.stage = BsonSerializer.Deserialize<Stage>(harness.Session.stage.ToBson());
        AssertEqual(task.Result ?? 1, Row().Schedule.Single().Value, "Cerberus task derives persisted stage completion");
        AssertEqual(3, Row().State, "Cerberus authored task achieved");
        FinishTaskResponse earned = Claim();
        AssertEqual(0, earned.Code, "Cerberus task claim succeeds");
        AssertEqual(true, earned.RewardGoodsList.Count > 0, "Cerberus task grants authored reward");
        harness.Session.player = BsonSerializer.Deserialize<Player>(harness.Session.player.ToBson());
        AssertEqual(4, Row().State, "Cerberus task claim survives BSON reload");
        string inventory = harness.Session.inventory.ToJson();
        AssertEqual(true, Claim().Code != 0, "Cerberus task rejects duplicate claim");
        AssertEqual(inventory, harness.Session.inventory.ToJson(), "Cerberus task duplicate cannot grant twice");
        ActivityScheduleEntry[] schedules = (ActivityScheduleEntry[])ActivityScheduleService.All;
        int slot = Array.FindIndex(schedules, row => row.Id == group.TimeId);
        ActivityScheduleEntry original = schedules[slot];
        try
        {
            schedules[slot] = new ActivityScheduleEntry(original.Id, 1, 2, "synthetic-test:cerberus-task-closed");
            AssertEqual(false, BuildTaskData(harness.Session).Any(row => row.Id == task.Id), "Cerberus closed-window task hidden");
        }
        finally { schedules[slot] = original; }
    }
}
