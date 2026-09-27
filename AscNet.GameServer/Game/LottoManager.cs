using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.lotto;
using AscNet.Table.V2.share.wheelchairmanual;

namespace AscNet.GameServer.Game;

internal static class LottoManager
{
    private sealed record Catalog(
        LottoTable Lotto,
        LottoPrimaryTable Primary,
        IReadOnlyDictionary<int, LottoRewardTable> Rewards);

    private static readonly Lazy<Dictionary<int, Catalog>> Catalogs = new(CreateCatalogs);
    private static readonly Lazy<Dictionary<int, int>> CharacterQualities = new(() =>
        TableReaderV2.Parse<CharacterQualityTable>()
            .GroupBy(row => row.CharacterId)
            .ToDictionary(group => group.Key, group => group.Min(row => row.Quality)));
    private static readonly Lazy<Dictionary<int, int>> EquipQualities = new(() =>
        TableReaderV2.Parse<EquipTable>().ToDictionary(row => row.Id, row => row.Quality));
    private static readonly Lazy<Dictionary<int, int>> ItemQualities = new(() =>
        TableReaderV2.Parse<ItemTable>().ToDictionary(row => row.Id, row => row.Quality));

    // EN share/text/CodeText: Lotto* and ItemCountNotEnough.
    internal const int NotOpen = 20111001;
    internal const int InvalidCatalog = 20111002;
    internal const int Exhausted = 20111003;
    internal const int InvalidTicket = 20111004;
    internal const int InvalidTicketKey = 20111005;
    internal const int InvalidTicketCost = 20111006;
    internal const int WrongTicketStage = 20111007;
    internal const int InvalidPrimary = 20111008;
    internal const int InsufficientItems = 20012004;

    internal static LottoResponse Draw(Session session, int primaryId, int lottoId)
    {
        int code = GetOpenProgress(session.player, primaryId, lottoId, out Catalog? catalog, out LottoStateInfo? progress);
        if (code != 0) return new() { Code = code };
        Catalog current = catalog!;
        LottoStateInfo state = progress!;
        if (state.Pending is { TicketId: > 0 }) return new() { Code = WrongTicketStage };
        if (state.Pending is null)
        {
            int index = state.LottoRewards.Count;
            if (index >= current.Rewards.Count) return new() { Code = Exhausted };
            if (index >= current.Lotto.ConsumeCountList.Count) return new() { Code = InvalidCatalog };
            int cost = current.Lotto.ConsumeCountList[index];
            if (cost <= 0 || !Inventory.IsValidClientItemId(current.Lotto.ConsumeId))
                return new() { Code = InvalidCatalog };
            if (Balance(session, current.Lotto.ConsumeId) < cost) return new() { Code = InsufficientItems };

            LottoRewardTable[] remaining = current.Rewards.Values.Where(row => !state.LottoRewards.Contains(row.Id)).ToArray();
            if (remaining.Any(row => index >= row.Weights.Count || row.Weights[index] < 0))
                return new() { Code = InvalidCatalog };
            long total = remaining.Sum(row => (long)row.Weights[index]);
            if (total <= 0) return new() { Code = InvalidCatalog };
            long roll = Random.Shared.NextInt64(total);
            LottoRewardTable selected = remaining.First(row => (roll -= row.Weights[index]) < 0);
            SavePending(session, state, new()
            {
                RewardId = selected.Id,
                LottoTime = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                CostItemId = current.Lotto.ConsumeId,
                CostCount = cost
            });
        }
        LottoPendingOperation pending = state.Pending!;
        (RewardApplicationResult application, List<RewardGoods> extra) = Complete(session, current, state);
        application.SendPushes(session);
        LottoInfoResponse.LottoInfo info = BuildInfo(session.player, current);
        return new()
        {
            LottoRewardId = pending.RewardId,
            RewardList = application.RewardGoods.Take(1).ToList(),
            ExtraRewardList = extra,
            ExtraRewardState = state.ExtraRewardState,
            LottoRecords = info.LottoRecords
        };
    }

