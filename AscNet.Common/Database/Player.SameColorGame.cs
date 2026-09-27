using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace AscNet.Common.Database;

public sealed class SameColorGameBossRecord
{
    [BsonElement("boss_id")] public int BossId { get; set; }
    [BsonElement("max_point")] public long MaxPoint { get; set; }
    [BsonElement("max_combo")] public int MaxCombo { get; set; }
    [BsonElement("last_use_role_id")] public int LastUseRoleId { get; set; }
}

public sealed class SameColorGameState
{
    [BsonElement("activity_id")] public int ActivityId { get; set; }
    [BsonElement("boss_records")] public List<SameColorGameBossRecord> BossRecords { get; set; } = new();
    // Sum of completed run scores. Abandoned/given-up runs never contribute.
    [BsonElement("total_score")] public long TotalScore { get; set; }
    // The unfinished run, or null when the player is not on a board.
    [BsonElement("run")] public SameColorGameRun? Run { get; set; }
    // Durable intent: ranking projections written after a terminal settlement. Kept in the
    // player document so a failed projection can be retried by a later rank query.
    [BsonElement("pending_rank_updates")] public List<SameColorGameRankUpdate> PendingRankUpdates { get; set; } = new();
}

public sealed class SameColorGameRankUpdate
{
    // 0 = total-score board, otherwise the boss score board.
    [BsonElement("boss_id")] public int BossId { get; set; }
    [BsonElement("score")] public long Score { get; set; }
    [BsonElement("role_id")] public int RoleId { get; set; }
    [BsonElement("achieved_at")] public long AchievedAt { get; set; }
}

/// <summary>One in-progress board. ItemId 0 marks an empty cell.</summary>
public sealed class SameColorGameRun
{
    [BsonElement("boss_id")] public int BossId { get; set; }
    [BsonElement("role_id")] public int RoleId { get; set; }
    [BsonElement("skill_group_ids")] public List<int> SkillGroupIds { get; set; } = new();
    [BsonElement("rows")] public int Rows { get; set; }
    [BsonElement("cols")] public int Cols { get; set; }
    [BsonElement("board")] public List<SameColorGameCell> Board { get; set; } = new();
    [BsonElement("cur_round")] public int CurRound { get; set; }
    [BsonElement("max_round")] public int MaxRound { get; set; }
    [BsonElement("energy")] public int Energy { get; set; }
    [BsonElement("score")] public long Score { get; set; }
    [BsonElement("max_combo")] public int MaxCombo { get; set; }
    [BsonElement("total_game_combo")] public long TotalGameCombo { get; set; }
    [BsonElement("obtain_energy")] public long ObtainEnergy { get; set; }
    [BsonElement("skill_cost_energy")] public long SkillCostEnergy { get; set; }
    [BsonElement("boss_cost_energy")] public long BossCostEnergy { get; set; }
    [BsonElement("start_unix")] public long StartUnix { get; set; }
    [BsonElement("next_buff_uid")] public int NextBuffUid { get; set; }
    [BsonElement("next_weak_uid")] public int NextWeakUid { get; set; }
    [BsonElement("buffs")] public List<SameColorGameBuffState> Buffs { get; set; } = new();
    [BsonElement("skills")] public List<SameColorGameSkillState> Skills { get; set; } = new();
    [BsonElement("skill_use_count")] public List<SameColorGameCounter> SkillUseCount { get; set; } = new();
    [BsonElement("prop_stats")] public List<SameColorGamePropStat> PropStats { get; set; } = new();
    [BsonElement("weak_infos")] public List<SameColorGameWeakInfo> WeakInfos { get; set; } = new();
    // Skill 602 drop conversion, skill 603 drop exclusion. Colour 0 means inactive.
    [BsonElement("drop_color")] public int DropColor { get; set; }
    [BsonElement("drop_color_left")] public int DropColorLeft { get; set; }
    [BsonElement("excluded_color")] public int ExcludedColor { get; set; }
    [BsonElement("excluded_color_left")] public int ExcludedColorLeft { get; set; }
    [BsonElement("drop_color_buff_uid")] public int DropColorBuffUid { get; set; }
    [BsonElement("excluded_color_buff_uid")] public int ExcludedColorBuffUid { get; set; }
    [BsonElement("rng_seed")] public long RngSeed { get; set; }
    [BsonElement("rng_step")] public long RngStep { get; set; }

