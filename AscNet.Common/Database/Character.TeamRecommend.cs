using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;
using MessagePack;

namespace AscNet.Common.Database;

public partial class Character
{
    [BsonElement("team_recommend_targets")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, TeamRecommendTargetState> TeamRecommendTargets { get; set; } = new();

    [BsonElement("team_recommend_finish_events")]
    public HashSet<int> TeamRecommendFinishEvents { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class TeamRecommendTargetState
{
    public int BaseCharacterId { get; set; }
    public int TeamCfgId { get; set; }
    public int BaseFormationId { get; set; }
    // AscNet policy: frozen server-local top snapshot chosen via SourceType=2 (or copied via 3).
    public TeamRecommendFormationData? TargetFormation { get; set; }
}

[MessagePackObject(true)]
public sealed class TeamRecommendFormationData
{
    public List<TeamRecommendCharacterData> CharacterDatas { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class TeamRecommendCharacterData
{
    public int CharacterId { get; set; }
    public int CharacterQualityStar { get; set; }
    public int WeaponId { get; set; }
    public int WeaponOverrunChoseSuit { get; set; }
    public List<TeamRecommendResonanceData> WeaponResonanceDatas { get; set; } = new();
    public List<int> EquipIds { get; set; } = new();
    public List<TeamRecommendResonanceData> EquipResonanceDatas { get; set; } = new();
    public int PartnerId { get; set; }
}

[MessagePackObject(true)]
public sealed class TeamRecommendResonanceData
{
    public int Slot { get; set; }
    public int Type { get; set; }
    public int TemplateId { get; set; }
}
