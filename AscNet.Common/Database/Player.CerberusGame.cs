using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("cerberus_game")]
    public CerberusGameState CerberusGame { get; set; } = new();
}

public sealed partial class CerberusGameState
{
    [BsonElement("activity_id")] public int ActivityId { get; set; }
    [BsonElement("pass_story_points")] public List<int> PassStoryPoints { get; set; } = [];
    [BsonElement("stages")] public List<CerberusGameStageState> Stages { get; set; } = [];
    [BsonElement("chapter_teams")] public List<CerberusGameChapterTeamState> ChapterTeams { get; set; } = [];
}

public sealed class CerberusGameStageState
{
    [BsonElement("stage_id")] public uint StageId { get; set; }
    [BsonElement("passed")] public bool Passed { get; set; }
    [BsonElement("star_mark")] public int StarMark { get; set; }
    [BsonElement("got_star_reward_ids")] public List<int> GotStarRewardIds { get; set; } = [];
    [BsonElement("stage_params")] public List<int> StageParams { get; set; } = [];
    [BsonElement("team_info")] public CerberusGameTeamInfo TeamInfo { get; set; } = new();
}

public sealed class CerberusGameChapterTeamState
{
    [BsonElement("chapter_id")] public int ChapterId { get; set; }
    [BsonElement("team_info")] public CerberusGameTeamInfo TeamInfo { get; set; } = new();
}
