using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.gacha;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace AscNet.Test;

internal partial class Program
{
    /// <summary>4.8 coating self-choice (Activity 4, Group 5, Gacha 50..53, TimeId 51001) under an injected window.</summary>
    private static void ValidateCoatingGacha48Compatibility()
    {
        PacketFactory.LoadPacketHandlers();
        GachaFashionSelfChoiceActivityTable activity = TableReaderV2.Parse<GachaFashionSelfChoiceActivityTable>().Single(row => row.Id == 4);
        GachaFashionSelfChoiceGroupTable group = TableReaderV2.Parse<GachaFashionSelfChoiceGroupTable>().Single(row => row.Id == 5);
        AssertEqual(51001, activity.TimeId, "4.8 self-choice activity TimeId");
        AssertEqual("5", string.Join(",", activity.GachaGroupIds), "4.8 self-choice activity groups");
        AssertEqual("50,51,52,53", string.Join(",", group.GachaIds), "4.8 self-choice group gachas");
        GachaTable gacha = TableReaderV2.Parse<GachaTable>().Single(row => row.Id == 50);
        GachaCourseRewardTable course = TableReaderV2.Parse<GachaCourseRewardTable>().Single(row => row.Id == gacha.CourseRewardId);
        GachaItemExchangeTable exchange = TableReaderV2.Parse<GachaItemExchangeTable>().Single(row => row.Id == gacha.ExchangeId);
        HashSet<int> rareIds = TableReaderV2.Parse<GachaRewardTable>().Where(row => row.GroupId == gacha.PropAddGroupId).Select(row => row.Id).ToHashSet();

        FieldInfo gate = RequiredAscNetGameServerType("AscNet.GameServer.Game.GachaManager")
            .GetField("IsTimeOpen", BindingFlags.Static | BindingFlags.NonPublic)!;
        object realGate = gate.GetValue(null)!;
        MethodInfo selfChoice = RequiredAscNetGameServerType("AscNet.GameServer.Game.GachaManager")
            .GetMethod("BuildSelfChoicePayload", BindingFlags.Static | BindingFlags.NonPublic)!;
        NotifySelfChoiceGachaData SelfChoice(Player player) => (NotifySelfChoiceGachaData)selfChoice.Invoke(null, [player])!;
        bool realOpen = AscNet.GameServer.Game.ActivityScheduleService.IsOpen(51001, DateTimeOffset.UtcNow);

        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        IMongoCollection<Player> collection = DispatchProxy.Create<IMongoCollection<Player>, GachaPlayerSaveProxy>();
        GachaPlayerSaveProxy saves = (GachaPlayerSaveProxy)(object)collection;
        typeof(MongoCollectionOverride).GetMethod("SetStaticField", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [typeof(Player).GetField("collection", BindingFlags.Static | BindingFlags.Public)!, collection]);
        int packetId = 48_510_000;
        const int uid = 48_510;

        T Call<T>(LoopbackSessionHarness harness, object request, string name)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(request.GetType().Name, harness.Session, id, request);
            for (int index = 0; index < 64; index++)
            {
                Packet packet = harness.ReadPacket(name);
                if (packet.Type == Packet.ContentType.Push) continue;
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, response.Id, name + " correlation");
                AssertEqual(typeof(T).Name, response.Name, name + " response name");
                return MessagePackSerializer.Deserialize<T>(response.Content);
            }
            throw new InvalidDataException(name + " missing response");
        }
        long Balance(LoopbackSessionHarness harness, int itemId) =>
            harness.Session.inventory.Items.SingleOrDefault(item => item.Id == itemId)?.Count ?? 0;
        LoopbackSessionHarness Relog(LoopbackSessionHarness from, string name)
        {
            LoopbackSessionHarness next = new(BsonSerializer.Deserialize<Character>(from.Session.character.ToBson()),
                BsonSerializer.Deserialize<Player>(saves.Persisted!), BsonSerializer.Deserialize<Inventory>(from.Session.inventory.ToBson()), name);
            next.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            return next;
        }

        Character character = new() { Uid = uid, Characters = [], Equips = [], Fashions = [], Partners = [] };
        Inventory inventory = new()
        {
            Uid = uid,
            Items = [new Item { Id = gacha.ConsumeId, Count = 100 }, new Item { Id = TableReaderV2.Parse<GachaTable>().Single(row => row.Id == 51).ConsumeId, Count = 1 }, new Item { Id = exchange.UseItemIds[0], Count = exchange.UseItemCounts[0] * 2 }]
        };
        using LoopbackSessionHarness harness = new(character, inventory: inventory, sessionId: "coating-gacha-48");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);

        // Real calendar: 51001 is authored with no fallback; closed means every mutation is rejected.
        if (!realOpen)
        {
            AssertEqual(20061003, Call<GachaResponse>(harness, new GachaRequest { Id = 50, Times = 1 }, "real closed draw").Code, "real 51001 closed draw");
            AssertEqual(20260002, Call<ChoiceGachaResponse>(harness, new ChoiceGachaRequest { Id = 50 }, "real closed choice").Code, "real 51001 closed choice");
            AssertEqual(0, SelfChoice(harness.Session.player).ActivityId, "closed self-choice notify ActivityId");
        }

        gate.SetValue(null, (Func<int, bool>)(timeId => timeId == 51001));
        try
        {
            AssertEqual(4, SelfChoice(harness.Session.player).ActivityId, "open self-choice notify ActivityId");
            AssertEqual(20260005, Call<GachaResponse>(harness, new GachaRequest { Id = 50, Times = 1 }, "unselected draw").Code, "draw requires choice");
            AssertEqual(0, Call<ChoiceGachaResponse>(harness, new ChoiceGachaRequest { Id = 50 }, "choose 50").Code, "choose 50");
            AssertEqual(20260004, Call<ChoiceGachaResponse>(harness, new ChoiceGachaRequest { Id = 50 }, "rechoose same").Code, "same selection rejected");
            AssertEqual(20260005, Call<GachaResponse>(harness, new GachaRequest { Id = 51, Times = 1 }, "other draw").Code, "unchosen sibling locked");
            AssertEqual(20061006, Call<GachaResponse>(harness, new GachaRequest { Id = 50, Times = 3 }, "bad times").Code, "BtnGachaCount validation");

            // 20 draws: guarantee forces a special reward by the 20th miss-free window.
            GachaResponse first = Call<GachaResponse>(harness, new GachaRequest { Id = 50, Times = 10 }, "draw 10");
            AssertEqual(0, first.Code, "draw 10 succeeds");
            AssertEqual(10, first.RewardList.Count, "ten rewards");
            GachaResponse second = Call<GachaResponse>(harness, new GachaRequest { Id = 50, Times = 10 }, "draw 20");
            AssertEqual(20, second.GachaCourseResult.TotalTimes, "total times");
            AssertEqual(80L, Balance(harness, gacha.ConsumeId), "20 tickets debited");
            GachaStateInfo state = harness.Session.player.Gacha.Infos.Single(info => info.Id == 50);
            AssertEqual(true, state.RewardTimes.Keys.Any(rareIds.Contains), "20-draw guarantee yields a special reward");
            AssertEqual(true, state.MissTimes < gacha.PropStartTimes, "miss counter bounded by guarantee");
            AssertEqual(true, state.RewardTimes.Where(pair => rareIds.Contains(pair.Key)).All(pair => pair.Value == 1), "limited specials never repeat");
            if (state.RewardTimes.ContainsKey(1338))
                AssertEqual(true, harness.Session.player.OwnedBackgroundIds.Contains(14000015), "special 1338 grants background 14000015");

            // Milestone at course.LimitDrawTimes[0].
            int toMilestone = course.LimitDrawTimes[0] - state.TotalTimes;
            GachaResponse milestone = null!;
            for (int drawn = 0; drawn < toMilestone; drawn += 10)
                milestone = Call<GachaResponse>(harness, new GachaRequest { Id = 50, Times = 10 }, "draw to milestone");
            AssertEqual(true, milestone.GachaCourseResult.RewardList.Count > 0, "milestone course reward granted once crossed");

            // Change 50 -> 51 -> 50: progress is per coating and survives switching.
            string progress50 = state.ToJson();
            AssertEqual(0, Call<ChoiceGachaResponse>(harness, new ChoiceGachaRequest { Id = 51 }, "change to 51").Code, "change to 51");
            AssertEqual(20260005, Call<GachaResponse>(harness, new GachaRequest { Id = 50, Times = 1 }, "draw old after change").Code, "old coating locked after change");
            AssertEqual(0, Call<GachaResponse>(harness, new GachaRequest { Id = 51, Times = 1 }, "draw 51").Code, "draw 51");
            AssertEqual(0, Call<ChoiceGachaResponse>(harness, new ChoiceGachaRequest { Id = 50 }, "change back to 50").Code, "change back to 50");
            AssertEqual(progress50, harness.Session.player.Gacha.Infos.Single(info => info.Id == 50).ToJson(), "50 progress unchanged by switching");
            AssertEqual(1, harness.Session.player.Gacha.Infos.Single(info => info.Id == 51).TotalTimes, "51 keeps its own progress");
            AssertEqual(1, Call<GetGachaInfoResponse>(harness, new GetGachaInfoRequest { Id = 51 }, "info 51").GachaRecordList!.Count, "51 records separate");

            // Ack loss on the intent save: write committed, caller saw an exception. Same session must not reroll.
            long beforeAck = Balance(harness, gacha.ConsumeId);
            saves.FailAfterWrite = true;
            saves.WritesUntilFailure = 0;
            try
            {
                InvokeRegisteredRequestHandler(nameof(GachaRequest), harness.Session, ++packetId, new GachaRequest { Id = 50, Times = 1 });
                throw new InvalidDataException("Gacha ack-loss save unexpectedly succeeded.");
            }
            catch (InvalidDataException exception) when (exception.InnerException is MongoException) { }
            finally { saves.WritesUntilFailure = -1; saves.FailAfterWrite = false; }
            AssertNoAvailablePacket(harness, "ack-loss intent emits nothing");
            int ackFrozen = BsonSerializer.Deserialize<Player>(saves.Persisted!).Gacha.Infos.Single(i => i.Id == 50).Pending!.RewardIds.Single();
            AssertEqual(beforeAck, Balance(harness, gacha.ConsumeId), "ack-loss intent pays nothing");
            AssertEqual(0, Call<GachaResponse>(harness, new GachaRequest { Id = 50, Times = 10 }, "retry after ack loss").Code, "retry after ack loss");
            GachaStateInfo afterAck = harness.Session.player.Gacha.Infos.Single(i => i.Id == 50);
            AssertEqual(ackFrozen, afterAck.Records[^1].RewardId, "ack-loss retry pays the durable roll, not a reroll");
            AssertEqual(beforeAck - gacha.ConsumeCount, Balance(harness, gacha.ConsumeId), "ack-loss retry pays one draw once");

            // Exchange: SelectIndex 0, limits and receipts.
            AssertEqual(20061012, Call<GachaItemExchangeResponse>(harness,
                new GachaItemExchangeRequest { Id = 50, ExchangeNum = exchange.BuyCountMax + 1 }, "over single").Code, "single purchase cap");
            GachaItemExchangeResponse bought = Call<GachaItemExchangeResponse>(harness, new GachaItemExchangeRequest { Id = 50, ExchangeNum = 2, SelectIndex = 0 }, "exchange");
            AssertEqual(0, bought.Code, "exchange succeeds");
            AssertEqual(gacha.ConsumeId, bought.GainItemId, "exchange grants gacha currency");
            AssertEqual(2, bought.CurExchangeItemCount, "exchange count");
            AssertEqual(0L, Balance(harness, exchange.UseItemIds[0]), "exchange debit");

            // Persisted login state.
            using LoopbackSessionHarness relog = Relog(harness, "coating-gacha-48-relog");
            GetGachaInfoResponse info = Call<GetGachaInfoResponse>(relog, new GetGachaInfoRequest { Id = 50 }, "relog info");
            AssertEqual(state.TotalTimes, info.TotalTimes, "relog total");
            AssertEqual(state.MissTimes, info.MissTimes, "relog miss");
            AssertEqual(2, info.CurExchangeItemCount, "relog exchange");
            AssertEqual(state.TotalTimes, info.GachaRecordList!.Count, "relog records");
            AssertEqual(50, relog.Session.player.Gacha.SelectedGroupIdToGachaId[5], "relog selection");

            // Failed final save leaves a durable pending draw; retry cannot reroll or re-debit.
            long before = Balance(relog, gacha.ConsumeId);
            saves.WritesUntilFailure = 1;
            try
            {
                InvokeRegisteredRequestHandler(nameof(GachaRequest), relog.Session, ++packetId, new GachaRequest { Id = 50, Times = 1 });
                throw new InvalidDataException("Gacha injected final save unexpectedly succeeded.");
            }
            catch (InvalidDataException exception) when (exception.InnerException is MongoException) { }
            finally { saves.WritesUntilFailure = -1; }
            AssertNoAvailablePacket(relog, "failed gacha save emits nothing");
            // Receipt (debit + reward) is durable; the final player save failed. Window then closes; BSON reload + login recovery.
            Player pending = BsonSerializer.Deserialize<Player>(saves.Persisted!);
            GachaPendingOperation frozen = pending.Gacha.Infos.Single(i => i.Id == 50).Pending!;
            AssertEqual(1, frozen.RewardIds.Count, "frozen single draw");
            string inventoryAfterReceipt = relog.Session.inventory.ToJson();
            AssertEqual(before - gacha.ConsumeCount, Balance(relog, gacha.ConsumeId), "receipt debited once");
            gate.SetValue(null, (Func<int, bool>)(_ => false));
            using LoopbackSessionHarness login = new(BsonSerializer.Deserialize<Character>(relog.Session.character.ToBson()), pending,
                BsonSerializer.Deserialize<Inventory>(relog.Session.inventory.ToBson()), "coating-gacha-48-closed-login");
            login.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            RequiredAscNetGameServerType("AscNet.GameServer.Game.GachaManager")
                .GetMethod("RecoverPending", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [login.Session]);
            GachaStateInfo recovered = login.Session.player.Gacha.Infos.Single(i => i.Id == 50);
            AssertEqual(true, recovered.Pending is null, "login recovery completes pending after window end");
            AssertEqual(frozen.RewardIds[0], recovered.Records[^1].RewardId, "login recovery keeps frozen roll");
            AssertEqual(state.TotalTimes + 1, recovered.TotalTimes, "login recovery commits one draw");
            AssertEqual(inventoryAfterReceipt, login.Session.inventory.ToJson(), "login recovery neither re-debits nor re-grants");
            AssertEqual(true, BsonSerializer.Deserialize<Player>(saves.Persisted!).Gacha.Infos.Single(i => i.Id == 50).Pending is null, "recovery durably clears pending");
            AssertEqual(20061003, Call<GachaResponse>(login, new GachaRequest { Id = 50, Times = 1 }, "closed after window").Code, "closed window rejects new draws");
            // Unpaid durable intent with no receipt and funds spent elsewhere: login drops it instead of wedging.
            Player unpaid = BsonSerializer.Deserialize<Player>(saves.Persisted!);
            unpaid.Gacha.Infos.Single(i => i.Id == 50).Pending = new() { RewardIds = [frozen.RewardIds[0]], CostItemId = gacha.ConsumeId, CostCount = gacha.ConsumeCount };
            Inventory broke = BsonSerializer.Deserialize<Inventory>(login.Session.inventory.ToBson());
            broke.Items.RemoveAll(item => item.Id == gacha.ConsumeId);
            string brokeJson = broke.ToJson();
            using LoopbackSessionHarness unpaidLogin = new(BsonSerializer.Deserialize<Character>(login.Session.character.ToBson()), unpaid, broke, "coating-gacha-48-unpaid");
            unpaidLogin.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            int totalBefore = unpaid.Gacha.Infos.Single(i => i.Id == 50).TotalTimes;
            RequiredAscNetGameServerType("AscNet.GameServer.Game.GachaManager")
                .GetMethod("RecoverPending", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [unpaidLogin.Session]);
            GachaStateInfo dropped = unpaidLogin.Session.player.Gacha.Infos.Single(i => i.Id == 50);
            AssertEqual(true, dropped.Pending is null, "unpaid intent dropped at login");
            AssertEqual(totalBefore, dropped.TotalTimes, "dropped intent grants no draw");
            AssertEqual(brokeJson, unpaidLogin.Session.inventory.ToJson(), "dropped intent neither debits nor grants");
            AssertEqual(true, BsonSerializer.Deserialize<Player>(saves.Persisted!).Gacha.Infos.Single(i => i.Id == 50).Pending is null, "drop is durable");

            // Authored special 1338 (Background 14000015): frozen roll paid through the real draw path.
            gate.SetValue(null, (Func<int, bool>)(timeId => timeId == 51001));
            const int bgUid = 48_511;
            Player bgPlayer = BsonSerializer.Deserialize<Player>(saves.Persisted!);
            bgPlayer.Gacha = new() { SelectedGroupIdToGachaId = { [5] = 50 } };
            bgPlayer.Gacha.Infos.Add(new() { Id = 50, Pending = new() { RewardIds = [1338], CostItemId = gacha.ConsumeId, CostCount = gacha.ConsumeCount, Time = 1 } });
            bgPlayer.OwnedBackgroundIds.Remove(14000015);
            using LoopbackSessionHarness bg = new(new Character { Uid = bgUid, Characters = [], Equips = [], Fashions = [], Partners = [] }, bgPlayer,
                new Inventory { Uid = bgUid, Items = [new Item { Id = gacha.ConsumeId, Count = gacha.ConsumeCount }] }, "coating-gacha-48-background");
            bg.Session.stage = CreateLoginAccountCompatibilityStage(bgUid);
            int bgPacket = ++packetId;
            InvokeRegisteredRequestHandler(nameof(GachaRequest), bg.Session, bgPacket, new GachaRequest { Id = 50, Times = 1 });
            bool backgroundPushed = false;
            GachaResponse? bgResponse = null;
            for (int index = 0; index < 64 && bgResponse is null; index++)
            {
                Packet packet = bg.ReadPacket("background draw");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    if (push.Name == "NotifyAddBackground")
                        backgroundPushed |= MessagePackSerializer.ConvertToJson(push.Content).Contains("14000015");
                    continue;
                }
                bgResponse = MessagePackSerializer.Deserialize<GachaResponse>(MessagePackSerializer.Deserialize<Packet.Response>(packet.Content).Content);
            }
            AssertEqual(0, bgResponse!.Code, "background special draw succeeds");
            AssertEqual(14000015, bgResponse.RewardList.Single().TemplateId, "background special reward goods");
            AssertEqual(true, bg.Session.player.OwnedBackgroundIds.Contains(14000015), "special 1338 grants Background 14000015");
            AssertEqual(true, backgroundPushed, "NotifyAddBackground{14000015} precedes response");
            AssertEqual(0L, Balance(bg, gacha.ConsumeId), "background draw debits once");
        }
        finally
        {
            gate.SetValue(null, realGate);
        }
    }

    private class GachaPlayerSaveProxy : RecordingMongoCollectionProxy<Player>
    {
        public int WritesUntilFailure { get; set; } = -1;
        public bool FailAfterWrite { get; set; }
        public byte[]? Persisted { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IMongoCollection<Player>.ReplaceOne)
                && args?.OfType<Player>().SingleOrDefault() is Player player)
            {
                if (WritesUntilFailure == 0 && !FailAfterWrite)
                    throw new MongoException("Injected gacha player-save failure.");
                if (WritesUntilFailure > 0)
                    WritesUntilFailure--;
                object? result = base.Invoke(targetMethod, args);
                Persisted = player.ToBson();
                if (WritesUntilFailure == 0 && FailAfterWrite)
                {
                    WritesUntilFailure = -1;
                    throw new MongoException("Injected gacha player-save ack loss.");
                }
                return result;
            }
            return base.Invoke(targetMethod, args);
        }
    }
}
