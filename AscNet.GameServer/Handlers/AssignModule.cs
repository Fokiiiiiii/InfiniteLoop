using AscNet.Common.Database;
using AscNet.GameServer.Game;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.assign;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class AssignGetDataRequest { }
[MessagePackObject(true)] public sealed class AssignGetDataResponse { public AssignInfo AssignInfo { get; set; } = new(); }
[MessagePackObject(true)] public sealed class AssignSetCharacterRequest { public int ChapterId { get; set; } public long CharacterId { get; set; } }
[MessagePackObject(true)] public sealed class AssignSetCharacterResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class AssignSetTeamRequest { public int GroupId { get; set; } public List<List<long>> TeamList { get; set; } = new(); public List<int> FirstFightPosList { get; set; } = new(); public List<int> CaptainPosList { get; set; } = new(); }
[MessagePackObject(true)] public sealed class AssignSetTeamResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class AssignResetStageRequest { public int GroupId { get; set; } public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class AssignResetStageResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class AssignGetRewardRequest { public int ChapterId { get; set; } }
[MessagePackObject(true)] public sealed class AssignGetRewardResponse { public int Code { get; set; } public List<RewardGoods> RewardList { get; set; } = new(); }
[MessagePackObject(true)] public sealed class AssignInfo { public List<AssignChapterInfo> ChapterRecords { get; set; } = new(); public List<AssignGroupInfo> GroupRecords { get; set; } = new(); public List<AssignGroupTeamInfo> GroupTeamRecords { get; set; } = new(); }
[MessagePackObject(true)] public sealed class AssignChapterInfo { public int ChapterId { get; set; } public long CharacterId { get; set; } public bool IsGetReward { get; set; } }
[MessagePackObject(true)] public sealed class AssignGroupInfo { public int GroupId { get; set; } public int Count { get; set; } public bool IsPerfect { get; set; } public List<int> FinishStageIds { get; set; } = new(); }
[MessagePackObject(true)] public sealed class AssignGroupTeamInfo { public int GroupId { get; set; } public List<List<long>> TeamInfoList { get; set; } = new(); public List<int> CaptainPosList { get; set; } = new(); public List<int> FirstFightPosList { get; set; } = new(); }

internal static class AssignModule
{
    internal const int GroupMissing = 20182001, StageMissing = 20182002, StageFinished = 20182003, TeamUnset = 20182004, FightCardCount = 20182005, FightCardMismatch = 20182006, TeamListEmpty = 20182007, TeamCountMismatch = 20182008, DuplicateMember = 20182009, CharacterMissing = 20182010, CharacterOccupied = 20182011, SettleStageMismatch = 20182012, ChapterUnfinished = 20182013, ResetUnfinished = 20182014, FinishedTeamImmutable = 20182015, RewardClaimed = 20182016, RewardMissing = 20182017;
    internal const int PreGroupUnfinished = 20003140;

    private sealed record GroupData(AssignGroupTable Config, int ChapterId);
    private sealed record Data(
        IReadOnlyList<AssignChapterTable> Chapters,
        IReadOnlyDictionary<int, AssignChapterTable> ByChapter,
        IReadOnlyList<GroupData> Groups,
        IReadOnlyDictionary<int, GroupData> ByGroup,
        IReadOnlyDictionary<int, GroupData> ByStage,
        IReadOnlyDictionary<int, AssignTeamInfoTable> Teams);

    private static readonly Lazy<Data> Runtime = new(() =>
    {
        List<AssignChapterTable> chapters = TableReaderV2.Parse<AssignChapterTable>()
            .OrderBy(chapter => chapter.ChapterId)
            .ToList();
        Dictionary<int, AssignGroupTable> groupConfigs = TableReaderV2.Parse<AssignGroupTable>()
            .ToDictionary(group => group.GroupId);
        List<GroupData> groups = chapters
            .SelectMany(chapter => chapter.GroupId.Select(groupId => new GroupData(groupConfigs[groupId], chapter.ChapterId)))
            .ToList();
        return new Data(
            chapters,
            chapters.ToDictionary(chapter => chapter.ChapterId),
            groups,
            groups.ToDictionary(group => group.Config.GroupId),
            groups.SelectMany(group => group.Config.StageId.Select(stageId => (stageId, group)))
                .ToDictionary(entry => entry.stageId, entry => entry.group),
            TableReaderV2.Parse<AssignTeamInfoTable>().ToDictionary(team => team.Id));
    });

    internal static AssignInfo BuildAssignInfo(Player player)
    {
        AssignState state = player.Assign ??= new();
        return new AssignInfo
        {
            ChapterRecords = Runtime.Value.Chapters
                .Select(chapter => state.Chapters.FirstOrDefault(record => record.ChapterId == chapter.ChapterId))
                .OfType<AssignChapterState>()
                .Select(ToChapterInfo)
                .ToList(),
            GroupRecords = Runtime.Value.Groups.Select(group =>
            {
                AssignGroupState? record = state.Groups.FirstOrDefault(saved => saved.GroupId == group.Config.GroupId);
                return new AssignGroupInfo
                {
                    GroupId = group.Config.GroupId,
                    Count = record?.Count ?? 0,
                    IsPerfect = record?.IsPerfect ?? false,
                    FinishStageIds = record?.FinishStageIds.Where(group.Config.StageId.Contains).ToList() ?? []
                };
            }).ToList(),
            GroupTeamRecords = Runtime.Value.Groups.Select(group => state.Teams.FirstOrDefault(saved => saved.GroupId == group.Config.GroupId))
                .OfType<AssignTeamState>()
                .Select(team => new AssignGroupTeamInfo
                {
                    GroupId = team.GroupId,
                    TeamInfoList = team.TeamInfoList.Select(members => members.ToList()).ToList(),
                    CaptainPosList = team.CaptainPosList.ToList(),
                    FirstFightPosList = team.FirstFightPosList.ToList()
                }).ToList()
        };
    }

    internal static List<dynamic> BuildLoginChapterRecords(Session session)
    {
        AssignState state = session.player.Assign ??= new();
        return Runtime.Value.Chapters
            .Select(chapter => state.Chapters.FirstOrDefault(record => record.ChapterId == chapter.ChapterId))
            .OfType<AssignChapterState>()
            .Select(record => (dynamic)ToChapterInfo(record))
            .ToList();
    }

    [RequestPacketHandler("AssignGetDataRequest")]
    public static void AssignGetDataRequestHandler(Session session, Packet.Request packet) =>
        session.SendResponse(new AssignGetDataResponse { AssignInfo = BuildAssignInfo(session.player) }, packet.Id);

    [RequestPacketHandler("AssignSetTeamRequest")]
    public static void AssignSetTeamRequestHandler(Session session, Packet.Request packet)
    {
        AssignSetTeamRequest request = packet.Deserialize<AssignSetTeamRequest>();
        if (!Runtime.Value.ByGroup.TryGetValue(request.GroupId, out GroupData? group)) { SendTeam(session, packet, GroupMissing); return; }
        if (request.TeamList.Count == 0) { SendTeam(session, packet, TeamListEmpty); return; }
        if (request.TeamList.Count != group.Config.TeamInfoId.Count || request.CaptainPosList.Count != request.TeamList.Count || request.FirstFightPosList.Count != request.TeamList.Count) { SendTeam(session, packet, TeamCountMismatch); return; }

        AssignState state = session.player.Assign ??= new();
        if (!IsUnlocked(state, group)) { SendTeam(session, packet, PreGroupUnfinished); return; }
        AssignGroupState progress = Group(state, request.GroupId);
        AssignTeamState? existing = state.Teams.FirstOrDefault(team => team.GroupId == request.GroupId);
        for (int index = 0; index < request.TeamList.Count; index++)
        {
            if (!Runtime.Value.Teams.TryGetValue(group.Config.TeamInfoId[index], out AssignTeamInfoTable? config)) { SendTeam(session, packet, GroupMissing); return; }
            List<long> members = request.TeamList[index];
            if (members.Count != config.NeedCharacter || members.Any(characterId => characterId <= 0) || members.Distinct().Count() != members.Count || members.Any(characterId => session.character.Characters.All(character => character.Id != characterId))) { SendTeam(session, packet, DuplicateMember); return; }
            if (request.CaptainPosList[index] < 1 || request.FirstFightPosList[index] < 1 || request.CaptainPosList[index] > members.Count || request.FirstFightPosList[index] > members.Count) { SendTeam(session, packet, TeamCountMismatch); return; }
            if (progress.FinishStageIds.Contains(group.Config.StageId[index]) && (existing is null || existing.TeamInfoList.Count <= index || !existing.TeamInfoList[index].SequenceEqual(members) || existing.CaptainPosList.Count <= index || existing.CaptainPosList[index] != request.CaptainPosList[index] || existing.FirstFightPosList.Count <= index || existing.FirstFightPosList[index] != request.FirstFightPosList[index])) { SendTeam(session, packet, FinishedTeamImmutable); return; }
        }
        if (request.TeamList.SelectMany(members => members).Distinct().Count() != request.TeamList.Sum(members => members.Count)) { SendTeam(session, packet, DuplicateMember); return; }

        AssignTeamState target = Team(state, request.GroupId);
        target.TeamInfoList = request.TeamList.Select(members => members.ToList()).ToList();
        target.CaptainPosList = request.CaptainPosList.ToList();
        target.FirstFightPosList = request.FirstFightPosList.ToList();
        session.player.Save();
        SendTeam(session, packet);
    }

    [RequestPacketHandler("AssignSetCharacterRequest")]
    public static void AssignSetCharacterRequestHandler(Session session, Packet.Request packet)
    {
        AssignSetCharacterRequest request = packet.Deserialize<AssignSetCharacterRequest>();
        if (!Runtime.Value.ByChapter.TryGetValue(request.ChapterId, out AssignChapterTable? chapter)) { SendCharacter(session, packet, GroupMissing); return; }
        AssignState state = session.player.Assign ??= new();
        bool completed = chapter.GroupId.All(groupId => state.Groups.Any(group => group.GroupId == groupId && group.Count > 0));
        if (!completed) { SendCharacter(session, packet, ChapterUnfinished); return; }
        if (request.CharacterId == 0)
        {
            AssignChapterState? own = state.Chapters.FirstOrDefault(record => record.ChapterId == request.ChapterId);
            if (own is null) { SendCharacter(session, packet, CharacterMissing); return; }
            if (own.CharacterId != 0)
            {
                own.CharacterId = 0;
                session.player.Save();
                List<CharacterData> clampedCharacters = CharacterPower.ClampCharacterSkillLevelsForClient(session);
                if (clampedCharacters.Count > 0)
                {
                    session.character.Save();
                    NotifyCharacterDataList characterUpdate = new();
                    characterUpdate.CharacterDataList.AddRange(clampedCharacters);
                    session.SendPush(characterUpdate);
                }
            }
            SendCharacter(session, packet);
            return;
        }
        CharacterData? character = session.character.Characters.FirstOrDefault(character => character.Id == request.CharacterId);
        if (character is null) { SendCharacter(session, packet, CharacterMissing); return; }
        if (!ExhibitionModule.MeetsCharacterConditions(session, character, chapter.SelectCharCondition)) { SendCharacter(session, packet, ChapterUnfinished); return; }
        if (state.Chapters.Any(record => record.ChapterId != request.ChapterId && record.CharacterId == request.CharacterId)) { SendCharacter(session, packet, CharacterOccupied); return; }
        Chapter(state, request.ChapterId).CharacterId = request.CharacterId;
        session.player.Save();
        List<CharacterData> adjustedCharacters = CharacterPower.ClampCharacterSkillLevelsForClient(session);
        if (adjustedCharacters.Count > 0)
        {
            session.character.Save();
            NotifyCharacterDataList characterUpdate = new();
            characterUpdate.CharacterDataList.AddRange(adjustedCharacters);
            session.SendPush(characterUpdate);
        }
        SendCharacter(session, packet);
    }

    [RequestPacketHandler("AssignResetStageRequest")]
    public static void AssignResetStageRequestHandler(Session session, Packet.Request packet)
    {
        AssignResetStageRequest request = packet.Deserialize<AssignResetStageRequest>();
        if (!Runtime.Value.ByGroup.TryGetValue(request.GroupId, out GroupData? group)) { SendReset(session, packet, GroupMissing); return; }
        if (!group.Config.StageId.Contains(request.StageId)) { SendReset(session, packet, StageMissing); return; }
        AssignGroupState? progress = session.player.Assign?.Groups.FirstOrDefault(saved => saved.GroupId == request.GroupId);
        if (progress is null || progress.Count > 0 || !progress.FinishStageIds.Remove(request.StageId)) { SendReset(session, packet, ResetUnfinished); return; }
        session.player.Save();
        SendReset(session, packet);
    }

    [RequestPacketHandler("AssignGetRewardRequest")]
    public static void AssignGetRewardRequestHandler(Session session, Packet.Request packet)
    {
        AssignGetRewardRequest request = packet.Deserialize<AssignGetRewardRequest>();
        if (!Runtime.Value.ByChapter.TryGetValue(request.ChapterId, out AssignChapterTable? chapter)) { SendReward(session, packet, GroupMissing); return; }
        AssignState state = session.player.Assign ??= new();
        if (!chapter.GroupId.All(groupId => state.Groups.Any(group => group.GroupId == groupId && group.Count > 0))) { SendReward(session, packet, ChapterUnfinished); return; }
        AssignChapterState chapterState = Chapter(state, request.ChapterId);
        if (chapterState.IsGetReward) { SendReward(session, packet, RewardClaimed); return; }
        List<AscNet.Table.V2.share.reward.RewardGoodsTable> configured = RewardHandler.GetRewardGoods(chapter.RewardId);
        if (configured.Count == 0) { SendReward(session, packet, RewardMissing); return; }
        RewardApplicationResult application;
        try
        {
            application = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant($"assign-chapter:{request.ChapterId}", configured)],
                session);
            chapterState.IsGetReward = true;
            session.player.SaveChecked();
        }
        catch
        {
            chapterState.IsGetReward = false;
            throw;
        }
        application.SendPushes(session);
        session.SendResponse(new AssignGetRewardResponse { Code = 0, RewardList = application.RewardGoods }, packet.Id);
    }

    internal static bool ApplyPreFight(Session session, PreFightRequest.PreFightRequestPreFightData data, out int code)
    {
        code = 0;
        if (!Runtime.Value.ByStage.TryGetValue((int)data.StageId, out GroupData? group)) return false;
        AssignState state = session.player.Assign ??= new();
        if (!IsUnlocked(state, group)) { code = PreGroupUnfinished; return true; }
        AssignGroupState progress = Group(state, group.Config.GroupId);
        if (progress.Count > 0 || progress.FinishStageIds.Contains((int)data.StageId)) { code = StageFinished; return true; }
        int index = group.Config.StageId.FindIndex(stageId => stageId == (int)data.StageId);
        int current = group.Config.StageId.FindIndex(stageId => !progress.FinishStageIds.Contains(stageId));
        if (index < 0 || index != current) { code = StageMissing; return true; }
        AssignTeamState? team = state.Teams.FirstOrDefault(saved => saved.GroupId == group.Config.GroupId);
        if (team is null || team.TeamInfoList.Count != group.Config.TeamInfoId.Count || team.TeamInfoList[index].Count == 0 || team.TeamInfoList[index].Any(characterId => session.character.Characters.All(character => character.Id != characterId))) { code = TeamUnset; return true; }
        IReadOnlyList<uint> cards = data.CardIds ?? [];
        if (cards.Count != team.TeamInfoList[index].Count) { code = FightCardCount; return true; }
        if (!cards.Select(cardId => (long)cardId).SequenceEqual(team.TeamInfoList[index])) { code = FightCardMismatch; return true; }
        return true;
    }

    internal static bool TrySettle(Session session, FightSettleResult result, out FightSettleResponse response)
    {
        response = null!;
        if (!Runtime.Value.ByStage.TryGetValue((int)result.StageId, out GroupData? group)) return false;
        if (!result.IsWin || result.IsForceExit)
        {
            response = BuildSettleResponse(result, false);
            return true;
        }
        AssignState state = session.player.Assign ??= new();
        AssignGroupState progress = Group(state, group.Config.GroupId);
        int index = group.Config.StageId.FindIndex(stageId => stageId == (int)result.StageId);
        int current = group.Config.StageId.FindIndex(stageId => !progress.FinishStageIds.Contains(stageId));
        PreFightRequest.PreFightRequestPreFightData? preFight = session.fight?.PreFight.PreFightData;
        if (progress.Count > 0 || index != current || preFight is null || preFight.StageId != result.StageId || !ApplyPreFight(session, preFight, out int preFightCode) || preFightCode != 0)
        {
            response = new FightSettleResponse { Code = SettleStageMismatch };
            return true;
        }
        progress.FinishStageIds.Add((int)result.StageId);
        if (group.Config.StageId.All(progress.FinishStageIds.Contains))
        {
            progress.Count++;
            progress.IsPerfect = true;
            progress.FinishStageIds.Clear();
            if (Runtime.Value.ByChapter[group.ChapterId].GroupId.All(groupId => state.Groups.Any(saved => saved.GroupId == groupId && saved.Count > 0)))
                Chapter(state, group.ChapterId);
        }
        session.player.Save();
        response = BuildSettleResponse(result, true);
        return true;
    }

    private static FightSettleResponse BuildSettleResponse(FightSettleResult result, bool isWin) => new()
    {
        Code = 0,
        Settle = new FightSettleResponse.FightSettleResponseSettle
        {
            IsWin = isWin,
            StageId = result.StageId,
            LeftTime = checked((int)result.LeftTime),
            NpcHpInfo = result.NpcHpInfo,
            RewardGoodsList = [],
            MultiRewardGoodsList = [],
            ChallengeCount = 0
        }
    };

    private static AssignChapterInfo ToChapterInfo(AssignChapterState state) => new() { ChapterId = state.ChapterId, CharacterId = state.CharacterId, IsGetReward = state.IsGetReward };
    private static bool IsUnlocked(AssignState state, GroupData group) =>
        group.Config.PreGroupId is not int predecessorId
        || predecessorId <= 0
        || state.Groups.Any(saved => saved.GroupId == predecessorId && saved.Count > 0);
    private static AssignChapterState Chapter(AssignState state, int id) => state.Chapters.FirstOrDefault(record => record.ChapterId == id) ?? Add(state.Chapters, new AssignChapterState { ChapterId = id });
    private static AssignGroupState Group(AssignState state, int id) => state.Groups.FirstOrDefault(record => record.GroupId == id) ?? Add(state.Groups, new AssignGroupState { GroupId = id });
    private static AssignTeamState Team(AssignState state, int id) => state.Teams.FirstOrDefault(record => record.GroupId == id) ?? Add(state.Teams, new AssignTeamState { GroupId = id });
    private static T Add<T>(List<T> records, T record) { records.Add(record); return record; }
    private static void SendCharacter(Session session, Packet.Request packet, int code = 0) => session.SendResponse(new AssignSetCharacterResponse { Code = code }, packet.Id);
    private static void SendTeam(Session session, Packet.Request packet, int code = 0) => session.SendResponse(new AssignSetTeamResponse { Code = code }, packet.Id);
    private static void SendReset(Session session, Packet.Request packet, int code = 0) => session.SendResponse(new AssignResetStageResponse { Code = code }, packet.Id);
    private static void SendReward(Session session, Packet.Request packet, int code) => session.SendResponse(new AssignGetRewardResponse { Code = code }, packet.Id);
}
