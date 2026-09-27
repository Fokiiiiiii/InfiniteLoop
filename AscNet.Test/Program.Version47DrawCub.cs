using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.client.draw;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.draw;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.partner;
using MessagePack;

namespace AscNet.Test;

internal partial class Program
{
   private static void ValidateVersion47DrawCubCompatibility()
    {
        AssertPartnerComposeViaSharedConstructor([16_410_000, 16_400_000]);
        AssertPartnerMutationDurabilityAndCosts();
    }


    private static DrawInfo[] Version47CatalogTemplates()
    {
        return RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager")
            .GetField("DrawTemplates", BindingFlags.Static | BindingFlags.NonPublic)?
            .GetValue(null) as DrawInfo[]
            ?? throw new MissingFieldException("AscNet.GameServer.Game.DrawManager", "DrawTemplates");
    }



    /// <summary>Compose CUBs through their authored shards and shared constructor.</summary>
    private static void AssertPartnerComposeViaSharedConstructor(int[] ids)
    {
        foreach (int templateId in ids)
        {
            PartnerTable config = TableReaderV2.Parse<PartnerTable>().Single(p => p.Id == templateId);
            ItemTable shard = TableReaderV2.Parse<ItemTable>().Single(i => i.Id == config.ChipItemId);
            AscNet.Common.Database.Character character = new()
            {
                Uid = 90_001 + templateId,
                Characters = [],
                Equips = [],
                Fashions = [],
                Partners = []
            };
            AscNet.Common.Database.Inventory inventory = new()
            {
                Uid = character.Uid,
                Items = [new Item { Id = config.ChipItemId, Count = config.ChipNeedCount }]
            };
            using MongoCollectionOverride mongoOverride =
                MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
            using LoopbackSessionHarness harness = new(character, inventory: inventory, sessionId: $"partner-{templateId}-test");

            InvokeRequestHandler(harness, nameof(PartnerComposeRequest), 19_001,
                new PartnerComposeRequest { TemplateIds = [templateId], IsOneKey = false });
            AssertItemPush(harness.ReadPacket("PartnerComposeRequest item push"), config.ChipItemId, 0,
                "PartnerComposeRequest item push");
            Packet partnerPacket = harness.ReadPacket("NotifyPartnerDataList");
            AssertEqual(Packet.ContentType.Push, partnerPacket.Type, "NotifyPartnerDataList packet type");
            Packet.Push partnerPush = MessagePackSerializer.Deserialize<Packet.Push>(partnerPacket.Content);
            NotifyPartnerDataList payload = MessagePackSerializer.Deserialize<NotifyPartnerDataList>(partnerPush.Content);
            PartnerData partner = payload.PartnerDataList.Single();
            AssertEqual(templateId, partner.TemplateId, $"composed partner {templateId} template");
            AssertIntegerList([1], payload.OperateTypes.Select(v => (long)v).ToArray(), "compose operation");
            AssertEqual(config.InitQuality, partner.Quality, $"composed partner {templateId} initial quality");
            AssertEqual(1, partner.Level, $"composed partner {templateId} level");
            AssertEqual(1, character.Partners.Count, $"composed partner {templateId} persisted count");
            AssertEqual(0, ((PartnerComposeResponse)ReadResponsePayload(
                harness, 19_001, nameof(PartnerComposeResponse), "PartnerComposeResponse",
                typeof(PartnerComposeResponse), maxPacketsToRead: 16)).Code,
                $"composed partner {templateId} code");
            AssertEqual(true, harness.Session.player.ArchivePartnerUnlockIds.Contains(templateId),
                $"composed partner {templateId} archive membership");
        }
    }

