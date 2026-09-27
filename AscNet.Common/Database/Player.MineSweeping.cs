using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("mine_sweeping")]
    public MineSweepingState MineSweeping { get; set; } = new();
}

public sealed class MineSweepingState
{
    [BsonElement("activity_id")]
    public int ActivityId { get; set; }

    [BsonElement("challenge_counts")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> ChallengeCounts { get; set; } = [];

    [BsonElement("stages")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, MineSweepingStageState> Stages { get; set; } = [];

    // Written before the wallet/reward claim; cleared after the claim is durable.
    [BsonElement("pending")]
    public MineSweepingPendingGrant? Pending { get; set; }
}

public sealed class MineSweepingStageState
{
    [BsonElement("status")]
    public int Status { get; set; } = 1;

    [BsonElement("failed_counts")]
    public int FailedCounts { get; set; }

    // Row-major 0-based cell indexes (y * columns + x); fixed for the stage once generated.
    [BsonElement("mines")]
    public List<int> Mines { get; set; } = [];

    [BsonElement("opened")]
    public List<int> Opened { get; set; } = [];

    [BsonElement("flagged")]
    public List<int> Flagged { get; set; } = [];

    [BsonElement("white_open")]
    public int WhiteGridOpenNumber { get; set; }

    [BsonElement("mine_open")]
    public int MineGridOpenNumber { get; set; }
}

public sealed class MineSweepingPendingGrant
{
    [BsonElement("claim_key")]
    public string ClaimKey { get; set; } = string.Empty;

    [BsonElement("reward_id")]
    public int RewardId { get; set; }

    [BsonElement("cost_item_id")]
    public int CostItemId { get; set; }

    [BsonElement("cost_count")]
    public int CostCount { get; set; }
}
