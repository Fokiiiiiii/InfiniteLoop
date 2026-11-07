using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("maintainer_action")]
    public MaintainerActionState MaintainerAction { get; set; } = new();
}

/// <summary>
/// Per-player Operation Guardians board. AscNet policy: the node pool for NodePoolId has no authored table,
/// so the board, card hands and node contents are generated from <see cref="Seed"/> + <see cref="Draws"/>
/// (a counter-based RNG) and persisted verbatim; nothing here is a retail capture.
/// </summary>
public sealed class MaintainerActionState
{
    /// <summary>MaintainerActionConfig.Id the board was built for; 0 = never initialised.</summary>
    [BsonElement("config_id")] public int ConfigId { get; set; }
    [BsonElement("week")] public long Week { get; set; }
    [BsonElement("day")] public long Day { get; set; }
    [BsonElement("seed")] public long Seed { get; set; }
    [BsonElement("draws")] public int Draws { get; set; }
    [BsonElement("player_node_id")] public int PlayerNodeId { get; set; }
    [BsonElement("reverse")] public bool Reverse { get; set; }
    /// <summary>The event of the node the player currently stands on has been resolved.</summary>
    [BsonElement("triggered")] public bool Triggered { get; set; } = true;
    /// <summary>Incremented on every arrival (card move or relocation); scopes event receipts and reward claims.</summary>
    [BsonElement("landing")] public int Landing { get; set; }
    [BsonElement("cards")] public List<int> Cards { get; set; } = [];
    [BsonElement("fight_win_count")] public int FightWinCount { get; set; }
    [BsonElement("box_count")] public int BoxCount { get; set; }
    [BsonElement("used_action_count")] public int UsedActionCount { get; set; }
    [BsonElement("extra_action_count")] public int ExtraActionCount { get; set; }
    [BsonElement("has_warehouse_node")] public bool HasWarehouseNode { get; set; }
    [BsonElement("warehouse_finish_count")] public int WarehouseFinishCount { get; set; }
    [BsonElement("start_bonus_claimed")] public bool StartBonusClaimed { get; set; }
    [BsonElement("nodes")] public List<MaintainerActionNodeState> Nodes { get; set; } = [];
    [BsonElement("records")] public List<MaintainerActionRecordState> Records { get; set; } = [];
    [BsonElement("last_event")] public MaintainerActionEventReceipt? LastEvent { get; set; }
}

public sealed class MaintainerActionNodeState
{
    [BsonElement("node_id")] public int NodeId { get; set; }
    /// <summary>Real node type; the wire shows 0 (UnKnow) until <see cref="Revealed"/>.</summary>
    [BsonElement("node_type")] public int NodeType { get; set; }
    [BsonElement("event_id")] public int EventId { get; set; }
    [BsonElement("value")] public string? Value { get; set; }
    [BsonElement("revealed")] public bool Revealed { get; set; }
}

public sealed class MaintainerActionRecordState
{
    [BsonElement("time")] public long Time { get; set; }
    [BsonElement("event_id")] public int EventId { get; set; }
    [BsonElement("values")] public List<int> Values { get; set; } = [];
}

/// <summary>The accepted NodeEvent response of the current landing, replayed verbatim on retry.</summary>
public sealed class MaintainerActionEventReceipt
{
    [BsonElement("landing")] public int Landing { get; set; }
    [BsonElement("node_id")] public int NodeId { get; set; }
    [BsonElement("node_type")] public int NodeType { get; set; }
    [BsonElement("event_id")] public int EventId { get; set; }
    [BsonElement("value")] public string? Value { get; set; }
}
