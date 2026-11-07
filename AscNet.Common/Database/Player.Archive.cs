using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("archive_unlocks")]
    public List<ArchiveUnlockRecord> ArchiveUnlocks { get; set; } = new();
    [BsonElement("archive_equipment")]
    public List<ArchiveEquipmentRecord> ArchiveEquipment { get; set; } = new();
    [BsonElement("archive_evaluations")]
    public List<ArchiveEvaluationRecord> ArchiveEvaluations { get; set; } = new();
}

public sealed class ArchiveUnlockRecord
{
    public string Kind { get; set; } = string.Empty;
    public int Id { get; set; }
}
public sealed class ArchiveEquipmentRecord
{
    public int Id { get; set; }
    public int Level { get; set; }
    public int Breakthrough { get; set; }
    public int ResonanceCount { get; set; }
}
public sealed class ArchiveEvaluationRecord
{
    public int Id { get; set; }
    public int Type { get; set; }
    public int Score { get; set; }
    public int Difficulty { get; set; }
    public List<int> Tags { get; set; } = new();
    public int LikeStatus { get; set; }
}
