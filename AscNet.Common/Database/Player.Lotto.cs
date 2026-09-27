using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public sealed class LottoState
{
    [BsonElement("infos")]
    public List<LottoStateInfo> Infos { get; set; } = new();
    [BsonElement("selected_primary_id_to_lotto_id")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> SelectedPrimaryIdToLottoId { get; set; } = new();
}

public sealed class LottoStateInfo
{
    [BsonElement("id")]
    public int Id { get; set; }

    [BsonElement("lotto_primary_id")]
    public int LottoPrimaryId { get; set; }

    [BsonElement("extra_reward_state")]
    public int ExtraRewardState { get; set; }

    [BsonElement("lotto_rewards")]
    public List<int> LottoRewards { get; set; } = new();

    [BsonElement("lotto_records")]
    public List<LottoStateRecord> LottoRecords { get; set; } = new();

    [BsonElement("ticket_purchase_count")]
    public int TicketPurchaseCount { get; set; }

    [BsonElement("pending")]
    public LottoPendingOperation? Pending { get; set; }
}

public sealed class LottoPendingOperation
{
    public int RewardId { get; set; }
    public int LottoTime { get; set; }
    public int TicketId { get; set; }
    public int TicketKey { get; set; }
    public int CostItemId { get; set; }
    public int CostCount { get; set; }
    public int ItemCount { get; set; }
}

public sealed class LottoStateRecord
{
    [BsonElement("reward_id")]
    public int RewardId { get; set; }

    [BsonElement("lotto_time")]
    public int LottoTime { get; set; }
}

public partial class Player
{
    [BsonElement("lotto")]
    public LottoState Lotto { get; set; } = new();
}
