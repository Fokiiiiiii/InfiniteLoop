# Player diagnostics (lucia / KRSDK)

Both native patches (`lucia.dll`, `PGR_Data/Plugins/KRSDK.dll`) append every diagnostic line to one file
beside `PGR.exe`:

```
<client folder>\ascnet-patch.log
```

(The same lines are also printed to stdout, which only exists when `PGR.exe` is started from a console or
Wine.) The file is cut when it grows past 1 MiB. Each launch appends, so send the **last launch**: everything
from the last `[lucia] OK KRSDK.bin identity` / `[lucia] FAILED KRSDK.bin identity` line to the end.

## What to send back

1. The region you play (EN / TW / KR / JP / CN).
2. All lines of `ascnet-patch.log` starting with `[lucia]` or `[KRSDK]` that contain `OK` or `FAILED`
   (or just the whole file).
3. If a line says `FAILED`, nothing else is needed: the reason and the looked-up name are in that line.

## Healthy launch (KR example)

```
[lucia] OK KRSDK.bin identity: package=com.herogame.pc.punishing.grayraven.kr game=G286 app=A1794 channel=240 channel_name=PC channel_op=null source=PGR_Data/Plugins/KRSDKRes/KRSDK.bin
[lucia] OK GameAssembly.dll module lookup: base=0x7FF8A1230000
[lucia] OK il2cpp exports: 12 il2cpp_* exports resolved from GameAssembly.dll
[lucia] OK il2cpp domain ready: assemblies=187 waited_ms=1200
[lucia] OK lookup assembly+class: UnityEngine.UnityWebRequestModule.dll!UnityEngine.Networking.UnityWebRequest
[lucia] OK lookup UnityEngine.UnityWebRequestModule.dll!UnityEngine.Networking.UnityWebRequest::InternalSetUrl(System.String)->System.Void: method at 0x7FF8A2345678
[lucia] OK routing origin: http://127.0.0.1:8080
[lucia] OK hook install: UnityEngine.Networking.UnityWebRequest::InternalSetUrl detour at 0x7FF8A2345678
[KRSDK] OK KRSDK.bin identity: package=com.herogame.pc.punishing.grayraven.kr game=G286 app=A1794 channel=240 channel_name=PC channel_op=null source=PGR_Data/Plugins/KRSDKRes/KRSDK.bin
[KRSDK] OK lucia.dll routing initialization
[lucia] OK first client config request: package=com.kurogame.punishing.grayraven.kr cdn_key=jqlCmYRizwT76uvX
```

