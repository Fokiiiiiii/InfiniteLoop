using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.archive;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.fuben.simulatetrain;
using MessagePack;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public sealed class ArchiveIdsRequest { public List<int> Ids { get; set; } = new(); }
[MessagePackObject(true)]
public sealed class ArchiveResponse { public int Code { get; set; } public List<int> SuccessIds { get; set; } = new(); }
[MessagePackObject(true)]
public sealed class ArchiveEvaluateRequest
{
    public int Id { get; set; }
    public int Type { get; set; }
    public int Score { get; set; }
    public int Difficulty { get; set; }
    public List<int> Tags { get; set; } = new();
}
[MessagePackObject(true)]
public sealed class ArchiveLike { public int Id { get; set; } public int LikeStatus { get; set; } }
[MessagePackObject(true)]
public sealed class ArchiveGiveLikeRequest { public int Type { get; set; } public List<ArchiveLike> LikeList { get; set; } = new(); }
[MessagePackObject(true)]
public sealed class ArchiveEvaluation
{
    public int Id { get; set; }
    public long Score { get; set; }
    public int ScoreCount { get; set; }
    public long Difficulty { get; set; }
    public int DifficultyCount { get; set; }
    public int LikeCount { get; set; }
    public int DislikeCount { get; set; }
    public List<ArchiveTagCount> Tags { get; set; } = new();
}
[MessagePackObject(true)]
public sealed class ArchiveTagCount { public int Id { get; set; } public int Count { get; set; } }
[MessagePackObject(true)]
public sealed class ArchivePersonalEvaluation
{
    public int Id { get; set; }
    public int Score { get; set; }
    public int Difficulty { get; set; }
    public int LikeStatus { get; set; }
    public List<int> Tags { get; set; } = new();
}
[MessagePackObject(true)]
public sealed class ArchiveEvaluationsResponse
{
    public int Code { get; set; }
    public List<ArchiveEvaluation> Evaluates { get; set; } = new();
    public List<ArchivePersonalEvaluation> PersonalEvaluates { get; set; } = new();
}

public static class ArchiveModule
{
    private static readonly Lazy<Dictionary<int, int>> CanonicalNpcs = new(() =>
        TableReaderV2.Parse<SameNpcGroupTable>().SelectMany(row => row.NpcId.Select(id => (Id: id, Canonical: row.Id)))
            .GroupBy(row => row.Id).Where(group => group.Select(row => row.Canonical).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Canonical));
    private static int CanonicalNpc(int id) => CanonicalNpcs.Value.GetValueOrDefault(id, id);

    private static void Respond<T>(Session session, Packet.Request packet, T response) =>
        session.SendResponse(packet.Name[..^"Request".Length] + "Response", MessagePackSerializer.Serialize(response), packet.Id);

    public static List<uint> UnlockedIds(Player player, string kind) =>
        player.ArchiveUnlocks.Where(row => row.Kind == kind).Select(row => checked((uint)row.Id)).Distinct().Order().ToList();

    private static bool ValidTag(int npcId, int tagId)
    {
        var monster = TableReaderV2.Parse<MonsterTable>().FirstOrDefault(row => row.NpcId.Contains(npcId));
        var inner = TableReaderV2.Parse<AscNet.Table.V2.client.archive.ArchiveMonsterInnerTable>()
            .FirstOrDefault(row => row.Id == monster?.Id);
        return inner is not null && TableReaderV2.Parse<TagTable>().Any(row => row.Id == tagId
            && row.TagGroupId.Contains(inner.TagGroupId));
    }

