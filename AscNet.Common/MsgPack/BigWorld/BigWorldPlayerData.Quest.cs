namespace AscNet.Common.MsgPack
{
    public partial class BigWorldPlayerData
    {
        // Legacy category -> questId; the client only reads it when TraceQuestData.IsEnabled is false.
        public Dictionary<int, int> TraceQuestIds = new();
        public BigWorldTraceQuestData TraceQuestData = new();
        public BigWorldInviteQuestInfo InviteQuestInfo = new();
        public BigWorldEnvironmentQuestData EnvironmentQuestData = new();
    }
}
