using MessagePack;

namespace AscNet.Common.MsgPack
{
    // Core PlayerData fields (consumers: XBigWorldAgency.lua:264-285).
    public partial class BigWorldPlayerData
    {
        public Theatre5DlcVector3? LastPosition { get; set; }
        public Theatre5DlcVector3? LastRotation { get; set; }
        public int LastLevelId { get; set; }
        public int LastWorldId { get; set; }
        public int CurNpcPos { get; set; }
        public Dictionary<int, List<int>> TeleporterData { get; set; } = new();
        public Dictionary<int, BigWorldLevelPlayDataDto> LevelPlayDatas { get; set; } = new();
        public List<int> EnteredLevelIds { get; set; } = new();
        public List<int> BigWorldGuideData { get; set; } = new();
        public List<int> CustomParamMarkData { get; set; } = new();
        public BigWorldFovData FovData { get; set; } = new();
        public BigWorldMapTrackPinData? MapTrackPinData { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldLevelPlayDataDto
    {
        public bool IsFullCleared { get; set; }
    }

    // Lua XBigWorldInstanceModel.UpdateLevelPlayData reads PlayId / LevelPlayData (4.8 client Lua; no native class).
    [MessagePackObject(true)]
    public class NotifyBigWorldLevelPlayDataChange
    {
        public int PlayId { get; set; }
        public BigWorldLevelPlayDataDto LevelPlayData { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class BigWorldFovData
    {
        public int FovType { get; set; }
        public Dictionary<int, int> LevelFovDatas { get; set; } = new();
    }
}