    public static bool ReconcileEquipment(Session session)
    {
        bool changed = false;
        session.player.ArchiveEquipment ??= new();
        foreach (var equip in session.character.Equips)
        {
            int id = checked((int)equip.TemplateId);
            var record = session.player.ArchiveEquipment.FirstOrDefault(row => row.Id == id);
            if (record is null)
            {
                record = new() { Id = id };
                session.player.ArchiveEquipment.Add(record);
                changed = true;
            }
            int level = Math.Max(record.Level, equip.Level);
            int breakthrough = Math.Max(record.Breakthrough, equip.Breakthrough);
            int resonance = Math.Max(record.ResonanceCount, equip.ResonanceInfo?.Count ?? 0);
            changed |= level != record.Level || breakthrough != record.Breakthrough || resonance != record.ResonanceCount;
            record.Level = level; record.Breakthrough = breakthrough; record.ResonanceCount = resonance;
        }
        return changed;
    }

    private static bool MonsterVisible(Player player, MonsterTable row) => row.UnlockType == 1
        && row.NpcId.Any(id => player.ArchiveMonsterKills.Keys.Any(killed => CanonicalNpc(killed) == CanonicalNpc(id))
            || TableReaderV2.Parse<SimulateTrainMonsterTable>().Any(practice => practice.NpcId.Any(npc => CanonicalNpc(npc) == CanonicalNpc(id))));

    private static bool Condition(Session session, int id, int parameter)
    {
        if (id == 0) return true;
        ConditionTable? row = TableReaderV2.Parse<ConditionTable>().FirstOrDefault(row => row.Id == id);
        if (row is null) return false;
        int target = row.Params.FirstOrDefault();
        var equips = session.player.ArchiveEquipment;
        var suitIds = TableReaderV2.Parse<EquipTable>().Where(equip => equip.SuitId == parameter).Select(equip => equip.Id).ToHashSet();
        return row.Type switch
        {
            30101 => session.player.ArchiveMonsterKills.Where(kill => CanonicalNpc(kill.Key) == CanonicalNpc(parameter)).Sum(kill => (long)kill.Value) >= target,
            31101 => equips.Any(equip => equip.Id == parameter),
            31102 => equips.Any(equip => equip.Id == parameter && equip.Level >= target),
            31103 => equips.Any(equip => equip.Id == parameter && equip.Breakthrough >= target),
            31104 => equips.Any(equip => suitIds.Contains(equip.Id) && equip.Level >= target),
            31105 => equips.Any(equip => suitIds.Contains(equip.Id) && equip.Breakthrough >= target),
            31106 => equips.Count(equip => suitIds.Contains(equip.Id)) >= target,
            _ => ArchiveCgModule.ConditionSatisfied(session, id, DateTimeOffset.UtcNow)
        };
    }

    private static int ValidateUnlock(Session session, string kind, int id)
    {
        switch (kind)
        {
            case "Monster":
                var monster = TableReaderV2.Parse<MonsterTable>().FirstOrDefault(row => row.Id == id);
                return monster is null ? 20065002 : MonsterVisible(session.player, monster) ? 0 : 20065004;
            case "MonsterInfo":
                var info = TableReaderV2.Parse<MonsterInfoTable>().FirstOrDefault(row => row.Id == id);
                return info is null ? 20065003 : Condition(session, info.Condition ?? 0, info.GroupId) ? 0 : 20065004;
            case "MonsterSkill":
                var skill = TableReaderV2.Parse<MonsterSkillTable>().FirstOrDefault(row => row.Id == id);
                return skill is null ? 20065003 : Condition(session, skill.Condition, skill.GroupId) ? 0 : 20065004;
            case "MonsterSetting":
                var setting = TableReaderV2.Parse<MonsterSettingTable>().FirstOrDefault(row => row.Id == id);
                return setting is null ? 20065003 : Condition(session, setting.Condition ?? 0, setting.GroupId) ? 0 : 20065004;
            case "Weapon":
                // Equip.Type is the weapon family, not the archive classification; the client classifies by Site.
                bool exists = TableReaderV2.Parse<EquipTable>().Any(row => row.Id == id && row.Site == 0);
                return !exists ? 20065003 : session.player.ArchiveEquipment.Any(row => row.Id == id) ? 0 : 20065004;
            case "Awareness":
                var suit = TableReaderV2.Parse<EquipTable>().Where(row => row.SuitId == id && row.Site is >= 1 and <= 6).Select(row => row.Id).ToHashSet();
                return suit.Count == 0 ? 20065003 : session.player.ArchiveEquipment.Any(row => suit.Contains(row.Id)) ? 0 : 20065004;
            case "WeaponSetting":
                var weapon = TableReaderV2.Parse<WeaponSettingTable>().FirstOrDefault(row => row.Id == id);
                return weapon is null ? 20065003 : weapon.Condition.All(condition => Condition(session, condition, weapon.EquipId)) ? 0 : 20065004;
            case "AwarenessSetting":
                var awareness = TableReaderV2.Parse<AwarenessSettingTable>().FirstOrDefault(row => row.Id == id);
                return awareness is null ? 20065003 : Condition(session, awareness.Condition, awareness.SuitId) ? 0 : 20065004;
            default: return 20065003;
        }
    }

