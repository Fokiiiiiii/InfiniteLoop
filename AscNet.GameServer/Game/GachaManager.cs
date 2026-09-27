using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.gacha;
using AscNet.Table.V2.share.reward;

namespace AscNet.GameServer.Game;

/// <summary>
/// share/gacha flow consumed by XGachaManager (GetGachaInfo/Gacha/ChoiceGacha/GachaItemExchange/NotifySelfChoiceGachaData).
/// Every draw/exchange saves its rolled outcome first, then pays through one idempotent reward receipt.
/// </summary>
internal static class GachaManager
{
    // EN share/text/CodeText Gacha* / GachaSelfChoice*.
    internal const int TemplateNotFound = 20061001;
    internal const int RewardUseUp = 20061002;
    internal const int NotOpen = 20061003;
    internal const int TimesNotEnough = 20061006;
    internal const int OrganizeNotFound = 20061008;
    internal const int CannotExchange = 20061010;
    internal const int ExchangeTemplateNotFound = 20061011;
    internal const int ExchangeBuyExceedCountLimit = 20061012;
    internal const int ExchangeItemNotFound = 20061013;
    internal const int ExchangeAlreadyLimit = 20061014;
    internal const int CourseRewardNotFound = 20061015;
    internal const int ConsumeCountError = 20061020;
    internal const int SelfChoiceGachaIdError = 20260002;
    internal const int SelfChoiceGachaIdExist = 20260004;
    internal const int SelfChoiceGachaIdNotSelected = 20260005;
    internal const int InsufficientItems = 20012004;

    private const int RewardTypeNotCount = 1; // XGachaConfigs.RewardType.NotCount
    private const int WeightScale = 10000;    // Every authored Gacha.Weight row sums to 10000.

    /// <summary>Calendar gate; tests substitute a synthetic window for undated TimeIds such as 51001.</summary>
    internal static Func<int, bool> IsTimeOpen = timeId =>
        timeId > 0 && ActivityScheduleService.IsOpen(timeId, DateTimeOffset.UtcNow);

    private static Dictionary<int, GachaTable> Gachas => Tables.Value.Gachas;
    private static readonly Lazy<(Dictionary<int, GachaTable> Gachas, ILookup<int, GachaRewardTable> Rewards)> Tables = new(() => (
        TableReaderV2.Parse<GachaTable>().ToDictionary(row => row.Id),
        TableReaderV2.Parse<GachaRewardTable>().ToLookup(row => row.GroupId)));

    private static bool IsOpen(GachaTable gacha) => IsTimeOpen(gacha.TimeId);

    private static IEnumerable<GachaRewardTable> Pool(GachaTable gacha) => gacha.GroupId.SelectMany(group => Tables.Value.Rewards[group]);

    private static GachaFashionSelfChoiceGroupTable? SelfChoiceGroup(int gachaId) =>
        TableReaderV2.Parse<GachaFashionSelfChoiceGroupTable>().FirstOrDefault(group => group.GachaIds.Contains(gachaId));

    private static GachaFashionSelfChoiceActivityTable? OpenSelfChoiceActivity() =>
        TableReaderV2.Parse<GachaFashionSelfChoiceActivityTable>().FirstOrDefault(activity => IsTimeOpen(activity.TimeId));

    private static GachaStateInfo? Find(Player player, int gachaId) =>
        (player.Gacha ??= new GachaState()).Infos.FirstOrDefault(info => info.Id == gachaId);

    private static long Balance(Session session, int itemId) =>
        session.inventory.Items.FirstOrDefault(item => item.Id == itemId)?.Count ?? 0;

    private static bool Available(GachaRewardTable reward, IReadOnlyDictionary<int, int> times) =>
        reward.RewardType == RewardTypeNotCount || times.GetValueOrDefault(reward.Id) < reward.UsableTimes;

