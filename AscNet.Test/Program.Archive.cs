using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.archive;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateArchiveCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        var collection = DispatchProxy.Create<IMongoCollection<Player>, RecordingMongoCollectionProxy<Player>>();
        var saved = (RecordingMongoCollectionProxy<Player>)(object)collection;
        MongoCollectionOverride.SetStaticField(MongoCollectionOverride.RequiredCollectionField(typeof(Player)), collection);
        const long id = 99_165;
        Player player = CreateDrawCompatibilityPlayer(id);
        Inventory inventory = CreateDrawCompatibilityInventory(id, []);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(id), player, inventory, "archive");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(id);
        int sequence = 49_500;
        T Call<T>(string name, object request) where T : new()
        {
            InvokeRegisteredRequestHandler(name, harness.Session, ++sequence, request);
            return (T)ReadResponsePayload(harness, sequence, name[..^7] + "Response", name, typeof(T), maxPacketsToRead: 64);
        }
        var monster = TableReaderV2.Parse<MonsterTable>().First();
        int npc = monster.NpcId.First();
        AssertEqual(20065002, Call<ArchiveResponse>("UnlockArchiveMonsterRequest", new ArchiveIdsRequest { Ids = [int.MaxValue] }).Code, "invalid monster rejected");
        var setting = TableReaderV2.Parse<MonsterSettingTable>().First(row => row.Condition > 0);
        AssertEqual(20065004, Call<ArchiveResponse>("UnlockMonsterSettingRequest", new ArchiveIdsRequest { Ids = [setting.Id] }).Code, "kill condition rejects fresh player");
        player.ArchiveMonsterKills[npc] = 100;
        AssertEqual(0, Call<ArchiveResponse>("UnlockArchiveMonsterRequest", new ArchiveIdsRequest { Ids = [monster.Id] }).Code, "encountered monster unlocks");
        int unlocks = player.ArchiveUnlocks.Count;
        byte[] bag = inventory.ToBson();
        AssertEqual(0, Call<ArchiveResponse>("UnlockArchiveMonsterRequest", new ArchiveIdsRequest { Ids = [monster.Id] }).Code, "repeat unlock succeeds");
        AssertEqual(unlocks, player.ArchiveUnlocks.Count, "repeat unlock does not duplicate state");
        AssertEqual(Convert.ToHexString(bag), Convert.ToHexString(inventory.ToBson()), "no reward invented where tables define none");
        player.ArchiveMonsterKills[setting.GroupId] = 100;
        AssertEqual(0, Call<ArchiveResponse>("UnlockMonsterSettingRequest", new ArchiveIdsRequest { Ids = [setting.Id] }).Code, "kill boundary unlocks setting");
        var info = TableReaderV2.Parse<MonsterInfoTable>().First(row => row.GroupId == npc);
        AssertEqual(0, Call<ArchiveResponse>("UnlockMonsterInfoRequest", new ArchiveIdsRequest { Ids = [info.Id] }).Code, "monster info unlocks from kill state");
        var skill = TableReaderV2.Parse<MonsterSkillTable>().First();
        player.ArchiveMonsterKills[skill.GroupId] = 1;
        AssertEqual(0, Call<ArchiveResponse>("UnlockMonsterSkillRequest", new ArchiveIdsRequest { Ids = [skill.Id] }).Code, "skill unlocks from authored kill boundary");
        var weaponSetting = TableReaderV2.Parse<WeaponSettingTable>().First(row =>
            !harness.Session.character.Equips.Any(equip => equip.TemplateId == row.EquipId)
            && row.Condition.All(condition => TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>()
                .Single(rule => rule.Id == condition).Type == 31101));
        AssertEqual(20065004, Call<ArchiveResponse>("UnlockWeaponSettingRequest", new ArchiveIdsRequest { Ids = [weaponSetting.Id] }).Code, "weapon setting requires ownership");
        harness.Session.character.AddEquip((uint)weaponSetting.EquipId);
        AssertEqual(0, Call<ArchiveResponse>("UnlockArchiveWeaponRequest", new ArchiveIdsRequest { Ids = [weaponSetting.EquipId] }).Code, "owned weapon unlocks");
        AssertEqual(0, Call<ArchiveResponse>("UnlockWeaponSettingRequest", new ArchiveIdsRequest { Ids = [weaponSetting.Id] }).Code, "owned weapon setting unlocks");
        var alternateWeapon = TableReaderV2.Parse<AscNet.Table.V2.share.equip.EquipTable>()
            .First(row => row.Site == 0 && row.Type != 1 && Character.IsOwnableEquipTemplate(row)
                && !harness.Session.character.Equips.Any(equip => equip.TemplateId == row.Id));
        AssertEqual(20065004, Call<ArchiveResponse>("UnlockArchiveWeaponRequest", new ArchiveIdsRequest { Ids = [alternateWeapon.Id] }).Code, "other weapon families require ownership");
        harness.Session.character.AddEquip((uint)alternateWeapon.Id);
        AssertEqual(0, Call<ArchiveResponse>("UnlockArchiveWeaponRequest", new ArchiveIdsRequest { Ids = [alternateWeapon.Id] }).Code, "other weapon families unlock by site");
        var awarenessSetting = TableReaderV2.Parse<AwarenessSettingTable>().First();
        var awarenessPieces = TableReaderV2.Parse<AscNet.Table.V2.share.equip.EquipTable>()
            .Where(row => row.Site is >= 1 and <= 6 && row.SuitId == awarenessSetting.SuitId && Character.IsOwnableEquipTemplate(row)).ToArray();
        AssertEqual(true, awarenessPieces.Length > 0, "authored awareness suit has ownable pieces");
        AssertEqual(20065004, Call<ArchiveResponse>("UnlockArchiveAwarenessRequest", new ArchiveIdsRequest { Ids = [awarenessSetting.SuitId] }).Code, "awareness suit requires ownership");
        AssertEqual(20065004, Call<ArchiveResponse>("UnlockAwarenessSettingRequest", new ArchiveIdsRequest { Ids = [awarenessSetting.Id] }).Code, "awareness setting requires distinct pieces");
        foreach (var piece in awarenessPieces) harness.Session.character.AddEquip((uint)piece.Id);
        AssertEqual(0, Call<ArchiveResponse>("UnlockArchiveAwarenessRequest", new ArchiveIdsRequest { Ids = [awarenessSetting.SuitId] }).Code, "owned awareness suit unlocks");
        AssertEqual(0, Call<ArchiveResponse>("UnlockAwarenessSettingRequest", new ArchiveIdsRequest { Ids = [awarenessSetting.Id] }).Code, "awareness setting checks distinct owned pieces");
        Player reload = BsonSerializer.Deserialize<Player>(saved.LastSuccessfulReplacementBson ?? throw new InvalidDataException("Archive state not saved"));
        AssertEqual(true, reload.ArchiveUnlocks.Any(row => row.Id == setting.Id && row.Kind == "MonsterSetting"), "unlock survives durable reload");
        harness.Session.player = reload;
        player = reload;
        Type account = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        var login = (NotifyArchiveLoginData)RequiredMethod(account, "BuildNotifyArchiveLoginData", BindingFlags.NonPublic | BindingFlags.Static, [typeof(Player)]).Invoke(null, [player])!;
        AssertEqual(true, login.MonsterSettings.Contains((uint)setting.Id), "login advertises persisted setting");
        AssertEqual(true, login.MonsterUnlockIds.Contains((uint)monster.Id), "login advertises persisted monster");
        AssertEqual(0, Call<ArchiveResponse>("ArchiveEvaluateRequest", new ArchiveEvaluateRequest { Id = npc, Type = 1, Score = 4, Difficulty = 2 }).Code, "first player evaluates");
        Player other = CreateDrawCompatibilityPlayer(id + 1);
        other.ArchiveEvaluations.Add(new() { Id = npc, Type = 1, Score = 2, Difficulty = 4, LikeStatus = 1 });
        saved.FindResults = [other, BsonSerializer.Deserialize<Player>(player.ToBson())];
        var ratings = Call<ArchiveEvaluationsResponse>("GetEvaluateRequest", new ArchiveIdsRequest { Ids = [npc] });
        AssertEqual(6L, ratings.Evaluates.Single().Score, "two persisted scores aggregate");
        AssertEqual(2, ratings.Evaluates.Single().ScoreCount, "two player score denominator");
        AssertEqual(6L, ratings.Evaluates.Single().Difficulty, "two persisted difficulties aggregate");
        AssertEqual(0, Call<ArchiveResponse>("ArchiveGiveLikeRequest", new ArchiveGiveLikeRequest { Type = 1, LikeList = [new() { Id = npc, LikeStatus = 2 }] }).Code, "like succeeds");
        AssertEqual(0, Call<ArchiveResponse>("ArchiveGiveLikeRequest", new ArchiveGiveLikeRequest { Type = 1, LikeList = [new() { Id = npc, LikeStatus = 2 }] }).Code, "same like retry succeeds");
        ratings = Call<ArchiveEvaluationsResponse>("GetEvaluateRequest", new ArchiveIdsRequest { Ids = [npc] });
        AssertEqual(1, ratings.Evaluates.Single().LikeCount, "like retry counted once");
        AssertEqual(1, ratings.Evaluates.Single().DislikeCount, "other player's dislike retained");
        AssertEqual(0, Call<ArchiveResponse>("ArchiveGiveLikeRequest", new ArchiveGiveLikeRequest { Type = 1, LikeList = [new() { Id = npc, LikeStatus = 0 }] }).Code, "unlike succeeds");
        ratings = Call<ArchiveEvaluationsResponse>("GetEvaluateRequest", new ArchiveIdsRequest { Ids = [npc] });
        AssertEqual(0, ratings.Evaluates.Single().LikeCount, "unlike removes vote");
        AssertEqual(20065003, Call<ArchiveResponse>("ArchiveGiveLikeRequest", new ArchiveGiveLikeRequest { Type = 1, LikeList = [new() { Id = npc, LikeStatus = 3 }] }).Code, "invalid like rejected");
        AssertEqual(20065005, Call<ArchiveResponse>("ArchiveEvaluateRequest", new ArchiveEvaluateRequest { Id = npc, Type = 1, Tags = [int.MaxValue] }).Code, "invalid tag rejected");
        AssertEqual(20065007, Call<ArchiveResponse>("ArchiveEvaluateRequest", new ArchiveEvaluateRequest { Id = npc, Type = 4 }).Code, "unsupported evaluation type closed");
        AssertEqual(20065006, Call<ArchiveEvaluationsResponse>("GetStoryEvaluateRequest", new ArchiveIdsRequest { Ids = [1] }).Code, "missing story rating contract closed");
    }
}
