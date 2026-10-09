using MessagePack;

namespace AscNet.Common.MsgPack
{
    // Character slice fields (XBigWorldAgency.lua:264-285, XBigWorldCommanderDIYAgency:UpdateData).
    public partial class BigWorldPlayerData
    {
        public int Gender { get; set; }
        public Dictionary<int, BigWorldCommanderOutfit> CommanderFashionOutfits { get; set; } = new();
        public int CurCommanderOutfitType { get; set; }
        public List<int> CommanderFashionBags { get; set; } = new();
        public bool CharacterInitialized { get; set; }
        public int CurrentTeamId { get; set; }
        public Dictionary<int, BigWorldTeamData> TeamDict { get; set; } = new();
        public Dictionary<int, BigWorldCharacterWearFashion> CharacterWearFashionDict { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class BigWorldCommanderOutfit
    {
        public Dictionary<int, Theatre5BigWorldCommanderFashion> WearFashionDict { get; set; } = new();
    }

    // XBWTeam:ToServerTeam.
    [MessagePackObject(true)]
    public class BigWorldTeamData
    {
        public int TeamId { get; set; }
        public List<BigWorldTeamCharacter> CharacterList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class BigWorldTeamCharacter
    {
        public int CharacterId { get; set; }
        public int Pos { get; set; }
    }

    // XBigWorldCharacterAgency:UpdateCharacter.
    [MessagePackObject(true)]
    public class BigWorldCharacterWearFashion
    {
        public int Character { get; set; }
        public int FashionId { get; set; }
        public int FashionColorId { get; set; }
        public BigWorldCharacterHeadInfo? DlcCharacterHeadInfo { get; set; }
    }

    [MessagePackObject(true)]
    public class BigWorldCharacterHeadInfo
    {
        public int HeadFashionId { get; set; }
        public int HeadFashionType { get; set; }
    }
}
