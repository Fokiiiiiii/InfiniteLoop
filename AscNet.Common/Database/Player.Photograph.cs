using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    /// <summary>Random home-scene settings (XPhotographManager random background pool).</summary>
    [BsonElement("random_background")]
    public RandomBackgroundState RandomBackground { get; set; } = new();
}

[BsonIgnoreExtraElements]
public class RandomBackgroundState
{
    [BsonElement("enabled")]
    public bool IsRandomBackground { get; set; }

    [BsonElement("fashion_enabled")]
    public bool IsBackgroundRandomFashion { get; set; }

    [BsonElement("pool")]
    public List<RandomBackgroundEntry> Pool { get; set; } = [];

    public RandomBackgroundState Clone() => new()
    {
        IsRandomBackground = IsRandomBackground,
        IsBackgroundRandomFashion = IsBackgroundRandomFashion,
        Pool = Pool.Select(entry => new RandomBackgroundEntry
        {
            BackgroundId = entry.BackgroundId,
            Chars = entry.Chars.Select(c => new RandomBackgroundChar
            {
                CharId = c.CharId,
                IsRandom = c.IsRandom,
                Fashions = [.. c.Fashions]
            }).ToList()
        }).ToList()
    };
}

[BsonIgnoreExtraElements]
public class RandomBackgroundEntry
{
    [BsonElement("background_id")]
    public int BackgroundId { get; set; }

    [BsonElement("chars")]
    public List<RandomBackgroundChar> Chars { get; set; } = [];
}

[BsonIgnoreExtraElements]
public class RandomBackgroundChar
{
    [BsonElement("char_id")]
    public int CharId { get; set; }

    [BsonElement("is_random")]
    public bool IsRandom { get; set; }

    [BsonElement("fashions")]
    public List<int> Fashions { get; set; } = [];
}
