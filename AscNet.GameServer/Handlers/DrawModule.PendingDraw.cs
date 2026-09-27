using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.draw;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.GameServer.Handlers;

internal partial class DrawModule
{
    // Every paid or free draw: roll, freeze outcome + pity/history + debit plan in one durable
    // player write, then grant/debit once through per-document reward receipts.
    private static void StartDraw(Session session, int packetId, int drawId, int count, int requestTicketId,
        PlayerDrawTicket? ticket, List<(int ItemId, int Count)> costPlan)
    {
        PlayerDrawState before = BsonSerializer.Deserialize<PlayerDrawState>(session.player.DrawState.ToBson());
        PlayerPendingDraw? pending = null;
        try
        {
            List<RewardGoods> rolled = [];
            for (int index = 0; index < count; index++)
                rolled.AddRange(DrawManager.DrawDraw(session.player, drawId, index));
            if (rolled.Count < count)
            {
                session.log.Warn($"Draw rejected: reason=reward-count, drawId={drawId}, expected={count}, actual={rolled.Count}.");
                session.SendResponse(new DrawDrawCardResponse { Code = 1 }, packetId);
                return;
            }
            List<Reward> drawn = rolled.Select(goods => new Reward
            {
                Id = goods.TemplateId,
                Count = goods.Count,
                Level = Math.Max(1, goods.Level),
                Type = (RewardType)goods.RewardType,
                NotifyAsRecycle = (RewardType)goods.RewardType == RewardType.Equip,
                ConvertFrom = goods.ConvertFrom
            }).ToList();
            List<int> characterIds = drawn.Where(reward => reward.Type == RewardType.Character)
                .SelectMany(reward => Enumerable.Repeat(reward.Id, reward.Count)).ToList();
            List<RewardGoods> goods = RewardHandler.ResolveRewards(drawn, session).Select(ToDrawRewardGoods).ToList();
            DrawInfo? info = DrawManager.ApplyDrawProgress(session.player, drawId, count);
            if (info is null)
            {
                session.log.Warn($"Draw rejected: reason=inactive-progress, drawId={drawId}, effectiveCount={count}.");
                session.SendResponse(new DrawDrawCardResponse { Code = 1 }, packetId);
                return;
            }
            for (int rewardIndex = 0; rewardIndex < goods.Count; rewardIndex++)
            {
                RewardGoods reward = goods[rewardIndex];
                session.log.Info($"DrawRewardFinal uid={session.player.PlayerData.Id} drawId={drawId} " +
                    $"groupId={info.GroupId} groupSubType={info.GroupSubType} drawCount={count} index={rewardIndex} " +
                    $"rewardType={reward.RewardType} templateId={reward.TemplateId} id={reward.Id} " +
                    $"primaryDisplayId={(reward.Id > 0 ? reward.Id : reward.TemplateId)} convertFrom={reward.ConvertFrom} " +
                    $"count={reward.Count} level={reward.Level} quality={reward.Quality} showQuality={reward.ShowQuality} " +
                    $"grade={reward.Grade} breakthrough={reward.Breakthrough}");
            }
            DrawManager.RecordDrawHistory(session.player, drawId, goods);
            DrawAdjustActivityInfo? adjust = info.GroupId == 1
                ? DrawManager.GetDrawAdjustActivityInfos(session.player).FirstOrDefault() : null;
            long uid = session.player.PlayerData.Id;
            string claim = ticket is not null
                ? $"free-draw:{uid}:{ticket.Id}:{ticket.Count}"
                : $"paid-draw:{uid}:{Guid.NewGuid():N}";
            if (ticket is not null)
                ticket.Count--;
            pending = new()
            {
                TicketId = requestTicketId,
                DrawId = drawId,
                Count = count,
                ClaimKey = claim,
                ClientDrawInfo = MessagePackSerializer.Serialize(info),
                Goods = goods,
                Costs = costPlan.ToDictionary(cost => cost.ItemId, cost => cost.Count),
                CharacterIds = characterIds,
                OccurredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                AdjustActivityId = adjust?.ActivityId ?? 0,
                AdjustTargetTimes = adjust?.TargetTimes ?? 0
            };
            session.player.DrawState.PendingDraw = pending;
            // A thrown write may still have committed (ack loss): keep the frozen intent live so a
            // retry replays it instead of rerolling, and confirm it before any grant or debit.
            try { session.player.SaveChecked(); }
            catch
            {
                pending.Unconfirmed = true;
                throw;
            }
        }
        finally
        {
            if (pending is null)
                session.player.DrawState = before;
        }
        CompletePendingDraw(session, packetId, pending, true);
    }

