using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Commands;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;
using Inventory = AscNet.Common.Database.Inventory;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static void ValidateSceneCommandCompatibility()
        {
            using MongoCollectionOverride noOpStages = MongoCollectionOverride.InstallNoOpStageCollection(); // login persists Stage rollover
            List<int> catalogIds = TableReaderV2.Parse<AscNet.Table.V2.share.photomode.BackgroundTable>()
                .Where(background => background.Id > 0 && background.SceneModelId > 0)
                .Select(background => background.Id)
                .Distinct()
                .Order()
                .ToList();
            AssertEqual(17, catalogIds.Count, "current home-scene catalog gap count derives from Background table");

            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> playerCollection,
                out _,
                out _);

            AscNet.GameServer.Commands.CommandFactory.commands.Clear();
            AscNet.GameServer.Commands.CommandFactory.LoadCommands();
            Type sceneCommandType = AscNet.GameServer.Commands.CommandFactory.commands.GetValueOrDefault("scene")
                ?? throw new InvalidDataException("CommandFactory.LoadCommands: expected command 'scene' to be discoverable.");
            AssertEqual("AscNet.GameServer.Commands.SceneCommand", sceneCommandType.FullName, "CommandFactory.LoadCommands command 'scene' type");

            const long playerId = 99_501;
            const int selectedBackgroundId = 14000005;
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            player.UseBackgroundId = selectedBackgroundId;
            player.OwnedBackgroundIds = [];

            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                player,
                CreateDrawCompatibilityInventory(playerId, []),
                "scene-command-compat-test");

            void AssertNoExtraScenePacket(LoopbackSessionHarness h, string name)
            {
                if (h.TryReadAvailablePacket($"{name} unexpected packet", out Packet extra))
                    throw new InvalidDataException($"{name}: unexpected extra {extra.Type} packet.");
            }

            AscNet.GameServer.Commands.Command unlock = AscNet.GameServer.Commands.CommandFactory.CreateCommand(
                "scene", harness.Session, ["unlock", "all"])
                ?? throw new InvalidDataException("SceneCommand: expected CommandFactory to create the command.");
            AssertEqual("Unlock every home scene background with 'all'.", unlock.Help, "SceneCommand Help");

            string? completionMessage = null;
            try { unlock.Execute(); }
            catch (CommandMessageCallbackException ex) { completionMessage = ex.Message; }
            AssertEqual($"Unlocked {catalogIds.Count} scene background(s).", completionMessage, "SceneCommand completion feedback");

            AssertEqual(string.Join(",", catalogIds), string.Join(",", player.OwnedBackgroundIds), "SceneCommand persisted owned background ids");
            AssertEqual(selectedBackgroundId, player.UseBackgroundId, "SceneCommand preserves selected background");

            HashSet<int> pushed = new();
            for (int i = 0; i < catalogIds.Count; i++)
            {
                NotifyAddBackground add = ReadPushPayload<NotifyAddBackground>(
                    harness, nameof(NotifyAddBackground), $"SceneCommand NotifyAddBackground push {i}");
                pushed.Add(add.BackgroundId);
            }
            AssertEqual(string.Join(",", catalogIds), string.Join(",", pushed.Order()), "SceneCommand NotifyAddBackground pushes cover every catalog id");
            AssertNoExtraScenePacket(harness, "scene unlock");

            AssertEqual(1, playerCollection.ReplaceOneCalls, "SceneCommand persists exactly once");

            AscNet.Common.Database.Player relogged = BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
                playerCollection.LastReplacement.ToBson());
            AssertEqual(string.Join(",", catalogIds), string.Join(",", relogged.OwnedBackgroundIds), "SceneCommand owned ids survive BSON relog");

            MethodInfo buildNotifyLogin = RequiredMethod(
                RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
                "BuildNotifyLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);
            NotifyLogin login = buildNotifyLogin.Invoke(null, [harness.Session]) as NotifyLogin
                ?? throw new InvalidDataException("AccountModule.BuildNotifyLogin returned nil.");
            AssertEqual(
                string.Join(",", catalogIds),
                string.Join(",", login.HaveBackgroundIds.Select(id => (int)id).Order()),
                "NotifyLogin HaveBackgroundIds reflect unlocked ownership");

            int savesBeforeRepeat = playerCollection.ReplaceOneCalls;
            string? repeatMessage = null;
            AscNet.GameServer.Commands.Command repeat = AscNet.GameServer.Commands.CommandFactory.CreateCommand(
                "scene", harness.Session, ["unlock", "all"])
                ?? throw new InvalidDataException("SceneCommand repeat: expected CommandFactory to create the command.");
            try { repeat.Execute(); }
            catch (CommandMessageCallbackException ex) { repeatMessage = ex.Message; }
            AssertEqual("All scene backgrounds are already unlocked.", repeatMessage, "SceneCommand repeat no-op feedback");
            AssertEqual(savesBeforeRepeat, playerCollection.ReplaceOneCalls, "SceneCommand repeat does not save");
            AssertNoExtraScenePacket(harness, "scene repeat");

            AscNet.Common.Database.Player failingPlayer = CreateDrawCompatibilityPlayer(playerId + 1);
            failingPlayer.UseBackgroundId = selectedBackgroundId;
            failingPlayer.OwnedBackgroundIds = [catalogIds[0]];
            using LoopbackSessionHarness failingHarness = new(
                CreateDrawCompatibilityCharacter(playerId + 1),
                failingPlayer,
                CreateDrawCompatibilityInventory(playerId + 1, []),
                "scene-command-failure-test");
            playerCollection.ThrowOnReplaceOne = true;
            string? failureMessage = null;
            AscNet.GameServer.Commands.Command failing = AscNet.GameServer.Commands.CommandFactory.CreateCommand(
                "scene", failingHarness.Session, ["unlock", "all"])
                ?? throw new InvalidDataException("SceneCommand failure: expected CommandFactory to create the command.");
            try { failing.Execute(); }
            catch (CommandMessageCallbackException ex) { failureMessage = ex.Message; }
            AssertEqual("Failed to persist scene unlocks.", failureMessage, "SceneCommand persistence failure feedback");
            AssertEqual(
                catalogIds[0].ToString(),
                string.Join(",", failingPlayer.OwnedBackgroundIds),
                "SceneCommand persistence failure rolls ownership back");
        }
    }
}
