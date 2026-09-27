using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.punishaar;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Runtime.CompilerServices;

namespace AscNet.GameServer.Handlers;

// Circuit Calculus (4.8 xpunishaar). Battle is simulated in client Lua; the server owns the run economy.
// Every mutating request works on a staged copy of PlayerPunishaarState, SaveChecked()s it, and only then
// sends notifies (Gold/MasterCard/SubCard/RewardResult) followed by the response. A failed save is settled
// against the durable document instead of the pre-request copy: a replacement that landed before the
// acknowledgement was lost is adopted and answered as success, one that did not land answers Code=1
// without pushes, and a document that cannot be read at all closes the session without saving anything.
//
// AscNet policy (user-authorized; retail CardGroup rows and a ShopGroup schema are not distributed):
// - Shop pools: the Shop table is split into blocks ended by each "Supply Shop" row; the distinct
//   StageGroup.RemedyShop groups (stage order) map 1:1 onto those blocks. A stage's shop nodes draw
//   ShopRandomAmount candidates from its block's non-Supply shops; the remedy shop is the block's Supply shop.
// - Shop stock: IsShow cards filtered by shop name (Red/Yellow/Blue Orb = that color, master or sub;
//   Battle/Support Card = master/sub types in the block colors; Mystery = everything; Supply = block
//   colors), uniform pick while total Size <= MaxShopCardAreaCount. Stock is always level 1 (master levels
//   come from merges and EventReward); sub cards carry no level rows and are always level 1.
// - Starting deck is empty (every stage opens on a shop and InitialGoldCount buys a level-1 card).
// - RNG is a per-save SplitMix64 state persisted with the save, so relog/retry never rerolls.
// - AllGold counts gold earned (node entry, fights, events, remedy), not sale refunds.
internal static class PunishaarModule
{
    private const int Failed = 1;
    private const int AreaFight = 1, AreaBag = 2;
    private const int NodeShop = 1, NodeEvent = 2, NodeFight = 3, NodeChoiceFight = 4, NodeStory = 5;
    private const int StatusWaitSelect = 1, StatusProcessing = 2, StatusRemedy = 3, StatusRewardReplace = 4,
        StatusFinished = 5, StatusExited = 6, StatusWaitSelectShop = 7;
    private const int RewardMasterCard = 1, RewardSubCard = 2, RewardGold = 3, RewardMaxHp = 4,
        RewardFightGrid = 5, RewardBagGrid = 6;
    private const int SettleFinished = 1, SettleDurabilityEnd = 2, SettleQuit = 3;
    private const int StageEndless = 2;

