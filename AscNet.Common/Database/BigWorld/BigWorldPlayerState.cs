using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database
{
    public partial class BigWorldPlayerState
    {
        [BsonElement("last_world_id")]
        public int LastWorldId { get; set; }

        [BsonElement("last_level_id")]
        public int LastLevelId { get; set; }

        [BsonElement("last_position")]
        public BigWorldVector3? LastPosition { get; set; }

        [BsonElement("last_rotation")]
        public BigWorldVector4? LastRotation { get; set; }

        [BsonElement("last_rotation_y")]
        public double? LastRotationY { get; set; }

        // Collected scene objects (reward-once ledger for CollectableComponent rewards).
        [BsonElement("claimed_scene_objects")]
        public List<BigWorldClaimedSceneObject> ClaimedSceneObjects { get; set; } = new();
    }

    public class BigWorldVector3
    {
        [BsonElement("x")]
        public double X { get; set; }

        [BsonElement("y")]
        public double Y { get; set; }

        [BsonElement("z")]
        public double Z { get; set; }
    }

    public class BigWorldVector4
    {
        [BsonElement("x")]
        public double X { get; set; }

        [BsonElement("y")]
        public double Y { get; set; }

        [BsonElement("z")]
        public double Z { get; set; }

        [BsonElement("w")]
        public double W { get; set; }
    }

    public class BigWorldClaimedSceneObject
    {
        [BsonElement("level_id")]
        public int LevelId { get; set; }

        [BsonElement("place_id")]
        public int PlaceId { get; set; }

        [BsonElement("uuid")]
        public int Uuid { get; set; }

        [BsonElement("claimed_at")]
        public long ClaimedAt { get; set; }
    }
}
