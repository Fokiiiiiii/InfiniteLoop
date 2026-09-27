using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("version47_envelope")]
    public EnvelopeState Envelope { get; set; } = new();

    [BsonElement("version47_pbr")]
    public PbrState Pbr { get; set; } = new();

    [BsonElement("version47_concert_preheating")]
    public ConcertPreHeatingState ConcertPreHeating { get; set; } = new();
}

/// <summary>Durable 4.7 Envelope activity state (daily ticket grant rollover + open/bind/AVG fields echoed by the enter response).</summary>
public sealed class EnvelopeState
{
    [BsonElement("activity_id")]
    public int ActivityId { get; set; }

    [BsonElement("last_daily_grant_business_day")]
    public int LastDailyGrantBusinessDay { get; set; }

    [BsonElement("opened_character_ids")]
    public List<int> OpenedCharacterIds { get; set; } = new();

    [BsonElement("instrument_bindings")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> InstrumentBindings { get; set; } = new();

    [BsonElement("avg_watched_character_ids")]
    public List<int> AvgWatchedCharacterIds { get; set; } = new();

    /// <summary>AscNet policy: prior-period daily task claim key → task id, earned but unclaimed at rollover; reissued once on Enter.</summary>
    [BsonElement("pending_task_reissues")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<string, int> PendingTaskReissues { get; set; } = new();
}

/// <summary>Durable 4.7 PBR activity root (empty/default meta progression, stage records, compendiums, segment settle).</summary>
public sealed class PbrState
{
    [BsonElement("activity_id")]
    public int ActivityId { get; set; }

    [BsonElement("meta_progression_unlock_nodes")]
    public List<int> MetaProgressionUnlockNodes { get; set; } = new();

    [BsonElement("stage_records")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, PbrStageRecordState> StageRecords { get; set; } = new();

    [BsonElement("compendium_items")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, PbrItemState> CompendiumItems { get; set; } = new();

    [BsonElement("compendium_monsters")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, PbrMonsterState> CompendiumMonsters { get; set; } = new();

    [BsonElement("segment_settle")]
    public PbrSegmentSettleState? SegmentSettle { get; set; }
}

public sealed class PbrStageRecordState
{
    [BsonElement("stage_id")] public int StageId { get; set; }
    [BsonElement("history_max_wave")] public int HistoryMaxWave { get; set; }
    [BsonElement("is_pass")] public bool IsPass { get; set; }
    [BsonElement("is_pass_wave")] public bool IsPassWave { get; set; }
}

public sealed class PbrItemState
{
    [BsonElement("item_id")] public int ItemId { get; set; }
    [BsonElement("unlock_time")] public long UnlockTime { get; set; }
    [BsonElement("gain_num")] public int GainNum { get; set; }
    [BsonElement("trigger_num")] public int TriggerNum { get; set; }
}

public sealed class PbrMonsterState
{
    [BsonElement("monster_id")] public int MonsterId { get; set; }
    [BsonElement("damage_total")] public long DamageTotal { get; set; }
    [BsonElement("be_kill_num")] public int BeKillNum { get; set; }
}

public sealed class PbrSegmentSettleState
{
    [BsonElement("state")] public int State { get; set; }
    [BsonElement("stage_id")] public int StageId { get; set; }
    [BsonElement("shop_data")] public PbrShopDataState? ShopData { get; set; }
    [BsonElement("wave")] public int Wave { get; set; }
    [BsonElement("character_id")] public int CharacterId { get; set; }
    [BsonElement("character_level")] public int CharacterLevel { get; set; }
    [BsonElement("character_exp")] public int CharacterExp { get; set; }
    [BsonElement("base_attrs")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> BaseAttrs { get; set; } = new();
    [BsonElement("cur_attrs")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> CurAttrs { get; set; } = new();
    [BsonElement("max_attrs")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> MaxAttrs { get; set; } = new();
    [BsonElement("items")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, PbrItemState> Items { get; set; } = new();
    [BsonElement("wave_monsters")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, PbrMonsterState> WaveMonsters { get; set; } = new();
    [BsonElement("wave_obrs")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, PbrItemState> WaveObrs { get; set; } = new();
}

public sealed class PbrShopDataState
{
    [BsonElement("shop_id")] public int ShopId { get; set; }
    [BsonElement("max_choose_count")] public int MaxChooseCount { get; set; }
    [BsonElement("max_fresh_count")] public int MaxFreshCount { get; set; }
    [BsonElement("use_choose_count")] public int UseChooseCount { get; set; }
    [BsonElement("use_fresh_count")] public int UseFreshCount { get; set; }
    [BsonElement("sell_items")] public List<int> SellItems { get; set; } = new();
}

/// <summary>Durable 4.7 Concert Pre-Heating activity state (completed stages).</summary>
public sealed class ConcertPreHeatingState
{
    [BsonElement("activity_id")]
    public int ActivityId { get; set; }

    [BsonElement("completed_stage_ids")]
    public List<int> CompletedStageIds { get; set; } = new();
}
