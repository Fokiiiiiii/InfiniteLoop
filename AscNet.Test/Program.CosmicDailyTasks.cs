using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.task;
using MessagePack;
using MongoDB.Bson;

namespace AscNet.Test;

internal static partial class Program
{
    // Cosmic Wonders TaskTimeLimit 764 remains dormant while its 50302 window is absent.
    private static void ValidateCosmicDailyTasks()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _);
        Type taskModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule");
        MethodInfo dispatch = typeof(Session).GetMethod("InvokeRequestHandler", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(Session).FullName, "InvokeRequestHandler");
        MethodInfo stageClear = RequiredMethod(taskModule, "RecordStageClear", BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(int), typeof(int), typeof(int), typeof(bool)]);
        TaskTimeLimitTable limit = TableReaderV2.Parse<TaskTimeLimitTable>().Single(row => row.Id == 764);
        Dictionary<int, TaskTable> tasks = TableReaderV2.Parse<TaskTable>().Where(row => limit.DayTaskId.Contains(row.Id)).ToDictionary(row => row.Id);
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        TaskTable Of(int type) => tasks.Values.Single(task => conditions[task.Condition].Type == type);
        TaskTable login = Of(10201), spend = Of(11202), active = Of(11203);

        const long uid = 48_764;
        Player player = CreateDrawCompatibilityPlayer(uid);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player,
            CreateDrawCompatibilityInventory(uid, [new Item { Id = Inventory.DailyActiveness, Count = 0 }]), "cosmic-daily");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        int packetId = 48_7640;
        bool Visible(TaskTable task) => BuildTaskData(harness.Session).Any(row => row.Id == task.Id);
        int Claim(TaskTable task)
        {
            int id = packetId++;
            dispatch.Invoke(harness.Session, [GetRegisteredRequestHandler(nameof(FinishTaskRequest)), new Packet.Request
            {
                Id = id, Name = nameof(FinishTaskRequest), Content = MessagePackSerializer.Serialize(new FinishTaskRequest { TaskId = task.Id })
            }]);
            // Task post-hooks (NotifyTask batch, reward pushes) legitimately precede the response.
            FinishTaskResponse response = ReadResponsePayload<FinishTaskResponse>(
                harness, id, nameof(FinishTaskResponse), "Cosmic day-task claim", maxPacketsToRead: 8);
            while (harness.TryReadAvailablePacket("drain cosmic claim", out _)) { }
            return response.Code;
        }
        void Spend(int serum)
        {
            stageClear.Invoke(null, [harness.Session, 0, 1, serum, false]);
            while (harness.TryReadAvailablePacket("drain stage clear", out _)) { }
        }

        AssertEqual(false, Visible(login), "closed Cosmic login daily is absent");
        AssertEqual(false, Visible(spend), "closed Cosmic Serum daily is absent");
        AssertEqual(false, Visible(active), "closed Cosmic activeness daily is absent");
        string inventoryBefore = harness.Session.inventory.ToJson();
        AssertEqual(20026007, Claim(login), "closed Cosmic login daily rejects reward");
        AssertEqual(20026007, Claim(spend), "closed Cosmic spend daily rejects reward");
        AssertEqual(20026007, Claim(active), "closed Cosmic activeness daily rejects reward");
        Spend(spend.Result ?? 1);
        AssertEqual(false, Visible(spend), "Serum spend does not unlock closed Cosmic daily");
        AssertEqual(false, player.MissionProgress.DayTaskConditionCounters.ContainsKey(spend.Condition),
            "closed Cosmic spend does not accumulate a daily counter");
        AssertEqual(inventoryBefore, harness.Session.inventory.ToJson(), "closed Cosmic claims grant no inventory rewards");
    }
}
