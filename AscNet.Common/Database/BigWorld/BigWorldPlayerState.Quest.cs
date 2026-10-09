using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database
{
    // Quest slice (BigWorldQuestModule): trace, invite rewards, environment groups, quest item bag.
    public partial class BigWorldPlayerState
    {
        [BsonElement("trace_quest_id")]
        public int TraceQuestId { get; set; }

        [BsonElement("last_trace_quest_id")]
        public int LastTraceQuestId { get; set; }

        [BsonElement("trace_ready_quest_id")]
        public int TraceReadyQuestId { get; set; }

        // Invite questId -> result count at claim time (PlayerData.InviteQuestInfo.ReceivedRewardInviteQuestIds).
        [BsonElement("received_reward_invite_quest_ids")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> ReceivedRewardInviteQuestIds { get; set; } = new();

        // levelId -> chosen DlcEnvironmentQuestGroup id (levels absent use IsDefaultGroup).
        [BsonElement("environment_quest_groups")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> EnvironmentQuestGroups { get; set; } = new();

        // Quests whose DlcQuest.RewardId was granted (once each, survives invite replays).
        [BsonElement("rewarded_quest_ids")]
        public List<int> RewardedQuestIds { get; set; } = new();

        // DlcInviteQuestResult ids whose RewardId was granted.
        [BsonElement("rewarded_invite_result_ids")]
        public List<int> RewardedInviteResultIds { get; set; } = new();

        // DlcQuestItem id -> count.
        [BsonElement("dlc_quest_bag")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> DlcQuestBag { get; set; } = new();
    }
}
