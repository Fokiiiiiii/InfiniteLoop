using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    /// <summary>Favorite-character mail ids received into the collection box (NotifyLoginMailCollectionBoxData.ReceivedFavoriteMailIds).</summary>
    [BsonElement("received_favorite_mail_ids")] public List<int> ReceivedFavoriteMailIds { get; set; } = [];
}
