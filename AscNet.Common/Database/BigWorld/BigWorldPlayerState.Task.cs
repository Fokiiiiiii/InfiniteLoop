using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database
{
    public partial class BigWorldPlayerState
    {
        // BigWorldTask id -> TaskModule.CurrentDailyResetPeriod at claim time (Type 6 daily tasks re-open next period).
        [BsonElement("claimed_task_periods")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, long> ClaimedTaskPeriods { get; set; } = new();
    }
}
