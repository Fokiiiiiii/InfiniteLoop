using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.lotto;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    /// <summary>Undated Cosmic Wonders stays closed; the separate self-choice lotto 49501 remains available.</summary>
    public static void ValidateMissingCalendarAscNetPolicy()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MethodInfo calendar = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildNewActivityCalendarPayload", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(DateTimeOffset)]);
        int[] open = (int[])((Dictionary<string, object?>)calendar.Invoke(null, [now])!)["OpenActivityIds"]!;
        AssertEqual(false, open.Contains(48011), "undated Cosmic Wonders calendar entry stays closed");

        LottoPrimaryTable primary = TableReaderV2.Parse<LottoPrimaryTable>().Single(row => row.TimeId == 49501);
        int lottoId = primary.LottoIdList[0];
        int otherLottoId = primary.LottoIdList[1];
        LottoTable lotto = TableReaderV2.Parse<LottoTable>().Single(row => row.Id == lottoId);
        Dictionary<int, LottoRewardTable> rewards = TableReaderV2.Parse<LottoRewardTable>()
            .Where(row => row.LottoId == lottoId).ToDictionary(row => row.Id);
        int cost = lotto.ConsumeCountList[0];
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players, out _, out _);
        const long uid = 48_950;
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, [new Item { Id = lotto.ConsumeId, Count = cost }]), "missing-calendar-lotto");
        int packetId = 48_950;
        T Call<T>(object request) where T : class
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(request.GetType().Name, harness.Session, id, request);
            return ReadResponsePayload<T>(harness, id, typeof(T).Name, request.GetType().Name, maxPacketsToRead: 16);
        }
        long Balance(int itemId) => harness.Session.inventory.Items.SingleOrDefault(item => item.Id == itemId)?.Count ?? 0;
        LottoRequest draw = new() { Id = primary.Id, LottoId = lottoId };

        AssertEqual(20111008, Call<LottoResponse>(draw).Code, "self-choice draw requires a selected pool");
        AssertEqual(0, Call<LottoSelfChoiceSelectResponse>(new LottoSelfChoiceSelectRequest
        { LottoPrimaryId = primary.Id, SelectedLottoId = lottoId }).Code, "self-choice lotto select open on actual clock");
        AssertEqual(lottoId, harness.Session.player.Lotto.SelectedPrimaryIdToLottoId[primary.Id], "self-choice selection recorded");
        AssertEqual(true, players.ReplaceOneCalls > 0, "self-choice selection saved before acknowledgement");
        AssertEqual(true, Call<LottoInfoResponse>(new LottoInfoRequest()).LottoInfos.Any(info => info.Id == lottoId),
            "selected self-choice pool listed");

        // Paid draw: exact authored first cost, one authored first-draw reward, durable receipt and progress.
        int savesBefore = players.ReplaceOneCalls;
        LottoResponse drawn = Call<LottoResponse>(draw);
        AssertEqual(0, drawn.Code, "paid self-choice draw accepted");
        AssertEqual(true, rewards.TryGetValue(drawn.LottoRewardId, out LottoRewardTable? reward) && reward!.Weights[0] > 0,
            "draw selects an authored first-draw reward");
        AssertEqual(reward!.TemplateId, drawn.RewardList.Single().TemplateId, "draw returns the selected reward");
        AssertEqual(0L, Balance(lotto.ConsumeId), "draw consumes exactly the authored first cost");
        AssertEqual(true, players.ReplaceOneCalls > savesBefore, "draw progress persisted");
        Player reloaded = BsonSerializer.Deserialize<Player>(harness.Session.player.ToBson());
        LottoStateInfo progress = reloaded.Lotto.Infos.Single(info => info.Id == lottoId);
        AssertEqual(drawn.LottoRewardId, progress.LottoRewards.Single(), "drawn reward survives reload");
        AssertEqual(true, progress.Pending is null, "no pending draw after commit");

        string before = harness.Session.player.Lotto.ToJson();
        string inventoryBefore = harness.Session.inventory.ToJson();
        AssertEqual(20012004, Call<LottoResponse>(draw).Code, "second draw requires the next authored cost");
        AssertEqual(before, harness.Session.player.Lotto.ToJson(), "unpaid draw preserves pool");
        AssertEqual(inventoryBefore, harness.Session.inventory.ToJson(), "unpaid draw preserves inventory and receipts");

        // Switching the self-choice pool guards the previous pool and keeps its progress.
        AssertEqual(0, Call<LottoSelfChoiceSelectResponse>(new LottoSelfChoiceSelectRequest
        { LottoPrimaryId = primary.Id, SelectedLottoId = otherLottoId }).Code, "self-choice switch accepted");
        AssertEqual(20111008, Call<LottoResponse>(draw).Code, "unselected pool cannot be drawn");
        AssertEqual(0, Call<LottoSelfChoiceSelectResponse>(new LottoSelfChoiceSelectRequest
        { LottoPrimaryId = primary.Id, SelectedLottoId = lottoId }).Code, "self-choice switch back accepted");
        AssertEqual(1, Call<LottoInfoResponse>(new LottoInfoRequest()).LottoInfos.Single(info => info.Id == lottoId).LottoRewards.Count,
            "pool progress kept across selection changes");

        // TEST-ONLY in-memory expiry: an authored window (were one added) still closes the pool.
        ActivityScheduleEntry[] schedules = (ActivityScheduleEntry[])ActivityScheduleService.All;
        int index = Array.FindIndex(schedules, entry => entry.Id == 49501);
        ActivityScheduleEntry saved = schedules[index];
        schedules[index] = saved with { StartTime = 1, EndTime = 2 };
        try
        {
            before = harness.Session.player.Lotto.ToJson();
            AssertEqual(20111001, Call<LottoResponse>(draw).Code, "expired self-choice lotto rejects draw");
            AssertEqual(before, harness.Session.player.Lotto.ToJson(), "expired draw preserves pool");
        }
        finally { schedules[index] = saved; }
    }
}
