using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database
{
    // SkyGarden minigames (cafe, drone, dorm); mutated only by SkyGarden{Cafe,Drone,Dorm}Module.
    public partial class BigWorldPlayerState
    {
        // Null until first use; initialised from SGCafe tables.
        [BsonElement("sg_cafe")]
        public XBigWorldCafeDb? SgCafe { get; set; }

        [BsonElement("sg_drone_stages")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, SgDroneStageInfo> SgDroneStages { get; set; } = new();

        [BsonElement("sg_drone_current")]
        public SgDroneCurrentStage? SgDroneCurrent { get; set; }

        // Null until first use; initialised from SgDorm tables.
        [BsonElement("sg_dorm")]
        public NotifySgDormData? SgDorm { get; set; }
    }

    public class SgDroneCurrentStage
    {
        [BsonElement("stage_id")]
        public int StageId { get; set; }

        [BsonElement("is_hard_mode")]
        public bool IsHardMode { get; set; }

        [BsonElement("seed")]
        public int Seed { get; set; }

        // MessagePack bytes of the client's StageSuspendSaveData, stored opaque.
        [BsonElement("save_data")]
        public byte[]? SaveData { get; set; }
    }
}
