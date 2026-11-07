using System.Security.Cryptography;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben;
using MessagePack;

namespace AscNet.GameServer.Handlers;

public static partial class CerberusGameModule
{
    private const int CombatAuthorizationError = 1033;

    [MessagePackObject(true)]
    public sealed class CerberusCustomFightData
    {
        public Dictionary<int, int> Dict { get; set; } = new();
    }

    private static bool TryReadRouteChoice(Session session, FightSettleResult result, out int? choice)
    {
        choice = null;
        var allowed = Conditions.Value.Values.Where(condition => condition.Type == 10201
                && condition.Params.Count > 1 && condition.Params[0] == result.StageId)
            .SelectMany(condition => condition.Params.Skip(1)).ToHashSet();
        if (allowed.Count == 0 || result.CustomData is null) return true;
        try
        {
            var custom = MessagePackSerializer.Deserialize<Dictionary<long, CerberusCustomFightData>>(
                MessagePackSerializer.Serialize((object)result.CustomData));
            if (custom is null || custom.Keys.Any(id => id != session.player.PlayerData.Id)) return false;
            if (!custom.TryGetValue(session.player.PlayerData.Id, out var data)) return true;
            if (data?.Dict is null) return false;
            // Installed behavior DoSetCustomData writes variable 3; 1/2 are unrelated native variables.
            if (!data.Dict.TryGetValue(3, out int value)) return true;
            if (!allowed.Contains(value)) return false;
            choice = value;
            return true;
        }
        catch (MessagePackSerializationException) { return false; }
    }

    internal static void CancelFight(Session session)
    {
        if (session.player.CerberusGame.Fight is not { Result: null }) return;
        session.player.CerberusGame.Fight = null;
        session.player.SaveChecked();
    }

    internal static int RecoverPendingFight(Session session)
    {
        if (session.player.CerberusGame.Fight is not { Result: not null } pending) return 0;
        var active = session.fight;
        session.fight = null;
        try
        {
            var result = MessagePackSerializer.Deserialize<FightSettleResult>(pending.Result);
            return TrySettleFight(session, result, out var response) ? response.Code : CombatAuthorizationError;
        }
        finally
        {
            if (active?.FightId != pending.FightId) session.fight = active;
        }
    }

    internal static uint RecordFightStarted(Session session, PreFightRequest request, uint fightId)
    {
        if (!IsStage(request.PreFightData.StageId))
        {
            CancelFight(session);
            return fightId;
        }
        // A retained receipt owns its identity until the next completed fight replaces it.
        while (session.player.CerberusGame.FightReceipts.ContainsKey(fightId))
            fightId = (uint)Random.Shared.Next(1, int.MaxValue);
        var data = request.PreFightData;
        int extraRobot = StagePoint(data.StageId)?.ExtraRobotId ?? 0;
        var robotIds = data.RobotIds
            ?? throw new InvalidOperationException("Validated Cerberus deployment has no robot slots.");
        GetStageState(session, data.StageId).TeamInfo = new()
        {
            CharacterIdList = [.. data.CardIds],
            // The agency injects support only into PreFight, not into its saved editable team.
            RobotIdList = robotIds.Select(id => id == extraRobot ? 0 : id).ToList(),
            CaptainPos = data.CaptainPos, FirstFightPos = data.FirstFightPos
        };
        var previousFight = session.player.CerberusGame.Fight;
        session.player.CerberusGame.Fight = new()
        {
            FightId = fightId,
            StageId = request.PreFightData.StageId,
            PreFight = MessagePackSerializer.Serialize(request)
        };
        try { session.player.SaveChecked(); }
        catch
        {
            session.player.CerberusGame.Fight = previousFight;
            throw;
        }
        return fightId;
    }

