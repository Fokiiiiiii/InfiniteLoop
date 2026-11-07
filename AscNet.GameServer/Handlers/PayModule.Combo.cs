using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public sealed class PurchaseComboRequest
{
    public uint ComboId { get; set; }
    public dynamic? Param { get; set; }
}

[MessagePackObject(true)]
public sealed class PurchaseComboResponse
{
    public int Code { get; set; }
    public dynamic? PurchaseInfo { get; set; }
    public List<dynamic> NewPurchaseInfoList { get; set; } = new();
    public dynamic? ComboInfo { get; set; }
    public List<RewardGoods> RewardList { get; set; } = new();
    public List<dynamic> RewardGoodsListByType { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class PurchaseSupplementGetDailyRewardRequest
{
    public uint Id { get; set; }
}

[MessagePackObject(true)]
public sealed class PurchaseSupplementGetDailyRewardResponse
{
    public int Code { get; set; }
    public dynamic? PurchaseInfo { get; set; }
    public List<RewardGoods> RewardList { get; set; } = new();
}

internal partial class PayModule
{
    private const int ComboNotFound = 20053037, ComboSoldOut = 20053038, ComboSubSoldOut = 20053039;
    private const int SupplementNotSupported = 20053040, SupplementNotNeeded = 20053041,
        SupplementRewardNotSupported = 20053043, MonthPassNotBought = 20053036, CurrencyNotEnough = 20012004;

    private static void ApplyComboState(List<dynamic> comboInfoList, Player? player)
    {
        foreach (dynamic combo in comboInfoList)
            if (combo is Dictionary<dynamic, dynamic> data)
                data["BuyTimes"] = player?.PurchaseComboBuyTimes.GetValueOrDefault(ReadDynamicUInt(data, "Id")) ?? 0;
    }

    [RequestPacketHandler("PurchaseComboRequest")]
    public static void PurchaseComboRequestHandler(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<PurchaseComboRequest>();
        PurchaseComboResponse response = new() { Code = 20053031 };
        lock (session.player)
        {
            try
            {
                response.Code = PurchaseCombo(session, request, response);
            }
            catch (Exception error)
            {
                session.log.Error($"Combo purchase {request.ComboId} failed: {error}");
                response.Code = 2;
            }
            if (response.Code != 0) session.log.Warn($"Combo purchase rejected: combo={request.ComboId}, code={response.Code}");
            session.SendResponse(response, packet.Id);
        }
    }

    private static int PurchaseCombo(Session session, PurchaseComboRequest request, PurchaseComboResponse response)
    {
        Player player = session.player;
        if (request.ComboId == 0 || request.ComboId > int.MaxValue || !ValidPurchaseParam(request.Param)
            || player.PendingPurchase is not null
            || (player.PendingCombo is not null && player.PendingCombo.Id != request.ComboId))
            return 20053031;
        var combo = Catalog.Value.FindCombo(request.ComboId);
        if (combo is null) return ComboNotFound;
        if (player.PendingCombo is null)
        {
            int code = PrepareCombo(session, combo, out PlayerPendingCombo? pending);
            if (code != 0) return code;
            player.PendingCombo = pending;
            try { player.SaveChecked(); }
            catch { player.PendingCombo = null; throw; }
        }
        RewardApplicationResult? result = ResumePendingCombo(session, out int resume);
        if (resume != 0) return resume;
        try { GrantMailDailyRewards(session); }
        catch (Exception error) { session.log.Error($"Daily purchase mail will retry on refresh: {error}"); }
        int uiType = ReadDynamicInt(combo, "UiType");
        response.NewPurchaseInfoList = BuildPurchaseListResponse([uiType], player).PurchaseInfoList;
        List<dynamic> infos = [combo];
        ApplyComboState(infos, player);
        response.ComboInfo = combo;
        response.RewardList = result!.RewardGoods;
        result.SendPushes(session);
        return 0;
    }

    /// <summary>Validates every bundle member with the ordinary purchase rules, priced at its bundle discount.</summary>
    private static int PrepareCombo(Session session, Dictionary<dynamic, dynamic> combo, out PlayerPendingCombo? pending)
    {
        pending = null;
        Player player = session.player;
        uint comboId = ReadDynamicUInt(combo, "Id");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int availability = PurchaseCatalog.AvailabilityCode(combo, now);
        if (availability != 0) return availability;
        int comboBought = player.PurchaseComboBuyTimes.GetValueOrDefault(comboId);
        int comboLimit = ReadDynamicInt(combo, "BuyLimitParam");
        if (comboLimit > 0 && comboBought >= comboLimit) return ComboSoldOut;

        PlayerPendingCombo draft = new()
        {
            Id = comboId, PreviousBuyTimes = comboBought, BuyTime = now, ConsumeId = ReadDynamicInt(combo, "ConsumeId")
        };
        List<RewardGoods> allGoods = [];
        foreach (var entry in (Dictionary<dynamic, dynamic>)combo["SubPackageDiscounts"])
        {
            uint subId = Convert.ToUInt32((object)entry.Key);
            int price = Convert.ToInt32((object)entry.Value);
            if (!TryFindPurchaseInfo(subId, null, out var subInfo)) return 20053001;
            int limit = ReadDynamicInt(subInfo!, "BuyLimitTimes");
            // The client leaves a member that has reached its own limit out of the bundle price.
            if (limit > 0 && CurrentBuyTimes(player, subInfo!) >= limit) continue;
            int code = ValidatePurchase(session, new PurchaseRequest { Id = subId, Count = 1, DiscountId = -1 },
                out _, out List<RewardGoods> goods, out _, price);
            if (code != 0) return code;
            PlayerPendingPurchase sub = new()
            {
                Id = subId, Count = 1, PreviousBuyTimes = player.PurchaseBuyTimes.GetValueOrDefault(subId), BuyTime = now,
                ConsumeId = draft.ConsumeId, ConsumeCount = price, Goods = goods,
                PeriodBuyTimes = checked(CurrentBuyTimes(player, subInfo!) + 1)
            };
            PreparePassUpdates(player, subInfo!, 1, sub);
            draft.Subs.Add(sub);
            draft.ConsumeCount = checked(draft.ConsumeCount + price);
            allGoods.AddRange(goods);
        }
        if (draft.Subs.Count == 0) return ComboSubSoldOut;
        if (draft.ConsumeCount > 0)
        {
            if (!Inventory.IsValidClientItemId(draft.ConsumeId)) return 20053031;
            if (draft.ConsumeCount > session.inventory.SpendableCount(draft.ConsumeId)) return CurrencyNotEnough;
        }
        if (!HasItemCapacity(session, ToRewardGoodsTable(allGoods))) return PayItemCapacityNotEnough;
        pending = draft;
        return 0;
    }


    private static RewardApplicationResult? ResumePendingCombo(Session session, out int code)
    {
        code = 0;
        Player player = session.player;
        PlayerPendingCombo? pending = player.PendingCombo;
        if (pending is null) return null;
        string key = $"purchase-combo:{player.PlayerData.Id}:{pending.Id}:{pending.PreviousBuyTimes}";
        List<RewardGoodsTable> rows = ToRewardGoodsTable(pending.Subs.SelectMany(sub => sub.Goods));
        if (!session.inventory.AppliedRewardClaims.Contains(key, StringComparer.Ordinal) && !HasItemCapacity(session, rows))
        {
            code = PayItemCapacityNotEnough;
            return null;
        }
        RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(
            [new RewardGrant(key, rows, pending.ConsumeCount > 0
                ? new Dictionary<int, int> { [pending.ConsumeId] = pending.ConsumeCount } : null)], session);

        var buyTimes = new Dictionary<uint, int>(player.PurchaseBuyTimes);
        var lastBuy = new Dictionary<uint, long>(player.PurchaseLastBuyTimes);
        var period = new Dictionary<uint, int>(player.PurchasePeriodBuyTimes);
        var passes = new Dictionary<uint, PlayerPurchaseDailyPass>(player.PurchaseDailyPasses);
        var comboTimes = new Dictionary<uint, int>(player.PurchaseComboBuyTimes);
        foreach (PlayerPendingPurchase sub in pending.Subs)
        {
            player.PurchaseBuyTimes[sub.Id] = checked(sub.PreviousBuyTimes + sub.Count);
            player.PurchaseLastBuyTimes[sub.Id] = sub.BuyTime;
            player.PurchasePeriodBuyTimes[sub.Id] = sub.PeriodBuyTimes;
            foreach (var pass in sub.DailyPasses) player.PurchaseDailyPasses[pass.Key] = pass.Value;
        }
        player.PurchaseComboBuyTimes[pending.Id] = checked(pending.PreviousBuyTimes + 1);
        player.PendingCombo = null;
        try { player.SaveChecked(); }
        catch
        {
            player.PurchaseBuyTimes = buyTimes; player.PurchaseLastBuyTimes = lastBuy;
            player.PurchasePeriodBuyTimes = period; player.PurchaseDailyPasses = passes;
            player.PurchaseComboBuyTimes = comboTimes; player.PendingCombo = pending;
            throw;
        }
        return result;
    }

    // ---- Makeup Sign-in Card (PurchaseSupplementGetDailyRewardRequest) ----

    private static long MonthStartDay(long day)
    {
        DateTime date = DateTime.UnixEpoch.AddDays(day);
        return (new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Days;
    }

    /// <summary>Unclaimed reward days this month, before today, inside the entitlement (cards expire at month end).</summary>
    internal static List<long> MissedDays(PlayerPurchaseDailyPass pass, long today)
    {
        List<long> missed = [];
        if (pass.FirstDay <= 0) return missed;
        for (long day = Math.Max(pass.FirstDay, MonthStartDay(today)); day < Math.Min(today, pass.EndDay); day++)
            if (!pass.ClaimedDays.Contains(day)) missed.Add(day);
        return missed;
    }

    private static dynamic? SupplementData(Player? player, uint id, Dictionary<dynamic, dynamic> info)
    {
        if (player is null || ReadDynamicInt(info, "DailyRewardSupplementGetConsumeItemId") <= 0
            || RemainingDays(player, id) <= 0 || !HasValue(info, "DailyRewardGoodsList")) return null;
        var pass = player.PurchaseDailyPasses.GetValueOrDefault(id);
        return pass is null ? null : new Dictionary<dynamic, dynamic> { ["Count"] = MissedDays(pass, PurchaseDay()).Count };
    }

    [RequestPacketHandler("PurchaseSupplementGetDailyRewardRequest")]
    public static void PurchaseSupplementGetDailyRewardRequestHandler(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<PurchaseSupplementGetDailyRewardRequest>();
        PurchaseSupplementGetDailyRewardResponse response = new() { Code = 20053001 };
        lock (session.player)
        {
            try { response.Code = ClaimSupplement(session, request.Id, response); }
            catch (Exception error)
            {
                session.log.Error($"Makeup sign-in failed: {error}");
                response.Code = 2;
            }
            session.SendResponse(response, packet.Id);
        }
    }

    private static int ClaimSupplement(Session session, uint id, PurchaseSupplementGetDailyRewardResponse response)
    {
        Player player = session.player;
        if (!TryFindPurchaseInfo(id, null, out var info)) return 20053001;
        int item = ReadDynamicInt(info!, "DailyRewardSupplementGetConsumeItemId");
        if (item <= 0) return SupplementNotSupported;
        var pass = player.PurchaseDailyPasses.GetValueOrDefault(id);
        if (pass is null || RemainingDays(player, id) <= 0) return MonthPassNotBought;
        if (!HasValue(info!, "DailyRewardGoodsList") || HasValue(info!, "PurchaseSignInInfo")) return SupplementRewardNotSupported;
        List<long> missed = MissedDays(pass, PurchaseDay());
        if (missed.Count == 0) return SupplementNotNeeded;
        // One card completes one missed day; the client previews min(cards, missed).
        int count = (int)Math.Min(session.inventory.SpendableCount(item), missed.Count);
        if (count <= 0) return CurrencyNotEnough;
        List<long> days = missed.Take(count).ToList();
        List<RewardGoodsTable> daily = ToRewardGoodsTable(ReadPurchaseRewards(new() { ["RewardGoodsList"] = info!["DailyRewardGoodsList"] }, 1));
        List<RewardGrant> grants = days.Select((day, index) => new RewardGrant(
            $"purchase-supplement:{player.PlayerData.Id}:{id}:{day}", daily,
            index == 0 ? new Dictionary<int, int> { [item] = count } : null)).ToList();
        if (!grants.All(grant => session.inventory.AppliedRewardClaims.Contains(grant.ClaimKey, StringComparer.Ordinal))
            && !HasItemCapacity(session, Enumerable.Repeat(daily, count).SelectMany(rows => rows)))
            return PayItemCapacityNotEnough;

        RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(grants, session);
        pass.ClaimedDays.AddRange(days);
        try { player.SaveChecked(); }
        catch { pass.ClaimedDays.RemoveAll(days.Contains); throw; }
        ApplyPurchaseState([info!], player);
        response.PurchaseInfo = info;
        response.RewardList = result.RewardGoods;
        result.SendPushes(session);
        return 0;
    }
}
