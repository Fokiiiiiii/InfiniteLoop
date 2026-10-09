using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database
{
    // Album, photograph unlocks, course, messages, news popups and help courses (BigWorldArchiveModule).
    // Legacy saves carry the retired flat course fields (big_world_course_read_element_ids / _task_progress).
    [BsonIgnoreExtraElements]
    public partial class BigWorldPlayerState
    {
        [BsonElement("album_photo_id_sequence")]
        public int AlbumPhotoIdSequence { get; set; }

        [BsonElement("album_photos")]
        public List<BigWorldAlbumPhoto> AlbumPhotos { get; set; } = new();

        [BsonElement("unlocked_camera_filters")]
        public List<int> UnlockedCameraFilters { get; set; } = new();

        [BsonElement("unlocked_character_actions")]
        public List<int> UnlockedCharacterActions { get; set; } = new();

        // versionId -> read BigWorldCourseCoreElement ids.
        [BsonElement("course_read_element_ids")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, List<int>> CourseReadElementIds { get; set; } = new();

        // Claimed BigWorldCourseTaskProgressReward ids (globally unique).
        [BsonElement("course_task_reward_ids")]
        public List<int> CourseTaskRewardIds { get; set; } = new();

        // BigWorldCourseExplorePoi id -> absolute progress (poi ids are globally unique).
        [BsonElement("course_poi_counts")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> CoursePoiCounts { get; set; } = new();

        [BsonElement("course_explore_reward_ids")]
        public List<int> CourseExploreRewardIds { get; set; } = new();

        // BigWorldCourseVersion ids whose explore complete reward was claimed.
        [BsonElement("course_explore_complete_version_ids")]
        public List<int> CourseExploreCompleteVersionIds { get; set; } = new();

        [BsonElement("messages")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, BigWorldMessageRecord> Messages { get; set; } = new();

        [BsonElement("news_popup_ids")]
        public List<int> NewsPopupIds { get; set; } = new();

        [BsonElement("help_courses")]
        public List<BigWorldHelpCourseRecord> HelpCourses { get; set; } = new();
    }

    public class BigWorldAlbumPhoto
    {
        [BsonElement("id")]
        public int Id { get; set; }

        [BsonElement("check_salt")]
        public int CheckSalt { get; set; }

        [BsonElement("is_task_hide_photo")]
        public bool IsTaskHidePhoto { get; set; }

        [BsonElement("task_hide_unique_id")]
        public int TaskHideUniqueId { get; set; }

        [BsonElement("message_ref_id")]
        public int MessageRefId { get; set; }

        [BsonElement("remark")]
        public string? Remark { get; set; }

        [BsonElement("create_time")]
        public long CreateTime { get; set; }
    }

    public class BigWorldMessageRecord
    {
        // 0 NotFinish, 1 Finish, 2 NotRead (client XEnumConst.BWMessage.MessageState).
        [BsonElement("state")]
        public int State { get; set; }

        [BsonElement("is_get_reward")]
        public bool IsGetReward { get; set; }

        [BsonElement("create_time")]
        public long CreateTime { get; set; }

        // Each read step id, in order; the client rebuilds the full path from FirstStepId.
        [BsonElement("step_ids")]
        public List<int> StepIds { get; set; } = new();
    }

    public class BigWorldHelpCourseRecord
    {
        [BsonElement("id")]
        public int Id { get; set; }

        [BsonElement("is_read")]
        public bool IsRead { get; set; }

        [BsonElement("create_time")]
        public long CreateTime { get; set; }
    }
}
