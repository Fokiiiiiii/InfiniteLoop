using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.task;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateWeeklyActivenessCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        const long uid = 99781;
        Player player = CreateDrawCompatibilityPlayer(uid);
        Inventory inventory = CreateDrawCompatibilityInventory(uid, []);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, inventory, "weekly-activeness");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        harness.Session.stage.FinishedTasks.Clear();
        player.MissionProgress.ClaimedTaskIds.Clear();
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule");
        MethodInfo reset = RequiredMethod(module, "EnsureMissionResets", BindingFlags.Static | BindingFlags.NonPublic, [typeof(AscNet.GameServer.Session)]);
        reset.Invoke(null, [harness.Session]);
        CurrentTaskActivenessTable rewards = TableReaderV2.Parse<CurrentTaskActivenessTable>().Single(row => row.Type == 5);
        int[] tasks = TableReaderV2.Parse<TaskTable>().Where(row => row.Tag == 1).Select(row => row.Id).Take(rewards.Activeness.Max()).ToArray();
        int packetId = 997810;
        GetWeeklyActivenessRewardResponse Claim()
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler("GetWeeklyActivenessRewardRequest", harness.Session, id, new Dictionary<string, int>());
            return ReadResponsePayload<GetWeeklyActivenessRewardResponse>(harness, id, nameof(GetWeeklyActivenessRewardResponse), "weekly claim", maxPacketsToRead: 64);
        }
        AssertEqual(20026034, Claim().Code, "Weekly insufficient completed tasks");
        for (int index = 0; index < rewards.Activeness.Count; index++)
        {
            harness.Session.stage.FinishedTasks = tasks.Take(rewards.Activeness[index]).ToList();
            var balances = inventory.Items.ToDictionary(item => item.Id, item => item.Count);
            var response = Claim();
            AssertEqual(0, response.Code, "Weekly eligible milestone");
            AssertEqual(true, response.WeeklyTaskActivenessProgress.Contains(rewards.Activeness[index]), "Weekly response uses milestone values");
            var reward = TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardTable>().Single(row => row.Id == rewards.RewardId[index]);
            var goods = TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardGoodsTable>().Where(row => reward.SubIds.Contains(row.Id)).ToList();
            AssertEqual(string.Join("|", goods.Select(row => $"{row.Id}:{row.TemplateId}:{row.Count}")),
                string.Join("|", response.RewardGoodsList.Select(row => $"{row.Id}:{row.TemplateId}:{row.Count}")), "Weekly distinct table reward");
            foreach (var item in goods.GroupBy(row => row.TemplateId))
                AssertEqual(balances.GetValueOrDefault(item.Key) + item.Sum(row => (long)row.Count),
                    inventory.Items.Single(row => row.Id == item.Key).Count, "Weekly reward mutates inventory");
        }
        AssertEqual(20026032, Claim().Code, "Weekly duplicate rejected");
        var inventoryAfterClaim = inventory.ToBson();
        AssertEqual(20026032, Claim().Code, "Repeated duplicate weekly claim rejected");
        AssertEqual(Convert.ToBase64String(inventoryAfterClaim), Convert.ToBase64String(inventory.ToBson()), "Weekly retries do not duplicate goods");
        harness.Session.player = player = BsonSerializer.Deserialize<Player>(player.ToBson());
        AssertEqual(rewards.Activeness.Count, player.MissionProgress.WeeklyTaskActivenessProgress.Count, "Weekly BSON persistence");
        AssertEqual(20026032, Claim().Code, "Weekly relog retry rejected");
        var groups = TableReaderV2.Parse<NewPlayerTaskGroupTable>().Take(2).ToArray();
        int mask = 0;
        foreach (var group in groups)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(SetNewPlayerTaskActiveUiRequest), harness.Session, id, new SetNewPlayerTaskActiveUiRequest { Group = group.Id });
            mask |= 1 << group.Id;
            AssertEqual(mask, ReadResponsePayload<SetNewPlayerTaskActiveUiResponse>(harness, id, nameof(SetNewPlayerTaskActiveUiResponse), "new player UI flag").Result, "UI flag accumulates bits");
        }
        AssertEqual(mask, BsonSerializer.Deserialize<Player>(player.ToBson()).PlayerData.NewPlayerTaskActiveUi, "UI flag persistence");
        int invalidId = packetId++;
        InvokeRegisteredRequestHandler(nameof(SetNewPlayerTaskActiveUiRequest), harness.Session, invalidId, new SetNewPlayerTaskActiveUiRequest { Group = -1 });
        AssertEqual(true, ReadResponsePayload<SetNewPlayerTaskActiveUiResponse>(harness, invalidId, nameof(SetNewPlayerTaskActiveUiResponse), "invalid group").Code != 0, "Invalid UI group rejected");
        harness.Session.player = player = BsonSerializer.Deserialize<Player>(player.ToBson());
        harness.Session.character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1021001, fashionId: 3021001));
        var doLogin = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "DoLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(AscNet.GameServer.Session)]);
        doLogin.Invoke(null, [harness.Session]);
        Dictionary<string, AscNet.GameServer.Packet.Push> startup = new(StringComparer.Ordinal);
        for (int index = 0; index < 192 && !startup.ContainsKey("NotifyWheelchairManualActivityUpdate"); index++)
        {
            var push = ReadStartupPush(harness, "Weekly activeness relog startup");
            startup.TryAdd(push.Name, push);
        }
        var loginTasks = DeserializeStartupPush<NotifyTaskData>(startup, nameof(NotifyTaskData), "Weekly login tasks");
        AssertIntegerList(rewards.Activeness.Select(value => (long)value).ToArray(),
            loginTasks.TaskData.WeeklyTaskActivenessProgress.Select(value => (long)value).ToArray(), "Weekly login claimed milestones");
        AssertEqual(true, tasks.All(loginTasks.TaskData.FinishedTasks.Contains), "Weekly login completed task points");
        var loginUi = DeserializeStartupPush<NotifyNewPlayerTaskStatus>(startup, nameof(NotifyNewPlayerTaskStatus), "Weekly login new player UI");
        AssertEqual(mask, loginUi.NewPlayerTaskActiveUi, "UI mask visible after actual login");
        player.MissionProgress.WeeklyResetWeek--;
        reset.Invoke(null, [harness.Session]);
        AssertEqual(0, player.MissionProgress.WeeklyTaskActivenessProgress.Count, "Weekly boundary clears claims");
        AssertEqual(20026034, Claim().Code, "Weekly boundary clears earned points");
        harness.Session.stage.FinishedTasks = tasks.Take(rewards.Activeness[0]).ToList();
        AssertEqual(0, Claim().Code, "New week can claim again");
    }
}
