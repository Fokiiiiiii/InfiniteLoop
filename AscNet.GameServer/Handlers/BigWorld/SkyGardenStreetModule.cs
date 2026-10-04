using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.bigworld.skygarden.shoppingstreet;

namespace AscNet.GameServer.Handlers.BigWorld
{
    /// <summary>
    /// Babylonia shopping street (client xskygardenshoppingstreet). The server owns the economy; retail's simulation
    /// is unobserved, so everything below is AscNet policy driven by the SgStreet* tables and one persisted
    /// SplitMix64 state (SgStreetPrivateState.Rng, seeded at stage start from player id, stage id and start count).
    ///
    /// AscNet policy summary:
    /// - Turns run 1..MaxTurn; OperatingStart is refused once Turn reaches MaxTurn (the client then sends WinSettle).
    /// - Customers per round = floor((InitCustomerNum + Σ ShopLv.CustomerNumFixed + attr101) × (10000 + Σ ShopLv.CustomerNumMultiple + attr102) / 10000),
    ///   environment likewise (InitEnvironment, EnvironmentFixed/Multiple, attr103/104) — the client CalculateProperty formula.
    ///   attr = Type-1 buffs + ShopLvBranchAdd.AttrTypes of chosen branches (pushed as NotifySgStreetAttrAdds so the client agrees).
    /// - Satisfaction = clamp((ShopScoreSat + EnvSat + attr105) × (10000 + attr106) / 10000, 0, MaxSatisfactionSatisfaction), where
    ///   EnvSat = SgStreetEnvironmentSatisfaction(environment) and ShopScoreSat = SgStreetShopScoreSatisfaction(avg score of built
    ///   SatisfactionShopSubType shops) × SgStreetSatisfactionFactor(#those subtypes offered by the stage but not built) / 10000.
    /// - Customer preferences ("Like" data) are rolled per shop on build: every value is the rounded mean of CustomerParam.*LikeRandom
    ///   uniform draws in the table range (Food chef uniform, grocery GroceryGoodsRandomNum liked goods, dessert order a shuffle).
    /// - Shop score (×10000, ≤ MaxShopScore) = SgStreetCustomerShopScore(goods mismatch type 1/3/5) + (gold distance type 2/4/6):
    ///   food mismatch Σ|count−like| + chef miss; grocery liked goods missing from the shelf, gold Σ|diff| over matched;
    ///   dessert inversions against the liked order. Scores are computed at OperatingStart and published at settle.
    /// - Each customer independently visits every built shop with chance = ShopLv.CustomerFactor + ShopScoreFactor(score)
    ///   + shop attr 1, × (10000 + shop attr 2), + recommend bonus (ShopLv.RecommendCustomerFactor + attr204); a customer who
    ///   visits nothing gets one Fake command. Visit gold = shop price (food/dessert Gold, one random shelf GoldCount, else
    ///   ShopLv.AwardGold) + shop attr 3 (+ attr203 when recommended), × (10000 + shop attr 4). Empty grocery shelves sell nothing.
    /// - Events per round from SgStreetCustomerEventNum (by customer count) scaled by attr201/202 and capped by
    ///   SgStreetCustomerEventNumLimit (by built inside/outside shop count); feedback counts per food/grocery/dessert with the
    ///   negative count capped by SgStreetFeedbackBadNum(score). Each command carries at most one event.
    /// - Settle recomputes gold server-side (client AwardGold is ignored): shop gold of all non-fake commands, DiscontentAwardGold of
    ///   handled discontent events, and Type-2 buff gold gained during settle. Unhandled events give nothing.
    /// - Buffs: Duration 0 = permanent (instant types 2/3-7/9 are applied and not kept); RemainingTurn ticks down at settle;
    ///   Type 8 re-adds its buffs every Params[0] turns; Type 10 = shop attr by subtype; Type 3-7 re-roll preferences.
    /// - Weighted picks use First/Repeat/Selected weights by whether the id was already picked this stage, plus AddWeigh for each
    ///   AddWeighCondition that passes (BigWorldConditionService). Billboards refresh every BillboardRefreshTurn turns;
    ///   turn promotions every PromotionInterval turns after FirstOutsideBuildTurn + PromotionStartTurnAfterOutsideBuild;
    ///   one news per turn; grapevine chance from SgStreetGrapevineGroup by turns since the last grapevine.
    /// - Task ConditionType semantics derived from SgStreetTask.ConditionDesc (see TaskValue). Stage-target tasks finish
    ///   automatically; billboard tasks become Achieved and are finished through SgStreetFinishTasksRequest.
    /// </summary>
    internal static class SkyGardenStreetModule
    {
        // CodeText ids (share/text/CodeText.tsv).
        internal const int StageNotStart = 20243002;          // SgStreetStageNotStart
        internal const int StageIsOngoing = 20243003;         // SgStreetStageIsOngoing
        internal const int StageIsNotCompleted = 20243004;    // SgStreetStageIsNotCompleted
        internal const int StageIsCompleted = 20243005;       // SgStreetStageIsCompleted
        internal const int StageCfgNotFound = 20243006;       // SgStreetStageCfgNotFound
        internal const int ConditionError = 20243007;         // SgStreetConditionError
        internal const int ResourceNotEnough = 20243009;      // SgStreetResourceNotEnough
        internal const int OperatingDataIsExist = 20243011;   // SgStreetOperatingDataIsExist
        internal const int OperatingDataNotExist = 20243012;  // SgStreetOperatingDataNotExist
        internal const int ShopDataNotExist = 20243013;       // SgStreetShopDataNotExist
        internal const int ShopDataIsExist = 20243014;        // SgStreetShopDataIsExist
        internal const int ShopLvCfgNotFound = 20243016;      // SgStreetShopLvCfgNotFound
        internal const int ShopBuildInCd = 20243022;          // SgStreetShopBuildInCd
        internal const int BuildOutsideTimesOver = 20243023;  // SgStreetShopBuildOutsideTimesOver
        internal const int BuildOutsideNotSatisfaction = 20243024; // SgStreetShopBuildOutsideNotSatisfaction
        internal const int BuildInvalidShopId = 20243025;     // SgStreetShopBuildInvalidShopId
        internal const int BuildInvalidPosition = 20243026;   // SgStreetShopBuildInvalidPosition
        internal const int BuildPositionUsed = 20243027;      // SgStreetShopBuildPositionUsed
        internal const int RemoveInvalidType = 20243029;      // SgStreetShopRemoveInvalidType
        internal const int RemoveCountLimit = 20243030;       // SgStreetShopRemoveCountLimit
        internal const int UpgradeInvalidBranchId = 20243031; // SgStreetShopUpgradeInvalidBranchId
        internal const int UpgradeCustomerNumNotEnough = 20243032; // SgStreetShopUpgradeCustomerNumNotEnough
        internal const int FoodTypeError = 20243033;          // SgStreetShopSetupFoodTypeError
        internal const int FoodInvalidChef = 20243034;        // SgStreetShopSetupFoodInvalidChef
        internal const int FoodInvalidGoodsCount = 20243036;  // SgStreetShopSetupFoodInvalidGoodsCount
        internal const int FoodInvalidGold = 20243037;        // SgStreetShopSetupFoodInvalidGold
        internal const int GroceryTypeError = 20243038;       // SgStreetShopSetupGroceryTypeError
        internal const int GroceryInvalidGoods = 20243039;    // SgStreetShopSetupGroceryInvalidGoods
        internal const int GroceryInvalidGoodsCount = 20243040; // SgStreetShopSetupGroceryInvalidGoodsCount
        internal const int GroceryGoodsRepeat = 20243041;     // SgStreetShopSetupGroceryGoodsRepeat
        internal const int GroceryInvalidGold = 20243042;     // SgStreetShopSetupGroceryInvalidGold
        internal const int DessertTypeError = 20243043;       // SgStreetShopSetupDessertTypeError
        internal const int DessertInvalidGoods = 20243044;    // SgStreetShopSetupDessertInvalidGoods
        internal const int DessertInvalidGoodsCount = 20243045; // SgStreetShopSetupDessertInvalidGoodsCount
        internal const int DessertInvalidGold = 20243046;     // SgStreetShopSetupDessertInvalidGold
        internal const int SetRecommendTypeError = 20243047;  // SgStreetShopSetRecommendTypeError
        internal const int PromoSelectGroupNotExist = 20243053; // SgStreetPromoSelectGroupNotExist
        internal const int PromoSelectInvalidIndex = 20243054;  // SgStreetPromoSelectInvalidIndex
        internal const int EmergencyOptionInvalid = 20243076; // SgStreetEventEmergencyOptionInvalid
        internal const int EventDataIsNull = 20243078;        // SgStreetCommandDataEventDataIsNull
        internal const int TaskFinishInvalidSource = 20243085; // SgStreetTaskFinishInvalidSource
        internal const int TaskFinishInvalidState = 20243086;  // SgStreetTaskFinishInvalidState
        internal const int BillboardIdNotFound = 20243094;    // SgStreetBillboardIdNotFound
        internal const int BillboardDataNotInit = 20243095;   // SgStreetBillboardDataNotInit
        internal const int TaskIdNotFound = 20243096;         // SgStreetTaskIdNotFound
        internal const int StageDataIsNull = 20243098;        // SgStreetStageDataIsNull
        internal const int StageNotBillboardSelect = 20243100; // SgStreetStageNotBillboardSelect

        // Client Agency enums.
        private const int GoldResourceId = 1, Inside = 1, Outside = 2, Food = 101, Grocery = 102, Dessert = 103;
        private const int CmdInside = 1, CmdOutside = 2, CmdFake = 3, EvDiscontent = 1, EvEmergency = 2, EvFeedBack = 3;
        private const int PromoTurnBase = 1, PromoShopBuild = 2, TaskActivated = 1, TaskAchieved = 2, TaskFinished = 3;
        private const int SourceStageTarget = 1, SourceBillboard = 2, Denominator = 10000;

        #region Tables
        private static class T
        {
            internal static readonly Lazy<Dictionary<int, SgStreetStageTable>> Stage = Dict<SgStreetStageTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetStageShopTable>> StageShop = Dict<SgStreetStageShopTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetShopTable>> Shop = Dict<SgStreetShopTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<(int, int), SgStreetShopLvTable>> ShopLv = new(() => TableReaderV2.Parse<SgStreetShopLvTable>().ToDictionary(r => (r.ShopId, r.Level)));
            internal static readonly Lazy<Dictionary<int, SgStreetShopLvBranchAddTable>> Branch = Dict<SgStreetShopLvBranchAddTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetBuffTable>> Buff = Dict<SgStreetBuffTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetTaskTable>> Task = Dict<SgStreetTaskTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetBillboardTable>> Billboard = Dict<SgStreetBillboardTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetPromotionTable>> Promotion = Dict<SgStreetPromotionTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetNewsTable>> News = Dict<SgStreetNewsTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetCustomerEventEmergencyTable>> Emergency = Dict<SgStreetCustomerEventEmergencyTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetCustomerParamTable>> CustomerParam = Dict<SgStreetCustomerParamTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetInsideShopFoodTable>> FoodShop = Dict<SgStreetInsideShopFoodTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetInsideShopFoodGoodsTable>> FoodGoods = Dict<SgStreetInsideShopFoodGoodsTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetInsideShopGroceryTable>> GroceryShop = Dict<SgStreetInsideShopGroceryTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetInsideShopGroceryGoodsTable>> GroceryGoods = Dict<SgStreetInsideShopGroceryGoodsTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<int, SgStreetInsideShopDessertTable>> DessertShop = Dict<SgStreetInsideShopDessertTable>(r => r.Id);
            internal static readonly Lazy<Dictionary<string, List<string>>> Config = new(() => TableReaderV2.Parse<SgStreetConfigTable>().ToDictionary(r => r.Key, r => r.Values));
            internal static readonly Lazy<List<SgStreetCustomerTable>> Customer = List<SgStreetCustomerTable>();
            internal static readonly Lazy<List<SgStreetCustomerShopScoreTable>> ShopScore = List<SgStreetCustomerShopScoreTable>();
            internal static readonly Lazy<List<SgStreetCustomerShopScoreFactorTable>> ScoreFactor = List<SgStreetCustomerShopScoreFactorTable>();
            internal static readonly Lazy<List<SgStreetShopScoreSatisfactionTable>> ScoreSatisfaction = List<SgStreetShopScoreSatisfactionTable>();
            internal static readonly Lazy<List<SgStreetEnvironmentSatisfactionTable>> EnvSatisfaction = List<SgStreetEnvironmentSatisfactionTable>();
            internal static readonly Lazy<List<SgStreetSatisfactionFactorTable>> SatisfactionFactor = List<SgStreetSatisfactionFactorTable>();
            internal static readonly Lazy<List<SgStreetOutSideShopBuildConditionTable>> OutsideBuild = List<SgStreetOutSideShopBuildConditionTable>();
            internal static readonly Lazy<List<SgStreetCustomerEventNumTable>> EventNum = List<SgStreetCustomerEventNumTable>();
            internal static readonly Lazy<List<SgStreetCustomerEventNumLimitTable>> EventNumLimit = List<SgStreetCustomerEventNumLimitTable>();
            internal static readonly Lazy<List<SgStreetCustomerEventEmergencyRandomTable>> EmergencyRandom = List<SgStreetCustomerEventEmergencyRandomTable>();
            internal static readonly Lazy<List<SgStreetFeedbackTable>> Feedback = List<SgStreetFeedbackTable>();
            internal static readonly Lazy<List<SgStreetFeedbackGroupTable>> FeedbackGroup = List<SgStreetFeedbackGroupTable>();
            internal static readonly Lazy<List<SgStreetFeedbackBadNumTable>> FeedbackBadNum = List<SgStreetFeedbackBadNumTable>();
            internal static readonly Lazy<List<SgStreetBillboardGroupTable>> BillboardGroup = List<SgStreetBillboardGroupTable>();
            internal static readonly Lazy<List<SgStreetPromotionRandomTable>> PromotionRandom = List<SgStreetPromotionRandomTable>();
            internal static readonly Lazy<List<SgStreetNewsGroupTable>> NewsGroup = List<SgStreetNewsGroupTable>();
            internal static readonly Lazy<List<SgStreetNewsTypeGroupTable>> NewsTypeGroup = List<SgStreetNewsTypeGroupTable>();
            internal static readonly Lazy<List<SgStreetGrapevineTable>> Grapevine = List<SgStreetGrapevineTable>();
            internal static readonly Lazy<List<SgStreetGrapevineGroupTable>> GrapevineGroup = List<SgStreetGrapevineGroupTable>();
            internal static readonly Lazy<List<SgStreetGrapevineRandomGroupTable>> GrapevineRandom = List<SgStreetGrapevineRandomGroupTable>();
            internal static readonly Lazy<Dictionary<int, SgStreetStageDescTable>> StageDesc = Dict<SgStreetStageDescTable>(r => r.Id);
            internal static readonly Lazy<List<SgStreetReviewDistributionTable>> ReviewDistribution = List<SgStreetReviewDistributionTable>();
            internal static readonly Lazy<List<SgStreetReviewGroupTable>> ReviewGroup = List<SgStreetReviewGroupTable>();
            internal static readonly Lazy<int> GoldMax = new(() => TableReaderV2.Parse<SgStreetResourceTable>().Single(r => r.Id == GoldResourceId).MaxCount);

