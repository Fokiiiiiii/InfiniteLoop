using MessagePack;

namespace AscNet.Common.MsgPack
{
    // BigWorld (Babylonia) core protocol, field names from EN 4.7 Lua callers and dump.cs D:352400-352790.
    // Nested native engine types reuse the Theatre5.cs transcriptions (XWorldData, XDlcVector3 ...).
#pragma warning disable CS8618

    [MessagePackObject(true)]
    public class BigWorldEnterWorldRequest
    {
        public int WorldId;
        public int LevelId;
    }

    [MessagePackObject(true)]
    public class BigWorldEnterResultData
    {
        public Theatre5WorldData WorldData;
        public byte[]? FightData;
        public byte[]? LevelData;
    }

    [MessagePackObject(true)]
    public class BigWorldEnterWorldResponse
    {
        public int Code;
        public BigWorldEnterResultData? EnterResultData;
        public BigWorldPlayerData? PlayerData;
        public Dictionary<int, DlcQuestItem>? DlcQuestBag;
    }

    [MessagePackObject(true)]
    public class BigWorldGetEnterWorldDataRequest
    {
    }

    [MessagePackObject(true)]
    public class BigWorldGetEnterWorldDataResponse
    {
        public int Code;
        public BigWorldEnterResultData? EnterResultData;
        public BigWorldPlayerData? PlayerData;
        public Dictionary<int, DlcQuestItem>? DlcQuestBag;
    }

    [MessagePackObject(true)]
    public class BigWorldOnModuleLoadCompleteRequest
    {
    }

    [MessagePackObject(true)]
    public class BigWorldOnModuleLoadCompleteResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class DlcWorldSaveDataRequest
    {
        public int WorldId;
    }

    [MessagePackObject(true)]
    public class DlcWorldSaveDataResponse
    {
        public int Code;
        public BigWorldWorldSaveData? WorldSaveData;
    }

    // XWorldSaveData / XLevelSaveData as the installed client sends them (retail also carries
    // LastEnterLevelTimestamp and LevelInternalSaveData, absent from dump.cs D:70612).
    [MessagePackObject(true)]
    public class BigWorldWorldSaveData
    {
        public Dictionary<int, BigWorldLevelSaveData> LevelDataDict = new();
    }

    [MessagePackObject(true)]
    public class BigWorldLevelSaveData
    {
        public int WorldId;
        public int LevelId;
        public long OfflineTimeout;
        public Theatre5DlcVector3? ReliablePos;
        public float ReliableRotationY;
        public long LastEnterLevelTimestamp;
        public object? LevelInternalSaveData;
        public BigWorldLevelActorSaveData ActorSaveData = new();
    }

    [MessagePackObject(true)]
    public class BigWorldLevelActorSaveData
    {
        // Raw XSceneObjectSaveData values persisted from DlcSceneObjectStateSet.
        public Dictionary<int, object> SoSaveDatas = new();
        public Dictionary<int, object> NpcSaveDatas = new();
    }

    [MessagePackObject(true)]
    public class DlcWorldEnterSucceedRequest
    {
        public int WorldId;
        public int LevelId;
        public Theatre5DlcVector3? LastPosition;
        public Theatre5DlcVector3? LastEulerAngles;
    }

    [MessagePackObject(true)]
    public class DlcWorldSceneObjectDataRequest
    {
        public int WorldId;
        public int LevelId;
    }

    [MessagePackObject(true)]
    public class DlcWorldSceneObjectDataResponse
    {
        public int Code;
        public Dictionary<int, object> SceneObjectStates = new();
    }

    [MessagePackObject(true)]
    public class DlcSceneObjectStateSetRequest
    {
        public int WorldId;
        public int LevelId;
        public Dictionary<int, object>? SceneObjectStates;
    }

    [MessagePackObject(true)]
    public class DlcSceneObjectStateSetResponse
    {
        public int Code;
        public List<RewardGoods> RewardGoods = new();
    }

