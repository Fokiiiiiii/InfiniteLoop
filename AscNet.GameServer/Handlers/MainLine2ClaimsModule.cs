using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.mainline2;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class MainLine2ReceiveTreasureRequest { public int ChapterId { get; set; } }
[MessagePackObject(true)] public sealed class MainLine2ReceiveTreasureResponse { public int Code { get; set; } public List<int> RewardIdxs { get; set; } = new(); public List<RewardGoods> RewardGoodsList { get; set; } = new(); }
[MessagePackObject(true)] public sealed class MainLine2ReceiveAchievementRequest { public int ChapterId { get; set; } }
[MessagePackObject(true)] public sealed class MainLine2ReceiveAchievementResponse { public int Code { get; set; } public List<RewardGoods> RewardGoodsList { get; set; } = new(); }
[MessagePackObject(true)] public sealed class MainLine2ReceiveEggsTreasureRequest { public int ChapterId { get; set; } public int EggId { get; set; } }
[MessagePackObject(true)] public sealed class MainLine2ReceiveEggsTreasureResponse { public int Code { get; set; } public List<RewardGoods> RewardGoodsList { get; set; } = new(); }

internal static class MainLine2ClaimsModule
{
    internal static HashSet<int> ChapterStages(IEnumerable<int> chapters)
    {
        HashSet<int> ids = chapters.ToHashSet();
        HashSet<int> groups = TableReaderV2.Parse<MainLine2ChapterTable>().Where(x => ids.Contains(x.ChapterId)).SelectMany(x => x.StageGroupIds).ToHashSet();
        return TableReaderV2.Parse<MainLine2StageGroupTable>().Where(x => groups.Contains(x.Id)).SelectMany(x => x.StageIds).Where(x => x > 0).ToHashSet();
    }
    private static int Passed(Session session, IEnumerable<int> ids) => ids.Count(id => session.stage.Stages.TryGetValue(id, out StageDatum? stage) && stage.Passed);
    private static int Achievements(Session session, IEnumerable<int> ids)
    {
        HashSet<int> selected = ids.ToHashSet();
        int count = 0;
        foreach (MainLine2StageTable row in TableReaderV2.Parse<MainLine2StageTable>().Where(x => selected.Contains(x.Id)))
        {
            if (!session.stage.Stages.TryGetValue(row.Id, out StageDatum? stage)) continue;
            for (int i = 0; i < row.AchievementTpyes.Count && i < 63; i++)
                if (row.AchievementTpyes[i] is 1 or 2 && (stage.Achievement & (1L << i)) != 0) count++;
        }
        return count;
    }
    private static List<RewardGoods> Grant(Session session, List<RewardGoodsTable> goods)
    {
        RewardApplicationResult result = RewardHandler.ApplyRewards(goods, session);
        session.player.Save(); session.inventory.Save(); session.character.Save(); result.SendPushes(session);
        return result.RewardGoods;
    }
    [RequestPacketHandler("MainLine2ReceiveTreasureRequest")]
    public static void Treasure(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<MainLine2ReceiveTreasureRequest>();
        var response = new MainLine2ReceiveTreasureResponse();
        MainLine2ChapterTable? chapter = TableReaderV2.Parse<MainLine2ChapterTable>().FirstOrDefault(x => x.ChapterId == request.ChapterId);
        MainLine2TreasureTable? treasure = TableReaderV2.Parse<MainLine2TreasureTable>().FirstOrDefault(x => x.Id == chapter?.TreasureId);
        if (chapter is null) response.Code = 20219003;
        else if (treasure is null) response.Code = 20219004;
        else if (TableReaderV2.Parse<MainLine2MainTable>().Any(x => x.ChapterIds.Contains(request.ChapterId) && x.TreasureId > 0)) response.Code = 20219009;
        else
        {
            FubenMainLine2Data data = session.player.FubenMainLine2Data ??= new();
            MainLine2ChapterDatum? state = data.ChapterDatas.FirstOrDefault(x => x.Id == request.ChapterId);
            int count = Passed(session, ChapterStages([request.ChapterId]));
            List<int> eligible = Enumerable.Range(0, Math.Min(treasure.StageCounts.Count, treasure.RewardIds.Count)).Where(i => count >= treasure.StageCounts[i]).ToList();
            List<int> indices = eligible.Where(i => state?.TreasureIdxs.Contains(i) != true).ToList();
            if (indices.Count == 0) response.Code = eligible.Count == 0 ? 20003009 : 20003010;
            else
            {
                List<RewardGoodsTable> goods = new();
                foreach (int i in indices)
                {
                    if (i < treasure.HighlightRewardIds.Count && treasure.HighlightRewardIds[i] > 0) goods.AddRange(RewardHandler.GetRewardGoods(treasure.HighlightRewardIds[i]));
                    if (treasure.RewardIds[i] > 0) goods.AddRange(RewardHandler.GetRewardGoods(treasure.RewardIds[i]));
                }
                if (goods.Count == 0) response.Code = 20020003;
                else
                {
                    if (state is null) { state = new MainLine2ChapterDatum { Id = request.ChapterId }; data.ChapterDatas.Add(state); }
                    state.TreasureIdxs.AddRange(indices); response.RewardIdxs = indices; response.RewardGoodsList = Grant(session, goods);
                }
            }
        }
        session.SendResponse(response, packet.Id);
    }
    [RequestPacketHandler("MainLine2ReceiveAchievementRequest")]
    public static void Achievement(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<MainLine2ReceiveAchievementRequest>();
        var response = new MainLine2ReceiveAchievementResponse();
        MainLine2MainTable? main = TableReaderV2.Parse<MainLine2MainTable>().FirstOrDefault(x => x.Id == request.ChapterId);
        MainLine2AchievementTable? achievement = TableReaderV2.Parse<MainLine2AchievementTable>().FirstOrDefault(x => x.Id == main?.AchievementId);
        FubenMainLine2Data data = session.player.FubenMainLine2Data ??= new();
        MainLine2MainDatum? state = data.MainDatas.FirstOrDefault(x => x.Id == request.ChapterId);
        if (main is null) response.Code = 20219007;
        else if (achievement is null) response.Code = 20219005;
        else if (state?.IsAchievementGet == true) response.Code = 20219006;
        else if (Achievements(session, ChapterStages(main.ChapterIds)) < achievement.Count) response.Code = 20219008;
        else
        {
            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(achievement.ClearRewardId);
            if (goods.Count == 0) response.Code = 20020003;
            else { if (state is null) { state = new MainLine2MainDatum { Id = main.Id }; data.MainDatas.Add(state); } state.IsAchievementGet = true; response.RewardGoodsList = Grant(session, goods); }
        }
        session.SendResponse(response, packet.Id);
    }
    [RequestPacketHandler("MainLine2ReceiveEggsTreasureRequest")]
    public static void Egg(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<MainLine2ReceiveEggsTreasureRequest>();
        var response = new MainLine2ReceiveEggsTreasureResponse();
        MainLine2ChapterTable? chapter = TableReaderV2.Parse<MainLine2ChapterTable>().FirstOrDefault(x => x.ChapterId == request.ChapterId);
        MainLine2EggsTreasureTable? egg = TableReaderV2.Parse<MainLine2EggsTreasureTable>().FirstOrDefault(x => x.Id == request.EggId);
        FubenMainLine2Data data = session.player.FubenMainLine2Data ??= new();
        ConditionTable? condition = TableReaderV2.Parse<ConditionTable>().FirstOrDefault(x => x.Id == egg?.ConditionId);
        if (chapter is null || chapter.EggIds != request.EggId) response.Code = 20219012;
        else if (egg is null) response.Code = 20219014;
        else if (data.EggsTreasureDistributeData.Any(x => Convert.ToInt32(x) == request.EggId)) response.Code = 20219015;
        else if (condition is null || !LifeTreeModule.ConditionSatisfied(session, condition)) response.Code = 20219013;
        else
        {
            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(egg.RewardId);
            if (goods.Count == 0) response.Code = 20020003;
            else { data.EggsTreasureDistributeData.Add(request.EggId); response.RewardGoodsList = Grant(session, goods); }
        }
        session.SendResponse(response, packet.Id);
    }
}