What each region must show (from the client's own files, not from the server):

| Region | `KRSDK.bin` `game` / `app` | `first client config request` package |
| ------ | -------------------------- | ------------------------------------- |
| KR | `G286` / `A1794`, channel `240` | `com.kurogame.punishing.grayraven.kr` |
| JP | `G282` / `A1778`, channel `240` | `com.kurogame.punishing.grayraven.jp` |

If `first client config request` is missing, the game never asked for `client/config/...`: the hook is
not active, or the client talks to a host the patch does not route.

## Failure lines

Format: `[lucia|KRSDK] FAILED <what was looked up>: <reason> | send: ascnet-patch.log (beside PGR.exe) and the client region`

| Line starts with | Meaning |
| ---------------- | ------- |
| `[lucia] FAILED KRSDK.bin identity` | `PGR_Data/Plugins/KRSDKRes/KRSDK.bin` is missing/unreadable or lacks a `KR_*` key (the key is named). Client install incomplete. |
| `[lucia] FAILED GameAssembly.dll module lookup` | `lucia.dll` was loaded outside `PGR.exe`'s IL2CPP process. |
| `[lucia] FAILED native routing ...: ... GameAssembly.dll does not export `il2cpp_...`` | The named IL2CPP export is missing: not the 4.8.0 client, or `GameAssembly.dll` was modified. |
| `[lucia] FAILED native routing ...: il2cpp_domain_assembly_open could not open assembly ...` | The assembly named in the line is not in this client's metadata. |
| `[lucia] FAILED native routing ...: il2cpp_class_from_name: type ...` / `method ...` | The class or method named in the line is missing or has another signature: Unity build differs from 4.8.0. |
| `[lucia] FAILED native routing ...: installing the InternalSetUrl detour failed` | Another injector/overlay hooked the same function. Send the list of overlays/injectors you run. |
| `[lucia] FAILED il2cpp domain ready` | IL2CPP still had no assemblies after 60 s (lucia keeps waiting). |
| `[KRSDK] FAILED lucia.dll routing initialization` | `lucia.dll` is missing next to `PGR.exe`, blocked (antivirus), is an old build, or its own `[lucia] FAILED` line (above in the same file) explains why. |
| `[KRSDK] FAILED KRSDK.bin identity` / `kurosdk_getConfigInfo packaged config` | Same as the `[lucia] FAILED KRSDK.bin identity` row; login is refused without a channel. |
| `[KRSDK] FAILED login channel from KRSDK.bin` | `KR_ChannelID` could not be read at login. |

When `lucia` fails, native routing stays off and the client talks to the retail servers.

## China (CN) client

The CN client (战双帕弥什) has no `KRSDK.dll` and no `KRSDK.bin`; it ships the official SDK
(`PGR_Data/Plugins/KRSDKEx.dll`, `libkrsdkcurl.dll`, `KRSDKRes/KRSDKConfig.json`). The Launcher installs only
`version.dll`, `lucia.dll`, `libraries.txt` and the generated `PGRBase.dll`; `PGR.exe`, `GameAssembly.dll`, `KRSDKEx.dll`
and `libkrsdkcurl.dll` are only hash-checked against `supported-client.json` (never modified or backed up), and
an unknown hash is reported as unsupported with the observed SHA-256. `lucia` redirects the SDK's `/sdkcom/*` requests in
process (a hook on `libkrsdkcurl.dll!kr_sdk_curl_easy_setopt`), so the official login UI stays in use.

Healthy CN lines in `ascnet-patch.log` (the same `routed` line appears per redirected SDK request, query strings are never logged):

```
[lucia] OK KRSDKConfig.json identity: package=com.kurogame.haru.hero game=G148 app=A1393 channel=19 channel_name=国内PC source=PGR_Data/Plugins/KRSDKRes/KRSDKConfig.json
[lucia] OK CN SDK hook install: PGR_Data/Plugins/libkrsdkcurl.dll!kr_sdk_curl_easy_setopt detour at 0x..., routing /sdkcom/* to http://127.0.0.1:8080
[lucia] routed https://sdkapi.kurogame.com/sdkcom/v2/sys/conf.lg -> http://127.0.0.1:8080/sdkcom/v2/sys/conf.lg
```

| Line starts with | Meaning |
| ---------------- | ------- |
| `[lucia] FAILED KRSDKConfig.json identity` | `KRSDKConfig.json` is missing/unreadable. |
| `[lucia] FAILED CN SDK routing (login stays on retail servers)` | `libkrsdkcurl.dll` could not be loaded or lacks `kr_sdk_curl_easy_setopt` (not the verified 4.8.0 CN SDK), or another injector hooked the same function. |

No `routed ... /sdkcom/...` line after the login window opens means the SDK never reached the hook. The SDK also keeps
its own cache under `%APPDATA%\KR_G148\A1393\` (agreement, `KRSDKCache.json`) and logs/cache under
`%USERPROFILE%\AppData\LocalLow\kurogame\战双帕弥什` (per the real player log); include them when reporting a CN login problem.

## No camera fade option

Launcher > Settings > **No camera fade** (off by default, saved in the launcher settings) starts `PGR.exe` with
`ASCNET_PATCH_NOFADE=1`. `lucia` then disables the character/camera proximity fade natively (no game bundle is
edited): `XCameraDitherDetector.UseCameraDither=false` and `XNpcDither.SetPlayerSelfDitherParameter` with opaque
values, re-applied every 2 s. Dying-enemy dissolves (`XDitherHelper`) are not touched. It supersedes
`tools/apply_pgr_nofade.py`; both set the same values, so having both active is harmless. Same on EN/TW/KR/JP
(all four `GameAssembly.dll` export every `il2cpp_*` function it needs).

Healthy lines (option on):

```
[lucia] OK no camera fade requested: ASCNET_PATCH_NOFADE=1; applying natively once IL2CPP is ready
[lucia] OK il2cpp exports (no camera fade): ...
[lucia] OK lookup XCameraDitherDetector: assembly=Assembly-CSharp.dll
[lucia] OK lookup XCameraDitherDetector.UseCameraDither: static System.Boolean
[lucia] OK lookup XNpcDither: assembly=Assembly-CSharp.dll
[lucia] OK lookup XNpcDither.SetPlayerSelfDitherParameter(...PlayerSelfDitherParameter): static, 1 parameter
[lucia] OK class init: XCameraDitherDetector, XNpcDither
[lucia] OK write XCameraDitherDetector.UseCameraDither=false: re-applied every 2 s
[lucia] OK apply XNpcDither.SetPlayerSelfDitherParameter(opaque): re-applied every 2 s
```

With the option off none of these lines appear. Every failure is a `[lucia] FAILED no camera fade` /
`lookup XCameraDitherDetector...` / `lookup XNpcDither...` / `write ...` / `apply ...` line naming the missing
class, field or method (client build differs from 4.8.0); the game keeps its default fade and routing is unaffected.
If only the `XNpcDither` lines fail, the camera fade is still disabled. `write`/`apply` lines are logged on the first
result and on every later change, not every 2 s.

## FPS unlock option

Launcher > Settings > **FPS unlock** + frame-rate field + **Apply** (off by default, 240 FPS suggested; saved in the
launcher settings) starts `PGR.exe` with `ASCNET_PATCH_FPS=<n>` (any positive whole number; absent = the game's own
frame rate). `lucia` then calls `UnityEngine.Application.set_targetFrameRate(n)` natively every 2 s (the game may
reset it). No game bundle is edited. Unity ignores `targetFrameRate` while `QualitySettings.vSyncCount > 0` (the game's
own VSync option sets it), so with the unlock on `lucia` sets `vSyncCount=0` and logs the change. The status text in the
launcher shows the saved setting (`At launch: ...`); a change takes effect at the next game launch. Same on EN/TW/KR/JP.

**Migration from the old bundle patch.** Earlier launchers injected a Lua hook (`PgrNativeFpsTimer`) into
`matrix.ab`'s `XUiMain.lua`. On every Check/startup the launcher looks for that hook; if found, and PGR.exe is not
running, it removes it (the usual same-size rewrite, original kept under `.ascnet-launcher/fps-backups/`), turns the
native FPS unlock on with the hook's old value, and logs `Removed the old bundle FPS patch ...`. While PGR.exe is
running it logs `the old bundle FPS patch (N FPS) is still installed; close PGR.exe and press Check to remove it`
(the hook keeps working until then). Afterwards only the native path exists; the launcher never writes the bundle again.

Healthy lines (option on):

```
[lucia] OK FPS unlock requested: ASCNET_PATCH_FPS=240; applying natively once IL2CPP is ready
[lucia] OK il2cpp exports (FPS unlock): ...
[lucia] OK lookup UnityEngine.Application: assembly=UnityEngine.CoreModule.dll
[lucia] OK lookup Application.set_targetFrameRate(Int32)/get_targetFrameRate(): static property accessors
[lucia] OK lookup UnityEngine.QualitySettings.vSyncCount: assembly=UnityEngine.CoreModule.dll
[lucia] OK read QualitySettings.vSyncCount: 0 (vsync off)          (or: OK write QualitySettings.vSyncCount=0: the game had vSyncCount=1, ...)
[lucia] OK apply Application.targetFrameRate=240: re-applied every 2 s
```

With the option off none of these lines appear. Failures are `[lucia] FAILED FPS unlock` (bad `ASCNET_PATCH_FPS`, missing
export, class or method: client differs from 4.8.0), `lookup ...`, `read/write QualitySettings.vSyncCount` (the unlock
may then be capped by vsync) or `apply Application.targetFrameRate=...` (call threw, or the value read back differently);
the game keeps its own frame rate and routing is unaffected. Lines are logged on the first result and on every later
change, not every 2 s.

## Optional trace (developers)

`ASCNET_PATCH_TRACE=1` (the Launcher clears it, so set it only when running `PGR.exe` by hand) adds
`[lucia] OK lookup XRemoteConfig: assembly=... class=XRemoteConfig` / `[lucia] FAILED lookup XRemoteConfig ...`
(`XRemoteConfig` is a metadata dump only; no hook is installed on it and routing does not depend on it),
`remote-config ...` field/method dumps and the first 64 `InternalSetUrl call url=...` lines.
