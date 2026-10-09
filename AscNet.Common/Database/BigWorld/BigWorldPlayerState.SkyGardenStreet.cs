using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database
{
    // Babylonia shopping street; mutated only by SkyGardenStreetModule.
    public partial class BigWorldPlayerState
    {
        [BsonElement("sg_street")]
        public SgStreetData SgStreet { get; set; } = new();

        [BsonElement("sg_street_private")]
        public SgStreetPrivateState SgStreetPrivate { get; set; } = new();
    }
}
