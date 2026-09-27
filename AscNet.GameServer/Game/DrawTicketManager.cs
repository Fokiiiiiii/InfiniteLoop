using System.Globalization;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.drawticket;

namespace AscNet.GameServer.Game;

internal static class DrawTicketManager
{
    private static readonly Lazy<Dictionary<int, DrawTicketTable>> Tickets = new(() =>
        TableReaderV2.Parse<DrawTicketTable>().ToDictionary(row => row.Id));

    internal static void Grant(Player player, int cfgId, int count, DateTimeOffset now)
    {
        if (count <= 0 || !Tickets.Value.TryGetValue(cfgId, out DrawTicketTable? cfg)
            || cfg.EffectType != 1
            || !DateTimeOffset.TryParseExact(cfg.EffectParamStr, "yyyy/M/d H:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset expires))
            throw new InvalidDataException($"Unsupported free draw ticket {cfgId}.");
        player.DrawState ??= new PlayerDrawState();
        List<PlayerDrawTicket> tickets = player.DrawState.FreeTickets ??= [];
        tickets.Add(new PlayerDrawTicket
        {
            Id = tickets.Count == 0 ? 1 : checked(tickets.Max(ticket => ticket.Id) + 1),
            CfgId = cfgId,
            Count = count,
            CreateTime = now.ToUnixTimeSeconds(),
            ExpireTime = expires.ToUnixTimeSeconds()
        });
    }

    internal static PlayerDrawTicket? Available(Player player, int instanceId, int groupId, int count, DateTimeOffset now)
    {
        PlayerDrawTicket? ticket = player.DrawState?.FreeTickets?.FirstOrDefault(row => row.Id == instanceId);
        return ticket is not null && ticket.Count > 0 && ticket.ExpireTime > now.ToUnixTimeSeconds()
            && Tickets.Value.TryGetValue(ticket.CfgId, out DrawTicketTable? cfg)
            && cfg.DrawGroupIds.Contains(groupId)
            && (cfg.UseTenDraw != 0) == (count == 10)
            && count is 1 or 10
            ? ticket : null;
    }

    internal static NotifyDrawTicketData BuildNotify(Player player) => new()
    {
        DrawTicketInfos = (player.DrawState?.FreeTickets ?? [])
            .Select(ticket => new NotifyDrawTicketData.NotifyDrawTicketDataDrawTicketInfo
            {
                Id = ticket.Id,
                CfgId = checked((uint)ticket.CfgId),
                Count = ticket.Count,
                CreateTime = checked((uint)ticket.CreateTime),
                ExpireTime = checked((uint)ticket.ExpireTime)
            }).ToList()
    };
}