    internal static LottoBuyTicketResponse BuyTicket(Session session, LottoBuyTicketRequest request)
    {
        int code = GetOpenProgress(session.player, request.LottoPrimaryId, request.LottoId, out Catalog? catalog, out LottoStateInfo? progress);
        if (code != 0) return new() { Code = code };
        Catalog current = catalog!;
        LottoStateInfo state = progress!;
        if (state.Pending is not null
            && (state.Pending.TicketId != request.TicketId || state.Pending.TicketKey != request.TicketKey))
            return new() { Code = WrongTicketStage };
        if (state.Pending is null)
        {
            int index = state.LottoRewards.Count;
            if (index >= current.Rewards.Count) return new() { Code = Exhausted };
            LottoBuyTicketRuleTable? rule = TableReaderV2.Parse<LottoBuyTicketRuleTable>().SingleOrDefault(row => row.Id == request.TicketId);
            if (rule is null) return new() { Code = InvalidTicket };
            if (index >= current.Lotto.BuyTicketRuleIdList.Count || current.Lotto.BuyTicketRuleIdList[index] != request.TicketId)
                return new() { Code = WrongTicketStage };
            int key = request.TicketKey - 1;
            if (key < 0 || key >= rule.UseItemId.Count || !Inventory.IsValidClientItemId(rule.UseItemId[key]))
                return new() { Code = InvalidTicketKey };
            if (key >= rule.UseItemCount.Count || rule.UseItemCount[key] <= 0 || rule.TargetItemCount <= 0)
                return new() { Code = InvalidTicketCost };
            if (Balance(session, rule.UseItemId[key]) < rule.UseItemCount[key])
                return new() { Code = InsufficientItems };
            SavePending(session, state, new()
            {
                TicketId = request.TicketId, TicketKey = request.TicketKey,
                CostItemId = rule.UseItemId[key], CostCount = rule.UseItemCount[key],
                ItemCount = rule.TargetItemCount
            });
        }
        int count = state.Pending!.ItemCount;
        (RewardApplicationResult application, _) = Complete(session, current, state);
        application.SendPushes(session);
        return new() { ItemId = current.Lotto.ConsumeId, ItemCount = count };
    }

    internal static void RecoverPending(Session session)
    {
        foreach (LottoStateInfo state in session.player.Lotto.Infos.ToArray())
            if (Catalogs.Value.TryGetValue(state.Id, out Catalog? catalog)
                && catalog.Primary.Id == state.LottoPrimaryId
                && state.Pending is not null)
            {
                (RewardApplicationResult application, _) = Complete(session, catalog, state);
                application.SendPushes(session);
            }
    }

    private static long Balance(Session session, int itemId) =>
        session.inventory.Items.FirstOrDefault(item => item.Id == itemId)?.Count ?? 0;

    private static int GetOpenProgress(Player player, int primaryId, int lottoId, out Catalog? catalog, out LottoStateInfo? state)
    {
        player.Lotto ??= new LottoState();
        state = null;
        LottoPrimaryTable? primary = TableReaderV2.Parse<LottoPrimaryTable>().FirstOrDefault(row => row.Id == primaryId);
        if (primary is null || primary.LottoIdList.Count == 0)
        {
            catalog = null;
            return InvalidPrimary;
        }
        if (lottoId == 0 && primary.LottoIdList.Count == 1)
            lottoId = primary.LottoIdList[0]; // The pre-4.8 client omitted LottoId for single pools.
        if (!primary.LottoIdList.Contains(lottoId)
            || primary.LottoIdList.Count > 1
               && player.Lotto.SelectedPrimaryIdToLottoId.GetValueOrDefault(primaryId) != lottoId)
        {
            catalog = null;
            return InvalidPrimary;
        }
        if (!Catalogs.Value.TryGetValue(lottoId, out catalog) || catalog.Primary.Id != primaryId)
            return InvalidCatalog;
        if (!IsOpen(catalog)) return NotOpen;
        if (!TryGetProgress(player, catalog, out state)) return InvalidCatalog;
        state ??= new() { Id = catalog.Lotto.Id, LottoPrimaryId = catalog.Primary.Id };
        return 0;
    }

    private static bool IsOpen(Catalog catalog)
    {
        if (catalog.Lotto.TimeId is not int timeId || timeId == 0)
            return false; // No calendar or current source link: do not re-open historical lotteries.
        if (ActivityScheduleService.TryGet(timeId, out ActivityScheduleEntry schedule))
            return schedule.IsOpen(DateTimeOffset.UtcNow);
        return TableReaderV2.Parse<WheelchairManualActivityTable>()
            .Any(row => row.LottoId == catalog.Lotto.Id && row.TimeId == timeId);
    }

    private static void SavePending(Session session, LottoStateInfo state, LottoPendingOperation pending)
    {
        bool added = !session.player.Lotto.Infos.Contains(state);
        if (added) session.player.Lotto.Infos.Add(state);
        state.Pending = pending;
        try { session.player.SaveChecked(); }
        catch
        {
            state.Pending = null;
            if (added) session.player.Lotto.Infos.Remove(state);
            throw;
        }
    }

