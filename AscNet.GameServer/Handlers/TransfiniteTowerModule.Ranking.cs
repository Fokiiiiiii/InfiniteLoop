using System.Globalization;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben.transfinitetower;
using AscNet.Table.V2.share.reward;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

// AscNet policy (no retail server rules exist): live server-local board of each player's persisted best
// eligible build, ordered MaxOrder desc, TotalSpendTime asc, AchievedAt asc, PlayerId asc. Rank rewards are
// decided once inside the authored RankRewardTimeId window (capped by RankRewardExpireInterval) after the
// activity closes, and delivered through durable receipts resumed at login.
internal static partial class TransfiniteTowerModule
{
    private const int RankActivityNotOpenCode = 20431001;
    private const int RankMvpInvalidCode = 20431010;
    private const int RankNotAvailableCode = 20431011;
    private const int RankNotSettledCode = 20431014;
    private const int RankServerInternalErrorCode = 2;
    // ponytail: AscNet list size; retail list size is unknown, raise if the client ever pages further.
    private const int RankListLimit = 100;

    private sealed record RankBoard(TransfiniteTowerActivityTable Activity, TransfiniteTowerChapterTable Chapter);

    private static readonly Lazy<List<TransfiniteTowerActivityTable>> RankActivities = new(() =>
        TableReaderV2.Parse<TransfiniteTowerActivityTable>());

    // Lua GetRankChapterId: the activity's chapters flagged IsRank.
    private static readonly Lazy<Dictionary<int, RankBoard>> RankBoards = new(() =>
    {
        Dictionary<int, TransfiniteTowerChapterTable> chapters = TableReaderV2.Parse<TransfiniteTowerChapterTable>()
            .ToDictionary(row => row.Id);
        Dictionary<int, RankBoard> boards = new();
        foreach (TransfiniteTowerActivityTable activity in RankActivities.Value)
            foreach (int chapterId in activity.ChapterIds)
                if (chapters.TryGetValue(chapterId, out TransfiniteTowerChapterTable? chapter) && chapter.IsRank == 1)
                    boards.TryAdd(chapterId, new RankBoard(activity, chapter));
        return boards;
    });

    private static readonly Lazy<(int NeedRank, long ExpireInterval)> RankConfig = new(() =>
    {
        Dictionary<string, TransfiniteTowerConfigTable> rows = TableReaderV2.Parse<TransfiniteTowerConfigTable>()
            .ToDictionary(row => row.Key);
        return (int.Parse(rows["RankRewardNeedRank"].Values[0], CultureInfo.InvariantCulture),
            long.Parse(rows["RankRewardExpireInterval"].Values[0], CultureInfo.InvariantCulture));
    });

    [RequestPacketHandler("TransfiniteTowerGetRankRequest")]
    public static void GetRank(Session session, Packet.Request packet)
    {
        TransfiniteTowerGetRankRequest request = packet.Deserialize<TransfiniteTowerGetRankRequest>();
        if (!RankBoards.Value.TryGetValue(request.ChapterId, out RankBoard? board))
        {
            session.SendResponse(new TransfiniteTowerGetRankResponse { Code = RankNotAvailableCode }, packet.Id);
            return;
        }
        DateTimeOffset now = Clock();
        if (!ActivityScheduleService.IsOpen(board.Activity.TimeId, now)
            && !ActivityScheduleService.IsOpen(board.Activity.RankRewardTimeId, now))
        {
            session.SendResponse(new TransfiniteTowerGetRankResponse { Code = RankActivityNotOpenCode }, packet.Id);
            return;
        }
        if (!TryPublishRank(session, request.ChapterId, out int rank, out int totalCount))
        {
            session.SendResponse(new TransfiniteTowerGetRankResponse { Code = RankNotAvailableCode }, packet.Id);
            return;
        }

        try
        {
            FilterDefinition<TransfiniteTowerRankEntry> participants = BoardFilter(board);
            List<TransfiniteTowerRankEntry> leaders = Ordered(participants).Limit(RankListLimit).ToList();
            // LastRankTotalSpendTime: time of the last locally reward-eligible entry.
            int lastIndex = Math.Min(RankConfig.Value.NeedRank, totalCount) - 1;
            int lastRankTime = lastIndex < 0
                ? 0
                : Ordered(participants).Skip(lastIndex).Limit(1).FirstOrDefault()?.TotalSpendTime ?? 0;
            session.SendResponse(new TransfiniteTowerGetRankResponse
            {
                Code = 0,
                RankList = leaders.Select((entry, index) => new TransfiniteTowerRankShow
                {
                    Id = entry.PlayerId,
                    Name = entry.Name,
                    HeadPortraitId = entry.HeadPortraitId,
                    HeadFrameId = entry.HeadFrameId,
                    RankNum = index + 1,
                    MaxOrder = entry.MaxOrder,
                    TotalSpendTime = entry.TotalSpendTime,
                    TotalPower = entry.TotalPower,
                    MvpFightId = entry.MvpFightId,
                    Characters = entry.Characters
                }).ToList(),
                Rank = rank,
                TotalCount = totalCount,
                LastRankTotalSpendTime = lastRankTime
            }, packet.Id);
        }
        catch (Exception exception)
        {
            session.log.Error($"Failed to query TransfiniteTower rank: {exception}");
            session.SendResponse(new TransfiniteTowerGetRankResponse { Code = RankNotAvailableCode }, packet.Id);
        }
    }

