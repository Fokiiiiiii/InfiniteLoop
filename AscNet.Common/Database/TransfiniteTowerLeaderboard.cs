using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace AscNet.Common.Database;

// AscNet policy: a server-local board of each player's persisted best eligible build.
public sealed class TransfiniteTowerRankEntry
{
    public static IMongoCollection<TransfiniteTowerRankEntry> collection =
        Common.db.GetCollection<TransfiniteTowerRankEntry>("transfinite_tower_rank_entries");

    [BsonId] public string Id { get; set; } = string.Empty;
    [BsonElement("activity_id")] public int ActivityId { get; set; }
    [BsonElement("chapter_id")] public int ChapterId { get; set; }
    [BsonElement("player_id")] public long PlayerId { get; set; }
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
    [BsonElement("head_portrait_id")] public long HeadPortraitId { get; set; }
    [BsonElement("head_frame_id")] public long HeadFrameId { get; set; }
    [BsonElement("max_order")] public int MaxOrder { get; set; }
    [BsonElement("total_spend_time")] public int TotalSpendTime { get; set; }
    [BsonElement("total_power")] public int TotalPower { get; set; }
    [BsonElement("mvp_fight_id")] public int MvpFightId { get; set; }
    [BsonElement("characters")] public List<TransfiniteTowerSettleCharacter> Characters { get; set; } = new();
    [BsonElement("achieved_at")] public long AchievedAt { get; set; }

    public static string BuildId(int activityId, int chapterId, long playerId) => $"{activityId}:{chapterId}:{playerId}";

    public static void EnsureIndexes()
    {
        collection.Indexes.CreateOne(new CreateIndexModel<TransfiniteTowerRankEntry>(
            Builders<TransfiniteTowerRankEntry>.IndexKeys
                .Ascending(entry => entry.ActivityId)
                .Ascending(entry => entry.ChapterId)
                .Descending(entry => entry.MaxOrder)
                .Ascending(entry => entry.TotalSpendTime)
                .Ascending(entry => entry.AchievedAt)
                .Ascending(entry => entry.PlayerId),
            new CreateIndexOptions { Name = "activity_chapter_rank" }));
    }
}

// Decided rank reward. Created atomically with player-document goods; Delivered only after every grant persisted.
[BsonIgnoreExtraElements]
public sealed class TransfiniteTowerRankRewardReceipt
{
    [BsonElement("activity_id")] public int ActivityId { get; set; }
    [BsonElement("chapter_id")] public int ChapterId { get; set; }
    [BsonElement("rank")] public int Rank { get; set; }
    [BsonElement("reward_ids")] public List<int> RewardIds { get; set; } = new();
    [BsonElement("decided_at")] public long DecidedAt { get; set; }
    [BsonElement("delivered")] public bool Delivered { get; set; }
}

public partial class Player
{
    [BsonElement("transfinite_tower_rank_rewards")]
    public List<TransfiniteTowerRankRewardReceipt> TransfiniteTowerRankRewards { get; set; } = new();
}
