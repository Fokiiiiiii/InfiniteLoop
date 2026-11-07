using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("shop_manual_refresh_times")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, int> ShopManualRefreshTimes { get; set; } = new();

    [BsonElement("shop_manual_refresh_periods")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, long> ShopManualRefreshPeriods { get; set; } = new();
}