            private static Lazy<Dictionary<int, TRow>> Dict<TRow>(Func<TRow, int> key) where TRow : ITable => new(() => TableReaderV2.Parse<TRow>().ToDictionary(key));
            private static Lazy<List<TRow>> List<TRow>() where TRow : ITable => new(() => TableReaderV2.Parse<TRow>());
        }

        private static int Cfg(string key, int index = 0) =>
            T.Config.Value.TryGetValue(key, out List<string>? values) && values.Count > index ? int.Parse(values[index]) : throw new InvalidDataException($"SgStreetConfig {key}[{index}] missing.");

        private static List<int> CfgList(string key) => T.Config.Value[key].Where(v => v.Length > 0).Select(int.Parse).ToList();

        // Table cells above int.MaxValue are uint-encoded negatives (e.g. 4294966296 = -1000).
        private static int Signed(string? value) => string.IsNullOrEmpty(value) ? 0 : unchecked((int)long.Parse(value));

        private static List<int> BuffParams(SgStreetBuffTable buff) => buff.Params.Select(Signed).ToList();

        // Row with the greatest key ≤ x (first row when x is below every key).
        private static TRow Range<TRow>(IEnumerable<TRow> rows, Func<TRow, int> key, int x)
        {
            List<TRow> ordered = rows.OrderBy(key).ToList();
            if (ordered.Count == 0)
                throw new InvalidDataException($"{typeof(TRow).Name} range is empty.");
            return ordered.LastOrDefault(row => key(row) <= x) ?? ordered[0];
        }
        #endregion

        #region Random
        private static ulong Next(SgStreetPrivateState pv)
        {
            ulong z = unchecked((ulong)(pv.Rng += unchecked((long)0x9E3779B97F4A7C15UL)));
            z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
            z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
            return z ^ (z >> 31);
        }

        private static int Roll(SgStreetPrivateState pv, int min, int maxInclusive) =>
            maxInclusive <= min ? min : min + (int)(Next(pv) % (ulong)(maxInclusive - min + 1));

        private static bool Chance(SgStreetPrivateState pv, int per10000) => per10000 > 0 && Roll(pv, 0, Denominator - 1) < per10000;

        // Rounded mean of `draws` uniform rolls (draws ≤ 1 = one uniform roll).
        private static int MeanRoll(SgStreetPrivateState pv, int min, int max, int draws)
        {
            int n = Math.Max(1, draws), sum = 0;
            for (int i = 0; i < n; i++)
                sum += Roll(pv, min, max);
            return (int)Math.Round(sum / (double)n, MidpointRounding.AwayFromZero);
        }

        private static T? Weighted<T>(SgStreetPrivateState pv, IEnumerable<(T Item, int Weight)> source)
        {
            List<(T Item, int Weight)> items = source.Where(entry => entry.Weight > 0).ToList();
            int total = items.Sum(entry => entry.Weight);
            if (total <= 0)
                return default;
            int roll = Roll(pv, 0, total - 1);
            foreach ((T item, int weight) in items)
            {
                if (roll < weight)
                    return item;
                roll -= weight;
            }
            return default;
        }

        private static T Pick<T>(SgStreetPrivateState pv, IReadOnlyList<T> items) => items[Roll(pv, 0, items.Count - 1)];

        private static bool Picked(SgStreetPrivateState pv, string kind, int id) => pv.Picked.Contains($"{kind}:{id}");

        private static void MarkPicked(SgStreetPrivateState pv, string kind, int id)
        {
            if (!Picked(pv, kind, id))
                pv.Picked.Add($"{kind}:{id}");
        }

        private static int WithConditions(Player player, int weight, IReadOnlyList<int> conditions, IReadOnlyList<int> adds)
        {
            for (int i = 0; i < conditions.Count; i++)
                if (conditions[i] != 0 && i < adds.Count && BigWorldConditionService.Check(player, conditions[i]))
                    weight += adds[i];
            return Math.Max(0, weight);
        }
        #endregion

        #region Conditions
        internal static void RegisterConditions()
        {
            // XSkyGardenShoppingStreetAgency:InitConditionCheck (834-868). Params[0] of compare types follows XTool.CommonVariableCompare.
            BigWorldConditionService.Register(10202001, (p, a) => WithStage(p, s => Compare(a, PropertyValue(s, At(a, 1)), 2)));
            BigWorldConditionService.Register(10202002, (p, a) => WithStage(p, s => Compare(a, At(a, 1) == GoldResourceId ? Gold(s) : 0, 2)));
            BigWorldConditionService.Register(10202003, (p, a) => WithStage(p, s => ShopBuildState(s, a)));
            BigWorldConditionService.Register(10202004, (p, a) => WithStage(p, s => At(a, 0) == 1
                ? a.Skip(1).All(id => s.BuffDatas.Any(b => b.BuffConfigId == id))
                : a.Skip(1).Any(id => s.BuffDatas.Any(b => b.BuffConfigId == id))));
            // Client compares area:GetShopScore() (Score / 10000) against Params[0].
            BigWorldConditionService.Register(10202005, (p, a) => WithStage(p, s => s.ShopDatas.Count(shop => shop.Score >= At(a, 0) * Denominator) >= At(a, 1)));
            BigWorldConditionService.Register(10202006, (p, a) => WithStage(p, s => s.ShopDatas.Count(shop =>
                (shop.MainType == At(a, 2) || At(a, 2) == 3) && shop.Level >= At(a, 0)) >= At(a, 1)));
            BigWorldConditionService.Register(10202007, (p, a) => WithStage(p, s => s.StatisticsData.PromotionTimes >= At(a, 0)));
            BigWorldConditionService.Register(10202008, (p, a) => WithStage(p, s => s.StatisticsData.DiscontentEventTimes >= At(a, 0)));
            BigWorldConditionService.Register(10202009, (p, a) => WithStage(p, s => s.StatisticsData.EmergencyEventTimes >= At(a, 0)));
            BigWorldConditionService.Register(10202010, (p, a) => WithStage(p, s => FinishTimes(s, At(a, 1)) >= At(a, 0)));
            BigWorldConditionService.Register(10202011, (p, a) => WithStage(p, s => s.StatisticsData.MaxDailyGold >= At(a, 0)));
            BigWorldConditionService.Register(10202012, (p, a) => WithStage(p, s => Compare(a, s.Turn, 1)));
            BigWorldConditionService.Register(10202013, (p, a) => WithStage(p, s => At(a, 0) == 1
                ? a.Skip(1).Any(s.StatisticsData.AllGrapevineIds.Contains)
                : a.Skip(1).All(s.StatisticsData.AllGrapevineIds.Contains)));
            BigWorldConditionService.Register(10202014, (p, a) => WithStage(p, s => s.ShopDatas.Any(shop =>
                shop.FoodData is not null && shop.FoodLikeData is not null && shop.FoodData.ChefId == shop.FoodLikeData.ChefId)));
            BigWorldConditionService.Register(10202015, (p, a) => WithStage(p, s => s.ShopDatas.Any(shop =>
                shop.FoodData is not null && shop.FoodLikeData is not null
                && Compare(a, shop.FoodData.GoodsCountList.Where((count, i) => i < shop.FoodLikeData.GoodsCountList.Count && count == shop.FoodLikeData.GoodsCountList[i]).Count(), 1))));
            BigWorldConditionService.Register(10202016, (p, a) => WithStage(p, s => s.ShopDatas.Any(shop =>
                shop.FoodData is not null && shop.FoodLikeData is not null && Compare(a, shop.FoodData.Gold - shop.FoodLikeData.Gold, 1))));
            BigWorldConditionService.Register(10202017, (p, a) => WithStage(p, s => s.ShopDatas.Any(shop =>
                shop.GroceryData is not null && shop.GroceryLikeData is not null
                && Compare(a, shop.GroceryLikeData.ShelfDatas.Count(like => shop.GroceryData.ShelfDatas.Any(set => set.GoodsId == like.GoodsId)), 1))));
            BigWorldConditionService.Register(10202018, (p, a) => WithStage(p, s => s.ShopDatas.Any(shop =>
                shop.GroceryData is not null && shop.GroceryLikeData is not null
                && Compare(a, shop.GroceryLikeData.ShelfDatas.Count(like => shop.GroceryData.ShelfDatas.Any(set => set.GoodsId == like.GoodsId && set.GoldCount != like.GoldCount)), 1))));
            BigWorldConditionService.Register(10202019, (p, a) => WithStage(p, s => s.ShopDatas.Any(shop =>
                shop.DessertData is not null && shop.DessertLikeData is not null && Compare(a, shop.DessertData.Gold - shop.DessertLikeData.Gold, 1))));
            BigWorldConditionService.Register(10202020, (p, a) => WithStage(p, s => s.ShopDatas.Any(shop =>
                shop.DessertData is not null && Compare(a, Inversions(shop.DessertData.GoodsIdList), 1))));
            BigWorldConditionService.Register(10202021, (p, a) => WithStage(p, s => s.StageId == At(a, 0) && (At(a, 1) == 0 || s.Turn == At(a, 1))));
            // AscNet policy: the client's "running" UI state is server-side "an OperatingData round is open".
            BigWorldConditionService.Register(10202022, (p, a) => WithStage(p, s => (s.OperatingData is not null ? 1 : 0) == At(a, 0)));
            BigWorldConditionService.Register(10202024, (p, a) => WithStage(p, s => Compare(a, Gold(s), 1)));
            BigWorldConditionService.Register(10202025, (p, a) => WithStage(p, s => Compare(a, Satisfaction(s), 1)));
            // AscNet policy: an emergency bubble exists while the open round carries an emergency event.
            BigWorldConditionService.Register(10202026, (p, a) => WithStage(p, s =>
                (s.OperatingData?.CustomerDatas.SelectMany(c => c.CommandDatas).Any(c => c.EventData?.Type == EvEmergency) ?? false) == (At(a, 0) != 0)));
            // AscNet policy: "current promotion type" = a pending PromotionSelectGroup of that type.
            BigWorldConditionService.Register(10202027, (p, a) => WithStage(p, s => s.PromotionSelectGroups.Any(g => g.Type == At(a, 0))));
            // 10202028 (no EN Lua registration): quest gate "stage P1 cleared".
            BigWorldConditionService.Register(10202028, (p, a) => p.BigWorldState.SgStreet.PassedStageRecords.ContainsKey(At(a, 0)));
        }

        private static int At(IReadOnlyList<int> values, int index) => index < values.Count ? values[index] : 0;

        private static bool WithStage(Player player, Func<SgStreetStageData, bool> check) =>
            player.BigWorldState.SgStreet.CurStageData is { } stage && check(stage);

        private static bool Compare(IReadOnlyList<int> args, int own, int valueIndex) => At(args, 0) switch
        {
            1 => own == At(args, valueIndex),
            2 => own >= At(args, valueIndex),
            3 => own <= At(args, valueIndex),
            4 => args.Skip(valueIndex).Contains(own),
            _ => false
        };

