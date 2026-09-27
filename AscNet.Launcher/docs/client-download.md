# Client download (parked design note)

Status: feasibility confirmed 2026-09-27; not implemented.

## Official source (global PC, no auth)
- Game index: `https://prod-alicdn-gamestarter.kurogame.com/launcher/game/G143/50015_LWdk9D2Ep9mpJmqBZZkcPBU2YNraEWBQ/index.json`
  (G143 = PGR global, 50015 = global appId; token from public TwintailTeam/game-manifests). Live version 4.8.0.
- TW (Traditional Chinese) PC client: `G279/50016`, same layout; 4.8.0 base
  `launcher/game/G279/50016/4.8.0/RfnyCruypnrxBymaqVVowmvKlxcjZpzc/` (47,116 files, 71.5 GB).
  Package `com.kurogame.punishing.grayraven.tw`; `PGR.exe` and `GameAssembly.dll` differ from EN.
- CDN bases (from index `cdnList`): `zspms-volcdn-gamestarter.kurogame.net`, `zspms-txcdn-gamestarter.kurogame.net`,
  `zspms-akcdn-gamestarter.pgr-game.com`, `zspms-alicdn-gamestarter.kurogame.net`.
- File list: `{cdn}/{config.indexFile}` → `{resource:[{dest, md5, size, chunkInfos?}]}`; 47,190 files, 71.7 GB.
  Files >100 MiB carry per-100 MiB chunk MD5s. File URL: `{cdn}/{config.baseUrl}{dest}`.
- `config.indexFileMd5` authenticates the file list. `resourcesExcludePath` = `PGR_Data/StreamingAssets/resource`.
- `patchConfig` offers KrDiff patches from 14 older versions (4.7.0 → 4.8.0 ≈ 11 GB); KrDiff = HDiff + zstd
  (MIT applier: `hdiffpatch-rs`, used by Vedaru/kuro).
- Older full builds (3.8.0, 4.1.0–4.7.0) were still downloadable per file via versioned paths recorded in
  TwintailTeam history; retention is undocumented.

## Verified against the supported 4.8.0 client
- MD5 match: `PGR.exe`, stock `GameAssembly.dll`, `UnityPlayer.dll`, stock `PGRBase.dll`.
- Official `KRSDK.dll` SHA-256 `59a1d02d…` = second accepted entry in `supported-client.json` (Steam ships `2a1d8f5d…`).

## Intended flow
1. User picks an empty folder (never a Steam install).
2. Pin the version from `supported-client.json` (use versioned URLs, not "latest"); confirm index MD5 first.
3. Resumable download with per-file/per-chunk MD5, disk-space check, repair.
4. Existing SETUP: supported-client check → setup-local.ps1 → patch install → PLAY.
5. On first start the game downloads `StreamingAssets/resource` itself from Kuro's `prod-encdn-*` CDN
   (AscNet's config points there; lucia.dll only redirects `client/config`, notices and notice HTML).

## Risks
- Undocumented Kuro CDN layout; token/paths can change.
- Pinned versions (client 4.8.0, document/resources 4.8.10) disappear when Kuro stops hosting them.
- ~72 GB pulled from Kuro servers; Kuro terms of service not reviewed.
