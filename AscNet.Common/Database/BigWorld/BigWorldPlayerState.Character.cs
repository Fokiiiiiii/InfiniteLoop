using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database
{
    // Character slice: teams, BigWorld character fashion/head, commander DIY, delayed show rewards.
    public partial class BigWorldPlayerState
    {
        // 0 = not seeded yet (BigWorldCharacterModule seeds team 1 and sets 1).
        [BsonElement("current_team_id")]
        public int CurrentTeamId { get; set; }

        // teamId -> members (Pos is 0-based).
        [BsonElement("teams")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, List<BigWorldTeamMemberState>> Teams { get; set; } = new();

        // BigWorld characterId -> worn fashion/head.
        [BsonElement("character_fashions")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, BigWorldCharacterFashionState> CharacterFashions { get; set; } = new();

        // 0 = DIY state not seeded yet.
        [BsonElement("commander_gender")]
        public int CommanderGender { get; set; }

        [BsonElement("cur_commander_outfit_type")]
        public int CurCommanderOutfitType { get; set; }

        // outfitType -> worn parts.
        [BsonElement("commander_fashion_outfits")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, List<BigWorldCommanderPartState>> CommanderFashionOutfits { get; set; } = new();

        [BsonElement("commander_fashion_bags")]
        public List<int> CommanderFashionBags { get; set; } = new();

        [BsonElement("character_initialized")]
        public bool CharacterInitialized { get; set; }

        [BsonElement("pending_show_rewards")]
        public List<RewardGoods> PendingShowRewards { get; set; } = new();
    }

    public class BigWorldTeamMemberState
    {
        [BsonElement("character_id")]
        public int CharacterId { get; set; }

        [BsonElement("pos")]
        public int Pos { get; set; }
    }

    public class BigWorldCharacterFashionState
    {
        [BsonElement("fashion_id")]
        public int FashionId { get; set; }

        [BsonElement("fashion_color_id")]
        public int FashionColorId { get; set; }

        [BsonElement("head_fashion_id")]
        public int HeadFashionId { get; set; }

        [BsonElement("head_fashion_type")]
        public int HeadFashionType { get; set; }
    }

    public class BigWorldCommanderPartState
    {
        [BsonElement("type_id")]
        public int TypeId { get; set; }

        [BsonElement("part_id")]
        public int PartId { get; set; }

        [BsonElement("colour_id")]
        public int ColourId { get; set; }
    }
}