    [MessagePackObject(true)]
    public class BigWorldCurNpcPosUpdateRequest
    {
        public int WorldId;
        public int LevelId;
        public int CurNpcPos;
    }

    [MessagePackObject(true)]
    public class BigWorldCurNpcPosUpdateResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class EnterInstLevelRequest
    {
        public int WorldId;
        public int InstLevelId;
        public object? Team;
        public Theatre5DlcVector3? TargetPos;
        public Theatre5DlcVector3? TargetRot;
    }

    [MessagePackObject(true)]
    public class EnterInstLevelResponse
    {
        public int Code;
        public BigWorldEnterResultData? EnterResultData;
    }

    [MessagePackObject(true)]
    public class LeaveInstLevelRequest
    {
        // XBigWorldInstanceAgency.LevelSaveOption: 0 None, 1 SaveExit, 2 NoSaveExit.
        public int InstSaveOption;
    }

    [MessagePackObject(true)]
    public class LeaveInstLevelResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class DlcReChallengeInstRequest
    {
    }

    [MessagePackObject(true)]
    public class DlcReChallengeInstResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldSaveFovDataRequest
    {
        public int FovType;
        public int FovGroupId;
    }

    [MessagePackObject(true)]
    public class BigWorldSaveFovDataResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldGuideOpenRequest
    {
        public int GuideId;
    }

    [MessagePackObject(true)]
    public class BigWorldGuideOpenResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldMarkCustomParamRequest
    {
        public int Id;
        public bool IsUnmark;
    }

    [MessagePackObject(true)]
    public class BigWorldMarkCustomParamResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldCheckIsShowMainRedPointRequest
    {
        public int SysModuleId;
    }

    [MessagePackObject(true)]
    public class BigWorldCheckIsShowMainRedPointResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldMapTrackPinData
    {
        public int WorldId;
        public int LevelId;
        public int TrackPinId;
    }

    [MessagePackObject(true)]
    public class BigWorldSetTrackMapPinIdRequest
    {
        public BigWorldMapTrackPinData? MapTrackPinData;
        // XEnumConst.BWMap.TrackOperator: 1 Begin, 2 Cancel, 3 Finish.
        public int Opt;
    }

    [MessagePackObject(true)]
    public class BigWorldSetTrackMapPinIdResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class NotifyBigWorldMainRedPoint
    {
        public Dictionary<int, bool> RedPoints = new();
    }

    [MessagePackObject(true)]
    public class NotifyExternalRequiredBigWorldPlayerData
    {
        public List<int> EnteredBigWorldIds = new();
        public int Gender;
        public List<int> CommanderFashionBags = new();
    }

    [MessagePackObject(true)]
    public class NotifyNewEnteredBigWorldId
    {
        public int WorldId;
    }

    [MessagePackObject(true)]
    public class NotifyNewEnteredBigWorldLevelId
    {
        public int LevelId;
    }

    [MessagePackObject(true)]
    public class NotifyBigWorldMapData
    {
        public Dictionary<int, int> BoxRewardedCntData = new();
    }

    [MessagePackObject(true)]
    public class NotifyBigWorldBoxData
    {
        public int LevelId;
        public int BoxRewardedCnt;
    }

    [MessagePackObject(true)]
    public class NotifyBigWorldActivateTeleporter
    {
        public int LevelId;
        public int PlaceId;
    }

    [MessagePackObject(true)]
    public class StartFightNotify
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class XRpcCommonResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class XRpcComponentActionResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class XRpcActorActionResponse
    {
        public int Code;
    }

    // XRpcActorReplicateResponse { XCode Code } (dump48 TypeDefIndex 1038).
    [MessagePackObject(true)]
    public class XRpcActorReplicateResponse
    {
        public int Code;
    }

    // dump.cs D:55676.
    [MessagePackObject(true)]
    public class DlcQuestItem
    {
        public int ItemId;
        public int Count;
    }
#pragma warning restore CS8618
}
