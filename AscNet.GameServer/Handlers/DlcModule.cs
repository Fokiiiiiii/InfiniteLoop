using AscNet.Common.MsgPack;

namespace AscNet.GameServer.Handlers
{
    // Generic DLC single-player fight routing. BigWorld (Babylonia) lives in Handlers/BigWorld.
    internal class DlcModule
    {
        // Both generic DLC names are registered exactly once. The two DLC modes present today
        // own disjoint, table-derived world ids (Theatre5 200, Theatre6 201/202), so ownership is
        // decided before either handler runs and neither can accept the other's report.
        [RequestPacketHandler("DlcSingleEnterFightRequest")]
        public static void DlcSingleEnterFightRequestHandler(Session session, Packet.Request packet)
        {
            DlcSingleEnterFightRequest request = packet.Deserialize<DlcSingleEnterFightRequest>();
            if (Theatre6Module.OwnsDlcWorld(request.WorldId))
            {
                Theatre6Module.EnterDlcFight(session, packet, request);
                return;
            }

            Theatre5Module.HandleDlcEnter(session, packet);
        }

        [RequestPacketHandler("DlcSingleFightSettleRequest")]
        public static void DlcSingleFightSettleRequestHandler(Session session, Packet.Request packet)
        {
            if (Theatre6Module.OwnsDlcSettleWorld(packet))
            {
                Theatre6Module.SettleDlcFight(session, packet);
                return;
            }

            Theatre5Module.HandleDlcSettle(session, packet);
        }
    }
}
