using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("social")]
    public SocialPlayerState Social { get; set; } = new();
}

[BsonIgnoreExtraElements]
public class SocialPlayerState
{
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, uint> Friends { get; set; } = new();
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, uint> Applications { get; set; } = new();
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, string> Remarks { get; set; } = new();
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, uint> Blocked { get; set; } = new();
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, uint> Refused { get; set; } = new();
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, uint> Deleted { get; set; } = new();
    public long ApplicationDay { get; set; } = -1;
    public int DailyApplications { get; set; }
    public uint RecommendTime { get; set; }
}
