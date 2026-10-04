using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.bigworld.skygarden.sgdronegame;
using MessagePack;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // SkyGarden drone game (xskygardendronegame). The client simulates the run (XSGDGInstance); the server
    // keeps the suspend save, validates settlement against SgDroneGameStarTarget and records bests/stars.
    internal static class SkyGardenDroneModule
    {
        // EN share/text/CodeText 20312xxx.
        internal const int NotOpen = 20312001;               // SgDroneGameNotOpen
        internal const int StageNotExist = 20312003;         // SgDroneGameStageNotExist
        internal const int HasSavedStageData = 20312004;     // SgDroneGameHasSavedStageData
        internal const int PreStageNotCompleted = 20312005;  // SgDroneGamePreStageNotCompleted
        internal const int PreConditionNotCompleted = 20312006; // SgDroneGamePreConditionNotCompleted
        internal const int NoCurrentStageData = 20312007;    // SgDroneGameNoCurrentStageData
        internal const int InvalidSuspendData = 20312008;    // SgDroneGameInvalidSuspendData
        internal const int InvalidSettleData = 20312009;     // SgDroneGameInvalidSettleData

        // SgDroneGameStarTarget.Type, semantics from ProgressDesc.
        private const int TargetReachObject = 7, TargetCollect = 8, TargetMaxDamaged = 9;

        private static readonly Lazy<Dictionary<int, SgDroneGameStageTable>> Stages = new(() =>
            TableReaderV2.Parse<SgDroneGameStageTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<int, SgDroneGameChapterTable>> ChapterByStage = new(() =>
            TableReaderV2.Parse<SgDroneGameChapterTable>().SelectMany(chapter => chapter.StageIds.Where(id => id > 0).Select(id => (id, chapter)))
                .ToDictionary(pair => pair.id, pair => pair.chapter));
        private static readonly Lazy<Dictionary<int, SgDroneGameStarTargetTable>> Targets = new(() =>
            TableReaderV2.Parse<SgDroneGameStarTargetTable>().ToDictionary(row => row.Id));

        internal static void RegisterConditions()
        {
            // XSkyGardenDroneGameAgency:OnCheckStageTargetAchieved(stageId, targetId).
            BigWorldConditionService.Register(10204001, (player, p) =>
                p.Count >= 2 && player.BigWorldState.SgDroneStages.TryGetValue(p[0], out SgDroneStageInfo? info) && info.FinishedStarTargets.Contains(p[1]));
        }

        private static List<int> StarTargets(SgDroneGameStageTable stage) => stage.StarTargets.Where(id => id > 0).ToList();

        // Pass = IsFinished or first StarTarget finished (XSGDroneStageEntity.lua:44-100).
        private static bool IsPassed(BigWorldPlayerState state, int stageId) =>
            state.SgDroneStages.TryGetValue(stageId, out SgDroneStageInfo? info)
            && (info.IsFinished || StarTargets(Stages.Value[stageId]).FirstOrDefault() is int first && info.FinishedStarTargets.Contains(first));

        private static SgDroneCurStageData? CurStageData(SgDroneCurrentStage? current) => current is null ? null : new SgDroneCurStageData
        {
            CurStageId = current.StageId,
            IsHardMode = current.IsHardMode,
            Seed = current.Seed,
            SaveData = current.SaveData is null ? null : MessagePackSerializer.Deserialize<object>(current.SaveData)
        };

        // Core: BigWorld enter (AscNet policy: retail push timing never captured).
        internal static void SendDroneData(Session session)
        {
            BigWorldPlayerState state = session.player.BigWorldState;
            session.SendPush(new NotifySgDroneGameData
            {
                GameData = new SgDroneGameData { StageInfo = state.SgDroneStages, CurStageData = CurStageData(state.SgDroneCurrent) }
            });
        }

        [RequestPacketHandler("SgDroneGameStageStartRequest")]
        public static void SgDroneGameStageStartRequestHandler(Session session, Packet.Request packet)
        {
            SgDroneGameStageStartRequest request = packet.Deserialize<SgDroneGameStageStartRequest>();
            BigWorldPlayerState state = session.player.BigWorldState;
            int code = !Stages.Value.TryGetValue(request.StageId, out SgDroneGameStageTable? stage) || !ChapterByStage.Value.TryGetValue(request.StageId, out SgDroneGameChapterTable? chapter) ? StageNotExist
                : state.SgDroneCurrent is { } current && current.StageId != request.StageId ? HasSavedStageData
                : !BigWorldConditionService.Check(session.player, chapter.Condition) ? NotOpen
                : stage.PreStageId > 0 && !IsPassed(state, stage.PreStageId) ? PreStageNotCompleted
                : !BigWorldConditionService.Check(session.player, stage.Condition) ? PreConditionNotCompleted
                : 0;
            if (code == 0 && state.SgDroneCurrent is null)
            {
                // AscNet policy: the level seed is server RNG.
                state.SgDroneCurrent = new SgDroneCurrentStage { StageId = request.StageId, IsHardMode = request.IsHardMode, Seed = Random.Shared.Next(1, int.MaxValue) };
                session.player.Save();
            }
            // Same stage archived: resume with the stored seed and save (Main.lua:64-75).
            session.SendResponse(new SgDroneGameStageStartResponse { Code = code, CurStageData = code == 0 ? CurStageData(state.SgDroneCurrent) : null }, packet.Id);
        }

        [RequestPacketHandler("SgDroneGameStageSuspendRequest")]
        public static void SgDroneGameStageSuspendRequestHandler(Session session, Packet.Request packet)
        {
            SgDroneGameStageSuspendRequest request = packet.Deserialize<SgDroneGameStageSuspendRequest>();
            SgDroneCurrentStage? current = session.player.BigWorldState.SgDroneCurrent;
            int code = current is null ? NoCurrentStageData
                : current.StageId != request.StageId || request.StageSuspendSaveData is null ? InvalidSuspendData
                : 0;
            if (code == 0)
            {
                current!.SaveData = MessagePackSerializer.Serialize(request.StageSuspendSaveData);
                session.player.Save();
            }
            session.SendResponse(new SgDroneGameStageSuspendResponse { Code = code }, packet.Id);
        }

        // Idempotent: also sent when closing the failure popup (PopupSettlement:54-58).
        [RequestPacketHandler("SgDroneGameStageGiveUpRequest")]
        public static void SgDroneGameStageGiveUpRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldPlayerState state = session.player.BigWorldState;
            if (state.SgDroneCurrent is not null)
            {
                state.SgDroneCurrent = null;
                session.player.Save();
            }
            session.SendResponse(new SgDroneGameStageGiveUpResponse(), packet.Id);
        }

        [RequestPacketHandler("SgDroneGameStageSettleRequest")]
        public static void SgDroneGameStageSettleRequestHandler(Session session, Packet.Request packet)
        {
            SgDroneGameStageSettleRequest request = packet.Deserialize<SgDroneGameStageSettleRequest>();
            BigWorldPlayerState state = session.player.BigWorldState;
            List<int> finished = [];
            int code = !Stages.Value.TryGetValue(request.StageId, out SgDroneGameStageTable? stage) ? StageNotExist
                : state.SgDroneCurrent?.StageId != request.StageId ? NoCurrentStageData
                : ValidateSettle(stage, request, finished);
            if (code != 0)
            {
                session.SendResponse(new SgDroneGameStageSettleResponse { Code = code }, packet.Id);
                return;
            }

            List<int> targets = StarTargets(stage!);
            if (!state.SgDroneStages.TryGetValue(stage!.Id, out SgDroneStageInfo? info))
                state.SgDroneStages[stage.Id] = info = new SgDroneStageInfo();
            // First finish of StarTargets[i] grants StarRewardIds[i].
            List<int> rewards = targets.Select((target, index) => (target, index))
                .Where(pair => finished.Contains(pair.target) && !info.FinishedStarTargets.Contains(pair.target) && pair.index < stage.StarRewardIds.Count)
                .Select(pair => stage.StarRewardIds[pair.index]).Where(id => id > 0).ToList();
            info.FinishedStarTargets = targets.Where(target => finished.Contains(target) || info.FinishedStarTargets.Contains(target)).ToList();
            info.MaxScore = Math.Max(info.MaxScore, request.Score);
            info.MinCostTime = info.MinCostTime == 0 || request.CostTime == 0 ? Math.Max(info.MinCostTime, request.CostTime) : Math.Min(info.MinCostTime, request.CostTime);
            // AscNet policy: IsFinished = every StarTarget finished.
            info.IsFinished = info.FinishedStarTargets.Count == targets.Count;
            state.SgDroneCurrent = null;
            session.player.Save();
            BigWorldTaskModule.OnProgressChanged(session);
            BigWorldQuestRuntime.OnConditionsChanged(session); // CheckRim objectives gated on 10204001
            foreach (int rewardId in rewards)
                BigWorldModule.GrantReward(session, rewardId);
            session.SendResponse(new SgDroneGameStageSettleResponse { StageInfo = state.SgDroneStages }, packet.Id);
        }

        // AscNet policy: target checks by SgDroneGameStarTarget.Type (7 reach, 8 collect >= P1, 9 damaged <= P1);
        // a win must at least finish the first (pass) target.
        private static int ValidateSettle(SgDroneGameStageTable stage, SgDroneGameStageSettleRequest request, List<int> finished)
        {
            List<int> targets = StarTargets(stage);
            if (request.CostTime < 0 || request.Score < 0 || request.TargetProgress.Keys.Any(id => !targets.Contains(id))
                || request.TargetProgress.Values.Any(value => value < 0))
                return InvalidSettleData;
            foreach (int id in targets)
            {
                if (!request.TargetProgress.TryGetValue(id, out int progress) || !Targets.Value.TryGetValue(id, out SgDroneGameStarTargetTable? target))
                    continue;
                int param = target.Params.FirstOrDefault();
                bool done = target.Type switch
                {
                    TargetReachObject => progress >= 1,
                    TargetCollect => progress >= param,
                    TargetMaxDamaged => progress <= param,
                    _ => false
                };
                if (done)
                    finished.Add(id);
            }
            return targets.Count > 0 && finished.Contains(targets[0]) ? 0 : InvalidSettleData;
        }
    }
}
