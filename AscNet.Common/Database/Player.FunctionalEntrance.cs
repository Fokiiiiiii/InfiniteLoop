using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    // FunctionId -> acknowledged RedPointNum (XFunctionModel:MarkEntryRedPoint); replayed in
    // NotifyFunctionalEntranceData.RedPointDatas at login.
    [BsonElement("functional_entrance_red_points")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> FunctionalEntranceRedPoints { get; set; } = new();
}
