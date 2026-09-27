using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben.transfinitetower;
using AscNet.Table.V2.share.robot;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal static partial class Program
{
    // Full Overclock Simulation run through the registered handlers: entry gates, the server-sided floor
    // clock, pending-floor confirmation, energy/fatigue accounting across a run, rollback refund, round
    // reset, chapter scoring/MVP and the persisted document after every protocol step.
    private static void ValidateTransfiniteTowerProgression()
    {
        PacketFactory.LoadPacketHandlers();
        const long playerId = 48_920;
        const int navigatorRequired = 20431004;
        const int chapterNotOpen = 20431002;
        const int stageProgressError = 20431003;
        const int challengeCountNotEnough = 20431005;
        const int refightTeamChanged = 20431028;
        const int lastRecordNotReset = 20431017;
        const int mvpInvalid = 20431010;
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TransfiniteTowerModule");
        FieldInfo clockField = module.GetField("Clock", BindingFlags.NonPublic | BindingFlags.Static)!;
        object originalClock = clockField.GetValue(null)!;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForTransfiniteCompatibility(
            out RecordingMongoCollectionProxy<Player> players, out _);
        try
        {
            TransfiniteTowerActivityTable activity = TableReaderV2.Parse<TransfiniteTowerActivityTable>().Single();
            AssertEqual(true, ActivityScheduleService.TryGet(activity.TimeId, out ActivityScheduleEntry play),
                "tower play calendar");
            long now = play.StartTime + 600;
            void SetClock(long seconds) =>
                clockField.SetValue(null, (Func<DateTimeOffset>)(() => DateTimeOffset.FromUnixTimeSeconds(seconds)));
            SetClock(now);
            // The durable Player as of the last acknowledged write: storage only proves a failing save did not
            // land while a read still shows that document.
            Player Durable() => BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("The tower scenario needs a durable Player before this step."));

            Dictionary<int, TransfiniteTowerStageTable> stagesById =
                TableReaderV2.Parse<TransfiniteTowerStageTable>().ToDictionary(row => row.Id);
            Dictionary<int, TransfiniteTowerCharacterTable> towerCharactersById =
                TableReaderV2.Parse<TransfiniteTowerCharacterTable>().ToDictionary(row => row.Id);
            Dictionary<int, TransfiniteTowerCharacterGroupTable> groups =
                TableReaderV2.Parse<TransfiniteTowerCharacterGroupTable>().ToDictionary(row => row.Id);
            Dictionary<int, TransfiniteTowerFightCountTable> fatigueByRowIndex =
                TableReaderV2.Parse<TransfiniteTowerFightCountTable>().ToDictionary(row => row.FightCount);
            // Config FightCount is the Lua ENERGY_MAX default the client's debuff index is measured against.
            int energyMax = int.Parse(TableReaderV2.Parse<TransfiniteTowerConfigTable>()
                .Single(row => row.Key == "FightCount").Values[0]);
            Dictionary<int, RobotTable> robotsById = TableReaderV2.Parse<RobotTable>()
                .GroupBy(row => row.Id).ToDictionary(rows => rows.Key, rows => rows.First());
            List<TransfiniteTowerStageTable> Floors(int stageGroupId) => [.. stagesById.Values
                .Where(row => row.StageGroupId == stageGroupId).OrderBy(row => row.Order)];
            // The floor's own character group decides both the authored magic ids and the energy quota.
            TransfiniteTowerCharacterTable StageCharacter(TransfiniteTowerStageTable stage, int characterId) =>
                groups[stage.CharacterGroupId].TowerCharacterIds.Select(id => towerCharactersById[id])
                    .Single(row => row.CharacterId == characterId);
            // Authored fatigue row for the client's debuff index: XTransfiniteTowerAgency:GetRoleDebuff
            // evaluates ENERGY_MAX - remaining against DEBUFF_HP_REDUCE, which is what the server feeds here.
            List<int> Fatigue(int rowIndex) => [.. fatigueByRowIndex[rowIndex].DebuffFightEventIds.Where(id => id > 0)];

            // Chapter 1 (the authored teaching tower) unlocks chapter 2 through a stage-passed condition;
            // the flow below proves that transition, so only the selector is derived here.
            TransfiniteTowerStageTable teachingFinal = Floors(1005).Single(row => row.Order == 3);
            List<int> teachingConditions = [.. TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>()
                .Where(row => row.Type == 10105 && row.Params.Count > 0 && row.Params[0] == teachingFinal.StageId)
                .Select(row => row.Id)];
            AssertEqual(1, teachingConditions.Count, "scenario needs one authored teaching-completion condition");
            List<TransfiniteTowerStageTable> teaching = Floors(1005);
            List<TransfiniteTowerStageTable> floors = Floors(1002);
            AssertEqual(true, teaching.Count == 3 && floors.Count >= 5,
                "scenario precondition: three teaching floors and at least five chapter floors");

            TransfiniteTowerCharacterGroupTable group = groups[floors[0].CharacterGroupId];
            List<TransfiniteTowerCharacterTable> roster = [.. group.TowerCharacterIds.Select(id => towerCharactersById[id])
                .Where(row => row.CharacterId > 0)];
            // Chapters 1 and 2 share their deployment pool, so the followers are the pool's intersection.
            List<TransfiniteTowerCharacterTable> teachingRoster = [.. groups[teaching[0].CharacterGroupId].TowerCharacterIds
                .Select(id => towerCharactersById[id]).Where(row => row.CharacterId > 0)];
            List<TransfiniteTowerCharacterTable> followers = [.. roster.Where(row => row.Type != 2)];
            List<TransfiniteTowerCharacterTable> shared = [.. followers.Where(row =>
                teachingRoster.Any(other => other.CharacterId == row.CharacterId))];
            TransfiniteTowerCharacterTable navigator = roster.First(row => row.Type == 2);
            AssertEqual(true, shared.Count >= 2, "chapters 1 and 2 share at least two followers");
            AssertEqual(true, teachingRoster.Any(row => row.CharacterId == navigator.CharacterId),
                "the teaching tower fields the same navigator");
            TransfiniteTowerCharacterTable first = shared[0];
            TransfiniteTowerCharacterTable second = shared[1];
            TransfiniteTowerCharacterTable reserve = followers.First(row => row.Id != first.Id && row.Id != second.Id);
            TransfiniteTowerCharacterTable reserveSecond = followers.First(row =>
                row.Id != first.Id && row.Id != second.Id && row.Id != reserve.Id);
            // Chapters 1 and 2 share the navigator and both followers; the reserves only fight chapter 2.
            int[] team = [navigator.CharacterId, first.CharacterId, second.CharacterId];
            int[] withoutNavigator = [first.CharacterId, second.CharacterId, 0];
            int[] shortened = [navigator.CharacterId, first.CharacterId, 0];
            int[] reserveTeam = [navigator.CharacterId, reserve.CharacterId, reserveSecond.CharacterId];

            Character character = CreateDrawCompatibilityCharacter(playerId);
            character.Characters = new[] { navigator, first, second, reserve, reserveSecond }
                .Select(row => TowerCharacterData(row.CharacterId)).ToList();
            using LoopbackSessionHarness harness = new(character, CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []), "transfinite-tower-progression");
            int packetId = 48_920;
            T Call<T>(string request, object body, string label)
            {
                int id = ++packetId;
                InvokeRegisteredRequestHandler(request, harness.Session, id, body);
                return ReadResponsePayload<T>(harness, id, typeof(T).Name, label);
            }
            JObject PreFight(TransfiniteTowerStageTable stage, int[] cards, int[] robots, string label)
            {
                int id = ++packetId;
                InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, id, new PreFightRequest
                {
                    PreFightData = new()
                    {
                        StageId = (uint)stage.StageId, CardIds = [.. cards.Select(card => (uint)card)],
                        RobotIds = [.. robots], CaptainPos = 1, FirstFightPos = 1
                    }
                });
                return ReadResponseMapPayload(harness, id, nameof(PreFightResponse), label);
            }
            TransfiniteTowerChapterInfo ChapterPush(string label) => ReadPushPayload<NotifyTransfiniteTowerChapterInfo>(
                harness, nameof(NotifyTransfiniteTowerChapterInfo), label).ChapterInfo!;
            void TaskSync(string label)
            {
                NotifyTask notify = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), label);
                AssertEqual(true, notify.Tasks.Tasks.Count > 0, label);
            }
            // Enters a floor and checks the deployed team: authored magic ids per slot, exactly one
            // navigator and the fatigue events the floor's consumed quota carries.
            long Enter(TransfiniteTowerStageTable stage, int[] cards, int[] robots, List<int> expectedFatigue, string label)
            {
                JObject wire = PreFight(stage, cards, robots, $"{label} pre-fight");
                AssertEqual(0, wire.Value<int>("Code"), $"{label} pre-fight code");
                JObject role = wire["FightData"]!["RoleData"]!.Children<JObject>()
                    .Single(row => row.Value<long>("Id") == playerId);
                AssertEqual(3, role["NpcData"]!.Children<JProperty>().Count(), $"{label} deployed members");
                List<int> fightEvents = [.. (wire["FightData"]!["EventIds"] as JArray ?? new JArray()).Select(value => value.Value<int>())];
                foreach (int eventId in stage.FightEventIds.Where(id => id > 0))
                    AssertEqual(true, fightEvents.Contains(eventId), $"{label} authored stage event {eventId}");
                for (int slot = 0; slot < 3; slot++)
                {
                    JObject npc = (JObject)role["NpcData"]![slot.ToString()]!;
                    int characterId = cards[slot] != 0 ? cards[slot] : CharacterIdOfRobot(robots[slot]);
                    List<int> magicIds = [.. npc["MagicIds"]!.Children<JProperty>().Select(property => int.Parse(property.Name))];
                    List<int> events = [.. (npc["EventIds"] as JArray ?? new JArray()).Select(value => value.Value<int>())];
                    List<int> slotFatigue = slot == 0 ? [] : expectedFatigue;
                    AssertEqual(string.Join(",", StageCharacter(stage, characterId).MagicIds.Where(id => id > 0).Order()),
                        string.Join(",", magicIds.Order()), $"{label} slot {slot} authored magic ids");
                    AssertEqual(string.Join(",", slotFatigue.Order()), string.Join(",", events.Order()),
                        $"{label} slot {slot} fatigue events");
                }
                return wire["FightData"]!.Value<long>("FightId");
            }
            int CharacterIdOfRobot(int robotId) => robotsById[robotId].CharacterId;
            TransfiniteTowerStageRecord SettleWin(TransfiniteTowerStageTable stage, long fightId, int spendSeconds, string label)
            {
                now += spendSeconds;
                SetClock(now);
                int id = ++packetId;
                InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, id, new FightSettleRequest
                {
                    Result = new FightSettleResult { StageId = (uint)stage.StageId, FightId = fightId, IsWin = true }
                });
                // The tower settle gate pushes the chapter info before FightModule answers the request.
                TransfiniteTowerChapterInfo push = ChapterPush($"{label} chapter info");
                FightSettleResponse settle = ReadResponsePayload<FightSettleResponse>(harness, id,
                    nameof(FightSettleResponse), $"{label} settle");
                AssertEqual(0, settle.Code, $"{label} settle code");
                AssertEqual(true, settle.Settle.IsWin, $"{label} settle win");
                AssertEqual(stage.Order, settle.Settle.TransfiniteTowerFightResult!.Order, $"{label} settle order");
                AssertEqual(spendSeconds, settle.Settle.TransfiniteTowerFightResult.SpendTime, $"{label} server-sided floor time");
                TransfiniteTowerStageRecord pending = push.CurBattleInfo!.PendingStageRecord!;
                AssertEqual(stage.Order, pending.Order, $"{label} pending order");
                AssertEqual(spendSeconds, pending.SpendTime, $"{label} pending time");
                AssertEqual(3, pending.Team.Count, $"{label} pending team size");
                return pending;
            }
            TransfiniteTowerChapterInfo ConfirmFloor(int chapterId, string label)
            {
                TransfiniteTowerStageSettleResponse response = Call<TransfiniteTowerStageSettleResponse>(
                    nameof(TransfiniteTowerStageSettleRequest), new TransfiniteTowerStageSettleRequest { ChapterId = chapterId },
                    $"{label} stage settle");
                AssertEqual(0, response.Code, $"{label} stage settle code");
                TransfiniteTowerChapterInfo info = ChapterPush($"{label} chapter info");
                TaskSync($"{label} task sync");
                return info;
            }
            string Expected(params (int CfgId, int Used)[] entries) => string.Join(",",
                entries.OrderBy(entry => entry.CfgId).Select(entry => $"{entry.CfgId}:{entry.Used}"));

            // A locked chapter, a missing navigator and a skipped floor must not reach the generic path.
            AssertEqual(chapterNotOpen, PreFight(floors[0], team, [0, 0, 0], "locked chapter pre-fight").Value<int>("Code"),
                "chapter 2 stays locked until the teaching tower is cleared");
            AssertEqual(null, harness.Session.fight, "locked chapter pre-fight leaves no fight");
            AssertEqual(navigatorRequired, PreFight(teaching[0], withoutNavigator, [0, 0, 0], "teaching pre-fight without navigator").Value<int>("Code"),
                "authored navigator requirement rejected");
            AssertEqual(stageProgressError, PreFight(teaching[1], team, [0, 0, 0], "teaching pre-fight out of order").Value<int>("Code"),
                "floor order is enforced from the first floor onwards");

            // Teaching floors read the configured quota without consuming it (agency.lua:680), carry their
            // floor group's magic ids and start on the fatigue row of their own configured quota: the first
            // teaching floor's group grants energyMax charges, which is row 0 (no authored penalty).
            long fightId = Enter(teaching[0], team, [0, 0, 0], [], "teaching floor 1");
            SettleWin(teaching[0], fightId, 30, "teaching floor 1");
            TransfiniteTowerChapterInfo teachingInfo = ConfirmFloor(1, "teaching floor 1");
            AssertEqual(1, teachingInfo.CurBattleInfo!.StageProgressIndex, "teaching progress after floor 1");
            // Teaching floors record their deployed frames exactly like the client's local prediction,
            // but the gate below always reads the configured quota for them (agency.lua:680).
            AssertEqual(Expected((StageCharacter(teaching[0], first.CharacterId).Id, 1), (StageCharacter(teaching[0], second.CharacterId).Id, 1)),
                TowerCounts(teachingInfo.CurBattleInfo.CharacterCountList), "teaching floor 1 records the deployed frames");
            AssertEqual(1, teachingInfo.MaxPassedOrder, "teaching max passed order");

            // Floor 2's group grants 2 charges and none are spent, so the client index is energyMax - 2.
            fightId = Enter(teaching[1], team, [0, 0, 0], Fatigue(energyMax - 2), "teaching floor 2");
            SettleWin(teaching[1], fightId, 20, "teaching floor 2");
            teachingInfo = ConfirmFloor(1, "teaching floor 2");
            AssertEqual(2, teachingInfo.MaxPassedOrder, "teaching max passed order after floor 2");

            // Teaching floor 3 fields the authored trial robot beside the navigator.
            TransfiniteTowerCharacterTable trial = StageCharacter(teaching[2], second.CharacterId);
            AssertEqual(true, trial.RobotId > 0, "authored trial robot for the teaching floor");
            // Floor 3's group grants a single charge, so the client index is energyMax - 1.
            fightId = Enter(teaching[2], [navigator.CharacterId, first.CharacterId, 0], [0, 0, trial.RobotId], Fatigue(energyMax - 1), "teaching floor 3");
            SettleWin(teaching[2], fightId, 10, "teaching floor 3");
            teachingInfo = ConfirmFloor(1, "teaching floor 3");
            AssertEqual(3, teachingInfo.MaxPassedOrder, "teaching tower completed");

            // Chapter settle scores the run: order, summed floor time, server-derived power and MVP.
            TransfiniteTowerChapterSettleResponse settled = Call<TransfiniteTowerChapterSettleResponse>(
                nameof(TransfiniteTowerChapterSettleRequest), new TransfiniteTowerChapterSettleRequest { ChapterId = 1 },
                "teaching chapter settle");
            AssertEqual(0, settled.Code, "teaching chapter settle code");
            TransfiniteTowerSettleInfo settleInfo = settled.SettleInfo!;
            AssertEqual(3, settleInfo.Order, "teaching settle order");
            AssertEqual(60, settleInfo.TotalSpendTime, "teaching settle total floor time");
            AssertEqual(4, settleInfo.Characters.Count, "teaching settle roster");
            AssertEqual((0, 0), (settled.Rank, settled.TotalCount), "teaching chapter is not ranked");
            AssertEqual(true, settleInfo.IsNewRecord, "first teaching clear is a record");
            foreach (TransfiniteTowerSettleCharacter member in settleInfo.Characters)
            {
                bool isTrial = member.FightId == trial.RobotId;
                AssertEqual(isTrial, member.IsTrial, $"trial flag for {member.FightId}");
                if (isTrial)
                {
                    RobotTable robot = robotsById[trial.RobotId];
                    AssertEqual(robot.ShowAbility ?? 0, member.Power, "trial robot power");
                    AssertEqual(robot.CharacterQuality, member.Quality, "trial robot quality");
                }
                else
                {
                    CharacterData owned = character.Characters.Single(row => row.Id == (uint)member.FightId);
                    AssertEqual(TowerCalculatedPower(harness.Session, owned), (long)member.Power, $"derived power for {member.FightId}");
                    AssertEqual(owned.Quality, member.Quality, $"quality for {member.FightId}");
                }
            }
            AssertEqual(settleInfo.Characters.Sum(member => (long)member.Power), (long)settleInfo.TotalPower, "settle total power");
            AssertEqual(settleInfo.Characters.OrderByDescending(member => member.Power).ThenBy(member => member.FightId).First().FightId,
                settleInfo.MvpFightId, "server-sided MVP is the strongest deployed frame");
            TransfiniteTowerChapterInfo chapterInfo = ChapterPush("teaching chapter settle");
            AssertEqual(null, chapterInfo.CurBattleInfo, "teaching settle closes the run");
            AssertEqual(3, chapterInfo.LastStageRecordList.Count, "teaching settle keeps the round records");
            AssertEqual("1,2,3", string.Join(",", chapterInfo.LastStageRecordList.Select(record => record.Order)), "teaching records are dense");
            AssertEqual(true, chapterInfo.CanSetMvp, "a record allows picking the MVP");
            AssertEqual(3, chapterInfo.BestOrder, "best order after the first clear");
            Player persisted = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
            TransfiniteTowerChapterInfo persistedTeaching = persisted.TransfiniteTower!.ChapterInfoList.Single(info => info.ChapterId == 1);
            AssertEqual(3, persistedTeaching.SettleInfo!.Order, "settle info persisted");
            AssertEqual(3, persistedTeaching.LastStageRecordList.Count, "round records persisted");

            // Clearing the teaching tower unlocks chapter 2; fatigue now follows each follower's remaining
            // charges (the client index the server feeds is ENERGY_MAX - remaining).
            int firstCfg = StageCharacter(floors[0], first.CharacterId).Id;
            int secondCfg = StageCharacter(floors[0], second.CharacterId).Id;
            int reserveCfg = StageCharacter(floors[0], reserve.CharacterId).Id;
            int reserveSecondCfg = StageCharacter(floors[0], reserveSecond.CharacterId).Id;
            fightId = Enter(floors[0], team, [0, 0, 0], [], "chapter 2 floor 1");
            SettleWin(floors[0], fightId, 31, "chapter 2 floor 1");
            // A failed confirmation save must not persist progress and must leave the floor confirmable: the
            // durable read still shows the settled-but-unconfirmed document, so nothing landed.
            players.FindResults = [Durable()];
            players.ThrowOnReplaceOne = true;
            AssertEqual(1033, Call<TransfiniteTowerStageSettleResponse>(nameof(TransfiniteTowerStageSettleRequest),
                new TransfiniteTowerStageSettleRequest { ChapterId = 2 }, "failed stage settle").Code,
                "a failed confirmation save is reported");
            players.ThrowOnReplaceOne = false;
            players.FindResults = null;
            AssertEqual(true, harness.Session.player.TransfiniteTower!.ChapterInfoList.Single(row => row.ChapterId == 2)
                .CurBattleInfo!.PendingStageRecord is not null, "failed confirmation keeps the pending floor");
            TransfiniteTowerChapterInfo info = ConfirmFloor(2, "chapter 2 floor 1");
            AssertEqual(1, info.CurBattleInfo!.StageProgressIndex, "chapter 2 progress after floor 1");
            AssertEqual(Expected((firstCfg, 1), (secondCfg, 1)), TowerCounts(info.CurBattleInfo.CharacterCountList), "floor 1 charges both followers once");
            AssertEqual(0, info.CurBattleInfo.RollbackOrder, "floor 1 is not a rollback point");
            AssertEqual(string.Join(",", team), string.Join(",", info.CurBattleInfo.LastTeamSelection!.CardIds), "last team selection recorded");

            fightId = Enter(floors[1], team, [0, 0, 0], Fatigue(energyMax - 2), "chapter 2 floor 2");
            SettleWin(floors[1], fightId, 22, "chapter 2 floor 2");
            info = ConfirmFloor(2, "chapter 2 floor 2");
            AssertEqual(Expected((firstCfg, 2), (secondCfg, 2)), TowerCounts(info.CurBattleInfo!.CharacterCountList), "floor 2 charges both followers twice");

            fightId = Enter(floors[2], team, [0, 0, 0], Fatigue(energyMax - 1), "chapter 2 floor 3");
            // Acknowledgement loss on the settle write: the write lands and the caller only sees the failure,
            // so the durable read shows the exact staged document. Rolling back to the pre-request document
            // would strand the winning floor: the next unrelated save would write a stale pending fight over
            // the committed one. The lost acknowledgement is reported as the committed success it was.
            players.BeforeReplaceOne = document =>
                players.FindResults = [BsonSerializer.Deserialize<Player>(document.ToBson())];
            players.ThrowAfterReplaceOne = true;
            SettleWin(floors[2], fightId, 12, "chapter 2 floor 3 acknowledgement loss");
            players.BeforeReplaceOne = null;
            players.FindResults = null;
            AssertEqual(false, players.ThrowAfterReplaceOne, "the settle acknowledgement loss fires once");
            Player committedSettle = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
            TransfiniteTowerBattleInfo committedBattle = committedSettle.TransfiniteTower!.ChapterInfoList
                .Single(row => row.ChapterId == 2).CurBattleInfo!;
            AssertEqual(null, committedSettle.TransfiniteTower.PendingFight, "the landed settle consumes the pending fight");
            AssertEqual(2, committedBattle.StageProgressIndex, "the landed settle keeps the progress unconfirmed");
            AssertEqual(3, committedBattle.PendingStageRecord!.Order, "the landed settle stores the winning floor");
            AssertEqual(true, harness.Session.player.TransfiniteTower!.ToBson().SequenceEqual(committedSettle.TransfiniteTower.ToBson()),
                "a lost settle acknowledgement adopts the committed tower state");

            // An unrelated save between the lost acknowledgement and the next request must write the committed
            // floor, never the rolled-back pending fight.
            harness.Session.player.PlayerData.Sign = "tower-settle-acknowledgement-loss";
            harness.Session.player.Save();
            Player afterUnrelatedSave = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
            AssertEqual(3, afterUnrelatedSave.TransfiniteTower!.ChapterInfoList.Single(row => row.ChapterId == 2)
                .CurBattleInfo!.PendingStageRecord!.Order, "an unrelated save keeps the committed winning floor");
            AssertEqual(null, afterUnrelatedSave.TransfiniteTower.PendingFight, "an unrelated save keeps the fight consumed");

            // The same settle request cannot credit the floor twice: the committed fight is already consumed.
            int writesBeforeDuplicateSettle = players.ReplaceOneCalls;
            AssertEqual(1033, Call<FightSettleResponse>(nameof(FightSettleRequest), new FightSettleRequest
            {
                Result = new FightSettleResult { StageId = (uint)floors[2].StageId, FightId = fightId, IsWin = true }
            }, "chapter 2 floor 3 duplicate settle").Code, "a consumed fight cannot settle twice");
            AssertEqual(writesBeforeDuplicateSettle, players.ReplaceOneCalls, "a duplicate settle persists nothing");

            info = ConfirmFloor(2, "chapter 2 floor 3");
            AssertEqual(3, info.CurBattleInfo!.StageProgressIndex, "chapter 2 progress after floor 3");
            AssertEqual(Expected((firstCfg, 3), (secondCfg, 3)), TowerCounts(info.CurBattleInfo.CharacterCountList), "the reconciled floor charges each follower exactly once");
            AssertEqual(3, info.CurBattleInfo.RollbackOrder, "authored rollback floor activated");
            AssertEqual(3, info.MaxPassedOrder, "chapter 2 max passed order");

            // Reloading the acknowledged document shows the same single charge and the advanced progress.
            harness.Session.player = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
            TransfiniteTowerBattleInfo reloadedBattle = harness.Session.player.TransfiniteTower!.ChapterInfoList
                .Single(row => row.ChapterId == 2).CurBattleInfo!;
            AssertEqual(3, reloadedBattle.StageProgressIndex, "reload keeps the acknowledged progress");
            AssertEqual(Expected((firstCfg, 3), (secondCfg, 3)), TowerCounts(reloadedBattle.CharacterCountList), "reload keeps the once-charged energy");
            AssertEqual(null, reloadedBattle.PendingStageRecord, "reload keeps the confirmed floor consumed");
            AssertEqual(null, harness.Session.player.TransfiniteTower.PendingFight, "reload keeps no pending fight");

            // Re-challenging a cleared floor reuses its recorded team and charges nothing again.
            AssertEqual(refightTeamChanged, PreFight(floors[2], shortened, [0, 0, 0], "re-challenge with another team").Value<int>("Code"),
                "re-challenge keeps the recorded team");
            fightId = Enter(floors[2], team, [0, 0, 0], Fatigue(energyMax - 1), "re-challenge");
            SettleWin(floors[2], fightId, 9, "re-challenge");
            info = ConfirmFloor(2, "re-challenge");
            AssertEqual(Expected((firstCfg, 3), (secondCfg, 3)), TowerCounts(info.CurBattleInfo!.CharacterCountList), "re-challenge charges nothing");
            AssertEqual(3, info.CurBattleInfo.StageRecordList.Count, "re-challenge replaces its floor record");
            AssertEqual("1,2,3", string.Join(",", info.CurBattleInfo.StageRecordList.Select(record => record.Order)), "floor records stay dense");

            // The exhausted followers cannot open the next floor, so floor 4 fields the reserves.
            AssertEqual(challengeCountNotEnough, PreFight(floors[3], team, [0, 0, 0], "exhausted followers").Value<int>("Code"),
                "a frame without remaining quota cannot deploy");
            // The reserves are untouched, so both of them sit on row 0.
            fightId = Enter(floors[3], reserveTeam, [0, 0, 0], [], "chapter 2 floor 4");
            SettleWin(floors[3], fightId, 8, "chapter 2 floor 4");
            info = ConfirmFloor(2, "chapter 2 floor 4");
            AssertEqual(4, info.CurBattleInfo!.StageProgressIndex, "chapter 2 progress after floor 4");
            AssertEqual(Expected((firstCfg, 3), (secondCfg, 3), (reserveCfg, 1), (reserveSecondCfg, 1)),
                TowerCounts(info.CurBattleInfo.CharacterCountList), "floor 4 charges the reserves");

            // Rollback returns to the active rollback point and refunds the discarded floor's energy.
            TransfiniteTowerRollbackResponse rolled = Call<TransfiniteTowerRollbackResponse>(
                nameof(TransfiniteTowerRollbackRequest), new TransfiniteTowerRollbackRequest { ChapterId = 2 }, "rollback");
            AssertEqual(0, rolled.Code, "rollback code");
            info = ChapterPush("rollback");
            AssertEqual(3, info.CurBattleInfo!.StageProgressIndex, "rollback returns to the rollback point");
            AssertEqual(Expected((firstCfg, 3), (secondCfg, 3)), TowerCounts(info.CurBattleInfo.CharacterCountList), "rollback refunds discarded energy");
            AssertEqual("1,2,3", string.Join(",", info.CurBattleInfo.StageRecordList.Select(record => record.Order)), "rollback drops later floors");

            fightId = Enter(floors[3], reserveTeam, [0, 0, 0], [], "chapter 2 floor 4 after rollback");
            SettleWin(floors[3], fightId, 7, "chapter 2 floor 4 after rollback");
            info = ConfirmFloor(2, "chapter 2 floor 4 after rollback");
            AssertEqual(Expected((firstCfg, 3), (secondCfg, 3), (reserveCfg, 1), (reserveSecondCfg, 1)),
                TowerCounts(info.CurBattleInfo!.CharacterCountList), "the re-cleared floor charges the reserves again");

            // Early chapter settle scores the run, then a new round needs an explicit reset.
            settled = Call<TransfiniteTowerChapterSettleResponse>(nameof(TransfiniteTowerChapterSettleRequest),
                new TransfiniteTowerChapterSettleRequest { ChapterId = 2 }, "chapter 2 chapter settle");
            AssertEqual(0, settled.Code, "chapter 2 chapter settle code");
            settleInfo = settled.SettleInfo!;
            AssertEqual(4, settleInfo.Order, "chapter 2 settle order");
            AssertEqual(69, settleInfo.TotalSpendTime, "chapter 2 settle total floor time");
            AssertEqual(5, settleInfo.Characters.Count, "chapter 2 settle roster");
            AssertEqual(settleInfo.Characters.Sum(member => (long)member.Power), (long)settleInfo.TotalPower, "chapter 2 settle total power");
            AssertEqual(true, settleInfo.IsNewRecord, "chapter 2 first clear is a record");
            AssertEqual((0, 0), (settled.Rank, settled.TotalCount), "chapter 2 is not ranked");
            info = ChapterPush("chapter 2 chapter settle");
            AssertEqual(null, info.CurBattleInfo, "chapter settle closes the run");
            AssertEqual("1,2,3,4", string.Join(",", info.LastStageRecordList.Select(record => record.Order)), "chapter settle keeps dense records");
            AssertEqual(4, info.BestOrder, "chapter 2 best order");
            AssertEqual(69, info.BestTotalSpendTime, "chapter 2 best total time");

            int mvpPacketId = ++packetId;
            InvokeRegisteredRequestHandler(nameof(TransfiniteTowerSetMvpRequest), harness.Session, mvpPacketId,
                new TransfiniteTowerSetMvpRequest { ChapterId = 2, MvpFightId = second.CharacterId });
            info = ChapterPush("set mvp");
            TransfiniteTowerSetMvpResponse mvp = ReadResponsePayload<TransfiniteTowerSetMvpResponse>(
                harness, mvpPacketId, nameof(TransfiniteTowerSetMvpResponse), "set mvp");
            AssertEqual(0, mvp.Code, "set mvp code");
            AssertEqual(second.CharacterId, info.SettleInfo!.MvpFightId, "settle info takes the picked MVP");
            AssertEqual(mvpInvalid, Call<TransfiniteTowerSetMvpResponse>(nameof(TransfiniteTowerSetMvpRequest),
                new TransfiniteTowerSetMvpRequest { ChapterId = 2, MvpFightId = 999_999_999 }, "foreign mvp").Code,
                "MVP must be a deployed frame");
            persisted = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
            AssertEqual(second.CharacterId,
                persisted.TransfiniteTower!.ChapterInfoList.Single(row => row.ChapterId == 2).SettleInfo!.MvpFightId, "MVP persisted");

            AssertEqual(lastRecordNotReset, PreFight(floors[0], team, [0, 0, 0], "new round before reset").Value<int>("Code"),
                "a settled tower needs an explicit reset before a new round");
            AssertEqual(0, Call<TransfiniteTowerResetChapterResponse>(nameof(TransfiniteTowerResetChapterRequest),
                new TransfiniteTowerResetChapterRequest { ChapterId = 2 }, "reset chapter").Code, "reset chapter code");
            info = ChapterPush("reset chapter");
            AssertEqual(0, info.LastStageRecordList.Count, "reset clears the round records");
            // A failed commitment save must leave no pending fight behind: storage still shows the reset
            // chapter, so the write provably did not land.
            players.FindResults = [Durable()];
            players.ThrowOnReplaceOne = true;
            AssertEqual(1033, PreFight(floors[0], team, [0, 0, 0], "pre-fight with a failed save").Value<int>("Code"),
                "a failed commitment save rejects the pre-fight");
            players.ThrowOnReplaceOne = false;
            players.FindResults = null;
            AssertEqual(null, harness.Session.fight, "failed commitment leaves no fight");
            AssertEqual(null, harness.Session.player.TransfiniteTower!.PendingFight, "failed commitment leaves no pending fight");
            fightId = Enter(floors[0], team, [0, 0, 0], [], "new round floor 1");
            SettleWin(floors[0], fightId, 6, "new round floor 1");
            info = ConfirmFloor(2, "new round floor 1");
            AssertEqual(Expected((firstCfg, 1), (secondCfg, 1)), TowerCounts(info.CurBattleInfo!.CharacterCountList), "a new round restores the full quota");

            // The teaching tower may replay from floor 1 without a reset (control.lua:440); any other
            // floor stays blocked until that replay commits.
            fightId = Enter(teaching[0], team, [0, 0, 0], [], "teaching replay");
            AssertEqual(lastRecordNotReset, PreFight(teaching[1], team, [0, 0, 0], "replay follow-up floor").Value<int>("Code"),
                "a teaching replay starts from floor 1 only");
            SettleWin(teaching[0], fightId, 5, "teaching replay");
            info = ConfirmFloor(1, "teaching replay");
            AssertEqual(0, info.LastStageRecordList.Count, "committing the replay starts the new teaching round");
            AssertEqual(1, info.CurBattleInfo!.StageProgressIndex, "teaching replay progress");
        }
        finally
        {
            clockField.SetValue(null, originalClock);
        }
    }

    private static string TowerCounts(List<TransfiniteTowerCharacterCount> counts) => string.Join(",",
        counts.OrderBy(count => count.CharacterCfgId).Select(count => $"{count.CharacterCfgId}:{count.UsedCount}"));
}
