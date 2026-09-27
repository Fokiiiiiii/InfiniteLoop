using MessagePack;

namespace AscNet.Common.MsgPack;

// Wire shapes: xmanager/xminesweepingmanager.lua and xentity/xminesweeping/*.lua (4.8).
[MessagePackObject(true)]
public sealed class MineSweepingGridData
{
    public int XIndex { get; set; }
    public int YIndex { get; set; }
    public int Type { get; set; }
    public int RoundMineNumber { get; set; }
}

[MessagePackObject(true)]
public sealed class MineSweepingStageInfo
{
    public int ActivityStageId { get; set; }
    public int WhiteGridTotalNumber { get; set; }
    public int AllowMineNumber { get; set; }
    public int WhiteGridOpenNumber { get; set; }
    public int MineGridOpenNumber { get; set; }
    public int FailedCounts { get; set; }
    public int Status { get; set; }
}

[MessagePackObject(true)]
public sealed class MineSweepingChapterData
{
    public int ActivityChapterId { get; set; }
    public int ChallengeCounts { get; set; }
    public List<MineSweepingStageInfo> ActivityStageList { get; set; } = [];
    public List<MineSweepingGridData> CurGridList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyMineSweepingData
{
    public int ActivityId { get; set; }
    public List<MineSweepingChapterData> MineSweepingList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class MineSweepingStartStageRequest
{
    public int ActivityChapterId { get; set; }
    public int ActivityStageId { get; set; }
}

[MessagePackObject(true)]
public sealed class MineSweepingStartStageResponse
{
    public int Code { get; set; }
    public MineSweepingStageInfo? ActivityStageInfo { get; set; }
    public int ChallengeCounts { get; set; }
}

[MessagePackObject(true)]
public sealed class MineSweepingOpenRequest
{
    public int ActivityChapterId { get; set; }
    public int ActivityStageId { get; set; }
    public int XIndex { get; set; }
    public int YIndex { get; set; }
}

[MessagePackObject(true)]
public sealed class MineSweepingOpenResponse
{
    public int Code { get; set; }
    public MineSweepingStageInfo? ActivityStageInfo { get; set; }
    public List<MineSweepingGridData> RefreshGridList { get; set; } = [];
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class MineSweepingFlagRequest
{
    public int ActivityChapterId { get; set; }
    public int ActivityStageId { get; set; }
    public int XIndex { get; set; }
    public int YIndex { get; set; }
    public bool IsFlag { get; set; }
}

[MessagePackObject(true)]
public sealed class MineSweepingFlagResponse { public int Code { get; set; } }
