using AscNet.Common;
using AscNet.Common.Database;
using KeraLua;
using Logger = AscNet.Logging.Logger;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Runs the client's authoritative XDlcQuestHotfixManager + questhotfix/Hotfix_*.lua server-side in an embedded
    // Lua 5.4 (KeraLua). Retail keeps these 4 hotfixes as the only server Lua of the quest system; bootstrap follows
    // XMain/XFightLuaEngine (CS.* stubs before `require "XMain"`, XLuaEngine Import/FileExists, case-insensitive
    // searcher, IsClient=false). Lua is loaded from the client Lua root (Config.DlcFightLuaRoot or a probed
    // ".runtime/installed-lua/dlcfight"); it is NOT vendored. Missing root => logged once, hotfixes unavailable.
    //
    // Thread safety: ONE Lua state per process; every entry is serialized by _gate (Lua states are not thread-safe).
    // The gate is held while a script calls back into BigWorldActors, which never re-enters this class.
    internal static class BigWorldQuestHotfix
    {
        private static readonly object _gate = new();
        private static Lua? _lua;
        private static bool _initTried;
        private static string? _unavailable;
        private static Dictionary<int, int> _scriptByObjective = new();

        // Host state visible to Lua callbacks (valid only while _gate is held).
        private static Session? _session;
        private static Logger? _log;

        // Delegates must stay rooted for the lifetime of the Lua state.
        private static readonly LuaFunction _hostSource = HostSource, _hostExists = HostExists, _hostLog = HostLog,
            _hostIsInteractable = HostIsInteractable, _hostSetInteractable = HostSetInteractable;

        // Why hotfixes are off (null = available or not yet initialised).
        internal static string? UnavailableReason { get { lock (_gate) return _unavailable; } }

        // Lowercased require path ("common/xclass") -> real file. The installed bundle is all-lowercase, EN is mixed case.
        private static Dictionary<string, string> _files = new();

        // Test/ops hook: drop the Lua state so the next call re-resolves the root (null = config/probe).
        internal static void Reset(string? rootOverride = null)
        {
            lock (_gate)
            {
                _lua?.Dispose();
                _lua = null;
                _initTried = false;
                _unavailable = null;
                _scriptByObjective = new();
                _rootOverride = rootOverride;
            }
        }
        private static string? _rootOverride;

        // Native XFightScriptProxy hook order: Enter(1) ScriptEnter(2) InProgress(3) Exit(4) ScriptExit(5).
        private static readonly string[] Hooks = { "", "OnStateEnter", "OnStateScriptEnter", "OnStateInProgress", "OnStateExit", "OnStateScriptExit" };
        private const int InProgress = 3;

        internal static void OnObjectiveState(Session session, int objectiveId, int state)
        {
            if (state < 1 || state >= Hooks.Length) return;
            lock (_gate)
            {
                _log = session.log;
                if (!EnsureInitialised() || !_scriptByObjective.TryGetValue(objectiveId, out int scriptId)) return;
                List<int> executed = session.player.BigWorldState.ExecutedHotfixScriptIds;
                if (executed.Contains(scriptId)) return;
                _session = session;
                try
                {
                    // ponytail: a script is recorded once its InProgress hook has run (the only state that unsticks the
                    // objective in all 4 shipped hotfixes); Enter/ScriptEnter hooks before that are not recorded.
                    if (!Call(scriptId, Hooks[state], out bool ran)) return;
                    if (state == InProgress && ran)
                    {
                        executed.Add(scriptId);
                        session.player.Save();
                    }
                }
                finally { _session = null; }
            }
        }

        // Runs hook `name` of the script; false on Lua error. `ran` = the script defines that hook.
        private static bool Call(int scriptId, string name, out bool ran)
        {
            Lua lua = _lua!;
            ran = false;
            lua.GetGlobal("AscNetHost");
            lua.GetField(-1, "RunHotfix");
            lua.PushInteger(scriptId);
            lua.PushString(name);
            if (lua.PCall(2, 1, 0) != LuaStatus.OK)
            {
                _log?.Error($"BigWorldQuestHotfix {name} of script {scriptId} failed: {lua.ToString(-1)}");
                lua.SetTop(0);
                return false;
            }
            ran = lua.ToBoolean(-1);
            lua.SetTop(0);
            return true;
        }

        private static bool EnsureInitialised()
        {
            if (_initTried) return _lua != null;
            _initTried = true;
            string? root = ResolveRoot();
            if (root == null)
            {
                _unavailable = "DlcFight Lua root not found (set DlcFightLuaRoot in Configs/config.json or provide .runtime/installed-lua/dlcfight)";
                _log?.Error($"BigWorldQuestHotfix unavailable: {_unavailable}");
                return false;
            }
            try
            {
                _files = Directory.EnumerateFiles(root, "*.lua", SearchOption.AllDirectories).ToDictionary(
                    f => Path.ChangeExtension(Path.GetRelativePath(root, f), null)!.Replace('\\', '/').ToLowerInvariant(), f => f);
                Lua lua = new();
                _lua = lua;
                lua.Register("__hostSource", _hostSource);
                lua.Register("__hostExists", _hostExists);
                lua.Register("__hostLog", _hostLog);
                lua.Register("__hostIsInteractable", _hostIsInteractable);
                lua.Register("__hostSetInteractable", _hostSetInteractable);
                if (lua.DoString(Bootstrap))
                    throw new InvalidOperationException(lua.ToString(-1));
                lua.SetTop(0);
                _scriptByObjective = ReadHotfixIds(lua);
                return true;
            }
            catch (Exception e)
            {
                _unavailable = $"Lua bootstrap from '{root}' failed: {e.Message}";
                _log?.Error($"BigWorldQuestHotfix unavailable: {_unavailable}", e);
                _lua?.Dispose();
                _lua = null;
                return false;
            }
        }

        // XDlcQuestHotfixManager.GetQuestObjectiveHotfixIds(): scriptId -> objectiveId, inverted for lookup by objective.
        private static Dictionary<int, int> ReadHotfixIds(Lua lua)
        {
            Dictionary<int, int> map = new();
            lua.GetGlobal("XDlcQuestHotfixManager");
            lua.GetField(-1, "GetQuestObjectiveHotfixIds");
            if (lua.PCall(0, 1, 0) != LuaStatus.OK) throw new InvalidOperationException(lua.ToString(-1));
            lua.PushNil();
            while (lua.Next(-2))
            {
                map[(int)lua.ToInteger(-1)] = (int)lua.ToInteger(-2);
                lua.Pop(1);
            }
            lua.SetTop(0);
            return map;
        }

        private static string? ResolveRoot()
        {
            string configured = _rootOverride ?? Common.Common.config.DlcFightLuaRoot;
            if (!string.IsNullOrWhiteSpace(configured))
                return Directory.Exists(configured) ? Path.GetFullPath(configured) : null;
            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
                for (DirectoryInfo? d = new(start); d != null; d = d.Parent)
                {
                    string candidate = Path.Combine(d.FullName, ".runtime", "installed-lua", "dlcfight");
                    if (Directory.Exists(candidate)) return candidate;
                }
            return null;
        }

        // ---- host callbacks (Lua -> C#). Self (arg 1) is present for method-style calls. ----
        private static int HostSource(IntPtr state)
        {
            Lua lua = Lua.FromIntPtr(state);
            if (_files.TryGetValue(lua.ToString(1).ToLowerInvariant(), out string? path)) lua.PushString(File.ReadAllText(path));
            else lua.PushNil();
            return 1;
        }

        private static int HostExists(IntPtr state)
        {
            Lua lua = Lua.FromIntPtr(state);
            lua.PushBoolean(_files.ContainsKey(lua.ToString(2).ToLowerInvariant())); // XLuaEngine:FileExists(path)
            return 1;
        }

        private static int HostLog(IntPtr state)
        {
            Lua lua = Lua.FromIntPtr(state);
            long level = lua.ToInteger(1);
            string message = lua.ToString(2);
            if (level >= 2) _log?.Error($"[XDlcLog] {message}");
            else if (level == 1) _log?.Warn($"[XDlcLog] {message}");
            else _log?.Debug($"[XDlcLog] {message}");
            return 0;
        }

        // proxy:IsActorInteractableComponentByPlaceId(actorType, placeId) -> bool
        private static int HostIsInteractable(IntPtr state)
        {
            Lua lua = Lua.FromIntPtr(state);
            lua.PushBoolean(BigWorldActors.IsInteractable(_session!, (int)lua.ToInteger(2), (int)lua.ToInteger(3)));
            return 1;
        }

        // proxy:SetActorInteractableComponentEnableByPlaceId(actorType, placeId, enable)
        private static int HostSetInteractable(IntPtr state)
        {
            Lua lua = Lua.FromIntPtr(state);
            BigWorldActors.SetInteractable(_session!, (int)lua.ToInteger(2), (int)lua.ToInteger(3), lua.ToBoolean(4));
            return 0;
        }

        // Globals must exist before `require "XMain"`: XMain.StepDlc ends with LuaLockG (unknown globals log errors).
        private const string Bootstrap = @"
CS = {
  StatusSyncFight = { XFightConfig = { IsClient = false, IsDebug = false, IsUnityEditor = false } },
  XDlcLog = { Debug = function(m) __hostLog(0, m) end, Warning = function(m) __hostLog(1, m) end, Error = function(m) __hostLog(2, m) end },
  HaruMath = { Vector3 = {} }, -- XScriptTool.lua:11 needs it non-nil; never constructed by the loaded files
}
XLuaEngine = {
  FileExists = function(self, path) return __hostExists(self, path) end,
  Import = function(self, path) error('XLuaEngine:Import is not supported server-side: ' .. tostring(path)) end,
}
table.insert(package.searchers, 2, function(name)
  local src = __hostSource(name)
  if not src then return '\n\tno lua file for ' .. name end
  return assert(load(src, '@' .. name)), name
end)
AscNetHost = {}
-- proxy = XFightScriptProxy subset the active hotfixes call
AscNetHost.Proxy = {
  IsActorInteractableComponentByPlaceId = __hostIsInteractable,
  SetActorInteractableComponentEnableByPlaceId = __hostSetInteractable,
}
AscNetHost.Proxy.__index = AscNetHost.Proxy
AscNetHost.ProxyObj = setmetatable({}, AscNetHost.Proxy)
-- Returns true when the script defines the hook and it ran.
function AscNetHost.RunHotfix(scriptId, hook)
  if not XDlcScriptManager.LoadQuestObjectiveHotfixScript(scriptId) then return false end
  local obj = XDlcScriptManager.NewQuestObjectiveHotfixScript(scriptId, AscNetHost.ProxyObj)
  if obj == nil or not obj['Has' .. hook .. 'Func'] then return false end
  obj:Init()
  obj[hook](obj, AscNetHost.ProxyObj)
  return true
end
require('XMain')
XMain.StepDlc()
";
    }
}
