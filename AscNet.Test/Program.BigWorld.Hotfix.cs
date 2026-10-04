using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using MessagePack;
using MongoDB.Bson.Serialization;

namespace AscNet.Test
{
    internal partial class Program
    {
        // --big-world-hotfix-only: BigWorldQuestHotfix runs the client's XDlcQuestHotfixManager + questhotfix Lua server-side.
        private static void ValidateBigWorldHotfix()
        {
            Type hotfix = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BigWorld.BigWorldQuestHotfix");
            Type actors = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BigWorld.BigWorldActors");
            const int Npc = 1;
            void State(Session s, int objective, int state) => hotfix.GetMethod("OnObjectiveState", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [s, objective, state]);
            void SetInteractable(Session s, int placeId, bool enable) => actors.GetMethod("SetInteractable", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session), typeof(int), typeof(int), typeof(bool)])!.Invoke(null, [s, Npc, placeId, enable]);
            void Load(Session s, params int[] placeIds) => actors.GetMethod("LoadLevelNpcs", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [s, 4001, placeIds]);
            bool IsInteractable(Session s, int placeId) => (bool)actors.GetMethod("IsInteractable", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [s, Npc, placeId])!;

            const long playerId = 99_931;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var playerSaves, out _, out _);
            hotfix.GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [null]);

            // Runs `act` on a logged-in player already in world 400; returns the SetInteractable pushes it produced
            // as (enable) in order. Player is reused across relogs by passing the saved one.
            List<bool> Session(Player player, string name, Action<Session> act)
            {
                using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
                    CreateDrawCompatibilityInventory(playerId, []), name);
                BigWorldCoreClient client = new(harness);
                AssertEqual(0, client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest()).Code, "enter");
                while (harness.TryReadAvailablePacket("drain", out _)) { }
                Load(harness.Session, 600010, 500026, 500028); // quest-loaded NPCs must be live for the push to be observable
                while (harness.TryReadAvailablePacket("drain", out _)) { }
                act(harness.Session);
                List<bool> enables = [];
                for (int idle = 0; idle < 3; idle++)
                {
                    while (harness.TryReadAvailablePacket("hotfix push", out Packet p))
                    {
                        idle = 0;
                        if (p.Type != Packet.ContentType.Push) continue;
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(p.Content);
                        if (push.Name != "XRpcComponentAction") continue;
                        object?[] rpc = MessagePackSerializer.Deserialize<object?[]>(push.Content);
                        if ((string)rpc[0]! == "RpcSetInteractableCmpEnableRequest")
                            enables.Add((bool)MessagePackSerializer.Deserialize<object?[]>((byte[])rpc[1]!)[0]!);
                    }
                    Thread.Sleep(30);
                }
                return enables;
            }

            // ---- 251127182 (objective 20010126): Npc 600010 non-interactable -> enabled once at InProgress.
            Player player = CreateDrawCompatibilityPlayer(playerId);
            List<bool> first = Session(player, "hotfix-1", s =>
            {
                SetInteractable(s, 600010, false);
                State(s, 20010126, 1);
                State(s, 20010126, 2);
                AssertEqual(false, IsInteractable(s, 600010), "Enter/ScriptEnter hooks do not touch the NPC");
                State(s, 20010126, 3);
                AssertEqual(true, IsInteractable(s, 600010), "InProgress enables 600010");
            });
            AssertEqual("True", string.Join(",", first.Skip(first.Count - 1)), "SetInteractable(enable) pushed at InProgress");
            AssertEqual("251127182", string.Join(",", player.BigWorldState.ExecutedHotfixScriptIds), "executed id recorded");

            // Repeat in the same session, then after relog: executed once, even though the NPC was disabled again.
            player = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson!);
            AssertEqual("251127182", string.Join(",", player.BigWorldState.ExecutedHotfixScriptIds), "executed id persisted across relog");
            List<bool> relog = Session(player, "hotfix-2", s =>
            {
                SetInteractable(s, 600010, false);
                State(s, 20010126, 3);
                State(s, 20010126, 3);
                AssertEqual(false, IsInteractable(s, 600010), "hotfix not re-executed after relog");
            });
            AssertEqual("False", string.Join(",", relog), "only the test's own disable was pushed after relog");

            // ---- Non-hotfixed objective: nothing.
            List<bool> plain = Session(CreateDrawCompatibilityPlayer(playerId), "hotfix-3", s =>
            {
                SetInteractable(s, 600010, false);
                State(s, 20010125, 3);
                AssertEqual(false, IsInteractable(s, 600010), "non-hotfixed objective leaves 600010");
            });
            AssertEqual("False", string.Join(",", plain), "no hotfix push for a non-hotfixed objective");

            // ---- 251127181 (objective 300501121): both branches.
            // Input A: 500026 off, 500028 on -> enable 500026 and disable 500028.
            Session(CreateDrawCompatibilityPlayer(playerId), "hotfix-4", s =>
            {
                SetInteractable(s, 500026, false);
                SetInteractable(s, 500028, true);
                State(s, 300501121, 3);
                AssertEqual((true, false), (IsInteractable(s, 500026), IsInteractable(s, 500028)), "251127181 both branches fire");
            });
            // Input B: 500026 on, 500028 off -> neither branch fires.
            List<bool> untouched = Session(CreateDrawCompatibilityPlayer(playerId), "hotfix-5", s =>
            {
                SetInteractable(s, 500026, true);
                SetInteractable(s, 500028, false);
                State(s, 300501121, 3);
                AssertEqual((true, false), (IsInteractable(s, 500026), IsInteractable(s, 500028)), "251127181 no branch fires");
            });
            AssertEqual("True,False", string.Join(",", untouched), "251127181 pushes nothing beyond the test's own setup");

            // ---- Missing Lua root: reported cleanly, hotfixes unavailable.
            string missing = Path.Combine(Path.GetTempPath(), "ascnet-no-such-lua-root");
            hotfix.GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [missing]);
            Player noLua = CreateDrawCompatibilityPlayer(playerId);
            Session(noLua, "hotfix-6", s =>
            {
                SetInteractable(s, 600010, false);
                State(s, 20010126, 3);
                AssertEqual(false, IsInteractable(s, 600010), "no Lua root: hotfix does nothing");
            });
            string? reason = (string?)hotfix.GetProperty("UnavailableReason", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            AssertEqual(true, reason != null && reason.Contains("Lua root not found"), "missing root reason reported");
            AssertEqual(0, noLua.BigWorldState.ExecutedHotfixScriptIds.Count, "nothing recorded without Lua");
            hotfix.GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [null]);

            Console.WriteLine("BigWorld hotfix validation passed.");
        }
    }
}