        // AscNet policy: "built" means Level > 0 (the client also counts its level-0 outside placeholders).
        private static bool ShopBuildState(SgStreetStageData stage, IReadOnlyList<int> args)
        {
            SgStreetStageShopTable group = T.StageShop.Value[stage.StageId];
            HashSet<int> subTypes = args.Skip(1).ToHashSet();
            IEnumerable<int> shops = group.InsideShopGroup.Concat(group.OutsideShopGroup).Where(id => id > 0 && subTypes.Contains(T.Shop.Value[id].SubType));
            return At(args, 0) == 1 ? shops.All(id => FindShop(stage, id) is not null) : shops.All(id => FindShop(stage, id) is null);
        }

        private static int FinishTimes(SgStreetStageData stage, int source) => source == 0
            ? stage.StatisticsData.FinishTaskTimeDict.GetValueOrDefault(SourceStageTarget) + stage.StatisticsData.FinishTaskTimeDict.GetValueOrDefault(SourceBillboard)
            : stage.StatisticsData.FinishTaskTimeDict.GetValueOrDefault(source);

        private static int Inversions(IReadOnlyList<int> values)
        {
            int count = 0;
            for (int i = 0; i < values.Count; i++)
                for (int j = i + 1; j < values.Count; j++)
                    if (values[i] > values[j])
                        count++;
            return count;
        }
        #endregion

        #region Derived values
        private static SgStreetShopData? FindShop(SgStreetStageData stage, int shopId) => stage.ShopDatas.FirstOrDefault(shop => shop.ShopId == shopId);

        private static int Gold(SgStreetStageData stage) => stage.ResourceDatas.FirstOrDefault(r => r.ResourceId == GoldResourceId)?.Count ?? 0;

        private static void AddGold(SgStreetStageData stage, int delta)
        {
            SgStreetResourceData gold = stage.ResourceDatas.First(r => r.ResourceId == GoldResourceId);
            gold.Count = Math.Clamp(gold.Count + delta, 0, T.GoldMax.Value);
        }

        private static SgStreetShopLvTable Lv(SgStreetShopData shop) => T.ShopLv.Value[(shop.ShopId, shop.Level)];

        internal static Dictionary<int, int> AttrAdds(SgStreetStageData stage)
        {
            Dictionary<int, int> adds = new();
            foreach (SgStreetBuffData buff in stage.BuffDatas)
            {
                SgStreetBuffTable row = T.Buff.Value[buff.BuffConfigId];
                if (row.Type == 1)
                {
                    List<int> p = BuffParams(row);
                    adds[p[0]] = adds.GetValueOrDefault(p[0]) + p[1];
                }
            }
            foreach (int branchId in stage.ShopDatas.SelectMany(shop => shop.BranchIds))
            {
                SgStreetShopLvBranchAddTable branch = T.Branch.Value[branchId];
                for (int i = 0; i < branch.AttrTypes.Count; i++)
                    if (branch.AttrTypes[i] != 0)
                        adds[branch.AttrTypes[i]] = adds.GetValueOrDefault(branch.AttrTypes[i]) + branch.AttrValues[i];
            }
            return adds;
        }

        // subType -> shopAttrType -> value, from Type-10 buffs (Params: shopAttrType, value, subTypes...).
        internal static Dictionary<int, Dictionary<int, int>> ShopAttrAdds(SgStreetStageData stage)
        {
            Dictionary<int, Dictionary<int, int>> adds = new();
            foreach (SgStreetBuffData buff in stage.BuffDatas)
            {
                SgStreetBuffTable row = T.Buff.Value[buff.BuffConfigId];
                if (row.Type != 10)
                    continue;
                List<int> p = BuffParams(row);
                foreach (int subType in p.Skip(2).Where(v => v != 0))
                {
                    Dictionary<int, int> bySub = adds.TryGetValue(subType, out Dictionary<int, int>? existing) ? existing : adds[subType] = new();
                    bySub[p[0]] = bySub.GetValueOrDefault(p[0]) + p[1];
                }
            }
            return adds;
        }

        private static int ShopAttr(SgStreetStageData stage, SgStreetShopData shop, int attrType)
        {
            int value = ShopAttrAdds(stage).GetValueOrDefault(T.Shop.Value[shop.ShopId].SubType)?.GetValueOrDefault(attrType) ?? 0;
            foreach (int branchId in shop.BranchIds)
            {
                SgStreetShopLvBranchAddTable branch = T.Branch.Value[branchId];
                for (int i = 0; i < branch.ShopAttrTypes.Count; i++)
                    if (branch.ShopAttrTypes[i] == attrType)
                        value += branch.ShopAttrValues[i];
            }
            return value;
        }

        private static int ScaledCost(int cost, int ratioAdd) => (int)((long)cost * (Denominator + Math.Min(Denominator, ratioAdd)) / Denominator);

        private static int CustomerCount(SgStreetStageData stage)
        {
            Dictionary<int, int> a = AttrAdds(stage);
            SgStreetStageTable row = T.Stage.Value[stage.StageId];
            long fixedTotal = row.InitCustomerNum + stage.ShopDatas.Sum(s => Lv(s).CustomerNumFixed) + a.GetValueOrDefault(101);
            long ratio = Denominator + stage.ShopDatas.Sum(s => Lv(s).CustomerNumMultiple) + a.GetValueOrDefault(102);
            return (int)Math.Max(0, fixedTotal * ratio / Denominator);
        }

        private static int Environment(SgStreetStageData stage)
        {
            Dictionary<int, int> a = AttrAdds(stage);
            SgStreetStageTable row = T.Stage.Value[stage.StageId];
            long fixedTotal = row.InitEnvironment + stage.ShopDatas.Sum(s => Lv(s).EnvironmentFixed) + a.GetValueOrDefault(103);
            long ratio = Denominator + stage.ShopDatas.Sum(s => Lv(s).EnvironmentMultiple) + a.GetValueOrDefault(104);
            return (int)Math.Max(0, fixedTotal * ratio / Denominator);
        }

        private static int EnvironmentSatisfaction(SgStreetStageData stage) =>
            Range(T.EnvSatisfaction.Value, r => r.Environment, Environment(stage)).Satisfaction;

        private static int ShopScoreSatisfaction(SgStreetStageData stage, IReadOnlyDictionary<int, int> scores)
        {
            HashSet<int> subTypes = CfgList("SatisfactionShopSubType").ToHashSet();
            SgStreetStageShopTable group = T.StageShop.Value[stage.StageId];
            List<SgStreetShopData> built = stage.ShopDatas.Where(s => subTypes.Contains(T.Shop.Value[s.ShopId].SubType)).ToList();
            int offeredMissing = group.InsideShopGroup.Where(id => id > 0).Select(id => T.Shop.Value[id].SubType).Distinct()
                .Count(sub => subTypes.Contains(sub) && built.All(s => T.Shop.Value[s.ShopId].SubType != sub));
            int average = built.Count == 0 ? 0 : (int)built.Average(s => scores.GetValueOrDefault(s.ShopId));
            int satisfaction = Range(T.ScoreSatisfaction.Value, r => r.ShopScore, average).Satisfaction;
            return satisfaction * Range(T.SatisfactionFactor.Value, r => r.ShopNum, offeredMissing).Factor / Denominator;
        }

        private static int SatisfactionOf(SgStreetStageData stage, int shopSatisfaction, int environmentSatisfaction)
        {
            Dictionary<int, int> a = AttrAdds(stage);
            long value = (long)(shopSatisfaction + environmentSatisfaction + a.GetValueOrDefault(105)) * (Denominator + a.GetValueOrDefault(106)) / Denominator;
            return (int)Math.Clamp(value, 0, Cfg("MaxSatisfactionSatisfaction"));
        }

        private static int Satisfaction(SgStreetStageData stage) => SatisfactionOf(stage, stage.ShopScoreSatisfaction, stage.EnvironmentSatisfaction);

        // Client _AttrBaseToResType: 1 customers, 2 environment, 3 satisfaction, 4 env sat, 5 shop sat, 6 other sat; else attr add.
        private static int PropertyValue(SgStreetStageData stage, int property) => property switch
        {
            1 => CustomerCount(stage),
            2 => Environment(stage),
            3 => Satisfaction(stage),
            4 => stage.EnvironmentSatisfaction,
            5 => stage.ShopScoreSatisfaction,
            6 => Satisfaction(stage) - stage.EnvironmentSatisfaction - stage.ShopScoreSatisfaction,
            _ => AttrAdds(stage).GetValueOrDefault(property)
        };
        #endregion

        #region Preferences and scores
        private static int FuncType(SgStreetShopData shop) => T.Shop.Value[shop.ShopId].FuncType;

        private static SgStreetCustomerParamTable CustomerParam(SgStreetStageData stage) => T.CustomerParam.Value[T.Stage.Value[stage.StageId].CustomerParamId];

        private static void InitSetup(SgStreetShopData shop)
        {
            switch (FuncType(shop))
            {
                case Food:
                    SgStreetInsideShopFoodTable food = T.FoodShop.Value[shop.ShopId];
                    shop.FoodData = new SgStreetFoodData
                    {
                        ChefId = food.Chef[0],
                        GoodsCountList = food.Goods.Select(id => T.FoodGoods.Value[id].GoodsInit).ToList(),
                        Gold = food.InitGold
                    };
                    break;
                case Grocery:
                    shop.GroceryData = new SgStreetGroceryData();
                    break;
                case Dessert:
                    // Client GetDefaultShopShowData shows the dessert goods reversed.
                    SgStreetInsideShopDessertTable dessert = T.DessertShop.Value[shop.ShopId];
                    shop.DessertData = new SgStreetDessertData { GoodsIdList = dessert.Goods.AsEnumerable().Reverse().ToList(), Gold = dessert.InitGold };
                    break;
            }
        }

        private static bool RollLike(SgStreetStageData stage, SgStreetPrivateState pv, SgStreetShopData shop)
        {
            SgStreetCustomerParamTable param = CustomerParam(stage);
            switch (FuncType(shop))
            {
                case Food:
                    SgStreetInsideShopFoodTable food = T.FoodShop.Value[shop.ShopId];
                    shop.FoodLikeData = new SgStreetFoodData
                    {
                        ChefId = Pick(pv, food.Chef),
                        GoodsCountList = food.Goods.Select(id => MeanRoll(pv, T.FoodGoods.Value[id].GoodsMin, T.FoodGoods.Value[id].GoodsMax, param.FoodGoodsLikeRandom)).ToList(),
                        Gold = MeanRoll(pv, food.GoldMin, food.GoldMax, param.FoodGoldLikeRandom)
                    };
                    return true;
                case Grocery:
                    SgStreetInsideShopGroceryTable grocery = T.GroceryShop.Value[shop.ShopId];
                    List<int> pool = grocery.Goods.ToList();
                    List<SgStreetShelfData> liked = new();
                    for (int i = 0; i < Math.Min(Cfg("GroceryGoodsRandomNum"), grocery.Goods.Count); i++)
                    {
                        int goodsId = Pick(pv, pool);
                        pool.Remove(goodsId);
                        SgStreetInsideShopGroceryGoodsTable goods = T.GroceryGoods.Value[goodsId];
                        liked.Add(new SgStreetShelfData { GoodsId = goodsId, GoldCount = MeanRoll(pv, goods.GoldMin, goods.GoldMax, param.GroceryGoldLikeRandom) });
                    }
                    shop.GroceryLikeData = new SgStreetGroceryData { ShelfDatas = liked };
                    return true;
                case Dessert:
                    SgStreetInsideShopDessertTable dessert = T.DessertShop.Value[shop.ShopId];
                    List<int> order = dessert.Goods.ToList();
                    for (int i = order.Count - 1; i > 0; i--)
                    {
                        int j = Roll(pv, 0, i);
                        (order[i], order[j]) = (order[j], order[i]);
                    }
                    shop.DessertLikeData = new SgStreetDessertData { GoodsIdList = order, Gold = MeanRoll(pv, dessert.GoldMin, dessert.GoldMax, param.DessertGoldLikeRandom) };
                    return true;
                default:
                    return false;
            }
        }

        private static int ScoreRow(int type, int value) => Range(T.ShopScore.Value.Where(r => r.Type == type), r => r.Value, value).Score;

        private static bool IsScored(SgStreetShopData shop) => FuncType(shop) is Food or Grocery or Dessert;

        // Per goods id: signed setup-minus-preference distance; key 0 = price.
        private static Dictionary<int, int> Differences(SgStreetShopData shop)
        {
            Dictionary<int, int> diff = new();
            switch (FuncType(shop))
            {
                case Food when shop.FoodData is { } set && shop.FoodLikeData is { } like:
                    List<int> goods = T.FoodShop.Value[shop.ShopId].Goods;
                    for (int i = 0; i < goods.Count; i++)
                        diff[goods[i]] = set.GoodsCountList.ElementAtOrDefault(i) - like.GoodsCountList.ElementAtOrDefault(i);
                    diff[0] = set.Gold - like.Gold;
                    break;
                case Grocery when shop.GroceryData is { } set && shop.GroceryLikeData is { } like:
                    foreach (SgStreetShelfData liked in like.ShelfDatas)
                        diff[liked.GoodsId] = set.ShelfDatas.FirstOrDefault(s => s.GoodsId == liked.GoodsId) is { } shelf
                            ? shelf.GoldCount - liked.GoldCount
                            : -liked.GoldCount; // liked goods not stocked
                    diff[0] = diff.Values.Sum();
                    break;
                case Dessert when shop.DessertData is { } set && shop.DessertLikeData is { } like:
                    foreach (int goodsId in like.GoodsIdList)
                        diff[goodsId] = set.GoodsIdList.IndexOf(goodsId) - like.GoodsIdList.IndexOf(goodsId);
                    diff[0] = set.Gold - like.Gold;
                    break;
            }
            return diff;
        }

