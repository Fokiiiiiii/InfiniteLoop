using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

/// <summary>One running/complete legacy auto-fight (StartAutoFightRequest). Removed when its rewards are obtained.</summary>
[BsonIgnoreExtraElements]
public sealed class AutoFightRecordState
{
    [BsonElement("stage_id")] public int StageId { get; set; }
    [BsonElement("card_ids")] public List<long> CardIds { get; set; } = new();
    [BsonElement("times")] public int Times { get; set; }
    [BsonElement("complete_time")] public long CompleteTime { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class MovieOptionsState
{
    [BsonElement("movie_id")] public string MovieId { get; set; } = "";
    [BsonElement("option_ids")] public List<int> OptionIds { get; set; } = new();
}

public partial class Player
{
    [BsonElement("auto_fight_records")] public List<AutoFightRecordState> AutoFightRecords { get; set; } = new();
    [BsonElement("movie_options")] public List<MovieOptionsState> MovieOptions { get; set; } = new();
}
