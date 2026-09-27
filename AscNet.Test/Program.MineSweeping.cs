using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.miniactivity.minesweepinggame;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    public static void RunMineSweepingCompatibility()
    {
        foreach (string name in new[] { "MineSweepingStartStageRequest", "MineSweepingOpenRequest", "MineSweepingFlagRequest" })
            AssertEqual("AscNet.GameServer.Handlers.MineSweepingModule", GetRegisteredRequestHandlerMethod(name).DeclaringType?.FullName,
                $"{name} registered handler");
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.MineSweepingModule");
        MethodInfo buildLogin = RequiredMethod(module, "BuildLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]);
        MethodInfo recoverPending = RequiredMethod(module, "RecoverPending", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]);
        int Const(string name) => (int)module.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        // EN CodeText ids, asserted as literals so a remap is caught.
        AssertEqual("20134001,20134004,20134007,20134008,20134009,20134010,20134011,20134012,20134013,20134014,20012004,2",
            string.Join(",", new[] { "ActivityNotOpen", "StageNotStart", "YOutRange", "XOutRange", "FlagError", "StageHadStarted",
                "PreChapterNotFinish", "PreStageNotFinish", "StageNotInChapter", "GridIsSwept", "ItemCountNotEnough", "ServerInternalError" }.Select(Const)),
            "MineSweeping CodeText ids");
        const int activityNotOpen = 20134001;

        MineSweepingActivityTable activity = TableReaderV2.Parse<MineSweepingActivityTable>().Single(row => row.TimeId == 50302);
        Dictionary<int, MineSweepingStageTable> stages = TableReaderV2.Parse<MineSweepingStageTable>().ToDictionary(row => row.Id);
        AssertEqual("1,2,3,4", string.Join(",", activity.ChapterIds.Where(id => id > 0)), "MineSweeping activity chapters");
        AssertEqual(8, stages.Count, "dormant MineSweeping authored stages retained");

        const long playerId = 99_890;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Inventory> inventorySaves);
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, [new Item { Id = activity.CoinItemId, Count = stages[101].CostCoinNum }]),
            "mine-sweeping-closed");
        long Count(int id) => harness.Session.inventory.Items.FirstOrDefault(item => item.Id == id)?.Count ?? 0;
        int packetId = 700;
        T Call<T>(string name, object request)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(name, harness.Session, id, request);
            while (true)
            {
                Packet packet = harness.ReadPacket(name);
                if (packet.Type == Packet.ContentType.Push) continue;
                Packet.Response wire = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, wire.Id, $"{name} response id");
                return MessagePackSerializer.Deserialize<T>(wire.Content);
            }
        }
        MineSweepingStartStageResponse Start(int chapter, int stage) =>
            Call<MineSweepingStartStageResponse>("MineSweepingStartStageRequest", new MineSweepingStartStageRequest { ActivityChapterId = chapter, ActivityStageId = stage });
        MineSweepingOpenResponse Open(int chapter, int stage, int x, int y) =>
            Call<MineSweepingOpenResponse>("MineSweepingOpenRequest", new MineSweepingOpenRequest { ActivityChapterId = chapter, ActivityStageId = stage, XIndex = x, YIndex = y });
        int Flag(int chapter, int stage, int x, int y, bool on) =>
            Call<MineSweepingFlagResponse>("MineSweepingFlagRequest", new MineSweepingFlagRequest { ActivityChapterId = chapter, ActivityStageId = stage, XIndex = x, YIndex = y, IsFlag = on }).Code;
        NotifyMineSweepingData? Login() => (NotifyMineSweepingData?)buildLogin.Invoke(null, [harness.Session]);
        long coins = Count(activity.CoinItemId);
        string inventory = harness.Session.inventory.ToJson();
        string progress = harness.Session.player.MineSweeping.ToJson();
        AssertEqual(null, Login(), "closed MineSweeping does not push login activity");
        AssertEqual(activityNotOpen, Start(1, 101).Code, "closed MineSweeping rejects start");
        AssertEqual(activityNotOpen, Open(1, 101, 1, 1).Code, "closed MineSweeping rejects open");
        AssertEqual(activityNotOpen, Flag(1, 101, 1, 1, true), "closed MineSweeping rejects flag");
        AssertEqual(coins, Count(activity.CoinItemId), "closed requests spend no MineSweeping coins");
        AssertEqual(inventory, harness.Session.inventory.ToJson(), "closed requests award no inventory goods");
        AssertEqual(progress, harness.Session.player.MineSweeping.ToJson(), "closed requests create no stage progress");
        AssertEqual(0, playerSaves.ReplaceOneCalls, "closed requests persist no MineSweeping progress");
        AssertEqual(0, inventorySaves.ReplaceOneCalls, "closed requests persist no reward or spend");

        // A previously durable unpaid start still reconciles, even after the event is removed.
        MineSweepingStageTable first = stages[101];
        harness.Session.player.MineSweeping.Pending = new MineSweepingPendingGrant
        {
            ClaimKey = $"minesweeping:{activity.Id}:1:start:1",
            CostItemId = activity.CoinItemId,
            CostCount = first.CostCoinNum
        };
        AssertEqual(true, (bool)recoverPending.Invoke(null, [harness.Session])!, "closed activity settles existing durable cost");
        AssertEqual(coins - first.CostCoinNum, Count(activity.CoinItemId), "existing cost charged once");
        AssertEqual(null, harness.Session.player.MineSweeping.Pending, "existing cost intent cleared");
        AssertEqual(true, (bool)recoverPending.Invoke(null, [harness.Session])!, "closed activity recovery remains idempotent");
        AssertEqual(coins - first.CostCoinNum, Count(activity.CoinItemId), "recovery does not double-charge");
    }

}
