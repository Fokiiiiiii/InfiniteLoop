using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.chat;
using AscNet.Table.V2.share.reward;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static void ValidateChatBoardRewardGrants()
        {
            Type handler = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler");
            Type grantType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardGrant");
            MethodInfo apply = RequiredMethod(handler, "ApplyRewardsOnceAndPersist",
                BindingFlags.Static | BindingFlags.Public,
                [typeof(IReadOnlyList<>).MakeGenericType(grantType), typeof(Session)]);
            MethodInfo getRewardType = RequiredMethod(handler, "GetRewardType",
                BindingFlags.Static | BindingFlags.Public, [typeof(RewardGoodsTable)]);
            MethodInfo getRewardGoods = RequiredMethod(handler, "GetRewardGoods",
                BindingFlags.Static | BindingFlags.Public, [typeof(int)]);

            // Derive the authored tower rank reward join: Reward 69589 -> its chat board goods.
            RewardGoodsTable authored = ((List<RewardGoodsTable>)getRewardGoods.Invoke(null, [69_589])!)
                .Single(row => getRewardType.Invoke(null, [row]) is RewardType.ChatBoard);
            ChatBoardTable chatBoard = TableReaderV2.Parse<ChatBoardTable>().Single(row => row.Id == authored.TemplateId);

            void Grant(LoopbackSessionHarness target, string claim, bool expectFailure = false)
            {
                Array grants = Array.CreateInstance(grantType, 1);
                grants.SetValue(Activator.CreateInstance(grantType, claim, new List<RewardGoodsTable> { authored }, null, null), 0);
                object? result;
                try { result = apply.Invoke(null, [grants, target.Session]); }
                catch (TargetInvocationException) when (expectFailure) { return; }
                if (expectFailure)
                    throw new InvalidDataException($"chat board reward {claim}: forced Player save failure did not throw.");
                result!.GetType().GetMethod("SendPushes", BindingFlags.Instance | BindingFlags.Public)!
                    .Invoke(result, [target.Session]);
            }
            static void AssertNoPacket(LoopbackSessionHarness target, string name)
            {
                if (target.TryReadAvailablePacket($"{name} unexpected packet", out Packet extra))
                    throw new InvalidDataException($"{name}: unexpected extra {extra.Type} packet.");
            }
            static ChatBoardUnlockState Board(Player player, int boardId) =>
                player.UnlockedChatBoards.Single(unlock => unlock.Id == boardId);
            static Player Reloaded(byte[]? bson, string name) => BsonSerializer.Deserialize<Player>(
                bson ?? throw new InvalidDataException($"{name}: expected a persisted Player."));

            const long playerId = 99_701;
            using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _))
            {
                Player player = CreateDrawCompatibilityPlayer(playerId);
                using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
                    CreateDrawCompatibilityInventory(playerId, []), "chat-board-reward-grant");

                long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Grant(harness, "chat-board-reward:first");
                long after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ChatBoardUnlockState first = Board(player, authored.TemplateId);
                AssertEqual(true, first.GetTime >= before && first.GetTime <= after,
                    "chat board reward: grant clock is now");
                AssertEqual((long)chatBoard.Duration, first.EndTime - first.GetTime,
                    "chat board reward: expiry uses the authored duration");
                NotifyChatBoardInfo firstPush = ReadPushPayload<NotifyChatBoardInfo>(
                    harness, nameof(NotifyChatBoardInfo), "chat board reward push");
                AssertEqual(first.EndTime, firstPush.ChatBoard.EndTime, "chat board reward push expiry");
                AssertEqual(1, player.ChatBoardRewardClaims.Count(claim => claim == "chat-board-reward:first"),
                    "chat board reward: one claim receipt on Player");
                AssertEqual(1, playerSaves.ReplaceOneCalls, "chat board reward: first grant saves Player once");

                // Receipt retry (ack loss): the committed ownership is replayed, nothing is granted or saved again.
                Grant(harness, "chat-board-reward:first");
                NotifyChatBoardInfo retryPush = ReadPushPayload<NotifyChatBoardInfo>(
                    harness, nameof(NotifyChatBoardInfo), "chat board reward retry push");
                AssertEqual(first.GetTime, retryPush.ChatBoard.GetTime,
                    "chat board reward: retry replays the grant clock");
                AssertEqual(first.EndTime, retryPush.ChatBoard.EndTime,
                    "chat board reward: retry replays the expiry");
                AssertEqual(first.EndTime, Board(player, authored.TemplateId).EndTime,
                    "chat board reward: retry does not extend expiry");
                AssertEqual(1, playerSaves.ReplaceOneCalls, "chat board reward: retry saves nothing");
                Player reloaded = Reloaded(playerSaves.LastSuccessfulReplacementBson, "chat board reward");
                AssertEqual(first.EndTime, Board(reloaded, authored.TemplateId).EndTime,
                    "chat board reward: reloaded ownership keeps the expiry");
                harness.Session.player = reloaded;
                Grant(harness, "chat-board-reward:first");
                NotifyChatBoardInfo reloadedRetryPush = ReadPushPayload<NotifyChatBoardInfo>(
                    harness, nameof(NotifyChatBoardInfo), "chat board reward reloaded retry push");
                AssertEqual(first.EndTime, reloadedRetryPush.ChatBoard.EndTime,
                    "chat board reward: reloaded retry replays the expiry");
                AssertEqual(first.EndTime, Board(harness.Session.player, authored.TemplateId).EndTime,
                    "chat board reward: reloaded retry does not extend expiry");
                AssertEqual(1, playerSaves.ReplaceOneCalls, "chat board reward: reloaded retry saves nothing");

                // A distinct claim stacks one more authored duration on the live board.
                Grant(harness, "chat-board-reward:second");
                ChatBoardUnlockState second = Board(harness.Session.player, authored.TemplateId);
                AssertEqual(first.EndTime + chatBoard.Duration, second.EndTime,
                    "chat board reward: distinct claim extends from the live expiry");
                AssertEqual(first.GetTime, second.GetTime, "chat board reward: extension keeps the original grant clock");
                NotifyChatBoardInfo secondPush = ReadPushPayload<NotifyChatBoardInfo>(
                    harness, nameof(NotifyChatBoardInfo), "chat board reward extension push");
                AssertEqual(second.EndTime, secondPush.ChatBoard.EndTime, "chat board reward extension push expiry");
                AssertEqual(2, playerSaves.ReplaceOneCalls, "chat board reward: distinct claim saves Player once");
                Grant(harness, "chat-board-reward:first");
                NotifyChatBoardInfo appliedRetryPush = ReadPushPayload<NotifyChatBoardInfo>(
                    harness, nameof(NotifyChatBoardInfo), "chat board reward applied claim retry push");
                AssertEqual(second.EndTime, appliedRetryPush.ChatBoard.EndTime,
                    "chat board reward: applied claim retry replays the live expiry");
                AssertEqual(second.EndTime, Board(harness.Session.player, authored.TemplateId).EndTime,
                    "chat board reward: retrying an applied claim never extends the board");
                AssertEqual(2, playerSaves.ReplaceOneCalls, "chat board reward: applied claim retry saves nothing");
            }

            // Player write never lands: the durable Inventory receipt survives and the retry converges on its clock.
            using (MongoCollectionOverride failureMongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> failureSaves,
                out _,
                out RecordingMongoCollectionProxy<Inventory> failureInventories))
            {
                long failurePlayerId = playerId + 1;
                Player failurePlayer = CreateDrawCompatibilityPlayer(failurePlayerId);
                using LoopbackSessionHarness failureHarness = new(
                    CreateDrawCompatibilityCharacter(failurePlayerId), failurePlayer,
                    CreateDrawCompatibilityInventory(failurePlayerId, []), "chat-board-reward-save-failure");

                // The durable document still holds the pre-grant player, which proves the write never landed.
                failureSaves.FindResults = [BsonSerializer.Deserialize<Player>(failurePlayer.ToBson())];
                failureSaves.ThrowOnReplaceOne = true;
                Grant(failureHarness, "chat-board-reward:failed-save", expectFailure: true);
                failureSaves.ThrowOnReplaceOne = false;
                failureSaves.FindResults = null;
                AssertEqual(0, failurePlayer.UnlockedChatBoards.Count,
                    "chat board reward: failed Player save rolls the board back");
                AssertEqual(0, failurePlayer.ChatBoardRewardClaims.Count,
                    "chat board reward: failed Player save rolls the claim receipt back");
                AssertNoPacket(failureHarness, "chat board reward failed save");
                Inventory durableInventory = BsonSerializer.Deserialize<Inventory>(
                    failureInventories.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("chat board reward: expected a persisted inventory receipt."));
                AssertEqual(true, durableInventory.AppliedRewardClaims.Contains(
                        "chat-board-reward:failed-save", StringComparer.Ordinal),
                    "chat board reward: durable inventory receipt survives the Player failure");
                long durableGrantTime = durableInventory.RewardClaimTimes["chat-board-reward:failed-save"];

                Grant(failureHarness, "chat-board-reward:failed-save");
                ChatBoardUnlockState recovered = Board(failurePlayer, authored.TemplateId);
                AssertEqual(durableGrantTime, recovered.GetTime,
                    "chat board reward: recovery reuses the durable grant clock");
                AssertEqual(durableGrantTime + chatBoard.Duration, recovered.EndTime,
                    "chat board reward: recovery expiry matches the original grant clock");
                NotifyChatBoardInfo recoveryPush = ReadPushPayload<NotifyChatBoardInfo>(
                    failureHarness, nameof(NotifyChatBoardInfo), "chat board reward recovery push");
                AssertEqual(recovered.EndTime, recoveryPush.ChatBoard.EndTime, "chat board reward recovery push expiry");
                AssertEqual(2, failureSaves.ReplaceOneCalls, "chat board reward: recovery saves Player once");
            }

            // Acknowledgement loss: the write lands, the caller sees the failure, so the failed save must
            // adopt the committed ownership and the same-session retry must replay it without extending.
            using (MongoCollectionOverride ackMongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> ackSaves,
                out _,
                out RecordingMongoCollectionProxy<Inventory> ackInventories))
            {
                long ackPlayerId = playerId + 2;
                Player ackPlayer = CreateDrawCompatibilityPlayer(ackPlayerId);
                using LoopbackSessionHarness ackHarness = new(
                    CreateDrawCompatibilityCharacter(ackPlayerId), ackPlayer,
                    CreateDrawCompatibilityInventory(ackPlayerId, []), "chat-board-reward-ack-loss");

                // The grant clock is durable in the Inventory receipt before the Player write, so a fixed
                // receipt clock fixes the ownership the landed acknowledgement must contain.
                const string ackClaim = "chat-board-reward:ack-loss";
                long ackClock = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
                long ackEndTime = ackClock + chatBoard.Duration;
                ackHarness.Session.inventory.RewardClaimTimes[ackClaim] = ackClock;
                Player committed = BsonSerializer.Deserialize<Player>(ackPlayer.ToBson());
                committed.UnlockedChatBoards.Add(new ChatBoardUnlockState
                {
                    Id = authored.TemplateId,
                    GetTime = ackClock,
                    EndTime = ackEndTime
                });
                committed.ChatBoardRewardClaims.Add(ackClaim);
                ackSaves.FindResults = [committed];

                ackSaves.ThrowAfterReplaceOne = true;
                Grant(ackHarness, ackClaim, expectFailure: true);
                ackSaves.FindResults = null;
                AssertNoPacket(ackHarness, "chat board reward acknowledgement loss");

                Player landed = Reloaded(ackSaves.LastSuccessfulReplacementBson,
                    "chat board reward acknowledgement loss");
                ChatBoardUnlockState landedBoard = Board(landed, authored.TemplateId);
                AssertEqual(ackClock, landedBoard.GetTime,
                    "chat board reward: lost acknowledgement still landed the grant clock");
                AssertEqual(ackEndTime, landedBoard.EndTime,
                    "chat board reward: lost acknowledgement landed the authored expiry");
                AssertEqual(1, landed.ChatBoardRewardClaims.Count(claim => claim == ackClaim),
                    "chat board reward: landed acknowledgement records one claim receipt");
                ChatBoardUnlockState adopted = Board(ackHarness.Session.player, authored.TemplateId);
                AssertEqual(landedBoard.GetTime, adopted.GetTime,
                    "chat board reward: failed save adopts the committed grant clock");
                AssertEqual(landedBoard.EndTime, adopted.EndTime,
                    "chat board reward: failed save adopts the committed expiry");
                AssertEqual(1, ackHarness.Session.player.ChatBoardRewardClaims.Count(claim => claim == ackClaim),
                    "chat board reward: failed save adopts the committed claim receipt");

                // An unrelated save of the live player between the failure and the retry keeps the committed
                // ownership instead of writing a rolled-back snapshot over it.
                int beforeUnrelatedSave = ackSaves.ReplaceOneCalls;
                ackHarness.Session.player.PlayerData.Sign = "chat-board-ack-loss";
                ackHarness.Session.player.Save();
                AssertEqual(beforeUnrelatedSave + 1, ackSaves.ReplaceOneCalls,
                    "chat board reward: unrelated save writes the live player once");
                Player unrelated = Reloaded(ackSaves.LastSuccessfulReplacementBson,
                    "chat board reward unrelated save");
                AssertEqual(landedBoard.GetTime, Board(unrelated, authored.TemplateId).GetTime,
                    "chat board reward: unrelated save keeps the committed grant clock");
                AssertEqual(landedBoard.EndTime, Board(unrelated, authored.TemplateId).EndTime,
                    "chat board reward: unrelated save keeps the committed expiry");
                AssertEqual(1, unrelated.ChatBoardRewardClaims.Count(claim => claim == ackClaim),
                    "chat board reward: unrelated save keeps one claim receipt");

                // The same-claim retry replays the committed ownership to the client and saves nothing.
                int beforeRetry = ackSaves.ReplaceOneCalls;
                Grant(ackHarness, ackClaim);
                NotifyChatBoardInfo retryPush = ReadPushPayload<NotifyChatBoardInfo>(
                    ackHarness, nameof(NotifyChatBoardInfo), "chat board reward acknowledgement loss retry push");
                AssertEqual(landedBoard.GetTime, retryPush.ChatBoard.GetTime,
                    "chat board reward: ack-loss retry replays the committed grant clock");
                AssertEqual(landedBoard.EndTime, retryPush.ChatBoard.EndTime,
                    "chat board reward: ack-loss retry replays the committed expiry");
                ChatBoardUnlockState retried = Board(ackHarness.Session.player, authored.TemplateId);
                AssertEqual(landedBoard.GetTime, retried.GetTime,
                    "chat board reward: ack-loss retry reuses the landed grant clock");
                AssertEqual(landedBoard.EndTime, retried.EndTime,
                    "chat board reward: ack-loss retry keeps the landed expiry");
                AssertEqual(beforeRetry, ackSaves.ReplaceOneCalls,
                    "chat board reward: ack-loss retry saves nothing");
                Player retriedPlayer = Reloaded(ackSaves.LastSuccessfulReplacementBson,
                    "chat board reward ack-loss retry");
                AssertEqual(landedBoard.GetTime, Board(retriedPlayer, authored.TemplateId).GetTime,
                    "chat board reward: persisted ack-loss retry keeps the exact grant clock");
                AssertEqual(landedBoard.EndTime, Board(retriedPlayer, authored.TemplateId).EndTime,
                    "chat board reward: persisted ack-loss retry expiry is exact");
                AssertEqual(1, retriedPlayer.ChatBoardRewardClaims.Count(claim => claim == ackClaim),
                    "chat board reward: ack-loss retry keeps one claim receipt");
                Inventory retriedInventory = BsonSerializer.Deserialize<Inventory>(
                    ackInventories.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("chat board reward: expected a persisted inventory receipt."));
                AssertEqual(1, retriedInventory.AppliedRewardClaims.Count(claim => claim == ackClaim),
                    "chat board reward: ack-loss retry persists one inventory receipt");
                AssertEqual(ackClock, retriedInventory.RewardClaimTimes[ackClaim],
                    "chat board reward: ack-loss retry uses the durable claim clock");

                // BSON reload keeps the committed ownership: the retry replays the same expiry and saves nothing.
                ackHarness.Session.player = Reloaded(ackSaves.LastSuccessfulReplacementBson,
                    "chat board reward ack-loss reload");
                Grant(ackHarness, ackClaim);
                NotifyChatBoardInfo reloadedRetryPush = ReadPushPayload<NotifyChatBoardInfo>(
                    ackHarness, nameof(NotifyChatBoardInfo), "chat board reward ack-loss reloaded retry push");
                AssertEqual(landedBoard.GetTime, reloadedRetryPush.ChatBoard.GetTime,
                    "chat board reward: reloaded ack-loss retry replays the committed grant clock");
                AssertEqual(landedBoard.EndTime, reloadedRetryPush.ChatBoard.EndTime,
                    "chat board reward: reloaded ack-loss retry replays the committed expiry");
                AssertEqual(landedBoard.EndTime, Board(ackHarness.Session.player, authored.TemplateId).EndTime,
                    "chat board reward: reloaded ack-loss retry does not extend the board");
                AssertEqual(beforeRetry, ackSaves.ReplaceOneCalls,
                    "chat board reward: ack-loss reload retry saves nothing");

                // Login consumption: the client payload carries the granted grant clock and expiry.
                NotifyChatBoardLoginData loginData = RequiredMethod(
                        RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
                        "BuildChatBoardLoginData", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Player)])
                    .Invoke(null, [ackHarness.Session.player]) as NotifyChatBoardLoginData
                    ?? throw new InvalidDataException("AccountModule.BuildChatBoardLoginData returned nil.");
                NotifyChatBoardLoginData.NotifyChatBoardLoginDataChatBoard loginBoard =
                    loginData.ChatBoards.Single(entry => entry.Id == authored.TemplateId);
                AssertEqual(retried.GetTime, loginBoard.GetTime, "chat board reward: login payload grant clock");
                AssertEqual(retried.EndTime, loginBoard.EndTime, "chat board reward: login payload expiry");
            }

            // Unresolved durable read: the failed write cannot be settled, so the session is closed without
            // persisting the possibly-diverging player.
            using (MongoCollectionOverride unresolvedMongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> unresolvedSaves, out _, out _))
            {
                long unresolvedPlayerId = playerId + 3;
                Player unresolvedPlayer = CreateDrawCompatibilityPlayer(unresolvedPlayerId);
                using LoopbackSessionHarness unresolvedHarness = new(
                    CreateDrawCompatibilityCharacter(unresolvedPlayerId), unresolvedPlayer,
                    CreateDrawCompatibilityInventory(unresolvedPlayerId, []), "chat-board-reward-unresolved");

                AssertEqual(true,
                    Server.Instance.Sessions.TryAdd(unresolvedHarness.Session.id, unresolvedHarness.Session),
                    "chat board reward: unresolved session registers");
                // The write never lands and the durable document cannot be read either: unresolved.
                unresolvedSaves.ThrowOnReplaceOne = true;
                Grant(unresolvedHarness, "chat-board-reward:unresolved", expectFailure: true);
                AssertEqual(false, Server.Instance.Sessions.ContainsKey(unresolvedHarness.Session.id),
                    "chat board reward: unresolved save closes the session");
                AssertEqual(1, unresolvedSaves.ReplaceOneCalls,
                    "chat board reward: unresolved save attempts one Player write");
                AssertEqual(null, unresolvedSaves.LastSuccessfulReplacementBson,
                    "chat board reward: unresolved session close persists nothing");
            }
        }
    }
}
