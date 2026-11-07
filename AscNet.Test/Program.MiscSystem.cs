using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.functional;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateMiscSystemCompatibility()
    {
        const long playerId = 99_480;
        FunctionalEntranceConfigTable entry = TableReaderV2.Parse<FunctionalEntranceConfigTable>().First(row => row.RedPointNum > 0);
        Character roster = CreateDrawCompatibilityCharacter(playerId);
        roster.Characters = [new CharacterData { Id = 1_021_001 }, new CharacterData { Id = 1_021_002 }];
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _);
        using LoopbackSessionHarness harness = new(roster, player, CreateDrawCompatibilityInventory(playerId, []), "misc-system");
        MethodInfo entranceData = RequiredMethod(typeof(Session).Assembly.GetType("AscNet.GameServer.Handlers.AccountModule")!,
            "BuildFunctionalEntranceData", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Player)]);
        int id = 99_480_000;
        T Call<T>(string name, object request) where T : class
        {
            InvokeRegisteredRequestHandler(name + "Request", harness.Session, ++id, request);
            return ReadResponsePayload<T>(harness, id, name + "Response", name);
        }

        // DormitoryDataRequest mirrors persisted dorm state (login projection).
        DormitoryDataResponse dorm = Call<DormitoryDataResponse>("DormitoryData", new DormitoryDataRequest());
        AssertEqual(0, dorm.Code, "dorm data succeeds");
        AssertEqual(2, dorm.CharacterList.Count, "dorm data lists every roster character");
        AssertEqual(true, player.Dorm.Rooms.Count > 0, "dorm data seeds free rooms");
        AssertEqual(player.Dorm.Rooms.Count, dorm.DormitoryList.Count, "dorm data lists persisted rooms");
        AssertEqual(player.Dorm.Furniture.Count, dorm.FurnitureList.Count, "dorm data lists persisted furniture");
        player.Dorm.Rooms[0].Name = "renamed-A";
        player.Dorm.Characters[0].Mood = 4_200;
        dorm = Call<DormitoryDataResponse>("DormitoryData", new DormitoryDataRequest());
        AssertEqual("renamed-A", dorm.DormitoryList.Single(room => room.DormitoryId == player.Dorm.Rooms[0].Id).DormitoryName, "dorm data reflects room name");
        AssertEqual(42, dorm.CharacterList.Single(c => c.CharacterId == player.Dorm.Characters[0].CharacterId).Mood, "dorm data reflects mood");

        // FunctionalEntranceRedPointNumUpdateRequest persists the table RedPointNum and replays it at login.
        AssertEqual(0, ((NotifyFunctionalEntranceData)entranceData.Invoke(null, [player])!).RedPointDatas.Count, "no red point before ack");
        AssertEqual(20421001, Call<FunctionalEntranceRedPointNumUpdateResponse>("FunctionalEntranceRedPointNumUpdate",
            new FunctionalEntranceRedPointNumUpdateRequest { FunctionId = -1 }).Code, "unknown function rejected");
        AssertEqual(0, player.FunctionalEntranceRedPoints.Count, "rejected request persists nothing");
        AssertEqual(0, Call<FunctionalEntranceRedPointNumUpdateResponse>("FunctionalEntranceRedPointNumUpdate",
            new FunctionalEntranceRedPointNumUpdateRequest { FunctionId = entry.FunctionId }).Code, "red point ack succeeds");
        AssertEqual(0, Call<FunctionalEntranceRedPointNumUpdateResponse>("FunctionalEntranceRedPointNumUpdate",
            new FunctionalEntranceRedPointNumUpdateRequest { FunctionId = entry.FunctionId }).Code, "red point ack idempotent");
        Player reloaded = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
            ?? throw new InvalidDataException("Red point ack did not save player."));
        var data = ((NotifyFunctionalEntranceData)entranceData.Invoke(null, [reloaded])!).RedPointDatas;
        AssertEqual(1, data.Count, "reloaded login carries one red point");
        AssertEqual(entry.RedPointNum, data[entry.FunctionId], "reloaded login carries table RedPointNum");

        // LimitedLoginVerifyRequest: no quiz is ever issued, so it fails closed.
        LimitedLoginVerifyResponse verify = Call<LimitedLoginVerifyResponse>("LimitedLoginVerify",
            new LimitedLoginVerifyRequest { QuizIdx = 1, Answer = 1, UseTime = 3 });
        AssertEqual(20138001, verify.Code, "limited login verify without issued quiz");
        AssertEqual(false, verify.IsCorrect, "limited login verify never reports correct");
    }
}