    /// <summary>Login: finish every saved-but-unpaid draw/exchange before snapshots, even after its window closed.</summary>
    internal static void RecoverPending(Session session)
    {
        foreach (GachaStateInfo state in (session.player.Gacha ??= new GachaState()).Infos.ToArray())
        {
            if (state.Pending is not { } pending || !Gachas.TryGetValue(state.Id, out GachaTable? gacha)) continue;
            // Login documents were just loaded, so they are authoritative: no receipt in either document and too little
            // currency means nothing was paid and nothing can be; drop the intent instead of wedging login.
            string claim = ClaimKey(session, gacha, state);
            if (session.inventory.AppliedRewardClaims?.Contains(claim) != true
                && session.character.AppliedRewardClaims?.Contains(claim) != true
                && Balance(session, pending.CostItemId) < pending.CostCount)
            {
                state.Pending = null;
                try { session.player.SaveChecked(); }
                catch { state.Pending = pending; throw; }
                continue;
            }
            Complete(session, gacha, state);
        }
    }

    internal static GetGachaInfoResponse GetInfo(Session session, int gachaId)
    {
        if (!Gachas.TryGetValue(gachaId, out GachaTable? gacha)) return new() { Code = TemplateNotFound };
        GachaStateInfo? state = Find(session.player, gachaId);
        if (state?.Pending is not null) Complete(session, gacha, state).SendPushes(session);
        return new()
        {
            GridInfoList = GridInfos(state),
            GachaRecordList = Records(state),
            CurExchangeItemCount = state?.ExchangeCount ?? 0,
            TotalTimes = state?.TotalTimes ?? 0,
            MissTimes = state?.MissTimes ?? 0
        };
    }

    internal static GachaResponse Draw(Session session, int gachaId, int times)
    {
        if (!Gachas.TryGetValue(gachaId, out GachaTable? gacha)) return new() { Code = TemplateNotFound };
        if (!IsOpen(gacha)) return new() { Code = NotOpen };
        if (gacha.OrganizeId > 0) return new() { Code = OrganizeNotFound }; // Organize boxes have no dated 4.8 row.
        if (SelfChoiceGroup(gachaId) is { } group
            && (session.player.Gacha ??= new()).SelectedGroupIdToGachaId.GetValueOrDefault(group.Id) != gachaId)
            return new() { Code = SelfChoiceGachaIdNotSelected };
        GachaStateInfo state = Find(session.player, gachaId) ?? new() { Id = gachaId };
        if (state.Pending is { ExchangeNum: > 0 }) Complete(session, gacha, state).SendPushes(session);
        if (state.Pending is null)
        {
            if (!gacha.BtnGachaCount.Contains(times)) return new() { Code = TimesNotEnough };
            if (gacha.ConsumeCount <= 0 || !Inventory.IsValidClientItemId(gacha.ConsumeId)) return new() { Code = ConsumeCountError };
            int cost = checked(gacha.ConsumeCount * times);
            if (Balance(session, gacha.ConsumeId) < cost) return new() { Code = InsufficientItems };
            if (!HasSourceLaw(gacha)) return new() { Code = TemplateNotFound };
            GachaPendingOperation? pending = Roll(gacha, state, times);
            if (pending is null) return new() { Code = RewardUseUp };
            if (gacha.CourseRewardId > 0)
            {
                GachaCourseRewardTable? course = TableReaderV2.Parse<GachaCourseRewardTable>().FirstOrDefault(row => row.Id == gacha.CourseRewardId);
                if (course is null || course.LimitDrawTimes.Count != course.RewardIds.Count) return new() { Code = CourseRewardNotFound };
                pending.CourseRewardIds = course.LimitDrawTimes
                    .Select((limit, index) => (limit, reward: course.RewardIds[index]))
                    .Where(pair => state.TotalTimes < pair.limit && state.TotalTimes + times >= pair.limit)
                    .Select(pair => pair.reward).ToList();
            }
            pending.CostItemId = gacha.ConsumeId;
            pending.CostCount = cost;
            SavePending(session, state, pending);
        }
        int drawn = state.Pending!.RewardIds.Count;
        RewardApplicationResult application = Complete(session, gacha, state);
        application.SendPushes(session);
        return new()
        {
            RewardList = application.RewardGoods.Take(drawn).ToList(),
            GridInfoList = GridInfos(state) ?? [],
            GachaRecordList = Records(state) ?? [],
            GachaCourseResult = new() { TotalTimes = state.TotalTimes, RewardList = application.RewardGoods.Skip(drawn).ToList() },
            MissTimes = state.MissTimes
        };
    }