    public SameColorGameRun Clone()
    {
        SameColorGameRun copy = (SameColorGameRun)MemberwiseClone();
        copy.SkillGroupIds = new List<int>(SkillGroupIds);
        copy.Board = Board.Select(cell => cell.Clone()).ToList();
        copy.Buffs = Buffs.Select(buff => buff.Clone()).ToList();
        copy.Skills = Skills.Select(skill => skill.Clone()).ToList();
        copy.SkillUseCount = SkillUseCount.Select(counter => counter.Clone()).ToList();
        copy.PropStats = PropStats.Select(stat => stat.Clone()).ToList();
        copy.WeakInfos = WeakInfos.Select(info => info.Clone()).ToList();
        return copy;
    }
}

public sealed class SameColorGameCell
{
    [BsonElement("item_id")] public int ItemId { get; set; }
    [BsonElement("ball_type")] public int BallType { get; set; }
    [BsonElement("weak_uid")] public int WeakUid { get; set; }
    [BsonElement("weak_hit_times")] public int WeakHitTimes { get; set; }

    public SameColorGameCell Clone() => (SameColorGameCell)MemberwiseClone();
}

public sealed class SameColorGameBuffState
{
    [BsonElement("buff_id")] public int BuffId { get; set; }
    [BsonElement("uid")] public int Uid { get; set; }
    [BsonElement("applied_round")] public int AppliedRound { get; set; }
    // 0 = permanent for the rest of the run.
    [BsonElement("left_turns")] public int LeftTurns { get; set; }
    [BsonElement("remaining_count")] public int RemainingCount { get; set; }
    // Summon instance that owns this buff, or 0 when the player owns it.
    [BsonElement("owner_weak_uid")] public int OwnerWeakUid { get; set; }

    public SameColorGameBuffState Clone() => (SameColorGameBuffState)MemberwiseClone();
}

public sealed class SameColorGameSkillState
{
    [BsonElement("group_id")] public int GroupId { get; set; }
    [BsonElement("skill_id")] public int SkillId { get; set; }
    [BsonElement("left_cd")] public int LeftCd { get; set; }

    public SameColorGameSkillState Clone() => (SameColorGameSkillState)MemberwiseClone();
}

public sealed class SameColorGameCounter
{
    [BsonElement("key")] public int Key { get; set; }
    [BsonElement("value")] public int Value { get; set; }

    public SameColorGameCounter Clone() => (SameColorGameCounter)MemberwiseClone();
}

public sealed class SameColorGamePropStat
{
    [BsonElement("ball_id")] public int BallId { get; set; }
    [BsonElement("round")] public int Round { get; set; }
    [BsonElement("gain_count")] public int GainCount { get; set; }
    [BsonElement("use_count")] public int UseCount { get; set; }

    public SameColorGamePropStat Clone() => (SameColorGamePropStat)MemberwiseClone();
}

public sealed class SameColorGameWeakInfo
{
    [BsonElement("uid")] public int Uid { get; set; }
    [BsonElement("item_id")] public int ItemId { get; set; }
    [BsonElement("create_round")] public int CreateRound { get; set; }
    [BsonElement("update_rounds")] public List<int> UpdateRounds { get; set; } = new();

    public SameColorGameWeakInfo Clone()
    {
        SameColorGameWeakInfo copy = (SameColorGameWeakInfo)MemberwiseClone();
        copy.UpdateRounds = new List<int>(UpdateRounds);
        return copy;
    }
}

/// <summary>Ranking projection for the current activity. One row per player and board.</summary>
public sealed class SameColorGameRankEntry
{
    public static IMongoCollection<SameColorGameRankEntry> collection =
        Common.db.GetCollection<SameColorGameRankEntry>("same_color_game_rank_entries");

    [BsonId] public string Id { get; set; } = string.Empty;
    [BsonElement("activity_id")] public int ActivityId { get; set; }
    [BsonElement("boss_id")] public int BossId { get; set; }
    [BsonElement("player_id")] public long PlayerId { get; set; }
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
    [BsonElement("head_portrait_id")] public long HeadPortraitId { get; set; }
    [BsonElement("head_frame_id")] public long HeadFrameId { get; set; }
    [BsonElement("score")] public long Score { get; set; }
    [BsonElement("role_id")] public int RoleId { get; set; }
    [BsonElement("achieved_at")] public long AchievedAt { get; set; }

    public static string BuildId(int activityId, int bossId, long playerId) =>
        $"{activityId}:{bossId}:{playerId}";
}

public partial class Player
{
    [BsonElement("same_color_game")]
    public SameColorGameState SameColorGame { get; set; } = new();
}
