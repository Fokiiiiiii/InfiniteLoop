using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.bigworld.common.commanderdiy;
using AscNet.Table.V2.share.bigworld.common.photograph;
using AscNet.Table.V2.share.bigworld.common.reward;
using AscNet.Table.V2.share.bigworld.skygarden.dormitory;
using AscNet.Table.V2.share.dlcworld.questsystem;
using AscNet.Table.V2.share.reward;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // BigWorld reward ids (BigWorldReward.SubIds -> BigWorldRewardGoods), never share/reward/Reward.tsv
    // (client XBigWorldServiceAgency:GetDlcRewardSubIds). Idempotence is the caller's job.
    internal static class BigWorldRewardService
    {
        private static readonly Lazy<Dictionary<int, BigWorldRewardTable>> Rewards = new(() =>
            TableReaderV2.Parse<BigWorldRewardTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<int, BigWorldRewardGoodsTable>> Goods = new(() =>
            TableReaderV2.Parse<BigWorldRewardGoodsTable>().ToDictionary(row => row.Id));

        // Goods whose state lives in a BigWorld slice, not in main-game inventory/character documents.
        // Returned but NOT applied here: the owning slice applies them from the returned list
        // (SgDorm furniture and coatings -> SkyGardenDorm, DlcQuestItem -> Core quest bag,
        // DIY parts -> Character CommanderFashionBags, photo filters/actions -> Archive album).
        private static readonly Lazy<HashSet<int>> SliceOwnedTemplateIds = new(() =>
            TableReaderV2.Parse<SgDormFurnitureTable>().Select(row => row.Id)
                .Concat(TableReaderV2.Parse<SgDormFashionTable>().Select(row => row.Id))
                .Concat(TableReaderV2.Parse<DlcQuestItemTable>().Select(row => row.Id))
                .Concat(TableReaderV2.Parse<BigWorldDIYPartTable>().Select(row => row.Id))
                .Concat(TableReaderV2.Parse<BigWorldPhotographFiltersTable>().Select(row => row.Id))
                .Concat(TableReaderV2.Parse<BigWorldPhotographActionsTable>().Select(row => row.Id))
                .ToHashSet());

        internal static bool Exists(int bigWorldRewardId) => Rewards.Value.ContainsKey(bigWorldRewardId);

        internal static bool IsSliceOwned(int templateId) => SliceOwnedTemplateIds.Value.Contains(templateId);

        // Applies main-game goods (items incl. BigWorldItem, head portraits, nameplates, emojis, chat boards ...)
        // through RewardHandler, saves and pushes their notifies, and returns every goods row (slice-owned ones
        // unapplied, see IsSliceOwned) in table order.
        internal static List<RewardGoods> Grant(Session session, int bigWorldRewardId)
        {
            if (!Rewards.Value.TryGetValue(bigWorldRewardId, out BigWorldRewardTable? reward))
                throw new InvalidDataException($"BigWorldReward {bigWorldRewardId} does not exist.");

            List<BigWorldRewardGoodsTable> rows = reward.SubIds
                .Select(id => Goods.Value.TryGetValue(id, out BigWorldRewardGoodsTable? row)
                    ? row
                    : throw new InvalidDataException($"BigWorldReward {bigWorldRewardId} references missing goods {id}."))
                .ToList();

            List<RewardGoodsTable> applied = rows.Where(row => !IsSliceOwned(row.TemplateId))
                .Select(row => new RewardGoodsTable { Id = row.Id, TemplateId = row.TemplateId, Count = row.Count })
                .ToList();
            RewardApplicationResult result = RewardHandler.ApplyRewards(applied, session);
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            result.SendPushes(session);

            Dictionary<int, RewardGoods> appliedById = result.RewardGoods.GroupBy(goods => goods.Id)
                .ToDictionary(group => group.Key, group => group.First());
            return rows.Select(row => appliedById.TryGetValue(row.Id, out RewardGoods? goods)
                    ? goods
                    : new RewardGoods
                    {
                        Id = row.Id,
                        TemplateId = row.TemplateId,
                        Count = row.Count,
                        // AscNet policy: slice-owned goods reuse the main-game TemplateId/1e6 type encoding;
                        // the client displays BigWorld goods by TemplateId (BigWorldRewardGoodDetails), not RewardType.
                        RewardType = (int)(RewardHandler.GetRewardType(new RewardGoodsTable { Id = row.Id, TemplateId = row.TemplateId }) ?? 0)
                    })
                .ToList();
        }
    }
}
