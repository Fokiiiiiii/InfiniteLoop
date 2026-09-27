using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.item;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateItemBuyAssetCompatibility()
    {
        FieldInfo clock = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager")
            .GetField("UtcNow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var priorClock = (Func<DateTimeOffset>)clock.GetValue(null)!;
        clock.SetValue(null, (Func<DateTimeOffset>)(() => new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)));
        try
        {
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out var playerSaves, out var characterSaves, out var inventorySaves);
            long uid = 48_170;
            int packetId = 48_800;
            static long Held(Inventory inventory, int itemId) =>
                inventory.Items.FirstOrDefault(item => item.Id == itemId)?.Count ?? 0L;
            static Inventory Copy(Inventory inventory) => BsonSerializer.Deserialize<Inventory>(inventory.ToBson());
            ItemBuyAssetResponse Buy(LoopbackSessionHarness harness, int itemId, int times, int consumeId, string label)
            {
                InvokeRegisteredRequestHandler(nameof(ItemBuyAssetRequest), harness.Session, ++packetId,
                    new ItemBuyAssetRequest { ItemId = itemId, Times = times, ConsumeId = consumeId });
                return ReadResponsePayload<ItemBuyAssetResponse>(harness, packetId, nameof(ItemBuyAssetResponse), label,
                    maxPacketsToRead: 10);
            }
            void BuyFails(LoopbackSessionHarness harness, int itemId, int times, string label)
            {
                try
                {
                    _ = Buy(harness, itemId, times, 0, label);
                    throw new InvalidDataException($"{label}: injected inventory save failure was not raised.");
                }
                catch (InvalidDataException error) when (error.InnerException is MongoDB.Driver.MongoException) { }
                while (harness.TryReadAvailablePacket(label, out _)) { }
            }
            Dictionary<int, BuyAssetConfigTable> configs = TableReaderV2.Parse<BuyAssetConfigTable>().ToDictionary(row => row.Id);

            foreach ((int ticket, int drawId, int unit) in new[] { (50017, 5612, 175), (50018, 381, 250), (50019, 7068, 250) })
            {
                // Expected price comes from the authored source row, not from the handler.
                BuyAssetConfigTable price = configs[TableReaderV2.Parse<BuyAssetTable>().Single(row => row.Id == ticket).Config[0]];
                int consumeId = price.ConsumeId[0];
                long cost = (long)price.ConsumeCount[0] * unit, gain = (long)price.GainCount * unit;
                using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(++uid),
                    CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid,
                        [new Item { Id = consumeId, Count = 3 * cost + 2 }]), $"buy-asset-{ticket}");
                while (harness.TryReadAvailablePacket("buy asset login", out _)) { }
                int saves = inventorySaves.ReplaceOneCalls + playerSaves.ReplaceOneCalls + characterSaves.ReplaceOneCalls;
                foreach ((string name, int itemId, int times, int consume, int code) in new[]
                {
                    ("forged consume", ticket, 1, 1, 20012029),
                    ("self consume", ticket, 1, ticket, 20012029),
                    ("not for sale", ticket + 4, 1, 0, 20012007),
                    ("zero", ticket, 0, consumeId, 20012001),
                    ("negative", ticket, -1, consumeId, 20012001),
                    ("insufficient", ticket, 3 * unit + 3, consumeId, 20012004),
                })
                {
                    ItemBuyAssetResponse rejected = Buy(harness, itemId, times, consume, name);
                    AssertEqual((code, 0), (rejected.Code, rejected.Count), $"BuyAsset {ticket} {name} rejected");
                }
                AssertEqual((saves, 3 * cost + 2, 0L), (inventorySaves.ReplaceOneCalls + playerSaves.ReplaceOneCalls
                    + characterSaves.ReplaceOneCalls, Held(harness.Session.inventory, consumeId), Held(harness.Session.inventory, ticket)),
                    $"BuyAsset {ticket} rejections persist nothing and keep the wallet");

                // Write fails before commit: the stored document is the pre-purchase one.
                inventorySaves.FindResults = [Copy(harness.Session.inventory)];
                inventorySaves.ThrowOnReplaceOne = true;
                try { BuyFails(harness, ticket, unit, "pre-commit failure"); }
                finally { inventorySaves.ThrowOnReplaceOne = false; }
                AssertEqual((3 * cost + 2, 0L, true), (Held(harness.Session.inventory, consumeId), Held(harness.Session.inventory, ticket),
                    harness.Session.player.PendingBuyAsset is null), $"BuyAsset {ticket} pre-commit failure keeps the stored wallet");

                // Acknowledgement lost after commit (wallet funds more purchases): exactly one purchase, acknowledged.
                Dictionary<int, int> countersBefore = new(harness.Session.player.MissionProgress.ConditionCounters);
                inventorySaves.BeforeReplaceOne = document =>
                {
                    inventorySaves.FindResults = [Copy(document)];
                    throw new MongoDB.Driver.MongoException("Injected BuyAsset acknowledgement loss after commit.");
                };
                ItemBuyAssetResponse acked;
                try { acked = Buy(harness, ticket, unit, 0, "ack loss"); }
                finally { inventorySaves.BeforeReplaceOne = null; }
                inventorySaves.FindResults = null;
                AssertEqual((0, (int)gain, 2 * cost + 2, gain), (acked.Code, acked.Count, Held(harness.Session.inventory, consumeId),
                    Held(harness.Session.inventory, ticket)), $"BuyAsset {ticket} ack loss completes one purchase");
                Dictionary<int, int> countersAfterOne = new(harness.Session.player.MissionProgress.ConditionCounters);
                string Delta(Dictionary<int, int> from, Dictionary<int, int> to) => string.Join(",", to
                    .Select(pair => (pair.Key, Change: pair.Value - from.GetValueOrDefault(pair.Key)))
                    .Where(pair => pair.Change != 0).OrderBy(pair => pair.Key));
                string oneSpend = Delta(countersBefore, countersAfterOne);
                AssertEqual(true, oneSpend.Length > 0, $"BuyAsset {ticket} records 11202 spend progress");

                // Progress write fails after the inventory commit: the journal survives and the retry finishes it once.
                Player? durablePlayer = null;
                bool armed = true;
                playerSaves.BeforeReplaceOne = row =>
                {
                    if (armed && row.PendingBuyAsset is null)
                    {
                        armed = false;
                        // Nothing was written by this attempt, so the handler's reload must still find the
                        // journal write it committed first: hand back that document.
                        playerSaves.FindResults = [BsonSerializer.Deserialize<Player>(durablePlayer!.ToBson())];
                        throw new MongoDB.Driver.MongoException("Injected BuyAsset progress write failure.");
                    }
                    durablePlayer = BsonSerializer.Deserialize<Player>(row.ToBson());
                };
                try { BuyFails(harness, ticket, unit, "progress failure"); }
                finally
                {
                    playerSaves.BeforeReplaceOne = null;
                    playerSaves.FindResults = null;
                }
                AssertEqual((cost + 2, 2 * gain, false, ""), (Held(harness.Session.inventory, consumeId), Held(harness.Session.inventory, ticket),
                    harness.Session.player.PendingBuyAsset is null,
                    Delta(countersAfterOne, harness.Session.player.MissionProgress.ConditionCounters)),
                    $"BuyAsset {ticket} progress failure keeps the journal and no progress");
                ItemBuyAssetResponse resumed = Buy(harness, ticket, unit, 0, "resume");
                AssertEqual((0, (int)gain, cost + 2, 2 * gain, true, oneSpend), (resumed.Code, resumed.Count,
                    Held(harness.Session.inventory, consumeId), Held(harness.Session.inventory, ticket),
                    harness.Session.player.PendingBuyAsset is null,
                    Delta(countersAfterOne, harness.Session.player.MissionProgress.ConditionCounters)),
                    $"BuyAsset {ticket} retry finishes the journal exactly once");

                harness.Session.Save(); // Session.Disconnect save path.
                Inventory stored = BsonSerializer.Deserialize<Inventory>(inventorySaves.LastSuccessfulReplacementBson!);
                AssertEqual((cost + 2, 2 * gain, 2 * unit), (Held(stored, consumeId), Held(stored, ticket),
                    stored.Items.Single(item => item.Id == ticket).TotalBuyTimes), $"BuyAsset {ticket} disconnect save keeps both purchases");

                AssertEqual(0, DrawWithTicket(harness, drawId, 1, 0, ++packetId, $"draw {drawId} with bought {ticket}").Code,
                    $"draw {drawId} accepts purchased {ticket}");
                AssertEqual(2 * gain - unit, Held(harness.Session.inventory, ticket), $"draw {drawId} spends purchased {ticket}");
            }

            // Item 2 + 3 are one client balance (XItemManager.GetCount); ConsumeId 3 spends item 2 first, then 3.
            using (LoopbackSessionHarness family = new(CreateDrawCompatibilityCharacter(++uid),
                CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid,
                    [new Item { Id = 2, Count = 4 }, new Item { Id = 3, Count = 2 }]), "buy-asset-family"))
            {
                while (family.TryReadAvailablePacket("buy asset family login", out _)) { }
                ItemBuyAssetResponse bought = Buy(family, 50017, 5, 3, "family buy");
                Inventory stored = BsonSerializer.Deserialize<Inventory>(inventorySaves.LastSuccessfulReplacementBson!);
                AssertEqual((0, 5, 0L, 1L, 5L), (bought.Code, bought.Count, Held(stored, 2), Held(stored, 3), Held(stored, 50017)),
                    "BuyAsset spends the combined 2+3 balance and persists it");
            }

            long max = Inventory.GetMaxCount(TableReaderV2.Parse<ItemTable>().Single(row => row.Id == 50017));
            using LoopbackSessionHarness full = new(CreateDrawCompatibilityCharacter(++uid),
                CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid,
                    [new Item { Id = 3, Count = 10 }, new Item { Id = 50017, Count = max }]), "buy-asset-cap");
            while (full.TryReadAvailablePacket("buy asset cap login", out _)) { }
            AssertEqual((20012005, 10L, max), (Buy(full, 50017, 1, 3, "cap").Code, Held(full.Session.inventory, 3),
                Held(full.Session.inventory, 50017)), "BuyAsset cap leaves state unchanged");

            // Journal write commits but its acknowledgement is lost: the reloaded journal proves it, so the
            // purchase finishes once instead of being silently reset or debited twice.
            (int journalTicket, int journalUnit) = (50017, 175);
            BuyAssetConfigTable journalPrice = configs[TableReaderV2.Parse<BuyAssetTable>()
                .Single(row => row.Id == journalTicket).Config[0]];
            int journalConsume = journalPrice.ConsumeId[0];
            long journalCost = (long)journalPrice.ConsumeCount[0] * journalUnit;
            long journalGain = (long)journalPrice.GainCount * journalUnit;
            using (LoopbackSessionHarness journalAck = new(CreateDrawCompatibilityCharacter(++uid),
                CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid,
                    [new Item { Id = journalConsume, Count = 3 * journalCost + 2 }]), "buy-asset-journal-ack"))
            {
                while (journalAck.TryReadAvailablePacket("buy asset journal login", out _)) { }
                bool armed = true;
                playerSaves.BeforeReplaceOne = row =>
                {
                    if (armed && row.PendingBuyAsset is not null)
                    {
                        armed = false;
                        playerSaves.FindResults = [BsonSerializer.Deserialize<Player>(row.ToBson())];
                        throw new MongoDB.Driver.MongoException("Injected BuyAsset journal acknowledgement loss.");
                    }
                };
                ItemBuyAssetResponse acked;
                try { acked = Buy(journalAck, journalTicket, journalUnit, journalConsume, "journal ack loss"); }
                finally
                {
                    playerSaves.BeforeReplaceOne = null;
                    playerSaves.FindResults = null;
                }
                AssertEqual((0, (int)journalGain, 2 * journalCost + 2, journalGain, true),
                    (acked.Code, acked.Count, Held(journalAck.Session.inventory, journalConsume),
                        Held(journalAck.Session.inventory, journalTicket),
                        journalAck.Session.player.PendingBuyAsset is null),
                    "BuyAsset journal acknowledgement loss completes exactly one purchase");
            }
        }
        finally
        {
            clock.SetValue(null, priorClock);
        }
    }
}
