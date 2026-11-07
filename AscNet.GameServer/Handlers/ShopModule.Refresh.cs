using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using MessagePack;
using ClientShop = AscNet.Common.MsgPack.GetShopInfoResponse.GetShopInfoResponseClientShop;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public sealed class RefreshShopRequest
{
    public uint Id { get; set; }
}

[MessagePackObject(true)]
public sealed class RefreshShopResponse
{
    public int Code { get; set; }
    public ClientShop? ClientShop { get; set; }
}

internal partial class ShopModule
{
    // Static server Shop/ShopGoods fields, when supplied by the curated catalog.
    // AscNet policy: apply authored shop clocks/reset flags; retail server rows are absent.
    private static readonly Dictionary<uint, (int ClockId, bool ResetManualTimes)> ShopRefreshRules = new();
    private static readonly HashSet<uint> ShopResetGoods = new();

    private static bool ReconcileManualShopRefresh(Player player, uint shopId, DateTimeOffset now)
    {
        player.ShopBuyTimes ??= new();
        player.ShopManualRefreshTimes ??= new();
        player.ShopManualRefreshPeriods ??= new();
        if (!ShopRefreshRules.TryGetValue(shopId, out var rule)
            || !TryGetAlarmWindow(rule.ClockId, now, out long period, out _))
            return false;
        bool hasPrevious = player.ShopManualRefreshPeriods.TryGetValue(shopId, out long previous);
        if (hasPrevious && previous >= period)
            return false;
        if (hasPrevious && rule.ResetManualTimes)
            player.ShopManualRefreshTimes.Remove(shopId);
        player.ShopManualRefreshPeriods[shopId] = period;
        if (hasPrevious && ShopCatalog(shopId) is { } shop)
            foreach (var goods in shop.GoodsList.Where(goods => ShopResetGoods.Contains(goods.Id)))
                player.ShopBuyTimes.Remove(goods.Id);
        return true;
    }

    [RequestPacketHandler("RefreshShopRequest")]
    public static void RefreshShopRequestHandler(Session session, Packet.Request packet)
    {
        RefreshShopRequest request = packet.Deserialize<RefreshShopRequest>();
        RefreshShopResponse response = new();
        ClientShop? catalog = ShopCatalog(request.Id);
        int code = catalog is null ? 20030007 : ShopInfoCode(session, request.Id);
        bool reconciled = false;
        if (code == 0 && catalog is not null)
        {
            reconciled = ReconcileManualShopRefresh(session.player, request.Id, DateTimeOffset.UtcNow);
            int used = session.player.ShopManualRefreshTimes.GetValueOrDefault(request.Id);
            // Missing server-owned reset semantics must not silently turn into a daily reset.
            if (catalog.ManualResetTimesLimit == 0 || catalog.ManualResetTimesLimit < -1
                || !ShopRefreshRules.TryGetValue(request.Id, out var rule)
                || rule.ClockId != 0 && !TryGetAlarmWindow(rule.ClockId, DateTimeOffset.UtcNow, out _, out _)
                || catalog.RefreshCostId < 0 || catalog.RefreshCostCount < 0
                || catalog.RefreshCostCount > 0 && (catalog.RefreshCostId <= 0 || !Inventory.IsValidClientItemId(catalog.RefreshCostId)))
                code = 20030018;
            else if (catalog.BuyTimesLimit > 0
                && catalog.GoodsList.Sum(goods => (long)session.player.ShopBuyTimes.GetValueOrDefault(goods.Id)) >= catalog.BuyTimesLimit)
                code = 20030011;
            else if (used == int.MaxValue || catalog.ManualResetTimesLimit > 0 && used >= catalog.ManualResetTimesLimit)
                code = 20030010;
            else if (catalog.RefreshCostCount > 0 && session.inventory.SpendableCount(catalog.RefreshCostId) < catalog.RefreshCostCount)
                code = 20012004;
            else
            {
                NotifyItemDataList items = new();
                if (catalog.RefreshCostCount > 0)
                    items.ItemDataList.AddRange(session.inventory.Spend(catalog.RefreshCostId, catalog.RefreshCostCount));
                foreach (var goods in catalog.GoodsList.Where(goods => ShopResetGoods.Contains(goods.Id)))
                    session.player.ShopBuyTimes.Remove(goods.Id);
                session.player.ShopManualRefreshTimes[request.Id] = used + 1;
                session.inventory.Save();
                session.player.Save();
                if (items.ItemDataList.Count > 0)
                    session.SendPush(items);
                response.ClientShop = BuildClientShop(request.Id, session.player);
            }
        }
        if (code != 0 && reconciled)
            session.player.Save();
        response.Code = code;
        session.SendResponse(response, packet.Id);
    }
}
