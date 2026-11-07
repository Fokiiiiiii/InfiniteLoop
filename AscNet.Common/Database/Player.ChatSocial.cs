using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("chat_social")]
    public ChatSocialState ChatSocial { get; set; } = new();
}

[BsonIgnoreExtraElements]
public class ChatSocialState
{
    // Client-saved order of non-default emoji packs (SaveEmojiPackageIdRequest).
    public List<int> EmojiPackOrder { get; set; } = [];

    // Receiver-side chat gift inbox. GiftId is unique per receiver and doubles as the offline message sequence.
    public List<ChatGiftRecord> Gifts { get; set; } = [];
    public int LastGiftId { get; set; }
    // Receive quota (GetGiftMaxCountEveryDay) for the daily period in GiftReceiveDay.
    public long GiftReceiveDay { get; set; } = -1;
    public int GiftReceivedToday { get; set; }
    // Sender-side: friend id -> daily period of the last gift sent to them (one gift per friend per day).
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, long> GiftSentDays { get; set; } = new();

    // XReportManager counters and durable report log (reporter side).
    public long ReportDay { get; set; } = -1;
    public int ReportTimes { get; set; }
    public long LastReportTime { get; set; }
    public List<ChatReportRecord> Reports { get; set; } = [];
}

[BsonIgnoreExtraElements]
public class ChatGiftRecord
{
    public int GiftId { get; set; }
    public uint SenderId { get; set; }
    public int ItemId { get; set; }
    public int Count { get; set; }
    public long CreateTime { get; set; }
    // 1 WaitReceive, 2 Received, 3 Fetched (expired); mirrors ChatGiftState.
    public int Status { get; set; }
}

[BsonIgnoreExtraElements]
public class ChatReportRecord
{
    // 0 = player, 1 = guild.
    public int Kind { get; set; }
    public long TargetId { get; set; }
    public string TargetName { get; set; } = string.Empty;
    public int TargetLevel { get; set; }
    public int Entry { get; set; }
    public List<int> Tags { get; set; } = [];
    public string Message { get; set; } = string.Empty;
    public string ReportMessage { get; set; } = string.Empty;
    public int ChatChannel { get; set; }
    public long Time { get; set; }
}
