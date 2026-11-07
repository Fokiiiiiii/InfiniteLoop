using System.Globalization;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.equip;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    [MessagePackObject(true)]
    public class EquipTransformChipRequest
    {
        public int SuitId;
        public int Site;
        // The client's XMessagePack encodes an empty Lua table as nil.
        public List<int>? UseIdList;
    }

    [MessagePackObject(true)]
    public class EquipTransformChipResponse
    {
        public int Code;
        public EquipData? EquipData;
    }

    [MessagePackObject(true)]
    public class EquipUpdateRecycleRequest
    {
        public int EquipId;
        public bool IsRecycle;
    }

    [MessagePackObject(true)]
    public class EquipUpdateRecycleResponse
    {
        public int Code;
    }

    internal partial class EquipModule
    {
        private const int EquipManagerGetEquipNotFound = 20021004;
        private const int EquipManagerDecomposeDoNot = 20021019;
        private const int EquipIsLockedCode = 20021025;
        private const int EquipSuitCanNotTransform = 20021049;
        private const int EquipSuitSiteError = 20021050;
        private const int EquipTransformChipUseEquipCountError = 20021051;
        private const int EquipTransformChipUseEquipSuitError = 20021052;

        // Mirrors XEquipModel:IsEquipCanRecycle plus the team-prefab filter of GetCanRecycleAwarenessIds.
        private static bool IsRecyclableChip(Session session, EquipData chip, EquipTable template) =>
            template.Site > 0
            && template.Star <= 5
            && Character.IsOwnableEquipTemplate(template)
            && chip.CharacterId == 0
            && chip.Level == 1
            && chip.Exp == 0
            && chip.Breakthrough == 0
            && !chip.IsLock
            && chip.ResonanceInfo is not { Count: > 0 }
            && chip.UnconfirmedResonanceInfo is not { Count: > 0 }
            && chip.AwakeSlotList is not { Count: > 0 }
            && !session.player.IsEquipInTeamPrefab(chip.Id)
            && !session.player.IsEquipInChipGroup(chip.Id);

        [RequestPacketHandler("EquipUpdateRecycleRequest")]
        public static void EquipUpdateRecycleRequestHandler(Session session, Packet.Request packet)
        {
            EquipUpdateRecycleRequest request = packet.Deserialize<EquipUpdateRecycleRequest>();
            EquipData? equip = session.character.Equips.Find(candidate => candidate.Id == request.EquipId);
            if (equip is null)
            {
                session.SendResponse(new EquipUpdateRecycleResponse { Code = 20021012 }, packet.Id);
                return;
            }

            if (equip.IsRecycle != request.IsRecycle)
            {
                // Clearing the mark is always allowed; setting it requires a currently recyclable chip.
                if (request.IsRecycle
                    && !(Character.ResolveEquipTemplate(equip.TemplateId) is { } template
                        && IsRecyclableChip(session, equip, template)))
                {
                    session.SendResponse(new EquipUpdateRecycleResponse { Code = EquipManagerDecomposeDoNot }, packet.Id);
                    return;
                }

                equip.IsRecycle = request.IsRecycle;
                session.character.Save();
            }

            session.SendResponse(new EquipUpdateRecycleResponse(), packet.Id);
        }

        [RequestPacketHandler("EquipTransformChipRequest")]
        public static void EquipTransformChipRequestHandler(Session session, Packet.Request packet)
        {
            EquipTransformChipRequest request = packet.Deserialize<EquipTransformChipRequest>();
            void Fail(int code) => session.SendResponse(new EquipTransformChipResponse { Code = code }, packet.Id);

            if (!DrawManager.IsTransformSuitAvailable(request.SuitId))
            {
                Fail(EquipSuitCanNotTransform);
                return;
            }

            EquipSuitTable? suit = TableReaderV2.Parse<EquipSuitTable>().Find(row => row.Id == request.SuitId);
            EquipTable? target = suit is not null && request.Site >= 1 && request.Site <= suit.EquipIds.Count
                ? Character.ResolveEquipTemplate((uint)suit.EquipIds[request.Site - 1])
                : null;
            if (target is null || target.Site != request.Site || target.SuitId != request.SuitId)
            {
                Fail(EquipSuitSiteError);
                return;
            }

            ConfigTable? useCountRow = TableReaderV2.Parse<ConfigTable>()
                .Find(row => row.Key == "EquipTransformChipUseCount");
            List<int> useIds = request.UseIdList ?? [];
            if (useCountRow is null
                || !int.TryParse(useCountRow.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int useCount)
                || useIds.Count != useCount
                || useIds.Distinct().Count() != useIds.Count)
            {
                Fail(EquipTransformChipUseEquipCountError);
                return;
            }

            // The client treats the first id as the main material whose progression the result inherits.
            List<EquipData> materials = [];
            foreach (int useId in useIds)
            {
                EquipData? material = useId > 0
                    ? session.character.Equips.Find(candidate => candidate.Id == useId)
                    : null;
                if (material is null)
                {
                    Fail(EquipManagerGetEquipNotFound);
                    return;
                }

                EquipTable? template = Character.ResolveEquipTemplate(material.TemplateId);
                if (template is null
                    || template.Site <= 0
                    || template.SuitId != request.SuitId
                    || template.Site == request.Site)
                {
                    Fail(EquipTransformChipUseEquipSuitError);
                    return;
                }
                if (material.IsLock)
                {
                    Fail(EquipIsLockedCode);
                    return;
                }
                if (material.CharacterId != 0
                    || session.player.IsEquipInTeamPrefab(material.Id)
                    || session.player.IsEquipInChipGroup(material.Id))
                {
                    Fail(EquipManagerDecomposeDoNot);
                    return;
                }
                materials.Add(material);
            }

            EquipData main = materials[0];
            EquipBreakThroughTable? progression = Character.ResolveEquipBreakThrough(
                (uint)target.Id, main.Breakthrough);
            if (progression is null || main.Level < 1 || main.Level > progression.LevelLimit)
            {
                Fail(20021002);
                return;
            }

            EquipData? result = session.character.AddEquip((uint)target.Id, level: main.Level);
            if (result is null)
            {
                Fail(20021005);
                return;
            }
            result.Exp = main.Exp;
            result.Breakthrough = main.Breakthrough;

            NotifyEquipDataList notify = new();
            foreach (EquipData material in materials)
            {
                session.character.Equips.Remove(material);
                notify.DeletedEquipIdList.Add(material.Id);
                foreach ((uint EquipId, int Slot) key in session.PendingEquipResonances.Keys
                             .Where(key => key.EquipId == material.Id).ToList())
                    session.PendingEquipResonances.Remove(key);
            }
            notify.EquipDataList.Add(result);
            session.character.Save();

            session.SendPush(notify);
            session.SendResponse(new EquipTransformChipResponse { EquipData = result }, packet.Id);
        }
    }
}