    internal static void ResumePendingDraw(Session session)
    {
        if (session.player.DrawState?.PendingDraw is PlayerPendingDraw pending)
            CompletePendingDraw(session, null, pending, false);
    }

    private static void CompletePendingDraw(Session session, int? packetId, PlayerPendingDraw pending, bool sameRequest)
    {
        if (pending.Unconfirmed)
        {
            session.player.SaveChecked();
            pending.Unconfirmed = false;
        }
        List<RewardGoodsTable> goods = pending.Goods.Select(reward => new RewardGoodsTable
        {
            Id = reward.TemplateId,
            TemplateId = reward.TemplateId,
            Count = reward.Count,
            Params = []
        }).ToList();
        RewardApplicationResult application = RewardHandler.ApplyRewardsOnceAndPersist(
            [new RewardGrant(pending.ClaimKey, goods, pending.Costs.Count > 0 ? pending.Costs : null)], session);

        DrawInfo info = MessagePackSerializer.Deserialize<DrawInfo>(pending.ClientDrawInfo);
        bool qualified = false;
        bool progressed = false;
        // Task effects land in the same player write that clears the intent, and the claim recorded in that
        // write keeps a replayed intent (same-request retry, resume, resurrected pending) from counting twice.
        if (!string.Equals(session.player.DrawState.LastDrawProgressClaim, pending.ClaimKey, StringComparison.Ordinal))
        {
            TaskModule.EnsureMissionResets(session);
            qualified = TaskModule.RecordQualifiedDrawCharacterProgress(session, pending.DrawId,
                pending.CharacterIds, persist: false);
            progressed = TaskModule.ApplyTableDrivenProgressUnsaved(session,
                [(27000, info.GroupId, pending.Count), .. pending.Costs.Select(cost => (11202, (int?)cost.Key, cost.Value))],
                pending.OccurredAt);
            session.player.DrawState.LastDrawProgressClaim = pending.ClaimKey;
        }
        session.player.DrawState.PendingDraw = null;
        try { session.player.SaveChecked(); }
        catch
        {
            // An unacknowledged write may still have committed, and only a successful write may clear the
            // intent: keep it live so a retry replays the frozen outcome through the receipt- and claim-gated
            // path instead of charging the player for a fresh roll.
            session.player.DrawState.PendingDraw = pending;
            throw;
        }

        if (packetId is null)
            return;
        // Draws advertise a recyclable acquisition copy without changing the stored equip's IsRecycle.
        for (int index = 0; index < application.EquipData.EquipDataList.Count; index++)
        {
            EquipData equip = application.EquipData.EquipDataList[index];
            if (pending.Goods.Any(reward => reward.RewardType == (int)RewardType.Equip && reward.TemplateId == equip.TemplateId))
                application.EquipData.EquipDataList[index] = RewardHandler.CloneEquipForNotification(equip, isRecycle: true);
        }
        application.SendPushes(session);
        if (pending.Costs.Count == 0)
            session.SendPush(DrawTicketManager.BuildNotify(session.player));
        if (qualified || progressed)
            TaskModule.SendTaskSync(session);
        if (TableReaderV2.Parse<DrawCanLiverActivityTable>().Any(activity =>
            activity.DrawIds.Contains(pending.DrawId)
            && ActivityScheduleService.IsOpen(activity.TimeId, DateTimeOffset.UtcNow)))
            session.SendPush(BuildNotifyDrawCanLiverData(session.player));
        session.SendResponse(!sameRequest ? new DrawDrawCardResponse { Code = 1 } : new DrawDrawCardResponse
        {
            Code = 0,
            ClientDrawInfo = info,
            RewardGoodsList = pending.Goods.ToList(),
            DrawAdjustData = pending.AdjustActivityId > 0
                ? new() { ActivityId = pending.AdjustActivityId, TargetTimes = pending.AdjustTargetTimes } : null
        }, packetId.Value);
    }
}
