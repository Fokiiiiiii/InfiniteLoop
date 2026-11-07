using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.scoretitle;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.player;
using AscNet.Table.V2.client.collectionwall;

namespace AscNet.GameServer.Handlers;

internal static class ProfileShowcaseModule
{
    // AscNet missing-retail policy: quotas and wall grid bounds are explicit local rules.
    internal static int Policy(string key) => TableReaderV2.Parse<ProfilePolicyTable>().Single(x => x.Key == key).Value;
    private static bool Condition(Player player, int id)
    {
        if (id == 0) return true;
        var rule = TableReaderV2.Parse<ConditionTable>().FirstOrDefault(x => x.Id == id);
        return rule is not null && rule.Type == 10101 && rule.Params.Count > 0 && player.PlayerData.Level >= rule.Params[0];
    }
    internal static void EnsureWalls(Player player)
    {
        foreach (var row in TableReaderV2.Parse<CollectionWallTable>().Where(x => Condition(player, x.Condition)))
            if (!player.Profile.Walls.Any(w => w.Id == row.Id)) player.Profile.Walls.Add(new ProfileWall { Id = row.Id, BackgroundId = (uint)row.InitBackgroundId, PedestalId = (uint)row.InitPedestalId });
        foreach (var row in TableReaderV2.Parse<CollectionWallDecorationTable>().Where(x => x.UnlockType == 1 && Condition(player, x.Condition)))
            if (!player.Profile.UnlockedDecorationIds.Contains((uint)row.Id)) player.Profile.UnlockedDecorationIds.Add((uint)row.Id);
    }
    internal static NotifyScoreTitleData LoginData(Session session)
    {
        int previousWallCount = session.player.Profile.Walls.Count;
        int previousDecorationCount = session.player.Profile.UnlockedDecorationIds.Count;
        EnsureWalls(session.player);
        if (previousWallCount != session.player.Profile.Walls.Count || previousDecorationCount != session.player.Profile.UnlockedDecorationIds.Count) session.player.Save();
        return new NotifyScoreTitleData
        {
            TitleInfos = session.character.ScoreTitles,
            HideTypes = session.player.Profile.HiddenTitleTypes.Select(x => (dynamic)x).ToList(),
            IsHideCollection = session.player.Profile.HideCollection,
            WallInfos = session.player.Profile.Walls.Select(WireWall).ToList(),
            UnlockedDecorationIds = session.player.Profile.UnlockedDecorationIds
        };
    }
    internal static NotifyScoreTitleData.NotifyScoreTitleDataWallInfo WireWall(ProfileWall w) => new() { Id = w.Id, BackgroundId = w.BackgroundId, PedestalId = w.PedestalId, IsShow = w.IsShow, CollectionSetInfos = w.CollectionSetInfos.Select(x => (dynamic)x).ToList() };
    internal static int ChangeDisplay(Player player, Character characters, long id)
    {
        if (!characters.Characters.Any(c => c.Id == id)) return 20000003;
        player.PlayerData.DisplayCharId = id;
        return 0;
    }
    internal static int SetTitleShows(Player player, ScoreTitleShowSetRequest request)
    {
        if (request.ScoreTitleShowSetInfos is null || request.ScoreTitleShowSetInfos.Select(x => x.Type).Distinct().Count() != request.ScoreTitleShowSetInfos.Count) return 5;
        var types = TableReaderV2.Parse<ScoreTitleTable>().Select(x => x.Type).ToHashSet();
        if (request.ScoreTitleShowSetInfos.Any(x => !types.Contains(x.Type) || x.Hide is < 0 or > 1)) return 5;
        foreach (var entry in request.ScoreTitleShowSetInfos) { player.Profile.HiddenTitleTypes.Remove(entry.Type); if (entry.Hide == 1) player.Profile.HiddenTitleTypes.Add(entry.Type); }
        return 0;
    }
    internal static int EditWall(Player player, Character characters, ProfileWall request)
    {
        EnsureWalls(player);
        var wall = player.Profile.Walls.FirstOrDefault(w => w.Id == request.Id);
        // XMessagePack encodes the client's empty CollectionSetInfos table as nil; that is a cleared wall.
        request.CollectionSetInfos ??= [];
        if (wall is null) return 5;
        var decorations = TableReaderV2.Parse<CollectionWallDecorationTable>();
        if (!decorations.Any(x => x.Id == request.BackgroundId && x.Type == 1 && player.Profile.UnlockedDecorationIds.Contains((uint)x.Id)) || !decorations.Any(x => x.Id == request.PedestalId && x.Type == 2 && player.Profile.UnlockedDecorationIds.Contains((uint)x.Id))) return 5;
        var occupied = new HashSet<(int, int)>();
        var ids = new HashSet<uint>();
        var groups = new HashSet<int>();
        foreach (var placement in request.CollectionSetInfos)
        {
            var owned = characters.ScoreTitles.FirstOrDefault(t => t.Id == placement.Id);
            var authority = TableReaderV2.Parse<ScoreTitleTable>().FirstOrDefault(t => t.Id == placement.Id);
            var size = TableReaderV2.Parse<CollectionSizeTable>().FirstOrDefault(t => t.Id == placement.SizeId);
            if (owned is null || authority is null || size is null || placement.SizeId != owned.Quality || !ids.Add(placement.Id) || placement.X < 0 || placement.Y < 0 || placement.X > Policy("WallWidth") - size.GridNum || placement.Y > Policy("WallHeight") - size.GridNum) return 5;
            if (authority.GroupId > 0 && !groups.Add(authority.GroupId)) return 5;
            if (player.Profile.Walls.Any(w => w.Id != wall.Id && w.CollectionSetInfos.Any(p => p.Id == placement.Id))) return 5;
            for (int x = placement.X; x < placement.X + size.GridNum; x++) for (int y = placement.Y; y < placement.Y + size.GridNum; y++) if (!occupied.Add((x, y))) return 5;
        }
        wall.BackgroundId = request.BackgroundId; wall.PedestalId = request.PedestalId; wall.CollectionSetInfos = request.CollectionSetInfos;
        foreach (var title in characters.ScoreTitles) { if (title.WallId == wall.Id) title.WallId = 0; if (ids.Contains(title.Id)) title.WallId = wall.Id; }
        return 0;
    }
    [RequestPacketHandler("ChangePlayerDisplayCharIdRequest")]
    public static void DisplayHandler(Session s, Packet.Request p) { int code = ChangeDisplay(s.player, s.character, p.Deserialize<ChangePlayerDisplayCharIdRequest>().CharId); if (code == 0) s.player.Save(); ProfileModule.Respond(s, p, code); }
    [RequestPacketHandler("ScoreTitleShowSetRequest")]
    public static void TitleShowsHandler(Session s, Packet.Request p) { int code = SetTitleShows(s.player, p.Deserialize<ScoreTitleShowSetRequest>()); if (code == 0) s.player.Save(); ProfileModule.Respond(s, p, code); }
    [RequestPacketHandler("CollectionShowSetRequest")]
    public static void CollectionShowHandler(Session s, Packet.Request p) { s.player.Profile.HideCollection = p.Deserialize<CollectionShowSetRequest>().IsHideCollection; s.player.Save(); ProfileModule.Respond(s, p, 0); }
    [RequestPacketHandler("EditCollectionWallRequest")]
    public static void WallHandler(Session s, Packet.Request p) { int code = EditWall(s.player, s.character, p.Deserialize<ProfileWall>()); if (code == 0) { s.character.Save(); s.player.Save(); } ProfileModule.Respond(s, p, code); }
    [RequestPacketHandler("EditCollectionWallIsShowRequest")]
    public static void WallShowHandler(Session s, Packet.Request p)
    {
        var request = p.Deserialize<EditCollectionWallIsShowRequest>(); EnsureWalls(s.player);
        int code = request.CollectionWallIsShowInfos is null || request.CollectionWallIsShowInfos.Select(x => x.Id).Distinct().Count() != request.CollectionWallIsShowInfos.Count || request.CollectionWallIsShowInfos.Any(x => !s.player.Profile.Walls.Any(w => w.Id == x.Id)) ? 5 : 0;
        if (code == 0) { foreach (var setting in request.CollectionWallIsShowInfos!) s.player.Profile.Walls.Single(w => w.Id == setting.Id).IsShow = setting.IsShow; s.player.Save(); }
        ProfileModule.Respond(s, p, code);
    }
}