    [RequestPacketHandler("TransfiniteTowerSetMvpRequest")]
    public static void SetMvp(Session session, Packet.Request packet)
    {
        TransfiniteTowerSetMvpRequest request = packet.Deserialize<TransfiniteTowerSetMvpRequest>();
        int code = SetMvpCore(session, request.ChapterId, request.MvpFightId);
        session.SendResponse(new TransfiniteTowerSetMvpResponse { Code = code }, packet.Id);
    }

    private static int SetMvpCore(Session session, int chapterId, int mvpFightId)
    {
        TransfiniteTowerActivityTable? activity = RankActivities.Value.FirstOrDefault(row => row.ChapterIds.Contains(chapterId));
        if (activity is null || !ActivityScheduleService.IsOpen(activity.TimeId, Clock()))
            return RankActivityNotOpenCode;
        TransfiniteTowerChapterInfo? chapter = session.player.TransfiniteTower?.ChapterInfoList
            .FirstOrDefault(info => info.ChapterId == chapterId);
        TransfiniteTowerSettleInfo? settle = chapter?.SettleInfo;
        if (chapter is null || settle is null)
            return RankNotSettledCode;
        if (!chapter.CanSetMvp || !settle.Characters.Any(character => character.FightId == mvpFightId))
            return RankMvpInvalidCode;

        TransfiniteTowerRankInfo? rankInfo = chapter.RankInfo;
        // Only the settle that produced the ranked best may move the ranked MVP.
        bool ranked = rankInfo is not null && rankInfo.AchievedAt == settle.AchievedAt;
        settle.MvpFightId = mvpFightId;
        if (ranked)
            rankInfo!.MvpFightId = mvpFightId;
        if (!TrySave(session))
            return RankServerInternalErrorCode;
        // Publication failure is recovered from the persisted RankInfo on the next publish.
        if (ranked)
            TryPublishRank(session, chapterId, out _, out _);
        SendChapterInfo(session, chapter);
        return 0;
    }

    /// Idempotently publishes the player's persisted best for a rank chapter and reads its local standing.
    /// Returns false only on storage failure (logged); rank/totalCount are then 0.
    internal static bool TryPublishRank(Session session, int chapterId, out int rank, out int totalCount)
    {
        rank = 0;
        totalCount = 0;
        if (!RankBoards.Value.TryGetValue(chapterId, out RankBoard? board))
            return true;
        try
        {
            TransfiniteTowerRankEntry? own = PublishRankEntry(session.player, board);
            FilterDefinition<TransfiniteTowerRankEntry> participants = BoardFilter(board);
            totalCount = ToCount(TransfiniteTowerRankEntry.collection.CountDocuments(participants));
            rank = own is null
                ? 0
                : ToCount(TransfiniteTowerRankEntry.collection.CountDocuments(
                    Builders<TransfiniteTowerRankEntry>.Filter.And(participants, BetterThan(own))) + 1);
            return true;
        }
        catch (Exception exception)
        {
            session.log.Error($"Failed to publish TransfiniteTower rank: {exception}");
            rank = 0;
            totalCount = 0;
            return false;
        }
    }

    /// Login entry (runs regardless of activity state): republishes persisted bests and resumes rank rewards.
    internal static void ResumeRankReward(Session session)
    {
        if (session.player.TransfiniteTower is null
            && (session.player.TransfiniteTowerRankRewards ??= new()).All(receipt => receipt.Delivered))
            return;
        DateTimeOffset now = Clock();
        foreach (RankBoard board in RankBoards.Value.Values)
        {
            if (!TryPublishRank(session, board.Chapter.Id, out int rank, out _))
                continue;
            try
            {
                DeliverRankReward(session, board, rank, now);
            }
            catch (Exception exception)
            {
                session.log.Error($"Failed to deliver TransfiniteTower rank reward: {exception}");
            }
        }
    }

