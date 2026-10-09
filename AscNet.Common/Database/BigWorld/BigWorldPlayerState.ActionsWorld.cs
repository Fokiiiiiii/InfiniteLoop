using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database
{
    // ActionRuntime WorldState slice: level-action effects with no client-native save slot (quest DynamicData carries the
    // native ones: FuncEntryDisableDict, SystemFuncControlSaveData, LevelActorSaves).
    public partial class BigWorldPlayerState
    {
        // ClearPlace records; RestorePlace removes them. The enter snapshot reflects them through actor overrides.
        [BsonElement("executed_clear_places")]
        public List<BigWorldClearPlaceRecord> ExecutedClearPlaces { get; set; } = new();

        // SetActorAIEnabled overrides (absent = enabled).
        [BsonElement("actor_ai_overrides")]
        public List<BigWorldActorAiOverride> ActorAiOverrides { get; set; } = new();

        // NpcTetherFollowMove / NpcToggleFollowPauseState (XNpcSaveData has no tether slot in the known client layout).
        [BsonElement("npc_follow_extras")]
        public List<BigWorldNpcFollowExtra> NpcFollowExtras { get; set; } = new();

        // TakePhotoSilent requests; ShotId = index + 1.
        [BsonElement("silent_photo_requests")]
        public List<BigWorldSilentPhotoRequest> SilentPhotoRequests { get; set; } = new();
    }

    public class BigWorldClearPlaceRecord
    {
        [BsonElement("level_id")]
        public int LevelId { get; set; }

        [BsonElement("quest_id")]
        public int QuestId { get; set; }

        [BsonElement("clear_id")]
        public string ClearId { get; set; } = "";

        // Actors the clear unloaded with their BigWorldActorOverride.Loaded before it (null = table default).
        [BsonElement("actors")]
        public List<BigWorldClearedActor> Actors { get; set; } = new();
    }

    public class BigWorldClearedActor
    {
        [BsonElement("actor_type")]
        public int ActorType { get; set; }

        [BsonElement("place_id")]
        public int PlaceId { get; set; }

        [BsonElement("prior_loaded")]
        public bool? PriorLoaded { get; set; }
    }

    public class BigWorldActorAiOverride
    {
        [BsonElement("level_id")]
        public int LevelId { get; set; }

        [BsonElement("actor_type")]
        public int ActorType { get; set; }

        [BsonElement("place_id")]
        public int PlaceId { get; set; }

        [BsonElement("enabled")]
        public bool Enabled { get; set; }
    }

    // Older saves still carry the retired "tether_json" element.
    [BsonIgnoreExtraElements]
    public class BigWorldNpcFollowExtra
    {
        [BsonElement("level_id")]
        public int LevelId { get; set; }

        [BsonElement("quest_id")]
        public int QuestId { get; set; }

        [BsonElement("place_id")]
        public int PlaceId { get; set; }

        // Tether follow mode (14008) of this level NPC, kept per player like RelativeFollow; null when no tether is active.
        [BsonElement("tether_follow")]
        public AscNet.Common.MsgPack.Theatre5NpcTetherFollowModeSaveData? Tether { get; set; }

        [BsonElement("paused")]
        public bool Paused { get; set; }

        // Relative follow mode (14001) of this level NPC, kept per player: the quest's LevelActorSaves copy is discarded when
        // the quest finishes, but the NPC keeps following (1001 starts Lucia 900014 following and never stops her).
        [BsonElement("relative_follow")]
        public AscNet.Common.MsgPack.Theatre5NpcRelativeFollowModeSaveData? RelativeFollow { get; set; }
    }

    public class BigWorldSilentPhotoRequest
    {
        [BsonElement("level_id")]
        public int LevelId { get; set; }

        [BsonElement("quest_id")]
        public int QuestId { get; set; }

        [BsonElement("photo_key")]
        public int PhotoKey { get; set; }

        [BsonElement("spot_id")]
        public int SpotId { get; set; }

        [BsonElement("fov")]
        public float Fov { get; set; }
    }
}
