using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers.BigWorld;
using MongoDB.Bson.Serialization;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static void ValidateBigWorldCharacter()
        {
            const long uid = 47_120;
            Type module = typeof(Session).Assembly.GetType("AscNet.GameServer.Handlers.BigWorld.BigWorldCharacterModule", true)!;
            Type conditions = typeof(Session).Assembly.GetType("AscNet.GameServer.Handlers.BigWorld.BigWorldConditionService", true)!;
            MethodInfo applySlice = module.GetMethod("ApplySliceRewards", BindingFlags.Static | BindingFlags.NonPublic)!;
            MethodInfo fill = module.GetMethod("FillPlayerData", BindingFlags.Static | BindingFlags.NonPublic)!;
            MethodInfo npcList = module.GetMethod("BuildWorldNpcList", BindingFlags.Static | BindingFlags.NonPublic)!;
            MethodInfo check = conditions.GetMethod("Check", BindingFlags.Static | BindingFlags.NonPublic)!;

            Player player = CreateDrawCompatibilityPlayer(uid);
            Character character = CreateDrawCompatibilityCharacter(uid);
            character.Characters.Add(new CharacterData { Id = 1021006 });
            character.Characters.Add(new CharacterData { Id = 1021007 });
            character.Fashions.Add(new FashionList { Id = 6006103 });
            character.FashionColors[6006103] = [33000001];
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> saves, out _, out _);
            using LoopbackSessionHarness h = new(character, player, CreateDrawCompatibilityInventory(uid, []), "bw-character");
            BigWorldPlayerState s = player.BigWorldState;
            int id = 100;

            // Seeding: DIY from BigWorldDIYBagInit/Outfit defaults (male res colours), team 1 from DefaultTeamPos.
            AssertEqual(false, (bool)check.Invoke(null, [player, 50003202])!, "23102 before owning 29990002");
            BigWorldPlayerData data = new();
            fill.Invoke(null, [player, data]);
            AssertEqual(12, data.CommanderFashionBags.Count, "bag seeded from BigWorldDIYBagInit");
            AssertEqual(1002102, data.CommanderFashionOutfits[1].WearFashionDict[1].ColourId, "outfit 1 type 1 default colour (male res 10021)");
            AssertEqual(29990001, data.CommanderFashionOutfits[2].WearFashionDict[99].PartId, "outfit 2 default suit");
            AssertEqual(1, data.CurrentTeamId, "current team seeded");
            AssertEqual(2011001, data.TeamDict[1].CharacterList.Single().CharacterId, "team 1 = DefaultTeamPos1");
            AssertEqual(false, data.CharacterInitialized, "DIY not initialized");

            // Slice reward before DIY init: bag push + delayed show reward.
            List<RewardGoods> goods = [new RewardGoods { Id = 1, TemplateId = 29990002, Count = 1 }, new RewardGoods { Id = 2, TemplateId = 950000, Count = 5 }];
            applySlice.Invoke(null, [h.Session, goods]);
            AssertEqual(true, ReadPushPayload<NotifyBigWorldCommanderFashionBagUpdate>(h, nameof(NotifyBigWorldCommanderFashionBagUpdate), "bag push").DlcFashionBags.Contains(29990002), "bag push has part");
            AssertEqual(2, ReadPushPayload<NotifyBigWorldShowReward>(h, nameof(NotifyBigWorldShowReward), "show push").RewardGoodsList.Count, "show reward queued");
            AssertEqual(true, (bool)check.Invoke(null, [player, 50003202])!, "23102 after owning 29990002");
            Code(nameof(BigWorldShowRewardFinishRequest), new BigWorldShowRewardFinishRequest(), 0, nameof(BigWorldShowRewardFinishResponse));
            AssertEqual(0, s.PendingShowRewards.Count, "show reward cleared");

            // Teams.
            BigWorldTeamData Team(int teamId, params (int Pos, int Char)[] m) => new()
            {
                TeamId = teamId, CharacterList = m.Select(x => new BigWorldTeamCharacter { Pos = x.Pos, CharacterId = x.Char }).ToList()
            };
            Code(nameof(BigWorldTeamChangeRequest), new BigWorldTeamChangeRequest { ChangeTeam = Team(2, (0, 2011001), (1, 1021006)) }, 0, nameof(BigWorldTeamChangeResponse));
            Code(nameof(BigWorldTeamChangeRequest), new BigWorldTeamChangeRequest { ChangeTeam = Team(3, (2, 1021007)) }, 0, nameof(BigWorldTeamChangeResponse));
            AssertEqual(2, s.Teams[2].Count, "team 2 persisted");
            AssertEqual(2, s.Teams[3][0].Pos, "team 3 pos persisted");
            foreach ((BigWorldTeamData bad, int code, string label) in new[]
            {
                (Team(2, (0, 1011004)), 25000006, "unowned character"),
                (Team(2, (0, 999)), 25000003, "unknown character"),
                (Team(2, (0, 1021006), (1, 1021006)), 25000007, "duplicate"),
                (Team(2, (3, 1021006)), 25004034, "pos out of range"),
                (Team(4, (0, 1021006)), 25004034, "team id out of range"),
                (Team(1), 25000006, "empty current team")
            })
            {
                Code(nameof(BigWorldTeamChangeRequest), new BigWorldTeamChangeRequest { ChangeTeam = bad }, code, nameof(BigWorldTeamChangeResponse));
                AssertEqual(2, s.Teams[2].Count, label + " leaves team unchanged");
            }
            Code(nameof(BigWorldTeamIndexChangeRequest), new BigWorldTeamIndexChangeRequest { CurrentTeamId = 2 }, 0, nameof(BigWorldTeamIndexChangeResponse));
            Code(nameof(BigWorldTeamIndexChangeRequest), new BigWorldTeamIndexChangeRequest { CurrentTeamId = 5 }, 25004034, nameof(BigWorldTeamIndexChangeResponse));
            AssertEqual(2, s.CurrentTeamId, "current team switched");

            // Character fashion + head.
            Code(nameof(BigWorldCharacterFashionUseRequest), new BigWorldCharacterFashionUseRequest { FashionId = 6006103, FashionColorId = 33000001 }, 0, nameof(BigWorldCharacterFashionUseResponse));
            Code(nameof(BigWorldCharacterFashionUseRequest), new BigWorldCharacterFashionUseRequest { FashionId = 6004301 }, 0, nameof(BigWorldCharacterFashionUseResponse));
            AssertEqual(33000001, s.CharacterFashions[1021007].FashionColorId, "owned fashion colour worn");
            AssertEqual(6004301, s.CharacterFashions[1021006].FashionId, "default fashion always usable");
            Code(nameof(BigWorldCharacterFashionUseRequest), new BigWorldCharacterFashionUseRequest { FashionId = 6006203 }, 20010002, nameof(BigWorldCharacterFashionUseResponse));
            Code(nameof(BigWorldCharacterFashionUseRequest), new BigWorldCharacterFashionUseRequest { FashionId = 1 }, 25202001, nameof(BigWorldCharacterFashionUseResponse));
            Code(nameof(BigWorldCharacterFashionUseRequest), new BigWorldCharacterFashionUseRequest { FashionId = 6006103, FashionColorId = 33000002 }, 25202002, nameof(BigWorldCharacterFashionUseResponse));
            character.FashionColors.Clear();
            Code(nameof(BigWorldCharacterFashionUseRequest), new BigWorldCharacterFashionUseRequest { FashionId = 6006103, FashionColorId = 33000001 }, 25202003, nameof(BigWorldCharacterFashionUseResponse));
            BigWorldCharacterSetHeadInfoRequest Head(int tpl, int fashion, int type) => new() { TemplateId = tpl, CharacterHeadInfo = new BigWorldCharacterHeadInfo { HeadFashionId = fashion, HeadFashionType = type } };
            Code(nameof(BigWorldCharacterSetHeadInfoRequest), Head(1021007, 6006103, 2), 0, nameof(BigWorldCharacterSetHeadInfoResponse));
            AssertEqual(6006103, s.CharacterFashions[1021007].HeadFashionId, "head persisted");
            Code(nameof(BigWorldCharacterSetHeadInfoRequest), Head(1021007, 6006101, 1), 20034022, nameof(BigWorldCharacterSetHeadInfoResponse));
            Code(nameof(BigWorldCharacterSetHeadInfoRequest), Head(1021006, 6006103, 0), 20010005, nameof(BigWorldCharacterSetHeadInfoResponse));

            // Commander DIY.
            BigWorldCommanderFashionUpdateRequest Diy(int outfit, int gender, params (int Type, int Part, int Colour)[] parts) => new()
            {
                OutfitType = outfit, Gender = gender,
                CommanderFashionList = parts.ToDictionary(p => p.Type, p => new Theatre5BigWorldCommanderFashion { PartId = p.Part, ColourId = p.Colour })
            };
            foreach ((BigWorldCommanderFashionUpdateRequest bad, int code) in new[]
            {
                (Diy(3, 1, (1, 29001002, 0)), 25201001),
                (Diy(1, 3, (1, 29001002, 0)), 25200104),
                (Diy(1, 1, (1, 29001002, 0), (2, 29002001, 0), (3, 29003001, 0), (4, 29004003, 0)), 25200004),
                (Diy(1, 1, (1, 29001002, 0), (2, 29002001, 0), (3, 29003001, 9999)), 25200007),
                (Diy(1, 1, (1, 29001002, 0), (2, 29002001, 1002102), (3, 29003001, 0)), 25200006),
                (Diy(1, 1, (1, 29001002, 0), (2, 29002001, 0)), 25200008),
                (Diy(1, 1, (1, 29002001, 0), (2, 29002001, 0), (3, 29003001, 0)), 25200003),
                (Diy(2, 1, (1, 29001002, 0), (2, 29002001, 0), (3, 29003001, 0)), 25201002),
                (Diy(1, 1, (1, 29001002, 0), (2, 29002001, 0), (3, 29003001, 0), (99, 29990001, 0)), 25201003)
            })
                Code(nameof(BigWorldCommanderFashionUpdateRequest), bad, code, nameof(BigWorldCommanderFashionUpdateResponse));
            AssertEqual(false, s.CharacterInitialized, "rejected DIY does not initialize");
            Code(nameof(BigWorldCommanderFashionUpdateRequest), Diy(1, 2, (1, 29001002, 1002202), (2, 29002001, 0), (3, 29003001, 3001201)), 0, nameof(BigWorldCommanderFashionUpdateResponse));
            AssertEqual(2, s.CommanderGender, "gender persisted");
            AssertEqual(true, s.CharacterInitialized, "DIY initialized");
            Code(nameof(BigWorldCommanderFashionUpdateRequest), Diy(2, 2, (99, 29990002, 0)), 25201002, nameof(BigWorldCommanderFashionUpdateResponse));
            Code(nameof(BigWorldCommanderFashionUpdateRequest), Diy(2, 2, (99, 29990001, 0)), 0, nameof(BigWorldCommanderFashionUpdateResponse));
            AssertEqual(2, s.CurCommanderOutfitType, "current outfit switched");

            // In the world the saved outfit re-dresses the live commandant: RpcSyncPlayerSelfDIY with a nil replicate list (client
            // reloads the existing player-self NPC in place; a replicate makes it destroy/re-create the actor, which lost the head).
            int savedTeam = s.CurrentTeamId;
            s.CurrentTeamId = 1; // team 1 = the commandant (DefaultTeamPos1)
            h.Session.BigWorldWorldId = 400;
            s.LastLevelId = 4001;
            Code(nameof(BigWorldCommanderFashionUpdateRequest), Diy(1, 2, (1, 29001002, 1002202), (2, 29002001, 0), (3, 29003001, 3001201)), 0, nameof(BigWorldCommanderFashionUpdateResponse));
            object[] sync = ReadPushPayload<object[]>(h, "XRpcCommon", "diy sync push");
            object?[] syncArgs = MessagePack.MessagePackSerializer.Deserialize<object?[]>((byte[])sync[1]);
            object?[] diyData = (object?[])syncArgs[3]!;
            string Parts(object? partData) => string.Join(",", ((IEnumerable<object?>)((IDictionary<object, object>)partData!)["PartList"])
                .Select(p => (IDictionary<object, object>)p!).Select(p => $"{p["PartId"]}:{p["ColourId"]}"));
            var npcData = (IDictionary<object, object>)syncArgs[1]!;
            AssertEqual(("RpcSyncPlayerSelfDIY", 4001L, true, true, 2L, "29001002:1002202,29002001:0,29003001:3001201", "29001002:1002202,29002001:0,29003001:3001201"),
                ((string)sync[0], Convert.ToInt64(syncArgs[0]), (bool)npcData["IsPlayerSelf"], syncArgs[2] is null, Convert.ToInt64(diyData[1]), Parts(diyData[5]), Parts(npcData["PartData"])),
                "outfit sync: level, IsPlayerSelf NpcData, nil replicate list, gender, parts");
            h.Session.BigWorldWorldId = 0;
            Code(nameof(BigWorldCommanderFashionUpdateRequest), Diy(1, 2, (1, 29001002, 1002202), (2, 29002001, 0), (3, 29003001, 3001201)), 0, nameof(BigWorldCommanderFashionUpdateResponse));
            Thread.Sleep(50);
            AssertEqual(false, h.TryReadAvailablePacket("no diy sync", out Packet stray) && stray.Type == Packet.ContentType.Push, "no sync outside the world");
            s.CurrentTeamId = savedTeam;


            // After init: bag grows, no show reward queued.
            applySlice.Invoke(null, [h.Session, new List<RewardGoods> { new() { Id = 3, TemplateId = 29004003, Count = 1 } }]);
            AssertEqual(true, ReadPushPayload<NotifyBigWorldCommanderFashionBagUpdate>(h, nameof(NotifyBigWorldCommanderFashionBagUpdate), "bag push 2").DlcFashionBags.Contains(29004003), "second part");
            AssertNoAvailablePacket(h, "no show reward after init");
            Code(nameof(BigWorldCommanderFashionUpdateRequest), Diy(1, 1, (1, 29001002, 0), (2, 29002001, 0), (3, 29003001, 0), (4, 29004003, 0)), 0, nameof(BigWorldCommanderFashionUpdateResponse));

            // Reload from last save.
            Player reloaded = BsonSerializer.Deserialize<Player>(saves.LastSuccessfulReplacementBson ?? throw new InvalidDataException("no save"));
            BigWorldPlayerData after = new();
            fill.Invoke(null, [reloaded, after]);
            AssertEqual(2, after.CurrentTeamId, "reload current team");
            AssertEqual(1021006, after.TeamDict[2].CharacterList[1].CharacterId, "reload team 2");
            AssertEqual(29004003, after.CommanderFashionOutfits[1].WearFashionDict[4].PartId, "reload outfit 1");
            AssertEqual(1, after.CurCommanderOutfitType, "reload outfit type");
            AssertEqual(1, after.Gender, "reload gender");
            AssertEqual(6006103, after.CharacterWearFashionDict[1021007].DlcCharacterHeadInfo!.HeadFashionId, "reload head");
            List<Theatre5WorldNpcData> npcs = (List<Theatre5WorldNpcData>)npcList.Invoke(null, [reloaded])!;
            AssertEqual(3004, npcs[0].Id, "commandant npc");
            AssertEqual(4, npcs[0].PartData!.PartList.Count, "commandant parts from outfit 1");
            AssertEqual(6004301, npcs[1].Character!.FashionId, "member fashion");

            // A handler reaching an unseeded player seeds team 1 and pushes BigWorldTeamNotify before the response.
            using (LoopbackSessionHarness fresh = new(CreateDrawCompatibilityCharacter(uid + 1), CreateDrawCompatibilityPlayer(uid + 1), CreateDrawCompatibilityInventory(uid + 1, []), "bw-character-fresh"))
            {
                InvokeRegisteredRequestHandler(nameof(BigWorldTeamIndexChangeRequest), fresh.Session, 7, new BigWorldTeamIndexChangeRequest { CurrentTeamId = 2 });
                BigWorldTeamNotify notify = ReadPushPayload<BigWorldTeamNotify>(fresh, nameof(BigWorldTeamNotify), "seed notify");
                AssertEqual(2011001, notify.Teams.Single(t => t.TeamId == 1).CharacterList.Single().CharacterId, "seeded team notified");
                AssertEqual(25000006, ReadResponsePayload<BigWorldTeamIndexChangeResponse>(fresh, 7, nameof(BigWorldTeamIndexChangeResponse), "empty team 2").Code, "empty team 2 rejected");
                AssertEqual(1, fresh.Session.player.BigWorldState.CurrentTeamId, "current stays seeded team");
            }

            // In-world team change: RpcSyncTeamData [LevelId, CurNpcPos, NpcDataList, [CurNpcPos, NpcDataList, [[replicates]]]] carries the
            // new team; only members new to the client are replicated (server-controlled), the member switch persists CurNpcPos.
            Character worldCharacter = CreateDrawCompatibilityCharacter(uid + 2);
            worldCharacter.Characters.Add(new CharacterData { Id = 1021006 });
            worldCharacter.Characters.Add(new CharacterData { Id = 1021007 });
            Player worldPlayer = CreateDrawCompatibilityPlayer(uid + 2);
            using (LoopbackSessionHarness world = new(worldCharacter, worldPlayer, CreateDrawCompatibilityInventory(uid + 2, []), "bw-character-world"))
            {
                BigWorldCoreClient client = new(world);
                BigWorldEnterWorldResponse enter = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest());
                AssertEqual(0, enter.Code, "world enter");
                long lastSequence = BwInt(((object?[])MessagePack.MessagePackSerializer.Deserialize<object?[]>(enter.EnterResultData!.LevelData!)[3]!)[4]);
                BigWorldPlayerState ws = worldPlayer.BigWorldState;

                (object?[] Args, object?[] Fight, string Chars, string Replicated) Sync(string request, object message)
                {
                    client.Call<object?>(request, message);
                    List<object?[]> syncs = client.XRpcPushes("XRpcCommon").Where(e => (string)e[0]! == "RpcSyncTeamData").ToList();
                    AssertEqual(1, syncs.Count, request + " pushes one RpcSyncTeamData");
                    object?[] args = MessagePack.MessagePackSerializer.Deserialize<object?[]>((byte[])syncs[0][1]!);
                    object?[] fight = (object?[])args[3]!;
                    string Chars(object?[] npcs) => string.Join(",", npcs.Select(n => BwInt(((IDictionary<object, object>)((IDictionary<object, object>)n!)["Character"])["Id"])));
                    object?[] replicates = (object?[])((object?[])fight[2]!)[0]!;
                    AssertEqual(Chars((object?[])args[2]!), Chars((object?[])fight[1]!), "top-level and FightTeamNpcData lists agree");
                    AssertEqual(BwInt(args[1]), BwInt(fight[0]), "top-level and FightTeamNpcData CurNpcPos agree");
                    AssertEqual(4001L, BwInt(args[0]), "sync level");
                    // Component layout per retail 1276: a backstage member carries 16 components ending at XNpcAudioComponent
                    // (nil-listed active-only components crashed the client's XVarComponent deserializer).
                    static string Layout(object? comps)
                    {
                        object?[] list = MessagePack.MessagePackSerializer.Deserialize<object?[]>((byte[])comps!);
                        return $"{list.Length}:{((object?[])list[^1]!)[1]}";
                    }
                    // RepNpc HideFlags (index 12): ENpcHideFlags.Backstage (2) hides a member waiting backstage (retail 979).
                    static long Hide(object? repNpc) => BwInt(MessagePack.MessagePackSerializer.Deserialize<object?[]>((byte[])repNpc!)[12]);
                    return (args, fight, Chars((object?[])args[2]!), string.Join(",", replicates.Cast<object?[]>().Select(r => $"{r[0]}:{BwInt(r[2])}:{BwInt(r[5]) >> 4}:{Layout(r[7])}:hide{Hide(r[1])}")));
                }

                // Team 1 (current): commandant alone -> commandant + 1021006. Only 1021006 is new.
                var first = Sync(nameof(BigWorldTeamChangeRequest), new BigWorldTeamChangeRequest { ChangeTeam = Team(1, (0, 2011001), (1, 1021006)) });
                AssertEqual(("2011001,1021006", $"XNpc:15:{lastSequence + 1}:16:XNpcAudioComponent:hide2", 0L), (first.Chars, first.Replicated, BwInt(first.Args[1])), "first sync: both members, one hidden backstage replicate");
                // Another team for the same slot: 1021006 stays, 1021007 is new; the previously controlled commandant is gone.
                var second = Sync(nameof(BigWorldTeamChangeRequest), new BigWorldTeamChangeRequest { ChangeTeam = Team(1, (0, 1021007), (1, 1021006)) });
                AssertEqual(("1021007,1021006", $"XNpc:15:{lastSequence + 2}:16:XNpcAudioComponent:hide2", 0L), (second.Chars, second.Replicated, BwInt(second.Args[1])), "second sync: distinct team, only 1021007 replicated (controlled, but backstage so the client switches to it: backstage layout, hidden)");
                AssertEqual(((int)(lastSequence + 2) << 4 | 15, (int)(lastSequence + 1) << 4 | 15), (BigWorldActors.TeamNpcUuid(world.Session, 4001, 0), BigWorldActors.TeamNpcUuid(world.Session, 4001, 1)),
                    "team uuids: new member and the retained 1021006 keep the uuids the client knows");
                // A team that is not the current one is not on the client.
                client.Call<object?>(nameof(BigWorldTeamChangeRequest), new BigWorldTeamChangeRequest { ChangeTeam = Team(2, (0, 2011001), (1, 1021006)) });
                AssertEqual(false, client.XRpcPushes("XRpcCommon").Any(e => (string)e[0]! == "RpcSyncTeamData"), "non-current team change pushes nothing");
                // Switching the current team index syncs the other team.
                var third = Sync(nameof(BigWorldTeamIndexChangeRequest), new BigWorldTeamIndexChangeRequest { CurrentTeamId = 2 });
                AssertEqual(("2011001,1021006", $"XNpc:15:{lastSequence + 3}:33:XNpcFightInfoRecorderComponent:hide2", 0L), (third.Chars, third.Replicated, BwInt(third.Args[1])), "index change syncs team 2, replicating only the (controlled, backstage) commandant");
                client.Call<object?>(nameof(BigWorldTeamIndexChangeRequest), new BigWorldTeamIndexChangeRequest { CurrentTeamId = 2 });
                AssertEqual(false, client.HasPush("XRpcCommon"), "re-selecting the current team pushes nothing");

                // Member switch: RpcSwitchPlayerNpcRequest [LevelId, CharacterId, NpcId, IsTrial] -> RpcSwitchPlayerNpc [LevelId, Pos].
                byte[] switchArgs = MessagePack.MessagePackSerializer.Serialize(new object[] { 4001, 1021006, 1060, false });
                AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", new object[] { "RpcSwitchPlayerNpcRequest", switchArgs, (byte)15, (byte)1, 4001 }).Code, "switch member");
                AssertEqual(1, ws.CurNpcPos, "switch persisted CurNpcPos");
                object?[] switched = client.XRpcPushes("XRpcCommon").Single(e => (string)e[0]! == "RpcSwitchPlayerNpc");
                AssertEqual((4001L, 1L), (BwInt(MessagePack.MessagePackSerializer.Deserialize<object?[]>((byte[])switched[1]!)[0]), BwInt(MessagePack.MessagePackSerializer.Deserialize<object?[]>((byte[])switched[1]!)[1])), "switch push [LevelId, Pos]");
                // The controlled member survives the next team change at its new pos.
                var fourth = Sync(nameof(BigWorldTeamChangeRequest), new BigWorldTeamChangeRequest { ChangeTeam = Team(2, (0, 1021007), (1, 2011001), (2, 1021006)) });
                AssertEqual(("1021007,2011001,1021006", 2L), (fourth.Chars, BwInt(fourth.Args[1])), "controlled 1021006 keeps control at pos 2");
                AssertEqual(2, ws.CurNpcPos, "CurNpcPos follows the controlled member");
                // Backstage layout by NPC fight logic (live: a backstage commandant lacking XSkillActionComponent threw
                // NullReferenceException in XNpcFSMComponent.IsNpcDoSkill every frame and on switch-out):
                // NeedFightLogicType 1 (commandant 3004) keeps the full layout, an ordinary member (3033) the 16-component one.
                static string Backstage(Player p, int npcId) => Layout(BigWorldModule.BuildNpcComponents(p, new Theatre5WorldNpcData { Id = npcId }, 0, isBackstage: true));
                static string Layout(byte[] comps) { object?[] list = MessagePack.MessagePackSerializer.Deserialize<object?[]>(comps); return $"{list.Length}:{((object?[])list[^1]!)[1]}"; }
                AssertEqual(("33:XNpcFightInfoRecorderComponent", "16:XNpcAudioComponent"), (Backstage(worldPlayer, 3004), Backstage(worldPlayer, 3033)), "backstage layout: commandant full, member 16 components");
                // XNpcModelComponent snapshot = hidden-joint counts derived from Npc.BornMagic (AddBuff -> Buff -> HideJoint), not a
                // constant. Retail 979/1276: 1021006 {Engine01,Engine02}, 1021007 {SGHide}, commandant 2011001 [nil].
                var characterNpcs = AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.bigworld.common.character.BigWorldCharacterTable>().ToDictionary(r => r.Id, r => r.NpcId);
                string Joints(int characterId)
                {
                    object?[] list = MessagePack.MessagePackSerializer.Deserialize<object?[]>(BigWorldModule.BuildNpcComponents(worldPlayer, new Theatre5WorldNpcData { Id = characterNpcs[characterId] }, 0, isBackstage: true));
                    object?[] model = MessagePack.MessagePackSerializer.Deserialize<object?[]>((byte[])list.Cast<object?[]>().Single(c => (string)c[1]! == "XNpcModelComponent")[0]!);
                    return model[0] is null ? "nil" : string.Join(",", ((IDictionary<object, object>)model[0]!).Select(e => $"{e.Key}:{e.Value}").OrderBy(s => s, StringComparer.Ordinal));
                }
                AssertEqual(("Engine01:1,Engine02:1", "SGHide:1", "SGHide:1", "nil"), (Joints(1021006), Joints(1021007), Joints(1031005), Joints(2011001)), "model joints equal retail (979/1276) for 1021006, 1021007, 1031005 and the commandant");
                AssertEqual(("Chain:1", "nil"), (Joints(1071005), Joints(1071004)), "Karenina Effulgence hides Chain; Karenina Babylonia Test hides nothing");
            }
            Console.WriteLine("BigWorld character: PASS");

            void Code(string name, object request, int code, string responseName)
            {
                int packetId = ++id;
                InvokeRegisteredRequestHandler(name, h.Session, packetId, request);
                Packet packet;
                while ((packet = h.ReadPacket(name)).Type == Packet.ContentType.Push)
                    AssertEqual(nameof(BigWorldTeamNotify), MessagePack.MessagePackSerializer.Deserialize<Packet.Push>(packet.Content).Name, "only team notify pushes");
                Packet.Response response = MessagePack.MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(packetId, response.Id, name + " id");
                AssertEqual(responseName, response.Name, name + " response name");
                AssertEqual(code, Newtonsoft.Json.Linq.JObject.Parse(MessagePack.MessagePackSerializer.ConvertToJson(response.Content)).Value<int>("Code"), name + " code");
            }
        }
    }
}
