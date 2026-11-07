using MessagePack;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public sealed class CerberusGameTeamInfo
{
    public List<uint> CharacterIdList { get; set; } = [0, 0, 0];
    public List<int> RobotIdList { get; set; } = [0, 0, 0];
    public int CaptainPos { get; set; } = 1;
    public int FirstFightPos { get; set; } = 1;
}

[MessagePackObject(true)]
public sealed class CerberusGameStageInfo
{
    public uint StageId { get; set; }
    public bool Passed { get; set; }
    public int StarMark { get; set; }
    public List<int> GotStarRewardIds { get; set; } = [];
    public List<int> StageParams { get; set; } = [];
    public CerberusGameTeamInfo TeamInfo { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class CerberusGameStoryLineInfo
{
    public List<int> PassStoryPoints { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class CerberusGameChapterInfo
{
    public int ChapterId { get; set; }
    public List<CerberusGameStoryLineInfo> StoryLineInfos { get; set; } = [];
    public CerberusGameTeamInfo TeamInfo { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class CerberusGameData
{
    public int ActivityId { get; set; }
    public List<CerberusGameChapterInfo> ChapterInfos { get; set; } = [];
    public List<CerberusGameStageInfo> StageInfos { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyCerberusGameData
{
    public CerberusGameData CerberusGameData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyCerberusGameStageInfo
{
    public CerberusGameStageInfo CerberusGameStageInfo { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class CerberusGamePassStoryPointRequest
{
    public int ChapterId { get; set; }
    public int StoryLineId { get; set; }
    public int StoryPointId { get; set; }
}

[MessagePackObject(true)]
public sealed class CerberusGamePassStoryPointResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class CerberusGameSetTeamRequest
{
    public uint StageId { get; set; }
    public CerberusGameTeamInfo? TeamInfo { get; set; }
}

[MessagePackObject(true)]
public sealed class CerberusGameSetTeamResponse
{
    public int Code { get; set; }
}