    internal static bool TrySettleFight(Session session, FightSettleResult result, out FightSettleResponse response)
    {
        response = new();
        if (!IsStage(result.StageId)) return false;
        response.Code = CombatAuthorizationError;
        if (result.FightId is < int.MinValue or > uint.MaxValue) return true;
        uint fightId = unchecked((uint)result.FightId);
        byte[] serialized = MessagePackSerializer.Serialize(result);
        CerberusGameState state = session.player.CerberusGame;
        if (state.FightReceipts.TryGetValue(fightId, out var receipt))
        {
            if (receipt.StageId != result.StageId || !receipt.ResultHash.AsSpan().SequenceEqual(SHA256.HashData(serialized))) return true;
            response = MessagePackSerializer.Deserialize<FightSettleResponse>(receipt.Response);
            session.SendPush(BuildNotifyStageInfo(session, result.StageId));
            if (response.Settle?.IsWin == true && session.stage.Stages.TryGetValue(result.StageId, out var replayedStage))
                session.SendPush(new NotifyStageData { StageList = [replayedStage] });
            return true;
        }
        CerberusGameFightAttempt? attempt = state.Fight;
        if (attempt is null || attempt.FightId != fightId || attempt.StageId != result.StageId) return true;
        if (session.fight is { } active && (active.FightId != fightId || active.PreFight.PreFightData.StageId != result.StageId))
            return true;
        var accepted = MessagePackSerializer.Deserialize<PreFightRequest>(attempt.PreFight).PreFightData;
        if (accepted.StageId != result.StageId || accepted.SpeedrunStageId != 0 || accepted.ChallengeCount != 1
            || (result.PlayerIds is { Length: > 0 } && result.PlayerIds.Any(id => id != session.player.PlayerData.Id)))
            return true;
        if (accepted.CardIds is not { Count: 3 } acceptedCards
            || accepted.RobotIds is not { Count: 3 } acceptedRobots) return true;
        StageTable? stage = TableReaderV2.Parse<StageTable>().Find(row => row.StageId == result.StageId);
        if (stage is null || stage.StageType is 2 or 3 || result.LeftTime is < int.MinValue or > int.MaxValue
            || result.AddStars < 0 || (result.AddStars & ~((1 << stage.StarDesc.Count) - 1)) != 0)
            return true;
        if (attempt.Result is not null && !attempt.Result.AsSpan().SequenceEqual(serialized)) return true;
        bool won = result.IsWin && !result.IsForceExit;
        int? routeChoice = null;
        if (won && !TryReadRouteChoice(session, result, out routeChoice)) return true;
        if (attempt.Result is null)
        {
            attempt.RewardIds.Clear();
            bool first = session.stage.Stages.GetValueOrDefault(result.StageId)?.Passed != true;
            attempt.IsFirstClear = first;
            if (won)
            {
                if (stage.FinishDropId is > 0) attempt.RewardIds.Add(stage.FinishDropId.Value);
                if (first && stage.FirstRewardId is > 0) attempt.RewardIds.Add(stage.FirstRewardId.Value);
                var modeStage = GetStageState(session, result.StageId);
                for (int i = 0; i < stage.StarRewardId.Count; i++)
                    if ((result.AddStars & (1 << i)) != 0 && stage.StarRewardId[i] > 0
                        && !modeStage.GotStarRewardIds.Contains(stage.StarRewardId[i]))
                        attempt.RewardIds.Add(stage.StarRewardId[i]);
                var previous = session.stage.Stages.GetValueOrDefault(result.StageId);
                var datum = FightModule.BuildFightSettleStageDatum(result.StageId,
                    (previous?.StarsMark ?? 0L) | (long)(uint)result.AddStars, previous?.Achievement ?? 0,
                    acceptedCards.Select((id, index) => id > 0 ? (long)id
                        : acceptedRobots[index] > 0 ? Robots.Value[acceptedRobots[index]].CharacterId : 0)
                        .Where(id => id > 0).ToList(), false, previous);
                attempt.StageData = MessagePackSerializer.Serialize(datum);
            }
        }
        List<RewardGoods> goods = [];
        RewardApplicationResult? application = null;
        var grants = attempt.RewardIds.Distinct().Select(rewardId => new RewardGrant(
            rewardId == stage.FirstRewardId || stage.StarRewardId.Contains(rewardId)
                ? $"cerberus:stage:{session.player.PlayerData.Id}:{result.StageId}:{rewardId}"
                : $"cerberus:fight:{session.player.PlayerData.Id}:{fightId}:{rewardId}",
            RewardHandler.GetRewardGoods(rewardId))).ToList();
        if (grants.Any(grant => grant.Goods.Count == 0))
        {
            if (attempt.Result is null)
            {
                attempt.RewardIds.Clear();
                attempt.StageData = null;
                attempt.IsFirstClear = false;
            }
            return true;
        }
        if (attempt.Result is null)
        {
            attempt.Result = serialized;
            try { session.player.SaveChecked(); }
            catch
            {
                attempt.Result = null;
                attempt.StageData = null;
                attempt.RewardIds.Clear();
                throw;
            }
        }
        if (grants.Count > 0)
        {
            application = RewardHandler.ApplyRewardsOnceAndPersist(grants, session);
            goods = application.RewardGoods;
        }
        if (won)
        {
            var modeStage = GetStageState(session, result.StageId);
            // AscNet policy: retain each authored branch visited by a validated winning report.
            // Native producer is authoritative; the retail server's projection is unobserved.
            if (routeChoice is int choice && !modeStage.StageParams.Contains(choice))
                modeStage.StageParams.Add(choice);
            foreach (int rewardId in attempt.RewardIds.Where(stage.StarRewardId.Contains))
                if (!modeStage.GotStarRewardIds.Contains(rewardId)) modeStage.GotStarRewardIds.Add(rewardId);
            RecordStageClear(session, result.StageId, result.AddStars);
            session.stage.AddStage(MessagePackSerializer.Deserialize<StageDatum>(attempt.StageData!));
            session.stage.SaveChecked();
        }
        response = new()
        {
            Settle = new()
            {
                IsWin = won,
                StageId = result.StageId,
                StarsMark = won ? (int)session.stage.Stages[result.StageId].StarsMark : 0,
                LeftTime = (int)result.LeftTime,
                ChallengeCount = won ? 1 : 0,
                RewardGoodsList = goods,
                MultiRewardGoodsList = goods.Count > 0 ? [goods] : [],
                NpcHpInfo = result.NpcHpInfo
            }
        };
        var previousReceipts = state.FightReceipts;
        // Only the latest completed fight can be retried; never retain unbounded combat telemetry.
        state.FightReceipts = new()
        {
            [fightId] = new() { StageId = result.StageId, ResultHash = SHA256.HashData(serialized), Response = MessagePackSerializer.Serialize(response) }
        };
        state.Fight = null;
        try { session.player.SaveChecked(); }
        catch
        {
            state.FightReceipts = previousReceipts;
            state.Fight = attempt;
            throw;
        }
        session.fight = null;
        application?.SendPushes(session);
        if (won)
        {
            session.SendPush(BuildNotifyStageInfo(session, result.StageId));
            session.SendPush(new NotifyStageData { StageList = [session.stage.Stages[result.StageId]] });
            TaskModule.RecordStageClear(session, checked((int)result.StageId), 1, 0, attempt.IsFirstClear);
            TaskModule.SendCerberusGameTaskSync(session);
        }
        return true;
    }

    internal static bool TryEnterStory(Session session, EnterStoryRequest request, out EnterStoryResponse response)
    {
        response = new();
        if (request.StageId <= 0 || !IsStage((uint)request.StageId)) return false;
        response.Code = ValidateStoryStage(session, (uint)request.StageId);
        if (response.Code != 0) return true;
        RecordStageClear(session, (uint)request.StageId, 0);
        StageDatum datum = session.stage.Stages.GetValueOrDefault(request.StageId) ?? new()
        {
            StageId = request.StageId,
            CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BestCardIds = [],
            LastCardIds = []
        };
        bool first = !datum.Passed;
        datum.Passed = true;
        session.stage.AddStage(datum);
        session.stage.SaveChecked();
        session.SendPush(BuildNotifyStageInfo(session, (uint)request.StageId));
        session.SendPush(new NotifyStageData { StageList = [datum] });
        if (first) TaskModule.RecordStageClear(session, request.StageId, 1, 0, true);
        TaskModule.SendCerberusGameTaskSync(session);
        return true;
    }
}
