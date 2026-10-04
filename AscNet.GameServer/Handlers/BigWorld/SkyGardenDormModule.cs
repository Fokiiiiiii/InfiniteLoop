using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.bigworld.skygarden.dormitory;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // SkyGarden dorm (xskygardendorm). NotifySgDormData is table-driven: a fresh player reproduces the
    // retail capture (InitFurniture ids 1..n, InitFashion, one default container per SgDormArea).
    internal static class SkyGardenDormModule
    {
        // EN share/text/CodeText 20248xxx.
        internal const int FurnitureCfgNotExist = 20248001;     // SgDormFurnitureCfgNotExist
        internal const int LayoutCfgNotExist = 20248002;        // SgDormLayoutCfgNotExist
        internal const int FashionCfgNotExist = 20248003;       // SgDormFashionCfgNotExist
        internal const int AreaCfgNotExist = 20248004;          // SgDormAreaCfgNotExist
        internal const int FurnitureNotExist = 20248005;        // SgDormFurnitureNotExist (not owned)
        internal const int FurnitureInfoIsNull = 20248006;      // SgDormFurnitureInfoIsNull
        internal const int AreaTypeNotMatch = 20248007;         // SgDormAreaTypeNotMatch
        internal const int ContainerPutCountLimit = 20248008;   // SgDormContainerPutFurnitureCountLimit
        internal const int AreaFurnitureCountOverLimit = 20248010; // SgDormAreaFurnitureCountOverLimit
        internal const int FashionLocked = 20248011;            // SgDormFashionUnlock ("Coating locked")
        internal const int FurnitureNotContainerType = 20248012; // SgDormFurnitureNotContainerType
        internal const int CanNotPlaceContainer = 20248013;     // SgDormCanNotPlaceContainerFurniture
        internal const int FurnitureAlreadyOccupied = 20248014; // SgDormFurnitureAlreadyOccupied
        internal const int FashionIdInvalid = 20248015;         // SgDormFashionIdInvalid
        internal const int SaveAndApplyInvalid = 20248016;      // SgDormSaveAndApplyLayoutInvalid
        internal const int SaveAndApplySame = 20248017;         // SgDormSaveAndApplyLayoutSame
        internal const int IdAndPhotoIdIsZero = 20248020;       // SgDormFurnitureIdAndPhotoIdIsZero
        internal const int PhotoAlreadyOccupied = 20248021;     // SgDormFurniturePhotoAlreadyOccupied

        private const int DefaultScale = 1000; // XSgFurnitureData ScaleRatio

        private static readonly Lazy<Dictionary<string, List<string>>> Config = new(() =>
            TableReaderV2.Parse<SgDormConfigTable>().ToDictionary(row => row.Key, row => row.Values.Where(v => !string.IsNullOrEmpty(v)).ToList()));
        private static readonly Lazy<Dictionary<int, SgDormAreaTable>> Areas = new(() =>
            TableReaderV2.Parse<SgDormAreaTable>().ToDictionary(row => row.AreaType));
        private static readonly Lazy<Dictionary<int, SgDormLayoutTable>> Layouts = new(() =>
            TableReaderV2.Parse<SgDormLayoutTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<int, SgDormFurnitureTable>> Furniture = new(() =>
            TableReaderV2.Parse<SgDormFurnitureTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<int, SgDormFurnitureTypeTable>> FurnitureTypes = new(() =>
            TableReaderV2.Parse<SgDormFurnitureTypeTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<int, SgDormFashionTable>> Fashions = new(() =>
            TableReaderV2.Parse<SgDormFashionTable>().ToDictionary(row => row.Id));

        internal static NotifySgDormData State(Player player) => player.BigWorldState.SgDorm ??= CreateInitial();

        private static NotifySgDormData CreateInitial()
        {
            NotifySgDormData dorm = new() { CurFashionId = int.Parse(Config.Value["InitFashion"][0]) };
            foreach (string cfgId in Config.Value["InitFurniture"])
                dorm.FurnitureList.Add(new SgDormFurnitureItem { Id = dorm.FurnitureList.Count + 1, CfgId = int.Parse(cfgId) });
            foreach (SgDormAreaTable area in Areas.Value.Values.OrderBy(row => row.AreaType))
            {
                int containerCfgId = Layouts.Value[area.DefaultLayoutId].DefaultContainer;
                SgDormFurnitureItem? container = dorm.FurnitureList.FirstOrDefault(item => item.CfgId == containerCfgId);
                SgDormLayoutData layout = new() { AreaType = area.AreaType, LayoutId = area.DefaultLayoutId };
                if (container is not null)
                    layout.FurnitureInfos.Add(new SgDormFurnitureInfo
                    {
                        Container = new SgDormPlacedFurniture { Id = container.Id, CfgId = container.CfgId, Scale = DefaultScale }
                    });
                dorm.LayoutList.Add(layout);
                dorm.CurAreaLayout[area.AreaType] = area.DefaultLayoutId;
            }
            return dorm;
        }

        // Core: BigWorld enter.
        internal static void SendDormData(Session session) => session.SendPush(State(session.player));

        // Furniture (SgDormFurniture ids) and coating (SgDormFashion ids, 27xxxxxx) goods returned unapplied by BigWorldRewardService.Grant.
        internal static void ApplySliceRewards(Session session, IReadOnlyList<RewardGoods> goods)
        {
            NotifySgDormData dorm = State(session.player);
            NotifySgDormFurnitureAdd push = new();
            List<NotifySgDormFashionAdd> fashionPushes = [];
            foreach (RewardGoods good in goods)
            {
                // AscNet policy [INF]: a coating already owned (or SgDormFashion.IsDefault) is dropped. ConvertItemId/Count would convert
                // the duplicate, but every row names item 999999, which does not exist in Item.tsv.
                if (Fashions.Value.TryGetValue(good.TemplateId, out SgDormFashionTable? fashion))
                {
                    if (fashion.IsDefault != 1 && dorm.DormFashionList.All(item => item.Id != fashion.Id))
                    {
                        SgDormFashionData owned = new() { Id = fashion.Id };
                        dorm.DormFashionList.Add(owned);
                        fashionPushes.Add(new NotifySgDormFashionAdd { AddDormFashion = owned });
                    }
                    continue;
                }
                if (!Furniture.Value.TryGetValue(good.TemplateId, out SgDormFurnitureTable? cfg))
                    continue;
                for (int i = 0; i < good.Count; i++)
                {
                    // AscNet policy: copies beyond SgDormFurniture.MaxCount are dropped (every row authors MaxCount 1).
                    if (dorm.FurnitureList.Count(item => item.CfgId == cfg.Id) >= Math.Max(1, cfg.MaxCount))
                        break;
                    // AscNet policy: instance id = current max + 1 (matches the 1..n initial numbering).
                    SgDormFurnitureItem item = new() { Id = dorm.FurnitureList.Select(f => f.Id).DefaultIfEmpty(0).Max() + 1, CfgId = cfg.Id };
                    dorm.FurnitureList.Add(item);
                    push.AddFurnitureList.Add(item);
                }
            }
            if (push.AddFurnitureList.Count == 0 && fashionPushes.Count == 0)
                return;
            session.player.Save();
            if (push.AddFurnitureList.Count > 0)
                session.SendPush(push);
            foreach (NotifySgDormFashionAdd fashionPush in fashionPushes)
                session.SendPush(fashionPush);
            BigWorldTaskModule.OnProgressChanged(session);
        }

        [RequestPacketHandler("SgDormSaveAndApplyLayoutRequest")]
        public static void SgDormSaveAndApplyLayoutRequestHandler(Session session, Packet.Request packet)
        {
            SgDormSaveAndApplyLayoutRequest request = packet.Deserialize<SgDormSaveAndApplyLayoutRequest>();
            NotifySgDormData dorm = State(session.player);
            int code = ValidateLayout(dorm, request);
            if (code == 0)
            {
                if (request.SaveLayoutId > 0)
                {
                    dorm.LayoutList.RemoveAll(layout => layout.AreaType == request.AreaType && layout.LayoutId == request.SaveLayoutId);
                    dorm.LayoutList.Add(new SgDormLayoutData
                    {
                        AreaType = request.AreaType,
                        LayoutId = request.SaveLayoutId,
                        FurnitureInfos = request.SaveFurnitureInfos
                    });
                }
                if (request.ApplyLayoutId > 0)
                    dorm.CurAreaLayout[request.AreaType] = request.ApplyLayoutId;
                session.player.Save();
            }
            session.SendResponse(new SgDormSaveAndApplyLayoutResponse { Code = code }, packet.Id);
        }

        private static int ValidateLayout(NotifySgDormData dorm, SgDormSaveAndApplyLayoutRequest request)
        {
            if (!Areas.Value.TryGetValue(request.AreaType, out SgDormAreaTable? area))
                return AreaCfgNotExist;
            if (request.SaveLayoutId <= 0 && request.ApplyLayoutId <= 0)
                return SaveAndApplyInvalid;
            // AscNet policy [INFERENCE from CodeText 20248017]: saving and applying the same preset in one call is rejected.
            if (request.SaveLayoutId > 0 && request.SaveLayoutId == request.ApplyLayoutId)
                return SaveAndApplySame;
            foreach (int layoutId in new[] { request.SaveLayoutId, request.ApplyLayoutId }.Where(id => id > 0))
            {
                if (!Layouts.Value.TryGetValue(layoutId, out SgDormLayoutTable? layout))
                    return LayoutCfgNotExist;
                if (layout.AreaType != request.AreaType)
                    return AreaTypeNotMatch;
            }
            if (request.SaveLayoutId <= 0)
                return 0;
            if (request.SaveFurnitureInfos.Count == 0)
                return FurnitureInfoIsNull;

            Dictionary<int, int> owned = dorm.FurnitureList.ToDictionary(item => item.Id, item => item.CfgId);
            HashSet<int> usedIds = [];
            HashSet<int> usedPhotos = [];
            int placedCount = 0;
            int photoTypeId = int.Parse(Config.Value["LocalPhotoFurnitureTypeId"][0]);
            foreach (SgDormFurnitureInfo info in request.SaveFurnitureInfos)
            {
                SgDormPlacedFurniture? container = info.Container;
                if (container is null || container.Id <= 0)
                    return FurnitureInfoIsNull;
                int code = CheckOwned(owned, usedIds, container, out SgDormFurnitureTypeTable? containerType, out SgDormFurnitureTable? containerCfg);
                if (code != 0)
                    return code;
                if (containerType!.IsContainer != 1)
                    return FurnitureNotContainerType;
                if (containerType.AreaType != request.AreaType)
                    return AreaTypeNotMatch;

                Dictionary<int, int> capacity = containerCfg!.PutMajorType.Zip(containerCfg.PutCapacity)
                    .Where(pair => pair.First > 0).ToDictionary(pair => pair.First, pair => pair.Second);
                Dictionary<int, int> put = [];
                foreach (SgDormPlacedFurniture placed in info.PlacementFurniture)
                {
                    SgDormFurnitureTypeTable? type;
                    if (placed.Id <= 0)
                    {
                        // Album photo (XSgFurnitureData: Id 0, PhotoId = album photo id).
                        if (placed.PhotoId <= 0)
                            return IdAndPhotoIdIsZero;
                        if (!usedPhotos.Add(placed.PhotoId))
                            return PhotoAlreadyOccupied;
                        type = FurnitureTypes.Value[photoTypeId];
                    }
                    else
                    {
                        code = CheckOwned(owned, usedIds, placed, out type, out _);
                        if (code != 0)
                            return code;
                        if (type!.IsContainer == 1)
                            return CanNotPlaceContainer;
                    }
                    if (type.AreaType != request.AreaType)
                        return AreaTypeNotMatch;
                    int count = put[type.MajorType] = put.GetValueOrDefault(type.MajorType) + 1;
                    if (count > capacity.GetValueOrDefault(type.MajorType))
                        return ContainerPutCountLimit;
                    if (++placedCount > area.MaxFurnitureCount)
                        return AreaFurnitureCountOverLimit;
                }
            }
            return 0;
        }

        private static int CheckOwned(Dictionary<int, int> owned, HashSet<int> usedIds, SgDormPlacedFurniture furniture,
            out SgDormFurnitureTypeTable? type, out SgDormFurnitureTable? cfg)
        {
            type = null;
            cfg = null;
            if (!owned.TryGetValue(furniture.Id, out int ownedCfgId) || ownedCfgId != furniture.CfgId)
                return FurnitureNotExist;
            if (!usedIds.Add(furniture.Id))
                return FurnitureAlreadyOccupied;
            if (!Furniture.Value.TryGetValue(furniture.CfgId, out cfg) || !FurnitureTypes.Value.TryGetValue(cfg.TypeId, out type))
                return FurnitureCfgNotExist;
            return 0;
        }

        [RequestPacketHandler("SgDormSetFashionRequest")]
        public static void SgDormSetFashionRequestHandler(Session session, Packet.Request packet)
        {
            SgDormSetFashionRequest request = packet.Deserialize<SgDormSetFashionRequest>();
            NotifySgDormData dorm = State(session.player);
            int code = request.FashionId <= 0 ? FashionIdInvalid
                : !Fashions.Value.TryGetValue(request.FashionId, out SgDormFashionTable? fashion) ? FashionCfgNotExist
                // Unlocked = owned or SgDormFashion.IsDefault (XSkyGardenDormModel:317-323).
                : fashion.IsDefault != 1 && dorm.DormFashionList.All(item => item.Id != request.FashionId) ? FashionLocked
                : 0;
            if (code == 0)
            {
                dorm.CurFashionId = request.FashionId;
                session.player.Save();
            }
            session.SendResponse(new SgDormSetFashionResponse { Code = code }, packet.Id);
        }
    }
}