    private static readonly Lazy<List<PunishaarActivityTable>> Activities = new(() => TableReaderV2.Parse<PunishaarActivityTable>());
    private static readonly Lazy<Dictionary<int, PunishaarStageGroupTable>> StageGroups = new(() =>
        TableReaderV2.Parse<PunishaarStageGroupTable>().ToDictionary(x => x.StageId));
    private static readonly Lazy<Dictionary<int, List<int>>> StageContentIds = new(() =>
        TableReaderV2.Parse<PunishaarStageContentGroupTable>().ToDictionary(x => x.StageId, x => x.StageContentIds.Where(id => id > 0).ToList()));
    private static readonly Lazy<Dictionary<int, PunishaarStageContentTable>> Contents = new(() =>
        TableReaderV2.Parse<PunishaarStageContentTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, PunishaarCardTable>> Cards = new(() =>
        TableReaderV2.Parse<PunishaarCardTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<HashSet<(int, int)>> CardLevels = new(() =>
        TableReaderV2.Parse<PunishaarCardLevelTable>().Select(x => (x.CardId, x.Level)).ToHashSet());
    private static readonly Lazy<Dictionary<int, PunishaarCardSaleTable>> Sales = new(() =>
        TableReaderV2.Parse<PunishaarCardSaleTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<List<PunishaarFightTable>> Fights = new(() => TableReaderV2.Parse<PunishaarFightTable>());
    private static readonly Lazy<List<PunishaarEventGroupTable>> EventGroups = new(() => TableReaderV2.Parse<PunishaarEventGroupTable>());
    private static readonly Lazy<Dictionary<int, PunishaarEventRewardTable>> EventRewards = new(() =>
        TableReaderV2.Parse<PunishaarEventRewardTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<string, List<int>>> Config = new(() =>
        TableReaderV2.Parse<PunishaarConfigTable>().ToDictionary(x => x.Key,
            x => x.Param));
    private static readonly Lazy<ShopPolicy> Shops = new(BuildShopPolicy);

    private static int Cfg(string key, int index = 0) => Config.Value[key][index];
    private static bool IsMasterType(int type) => Config.Value["MasterCardTypeList"].Contains(type);
    private static bool IsSubType(int type) => Config.Value["SubCardTypeList"].Contains(type);

    // MasterCardSlotTypeLimit = [masterType, subType, ...] (authored 1,3,2,4 = Character<-Awareness, Weapon<-Resonance).
    private static int HostTypeFor(int subType)
    {
        List<int> pairs = Config.Value["MasterCardSlotTypeLimit"];
        for (int i = 0; i + 1 < pairs.Count; i += 2)
            if (pairs[i + 1] == subType) return pairs[i];
        return 0;
    }

    #region Shop policy

    internal sealed record ShopPolicy(
        Dictionary<int, List<int>> PoolByShopGroup, Dictionary<int, int> SupplyByRemedyGroup, Dictionary<int, List<int>> StockByShop);

    private static ShopPolicy BuildShopPolicy()
    {
        List<PunishaarShopTable> shops = TableReaderV2.Parse<PunishaarShopTable>().OrderBy(x => x.Id).ToList();
        List<(List<PunishaarShopTable> Regular, PunishaarShopTable Supply)> blocks = [];
        List<PunishaarShopTable> current = [];
        foreach (PunishaarShopTable shop in shops)
        {
            if (shop.Name.StartsWith("Supply", StringComparison.Ordinal))
            {
                blocks.Add((current, shop));
                current = [];
            }
            else current.Add(shop);
        }
        if (current.Count > 0) throw new InvalidOperationException("Punishaar shop rows after the last Supply Shop have no block.");
        List<PunishaarStageGroupTable> stages = StageGroups.Value.Values.OrderBy(x => x.StageId).ToList();
        List<int> remedyGroups = stages.Select(x => x.RemedyShop).Distinct().ToList();
        if (remedyGroups.Count != blocks.Count)
            throw new InvalidOperationException($"Punishaar {remedyGroups.Count} remedy groups do not match {blocks.Count} shop blocks.");

        Dictionary<int, List<int>> poolByGroup = [];
        Dictionary<int, int> supplyByRemedy = [];
        Dictionary<int, List<int>> stock = [];
        // Sub cards (Awareness/Resonance) have no PunishaarCardLevel rows - they are passive and never
        // levelled - so only master cards must own a level-1 row to be stockable. Requiring it for subs
        // would empty every "Support Card Shop" pool and abort the whole mode at first use.
        List<PunishaarCardTable> eligible = Cards.Value.Values
            .Where(c => c.IsShow != 0 && (IsSubType(c.Type) || CardLevels.Value.Contains((c.Id, 1))))
            .OrderBy(c => c.Id).ToList();
        for (int b = 0; b < blocks.Count; b++)
        {
            (List<PunishaarShopTable> regular, PunishaarShopTable supply) = blocks[b];
            HashSet<int> colors = [];
            foreach (PunishaarShopTable shop in regular)
                colors.UnionWith(OrbColor(shop.Name) is int color ? [color] : shop.Name.StartsWith("Mystery", StringComparison.Ordinal)
                    ? eligible.Select(c => c.Color) : []);
            foreach (PunishaarShopTable shop in regular.Append(supply))
            {
                Func<PunishaarCardTable, bool> filter = OrbColor(shop.Name) is int color ? c => c.Color == color
                    : shop.Name.StartsWith("Battle Card", StringComparison.Ordinal) ? c => IsMasterType(c.Type) && colors.Contains(c.Color)
                    : shop.Name.StartsWith("Support Card", StringComparison.Ordinal) ? c => IsSubType(c.Type) && colors.Contains(c.Color)
                    : shop.Name.StartsWith("Mystery", StringComparison.Ordinal) ? _ => true
                    : shop.Name.StartsWith("Supply", StringComparison.Ordinal) ? c => colors.Contains(c.Color)
                    : throw new InvalidOperationException($"Punishaar shop {shop.Id} '{shop.Name}' has no AscNet stock rule.");
                List<int> ids = eligible.Where(filter).Select(c => c.Id).ToList();
                if (ids.Count == 0) throw new InvalidOperationException($"Punishaar shop {shop.Id} has an empty stock pool.");
                stock[shop.Id] = ids;
            }
            int remedy = remedyGroups[b];
            supplyByRemedy[remedy] = supply.Id;
            foreach (PunishaarStageGroupTable stage in stages.Where(s => s.RemedyShop == remedy))
                foreach (int contentId in StageContentIds.Value.GetValueOrDefault(stage.StageId) ?? [])
                    if (Contents.Value.TryGetValue(contentId, out PunishaarStageContentTable? content) && content.ContentType == NodeShop)
                        poolByGroup[content.ShopGroupId] = regular.Select(x => x.Id).ToList();
        }
        return new(poolByGroup, supplyByRemedy, stock);
    }

    private static int? OrbColor(string name) =>
        name.StartsWith("Red Orb", StringComparison.Ordinal) ? 1
        : name.StartsWith("Yellow Orb", StringComparison.Ordinal) ? 2
        : name.StartsWith("Blue Orb", StringComparison.Ordinal) ? 3 : null;

    internal static ShopPolicy GetShopPolicy() => Shops.Value;

    #endregion

    #region Context, persistence, rng

    private sealed class Ctx(Session session, PlayerPunishaarState state, PunishaarActivityTable activity, DateTimeOffset now)
    {
        public Session Session { get; } = session;
        public PlayerPunishaarState State { get; } = state;
        public PunishaarActivityTable Activity { get; } = activity;
        public DateTimeOffset Now { get; } = now;
        public List<Action<Session>> Pushes { get; } = [];
        public bool Dirty { get; set; }
        public bool SuppressPushes { get; set; }
        public void Push<T>(T push) where T : new()
        {
            if (!SuppressPushes) Pushes.Add(s => s.SendPush(push));
        }
    }

    private static ServerCodeException Err(int code) => new($"Punishaar request rejected with {code}", code);

    // Every wall-clock read in the mode goes through this seam: the activity window, the stage TimeIds and
    // the task projection are all authored schedule rows, so an operator or a harness can freeze the mode
    // into its window instead of depending on the machine date.
    internal static Func<DateTimeOffset> Clock = static () => DateTimeOffset.UtcNow;

    private static PunishaarActivityTable? Active(DateTimeOffset now) =>
        Activities.Value.Where(a => a.TimeId > 0 && ActivityScheduleService.IsOpen(a.TimeId, now))
            .OrderByDescending(a => a.Id).FirstOrDefault();

    private static PlayerPunishaarState Clone(PlayerPunishaarState state) =>
        BsonSerializer.Deserialize<PlayerPunishaarState>(state.ToBson());

    // The durable document holds the image of the staged graph, so a read-back either shows the staged
    // mutation or the state the request started from. BSON promises neither field order nor dictionary
    // entry order across that round trip (a remove/re-add reorders an ArrayOfDocuments dictionary), so the
    // two images are compared order-insensitively: map fields and {k, v} dictionary entries are sorted,
    // while lists the mode treats as ordered (ids, goods, positions, history) keep their order.
    private static bool SameState(PlayerPunishaarState left, PlayerPunishaarState right) =>
        Canonical(left).Equals(Canonical(right));

    private static BsonValue Canonical(PlayerPunishaarState state) =>
        Canonical(state.ToBsonDocument());

    private static BsonValue Canonical(BsonValue value)
    {
        if (value is BsonDocument document)
            return new BsonDocument(document.OrderBy(field => field.Name, StringComparer.Ordinal)
                .Select(field => new BsonElement(field.Name, Canonical(field.Value))));
        if (value is not BsonArray array) return value;
        IEnumerable<BsonValue> items = array.Select(Canonical);
        if (array.All(item => item is BsonDocument entry && entry.ElementCount == 2 && entry.Contains("k") && entry.Contains("v")))
            items = items.OrderBy(item => item["k"]);
        return new BsonArray(items);
    }

    // Transport resend suppression - this cache never stands in for a failed save. Packet ids are
    // monotonic per connection (verified against retail captures), so the only duplicate that can
    // legitimately arrive is a retry of the newest request: that one gets the frozen response bytes and its
    // absolute pushes back, because no later Punishaar action has happened and those snapshots are still
    // current. Every older id is refused instead of replayed or re-executed: the mode answers with absolute
    // snapshots (stage/node/gold/card), so re-emitting a retired one would rewind the client over later
    // committed actions, and re-running the action would pay a second time. One slot is the whole window -
    // an id at or below it that is not that exact attempt is retired by definition.
    // Failures are not cached: every guard is a deterministic read of the durable state, so a retry of the
    // newest id re-derives the same Code (an older id is refused before it reaches a guard).
    // Not covered (protocol limit): a re-pressed action that arrives with a NEW packet id after a lost ack
    // cannot be told apart from a fresh intent - ExitNode carries no node id, so "enter shop, leave
    // without buying" and "retry the exit I already committed" are the same bytes. The state guards below
    // keep such a retry from double-paying (a repeated shop/gold/durability effect is rejected), and
    // login/GetData/EnterStage resync the client from the durable state.
    private sealed record Receipt(int Id, string RequestName, string ResponseName, byte[] Request, byte[] Response,
        List<Action<Session>> Pushes);

    // Receipts are transport-local: a new connection restarts the id space, so they live with the session.
    private sealed class ReceiptLog { public Receipt? Newest; }

    private static readonly ConditionalWeakTable<Session, ReceiptLog> Receipts = new();

    // Runs body on a staged copy; the copy replaces the live state only once it is durable.
    private static void Handle<TResp>(Session session, Packet.Request packet, Func<Ctx, TResp> body, Func<int, TResp> fail,
        Action<Session>? after = null) where TResp : new()
    {
        ReceiptLog log = Receipts.GetOrCreateValue(session);
        if (log.Newest is { } newest && packet.Id <= newest.Id)
        {
            if (packet.Id == newest.Id && newest.RequestName == packet.Name && newest.ResponseName == typeof(TResp).Name
                && newest.Request.AsSpan().SequenceEqual(packet.Content ?? []))
            {
                foreach (Action<Session> push in newest.Pushes) push(session);
                session.SendResponse(newest.ResponseName, newest.Response, packet.Id);
                return;
            }
            session.log.Warn($"Punishaar refused packet id {packet.Id} ({packet.Name}); "
                + $"it is not the newest transport attempt (id {newest.Id}, {newest.RequestName}).");
            session.SendResponse(fail(Failed), packet.Id);
            return;
        }
        DateTimeOffset now = Clock();
        if (Active(now) is not { } activity)
        {
            session.SendResponse(fail(20430001), packet.Id);
            return;
        }
        PlayerPunishaarState live = session.player.Punishaar;
        PlayerPunishaarState staged = Clone(live);
        if (staged.ActivityId != activity.Id)
        {
            staged = new PlayerPunishaarState { ActivityId = activity.Id };
        }
        Ctx ctx = new(session, staged, activity, now) { Dirty = live.ActivityId != activity.Id };
        TResp response;
        try
        {
            response = body(ctx);
        }
        catch (ServerCodeException exception)
        {
            session.SendResponse(fail(exception.Code), packet.Id);
            return;
        }
        catch (Exception exception)
        {
            // The staged copy is dropped, so the live state still matches the durable one: the client can
            // resync from it. Answer anyway, or the caller waits out its request lock for nothing.
            session.log.Error($"Punishaar request {packet.Name} could not be evaluated: {exception}");
            session.SendResponse(fail(Failed), packet.Id);
            return;
        }
        if (ctx.Dirty)
        {
            session.player.Punishaar = staged;
            try
            {
                session.player.SaveChecked();
            }
            catch (Exception exception)
            {
                // The outcome of the write is unknown: the replacement may have landed before the
                // acknowledgement was lost. Falling back to the pre-request copy would let a later
                // unrelated save write it over a committed purchase, so the durable document decides.
                if (Player.TryFromPlayerId(session.player.PlayerData.Id) is not { } stored)
                {
                    // Storage cannot even say which state is durable, so the in-memory copy may now
                    // disagree with it: close the transport without writing that copy anywhere.
                    session.log.Error($"Punishaar save failed and the durable state could not be read; "
                        + $"closing the session: {exception}");
                    session.DisconnectProtocol(persistState: false);
                    return;
                }
                bool committed = SameState(stored.Punishaar, staged);
                session.player.Punishaar = stored.Punishaar;
                if (!committed)
                {
                    session.log.Error($"Punishaar save failed; the durable state was adopted: {exception}");
                    session.SendResponse(fail(Failed), packet.Id);
                    return;
                }
                session.log.Warn($"Punishaar save acknowledgement was lost; "
                    + $"the committed state was adopted: {exception}");
            }
        }
        byte[] responseBytes = MessagePackSerializer.Serialize(response);
        foreach (Action<Session> push in ctx.Pushes) push(session);
        session.SendResponse(typeof(TResp).Name, responseBytes, packet.Id);
        log.Newest = new Receipt(packet.Id, packet.Name, typeof(TResp).Name, packet.Content ?? [], responseBytes, ctx.Pushes);
        after?.Invoke(session);
    }

    private static int Next(PunishaarStage stage, int max)
    {
        unchecked
        {
            ulong z = (ulong)stage.RngState + 0x9E3779B97F4A7C15UL;
            stage.RngState = (long)z;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (int)(z % (ulong)max);
        }
    }

    private static List<T> PickDistinct<T>(PunishaarStage stage, IEnumerable<T> source, Func<T, int> weight, int count)
    {
        List<T> pool = source.Where(x => weight(x) > 0).ToList();
        List<T> picked = [];
        while (picked.Count < count && pool.Count > 0)
        {
            int roll = Next(stage, pool.Sum(weight));
            int index = 0;
            while (roll >= weight(pool[index])) roll -= weight(pool[index++]);
            picked.Add(pool[index]);
            pool.RemoveAt(index);
        }
        return picked;
    }

    #endregion

    #region Projection

    internal static NotifyPunishaarLoginData BuildLoginData(Player player, DateTimeOffset now)
    {
        if (Active(now) is not { } activity) return new NotifyPunishaarLoginData();
        PlayerPunishaarState state = player.Punishaar.ActivityId == activity.Id ? player.Punishaar : new();
        return new NotifyPunishaarLoginData
        {
            ActivityId = activity.Id,
            SaveStageIds = state.StageSaves.Keys.OrderBy(x => x).ToList(),
            PassStageIds = state.PassedStageIds.ToList(),
            CharacterCardCatalogsDict = new(state.CharacterCatalogs),
            PartnerCardCatalogsDict = new(state.PartnerCatalogs),
            EquipCatalogs = state.EquipCatalogs.ToList(),
            ResonanceCatalogs = state.ResonanceCatalogs.ToList()
        };
    }

    internal static void SendLoginData(Session session) => session.SendPush(BuildLoginData(session.player, Clock()));

    private static PunishaarDataDb BuildDataDb(Ctx c) => new()
    {
        ActivityId = c.Activity.Id,
        CurrentStageId = c.State.CurrentStageId,
        StageSaves = new(c.State.StageSaves),
        PassedStageIds = c.State.PassedStageIds.ToList(),
        StageChallengeCounts = new(c.State.StageChallengeCounts),
        CharacterCardCatalogsDict = new(c.State.CharacterCatalogs),
        PartnerCardCatalogsDict = new(c.State.PartnerCatalogs),
        EquipCatalogs = c.State.EquipCatalogs.ToList(),
        ResonanceCatalogs = c.State.ResonanceCatalogs.ToList()
    };

    #endregion

    #region Stage lifecycle

    private static PunishaarStageGroupTable OpenStage(Ctx c, int stageId)
    {
        if (!StageGroups.Value.TryGetValue(stageId, out PunishaarStageGroupTable? group) || group.GroupId != c.Activity.StageGroup
            || !StageContentIds.Value.ContainsKey(stageId))
            throw Err(20430002);
        if (group.TimeId <= 0 || !ActivityScheduleService.IsOpen(group.TimeId, c.Now)) throw Err(20430027);
        return group;
    }

    private static PunishaarStage CurrentSave(Ctx c)
    {
        if (c.State.CurrentStageId == 0) throw Err(20430049);
        if (!c.State.StageSaves.TryGetValue(c.State.CurrentStageId, out PunishaarStage? stage)) throw Err(20430069);
        OpenStage(c, stage.StageId);
        c.Dirty = true;
        return stage;
    }

    [RequestPacketHandler("XPunishaarGetDataRequest")]
    public static void GetData(Session session, Packet.Request packet) =>
        Handle(session, packet, c => new XPunishaarGetDataResponse { DataDb = BuildDataDb(c) }, code => new XPunishaarGetDataResponse { Code = code });

    [RequestPacketHandler("XPunishaarStartStageRequest")]
    public static void StartStage(Session session, Packet.Request packet)
    {
        XPunishaarStartStageRequest request = packet.Deserialize<XPunishaarStartStageRequest>();
        Handle(session, packet, c =>
        {
            PunishaarStageGroupTable group = OpenStage(c, request.StageId);
            if (group.PreStageId > 0 && !c.State.PassedStageIds.Contains(group.PreStageId)) throw Err(20430012);
            // A new save takes a slot; replacing this stage's own save is the client's Retry path
            // (XUiPunishaarExploreDetail shows Retry only for a cleared stage, Start only without a save).
            // Every start records itself: the challenge count below and the save replace are the only
            // durable effects, so an abandoned run can never be resumed for free.
            if (!c.State.StageSaves.ContainsKey(group.StageId) && c.State.StageSaves.Count >= Cfg("MaxSaveCount")) throw Err(20430025);
            PunishaarStage stage = new()
            {
                StageId = group.StageId,
                Gold = Cfg("InitialGoldCount"),
                Durability = Cfg("Durability"),
                CurrentRound = 1,
                FightAreaGridLimit = Cfg("InitialBattleCardAreaCount"),
                BagGridLimit = Cfg("InitialBagCardAreaCount"),
                RngState = Random.Shared.NextInt64()
            };
            // The response carries the absolute stage; the client has no stage to apply notifies to yet.
            c.SuppressPushes = true;
            EnterNode(c, stage, StageContentIds.Value[group.StageId][0]);
            c.State.StageSaves[group.StageId] = stage;
            c.State.StageChallengeCounts[group.StageId] = c.State.StageChallengeCounts.GetValueOrDefault(group.StageId) + 1;
            c.State.CurrentStageId = group.StageId;
            c.Dirty = true;
            return new XPunishaarStartStageResponse { Stage = stage };
        }, code => new XPunishaarStartStageResponse { Code = code });
    }

    [RequestPacketHandler("XPunishaarEnterStageRequest")]
    public static void EnterStage(Session session, Packet.Request packet)
    {
        XPunishaarEnterStageRequest request = packet.Deserialize<XPunishaarEnterStageRequest>();
        Handle(session, packet, c =>
        {
            if (!c.State.StageSaves.TryGetValue(request.StageId, out PunishaarStage? stage)) throw Err(20430004);
            OpenStage(c, request.StageId);
            c.State.CurrentStageId = request.StageId;
            c.Dirty = true;
            return new XPunishaarEnterStageResponse { Stage = stage };
        }, code => new XPunishaarEnterStageResponse { Code = code });
    }

    [RequestPacketHandler("XPunishaarAwayStageRequest")]
    public static void AwayStage(Session session, Packet.Request packet)
    {
        XPunishaarAwayStageRequest request = packet.Deserialize<XPunishaarAwayStageRequest>();
        Handle(session, packet, c =>
        {
            if (c.State.CurrentStageId == 0) throw Err(20430049);
            if (request.StageId != c.State.CurrentStageId || !c.State.StageSaves.ContainsKey(request.StageId)) throw Err(20430069);
            c.State.CurrentStageId = 0;
            c.Dirty = true;
            return new XPunishaarAwayStageResponse();
        }, code => new XPunishaarAwayStageResponse { Code = code });
    }

    [RequestPacketHandler("XPunishaarQuitStageRequest")]
    public static void QuitStage(Session session, Packet.Request packet) =>
        Handle(session, packet, c => new XPunishaarQuitStageResponse { SettleInfo = Settle(c, CurrentSave(c), SettleQuit) },
            code => new XPunishaarQuitStageResponse { Code = code }, TaskModule.SendPunishaarTaskSync);

    private static PunishaarSettleInfo Settle(Ctx c, PunishaarStage stage, int settleType)
    {
        bool newRecord = false;
        if (StageGroups.Value[stage.StageId].Type == StageEndless)
        {
            newRecord = stage.CurrentRound > c.State.BestRounds.GetValueOrDefault(stage.StageId);
            if (newRecord) c.State.BestRounds[stage.StageId] = stage.CurrentRound;
        }
        if (settleType == SettleFinished && !c.State.PassedStageIds.Contains(stage.StageId))
            c.State.PassedStageIds.Add(stage.StageId);
        c.State.StageSaves.Remove(stage.StageId);
        if (c.State.CurrentStageId == stage.StageId) c.State.CurrentStageId = 0;
        c.Dirty = true;
        return new PunishaarSettleInfo
        {
            SettleType = settleType,
            StageId = stage.StageId,
            Durability = stage.Durability,
            AllGold = stage.AllGold,
            FightWinCount = stage.FightWinCount,
            CurrentRound = stage.CurrentRound,
            IsNewRecord = newRecord
        };
    }

    #endregion

    #region Nodes

    private static void AddGold(Ctx c, PunishaarStage stage, int amount, List<PunishaarRewardGoods>? rewards)
    {
        if (amount <= 0) return;
        stage.Gold = checked(stage.Gold + amount);
        stage.AllGold = checked(stage.AllGold + amount);
        c.Push(new NotifyPunishaarGoldChange { Gold = stage.Gold });
        rewards?.Add(new PunishaarRewardGoods { RewardType = RewardGold, Amount = amount });
    }

    private static void EnterNode(Ctx c, PunishaarStage stage, int contentId)
    {
        if (!Contents.Value.TryGetValue(contentId, out PunishaarStageContentTable? content)) throw Err(20430009);
        PunishaarNode node = new() { NodeId = content.Id, Type = content.ContentType };
        switch (content.ContentType)
        {
            case NodeShop:
                if (!Shops.Value.PoolByShopGroup.TryGetValue(content.ShopGroupId, out List<int>? pool)) throw Err(20430030);
                List<int> candidates = PickDistinct(stage, pool, _ => 1, Cfg("ShopRandomAmount"));
                node.ShopInfo = new PunishaarShopInfo { CandidateShopIds = candidates };
                if (candidates.Count > 1) node.Status = StatusWaitSelectShop;
                else
                {
                    node.Status = StatusProcessing;
                    node.ShopInfo.SelectedShopId = candidates[0];
                    FillGoods(stage, node.ShopInfo);
                }
                break;
            case NodeEvent:
                List<int> events = PickDistinct(stage, EventGroups.Value.Where(e => e.GroupId == content.EventGroupId), e => e.Weight,
                    content.EventRandomAmount).Select(e => e.Id).ToList();
                if (events.Count == 0) throw Err(20430030);
                node.EventInfo = new PunishaarEventInfo { RandomEventIds = events };
                node.Status = StatusWaitSelect;
                break;
            case NodeFight:
            case NodeChoiceFight:
                List<int> fights = PickDistinct(stage, Fights.Value.Where(f => f.GroupId == content.FightGroupId), f => f.Weight,
                    content.ContentType == NodeFight ? 1 : Cfg("FightRandomAmount")).Select(f => f.Id).ToList();
                if (fights.Count == 0) throw Err(20430030);
                node.FightInfo = new PunishaarFightInfo { RandomFightIds = fights };
                if (content.ContentType == NodeFight)
                {
                    node.FightInfo.SelectedFightId = fights[0];
                    node.Status = StatusProcessing;
                }
                else node.Status = StatusWaitSelect;
                break;
            case NodeStory:
                node.Status = StatusProcessing;
                break;
            default:
                throw Err(20430008);
        }
        stage.CurrentNode = node;
        AddGold(c, stage, content.EnterGold, null);
    }

    // Keeps frozen goods (in order) and fills the remaining shop area with uniform level-1 stock.
    private static void FillGoods(PunishaarStage stage, PunishaarShopInfo shop)
    {
        List<int> stock = Shops.Value.StockByShop[shop.SelectedShopId];
        shop.Goods = shop.Goods.Where(g => g.Frozen && !g.IsBought).ToList();
        int remaining = Cfg("MaxShopCardAreaCount") - shop.Goods.Sum(g => Cards.Value[g.CardId].Size);
        while (true)
        {
            List<int> fits = stock.Where(id => Cards.Value[id].Size <= remaining).ToList();
            if (fits.Count == 0) break;
            int cardId = fits[Next(stage, fits.Count)];
            shop.Goods.Add(new PunishaarGoods { CardId = cardId, Level = 1 });
            remaining -= Cards.Value[cardId].Size;
        }
    }

    private static PunishaarShopInfo ActiveShop(PunishaarNode node, int code)
    {
        bool shop = node.Type == NodeShop && node.Status == StatusProcessing;
        bool remedy = node.Type is NodeFight or NodeChoiceFight && node.Status == StatusRemedy;
        if ((!shop && !remedy) || node.ShopInfo is not { SelectedShopId: > 0 } info) throw Err(code);
        return info;
    }

    [RequestPacketHandler("XPunishaarSelectShopRequest")]
    public static void SelectShop(Session session, Packet.Request packet)
    {
        XPunishaarSelectShopRequest request = packet.Deserialize<XPunishaarSelectShopRequest>();
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            PunishaarNode node = stage.CurrentNode;
            if (node.Type != NodeShop) throw Err(20430006);
            if (node.Status != StatusWaitSelectShop || node.ShopInfo is null) throw Err(20430054);
            if (!node.ShopInfo.CandidateShopIds.Contains(request.ShopId)) throw Err(20430053);
            node.ShopInfo.SelectedShopId = request.ShopId;
            node.Status = StatusProcessing;
            FillGoods(stage, node.ShopInfo);
            return new XPunishaarSelectShopResponse { Node = node };
        }, code => new XPunishaarSelectShopResponse { Code = code });
    }

    [RequestPacketHandler("XPunishaarRefreshShopRequest")]
    public static void RefreshShop(Session session, Packet.Request packet) =>
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            PunishaarShopInfo shop = ActiveShop(stage.CurrentNode, 20430055);
            if (shop.Goods.Count > 0 && shop.Goods.All(g => g.Frozen)) throw Err(20430057);
            int cost = Cfg("ShopRefresh", 0) + Cfg("ShopRefresh", 1) * shop.RefreshTimes;
            if (stage.Gold < cost) throw Err(20430056);
            stage.Gold -= cost;
            shop.RefreshTimes++;
            FillGoods(stage, shop);
            c.Push(new NotifyPunishaarGoldChange { Gold = stage.Gold });
            return new XPunishaarRefreshShopResponse { Node = stage.CurrentNode };
        }, code => new XPunishaarRefreshShopResponse { Code = code });

