using MessagePack;

namespace AscNet.Common.MsgPack
{
    // Quest-owned PlayerData sections (XBigWorldAgency.lua:275-279, XBigWorldQuestModel.lua:1012-1078).
    [MessagePackObject(true)]
    public class BigWorldTraceQuestData
    {
        public bool IsEnabled;
        public int CurrentTraceQuestId;
        public int LastTraceQuestId;
        public int CurrentTraceReadyQuestId;
    }

    [MessagePackObject(true)]
    public class BigWorldInviteQuestInfo
    {
        public List<int> UnlockedInviteQuestResultIds = new();
        public Dictionary<int, int> ReceivedRewardInviteQuestIds = new();
    }

    [MessagePackObject(true)]
    public class BigWorldEnvironmentQuestData
    {
        public Dictionary<int, int> ActivatedQuestGroupIds = new();
    }

    // dump.cs D:352532-352680.
    [MessagePackObject(true)] public class BigWorldNotifyReward { public List<RewardGoods> RewardGoodsList = new(); }
    [MessagePackObject(true)] public class NotifyDlcQuestActivate { public List<int> NewActivatedQuestIds = new(); }
    [MessagePackObject(true)] public class NotifyDlcQuestFinish { public List<int> NewFinishedQuestIds = new(); public List<int> ActiveQuests = new(); public List<int> FinishedQuests = new(); }
    [MessagePackObject(true)] public class NotifyDlcQuestUpdate { public Theatre5DlcQuest? QuestData; }
    [MessagePackObject(true)] public class NotifyDlcQuestItemUpdate { public Dictionary<int, DlcQuestItem> DlcQuestItemChangeDict = new(); }
    [MessagePackObject(true)] public class NotifyDlcQuestItemObtainDisplay { public Dictionary<int, DlcQuestItem> DlcQuestItemChangeDict = new(); }

    [MessagePackObject(true)] public class DlcQuestUpdateRequest { public List<Theatre5DlcQuestStepObjective>? QuestStepObjectiveList; }
    [MessagePackObject(true)] public class DlcQuestUpdateResponse { public int Code; }
    [MessagePackObject(true)] public class DlcQuestTraceIdChangeRequest { public int ChangeTraceQuestId; }
    [MessagePackObject(true)] public class DlcQuestTraceIdChangeResponse { public int Code; }
    [MessagePackObject(true)] public class BigWorldSetTrackReadyQuestIdRequest { public int QuestId; }
    [MessagePackObject(true)] public class BigWorldSetTrackReadyQuestIdResponse { public int Code; }
    [MessagePackObject(true)] public class DlcEnvironmentQuestGroupChangeRequest { public int LevelId; public int QuestGroupId; }
    [MessagePackObject(true)] public class DlcEnvironmentQuestGroupChangeResponse { public int Code; public BigWorldEnvironmentQuestData? EnvironmentQuestData; }
    [MessagePackObject(true)] public class DlcInviteQuestAcceptRequest { public int QuestId; }
    [MessagePackObject(true)] public class DlcInviteQuestAcceptResponse { public int Code; }
    [MessagePackObject(true)] public class DlcInviteQuestResultNumRewardRequest { public int QuestId; }
    [MessagePackObject(true)] public class DlcInviteQuestResultNumRewardResponse { public int Code; }

    // XBigWorldQuestAgency.lua:45-70.
    [MessagePackObject(true)] public class NotifyDlcInviteQuestResultReward { public int QuestId; public int ResultId; public List<RewardGoods> RewardItems = new(); public BigWorldInviteQuestInfo InviteQuestInfo = new(); }
    [MessagePackObject(true)] public class NotifyDlcInviteQuestResultNumReward { public List<RewardGoods> RewardItems = new(); public BigWorldInviteQuestInfo InviteQuestInfo = new(); }
}