        internal static int ShopScore(SgStreetShopData shop)
        {
            int score = FuncType(shop) switch
            {
                Food when shop.FoodData is { } set && shop.FoodLikeData is { } like =>
                    ScoreRow(1, set.GoodsCountList.Select((count, i) => Math.Abs(count - like.GoodsCountList.ElementAtOrDefault(i))).Sum() + (set.ChefId == like.ChefId ? 0 : 1))
                    + ScoreRow(2, Math.Abs(set.Gold - like.Gold)),
                Grocery when shop.GroceryData is { } set && shop.GroceryLikeData is { } like =>
                    ScoreRow(3, like.ShelfDatas.Count(l => set.ShelfDatas.All(s => s.GoodsId != l.GoodsId)))
                    + ScoreRow(4, like.ShelfDatas.Sum(l => set.ShelfDatas.FirstOrDefault(s => s.GoodsId == l.GoodsId) is { } s ? Math.Abs(s.GoldCount - l.GoldCount) : 0)),
                Dessert when shop.DessertData is { } set && shop.DessertLikeData is { } like =>
                    ScoreRow(5, Inversions(set.GoodsIdList.Select(id => like.GoodsIdList.IndexOf(id)).ToList()))
                    + ScoreRow(6, Math.Abs(set.Gold - like.Gold)),
                _ => 0
            };
            return Math.Min(score, Cfg("MaxShopScore") * Denominator);
        }

        private static Dictionary<int, int> CurrentScores(SgStreetStageData stage) =>
            stage.ShopDatas.ToDictionary(s => s.ShopId, s => IsScored(s) ? ShopScore(s) : 0);
        #endregion

        #region Buffs
        private sealed class Changes
        {
            internal bool Buffs, Resource, Like, Statistics;
            internal int BuffGold;
            internal readonly List<SgStreetShopData> Shops = new();
            internal readonly List<SgStreetTaskData> Tasks = new();
            internal readonly List<int> RemovedTasks = new();
        }

        private static void AddBuff(Player player, SgStreetStageData stage, SgStreetPrivateState pv, int buffId, Changes changes)
        {
            if (buffId <= 0)
                return;
            SgStreetBuffTable row = T.Buff.Value.TryGetValue(buffId, out SgStreetBuffTable? found) ? found : throw new InvalidDataException($"SgStreetBuff {buffId} missing.");
            if (!BigWorldConditionService.Check(player, row.ConditionId))
                return;
            List<int> p = BuffParams(row);
            bool keep = row.Type is 1 or 8 or 10 || row.Duration > 0;
            switch (row.Type)
            {
                case 2:
                    AddGold(stage, p[0]);
                    changes.BuffGold += p[0];
                    changes.Resource = true;
                    break;
                case 3:
                    RerollLikes(stage, pv, stage.ShopDatas.Where(s => p.Contains(T.Shop.Value[s.ShopId].SubType)).ToList(), changes);
                    break;
                case 4:
                    // Params: count, subTypes...
                    List<SgStreetShopData> pool = stage.ShopDatas.Where(s => p.Skip(1).Contains(T.Shop.Value[s.ShopId].SubType)).ToList();
                    List<SgStreetShopData> chosen = new();
                    for (int i = 0; i < p[0] && pool.Count > 0; i++)
                    {
                        SgStreetShopData shop = Pick(pv, pool);
                        pool.Remove(shop);
                        chosen.Add(shop);
                    }
                    RerollLikes(stage, pv, chosen, changes);
                    break;
                case 5:
                case 6:
                case 7:
                    // AscNet policy: "change the preference of food/grocery/dessert shops" re-rolls those shops' preferences.
                    int func = row.Type == 5 ? Food : row.Type == 6 ? Grocery : Dessert;
                    RerollLikes(stage, pv, stage.ShopDatas.Where(s => FuncType(s) == func).ToList(), changes);
                    break;
                case 9:
                    changes.Buffs |= stage.BuffDatas.RemoveAll(b => p.Contains(b.BuffConfigId)) > 0;
                    break;
            }
            if (keep)
            {
                stage.BuffDatas.Add(new SgStreetBuffData { BuffUid = ++pv.NextUid, BuffConfigId = buffId, RemainingTurn = row.Duration, CreateTurn = stage.Turn });
                changes.Buffs = true;
            }
        }

        private static void RerollLikes(SgStreetStageData stage, SgStreetPrivateState pv, List<SgStreetShopData> shops, Changes changes)
        {
            foreach (SgStreetShopData shop in shops)
                if (RollLike(stage, pv, shop))
                {
                    changes.Like = true;
                    if (!changes.Shops.Contains(shop))
                        changes.Shops.Add(shop);
                }
        }

        private static void TickBuffs(Player player, SgStreetStageData stage, SgStreetPrivateState pv, Changes changes)
        {
            foreach (SgStreetBuffData buff in stage.BuffDatas.ToList())
            {
                SgStreetBuffTable row = T.Buff.Value[buff.BuffConfigId];
                if (row.Type == 8)
                {
                    List<int> p = BuffParams(row);
                    if (p[0] > 0 && (stage.Turn - buff.CreateTurn + 1) % p[0] == 0)
                        foreach (int added in p.Skip(1))
                            AddBuff(player, stage, pv, added, changes);
                }
                if (buff.RemainingTurn > 0 && --buff.RemainingTurn == 0)
                    stage.BuffDatas.Remove(buff);
                changes.Buffs = true;
            }
        }
        #endregion

        #region Tasks
        private static SgStreetTaskData NewTask(SgStreetStageData stage, SgStreetPrivateState pv, int configId, int source)
        {
            SgStreetTaskTable row = T.Task.Value[configId];
            SgStreetTaskData task = new() { Id = ++pv.NextUid, ConfigId = configId, State = TaskActivated, Source = source };
            pv.TaskBases[task.Id] = row.ConditionType switch
            {
                5 => pv.UpgradeTimes,
                6 => stage.StatisticsData.DiscontentEventTimes,
                7 => stage.StatisticsData.EmergencyEventTimes,
                _ => 0
            };
            stage.TaskDatas.Add(task);
            return task;
        }

        // SgStreetTask.ConditionType from ConditionDesc (AscNet policy; the type is server-only in retail).
        private static int TaskValue(SgStreetStageData stage, SgStreetPrivateState pv, SgStreetTaskData task)
        {
            SgStreetTaskTable row = T.Task.Value[task.ConfigId];
            IReadOnlyList<int> p = row.ConditionParams;
            int baseValue = pv.TaskBases.GetValueOrDefault(task.Id);
            return row.ConditionType switch
            {
                1 => PropertyValue(stage, At(p, 1)),                                                   // "Traffic/Atmosphere/Satisfaction reaches X"
                3 => stage.ShopDatas.Count(s => (At(p, 1) == 3 || s.MainType == At(p, 1)) && s.Level >= At(p, 2)), // "Upgrade N outer ring stores to Lv.X"
                4 => stage.ShopDatas.Where(s => T.Shop.Value[s.ShopId].SubType == At(p, 1)).Select(s => s.Score).DefaultIfEmpty(0).Max(), // "<shop> Rating reaches X"
                5 => pv.UpgradeTimes - baseValue,                                                      // "Upgrade store N times"
                6 => stage.StatisticsData.DiscontentEventTimes - baseValue,                            // "Collect customer tips N times"
                7 => stage.StatisticsData.EmergencyEventTimes - baseValue,                             // "Resolve N sudden events"
                9 => task.Schedule,                                                                    // "Single round income reaches X" (max round AwardGold, raised at settle)
                10 => FinishTimes(stage, At(p, 1)),                                                    // "Complete N Marquee missions"
                11 => stage.StatisticsData.AccumulativeGold,                                           // "Total income reaches X"
                _ => throw new InvalidDataException($"SgStreetTask {task.ConfigId} has unsupported ConditionType {row.ConditionType}.")
            };
        }

        private static void UpdateTasks(SgStreetStageData stage, SgStreetPrivateState pv, Changes changes)
        {
            foreach (SgStreetTaskData task in stage.TaskDatas.Where(t => t.State == TaskActivated))
            {
                int value = TaskValue(stage, pv, task);
                int state = value >= T.Task.Value[task.ConfigId].Schedule ? (task.Source == SourceStageTarget ? TaskFinished : TaskAchieved) : TaskActivated;
                if (value == task.Schedule && state == task.State)
                    continue;
                task.Schedule = value;
                task.State = state;
                if (state == TaskFinished)
                {
                    stage.StatisticsData.FinishTaskTimeDict[task.Source] = stage.StatisticsData.FinishTaskTimeDict.GetValueOrDefault(task.Source) + 1;
                    changes.Statistics = true;
                }
                changes.Tasks.Add(task);
            }
        }
        #endregion

        #region Turn events
        private static void RefreshBillboard(Player player, SgStreetStageData stage, SgStreetPrivateState pv, Changes changes)
        {
            int group = T.Stage.Value[stage.StageId].BillboardGroup;
            if (group <= 0)
                return;
            SgStreetBillboardData? data = stage.BillboardData;
            if (data is not null && stage.Turn < data.LastRefreshTurn + Cfg("BillboardRefreshTurn"))
                return;
            if (data is not null && data.CurrentTaskId != 0)
                ExpireBillboardTask(stage, pv, data, changes);
            List<SgStreetBillboardGroupTable> pool = T.BillboardGroup.Value.Where(r => r.GroupId == group).ToList();
            List<int> picks = new();
            for (int i = 0; i < Cfg("BillboardTaskGenNum"); i++)
            {
                SgStreetBillboardGroupTable? pick = Weighted(pv, pool.Where(r => !picks.Contains(r.BillboardId)).Select(r =>
                    (r, WithConditions(player, Picked(pv, "billboard", r.BillboardId) ? r.RepeatWeigh : r.Weigh, r.AddWeighCondition, r.AddWeigh.Select(Signed).ToList()))));
                if (pick is null)
                    break;
                picks.Add(pick.BillboardId);
                MarkPicked(pv, "billboard", pick.BillboardId);
            }
            stage.BillboardData = new SgStreetBillboardData { LastRefreshTurn = stage.Turn, RandomBillboards = picks };
        }

        private static void ExpireBillboardTask(SgStreetStageData stage, SgStreetPrivateState pv, SgStreetBillboardData data, Changes changes)
        {
            SgStreetBillboardTable billboard = T.Billboard.Value[data.CurrentBillboardId];
            if (stage.TaskDatas.RemoveAll(t => t.Id == data.CurrentTaskId) > 0)
                changes.RemovedTasks.Add(data.CurrentTaskId);
            pv.TaskBases.Remove(data.CurrentTaskId);
            changes.Buffs |= stage.BuffDatas.RemoveAll(b => billboard.RestrictBuff.Contains(b.BuffConfigId)) > 0;
            data.CurrentTaskId = 0;
        }

        private static SgStreetPromotionSelectGroup? AddPromotionGroup(Player player, SgStreetStageData stage, SgStreetPrivateState pv, int groupId, int type, int count)
        {
            List<SgStreetPromotionRandomTable> pool = T.PromotionRandom.Value.Where(r => r.GroupId == groupId).ToList();
            List<int> picks = new();
            for (int i = 0; i < count; i++)
            {
                SgStreetPromotionRandomTable? pick = Weighted(pv, pool.Where(r => !picks.Contains(r.PromotionId)).Select(r =>
                    (r, WithConditions(player,
                        Picked(pv, "promotion-selected", r.PromotionId) ? r.SelectedWeight : Picked(pv, "promotion", r.PromotionId) ? r.RepeatWeight : r.FirstWeight,
                        r.AddWeightConditions, r.AddWeights.Select(Signed).ToList()))));
                if (pick is null)
                    break;
                picks.Add(pick.PromotionId);
                MarkPicked(pv, "promotion", pick.PromotionId);
            }
            if (picks.Count == 0)
                return null;
            SgStreetPromotionSelectGroup group = new() { Id = ++pv.NextUid, Type = type, PromotionIds = picks };
            stage.PromotionSelectGroups.Add(group);
            return group;
        }

