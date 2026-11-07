using MessagePack;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public sealed class QueryPlayerDetailRequest { public long PlayerId { get; set; } }
[MessagePackObject(true)]
public sealed class QueryPlayerDetailResponse { public int Code { get; set; } public Dictionary<string, object?>? Detail { get; set; } }
[MessagePackObject(true)]
public sealed class SetAppearanceRequest
{
    public int CharactersAppearanceType { get; set; }
    public List<long> Characters { get; set; } = [];
    public AppearanceSettingInfo? AppearanceSettingInfo { get; set; }
}
[MessagePackObject(true)]
public sealed class SetAppearanceResponse { public int Code { get; set; } }
[MessagePackObject(true)]
public sealed class ProfilePlayerRequest { public long PlayerId { get; set; } public long CharacterId { get; set; } }
[MessagePackObject(true)]
public sealed class QueryPlayerCharacterListResponse { public int Code { get; set; } public List<CharacterData> CharacterShows { get; set; } = []; }
[MessagePackObject(true)]
public sealed class QueryPlayerFashionResponse { public int Code { get; set; } public List<long> Fashions { get; set; } = []; public Dictionary<int, List<int>> FashionColors { get; set; } = []; }
[MessagePackObject(true)]
public sealed class QueryPlayerWeaponFashionResponse { public int Code { get; set; } public List<int> WeaponFashions { get; set; } = []; }
[MessagePackObject(true)]
public sealed class QueryPlayerTitleResponse { public int Code { get; set; } public List<NotifyScoreTitleData.NotifyScoreTitleDataTitleInfo> Titles { get; set; } = []; public List<NotifyScoreTitleData.NotifyScoreTitleDataWallInfo> Walls { get; set; } = []; }
[MessagePackObject(true)]
public sealed class QueryCharacterInfoResponse
{
    public int Code { get; set; }
    public CharacterData? CharacterData { get; set; }
    public List<EquipData> EquipData { get; set; } = [];
    public PartnerData? PartnerData { get; set; }
    public int WeaponFashionId { get; set; }
    public List<object> AssignChapterRecords { get; set; } = [];
    public List<int> AwarenessSetPositions { get; set; } = [];
}
