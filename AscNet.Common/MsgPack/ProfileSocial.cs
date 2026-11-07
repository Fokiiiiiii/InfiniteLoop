using MessagePack;
namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public sealed class ProfileResult { public int Code { get; set; } }
[MessagePackObject(true)]
public sealed class ProfileWall
{
    public int Id { get; set; }
    public uint PedestalId { get; set; }
    public uint BackgroundId { get; set; }
    public bool IsShow { get; set; }
    public List<ProfileCollectionPlacement> CollectionSetInfos { get; set; } = [];
}
[MessagePackObject(true)]
public sealed class ProfileCollectionPlacement { public uint Id { get; set; } public int X { get; set; } public int Y { get; set; } public int SizeId { get; set; } }
[MessagePackObject(true)]
public sealed class ProfileShowSetting { public int Type { get; set; } public int Hide { get; set; } }
[MessagePackObject(true)]
public sealed class ScoreTitleShowSetRequest { public List<ProfileShowSetting> ScoreTitleShowSetInfos { get; set; } = []; }
[MessagePackObject(true)]
public sealed class CollectionShowSetRequest { public bool IsHideCollection { get; set; } }
[MessagePackObject(true)]
public sealed class ChangePlayerDisplayCharIdRequest { public long CharId { get; set; } }
[MessagePackObject(true)]
public sealed class CollectionWallVisibility { public int Id { get; set; } public bool IsShow { get; set; } }
[MessagePackObject(true)]
public sealed class EditCollectionWallIsShowRequest { public List<CollectionWallVisibility> CollectionWallIsShowInfos { get; set; } = []; }
[MessagePackObject(true)]
public sealed class ProfileDiary
{
    public long Id { get; set; }
    public long PlayerId { get; set; }
    public string Name { get; set; } = "";
    public long CurrHeadPortraitId { get; set; }
    public string Content { get; set; } = "";
    public long CreateTime { get; set; }
    public bool IsBanMsg { get; set; }
    public int UpCount => LikedPlayerIds.Count;
    public int LeaveMsgCount => Msgs.Count;
    [IgnoreMember]
    public List<long> LikedPlayerIds { get; set; } = [];
    public List<ProfileMessage> Msgs { get; set; } = [];
}
[MessagePackObject(true)]
public sealed class ProfileMessage
{
    public long Id { get; set; }
    public long DailyId { get; set; }
    public long PlayerId { get; set; }
    public string Name { get; set; } = "";
    public long CurrHeadPortraitId { get; set; }
    public string Content { get; set; } = "";
    public long LeaveMsgTime { get; set; }
}
[MessagePackObject(true)]
public sealed class PersonalInfoRequest
{
    public long PlayerId { get; set; }
    public long DailyId { get; set; }
    public long MsgId { get; set; }
    public long viewId { get; set; }
    public int PageNum { get; set; }
    public string DailyContent { get; set; } = "";
    public string LeaveMsg { get; set; } = "";
}
[MessagePackObject(true)]
public sealed class GetDailysResponse { public int Code { get; set; } public List<ProfileDiary> DailyList { get; set; } = []; }
[MessagePackObject(true)]
public sealed class LookOverLeaveMsgResponse { public int Code { get; set; } public List<ProfileMessage> MsgArray { get; set; } = []; }
[MessagePackObject(true)]
public sealed class ViewPersonalInfoResponse { public int Code { get; set; } public Dictionary<string, object?>? data { get; set; } }