    internal static GachaItemExchangeResponse Exchange(Session session, GachaItemExchangeRequest request)
    {
        if (!Gachas.TryGetValue(request.Id, out GachaTable? gacha)) return new() { Code = TemplateNotFound };
        if (!IsOpen(gacha)) return new() { Code = NotOpen };
        if (gacha.ExchangeId <= 0) return new() { Code = CannotExchange };
        GachaStateInfo state = Find(session.player, request.Id) ?? new() { Id = request.Id };
        if (state.Pending is { ExchangeNum: 0 }) Complete(session, gacha, state).SendPushes(session); // Finish an unpaid draw first.
        if (state.Pending is null)
        {
            GachaItemExchangeTable? rule = TableReaderV2.Parse<GachaItemExchangeTable>().FirstOrDefault(row => row.Id == gacha.ExchangeId);
            if (rule is null) return new() { Code = ExchangeTemplateNotFound };
            if (request.ExchangeNum <= 0 || request.ExchangeNum > rule.BuyCountMax) return new() { Code = ExchangeBuyExceedCountLimit };
            if (request.SelectIndex < 0 || request.SelectIndex >= rule.UseItemIds.Count || request.SelectIndex >= rule.UseItemCounts.Count
                || !Inventory.IsValidClientItemId(rule.UseItemIds[request.SelectIndex]) || rule.UseItemCounts[request.SelectIndex] <= 0)
                return new() { Code = ExchangeItemNotFound };
            if (state.ExchangeCount + request.ExchangeNum > rule.TotalBuyCountMax) return new() { Code = ExchangeAlreadyLimit };
            int cost = checked(rule.UseItemCounts[request.SelectIndex] * request.ExchangeNum);
            if (Balance(session, rule.UseItemIds[request.SelectIndex]) < cost) return new() { Code = InsufficientItems };
            SavePending(session, state, new()
            {
                CostItemId = rule.UseItemIds[request.SelectIndex], CostCount = cost, ExchangeNum = request.ExchangeNum
            });
        }
        int count = state.Pending!.ExchangeNum;
        Complete(session, gacha, state).SendPushes(session);
        return new() { CurExchangeItemCount = state.ExchangeCount, GainItemId = gacha.ConsumeId, GainItemCount = count };
    }

    internal static int Choose(Player player, int gachaId)
    {
        player.Gacha ??= new GachaState();
        GachaFashionSelfChoiceActivityTable? activity = OpenSelfChoiceActivity();
        GachaFashionSelfChoiceGroupTable? group = SelfChoiceGroup(gachaId);
        if (activity is null || group is null || !activity.GachaGroupIds.Contains(group.Id)
            || !Gachas.TryGetValue(gachaId, out GachaTable? gacha) || !IsOpen(gacha))
            return SelfChoiceGachaIdError;
        // Lua OpenSelfChoiceEntranceForChange lets the player switch coatings; progress stays per gacha.
        bool had = player.Gacha.SelectedGroupIdToGachaId.TryGetValue(group.Id, out int previous);
        if (had && previous == gachaId) return SelfChoiceGachaIdExist;
        player.Gacha.SelectedGroupIdToGachaId[group.Id] = gachaId;
        try { player.SaveChecked(); }
        catch
        {
            if (had) player.Gacha.SelectedGroupIdToGachaId[group.Id] = previous;
            else player.Gacha.SelectedGroupIdToGachaId.Remove(group.Id);
            throw;
        }
        return 0;
    }

