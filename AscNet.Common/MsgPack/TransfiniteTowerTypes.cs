using AscNet.Common.Database;
using MessagePack;

namespace AscNet.Common.MsgPack;

// Field names recovered from the installed 4.8 Lua (xtransfinitetoweragency.lua / xtransfinitetowercontrol.lua).
[MessagePackObject(true)] public sealed class TransfiniteTowerFightResult { public int Order { get; set; } public int SpendTime { get; set; } }
[MessagePackObject(true)] public sealed class NotifyTransfiniteTowerData { public TransfiniteTowerState? TransfiniteTowerDataDb { get; set; } }
[MessagePackObject(true)] public sealed class NotifyTransfiniteTowerChapterInfo { public TransfiniteTowerChapterInfo? ChapterInfo { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteTowerStageSettleRequest { public int ChapterId { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteTowerStageSettleResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteTowerChapterSettleRequest { public int ChapterId { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteTowerChapterSettleResponse { public int Code { get; set; } public TransfiniteTowerSettleInfo? SettleInfo { get; set; } public int Rank { get; set; } public int TotalCount { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteTowerRollbackRequest { public int ChapterId { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteTowerRollbackResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteTowerResetChapterRequest { public int ChapterId { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteTowerResetChapterResponse { public int Code { get; set; } }
