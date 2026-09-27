using MessagePack;

namespace AscNet.Common.MsgPack;

// Installed 4.8 XGachaManager Lua: GetGachaInfo/Gacha/ChoiceGacha/GetGachaOrganizeInfo/GachaItemExchange
// requests and XRpc.NotifySelfChoiceGachaData.
[MessagePackObject(true)]
public sealed class GetGachaInfoRequest
{
    public int Id { get; set; }
}

[MessagePackObject(true)]
public sealed class GachaGridInfo
{
    public int Id { get; set; }
    public int Times { get; set; }
}

[MessagePackObject(true)]
public sealed class GachaRecord
{
    public RewardGoods RewardGoods { get; set; } = new();
    public long GachaTime { get; set; }
}

[MessagePackObject(true)]
public sealed class GetGachaInfoResponse
{
    public int Code { get; set; }
    public List<GachaGridInfo>? GridInfoList { get; set; }
    public List<GachaRecord>? GachaRecordList { get; set; }
    public int CurExchangeItemCount { get; set; }
    public List<RewardGoods>? GetRewardList { get; set; }
    public int TotalTimes { get; set; }
    public int MissTimes { get; set; }
}

[MessagePackObject(true)]
public sealed class GachaRequest
{
    public int Id { get; set; }
    public int Times { get; set; }
}

[MessagePackObject(true)]
public sealed class GachaResponse
{
    public int Code { get; set; }
    public List<RewardGoods> RewardList { get; set; } = new();
    public List<GachaGridInfo> GridInfoList { get; set; } = new();
    public List<GachaRecord> GachaRecordList { get; set; } = new();
    public GachaCourseResultInfo GachaCourseResult { get; set; } = new();
    public int MissTimes { get; set; }

    [MessagePackObject(true)]
    public sealed class GachaCourseResultInfo
    {
        public int TotalTimes { get; set; }
        public List<RewardGoods> RewardList { get; set; } = new();
    }
}

[MessagePackObject(true)]
public sealed class ChoiceGachaRequest
{
    public int Id { get; set; }
}

[MessagePackObject(true)]
public sealed class ChoiceGachaResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class GetGachaOrganizeInfoRequest
{
    public int OrganizeId { get; set; }
}

[MessagePackObject(true)]
public sealed class GetGachaOrganizeInfoResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class GachaItemExchangeRequest
{
    public int Id { get; set; }
    public int ExchangeNum { get; set; }
    /// <summary>Zero-based index into GachaItemExchange.UseItemIds.</summary>
    public int SelectIndex { get; set; }
}

[MessagePackObject(true)]
public sealed class GachaItemExchangeResponse
{
    public int Code { get; set; }
    public int CurExchangeItemCount { get; set; }
    public int GainItemId { get; set; }
    public int GainItemCount { get; set; }
}


[MessagePackObject(true)]
public sealed class NotifySelfChoiceGachaData
{
    public int ActivityId { get; set; }
    public List<SelfChoiceGachaGroup> ChoiceGroupList { get; set; } = [];

    [MessagePackObject(true)]
    public sealed class SelfChoiceGachaGroup
    {
        public int GroupId { get; set; }
        public int SelectedGachaId { get; set; }
    }
}