        private static void RollTurnPromotion(Player player, SgStreetStageData stage, SgStreetPrivateState pv)
        {
            if (stage.FirstOutsideBuildTurn <= 0)
                return;
            int start = stage.FirstOutsideBuildTurn + Cfg("PromotionStartTurnAfterOutsideBuild");
            int interval = Math.Max(1, T.Stage.Value[stage.StageId].PromotionInterval);
            if (stage.Turn < start || (stage.Turn - start) % interval != 0)
                return;
            List<SgStreetShopData> outside = stage.ShopDatas.Where(s => s.MainType == Outside && T.Shop.Value[s.ShopId].TurnPromotionGroupId > 0).ToList();
            if (outside.Count > 0)
                AddPromotionGroup(player, stage, pv, T.Shop.Value[Pick(pv, outside).ShopId].TurnPromotionGroupId, PromoTurnBase, Cfg("PromotionTurnBaseTargetCount"));
        }

        private static void RollNews(Player player, SgStreetStageData stage, SgStreetPrivateState pv, Changes changes)
        {
            int typeGroup = T.Stage.Value[stage.StageId].NewsTypeGroup;
            if (typeGroup <= 0)
                return;
            SgStreetNewsTypeGroupTable row = Range(T.NewsTypeGroup.Value.Where(r => r.TypeGroupId == typeGroup), r => r.NeedSatisfaction, Satisfaction(stage));
            // AscNet policy: NewsType 1 is the negative kind (its weight falls as satisfaction rises); attr304 subtracts from it.
            int negativeCut = AttrAdds(stage).GetValueOrDefault(304);
            int? groupId = Weighted(pv, row.GroupId.Select((id, i) => ((int?)id, row.Weigh[i] - (row.NewsType.ElementAtOrDefault(i) == 1 ? negativeCut : 0))));
            if (groupId is null)
                return;
            SgStreetNewsGroupTable? news = Weighted(pv, T.NewsGroup.Value.Where(r => r.GroupId == groupId).Select(r =>
                (r, WithConditions(player, Picked(pv, "news", r.NewsId) ? r.RepeatWeigh : r.Weigh, r.AddWeighCondition, r.AddWeigh.Select(Signed).ToList()))));
            if (news is null)
                return;
            MarkPicked(pv, "news", news.NewsId);
            stage.NewsDatas.Add(new SgStreetNewsData { Turn = stage.Turn, NewsId = news.NewsId });
            foreach (int buffId in T.News.Value[news.NewsId].Buff)
                AddBuff(player, stage, pv, buffId, changes);
        }

        private static void RollGrapevine(Player player, SgStreetStageData stage, SgStreetPrivateState pv)
        {
            int group = T.Stage.Value[stage.StageId].GrapevineGroup;
            if (group <= 0 || stage.Turn <= Cfg("GrapevineSkipTurn"))
                return;
            pv.TurnsSinceGrapevine += Cfg("GrapevineTurnInterval");
            SgStreetGrapevineGroupTable row = Range(T.GrapevineGroup.Value.Where(r => r.GroupId == group), r => r.TurnValue, pv.TurnsSinceGrapevine);
            List<SgStreetShopData> scored = stage.ShopDatas.Where(IsScored).ToList();
            if (scored.Count == 0 || !Chance(pv, row.Probability))
                return;
            SgStreetShopData shop = Pick(pv, scored);
            int randomGroup = FuncType(shop) switch { Food => row.FoodRandomGroup, Grocery => row.GroceryRandomGroup, _ => row.DessertRandomGroup };
            SgStreetGrapevineRandomGroupTable? type = Weighted(pv, T.GrapevineRandom.Value.Where(r => r.GroupId == randomGroup).Select(r =>
                (r, Picked(pv, "grapevine-type", r.GrapevineType) ? r.RepeatWeight : r.Weight)));
            if (type is null)
                return;
            SgStreetGrapevineTable? grapevine = Weighted(pv, T.Grapevine.Value.Where(r => r.GrapevineType == type.GrapevineType).Select(r => (r, r.Weight)));
            if (grapevine is null)
                return;
            MarkPicked(pv, "grapevine-type", type.GrapevineType);
            // Rumours reveal the largest preference gaps, ordered by distance (GrapevineIndexParam indexes into this list).
            int needed = T.StageDesc.Value.TryGetValue(grapevine.GrapevineType, out SgStreetStageDescTable? desc) ? Math.Max(1, desc.GrapevineIndexParam.DefaultIfEmpty(1).Max()) : 1;
            List<SgStreetGrapevineParam> list = Differences(shop).Where(d => d.Key != 0).OrderByDescending(d => Math.Abs(d.Value)).ThenBy(d => d.Key)
                .Take(needed).Select(d => new SgStreetGrapevineParam { GoodId = d.Key, Difference = d.Value }).ToList();
            stage.ShopGrapevineDatas.Add(new SgStreetGrapevineData { Turn = stage.Turn, GrapevineId = grapevine.Id, ShopId = shop.ShopId, GrapevineList = list });
            if (!stage.StatisticsData.AllGrapevineIds.Contains(grapevine.Id))
                stage.StatisticsData.AllGrapevineIds.Add(grapevine.Id);
            pv.TurnsSinceGrapevine = 0;
        }

        private static List<int> RollReviews(Player player, SgStreetStageData stage, SgStreetPrivateState pv, int satisfaction)
        {
            SgStreetStageTable row = T.Stage.Value[stage.StageId];
            SgStreetReviewDistributionTable distribution = Range(T.ReviewDistribution.Value.Where(r => r.GroupId == row.ReviewDistributionGroup), r => r.NeedSatisfaction, satisfaction);
            List<int> reviews = new();
            foreach ((int group, int count) in new[] { (row.ReviewGoodGroup, distribution.GoodReviewNum), (row.ReviewBadGroup, distribution.BadReviewNum) })
                for (int i = 0; i < count; i++)
                {
                    SgStreetReviewGroupTable? pick = Weighted(pv, T.ReviewGroup.Value
                        .Where(r => r.GroupId == group && !reviews.Contains(r.StreetReviewId) && BigWorldConditionService.Check(player, r.Condition))
                        .Select(r => (r, Picked(pv, "review", r.StreetReviewId) ? r.RepeatWeigh : r.Weigh)));
                    if (pick is null)
                        break;
                    MarkPicked(pv, "review", pick.StreetReviewId);
                    reviews.Add(pick.StreetReviewId);
                }
            return reviews;
        }

        private static void BeginTurn(Player player, SgStreetStageData stage, SgStreetPrivateState pv, Changes changes)
        {
            stage.CurrentTurnInsideBuilds.Clear();
            stage.InsideBuildTimes = 0;
            RefreshBillboard(player, stage, pv, changes);
            RollTurnPromotion(player, stage, pv);
            RollNews(player, stage, pv, changes);
            RollGrapevine(player, stage, pv);
        }
        #endregion

        #region Operating round
        private static SgStreetOperatingData BuildOperatingData(Player player, SgStreetStageData stage, SgStreetPrivateState pv)
        {
            Dictionary<int, int> scores = CurrentScores(stage);
            Dictionary<int, int> a = AttrAdds(stage);
            SgStreetStageTable row = T.Stage.Value[stage.StageId];
            SgStreetCustomerParamTable param = CustomerParam(stage);
            int customerCount = CustomerCount(stage);
            List<SgStreetShopData> shops = stage.ShopDatas.OrderBy(s => s.MainType).ThenBy(s => s.Position).ToList();
            List<int> heads = T.Customer.Value.Where(c => c.Group == param.CustomerHeadIconGroup).Select(c => c.Id).ToList();

            SgStreetOperatingData data = new();
            for (int c = 0; c < customerCount; c++)
            {
                SgStreetCustomerData customer = new() { Id = ++pv.NextUid, CustomerId = heads.Count > 0 ? Pick(pv, heads) : 0 };
                foreach (SgStreetShopData shop in shops)
                {
                    SgStreetShopLvTable lv = Lv(shop);
                    bool recommended = shop.ShopId == stage.RecommendShopId;
                    long factor = lv.CustomerFactor + (IsScored(shop) ? Signed(Range(T.ScoreFactor.Value, r => r.ShopScore, scores[shop.ShopId]).Factor) : 0) + ShopAttr(stage, shop, 1);
                    factor = factor * (Denominator + ShopAttr(stage, shop, 2)) / Denominator;
                    if (recommended)
                        factor += lv.RecommendCustomerFactor + a.GetValueOrDefault(204);
                    int? price = VisitPrice(pv, shop, lv);
                    if (price is null || !Chance(pv, (int)Math.Clamp(factor, 0, Denominator)))
                        continue;
                    long gold = price.Value + ShopAttr(stage, shop, 3) + (recommended ? a.GetValueOrDefault(203) : 0);
                    gold = gold * (Denominator + ShopAttr(stage, shop, 4)) / Denominator;
                    customer.CommandDatas.Add(new SgStreetCommandData
                    {
                        Id = ++pv.NextUid, TargetId = shop.ShopId, Type = shop.MainType == Inside ? CmdInside : CmdOutside, ShopAwardGold = (int)Math.Max(0, gold)
                    });
                }
                if (customer.CommandDatas.Count == 0 && shops.Count > 0)
                    customer.CommandDatas.Add(new SgStreetCommandData { Id = ++pv.NextUid, TargetId = Pick(pv, shops).ShopId, Type = CmdFake });
                data.CustomerDatas.Add(customer);
            }

            AssignEvents(player, stage, pv, data, scores, customerCount, a, row, param);
            data.ShopScoreSatisfaction = ShopScoreSatisfaction(stage, scores);
            data.EnvironmentSatisfaction = EnvironmentSatisfaction(stage);
            return data;
        }

        private static int? VisitPrice(SgStreetPrivateState pv, SgStreetShopData shop, SgStreetShopLvTable lv) => FuncType(shop) switch
        {
            Food => shop.FoodData?.Gold,
            Dessert => shop.DessertData?.Gold,
            Grocery => shop.GroceryData is { ShelfDatas.Count: > 0 } grocery ? Pick(pv, grocery.ShelfDatas).GoldCount : null,
            _ => lv.AwardGold
        };

        private static void AssignEvents(Player player, SgStreetStageData stage, SgStreetPrivateState pv, SgStreetOperatingData data,
            Dictionary<int, int> scores, int customerCount, Dictionary<int, int> a, SgStreetStageTable row, SgStreetCustomerParamTable param)
        {
            List<(SgStreetCustomerData Customer, SgStreetCommandData Command)> free = data.CustomerDatas
                .SelectMany(c => c.CommandDatas.Where(cmd => cmd.Type != CmdFake).Select(cmd => (c, cmd))).ToList();
            SgStreetCustomerEventNumTable num = Range(T.EventNum.Value.Where(r => r.GroupId == row.CustomerEventNumGroupId), r => r.CustomerNum, customerCount);
            int Limit(int type, int shopNum)
            {
                List<SgStreetCustomerEventNumLimitTable> rows = T.EventNumLimit.Value.Where(r => r.Type == type && row.CustomerEventNumLimitGroupId.Contains(r.GroupId)).ToList();
                return rows.Count == 0 ? int.MaxValue : Range(rows, r => r.ShopNum, shopNum).EventNumLimit;
            }

            int discontent = Math.Min(num.DiscontentNum * (Denominator + a.GetValueOrDefault(201)) / Denominator, Limit(EvDiscontent, stage.ShopDatas.Count(s => s.MainType == Inside)));
            int emergency = Math.Min(num.EmergencyNum * (Denominator + a.GetValueOrDefault(202)) / Denominator, Limit(EvEmergency, stage.ShopDatas.Count(s => s.MainType == Outside)));

            SgStreetCommandData? Take(Func<SgStreetCommandData, bool> eligible)
            {
                List<(SgStreetCustomerData Customer, SgStreetCommandData Command)> pool = free.Where(f => eligible(f.Command)).ToList();
                if (pool.Count == 0)
                    return null;
                (SgStreetCustomerData, SgStreetCommandData) pick = Pick(pv, pool);
                free.Remove(pick);
                return pick.Item2;
            }

            for (int i = 0; i < discontent && Take(_ => true) is { } cmd; i++)
                cmd.EventData = new SgStreetCustomerEventData { Id = ++pv.NextUid, Type = EvDiscontent, DiscontentAwardGold = row.DiscontentAwardGold };

            for (int i = 0; i < emergency; i++)
            {
                SgStreetCommandData? cmd = Take(c => c.Type == CmdOutside && T.Shop.Value[c.TargetId].EventEmergencyGroupId > 0);
                if (cmd is null)
                    break;
                int group = T.Shop.Value[cmd.TargetId].EventEmergencyGroupId;
                SgStreetCustomerEventEmergencyRandomTable? pick = Weighted(pv, T.EmergencyRandom.Value.Where(r => r.GroupId == group).Select(r =>
                    (r, Picked(pv, "emergency", r.EventId) ? r.SelectedWeight : r.FirstWeight)));
                if (pick is null)
                {
                    free.Add((data.CustomerDatas.First(c => c.CommandDatas.Contains(cmd)), cmd));
                    break;
                }
                MarkPicked(pv, "emergency", pick.EventId);
                cmd.EventData = new SgStreetCustomerEventData { Id = ++pv.NextUid, Type = EvEmergency, EmergencyEventId = pick.EventId };
            }

            foreach ((int func, int good, int bad, int goodGroup, int badGroup) in new[]
            {
                (Food, num.FoodFeedbackNum, num.FoodNegativeFeedbackNum, param.FoodFeedbackGroup, param.FoodFeedbackBadGroup),
                (Grocery, num.GroceryFeedbackNum, num.GroceryNegativeFeedbackNum, param.GroceryFeedbackGroup, param.GroceryFeedbackBadGroup),
                (Dessert, num.DessertFeedbackNum, num.DessertNegativeFeedbackNum, param.DessertFeedbackGroup, param.DessertFeedbackBadGroup)
            })
            {
                foreach (SgStreetShopData shop in stage.ShopDatas.Where(s => FuncType(s) == func))
                {
                    int badCount = Math.Min(bad, Range(T.FeedbackBadNum.Value, r => r.Score, scores[shop.ShopId]).FeedbackBadNum);
                    foreach ((int group, int count) in new[] { (goodGroup, good), (badGroup, badCount) })
                        for (int i = 0; i < count; i++)
                        {
                            SgStreetCommandData? cmd = Take(c => c.TargetId == shop.ShopId);
                            SgStreetFeedBackData? feedback = cmd is null ? null : RollFeedback(pv, shop, group);
                            if (cmd is null || feedback is null)
                                break;
                            cmd.EventData = new SgStreetCustomerEventData { Id = ++pv.NextUid, Type = EvFeedBack, FeedBackData = feedback };
                            feedback.Id = cmd.EventData.Id;
                        }
                }
            }
        }