    [RequestPacketHandler("XPunishaarFreezeGoodsRequest")]
    public static void FreezeGoods(Session session, Packet.Request packet)
    {
        XPunishaarFreezeGoodsRequest request = packet.Deserialize<XPunishaarFreezeGoodsRequest>();
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            PunishaarShopInfo shop = ActiveShop(stage.CurrentNode, 20430055);
            if (request.Index < 1 || request.Index > shop.Goods.Count) throw Err(20430058);
            PunishaarGoods goods = shop.Goods[request.Index - 1];
            if (goods.IsBought) throw Err(20430066);
            goods.Frozen = request.IsFreeze;
            return new XPunishaarFreezeGoodsResponse { Node = stage.CurrentNode };
        }, code => new XPunishaarFreezeGoodsResponse { Code = code });
    }

    [RequestPacketHandler("XPunishaarBuyGoodsRequest")]
    public static void BuyGoods(Session session, Packet.Request packet)
    {
        XPunishaarBuyGoodsRequest request = packet.Deserialize<XPunishaarBuyGoodsRequest>();
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            PunishaarShopInfo shop = ActiveShop(stage.CurrentNode, 20430055);
            if (request.Index < 1 || request.Index > shop.Goods.Count) throw Err(20430058);
            PunishaarGoods goods = shop.Goods[request.Index - 1];
            if (goods.IsBought) throw Err(20430066);
            if (!Cards.Value.TryGetValue(goods.CardId, out PunishaarCardTable? card)) throw Err(20430042);
            if (!Sales.Value.TryGetValue(card.Type * 100 + card.Size * 10 + goods.Level, out PunishaarCardSaleTable? sale)) throw Err(20430061);
            if (stage.Gold < sale.Buy) throw Err(20430059);
            stage.Gold -= sale.Buy;
            goods.IsBought = true;
            goods.Frozen = false;
            c.Push(new NotifyPunishaarGoldChange { Gold = stage.Gold });
            Acquire(c, stage, goods.CardId, goods.Level, goods.SubCardId, request.CardDetail, chain: true);
            return new XPunishaarBuyGoodsResponse { Node = stage.CurrentNode };
        }, code => new XPunishaarBuyGoodsResponse { Code = code });
    }

    [RequestPacketHandler("XPunishaarSellCardRequest")]
    public static void SellCard(Session session, Packet.Request packet)
    {
        XPunishaarSellCardRequest request = packet.Deserialize<XPunishaarSellCardRequest>();
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            ActiveShop(stage.CurrentNode, 20430062);
            if (!stage.TotalMasterCards.TryGetValue(request.MasterCardId, out PunishaarMasterCard? owned)) throw Err(20430063);
            int price = SellPrice(owned);
            stage.TotalMasterCards.Remove(owned.Id);
            stage.Gold = checked(stage.Gold + price);
            c.Push(new NotifyPunishaarGoldChange { Gold = stage.Gold });
            c.Push(new NotifyPunishaarMasterCardChange { RemovedCardIds = [owned.Id] });
            return new XPunishaarSellCardResponse { Node = stage.CurrentNode };
        }, code => new XPunishaarSellCardResponse { Code = code });
    }

    internal static int SellPrice(PunishaarMasterCard owned)
    {
        PunishaarCardTable card = Cards.Value.GetValueOrDefault(owned.TemplateId) ?? throw Err(20430042);
        int total = Sales.Value.GetValueOrDefault(card.Type * 100 + card.Size * 10 + owned.Level)?.Sell ?? throw Err(20430061);
        if (owned.SubCardId != 0)
        {
            PunishaarCardTable sub = Cards.Value.GetValueOrDefault(owned.SubCardId) ?? throw Err(20430042);
            total += Sales.Value.GetValueOrDefault(sub.Type * 100 + sub.Size * 10 + 1)?.Sell ?? throw Err(20430061);
        }
        return total;
    }

    [RequestPacketHandler("XPunishaarDiscardCardRequest")]
    public static void DiscardCard(Session session, Packet.Request packet)
    {
        XPunishaarDiscardCardRequest request = packet.Deserialize<XPunishaarDiscardCardRequest>();
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            if (!stage.TotalMasterCards.TryGetValue(request.MasterCardId, out PunishaarMasterCard? owned)) throw Err(20430033);
            if (request.IsMasterCard)
            {
                stage.TotalMasterCards.Remove(owned.Id);
                c.Push(new NotifyPunishaarMasterCardChange { RemovedCardIds = [owned.Id] });
            }
            else
            {
                if (owned.SubCardId == 0) throw Err(20430048);
                owned.SubCardId = 0;
                c.Push(new NotifyPunishaarSubCardChange { MasterCardId = owned.Id, SubCardId = 0 });
            }
            return new XPunishaarDiscardCardResponse();
        }, code => new XPunishaarDiscardCardResponse { Code = code });
    }

    [RequestPacketHandler("XPunishaarSetCardPosRequest")]
    public static void SetCardPos(Session session, Packet.Request packet)
    {
        XPunishaarSetCardPosRequest request = packet.Deserialize<XPunishaarSetCardPosRequest>();
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            List<PunishaarCardPosInfo> list = request.CardPosList is { Count: > 0 } l ? l : throw Err(20430039);
            ApplyPositions(stage, list, stage.TotalMasterCards.Keys.ToList(), null);
            ValidateLayout(stage);
            return new XPunishaarSetCardPosResponse();
        }, code => new XPunishaarSetCardPosResponse { Code = code });
    }

    // The list must name exactly the expected cards; area is optionally pinned (buy/pending repack of one area).
    private static void ApplyPositions(PunishaarStage stage, List<PunishaarCardPosInfo> list, List<int> expected, int? area)
    {
        if (list.Any(x => x is null)) throw Err(20430040);
        if (list.Select(x => x.Id).Distinct().Count() != list.Count) throw Err(20430035);
        if (list.Count != expected.Count) throw Err(20430034);
        foreach (PunishaarCardPosInfo info in list)
        {
            if (!expected.Contains(info.Id) || !stage.TotalMasterCards.ContainsKey(info.Id)) throw Err(20430033);
            if (info.AreaType is not (AreaFight or AreaBag) || (area is int pinned && info.AreaType != pinned)) throw Err(20430041);
        }
        foreach (PunishaarCardPosInfo info in list)
        {
            stage.TotalMasterCards[info.Id].AreaType = info.AreaType;
            stage.TotalMasterCards[info.Id].StartPos = info.StartPos;
        }
    }

    internal static void ValidateLayout(PunishaarStage stage)
    {
        foreach (int area in new[] { AreaFight, AreaBag })
        {
            int limit = area == AreaFight ? stage.FightAreaGridLimit : stage.BagGridLimit;
            bool[] used = new bool[limit + 1];
            foreach (PunishaarMasterCard owned in stage.TotalMasterCards.Values.Where(x => x.AreaType == area))
            {
                int size = Cards.Value.GetValueOrDefault(owned.TemplateId)?.Size ?? throw Err(20430042);
                if (size <= 0) throw Err(20430043);
                if (owned.StartPos < 1 || owned.StartPos + size - 1 > limit) throw Err(20430037);
                for (int p = owned.StartPos; p < owned.StartPos + size; p++)
                {
                    if (used[p]) throw Err(20430038);
                    used[p] = true;
                }
            }
        }
        if (stage.TotalMasterCards.Values.Any(x => x.AreaType is not (AreaFight or AreaBag))) throw Err(20430041);
    }

    // Shared by BuyGoods (chain merge) and HandlePendingReward (single merge, as the client's pending path).
    private static void Acquire(Ctx c, PunishaarStage stage, int cardId, int level, int goodsSubCardId,
        PunishaarRewardCardDetailInfo? detail, bool chain)
    {
        if (detail is null) throw Err(20430036);
        if (!Cards.Value.TryGetValue(cardId, out PunishaarCardTable? card)) throw Err(20430042);
        // Master cards upgrade through PunishaarCardLevel rows (20430044 = "card level configuration not
        // found"); sub cards have no level rows at all and are only ever obtained at level 1.
        if (IsMasterType(card.Type))
        {
            if (!CardLevels.Value.Contains((cardId, level))) throw Err(20430044);
        }
        else if (level != 1) throw Err(20430044);
        UnlockCatalog(c.State, card, level);
        if (IsMasterType(card.Type))
        {
            if (detail.MasterCardId != 0)
            {
                if (!stage.TotalMasterCards.TryGetValue(detail.MasterCardId, out PunishaarMasterCard? first)
                    || first.TemplateId != cardId || first.Level != level || !CardLevels.Value.Contains((cardId, level + 1)))
                    throw Err(20430045);
                List<PunishaarMasterCard> consumed = [first];
                int finalLevel = level + 1;
                while (chain && CardLevels.Value.Contains((cardId, finalLevel + 1))
                    && stage.TotalMasterCards.Values.Where(x => x.TemplateId == cardId && x.Level == finalLevel && !consumed.Contains(x))
                        .OrderBy(x => x.Id).FirstOrDefault() is { } link)
                {
                    consumed.Add(link);
                    finalLevel++;
                }
                List<int> subs = consumed.Select(x => x.SubCardId).Prepend(goodsSubCardId).Where(x => x != 0).ToList();
                if (detail.SubCardId != 0 && !subs.Contains(detail.SubCardId)) throw Err(20430046);
                PunishaarMasterCard merged = new()
                {
                    Id = ++stage.NextCardId, TemplateId = cardId, Level = finalLevel,
                    AreaType = first.AreaType, StartPos = first.StartPos, SubCardId = detail.SubCardId
                };
                foreach (PunishaarMasterCard used in consumed) stage.TotalMasterCards.Remove(used.Id);
                stage.TotalMasterCards[merged.Id] = merged;
                UnlockCatalog(c.State, card, finalLevel);
                c.Push(new NotifyPunishaarMasterCardChange { AddedCard = merged, RemovedCardIds = consumed.Select(x => x.Id).ToList() });
                return;
            }
            if (detail.AreaType is not (AreaFight or AreaBag)) throw Err(20430041);
            if (detail.IsCardsPosChange)
                ApplyPositions(stage, detail.CardPosList ?? throw Err(20430039),
                    stage.TotalMasterCards.Values.Where(x => x.AreaType == detail.AreaType).Select(x => x.Id).ToList(), detail.AreaType);
            PunishaarMasterCard added = new()
            {
                Id = ++stage.NextCardId, TemplateId = cardId, Level = level,
                AreaType = detail.AreaType, StartPos = detail.StartPos, SubCardId = goodsSubCardId
            };
            stage.TotalMasterCards[added.Id] = added;
            ValidateLayout(stage);
            c.Push(new NotifyPunishaarMasterCardChange { AddedCard = added });
            return;
        }
        if (!IsSubType(card.Type)) throw Err(20430047);
        if (!stage.TotalMasterCards.TryGetValue(detail.MasterCardId, out PunishaarMasterCard? host)) throw Err(20430033);
        if (Cards.Value.GetValueOrDefault(host.TemplateId)?.Type != HostTypeFor(card.Type)) throw Err(20430047);
        host.SubCardId = cardId; // a replaced sub-card is discarded
        c.Push(new NotifyPunishaarSubCardChange { MasterCardId = host.Id, SubCardId = cardId });
    }

    private static void UnlockCatalog(PlayerPunishaarState state, PunishaarCardTable card, int level)
    {
        List<int> set = card.Type switch
        {
            1 => (state.CharacterCatalogs.TryGetValue(level, out PunishaarCatalog? ch) ? ch : state.CharacterCatalogs[level] = new()).Catalogs,
            2 => (state.PartnerCatalogs.TryGetValue(level, out PunishaarCatalog? pa) ? pa : state.PartnerCatalogs[level] = new()).Catalogs,
            3 => state.EquipCatalogs,
            4 => state.ResonanceCatalogs,
            _ => throw Err(20430047)
        };
        if (!set.Contains(card.Id)) set.Add(card.Id);
    }

    [RequestPacketHandler("XPunishaarSelectEventRequest")]
    public static void SelectEvent(Session session, Packet.Request packet)
    {
        XPunishaarSelectEventRequest request = packet.Deserialize<XPunishaarSelectEventRequest>();
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            PunishaarNode node = stage.CurrentNode;
            if (node.Type != NodeEvent || node.EventInfo is null) throw Err(20430006);
            if (node.Status != StatusWaitSelect) throw Err(20430015);
            if (!node.EventInfo.RandomEventIds.Contains(request.EventId)) throw Err(20430014);
            node.EventInfo.SelectedEventId = request.EventId;
            node.Status = StatusProcessing;
            return new XPunishaarSelectEventResponse { Node = node };
        }, code => new XPunishaarSelectEventResponse { Code = code });
    }

    [RequestPacketHandler("XPunishaarFinishEventRequest")]
    public static void FinishEvent(Session session, Packet.Request packet) =>
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            PunishaarNode node = stage.CurrentNode;
            if (node.Type != NodeEvent || node.EventInfo is null) throw Err(20430006);
            if (node.Status == StatusWaitSelect) throw Err(20430017);
            if (node.Status != StatusProcessing) throw Err(20430018);
            PunishaarEventGroupTable row = EventGroups.Value.FirstOrDefault(e => e.Id == node.EventInfo.SelectedEventId) ?? throw Err(20430019);
            PunishaarEventRewardTable reward = EventRewards.Value.GetValueOrDefault(row.EventRewardId) ?? throw Err(20430026);
            List<PunishaarRewardGoods> rewards = [];
            AddGold(c, stage, reward.GoldCount, rewards);
            if (reward.CardId > 0)
            {
                PunishaarCardTable card = Cards.Value.GetValueOrDefault(reward.CardId) ?? throw Err(20430042);
                if (!CardLevels.Value.Contains((card.Id, reward.CardLevel))) throw Err(20430044);
                node.PendingRewardCardId = card.Id;
                node.PendingRewardCardLevel = reward.CardLevel;
                node.Status = StatusRewardReplace;
                rewards.Add(new PunishaarRewardGoods
                {
                    RewardType = IsMasterType(card.Type) ? RewardMasterCard : RewardSubCard,
                    CardId = card.Id, Level = reward.CardLevel, Amount = 1
                });
            }
            else node.Status = StatusFinished;
            c.Push(new NotifyPunishaarRewardResult { StageId = stage.StageId, RewardGoodsList = rewards });
            return new XPunishaarFinishEventResponse { Node = node };
        }, code => new XPunishaarFinishEventResponse { Code = code });

    [RequestPacketHandler("XPunishaarHandlePendingRewardRequest")]
    public static void HandlePendingReward(Session session, Packet.Request packet)
    {
        XPunishaarHandlePendingRewardRequest request = packet.Deserialize<XPunishaarHandlePendingRewardRequest>();
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            PunishaarNode node = stage.CurrentNode;
            if (node.Status != StatusRewardReplace) throw Err(20430031);
            if (node.PendingRewardCardId == 0) throw Err(20430032);
            if (request.IsAccept)
                Acquire(c, stage, node.PendingRewardCardId, node.PendingRewardCardLevel, 0,
                    request.CardDetail ?? throw Err(20430067), chain: false);
            node.PendingRewardCardId = 0;
            node.PendingRewardCardLevel = 0;
            node.Status = StatusFinished;
            return new XPunishaarHandlePendingRewardResponse { Node = node };
        }, code => new XPunishaarHandlePendingRewardResponse { Code = code });
    }

    [RequestPacketHandler("XPunishaarSelectFightRequest")]
    public static void SelectFight(Session session, Packet.Request packet)
    {
        XPunishaarSelectFightRequest request = packet.Deserialize<XPunishaarSelectFightRequest>();
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            PunishaarNode node = stage.CurrentNode;
            if (node.Type is not (NodeFight or NodeChoiceFight) || node.FightInfo is null) throw Err(20430006);
            if (node.Status != StatusWaitSelect) throw Err(20430016);
            if (!node.FightInfo.RandomFightIds.Contains(request.FightId)) throw Err(20430013);
            node.FightInfo.SelectedFightId = request.FightId;
            node.Status = StatusProcessing;
            return new XPunishaarSelectFightResponse { Node = node };
        }, code => new XPunishaarSelectFightResponse { Code = code });
    }

    // Remedy -> Processing (leave remedy shop), or Processing -> fight started (client then runs the battle).
    [RequestPacketHandler("XPunishaarEnterFightRequest")]
    public static void EnterFight(Session session, Packet.Request packet) =>
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            PunishaarNode node = stage.CurrentNode;
            if (node.Type is not (NodeFight or NodeChoiceFight) || node.FightInfo is null) throw Err(20430006);
            if (node.Status == StatusRemedy)
            {
                node.Status = StatusProcessing;
                node.ShopInfo = null;
            }
            else if (node.Status == StatusProcessing && node.FightInfo.SelectedFightId != 0) node.FightStarted = true;
            else throw Err(node.Status == StatusFinished ? 20430021 : 20430020);
            return new XPunishaarEnterFightResponse { Node = node };
        }, code => new XPunishaarEnterFightResponse { Code = code });

    [RequestPacketHandler("XPunishaarFinishFightRequest")]
    public static void FinishFight(Session session, Packet.Request packet)
    {
        XPunishaarFinishFightRequest request = packet.Deserialize<XPunishaarFinishFightRequest>();
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            PunishaarNode node = stage.CurrentNode;
            if (node.Type is not (NodeFight or NodeChoiceFight) || node.FightInfo is null) throw Err(20430006);
            if (node.Status == StatusFinished) throw Err(20430021);
            if (node.Status != StatusProcessing || !node.FightStarted || node.FightInfo.SelectedFightId == 0) throw Err(20430020);
            CheckFightResult(request);
            node.FightStarted = false;
            List<PunishaarRewardGoods> rewards = [];
            if (request.IsWin)
            {
                stage.FightWinCount++;
                stage.WonNodeCounts[node.Type] = stage.WonNodeCounts.GetValueOrDefault(node.Type) + 1;
                int key = stage.StageId * 10 + node.Type;
                c.State.BestWonNodeCounts[key] = Math.Max(c.State.BestWonNodeCounts.GetValueOrDefault(key), stage.WonNodeCounts[node.Type]);
                AddGold(c, stage, Contents.Value[node.NodeId].Gold, rewards);
                rewards.Add(new PunishaarRewardGoods { RewardType = RewardMaxHp, Amount = Cfg("HPGrowthValue") });
                int fightGrow = Math.Min(Cfg("GrowBattleCardAreaCount"), Math.Max(0, Cfg("MaxBattleCardAreaCount") - stage.FightAreaGridLimit));
                int bagGrow = Math.Min(Cfg("GrowBagCardAreaCount"), Math.Max(0, Cfg("MaxBagCardAreaCount") - stage.BagGridLimit));
                stage.FightAreaGridLimit += fightGrow;
                stage.BagGridLimit += bagGrow;
                if (fightGrow > 0) rewards.Add(new PunishaarRewardGoods { RewardType = RewardFightGrid, Amount = fightGrow });
                if (bagGrow > 0) rewards.Add(new PunishaarRewardGoods { RewardType = RewardBagGrid, Amount = bagGrow });
                node.Status = StatusFinished;
                c.Push(new NotifyPunishaarRewardResult { StageId = stage.StageId, RewardGoodsList = rewards });
                return new XPunishaarFinishFightResponse { Stage = stage };
            }
            stage.Durability--;
            if (stage.Durability <= 0)
            {
                stage.Durability = 0;
                return new XPunishaarFinishFightResponse { SettleInfo = Settle(c, stage, SettleDurabilityEnd) };
            }
            PunishaarStageGroupTable group = StageGroups.Value[stage.StageId];
            int supply = Shops.Value.SupplyByRemedyGroup.GetValueOrDefault(group.RemedyShop);
            if (supply == 0) throw Err(20430030);
            node.Status = StatusRemedy;
            node.ShopInfo = new PunishaarShopInfo { CandidateShopIds = [supply], SelectedShopId = supply };
            FillGoods(stage, node.ShopInfo);
            AddGold(c, stage, group.RemedyGold, rewards);
            c.Push(new NotifyPunishaarRewardResult { StageId = stage.StageId, RewardGoodsList = rewards });
            return new XPunishaarFinishFightResponse { Stage = stage };
        }, code => new XPunishaarFinishFightResponse { Code = code }, TaskModule.SendPunishaarTaskSync);
    }

    // Client producer (xpunishaarfightcontrol.lua CheckBattleEnd/FireBattleEnded/CollectBattleStats):
    // Win iff the enemy died (simultaneous death is a win); loss only when the player died alone.
    // LoseMaxSignalBallColor is 0 on win and the most common slot color (0 if the slot is empty) on loss.
    // EndHp/EnemyEndHp are the STE HP values at fire time. This is a consistency check, not battle proof.
    internal static void CheckFightResult(XPunishaarFinishFightRequest request)
    {
        if (request.IsWin)
        {
            if (request.LoseMaxSignalBallColor != 0) throw Err(20430022);
            if (request.EnemyEndHp > 0) throw Err(20430024);
            return;
        }
        if (request.LoseMaxSignalBallColor != 0 && !Cards.Value.Values.Any(card => card.Color == request.LoseMaxSignalBallColor))
            throw Err(20430022);
        if (request.EndHp > 0 || request.EnemyEndHp <= 0) throw Err(20430024);
    }

    [RequestPacketHandler("XPunishaarExitNodeRequest")]
    public static void ExitNode(Session session, Packet.Request packet) =>
        Handle(session, packet, c =>
        {
            PunishaarStage stage = CurrentSave(c);
            PunishaarNode node = stage.CurrentNode;
            bool canExit = node.Type switch
            {
                NodeShop => node.Status == StatusProcessing,
                NodeStory => node.Status is StatusProcessing or StatusFinished,
                NodeFight or NodeChoiceFight => node.Status == StatusFinished ? true : throw Err(20430029),
                _ => node.Status == StatusFinished
            };
            if (!canExit) throw Err(20430023);
            node.Status = StatusExited;
            node.FightStarted = false;
            stage.HistoryNodeList.Add(node);
            List<int> ids = StageContentIds.Value[stage.StageId];
            int index = ids.IndexOf(node.NodeId);
            if (index < 0) throw Err(20430009);
            if (index + 1 < ids.Count)
            {
                EnterNode(c, stage, ids[index + 1]);
                return new XPunishaarExitNodeResponse { Stage = stage };
            }
            if (StageGroups.Value[stage.StageId].Type == StageEndless && stage.CurrentRound < Cfg("EndlessMaxRound"))
            {
                stage.CurrentRound++;
                stage.HistoryNodeList.Clear();
                EnterNode(c, stage, ids[0]);
                return new XPunishaarExitNodeResponse { Stage = stage };
            }
            return new XPunishaarExitNodeResponse { SettleInfo = Settle(c, stage, SettleFinished) };
        }, code => new XPunishaarExitNodeResponse { Code = code }, TaskModule.SendPunishaarTaskSync);

    #endregion
}