    private static void DeliverRankReward(Session session, RankBoard board, int rank, DateTimeOffset now)
    {
        Player player = session.player;
        List<TransfiniteTowerRankRewardReceipt> receipts = player.TransfiniteTowerRankRewards ??= new();
        TransfiniteTowerRankRewardReceipt? receipt = receipts.FirstOrDefault(value =>
            value.ActivityId == board.Activity.Id && value.ChapterId == board.Chapter.Id);
        long nowSeconds = now.ToUnixTimeSeconds();
        if (receipt is null)
        {
            // Standings are final only after gameplay closes; undecided rewards expire with the authored window.
            if (ActivityScheduleService.IsOpen(board.Activity.TimeId, now)
                || !ActivityScheduleService.TryGet(board.Activity.RankRewardTimeId, out ActivityScheduleEntry window)
                || !window.IsOpen(now)
                || nowSeconds >= window.StartTime + RankConfig.Value.ExpireInterval
                || rank <= 0
                || rank > RankConfig.Value.NeedRank)
                return;
            List<int> rewardIds = board.Chapter.RankRewardIds.Where(id => id > 0).ToList();
            if (rewardIds.Count == 0)
                return;

            receipt = new TransfiniteTowerRankRewardReceipt
            {
                ActivityId = board.Activity.Id,
                ChapterId = board.Chapter.Id,
                Rank = rank,
                RewardIds = rewardIds,
                DecidedAt = nowSeconds
            };
            // Player-document goods (chat boards) commit atomically with the decision.
            List<ChatBoardUnlockState> originalBoards = player.UnlockedChatBoards
                .Select(value => new ChatBoardUnlockState { Id = value.Id, GetTime = value.GetTime, EndTime = value.EndTime })
                .ToList();
            receipts.Add(receipt);
            try
            {
                foreach (RewardGoodsTable goods in rewardIds.SelectMany(RewardHandler.GetRewardGoods)
                    .Where(goods => RewardHandler.GetRewardType(goods) == RewardType.ChatBoard))
                    RewardHandler.UnlockChatBoardReward(goods.TemplateId, player, nowSeconds);
                player.SaveChecked();
            }
            catch (Exception exception)
            {
                // The write outcome is unknown: it may have landed (acknowledgement loss). Never fall back to
                // the pre-request snapshot, which a later unrelated save would write over the committed
                // decision; adopt the durable decision when storage shows it and fail the session when the
                // durable document cannot be read at all.
                Player? stored = TryReadStoredPlayer(player);
                TransfiniteTowerRankRewardReceipt? durable = stored is null ? null : PersistedReceipt(stored, board);
                if (stored is null)
                {
                    session.DisconnectProtocol(persistState: false);
                    throw;
                }
                if (durable is null)
                {
                    // Storage proves this decision never landed: the in-memory grant is discarded.
                    receipts.Remove(receipt);
                    player.UnlockedChatBoards = originalBoards;
                    throw;
                }
                // Frozen DecidedAt/Rank/RewardIds and the persisted unlock windows win; nothing is regranted.
                player.TransfiniteTowerRankRewards = stored.TransfiniteTowerRankRewards ?? [];
                player.UnlockedChatBoards = stored.UnlockedChatBoards ?? [];
                receipt = durable;
                session.log.Warn($"TransfiniteTower rank reward decision reconciled from storage: {exception.Message}");
            }
            AnnounceRankChatBoards(session, player, receipt);
        }
        else if (!receipt.Delivered)
        {
            // A retry replays the announcement the earlier pass may have lost, from the persisted unlock state.
            AnnounceRankChatBoards(session, player, receipt);
        }
        if (receipt.Delivered)
            return;

        List<RewardGoodsTable> allGoods = receipt.RewardIds.SelectMany(RewardHandler.GetRewardGoods).ToList();
        List<RewardGoodsTable> documentGoods = allGoods
            .Where(goods => RewardHandler.GetRewardType(goods) != RewardType.ChatBoard)
            .ToList();
        RewardApplicationResult? application = documentGoods.Count == 0
            ? null
            : RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant($"transfinite-tower-rank:{player.PlayerData.Id}:{receipt.ActivityId}:{receipt.ChapterId}", documentGoods)],
                session);
        receipt.Delivered = true;
        try
        {
            player.SaveChecked();
        }
        catch (Exception exception)
        {
            // The delivery acknowledgement is uncertain too: keep the live receipt terminal when storage shows
            // the flag landed, otherwise leave it undelivered so the idempotent grant is retried.
            Player? stored = TryReadStoredPlayer(player);
            TransfiniteTowerRankRewardReceipt? durable = stored is null ? null : PersistedReceipt(stored, board);
            if (stored is null)
            {
                session.DisconnectProtocol(persistState: false);
                throw;
            }
            if (durable?.Delivered != true)
            {
                receipt.Delivered = false;
                throw;
            }
            session.log.Warn($"TransfiniteTower rank reward delivery reconciled from storage: {exception.Message}");
        }
        application?.SendPushes(session);
        session.SendPush(new NotifyTransfiniteTowerRankReward
        {
            RewardGoodsList = allGoods.Select(goods => new RewardGoods
            {
                Id = goods.Id,
                TemplateId = goods.TemplateId,
                Count = goods.Count,
                RewardType = (int)(RewardHandler.GetRewardType(goods)
                    ?? throw new InvalidDataException($"TransfiniteTower rank reward goods {goods.Id} has no reward type."))
            }).ToList()
        });
    }

    // Durable read for an ambiguous write on the shared safe-read boundary: Player.TryFromPlayerId returns
    // null when storage cannot be read, and null means the outcome is unknown rather than "did not land".
    private static Player? TryReadStoredPlayer(Player live) => Player.TryFromPlayerId(live.PlayerData.Id);

    private static TransfiniteTowerRankRewardReceipt? PersistedReceipt(Player stored, RankBoard board) =>
        (stored.TransfiniteTowerRankRewards ?? []).FirstOrDefault(value =>
            value.ActivityId == board.Activity.Id && value.ChapterId == board.Chapter.Id);

    // Announces the receipt's chat boards from the persisted unlock state, so a retry after a lost push
    // replays exactly the durable Id/GetTime/EndTime and can never extend an authored duration.
    private static void AnnounceRankChatBoards(Session session, Player player, TransfiniteTowerRankRewardReceipt receipt)
    {
        foreach (RewardGoodsTable goods in receipt.RewardIds.SelectMany(RewardHandler.GetRewardGoods)
            .Where(goods => RewardHandler.GetRewardType(goods) == RewardType.ChatBoard))
        {
            ChatBoardUnlockState? unlock = player.UnlockedChatBoards.Find(state => state.Id == goods.TemplateId);
            if (unlock is null)
                continue;
            session.SendPush(new NotifyChatBoardInfo
            {
                ChatBoard = new() { Id = unlock.Id, GetTime = unlock.GetTime, EndTime = unlock.EndTime }
            });
        }
    }

    private static TransfiniteTowerRankEntry? PublishRankEntry(Player player, RankBoard board)
    {
        string id = TransfiniteTowerRankEntry.BuildId(board.Activity.Id, board.Chapter.Id, player.PlayerData.Id);
        FilterDefinitionBuilder<TransfiniteTowerRankEntry> filter = Builders<TransfiniteTowerRankEntry>.Filter;
        TransfiniteTowerRankInfo? info = player.TransfiniteTower?.ChapterInfoList
            .FirstOrDefault(chapter => chapter.ChapterId == board.Chapter.Id)?.RankInfo;
        if (info is null || info.MaxOrder <= 0 || info.Characters is not { Count: > 0 })
            return FindRankEntry(id);

        TransfiniteTowerRankEntry candidate = new()
        {
            Id = id,
            ActivityId = board.Activity.Id,
            ChapterId = board.Chapter.Id,
            PlayerId = player.PlayerData.Id,
            Name = player.PlayerData.Name,
            HeadPortraitId = player.PlayerData.CurrHeadPortraitId,
            HeadFrameId = player.PlayerData.CurrHeadFrameId,
            MaxOrder = info.MaxOrder,
            TotalSpendTime = info.TotalSpendTime,
            TotalPower = info.TotalPower,
            MvpFightId = info.MvpFightId,
            Characters = info.Characters,
            AchievedAt = info.AchievedAt
        };
        for (int attempt = 0; attempt < 5; attempt++)
        {
            TransfiniteTowerRankEntry? current = FindRankEntry(id);
            if (current is not null)
            {
                int order = CompareRank(candidate, current);
                // Never replace a better stored result; skip no-op rewrites of the same build.
                if (order > 0 || order == 0
                    && current.MvpFightId == candidate.MvpFightId
                    && current.TotalPower == candidate.TotalPower
                    && current.Name == candidate.Name
                    && current.HeadPortraitId == candidate.HeadPortraitId
                    && current.HeadFrameId == candidate.HeadFrameId)
                    return current;
                ReplaceOneResult replaced = TransfiniteTowerRankEntry.collection.ReplaceOne(
                    filter.And(
                        filter.Eq(entry => entry.Id, id),
                        filter.Eq(entry => entry.MaxOrder, current.MaxOrder),
                        filter.Eq(entry => entry.TotalSpendTime, current.TotalSpendTime),
                        filter.Eq(entry => entry.AchievedAt, current.AchievedAt),
                        filter.Eq(entry => entry.MvpFightId, current.MvpFightId)),
                    candidate);
                if (replaced.IsAcknowledged && replaced.MatchedCount == 1)
                    return candidate;
                continue;
            }
            try
            {
                ReplaceOneResult inserted = TransfiniteTowerRankEntry.collection.ReplaceOne(
                    filter.And(filter.Eq(entry => entry.Id, id), filter.Exists(entry => entry.ActivityId, false)),
                    candidate,
                    new ReplaceOptions { IsUpsert = true });
                if (inserted.IsAcknowledged && (inserted.MatchedCount == 1 || inserted.UpsertedId is not null))
                    return candidate;
            }
            catch (MongoWriteException exception)
                when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
            }
        }
        throw new MongoException($"Could not persist TransfiniteTower rank entry {id} after concurrent updates.");
    }

    private static TransfiniteTowerRankEntry? FindRankEntry(string id) =>
        TransfiniteTowerRankEntry.collection.Find(entry => entry.Id == id).Limit(1).FirstOrDefault();

    private static FilterDefinition<TransfiniteTowerRankEntry> BoardFilter(RankBoard board)
    {
        FilterDefinitionBuilder<TransfiniteTowerRankEntry> filter = Builders<TransfiniteTowerRankEntry>.Filter;
        return filter.And(
            filter.Eq(entry => entry.ActivityId, board.Activity.Id),
            filter.Eq(entry => entry.ChapterId, board.Chapter.Id));
    }

    private static IFindFluent<TransfiniteTowerRankEntry, TransfiniteTowerRankEntry> Ordered(
        FilterDefinition<TransfiniteTowerRankEntry> participants) =>
        TransfiniteTowerRankEntry.collection.Find(participants)
            .SortByDescending(entry => entry.MaxOrder)
            .ThenBy(entry => entry.TotalSpendTime)
            .ThenBy(entry => entry.AchievedAt)
            .ThenBy(entry => entry.PlayerId);

    private static FilterDefinition<TransfiniteTowerRankEntry> BetterThan(TransfiniteTowerRankEntry own)
    {
        FilterDefinitionBuilder<TransfiniteTowerRankEntry> filter = Builders<TransfiniteTowerRankEntry>.Filter;
        return filter.Or(
            filter.Gt(entry => entry.MaxOrder, own.MaxOrder),
            filter.And(
                filter.Eq(entry => entry.MaxOrder, own.MaxOrder),
                filter.Lt(entry => entry.TotalSpendTime, own.TotalSpendTime)),
            filter.And(
                filter.Eq(entry => entry.MaxOrder, own.MaxOrder),
                filter.Eq(entry => entry.TotalSpendTime, own.TotalSpendTime),
                filter.Lt(entry => entry.AchievedAt, own.AchievedAt)),
            filter.And(
                filter.Eq(entry => entry.MaxOrder, own.MaxOrder),
                filter.Eq(entry => entry.TotalSpendTime, own.TotalSpendTime),
                filter.Eq(entry => entry.AchievedAt, own.AchievedAt),
                filter.Lt(entry => entry.PlayerId, own.PlayerId)));
    }

    // Positive when current ranks strictly ahead of candidate (same player: PlayerId never breaks the tie).
    private static int CompareRank(TransfiniteTowerRankEntry candidate, TransfiniteTowerRankEntry current)
    {
        if (current.MaxOrder != candidate.MaxOrder)
            return current.MaxOrder > candidate.MaxOrder ? 1 : -1;
        if (current.TotalSpendTime != candidate.TotalSpendTime)
            return current.TotalSpendTime < candidate.TotalSpendTime ? 1 : -1;
        if (current.AchievedAt != candidate.AchievedAt)
            return current.AchievedAt < candidate.AchievedAt ? 1 : -1;
        return 0;
    }

    private static int ToCount(long value) => (int)Math.Min(value, int.MaxValue);
}