        // Feedback rows describe the truth: GoodsId (0 = price) and DiffScope (distance bucket) are matched to the shop's real gap.
        private static SgStreetFeedBackData? RollFeedback(SgStreetPrivateState pv, SgStreetShopData shop, int group)
        {
            SgStreetFeedbackGroupTable? type = Weighted(pv, T.FeedbackGroup.Value.Where(r => r.GroupId == group)
                .Select(r => (r, Picked(pv, "feedback-type", r.FeedbackType) ? r.RepeatWeigh : r.Weigh)));
            if (type is null)
                return null;
            MarkPicked(pv, "feedback-type", type.FeedbackType);
            List<SgStreetFeedbackTable> rows = T.Feedback.Value.Where(r => r.FeedbackType == type.FeedbackType).ToList();
            if (rows.Count == 0)
                return null;
            Dictionary<int, int> diff = Differences(shop);
            int goodsId = Pick(pv, rows.Select(r => r.GoodsId).Distinct().ToList());
            int difference = diff.GetValueOrDefault(goodsId);
            SgStreetFeedbackTable row = Range(rows.Where(r => r.GoodsId == goodsId), r => r.DiffScope, Math.Abs(difference));
            List<int> descIndexes = row.Desc.Select((text, i) => (text, i)).Where(d => !string.IsNullOrEmpty(d.text)).Select(d => d.i).ToList();
            return new SgStreetFeedBackData
            {
                FeedbackTemplateId = row.Id,
                DescIndex = descIndexes.Count > 0 ? Pick(pv, descIndexes) : 0,
                GoodId = goodsId,
                Difference = difference
            };
        }
        #endregion

        #region Pushes
        internal static void SendWorldEnterPushes(Session session)
        {
            SgStreetData data = session.player.BigWorldState.SgStreet;
            session.SendPush(new NotifySgStreetData { Data = data });
            if (data.CurStageData is { } stage)
                PushAttrs(session, stage);
        }

        private static void PushAttrs(Session session, SgStreetStageData stage) =>
            session.SendPush(new NotifySgStreetAttrAdds { AttrAdds = AttrAdds(stage), ShopAttrAdds = ShopAttrAdds(stage) });

        private static void Flush(Session session, SgStreetStageData stage, Changes changes)
        {
            foreach (SgStreetShopData shop in changes.Shops)
                session.SendPush(new NotifySgStreetShopChange { Datas = shop });
            if (changes.Like)
                session.SendPush(new NotifySgStreetLikeChange());
            if (changes.Resource)
                session.SendPush(new NotifySgStreetResourceChange { Datas = stage.ResourceDatas });
            if (changes.Buffs)
            {
                session.SendPush(new NotifySgStreetBuffsData { BuffDatas = stage.BuffDatas });
                PushAttrs(session, stage);
            }
            if (changes.Statistics)
                session.SendPush(new NotifySgStreetStatisticsData { StatisticsData = stage.StatisticsData });
            if (changes.RemovedTasks.Count > 0)
                session.SendPush(new NotifySgStreetTaskDataRemove { TaskIds = changes.RemovedTasks });
            if (changes.Tasks.Count > 0)
                session.SendPush(new NotifySgStreetTaskData { TaskDatas = changes.Tasks });
        }
        #endregion

        #region Handlers
        private static void Respond<TResponse>(Session session, int packetId, Func<Player, TResponse> handle) where TResponse : ISgStreetResponse, new()
        {
            TResponse response;
            lock (Session.GetPlayerOperationLock(session.player.PlayerData.Id))
            {
                response = handle(session.player);
                if (response.Code == 0)
                    session.player.Save();
            }
            session.SendResponse(response, packetId);
        }

        [RequestPacketHandler("SgStreetStageEnterRequest")]
        public static void SgStreetStageEnterRequestHandler(Session session, Packet.Request packet)
        {
            // AscNet policy: the client sends Enter when the street UI opens; the server answers by re-syncing street data.
            Respond(session, packet.Id, player =>
            {
                SendWorldEnterPushes(session);
                return new SgStreetStageEnterResponse();
            });
        }

        [RequestPacketHandler("SgStreetStageStartRequest")]
        public static void SgStreetStageStartRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetStageStartRequest request = packet.Deserialize<SgStreetStageStartRequest>();
            Respond(session, packet.Id, player =>
            {
                SgStreetData street = player.BigWorldState.SgStreet;
                if (street.CurStageData is not null)
                    return new SgStreetStageStartResponse { Code = StageIsOngoing };
                if (!T.Stage.Value.TryGetValue(request.StageId, out SgStreetStageTable? row) || !T.StageShop.Value.ContainsKey(request.StageId))
                    return new SgStreetStageStartResponse { Code = StageCfgNotFound };
                if (request.StageId > TargetStageId(street) || !BigWorldConditionService.Check(player, row.Condition))
                    return new SgStreetStageStartResponse { Code = ConditionError };

                SgStreetPrivateState pv = player.BigWorldState.SgStreetPrivate;
                pv.StageStartCount++;
                pv.Rng = unchecked(player.PlayerData.Id * 1_000_003L + request.StageId * 7_919L + pv.StageStartCount);
                pv.NextUid = 0;
                pv.Picked.Clear();
                pv.TaskBases.Clear();
                pv.TurnsSinceGrapevine = 0;
                pv.UpgradeTimes = 0;

                SgStreetStageData stage = new() { StageId = row.Id, Turn = 1 };
                stage.ResourceDatas.Add(new SgStreetResourceData { ResourceId = GoldResourceId, Count = Math.Min(row.InitGold, T.GoldMax.Value) });
                // SgStreetStageShop.InitShop/InitShopPos are blank in every 4.7 row, so no shop starts pre-built.
                Changes changes = new();
                foreach (int taskId in row.TargetTaskIds.Where(id => id > 0))
                    NewTask(stage, pv, taskId, SourceStageTarget);
                stage.EnvironmentSatisfaction = EnvironmentSatisfaction(stage);
                stage.ShopScoreSatisfaction = ShopScoreSatisfaction(stage, stage.ShopDatas.ToDictionary(s => s.ShopId, s => s.Score));
                BeginTurn(player, stage, pv, changes);
                UpdateTasks(stage, pv, changes);
                street.CurStageData = stage;
                PushAttrs(session, stage);
                return new SgStreetStageStartResponse { StageData = stage };
            });
        }

        // Client GetTargetStageId: highest passed + 1 (PassedStageRecords is keyed 1..n), else DefaultStageId, capped at the max stage.
        private static int TargetStageId(SgStreetData street) =>
            Math.Min(street.PassedStageRecords.Count > 0 ? street.PassedStageRecords.Count + 1 : Cfg("DefaultStageId"), T.Stage.Value.Keys.Max());

        private static int ValidateStage(SgStreetData street, int stageId) =>
            street.CurStageData is null ? StageDataIsNull : street.CurStageData.StageId != stageId ? StageNotStart : 0;

