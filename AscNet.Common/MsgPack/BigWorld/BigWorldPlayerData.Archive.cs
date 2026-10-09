using MessagePack;

namespace AscNet.Common.MsgPack
{
    public partial class BigWorldPlayerData
    {
        public BigWorldPhotographData BigWorldPhotographData = new();
        public Dictionary<int, BigWorldMessageData> BigWorldMessageDict = new();
        public List<BigWorldHelpCourseData> BigWorldHelpCourseList = new();
        public List<int> NewsPopupData = new();
    }

    [MessagePackObject(true)]
    public class BigWorldPhotographData
    {
        public List<int> UnlockedCameraFilters = new();
        public List<int> UnlockedCharacterActions = new();
    }

    [MessagePackObject(true)]
    public class BigWorldMessageData
    {
        public int MessageId;
        public int State;
        public bool IsGetReward;
        public long CreateTime;
        public List<BigWorldMessageStepRecord> StepRecordList = new();
    }

    [MessagePackObject(true)]
    public class BigWorldMessageStepRecord
    {
        public int LatestStepId;
    }

    [MessagePackObject(true)]
    public class BigWorldHelpCourseData
    {
        public int Id;
        public bool IsRead;
        public long CreateTime;
    }

    [MessagePackObject(true)]
    public class BigWorldPhotoData
    {
        public int Id;
        public int CheckSalt;
        public bool IsTaskHidePhoto;
        public int MessageRefId;
        public string? Remark;
        public long CreateTime;
    }

    [MessagePackObject(true)]
    public class BigWorldAlbumData
    {
        public int PhotoIdSequence;
        public List<BigWorldPhotoData> PhotoDatas = new();
        public Dictionary<int, BigWorldPhotoData> TaskHidePhotoData = new();
    }

    [MessagePackObject(true)] public class NotifyBigWorldAlbumUpdate { public BigWorldAlbumData AlbumData = new(); }
    [MessagePackObject(true)] public class NotifyBigWorldPhotographDataUpdate { public BigWorldPhotographData BigWorldPhotographData = new(); }

    [MessagePackObject(true)] public class BigWorldAlbumDataResponse { public int Code; public List<BigWorldPhotoData> PhotoDatas = new(); public Dictionary<int, BigWorldPhotoData> TaskPhotoData = new(); }
    [MessagePackObject(true)] public class BigWorldAlbumAddPhotoRequest { public bool IsTaskHidePhoto; public int TaskHideUniqueId; }
    [MessagePackObject(true)] public class BigWorldAlbumAddPhotoResponse { public int Code; public BigWorldPhotoData? PhotoData; }
    [MessagePackObject(true)] public class BigWorldAlbumDeletePhotoRequest { public List<int>? PhotoIds; }
    [MessagePackObject(true)] public class BigWorldAlbumDeletePhotoResponse { public int Code; }
    [MessagePackObject(true)] public class BigWorldAlbumUpdatePhotoRequest { public int PhotoId; public string? Remark; }
    [MessagePackObject(true)] public class BigWorldAlbumUpdatePhotoResponse { public int Code; }

    // Course (xdata/XBWCourse*Data.lua).
    [MessagePackObject(true)]
    public class BigWorldCourseVersionData
    {
        public int VersionId;
        public BigWorldCourseTaskCntData TaskCntData = new();
        public BigWorldCourseExploreCntData ExploreCntData = new();
        public BigWorldCourseCoreCntData CoreCntData = new();
    }

    [MessagePackObject(true)] public class BigWorldCourseTaskCntData { public int ContentId; public int TotalProgress; public List<int> GotRewardIds = new(); }
    [MessagePackObject(true)] public class BigWorldCourseExploreCntData { public int ContentId; public bool IsGotCompleteReward; public Dictionary<int, BigWorldCourseExploreData> ExploreDatas = new(); }
    [MessagePackObject(true)] public class BigWorldCourseExploreData { public int ExploreId; public bool IsGotReward; public Dictionary<int, int> PoiCounts = new(); }
    [MessagePackObject(true)] public class BigWorldCourseCoreCntData { public int ContentId; public List<int> ReadElementIds = new(); }
    [MessagePackObject(true)] public class BigWorldCourseData { public Dictionary<int, BigWorldCourseVersionData> Datas = new(); }

    [MessagePackObject(true)] public class NotifyBigWorldCourseData { public BigWorldCourseData Data = new(); }
    [MessagePackObject(true)] public class NotifyBigWorldCourseExploreProgress { public int VersionId; public int ExploreId; public int PoiId; public int Count; }
    [MessagePackObject(true)] public class NotifyBigWorldCourseTaskCntProgress { public int VersionId; public int TotalProgress; }

    [MessagePackObject(true)] public class BigWorldCourseCoreSetReadRequest { public int VersionId; public List<int>? ElementIds; }
    [MessagePackObject(true)] public class BigWorldCourseCoreSetReadResponse { public int Code; public List<int> SuccessIds = new(); }
    [MessagePackObject(true)] public class BigWorldCourseTaskCntGetRewardRequest { public int VersionId; }
    [MessagePackObject(true)] public class BigWorldCourseTaskCntGetRewardResponse { public int Code; public List<int> GotRewardIds = new(); public List<RewardGoods> RewardGoodsList = new(); }
    [MessagePackObject(true)] public class BigWorldCourseExploreCntGetRewardRequest { public int ExploreId; }
    [MessagePackObject(true)] public class BigWorldCourseExploreCntGetRewardResponse { public int Code; public List<RewardGoods> RewardGoodsList = new(); }
    [MessagePackObject(true)] public class BigWorldCourseExploreCntGetCompleteRewardRequest { public int VersionId; }
    [MessagePackObject(true)] public class BigWorldCourseExploreCntGetCompleteRewardResponse { public int Code; public List<RewardGoods> RewardGoodsList = new(); }

    // Message.
    [MessagePackObject(true)] public class NotifyBigWorldNotReadMessage { public int MessageId; public int StepId; public int State; }
    [MessagePackObject(true)] public class NotifyBigWorldMessageRecordUpdate { public Dictionary<int, BigWorldMessageData> BigWorldMessageDict = new(); }
    [MessagePackObject(true)] public class BigWorldMessageReadRecordRequest { public int MessageId; public int StepId; }
    [MessagePackObject(true)] public class BigWorldMessageReadRecordResponse { public int Code; public List<RewardGoods> RewardGoodsList = new(); }

    // News.
    [MessagePackObject(true)] public class BigWorldNewsMarkPopupRequest { public List<int>? NewsIds; }
    [MessagePackObject(true)] public class BigWorldNewsMarkPopupResponse { public int Code; public List<int> MarkedNewsIds = new(); }

    // Help course (teach).
    [MessagePackObject(true)] public class NotifyBigWorldHelpCourseUnlock { public BigWorldHelpCourseData Data = new(); }
    [MessagePackObject(true)] public class BigWorldHelpCourseUnlockRequest { public int CourseId; public bool IsRead; }
    [MessagePackObject(true)] public class BigWorldHelpCourseUnlockResponse { public int Code; }
    [MessagePackObject(true)] public class BigWorldHelpCourseReadRequest { public int CourseId; }
    [MessagePackObject(true)] public class BigWorldHelpCourseReadResponse { public int Code; }
}
