using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.chat;
using AscNet.Table.V2.share.fuben.transfinitetower;
using AscNet.Table.V2.share.reward;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateTransfiniteTowerRanking()
    {
        PacketFactory.LoadPacketHandlers();
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TransfiniteTowerModule");
        FieldInfo clockField = module.GetField("Clock", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo publish = module.GetMethod("TryPublishRank", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo resume = module.GetMethod("ResumeRankReward", BindingFlags.NonPublic | BindingFlags.Static)!;
        object originalClock = clockField.GetValue(null)!;
        IMongoCollection<Player> originalPlayers = Player.collection;
        IMongoCollection<TransfiniteTowerRankEntry> originalRanks = TransfiniteTowerRankEntry.collection;
        IMongoCollection<Player> playerStore = DispatchProxy.Create<IMongoCollection<Player>, RecordingMongoCollectionProxy<Player>>();
        RecordingMongoCollectionProxy<Player> players = (RecordingMongoCollectionProxy<Player>)(object)playerStore;
        IMongoCollection<TransfiniteTowerRankEntry> rankStore =
            DispatchProxy.Create<IMongoCollection<TransfiniteTowerRankEntry>, TowerRankMemoryCollection>();
        TowerRankMemoryCollection ranks = (TowerRankMemoryCollection)(object)rankStore;
        Player.collection = playerStore;
        TransfiniteTowerRankEntry.collection = rankStore;
        try
        {
            TransfiniteTowerActivityTable activity = TableReaderV2.Parse<TransfiniteTowerActivityTable>().Single();
            TransfiniteTowerChapterTable chapter = TableReaderV2.Parse<TransfiniteTowerChapterTable>()
                .First(row => row.IsRank == 1 && activity.ChapterIds.Contains(row.Id));
            int ordinaryChapterId = activity.ChapterIds.First(id => id != chapter.Id);
            AssertEqual(true, ActivityScheduleService.TryGet(activity.TimeId, out ActivityScheduleEntry play), "tower play calendar");
            AssertEqual(true, ActivityScheduleService.TryGet(activity.RankRewardTimeId, out ActivityScheduleEntry reward), "tower rank reward calendar");
            long expire = long.Parse(TableReaderV2.Parse<TransfiniteTowerConfigTable>()
                .Single(row => row.Key == "RankRewardExpireInterval").Values[0]);
            void SetClock(long seconds) => clockField.SetValue(null, (Func<DateTimeOffset>)(() => DateTimeOffset.FromUnixTimeSeconds(seconds)));
            (bool Ok, int Rank, int Total) Publish(Session session)
            {
                object?[] args = [session, chapter.Id, 0, 0];
                bool ok = (bool)publish.Invoke(null, args)!;
                return (ok, (int)args[2]!, (int)args[3]!);
            }
            SetClock(play.StartTime + 60);

            const long idA = 48_960, idB = 48_961, idC = 48_962;
            using LoopbackSessionHarness a = TowerRankHarness(idA, chapter.Id, 10, 300, 1_000, (1021, false, 9_000), (7001, true, 7_000));
            using LoopbackSessionHarness b = TowerRankHarness(idB, chapter.Id, 10, 300, 900, (1031, false, 8_000), (1041, false, 6_000));
            using LoopbackSessionHarness c = TowerRankHarness(idC, chapter.Id, 10, 300, 1_000, (1051, false, 5_000));

            // Ties: earlier achievement first, then lower player id; publication order is irrelevant.
            Publish(a.Session);
            Publish(c.Session);
            Publish(b.Session);
            AssertEqual((true, 2, 3), Publish(a.Session), "A behind earlier B, ahead of same-time higher-id C");
            AssertEqual((true, 1, 3), Publish(b.Session), "B first by earlier achievement");
            AssertEqual((true, 3, 3), Publish(c.Session), "C last by player id tie-break");
            int writes = ranks.Writes;
            Publish(a.Session);
            AssertEqual(writes, ranks.Writes, "republishing an unchanged best performs no write");

            // A stale/worse persisted build never regresses the stored best.
            TransfiniteTowerRankInfo aInfo = a.Session.player.TransfiniteTower!.ChapterInfoList[0].RankInfo!;
            (aInfo.MaxOrder, aInfo.TotalSpendTime, aInfo.AchievedAt) = (9, 100, 2_000);
            AssertEqual((true, 2, 3), Publish(a.Session), "worse build keeps prior standing");
            AssertEqual(10, ranks.Get(idA).MaxOrder, "stored best order preserved");
            AssertEqual(300, ranks.Get(idA).TotalSpendTime, "stored best time preserved");

            // A genuinely better persisted build replaces the entry with its snapshot.
            a.Session.player.TransfiniteTower = TowerRankState(chapter.Id, 11, 500, 3_000, (1021, false, 9_500), (7002, true, 7_100));
            AssertEqual((true, 1, 3), Publish(a.Session), "higher order outranks faster lower order");

            int packetId = 48_960;
            InvokeRegisteredRequestHandler(nameof(TransfiniteTowerGetRankRequest), a.Session, ++packetId,
                new TransfiniteTowerGetRankRequest { ChapterId = chapter.Id });
            TransfiniteTowerGetRankResponse board = ReadResponsePayload<TransfiniteTowerGetRankResponse>(
                a, packetId, nameof(TransfiniteTowerGetRankResponse), "tower rank query");
            AssertEqual(0, board.Code, "rank query code");
            AssertEqual("48960,48961,48962", string.Join(",", board.RankList.Select(show => show.Id)), "rank list order");
            AssertEqual("1,2,3", string.Join(",", board.RankList.Select(show => show.RankNum)), "rank numbers");
            AssertEqual((1, 3), (board.Rank, board.TotalCount), "own rank and total");
            AssertEqual(300, board.LastRankTotalSpendTime, "last eligible entry time (fewer entries than threshold)");
            AssertEqual("1021:False:9500,7002:True:7100", string.Join(",", board.RankList[0].Characters.Select(x => $"{x.FightId}:{x.IsTrial}:{x.Power}")),
                "A's persisted better build snapshot");
            AssertEqual("1031:False:8000,1041:False:6000", string.Join(",", board.RankList[1].Characters.Select(x => $"{x.FightId}:{x.IsTrial}:{x.Power}")),
                "B's distinct build snapshot");
            AssertEqual(16_600, board.RankList[0].TotalPower, "server-derived total power");

            InvokeRegisteredRequestHandler(nameof(TransfiniteTowerGetRankRequest), a.Session, ++packetId,
                new TransfiniteTowerGetRankRequest { ChapterId = ordinaryChapterId });
            AssertEqual(20431011, ReadResponsePayload<TransfiniteTowerGetRankResponse>(a, packetId,
                nameof(TransfiniteTowerGetRankResponse), "non-rank chapter").Code, "non-rank chapter has no board");

            // MVP: must be in the settled roster, requires CanSetMvp, survives persistence, updates the entry.
            int SetMvp(LoopbackSessionHarness harness, int fightId)
            {
                InvokeRegisteredRequestHandler(nameof(TransfiniteTowerSetMvpRequest), harness.Session, ++packetId,
                    new TransfiniteTowerSetMvpRequest { ChapterId = chapter.Id, MvpFightId = fightId });
                return ReadResponsePayload<TransfiniteTowerSetMvpResponse>(harness, packetId,
                    nameof(TransfiniteTowerSetMvpResponse), "tower set mvp", maxPacketsToRead: 2).Code;
            }
            AssertEqual(20431010, SetMvp(a, 1031), "MVP outside roster rejected");
            // Storage proves the write never landed: the durable pre-request MVP replaces the staged one.
            players.FindResults = [BsonSerializer.Deserialize<Player>(a.Session.player.ToBson())];
            players.ThrowOnReplaceOne = true;
            AssertEqual(2, SetMvp(a, 7002), "MVP persistence failure reported");
            players.ThrowOnReplaceOne = false;
            players.FindResults = null;
            AssertEqual(1021, a.Session.player.TransfiniteTower!.ChapterInfoList[0].RankInfo!.MvpFightId, "failed MVP write rolled back");
            AssertEqual(1021, a.Session.player.TransfiniteTower.ChapterInfoList[0].SettleInfo!.MvpFightId, "failed MVP write rolls back the settle MVP too");

            // A lost acknowledgement instead: storage shows the exact staged document, so the pick is adopted
            // and republished rather than rolled back to the stale MVP.
            players.BeforeReplaceOne = document =>
                players.FindResults = [BsonSerializer.Deserialize<Player>(document.ToBson())];
            players.ThrowAfterReplaceOne = true;
            AssertEqual(0, SetMvp(a, 7002), "MVP acknowledgement loss is reconciled as committed");
            players.BeforeReplaceOne = null;
            players.FindResults = null;
            AssertEqual(false, players.ThrowAfterReplaceOne, "the MVP acknowledgement loss fires once");
            AssertEqual(7002, a.Session.player.TransfiniteTower!.ChapterInfoList[0].SettleInfo!.MvpFightId,
                "a lost MVP acknowledgement adopts the committed settle MVP");
            AssertEqual(7002, a.Session.player.TransfiniteTower.ChapterInfoList[0].RankInfo!.MvpFightId,
                "a lost MVP acknowledgement adopts the committed rank MVP");
            AssertEqual(0, SetMvp(a, 7002), "trial member eligible as MVP");
            Player persistedA = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
            AssertEqual(7002, persistedA.TransfiniteTower!.ChapterInfoList[0].SettleInfo!.MvpFightId, "persisted settle MVP");
            AssertEqual(7002, persistedA.TransfiniteTower.ChapterInfoList[0].RankInfo!.MvpFightId, "persisted rank MVP");
            AssertEqual(7002, ranks.Get(idA).MvpFightId, "published entry MVP");
            AssertEqual(11, ranks.Get(idA).MaxOrder, "MVP update keeps best result");
            b.Session.player.TransfiniteTower!.ChapterInfoList[0].CanSetMvp = false;
            AssertEqual(20431010, SetMvp(b, 1041), "MVP locked after non-record settle");

            // Rewards are not decided while gameplay is open.
            resume.Invoke(null, [a.Session]);
            AssertEqual(0, a.Session.player.TransfiniteTowerRankRewards.Count, "no rank reward during play window");

            long decidedAt = reward.StartTime + 60;
            SetClock(decidedAt);
            AssertEqual(false, ActivityScheduleService.IsOpen(activity.TimeId, DateTimeOffset.FromUnixTimeSeconds(decidedAt)),
                "gameplay closed during reward window");
            Dictionary<int, ChatBoardTable> chatBoards = TableReaderV2.Parse<ChatBoardTable>().ToDictionary(row => row.Id);
            List<RewardGoodsTable> rewardGoodsRows = TableReaderV2.Parse<RewardGoodsTable>();
            var expectedGoods = chapter.RankRewardIds.Where(id => id > 0)
                .SelectMany(id => ResolveRewardGoods(id, rewardGoodsRows, "tower rank reward")).ToList();
            AssertEqual(true, expectedGoods.Count > 0, "authored rank reward goods");
            RewardGoodsTable authoredBoard = expectedGoods.Single(goods => chatBoards.ContainsKey(goods.TemplateId));
            long boardEndTime = decidedAt + chatBoards[authoredBoard.TemplateId].Duration;
            // The document a committed decision must contain: the frozen receipt plus the authored unlock window.
            Player Committed(Player live, int rank, bool delivered)
            {
                Player clone = BsonSerializer.Deserialize<Player>(live.ToBson());
                clone.TransfiniteTowerRankRewards.RemoveAll(value =>
                    value.ActivityId == activity.Id && value.ChapterId == chapter.Id);
                clone.TransfiniteTowerRankRewards.Add(new TransfiniteTowerRankRewardReceipt
                {
                    ActivityId = activity.Id, ChapterId = chapter.Id, Rank = rank,
                    RewardIds = [.. chapter.RankRewardIds.Where(id => id > 0)], DecidedAt = decidedAt, Delivered = delivered
                });
                clone.UnlockedChatBoards.RemoveAll(board => board.Id == authoredBoard.TemplateId);
                clone.UnlockedChatBoards.Add(new ChatBoardUnlockState
                {
                    Id = authoredBoard.TemplateId, GetTime = decidedAt, EndTime = boardEndTime
                });
                return clone;
            }

            // Decision write fails and storage proves nothing landed: no receipt and no board may survive.
            players.FindResults = [BsonSerializer.Deserialize<Player>(a.Session.player.ToBson())];
            players.ThrowOnReplaceOne = true;
            resume.Invoke(null, [a.Session]);
            players.ThrowOnReplaceOne = false;
            players.FindResults = null;
            AssertEqual(0, a.Session.player.TransfiniteTowerRankRewards.Count, "failed decision leaves no receipt");
            AssertEqual(0, a.Session.player.UnlockedChatBoards.Count, "failed decision grants nothing");

            // Decision persists, delivery acknowledgement fails: the landed decision is kept and retried once.
            int attempt = 0;
            players.BeforeReplaceOne = _ => players.ThrowOnReplaceOne = ++attempt == 2;
            players.FindResults = [Committed(a.Session.player, rank: 1, delivered: false)];
            resume.Invoke(null, [a.Session]);
            players.BeforeReplaceOne = null;
            players.ThrowOnReplaceOne = false;
            players.FindResults = null;
            Player reloaded = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
            AssertEqual(1, reloaded.TransfiniteTowerRankRewards.Count, "decided receipt persisted");
            AssertEqual(false, reloaded.TransfiniteTowerRankRewards[0].Delivered, "delivery still owed after partial write");
            AssertEqual(1, reloaded.TransfiniteTowerRankRewards[0].Rank, "receipt freezes decided rank");
            AssertEqual(decidedAt, reloaded.TransfiniteTowerRankRewards[0].DecidedAt, "receipt freezes the decision clock");
            AssertEqual(boardEndTime, reloaded.UnlockedChatBoards.Single().EndTime, "board keeps the authored expiry");
            string boardsAfterDecision = string.Join(",", reloaded.UnlockedChatBoards.Select(x => $"{x.Id}:{x.EndTime}"));

            // A retry re-announces the durable board before delivering, without extending it.
            using LoopbackSessionHarness relogin = new(CreateDrawCompatibilityCharacter(idA), reloaded,
                CreateDrawCompatibilityInventory(idA, []), "transfinite-tower-rank-relogin");
            resume.Invoke(null, [relogin.Session]);
            NotifyChatBoardInfo reloginBoard = ReadPushPayload<NotifyChatBoardInfo>(
                relogin, nameof(NotifyChatBoardInfo), "tower rank board push", maxPacketsToRead: 2);
            AssertEqual($"{authoredBoard.TemplateId}:{boardEndTime}",
                $"{reloginBoard.ChatBoard.Id}:{reloginBoard.ChatBoard.EndTime}",
                "retry announces the durable board without extending it");
            NotifyTransfiniteTowerRankReward notify = ReadPushPayload<NotifyTransfiniteTowerRankReward>(
                relogin, nameof(NotifyTransfiniteTowerRankReward), "tower rank reward push", maxPacketsToRead: 2);
            AssertEqual(string.Join(",", expectedGoods.Select(x => x.TemplateId)),
                string.Join(",", notify.RewardGoodsList.Select(x => x.TemplateId)), "pushed authored goods");
            AssertEqual(true, relogin.Session.player.TransfiniteTowerRankRewards[0].Delivered, "delivery completed on login");
            AssertEqual(boardsAfterDecision, string.Join(",", relogin.Session.player.UnlockedChatBoards.Select(x => $"{x.Id}:{x.EndTime}")),
                "retry does not grant twice");
            int playerWrites = players.ReplaceOneCalls;
            resume.Invoke(null, [relogin.Session]);
            AssertEqual(playerWrites, players.ReplaceOneCalls, "delivered receipt is terminal");

            // Decision acknowledgement loss: the committed decision and board landed, so the failed save must
            // adopt them (never the pre-request snapshot) and a later unrelated save must keep them.
            const long idD = 48_963;
            using LoopbackSessionHarness d = TowerRankHarness(idD, chapter.Id, 11, 400, 4_000, (1_061, false, 9_100));
            (bool dOk, int dRank, _) = Publish(d.Session);
            AssertEqual(true, dOk, "acknowledgement-loss rank publish");
            AssertEqual(1, dRank, "acknowledgement-loss build ranks first");
            players.FindResults = [Committed(d.Session.player, dRank, delivered: false)];
            byte[]? committedDecision = null;
            players.BeforeReplaceOne = document => committedDecision ??= document.ToBson();
            players.ThrowAfterReplaceOne = true;
            resume.Invoke(null, [d.Session]);
            players.BeforeReplaceOne = null;
            players.FindResults = null;
            AssertEqual(false, players.ThrowAfterReplaceOne, "decision acknowledgement loss fires once");
            Player landed = BsonSerializer.Deserialize<Player>(
                committedDecision ?? throw new InvalidDataException("expected the committed decision write."));
            AssertEqual(decidedAt, landed.TransfiniteTowerRankRewards.Single().DecidedAt, "committed decision landed with its clock");
            AssertEqual(1, landed.TransfiniteTowerRankRewards.Single().Rank, "committed decision landed with its rank");
            AssertEqual(false, landed.TransfiniteTowerRankRewards.Single().Delivered, "committed decision landed undelivered");
            AssertEqual(boardEndTime, landed.UnlockedChatBoards.Single(board => board.Id == authoredBoard.TemplateId).EndTime,
                "committed board landed with the authored expiry");
            AssertEqual(decidedAt, d.Session.player.TransfiniteTowerRankRewards.Single().DecidedAt,
                "failed decision save adopts the committed decision");
            AssertEqual(boardEndTime, d.Session.player.UnlockedChatBoards.Single(board => board.Id == authoredBoard.TemplateId).EndTime,
                "failed decision save adopts the committed expiry");
            NotifyChatBoardInfo adoptedBoard = ReadPushPayload<NotifyChatBoardInfo>(
                d, nameof(NotifyChatBoardInfo), "tower rank board push after reconciliation");
            AssertEqual(boardEndTime, adoptedBoard.ChatBoard.EndTime, "reconciled board push carries the committed expiry");
            NotifyTransfiniteTowerRankReward adoptedReward = ReadPushPayload<NotifyTransfiniteTowerRankReward>(
                d, nameof(NotifyTransfiniteTowerRankReward), "tower rank reward push after reconciliation");
            AssertEqual(string.Join(",", expectedGoods.Select(x => x.TemplateId)),
                string.Join(",", adoptedReward.RewardGoodsList.Select(x => x.TemplateId)), "reconciled run announces the authored goods");
            AssertEqual(true, d.Session.player.TransfiniteTowerRankRewards.Single().Delivered, "reconciled run completes the delivery");
            d.Session.player.PlayerData.Sign = "transfinite-tower-ack-loss";
            d.Session.player.Save();
            Player afterDecisionLoss = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
            AssertEqual(decidedAt, afterDecisionLoss.TransfiniteTowerRankRewards.Single().DecidedAt,
                "unrelated save keeps the committed decision");
            AssertEqual(true, afterDecisionLoss.TransfiniteTowerRankRewards.Single().Delivered,
                "unrelated save keeps the terminal receipt");
            AssertEqual(boardEndTime, afterDecisionLoss.UnlockedChatBoards.Single(board => board.Id == authoredBoard.TemplateId).EndTime,
                "unrelated save keeps the committed expiry");

            // Delivery acknowledgement loss: the landed flag must stay terminal and the retry must replay the
            // durable board instead of extending it.
            const long idE = 48_964;
            using LoopbackSessionHarness e = TowerRankHarness(idE, chapter.Id, 11, 450, 5_000, (1_071, false, 9_200));
            (bool eOk, int eRank, _) = Publish(e.Session);
            AssertEqual(true, eOk, "delivery-acknowledgement rank publish");
            e.Session.player.TransfiniteTowerRankRewards =
            [
                new TransfiniteTowerRankRewardReceipt
                {
                    ActivityId = activity.Id, ChapterId = chapter.Id, Rank = eRank,
                    RewardIds = [.. chapter.RankRewardIds.Where(id => id > 0)], DecidedAt = decidedAt
                }
            ];
            e.Session.player.UnlockedChatBoards =
            [
                new ChatBoardUnlockState { Id = authoredBoard.TemplateId, GetTime = decidedAt, EndTime = boardEndTime }
            ];
            players.FindResults = [Committed(e.Session.player, eRank, delivered: true)];
            players.ThrowAfterReplaceOne = true;
            resume.Invoke(null, [e.Session]);
            players.FindResults = null;
            AssertEqual(false, players.ThrowAfterReplaceOne, "delivery acknowledgement loss fires once");
            NotifyChatBoardInfo retriedBoard = ReadPushPayload<NotifyChatBoardInfo>(
                e, nameof(NotifyChatBoardInfo), "tower rank board push on retry");
            AssertEqual(decidedAt, retriedBoard.ChatBoard.GetTime, "retry replays the durable grant clock");
            AssertEqual(boardEndTime, retriedBoard.ChatBoard.EndTime, "retry does not extend the authored expiry");
            NotifyTransfiniteTowerRankReward retriedReward = ReadPushPayload<NotifyTransfiniteTowerRankReward>(
                e, nameof(NotifyTransfiniteTowerRankReward), "tower rank reward push on retry");
            AssertEqual(string.Join(",", expectedGoods.Select(x => x.TemplateId)),
                string.Join(",", retriedReward.RewardGoodsList.Select(x => x.TemplateId)), "retry announces the authored goods");
            AssertEqual(true, e.Session.player.TransfiniteTowerRankRewards.Single().Delivered,
                "a landed delivery acknowledgement keeps the receipt terminal");
            e.Session.player.PlayerData.Sign = "transfinite-tower-delivery-ack-loss";
            e.Session.player.Save();
            Player afterDeliveryLoss = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
            AssertEqual(true, afterDeliveryLoss.TransfiniteTowerRankRewards.Single().Delivered,
                "unrelated save keeps the landed delivery");
            AssertEqual(boardEndTime, afterDeliveryLoss.UnlockedChatBoards.Single(board => board.Id == authoredBoard.TemplateId).EndTime,
                "unrelated save keeps the replayed expiry");
            int terminalWrites = players.ReplaceOneCalls;
            resume.Invoke(null, [e.Session]);
            AssertEqual(terminalWrites, players.ReplaceOneCalls, "acknowledged receipt stays terminal");

            // Unresolved write: the durable document cannot be read, so the session must be closed without
            // persisting the possibly stale player document.
            const long idF = 48_965;
            using LoopbackSessionHarness f = TowerRankHarness(idF, chapter.Id, 12, 420, 4_500, (1_081, false, 9_300));
            (bool fOk, int fRank, _) = Publish(f.Session);
            AssertEqual(true, fOk, "unresolved-write rank publish");
            AssertEqual(1, fRank, "unresolved-write build ranks first");
            AssertEqual(true, Server.Instance.Sessions.TryAdd(f.Session.id, f.Session), "unresolved-write session registered");
            int unresolvedWrites = players.ReplaceOneCalls;
            byte[]? lastLandedWrite = players.LastSuccessfulReplacementBson;
            players.FindResults = null;
            players.ThrowOnReplaceOne = true;
            resume.Invoke(null, [f.Session]);
            players.ThrowOnReplaceOne = false;
            AssertEqual(false, Server.Instance.Sessions.ContainsKey(f.Session.id),
                "an unreadable durable document closes the session");
            AssertEqual(unresolvedWrites + 1, players.ReplaceOneCalls,
                "the failed write is the only replacement; the unresolved close persists nothing");
            AssertEqual(lastLandedWrite is null, players.LastSuccessfulReplacementBson is null,
                "the unresolved close lands no replacement");

            // The same unresolved boundary on a non-reward tower mutation (MVP): the durable document cannot
            // be read, so the session closes and the possibly stale player document is never persisted.
            SetClock(play.StartTime + 60);
            const long idG = 48_966;
            using LoopbackSessionHarness g = TowerRankHarness(idG, chapter.Id, 12, 460, 5_500,
                (1_091, false, 9_400), (1_092, false, 9_300));
            // An unranked best keeps this scenario off the rank board.
            g.Session.player.TransfiniteTower!.ChapterInfoList[0].RankInfo = null;
            AssertEqual(0, SetMvp(g, 1_092), "unresolved-mutation MVP baseline");
            AssertEqual(true, Server.Instance.Sessions.TryAdd(g.Session.id, g.Session), "unresolved-mutation session registered");
            int unresolvedMutations = players.ReplaceOneCalls;
            byte[] lastLandedMutation = players.LastSuccessfulReplacementBson!;
            players.FindResults = null;
            players.ThrowOnReplaceOne = true;
            bool mutationAborted = false;
            try
            {
                SetMvp(g, 1_091);
            }
            catch (InvalidDataException)
            {
                mutationAborted = true;
            }
            players.ThrowOnReplaceOne = false;
            AssertEqual(true, mutationAborted, "an unreadable durable document aborts the tower mutation");
            AssertEqual(false, Server.Instance.Sessions.ContainsKey(g.Session.id),
                "an unreadable durable document closes the tower session");
            AssertEqual(unresolvedMutations + 1, players.ReplaceOneCalls,
                "the failed tower mutation is the only replacement; the unresolved close persists nothing");
            AssertEqual(true, lastLandedMutation.SequenceEqual(players.LastSuccessfulReplacementBson!),
                "the unresolved tower close lands no replacement");

            // Undecided rewards expire with the authored interval.
            SetClock(Math.Min(reward.EndTime, reward.StartTime + expire) - 1 + 1);
            resume.Invoke(null, [c.Session]);
            AssertEqual(0, c.Session.player.TransfiniteTowerRankRewards.Count, "expired undecided reward not granted");
        }
        finally
        {
            clockField.SetValue(null, originalClock);
            Player.collection = originalPlayers;
            TransfiniteTowerRankEntry.collection = originalRanks;
        }
    }

    private static LoopbackSessionHarness TowerRankHarness(long playerId, int chapterId, int order, int time, long achievedAt,
        params (int FightId, bool IsTrial, int Power)[] build)
    {
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.TransfiniteTower = TowerRankState(chapterId, order, time, achievedAt, build);
        return new LoopbackSessionHarness(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, []), $"transfinite-tower-rank-{playerId}");
    }

    private static TransfiniteTowerState TowerRankState(int chapterId, int order, int time, long achievedAt,
        params (int FightId, bool IsTrial, int Power)[] build)
    {
        List<TransfiniteTowerSettleCharacter> Characters() => build.Select(x => new TransfiniteTowerSettleCharacter
        {
            FightId = x.FightId, IsTrial = x.IsTrial, Quality = 5, Power = x.Power
        }).ToList();
        int power = build.Sum(x => x.Power);
        return new TransfiniteTowerState
        {
            ChapterInfoList =
            [
                new TransfiniteTowerChapterInfo
                {
                    ChapterId = chapterId, CanSetMvp = true, BestOrder = order, BestTotalSpendTime = time, MaxPassedOrder = order,
                    SettleInfo = new TransfiniteTowerSettleInfo
                    {
                        Order = order, TotalSpendTime = time, TotalPower = power, Characters = Characters(),
                        MvpFightId = build[0].FightId, IsNewRecord = true, AchievedAt = achievedAt
                    },
                    RankInfo = new TransfiniteTowerRankInfo
                    {
                        MaxOrder = order, TotalSpendTime = time, TotalPower = power, MvpFightId = build[0].FightId,
                        Characters = Characters(), AchievedAt = achievedAt
                    }
                }
            ]
        };
    }

    // In-memory rank collection evaluating the module's real rendered filters/sorts.
    private class TowerRankMemoryCollection : DispatchProxy
    {
        private readonly Dictionary<string, BsonDocument> rows = new();
        public int Writes { get; private set; }

        public TransfiniteTowerRankEntry Get(long playerId) =>
            BsonSerializer.Deserialize<TransfiniteTowerRankEntry>(rows.Values.Single(row => row["player_id"].ToInt64() == playerId));

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            args ??= [];
            IBsonSerializerRegistry registry = BsonSerializer.SerializerRegistry;
            IBsonSerializer<TransfiniteTowerRankEntry> serializer = registry.GetSerializer<TransfiniteTowerRankEntry>();
            BsonDocument? query = args.OfType<FilterDefinition<TransfiniteTowerRankEntry>>().FirstOrDefault()?.Render(serializer, registry);
            IEnumerable<BsonDocument> matches = rows.Values.Where(row => query is null || Matches(row, query)).ToList();
            switch (method?.Name)
            {
                case nameof(IMongoCollection<TransfiniteTowerRankEntry>.FindSync):
                {
                    FindOptions<TransfiniteTowerRankEntry, TransfiniteTowerRankEntry>? options =
                        args.OfType<FindOptions<TransfiniteTowerRankEntry, TransfiniteTowerRankEntry>>().SingleOrDefault();
                    BsonDocument? sort = options?.Sort?.Render(serializer, registry);
                    if (sort is not null)
                        matches = matches.Order(Comparer<BsonDocument>.Create((left, right) =>
                        {
                            foreach (BsonElement key in sort)
                            {
                                int compared = left[key.Name].CompareTo(right[key.Name]);
                                if (compared != 0)
                                    return key.Value.ToInt32() < 0 ? -compared : compared;
                            }
                            return 0;
                        }));
                    matches = matches.Skip(options?.Skip ?? 0);
                    if (options?.Limit is int limit)
                        matches = matches.Take(limit);
                    return new StaticAsyncCursor<TransfiniteTowerRankEntry>(
                        matches.Select(row => BsonSerializer.Deserialize<TransfiniteTowerRankEntry>(row)).ToList());
                }
                case nameof(IMongoCollection<TransfiniteTowerRankEntry>.CountDocuments):
                    return (long)matches.Count();
                case nameof(IMongoCollection<TransfiniteTowerRankEntry>.ReplaceOne):
                {
                    TransfiniteTowerRankEntry document = args.OfType<TransfiniteTowerRankEntry>().Single();
                    BsonDocument? hit = matches.FirstOrDefault();
                    if (hit is not null)
                    {
                        rows[hit["_id"].AsString] = document.ToBsonDocument();
                        Writes++;
                        return new ReplaceOneResult.Acknowledged(1, 1, null);
                    }
                    if (args.OfType<ReplaceOptions>().SingleOrDefault()?.IsUpsert != true || rows.ContainsKey(document.Id))
                        return new ReplaceOneResult.Acknowledged(0, 0, null);
                    rows[document.Id] = document.ToBsonDocument();
                    Writes++;
                    return new ReplaceOneResult.Acknowledged(0, 0, document.Id);
                }
            }
            throw new NotSupportedException($"Tower rank test collection does not support {method?.Name}.");
        }

        private static bool Matches(BsonDocument row, BsonDocument query) => query.All(element => element.Name switch
        {
            "$and" => element.Value.AsBsonArray.All(value => Matches(row, value.AsBsonDocument)),
            "$or" => element.Value.AsBsonArray.Any(value => Matches(row, value.AsBsonDocument)),
            _ => MatchesField(row.Contains(element.Name) ? row[element.Name] : null, element.Value)
        });

        private static bool MatchesField(BsonValue? value, BsonValue condition)
        {
            if (condition is not BsonDocument operators || operators.ElementCount == 0 || !operators.GetElement(0).Name.StartsWith('$'))
                return value is not null && value.CompareTo(condition) == 0;
            return operators.All(op => op.Name switch
            {
                "$exists" => (value is not null) == op.Value.ToBoolean(),
                "$eq" => value is not null && value.CompareTo(op.Value) == 0,
                "$gt" => value is not null && value.CompareTo(op.Value) > 0,
                "$gte" => value is not null && value.CompareTo(op.Value) >= 0,
                "$lt" => value is not null && value.CompareTo(op.Value) < 0,
                "$lte" => value is not null && value.CompareTo(op.Value) <= 0,
                _ => throw new NotSupportedException($"Tower rank test filter operator {op.Name}.")
            });
        }
    }
}