        [RequestPacketHandler("SgStreetStageGiveUpRequest")]
        public static void SgStreetStageGiveUpRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetStageGiveUpRequest request = packet.Deserialize<SgStreetStageGiveUpRequest>();
            Respond(session, packet.Id, player =>
            {
                SgStreetData street = player.BigWorldState.SgStreet;
                int code = ValidateStage(street, request.StageId);
                if (code == 0)
                    street.CurStageData = null;
                return new SgStreetStageGiveUpResponse { Code = code };
            });
        }

        [RequestPacketHandler("SgStreetOperatingStartRequest")]
        public static void SgStreetOperatingStartRequestHandler(Session session, Packet.Request packet)
        {
            Respond(session, packet.Id, player =>
            {
                SgStreetStageData? stage = player.BigWorldState.SgStreet.CurStageData;
                if (stage is null)
                    return new SgStreetOperatingStartResponse { Code = StageDataIsNull };
                if (stage.OperatingData is not null)
                    return new SgStreetOperatingStartResponse { Code = OperatingDataIsExist };
                if (stage.Turn >= T.Stage.Value[stage.StageId].MaxTurn)
                    return new SgStreetOperatingStartResponse { Code = StageIsCompleted };
                if (stage.BillboardData is { RandomBillboards.Count: > 0, CurrentBillboardId: 0 })
                    return new SgStreetOperatingStartResponse { Code = StageNotBillboardSelect };
                stage.OperatingData = BuildOperatingData(player, stage, player.BigWorldState.SgStreetPrivate);
                return new SgStreetOperatingStartResponse { OperatingData = stage.OperatingData };
            });
        }

        [RequestPacketHandler("SgStreetOperatingSettleRequest")]
        public static void SgStreetOperatingSettleRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetOperatingSettleRequest request = packet.Deserialize<SgStreetOperatingSettleRequest>();
            Respond(session, packet.Id, player =>
            {
                SgStreetStageData? stage = player.BigWorldState.SgStreet.CurStageData;
                if (stage is null)
                    return new SgStreetOperatingSettleResponse { Code = StageDataIsNull };
                if (stage.OperatingData is not { } operating)
                    return new SgStreetOperatingSettleResponse { Code = OperatingDataNotExist };

                Dictionary<int, (SgStreetCustomerData Customer, SgStreetCommandData Command)> events = operating.CustomerDatas
                    .SelectMany(c => c.CommandDatas.Where(cmd => cmd.EventData is not null).Select(cmd => (c, cmd)))
                    .ToDictionary(e => e.cmd.EventData!.Id);
                HashSet<int> handled = new();
                foreach (SgStreetEventResult result in request.SettleParam?.EventResults ?? new())
                {
                    if (!events.TryGetValue(result.Id, out var entry) || !handled.Add(result.Id))
                        return new SgStreetOperatingSettleResponse { Code = EventDataIsNull };
                    int options = entry.Command.EventData!.Type == EvEmergency ? T.Emergency.Value[entry.Command.EventData.EmergencyEventId].OptionBuffs.Count : 0;
                    if (entry.Command.EventData.Type == EvEmergency ? result.EmergencyOptionIndex < 1 || result.EmergencyOptionIndex > options : result.EmergencyOptionIndex != 0)
                        return new SgStreetOperatingSettleResponse { Code = EmergencyOptionInvalid };
                }

                SgStreetPrivateState pv = player.BigWorldState.SgStreetPrivate;
                Changes changes = new();
                Dictionary<int, int> scores = CurrentScores(stage);
                bool spOption = AttrAdds(stage).GetValueOrDefault(305) > 0;
                SgStreetSettleData settle = new() { LastSettleStatisticData = stage.LastResultData?.CurrentSettleStatisticData };
                Dictionary<int, int> customerNums = new();
                foreach (SgStreetCommandData cmd in operating.CustomerDatas.SelectMany(c => c.CommandDatas).Where(c => c.Type != CmdFake))
                {
                    settle.CommandAwardGold += cmd.ShopAwardGold;
                    customerNums[cmd.TargetId] = customerNums.GetValueOrDefault(cmd.TargetId) + 1;
                }
                foreach (SgStreetShopData shop in stage.ShopDatas)
                {
                    shop.Score = scores[shop.ShopId];
                    shop.CustomerNum += customerNums.GetValueOrDefault(shop.ShopId);
                    shop.LastFeedBacks = events.Values.Where(e => e.Command.TargetId == shop.ShopId && e.Command.EventData!.Type == EvFeedBack)
                        .Select(e => e.Command.EventData!.FeedBackData!).ToList();
                    shop.LastCollectFeedBacks = events.Values.Where(e => e.Command.TargetId == shop.ShopId && e.Command.EventData!.Type == EvFeedBack && handled.Contains(e.Command.EventData.Id))
                        .Select(e => new SgStreetCollectFeedBack { CustomerId = e.Customer.CustomerId, FeedBackData = e.Command.EventData!.FeedBackData! }).ToList();
                }

                foreach (SgStreetEventResult result in request.SettleParam?.EventResults ?? new())
                {
                    SgStreetCustomerEventData ev = events[result.Id].Command.EventData!;
                    if (ev.Type == EvDiscontent)
                    {
                        settle.DiscontentAwardGold += ev.DiscontentAwardGold;
                        stage.StatisticsData.DiscontentEventTimes++;
                    }
                    else if (ev.Type == EvEmergency)
                    {
                        SgStreetCustomerEventEmergencyTable emergency = T.Emergency.Value[ev.EmergencyEventId];
                        List<int> buffs = spOption && emergency.SpOptionBuffs.Count >= result.EmergencyOptionIndex ? emergency.SpOptionBuffs : emergency.OptionBuffs;
                        AddBuff(player, stage, pv, buffs[result.EmergencyOptionIndex - 1], changes);
                        stage.StatisticsData.EmergencyEventTimes++;
                    }
                }
                foreach (int type in new[] { EvDiscontent, EvEmergency, EvFeedBack })
                    settle.EventSettles.Add(new SgStreetEventSettle
                    {
                        EventType = type,
                        TotalCount = events.Values.Count(e => e.Command.EventData!.Type == type),
                        HandledCount = handled.Count(id => events[id].Command.EventData!.Type == type)
                    });

                AddGold(stage, settle.CommandAwardGold + settle.DiscontentAwardGold);
                settle.AwardGold = settle.CommandAwardGold + settle.DiscontentAwardGold + changes.BuffGold;
                SgStreetStatisticsData stats = stage.StatisticsData;
                stats.AccumulativeGold += settle.AwardGold;
                stats.MaxDailyGold = Math.Max(stats.MaxDailyGold, settle.AwardGold);
                foreach ((int shopId, int count) in customerNums)
                    stats.CustomerNums[shopId] = stats.CustomerNums.GetValueOrDefault(shopId) + count;

                stage.ShopScoreSatisfaction = ShopScoreSatisfaction(stage, scores);
                stage.EnvironmentSatisfaction = EnvironmentSatisfaction(stage);
                settle.CurrentSettleStatisticData = new SgStreetSettleStatisticData
                {
                    Satisfaction = Satisfaction(stage),
                    EnvironmentSatisfaction = stage.EnvironmentSatisfaction,
                    ShopScoreSatisfaction = stage.ShopScoreSatisfaction,
                    CustomerNums = customerNums,
                    ShopScoreDatas = new Dictionary<int, int>(scores)
                };
                settle.Reviews = RollReviews(player, stage, pv, settle.CurrentSettleStatisticData.Satisfaction);
                stage.LastResultData = settle;
                stage.OperatingData = null;

                TickBuffs(player, stage, pv, changes);
                foreach (SgStreetTaskData task in stage.TaskDatas.Where(t => t.State == TaskActivated && T.Task.Value[t.ConfigId].ConditionType == 9))
                    task.Schedule = Math.Max(task.Schedule, settle.AwardGold);
                stage.Turn++;
                BeginTurn(player, stage, pv, changes);
                UpdateTasks(stage, pv, changes);

                if (changes.Like)
                    session.SendPush(new NotifySgStreetLikeChange());
                PushAttrs(session, stage);
                session.SendPush(new NotifySgStreetAfterOperatingSettleStageData { Data = stage });
                return new SgStreetOperatingSettleResponse { SettleData = settle };
            });
        }

        private static SgStreetShopData NewShop(Player player, SgStreetStageData stage, SgStreetPrivateState pv, int shopId, int position, Changes changes)
        {
            SgStreetShopTable row = T.Shop.Value[shopId];
            SgStreetShopData shop = new() { ShopId = shopId, Position = position, Level = 1, MainType = row.MainType };
            shop.UpgradeBranchIds = NextBranches(shop);
            InitSetup(shop);
            RollLike(stage, pv, shop);
            stage.ShopDatas.Add(shop);
            AddBuff(player, stage, pv, Lv(shop).BuffId, changes);
            return shop;
        }

        private static List<int> NextBranches(SgStreetShopData shop) =>
            T.ShopLv.Value.TryGetValue((shop.ShopId, shop.Level + 1), out SgStreetShopLvTable? next) ? next.UpgradeBranch.Where(id => id > 0).ToList() : new();

        private static int EditableStage(Player player, out SgStreetStageData stage)
        {
            stage = player.BigWorldState.SgStreet.CurStageData!;
            return stage is null ? StageDataIsNull : stage.OperatingData is not null ? OperatingDataIsExist : 0;
        }

        [RequestPacketHandler("SgStreetShopBuildRequest")]
        public static void SgStreetShopBuildRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetShopBuildRequest request = packet.Deserialize<SgStreetShopBuildRequest>();
            Respond(session, packet.Id, player =>
            {
                int code = EditableStage(player, out SgStreetStageData stage);
                if (code != 0)
                    return new SgStreetShopBuildResponse { Code = code };
                SgStreetStageShopTable group = T.StageShop.Value[stage.StageId];
                bool inside = group.InsideShopGroup.Contains(request.ShopId);
                if (request.ShopId <= 0 || !inside && !group.OutsideShopGroup.Contains(request.ShopId))
                    return new SgStreetShopBuildResponse { Code = BuildInvalidShopId };
                if (FindShop(stage, request.ShopId) is not null)
                    return new SgStreetShopBuildResponse { Code = ShopDataIsExist };
                SgStreetShopTable row = T.Shop.Value[request.ShopId];
                int mainType = inside ? Inside : Outside;
                bool positionOk = inside
                    ? request.Position >= 1 && request.Position <= Cfg("InsideShowMaxNum") && !group.LockInsidePos.Contains(request.Position)
                    : request.Position == group.OutsideShopGroup.IndexOf(request.ShopId) + 1 && !group.LockOutsidePos.Contains(request.Position);
                if (!positionOk)
                    return new SgStreetShopBuildResponse { Code = BuildInvalidPosition };
                if (stage.ShopDatas.Any(s => s.MainType == mainType && s.Position == request.Position))
                    return new SgStreetShopBuildResponse { Code = BuildPositionUsed };
                if (!BigWorldConditionService.Check(player, row.Condition) || !BigWorldConditionService.Check(player, T.ShopLv.Value[(row.Id, 1)].Condition))
                    return new SgStreetShopBuildResponse { Code = ConditionError };
                if (inside && stage.CurrentTurnInsideBuilds.Count >= Cfg("InsideBuildTimesInTurn"))
                    return new SgStreetShopBuildResponse { Code = ShopBuildInCd };
                if (!inside)
                {
                    int built = stage.ShopDatas.Count(s => s.MainType == Outside);
                    SgStreetOutSideShopBuildConditionTable? need = T.OutsideBuild.Value.FirstOrDefault(r => r.BuildNum == built + 1);
                    if (need is null)
                        return new SgStreetShopBuildResponse { Code = BuildOutsideTimesOver };
                    if (Satisfaction(stage) < need.NeedSatisfaction)
                        return new SgStreetShopBuildResponse { Code = BuildOutsideNotSatisfaction };
                }
                int cost = ScaledCost(row.Cost, ShopAttrAdds(stage).GetValueOrDefault(row.SubType)?.GetValueOrDefault(5) ?? 0);
                if (Gold(stage) < cost)
                    return new SgStreetShopBuildResponse { Code = ResourceNotEnough };

                SgStreetPrivateState pv = player.BigWorldState.SgStreetPrivate;
                Changes changes = new() { Resource = cost != 0 };
                AddGold(stage, -cost);
                SgStreetShopData shop = NewShop(player, stage, pv, row.Id, request.Position, changes);
                shop.TotalCost = cost;
                if (inside)
                {
                    stage.CurrentTurnInsideBuilds.Add(shop.ShopId);
                    stage.InsideBuildTimes++;
                }
                else
                {
                    if (stage.FirstOutsideBuildTurn == 0)
                        stage.FirstOutsideBuildTurn = stage.Turn;
                    if (row.BuildPromotionGroupId > 0 && AddPromotionGroup(player, stage, pv, row.BuildPromotionGroupId, PromoShopBuild, Cfg("PromotionShopBaseTargetCount")) is { } promotion)
                        session.SendPush(new NotifySgStreetPromotionSelectGroupAdd { Data = promotion });
                }
                stage.EnvironmentSatisfaction = EnvironmentSatisfaction(stage);
                stage.ShopScoreSatisfaction = ShopScoreSatisfaction(stage, stage.ShopDatas.ToDictionary(s => s.ShopId, s => s.Score));
                UpdateTasks(stage, pv, changes);
                changes.Buffs = true; // shop base attributes changed: resend attrs
                Flush(session, stage, changes);
                return new SgStreetShopBuildResponse { ShopData = shop, CurrentTurnInsideBuilds = stage.CurrentTurnInsideBuilds };
            });
        }

        [RequestPacketHandler("SgStreetShopRemoveRequest")]
        public static void SgStreetShopRemoveRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetShopRemoveRequest request = packet.Deserialize<SgStreetShopRemoveRequest>();
            Respond(session, packet.Id, player =>
            {
                int code = EditableStage(player, out SgStreetStageData stage);
                if (code != 0)
                    return new SgStreetShopRemoveResponse { Code = code };
                if (FindShop(stage, request.ShopId) is not { } shop)
                    return new SgStreetShopRemoveResponse { Code = ShopDataNotExist };
                if (shop.MainType != Inside)
                    return new SgStreetShopRemoveResponse { Code = RemoveInvalidType };
                if (stage.ShopDatas.Count(s => s.MainType == Inside) <= 1)
                    return new SgStreetShopRemoveResponse { Code = RemoveCountLimit };

                Changes changes = new() { Resource = true, Buffs = true };
                int refund = (int)((long)shop.TotalCost * (Cfg("ShopRemoveReturnRate") + AttrAdds(stage).GetValueOrDefault(303)) / Denominator);
                stage.ShopDatas.Remove(shop);
                if (stage.CurrentTurnInsideBuilds.Remove(shop.ShopId))
                    stage.InsideBuildTimes = 0;
                if (stage.RecommendShopId == shop.ShopId)
                    stage.RecommendShopId = 0;
                AddGold(stage, Math.Max(0, refund));
                stage.EnvironmentSatisfaction = EnvironmentSatisfaction(stage);
                stage.ShopScoreSatisfaction = ShopScoreSatisfaction(stage, stage.ShopDatas.ToDictionary(s => s.ShopId, s => s.Score));
                UpdateTasks(stage, player.BigWorldState.SgStreetPrivate, changes);
                Flush(session, stage, changes);
                return new SgStreetShopRemoveResponse { CurrentTurnInsideBuilds = stage.CurrentTurnInsideBuilds };
            });
        }

        [RequestPacketHandler("SgStreetShopUpgradeRequest")]
        public static void SgStreetShopUpgradeRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetShopUpgradeRequest request = packet.Deserialize<SgStreetShopUpgradeRequest>();
            Respond(session, packet.Id, player =>
            {
                int code = EditableStage(player, out SgStreetStageData stage);
                if (code != 0)
                    return new SgStreetShopUpgradeResponse { Code = code };
                if (FindShop(stage, request.ShopId) is not { } shop)
                    return new SgStreetShopUpgradeResponse { Code = ShopDataNotExist };
                if (!T.ShopLv.Value.TryGetValue((shop.ShopId, shop.Level + 1), out SgStreetShopLvTable? next))
                    return new SgStreetShopUpgradeResponse { Code = ShopLvCfgNotFound };
                if (shop.UpgradeBranchIds.Count > 0 ? !shop.UpgradeBranchIds.Contains(request.BranchId) : request.BranchId != 0)
                    return new SgStreetShopUpgradeResponse { Code = UpgradeInvalidBranchId };
                if (shop.MainType == Outside && shop.CustomerNum < next.NeedCustomerNum)
                    return new SgStreetShopUpgradeResponse { Code = UpgradeCustomerNumNotEnough };
                if (!BigWorldConditionService.Check(player, next.Condition))
                    return new SgStreetShopUpgradeResponse { Code = ConditionError };
                int cost = ScaledCost(next.Cost, ShopAttrAdds(stage).GetValueOrDefault(T.Shop.Value[shop.ShopId].SubType)?.GetValueOrDefault(6) ?? 0);
                if (Gold(stage) < cost)
                    return new SgStreetShopUpgradeResponse { Code = ResourceNotEnough };

                SgStreetPrivateState pv = player.BigWorldState.SgStreetPrivate;
                Changes changes = new() { Resource = cost != 0, Buffs = true };
                AddGold(stage, -cost);
                shop.Level++;
                shop.TotalCost += cost;
                if (request.BranchId > 0)
                    shop.BranchIds.Add(request.BranchId);
                shop.UpgradeBranchIds = NextBranches(shop);
                pv.UpgradeTimes++;
                AddBuff(player, stage, pv, next.BuffId, changes);
                stage.EnvironmentSatisfaction = EnvironmentSatisfaction(stage);
                UpdateTasks(stage, pv, changes);
                Flush(session, stage, changes);
                return new SgStreetShopUpgradeResponse { ShopData = shop };
            });
        }

        private static void Setup<TResponse>(Session session, Packet.Request packet, int shopId, int funcType, int typeError, Func<SgStreetShopData, int> apply)
            where TResponse : ISgStreetShopResponse, new()
        {
            Respond(session, packet.Id, player =>
            {
                int code = EditableStage(player, out SgStreetStageData stage);
                if (code == 0 && FindShop(stage, shopId) is not { } found)
                    code = ShopDataNotExist;
                SgStreetShopData? shop = code == 0 ? FindShop(stage, shopId) : null;
                if (code == 0 && FuncType(shop!) != funcType)
                    code = typeError;
                if (code == 0)
                    code = apply(shop!);
                if (code != 0)
                    return new TResponse { Code = code };
                Changes changes = new();
                UpdateTasks(stage, player.BigWorldState.SgStreetPrivate, changes);
                Flush(session, stage, changes);
                return new TResponse { ShopData = shop };
            });
        }

        [RequestPacketHandler("SgStreetShopSetupFoodRequest")]
        public static void SgStreetShopSetupFoodRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetShopSetupFoodRequest request = packet.Deserialize<SgStreetShopSetupFoodRequest>();
            Setup<SgStreetShopSetupFoodResponse>(session, packet, request.ShopId, Food, FoodTypeError, shop =>
            {
                SgStreetInsideShopFoodTable food = T.FoodShop.Value[shop.ShopId];
                if (!food.Chef.Contains(request.ChefId))
                    return FoodInvalidChef;
                if (request.GoodsCountList.Count != food.Goods.Count
                    || request.GoodsCountList.Where((count, i) => count < T.FoodGoods.Value[food.Goods[i]].GoodsMin || count > T.FoodGoods.Value[food.Goods[i]].GoodsMax).Any())
                    return FoodInvalidGoodsCount;
                if (request.GoldCount < food.GoldMin || request.GoldCount > food.GoldMax)
                    return FoodInvalidGold;
                shop.FoodData = new SgStreetFoodData { ChefId = request.ChefId, GoodsCountList = request.GoodsCountList.ToList(), Gold = request.GoldCount };
                return 0;
            });
        }

        [RequestPacketHandler("SgStreetShopSetupGroceryRequest")]
        public static void SgStreetShopSetupGroceryRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetShopSetupGroceryRequest request = packet.Deserialize<SgStreetShopSetupGroceryRequest>();
            Setup<SgStreetShopSetupGroceryResponse>(session, packet, request.ShopId, Grocery, GroceryTypeError, shop =>
            {
                SgStreetInsideShopGroceryTable grocery = T.GroceryShop.Value[shop.ShopId];
                List<SgStreetShelfData> shelves = request.ShelfDataList ?? new();
                if (shelves.Count > grocery.ShelfNum)
                    return GroceryInvalidGoodsCount;
                if (shelves.Any(s => !grocery.Goods.Contains(s.GoodsId)))
                    return GroceryInvalidGoods;
                if (shelves.Select(s => s.GoodsId).Distinct().Count() != shelves.Count)
                    return GroceryGoodsRepeat;
                if (shelves.Any(s => s.GoldCount < T.GroceryGoods.Value[s.GoodsId].GoldMin || s.GoldCount > T.GroceryGoods.Value[s.GoodsId].GoldMax))
                    return GroceryInvalidGold;
                shop.GroceryData = new SgStreetGroceryData { ShelfDatas = shelves.Select(s => new SgStreetShelfData { GoodsId = s.GoodsId, GoldCount = s.GoldCount }).ToList() };
                return 0;
            });
        }

        [RequestPacketHandler("SgStreetShopSetupDessertRequest")]
        public static void SgStreetShopSetupDessertRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetShopSetupDessertRequest request = packet.Deserialize<SgStreetShopSetupDessertRequest>();
            Setup<SgStreetShopSetupDessertResponse>(session, packet, request.ShopId, Dessert, DessertTypeError, shop =>
            {
                SgStreetInsideShopDessertTable dessert = T.DessertShop.Value[shop.ShopId];
                if (request.GoodsIdList.Count != dessert.Goods.Count)
                    return DessertInvalidGoodsCount;
                if (!request.GoodsIdList.Order().SequenceEqual(dessert.Goods.Order()))
                    return DessertInvalidGoods;
                if (request.GoldCount < dessert.GoldMin || request.GoldCount > dessert.GoldMax)
                    return DessertInvalidGold;
                shop.DessertData = new SgStreetDessertData { GoodsIdList = request.GoodsIdList.ToList(), Gold = request.GoldCount };
                return 0;
            });
        }

        [RequestPacketHandler("SgStreetShopSetRecommendRequest")]
        public static void SgStreetShopSetRecommendRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetShopSetRecommendRequest request = packet.Deserialize<SgStreetShopSetRecommendRequest>();
            Respond(session, packet.Id, player =>
            {
                int code = EditableStage(player, out SgStreetStageData stage);
                if (code != 0)
                    return new SgStreetShopSetRecommendResponse { Code = code };
                if (FindShop(stage, request.ShopId) is not { } shop)
                    return new SgStreetShopSetRecommendResponse { Code = ShopDataNotExist };
                if (shop.MainType != Outside)
                    return new SgStreetShopSetRecommendResponse { Code = SetRecommendTypeError };
                stage.RecommendShopId = shop.ShopId;
                return new SgStreetShopSetRecommendResponse();
            });
        }

        [RequestPacketHandler("SgStreetPromotionSelectRequest")]
        public static void SgStreetPromotionSelectRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetPromotionSelectRequest request = packet.Deserialize<SgStreetPromotionSelectRequest>();
            Respond(session, packet.Id, player =>
            {
                SgStreetStageData? stage = player.BigWorldState.SgStreet.CurStageData;
                if (stage is null)
                    return new SgStreetPromotionSelectResponse { Code = StageDataIsNull };
                if (stage.PromotionSelectGroups.FirstOrDefault(g => g.Id == request.SelectGroupId) is not { } group)
                    return new SgStreetPromotionSelectResponse { Code = PromoSelectGroupNotExist };
                if (request.Index < 0 || request.Index > group.PromotionIds.Count)
                    return new SgStreetPromotionSelectResponse { Code = PromoSelectInvalidIndex };
                stage.PromotionSelectGroups.Remove(group);
                Changes changes = new();
                if (request.Index > 0)
                {
                    SgStreetPrivateState pv = player.BigWorldState.SgStreetPrivate;
                    int promotionId = group.PromotionIds[request.Index - 1];
                    MarkPicked(pv, "promotion-selected", promotionId);
                    foreach (int buffId in T.Promotion.Value[promotionId].BuffIds)
                        AddBuff(player, stage, pv, buffId, changes);
                    stage.StatisticsData.PromotionTimes++;
                    changes.Statistics = true;
                    UpdateTasks(stage, pv, changes);
                }
                Flush(session, stage, changes);
                return new SgStreetPromotionSelectResponse();
            });
        }

        [RequestPacketHandler("SgStreetBillboardSelectRequest")]
        public static void SgStreetBillboardSelectRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetBillboardSelectRequest request = packet.Deserialize<SgStreetBillboardSelectRequest>();
            Respond(session, packet.Id, player =>
            {
                SgStreetStageData? stage = player.BigWorldState.SgStreet.CurStageData;
                if (stage is null)
                    return new SgStreetBillboardSelectResponse { Code = StageDataIsNull };
                if (stage.BillboardData is not { } data)
                    return new SgStreetBillboardSelectResponse { Code = BillboardDataNotInit };
                if (data.CurrentBillboardId != 0 || !data.RandomBillboards.Contains(request.BillboardId))
                    return new SgStreetBillboardSelectResponse { Code = BillboardIdNotFound };
                SgStreetPrivateState pv = player.BigWorldState.SgStreetPrivate;
                SgStreetBillboardTable billboard = T.Billboard.Value[request.BillboardId];
                Changes changes = new();
                SgStreetTaskData task = NewTask(stage, pv, billboard.TaskId, SourceBillboard);
                data.CurrentBillboardId = billboard.Id;
                data.CurrentTaskId = task.Id;
                foreach (int buffId in billboard.RestrictBuff)
                    AddBuff(player, stage, pv, buffId, changes);
                changes.Tasks.Add(task);
                UpdateTasks(stage, pv, changes);
                Flush(session, stage, changes);
                return new SgStreetBillboardSelectResponse { TaskId = task.Id };
            });
        }

        [RequestPacketHandler("SgStreetFinishTasksRequest")]
        public static void SgStreetFinishTasksRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetFinishTasksRequest request = packet.Deserialize<SgStreetFinishTasksRequest>();
            Respond(session, packet.Id, player =>
            {
                SgStreetStageData? stage = player.BigWorldState.SgStreet.CurStageData;
                if (stage is null)
                    return new SgStreetFinishTasksResponse { Code = StageDataIsNull };
                List<int> ids = (request.TaskIds ?? new()).Distinct().ToList();
                foreach (int id in ids)
                {
                    SgStreetTaskData? task = stage.TaskDatas.FirstOrDefault(t => t.Id == id);
                    if (task is null)
                        return new SgStreetFinishTasksResponse { Code = TaskIdNotFound };
                    if (task.Source != SourceBillboard)
                        return new SgStreetFinishTasksResponse { Code = TaskFinishInvalidSource };
                    if (task.State != TaskAchieved)
                        return new SgStreetFinishTasksResponse { Code = TaskFinishInvalidState };
                }
                SgStreetPrivateState pv = player.BigWorldState.SgStreetPrivate;
                Changes changes = new() { Statistics = ids.Count > 0 };
                foreach (int id in ids)
                {
                    stage.TaskDatas.RemoveAll(t => t.Id == id);
                    pv.TaskBases.Remove(id);
                    stage.StatisticsData.FinishTaskTimeDict[SourceBillboard] = stage.StatisticsData.FinishTaskTimeDict.GetValueOrDefault(SourceBillboard) + 1;
                    if (stage.BillboardData is { } data && data.CurrentTaskId == id)
                    {
                        SgStreetBillboardTable billboard = T.Billboard.Value[data.CurrentBillboardId];
                        if (billboard.TaskFinishNeedRemoveBuff == 1)
                            changes.Buffs |= stage.BuffDatas.RemoveAll(b => billboard.RestrictBuff.Contains(b.BuffConfigId)) > 0;
                        foreach (int buffId in billboard.RewardBuff)
                            AddBuff(player, stage, pv, buffId, changes);
                        data.CurrentTaskId = 0;
                    }
                }
                UpdateTasks(stage, pv, changes);
                Flush(session, stage, changes);
                return new SgStreetFinishTasksResponse { FinishedTaskIds = ids };
            });
        }

        [RequestPacketHandler("SgStreetStageWinSettleRequest")]
        public static void SgStreetStageWinSettleRequestHandler(Session session, Packet.Request packet)
        {
            SgStreetStageWinSettleRequest request = packet.Deserialize<SgStreetStageWinSettleRequest>();
            Respond(session, packet.Id, player =>
            {
                SgStreetData street = player.BigWorldState.SgStreet;
                int code = ValidateStage(street, request.StageId);
                if (code != 0)
                    return new SgStreetStageWinSettleResponse { Code = code };
                SgStreetStageData stage = street.CurStageData!;
                SgStreetStageTable row = T.Stage.Value[stage.StageId];
                bool targetsDone = stage.TaskDatas.Where(t => t.Source == SourceStageTarget).All(t => t.State == TaskFinished);
                if (stage.OperatingData is not null || stage.Turn < row.MaxTurn && !targetsDone)
                    return new SgStreetStageWinSettleResponse { Code = StageIsNotCompleted };

                bool isNew = !street.PassedStageRecords.TryGetValue(stage.StageId, out SgStreetPassedStageRecord? record);
                record ??= street.PassedStageRecords[stage.StageId] = new SgStreetPassedStageRecord();
                List<RewardGoods> rewards = new();
                if (isNew && row.RewardId > 0)
                    rewards.AddRange(BigWorldRewardService.Grant(session, row.RewardId));
                for (int i = 0; i < row.TargetTaskIds.Count; i++)
                {
                    int index = i + 1;
                    bool finished = stage.TaskDatas.Any(t => t.Source == SourceStageTarget && t.ConfigId == row.TargetTaskIds[i] && t.State == TaskFinished);
                    if (!finished || record.RewardIndexRecord.Contains(index))
                        continue;
                    record.RewardIndexRecord.Add(index);
                    if (i < row.TargetTaskRewards.Count && row.TargetTaskRewards[i] > 0)
                        rewards.AddRange(BigWorldRewardService.Grant(session, row.TargetTaskRewards[i]));
                }
                street.SceneData = new SgStreetSceneData { ShopDatas = stage.ShopDatas, BillboardData = stage.BillboardData };
                street.CurStageData = null;
                session.SendPush(new NotifySgStreetStageSettle { StageData = null, SceneData = street.SceneData, PassedStageRecords = street.PassedStageRecords });
                BigWorldTaskModule.OnProgressChanged(session); // BigWorld tasks 990030-990036 count street passes/stars
                BigWorldQuestRuntime.OnConditionsChanged(session); // CheckRim objectives gated on 10202028 (street stage cleared)
                return new SgStreetStageWinSettleResponse { RewardGoodsList = rewards, IsNewStagePassed = isNew };
            });
        }
        #endregion
    }
}
