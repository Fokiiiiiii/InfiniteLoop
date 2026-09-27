using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    // Source rows: FestivalActivity 68 (TimeId 50202) = 15010701..08 (07 is the only fight), Id 24 retro starts at story 30130507;
    // ExperimentLevel 139 SingStageId 30101143, 138 SingStageId 30101144.
    private static void ValidateFestivalExperimentClearRelogin()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForStudyProgressionCompatibility(
            out RecordingMongoCollectionProxy<AscNet.Common.Database.Stage> stageSaves);
        Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        int packetId = 48_700;

        LoopbackSessionHarness Harness(long uid, AscNet.Common.Database.Stage? stage = null)
        {
            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(uid);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1_021_001, fashionId: 3_021_001));
            LoopbackSessionHarness harness = new(character, CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid, []), $"festival48-{uid}");
            harness.Session.stage = stage ?? CreateLoginAccountCompatibilityStage(uid);
            return harness;
        }

        List<long> experimentPushes = [];
        JObject Drain(LoopbackSessionHarness harness, int id, string name)
        {
            for (int index = 0; index < 64; index++)
            {
                Packet packet = harness.ReadPacket($"{name} packet {index + 1}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    if (push.Name == "NotifyUpdateExperimentId")
                        experimentPushes.Add(JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content)).Value<long>("Id"));
                    continue;
                }
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, response.Id, $"{name} response id");
                JObject payload = JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
                AssertEqual(0L, payload.Value<long?>("Code") ?? 0, $"{name} Code");
                return payload;
            }
            throw new InvalidDataException($"{name}: missing response.");
        }

        void Story(LoopbackSessionHarness harness, int stageId)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(nameof(EnterStoryRequest), harness.Session, id, new EnterStoryRequest { StageId = stageId });
            Drain(harness, id, $"story {stageId}");
        }

        void Fight(LoopbackSessionHarness harness, int stageId, long uid)
        {
            int preId = ++packetId;
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, preId, new PreFightRequest
            {
                PreFightData = new() { ChallengeCount = 1, StageId = (uint)stageId, CardIds = [1_021_001], RobotIds = [], FirstFightPos = 1, CaptainPos = 1 }
            });
            long fightId = RequiredValue<long>(RequiredObject(Drain(harness, preId, $"prefight {stageId}"), "FightData", "prefight"), "FightId", JTokenType.Integer, "prefight");
            int settleId = ++packetId;
            InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, settleId, CreateMissingStageSettleRequest((uint)stageId, fightId, uid));
            Drain(harness, settleId, $"settle {stageId}");
        }

        // Relogin: reload the last saved Stage document through a wire round-trip, then project login payloads from it.
        (Dictionary<long, Dictionary<long, long>> Festivals, long[] FinishIds) Relogin(long uid)
        {
            AscNet.Common.Database.Stage saved = stageSaves.LastReplacement ?? throw new InvalidDataException("no Stage save");
            AssertEqual(uid, saved.Uid, "saved Stage belongs to the relogging player");
            AscNet.Common.Database.Stage reloaded = new()
            {
                Id = saved.Id, Uid = saved.Uid,
                Stages = saved.Stages.ToDictionary(pair => pair.Key, pair => MessagePackSerializer.Deserialize<StageDatum>(MessagePackSerializer.Serialize(pair.Value))),
                Course = saved.Course.ToList(), PrequelRewardedStages = saved.PrequelRewardedStages.ToList(), FinishedTasks = saved.FinishedTasks.ToList()
            };
            JObject Wire(string method) => JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(
                InvokePrivateStaticWithArgs<Dictionary<string, object?>>(accountModule, method, [reloaded]))));
            Dictionary<long, Dictionary<long, long>> festivals = ((JArray)Wire("BuildFestivalPayload")["FestivalInfos"]!).OfType<JObject>().ToDictionary(
                info => info.Value<long>("Id"),
                info => ((JArray)info["StageInfos"]!).OfType<JObject>().ToDictionary(row => row.Value<long>("Id"), row => row.Value<long>("ChallengeCount")));
            return (festivals, ((JArray)Wire("BuildExperimentPayload")["FinishIds"]!).Values<long>().ToArray());
        }

        const long playerA = 48_701, playerB = 48_702;
        using (LoopbackSessionHarness a = Harness(playerA))
        {
            Story(a, 15_010_701);
            var first = Relogin(playerA);
            AssertEqual("68", string.Join(',', first.Festivals.Keys), "state 1 festival ids");
            AssertEqual("15010701:0", string.Join(',', first.Festivals[68].Select(p => $"{p.Key}:{p.Value}")), "state 1 festival 68 stages");
            AssertEqual(0, first.FinishIds.Length, "state 1 no finished trials");

            foreach (int stageId in new[] { 15_010_702, 15_010_703, 15_010_704, 15_010_705, 15_010_706 })
                Story(a, stageId);
            Fight(a, 15_010_707, playerA);
            Fight(a, 15_010_707, playerA);
            AssertEqual(0, experimentPushes.Count, "festival clears emit no experiment push");
            Fight(a, 30_101_143, playerA);
            AssertEqual("139", string.Join(',', experimentPushes), "first trial clear pushes NotifyUpdateExperimentId 139 live");
            Fight(a, 30_101_143, playerA);
            AssertEqual("139", string.Join(',', experimentPushes), "repeat trial clear does not duplicate the push");
            var second = Relogin(playerA);
            AssertEqual(7, second.Festivals[68].Count, "state 2 festival 68 cleared stage count");
            AssertEqual(2L, second.Festivals[68][15_010_707], "state 2 fight ChallengeCount");
            AssertEqual(false, second.Festivals[68].ContainsKey(15_010_708), "uncleared stage stays absent");
            AssertEqual("139", string.Join(',', second.FinishIds), "state 2 only cleared trial 139 finished");
        }

        using (LoopbackSessionHarness b = Harness(playerB))
        {
            Story(b, 30_130_507);
            var other = Relogin(playerB);
            AssertEqual("24", string.Join(',', other.Festivals.Keys), "player B sees only its retro festival clear");
            AssertEqual(0, other.FinishIds.Length, "player B has no trial clears");
        }
    }
}
