using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben.cerberusgame;
using AscNet.Table.V2.client.fuben.cerberusgame;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.robot;

namespace AscNet.GameServer.Handlers;

public static partial class CerberusGameModule
{
    private const int Locked = 20003024;
    private const int InvalidTeam = 20009021;
    private static readonly Lazy<Dictionary<int, CerberusGameActivityTable>> Activities = new(() => TableReaderV2.Parse<CerberusGameActivityTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, CerberusGameChapterTable>> Chapters = new(() => TableReaderV2.Parse<CerberusGameChapterTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, CerberusGameStoryLineTable>> Lines = new(() => TableReaderV2.Parse<CerberusGameStoryLineTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, CerberusGameStoryPointTable>> Points = new(() => TableReaderV2.Parse<CerberusGameStoryPointTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, CerberusGameChallengeTable>> Challenges = new(() => TableReaderV2.Parse<CerberusGameChallengeTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<HashSet<int>> Communications = new(() => TableReaderV2.Parse<CerberusGameCommunicationTable>().Select(x => x.Id).ToHashSet());
    private static readonly Lazy<List<CerberusGameRoleTable>> Roles = new(() => TableReaderV2.Parse<CerberusGameRoleTable>().ToList());
    private static readonly Lazy<Dictionary<int, RobotTable>> Robots = new(() => TableReaderV2.Parse<RobotTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, ConditionTable>> Conditions = new(() => TableReaderV2.Parse<ConditionTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<int[]> EntryConditions = new(() =>
    {
        var functions = TableReaderV2.Parse<AscNet.Table.V2.client.functional.SkipFunctionalTable>()
            .Where(skip => skip.UiName == "OnOpenCerberusGame" && skip.FunctionalId is > 0)
            .Select(skip => skip.FunctionalId!.Value).ToHashSet();
        return TableReaderV2.Parse<AscNet.Table.V2.share.functional.FunctionalOpenTable>()
            .Where(function => functions.Contains(function.Id)).SelectMany(function => function.Condition).Distinct().ToArray();
    });

    private static CerberusGameActivityTable? ActiveActivity() => Activities.Value.Values
        .Where(x => x.TimeId is > 0 && ActivityScheduleService.IsOpen(x.TimeId.Value, DateTimeOffset.UtcNow))
        .OrderByDescending(x => x.Id).FirstOrDefault();

    private static CerberusGameStoryPointTable? StagePoint(uint stageId) => Points.Value.Values.FirstOrDefault(x => x.StoryPointType is 1 or 3 && x.StoryPointTypeParams.Count > 0 && x.StoryPointTypeParams[0] == stageId);
    private static CerberusGameStoryLineTable? PointLine(int pointId) => Lines.Value.Values.FirstOrDefault(x => x.StoryPointIds.Contains(pointId));
    private static int StageChapter(uint stageId) => Challenges.Value.TryGetValue((int)stageId, out var challenge) ? challenge.ChapterId : StagePoint(stageId) is { } point ? PointLine(point.Id)?.ChapterId ?? 0 : 0;
    public static bool IsStage(uint stageId) => stageId <= int.MaxValue && StageChapter(stageId) != 0;

    public static NotifyCerberusGameData BuildNotify(Session session)
    {
        var activity = ActiveActivity();
        var state = session.player.CerberusGame;
        int activityId = activity?.Id ?? 0;
        if (state.ActivityId != activityId)
        {
            state.ActivityId = activityId;
            session.player.SaveChecked();
        }
        if (activity is null) return new();
        return new NotifyCerberusGameData
        {
            CerberusGameData = new CerberusGameData
            {
                ActivityId = activity.Id,
                ChapterInfos = activity.ChapterIds.Where(Chapters.Value.ContainsKey).Select(chapterId => new CerberusGameChapterInfo
                {
                    ChapterId = chapterId,
                    TeamInfo = ChapterTeam(session, chapterId),
                    StoryLineInfos = Lines.Value.Values.Where(line => line.ChapterId == chapterId).OrderBy(line => line.Id).Select(line => new CerberusGameStoryLineInfo
                    {
                        PassStoryPoints = line.StoryPointIds.Where(state.PassStoryPoints.Contains).ToList()
                    }).ToList()
                }).ToList(),
                StageInfos = state.Stages.Where(stage => activity.ChapterIds.Contains(StageChapter(stage.StageId))).OrderBy(stage => stage.StageId)
                    .Select(stage => BuildNotifyStageInfo(session, stage.StageId).CerberusGameStageInfo).ToList()
            }
        };
    }

    private static bool ChapterOpen(Session session, int chapterId) => ActiveActivity() is { } activity
        && activity.ChapterIds.Contains(chapterId) && Chapters.Value.TryGetValue(chapterId, out var chapter)
        && EntryConditions.Value.Length > 0 && EntryConditions.Value.All(condition => ConditionSatisfied(session, condition))
        && (chapter.TimeId <= 0 || ActivityScheduleService.IsOpen(chapter.TimeId, DateTimeOffset.UtcNow))
        && ConditionSatisfied(session, chapter.OpenCondition.GetValueOrDefault());

    private static bool PointOpen(Session session, CerberusGameStoryPointTable point, CerberusGameStoryLineTable line) => ChapterOpen(session, line.ChapterId)
        && ConditionSatisfied(session, line.OpenCondition.GetValueOrDefault())
        && ConditionSatisfied(session, point.OpenCondition.GetValueOrDefault())
        // EN XCerberusGameStoryPoint.GetIsShow accepts ANY predecessor, not all branches.
        && (point.PrePointIds.Count == 0 || point.PrePointIds.Any(session.player.CerberusGame.PassStoryPoints.Contains));

    public static int ValidateStage(Session session, uint stageId)
    {
        if (!IsStage(stageId) || !ChapterOpen(session, StageChapter(stageId))) return Locked;
        if (Challenges.Value.TryGetValue((int)stageId, out var challenge))
            return challenge.PreStageId is > 0 && !StagePassed(session, (uint)challenge.PreStageId.Value) ? Locked : 0;
        var point = StagePoint(stageId)!;
        var line = PointLine(point.Id);
        return line is not null && PointOpen(session, point, line) ? 0 : Locked;
    }

    public static int ValidateStoryStage(Session session, uint stageId) => StagePoint(stageId)?.StoryPointType == 1 ? ValidateStage(session, stageId) : Locked;

    public static int ValidatePreFight(Session session, PreFightRequest.PreFightRequestPreFightData request)
    {
        int code = ValidateStage(session, request.StageId);
        if (code != 0) return code;
        // IsHasAssist mirrors the client's global PlayerPrefs AssistSwitch, sent from every battle room; no assist is granted.
        if (StagePoint(request.StageId)?.StoryPointType == 1 || request.ChallengeCount != 1 || request.SpeedrunStageId != 0) return Locked;
        return ValidateTeam(session, request.StageId, new CerberusGameTeamInfo
        {
            CharacterIdList = request.CardIds ?? [], RobotIdList = request.RobotIds ?? [],
            CaptainPos = request.CaptainPos, FirstFightPos = request.FirstFightPos
        }, true);
    }

    [RequestPacketHandler("CerberusGamePassStoryPointRequest")]
    public static void CerberusGamePassStoryPointRequestHandler(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<CerberusGamePassStoryPointRequest>();
        int code = Locked;
        if (Points.Value.TryGetValue(request.StoryPointId, out var point)
            && Lines.Value.TryGetValue(request.StoryLineId, out var line)
            && line.ChapterId == request.ChapterId && line.StoryPointIds.Contains(point.Id)
            && PointOpen(session, point, line) && point.StoryPointType is 1 or 2
            && point.StoryPointTypeParams.Count > 0)
        {
            bool valid = point.StoryPointType == 1
                ? StagePassed(session, (uint)point.StoryPointTypeParams[0])
                : Communications.Value.Contains(point.StoryPointTypeParams[0]);
            if (valid)
            {
                if (!session.player.CerberusGame.PassStoryPoints.Contains(point.Id))
                {
                    session.player.CerberusGame.PassStoryPoints.Add(point.Id);
                    try { session.player.SaveChecked(); }
                    catch
                    {
                        session.player.CerberusGame.PassStoryPoints.Remove(point.Id);
                        throw;
                    }
                }
                code = 0;
            }
        }
        session.SendResponse(new CerberusGamePassStoryPointResponse { Code = code }, packet.Id);
    }

    [RequestPacketHandler("CerberusGameSetTeamRequest")]
    public static void CerberusGameSetTeamRequestHandler(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<CerberusGameSetTeamRequest>();
        int code = ValidateStage(session, request.StageId);
        if (code == 0) code = ValidateTeam(session, request.StageId, request.TeamInfo, false);
        if (code == 0)
        {
            var team = CloneTeam(request.TeamInfo!);
            GetStageState(session, request.StageId).TeamInfo = team;
            int chapterId = StageChapter(request.StageId);
            var chapter = session.player.CerberusGame.ChapterTeams.FirstOrDefault(x => x.ChapterId == chapterId);
            if (chapter is null)
            {
                chapter = new CerberusGameChapterTeamState { ChapterId = chapterId };
                session.player.CerberusGame.ChapterTeams.Add(chapter);
            }
            chapter.TeamInfo = CloneTeam(team);
            session.player.SaveChecked();
        }
        session.SendResponse(new CerberusGameSetTeamResponse { Code = code }, packet.Id);
    }

    public static int ValidateTeam(Session session, uint stageId, CerberusGameTeamInfo? team, bool includeExtraRobot)
    {
        if (team is null || team.CharacterIdList is null || team.RobotIdList is null
            || team.CharacterIdList.Count != 3 || team.RobotIdList.Count != 3
            || team.CaptainPos is < 1 or > 3 || team.FirstFightPos is < 1 or > 3) return InvalidTeam;
        var point = StagePoint(stageId);
        HashSet<int> allowedRobots;
        HashSet<int> allowedCharacters;
        if (Challenges.Value.TryGetValue((int)stageId, out var challenge))
        {
            allowedRobots = challenge.RobotIds.ToHashSet();
            allowedCharacters = challenge.CharacterIds.ToHashSet();
        }
        else if (point is { StoryPointType: 3 } && PointLine(point.Id) is { } line)
        {
            allowedCharacters = point.StoryPointTypeParams.Skip(1).ToHashSet();
            allowedRobots = Roles.Value.Where(x => x.Chapter == line.ChapterId && x.Difficulty == line.Difficulty)
                .Select(x => x.RoleId).Where(id => Robots.Value.TryGetValue(id, out var robot) && allowedCharacters.Contains(robot.CharacterId)).ToHashSet();
        }
        else return InvalidTeam;
        int extra = includeExtraRobot ? point?.ExtraRobotId ?? 0 : 0;
        // The agency injects this support robot into an empty slot, never as captain/first fighter.
        if (extra > 0 && (!team.RobotIdList.Contains(extra)
            || team.RobotIdList[team.CaptainPos - 1] == extra
            || team.RobotIdList[team.FirstFightPos - 1] == extra)) return InvalidTeam;
        HashSet<uint> deployed = [];
        for (int i = 0; i < 3; i++)
        {
            uint character = team.CharacterIdList[i];
            int robotId = team.RobotIdList[i];
            if (robotId < 0 || (character != 0 && robotId != 0)) return InvalidTeam;
            if (robotId != 0)
            {
                if ((!allowedRobots.Contains(robotId) && robotId != extra) || !Robots.Value.TryGetValue(robotId, out var robot)) return InvalidTeam;
                character = (uint)robot.CharacterId;
            }
            else if (character != 0 && (!allowedCharacters.Contains((int)character) || !session.character.Characters.Any(x => x.Id == character))) return InvalidTeam;
            if (character != 0 && !deployed.Add(character)) return InvalidTeam;
        }
        bool Occupied(int pos) => team.CharacterIdList[pos - 1] != 0 || team.RobotIdList[pos - 1] > 0;
        // The battle-room proxy requires every distinct character in the authored role pool.
        int requiredCount = allowedCharacters.Count + (extra > 0 ? 1 : 0);
        return requiredCount > 0 && deployed.Count == requiredCount
            && Occupied(team.CaptainPos) && Occupied(team.FirstFightPos) ? 0 : InvalidTeam;
    }

    private static CerberusGameTeamInfo CloneTeam(CerberusGameTeamInfo team) => new()
    {
        CharacterIdList = [.. team.CharacterIdList], RobotIdList = [.. team.RobotIdList],
        CaptainPos = team.CaptainPos, FirstFightPos = team.FirstFightPos
    };

    private static CerberusGameTeamInfo ChapterTeam(Session session, int chapterId)
    {
        var saved = session.player.CerberusGame.ChapterTeams.FirstOrDefault(x => x.ChapterId == chapterId);
        if (saved is not null) return CloneTeam(saved.TeamInfo);
        var result = new CerberusGameTeamInfo();
        if (Chapters.Value.TryGetValue(chapterId, out var chapter) && chapter.DefaultTeam.FirstOrDefault() is { } defaults)
        {
            var robots = defaults.Split('|').Select(int.Parse).ToList();
            if (robots.Count == 3) result.RobotIdList = robots;
        }
        return result;
    }

    public static CerberusGameTeamInfo GetTeam(Session session, uint stageId) => session.player.CerberusGame.Stages.FirstOrDefault(x => x.StageId == stageId) is { } stage
        ? CloneTeam(stage.TeamInfo) : ChapterTeam(session, StageChapter(stageId));

    public static CerberusGameStageState GetStageState(Session session, uint stageId)
    {
        var state = session.player.CerberusGame.Stages.FirstOrDefault(x => x.StageId == stageId);
        if (state is null)
        {
            state = new CerberusGameStageState { StageId = stageId, TeamInfo = ChapterTeam(session, StageChapter(stageId)) };
            session.player.CerberusGame.Stages.Add(state);
        }
        return state;
    }

    public static NotifyCerberusGameStageInfo RecordStageClear(Session session, uint stageId, int starMark)
    {
        var state = GetStageState(session, stageId);
        state.Passed = true;
        state.StarMark |= starMark;
        if (StagePoint(stageId) is { StoryPointType: 3 } point && !session.player.CerberusGame.PassStoryPoints.Contains(point.Id))
            session.player.CerberusGame.PassStoryPoints.Add(point.Id);
        session.player.SaveChecked();
        return BuildNotifyStageInfo(session, stageId);
    }

    public static NotifyCerberusGameStageInfo BuildNotifyStageInfo(Session session, uint stageId)
    {
        var state = session.player.CerberusGame.Stages.FirstOrDefault(x => x.StageId == stageId);
        return new NotifyCerberusGameStageInfo { CerberusGameStageInfo = new CerberusGameStageInfo
        {
            StageId = stageId, Passed = state?.Passed ?? false, StarMark = state?.StarMark ?? 0,
            GotStarRewardIds = state is null ? [] : [.. state.GotStarRewardIds],
            StageParams = state is null ? [] : [.. state.StageParams], TeamInfo = GetTeam(session, stageId)
        }};
    }

    private static bool StagePassed(Session session, uint stageId) => session.player.CerberusGame.Stages.Any(x => x.StageId == stageId && x.Passed)
        || session.stage.Stages.TryGetValue(stageId, out var stage) && stage.Passed;

    private static bool ConditionSatisfied(Session session, int id, HashSet<int>? visiting = null)
    {
        if (id <= 0) return true;
        visiting ??= [];
        if (!visiting.Add(id) || !Conditions.Value.TryGetValue(id, out var condition)) return false;
        try
        {
            if (!string.IsNullOrWhiteSpace(condition.Formula))
            {
                // EN XConditionFormula evaluates '&' and '|' left-to-right with equal precedence.
                var terms = condition.Formula.Split(['&', '|']);
                var operators = condition.Formula.Where(c => c is '&' or '|').ToArray();
                bool Term(string term) => int.TryParse(term.Trim().TrimStart('!'), out int child)
                    && (ConditionSatisfied(session, child, visiting) != term.Trim().StartsWith('!'));
                bool value = Term(terms[0]);
                for (int i = 0; i < operators.Length; i++)
                {
                    bool next = Term(terms[i + 1]);
                    value = operators[i] == '&' ? value && next : value || next;
                }
                return value;
            }
            if (condition.Params.Count == 0) return false;
            int value0 = condition.Params[0];
            return condition.Type switch
            {
                10101 => session.player.PlayerData.Level >= value0,
                10105 => StagePoint((uint)value0) is { } point
                    ? session.player.CerberusGame.PassStoryPoints.Contains(point.Id)
                    : StagePassed(session, (uint)value0),
                10201 => condition.Params.Count >= 2 && condition.Params.Skip(1).All(param => session.player.CerberusGame.Stages.FirstOrDefault(x => x.StageId == value0)?.StageParams.Contains(param) == true),
                23001 => ActivityScheduleService.IsOpen(value0, DateTimeOffset.UtcNow),
                _ => false
            };
        }
        finally { visiting.Remove(id); }
    }
}
