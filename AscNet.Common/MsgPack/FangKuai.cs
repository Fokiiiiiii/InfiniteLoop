using MessagePack;

namespace AscNet.Common.MsgPack;

// Installed 4.8 XFangKuai Lua: control start/sync/settle, stage/activity entities,
// and XUi/XUiFangKuai/XEntity/XFangKuaiBlock:GetServerData.
[MessagePackObject(true)]
public sealed class NotifyFangKuaiData
{
    public int ActivityId { get; set; }
    public List<int> FinishedStageIds { get; set; } = [];
    public Dictionary<int, FangKuaiStageHistory> StageHistroyDict { get; set; } = [];
    public Dictionary<int, FangKuaiStageData> StageDataDict { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class FangKuaiStageHistory
{
    public long MaxScore { get; set; }
    public int TotalRound { get; set; }
    public int MaxRound { get; set; }
}

[MessagePackObject(true)]
public sealed class FangKuaiStageData
{
    public int StageId { get; set; }
    public long Point { get; set; }
    public int Round { get; set; }
    public int ExtraRound { get; set; }
    public int Combo { get; set; }
    public int FrozenRoundCount { get; set; }
    public int FallingBlockCount { get; set; }
    public int FallingBlockCd { get; set; }
    // Four indexed item slots; zero marks an empty slot without truncating Lua ipairs.
    public List<int> ItemIds { get; set; } = [];
    public List<int> HistoryItemIds { get; set; } = [];
    public List<int> HistoryUsedItemIds { get; set; } = [];
    public List<int> HistoryDiscardItemIds { get; set; } = [];
    public List<FangKuaiBlockData> Blocks { get; set; } = [];
    public List<FangKuaiBlockData> PreviewBlocks { get; set; } = [];
    public int FrenzyVal { get; set; }
    public int FrenzyStage { get; set; }
    public int FrenzyEnergy { get; set; }
    public int AccumulatedEnhanceCount { get; set; }
    public List<int> EnhancedItemIndexes { get; set; } = [];
    public List<FangKuaiFrenzyRecord> HistoryFrenzyRecords { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class FangKuaiBlockData
{
    public int Id { get; set; }
    public int BlockId { get; set; }
    public int Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Length { get; set; }
    public int Color { get; set; }
    public int Direction { get; set; }
    public int ItemId { get; set; }
    public int HitCount { get; set; }
}

[MessagePackObject(true)]
public sealed class FangKuaiItemOperator
{
    public int OperatorType { get; set; }
    public int Index { get; set; }
    public int Id { get; set; }
}

[MessagePackObject(true)]
public sealed class FangKuaiComboScore
{
    public long BaseScore { get; set; }
    public int ComboCount { get; set; }
}

[MessagePackObject(true)]
public sealed class FangKuaiFrenzyRecord
{
    public int EntryFrenzyRound { get; set; }
    public int FrenzyMaxLevel { get; set; }
}

[MessagePackObject(true)]
public sealed class FangKuaiOperatorData
{
    public int Round { get; set; }
    public long Point { get; set; }
    public int Combo { get; set; }
    public int FrozenRoundCount { get; set; }
    public int FallingBlockCount { get; set; }
    public int FallingBlockCd { get; set; }
    public List<FangKuaiItemOperator> ItemOperatorList { get; set; } = [];
    public List<FangKuaiComboScore> ComboScoreList { get; set; } = [];
    public List<FangKuaiBlockData> PreviewBlocks { get; set; } = [];
    public List<FangKuaiBlockData> Blocks { get; set; } = [];
    public int FrenzyVal { get; set; }
    public int FrenzyStage { get; set; }
    public int FrenzyEnergy { get; set; }
    public int AccumulatedEnhanceCount { get; set; }
    public List<int> EnhancedItemIndexes { get; set; } = [];
    public List<FangKuaiFrenzyRecord> HistoryFrenzyRecords { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class FangKuaiSettleData
{
    public long Point { get; set; }
    public int Round { get; set; }
    public bool IsNewScoreRecord { get; set; }
    public bool IsNewRoundRecord { get; set; }
    public bool IsStageFinished { get; set; }
}

[MessagePackObject(true)]
public sealed class FangKuaiStageStartRequest
{
    public int StageId { get; set; }
    public int CharacterId { get; set; }
}

[MessagePackObject(true)]
public sealed class FangKuaiStageStartResponse
{
    public int Code { get; set; }
    public FangKuaiStageData CurData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class FangKuaiStageSyncOperatorRequest
{
    public int StageId { get; set; }
    public FangKuaiOperatorData OperatorData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class FangKuaiStageSyncOperatorResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class FangKuaiStageSettleRequest
{
    public int StageId { get; set; }
    public int SettleType { get; set; }
}

[MessagePackObject(true)]
public sealed class FangKuaiStageSettleResponse
{
    public int Code { get; set; }
    public FangKuaiSettleData SettleData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyFangKuaiCurStageData
{
    public FangKuaiStageData CurData { get; set; } = new();
}
