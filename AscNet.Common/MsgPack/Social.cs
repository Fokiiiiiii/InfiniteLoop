using MessagePack;
using PlayerInfo = AscNet.Common.MsgPack.GetPlayerInfoListResponse.GetPlayerInfoListResponsePlayerInfo;
namespace AscNet.Common.MsgPack;

[MessagePackObject(true)] public class ApplyFriendRequest { public uint TargetPlayerId { get; set; } }
[MessagePackObject(true)] public class ApplyFriendResponse { public int Code { get; set; } public uint OperationTime { get; set; } }
[MessagePackObject(true)] public class OperationApplyFriendRequest { public uint TargetPlayerId { get; set; } public bool IsAccept { get; set; } }
[MessagePackObject(true)] public class OperationApplyFriendResponse { public int Code { get; set; } }
[MessagePackObject(true)] public class DeleteFriendRequest { public List<uint> Ids { get; set; } = new(); }
[MessagePackObject(true)] public class DeleteFriendResponse { public int Code { get; set; } }
[MessagePackObject(true)] public class FriendRemarkRequest { public uint Id { get; set; } public string Name { get; set; } = string.Empty; }
[MessagePackObject(true)] public class FriendRemarkResponse { public int Code { get; set; } }
[MessagePackObject(true)] public class BlockPlayerRequest { public uint PlayerId { get; set; } }
[MessagePackObject(true)] public class BlockPlayerResponse { public int Code { get; set; } }
[MessagePackObject(true)] public class RemoveBlacklistRequest { public uint PlayerId { get; set; } }
[MessagePackObject(true)] public class RemoveBlacklistResponse { public int Code { get; set; } }
[MessagePackObject(true)] public class GetRecommendPlayerListResponse { public int Code { get; set; } public uint RefreshTime { get; set; } public List<PlayerInfo> RecommendFriendsList { get; set; } = new(); }
[MessagePackObject(true)] public class GetSpecifyPlayerInfoRequest { public uint Id { get; set; } }
[MessagePackObject(true)] public class GetSpecifyPlayerInfoResponse { public int Code { get; set; } public PlayerInfo? PlayerInfo { get; set; } }
[MessagePackObject(true)] public class SocialRemark { public uint Id { get; set; } public string Remark { get; set; } = string.Empty; }
[MessagePackObject(true)] public class SocialBlock { public uint PlayerId { get; set; } public uint BlockTime { get; set; } }
[MessagePackObject(true)] public class NotifyAddApply { public uint ApplyId { get; set; } public uint CreateTime { get; set; } }
[MessagePackObject(true)] public class NotifyAddFriend { public uint PlayerId { get; set; } public uint CreateTime { get; set; } public int Exp { get; set; } }
[MessagePackObject(true)] public class NotifyDeleteFriend { public uint PlayerId { get; set; } }
[MessagePackObject(true)] public class NotifyBlock { public uint PlayerId { get; set; } }
[MessagePackObject(true)] public class NotifyRefuseApply { }
