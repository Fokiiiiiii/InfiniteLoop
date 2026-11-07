using MongoDB.Bson.Serialization.Attributes;
namespace AscNet.Common.Database;
public partial class Player
{
    [BsonElement("extra_chapter_treasures")] public List<int> ExtraChapterTreasures { get; set; } = new();
    [BsonElement("short_story_treasures")] public List<int> ShortStoryTreasures { get; set; } = new();
}
