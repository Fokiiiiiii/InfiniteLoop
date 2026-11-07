using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.scoretitle;
using AscNet.Table.V2.share.player;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MessagePack;
using Newtonsoft.Json;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateProfileCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(out var saves, out var characterSaves, out _, out _);
        var a = CreateDrawCompatibilityPlayer(48781);
        var b = CreateDrawCompatibilityPlayer(48782);
        var ac = CreateDrawCompatibilityCharacter(a.PlayerData.Id);
        var bc = CreateDrawCompatibilityCharacter(b.PlayerData.Id);
        var characterIds = TableReaderV2.Parse<CharacterTable>().Take(2).Select(x => (uint)x.Id).ToArray();
        bc.Characters = characterIds.Select((id, i) => new CharacterData { Id = id, Level = i + 10, Quality = i + 2 }).ToList();
        var fashionIds = TableReaderV2.Parse<FashionTable>().Take(2).Select(x => x.Id).ToArray();
        bc.Fashions = fashionIds.Select(id => new FashionList { Id = id }).ToList();
        bc.WeaponFashions = [new WeaponFashionData { Id = 1 }, new WeaponFashionData { Id = 2 }];
        var titleRows = TableReaderV2.Parse<ScoreTitleTable>().Where(x => x.InitQuality is >= 2 and <= 6).Take(2).ToArray();
        bc.ScoreTitles = titleRows.Select(x => new NotifyScoreTitleData.NotifyScoreTitleDataTitleInfo { Id = (uint)x.Id, Quality = x.InitQuality }).ToList();
        a.PlayerData.AppearanceSettingInfo = Appearance(1);
        b.PlayerData.AppearanceSettingInfo = Appearance(1);
        using LoopbackSessionHarness ah = new(ac, a, sessionId: "profile-a");
        using LoopbackSessionHarness bh = new(bc, b, sessionId: "profile-b");
        Server.Instance.Sessions.TryAdd("profile-a", ah.Session);
        Server.Instance.Sessions.TryAdd("profile-b", bh.Session);
        int packetId = 48780;
        try
        {
            SetAppearanceRequest selection = new() { CharactersAppearanceType = 1, Characters = [(long)characterIds[0]], AppearanceSettingInfo = Appearance(1) };
            AssertEqual(0, Call<SetAppearanceResponse>(bh, "SetAppearanceRequest", selection).Code, "Profile selected appearance succeeds");
            int nameplateId = TableReaderV2.Parse<AscNet.Table.V2.share.nameplate.NameplateTable>().First().Id;
            bc.Nameplates = [new NameplateData { Id = nameplateId }];
            bc.CurrentWearNameplate = nameplateId;
            b.GatherRewards = [(int)characterIds[0]];
            var detail = Call<QueryPlayerDetailResponse>(ah, "QueryPlayerDetailRequest", new { PlayerId = b.PlayerData.Id });
            AssertEqual(0, detail.Code, "Other player's real profile query");
            AssertEqual(b.PlayerData.Name, detail.Detail!["Name"]!.ToString(), "Profile name comes from target");
            // UiPlayerInfo aborts on a missing nameplate key (nil ~= 0) and pairs() over missing GatherIds.
            AssertEqual((long)nameplateId, Convert.ToInt64(detail.Detail["CurrentWearNameplate"]), "Detail carries the target's worn nameplate");
            AssertEqual((long)b.GatherRewards[0], Convert.ToInt64(((object[])detail.Detail["GatherIds"]!).Single()), "Detail carries the target's gather rewards");
            AssertEqual(0L, Convert.ToInt64(((Dictionary<object, object>)detail.Detail["MentorDetail"]!)["MentorId"]), "Detail has an empty mentor identity");
            var selfDetail = Call<QueryPlayerDetailResponse>(ah, "QueryPlayerDetailRequest", new { PlayerId = a.PlayerData.Id });
            AssertEqual(0L, Convert.ToInt64(selfDetail.Detail!["CurrentWearNameplate"]), "No worn nameplate serializes as 0, not a missing key");
            AssertEqual(0, ((object[])selfDetail.Detail["GatherIds"]!).Length, "Empty gather list is present");
            var shows = Call<QueryPlayerCharacterListResponse>(ah, "QueryPlayerCharacterListRequest", new { PlayerId = b.PlayerData.Id });
            AssertEqual((long)characterIds[0], (long)shows.CharacterShows.Single().Id, "Selected character visibility");
            selection.Characters = [(long)characterIds[1]];
            AssertEqual(0, Call<SetAppearanceResponse>(bh, "SetAppearanceRequest", selection).Code, "Second distinct selected appearance");
            AssertEqual((long)characterIds[1], (long)Call<QueryPlayerCharacterListResponse>(ah, "QueryPlayerCharacterListRequest", new { PlayerId = b.PlayerData.Id }).CharacterShows.Single().Id, "Second selection derived independently");
            AssertEqual(2, Call<QueryPlayerFashionResponse>(ah, "QueryPlayerFashionRequest", new { PlayerId = b.PlayerData.Id }).Fashions.Count, "Other player's owned coatings");
            AssertEqual(2, Call<QueryPlayerWeaponFashionResponse>(ah, "QueryPlayerWeaponFashionRequest", new { PlayerId = b.PlayerData.Id }).WeaponFashions.Count, "Other player's weapon coatings");
            AssertEqual(2, Call<QueryPlayerTitleResponse>(ah, "QueryPlayerTitleRequest", new { PlayerId = b.PlayerData.Id }).Titles.Count, "Other player's real titles");
            AssertEqual(11, Call<QueryCharacterInfoResponse>(ah, "QueryCharacterInfoRequest", new { PlayerId = b.PlayerData.Id, CharacterId = characterIds[1] }).CharacterData!.Level, "Selected character real level");
            AssertEqual(20037003, Call<QueryCharacterInfoResponse>(ah, "QueryCharacterInfoRequest", new { PlayerId = b.PlayerData.Id, CharacterId = characterIds[0] }).Code, "Unselected character detail rejected");
            selection.AppearanceSettingInfo = Appearance(3);
            AssertEqual(0, Call<SetAppearanceResponse>(bh, "SetAppearanceRequest", selection).Code, "Private appearance saves");
            AssertEqual(20037002, Call<QueryPlayerCharacterListResponse>(ah, "QueryPlayerCharacterListRequest", new { PlayerId = b.PlayerData.Id }).Code, "Character privacy enforced server-side");
            AssertEqual(20037002, Call<QueryPlayerFashionResponse>(ah, "QueryPlayerFashionRequest", new { PlayerId = b.PlayerData.Id }).Code, "Coating privacy enforced");
            AssertEqual(20037002, Call<QueryPlayerWeaponFashionResponse>(ah, "QueryPlayerWeaponFashionRequest", new { PlayerId = b.PlayerData.Id }).Code, "Weapon coating privacy enforced");
            AssertEqual(20037002, Call<QueryPlayerTitleResponse>(ah, "QueryPlayerTitleRequest", new { PlayerId = b.PlayerData.Id }).Code, "Title privacy enforced");
            AssertEqual(0, Call<QueryPlayerCharacterListResponse>(bh, "QueryPlayerCharacterListRequest", new { PlayerId = b.PlayerData.Id }).Code, "Self can see private characters");
            selection.AppearanceSettingInfo = Appearance(2);
            Call<SetAppearanceResponse>(bh, "SetAppearanceRequest", selection);
            AssertEqual(20037002, Call<QueryPlayerCharacterListResponse>(ah, "QueryPlayerCharacterListRequest", new { PlayerId = b.PlayerData.Id }).Code, "Friend-only hides from strangers");
            b.Social.Friends[(uint)a.PlayerData.Id] = 1;
            AssertEqual(0, Call<QueryPlayerCharacterListResponse>(ah, "QueryPlayerCharacterListRequest", new { PlayerId = b.PlayerData.Id }).Code, "Friend-only permits persisted friend");
            selection.Characters = [long.MaxValue];
            AssertEqual(20000003, Call<SetAppearanceResponse>(bh, "SetAppearanceRequest", selection).Code, "Appearance requires owned characters");
            AssertEqual(2L, BsonSerializer.Deserialize<Player>(saves.LastReplacement!.ToBson()).PlayerData.AppearanceSettingInfo.TitleType, "Appearance persisted in saved player");

            AssertEqual(0, Code(bh, "WriteDailyRequest", new { DailyContent = "first diary" }), "Diary write");
            AssertEqual(0, Code(bh, "WriteDailyRequest", new { DailyContent = "second diary" }), "Distinct diary write");
            long diaryId = b.Profile.Diaries[0].Id;
            var diaries = Call<GetDailysResponse>(ah, "GetDailysRequest", new { PlayerId = b.PlayerData.Id, PageNum = 1 });
            AssertEqual("second diary", diaries.DailyList[0].Content, "Diaries newest first");
            AssertEqual(0, Code(ah, "LeaveMsgRequest", new { PlayerId = b.PlayerData.Id, DailyId = diaryId, LeaveMsg = "hello" }), "Other player leaves a message");
            var messages = Call<LookOverLeaveMsgResponse>(ah, "LookOverLeaveMsgRequest", new { PlayerId = b.PlayerData.Id, DailyId = diaryId });
            AssertEqual(a.PlayerData.Name, messages.MsgArray.Single().Name, "Message author derived from sender");
            AssertEqual(0, Code(bh, "DeleteLeaveMsgRequest", new { DailyId = diaryId, MsgId = messages.MsgArray[0].Id }), "Owner deletes message");
            AssertEqual(0, Code(bh, "DeleteLeaveMsgRequest", new { DailyId = diaryId, MsgId = messages.MsgArray[0].Id }), "Delete retry idempotent");
            AssertEqual(0, Code(bh, "BanMsgRequest", new { DailyId = diaryId }), "Owner bans comments");
            AssertEqual(20037002, Code(ah, "LeaveMsgRequest", new { PlayerId = b.PlayerData.Id, DailyId = diaryId, LeaveMsg = "blocked" }), "Ban prevents future comments");
            AssertEqual(20038002, Code(ah, "GiveLikeRequest", new { PlayerId = b.PlayerData.Id, DailyId = diaryId }), "Like add uses client success code");
            AssertEqual(20038003, Code(ah, "GiveLikeRequest", new { PlayerId = b.PlayerData.Id, DailyId = diaryId }), "Second click removes like");
            int likeLimit = TableReaderV2.Parse<ProfilePolicyTable>().Single(x => x.Key == "DailyLikeLimit").Value;
            for (int i = 1; i < likeLimit; i++) { Code(ah, "GiveLikeRequest", new { PlayerId = b.PlayerData.Id, DailyId = diaryId }); Code(ah, "GiveLikeRequest", new { PlayerId = b.PlayerData.Id, DailyId = diaryId }); }
            AssertEqual(20038001, Code(ah, "GiveLikeRequest", new { PlayerId = b.PlayerData.Id, DailyId = diaryId }), "Daily like additions capped even after unlikes");
            var reload = BsonSerializer.Deserialize<Player>(b.ToBson());
            AssertEqual(true, reload.Profile.Diaries.Single(d => d.Id == diaryId).IsBanMsg, "Diary ban survives BSON reload");
            AssertEqual(5, Code(bh, "WriteDailyRequest", new { DailyContent = new string('x', 101) }), "Client text limit enforced");
            AssertEqual(0, Code(bh, "DeleteDailyRequest", new { DailyId = diaryId }), "Owner deletes diary");
            AssertEqual(0, Code(bh, "DeleteDailyRequest", new { DailyId = diaryId }), "Diary delete retry idempotent");
            AssertEqual(1, b.Profile.Diaries.Count, "Deleting diary preserves different diary");
            AssertEqual(0, Call<ViewPersonalInfoResponse>(ah, "ViewPersonalInfoRequest", new { viewId = b.PlayerData.Id }).Code, "Legacy personal view request");

            AssertEqual(20000003, Code(bh, "ChangePlayerDisplayCharIdRequest", new { CharId = long.MaxValue }), "Display requires owned character");
            foreach (uint id in characterIds) { AssertEqual(0, Code(bh, "ChangePlayerDisplayCharIdRequest", new { CharId = id }), "Owned display selection"); AssertEqual((long)id, b.PlayerData.DisplayCharId, "Distinct display selection derived"); }
            AssertEqual(0, Code(bh, "ScoreTitleShowSetRequest", new { ScoreTitleShowSetInfos = new[] { new { Type = titleRows[0].Type, Hide = 1 } } }), "Title show setting");
            AssertEqual(5, Code(bh, "ScoreTitleShowSetRequest", new { ScoreTitleShowSetInfos = new[] { new { Type = -1, Hide = 1 } } }), "Unknown title type rejected");
            AssertEqual(0, Code(bh, "CollectionShowSetRequest", new { IsHideCollection = true }), "Whole collection visibility persists");
            var wallRow = TableReaderV2.Parse<CollectionWallTable>().First(x => x.Condition == 0);
            var placement = new ProfileCollectionPlacement { Id = bc.ScoreTitles[0].Id, SizeId = bc.ScoreTitles[0].Quality, X = 0, Y = 0 };
            var edit = new ProfileWall { Id = wallRow.Id, BackgroundId = (uint)wallRow.InitBackgroundId, PedestalId = (uint)wallRow.InitPedestalId, CollectionSetInfos = [placement] };
            AssertEqual(0, Code(bh, "EditCollectionWallRequest", edit), "Owned collection wall edit");
            placement.X = -1;
            AssertEqual(5, Code(bh, "EditCollectionWallRequest", edit), "Out-of-bounds wall placement rejected");
            placement.X = 1;
            AssertEqual(0, Code(bh, "EditCollectionWallRequest", edit), "Second distinct wall position");
            // The client edit surface is 16x9 cells; a 2-cell collectible on the top row starts at Y = 7.
            int grid = TableReaderV2.Parse<AscNet.Table.V2.client.collectionwall.CollectionSizeTable>().Single(x => x.Id == placement.SizeId).GridNum;
            placement.Y = 9 - grid;
            AssertEqual(0, Code(bh, "EditCollectionWallRequest", edit), "Top-row placement inside the client wall accepted");
            placement.Y = 10 - grid;
            AssertEqual(5, Code(bh, "EditCollectionWallRequest", edit), "Placement past the client wall top rejected");
            placement.X = 16 - grid;
            placement.Y = 0;
            AssertEqual(0, Code(bh, "EditCollectionWallRequest", edit), "Right-edge placement inside the client wall accepted");
            // XMessagePack sends an emptied wall's CollectionSetInfos as nil.
            AssertEqual(0, Code(bh, "EditCollectionWallRequest", new { Id = wallRow.Id, PedestalId = edit.PedestalId, BackgroundId = edit.BackgroundId, CollectionSetInfos = (object?)null }), "Clearing a wall (nil list) succeeds");
            AssertEqual(0, b.Profile.Walls.Single(w => w.Id == wallRow.Id).CollectionSetInfos.Count, "Cleared wall has no placements");
            placement.X = 1;
            AssertEqual(0, Code(bh, "EditCollectionWallRequest", edit), "Wall can be refilled after clearing");
            AssertEqual(0, Code(bh, "EditCollectionWallIsShowRequest", new { CollectionWallIsShowInfos = new[] { new { Id = wallRow.Id, IsShow = true } } }), "Wall display flag");
            AssertEqual(5, Code(bh, "EditCollectionWallIsShowRequest", new { CollectionWallIsShowInfos = new[] { new { Id = -1, IsShow = true } } }), "Unknown wall rejected");
            reload = BsonSerializer.Deserialize<Player>(saves.LastReplacement!.ToBson());
            AssertEqual(true, reload.Profile.HideCollection, "Collection show flag persisted");
            AssertEqual(true, reload.Profile.HiddenTitleTypes.Contains(titleRows[0].Type), "Title hide type persisted");
            AssertEqual(1, reload.Profile.Walls.Single(w => w.Id == wallRow.Id).CollectionSetInfos.Single().X, "Wall placement persisted");
            AssertEqual(true, reload.Profile.Walls.Single(w => w.Id == wallRow.Id).IsShow, "Wall visibility persisted");
            AssertEqual(characterIds[1], (uint)reload.PlayerData.DisplayCharId, "Display character persisted");
            AssertEqual(wallRow.Id, BsonSerializer.Deserialize<AscNet.Common.Database.Character>(characterSaves.LastReplacement!.ToBson()).ScoreTitles[0].WallId, "Title wall membership persisted");
        }
        finally { Server.Instance.Sessions.TryRemove("profile-a", out _); Server.Instance.Sessions.TryRemove("profile-b", out _); }

        T Call<T>(LoopbackSessionHarness harness, string name, object request)
        {
            GetRegisteredRequestHandler(name).Invoke(harness.Session, new Packet.Request { Id = ++packetId, Name = name, Content = MessagePackSerializer.ConvertFromJson(JsonConvert.SerializeObject(request)) });
            T result = ReadResponsePayload<T>(harness, packetId, name[..^7] + "Response", name);
            AssertNoAvailablePacket(harness, name + " has no uncontracted pushes");
            return result;
        }
        int Code(LoopbackSessionHarness h, string name, object request) => Call<ProfileResult>(h, name, request).Code;
        static AppearanceSettingInfo Appearance(long visibility) => new() { TitleType = visibility, CharacterType = visibility, FashionType = visibility, WeaponFashionType = visibility, DormitoryType = visibility };
    }
}
