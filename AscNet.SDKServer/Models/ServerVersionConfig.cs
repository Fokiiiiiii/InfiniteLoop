using Newtonsoft.Json;

namespace AscNet.SDKServer.Models
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public class ServerVersionConfig
    {
        [JsonProperty("DocumentVersion", NullValueHandling = NullValueHandling.Ignore)]
        public string DocumentVersion { get; set; }

        [JsonProperty("LaunchModuleVersion", NullValueHandling = NullValueHandling.Ignore)]
        public string LaunchModuleVersion { get; set; }

        [JsonProperty("IndexMd5", NullValueHandling = NullValueHandling.Ignore)]
        public string IndexMd5 { get; set; }

        [JsonProperty("IndexSha1", NullValueHandling = NullValueHandling.Ignore)]
        public string IndexSha1 { get; set; }

        [JsonProperty("LaunchIndexSha1", NullValueHandling = NullValueHandling.Ignore)]
        public string LaunchIndexSha1 { get; set; }

        [JsonProperty("Packages", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, ServerVersionConfig>? Packages { get; set; }

        // A region's authoritative config.tab, in order. "{origin}" in a Value marks an AscNet-owned row
        // (server list, pay callbacks) and is replaced with the public origin; every other row is served verbatim.
        [JsonProperty("ConfigRows", NullValueHandling = NullValueHandling.Ignore)]
        public List<RemoteConfig>? ConfigRows { get; set; }
    }
}