    private static (RewardApplicationResult Application, List<RewardGoods> Extra) Complete(
        Session session, Catalog catalog, LottoStateInfo state)
    {
        LottoPendingOperation pending = state.Pending!;
        bool ticket = pending.TicketId > 0;
        int sequence = ticket ? state.TicketPurchaseCount : state.LottoRewards.Count;
        string claim = $"lotto:{session.player.PlayerData.Id}:{catalog.Lotto.Id}:{(ticket ? "ticket" : "draw")}:{sequence}";
        LottoRewardTable? reward = ticket ? null : catalog.Rewards[pending.RewardId];
        List<RewardGoodsTable> goods = [new()
        {
            TemplateId = ticket ? catalog.Lotto.ConsumeId : reward!.TemplateId,
            Count = ticket ? pending.ItemCount : reward!.Count
        }];
        bool extra = !ticket && state.ExtraRewardState != 2
            && catalog.Lotto.ExtraRewardId is int extraId && extraId > 0
            && catalog.Lotto.ReachRewardTimes is int threshold && threshold > 0
            && state.LottoRewards.Count + 1 >= threshold;
        if (extra)
        {
            List<RewardGoodsTable> configured = RewardHandler.GetRewardGoods(catalog.Lotto.ExtraRewardId ?? 0);
            if (configured.Count == 0) throw new InvalidDataException("Lotto extra reward is not configured.");
            goods.AddRange(configured);
        }
        RewardApplicationResult application = RewardHandler.ApplyRewardsOnceAndPersist(
            [new RewardGrant(claim, goods, new Dictionary<int, int> { [pending.CostItemId] = pending.CostCount })], session);
        int oldExtra = state.ExtraRewardState;
        if (ticket) state.TicketPurchaseCount++;
        else
        {
            state.LottoRewards.Add(pending.RewardId);
            state.LottoRecords.Add(new() { RewardId = pending.RewardId, LottoTime = pending.LottoTime });
            if (extra) state.ExtraRewardState = 2;
        }
        state.Pending = null;
        try { session.player.SaveChecked(); }
        catch
        {
            state.Pending = pending;
            state.ExtraRewardState = oldExtra;
            if (ticket) state.TicketPurchaseCount--;
            else
            {
                state.LottoRewards.RemoveAt(state.LottoRewards.Count - 1);
                state.LottoRecords.RemoveAt(state.LottoRecords.Count - 1);
            }
            throw;
        }
        return (application, extra ? application.RewardGoods.Skip(1).ToList() : []);
    }

    internal static List<LottoInfoResponse.LottoInfo> BuildInfos(Player player)
    {
        player.Lotto ??= new LottoState();
        return Catalogs.Value.Values.Where(IsOpen)
            .Where(catalog => catalog.Primary.LottoIdList.Count == 1
                || player.Lotto.SelectedPrimaryIdToLottoId.GetValueOrDefault(catalog.Primary.Id) == catalog.Lotto.Id)
            .Where(catalog => TryGetProgress(player, catalog, out _))
            .Select(catalog => BuildInfo(player, catalog))
            .OrderBy(info => info.LottoPrimaryId).ThenBy(info => info.Id)
            .ToList();
    }

    private static LottoInfoResponse.LottoInfo BuildInfo(Player player, Catalog catalog)
    {
        TryGetProgress(player, catalog, out LottoStateInfo? progress);
        return new LottoInfoResponse.LottoInfo
        {
            Id = catalog.Lotto.Id,
            LottoPrimaryId = progress?.LottoPrimaryId ?? catalog.Primary.Id,
            ExtraRewardState = progress?.ExtraRewardState ?? 0,
            LottoRewards = progress?.LottoRewards.ToList() ?? [],
            LottoRecords = progress?.LottoRecords.Select(record => new LottoInfoResponse.LottoRecord
            {
                RewardGoods = ToRewardGoods(catalog.Rewards[record.RewardId]),
                LottoTime = record.LottoTime
            }).ToList() ?? []
        };
    }

    internal static Dictionary<string, object?> BuildSelfChoicePayload(Player player)
    {
        player.Lotto ??= new LottoState();
        int[] primaries = Catalogs.Value.Values.Where(IsOpen)
            .Select(catalog => catalog.Primary)
            .Where(primary => primary.LottoIdList.Count > 1)
            .Select(primary => primary.Id).Distinct().ToArray();
        return new()
        {
            ["LottoPrimaryIds"] = primaries,
            ["CurrentPrimaryIdToLottoId"] = player.Lotto.SelectedPrimaryIdToLottoId
                .Where(pair => primaries.Contains(pair.Key)
                    && Catalogs.Value.ContainsKey(pair.Value)
                    && Catalogs.Value[pair.Value].Primary.Id == pair.Key)
                .ToDictionary(pair => pair.Key, pair => pair.Value)
        };
    }