    private static void Unlock(Session session, Packet.Request packet, string kind)
    {
        ArchiveIdsRequest request = packet.Deserialize<ArchiveIdsRequest>();
        ArchiveResponse response = new();
        if (request.Ids is null)
        {
            Respond(session, packet, new ArchiveResponse { Code = 20065003 });
            return;
        }
        bool changed = ReconcileEquipment(session);
        foreach (int id in request.Ids.Distinct())
        {
            bool unlocked = session.player.ArchiveUnlocks.Any(row => row.Kind == kind && row.Id == id);
            int code = unlocked ? 0 : ValidateUnlock(session, kind, id);
            if (code != 0) { response.Code = code; continue; }
            if (!unlocked) { session.player.ArchiveUnlocks.Add(new() { Kind = kind, Id = id }); changed = true; }
            response.SuccessIds.Add(id);
        }
        if (changed) session.player.Save();
        Respond(session, packet, response);
    }

    [RequestPacketHandler("UnlockArchiveMonsterRequest")]
    public static void Monster(Session session, Packet.Request packet) => Unlock(session, packet, "Monster");
    [RequestPacketHandler("UnlockMonsterInfoRequest")]
    public static void MonsterInfo(Session session, Packet.Request packet) => Unlock(session, packet, "MonsterInfo");
    [RequestPacketHandler("UnlockMonsterSkillRequest")]
    public static void MonsterSkill(Session session, Packet.Request packet) => Unlock(session, packet, "MonsterSkill");
    [RequestPacketHandler("UnlockMonsterSettingRequest")]
    public static void MonsterSetting(Session session, Packet.Request packet) => Unlock(session, packet, "MonsterSetting");
    [RequestPacketHandler("UnlockArchiveWeaponRequest")]
    public static void Weapon(Session session, Packet.Request packet) => Unlock(session, packet, "Weapon");
    [RequestPacketHandler("UnlockWeaponSettingRequest")]
    public static void WeaponSetting(Session session, Packet.Request packet) => Unlock(session, packet, "WeaponSetting");
    [RequestPacketHandler("UnlockArchiveAwarenessRequest")]
    public static void Awareness(Session session, Packet.Request packet) => Unlock(session, packet, "Awareness");
    [RequestPacketHandler("UnlockAwarenessSettingRequest")]
    public static void AwarenessSetting(Session session, Packet.Request packet) => Unlock(session, packet, "AwarenessSetting");

    private static bool ValidNpc(int id) => TableReaderV2.Parse<MonsterTable>().Any(row => row.NpcId.Contains(id));
    private static ArchiveEvaluationRecord Evaluation(Player player, int id)
    {
        var result = player.ArchiveEvaluations.FirstOrDefault(row => row.Type == 1 && row.Id == id);
        if (result is not null) return result;
        result = new() { Type = 1, Id = id };
        player.ArchiveEvaluations.Add(result);
        return result;
    }

