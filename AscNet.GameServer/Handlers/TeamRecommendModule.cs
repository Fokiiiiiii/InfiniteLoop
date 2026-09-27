using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.teamrecommend;
using MessagePack;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public sealed class TeamRecommendCharacterRequest
{
    public int? CharacterId { get; set; }
}

[MessagePackObject(true)]
public sealed class TeamRecommendSetTargetRequest
{
    public int? CharacterId { get; set; }
    public int? BaseCfgId { get; set; }
}

[MessagePackObject(true)]
public sealed class TeamRecommendSetFormationTargetRequest
{
    public int? CharacterId { get; set; }
    public int? TeamCfgId { get; set; }
    public int? SourceType { get; set; }
    public int? SourceId { get; set; }
    public TeamRecommendFormationData? TargetFormation { get; set; }
}

[MessagePackObject(true)]
public sealed class TeamRecommendFormationRequest
{
    public int? CharacterId { get; set; }
    public List<int>? TeamCfgIds { get; set; }
}

[MessagePackObject(true)]
public sealed class TeamRecommendResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class TeamRecommendFormationResponse
{
    public int Code { get; set; }
    public Dictionary<int, TeamRecommendFormationData> TeamRecommendFormations { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class TeamRecommendTargetResponse
{
    public int Code { get; set; }
    public TeamRecommendTargetState? Target { get; set; }
}

[MessagePackObject(true)]
public sealed class TeamRecommendTargetsDb
{
    public Dictionary<int, TeamRecommendTargetState> CharacterTargets { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class TeamRecommendGetAllTargetsResponse
{
    public int Code { get; set; }
    public TeamRecommendTargetsDb CharacterTargets { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyTeamRecommendTargetData
{
    public TeamRecommendTargetsDb CharacterTargets { get; set; } = new();
}

internal static class TeamRecommendModule
{
    private const int Invalid = 1;
    private const int CharacterMissing = 20009011;
    private static void Reply<T>(Session session, Packet.Request packet, T response) =>
        session.SendResponse(packet.Name[..^7] + "Response", MessagePackSerializer.Serialize(response), packet.Id);


    private static bool Owns(Session session, int? characterId) => characterId is > 0
        && session.character.Characters.Any(character => character.Id == characterId.Value);

    private static bool HasSpace(Session session, int characterId)
    {
        if (session.character.TeamRecommendTargets.ContainsKey(characterId))
            return true;
        int limit = TableReaderV2.Parse<TeamRecommendConfigTable>()
            .Single(row => row.Key == "CharacterTargetLimit").Values[0];
        return session.character.TeamRecommendTargets.Count < limit;
    }

    private static TeamRecommendTargetsDb Snapshot(Session session) => new()
    {
        CharacterTargets = new Dictionary<int, TeamRecommendTargetState>(session.character.TeamRecommendTargets)
    };

    internal static void SendLoginState(Session session) => session.SendPush(new NotifyTeamRecommendTargetData
    {
        CharacterTargets = Snapshot(session)
    });

    private static bool IsBaseCharacter(int baseCharacterId, int characterId) =>
        TableReaderV2.Parse<TeamRecommendBaseCharacterTable>()
            .Any(row => row.Id == baseCharacterId && row.CharacterId == characterId);

    private static bool HasConfiguredCharacter(int baseCharacterId, int characterId) =>
        IsBaseCharacter(baseCharacterId, characterId)
        && TableReaderV2.Parse<TeamRecommendCharacterTargetTable>()
            .Any(row => row.CharacterId == characterId && row.BaseCharacterIds.Contains(baseCharacterId));

    private static void SaveTarget(Session session, int characterId, TeamRecommendTargetState? next)
    {
        bool hadOld = session.character.TeamRecommendTargets.TryGetValue(characterId, out TeamRecommendTargetState? old);
        if (hadOld && next is not null && old!.BaseCharacterId == next.BaseCharacterId
            && old.TeamCfgId == next.TeamCfgId && old.BaseFormationId == next.BaseFormationId
            && old.TargetFormation is null && next.TargetFormation is null)
            return;
        bool hadEvent = session.character.TeamRecommendFinishEvents.Remove(characterId);
        if (next is null) session.character.TeamRecommendTargets.Remove(characterId);
        else session.character.TeamRecommendTargets[characterId] = next;
        try { session.character.SaveChecked(); }
        catch
        {
            if (hadOld) session.character.TeamRecommendTargets[characterId] = old!;
            else session.character.TeamRecommendTargets.Remove(characterId);
            if (hadEvent) session.character.TeamRecommendFinishEvents.Add(characterId);
            throw;
        }
    }

    [RequestPacketHandler("TeamRecommendSetTargetRequest")]
    public static void SetTarget(Session session, Packet.Request packet)
    {
        TeamRecommendSetTargetRequest request = packet.Deserialize<TeamRecommendSetTargetRequest>();
        TeamRecommendTargetResponse response = new();
        if (!Owns(session, request.CharacterId)) response.Code = CharacterMissing;
        else if (request.BaseCfgId is not > 0 || !HasSpace(session, request.CharacterId!.Value)
            || !HasConfiguredCharacter(request.BaseCfgId.Value, request.CharacterId.Value)) response.Code = Invalid;
        else
        {
            int characterId = request.CharacterId!.Value;
            response.Target = new TeamRecommendTargetState { BaseCharacterId = request.BaseCfgId!.Value };
            SaveTarget(session, characterId, response.Target);
        }
        Reply(session, packet, response);
    }

    [RequestPacketHandler("TeamRecommendSetFormationTargetRequest")]
    public static void SetFormationTarget(Session session, Packet.Request packet)
    {
        TeamRecommendSetFormationTargetRequest request = packet.Deserialize<TeamRecommendSetFormationTargetRequest>();
        TeamRecommendTargetResponse response = new();
        if (!Owns(session, request.CharacterId)) response.Code = CharacterMissing;
        else if (request.TeamCfgId is not > 0 || request.SourceId is not > 0
            || !HasSpace(session, request.CharacterId!.Value)
            || !TableReaderV2.Parse<TeamRecommendFormationTable>().Any(row => row.Id == request.TeamCfgId.Value))
            response.Code = Invalid;
        else if (request.SourceType == 1)
        {
            TeamRecommendBaseFormationTable? formation = TableReaderV2.Parse<TeamRecommendBaseFormationTable>()
                .Find(row => row.Id == request.SourceId.Value && row.FormationId == request.TeamCfgId.Value);
            if (formation is null || !formation.BaseCharacterIds.Any(id => IsBaseCharacter(id, request.CharacterId!.Value)))
                response.Code = Invalid;
            else
                response.Target = new TeamRecommendTargetState
                {
                    TeamCfgId = request.TeamCfgId.Value,
                    BaseFormationId = formation.Id
                };
        }
        else if (request.SourceType == 3 && session.character.TeamRecommendTargets.TryGetValue(
            request.SourceId.Value, out TeamRecommendTargetState? source) && source.TeamCfgId == request.TeamCfgId
            && (source.BaseFormationId > 0 || source.TargetFormation is not null))
        {
            TeamRecommendBaseFormationTable? formation = TableReaderV2.Parse<TeamRecommendBaseFormationTable>()
                .Find(row => row.Id == source.BaseFormationId && row.FormationId == source.TeamCfgId);
            if (source.TargetFormation is not null)
                response.Target = source.TargetFormation.CharacterDatas.Any(row => row.CharacterId == request.CharacterId)
                    ? new TeamRecommendTargetState { TeamCfgId = source.TeamCfgId, TargetFormation = source.TargetFormation }
                    : null;
            else
                response.Target = formation is not null && formation.BaseCharacterIds.Any(id => IsBaseCharacter(id, request.CharacterId!.Value))
                    ? new TeamRecommendTargetState { TeamCfgId = source.TeamCfgId, BaseFormationId = source.BaseFormationId }
                    : null;
            if (response.Target is null) response.Code = Invalid;
        }
        else if (request.SourceType == 2 && request.TargetFormation is not null)
        {
            // The client echoes a snapshot it received from TeamRecommendFormationRequest; only the
            // server's current top snapshot for that source character is accepted, never client data.
            TeamRecommendFormationData? top = TopFormations(request.SourceId.Value, [request.TeamCfgId.Value])
                .GetValueOrDefault(request.TeamCfgId.Value);
            if (top is not null && SameFormation(top, request.TargetFormation)
                && top.CharacterDatas.Any(row => row.CharacterId == request.CharacterId))
                response.Target = new TeamRecommendTargetState { TeamCfgId = request.TeamCfgId.Value, TargetFormation = top };
            else response.Code = Invalid;
        }
        else response.Code = Invalid;
        if (response.Target is not null)
            SaveTarget(session, request.CharacterId!.Value, response.Target);
        Reply(session, packet, response);
    }

    [RequestPacketHandler("TeamRecommendGetAllTargetsRequest")]
    public static void GetAllTargets(Session session, Packet.Request packet) => Reply(session, packet,
        new TeamRecommendGetAllTargetsResponse { CharacterTargets = Snapshot(session) });

    [RequestPacketHandler("TeamRecommendDeleteTargetRequest")]
    public static void DeleteTarget(Session session, Packet.Request packet)
    {
        TeamRecommendCharacterRequest request = packet.Deserialize<TeamRecommendCharacterRequest>();
        int code = !Owns(session, request.CharacterId) ? CharacterMissing
            : !session.character.TeamRecommendTargets.ContainsKey(request.CharacterId!.Value) ? Invalid : 0;
        if (code == 0)
            SaveTarget(session, request.CharacterId!.Value, null);
        Reply(session, packet, new TeamRecommendResponse { Code = code });
    }

    [RequestPacketHandler("TeamRecommendFormationRequest")]
    public static void Formation(Session session, Packet.Request packet)
    {
        TeamRecommendFormationRequest request = packet.Deserialize<TeamRecommendFormationRequest>();
        TeamRecommendFormationResponse response = new();
        List<TeamRecommendFormationTable> formations = TableReaderV2.Parse<TeamRecommendFormationTable>();
        if (request.CharacterId is not > 0 || request.TeamCfgIds is not { Count: > 0 }
            || request.TeamCfgIds.Any(id => !formations.Any(row => row.Id == id)))
            response.Code = Invalid;
        else response.TeamRecommendFormations = TopFormations(request.CharacterId.Value, request.TeamCfgIds);
        Reply(session, packet, response);
    }

    // AscNet policy, not retail parity: "global" standings are this server's persisted accounts.
    // A team is the authored BaseFormation (FormationId, entry CharacterId) roster; an account is
    // eligible when it owns all three members inside the Formation's authored quality-star window,
    // each wearing exactly one table-valid weapon and six awareness sites. Rank: summed quality-star,
    // then summed character level, then lowest UID. Snapshots expose only template ids, never UID/name.
    // Missing teams are omitted so the client falls back to its authored configuration.
    internal static Dictionary<int, TeamRecommendFormationData> TopFormations(int characterId, IEnumerable<int> teamCfgIds)
    {
        Dictionary<int, TeamRecommendFormationData> result = new();
        List<AscNet.Common.Database.Character>? players = null;
        List<EquipTable> templates = TableReaderV2.Parse<EquipTable>();
        foreach (int teamCfgId in teamCfgIds.Distinct())
        {
            TeamRecommendFormationTable? formation = TableReaderV2.Parse<TeamRecommendFormationTable>()
                .Find(row => row.Id == teamCfgId);
            TeamRecommendBaseFormationTable? roster = TableReaderV2.Parse<TeamRecommendBaseFormationTable>()
                .Find(row => row.FormationId == teamCfgId && row.CharacterId == characterId);
            if (formation is null || roster is null) continue;
            List<int> members = roster.BaseCharacterIds.Select(id => TableReaderV2.Parse<TeamRecommendBaseCharacterTable>()
                .Find(row => row.Id == id)?.CharacterId ?? 0).ToList();
            if (members.Count != 3 || members.Any(id => id <= 0) || members.Distinct().Count() != 3) continue;
            // ponytail: full account scan per request; add a Mongo projection/index if accounts grow large.
            players ??= AscNet.Common.Database.Character.collection
                .Find(FilterDefinition<AscNet.Common.Database.Character>.Empty).ToList();
            var best = players
                .Select(player => (player.Uid, Build: BuildSnapshot(player, members, formation, templates)))
                .Where(row => row.Build is not null)
                .OrderByDescending(row => row.Build!.Value.Grade)
                .ThenByDescending(row => row.Build!.Value.Level)
                .ThenBy(row => row.Uid)
                .FirstOrDefault();
            if (best.Build is not null) result[teamCfgId] = best.Build.Value.Data;
        }
        return result;
    }

    private static (TeamRecommendFormationData Data, int Grade, int Level)? BuildSnapshot(
        AscNet.Common.Database.Character player, List<int> members, TeamRecommendFormationTable formation,
        List<EquipTable> templates)
    {
        TeamRecommendFormationData data = new();
        int grade = 0, level = 0;
        foreach (int id in members)
        {
            CharacterData? owned = player.Characters.FirstOrDefault(row => row.Id == (uint)id);
            if (owned is null) return null;
            int qualityStar = owned.Quality * 1000 + owned.Star;
            if (qualityStar < formation.MinCharacterQualityStar || qualityStar > formation.MaxCharacterQualityStar)
                return null;
            List<EquipData> worn = new();
            for (int site = 0; site <= 6; site++)
            {
                // Null for missing, duplicated, or dangling (template not in the equip table) gear.
                EquipData? equip = TeamRecommendProgress.Worn(player, id, site, templates, out _);
                if (equip is null) return null;
                worn.Add(equip);
            }
            List<PartnerData> partners = player.Partners.Where(row => row.CharacterId == id).ToList();
            if (partners.Count > 1) return null;
            bool Bound(ResonanceInfo info) => info.TemplateId > 0 && (info.CharacterId == 0 || info.CharacterId == id);
            TeamRecommendCharacterData entry = new()
            {
                CharacterId = id,
                CharacterQualityStar = qualityStar,
                WeaponId = (int)worn[0].TemplateId,
                WeaponOverrunChoseSuit = worn[0].WeaponOverrunData?.ChoseSuit ?? 0,
                WeaponResonanceDatas = (worn[0].ResonanceInfo ?? []).Where(r => Bound(r) && r.Slot is >= 1 and <= 3)
                    .GroupBy(r => r.Slot).OrderBy(g => g.Key).Select(g => g.First())
                    .Select(r => new TeamRecommendResonanceData { Slot = r.Slot, Type = (int)r.Type, TemplateId = r.TemplateId })
                    .ToList(),
                EquipIds = worn.Skip(1).Select(equip => (int)equip.TemplateId).ToList(),
                PartnerId = partners.FirstOrDefault()?.TemplateId ?? 0
            };
            // Site-major, two slots per site with empty placeholders: the client maps the Nth entry of
            // each slot to the Nth awareness site.
            foreach (EquipData equip in worn.Skip(1))
                for (int slot = 1; slot <= 2; slot++)
                {
                    ResonanceInfo? resonance = (equip.ResonanceInfo ?? []).FirstOrDefault(r => Bound(r) && r.Slot == slot);
                    entry.EquipResonanceDatas.Add(new TeamRecommendResonanceData
                    {
                        Slot = slot, Type = (int)(resonance?.Type ?? 0), TemplateId = resonance?.TemplateId ?? 0
                    });
                }
            data.CharacterDatas.Add(entry);
            grade += qualityStar;
            level += owned.Level;
        }
        return (data, grade, level);
    }

    // The client may reorder CharacterDatas locally; member content must match exactly.
    private static bool SameFormation(TeamRecommendFormationData a, TeamRecommendFormationData b) =>
        a.CharacterDatas.Select(row => Convert.ToBase64String(MessagePackSerializer.Serialize(row))).Order()
            .SequenceEqual(b.CharacterDatas.Select(row => Convert.ToBase64String(MessagePackSerializer.Serialize(row))).Order());

    [RequestPacketHandler("TeamRecommendFinishTargetRequest")]
    public static void FinishTarget(Session session, Packet.Request packet)
    {
        TeamRecommendCharacterRequest request = packet.Deserialize<TeamRecommendCharacterRequest>();
        int code = !Owns(session, request.CharacterId) ? CharacterMissing : Invalid;
        if (code == Invalid && session.character.TeamRecommendTargets.TryGetValue(
            request.CharacterId!.Value, out TeamRecommendTargetState? target)
            && TeamRecommendProgress.Calculate(session, request.CharacterId.Value, target) is >= 1)
        {
            SaveTarget(session, request.CharacterId.Value, null);
            code = 0;
        }
        Reply(session, packet, new TeamRecommendResponse { Code = code });
    }

    [RequestPacketHandler("TeamRecommendFinishTargetEventRequest")]
    public static void FinishTargetEvent(Session session, Packet.Request packet)
    {
        TeamRecommendCharacterRequest request = packet.Deserialize<TeamRecommendCharacterRequest>();
        int code = !Owns(session, request.CharacterId) ? CharacterMissing : Invalid;
        if (code == Invalid && session.character.TeamRecommendTargets.TryGetValue(
            request.CharacterId!.Value, out TeamRecommendTargetState? target))
        {
            double? progress = TeamRecommendProgress.Calculate(session, request.CharacterId.Value, target);
            double threshold = TableReaderV2.Parse<TeamRecommendConfigTable>()
                .Single(row => row.Key == "TargetFinishPercentage").Values[0];
            if (progress * 100 >= threshold)
            {
                code = 0;
                if (session.character.TeamRecommendFinishEvents.Add(request.CharacterId.Value))
                {
                    try { session.character.SaveChecked(); }
                    catch
                    {
                        session.character.TeamRecommendFinishEvents.Remove(request.CharacterId.Value);
                        throw;
                    }
                }
            }
        }
        Reply(session, packet, new TeamRecommendResponse { Code = code });
    }
}