    internal static int Select(Player player, int primaryId, int lottoId)
    {
        player.Lotto ??= new LottoState();
        if (!Catalogs.Value.TryGetValue(lottoId, out Catalog? catalog)
            || catalog.Primary.Id != primaryId || catalog.Primary.LottoIdList.Count < 2)
            return InvalidPrimary;
        if (!IsOpen(catalog)) return NotOpen;
        player.Lotto.SelectedPrimaryIdToLottoId.TryGetValue(primaryId, out int previous);
        player.Lotto.SelectedPrimaryIdToLottoId[primaryId] = lottoId;
        try { player.SaveChecked(); }
        catch
        {
            if (previous == 0) player.Lotto.SelectedPrimaryIdToLottoId.Remove(primaryId);
            else player.Lotto.SelectedPrimaryIdToLottoId[primaryId] = previous;
            throw;
        }
        return 0;
    }

    private static Dictionary<int, Catalog> CreateCatalogs()
    {
        HashSet<int> rules = TableReaderV2.Parse<LottoBuyTicketRuleTable>().Select(row => row.Id).ToHashSet();
        ILookup<int, LottoRewardTable> rewards = TableReaderV2.Parse<LottoRewardTable>().ToLookup(row => row.LottoId);
        LottoTable[] lottoRows = TableReaderV2.Parse<LottoTable>().ToArray();
        Dictionary<int, Catalog> catalogs = [];
        foreach (LottoPrimaryTable primary in TableReaderV2.Parse<LottoPrimaryTable>())
            foreach (LottoTable lotto in lottoRows
                .Where(row => primary.LottoIdList.Contains(row.Id) && row.TimeId == primary.TimeId))
            {
                if (lotto.BuyTicketRuleIdList.Count == 0
                    || !lotto.BuyTicketRuleIdList.All(rules.Contains)
                    || lotto.ConsumeCountList.Count < rewards[lotto.Id].Count()
                    || !rewards[lotto.Id].Any())
                    continue;
                catalogs.TryAdd(lotto.Id, new Catalog(lotto, primary, rewards[lotto.Id].ToDictionary(row => row.Id)));
            }
        return catalogs;
    }

    private static bool TryGetProgress(Player player, Catalog catalog, out LottoStateInfo? progress)
    {
        progress = null;
        LottoState state = player.Lotto ??= new LottoState();
        LottoStateInfo[] matches = state.Infos.Where(info => info.Id == catalog.Lotto.Id).ToArray();
        if (matches.Length == 0) return true;
        if (matches.Length != 1) return false;

        LottoStateInfo candidate = matches[0];
        if (candidate.LottoPrimaryId != catalog.Primary.Id
            || candidate.ExtraRewardState is < 0 or > 2 || candidate.TicketPurchaseCount < 0
            || candidate.LottoRewards is null || candidate.LottoRecords is null)
            return false;

        HashSet<int> claimed = candidate.LottoRewards.ToHashSet();
        if (claimed.Count != candidate.LottoRewards.Count || !claimed.All(catalog.Rewards.ContainsKey))
            return false;

        HashSet<int> recorded = candidate.LottoRecords.Select(record => record.RewardId).ToHashSet();
        if (recorded.Count != candidate.LottoRecords.Count
            || candidate.LottoRecords.Any(record => record.LottoTime < 0 || !catalog.Rewards.ContainsKey(record.RewardId))
            || !recorded.SetEquals(claimed))
            return false;

        progress = candidate;
        return true;
    }

    private static LottoInfoResponse.LottoRecord.LottoRewardGoods ToRewardGoods(LottoRewardTable reward)
    {
        int rewardType = 1;
        int quality = 0;
        if (CharacterQualities.Value.TryGetValue(reward.TemplateId, out int characterQuality))
        {
            rewardType = 2;
            quality = characterQuality;
        }
        else if (EquipQualities.Value.TryGetValue(reward.TemplateId, out int equipQuality))
        {
            rewardType = 3;
            quality = equipQuality;
        }
        else if (ItemQualities.Value.TryGetValue(reward.TemplateId, out int itemQuality))
        {
            quality = itemQuality;
        }

        return new()
        {
            RewardType = rewardType,
            TemplateId = (uint)reward.TemplateId,
            Count = reward.Count,
            Level = 0,
            Quality = quality,
            Grade = rewardType == 2 && quality > 0 ? 1 : 0,
            Breakthrough = 0,
            ConvertFrom = 0,
            IsGift = false,
            RewardMulti = 0,
            Id = 0
        };
    }
}
