using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.miniactivity.fangkuai;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    public static void RunFangKuaiCompatibility()
    {
        foreach (string packet in new[] { "FangKuaiStageStartRequest", "FangKuaiStageSyncOperatorRequest", "FangKuaiStageSettleRequest" })
            AssertEqual("AscNet.GameServer.Handlers.FangKuaiModule", GetRegisteredRequestHandlerMethod(packet).DeclaringType?.FullName,
                $"{packet} registered handler");
        AssertMailNamedMapKeys(new FangKuaiStageStartRequest { StageId = 5011, CharacterId = 1 },
            ["CharacterId", "StageId"], "FangKuai start request");
        AssertMailNamedMapKeys(new FangKuaiStageSyncOperatorRequest(), ["OperatorData", "StageId"], "FangKuai sync request");
        AssertMailNamedMapKeys(new FangKuaiStageSettleRequest(), ["SettleType", "StageId"], "FangKuai settle request");

        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.FangKuaiModule");
        MethodInfo validate = RequiredMethod(module, "TryApplyOperator", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(FangKuaiStageData), typeof(FangKuaiOperatorData), typeof(FangKuaiStageTable), typeof(FangKuaiStageData).MakeByRefType()]);
        MethodInfo canSettle = RequiredMethod(module, "CanSettle", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(FangKuaiStageData), typeof(FangKuaiStageTable), typeof(int)]);
        static bool Apply(MethodInfo method, FangKuaiStageData old, FangKuaiOperatorData op, FangKuaiStageTable stage, out FangKuaiStageData? accepted)
        {
            object?[] values = [old, op, stage, null];
            bool result = (bool)method.Invoke(null, values)!;
            accepted = values[3] as FangKuaiStageData;
            return result;
        }
        FangKuaiActivityTable activity = TableReaderV2.Parse<FangKuaiActivityTable>().Single(a => a.ChapterIds.Contains(201));
        FangKuaiStageTable[] stages = TableReaderV2.Parse<FangKuaiStageTable>().Where(s => s.Id is 5011 or 5013).ToArray();
        FangKuaiBlockTable[] templates = TableReaderV2.Parse<FangKuaiBlockTable>()
            .Where(b => stages.Any(s => s.Id == b.StageId) && b.Type == 1 && b.UnBuild == 0).ToArray();
        FangKuaiStageBlockRuleTable[] firstRules = TableReaderV2.Parse<FangKuaiStageBlockRuleTable>()
            .Where(rule => stages.Any(s => s.Id == rule.StageId) && rule.MinLine == 1 && rule.Type == 1).ToArray();
        FangKuaiBlockPointTable[] points = TableReaderV2.Parse<FangKuaiBlockPointTable>().Where(b => b.BlockType == 1).ToArray();
        FangKuaiComboTable combo = TableReaderV2.Parse<FangKuaiComboTable>().Single(b => b.Id == 1);
        foreach (FangKuaiStageTable stage in stages)
        {
            FangKuaiStageBlockRuleTable rule = firstRules.Single(r => r.StageId == stage.Id);
            FangKuaiBlockData[] board = Enumerable.Range(0, rule.FixBlockXs.Count).Select(index =>
            {
                FangKuaiBlockTable template = templates.First(b => b.StageId == stage.Id &&
                    b.Length == rule.FixBlockLengths[index] && b.Type == rule.FixBlockType[index]);
                return new FangKuaiBlockData
                {
                    Id = index + 1, BlockId = template.Id, Type = template.Type,
                    X = rule.FixBlockXs[index] + 1, Y = 1, Length = template.Length,
                    Color = template.Colors[0], Direction = template.Direction == 0 ? 1 : template.Direction
                };
            }).ToArray();
            FangKuaiBlockPointTable blockPoint = points.Single(b => b.BlockLength == board[0].Length);
            FangKuaiStageData old = new() { StageId = stage.Id, Combo = 1 };
            FangKuaiOperatorData init = new() { Round = 0, Combo = 1, Blocks = board.ToList() };
            AssertEqual(true, Apply(validate, old, init, stage, out FangKuaiStageData? saved),
                $"FangKuai stage {stage.Id} accepts authored initial row");
            AssertEqual(board.Length, saved!.Blocks.Count, $"FangKuai stage {stage.Id} persists initial board");
            FangKuaiOperatorData first = new()
            {
                Round = 1, Combo = 1, Point = blockPoint.Point * combo.Radio / 10000,
                Blocks = board.Skip(1).ToList(),
                ComboScoreList = [new FangKuaiComboScore { BaseScore = blockPoint.Point, ComboCount = 1 }]
            };
            AssertEqual(true, Apply(validate, saved, first, stage, out FangKuaiStageData? next),
                $"FangKuai stage {stage.Id} computes first score from authored clear operand");
            AssertEqual(first.Point, next!.Point, $"FangKuai stage {stage.Id} persisted score");
            AssertEqual(false, (bool)canSettle.Invoke(null, [saved, stage, 1])!,
                $"FangKuai stage {stage.Id} cannot settle before a turn");
            AssertEqual(true, (bool)canSettle.Invoke(null, [next, stage, 1])!,
                $"FangKuai stage {stage.Id} may finish after a turn or board overflow");
            FangKuaiStageData emptied = new() { StageId = stage.Id, Round = 1, Point = next.Point };
            AssertEqual(true, (bool)canSettle.Invoke(null, [emptied, stage, 1])!,
                $"FangKuai stage {stage.Id} may settle after clearing the last block");
            FangKuaiScoreRateTable advance = TableReaderV2.Parse<FangKuaiScoreRateTable>()
                .Single(r => r.StageId == stage.Id && r.Grade == stage.SettleScoreGrade);
            AssertEqual(false, (bool)canSettle.Invoke(null, [next, stage, 3])!,
                $"FangKuai stage {stage.Id} rejects premature advance settlement");
            FangKuaiStageData ranked = new() { StageId = stage.Id, Round = 1, Point = advance.Score, Blocks = board.ToList() };
            AssertEqual(true, (bool)canSettle.Invoke(null, [ranked, stage, 3])!,
                $"FangKuai stage {stage.Id} accepts authored rating threshold");
            FangKuaiOperatorData forged = new()
            {
                Round = 1, Combo = 1, Point = first.Point + 1,
                ComboScoreList = first.ComboScoreList
            };
            AssertEqual(false, Apply(validate, saved, forged, stage, out _),
                $"FangKuai stage {stage.Id} rejects score inconsistent with the clear operand");
            AssertEqual(0L, saved.Point, $"FangKuai stage {stage.Id} failed sync leaves old score unchanged");
            FangKuaiOperatorData illegalUse = new()
            {
                Round = 0, Combo = 1, Blocks = board.ToList(),
                ItemOperatorList = [new FangKuaiItemOperator { OperatorType = 0, Index = 1, Id = 4 }]
            };
            AssertEqual(false, Apply(validate, saved, illegalUse, stage, out _),
                $"FangKuai stage {stage.Id} rejects use of an unowned item");
            AssertEqual(0, saved.ItemIds.Count(id => id > 0),
                $"FangKuai stage {stage.Id} keeps item slots on rejected use");
            FangKuaiOperatorData foreign = new()
            {
                Round = 0, Combo = 1,
                Blocks = [new FangKuaiBlockData
                {
                    Id = 1, BlockId = templates.First(b => b.StageId != stage.Id).Id, Type = board[0].Type,
                    X = 1, Y = 1, Length = 1, Color = board[0].Color, Direction = board[0].Direction
                }]
            };
            AssertEqual(false, Apply(validate, old, foreign, stage, out _),
                $"FangKuai stage {stage.Id} rejects foreign stage block");
            FangKuaiOperatorData duplicateRound = new()
            {
                Round = 0, Combo = 1, Point = next.Point,
                ComboScoreList = first.ComboScoreList
            };
            AssertEqual(false, Apply(validate, next, duplicateRound, stage, out _),
                $"FangKuai stage {stage.Id} rejects rollback");
        }

        FangKuaiBlockPointTable point = points.Single(b => b.BlockLength == 1);
        PlayerFangKuaiState persisted = new()
        {
            ActivityId = activity.Id, PlayedStageIds = [stages[0].Id],
            TotalScoresByStage = new() { [stages[0].Id] = point.Point },
            StageDataDict = new() { [201] = new FangKuaiStageData { StageId = stages[0].Id, Point = point.Point, Round = 1 } }
        };
        PlayerFangKuaiState reloaded = BsonSerializer.Deserialize<PlayerFangKuaiState>(persisted.ToBson());
        AssertEqual((long)point.Point, reloaded.TotalScoresByStage[stages[0].Id], "FangKuai cumulative task points survive reload");
        AssertEqual(1, reloaded.StageDataDict[201].Round, "FangKuai chapter board survives reload");

        // AscNet policy: chapter TimeIds without an authored window inherit the parent activity window.
        FangKuaiStageTable firstStage = stages[0];
        AssertEqual(true, ActivityScheduleService.TryGet(activity.TimeId, out ActivityScheduleEntry parent), "FangKuai parent window authored");
        foreach (int chapterTimeId in new[] { 50802, 50803, 50804 })
        {
            AssertEqual(true, ActivityScheduleService.TryGet(chapterTimeId, out ActivityScheduleEntry chapter), $"FangKuai chapter {chapterTimeId} staged");
            AssertEqual((parent.StartTime, parent.EndTime), (chapter.StartTime, chapter.EndTime), $"FangKuai chapter {chapterTimeId} inherits parent window");
            AssertEqual(true, chapter.Source.StartsWith("local-policy:FangKuai:chapter-inherits-parent-window:", StringComparison.Ordinal),
                $"FangKuai chapter {chapterTimeId} provenance is local policy");
        }
        // Expired parent (and therefore inherited chapter) rejects a real start without state change.
        using (ScheduleOverride(activity.TimeId, firstStage.TimeId, 1, 2))
        {
            long uid = 48_501;
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid),
                CreateDrawCompatibilityInventory(uid, []), "fangkuai-calendar-test");
            byte[] before = harness.Session.player.FangKuai.ToBson();
            InvokeRequestHandler(harness, "FangKuaiStageStartRequest", 4801,
                new FangKuaiStageStartRequest { StageId = firstStage.Id, CharacterId = 1 });
            FangKuaiStageStartResponse denied = ReadResponsePayload<FangKuaiStageStartResponse>(
                harness, 4801, nameof(FangKuaiStageStartResponse), "FangKuai expired parent window");
            AssertEqual(true, denied.Code != 0, "FangKuai expired parent must reject start");
            AssertEqual(true, before.SequenceEqual(harness.Session.player.FangKuai.ToBson()), "FangKuai rejected start preserves state");
        }
        RunFangKuaiRegisteredFlow();
    }

    /// <summary>TEST-ONLY in-memory shift of the parent and inherited chapter windows; restored on dispose.</summary>
    private static IDisposable ScheduleOverride(int parentTimeId, int chapterTimeId, long start, long end)
    {
        ActivityScheduleEntry[] schedules = (ActivityScheduleEntry[])ActivityScheduleService.All;
        int[] indexes = [Array.FindIndex(schedules, e => e.Id == parentTimeId), Array.FindIndex(schedules, e => e.Id == chapterTimeId)];
        if (indexes.Any(index => index < 0)) throw new InvalidDataException("FangKuai parent/chapter schedule missing.");
        ActivityScheduleEntry[] saved = indexes.Select(index => schedules[index]).ToArray();
        foreach (int index in indexes)
            schedules[index] = schedules[index] with { StartTime = start, EndTime = end };
        return new ScheduleRestore(() => { for (int i = 0; i < indexes.Length; i++) schedules[indexes[i]] = saved[i]; });
    }

    private sealed class ScheduleRestore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private static void RunFangKuaiRegisteredFlow()
    {
        using (ScheduleOverride(50801, 50802, 0, 0))
        {
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> players, out _, out _);
            const long uid = 48_502;
            Character character = CreateDrawCompatibilityCharacter(uid);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1021001, 3021001));
            using LoopbackSessionHarness harness = new(character, CreateDrawCompatibilityPlayer(uid),
                CreateDrawCompatibilityInventory(uid, []), "fangkuai-synthetic-calendar");
            int packetId = 48_502;
            T Call<T>(string requestName, object request) where T : class
            {
                int id = packetId++;
                InvokeRegisteredRequestHandler(requestName, harness.Session, id, request);
                return ReadResponsePayload<T>(harness, id, typeof(T).Name, requestName, maxPacketsToRead: 8);
            }
            List<FangKuaiStageBlockRuleTable> rules = TableReaderV2.Parse<FangKuaiStageBlockRuleTable>();
            List<FangKuaiBlockTable> templates = TableReaderV2.Parse<FangKuaiBlockTable>();
            List<FangKuaiBlockPointTable> points = TableReaderV2.Parse<FangKuaiBlockPointTable>();
            int clearPoint = points.Single(row => row.BlockType == 1 && row.BlockLength == 1).Point;
            int comboRatio = TableReaderV2.Parse<FangKuaiComboTable>().Single(row => row.Id == 1).Radio;
            AssertEqual(true, Call<FangKuaiStageStartResponse>(nameof(FangKuaiStageStartRequest),
                new FangKuaiStageStartRequest { StageId = 5012, CharacterId = 1021001 }).Code != 0,
                "FangKuai open chapter still rejects a stage whose PreStageId is uncleared");
            foreach (int stageId in new[] { 5011, 5012 })
            {
                FangKuaiStageTable stage = TableReaderV2.Parse<FangKuaiStageTable>().Single(row => row.Id == stageId);
                FangKuaiStageStartResponse started = Call<FangKuaiStageStartResponse>(
                    nameof(FangKuaiStageStartRequest), new FangKuaiStageStartRequest { StageId = stageId, CharacterId = 1021001 });
                AssertEqual(0, started.Code, $"FangKuai synthetic stage {stageId} registered start");
                AssertEqual(stageId, started.CurData.StageId, $"FangKuai synthetic stage {stageId} fresh chapter run");
                FangKuaiStageBlockRuleTable rule = rules.Single(row => row.StageId == stageId && row.Type == 1 && row.MinLine == 1);
                List<FangKuaiBlockData> board = Enumerable.Range(0, rule.FixBlockXs.Count).Select(index =>
                {
                    FangKuaiBlockTable template = templates.First(row => row.StageId == stageId &&
                        row.Type == rule.FixBlockType[index] && row.Length == rule.FixBlockLengths[index] && row.UnBuild == 0);
                    return new FangKuaiBlockData
                    {
                        Id = index + 1, BlockId = template.Id, Type = template.Type, Length = template.Length,
                        X = rule.FixBlockXs[index] + 1, Y = 1, Color = template.Colors[0],
                        Direction = template.Direction == 0 ? 1 : template.Direction, HitCount = 0
                    };
                }).ToList();
                AssertEqual(0, Call<FangKuaiStageSyncOperatorResponse>(
                    nameof(FangKuaiStageSyncOperatorRequest), new FangKuaiStageSyncOperatorRequest
                    {
                        StageId = stageId, OperatorData = new FangKuaiOperatorData { Combo = 1, Blocks = board }
                    }).Code, $"FangKuai synthetic stage {stageId} accepted authored initial board");
                AssertEqual(board.Count, harness.Session.player.FangKuai.StageDataDict[201].Blocks.Count,
                    $"FangKuai synthetic stage {stageId} persisted authored board");
                if (stageId == 5012)
                {
                    AssertEqual(0, Call<FangKuaiStageSettleResponse>(
                        nameof(FangKuaiStageSettleRequest), new FangKuaiStageSettleRequest
                        { StageId = stageId, SettleType = 4 }).Code, "FangKuai synthetic second stage give-up");
                    AssertEqual(1, harness.Session.player.FangKuai.FinishedStageIds.Count,
                        "FangKuai give-up cannot grant another clear");
                    break;
                }
                long point = clearPoint * comboRatio / 10000;
                for (int round = 1; round <= stage.MaxRound; round++)
                {
                    FangKuaiOperatorData snapshot = new()
                    {
                        Round = round, Point = point, Combo = 1, Blocks = board.Skip(1).ToList(),
                        ComboScoreList = round == 1
                            ? [new FangKuaiComboScore { BaseScore = clearPoint, ComboCount = 1 }] : []
                    };
                    AssertEqual(0, Call<FangKuaiStageSyncOperatorResponse>(
                        nameof(FangKuaiStageSyncOperatorRequest), new FangKuaiStageSyncOperatorRequest
                        { StageId = stageId, OperatorData = snapshot }).Code, $"FangKuai synthetic turn {round}");
                }
                FangKuaiStageSettleResponse settled = Call<FangKuaiStageSettleResponse>(
                    nameof(FangKuaiStageSettleRequest), new FangKuaiStageSettleRequest { StageId = stageId, SettleType = 1 });
                AssertEqual(0, settled.Code, "FangKuai synthetic normal settlement at authored turn limit");
                AssertEqual(point, settled.SettleData.Point, "FangKuai registered settlement uses accepted score");
                AssertEqual(1, harness.Session.player.FangKuai.FinishedStageIds.Count, "FangKuai first clear persisted");
                AssertEqual(point, harness.Session.player.FangKuai.TotalScoresByStage[stageId],
                    "FangKuai accepted cumulative task points persisted");
                AssertEqual(true, harness.Session.character.ScoreTitles.Any(title => title.Id == 13019740),
                    "FangKuai authored first-stage collection reward granted");
                AssertEqual(1, harness.Session.inventory.AppliedRewardClaims.Count(
                    key => key.StartsWith("fangkuai:first:", StringComparison.Ordinal)),
                    "FangKuai first collection reward has one durable receipt");
                AssertEqual(true, Call<FangKuaiStageSettleResponse>(nameof(FangKuaiStageSettleRequest),
                    new FangKuaiStageSettleRequest { StageId = stageId, SettleType = 1 }).Code != 0,
                    "FangKuai repeated settle cannot regrant collection");
                AssertEqual(1, harness.Session.inventory.AppliedRewardClaims.Count(
                    key => key.StartsWith("fangkuai:first:", StringComparison.Ordinal)),
                    "FangKuai rejected replay preserves reward receipt");
                AssertEqual(true, players.ReplaceOneCalls > 0, "FangKuai registered flow persisted player");
                Player restored = BsonSerializer.Deserialize<Player>(harness.Session.player.ToBson());
                AssertEqual(true, restored.FangKuai.FinishedStageIds.Contains(stageId), "FangKuai first clear survives relog");
            }
        }
    }
}
