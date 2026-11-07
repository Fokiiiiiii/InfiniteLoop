using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public sealed partial class CerberusGameState
{
    [BsonElement("fight")] public CerberusGameFightAttempt? Fight { get; set; }
    [BsonElement("fight_receipts")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, CerberusGameFightReceipt> FightReceipts { get; set; } = new();
}

public sealed class CerberusGameFightAttempt
{
    [BsonElement("fight_id")] public uint FightId { get; set; }
    [BsonElement("stage_id")] public uint StageId { get; set; }
    [BsonElement("pre_fight")] public byte[] PreFight { get; set; } = [];
    [BsonElement("result")] public byte[]? Result { get; set; }
    [BsonElement("stage_data")] public byte[]? StageData { get; set; }
    [BsonElement("is_first_clear")] public bool IsFirstClear { get; set; }
    [BsonElement("reward_ids")] public List<int> RewardIds { get; set; } = [];
}

public sealed class CerberusGameFightReceipt
{
    [BsonElement("stage_id")] public uint StageId { get; set; }
    [BsonElement("result_hash")] public byte[] ResultHash { get; set; } = [];
    [BsonElement("response")] public byte[] Response { get; set; } = [];
}
