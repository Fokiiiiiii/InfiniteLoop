using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database
{
    // State read by BigWorldConditionService; mutated by the Core slice.
    public partial class BigWorldPlayerState
    {
        // Native DlcQuestInfo (FinishedQuests, ActiveQuests[QuestState/Steps/FinishedObjectiveIds], ReadyQuestIds).
        [BsonElement("quest_data")]
        public Theatre5DlcQuestInfo QuestData { get; set; } = new();

        [BsonElement("entered_world_ids")]
        public List<int> EnteredWorldIds { get; set; } = new();

        // Client XBigWorldMapModel.UpdateUnlockLevelMap source (condition 10101009).
        [BsonElement("entered_level_ids")]
        public List<int> EnteredLevelIds { get; set; } = new();

        // levelId -> activated teleporter placeIds (PlayerData.TeleporterData).
        [BsonElement("teleporter_data")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, List<int>> TeleporterData { get; set; } = new();

        // levelPlayId -> data (PlayerData.LevelPlayDatas).
        [BsonElement("level_play_datas")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, BigWorldLevelPlayData> LevelPlayDatas { get; set; } = new();

        // PlayerData.InviteQuestInfo.UnlockedInviteQuestResultIds (condition 10101007).
        [BsonElement("unlocked_invite_quest_result_ids")]
        public List<int> UnlockedInviteQuestResultIds { get; set; } = new();

        // PlayerData.CustomParamMarkData (condition 10101008).
        [BsonElement("custom_param_mark_data")]
        public List<int> CustomParamMarkData { get; set; } = new();

        // levelId -> placeId -> XSceneObjectSaveData as reported by the engine (DlcSceneObjectStateSet), kept as the
        // raw MessagePack value: the installed client carries fields newer than dump.cs (PlaceId, Position, Rotation,
        // BeScanned, VarCompData), so a typed copy would drop them. Served back through DlcWorldSceneObjectData and
        // DlcWorldSaveData ActorSaveData.SoSaveDatas.
        [BsonElement("scene_object_states")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, BigWorldLevelSceneObjects> SceneObjectStates { get; set; } = new();

        [BsonElement("cur_npc_pos")]
        public int CurNpcPos { get; set; }

        // PlayerData.BigWorldGuideData (finished BigWorldOpenGuide ids).
        [BsonElement("guide_data")]
        public List<int> GuideData { get; set; } = new();

        // PlayerData.FovData: FovType (default perspective, 0 = unset) + FovGroupId -> FovType.
        [BsonElement("fov_type")]
        public int FovType { get; set; }

        [BsonElement("level_fov_datas")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> LevelFovDatas { get; set; } = new();

        // PlayerData.MapTrackPinData; null when nothing is tracked.
        [BsonElement("map_track_pin")]
        public BigWorldMapTrackPin? MapTrackPin { get; set; }

        // Current instance level (EnterInstLevel); 0 when in the open world (LastWorldId/LastLevelId).
        [BsonElement("inst_level_id")]
        public int InstLevelId { get; set; }

        // Last known player pose inside InstLevelId (spawn pose until the first position report); resumed on relog.
        [BsonElement("inst_position")]
        public BigWorldVector3? InstPosition { get; set; }

        [BsonElement("inst_rotation_y")]
        public double? InstRotationY { get; set; }
    }

    public class BigWorldLevelSceneObjects
    {
        [BsonElement("states")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, byte[]> States { get; set; } = new();
    }

    public class BigWorldMapTrackPin
    {
        [BsonElement("world_id")]
        public int WorldId { get; set; }

        [BsonElement("level_id")]
        public int LevelId { get; set; }

        [BsonElement("track_pin_id")]
        public int TrackPinId { get; set; }
    }

    public class BigWorldLevelPlayData
    {
        [BsonElement("is_full_cleared")]
        public bool IsFullCleared { get; set; }
    }
}