    /// <summary>Mutation handlers must persist on success, reject insufficient resources, and apply
    /// table-derived costs — no successful mutation is memory-only.</summary>
    private static void AssertPartnerMutationDurabilityAndCosts()
    {
        int templateId = 16_410_000;
        PartnerTable config = TableReaderV2.Parse<PartnerTable>().Single(p => p.Id == templateId);

        AscNet.Common.Database.Character character = new()
        {
            Uid = 90_200,
            Characters = [],
            Equips = [],
            Fashions = [],
            Partners =
            [
                new PartnerData
                {
                    Id = 1,
                    TemplateId = templateId,
                    Level = 1,
                    Quality = config.InitQuality,
                    SkillList = BuildInitialSkillListFor(templateId),
                    UnlockSkillGroup = TableReaderV2.Parse<PartnerSkillTable>()
                        .Single(s => s.PartnerId == templateId).MainSkillGroupId.ToList()
                }
            ]
        };
        AscNet.Common.Database.Inventory inventory = new()
        {
            Uid = character.Uid,
            Items =
            [
                new Item { Id = AscNet.Common.Database.Inventory.Coin, Count = 1_000_000 },
                new Item { Id = 30113, Count = 100 }
            ]
        };

        using MongoCollectionOverride mongoOverride =
            MongoCollectionOverride.InstallForDailySignInCompatibility(
                out _,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characterCollection,
                out _);
        using LoopbackSessionHarness harness = new(character, inventory: inventory, sessionId: "partner-mutation-test");

        PartnerData partner = character.Partners.Single();
        int savesBefore = characterCollection.ReplaceOneCalls;

        AscNet.Common.Database.Character poorCharacter = new()
        {
            Uid = 90_201,
            Characters = [],
            Equips = [],
            Fashions = [],
            Partners = character.Partners.Select(BsonClone).ToList()
        };
        AscNet.Common.Database.Inventory poorInventory = new()
        {
            Uid = 90_201,
            Items = [new Item { Id = AscNet.Common.Database.Inventory.Coin, Count = 0 }]
        };
        using LoopbackSessionHarness poorHarness = new(poorCharacter, inventory: poorInventory, sessionId: "partner-poor-test");
        InvokeRequestHandler(poorHarness, nameof(PartnerLevelUpRequest), 19_201,
            new PartnerLevelUpRequest { PartnerId = partner.Id, UseItems = new() { [30113] = 1 } });
        AssertEqual(1, ((PartnerLevelUpResponse)ReadResponsePayload(
            poorHarness, 19_201, nameof(PartnerLevelUpResponse), "insufficient PartnerLevelUpResponse",
            typeof(PartnerLevelUpResponse), maxPacketsToRead: 16)).Code,
            "insufficient partner level-up rejected");

        PartnerBreakThroughTable breakthrough = TableReaderV2.Parse<PartnerBreakThroughTable>()
            .Single(b => b.PartnerId == templateId && b.BreakTimes == 0);
        InvokeRequestHandler(harness, nameof(PartnerLevelUpRequest), 19_202,
            new PartnerLevelUpRequest { PartnerId = partner.Id, UseItems = new() { [30113] = 1 } });
        harness.ReadPacket("level-up item push");
        PartnerLevelUpResponse levelUp = (PartnerLevelUpResponse)ReadResponsePayload(
            harness, 19_202, nameof(PartnerLevelUpResponse), "PartnerLevelUpResponse",
            typeof(PartnerLevelUpResponse), maxPacketsToRead: 16);
        AssertEqual(0, levelUp.Code, "partner level-up code");
        AssertEqual(true, partner.Level > 1, "partner level advanced");
        AssertEqual(savesBefore + 1, characterCollection.ReplaceOneCalls, "partner level-up persists Character");
        AssertEqual(true, partner.Level <= breakthrough.LevelLimit, "partner level respects breakthrough cap");
    }

    private static List<PartnerSkillData> BuildInitialSkillListFor(int templateId)
    {
        PartnerSkillTable skillConfig = TableReaderV2.Parse<PartnerSkillTable>().Single(s => s.PartnerId == templateId);
        PartnerMainSkillGroupTable mainGroup = TableReaderV2.Parse<PartnerMainSkillGroupTable>()
            .Single(g => g.Id == skillConfig.DefaultMainSkillGroupId);
        ILookup<int, PartnerPassiveSkillGroupTable> passives =
            TableReaderV2.Parse<PartnerPassiveSkillGroupTable>().ToLookup(g => g.Id);
        List<PartnerSkillData> skills =
        [
            new PartnerSkillData { Id = mainGroup.SkillId.First(), Level = 1, IsWear = true, Type = 1 }
        ];
        skills.AddRange(skillConfig.PassiveSkillGroupId.Select(gid => new PartnerSkillData
        {
            Id = passives[gid].Single().SkillId,
            Level = 1,
            IsWear = false,
            Type = 2
        }));
        return skills;
    }

    private static PartnerData BsonClone(PartnerData source)
    {
        return new PartnerData
        {
            Id = source.Id,
            TemplateId = source.TemplateId,
            Name = source.Name,
            CharacterId = source.CharacterId,
            Level = source.Level,
            Exp = source.Exp,
            BreakThrough = source.BreakThrough,
            IsLock = source.IsLock,
            Quality = source.Quality,
            StarSchedule = source.StarSchedule,
            SkillList = source.SkillList.Select(s => new PartnerSkillData
            {
                Id = s.Id,
                Level = s.Level,
                IsWear = s.IsWear,
                Type = s.Type
            }).ToList(),
            UnlockSkillGroup = source.UnlockSkillGroup.ToList(),
            CreateTime = source.CreateTime
        };
    }
}
