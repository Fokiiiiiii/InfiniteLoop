using MessagePack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

// Babylonia shopping street (client xskygardenshoppingstreet) wire shapes. Field names follow the Lua consumers:
// Model:_UpdateStageData/StartRunRound, XSGGameArea, PopupRoundEnd, Control request callbacks.
// The same classes are persisted inside BigWorldPlayerState.SgStreet.
namespace AscNet.Common.MsgPack
{
#pragma warning disable CS8618
    [MessagePackObject(true)]
    public class SgStreetResourceData
    {
        public int ResourceId { get; set; }
        public int Count { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetFoodData
    {
        public int ChefId { get; set; }
        public List<int> GoodsCountList { get; set; } = new();
        public int Gold { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetShelfData
    {
        public int GoodsId { get; set; }
        public int GoldCount { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetGroceryData
    {
        public List<SgStreetShelfData> ShelfDatas { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class SgStreetDessertData
    {
        public List<int> GoodsIdList { get; set; } = new();
        public int Gold { get; set; }
    }

    // Control:ParseFeedback / GridFeedback.
    [MessagePackObject(true)]
    public class SgStreetFeedBackData
    {
        public int Id { get; set; }
        public int FeedbackTemplateId { get; set; }
        public int DescIndex { get; set; }
        public int GoodId { get; set; }
        public int Difference { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetCollectFeedBack
    {
        public int CustomerId { get; set; }
        public SgStreetFeedBackData FeedBackData { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetShopData
    {
        public int ShopId { get; set; }
        public int Position { get; set; }
        public int Level { get; set; }
        public int MainType { get; set; }
        public bool IsBuildByInit { get; set; }
        public List<int> UpgradeBranchIds { get; set; } = new();
        public List<int> BranchIds { get; set; } = new();
        public int Score { get; set; }
        public int TotalCost { get; set; }
        public int CustomerNum { get; set; }
        public SgStreetFoodData? FoodData { get; set; }
        public SgStreetFoodData? FoodLikeData { get; set; }
        public SgStreetGroceryData? GroceryData { get; set; }
        public SgStreetGroceryData? GroceryLikeData { get; set; }
        public SgStreetDessertData? DessertData { get; set; }
        public SgStreetDessertData? DessertLikeData { get; set; }
        public List<SgStreetCollectFeedBack> LastCollectFeedBacks { get; set; } = new();
        public List<SgStreetFeedBackData> LastFeedBacks { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class SgStreetBuffData
    {
        public int BuffUid { get; set; }
        public int BuffConfigId { get; set; }
        public int RemainingTurn { get; set; }
        public int CreateTurn { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetNewsData
    {
        public int Turn { get; set; }
        public int NewsId { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetGrapevineParam
    {
        public int GoodId { get; set; }
        public int Difference { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetGrapevineData
    {
        public int Turn { get; set; }
        public int GrapevineId { get; set; }
        public int ShopId { get; set; }
        public List<SgStreetGrapevineParam> GrapevineList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class SgStreetBillboardData
    {
        public int CurrentBillboardId { get; set; }
        public int CurrentTaskId { get; set; }
        public int LastRefreshTurn { get; set; }
        public List<int> RandomBillboards { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class SgStreetStatisticsData
    {
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> CustomerNums { get; set; } = new();
        public List<int> AllGrapevineIds { get; set; } = new();
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> FinishTaskTimeDict { get; set; } = new();
        public int MaxDailyGold { get; set; }
        public int PromotionTimes { get; set; }
        public int DiscontentEventTimes { get; set; }
        public int EmergencyEventTimes { get; set; }
        public int AccumulativeGold { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetTaskData
    {
        public int Id { get; set; }
        public int ConfigId { get; set; }
        public int State { get; set; }
        public int Source { get; set; }
        public int Schedule { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetPromotionSelectGroup
    {
        public int Id { get; set; }
        public int Type { get; set; }
        public List<int> PromotionIds { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class SgStreetCustomerEventData
    {
        public int Id { get; set; }
        public int Type { get; set; }
        public int DiscontentAwardGold { get; set; }
        public int EmergencyEventId { get; set; }
        public SgStreetFeedBackData? FeedBackData { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetCommandData
    {
        public int Id { get; set; }
        public int TargetId { get; set; }
        public int Type { get; set; }
        public int ShopAwardGold { get; set; }
        public SgStreetCustomerEventData? EventData { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetCustomerData
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public List<SgStreetCommandData> CommandDatas { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class SgStreetOperatingData
    {
        public int EnvironmentSatisfaction { get; set; }
        public int ShopScoreSatisfaction { get; set; }
        public List<SgStreetCustomerData> CustomerDatas { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class SgStreetSettleStatisticData
    {
        public int Satisfaction { get; set; }
        public int EnvironmentSatisfaction { get; set; }
        public int ShopScoreSatisfaction { get; set; }
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> CustomerNums { get; set; } = new();
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> ShopScoreDatas { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class SgStreetEventSettle
    {
        public int EventType { get; set; }
        public int HandledCount { get; set; }
        public int TotalCount { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetSettleData
    {
        public int AwardGold { get; set; }
        public int CommandAwardGold { get; set; }
        public int DiscontentAwardGold { get; set; }
        public List<int> Reviews { get; set; } = new();
        public List<SgStreetEventSettle> EventSettles { get; set; } = new();
        public SgStreetSettleStatisticData CurrentSettleStatisticData { get; set; } = new();
        public SgStreetSettleStatisticData? LastSettleStatisticData { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetStageData
    {
        public int StageId { get; set; }
        public int Turn { get; set; }
        public List<SgStreetShopData> ShopDatas { get; set; } = new();
        public List<SgStreetResourceData> ResourceDatas { get; set; } = new();
        public int EnvironmentSatisfaction { get; set; }
        public int ShopScoreSatisfaction { get; set; }
        public SgStreetSettleData? LastResultData { get; set; }
        public List<SgStreetBuffData> BuffDatas { get; set; } = new();
        public int FirstOutsideBuildTurn { get; set; }
        public List<SgStreetNewsData> NewsDatas { get; set; } = new();
        public List<SgStreetGrapevineData> ShopGrapevineDatas { get; set; } = new();
        public SgStreetBillboardData? BillboardData { get; set; }
        public SgStreetStatisticsData StatisticsData { get; set; } = new();
        public List<SgStreetTaskData> TaskDatas { get; set; } = new();
        public int RecommendShopId { get; set; }
        public List<SgStreetPromotionSelectGroup> PromotionSelectGroups { get; set; } = new();
        public List<int> CurrentTurnInsideBuilds { get; set; } = new();
        public int InsideBuildTimes { get; set; }
        public SgStreetOperatingData? OperatingData { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetSceneData
    {
        public List<SgStreetShopData> ShopDatas { get; set; } = new();
        public SgStreetBillboardData? BillboardData { get; set; }
    }

    [MessagePackObject(true)]
    public class SgStreetPassedStageRecord
    {
        public List<int> RewardIndexRecord { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class SgStreetData
    {
        public SgStreetStageData? CurStageData { get; set; }
        public SgStreetSceneData? SceneData { get; set; }
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, SgStreetPassedStageRecord> PassedStageRecords { get; set; } = new();
    }

    // Server-private street state persisted beside the wire data (never sent).
    public class SgStreetPrivateState
    {
        // AscNet policy: SplitMix64 state seeded at stage start; every roll advances it, so a run is replayable.
        [BsonElement("rng")]
        public long Rng { get; set; }

        [BsonElement("next_uid")]
        public int NextUid { get; set; }

        [BsonElement("stage_start_count")]
        public int StageStartCount { get; set; }

        // "<kind>:<id>" picks in the current stage; drives First/Repeat/Selected weights.
        [BsonElement("picked")]
        public List<string> Picked { get; set; } = new();

        [BsonElement("turns_since_grapevine")]
        public int TurnsSinceGrapevine { get; set; }

        // Task uid -> counter base for event-count tasks (value of the counter when the task was created).
        [BsonElement("task_bases")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> TaskBases { get; set; } = new();

        [BsonElement("upgrade_times")]
        public int UpgradeTimes { get; set; }
    }

    public interface ISgStreetResponse { int Code { get; set; } }
    public interface ISgStreetShopResponse : ISgStreetResponse { SgStreetShopData? ShopData { get; set; } }

    // Request / response / notify DTOs.
    [MessagePackObject(true)] public class SgStreetStageEnterRequest { }
    [MessagePackObject(true)] public class SgStreetStageEnterResponse : ISgStreetResponse { public int Code { get; set; } }
    [MessagePackObject(true)] public class SgStreetStageStartRequest { public int StageId { get; set; } }
    [MessagePackObject(true)] public class SgStreetStageStartResponse : ISgStreetResponse { public int Code { get; set; } public SgStreetStageData? StageData { get; set; } }
    [MessagePackObject(true)] public class SgStreetStageGiveUpRequest { public int StageId { get; set; } }
    [MessagePackObject(true)] public class SgStreetStageGiveUpResponse : ISgStreetResponse { public int Code { get; set; } }
    [MessagePackObject(true)] public class SgStreetOperatingStartRequest { }
    [MessagePackObject(true)] public class SgStreetOperatingStartResponse : ISgStreetResponse { public int Code { get; set; } public SgStreetOperatingData? OperatingData { get; set; } }
    [MessagePackObject(true)] public class SgStreetEventResult { public int Id { get; set; } public int EmergencyOptionIndex { get; set; } }
    [MessagePackObject(true)] public class SgStreetSettleParam { public int AwardGold { get; set; } public List<SgStreetEventResult> EventResults { get; set; } = new(); }
    [MessagePackObject(true)] public class SgStreetOperatingSettleRequest { public SgStreetSettleParam SettleParam { get; set; } = new(); }
    [MessagePackObject(true)] public class SgStreetOperatingSettleResponse : ISgStreetResponse { public int Code { get; set; } public SgStreetSettleData? SettleData { get; set; } }
    [MessagePackObject(true)] public class SgStreetShopBuildRequest { public int ShopId { get; set; } public int Position { get; set; } }
    [MessagePackObject(true)] public class SgStreetShopBuildResponse : ISgStreetResponse { public int Code { get; set; } public SgStreetShopData? ShopData { get; set; } public List<int> CurrentTurnInsideBuilds { get; set; } = new(); }
    [MessagePackObject(true)] public class SgStreetShopRemoveRequest { public int ShopId { get; set; } }
    [MessagePackObject(true)] public class SgStreetShopRemoveResponse : ISgStreetResponse { public int Code { get; set; } public List<int> CurrentTurnInsideBuilds { get; set; } = new(); }
    [MessagePackObject(true)] public class SgStreetShopUpgradeRequest { public int ShopId { get; set; } public int BranchId { get; set; } }
    [MessagePackObject(true)] public class SgStreetShopUpgradeResponse : ISgStreetResponse { public int Code { get; set; } public SgStreetShopData? ShopData { get; set; } }
    [MessagePackObject(true)] public class SgStreetShopSetupFoodRequest { public int ShopId { get; set; } public int ChefId { get; set; } public List<int> GoodsCountList { get; set; } = new(); public int GoldCount { get; set; } }
    [MessagePackObject(true)] public class SgStreetShopSetupGroceryRequest { public int ShopId { get; set; } public List<SgStreetShelfData> ShelfDataList { get; set; } = new(); }
    [MessagePackObject(true)] public class SgStreetShopSetupDessertRequest { public int ShopId { get; set; } public List<int> GoodsIdList { get; set; } = new(); public int GoldCount { get; set; } }
    [MessagePackObject(true)] public class SgStreetShopSetupFoodResponse : ISgStreetShopResponse { public int Code { get; set; } public SgStreetShopData? ShopData { get; set; } }
    [MessagePackObject(true)] public class SgStreetShopSetupGroceryResponse : ISgStreetShopResponse { public int Code { get; set; } public SgStreetShopData? ShopData { get; set; } }
    [MessagePackObject(true)] public class SgStreetShopSetupDessertResponse : ISgStreetShopResponse { public int Code { get; set; } public SgStreetShopData? ShopData { get; set; } }
    [MessagePackObject(true)] public class SgStreetShopSetRecommendRequest { public int ShopId { get; set; } }
    [MessagePackObject(true)] public class SgStreetShopSetRecommendResponse : ISgStreetResponse { public int Code { get; set; } }
    [MessagePackObject(true)] public class SgStreetPromotionSelectRequest { public int SelectGroupId { get; set; } public int Index { get; set; } }
    [MessagePackObject(true)] public class SgStreetPromotionSelectResponse : ISgStreetResponse { public int Code { get; set; } }
    [MessagePackObject(true)] public class SgStreetBillboardSelectRequest { public int BillboardId { get; set; } }
    [MessagePackObject(true)] public class SgStreetBillboardSelectResponse : ISgStreetResponse { public int Code { get; set; } public int TaskId { get; set; } }
    [MessagePackObject(true)] public class SgStreetFinishTasksRequest { public List<int> TaskIds { get; set; } = new(); }
    [MessagePackObject(true)] public class SgStreetFinishTasksResponse : ISgStreetResponse { public int Code { get; set; } public List<int> FinishedTaskIds { get; set; } = new(); }
    [MessagePackObject(true)] public class SgStreetStageWinSettleRequest { public int StageId { get; set; } }
    [MessagePackObject(true)] public class SgStreetStageWinSettleResponse : ISgStreetResponse { public int Code { get; set; } public List<RewardGoods> RewardGoodsList { get; set; } = new(); public bool IsNewStagePassed { get; set; } }

    [MessagePackObject(true)] public class NotifySgStreetData { public SgStreetData Data { get; set; } }
    [MessagePackObject(true)] public class NotifySgStreetCurStageData { public SgStreetStageData? Data { get; set; } }
    [MessagePackObject(true)] public class NotifySgStreetAfterOperatingSettleStageData { public SgStreetStageData Data { get; set; } }
    [MessagePackObject(true)] public class NotifySgStreetStageSettle { public SgStreetStageData? StageData { get; set; } public SgStreetSceneData? SceneData { get; set; } public Dictionary<int, SgStreetPassedStageRecord> PassedStageRecords { get; set; } = new(); }
    [MessagePackObject(true)] public class NotifySgStreetResourceChange { public List<SgStreetResourceData> Datas { get; set; } = new(); }
    [MessagePackObject(true)] public class NotifySgStreetShopChange { public SgStreetShopData Datas { get; set; } }
    [MessagePackObject(true)] public class NotifySgStreetPromotionSelectGroupAdd { public SgStreetPromotionSelectGroup Data { get; set; } }
    [MessagePackObject(true)] public class NotifySgStreetAttrAdds { public Dictionary<int, int> AttrAdds { get; set; } = new(); public Dictionary<int, Dictionary<int, int>> ShopAttrAdds { get; set; } = new(); }
    [MessagePackObject(true)] public class NotifySgStreetBuffsData { public List<SgStreetBuffData> BuffDatas { get; set; } = new(); }
    [MessagePackObject(true)] public class NotifySgStreetStatisticsData { public SgStreetStatisticsData StatisticsData { get; set; } }
    [MessagePackObject(true)] public class NotifySgStreetTaskData { public List<SgStreetTaskData> TaskDatas { get; set; } = new(); }
    [MessagePackObject(true)] public class NotifySgStreetTaskDataRemove { public List<int> TaskIds { get; set; } = new(); }
    [MessagePackObject(true)] public class NotifySgStreetLikeChange { }
#pragma warning restore CS8618
}
