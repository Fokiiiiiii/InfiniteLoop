using MessagePack;
using MessagePack.Formatters;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.MsgPack
{
    // Lua tables arrive as arrays when their keys are 1..n (and possibly when empty), otherwise as maps.
    // These formatters accept nil/array/map on input and always write the canonical shape.
    public sealed class LuaListFormatter<T> : IMessagePackFormatter<List<T>?>
    {
        public void Serialize(ref MessagePackWriter writer, List<T>? value, MessagePackSerializerOptions options)
        {
            value ??= [];
            IMessagePackFormatter<T> formatter = options.Resolver.GetFormatterWithVerify<T>();
            writer.WriteArrayHeader(value.Count);
            foreach (T item in value)
                formatter.Serialize(ref writer, item, options);
        }

        public List<T>? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            List<T> list = [];
            if (reader.TryReadNil())
                return list;
            IMessagePackFormatter<T> formatter = options.Resolver.GetFormatterWithVerify<T>();
            if (reader.NextMessagePackType == MessagePackType.Map)
            {
                int count = reader.ReadMapHeader();
                for (int i = 0; i < count; i++)
                {
                    reader.Skip();
                    list.Add(formatter.Deserialize(ref reader, options));
                }
                return list;
            }
            int length = reader.ReadArrayHeader();
            for (int i = 0; i < length; i++)
                list.Add(formatter.Deserialize(ref reader, options));
            return list;
        }
    }

    public sealed class LuaIntDictFormatter<TValue> : IMessagePackFormatter<Dictionary<int, TValue>?>
    {
        public void Serialize(ref MessagePackWriter writer, Dictionary<int, TValue>? value, MessagePackSerializerOptions options)
        {
            value ??= [];
            IMessagePackFormatter<TValue> formatter = options.Resolver.GetFormatterWithVerify<TValue>();
            writer.WriteMapHeader(value.Count);
            foreach ((int key, TValue item) in value)
            {
                writer.Write(key);
                formatter.Serialize(ref writer, item, options);
            }
        }

        public Dictionary<int, TValue>? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            Dictionary<int, TValue> dict = [];
            if (reader.TryReadNil())
                return dict;
            IMessagePackFormatter<TValue> formatter = options.Resolver.GetFormatterWithVerify<TValue>();
            if (reader.NextMessagePackType == MessagePackType.Array)
            {
                int length = reader.ReadArrayHeader();
                for (int i = 1; i <= length; i++)
                    dict[i] = formatter.Deserialize(ref reader, options);
                return dict;
            }
            int count = reader.ReadMapHeader();
            for (int i = 0; i < count; i++)
            {
                int key = reader.NextMessagePackType == MessagePackType.String ? int.Parse(reader.ReadString()!) : reader.ReadInt32();
                dict[key] = formatter.Deserialize(ref reader, options);
            }
            return dict;
        }
    }

    #region Cafe (dump.cs 70188-70285, 353038-353215)

    [MessagePackObject(true)]
    public class XBigWorldCafeDb
    {
        public CafeGambling? CafeGambling { get; set; }
        [MessagePackFormatter(typeof(LuaIntDictFormatter<int>))]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> CardDict { get; set; } = [];
        [MessagePackFormatter(typeof(LuaIntDictFormatter<List<int>>))]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, List<int>> CardGroupList { get; set; } = [];
        [MessagePackFormatter(typeof(LuaIntDictFormatter<CafeStageRecord>))]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, CafeStageRecord> CafeStageList { get; set; } = [];
        public long BeginTime { get; set; }
    }

    [MessagePackObject(true)]
    public class CafeStageRecord
    {
        public int StageId { get; set; }
        public int MaxSales { get; set; }
        public int GetMaxStarReward { get; set; }
        public bool IsKickOutCafe { get; set; }
    }

    [MessagePackObject(true)]
    public class BuffAddition
    {
        public int Coffee { get; set; }
        public int Review { get; set; }
    }

    // Client sends NextRoundBuffs as {BUffId, CardId} (XSkyGardenCafeBattle.lua:666-669) but reads BuffId (:631-652).
    [MessagePackFormatter(typeof(CafeBuffFormatter))]
    public class CafeBuff
    {
        public int BuffId { get; set; }
        public int CardId { get; set; }
    }

    public sealed class CafeBuffFormatter : IMessagePackFormatter<CafeBuff?>
    {
        public void Serialize(ref MessagePackWriter writer, CafeBuff? value, MessagePackSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNil();
                return;
            }
            writer.WriteMapHeader(2);
            writer.Write(nameof(CafeBuff.BuffId));
            writer.Write(value.BuffId);
            writer.Write(nameof(CafeBuff.CardId));
            writer.Write(value.CardId);
        }

        public CafeBuff? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil())
                return null;
            CafeBuff buff = new();
            int count = reader.ReadMapHeader();
            for (int i = 0; i < count; i++)
            {
                switch (reader.ReadString())
                {
                    case "BuffId":
                    case "BUffId":
                        buff.BuffId = reader.ReadInt32();
                        break;
                    case "CardId":
                        buff.CardId = reader.ReadInt32();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            return buff;
        }
    }

    [MessagePackObject(true)]
    public class CafeGambling
    {
        public int StageId { get; set; }
        public int Round { get; set; }
        public int SumSales { get; set; }
        public int ActPoint { get; set; }
        public int HandCardPosNum { get; set; }
        public int ReviewNum { get; set; }
        [MessagePackFormatter(typeof(LuaListFormatter<int>))] public List<int> HandCards { get; set; } = [];
        [MessagePackFormatter(typeof(LuaListFormatter<int>))] public List<int> AbandonCards { get; set; } = [];
        [MessagePackFormatter(typeof(LuaListFormatter<int>))] public List<int> BanCards { get; set; } = [];
        [MessagePackFormatter(typeof(LuaListFormatter<int>))] public List<int> CardsWarehouse { get; set; } = [];
        // Native HashSet<int>; wire shape is an array either way.
        [MessagePackFormatter(typeof(LuaListFormatter<int>))] public List<int> PriorityCard { get; set; } = [];
        [MessagePackFormatter(typeof(LuaListFormatter<int>))] public List<int> RetainHandCard { get; set; } = [];
        [MessagePackFormatter(typeof(LuaIntDictFormatter<int>))]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> UseCardTimes { get; set; } = [];
        [MessagePackFormatter(typeof(LuaIntDictFormatter<BuffAddition>))]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, BuffAddition> BuffAdditionDict { get; set; } = [];
        public int CardGroupId { get; set; }
        [MessagePackFormatter(typeof(LuaListFormatter<CafeBuff>))] public List<CafeBuff> NextRoundBuffs { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class NotifyBigWorldCafeData
    {
        public XBigWorldCafeDb Data { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class BigWorldCafeNewRoundRequest
    {
        public int CardGroupId { get; set; }
        [MessagePackFormatter(typeof(LuaListFormatter<int>))] public List<int> CardList { get; set; } = [];
        public CafeGambling? CafeGambling { get; set; }
        [MessagePackFormatter(typeof(LuaListFormatter<int>))] public List<int> AbandonedCardList { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class BigWorldCafeNewRoundResponse
    {
        public int Code { get; set; }
        public CafeGambling? CafeGambling { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldCafeNextRoundRequest
    {
        public CafeGambling? CafeGambling { get; set; }
        public int ResetTimes { get; set; }
        [MessagePackFormatter(typeof(LuaListFormatter<int>))] public List<int> PlayedCardList { get; set; } = [];
        [MessagePackFormatter(typeof(LuaListFormatter<int>))] public List<int> HandCardsBeforePlay { get; set; } = [];
        [MessagePackFormatter(typeof(LuaListFormatter<int>))] public List<int> ReviewNumChangeList { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class BigWorldCafeNextRoundResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyBigWorldCafeSettle
    {
        public int Code { get; set; }
        public int StageId { get; set; }
        public int Star { get; set; }
        public int SumSales { get; set; }
        public List<int> AwardList { get; set; } = [];
        public List<int> RewardCardList { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class NotifyBigWorldNewCafeCard
    {
        public int CardId { get; set; }
        public int Num { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldCafeCardGroupListSaveRequest
    {
        public int GroupId { get; set; }
        [MessagePackFormatter(typeof(LuaListFormatter<int>))] public List<int> CardList { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class BigWorldCafeCardGroupListSaveResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldCafeGiveUpResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldCafeGuideKickOutSceneRequest
    {
        public int StageId { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldCafeGuideKickOutSceneResponse
    {
        public int Code { get; set; }
    }

    #endregion

    #region Drone (Lua only: XSkyGardenDroneGameAgency/Control, xdata/XSGDroneStageData)

    [MessagePackObject(true)]
    public class SgDroneStageInfo
    {
        public bool IsFinished { get; set; }
        public int MinCostTime { get; set; }
        public int MaxScore { get; set; }
        public List<int> FinishedStarTargets { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class SgDroneCurStageData
    {
        public int CurStageId { get; set; }
        public bool IsHardMode { get; set; }
        public int Seed { get; set; }
        // Opaque client StageSuspendSaveData (Game.lua:684-714), echoed back as sent.
        public object? SaveData { get; set; }
    }

    [MessagePackObject(true)]
    public class SgDroneGameData
    {
        public Dictionary<int, SgDroneStageInfo> StageInfo { get; set; } = [];
        public SgDroneCurStageData? CurStageData { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifySgDroneGameData
    {
        public SgDroneGameData GameData { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class SgDroneGameStageStartRequest
    {
        public int StageId { get; set; }
        public bool IsHardMode { get; set; }
    }

    [MessagePackObject(true)]
    public class SgDroneGameStageStartResponse
    {
        public int Code { get; set; }
        public SgDroneCurStageData? CurStageData { get; set; }
    }

    [MessagePackObject(true)]
    public class SgDroneGameStageSuspendRequest
    {
        public int StageId { get; set; }
        public object? StageSuspendSaveData { get; set; }
    }

    [MessagePackObject(true)]
    public class SgDroneGameStageSuspendResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class SgDroneGameStageGiveUpResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class SgDroneGameStageSettleRequest
    {
        public int StageId { get; set; }
        public int CostTime { get; set; }
        public int Score { get; set; }
        [MessagePackFormatter(typeof(LuaIntDictFormatter<int>))] public Dictionary<int, int> TargetProgress { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class SgDroneGameStageSettleResponse
    {
        public int Code { get; set; }
        public Dictionary<int, SgDroneStageInfo> StageInfo { get; set; } = [];
    }

    #endregion

    #region Dorm (retail NotifySgDormData capture; XSgDormData.lua:72-82, XSkyGardenDormControl.lua:397-447)

    [MessagePackObject(true)]
    public class SgDormFurnitureItem
    {
        public int Id { get; set; }
        public int CfgId { get; set; }
    }

    // Retail key order: Layer, X, Y, Angle, Index, Scale, PhotoId, Id, CfgId.
    [MessagePackObject(true)]
    public class SgDormPlacedFurniture
    {
        public int Layer { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Angle { get; set; }
        public int Index { get; set; }
        public int Scale { get; set; }
        public int PhotoId { get; set; }
        public int Id { get; set; }
        public int CfgId { get; set; }
    }

    [MessagePackObject(true)]
    public class SgDormFurnitureInfo
    {
        public SgDormPlacedFurniture? Container { get; set; }
        [MessagePackFormatter(typeof(LuaListFormatter<SgDormPlacedFurniture>))]
        public List<SgDormPlacedFurniture> PlacementFurniture { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class SgDormLayoutData
    {
        public int AreaType { get; set; }
        public int LayoutId { get; set; }
        public List<SgDormFurnitureInfo> FurnitureInfos { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class SgDormFashionData
    {
        public int Id { get; set; }
    }

    // Also the persisted dorm state (BigWorldPlayerState.SgDorm).
    [MessagePackObject(true)]
    public class NotifySgDormData
    {
        public int CurFashionId { get; set; }
        public List<SgDormFurnitureItem> FurnitureList { get; set; } = [];
        public List<SgDormLayoutData> LayoutList { get; set; } = [];
        public List<SgDormFashionData> DormFashionList { get; set; } = [];
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> CurAreaLayout { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class NotifySgDormFurnitureAdd
    {
        public List<SgDormFurnitureItem> AddFurnitureList { get; set; } = [];
    }

    // XSgDormData:NotifySgDormFashionAdd reads data.AddDormFashion.Id [LUA XSgDormData.lua:84].
    [MessagePackObject(true)]
    public class NotifySgDormFashionAdd
    {
        public SgDormFashionData AddDormFashion { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class SgDormSaveAndApplyLayoutRequest
    {
        public int AreaType { get; set; }
        public int SaveLayoutId { get; set; }
        public int ApplyLayoutId { get; set; }
        [MessagePackFormatter(typeof(LuaListFormatter<SgDormFurnitureInfo>))]
        public List<SgDormFurnitureInfo> SaveFurnitureInfos { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class SgDormSaveAndApplyLayoutResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class SgDormSetFashionRequest
    {
        public int FashionId { get; set; }
    }

    [MessagePackObject(true)]
    public class SgDormSetFashionResponse
    {
        public int Code { get; set; }
    }

    #endregion
}
