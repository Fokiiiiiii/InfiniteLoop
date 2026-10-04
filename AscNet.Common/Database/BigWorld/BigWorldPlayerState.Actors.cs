using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database
{
    // ActionRuntime slice: level-action effects that outlive the session (LoadNpc/UnloadNpc/LoadSceneObject/
    // UnloadSceneObject/EnableActorInteractableComponent) and the ActionListId counter.
    public partial class BigWorldPlayerState
    {
        // One row per (level, actor) touched by a level action; the enter snapshot applies these on top of the table defaults.
        [BsonElement("actor_overrides")]
        public List<BigWorldActorOverride> ActorOverrides { get; set; } = new();
    }

    public class BigWorldActorOverride
    {
        [BsonElement("level_id")]
        public int LevelId { get; set; }

        // EActorType: 1 Npc, 2 SceneObject.
        [BsonElement("actor_type")]
        public int ActorType { get; set; }

        [BsonElement("place_id")]
        public int PlaceId { get; set; }

        // null = table default; true = a level action loaded it; false = a level action unloaded it.
        [BsonElement("loaded")]
        public bool? Loaded { get; set; }

        // null = table default; otherwise the last EnableActorInteractableComponent value.
        [BsonElement("interactable")]
        public bool? Interactable { get; set; }

        // null = not scanned; otherwise the last RpcChangeActorScannedStateNotify BeScanned value (XBeScannedComponent._curBeScanned).
        [BsonElement("be_scanned")]
        public bool? BeScanned { get; set; }

        // Last pose the controlling client reported for this NPC (XRpcNpcPositionAndRotation); null = config placement.
        [BsonElement("position")]
        public BigWorldVector3? Position { get; set; }

        [BsonElement("rotation")]
        public BigWorldVector4? Rotation { get; set; }
    }
}
