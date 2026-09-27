using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.miniactivity.envelope;
using AscNet.Table.V2.share.miniactivity.musicgame.concertpreheating;
using AscNet.Table.V2.share.pbr;
using AscNet.Table.V2.share.task;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateVersion47EventCompatibility()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();

        static Type ModuleType() => RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Version47EventModule");
        static MethodInfo ModuleMethod(string name, params Type[] signature) =>
            RequiredMethod(ModuleType(), name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, signature);
        static object? InvokeMaybe(string method, Type[] signature, params object?[] arguments) =>
            ModuleMethod(method, signature).Invoke(null, arguments);
        static object Invoke(string method, Type[] signature, params object?[] arguments) =>
            InvokeMaybe(method, signature, arguments)
            ?? throw new InvalidDataException($"Version47EventModule.{method} returned null.");

        static ActivityScheduleEntry RequireSchedule(long timeId, string family)
        {
            if (!ActivityScheduleService.TryGet(timeId, out ActivityScheduleEntry entry))
                throw new InvalidDataException($"4.7 {family} TimeId {timeId} is not staged in ActivitySchedule.tsv.");
            if (entry.StartTime <= 0)
                throw new InvalidDataException($"4.7 {family} TimeId {timeId} has no concrete StartTime.");
            return entry;
        }

        ValidateVersion47EventWireShape();

        // ---- authoritative schedule anchors + deterministic open/closed clocks ----
        EnvelopeActivityTable envelope = TableReaderV2.Parse<EnvelopeActivityTable>().Single();
        PBRActivityTable pbr = TableReaderV2.Parse<PBRActivityTable>().Single(row => row.TimeId is int t && t > 0);
        ConcertPreHeatingActivityTable concert = TableReaderV2.Parse<ConcertPreHeatingActivityTable>().Single(row => row.TimeId > 0);
        ActivityScheduleEntry envelopeSchedule = RequireSchedule(envelope.TimeId, "Envelope");
        ActivityScheduleEntry pbrSchedule = RequireSchedule(pbr.TimeId!.Value, "PBR");
        ActivityScheduleEntry concertSchedule = RequireSchedule(concert.TimeId, "Concert");
        DateTimeOffset envelopeOpen = DateTimeOffset.FromUnixTimeSeconds(envelopeSchedule.StartTime);
        DateTimeOffset pbrOpen = DateTimeOffset.FromUnixTimeSeconds(pbrSchedule.StartTime);
        DateTimeOffset concertOpen = DateTimeOffset.FromUnixTimeSeconds(concertSchedule.StartTime);
        DateTimeOffset[] allOpen = { envelopeOpen, pbrOpen, concertOpen };
        DateTimeOffset commonOpen = allOpen.Max();
        DateTimeOffset commonClosed = allOpen.Min().AddSeconds(-1);
        foreach ((ActivityScheduleEntry schedule, long timeId, string family) in
            new[] { (envelopeSchedule, (long)envelope.TimeId, "Envelope"), (pbrSchedule, pbr.TimeId!.Value, "PBR"), (concertSchedule, (long)concert.TimeId, "Concert") })
        {
            if (schedule.EndTime > 0 && schedule.EndTime <= schedule.StartTime + 86400)
                throw new InvalidDataException($"4.7 {family} schedule window is shorter than one day; cannot test business-day rollover.");
        }

        ValidateVersion47EnvelopeCompatibility(envelope, envelopeOpen, InvokeMaybe, Invoke);
        ValidateVersion47PbrCompatibility(pbr, pbrOpen, InvokeMaybe, Invoke);
        ValidateVersion47ConcertCompatibility(concert, concertOpen, InvokeMaybe);
        ValidateVersion47SendLoginPushesOrder(commonOpen, commonClosed, InvokeMaybe);
    }

    private static void ValidateVersion47EventWireShape()
    {
        // Envelope
        AssertMailNamedMapKeys(new NotifyEnvelope { ActivityId = 1, HasReward = true }, ["ActivityId", "HasReward"], "NotifyEnvelope");
        AssertMailNamedMapKeys(new EnvelopeEnterRequest(), [], "EnvelopeEnterRequest");
        AssertMailNamedMapKeys(new EnvelopeEnterResponse(), ["Code", "RewardGoodsList", "TaskRewardGoodsList", "OpenedCharacterIds", "InstrumentBindings", "AvgWatchedCharacterIds"], "EnvelopeEnterResponse");
        AssertMailNamedMapKeys(new EnvelopeOpenRequest(), ["Id"], "EnvelopeOpenRequest");
        AssertMailNamedMapKeys(new EnvelopeOpenResponse(), ["Code"], "EnvelopeOpenResponse");
        AssertMailNamedMapKeys(new EnvelopeSelectOpenRequest(), ["CharacterId"], "EnvelopeSelectOpenRequest");
        AssertMailNamedMapKeys(new EnvelopeSelectOpenResponse(), ["Code"], "EnvelopeSelectOpenResponse");
        AssertMailNamedMapKeys(new EnvelopeBindRequest(), ["Bindings"], "EnvelopeBindRequest");
        AssertMailNamedMapKeys(new EnvelopeBindResponse(), ["Code"], "EnvelopeBindResponse");
        AssertMailNamedMapKeys(new EnvelopeRecordAvgRequest(), ["CharacterId"], "EnvelopeRecordAvgRequest");
        AssertMailNamedMapKeys(new EnvelopeRecordAvgResponse(), ["Code"], "EnvelopeRecordAvgResponse");

        // PBR root
        AssertMailNamedMapKeys(new PbrActivityDataNotify(), ["PbrDataDb"], "PbrActivityDataNotify");
        AssertMailNamedMapKeys(new PbrDataDb(), ["ActivityId", "SegmentSettleData", "MetaProgression", "StageRecords", "Compendiums"], "PbrDataDb");
        AssertMailNamedMapKeys(new PbrMetaProgression(), ["UnlockNodes"], "PbrMetaProgression");
        AssertMailNamedMapKeys(new PbrCompendiums(), ["CompendiumItems", "CompendiumMonsters"], "PbrCompendiums");
        AssertMailNamedMapKeys(new PbrCompendiumPush(), ["AddCompendiumItems", "UpdateCompendiumItems", "AddCompendiumMonsters", "UpdateCompendiumMonsters"], "PbrCompendiumPush");

        // Concert
        AssertMailNamedMapKeys(new NotifyConcertPreHeating(), ["ConcertPreHeatingDataDb"], "NotifyConcertPreHeating");
        AssertMailNamedMapKeys(new ConcertPreHeatingDataDb(), ["ActivityId", "StageFinish"], "ConcertPreHeatingDataDb");
        AssertMailNamedMapKeys(new NotifyConcertVideoConfig(), ["ConcertVideoConfigs"], "NotifyConcertVideoConfig");
    }

    private static void ValidateVersion47EnvelopeCompatibility(
        EnvelopeActivityTable envelope,
        DateTimeOffset envelopeOpen,
        Func<string, Type[], object?[], object?> invokeMaybe,
        Func<string, Type[], object?[], object> invoke)
    {
        // NotifyEnvelope reflects daily-grant availability (fresh => HasReward true).
        Player envelopeFresh = CreateDrawCompatibilityPlayer(47_101);
        NotifyEnvelope? freshNotify = (NotifyEnvelope?)invokeMaybe("BuildEnvelopeNotify", [typeof(Player), typeof(DateTimeOffset)], [envelopeFresh, envelopeOpen]);
        AssertEqual(envelope.Id, freshNotify!.ActivityId, "Envelope login push activity id");
        AssertEqual(true, freshNotify.HasReward, "Envelope fresh login has reward");

        // Claimed today => no reward; next business day => reward again; closed => no push.
        envelopeFresh.Envelope.ActivityId = envelope.Id;
        envelopeFresh.Envelope.LastDailyGrantBusinessDay =
            (int)invoke("BusinessDay", [typeof(DateTimeOffset)], [envelopeOpen]);
        NotifyEnvelope? claimedNotify = (NotifyEnvelope?)invokeMaybe("BuildEnvelopeNotify", [typeof(Player), typeof(DateTimeOffset)], [envelopeFresh, envelopeOpen]);
        AssertEqual(false, claimedNotify!.HasReward, "Envelope claimed today has no reward");
        NotifyEnvelope? nextDayNotify = (NotifyEnvelope?)invokeMaybe("BuildEnvelopeNotify", [typeof(Player), typeof(DateTimeOffset)], [envelopeFresh, envelopeOpen.AddDays(1)]);
        AssertEqual(true, nextDayNotify!.HasReward, "Envelope next business day has reward");
        AssertEqual(null, (NotifyEnvelope?)invokeMaybe("BuildEnvelopeNotify", [typeof(Player), typeof(DateTimeOffset)], [envelopeFresh, envelopeOpen.AddSeconds(-1)]),
            "Envelope closed clock yields no push");

        // First enter grants the daily ticket, pushes the item before the exact response, and
        // echoes persisted open/bind/AVG fields; replay on the same business day is non-duplicating.
        long envelopeUid = 47_102;
        using (LoopbackSessionHarness envelopeHarness = new(
            CreateDrawCompatibilityCharacter(envelopeUid),
            CreateDrawCompatibilityPlayer(envelopeUid),
            CreateDrawCompatibilityInventory(envelopeUid, []),
            "version47-envelope-enter-test"))
        {
            invokeMaybe("HandleEnvelopeEnter", [typeof(Session), typeof(int), typeof(DateTimeOffset)], [envelopeHarness.Session, 1001, envelopeOpen]);
            NotifyItemDataList itemPush = ReadPushPayload<NotifyItemDataList>(envelopeHarness, nameof(NotifyItemDataList), "Envelope first-enter item push");
            ReadPushPayload<NotifyTask>(envelopeHarness, nameof(NotifyTask), "Envelope first-enter task snapshot");
            EnvelopeEnterResponse first = ReadResponsePayload<EnvelopeEnterResponse>(
                envelopeHarness, 1001, nameof(EnvelopeEnterResponse), "Envelope first-enter response");
            AssertEqual(0, first.Code, "Envelope first-enter code");
            if (first.RewardGoodsList.Count == 0)
                throw new InvalidDataException("Envelope first-enter granted no daily ticket reward.");
            if (itemPush.ItemDataList.Count == 0)
                throw new InvalidDataException("Envelope first-enter emitted no item push.");
            // Reward ordering: the item push precedes the response, and the response lists the
            // granted daily ticket (from the Envelope + Reward tables) first.
            AssertEqual(envelope.TicketItemId, first.RewardGoodsList[0].TemplateId,
                "Envelope response reward is the daily ticket");
            AssertEqual(1, first.RewardGoodsList[0].RewardType, "Envelope reward type is Item");
            AssertEqual(0, first.TaskRewardGoodsList.Count, "Envelope first-enter task rewards empty");
            AssertEqual(0, first.OpenedCharacterIds.Count, "Envelope first-enter opened characters empty");
            AssertEqual(0, first.InstrumentBindings.Count, "Envelope first-enter instrument bindings empty");
            AssertEqual(0, first.AvgWatchedCharacterIds.Count, "Envelope first-enter avg characters empty");

            long grantedCount = envelopeHarness.Session.inventory.Items
                .Single(item => item.Id == envelope.TicketItemId).Count;
            envelopeHarness.Session.player.Envelope.LastDailyGrantBusinessDay = 0;
            invokeMaybe("HandleEnvelopeEnter", [typeof(Session), typeof(int), typeof(DateTimeOffset)],
                [envelopeHarness.Session, 1004, envelopeOpen]);
            ReadPushPayload<NotifyItemDataList>(envelopeHarness, nameof(NotifyItemDataList), "Envelope receipt replay item mirror");
            ReadPushPayload<NotifyTask>(envelopeHarness, nameof(NotifyTask), "Envelope receipt replay task snapshot");
            EnvelopeEnterResponse recovered = ReadResponsePayload<EnvelopeEnterResponse>(
                envelopeHarness, 1004, nameof(EnvelopeEnterResponse), "Envelope recovered receipt response");
            AssertEqual(0, recovered.Code, "Envelope recovered entry succeeds");
            AssertEqual(0, recovered.RewardGoodsList.Count, "receipt retry never advertises duplicate rewards");
            AssertEqual(grantedCount, envelopeHarness.Session.inventory.Items.Single(item => item.Id == envelope.TicketItemId).Count,
                "receipt retry does not regrant a ticket");

            // Same business day replay: no item push, no re-grant, still succeeds.
            invokeMaybe("HandleEnvelopeEnter", [typeof(Session), typeof(int), typeof(DateTimeOffset)], [envelopeHarness.Session, 1002, envelopeOpen]);
            ReadPushPayload<NotifyTask>(envelopeHarness, nameof(NotifyTask), "Envelope repeated entry task snapshot");
            EnvelopeEnterResponse replay = ReadResponsePayload<EnvelopeEnterResponse>(
                envelopeHarness, 1002, nameof(EnvelopeEnterResponse), "Envelope same-day replay response");
            AssertEqual(0, replay.Code, "Envelope replay code");
            AssertEqual(0, replay.RewardGoodsList.Count, "Envelope replay does not re-grant");

            // Next business day: a fresh grant is issued again (item push + response).
            invokeMaybe("HandleEnvelopeEnter", [typeof(Session), typeof(int), typeof(DateTimeOffset)], [envelopeHarness.Session, 1003, envelopeOpen.AddDays(1)]);
            ReadPushPayload<NotifyItemDataList>(envelopeHarness, nameof(NotifyItemDataList), "Envelope next-day item push");
            ReadPushPayload<NotifyTask>(envelopeHarness, nameof(NotifyTask), "Envelope next-day task snapshot");
            EnvelopeEnterResponse secondDay = ReadResponsePayload<EnvelopeEnterResponse>(
                envelopeHarness, 1003, nameof(EnvelopeEnterResponse), "Envelope next-day response");
            AssertEqual(0, secondDay.Code, "Envelope next-day code");
            if (secondDay.RewardGoodsList.Count == 0)
                throw new InvalidDataException("Envelope next-day enter granted no daily ticket reward.");
        }

        // BSON round-trip preserves the durable daily-grant day and echo fields.
        envelopeFresh.Envelope.OpenedCharacterIds = [3, 1, 1];
        envelopeFresh.Envelope.InstrumentBindings = new Dictionary<int, int> { [4] = 2 };
        envelopeFresh.Envelope.AvgWatchedCharacterIds = [7];
        EnvelopeState reloadedEnvelope = BsonSerializer.Deserialize<EnvelopeState>(envelopeFresh.Envelope.ToBson());
        AssertEqual(envelopeFresh.Envelope.LastDailyGrantBusinessDay, reloadedEnvelope.LastDailyGrantBusinessDay,
            "Envelope BSON reload preserves daily-grant business day");
        AssertEqual("1,3", string.Join(",", reloadedEnvelope.OpenedCharacterIds.Distinct().Order()), "Envelope BSON reload opened characters");
        AssertEqual(2, reloadedEnvelope.InstrumentBindings[4], "Envelope BSON reload instrument binding");
        AssertEqual("7", string.Join(",", reloadedEnvelope.AvgWatchedCharacterIds), "Envelope BSON reload avg characters");
        ValidateEnvelopeInvitations(envelope, envelopeOpen, invokeMaybe);
    }

    /// <summary>AscNet-policy Envelope Enter accrual: first entry, missed days, already-claimed days,
    /// earned-but-unclaimed daily task reissue, save-failure retry, BSON relog, expired rejection.</summary>
    private static void ValidateEnvelopeCatchUpAndReissue()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _,
            out RecordingMongoCollectionProxy<Inventory> inventorySaves);
        EnvelopeActivityTable activity = TableReaderV2.Parse<EnvelopeActivityTable>().Single();
        ActivityScheduleService.TryGet(activity.TimeId, out ActivityScheduleEntry window);
        DateTimeOffset open = DateTimeOffset.FromUnixTimeSeconds(window.StartTime);
        TaskTable loginTask = TableReaderV2.Parse<TaskTable>().Single(row => row.GroupId == activity.TaskDailyGroup
            && TableReaderV2.Parse<ConditionTable>().Single(condition => condition.Id == row.Condition).Type == 10202);
        MethodInfo handle = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Version47EventModule"),
            "HandleEnvelopeEnter", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session), typeof(int), typeof(DateTimeOffset)]);
        long Count(LoopbackSessionHarness harness, int itemId) =>
            harness.Session.inventory.Items.FirstOrDefault(item => item.Id == itemId)?.Count ?? 0;
        EnvelopeEnterResponse Enter(LoopbackSessionHarness harness, int id, DateTimeOffset clock, string label)
        {
            handle.Invoke(null, [harness.Session, id, clock]);
            return (EnvelopeEnterResponse)ReadResponsePayload(harness, id, nameof(EnvelopeEnterResponse), label,
                typeof(EnvelopeEnterResponse), maxPacketsToRead: 64);
        }
        LoopbackSessionHarness Harness(long uid, Player player) => new(CreateDrawCompatibilityCharacter(uid), player,
            CreateDrawCompatibilityInventory(uid, []), $"envelope-catchup-{uid}");

        // First entry on event day 4: authored FirstDayReward today + DailyTicketReward for 3 missed days.
        using (LoopbackSessionHarness late = Harness(47_120, CreateDrawCompatibilityPlayer(47_120)))
        {
            DateTimeOffset day4 = open.AddDays(3);
            EnvelopeEnterResponse first = Enter(late, 4801, day4, "Envelope late first entry");
            AssertEqual(0, first.Code, "late first entry succeeds");
            AssertEqual(5L + 3 * 3, Count(late, activity.TicketItemId), "first-day 5 tickets + 3 missed days x 3");
            AssertEqual(3L, Count(late, activity.SelectChoiceItemId), "first-day select-choice grant once");
            AssertEqual(true, first.RewardGoodsList.Count > 0, "late first entry advertises granted goods");
            AssertEqual(0, first.TaskRewardGoodsList.Count, "no reissue without prior earned tasks");
            EnvelopeEnterResponse replay = Enter(late, 4802, day4, "Envelope late reconnect");
            AssertEqual(0, replay.RewardGoodsList.Count, "reconnect advertises nothing");
            AssertEqual(14L, Count(late, activity.TicketItemId), "reconnect grants nothing");
            Player relog = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson!);
            AssertEqual(activity.Id, relog.Envelope.ActivityId, "relog keeps activity");
            late.Session.player = relog;
            EnvelopeEnterResponse relogged = Enter(late, 4803, day4.AddHours(1), "Envelope relog same day");
            AssertEqual(0, relogged.RewardGoodsList.Count, "relog same day grants nothing");
            AssertEqual(14L, Count(late, activity.TicketItemId), "relog inventory unchanged");
        }

        // Already-claimed day 1, return on day 3: only days 2-3 accrue, no second first-day reward.
        using (LoopbackSessionHarness back = Harness(47_121, CreateDrawCompatibilityPlayer(47_121)))
        {
            Enter(back, 4811, open, "Envelope day-1 entry");
            AssertEqual(5L, Count(back, activity.TicketItemId), "day-1 first entry");
            playerSaves.ThrowOnReplaceOne = true;
            bool failed = false;
            try { handle.Invoke(null, [back.Session, 4812, open.AddDays(2)]); }
            catch (TargetInvocationException) { failed = true; }
            finally { playerSaves.ThrowOnReplaceOne = false; }
            AssertEqual(true, failed, "player save failure surfaces");
            AssertEqual(11L, Count(back, activity.TicketItemId), "receipts committed days 2-3 before the failed marker save");
            while (back.TryReadAvailablePacket("drain failed entry", out _)) { }
            EnvelopeEnterResponse retry = Enter(back, 4813, open.AddDays(2), "Envelope retry after save failure");
            AssertEqual(0, retry.Code, "retry converges");
            AssertEqual(11L, Count(back, activity.TicketItemId), "retry never multiplies days 2-3");
            AssertEqual(3L, Count(back, activity.SelectChoiceItemId), "no second first-day reward");
            AssertEqual(TaskBusinessDay(open.AddDays(2)), back.Session.player.Envelope.LastDailyGrantBusinessDay, "retry persists watermark");
        }

        // Earned-but-unclaimed daily login task at rollover is reissued once; a claimed one is not.
        long loginTaskGoods = RewardHandlerGoods(loginTask.RewardId ?? 0)
            .Where(good => good.TemplateId == activity.TicketItemId).Sum(good => (long)good.Count);
        Player ReissuePlayer(long uid)
        {
            Player owed = CreateDrawCompatibilityPlayer(uid);
            owed.PlayerData.NewPlayerTaskActiveDay = 1;
            owed.MissionProgress.DailyResetDay = window.StartTime / 86_400 + 1;
            return owed;
        }
        foreach (bool claimed in new[] { false, true })
        {
            long uid = claimed ? 47_123 : 47_122;
            Player player = ReissuePlayer(uid);
            if (claimed) player.MissionProgress.ClaimedTaskIds.Add(loginTask.Id);
            using LoopbackSessionHarness harness = Harness(uid, player);
            EnvelopeEnterResponse entered = Enter(harness, 4821, open.AddDays(2), "Envelope reissue entry");
            AssertEqual(claimed ? 0 : 1, entered.TaskRewardGoodsList.Count, $"reissue claimed={claimed} task rewards listed");
            AssertEqual(5L + 2 * 3 + (claimed ? 0 : loginTaskGoods), Count(harness, activity.TicketItemId), $"reissue claimed={claimed} inventory delta");
            AssertEqual(0, harness.Session.player.Envelope.PendingTaskReissues.Count, "pending reissue cleared durably");
            EnvelopeEnterResponse again = Enter(harness, 4822, open.AddDays(2), "Envelope reissue replay");
            AssertEqual(0, again.TaskRewardGoodsList.Count, "reissue happens exactly once");
            AssertEqual(0, BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson!).Envelope.PendingTaskReissues.Count,
                "BSON reload has no pending reissue");
        }

        // A reissue whose payout is durable while its marker clear is not re-lists it from the receipt, never twice.
        using (LoopbackSessionHarness owed = Harness(47_125, ReissuePlayer(47_125)))
        {
            bool armed = true;
            playerSaves.BeforeReplaceOne = row =>
            {
                if (armed && row.Envelope.PendingTaskReissues.Count == 0
                    && row.Envelope.LastDailyGrantBusinessDay == TaskBusinessDay(open.AddDays(2)))
                {
                    armed = false;
                    throw new MongoDB.Driver.MongoException("Injected Envelope reissue marker save failure.");
                }
            };
            bool failed = false;
            try { handle.Invoke(null, [owed.Session, 4826, open.AddDays(2)]); }
            catch (TargetInvocationException) { failed = true; }
            finally { playerSaves.BeforeReplaceOne = null; }
            AssertEqual(true, failed, "reissue marker save failure surfaces");
            long paid = Count(owed, activity.TicketItemId);
            AssertEqual(5L + 2 * 3 + loginTaskGoods, paid, "dailies and reissue paid once before the failed marker save");
            while (owed.TryReadAvailablePacket("drain failed reissue entry", out _)) { }
            EnvelopeEnterResponse replay = Enter(owed, 4827, open.AddDays(2), "Envelope reissue retry");
            AssertEqual(0, replay.Code, "reissue retry converges");
            AssertEqual(1, replay.TaskRewardGoodsList.Count, "retry re-lists the recovered reissue");
            AssertEqual(paid, Count(owed, activity.TicketItemId), "retry never pays the reissue twice");
            AssertEqual(0, owed.Session.player.Envelope.PendingTaskReissues.Count, "retry clears the pending reissue");
        }

        // A lost marker write after the first payout must not re-plan the first-day business day as a plain daily:
        // the retry rebuilds the same first-day grant (97091 x5 + 97092 x3) and re-pushes both inventory rows.
        using (LoopbackSessionHarness first = Harness(47_126, CreateDrawCompatibilityPlayer(47_126)))
        {
            bool armed = true;
            playerSaves.BeforeReplaceOne = row =>
            {
                if (armed && row.Envelope.LastDailyGrantBusinessDay == TaskBusinessDay(open.AddDays(2)))
                {
                    armed = false;
                    throw new MongoDB.Driver.MongoException("Injected Envelope first-day marker save failure.");
                }
            };
            bool failed = false;
            try { handle.Invoke(null, [first.Session, 4828, open.AddDays(2)]); }
            catch (TargetInvocationException) { failed = true; }
            finally { playerSaves.BeforeReplaceOne = null; }
            AssertEqual(true, failed, "first-entry marker save failure surfaces");
            AssertEqual(5L + 2 * 3, Count(first, activity.TicketItemId), "first-entry ticket payout is durable");
            AssertEqual(3L, Count(first, activity.SelectChoiceItemId), "first-entry select-choice payout is durable");
            while (first.TryReadAvailablePacket("drain failed first entry", out _)) { }
            handle.Invoke(null, [first.Session, 4829, open.AddDays(2)]);
            NotifyItemDataList pushed = ReadPushPayload<NotifyItemDataList>(first, nameof(NotifyItemDataList),
                "first-entry retry inventory");
            AssertEqual(11L, pushed.ItemDataList.Last(item => item.Id == activity.TicketItemId).Count,
                "first-entry retry restores the client's ticket stack");
            AssertEqual(3L, pushed.ItemDataList.Last(item => item.Id == activity.SelectChoiceItemId).Count,
                "first-entry retry restores the client's select-choice stack");
            EnvelopeEnterResponse retried = (EnvelopeEnterResponse)ReadResponsePayload(first, 4829,
                nameof(EnvelopeEnterResponse), "Envelope first-entry retry", typeof(EnvelopeEnterResponse), maxPacketsToRead: 8);
            AssertEqual(0, retried.Code, "first-entry retry converges");
            AssertEqual((5L + 2 * 3, 3L), (retried.RewardGoodsList.Where(good => good.TemplateId == activity.TicketItemId)
                    .Sum(good => (long)good.Count), retried.RewardGoodsList.Where(good => good.TemplateId == activity.SelectChoiceItemId)
                    .Sum(good => (long)good.Count)),
                "first-entry retry reports dailies plus the exact first-day composition");
            AssertEqual((5L + 2 * 3, 3L), (Count(first, activity.TicketItemId), Count(first, activity.SelectChoiceItemId)),
                "first-entry retry never pays again");
            AssertEqual(TaskBusinessDay(open.AddDays(2)), first.Session.player.Envelope.LastDailyGrantBusinessDay,
                "first-entry retry persists the watermark");
        }

        // The same lost marker across a midnight boundary: relogging from the failed snapshots (pre-marker Player,
        // awarded Inventory) the next business day must still report the first-day grant and pay exactly one new daily.
        using (LoopbackSessionHarness rolled = Harness(47_127, CreateDrawCompatibilityPlayer(47_127)))
        {
            bool armed = true;
            playerSaves.BeforeReplaceOne = row =>
            {
                if (armed && row.Envelope.LastDailyGrantBusinessDay == TaskBusinessDay(open.AddDays(2)))
                {
                    armed = false;
                    throw new MongoDB.Driver.MongoException("Injected Envelope pre-midnight marker save failure.");
                }
            };
            bool failed = false;
            try { handle.Invoke(null, [rolled.Session, 4833, open.AddDays(2)]); }
            catch (TargetInvocationException) { failed = true; }
            finally { playerSaves.BeforeReplaceOne = null; }
            AssertEqual(true, failed, "pre-midnight marker save failure surfaces");
            AssertEqual((5L + 2 * 3, 3L), (Count(rolled, activity.TicketItemId), Count(rolled, activity.SelectChoiceItemId)),
                "pre-midnight payout is durable");
            while (rolled.TryReadAvailablePacket("drain failed pre-midnight entry", out _)) { }
            rolled.Session.player = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson!);
            rolled.Session.inventory = BsonSerializer.Deserialize<Inventory>(inventorySaves.LastSuccessfulReplacementBson!);
            AssertEqual(0, rolled.Session.player.Envelope.LastDailyGrantBusinessDay, "failed marker left no watermark");
            handle.Invoke(null, [rolled.Session, 4834, open.AddDays(3)]);
            EnvelopeEnterResponse nextDay = (EnvelopeEnterResponse)ReadResponsePayload(rolled, 4834,
                nameof(EnvelopeEnterResponse), "Envelope next-business-day retry", typeof(EnvelopeEnterResponse), maxPacketsToRead: 8);
            AssertEqual(0, nextDay.Code, "next-business-day retry converges");
            AssertEqual((5L + 3 * 3, 3L), (nextDay.RewardGoodsList.Where(good => good.TemplateId == activity.TicketItemId)
                    .Sum(good => (long)good.Count), nextDay.RewardGoodsList.Where(good => good.TemplateId == activity.SelectChoiceItemId)
                    .Sum(good => (long)good.Count)),
                "next-business-day retry reports the first-day grant plus three dailies");
            AssertEqual((5L + 3 * 3, 3L), (Count(rolled, activity.TicketItemId), Count(rolled, activity.SelectChoiceItemId)),
                "next-business-day retry pays exactly one new daily and no second first bonus");
            AssertEqual(TaskBusinessDay(open.AddDays(3)), rolled.Session.player.Envelope.LastDailyGrantBusinessDay,
                "next-business-day retry persists the new watermark");
        }

        // Source-known expired event: the registered request is rejected with the normal not-open code.
        using (LoopbackSessionHarness expired = Harness(47_124, CreateDrawCompatibilityPlayer(47_124)))
        {
            handle.Invoke(null, [expired.Session, 4831, DateTimeOffset.FromUnixTimeSeconds(window.EndTime)]);
            AssertEqual(20428001, ReadResponsePayload<EnvelopeEnterResponse>(expired, 4831, nameof(EnvelopeEnterResponse), "Envelope expired").Code,
                "expired window rejected");
            AssertEqual(0L, Count(expired, activity.TicketItemId), "expired window grants nothing");
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= window.EndTime)
            {
                InvokeRegisteredRequestHandler(nameof(EnvelopeEnterRequest), expired.Session, 4832, new EnvelopeEnterRequest());
                AssertEqual(20428001, ReadResponsePayload<EnvelopeEnterResponse>(expired, 4832, nameof(EnvelopeEnterResponse), "Envelope registered expired").Code,
                    "registered Enter rejects the expired event");
            }
        }

        static int TaskBusinessDay(DateTimeOffset now) => (int)(now.UtcDateTime.AddHours(-5).Date - DateTime.UnixEpoch).TotalDays;
        static List<AscNet.Table.V2.share.reward.RewardGoodsTable> RewardHandlerGoods(int rewardId)
        {
            HashSet<int> subIds = TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardTable>().Single(row => row.Id == rewardId).SubIds.ToHashSet();
            return TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardGoodsTable>().Where(row => subIds.Contains(row.Id)).ToList();
        }
    }

    private static void ValidateEnvelopeInvitations(
        EnvelopeActivityTable activity,
        DateTimeOffset open,
        Func<string, Type[], object?[], object?> invoke)
    {
        EnvelopeListTable[] invitations = TableReaderV2.Parse<EnvelopeListTable>().Take(3).ToArray();
        EnvelopeInstrumentTable instrument = TableReaderV2.Parse<EnvelopeInstrumentTable>().MinBy(row => row.OpenTarget)!;
        long uid = 47_109;
        Player player = CreateDrawCompatibilityPlayer(uid);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(uid), player,
            CreateDrawCompatibilityInventory(uid,
            [
                new Item { Id = activity.TicketItemId, Count = 2 },
                new Item { Id = activity.SelectChoiceItemId, Count = 1 }
            ]), "envelope-invitations");

        void Call<TRequest>(string method, TRequest request, int id, DateTimeOffset? clock = null) =>
            invoke(method, [typeof(Session), typeof(TRequest), typeof(int), typeof(DateTimeOffset)],
                [harness.Session, request, id, clock ?? open]);
        ConditionTable invitationFive = TableReaderV2.Parse<ConditionTable>()
            .Single(row => row.Type == 141001 && row.Params.SequenceEqual([5]));
        TaskTable inviteTask = TableReaderV2.Parse<TaskTable>()
            .Single(row => row.GroupId == activity.TaskGroup && row.Condition == invitationFive.Id);

        Call("HandleEnvelopeOpen", new EnvelopeOpenRequest { Id = invitations[0].Id }, 1071);
        ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Envelope ticket spend");
        ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Envelope first invitation task sync");
        AssertEqual(0, ReadResponsePayload<EnvelopeOpenResponse>(
            harness, 1071, nameof(EnvelopeOpenResponse), "Envelope opened").Code, "first invitation accepted");
        Call("HandleEnvelopeOpen", new EnvelopeOpenRequest { Id = invitations[0].Id }, 1072);
        AssertEqual(20428004, ReadResponsePayload<EnvelopeOpenResponse>(
            harness, 1072, nameof(EnvelopeOpenResponse), "Envelope replay").Code, "replay rejects duplicate without debit");

        Call("HandleEnvelopeSelectOpen", new EnvelopeSelectOpenRequest { CharacterId = invitations[1].CharacterId }, 1073);
        ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Envelope chosen ticket spend");
        NotifyTask earlyTasks = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Envelope chosen invitation task sync");
        AssertEqual(1, earlyTasks.Tasks.Tasks.Single(row => row.Id == (uint)inviteTask.Id).State,
            "inviting two characters leaves five-invitation task active");
        AssertEqual(0, ReadResponsePayload<EnvelopeSelectOpenResponse>(
            harness, 1073, nameof(EnvelopeSelectOpenResponse), "Envelope chosen").Code, "chosen invitation accepted");
        AssertEqual(0, harness.Session.inventory.Items.Single(row => row.Id == activity.TicketItemId).Count,
            "two distinct invitations consume precisely two tickets");
        AssertEqual(0, harness.Session.inventory.Items.Single(row => row.Id == activity.SelectChoiceItemId).Count,
            "chosen invitation consumes choice ticket");
        Call("HandleEnvelopeSelectOpen", new EnvelopeSelectOpenRequest { CharacterId = invitations[0].CharacterId }, 1079);
        AssertEqual(20428004, ReadResponsePayload<EnvelopeSelectOpenResponse>(
            harness, 1079, nameof(EnvelopeSelectOpenResponse), "Envelope selected replay").Code,
            "different endpoint cannot invite an opened character again");
        Call("HandleEnvelopeOpen", new EnvelopeOpenRequest { Id = invitations[2].Id }, 1080);
        AssertEqual(20012004, ReadResponsePayload<EnvelopeOpenResponse>(
            harness, 1080, nameof(EnvelopeOpenResponse), "Envelope tickets exhausted").Code,
            "no ticket rejects an unopened character");

        Call("HandleEnvelopeRecordAvg", new EnvelopeRecordAvgRequest { CharacterId = invitations[0].CharacterId }, 1074);
        ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Envelope watched-story task sync");
        AssertEqual(0, ReadResponsePayload<EnvelopeRecordAvgResponse>(
            harness, 1074, nameof(EnvelopeRecordAvgResponse), "Envelope story watched").Code, "opened story recorded");
        Call("HandleEnvelopeRecordAvg", new EnvelopeRecordAvgRequest { CharacterId = invitations[0].CharacterId }, 1075);
        AssertEqual(20428012, ReadResponsePayload<EnvelopeRecordAvgResponse>(
            harness, 1075, nameof(EnvelopeRecordAvgResponse), "Envelope story replay").Code, "story replay rejected");

        Call("HandleEnvelopeBind", new EnvelopeBindRequest { Bindings = new() { [instrument.Id] = invitations[0].CharacterId } }, 1076);
        AssertEqual(20428007, ReadResponsePayload<EnvelopeBindResponse>(
            harness, 1076, nameof(EnvelopeBindResponse), "Envelope locked instrument").Code, "instrument threshold enforced");
        EnvelopeState restored = BsonSerializer.Deserialize<EnvelopeState>(player.Envelope.ToBson());
        AssertEqual(2, restored.OpenedCharacterIds.Count, "relog restores invitations");
        AssertEqual(invitations[0].CharacterId, restored.AvgWatchedCharacterIds.Single(), "relog restores story");
        AssertEqual(0, restored.InstrumentBindings.Count, "locked instrument did not persist");
        player.Envelope.OpenedCharacterIds.AddRange(TableReaderV2.Parse<EnvelopeCharacterTable>()
            .Select(row => row.Id).Where(id => !player.Envelope.OpenedCharacterIds.Contains(id))
            .Take(instrument.OpenTarget - player.Envelope.OpenedCharacterIds.Count));
        Call("HandleEnvelopeBind", new EnvelopeBindRequest { Bindings = new() { [instrument.Id] = invitations[0].CharacterId } }, 1081);
        NotifyTask musicianTasks = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Envelope musician task sync");
        AssertEqual(3, musicianTasks.Tasks.Tasks.Single(row => row.Id == (uint)inviteTask.Id).State,
            "nine opened characters achieve authored five-invitation task");
        AssertEqual(0, ReadResponsePayload<EnvelopeBindResponse>(
            harness, 1081, nameof(EnvelopeBindResponse), "Envelope unlocked instrument").Code,
            "instrument binds after table threshold");
        Call("HandleEnvelopeBind", new EnvelopeBindRequest { Bindings = new() }, 1082);
        ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Envelope unbind task sync");
        AssertEqual(0, ReadResponsePayload<EnvelopeBindResponse>(
            harness, 1082, nameof(EnvelopeBindResponse), "Envelope empty bindings").Code,
            "full-map replacement unbinds omitted instrument");
        AssertEqual(0, BsonSerializer.Deserialize<EnvelopeState>(player.Envelope.ToBson()).InstrumentBindings.Count,
            "empty binding map survives BSON relog");
        Call("HandleEnvelopeOpen", new EnvelopeOpenRequest { Id = invitations[0].Id }, 1077);
        AssertEqual(20428004, ReadResponsePayload<EnvelopeOpenResponse>(
            harness, 1077, nameof(EnvelopeOpenResponse), "Envelope closed replay").Code, "persisted invitation replay rejected");
        Call("HandleEnvelopeRecordAvg", new EnvelopeRecordAvgRequest { CharacterId = invitations[0].CharacterId }, 1078, open.AddSeconds(-1));
        AssertEqual(20428001, ReadResponsePayload<EnvelopeRecordAvgResponse>(
            harness, 1078, nameof(EnvelopeRecordAvgResponse), "Envelope closed request").Code, "inactive event rejects story");

        long emptyUid = uid + 1;
        using LoopbackSessionHarness noTickets = new(
            CreateDrawCompatibilityCharacter(emptyUid), CreateDrawCompatibilityPlayer(emptyUid),
            CreateDrawCompatibilityInventory(emptyUid, []), "envelope-empty-wallet");
        invoke("HandleEnvelopeOpen",
            [typeof(Session), typeof(EnvelopeOpenRequest), typeof(int), typeof(DateTimeOffset)],
            [noTickets.Session, new EnvelopeOpenRequest { Id = invitations[0].Id }, 1083, open]);
        AssertEqual(20012004, ReadResponsePayload<EnvelopeOpenResponse>(
            noTickets, 1083, nameof(EnvelopeOpenResponse), "Envelope missing ticket item").Code,
            "missing inventory row never grants an invitation");
        AssertEqual(0, noTickets.Session.player.Envelope.OpenedCharacterIds.Count,
            "missing ticket leaves durable invitation state unchanged");
        if (!ActivityScheduleService.IsOpen(activity.TimeId, DateTimeOffset.UtcNow))
        {
            Dictionary<string, RequestPacketHandlerDelegate> registered = new(PacketFactory.ReqHandlers);
            try
            {
                PacketFactory.LoadPacketHandlers();
                if (PacketFactory.GetRequestPacketHandler(nameof(EnvelopeOpenRequest)) is null)
                    throw new InvalidDataException("EnvelopeOpenRequest was not registered for Session socket dispatch.");
                noTickets.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(
                    nameof(EnvelopeOpenRequest), 1084, new EnvelopeOpenRequest { Id = invitations[0].Id }));
                AssertEqual(20428001, ReadResponsePayload<EnvelopeOpenResponse>(
                    noTickets, 1084, nameof(EnvelopeOpenResponse), "actual Session closed envelope request").Code,
                    "normal socket dispatch rejects expired activity");
            }
            finally
            {
                PacketFactory.ReqHandlers.Clear();
                foreach ((string name, RequestPacketHandlerDelegate handler) in registered)
                    PacketFactory.ReqHandlers.Add(name, handler);
            }
        }
    }

    private static void ValidateVersion47PbrCompatibility(
        PBRActivityTable pbr,
        DateTimeOffset pbrOpen,
        Func<string, Type[], object?[], object?> invokeMaybe,
        Func<string, Type[], object?[], object> invoke)
    {
        // Inactive clock: no login root.
        Player pbrEmpty = CreateDrawCompatibilityPlayer(47_201);
        AssertEqual(null, invokeMaybe("BuildPbrNotify", [typeof(Player), typeof(DateTimeOffset)], [pbrEmpty, pbrOpen.AddSeconds(-1)]),
            "PBR inactive clock yields no push");

        // Active empty state: durable default/empty meta progression, stage records, compendiums,
        // and null segment settle exactly matching the retail root.
        PbrActivityDataNotify activeRoot = (PbrActivityDataNotify)invoke("BuildPbrNotify", [typeof(Player), typeof(DateTimeOffset)], [pbrEmpty, pbrOpen]);
        AssertEqual(pbr.Id, activeRoot.PbrDataDb.ActivityId, "PBR active login activity id");
        AssertEqual(null, activeRoot.PbrDataDb.SegmentSettleData, "PBR active login segment settle null");
        AssertEqual(0, activeRoot.PbrDataDb.MetaProgression.UnlockNodes.Count, "PBR active login unlock nodes empty");
        AssertEqual(0, activeRoot.PbrDataDb.StageRecords.Count, "PBR active login stage records empty");
        AssertEqual(0, activeRoot.PbrDataDb.Compendiums.CompendiumItems.Count, "PBR active login compendium items empty");
        AssertEqual(0, activeRoot.PbrDataDb.Compendiums.CompendiumMonsters.Count, "PBR active login compendium monsters empty");

        // Distinct persisted state is reflected through the root.
        Player pbrPopulated = CreateDrawCompatibilityPlayer(47_202);
        pbrPopulated.Pbr.ActivityId = pbr.Id;
        pbrPopulated.Pbr.MetaProgressionUnlockNodes = [5, 3, 3];
        pbrPopulated.Pbr.StageRecords[30063133] = new PbrStageRecordState { StageId = 30063133, HistoryMaxWave = 4, IsPass = true, IsPassWave = true };
        pbrPopulated.Pbr.CompendiumItems[1] = new PbrItemState { ItemId = 1, UnlockTime = 123, GainNum = 2, TriggerNum = 3 };
        pbrPopulated.Pbr.CompendiumMonsters[7] = new PbrMonsterState { MonsterId = 7, DamageTotal = 99, BeKillNum = 1 };
        PbrActivityDataNotify populated = (PbrActivityDataNotify)invoke("BuildPbrNotify", [typeof(Player), typeof(DateTimeOffset)], [pbrPopulated, pbrOpen]);
        AssertEqual("3,5", string.Join(",", populated.PbrDataDb.MetaProgression.UnlockNodes), "PBR populated unlock nodes sorted distinct");
        AssertEqual(true, populated.PbrDataDb.StageRecords[30063133].IsPass, "PBR populated stage record pass");
        AssertEqual(4, populated.PbrDataDb.StageRecords[30063133].HistoryMaxWave, "PBR populated stage record max wave");
        AssertEqual(2, populated.PbrDataDb.Compendiums.CompendiumItems[1].GainNum, "PBR populated compendium item gain");
        AssertEqual(1, populated.PbrDataDb.Compendiums.CompendiumMonsters[7].BeKillNum, "PBR populated compendium monster kills");

        // Compendium push helper maps real mutations to the wire contract.
        PbrCompendiumPush compendiumPush = (PbrCompendiumPush)invoke("BuildCompendiumPush",
            [typeof(IEnumerable<PbrItemState>), typeof(IEnumerable<PbrItemState>), typeof(IEnumerable<PbrMonsterState>), typeof(IEnumerable<PbrMonsterState>)],
            [new[] { new PbrItemState { ItemId = 9, UnlockTime = 1, GainNum = 1, TriggerNum = 0 } }, null, null,
             new[] { new PbrMonsterState { MonsterId = 77, DamageTotal = 5, BeKillNum = 3 } }]);
        AssertEqual(1, compendiumPush.AddCompendiumItems.Count, "PBR compendium push added item count");
        AssertEqual(9, compendiumPush.AddCompendiumItems[0].ItemId, "PBR compendium push added item id");
        AssertEqual(1, compendiumPush.UpdateCompendiumMonsters.Count, "PBR compendium push updated monster count");
        AssertEqual(3, compendiumPush.UpdateCompendiumMonsters[0].BeKillNum, "PBR compendium push updated monster kills");

        // BSON round-trip preserves durable PBR state.
        PbrState reloadedPbr = BsonSerializer.Deserialize<PbrState>(pbrPopulated.Pbr.ToBson());
        AssertEqual("5,3,3", string.Join(",", reloadedPbr.MetaProgressionUnlockNodes), "PBR BSON reload unlock nodes");
        AssertEqual(true, reloadedPbr.StageRecords[30063133].IsPass, "PBR BSON reload stage record");
        AssertEqual(2, reloadedPbr.CompendiumItems[1].GainNum, "PBR BSON reload compendium item");
        AssertEqual(1, reloadedPbr.CompendiumMonsters[7].BeKillNum, "PBR BSON reload compendium monster");
    }

    private static void ValidateVersion47ConcertCompatibility(
        ConcertPreHeatingActivityTable concert,
        DateTimeOffset concertOpen,
        Func<string, Type[], object?[], object?> invokeMaybe)
    {
        Player concertEmpty = CreateDrawCompatibilityPlayer(47_301);
        AssertEqual(null, invokeMaybe("BuildConcertNotify", [typeof(Player), typeof(DateTimeOffset)], [concertEmpty, concertOpen.AddSeconds(-1)]),
            "Concert inactive clock yields no push");
        AssertEqual(null, invokeMaybe("BuildConcertVideoConfigNotify", [typeof(DateTimeOffset)], [concertOpen.AddSeconds(-1)]),
            "Concert video config inactive clock yields no push");

        NotifyConcertPreHeating activeConcert = (NotifyConcertPreHeating)invokeMaybe("BuildConcertNotify", [typeof(Player), typeof(DateTimeOffset)], [concertEmpty, concertOpen])!;
        AssertEqual(concert.Id, activeConcert.ConcertPreHeatingDataDb.ActivityId, "Concert active login activity id");
        AssertEqual(0, activeConcert.ConcertPreHeatingDataDb.StageFinish.Count, "Concert active empty stage finish");

        // Video map is built strictly from the current ConcertVideoConfig table (the captured
        // player URL is never a runtime source).
        NotifyConcertVideoConfig video = (NotifyConcertVideoConfig)invokeMaybe("BuildConcertVideoConfigNotify", [typeof(DateTimeOffset)], [concertOpen])!;
        List<ConcertVideoConfigTable> videoRows = TableReaderV2.Parse<ConcertVideoConfigTable>().ToList();
        AssertEqual(videoRows.Count, video.ConcertVideoConfigs.Count, "Concert video map row count matches table");
        foreach (ConcertVideoConfigTable row in videoRows)
        {
            if (!video.ConcertVideoConfigs.TryGetValue(row.Id, out ConcertVideoConfigEntry? entry) || entry is null)
                throw new InvalidDataException($"Concert video config row {row.Id} missing.");
            AssertEqual(row.LiveUrl, entry.LiveUrl, $"Concert video config row {row.Id} live url from table");
            AssertEqual(row.RecordUrl, entry.RecordUrl, $"Concert video config row {row.Id} record url from table");
            AssertEqual(row.LiveTimeId, entry.LiveTimeId, $"Concert video config row {row.Id} live time id");
            AssertEqual(row.RecordTimeId, entry.RecordTimeId, $"Concert video config row {row.Id} record time id");
        }

        // Distinct persisted completed stages are deduplicated and sorted.
        Player concertDone = CreateDrawCompatibilityPlayer(47_302);
        concertDone.ConcertPreHeating.ActivityId = concert.Id;
        concertDone.ConcertPreHeating.CompletedStageIds = [102, 101, 101];
        NotifyConcertPreHeating done = (NotifyConcertPreHeating)invokeMaybe("BuildConcertNotify", [typeof(Player), typeof(DateTimeOffset)], [concertDone, concertOpen])!;
        AssertEqual("101,102", string.Join(",", done.ConcertPreHeatingDataDb.StageFinish.Select(stage => stage.StageId)),
            "Concert completed stages deduplicated and sorted");

        // BSON round-trip preserves durable completed stages.
        ConcertPreHeatingState reloadedConcert = BsonSerializer.Deserialize<ConcertPreHeatingState>(concertDone.ConcertPreHeating.ToBson());
        AssertEqual("102,101,101", string.Join(",", reloadedConcert.CompletedStageIds), "Concert BSON reload completed stages");
    }

    private static void ValidateVersion47SendLoginPushesOrder(
        DateTimeOffset commonOpen,
        DateTimeOffset commonClosed,
        Func<string, Type[], object?[], object?> invokeMaybe)
    {
        long uid = 47_401;
        using (LoopbackSessionHarness loginHarness = new(
            CreateDrawCompatibilityCharacter(uid),
            CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, []),
            "version47-login-push-order-test"))
        {
            // Observed startup order with all three families active: Concert, ConcertVideoConfig,
            // PBR, Envelope.
            invokeMaybe("SendLoginPushes", [typeof(Session), typeof(DateTimeOffset)], [loginHarness.Session, commonOpen]);
            ReadPushPayload<NotifyConcertPreHeating>(loginHarness, nameof(NotifyConcertPreHeating), "login-push concert");
            ReadPushPayload<NotifyConcertVideoConfig>(loginHarness, nameof(NotifyConcertVideoConfig), "login-push concert video");
            ReadPushPayload<PbrActivityDataNotify>(loginHarness, nameof(PbrActivityDataNotify), "login-push pbr");
            ReadPushPayload<NotifyEnvelope>(loginHarness, nameof(NotifyEnvelope), "login-push envelope");

            // Independent clock: when no 4.7 activity is open, no family emits anything.
            invokeMaybe("SendLoginPushes", [typeof(Session), typeof(DateTimeOffset)], [loginHarness.Session, commonClosed]);
            if (loginHarness.TryReadAvailablePacket("login-push closed-clock unexpected push", out _))
                throw new InvalidDataException("SendLoginPushes emitted a push when no 4.7 activity is open.");
        }
    }
}
