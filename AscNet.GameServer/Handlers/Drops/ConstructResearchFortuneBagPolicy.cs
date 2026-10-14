using AscNet.Common.Util;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;

namespace AscNet.GameServer.Handlers.Drops
{
    // AscNet policy for the Construct Research Surprise Fortune Bag, not a retail DropGroup.
    // The client ships no drop table. Counts and weights are the chances printed on the item.
    // The row id is that item. Reward 1011 pays Cogs and Black Cards and is not this grant.
    internal static class ConstructResearchFortuneBagPolicy
    {
        private static readonly Lazy<Dictionary<int, (int TicketItemId, IReadOnlyList<(int Count, int Weight)> Tiers)>> Bags = new(() =>
        {
            Dictionary<int, (int TicketItemId, IReadOnlyList<(int Count, int Weight)> Tiers)> bags = new();
            foreach (int itemId in TableReaderV2.Parse<ConstructResearchFortuneBagPolicyTable>().Select(row => row.Id).Distinct())
            {
                if (TryBuild(itemId, out int ticketItemId, out IReadOnlyList<(int Count, int Weight)> tiers))
                    bags.Add(itemId, (ticketItemId, tiers));
            }
            return bags;
        });

        internal static bool Applies(int itemId) => Bags.Value.ContainsKey(itemId);

        internal static bool TryResolve(int itemId, out int ticketItemId, out IReadOnlyList<(int Count, int Weight)> tiers)
        {
            if (Bags.Value.TryGetValue(itemId, out var resolved))
            {
                ticketItemId = resolved.TicketItemId;
                tiers = resolved.Tiers;
                return true;
            }
            ticketItemId = 0;
            tiers = Array.Empty<(int Count, int Weight)>();
            return false;
        }

        internal static bool TryGrant(int itemId, int boxCount, out RewardGoodsTable good)
        {
            good = null!;
            if (!TryResolve(itemId, out int ticketItemId, out IReadOnlyList<(int Count, int Weight)> tiers)
                || boxCount <= 0)
                return false;

            int weight = 0;
            foreach ((int _, int tierWeight) in tiers)
                weight += tierWeight;
            if (weight <= 0)
                return false;

            long total = 0;
            for (int index = 0; index < boxCount; index++)
            {
                int roll = Random.Shared.Next(weight);
                int cursor = 0;
                int picked = 0;
                foreach ((int count, int tierWeight) in tiers)
                {
                    cursor += tierWeight;
                    if (roll < cursor)
                    {
                        picked = count;
                        break;
                    }
                }
                if (picked <= 0)
                    return false;
                try { total = checked(total + picked); }
                catch (OverflowException) { return false; }
            }

            if (total <= 0 || total > int.MaxValue)
                return false;
            good = new RewardGoodsTable { TemplateId = ticketItemId, Count = (int)total, Params = [] };
            return true;
        }

        private static bool TryBuild(int itemId, out int ticketItemId, out IReadOnlyList<(int Count, int Weight)> tiers)
        {
            ticketItemId = 0;
            tiers = Array.Empty<(int Count, int Weight)>();
            List<(int Count, int Weight)> built = new();
            int? ticket = null;
            foreach (ConstructResearchFortuneBagPolicyTable row in TableReaderV2.Parse<ConstructResearchFortuneBagPolicyTable>())
            {
                if (row.Id != itemId)
                    continue;
                if (row.Id <= 0 || row.TicketItemId <= 0 || row.Count <= 0 || row.Weight <= 0)
                    return false;
                if (ticket is int existing && existing != row.TicketItemId)
                    return false;
                if (built.Any(tier => tier.Count == row.Count))
                    return false;
                ticket = row.TicketItemId;
                built.Add((row.Count, row.Weight));
            }
            if (ticket is not int ticketId || built.Count == 0 || built.Sum(tier => tier.Weight) != 100)
                return false;
            if (!TableReaderV2.Parse<ItemTable>().Any(row => row.Id == ticketId))
                return false;

            ticketItemId = ticketId;
            tiers = built;
            return true;
        }
    }
}
