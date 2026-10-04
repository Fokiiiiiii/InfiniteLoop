using AscNet.Common.MsgPack;
using AscNet.Common.Database;
using MessagePack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.guide;
using AscNet.Table.V2.share.bigworld.common.guide;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class GuideGroupFinishRequest
    {
        public int GroupId;
    }

    [MessagePackObject(true)]
    public class GuideGroupFinishResponse
    {
        public int Code;
        public List<RewardGoods>? RewardGoodsList;
    }

    [MessagePackObject(true)]
    public class GuideCompleteRequest
    {
        public int GuideGroupId;
    }

    [MessagePackObject(true)]
    public class NotifyGuide
    {
        public int GuideGroupId;
    }

    [MessagePackObject(true)]
    public class GuideCompleteResponse
    {
        public int Code;
        public List<RewardGoods>? RewardGoodsList;
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class GuideModule
    {
        private static readonly Lazy<Dictionary<int, GuideGroupTable>> GuideGroups = new(() =>
            TableReaderV2.Parse<GuideGroupTable>().ToDictionary(guide => guide.Id));
        private static readonly Lazy<Dictionary<int, GuideCompleteTable>> GuideCompletions = new(() =>
            TableReaderV2.Parse<GuideCompleteTable>().ToDictionary(completion => completion.Id));
        // Babylonia's guide proxy (XSkyGardenGuideProxy) sends the same Guide* requests with BigWorldGuideGroup ids;
        // completion lands in the shared GuideData list that NotifyLogin feeds to XGuideManager.InitGuideData.
        private static readonly Lazy<Dictionary<int, BigWorldGuideGroupTable>> BigWorldGuides = new(() =>
        {
            HashSet<int> completions = TableReaderV2.Parse<BigWorldGuideCompleteTable>().Select(row => row.Id).ToHashSet();
            return TableReaderV2.Parse<BigWorldGuideGroupTable>()
                .Where(guide => completions.Contains(guide.CompleteId))
                .ToDictionary(guide => guide.Id);
        });
        [RequestPacketHandler("GuideOpenRequest")]
        public static void GuideOpenRequestHandler(Session session, Packet.Request packet)
        {
            GuideCompleteRequest request = packet.Deserialize<GuideCompleteRequest>();
            bool valid = IsValidGuide(request.GuideGroupId);
            session.OpenedGuideGroupId = valid ? request.GuideGroupId : null;
            session.SendResponse(new GuideOpenResponse
            {
                Code = valid ? 0 : 1
            }, packet.Id);
        }

        [RequestPacketHandler("GuideGroupFinishRequest")]
        public static void GuideGroupFinishRequestHandler(Session session, Packet.Request packet)
        {
            GuideGroupFinishRequest request = packet.Deserialize<GuideGroupFinishRequest>();
            List<GuideGroupTable> groupGuides = GuideGroups.Value.Values
                .Where(guide => guide.GroupId == request.GroupId)
                .ToList();
            if (groupGuides.Count == 0)
            {
                List<long> bigWorldGuideIds = BigWorldGuides.Value.Values
                    .Where(guide => guide.GroupId == request.GroupId)
                    .Select(guide => (long)guide.Id)
                    .ToList();
                if (bigWorldGuideIds.Count == 0)
                {
                    session.SendResponse(new GuideGroupFinishResponse { Code = 1 }, packet.Id);
                    return;
                }

                List<RewardGoods>? groupRewards = null;
                foreach (long guideId in bigWorldGuideIds)
                {
                    List<RewardGoods>? rewards = CompleteBigWorldGuide(session, (int)guideId);
                    if (rewards is not null)
                        (groupRewards ??= new()).AddRange(rewards);
                }
                BigWorld.BigWorldQuestRuntime.OnConditionsChanged(session);
                session.SendResponse(new GuideGroupFinishResponse { RewardGoodsList = groupRewards }, packet.Id);
                BigWorld.BigWorldQuestRuntime.Tick(session);
                return;
            }

            session.player.PlayerData.GuideData ??= new();
            List<GuideGroupTable> addedGuides = groupGuides
                .Where(guide => !session.player.PlayerData.GuideData.Contains(guide.Id))
                .ToList();
            if (addedGuides.Count == 0)
            {
                session.SendResponse(new GuideGroupFinishResponse(), packet.Id);
                return;
            }

            List<RewardGrant> rewardGrants = new();
            foreach (GuideGroupTable guide in addedGuides.Where(guide => guide.RewardId > 0))
            {
                var configuredRewards = RewardHandler.GetRewardGoods(guide.RewardId);
                if (configuredRewards.Count == 0)
                {
                    session.SendResponse(new GuideGroupFinishResponse { Code = 1 }, packet.Id);
                    return;
                }
                rewardGrants.Add(new RewardGrant($"guide:{guide.Id}", configuredRewards));
            }

            RewardApplicationResult? rewardApplication = null;
            try
            {
                if (rewardGrants.Count > 0)
                    rewardApplication = RewardHandler.ApplyRewardsOnceAndPersist(rewardGrants, session);
            }
            catch (Exception exception)
            {
                session.log.Error(
                    $"Failed to persist guide group rewards {request.GroupId}: {exception}");
                session.SendResponse(new GuideGroupFinishResponse { Code = 1 }, packet.Id);
                return;
            }

            List<long> addedGuideIds = addedGuides.Select(guide => (long)guide.Id).ToList();
            session.player.PlayerData.GuideData.AddRange(addedGuideIds);
            try
            {
                session.player.SaveChecked();
            }
            catch (Exception exception)
            {
                session.player.PlayerData.GuideData.RemoveAll(addedGuideIds.Contains);
                session.log.Error(
                    $"Failed to persist guide group completion {request.GroupId}: {exception}");
                session.SendResponse(new GuideGroupFinishResponse { Code = 1 }, packet.Id);
                return;
            }

            rewardApplication?.SendPushes(session);
            BigWorld.BigWorldQuestRuntime.OnConditionsChanged(session);
            session.SendResponse(new GuideGroupFinishResponse
            {
                RewardGoodsList = rewardApplication?.RewardGoods
            }, packet.Id);
            BigWorld.BigWorldQuestRuntime.Tick(session);
        }

        [RequestPacketHandler("GuideCompleteRequest")]
        public static void GuideCompleteRequestHandler(Session session, Packet.Request packet)
        {
            GuideCompleteRequest request = packet.Deserialize<GuideCompleteRequest>();
            if (!GuideGroups.Value.TryGetValue(request.GuideGroupId, out GuideGroupTable? guide)
                || !GuideCompletions.Value.ContainsKey(guide.CompleteId))
            {
                if (!BigWorldGuides.Value.ContainsKey(request.GuideGroupId))
                {
                    session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                    return;
                }

                List<RewardGoods>? rewards = CompleteBigWorldGuide(session, request.GuideGroupId);
                session.SendPush(new NotifyGuide { GuideGroupId = request.GuideGroupId });
                BigWorld.BigWorldQuestRuntime.OnConditionsChanged(session);
                session.SendResponse(new GuideCompleteResponse { RewardGoodsList = rewards }, packet.Id);
                BigWorld.BigWorldQuestRuntime.Tick(session);
                return;
            }

            session.player.PlayerData.GuideData ??= new();
            if (session.player.PlayerData.GuideData.Contains(request.GuideGroupId))
            {
                session.SendPush(new NotifyGuide { GuideGroupId = request.GuideGroupId });
                session.SendResponse(new GuideCompleteResponse(), packet.Id);
                return;
            }

            string claimKey = $"guide:{request.GuideGroupId}";

            RewardApplicationResult? rewardApplication = null;
            if (guide.RewardId is > 0)
            {
                var configuredRewards = RewardHandler.GetRewardGoods(guide.RewardId);
                if (configuredRewards.Count == 0)
                {
                    session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                    return;
                }
                try
                {
                    rewardApplication = RewardHandler.ApplyRewardsOnceAndPersist(
                        [new RewardGrant(claimKey, configuredRewards)],
                        session);
                }
                catch (Exception exception)
                {
                    session.log.Error(
                        $"Failed to persist guide reward {request.GuideGroupId}: {exception}");
                    session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                    return;
                }
            }

            session.player.PlayerData.GuideData.Add(request.GuideGroupId);
            try
            {
                session.player.SaveChecked();
            }
            catch (Exception exception)
            {
                session.player.PlayerData.GuideData.Remove(request.GuideGroupId);
                session.log.Error(
                    $"Failed to persist guide completion {request.GuideGroupId}: {exception}");
                session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                return;
            }
            rewardApplication?.SendPushes(session);
            session.SendPush(new NotifyGuide { GuideGroupId = request.GuideGroupId });
            // Retail: BigWorld quest objectives waiting on the guide advance before the response (CheckRimSystemCondition, sequence I).
            BigWorld.BigWorldQuestRuntime.OnConditionsChanged(session);
            session.SendResponse(new GuideCompleteResponse
            {
                RewardGoodsList = rewardApplication?.RewardGoods
            }, packet.Id);
            BigWorld.BigWorldQuestRuntime.Tick(session);
        }



        internal static void CompleteOpenedGuideOnStageSettle(Session session, IEnumerable<uint> settledStageIds)
            => CompleteOpenedGuide(session, 2, settledStageIds);

        internal static void CompleteOpenedGuideOnCourseClaim(Session session, uint stageId)
            => CompleteOpenedGuide(session, 12, [stageId]);

        private static void CompleteOpenedGuide(Session session, int completionMode, IEnumerable<uint> stageIds)
        {
            if (session.OpenedGuideGroupId is not int openedGuideId
                || !GuideGroups.Value.TryGetValue(openedGuideId, out GuideGroupTable? guide)
                || guide.RewardId != 0
                || !GuideCompletions.Value.TryGetValue(guide.CompleteId, out GuideCompleteTable? completion)
                || completion.Param.Count < 2
                || completion.Param[0] != completionMode
                || completion.Param[1] <= 0
                || !stageIds.Contains((uint)completion.Param[1]))
            {
                return;
            }

            session.player.PlayerData.GuideData ??= new();
            if (session.player.PlayerData.GuideData.Contains(openedGuideId))
            {
                session.OpenedGuideGroupId = null;
                return;
            }

            session.player.PlayerData.GuideData.Add(openedGuideId);
            try
            {
                session.player.SaveChecked();
            }
            catch (Exception exception)
            {
                session.player.PlayerData.GuideData.Remove(openedGuideId);
                session.log.Error($"Failed to persist completed guide {openedGuideId}: {exception}");
                return;
            }

            session.OpenedGuideGroupId = null;
            session.SendPush(new NotifyGuide { GuideGroupId = openedGuideId });
        }

        internal static void ReconcileStageCompletedGuides(Player player, Stage? stage)
        {
            if (stage?.Stages is null)
                return;

            player.PlayerData.GuideData ??= new();
            HashSet<long> completedGuideIds = player.PlayerData.GuideData.ToHashSet();
            HashSet<int> passedStageIds = stage.Stages.Values
                .Where(stageData => stageData.Passed
                    && stageData.StageId is >= int.MinValue and <= int.MaxValue)
                .Select(stageData => (int)stageData.StageId)
                .ToHashSet();
            bool changed = false;

            foreach (GuideGroupTable guide in GuideGroups.Value.Values.Where(guide => guide.RewardId == 0))
            {
                if (!GuideCompletions.Value.TryGetValue(guide.CompleteId, out GuideCompleteTable? completion)
                    || completion.Param.Count < 2
                    // CompleteCourse is a claimed legacy course reward, not a stage clear.
                    || !(completion.Param[0] == 2 && passedStageIds.Contains(completion.Param[1])
                        || completion.Param[0] == 12 && completion.Param[1] > 0
                            && stage.Course.Contains((uint)completion.Param[1]))
                    || !completedGuideIds.Add(guide.Id))
                {
                    continue;
                }

                player.PlayerData.GuideData.Add(guide.Id);
                changed = true;
            }

            if (changed)
                player.SaveChecked();
        }

        internal static void SkipCommonGuides(Player player)
        {
            player.PlayerData.GuideData ??= new();
            HashSet<long> completedGuides = new(player.PlayerData.GuideData);
            foreach (GuideGroupTable guide in GuideGroups.Value.Values.Where(guide => guide.Ignore == 0 && guide.RewardId == 0))
            {
                if (completedGuides.Add(guide.Id))
                    player.PlayerData.GuideData.Add(guide.Id);
            }
        }

        private static bool IsValidGuide(int guideGroupId)
            => GuideGroups.Value.TryGetValue(guideGroupId, out GuideGroupTable? guide)
                && GuideCompletions.Value.ContainsKey(guide.CompleteId)
                || BigWorldGuides.Value.ContainsKey(guideGroupId);

        // Records a BigWorld guide once; its RewardId is a BigWorldReward, granted only on first completion.
        private static List<RewardGoods>? CompleteBigWorldGuide(Session session, int guideId)
        {
            session.player.PlayerData.GuideData ??= new();
            if (session.player.PlayerData.GuideData.Contains(guideId))
                return null;

            session.player.PlayerData.GuideData.Add(guideId);
            session.player.SaveChecked();
            int rewardId = BigWorldGuides.Value[guideId].RewardId;
            return rewardId > 0 ? BigWorld.BigWorldModule.GrantReward(session, rewardId) : null;
        }
    }

}
