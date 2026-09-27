using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

// One in-flight ItemBuyAsset purchase. Inventory commit is detected by the target stack's TotalBuyTimes reaching
// TotalBuyTimes; the journal is cleared in the same Player write that records its 11202 spend progress.
public sealed class BuyAssetPendingOperation
{
    public int ItemId { get; set; }
    public int Times { get; set; }
    public int Gain { get; set; }
    public int BuyTimes { get; set; }
    public int TotalBuyTimes { get; set; }
    public long OccurredAt { get; set; }
    public List<BuyAssetPendingDebit> Debits { get; set; } = new();
}

public sealed class BuyAssetPendingDebit
{
    public int ItemId { get; set; }
    public int Count { get; set; }
}

public partial class Player
{
    [BsonElement("pending_buy_asset")]
    [BsonIgnoreIfNull]
    public BuyAssetPendingOperation? PendingBuyAsset { get; set; }
}
