using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.photomode;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class FashionSuitSetRequest
    {
        public int ClothFashionId { get; set; }
        public int WeaponFashionId { get; set; }
    }

    [MessagePackObject(true)]
    public class FashionSuitSetResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class SwitchRandomBackgroundRequest
    {
        public bool IsRandomBackground { get; set; }
        public int CurRandomBackgroundId { get; set; }
    }

    [MessagePackObject(true)]
    public class SwitchRandomBackgroundResponse
    {
        public int Code { get; set; }
        public RandomBackgroundData? AddRandomBackground { get; set; }
    }

    [MessagePackObject(true)]
    public class SwitchRandomFashionRequest
    {
        public bool IsBackgroundRandomFashion { get; set; }
    }

    [MessagePackObject(true)]
    public class SwitchRandomFashionResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class EditRandomBackGroundFashionRequest
    {
        public RandomBackgroundData? EditRandomBackground { get; set; }
    }

    [MessagePackObject(true)]
    public class EditRandomBackGroundFashionResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class AddRandomBackgroundRequest
    {
        public int BackgroundId { get; set; }
    }

    [MessagePackObject(true)]
    public class AddRandomBackgroundResponse
    {
        public int Code { get; set; }
        public RandomBackgroundData? AddRandomBackground { get; set; }
    }

    [MessagePackObject(true)]
    public class RemoveRandomBackgroundRequest
    {
        public int BackgroundId { get; set; }
    }

    [MessagePackObject(true)]
    public class RemoveRandomBackgroundResponse
    {
        public int Code { get; set; }
    }
