using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.cerberusgame;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Diagnostics;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateCerberusGameCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        ValidateCerberusGameTasks();
        CerberusBranchCompatibility();
        CerberusSettlementRecoveryCompatibility();
        foreach (string name in new[] { nameof(CerberusGamePassStoryPointRequest), nameof(CerberusGameSetTeamRequest), nameof(PreFightRequest), nameof(FightSettleRequest), nameof(EnterStoryRequest) })
            _ = GetRegisteredRequestHandlerMethod(name);

        var points = TableReaderV2.Parse<CerberusGameStoryPointTable>().ToDictionary(row => row.Id);
        var lines = TableReaderV2.Parse<CerberusGameStoryLineTable>();
        var line = lines.First(row => Convert.ToInt32(row.OpenCondition) == 0
            && row.StoryPointIds.Any(id => points.TryGetValue(id, out var point)
                && point.StoryPointType == 1 && !point.PrePointIds.Any(id => id > 0)));
        var story = line.StoryPointIds.Select(id => points[id]).First(row => row.StoryPointType == 1 && !row.PrePointIds.Any(id => id > 0));
        var locked = line.StoryPointIds.Select(id => points[id]).First(row => row.StoryPointType is 1 or 2 && row.PrePointIds.Any(id => id > 0));
        var chapterRows = TableReaderV2.Parse<CerberusGameChapterTable>();
        var challenge = TableReaderV2.Parse<CerberusGameChallengeTable>().First(row => Convert.ToInt32(row.PreStageId) == 0
            && chapterRows.Any(chapter => chapter.Id == row.ChapterId && Convert.ToInt32(chapter.OpenCondition) == 0));
        uint battleStageId = checked((uint)challenge.Id);
        uint ownedId = checked((uint)challenge.CharacterIds.First(id => id > 0));
        int foreignChapter = chapterRows.First(row => row.Id != line.ChapterId).Id;
        const long uid = 98_780;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(out var players, out _, out _, out var stages);
        Character character = CreateDrawCompatibilityCharacter(uid);
        character.Characters.Add(CreateLoginAccountCompatibilityCharacter(ownedId, fashionId: 0));
        using LoopbackSessionHarness harness = new(character, CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid, []), "cerberus-game");
        CerberusUnlockFunctionalGate(harness.Session);
        int packetId = 98_781;

        (T Response, List<(string Name, JObject Body)> Pushes) Call<T>(string requestName, object request, string responseName)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(requestName, harness.Session, id, request);
            List<(string Name, JObject Body)> pushes = [];
            for (int index = 0; index < 64; index++)
            {
                Packet packet = harness.ReadPacket($"Cerberus {requestName} packet {index}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    pushes.Add((push.Name, JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content))));
                    continue;
                }
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, response.Id, "Cerberus response correlation");
                return (ReadResponsePayload<T>(packet, responseName), pushes);
            }
            throw new InvalidDataException($"Missing Cerberus {responseName}.");
        }

        NotifyCerberusGameData Login() => MessagePackSerializer.Deserialize<NotifyCerberusGameData>(MessagePackSerializer.Serialize(CerberusGameModule.BuildNotify(harness.Session)));
        string Bag() => string.Join(";", harness.Session.inventory.Items.OrderBy(item => item.Id).Select(item => $"{item.Id}:{item.Count}"));
        int Story(int pointId, int? chapter = null) => Call<CerberusGamePassStoryPointResponse>(nameof(CerberusGamePassStoryPointRequest),
            new CerberusGamePassStoryPointRequest { ChapterId = chapter ?? line.ChapterId, StoryLineId = line.Id, StoryPointId = pointId }, nameof(CerberusGamePassStoryPointResponse)).Response.Code;
        int Team(CerberusGameTeamInfo team) => Call<CerberusGameSetTeamResponse>(nameof(CerberusGameSetTeamRequest),
            new CerberusGameSetTeamRequest { StageId = battleStageId, TeamInfo = team }, nameof(CerberusGameSetTeamResponse)).Response.Code;
        CerberusGameTeamInfo Robots() => new() { RobotIdList = challenge.RobotIds.ToList() };

        foreach (int timeId in TableReaderV2.Parse<CerberusGameActivityTable>().Select(row => Convert.ToInt32(row.TimeId))
            .Concat(chapterRows.Select(row => Convert.ToInt32(row.TimeId))).Where(id => id > 0).Distinct())
            AssertEqual(true, AscNet.GameServer.Game.ActivityScheduleService.IsOpen(timeId, DateTimeOffset.UtcNow), "authored Cerberus schedule available");
        NotifyCerberusGameData fresh = Login();
        AssertEqual(true, fresh.CerberusGameData.ActivityId > 0, "Cerberus table-selected activity");
        AssertEqual(true, fresh.CerberusGameData.ChapterInfos.Count > 0, "Cerberus wrapped ChapterInfos");
        AssertEqual(0, fresh.CerberusGameData.StageInfos.Count, "Cerberus fresh stage list");
        AssertEqual(true, fresh.CerberusGameData.ChapterInfos.All(chapter => chapter.TeamInfo.CharacterIdList.Count == 3 && chapter.TeamInfo.RobotIdList.Count == 3), "Cerberus positional team shape");
        AssertEqual(true, Story(int.MaxValue) != 0, "unknown story rejected");
        AssertEqual(true, Story(story.Id, foreignChapter) != 0, "wrong chapter rejected");
        AssertEqual(true, Story(locked.Id) != 0, "locked story rejected");
        AssertEqual(true, Story(story.Id) != 0, "story cannot be acknowledged before completion");
        int storyStage = story.StoryPointTypeParams[0];
        // Original XFubenAgency.FinishStoryRequest sends EnterStoryRequest, not a separate FinishStory RPC.
        AssertEqual(0, Call<EnterStoryResponse>(nameof(EnterStoryRequest), new EnterStoryRequest { StageId = storyStage }, nameof(EnterStoryResponse)).Response.Code, "story completion");
        AssertEqual(true, Login().CerberusGameData.StageInfos.Any(stage => stage.StageId == storyStage && stage.Passed), "completed story stage persisted");
        AssertEqual(0, Story(story.Id), "valid story acknowledgement");
        int storySaves = players.ReplaceOneCalls;
        string storyBag = Bag();
        AssertEqual(0, Story(story.Id), "story retry idempotent success");
        AssertEqual(storySaves, players.ReplaceOneCalls, "story retry does not save duplicate progress");
        AssertEqual(storyBag, Bag(), "story retry does not duplicate rewards");
        AssertEqual(1, Login().CerberusGameData.ChapterInfos.SelectMany(chapter => chapter.StoryLineInfos).SelectMany(storyLine => storyLine.PassStoryPoints).Count(id => id == story.Id), "story acknowledgement stored once");

        AssertEqual(0, Team(Robots()), "authored robot team accepted");
        var unknownRobot = Robots(); unknownRobot.RobotIdList[0] = int.MaxValue;
        AssertEqual(true, Team(unknownRobot) != 0, "unknown robot rejected");
        var duplicateRobot = Robots(); duplicateRobot.RobotIdList[1] = duplicateRobot.RobotIdList[0];
        AssertEqual(true, Team(duplicateRobot) != 0, "duplicate robot rejected");
        CerberusGameTeamInfo unowned = new() { CharacterIdList = [uint.MaxValue, 0, 0] };
        AssertEqual(true, Team(unowned) != 0, "unowned character rejected");
        unowned.CharacterIdList[0] = checked((uint)challenge.CharacterIds.First(id => id > 0 && id != ownedId));
        AssertEqual(true, Team(unowned) != 0, "authored but unowned character rejected");
        var foreignRobot = Robots();
        foreignRobot.RobotIdList[0] = TableReaderV2.Parse<AscNet.Table.V2.share.robot.RobotTable>().First(row => !challenge.RobotIds.Contains(row.Id)).Id;
        AssertEqual(true, Team(foreignRobot) != 0, "known but unauthorized robot rejected");
        var mixed = Robots(); mixed.RobotIdList[0] = 0; mixed.CharacterIdList[0] = ownedId;
        AssertEqual(0, Team(mixed), "owned character and authored robots accepted together");
        var overlapping = Robots(); overlapping.CharacterIdList[0] = ownedId;
        AssertEqual(true, Team(overlapping) != 0, "overlapping character and robot slots rejected");
        var badPosition = Robots(); badPosition.CaptainPos = 4;
        AssertEqual(true, Team(badPosition) != 0, "invalid captain position rejected");
        badPosition = Robots(); badPosition.FirstFightPos = 0;
        AssertEqual(true, Team(badPosition) != 0, "invalid first-fight position rejected");
        var shortTeam = Robots(); shortTeam.RobotIdList.RemoveAt(2);
        AssertEqual(true, Team(shortTeam) != 0, "malformed positional team rejected");
        var empty = new CerberusGameTeamInfo();
        AssertEqual(true, Team(empty) != 0, "empty team rejected");

        PreFightRequest PreRequest() => new() { PreFightData = new() { StageId = battleStageId, ChallengeCount = 1,
            CardIds = mixed.CharacterIdList.ToList(), RobotIds = mixed.RobotIdList.ToList(), CaptainPos = mixed.CaptainPos, FirstFightPos = mixed.FirstFightPos } };
        var invalidPre = PreRequest(); invalidPre.PreFightData.ChallengeCount = 2;
        AssertEqual(true, Call<PreFightResponse>(nameof(PreFightRequest), invalidPre, nameof(PreFightResponse)).Response.Code != 0, "Cerberus multi-challenge rejected");
        var pre = Call<PreFightResponse>(nameof(PreFightRequest), PreRequest(), nameof(PreFightResponse)).Response;
        AssertEqual(0, pre.Code, "Cerberus battle pre-fight");
        AssertEqual(battleStageId, pre.FightData.StageId, "fight derives selected stage");
        FightSettleRequest settle = CreateMissingStageSettleRequest(battleStageId, pre.FightData.FightId, uid);
        settle.Result.AddStars = 1;
        settle.Result.CustomData = new Dictionary<long, object>
        {
            [uid] = new Dictionary<string, object> { ["Dict"] = new Dictionary<int, int> { [3] = 1 } }
        };
        string beforeReward = Bag();
        var clear = Call<FightSettleResponse>(nameof(FightSettleRequest), settle, nameof(FightSettleResponse));
        AssertEqual(0, clear.Response.Code, "Cerberus battle settlement");
        var stagePush = clear.Pushes.Single(push => push.Name == nameof(NotifyCerberusGameStageInfo)).Body[nameof(NotifyCerberusGameStageInfo.CerberusGameStageInfo)]!;
        AssertEqual(battleStageId, stagePush.Value<uint>("StageId"), "Cerberus stage-info push selected stage");
        AssertEqual(true, stagePush.Value<bool>("Passed"), "Cerberus stage-info push passed");
        AssertEqual(1, stagePush.Value<int>("StarMark"), "Cerberus stage-info push derived stars");
        AssertEqual(true, stages.LastReplacement!.Stages[checked((int)battleStageId)].Passed, "generic stage saved");
        NotifyCerberusGameData progressed = Login();
        var persistedStage = progressed.CerberusGameData.StageInfos.Single(stage => stage.StageId == battleStageId);
        AssertEqual(0, persistedStage.StageParams.Count, "non-branch stage cannot persist route custom data");
        AssertEqual(1, persistedStage.StarMark, "login derives persisted star mask");
        AssertEqual(ownedId, persistedStage.TeamInfo.CharacterIdList[0], "login preserves distinct selected team");
        string rewardBag = Bag();
        AssertEqual(true, rewardBag != beforeReward, "table-authored first star reward really grants inventory");
        int authoredStarReward = TableReaderV2.Parse<StageTable>().Single(row => row.StageId == battleStageId).StarRewardId[0];
        AssertEqual(true, persistedStage.GotStarRewardIds.Contains(authoredStarReward), "star reward receipt derives authored reward id");
        int clearSaves = players.ReplaceOneCalls;
        int stageSaves = stages.ReplaceOneCalls;
        var retry = Call<FightSettleResponse>(nameof(FightSettleRequest), settle, nameof(FightSettleResponse));
        AssertEqual(0, retry.Response.Code, "same fight retry accepted");
        AssertEqual(MessagePackSerializer.SerializeToJson(clear.Response), MessagePackSerializer.SerializeToJson(retry.Response), "retry reproduces durable settlement response");
        AssertEqual(rewardBag, Bag(), "settlement retry does not duplicate reward inventory");
        AssertEqual(clearSaves, players.ReplaceOneCalls, "settlement retry does not save progress twice");
        AssertEqual(stageSaves, stages.ReplaceOneCalls, "settlement retry does not save generic stage twice");

        byte[] saved = (players.LastReplacement ?? throw new InvalidDataException("Cerberus player was not persisted.")).ToBson();
        harness.Session.player = BsonSerializer.Deserialize<Player>(saved);
        harness.Session.stage = BsonSerializer.Deserialize<Stage>(stages.LastReplacement!.ToBson());
        NotifyCerberusGameData reloaded = Login();
        AssertEqual(MessagePackSerializer.SerializeToJson(progressed), MessagePackSerializer.SerializeToJson(reloaded), "Cerberus saved BSON reload preserves complete client projection");
        AssertEqual(0, Story(story.Id), "story retry after persistence reload");
        AssertEqual(0, Call<FightSettleResponse>(nameof(FightSettleRequest), settle, nameof(FightSettleResponse)).Response.Code, "settlement receipt survives BSON reload");
        settle.Result.AddStars = 0;
        AssertEqual(true, Call<FightSettleResponse>(nameof(FightSettleRequest), settle, nameof(FightSettleResponse)).Response.Code != 0, "changed report cannot reuse settlement receipt");
        AssertEqual(rewardBag, Bag(), "reloaded retry preserves inventory");
        AssertEqual(true, MessagePackSerializer.SerializeToJson(fresh) != MessagePackSerializer.SerializeToJson(reloaded), "fresh and progressed outputs independently derive from state");
        CerberusOriginalLua(fresh, reloaded, clear.Pushes.Single(push => push.Name == nameof(NotifyCerberusGameStageInfo)).Body);
        CerberusExtraRobotCompatibility();
        Console.WriteLine("Cerberus compatibility: table-selected login/story/team/battle, stars, retries, rewards, BSON reload, original EN Lua consumer passed.");
    }

    private static void CerberusSettlementRecoveryCompatibility()
    {
        var chapters = TableReaderV2.Parse<CerberusGameChapterTable>();
        var stageRows = TableReaderV2.Parse<StageTable>();
        var challenge = TableReaderV2.Parse<CerberusGameChallengeTable>().First(row => row.PreStageId is not > 0
            && chapters.Any(chapter => chapter.Id == row.ChapterId && chapter.OpenCondition is not > 0)
            && stageRows.Any(stage => stage.StageId == row.Id && stage.StarRewardId.Count > 0 && stage.StarRewardId[0] > 0));
        uint stageId = checked((uint)challenge.Id);
        const long uid = 98_850;
        int packetId = 98_860;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(out var players, out _, out var inventories, out var stages);
        PreFightRequest Request() => new()
        {
            // The battle room sends the player's global assist toggle (issue #49); it must not lock the stage.
            PreFightData = new() { StageId = stageId, ChallengeCount = 1, IsHasAssist = true, CardIds = [0, 0, 0],
                RobotIds = challenge.RobotIds.ToList(), CaptainPos = 1, FirstFightPos = 1 }
        };
        PreFightResponse Pre(LoopbackSessionHarness harness)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, id, Request());
            return ReadResponsePayload<PreFightResponse>(harness, id, nameof(PreFightResponse), "recovered pre-fight", maxPacketsToRead: 64);
        }
        FightSettleResponse Settle(LoopbackSessionHarness harness, FightSettleRequest request)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, id, request);
            return ReadResponsePayload<FightSettleResponse>(harness, id, nameof(FightSettleResponse), "recovery settle", maxPacketsToRead: 64);
        }
        static string Bag(Inventory inventory) => string.Join(";", inventory.Items.OrderBy(item => item.Id).Select(item => $"{item.Id}:{item.Count}"));
        byte[] originalStage;
        byte[] durablePlayer;
        byte[] durableInventory;
        FightSettleRequest first;
        string granted;
        using (LoopbackSessionHarness interrupted = new(CreateDrawCompatibilityCharacter(uid), sessionId: "cerberus-interrupted-settle"))
        {
            CerberusUnlockFunctionalGate(interrupted.Session);
            originalStage = interrupted.Session.stage.ToBson();
            var pre = Pre(interrupted);
            AssertEqual(0, pre.Code, "failure fixture pre-fight");
            first = CreateMissingStageSettleRequest(stageId, pre.FightData.FightId, uid);
            first.Result.AddStars = 1;
            string before = Bag(interrupted.Session.inventory);
            stages.ThrowOnReplaceOne = true;
            try
            {
                InvokeRegisteredRequestHandler(nameof(FightSettleRequest), interrupted.Session, packetId++, first);
                throw new InvalidDataException("Expected injected Cerberus stage persistence failure.");
            }
            catch (InvalidDataException exception) when (exception.InnerException is MongoDB.Driver.MongoException) { }
            finally { stages.ThrowOnReplaceOne = false; }
            durablePlayer = players.LastSuccessfulReplacementBson ?? throw new InvalidDataException("Missing frozen player journal.");
            durableInventory = inventories.LastSuccessfulReplacementBson ?? throw new InvalidDataException("Missing committed star reward.");
            granted = Bag(BsonSerializer.Deserialize<Inventory>(durableInventory));
            AssertEqual(true, granted != before, "failure occurs after a real table-derived reward grant");
            AssertEqual(true, BsonSerializer.Deserialize<Player>(durablePlayer).CerberusGame.Fight?.Result is not null, "pending full result survives failed stage write");
        }
        using LoopbackSessionHarness reloaded = new(CreateDrawCompatibilityCharacter(uid),
            BsonSerializer.Deserialize<Player>(durablePlayer), BsonSerializer.Deserialize<Inventory>(durableInventory), "cerberus-settle-reload");
        reloaded.Session.stage = BsonSerializer.Deserialize<Stage>(originalStage);
        var next = Pre(reloaded);
        AssertEqual(0, next.Code, "new pre-fight recovers frozen settlement instead of blocking permanently");
        AssertEqual(granted, Bag(reloaded.Session.inventory), "recovery never grants the committed reward twice");
        AssertEqual(1L, reloaded.Session.stage.Stages[stageId].PassTimesTotal, "recovered frozen stage increments clear count once");
        AssertEqual(true, next.FightData.FightId != unchecked((uint)first.Result.FightId), "new attempt receives a distinct identity");
        AssertEqual(1, reloaded.Session.player.CerberusGame.FightReceipts.Count, "recovery retains one completed receipt");
        var firstReceipt = reloaded.Session.player.CerberusGame.FightReceipts[unchecked((uint)first.Result.FightId)];
        AssertEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(MessagePackSerializer.Serialize(first.Result))),
            Convert.ToHexString(firstReceipt.ResultHash), "receipt retains only the request hash");
        FightSettleRequest second = CreateMissingStageSettleRequest(stageId, next.FightData.FightId, uid);
        second.Result.AddStars = 1;
        var secondResponse = Settle(reloaded, second);
        AssertEqual(0, secondResponse.Code, "second distinct fight settles after recovery");
        AssertEqual(granted, Bag(reloaded.Session.inventory), "second clear does not duplicate first star reward");
        AssertEqual(2L, reloaded.Session.stage.Stages[stageId].PassTimesTotal, "two distinct accepted fights count twice");
        var receipts = reloaded.Session.player.CerberusGame.FightReceipts;
        AssertEqual(1, receipts.Count, "completed receipt storage stays bounded across fights");
        AssertEqual(true, receipts.ContainsKey(next.FightData.FightId), "only latest fight receipt retained");
        AssertEqual(32, receipts[next.FightData.FightId].ResultHash.Length, "completed result storage is fixed-size SHA256");
        var replay = Settle(reloaded, second);
        AssertEqual(MessagePackSerializer.SerializeToJson(secondResponse), MessagePackSerializer.SerializeToJson(replay), "latest hash-backed receipt replays exact response");
        AssertEqual(true, Settle(reloaded, first).Code != 0, "evicted old fight cannot settle again");
        AssertEqual(granted, Bag(reloaded.Session.inventory), "replay and evicted retry never duplicate reward");
        AssertEqual(2L, reloaded.Session.stage.Stages[stageId].PassTimesTotal, "replay and evicted retry preserve clear count");
    }

    private static void CerberusBranchCompatibility()
    {
        var conditions = TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>().ToDictionary(row => row.Id);
        var points = TableReaderV2.Parse<CerberusGameStoryPointTable>();
        var lines = TableReaderV2.Parse<CerberusGameStoryLineTable>();
        var robots = TableReaderV2.Parse<AscNet.Table.V2.share.robot.RobotTable>().ToDictionary(row => row.Id);
        var roles = TableReaderV2.Parse<AscNet.Table.V2.client.fuben.cerberusgame.CerberusGameRoleTable>();
        var branches = conditions.Values.Where(row => row.Type == 10201 && row.Params.Count == 2
            && points.Any(point => point.StoryPointType == 3 && point.StoryPointTypeParams[0] == row.Params[0]))
            .GroupBy(row => row.Params[0]).ToList();
        AssertEqual(true, branches.Count > 0, "authoritative Cerberus route conditions exist");
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(out var players, out _, out _, out _);
        long uid = 98_800;
        int packetId = 98_820;
        foreach (var branch in branches)
        {
            var source = points.Single(point => point.StoryPointType == 3 && point.StoryPointTypeParams[0] == branch.Key);
            var line = lines.Single(row => row.StoryPointIds.Contains(source.Id));
            var choices = branch.Select(row => row.Params[1]).Order().ToArray();
            AssertEqual("1,2", string.Join(",", choices), "native stored route choices, not talk option order");
            var targets = points.Where(point => point.PrePointIds.Contains(source.Id) && point.OpenCondition > 0)
                .Select(point => (Point: point, Leaves: (conditions[point.OpenCondition!.Value].Formula
                    ?? throw new InvalidDataException("Authored branch condition has no formula.")).Split(['&', '|'])
                    .Select(int.Parse).Where(id => conditions[id].Type == 10201).Select(id => conditions[id]).ToList()))
                .Where(target => target.Leaves.Any(leaf => leaf.Params[0] == branch.Key)).ToList();
            AssertEqual(2, targets.Count, "both authored route targets resolved from formulas");
            foreach (string scenario in new[] { "choice1", "choice2", "absent", "no-custom", "invalid", "wrong-owner", "loss" })
            {
                long id = uid++;
                using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(id), sessionId: $"cerberus-route-{source.Id}-{scenario}");
                CerberusUnlockFunctionalGate(harness.Session);
                harness.Session.player.CerberusGame.PassStoryPoints.AddRange(source.PrePointIds);
                var team = roles.Where(role => role.Chapter == line.ChapterId && role.Difficulty == line.Difficulty)
                    .Select(role => role.RoleId).Where(robotId => source.StoryPointTypeParams.Skip(1).Contains(robots[robotId].CharacterId))
                    .Distinct().ToList();
                AssertEqual(3, team.Count, "branch producer deploys authored three-robot team");
                int preId = packetId++;
                InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, preId, new PreFightRequest
                {
                    PreFightData = new() { StageId = checked((uint)branch.Key), ChallengeCount = 1, CardIds = [0, 0, 0],
                        RobotIds = team, CaptainPos = 1, FirstFightPos = 1 }
                });
                var pre = ReadResponsePayload<PreFightResponse>(harness, preId, nameof(PreFightResponse), "route pre-fight", maxPacketsToRead: 32);
                AssertEqual(0, pre.Code, "branch producer pre-fight accepted");
                FightSettleRequest settle = CreateMissingStageSettleRequest(checked((uint)branch.Key), pre.FightData.FightId, id);
                settle.Result.AddStars = 0;
                settle.Result.IsWin = scenario != "loss";
                int choice = scenario == "choice1" ? choices[0] : choices[1];
                Dictionary<int, int> records = new() { [1] = source.Id, [2] = branch.Key };
                if (scenario != "absent") records[3] = scenario == "invalid" ? choices.Max() + 1 : choice;
                // Installed DoSetCustomData1#2001|3|1 / ...|3|2 writes the local player's Dict key 3.
                settle.Result.CustomData = new Dictionary<long, object>
                {
                    [scenario == "wrong-owner" ? id + 1 : id] = new Dictionary<string, object> { ["Dict"] = records }
                };
                if (scenario == "no-custom") settle.Result.CustomData = null;
                string beforePlayer = Convert.ToHexString(harness.Session.player.ToBson());
                string beforeInventory = Convert.ToHexString(harness.Session.inventory.ToBson());
                int settleId = packetId++;
                InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, settleId, settle);
                var response = ReadResponsePayload<FightSettleResponse>(harness, settleId, nameof(FightSettleResponse), "route settle", maxPacketsToRead: 64);
                AssertEqual(scenario is "invalid" or "wrong-owner", response.Code != 0, "untrusted route telemetry rejected; absent/loss accepted without a route");
                if (scenario is "invalid" or "wrong-owner")
                {
                    AssertEqual(beforePlayer, Convert.ToHexString(harness.Session.player.ToBson()), "rejected route report preserves journal and progression");
                    AssertEqual(beforeInventory, Convert.ToHexString(harness.Session.inventory.ToBson()), "rejected route report grants nothing");
                }
                bool valid = scenario is "choice1" or "choice2";
                var route = CerberusGameModule.BuildNotify(harness.Session).CerberusGameData.StageInfos.SingleOrDefault(stage => stage.StageId == branch.Key);
                AssertEqual(valid ? choice.ToString() : "", string.Join(",", route?.StageParams ?? []), "only valid winning local-player Dict[3] becomes StageParams");
                foreach (var target in targets)
                {
                    bool expected = valid && target.Leaves.Any(leaf => leaf.Params[0] == branch.Key && leaf.Params[1] == choice);
                    AssertEqual(expected, CerberusGameModule.ValidateStage(harness.Session, checked((uint)target.Point.StoryPointTypeParams[0])) == 0,
                        "stored route unlocks exactly its authored point");
                }
                byte[] saved = (players.LastReplacement ?? throw new InvalidDataException("Branch report was not persisted.")).ToBson();
                harness.Session.player = BsonSerializer.Deserialize<Player>(saved);
                var reloaded = CerberusGameModule.BuildNotify(harness.Session).CerberusGameData.StageInfos.SingleOrDefault(stage => stage.StageId == branch.Key);
                AssertEqual(valid ? choice.ToString() : "", string.Join(",", reloaded?.StageParams ?? []), "route survives BSON reload without unrelated keys");
                if (valid)
                {
                    var fallback = (conditions[targets[0].Point.OpenCondition!.Value].Formula
                        ?? throw new InvalidDataException("Authored branch condition has no formula.")).Split(['&', '|'])
                        .Select(int.Parse).Select(conditionId => conditions[conditionId]).Single(row => row.Type == 10105);
                    var fallbackPoint = points.Single(point => point.StoryPointType is 1 or 3 && point.StoryPointTypeParams[0] == fallback.Params[0]);
                    harness.Session.player.CerberusGame.PassStoryPoints.Add(fallbackPoint.Id);
                    foreach (var target in targets)
                        AssertEqual(0, CerberusGameModule.ValidateStage(harness.Session, checked((uint)target.Point.StoryPointTypeParams[0])), "authored later clear opens both routes");
                }
            }
        }
    }

    private static void CerberusUnlockFunctionalGate(Session session)
    {
        var functions = TableReaderV2.Parse<AscNet.Table.V2.client.functional.SkipFunctionalTable>()
            .Where(row => row.UiName == "OnOpenCerberusGame" && row.FunctionalId > 0).Select(row => row.FunctionalId!.Value).ToHashSet();
        var ids = TableReaderV2.Parse<AscNet.Table.V2.share.functional.FunctionalOpenTable>()
            .Where(row => functions.Contains(row.Id)).SelectMany(row => row.Condition).ToHashSet();
        var gates = TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>().Where(row => ids.Contains(row.Id)).ToList();
        AssertEqual(true, gates.Count > 0 && gates.All(row => row.Type == 10101), "mode functional fixtures derive level gate from tables");
        session.player.PlayerData.Level = checked((uint)gates.Max(row => row.Params[0]));
    }

    private static void CerberusExtraRobotCompatibility()
    {
        var conditions = TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>().ToDictionary(row => row.Id);
        var point = TableReaderV2.Parse<CerberusGameStoryPointTable>().First(row => row.ExtraRobotId > 0);
        var line = TableReaderV2.Parse<CerberusGameStoryLineTable>().Single(row => row.StoryPointIds.Contains(point.Id));
        const long uid = 98_790;
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), sessionId: "cerberus-extra-robot");
        CerberusUnlockFunctionalGate(harness.Session);
        foreach (int id in point.PrePointIds)
            harness.Session.player.CerberusGame.PassStoryPoints.Add(id);
        // This fixture uses the authored alternative clear gate; branch report tests must use the native report producer.
        var clearGate = (conditions[point.OpenCondition!.Value].Formula
            ?? throw new InvalidDataException("Authored extra-robot condition has no formula.")).Split(['&', '|']).Select(int.Parse)
            .Select(id => conditions[id]).Single(row => row.Type == 10105);
        int clearedPoint = TableReaderV2.Parse<CerberusGameStoryPointTable>()
            .Single(row => row.StoryPointType is 1 or 3 && row.StoryPointTypeParams[0] == clearGate.Params[0]).Id;
        harness.Session.player.CerberusGame.PassStoryPoints.Add(clearedPoint);
        var robots = TableReaderV2.Parse<AscNet.Table.V2.share.robot.RobotTable>().ToDictionary(row => row.Id);
        var allowed = TableReaderV2.Parse<AscNet.Table.V2.client.fuben.cerberusgame.CerberusGameRoleTable>()
            .Where(row => row.Chapter == line.ChapterId && row.Difficulty == line.Difficulty)
            .Select(row => row.RoleId).Where(id => robots.TryGetValue(id, out var robot)
                && point.StoryPointTypeParams.Skip(1).Contains(robot.CharacterId)).Distinct().ToList();
        AssertEqual(2, allowed.Count, "extra-support fixture has two authored selectable robots");
        PreFightRequest.PreFightRequestPreFightData request = new()
        {
            StageId = checked((uint)point.StoryPointTypeParams[0]), ChallengeCount = 1,
            CardIds = [0, 0, 0], RobotIds = [allowed[0], allowed[1], 0], CaptainPos = 1, FirstFightPos = 1
        };
        AssertEqual(0, CerberusGameModule.ValidateStage(harness.Session, request.StageId), "extra-support fixture unlocked from authored clear gate");
        AssertEqual(true, CerberusGameModule.ValidatePreFight(harness.Session, request) != 0, "required support robot cannot be omitted");
        request.RobotIds[2] = point.ExtraRobotId!.Value;
        AssertEqual(0, CerberusGameModule.ValidatePreFight(harness.Session, request), "agency-injected support robot accepted");
        request.CaptainPos = 3;
        AssertEqual(true, CerberusGameModule.ValidatePreFight(harness.Session, request) != 0, "support robot cannot be captain");
        request.CaptainPos = 1;
        request.FirstFightPos = 3;
        AssertEqual(true, CerberusGameModule.ValidatePreFight(harness.Session, request) != 0, "support robot cannot be first fighter");
    }

    private static void CerberusOriginalLua(NotifyCerberusGameData fresh, NotifyCerberusGameData progressed, JObject stagePush)
    {
        string relative = Path.Combine("PGR_Data", "en", "lua", "matrix", "xmodule", "xcerberusgame", "XCerberusGameAgency.lua");
        string? agency = Environment.GetEnvironmentVariable("ASCNET_CERBERUS_AGENCY");
        if (string.IsNullOrWhiteSpace(agency))
            agency = new[] { Path.Combine("..", "..", relative), Path.Combine("/Volumes/Lucia/PGR-native-research", relative), Path.Combine("/PGR-native-research", relative) }.FirstOrDefault(File.Exists);
        if (agency is null || !File.Exists(agency))
            throw new FileNotFoundException("Original EN consumer required: set ASCNET_CERBERUS_AGENCY to XCerberusGameAgency.lua and ASCNET_LUA to the Lua executable.");
        string Literal(JToken? token) => token switch
        {
            JObject obj => "{" + string.Join(",", obj.Properties().Select(property => $"[{Literal(new JValue(property.Name))}]={Literal(property.Value)}")) + "}",
            JArray array => "{" + string.Join(",", array.Select(Literal)) + "}",
            null => "nil",
            _ when token.Type == JTokenType.Null => "nil",
            _ when token.Type == JTokenType.String => "\"" + token.Value<string>()!.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"",
            _ when token.Type == JTokenType.Boolean => token.Value<bool>() ? "true" : "false",
            _ => token.ToString(Newtonsoft.Json.Formatting.None)
        };
        string script = """
            package.preload['XModule/XBase/XFubenSimulationChallengeAgency'] = function() return {} end
            XClass = function() return {} end
            local reset = false
            XSaveTool = {SaveData=function(key,value) assert(key=='LastSelectStoryLineDifficulty' and value==nil); reset=true end}
            local agency = assert(loadfile(AGENCY))()
            local function consume(wire)
                local stories,teams,stages = {},{},{}
                local self = setmetatable({_Model={StageTeamInfoByServer={}}},{__index=agency})
                function self:GetXStoryPointById(id) return {SetPassed=function(_,v) stories[id]=v end} end
                function self:GetXTeamByChapterId(id) return {RefreshDataByCerberuseTeamInfo=function(_,v) assert(type(v.CharacterIdList)=='table' and #v.RobotIdList==3); teams[id]=v end} end
                function self:GetXStageById(id) return {SetServerData=function(_,v) assert(v.StageId==id and type(v.StarMark)=='number'); stages[id]=v end} end
                reset=false
                self:NotifyCerberusGameData(wire)
                assert(reset and self._Model.ActivityId==wire.CerberusGameData.ActivityId)
                for _,chapter in ipairs(wire.CerberusGameData.ChapterInfos) do
                    assert(teams[chapter.ChapterId]==chapter.TeamInfo)
                    for _,line in ipairs(chapter.StoryLineInfos) do for _,id in ipairs(line.PassStoryPoints) do assert(stories[id]) end end
                end
                for _,stage in ipairs(wire.CerberusGameData.StageInfos) do assert(stages[stage.StageId]==stage and self._Model.StageTeamInfoByServer[stage.StageId]==stage.TeamInfo) end
                return self,stages
            end
            consume(FRESH)
            local self,stages = consume(PROGRESSED)
            self:NotifyCerberusGameStageInfo(PUSH)
            local stage=PUSH.CerberusGameStageInfo
            assert(stages[stage.StageId]==stage and self._Model.StageTeamInfoByServer[stage.StageId]==stage.TeamInfo)
            print('Original EN NotifyCerberusGameData and NotifyCerberusGameStageInfo consumers passed')
            """;
        script = "local AGENCY=" + Literal(new JValue(Path.GetFullPath(agency)))
            + "\nlocal FRESH=" + Literal(JToken.Parse(MessagePackSerializer.SerializeToJson(fresh)))
            + "\nlocal PROGRESSED=" + Literal(JToken.Parse(MessagePackSerializer.SerializeToJson(progressed)))
            + "\nlocal PUSH=" + Literal(stagePush) + "\n" + script;
        ProcessStartInfo info = new(Environment.GetEnvironmentVariable("ASCNET_LUA") ?? "lua", "-")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not launch original EN Lua consumer.");
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidDataException($"Original EN Cerberus Lua failed ({process.ExitCode}): {stderr}");
        Console.Write(stdout);
    }
}