    [RequestPacketHandler("ArchiveEvaluateRequest")]
    public static void Evaluate(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<ArchiveEvaluateRequest>();
        int code = request.Type != 1 ? 20065007 : !ValidNpc(request.Id) ? 20065001
            : request.Score is < 0 or > 5 || request.Difficulty is < 0 or > 5 ? 20065003
            : request.Tags is null || request.Tags.Count > 3 || request.Tags.Distinct().Count() != request.Tags.Count
                || request.Tags.Any(id => !ValidTag(request.Id, id)) ? 20065005 : 0;
        if (code == 0)
        {
            var record = Evaluation(session.player, request.Id);
            record.Score = request.Score; record.Difficulty = request.Difficulty; record.Tags = request.Tags!;
            session.player.Save();
        }
        Respond(session, packet, new ArchiveResponse { Code = code });
    }

    [RequestPacketHandler("ArchiveGiveLikeRequest")]
    public static void Like(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<ArchiveGiveLikeRequest>();
        ArchiveResponse response = new();
        if (request.Type != 1) response.Code = 20065007;
        else if (request.LikeList is null || request.LikeList.Select(row => row.Id).Distinct().Count() != request.LikeList.Count)
            response.Code = 20065003;
        else
        {
            foreach (var like in request.LikeList)
            {
                int code = !ValidNpc(like.Id) ? 20065001 : like.LikeStatus is < 0 or > 2 ? 20065003 : 0;
                if (code != 0) { response.Code = code; continue; }
                Evaluation(session.player, like.Id).LikeStatus = like.LikeStatus;
                response.SuccessIds.Add(like.Id);
            }
            if (response.SuccessIds.Count > 0) session.player.Save();
        }
        Respond(session, packet, response);
    }

    [RequestPacketHandler("GetEvaluateRequest")]
    public static void GetEvaluations(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<ArchiveIdsRequest>();
        ArchiveEvaluationsResponse response = new();
        if (request.Ids is null || request.Ids.Any(id => !ValidNpc(id))) response.Code = 20065001;
        else
        {
            // One current evaluation per player: edits and unlike replace, never accumulate votes.
            var players = Player.collection.Find(Builders<Player>.Filter.Empty).ToList();
            players.RemoveAll(player => player.PlayerData.Id == session.player.PlayerData.Id);
            players.Add(session.player);
            foreach (int id in request.Ids.Distinct())
            {
                var records = players.Select(player => player.ArchiveEvaluations.FirstOrDefault(row => row.Type == 1 && row.Id == id)).Where(row => row is not null).Select(row => row!).ToArray();
                response.Evaluates.Add(new()
                {
                    Id = id, Score = records.Sum(row => (long)row.Score), ScoreCount = records.Count(row => row.Score > 0),
                    Difficulty = records.Sum(row => (long)row.Difficulty), DifficultyCount = records.Count(row => row.Difficulty > 0),
                    LikeCount = records.Count(row => row.LikeStatus == 2), DislikeCount = records.Count(row => row.LikeStatus == 1),
                    Tags = records.SelectMany(row => row.Tags).GroupBy(tag => tag).OrderByDescending(group => group.Count()).ThenBy(group => group.Key)
                        .Select(group => new ArchiveTagCount { Id = group.Key, Count = group.Count() }).ToList()
                });
                var own = session.player.ArchiveEvaluations.FirstOrDefault(row => row.Type == 1 && row.Id == id);
                response.PersonalEvaluates.Add(new() { Id = id, Score = own?.Score ?? 0, Difficulty = own?.Difficulty ?? 0,
                    Tags = own?.Tags.ToList() ?? new(), LikeStatus = own?.LikeStatus ?? 0 });
            }
        }
        Respond(session, packet, response);
    }

    [RequestPacketHandler("GetStoryEvaluateRequest")]
    public static void StoryEvaluations(Session session, Packet.Request packet) =>
        Respond(session, packet, new ArchiveEvaluationsResponse { Code = 20065006 });
}
