using System.Reflection;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.transfinitetower;
using MongoDB.Bson;

namespace AscNet.Test;

internal static partial class Program
{
    // Overclock Simulation (stage type 103) is owned end to end by TransfiniteTowerModule: the generic
    // PreFight/settlement path must never open, credit or clear one of its stages.
    private static void ValidateTransfiniteTowerBoundary()
    {
        PacketFactory.LoadPacketHandlers();
        const long playerId = 48_903;
        const int unauthorized = 1033;
        const int activityNotOpen = 20431001;
        const int chapterNotOpen = 20431002;
        const int teamSlotOverLimit = 20431019;
        List<TransfiniteTowerStageTable> towerStages = TableReaderV2.Parse<TransfiniteTowerStageTable>();
        // Scenario selectors: one stage of the rank chapter seeded by the teaching tower (locked for a fresh
        // player) and one stage of the teaching chapter itself (open, so tower validation is reached).
        uint lockedStageId = (uint)towerStages.Single(x => x.StageGroupId == 1001 && x.Order == 1).StageId;
        uint teachingStageId = (uint)towerStages.Single(x => x.StageGroupId == 1005 && x.Order == 1).StageId;
        List<StageTable> stages = TableReaderV2.Parse<StageTable>();
        foreach (uint id in new[] { lockedStageId, teachingStageId })
            AssertEqual(103, stages.Single(row => row.StageId == id).Type, $"tower battle stage {id} Type");
        const uint ordinaryStageId = 10420117;
        AssertEqual(true, stages.Single(row => row.StageId == ordinaryStageId).Type != 103,
            "ordinary battle stage remains outside tower boundary");

        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TransfiniteTowerModule");
        FieldInfo clockField = module.GetField("Clock", BindingFlags.NonPublic | BindingFlags.Static)!;
        object originalClock = clockField.GetValue(null)!;
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId), CreateDrawCompatibilityPlayer(playerId),
            CreateDrawCompatibilityInventory(playerId, []), "transfinite-tower-unsupported-boundary");
        int packetId = 48_903;
        try
        {
            TransfiniteTowerActivityTable activity = TableReaderV2.Parse<TransfiniteTowerActivityTable>().Single();
            AssertEqual(true, ActivityScheduleService.TryGet(activity.TimeId, out ActivityScheduleEntry play),
                "tower play calendar");
            void SetClock(long seconds) =>
                clockField.SetValue(null, (Func<DateTimeOffset>)(() => DateTimeOffset.FromUnixTimeSeconds(seconds)));

            SetClock(play.StartTime - 60);
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, ++packetId,
                new PreFightRequest { PreFightData = new() { StageId = teachingStageId, CardIds = [] } });
            AssertEqual(activityNotOpen, ReadResponsePayload<PreFightResponse>(harness, packetId,
                nameof(PreFightResponse), "closed-activity tower pre-fight").Code,
                "tower stage outside the authored activity window cannot start");
            AssertEqual(null, harness.Session.fight, "closed-activity tower pre-fight leaves no fight");

            SetClock(play.StartTime + 60);
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, ++packetId,
                new PreFightRequest { PreFightData = new() { StageId = lockedStageId, CardIds = [] } });
            AssertEqual(chapterNotOpen, ReadResponsePayload<PreFightResponse>(harness, packetId,
                nameof(PreFightResponse), "locked tower chapter pre-fight").Code,
                "a tower stage whose chapter unlock condition is unmet never starts a generic battle");
            AssertEqual(null, harness.Session.fight, "locked tower chapter pre-fight leaves no fight");

            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, ++packetId,
                new PreFightRequest { PreFightData = new() { StageId = teachingStageId, CardIds = [] } });
            AssertEqual(teamSlotOverLimit, ReadResponsePayload<PreFightResponse>(harness, packetId,
                nameof(PreFightResponse), "open tower chapter empty-team pre-fight").Code,
                "tower validation owns an open chapter stage instead of the generic deployment path");
            AssertEqual(null, harness.Session.fight, "empty tower team leaves no fight");

            // A fight opened before a tower PreFight commitment existed must not settle through generic StageDatum.
            // The harness seeds the default Stage fixture, so the boundary proof is that the document stays
            // byte-identical, not that no document exists.
            string stageBeforeRejectedSettlement = Convert.ToHexString(harness.Session.stage.ToBson());
            foreach (bool isWin in new[] { true, false })
            {
                uint id = teachingStageId;
                harness.Session.fight = new Fight(new PreFightRequest { PreFightData = new() { StageId = id } }, id);
                InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, ++packetId,
                    new FightSettleRequest { Result = new FightSettleResult { StageId = id, FightId = id, IsWin = isWin } });
                AssertEqual(unauthorized, ReadResponsePayload<FightSettleResponse>(harness, packetId,
                    nameof(FightSettleResponse), $"uncommitted tower stage {id} settlement").Code,
                    $"uncommitted tower stage {id} {(isWin ? "win" : "loss")} cannot settle generically");
                AssertEqual(null, harness.Session.fight, "rejected tower settlement consumes the fight identity");
                AssertEqual(stageBeforeRejectedSettlement, Convert.ToHexString(harness.Session.stage.ToBson()),
                    "rejected tower settlement never changes stage progress");
            }

            // Quick-clear must not bypass the same boundary by naming a tower as the effective result.
            string stageBeforeQuickClear = Convert.ToHexString(harness.Session.stage.ToBson());
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, ++packetId,
                new PreFightRequest { PreFightData = new() { StageId = ordinaryStageId, SpeedrunStageId = teachingStageId } });
            AssertEqual(unauthorized, ReadResponsePayload<PreFightResponse>(harness, packetId,
                nameof(PreFightResponse), "tower quick-clear pre-fight").Code,
                "ordinary stage cannot redirect its result to a tower stage");
            harness.Session.fight = new Fight(new PreFightRequest
            {
                PreFightData = new() { StageId = ordinaryStageId, SpeedrunStageId = teachingStageId }
            }, 103);
            InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, ++packetId,
                new FightSettleRequest { Result = new FightSettleResult { StageId = ordinaryStageId, FightId = 103, IsWin = true } });
            AssertEqual(unauthorized, ReadResponsePayload<FightSettleResponse>(harness, packetId,
                nameof(FightSettleResponse), "stale tower quick-clear settlement").Code,
                "stale ordinary fight cannot credit tower by quick-clear");
            AssertEqual(stageBeforeQuickClear, Convert.ToHexString(harness.Session.stage.ToBson()),
                "tower quick-clear leaves stage progress untouched");
            harness.Session.fight = null;

            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, ++packetId,
                new PreFightRequest { PreFightData = new() { StageId = ordinaryStageId, CardIds = [] } });
            AssertEqual(0, ReadResponsePayload<PreFightResponse>(harness, packetId,
                nameof(PreFightResponse), "ordinary stage PreFight").Code,
                "non-tower stage still enters normal fight");
        }
        finally
        {
            clockField.SetValue(null, originalClock);
        }
    }
}
