using MessagePack;
using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

// Persisted state doubles as the Lua-read wire shape (xtransfinitetowermodel.lua); server-only members are [IgnoreMember].
[MessagePackObject(true), BsonIgnoreExtraElements]
public sealed class TransfiniteTowerState
{
    [IgnoreMember] public int ActivityId { get; set; }
    public List<TransfiniteTowerChapterInfo> ChapterInfoList { get; set; } = new();
    public List<int> PassedTeachStageIds { get; set; } = new();
    [IgnoreMember] public TransfiniteTowerPendingFight? PendingFight { get; set; }
}

[MessagePackObject(true), BsonIgnoreExtraElements]
public sealed class TransfiniteTowerChapterInfo
{
    public int ChapterId { get; set; }
    public TransfiniteTowerBattleInfo? CurBattleInfo { get; set; }
    public int MaxPassedOrder { get; set; }
    public bool CanSetMvp { get; set; }
    public int BestOrder { get; set; }
    public int BestTotalSpendTime { get; set; }
    public TransfiniteTowerSettleInfo? SettleInfo { get; set; }
    public TransfiniteTowerRankInfo? RankInfo { get; set; }
    // Lua indexes this by Order, so it is kept dense and ordered 1..n.
    public List<TransfiniteTowerStageRecord> LastStageRecordList { get; set; } = new();
}

[MessagePackObject(true), BsonIgnoreExtraElements]
public sealed class TransfiniteTowerBattleInfo
{
    public int StageProgressIndex { get; set; }
    public int RollbackOrder { get; set; }
    public List<TransfiniteTowerStageRecord> StageRecordList { get; set; } = new();
    public TransfiniteTowerStageRecord? PendingStageRecord { get; set; }
    public List<TransfiniteTowerCharacterCount> CharacterCountList { get; set; } = new();
    public TransfiniteTowerTeamSelection? LastTeamSelection { get; set; }
}

[MessagePackObject(true), BsonIgnoreExtraElements]
public sealed class TransfiniteTowerStageRecord
{
    public int Order { get; set; }
    public int SpendTime { get; set; }
    public List<TransfiniteTowerTeamMember> Team { get; set; } = new();
    [IgnoreMember] public int StageCfgId { get; set; }
    [IgnoreMember] public TransfiniteTowerTeamSelection? Selection { get; set; }
    // Energy owners charged by this record: (CharacterCfgId, CharacterId) of non-navigators.
    [IgnoreMember] public List<TransfiniteTowerCharacterCount> Charged { get; set; } = new();
}

[MessagePackObject(true), BsonIgnoreExtraElements]
public sealed class TransfiniteTowerTeamMember
{
    public int FightId { get; set; }
    public bool IsNavigator { get; set; }
}

[MessagePackObject(true), BsonIgnoreExtraElements]
public sealed class TransfiniteTowerCharacterCount
{
    public int CharacterCfgId { get; set; }
    public int CharacterId { get; set; }
    public int UsedCount { get; set; }
}

[MessagePackObject(true), BsonIgnoreExtraElements]
public sealed class TransfiniteTowerTeamSelection
{
    public List<int> CardIds { get; set; } = new();
    public List<int> RobotIds { get; set; } = new();
    public int CaptainPos { get; set; }
    public int FirstFightPos { get; set; }
}

[MessagePackObject(true), BsonIgnoreExtraElements]
public sealed class TransfiniteTowerSettleInfo
{
    public int Order { get; set; }
    public int TotalSpendTime { get; set; }
    public int TotalPower { get; set; }
    public List<TransfiniteTowerSettleCharacter> Characters { get; set; } = new();
    public int MvpFightId { get; set; }
    public bool IsNewRecord { get; set; }
    [IgnoreMember] public long AchievedAt { get; set; }
}

[MessagePackObject(true), BsonIgnoreExtraElements]
public sealed class TransfiniteTowerSettleCharacter
{
    public int FightId { get; set; }
    public bool IsTrial { get; set; }
    public int Quality { get; set; }
    public int Power { get; set; }
}

[MessagePackObject(true), BsonIgnoreExtraElements]
public sealed class TransfiniteTowerRankInfo
{
    public int MaxOrder { get; set; }
    public int TotalSpendTime { get; set; }
    public int TotalPower { get; set; }
    public int MvpFightId { get; set; }
    [IgnoreMember] public List<TransfiniteTowerSettleCharacter> Characters { get; set; } = new();
    [IgnoreMember] public long AchievedAt { get; set; }
}

// Server-held PreFight commitment: the only team/clock a settlement may credit.
[BsonIgnoreExtraElements]
public sealed class TransfiniteTowerPendingFight
{
    public int ChapterId { get; set; }
    public int StageCfgId { get; set; }
    public long StageId { get; set; }
    public long FightId { get; set; }
    public long StartedAt { get; set; }
    public TransfiniteTowerTeamSelection Selection { get; set; } = new();
    public List<TransfiniteTowerTeamMember> Team { get; set; } = new();
    public List<TransfiniteTowerCharacterCount> Charged { get; set; } = new();
}

public partial class Player
{
    [BsonElement("transfinite_tower")] public TransfiniteTowerState? TransfiniteTower { get; set; }
}
