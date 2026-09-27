using AscNet.Common.Database;
using MessagePack;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)] public sealed class TransfiniteTowerGetRankRequest { public int ChapterId { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteTowerGetRankResponse { public int Code { get; set; } public List<TransfiniteTowerRankShow> RankList { get; set; } = new(); public int Rank { get; set; } public int TotalCount { get; set; } public int LastRankTotalSpendTime { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteTowerRankShow { public long Id { get; set; } public string Name { get; set; } = string.Empty; public long HeadPortraitId { get; set; } public long HeadFrameId { get; set; } public int RankNum { get; set; } public int MaxOrder { get; set; } public int TotalSpendTime { get; set; } public int TotalPower { get; set; } public int MvpFightId { get; set; } public List<TransfiniteTowerSettleCharacter> Characters { get; set; } = new(); }
[MessagePackObject(true)] public sealed class TransfiniteTowerSetMvpRequest { public int ChapterId { get; set; } public int MvpFightId { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteTowerSetMvpResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class NotifyTransfiniteTowerRankReward { public List<RewardGoods> RewardGoodsList { get; set; } = new(); }
