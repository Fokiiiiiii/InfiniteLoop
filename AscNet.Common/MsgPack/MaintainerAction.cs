using MessagePack;

namespace AscNet.Common.MsgPack;

// Operation Guardians (client MaintainerAction). Field names come from XMaintainerActionManager.lua and the
// XMaintainerAction* entities; NotifyMaintainerActionData lives in Types.cs.

[MessagePackObject(true)]
public sealed class MaintainerActionNodeEventRequest
{
}

[MessagePackObject(true)]
public sealed class MaintainerActionNodeEventResponse
{
    public int Code { get; set; }
    // Always present for node-bound outcomes: the client's UnKnow error path dereferences it.
    public NotifyMaintainerActionData.NotifyMaintainerActionDataNode? Node { get; set; }
}

[MessagePackObject(true)]
public sealed class MaintainerActionPlayCardRequest
{
    public int Card { get; set; }
}

[MessagePackObject(true)]
public sealed class MaintainerActionPlayCardResponse
{
    public int Code { get; set; }
    public int NodeId { get; set; }
    public int NewCard { get; set; }
}

[MessagePackObject(true)]
public sealed class MaintainerActionRecordRequest
{
}

[MessagePackObject(true)]
public sealed class MaintainerActionRecord
{
    public long Time { get; set; }
    public int EventId { get; set; }
    // Numeric: XUiGridRecord indexes the stage table with EventValues[1] and formats the rest with %d.
    public List<int> EventValues { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class MaintainerActionRecordResponse
{
    public int Code { get; set; }
    public List<MaintainerActionRecord> Records { get; set; } = new();
}

// XRpc.NotifyMaintainerActionDailyReset merges every key into the client's game data entity.
[MessagePackObject(true)]
public sealed class NotifyMaintainerActionDailyReset
{
    public int UsedActionCount { get; set; }
    public int ExtraActionCount { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyMaintainerActionFightWin
{
    public int FightWinCount { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyMaintainerActionNodeChange
{
    public NotifyMaintainerActionData.NotifyMaintainerActionDataNode Node { get; set; } = new();
}
