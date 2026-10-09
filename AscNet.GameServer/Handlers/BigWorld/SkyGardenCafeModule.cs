using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.bigworld.skygarden.cafe;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Kuroro Cafe (xskygardencafe). The client simulates the card game and sends full CafeGambling snapshots;
    // AscNet policy: trust SumSales/ReviewNum within structural checks (SGCafeBuffList/Effect/Condition are Lua-only).
    internal static class SkyGardenCafeModule
    {
        // EN share/text/CodeText 252000xx.
        internal const int GroupIdError = 25200038;          // BigWorldCafeGroupIdError
        internal const int GroupListError = 25200039;        // BigWorldCafeGroupListError (card count over limit)
        internal const int StageConfigError = 25200040;      // BigWorldCafeStageConfigError
        internal const int StageConditionError = 25200043;   // BigWorldCafeStageConditionError
        internal const int CardNumOverOwn = 25200045;        // BigWorldCafeCardNumOverown
        internal const int NewRoundError = 25200046;         // BigWorldCafeNewRoundError (must start at round 1)
        internal const int NextRoundError = 25200047;        // BigWorldCafeNextRoundError (round number)
        internal const int GamblingIsError = 25200048;       // BigWorldCafeCafeGamblingIsError (no run data)
        internal const int CardIdNotInConfig = 25200050;     // BigWorldCafeCafeCardIdNotInConfigError
        internal const int StoryListNotNull = 25200052;      // BigWorldCafeListNotNull
        internal const int ChallengeListIsNull = 25200053;   // BigWorldCafeCardListIsNull
        internal const int StageIdError = 25200054;          // BigWorldCafeStageIdError
        internal const int UseNotActivityCard = 25200057;    // BigWorldCafeUseNotActivityCard
        internal const int NotPass = 25200058;               // BigWorldCafeNotPass
        internal const int AlreadyKickOut = 25200059;        // BigWorldCafeAlreadyKickOut
        internal const int NotAllowKickOut = 25200060;       // BigWorldCafeNotAllowKickOut
        internal const int RoundCardError = 25200061;        // BigWorldCafeRoundCardError
        internal const int RoundReviewError = 25200063;      // BigWorldCafeRoundReviewError

        private const int StoryType = 1, ChallengeType = 2; // CafeType

        private static readonly Lazy<Dictionary<int, SGCafeStageTable>> Stages = new(() =>
            TableReaderV2.Parse<SGCafeStageTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<int, SGCafeCustomerTable>> Customers = new(() =>
            TableReaderV2.Parse<SGCafeCustomerTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<string, string>> Config = new(() =>
            TableReaderV2.Parse<SGCafeConfigTable>().ToDictionary(row => row.Id, row => row.Value ?? string.Empty));

        private static int ConfigInt(string key) => int.Parse(Config.Value[key]);

        internal static void RegisterConditions()
        {
            // XSkyGardenCafeAgency:CheckStageStar / CheckStageRound.
            BigWorldConditionService.Register(10203001, (player, p) =>
                p.Count >= 2 && p[0] > 0 && State(player).CafeStageList.TryGetValue(p[0], out CafeStageRecord? record) && record.GetMaxStarReward >= p[1]);
            BigWorldConditionService.Register(10203002, (player, p) =>
                p.Count >= 2 && p[0] > 0 && State(player).CafeGambling is { } run && run.StageId == p[0] && run.Round == p[1]);
        }

        internal static XBigWorldCafeDb State(Player player) => player.BigWorldState.SgCafe ??= CreateInitial();

        private static XBigWorldCafeDb CreateInitial()
        {
            XBigWorldCafeDb db = new()
            {
                // AscNet policy: BeginTime = creation time of the cafe record (no retail source).
                BeginTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            // AscNet policy: SGCafeCustomer has no unlock-source column; every customer referenced by
            // SGCafeCustomerPreset is owned with SGCafeCustomer.DefaultNum copies.
            foreach (int id in TableReaderV2.Parse<SGCafeCustomerPresetTable>().SelectMany(row => row.CustomerIds).Where(id => id > 0).Distinct())
                if (Customers.Value.TryGetValue(id, out SGCafeCustomerTable? customer))
                    db.CardDict[id] = customer.DefaultNum;
            return db;
        }

        // Core: BigWorld enter. Without this push the client's cafe IsOpen() is false (Model:60-73).
        internal static void SendCafeData(Session session) =>
            session.SendPush(new NotifyBigWorldCafeData { Data = State(session.player) });

        [RequestPacketHandler("BigWorldCafeCardGroupListSaveRequest")]
        public static void BigWorldCafeCardGroupListSaveRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldCafeCardGroupListSaveRequest request = packet.Deserialize<BigWorldCafeCardGroupListSaveRequest>();
            XBigWorldCafeDb db = State(session.player);
            int code = ValidateDeck(db, request.GroupId, request.CardList);
            if (code == 0)
            {
                db.CardGroupList[request.GroupId] = request.CardList;
                session.player.Save();
            }
            session.SendResponse(new BigWorldCafeCardGroupListSaveResponse { Code = code }, packet.Id);
        }

        private static int ValidateDeck(XBigWorldCafeDb db, int groupId, List<int> cards)
        {
            if (groupId < ConfigInt("MinGroupId") || groupId > ConfigInt("MaxGroupId"))
                return GroupIdError;
            if (cards.Count > ConfigInt("GroupMaxCardNum"))
                return GroupListError;
            foreach (IGrouping<int, int> group in cards.GroupBy(id => id))
            {
                if (!Customers.Value.ContainsKey(group.Key))
                    return CardIdNotInConfig;
                if (!db.CardDict.TryGetValue(group.Key, out int owned) || owned <= 0)
                    return UseNotActivityCard;
                if (group.Count() > owned)
                    return CardNumOverOwn;
            }
            // AscNet policy: server-side mirror of the client's MaxQuality{n}Limit caps (0 = unlimited, Control.lua:549-563).
            foreach (IGrouping<int, int> quality in cards.GroupBy(id => Customers.Value[id].Quality))
                if (Config.Value.TryGetValue($"MaxQuality{quality.Key}Limit", out string? raw)
                    && int.TryParse(raw, out int limit) && limit > 0 && quality.Count() > limit)
                    return GroupListError;
            return 0;
        }

        private static int CheckStageAccess(Player player, XBigWorldCafeDb db, int stageId, out SGCafeStageTable? stage)
        {
            if (!Stages.Value.TryGetValue(stageId, out stage))
                return StageConfigError;
            if (stage.PreStage > 0 && (!db.CafeStageList.TryGetValue(stage.PreStage, out CafeStageRecord? pre) || pre.GetMaxStarReward <= 0))
                return StageConditionError;
            return BigWorldConditionService.Check(player, stage.Condition) ? 0 : StageConditionError;
        }

        // AscNet policy: minimum structural checks on a client snapshot (ids known, hand/seat caps, non-negative totals).
        private static int ValidateSnapshot(CafeGambling run)
        {
            IEnumerable<int> ids = run.HandCards.Concat(run.AbandonCards).Concat(run.BanCards).Concat(run.CardsWarehouse)
                .Concat(run.PriorityCard).Concat(run.RetainHandCard).Concat(run.UseCardTimes.Keys).Concat(run.BuffAdditionDict.Keys);
            if (ids.Any(id => !Customers.Value.ContainsKey(id)))
                return CardIdNotInConfig;
            if (run.HandCards.Count > ConfigInt("MaxHandCardNum") || run.ActPoint > ConfigInt("MaxSeat") || run.ActPoint < 0)
                return RoundCardError;
            if (run.SumSales < 0 || run.ReviewNum < 0)
                return RoundReviewError;
            return 0;
        }

        [RequestPacketHandler("BigWorldCafeNewRoundRequest")]
        public static void BigWorldCafeNewRoundRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldCafeNewRoundRequest request = packet.Deserialize<BigWorldCafeNewRoundRequest>();
            XBigWorldCafeDb db = State(session.player);
            int code = ValidateNewRound(session.player, db, request);
            if (code == 0)
            {
                db.CafeGambling = request.CafeGambling;
                // AscNet policy mirroring the client, which Syncs the deck on success.
                if (Stages.Value[request.CafeGambling!.StageId].Type == ChallengeType)
                    db.CardGroupList[request.CardGroupId] = request.CardList;
                session.player.Save();
            }
            session.SendResponse(new BigWorldCafeNewRoundResponse { Code = code, CafeGambling = code == 0 ? db.CafeGambling : null }, packet.Id);
        }

        private static int ValidateNewRound(Player player, XBigWorldCafeDb db, BigWorldCafeNewRoundRequest request)
        {
            if (request.CafeGambling is not { } run)
                return GamblingIsError;
            int code = CheckStageAccess(player, db, run.StageId, out SGCafeStageTable? stage);
            if (code != 0)
                return code;
            if (run.Round != 1)
                return NewRoundError;
            if (stage!.Type == StoryType && request.CardList.Count > 0)
                return StoryListNotNull;
            if (stage.Type == ChallengeType)
            {
                if (request.CardList.Count == 0)
                    return ChallengeListIsNull;
                code = ValidateDeck(db, request.CardGroupId, request.CardList);
                if (code != 0)
                    return code;
            }
            return ValidateSnapshot(run);
        }

        [RequestPacketHandler("BigWorldCafeNextRoundRequest")]
        public static void BigWorldCafeNextRoundRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldCafeNextRoundRequest request = packet.Deserialize<BigWorldCafeNextRoundRequest>();
            XBigWorldCafeDb db = State(session.player);
            CafeGambling? stored = db.CafeGambling;
            CafeGambling? run = request.CafeGambling;
            int code = stored is null || run is null ? GamblingIsError
                : run.StageId != stored.StageId ? StageIdError
                : run.Round != stored.Round + 1 ? NextRoundError
                : ValidateSnapshot(run);
            if (code != 0)
            {
                session.SendResponse(new BigWorldCafeNextRoundResponse { Code = code }, packet.Id);
                return;
            }

            SGCafeStageTable stage = Stages.Value[run!.StageId];
            if (run.Round <= stage.Rounds)
            {
                db.CafeGambling = run;
                session.player.Save();
                session.SendResponse(new BigWorldCafeNextRoundResponse(), packet.Id);
                return;
            }

            // Last period ended: settle (the settle popup opens only on NotifyBigWorldCafeSettle, Control.lua:275).
            List<int> targets = stage.Target.Where(target => target > 0).ToList();
            int star = targets.Count(target => target <= run.SumSales);
            if (!db.CafeStageList.TryGetValue(stage.Id, out CafeStageRecord? record))
                db.CafeStageList[stage.Id] = record = new CafeStageRecord { StageId = stage.Id };
            // AscNet policy: Reward[i] is granted once, the first time star i+1 is reached.
            List<int> awards = stage.Reward.Take(star).Skip(record.GetMaxStarReward).Where(id => id > 0).ToList();
            record.MaxSales = Math.Max(record.MaxSales, run.SumSales);
            record.GetMaxStarReward = Math.Max(record.GetMaxStarReward, star);
            db.CafeGambling = null;
            session.player.Save();
            BigWorldTaskModule.OnProgressChanged(session);
            BigWorldQuestRuntime.OnConditionsChanged(session); // CheckRim objectives gated on 10203001 (cafe stage star)
            foreach (int rewardId in awards)
                BigWorldModule.GrantReward(session, rewardId);
            // Order vs the response is irrelevant to the client (Control.lua UpdateData rewrites the same score).
            session.SendPush(new NotifyBigWorldCafeSettle { StageId = stage.Id, Star = star, SumSales = run.SumSales, AwardList = awards });
            session.SendResponse(new BigWorldCafeNextRoundResponse(), packet.Id);
        }

        // Idempotent: the client also sends it as error recovery (Battle.lua:224-236).
        [RequestPacketHandler("BigWorldCafeGiveUpRequest")]
        public static void BigWorldCafeGiveUpRequestHandler(Session session, Packet.Request packet)
        {
            XBigWorldCafeDb db = State(session.player);
            if (db.CafeGambling is not null)
            {
                db.CafeGambling = null;
                session.player.Save();
            }
            session.SendResponse(new BigWorldCafeGiveUpResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldCafeGuideKickOutSceneRequest")]
        public static void BigWorldCafeGuideKickOutSceneRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldCafeGuideKickOutSceneRequest request = packet.Deserialize<BigWorldCafeGuideKickOutSceneRequest>();
            XBigWorldCafeDb db = State(session.player);
            db.CafeStageList.TryGetValue(request.StageId, out CafeStageRecord? record);
            int code = !Stages.Value.TryGetValue(request.StageId, out SGCafeStageTable? stage) ? StageConfigError
                : stage.IsKickOutCafe != 1 ? NotAllowKickOut
                : record is null || record.GetMaxStarReward <= 0 ? NotPass
                : record.IsKickOutCafe ? AlreadyKickOut
                : 0;
            if (code == 0)
            {
                record!.IsKickOutCafe = true;
                session.player.Save();
            }
            session.SendResponse(new BigWorldCafeGuideKickOutSceneResponse { Code = code }, packet.Id);
        }
    }
}
