using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    /// <summary>Favorite song ids in wire order (oldest first); capped by MusicPlayerConfig FavoriteSongMaxCount.</summary>
    [BsonElement("favorite_songs")]
    public List<int> FavoriteSongs { get; set; } = new();

    /// <summary>Background song ids in wire order (reverse of displayed playlist); capped by BackgroundSongMaxCount.</summary>
    [BsonElement("background_songs")]
    public List<int> BackgroundSongs { get; set; } = new();
}
