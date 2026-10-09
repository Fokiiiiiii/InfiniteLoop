using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database
{
    // TeamInstance action slice: trial team members (AddTrialNpcToTeam/RemoveTrialNpcFromTeam) and instance-level
    // results (FinishInstLevel/SettleInstLevel).
    public partial class BigWorldPlayerState
    {
        // Active trial team (TrialNpc ids, in team order); empty = the player's own team.
        [BsonElement("trial_npc_ids")]
        public List<int> TrialNpcIds { get; set; } = new();

        // ETrialNpcAddMode of the active trial team (1 Cover, 2 Append); 0 when none.
        [BsonElement("trial_add_mode")]
        public int TrialAddMode { get; set; }

        // CurNpcPos of the real team, restored when the trial team is removed.
        [BsonElement("trial_saved_cur_npc_pos")]
        public int TrialSavedCurNpcPos { get; set; }

        // levelId -> completed FinishInstLevel count (history read by InstanceComplete objectives, AppendHistoryCount).
        [BsonElement("inst_level_finished_counts")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> InstLevelFinishedCounts { get; set; } = new();

        // The current instance visit already ran FinishInstLevel (reset when the instance is left).
        [BsonElement("inst_level_finished")]
        public bool InstLevelFinished { get; set; }

        // levelId -> last SettleInstLevel record (XInstLevelSettleData).
        [BsonElement("inst_settle_records")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, BigWorldInstSettleRecord> InstSettleRecords { get; set; } = new();
    }

    public class BigWorldInstSettleRecord
    {
        [BsonElement("settle_type")] public int SettleType { get; set; }
        [BsonElement("theme")] public int Theme { get; set; }
        [BsonElement("quest_id")] public int QuestId { get; set; }
        [BsonElement("objective_ids")] public List<int> ObjectiveIds { get; set; } = new();
        [BsonElement("score")] public int Score { get; set; }
        [BsonElement("best_score")] public int BestScore { get; set; }
        [BsonElement("play_time")] public float PlayTime { get; set; }
        [BsonElement("is_win")] public bool IsWin { get; set; }
        [BsonElement("death_count")] public int DeathCount { get; set; }
        [BsonElement("gold_count")] public int GoldCount { get; set; }
        [BsonElement("star_count")] public int StarCount { get; set; }
        [BsonElement("trigger_judge")] public bool IsTriggerJudge { get; set; }
        [BsonElement("trigger_hide_road")] public bool IsTriggerHideRoad { get; set; }
    }
}
