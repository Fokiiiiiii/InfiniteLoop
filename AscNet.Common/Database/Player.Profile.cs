using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("profile")]
    public PlayerProfileState Profile { get; set; } = new();
}

public sealed class PlayerProfileState
{
    public List<int> HiddenTitleTypes { get; set; } = [];
    public bool HideCollection { get; set; }
    public List<ProfileWall> Walls { get; set; } = [];
    public List<uint> UnlockedDecorationIds { get; set; } = [];
    public List<ProfileDiary> Diaries { get; set; } = [];
    public long NextDiaryId { get; set; }
    public long NextMessageId { get; set; }
    public long LikePeriod { get; set; } = long.MinValue;
    public int LikesGiven { get; set; }
}
