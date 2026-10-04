using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database
{
    // Hotfix slice: quest-objective hotfix script ids already executed for this player
    // (retail: "服务端有记录id，不重复执行" in XDlcQuestHotfixManager.lua).
    public partial class BigWorldPlayerState
    {
        [BsonElement("executed_hotfix_script_ids")]
        public List<int> ExecutedHotfixScriptIds { get; set; } = new();
    }
}
