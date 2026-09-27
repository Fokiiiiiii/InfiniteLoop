using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public sealed class GachaState
{
    [BsonElement("infos")]
    public List<GachaStateInfo> Infos { get; set; } = new();
    [BsonElement("selected_group_id_to_gacha_id")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> SelectedGroupIdToGachaId { get; set; } = new();
}

public sealed class GachaStateInfo
{
    [BsonElement("id")]
    public int Id { get; set; }
    /// <summary>Draw count per GachaReward Id; client GridInfoList Times.</summary>
    [BsonElement("reward_times")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> RewardTimes { get; set; } = new();
    [BsonElement("total_times")]
    public int TotalTimes { get; set; }
    [BsonElement("miss_times")]
    public int MissTimes { get; set; }
    [BsonElement("exchange_count")]
    public int ExchangeCount { get; set; }
    [BsonElement("records")]
    public List<GachaStateRecord> Records { get; set; } = new();
    [BsonElement("pending")]
    public GachaPendingOperation? Pending { get; set; }
}

/// <summary>Rolled, saved-before-payment outcome so a failed completion retries the same receipt.</summary>
public sealed class GachaPendingOperation
{
    public List<int> RewardIds { get; set; } = new();
    public int MissTimes { get; set; }
    public List<int> CourseRewardIds { get; set; } = new();
    public long Time { get; set; }
    public int CostItemId { get; set; }
    public int CostCount { get; set; }
    public int ExchangeNum { get; set; }
    /// <summary>Live-only: the intent save threw, so it may or may not be durable; re-save before paying.</summary>
    [BsonIgnore]
    public bool Unconfirmed { get; set; }
}

/// <summary>Granted goods as paid (after duplicate conversion) for the client research log.</summary>
public sealed class GachaStateRecord
{
    [BsonElement("reward_id")]
    public int RewardId { get; set; }
    [BsonElement("template_id")]
    public int TemplateId { get; set; }
    [BsonElement("count")]
    public int Count { get; set; }
    [BsonElement("reward_type")]
    public int RewardType { get; set; }
    [BsonElement("convert_from")]
    public int ConvertFrom { get; set; }
    [BsonElement("time")]
    public long Time { get; set; }
}

public partial class Player
{
    [BsonElement("gacha")]
    public GachaState Gacha { get; set; } = new();
}