#pragma warning restore CS8618
    #endregion

    internal class WardrobePhotoModule
    {
        // CodeText.tsv
        private const int InvalidRequestCode = 20012001;
        private const int CharacterNotFoundCode = 20009001;
        private const int NotOpenCode = 20244001;
        private const int AlreadyOpenCode = 20244002;
        private const int AlreadyCloseCode = 20244003;
        private const int CloseSceneNotExistCode = 20244004;
        private const int CloseSceneLockedCode = 20244005;
        private const int AddNotOpenCode = 20244006;
        private const int AddSceneNotExistCode = 20244007;
        private const int AddSceneLockedCode = 20244008;
        private const int AddAlreadyRandomCode = 20244009;
        private const int AddLimitCode = 20244010;
        private const int RemoveNotOpenCode = 20244011;
        private const int RemoveSceneNotExistCode = 20244012;
        private const int RemoveSceneLockedCode = 20244013;
        private const int RemoveNotRandomCode = 20244014;
        private const int RemoveLastCode = 20244015;

        internal static RandomBackgroundLoginData BuildRandomBackgroundLoginData(Player player)
        {
            RandomBackgroundState state = player.RandomBackground ?? new();
            return new()
            {
                IsRandomBackground = state.IsRandomBackground,
                IsBackgroundRandomFashion = state.IsBackgroundRandomFashion,
                RandomBackgroundPool = state.Pool.Select(ToWire).ToList()
            };
        }

        private static RandomBackgroundData ToWire(RandomBackgroundEntry entry) => new()
        {
            BackgroundId = entry.BackgroundId,
            RandomChars = entry.Chars.Select(c => new RandomBackgroundCharData
            {
                CharId = c.CharId,
                IsRandom = c.IsRandom,
                RandomFashions = [.. c.Fashions]
            }).ToList()
        };

        private static bool SceneExists(int id) => TableReaderV2.Parse<BackgroundTable>().Any(row => row.Id == id);

        private static bool SceneOwned(Session session, int id) => AccountModule.BuildHaveBackgroundIds(session.player).Contains(id);

        /// <summary>Applies <paramref name="mutate"/> to a copy and commits it only if the player save succeeds.</summary>
        private static void Commit(Session session, Action<RandomBackgroundState> mutate)
        {
            RandomBackgroundState previous = session.player.RandomBackground ?? new();
            RandomBackgroundState next = previous.Clone();
            mutate(next);
            session.player.RandomBackground = next;
            try
            {
                session.player.SaveChecked();
            }
            catch
            {
                session.player.RandomBackground = previous;
                throw;
            }
        }

        // Client XUiSceneRandomSetting.OnBtnSaveClick: a scene with no random assistant auto-selects the
        // chief assistant (DisplayCharIdList[1]) wearing its current combat coating.
        private static RandomBackgroundEntry NewEntry(Session session, int backgroundId)
        {
            RandomBackgroundEntry entry = new() { BackgroundId = backgroundId };
            long chief = session.player.PlayerData.DisplayCharIdList.FirstOrDefault();
            CharacterData? character = session.character.Characters.Find(c => c.Id == chief);
            if (character is not null)
                entry.Chars.Add(new() { CharId = (int)character.Id, IsRandom = true, Fashions = [(int)character.FashionId] });
            return entry;
        }

        [RequestPacketHandler("SwitchRandomBackgroundRequest")]
        public static void HandleSwitchRandomBackground(Session session, Packet.Request packet)
        {
            SwitchRandomBackgroundRequest request = packet.Deserialize<SwitchRandomBackgroundRequest>();
            RandomBackgroundState state = session.player.RandomBackground ?? new();
            int id = request.CurRandomBackgroundId;
            RandomBackgroundData? added = null;

            int code = 0;
            if (request.IsRandomBackground)
            {
                if (state.IsRandomBackground) code = AlreadyOpenCode;
                else if (state.Pool.Count == 0)
                {
                    // First activation seeds the pool with the scene currently in use (client: res.AddRandomBackground).
                    if (!SceneExists(id)) code = AddSceneNotExistCode;
                    else if (!SceneOwned(session, id)) code = AddSceneLockedCode;
                }
            }
            else
            {
                if (!state.IsRandomBackground) code = AlreadyCloseCode;
                else if (!SceneExists(id)) code = CloseSceneNotExistCode;
                else if (!SceneOwned(session, id)) code = CloseSceneLockedCode;
            }

            if (code == 0)
            {
                Commit(session, next =>
                {
                    next.IsRandomBackground = request.IsRandomBackground;
                    if (request.IsRandomBackground && next.Pool.Count == 0)
                    {
                        RandomBackgroundEntry entry = NewEntry(session, id);
                        next.Pool.Add(entry);
                        added = ToWire(entry);
                    }
                });
                // Closing: the client adopts the last random scene as the current scene.
                if (!request.IsRandomBackground && session.player.UseBackgroundId != id)
                {
                    int previousBackground = session.player.UseBackgroundId;
                    session.player.UseBackgroundId = id;
                    try { session.player.SaveChecked(); }
                    catch { session.player.UseBackgroundId = previousBackground; throw; }
                }
            }

            session.SendResponse(new SwitchRandomBackgroundResponse { Code = code, AddRandomBackground = added }, packet.Id);
        }

        [RequestPacketHandler("SwitchRandomFashionRequest")]
        public static void HandleSwitchRandomFashion(Session session, Packet.Request packet)
        {
            SwitchRandomFashionRequest request = packet.Deserialize<SwitchRandomFashionRequest>();
            RandomBackgroundState state = session.player.RandomBackground ?? new();

            // Client opens UiSceneRandomSetting only while random scenes are on.
            if (!state.IsRandomBackground)
            {
                session.SendResponse(new SwitchRandomFashionResponse { Code = NotOpenCode }, packet.Id);
                return;
            }

            if (state.IsBackgroundRandomFashion != request.IsBackgroundRandomFashion)
                Commit(session, next => next.IsBackgroundRandomFashion = request.IsBackgroundRandomFashion);
            session.SendResponse(new SwitchRandomFashionResponse(), packet.Id);
        }

        [RequestPacketHandler("AddRandomBackgroundRequest")]
        public static void HandleAddRandomBackground(Session session, Packet.Request packet)
        {
            AddRandomBackgroundRequest request = packet.Deserialize<AddRandomBackgroundRequest>();
            RandomBackgroundState state = session.player.RandomBackground ?? new();
            int id = request.BackgroundId;

            int code = 0;
            if (!state.IsRandomBackground) code = AddNotOpenCode;
            else if (!SceneExists(id)) code = AddSceneNotExistCode;
            else if (!SceneOwned(session, id)) code = AddSceneLockedCode;
            else if (state.Pool.Any(e => e.BackgroundId == id)) code = AddAlreadyRandomCode;
            // Client XPhotographManager.AddRandomBackgroundRequest: Config RandomBackgroundCountLimit.
            else if (state.Pool.Count >= int.Parse(TableReaderV2.Parse<ConfigTable>().Single(r => r.Key == "RandomBackgroundCountLimit").Value))
                code = AddLimitCode;

            RandomBackgroundData? added = null;
            if (code == 0)
            {
                Commit(session, next =>
                {
                    RandomBackgroundEntry entry = NewEntry(session, id);
                    next.Pool.Add(entry);
                    added = ToWire(entry);
                });
            }

            session.SendResponse(new AddRandomBackgroundResponse { Code = code, AddRandomBackground = added }, packet.Id);
        }

        [RequestPacketHandler("RemoveRandomBackgroundRequest")]
        public static void HandleRemoveRandomBackground(Session session, Packet.Request packet)
        {
            RemoveRandomBackgroundRequest request = packet.Deserialize<RemoveRandomBackgroundRequest>();
            RandomBackgroundState state = session.player.RandomBackground ?? new();
            int id = request.BackgroundId;

            int code = 0;
            if (!state.IsRandomBackground) code = RemoveNotOpenCode;
            else if (!SceneExists(id)) code = RemoveSceneNotExistCode;
            else if (!SceneOwned(session, id)) code = RemoveSceneLockedCode;
            else if (!state.Pool.Any(e => e.BackgroundId == id)) code = RemoveNotRandomCode;
            // Client: "RandomBackgroundPoolCannotEmpty" when the pool has a single scene.
            else if (state.Pool.Count == 1) code = RemoveLastCode;

            if (code == 0)
                Commit(session, next => next.Pool.RemoveAll(e => e.BackgroundId == id));

            session.SendResponse(new RemoveRandomBackgroundResponse { Code = code }, packet.Id);
        }

        [RequestPacketHandler("EditRandomBackGroundFashionRequest")]
        public static void HandleEditRandomBackGroundFashion(Session session, Packet.Request packet)
        {
            EditRandomBackGroundFashionRequest request = packet.Deserialize<EditRandomBackGroundFashionRequest>();
            RandomBackgroundState state = session.player.RandomBackground ?? new();
            RandomBackgroundData? edit = request.EditRandomBackground;

            // Client OnBtnSaveClick: only while scene fashion randomisation is on; every submitted assistant
            // carries at least one fashion (empty ones are omitted) and at least one assistant is random.
            // Lua empty tables arrive as nil.
            List<RandomBackgroundCharData> chars = edit?.RandomChars ?? [];
            bool valid = edit is not null
                && state.IsBackgroundRandomFashion
                && state.Pool.Any(e => e.BackgroundId == edit.BackgroundId)
                && chars.Any(c => c.IsRandom)
                && chars.Select(c => c.CharId).Distinct().Count() == chars.Count;

            if (valid)
            {
                List<FashionTable> fashionRows = TableReaderV2.Parse<FashionTable>();
                foreach (RandomBackgroundCharData c in chars)
                {
                    List<int> fashions = c.RandomFashions ?? [];
                    c.RandomFashions = fashions;
                    valid = fashions.Count > 0
                        && fashions.Distinct().Count() == fashions.Count
                        && session.character.Characters.Any(owned => owned.Id == c.CharId)
                        && fashions.All(f => session.character.Fashions.Any(o => o.Id == f && !o.IsLock)
                            && fashionRows.Any(row => row.Id == f && row.CharacterId == c.CharId));
                    if (!valid) break;
                }
            }

            if (!valid)
            {
                session.SendResponse(new EditRandomBackGroundFashionResponse { Code = InvalidRequestCode }, packet.Id);
                return;
            }

            Commit(session, next =>
            {
                RandomBackgroundEntry entry = next.Pool.First(e => e.BackgroundId == edit!.BackgroundId);
                entry.Chars = chars.Select(c => new RandomBackgroundChar
                {
                    CharId = c.CharId,
                    IsRandom = c.IsRandom,
                    Fashions = [.. c.RandomFashions]
                }).ToList();
            });
            session.SendResponse(new EditRandomBackGroundFashionResponse(), packet.Id);
        }

        // Fire-and-forget on the client (XNetwork.Send, no callback); no table defines a share/photo reward,
        // so there is no state to change and nothing to answer.
        [RequestPacketHandler("ShareBackgroundRequest")]
        public static void HandleShareBackground(Session session, Packet.Request packet) { }

        [RequestPacketHandler("PhotoBackgroundRequest")]
        public static void HandlePhotoBackground(Session session, Packet.Request packet) { }

        // Binds a weapon coating to an owned character coating (FashionList.WeaponFashionId, the same
        // pairing FashionSuitPoolSaveRequest stores) without touching the random-pool flag.
        [RequestPacketHandler("FashionSuitSetRequest")]
        public static void HandleFashionSuitSet(Session session, Packet.Request packet)
        {
            FashionSuitSetRequest request = packet.Deserialize<FashionSuitSetRequest>();
            FashionTable? row = request.ClothFashionId > 0
                ? TableReaderV2.Parse<FashionTable>().Find(candidate => candidate.Id == request.ClothFashionId)
                : null;
            FashionList? fashion = row is null ? null
                : session.character.Fashions.Find(candidate => candidate.Id == request.ClothFashionId && !candidate.IsLock);

            if (row is null || fashion is null || !session.character.Characters.Any(c => c.Id == row.CharacterId))
            {
                session.SendResponse(new FashionSuitSetResponse { Code = row is not null && fashion is not null ? CharacterNotFoundCode : InvalidRequestCode }, packet.Id);
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (request.WeaponFashionId < 0 || (request.WeaponFashionId > 0 && !session.character.WeaponFashions.Any(w =>
                    w.Id == request.WeaponFashionId && (w.ExpireTime == 0 || w.ExpireTime > now))))
            {
                session.SendResponse(new FashionSuitSetResponse { Code = InvalidRequestCode }, packet.Id);
                return;
            }

            if (fashion.WeaponFashionId != request.WeaponFashionId)
            {
                fashion.WeaponFashionId = request.WeaponFashionId;
                session.character.Save();
            }

            session.SendPush(new FashionSyncNotify { FashionList = [fashion] });
            session.SendResponse(new FashionSuitSetResponse(), packet.Id);
        }
    }
}