    internal static NotifySelfChoiceGachaData BuildSelfChoicePayload(Player player)
    {
        player.Gacha ??= new GachaState();
        GachaFashionSelfChoiceActivityTable? activity = OpenSelfChoiceActivity();
        return new()
        {
            ActivityId = activity?.Id ?? 0,
            ChoiceGroupList = activity?.GachaGroupIds.Select(groupId => new NotifySelfChoiceGachaData.SelfChoiceGachaGroup
            {
                GroupId = groupId,
                SelectedGachaId = player.Gacha.SelectedGroupIdToGachaId.GetValueOrDefault(groupId)
            }).ToList() ?? []
        };
    }

    // Partial-probability laws (PropAdd 500/200) have no authoritative formula; those gachas stay closed.
    private static bool HasSourceLaw(GachaTable gacha) =>
        gacha.GroupId.Count == gacha.Weight.Count && gacha.Weight.Sum() == WeightScale
        && gacha.PropAdd >= WeightScale && gacha.PropStartTimes > 0 && gacha.PropAddInitVal == 0;

    private static GachaPendingOperation? Roll(GachaTable gacha, GachaStateInfo state, int times)
    {
        Dictionary<int, int> drawn = new(state.RewardTimes);
        GachaPendingOperation pending = new() { MissTimes = state.MissTimes, Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        for (int draw = 0; draw < times; draw++)
        {
            int[] weights = gacha.GroupId.Select((group, index) =>
                Tables.Value.Rewards[group].Any(reward => Available(reward, drawn)) ? gacha.Weight[index] : 0).ToArray();
            int prop = gacha.GroupId.IndexOf(gacha.PropAddGroupId);
            // Only the source-defined law is supported: PropAdd >= 10000 guarantees the special group once
            // MissTimes reaches PropStartTimes - 1 (EN rule text for 50..53: "Guaranteed ... every 20 attempts").
            if (prop >= 0 && weights[prop] > 0 && pending.MissTimes + 1 >= gacha.PropStartTimes)
                for (int index = 0; index < weights.Length; index++)
                    if (index != prop) weights[index] = 0;
            int groupIndex = Pick(weights);
            if (groupIndex < 0) return null;
            GachaRewardTable[] candidates = Tables.Value.Rewards[gacha.GroupId[groupIndex]].Where(reward => Available(reward, drawn)).ToArray();
            int rewardIndex = Pick(candidates.Select(reward => reward.Weight).ToArray());
            if (rewardIndex < 0) return null;
            GachaRewardTable selected = candidates[rewardIndex];
            drawn[selected.Id] = drawn.GetValueOrDefault(selected.Id) + 1;
            pending.RewardIds.Add(selected.Id);
            pending.MissTimes = gacha.GroupId[groupIndex] == gacha.PropAddGroupId ? 0 : pending.MissTimes + 1;
        }
        return pending;
    }

    private static int Pick(int[] weights)
    {
        long total = weights.Sum(weight => (long)Math.Max(0, weight));
        if (total <= 0) return -1;
        long roll = Random.Shared.NextInt64(total);
        for (int index = 0; index < weights.Length; index++)
            if ((roll -= Math.Max(0, weights[index])) < 0) return index;
        return -1;
    }

    private static void SavePending(Session session, GachaStateInfo state, GachaPendingOperation pending)
    {
        bool added = !session.player.Gacha.Infos.Contains(state);
        if (added) session.player.Gacha.Infos.Add(state);
        state.Pending = pending;
        // A thrown save may still have committed (ack loss): keep the frozen intent live so no reroll can happen,
        // and require a confirmed re-save before any payment (see Confirm).
        try { session.player.SaveChecked(); }
        catch
        {
            pending.Unconfirmed = true;
            throw;
        }
    }

    private static void Confirm(Session session, GachaPendingOperation pending)
    {
        if (!pending.Unconfirmed) return;
        session.player.SaveChecked();
        pending.Unconfirmed = false;
    }

    private static string ClaimKey(Session session, GachaTable gacha, GachaStateInfo state) =>
        $"gacha:{session.player.PlayerData.Id}:{gacha.Id}:{(state.Pending!.ExchangeNum > 0 ? "exchange:" + state.ExchangeCount : "draw:" + state.TotalTimes)}";

    private static RewardApplicationResult Complete(Session session, GachaTable gacha, GachaStateInfo state)
    {
        GachaPendingOperation pending = state.Pending!;
        bool exchange = pending.ExchangeNum > 0;
        Dictionary<int, GachaRewardTable> rewards = Pool(gacha).ToDictionary(row => row.Id);
        List<RewardGoodsTable> goods = exchange
            ? [new() { TemplateId = gacha.ConsumeId, Count = pending.ExchangeNum }]
            : pending.RewardIds.Select(id => new RewardGoodsTable { TemplateId = rewards[id].TemplateId, Count = rewards[id].Count })
                .Concat(pending.CourseRewardIds.SelectMany(id => RewardHandler.GetRewardGoods(id) is { Count: > 0 } configured
                    ? configured : throw new InvalidDataException($"Gacha course reward {id} is not configured.")))
                .ToList();
        Confirm(session, pending);
        string claim = ClaimKey(session, gacha, state);
        RewardApplicationResult application = RewardHandler.ApplyRewardsOnceAndPersist(
            [new RewardGrant(claim, goods, new Dictionary<int, int> { [pending.CostItemId] = pending.CostCount })], session);

        GachaStateInfo before = new()
        {
            RewardTimes = new(state.RewardTimes), TotalTimes = state.TotalTimes, MissTimes = state.MissTimes,
            ExchangeCount = state.ExchangeCount, Records = state.Records.ToList()
        };
        if (exchange) state.ExchangeCount += pending.ExchangeNum;
        else
        {
            for (int index = 0; index < pending.RewardIds.Count; index++)
            {
                int id = pending.RewardIds[index];
                state.RewardTimes[id] = state.RewardTimes.GetValueOrDefault(id) + 1;
                RewardGoods paid = application.RewardGoods[index];
                state.Records.Add(new()
                {
                    RewardId = id, TemplateId = paid.TemplateId, Count = paid.Count,
                    RewardType = paid.RewardType, ConvertFrom = paid.ConvertFrom, Time = pending.Time
                });
            }
            state.TotalTimes += pending.RewardIds.Count;
            state.MissTimes = pending.MissTimes;
        }
        state.Pending = null;
        try { session.player.SaveChecked(); }
        catch
        {
            (state.RewardTimes, state.TotalTimes, state.MissTimes, state.ExchangeCount, state.Records) =
                (before.RewardTimes, before.TotalTimes, before.MissTimes, before.ExchangeCount, before.Records);
            state.Pending = pending;
            throw;
        }
        return application;
    }

    private static List<GachaGridInfo>? GridInfos(GachaStateInfo? state) =>
        state is { RewardTimes.Count: > 0 }
            ? state.RewardTimes.OrderBy(pair => pair.Key).Select(pair => new GachaGridInfo { Id = pair.Key, Times = pair.Value }).ToList()
            : null;

    private static List<GachaRecord>? Records(GachaStateInfo? state) =>
        state is { Records.Count: > 0 }
            ? state.Records.Select(record => new GachaRecord
            {
                GachaTime = record.Time,
                RewardGoods = new RewardGoods
                {
                    TemplateId = record.TemplateId, Count = record.Count,
                    RewardType = record.RewardType, ConvertFrom = record.ConvertFrom
                }
            }).ToList()
            : null;
}
