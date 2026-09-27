using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("punishaar")]
    public PlayerPunishaarState Punishaar { get; set; } = new();
}

// Circuit Calculus: the whole run economy lives here; nothing crosses documents except task claims.
public sealed class PlayerPunishaarState
{
    public int ActivityId { get; set; }
    public int CurrentStageId { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, PunishaarStage> StageSaves { get; set; } = [];
    public List<int> PassedStageIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> StageChallengeCounts { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, PunishaarCatalog> CharacterCatalogs { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, PunishaarCatalog> PartnerCatalogs { get; set; } = [];
    public List<int> EquipCatalogs { get; set; } = [];
    public List<int> ResonanceCatalogs { get; set; } = [];
    // Best single-run won fight nodes, key = stageId * 10 + NodeType (NodeType is 1..5).
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> BestWonNodeCounts { get; set; } = [];
    // Best endless round reached, keyed by stageId.
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> BestRounds { get; set; } = [];
}
