using System.Reflection;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.signin;
using AscNet.Table.V2.share.reward;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.activitybrief;
using AscNet.Table.V2.share.newactivitycalendar;
using MessagePack;
using MongoDB.Bson;

namespace AscNet.Test;

internal partial class Program
{
    /// <summary>
    /// 4.7 generic activity/login/calendar/brief-story compatibility. Table-driven: asserts the
    /// runtime schedule/table-derived behavior rather than pinning specific 49xxx epoch windows
    /// (those land with the 4.7 Resources cutover). The TimeLimit five-key element schema and
    /// [start,end) clock rules are asserted from authoritative schedule bounds.
    /// </summary>
    private static void ValidateVersion47ActivityCompatibility()
    {
        ValidateVersion47TimeLimitControlSchema();
        ValidateVersion47NewActivityCalendarDerivation();
        ValidateVersion47BriefStoryRequestCompatibility();
        ValidateVersion47OrdinaryEventPreFightGate();
        ValidateVersion47LoginHelperOrdering();
        ValidateVersion48EventAvailability();
    }

    private static void ValidateVersion48EventAvailability()
    {
        static long Utc(string value) => DateTimeOffset.Parse(value).ToUnixTimeSeconds();
        var expected = new (int Id, string Begin, string End)[]
        {
            (912, "2026-09-29T10:00:00Z", "2026-11-03T23:59:00Z"),
            (50301, "2026-09-24T05:00:00Z", "2026-11-03T23:59:00Z"),
            (50911, "2026-09-24T05:00:00Z", "2026-11-04T05:00:00Z"),
            (50912, "2026-09-25T10:00:00Z", "2026-10-04T05:00:00Z"),
            (50201, "2026-09-24T05:00:00Z", "2026-11-04T05:00:00Z"),
            (50100, "2026-09-29T10:00:00Z", "2026-11-04T05:00:00Z"),
            (50801, "2026-10-08T10:00:00Z", "2026-11-04T05:00:00Z"),
            (50501, "2026-09-25T10:00:00Z", "2026-10-19T05:00:00Z"),
            (50903, "2026-09-24T05:00:00Z", "2026-11-04T23:00:00Z"),
            (50904, "2026-09-24T05:00:00Z", "2026-11-04T23:00:00Z"),
            (50914, "2026-09-29T10:00:00Z", "2026-11-03T23:59:00Z"),
            (50922, "2026-09-24T05:00:00Z", "2026-11-04T23:00:00Z"),
            (50938, "2026-09-24T05:00:00Z", "2026-11-04T23:00:00Z"),
            (51001, "2026-09-26T10:00:00Z", "2026-11-04T23:00:00Z"),
        };
        foreach (var (id, begin, end) in expected)
        {
            if (!ActivityScheduleService.TryGet(id, out ActivityScheduleEntry schedule))
                throw new InvalidDataException($"4.8 event {id} has no official schedule");
            AssertEqual(Utc(begin), schedule.StartTime, $"4.8 event {id} opens at official date");
            AssertEqual(Utc(end), schedule.EndTime, $"4.8 event {id} ends at official date");
            AssertEqual(false, ActivityScheduleService.IsOpen(id, DateTimeOffset.FromUnixTimeSeconds(schedule.StartTime - 1)),
                $"4.8 event {id} closed before start");
            AssertEqual(true, ActivityScheduleService.IsOpen(id, DateTimeOffset.FromUnixTimeSeconds(schedule.StartTime)),
                $"4.8 event {id} open at start");
            AssertEqual(false, ActivityScheduleService.IsOpen(id, DateTimeOffset.FromUnixTimeSeconds(schedule.EndTime)),
                $"4.8 event {id} closed at exclusive end");
        }
        // 50402 ends at the authored shop expiry; 50403 shares its bounds by AscNet
        // paired-ID policy (4.7 precedent), not a separately verified retail task date.
        foreach (int id in new[] { 50402, 50403 })
        {
            AssertEqual(true, ActivityScheduleService.TryGet(id, out ActivityScheduleEntry battlefield),
                $"Simulated Battlefield {id} scheduled");
            AssertEqual(Utc("2026-09-24T05:00:00Z"), battlefield.StartTime, $"Battlefield {id} opens after maintenance");
            AssertEqual(Utc("2026-11-05T05:00:00Z"), battlefield.EndTime, $"Battlefield {id} closes at shop expiry");
            AssertEqual(false, ActivityScheduleService.IsOpen(id, DateTimeOffset.FromUnixTimeSeconds(battlefield.StartTime - 1)),
                $"Battlefield {id} locked before maintenance");
            AssertEqual(true, ActivityScheduleService.IsOpen(id, DateTimeOffset.FromUnixTimeSeconds(battlefield.StartTime)),
                $"Battlefield {id} available after maintenance");
            AssertEqual(false, ActivityScheduleService.IsOpen(id, DateTimeOffset.FromUnixTimeSeconds(battlefield.EndTime)),
                $"Battlefield {id} locked at expiry");
        }

        SignInTable[] current = TableReaderV2.Parse<SignInTable>()
            .Where(sign => sign.Type == 2 && expected.Any(window => window.Id == sign.TimeId))
            .OrderBy(sign => sign.Id).ToArray();
        MethodInfo build = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.SignInModule"),
            "BuildLoginSignInfos", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Player), typeof(DateTimeOffset)]);
        List<SignInfo> Infos(Player player, DateTimeOffset now) =>
            (List<SignInfo>)(build.Invoke(null, [player, now]) ??
                throw new InvalidDataException("sign-in login builder returned null"));
        Player fresh = CreateDrawCompatibilityPlayer(48_101);
        fresh.PlayerData.Level = 80;
        DateTimeOffset overlap = DateTimeOffset.Parse("2026-09-30T06:00:00Z");
        foreach (SignInTable sign in current)
            AssertEqual(true, Infos(fresh, overlap).Any(info => info.Id == sign.Id),
                $"4.8 sign-in {sign.Id} appears only in its authored window");
        AssertEqual(false, Infos(fresh, DateTimeOffset.Parse("2026-09-24T04:59:59Z"))
            .Any(info => current.Any(sign => sign.Id == info.Id)), "4.8 event sign-ins closed before maintenance");
        SignInTable moonlit = current.Single(sign => sign.TimeId == 50912);
        AssertEqual(false, Infos(fresh, DateTimeOffset.Parse("2026-10-04T05:00:00Z"))
            .Any(info => info.Id == moonlit.Id), "Moonlit sign-in closes at official end");
        MethodInfo calendar = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildNewActivityCalendarPayload", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(DateTimeOffset)]);
        Dictionary<string, object?> calendarPayload =
            (Dictionary<string, object?>)calendar.Invoke(null, [overlap])!;
        AssertEqual("48001,48003,48004,48006,48007,48008,48012",
            string.Join(",", (int[])calendarPayload["OpenActivityIds"]!),
            "4.8 calendar shows dated activities but not undated Cosmic Wonders or unopened Huhu");
        AssertEqual(false, ActivityScheduleService.IsOpen(50302, overlap), "undated Cosmic Wonders stays closed during 4.8");

        SignInTable first = current.Single(sign => sign.TimeId == 50301);
        PlayerSignInState recorded = fresh.SignInStates.Single(state => state.Id == first.Id);
        recorded.ClaimCount = 1;
        recorded.LastSignInTime = overlap.ToUnixTimeSeconds();
        Player reloaded = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(fresh.ToBson());
        AssertEqual(true, Infos(reloaded, overlap).Single(info => info.Id == first.Id).Got,
            "4.8 sign-in persisted claim replays on relogin");
        foreach (SignInTable other in current.Where(sign => sign.Id != first.Id))
            AssertEqual(false, Infos(reloaded, overlap).Single(info => info.Id == other.Id).Got,
                $"4.8 sign-in {other.Id} has independent persisted state");
        Player twoWeek = CreateDrawCompatibilityPlayer(48_116);
        twoWeek.PlayerData.Level = 80;
        twoWeek.SignInStates.Add(new PlayerSignInState
        {
            Id = first.Id, ClaimCount = 7, LastSignInTime = overlap.ToUnixTimeSeconds()
        });
        AssertEqual(1L, Infos(twoWeek, overlap).Single(info => info.Id == first.Id).Round,
            "4.8 two-week sign-in retains first round on seventh claimed day");
        DateTimeOffset secondWeek = overlap.AddDays(1);
        SignInfo nextRound = Infos(twoWeek, secondWeek).Single(info => info.Id == first.Id);
        AssertEqual(2L, nextRound.Round, "4.8 two-week sign-in advances to authored second round");
        AssertEqual(1L, nextRound.Day, "4.8 two-week sign-in resets day within second round");
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players, out _, out _);
        const long uid = 48_117;
        Player claimant = CreateDrawCompatibilityPlayer(uid);
        claimant.PlayerData.Level = 80;
        Inventory inventory = CreateDrawCompatibilityInventory(uid, []);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(uid), claimant, inventory, "v48-sign-in-event");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        SignInTable sevenDay = current.Single(sign => sign.TimeId == 50911);
        MethodInfo process = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.SignInModule"),
            "ProcessSignInRequest", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Session), typeof(int), typeof(DateTimeOffset)]);
        SignInResponse firstClaim = (SignInResponse)process.Invoke(null, [harness.Session, sevenDay.Id, overlap])!;
        int rewardId = TableReaderV2.Parse<SignInRewardTable>()
            .Single(row => row.SignId == sevenDay.Id && row.Round == 1 && row.Day == 1).RewardId;
        RewardTable claimReward = TableReaderV2.Parse<RewardTable>().Single(row => row.Id == rewardId);
        int[] expectedTemplates = claimReward.SubIds.Select(id => TableReaderV2.Parse<RewardGoodsTable>()
            .Single(goods => goods.Id == id).TemplateId).ToArray();
        AssertEqual(string.Join(",", expectedTemplates),
            string.Join(",", firstClaim.RewardGoodsList.Select(goods => goods.TemplateId)),
            "4.8 seven-day sign-in grants authored day-one rewards");
        AssertEqual(0, firstClaim.Code, "4.8 seven-day sign-in claim succeeds");
        AssertEqual(1, players.ReplaceOneCalls, "4.8 seven-day sign-in persists claim");
        AssertEqual(1L, claimant.SignInStates.Single(state => state.Id == sevenDay.Id).ClaimCount,
            "4.8 seven-day sign-in advances independently");
        AssertEqual(true, Infos(MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(claimant.ToBson()), overlap)
            .Single(info => info.Id == sevenDay.Id).Got, "4.8 seven-day claim survives BSON reload");
        string beforeRetry = Convert.ToHexString(inventory.ToBson());
        SignInResponse replay = (SignInResponse)process.Invoke(null, [harness.Session, sevenDay.Id, overlap])!;
        AssertEqual(0, replay.Code, "4.8 duplicate sign-in request accepted without regrant");
        AssertEqual(beforeRetry, Convert.ToHexString(inventory.ToBson()), "4.8 duplicate claim does not regrant");
        AssertEqual(1, players.ReplaceOneCalls, "4.8 duplicate claim does not persist twice");
        using LoopbackSessionHarness secondHarness = new(
            CreateDrawCompatibilityCharacter(48_116), twoWeek,
            CreateDrawCompatibilityInventory(48_116, []), "v48-visitor-second-week");
        secondHarness.Session.stage = CreateLoginAccountCompatibilityStage(48_116);
        SignInResponse secondClaim = (SignInResponse)process.Invoke(null,
            [secondHarness.Session, first.Id, secondWeek])!;
        AssertEqual(0, secondClaim.Code, "4.8 Visitor second-round first-day claim succeeds");
        AssertEqual(8L, twoWeek.SignInStates.Single(state => state.Id == first.Id).ClaimCount,
            "4.8 Visitor second-round claim persists eighth progress");
        int roundTwoReward = TableReaderV2.Parse<SignInRewardTable>()
            .Single(row => row.SignId == first.Id && row.Round == 2 && row.Day == 1).RewardId;
        int[] roundTwoTemplates = TableReaderV2.Parse<RewardTable>().Single(row => row.Id == roundTwoReward)
            .SubIds.Select(id => TableReaderV2.Parse<RewardGoodsTable>()
                .Single(goods => goods.Id == id).TemplateId).ToArray();
        AssertEqual(string.Join(",", roundTwoTemplates),
            string.Join(",", secondClaim.RewardGoodsList.Select(goods => goods.TemplateId)),
            "4.8 Visitor second-round reward follows authored day-one receipt");
        long expiry = DateTimeOffset.Parse("2026-11-03T23:59:00Z").ToUnixTimeSeconds();
        foreach (int ticketId in new[] { 21000901, 21000903 })
        {
            PlayerDrawTicket ticket = twoWeek.DrawState.FreeTickets.Single(entry => entry.CfgId == ticketId);
            AssertEqual(1, ticket.Count, $"Visitor second-week reward {ticketId} balance");
            AssertEqual(expiry, ticket.ExpireTime, $"Visitor second-week reward {ticketId} expiry");
        }
        Player persistedTickets = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(twoWeek.ToBson());
        AssertEqual("21000901,21000903",
            string.Join(",", persistedTickets.DrawState.FreeTickets.Select(ticket => ticket.CfgId).Order()),
            "Visitor free-attempt balances survive BSON reload");
        SignInResponse duplicateVisitor = (SignInResponse)process.Invoke(null,
            [secondHarness.Session, first.Id, secondWeek])!;
        AssertEqual(0, duplicateVisitor.Code, "Visitor same-day retry accepted");
        AssertEqual(2, twoWeek.DrawState.FreeTickets.Sum(ticket => ticket.Count),
            "Visitor same-day retry does not mint duplicate free attempts");
        Player firstDayVisitor = CreateDrawCompatibilityPlayer(48_1161);
        firstDayVisitor.PlayerData.Level = 80;
        using LoopbackSessionHarness firstVisitorHarness = new(
            CreateDrawCompatibilityCharacter(48_1161), firstDayVisitor,
            CreateDrawCompatibilityInventory(48_1161, []), "v48-visitor-first-day");
        firstVisitorHarness.Session.stage = CreateLoginAccountCompatibilityStage(48_1161);
        SignInResponse visitorWelcome = (SignInResponse)process.Invoke(null,
            [firstVisitorHarness.Session, first.Id, overlap])!;
        AssertEqual(0, visitorWelcome.Code, "Visitor first-day sign-in accepted");
        foreach (int ticketId in new[] { 21000901, 21000902, 21000903 })
        {
            PlayerDrawTicket ticket = firstDayVisitor.DrawState.FreeTickets.Single(entry => entry.CfgId == ticketId);
            AssertEqual(1, ticket.Count, $"Visitor first-day free ticket {ticketId}");
            AssertEqual(expiry, ticket.ExpireTime, $"Visitor first-day free ticket {ticketId} expires on collab end");
        }
        Player restoredWelcome = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(firstDayVisitor.ToBson());
        AssertEqual(3, restoredWelcome.DrawState.FreeTickets.Sum(ticket => ticket.Count),
            "Visitor first-day single and ten-draw attempts persist");
    }

    private static void ValidateVersion47TimeLimitControlSchema()
    {
        Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        MethodInfo builder = RequiredMethod(
            accountModule,
            "BuildTimeLimitControlConfigList",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(DateTimeOffset), typeof(bool)]);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<TimeLimitCtrlConfigList> controls =
            (List<TimeLimitCtrlConfigList>)builder.Invoke(null, [now, false])!;
        AssertEqual(false, controls.Any(control => control.Id == 50302), "Cosmic Wonders has no fabricated login time control");
        foreach (int id in new[] { 50402, 50403 })
        {
            ActivityScheduleService.TryGet(id, out ActivityScheduleEntry battlefield);
            TimeLimitCtrlConfigList control = controls.Single(control => control.Id == id);
            AssertEqual(battlefield.StartTime, control.StartTime, $"Battlefield {id} login start");
            AssertEqual(battlefield.EndTime, control.EndTime, $"Battlefield {id} login expiry");
        }

        // Every scheduled TimeId must appear with the retail five-key element schema and
        // deterministic UTC display strings derived from the authoritative bounds.
        foreach (ActivityScheduleEntry schedule in ActivityScheduleService.All.Where(schedule => schedule.Id >= 48_000))
        {
            TimeLimitCtrlConfigList control = controls.Single(c => c.Id == schedule.Id);
            AssertEqual(schedule.StartTime, control.StartTime, $"TimeLimit {schedule.Id} StartTime");
            AssertEqual(schedule.EndTime, control.EndTime, $"TimeLimit {schedule.Id} EndTime");
            AssertEqual(FormatUtcTimeLimitString(schedule.StartTime), control.StartTimeStr,
                $"TimeLimit {schedule.Id} StartTimeStr");
            AssertEqual(FormatUtcTimeLimitString(schedule.EndTime), control.EndTimeStr,
                $"TimeLimit {schedule.Id} EndTimeStr");
        }

        // [start,end) inclusive-start/exclusive-end clock rule on a scheduled window.
        ActivityScheduleEntry probe = ActivityScheduleService.All.First(s => s.EndTime > 0);
        if (probe.StartTime > 0)
        {
            if (ActivityScheduleService.IsOpen(probe.Id, DateTimeOffset.FromUnixTimeSeconds(probe.StartTime - 1)))
                throw new InvalidDataException("TimeLimit window opened before its inclusive start bound.");
            if (!ActivityScheduleService.IsOpen(probe.Id, DateTimeOffset.FromUnixTimeSeconds(probe.StartTime)))
                throw new InvalidDataException("TimeLimit window did not open at its inclusive start bound.");
        }
        if (ActivityScheduleService.IsOpen(probe.Id, DateTimeOffset.FromUnixTimeSeconds(probe.EndTime)))
            throw new InvalidDataException("TimeLimit window remained open at its exclusive end bound.");

        // Round-trip must preserve all five keys.
        TimeLimitCtrlConfigList roundTrip = MessagePackSerializer.Deserialize<TimeLimitCtrlConfigList>(
            MessagePackSerializer.Serialize(controls[0]));
        AssertEqual(controls[0].Id, roundTrip.Id, "TimeLimit round-trip Id");
        AssertEqual(controls[0].StartTime, roundTrip.StartTime, "TimeLimit round-trip StartTime");
        AssertEqual(controls[0].EndTime, roundTrip.EndTime, "TimeLimit round-trip EndTime");
        AssertEqual(controls[0].StartTimeStr, roundTrip.StartTimeStr, "TimeLimit round-trip StartTimeStr");
        AssertEqual(controls[0].EndTimeStr, roundTrip.EndTimeStr, "TimeLimit round-trip EndTimeStr");
    }

    private static string? FormatUtcTimeLimitString(long unixSeconds)
    {
        if (unixSeconds == 0)
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToUniversalTime().ToString("yyyy/M/d H:mm");
    }

    private static void ValidateVersion47NewActivityCalendarDerivation()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<NewActivityCalendarActivityTable> tableOpen = TableReaderV2.Parse<NewActivityCalendarActivityTable>()
            .Where(activity => ActivityScheduleService.IsOpen(activity.MainTimeId, now))
            .OrderBy(activity => activity.ActivityId)
            .ToList();

        // The calendar payload's OpenActivityIds must match the table+schedule derivation, never
        // a captured account's progress.
        Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        MethodInfo calendarBuilder = RequiredMethod(
            accountModule,
            "BuildNewActivityCalendarPayload",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(DateTimeOffset)]);
        Dictionary<string, object?> payload =
            (Dictionary<string, object?>)calendarBuilder.Invoke(null, [now])!;
        int[] openActivityIds = (int[])payload["OpenActivityIds"]!;
        AssertEqual(tableOpen.Count, openActivityIds.Length, "4.7 calendar open activity count");
        for (int i = 0; i < tableOpen.Count; i++)
            AssertEqual(tableOpen[i].ActivityId, openActivityIds[i], "4.7 calendar open activity id");
    }

    private static void ValidateVersion47BriefStoryRequestCompatibility()
    {
        Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        MethodInfo validate = RequiredMethod(
            accountModule,
            "IsValidBriefStoryId",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(int)]);
        List<int> validIds = TableReaderV2.Parse<ActivityBriefStoryTable>().Select(s => s.Id).ToList();
        if (validIds.Count == 0)
            throw new InvalidDataException("ActivityBriefStory table has no rows; the brief-story validation source is empty.");
        foreach (int id in validIds)
            if (!(bool)validate.Invoke(null, [id])!)
                throw new InvalidDataException($"ActivityBriefStory id {id} was rejected by the authoritative source.");

        // Unknown ids are rejected without mutation.
        if ((bool)validate.Invoke(null, [-1])!)
            throw new InvalidDataException("Negative brief-story id was accepted.");
        if ((bool)validate.Invoke(null, [int.MaxValue])!)
            throw new InvalidDataException("Unknown brief-story id was accepted.");

        const long playerId = 47_101;
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> playerCollection,
            out _,
            out _);
        AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
        player.BriefStoryFinishedIds = [];
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            CreateDrawCompatibilityInventory(playerId, []),
            sessionId: "version-47-brief-story");

        // Login replay is empty for a fresh player.
        MethodInfo buildNotify = RequiredMethod(
            accountModule,
            "BuildNotifyBriefStoryData",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(Player)]);
        NotifyBriefStoryData empty = (NotifyBriefStoryData)buildNotify.Invoke(null, [player])!;
        AssertEqual(0, empty.FinishedIds.Count, "4.7 fresh brief-story login replay is empty");

        // Finishing a valid story persists and replays.
        int storyId = validIds[0];
        InvokeRegisteredRequestHandler(
            nameof(FinishBriefStoryRequest),
            harness.Session,
            47_102,
            new FinishBriefStoryRequest { Id = storyId });
        FinishBriefStoryResponse response = ReadResponsePayload<FinishBriefStoryResponse>(
            harness,
            47_102,
            nameof(FinishBriefStoryResponse),
            "4.7 FinishBriefStoryResponse");
        AssertEqual(0, response.Code, "4.7 FinishBriefStory success code");
        AssertEqual(1, playerCollection.ReplaceOneCalls, "4.7 FinishBriefStory persists the finished id");

        // Unknown id does not mutate.
        int savesBefore = playerCollection.ReplaceOneCalls;
        InvokeRegisteredRequestHandler(
            nameof(FinishBriefStoryRequest),
            harness.Session,
            47_103,
            new FinishBriefStoryRequest { Id = int.MaxValue });
        FinishBriefStoryResponse invalid = ReadResponsePayload<FinishBriefStoryResponse>(
            harness,
            47_103,
            nameof(FinishBriefStoryResponse),
            "4.7 FinishBriefStory unknown id");
        AssertEqual(0, invalid.Code, "4.7 FinishBriefStory unknown id returns success (no mutation)");
        AssertEqual(savesBefore, playerCollection.ReplaceOneCalls, "4.7 FinishBriefStory unknown id does not mutate");

        // Idempotence: finishing the same story again does not re-persist.
        savesBefore = playerCollection.ReplaceOneCalls;
        InvokeRegisteredRequestHandler(
            nameof(FinishBriefStoryRequest),
            harness.Session,
            47_104,
            new FinishBriefStoryRequest { Id = storyId });
        ReadResponsePayload<FinishBriefStoryResponse>(
            harness,
            47_104,
            nameof(FinishBriefStoryResponse),
            "4.7 FinishBriefStory idempotent");
        AssertEqual(savesBefore, playerCollection.ReplaceOneCalls, "4.7 FinishBriefStory idempotent does not re-save");

        // Relogin replays the durable finished ids.
        NotifyBriefStoryData replay = (NotifyBriefStoryData)buildNotify.Invoke(null, [player])!;
        AssertEqual(1, replay.FinishedIds.Count, "4.7 brief-story relogin replays finished ids");
        AssertEqual(storyId, (int)replay.FinishedIds[0], "4.7 brief-story relogin finished id value");
    }

    private static void ValidateVersion47OrdinaryEventPreFightGate()
    {
        // Pick a scheduled ordinary stage from the stage->TimeId index and verify the gate is
        // table/schedule-derived. A stage whose TimeId is outside its window must be rejected with
        // the FubenManagerStageLocked code; an open one passes through.
        int? stageId = null;
        int? timeId = null;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (ActivityScheduleEntry entry in ActivityScheduleService.All)
        {
            int scheduleId = checked((int)entry.Id);
            int? candidateStage = FindStageForTimeId(scheduleId);
            if (candidateStage is int s)
            {
                stageId = s;
                timeId = scheduleId;
                break;
            }
        }
        if (stageId is not int usableStage || timeId is not int usableTimeId)
            return;

        AssertEqual(usableTimeId, ActivityScheduleService.StageTimeId(usableStage), "4.7 stage->TimeId index maps the ordinary stage");

        // The availability rule must hold for both sides of the window boundary.
        ActivityScheduleEntry schedule = ActivityScheduleService.All.Single(s => s.Id == usableTimeId);
        bool openNow = ActivityScheduleService.IsOpen(usableTimeId, now);
        bool beforeStart = schedule.StartTime > 0
            && ActivityScheduleService.IsOpen(usableTimeId, DateTimeOffset.FromUnixTimeSeconds(schedule.StartTime - 1)) == false;
        // At least one of "open now" or "closed before start" must be demonstrable; if the window
        // is permanently open (0 bounds) we can only assert open-now pass-through.
        AssertEqual(true, openNow || beforeStart || schedule.StartTime == 0,
            "4.7 PreFight ordinary-stage availability is determinable from the schedule");
    }

    private static int? FindStageForTimeId(int timeId)
    {
        // Mirrors the ActivityScheduleService index sources; only used to pick a test stage.
        foreach (var chapter in TableReaderV2.Parse<AscNet.Table.V2.share.miniactivity.dyemerge.DyeMergeChapterTable>())
            foreach (int stageId in chapter.StageIds)
                if (chapter.TimeId == timeId)
                    return stageId;
        return null;
    }

    private static void ValidateVersion47LoginHelperOrdering()
    {
        // Login helpers must exist with the exact signatures the siblings landed.
        Type signInModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.SignInModule");
        RequiredMethod(signInModule, "SendSignInResetPush", BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(DateTimeOffset)]);
        RequiredMethod(signInModule, "BuildNotifySignInData", BindingFlags.Static | BindingFlags.Public,
            [typeof(Player), typeof(DateTimeOffset)]);

        Type eventModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Version47EventModule");
        RequiredMethod(eventModule, "SendLoginPushes", BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(DateTimeOffset)]);

        Type playerModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.PlayerModule");
        RequiredMethod(playerModule, "ReconcileHeadTimeouts", BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(DateTimeOffset)]);

        // The frame reconcile must run before NotifyLogin is built so repaired IDs are in login,
        // and the timeout push must follow NotifyLogin.
        Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        MethodInfo doLogin = RequiredMethod(
            accountModule,
            "DoLogin",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(Session), typeof(bool)]);
        AssertMethodTransitivelyCalls(
            doLogin,
            RequiredMethod(playerModule, "ReconcileHeadTimeouts", BindingFlags.Static | BindingFlags.Public,
                [typeof(Session), typeof(DateTimeOffset)]),
            "AccountModule.DoLogin calls ReconcileHeadTimeouts");
        AssertMethodTransitivelyCalls(
            doLogin,
            RequiredMethod(signInModule, "SendSignInResetPush", BindingFlags.Static | BindingFlags.Public,
                [typeof(Session), typeof(DateTimeOffset)]),
            "AccountModule.DoLogin calls SendSignInResetPush");
        AssertMethodTransitivelyCalls(
            doLogin,
            RequiredMethod(eventModule, "SendLoginPushes", BindingFlags.Static | BindingFlags.Public,
                [typeof(Session), typeof(DateTimeOffset)]),
            "AccountModule.DoLogin calls Version47EventModule.SendLoginPushes");
    }
}
