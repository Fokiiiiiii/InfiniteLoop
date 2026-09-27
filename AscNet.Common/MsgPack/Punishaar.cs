using MessagePack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.MsgPack;

// Installed 4.8 xpunishaar Lua: xpunishaarnetworkagency.lua (requests/notifies), xpunishaarmodel.lua,
// submodules/outside/xpunishaarsubmodeloutside.lua (login/DataDb) and xpunishaarenum.lua (wire enums).
// [IgnoreMember] fields are server-owned run state persisted in BSON only.

[MessagePackObject(true)]
public sealed class PunishaarCatalog
{
    public List<int> Catalogs { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class PunishaarMasterCard
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int Level { get; set; }
    public int AreaType { get; set; }
    public int StartPos { get; set; }
    public int SubCardId { get; set; }
}

[MessagePackObject(true)]
public sealed class PunishaarGoods
{
    public int CardId { get; set; }
    public int Level { get; set; }
    public int SubCardId { get; set; }
    public bool IsBought { get; set; }
    public bool Frozen { get; set; }
}

[MessagePackObject(true)]
public sealed class PunishaarShopInfo
{
    public List<int> CandidateShopIds { get; set; } = [];
    public int SelectedShopId { get; set; }
    public List<PunishaarGoods> Goods { get; set; } = [];
    public int RefreshTimes { get; set; }
}

[MessagePackObject(true)]
public sealed class PunishaarEventInfo
{
    public List<int> RandomEventIds { get; set; } = [];
    public int SelectedEventId { get; set; }
}

[MessagePackObject(true)]
public sealed class PunishaarFightInfo
{
    public List<int> RandomFightIds { get; set; } = [];
    public int SelectedFightId { get; set; }
}

[MessagePackObject(true)]
public sealed class PunishaarNode
{
    public int NodeId { get; set; }
    public int Type { get; set; }
    public int Status { get; set; }
    public PunishaarShopInfo? ShopInfo { get; set; }
    public PunishaarEventInfo? EventInfo { get; set; }
    public PunishaarFightInfo? FightInfo { get; set; }
    public int PendingRewardCardId { get; set; }
    public int PendingRewardCardLevel { get; set; }
    // Set by EnterFight; FinishFight is only accepted for a started fight.
    [IgnoreMember] public bool FightStarted { get; set; }
}

[MessagePackObject(true)]
public sealed class PunishaarStage
{
    public int StageId { get; set; }
    public PunishaarNode CurrentNode { get; set; } = new();
    public List<PunishaarNode> HistoryNodeList { get; set; } = [];
    public int Gold { get; set; }
    public int Durability { get; set; }
    public int FightWinCount { get; set; }
    public int CurrentRound { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, PunishaarMasterCard> TotalMasterCards { get; set; } = [];
    public int FightAreaGridLimit { get; set; }
    public int BagGridLimit { get; set; }
    [IgnoreMember] public int AllGold { get; set; }
    [IgnoreMember] public long RngState { get; set; }
    [IgnoreMember] public int NextCardId { get; set; }
    // Won fight nodes in this run keyed by NodeType; endless rounds accumulate.
    [IgnoreMember, BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> WonNodeCounts { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class PunishaarSettleInfo
{
    public int SettleType { get; set; }
    public int StageId { get; set; }
    public int Durability { get; set; }
    public int AllGold { get; set; }
    public int FightWinCount { get; set; }
    public int CurrentRound { get; set; }
    public bool IsNewRecord { get; set; }
}

[MessagePackObject(true)]
public sealed class PunishaarDataDb
{
    public int ActivityId { get; set; }
    public int CurrentStageId { get; set; }
    public Dictionary<int, PunishaarStage> StageSaves { get; set; } = [];
    public List<int> PassedStageIds { get; set; } = [];
    public Dictionary<int, int> StageChallengeCounts { get; set; } = [];
    public Dictionary<int, PunishaarCatalog> CharacterCardCatalogsDict { get; set; } = [];
    public Dictionary<int, PunishaarCatalog> PartnerCardCatalogsDict { get; set; } = [];
    public List<int> EquipCatalogs { get; set; } = [];
    public List<int> ResonanceCatalogs { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class PunishaarCardPosInfo
{
    public int Id { get; set; }
    public int AreaType { get; set; }
    public int StartPos { get; set; }
}

[MessagePackObject(true)]
public sealed class PunishaarRewardCardDetailInfo
{
    public int AreaType { get; set; }
    public int StartPos { get; set; }
    public int SubCardId { get; set; }
    public int MasterCardId { get; set; }
    public bool IsCardsPosChange { get; set; }
    public List<PunishaarCardPosInfo>? CardPosList { get; set; }
}

[MessagePackObject(true)]
public sealed class PunishaarRewardGoods
{
    public int RewardType { get; set; }
    public int CardId { get; set; }
    public int Level { get; set; }
    public int Amount { get; set; }
}

// Notifies
[MessagePackObject(true)]
public sealed class NotifyPunishaarLoginData
{
    public int ActivityId { get; set; }
    public List<int> SaveStageIds { get; set; } = [];
    public List<int> PassStageIds { get; set; } = [];
    public Dictionary<int, PunishaarCatalog> CharacterCardCatalogsDict { get; set; } = [];
    public Dictionary<int, PunishaarCatalog> PartnerCardCatalogsDict { get; set; } = [];
    public List<int> EquipCatalogs { get; set; } = [];
    public List<int> ResonanceCatalogs { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyPunishaarGoldChange { public int Gold { get; set; } }

[MessagePackObject(true)]
public sealed class NotifyPunishaarMasterCardChange
{
    public PunishaarMasterCard? AddedCard { get; set; }
    public List<int> RemovedCardIds { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyPunishaarSubCardChange
{
    public int MasterCardId { get; set; }
    public int SubCardId { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyPunishaarRewardResult
{
    public int StageId { get; set; }
    public List<PunishaarRewardGoods> RewardGoodsList { get; set; } = [];
}

// Requests
[MessagePackObject(true)] public sealed class XPunishaarGetDataRequest { }
[MessagePackObject(true)] public sealed class XPunishaarStartStageRequest { public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarEnterStageRequest { public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarAwayStageRequest { public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarQuitStageRequest { }
[MessagePackObject(true)] public sealed class XPunishaarSelectShopRequest { public int ShopId { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarRefreshShopRequest { }
[MessagePackObject(true)]
public sealed class XPunishaarBuyGoodsRequest
{
    public int Index { get; set; }
    public PunishaarRewardCardDetailInfo? CardDetail { get; set; }
}
[MessagePackObject(true)]
public sealed class XPunishaarFreezeGoodsRequest
{
    public int Index { get; set; }
    public bool IsFreeze { get; set; }
}
[MessagePackObject(true)] public sealed class XPunishaarSellCardRequest { public int MasterCardId { get; set; } }
[MessagePackObject(true)]
public sealed class XPunishaarDiscardCardRequest
{
    public bool IsMasterCard { get; set; }
    public int MasterCardId { get; set; }
}
[MessagePackObject(true)]
public sealed class XPunishaarSetCardPosRequest { public List<PunishaarCardPosInfo>? CardPosList { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarExitNodeRequest { }
[MessagePackObject(true)] public sealed class XPunishaarEnterFightRequest { }
[MessagePackObject(true)] public sealed class XPunishaarSelectFightRequest { public int FightId { get; set; } }
[MessagePackObject(true)]
public sealed class XPunishaarFinishFightRequest
{
    public bool IsWin { get; set; }
    public int LoseMaxSignalBallColor { get; set; }
    public int FightTime { get; set; }
    public bool FightSpeed { get; set; }
    public bool IsAutoFight { get; set; }
    public int UseSkillCount { get; set; }
    public int AutoUseSkillCount { get; set; }
    public int BallProduction { get; set; }
    public int BallConsumption { get; set; }
    public int StartHp { get; set; }
    public int EndHp { get; set; }
    public int EnemyStartHp { get; set; }
    public int EnemyEndHp { get; set; }
}
[MessagePackObject(true)] public sealed class XPunishaarSelectEventRequest { public int EventId { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarFinishEventRequest { }
[MessagePackObject(true)]
public sealed class XPunishaarHandlePendingRewardRequest
{
    public bool IsAccept { get; set; }
    public PunishaarRewardCardDetailInfo? CardDetail { get; set; }
}

// Responses
[MessagePackObject(true)]
public sealed class XPunishaarGetDataResponse { public int Code { get; set; } public PunishaarDataDb? DataDb { get; set; } }
[MessagePackObject(true)]
public sealed class XPunishaarStartStageResponse { public int Code { get; set; } public PunishaarStage? Stage { get; set; } }
[MessagePackObject(true)]
public sealed class XPunishaarEnterStageResponse { public int Code { get; set; } public PunishaarStage? Stage { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarAwayStageResponse { public int Code { get; set; } }
[MessagePackObject(true)]
public sealed class XPunishaarQuitStageResponse { public int Code { get; set; } public PunishaarSettleInfo? SettleInfo { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarSelectShopResponse { public int Code { get; set; } public PunishaarNode? Node { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarRefreshShopResponse { public int Code { get; set; } public PunishaarNode? Node { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarBuyGoodsResponse { public int Code { get; set; } public PunishaarNode? Node { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarFreezeGoodsResponse { public int Code { get; set; } public PunishaarNode? Node { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarSellCardResponse { public int Code { get; set; } public PunishaarNode? Node { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarDiscardCardResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarSetCardPosResponse { public int Code { get; set; } }
[MessagePackObject(true)]
public sealed class XPunishaarExitNodeResponse
{
    public int Code { get; set; }
    public PunishaarStage? Stage { get; set; }
    public PunishaarSettleInfo? SettleInfo { get; set; }
}
[MessagePackObject(true)] public sealed class XPunishaarEnterFightResponse { public int Code { get; set; } public PunishaarNode? Node { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarSelectFightResponse { public int Code { get; set; } public PunishaarNode? Node { get; set; } }
[MessagePackObject(true)]
public sealed class XPunishaarFinishFightResponse
{
    public int Code { get; set; }
    public PunishaarStage? Stage { get; set; }
    public PunishaarSettleInfo? SettleInfo { get; set; }
}
[MessagePackObject(true)] public sealed class XPunishaarSelectEventResponse { public int Code { get; set; } public PunishaarNode? Node { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarFinishEventResponse { public int Code { get; set; } public PunishaarNode? Node { get; set; } }
[MessagePackObject(true)] public sealed class XPunishaarHandlePendingRewardResponse { public int Code { get; set; } public PunishaarNode? Node { get; set; } }
