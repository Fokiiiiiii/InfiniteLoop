using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("fang_kuai")]
    public PlayerFangKuaiState FangKuai { get; set; } = new();
}

// Only the authoritative client board snapshot and server-owned stage progression are durable.
public sealed class PlayerFangKuaiState
{
    public int ActivityId { get; set; }
    public List<int> FinishedStageIds { get; set; } = [];
    public List<int> PlayedStageIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, long> TotalScoresByStage { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, FangKuaiStageHistory> StageHistroyDict { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, FangKuaiStageData> StageDataDict { get; set; } = [];
}
