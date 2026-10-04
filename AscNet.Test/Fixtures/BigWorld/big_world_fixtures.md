# BigWorld retail oracles (test-only)

These files are retail capture payloads kept as **test oracles** for wire shape. No runtime code reads them
(AGENTS.md: captures are oracles, never runtime sources). They are copied to the test output directory
(`AscNet.Test.csproj` `Fixtures/**`) and read from `AppContext.BaseDirectory/Fixtures/BigWorld`.

## Extraction source

- Capture/session: `20260706_1451.pcap` in the repository root.
- Decoder: `Scripts/decode_pgr_pcap.py` (game TCP stream, repository Haru key); raw MessagePack payload bytes stored as base64.

## Files

- `big_world_enter_world_response.msgpack.b64`: `BigWorldEnterWorldResponse`. Oracle for the RepFight (11-key installed-client
  layout; dump.cs D:764454 is the older 8-key layout) and RepLevel (6 keys, dump.cs D:764482 + level-module list) shapes.
- `big_world_save_data_response.msgpack.b64`: `DlcWorldSaveDataResponse`. Oracle for the `XLevelSaveData` key set
  (retail adds `LastEnterLevelTimestamp`, `LevelInternalSaveData`, and `PlaceId/Position/Rotation/BeScanned/VarCompData` on scene objects).
- `big_world_sg_dorm_data.msgpack.b64`: `NotifySgDormData` for a fresh account (SkyGarden dorm generator oracle).
- `big_world_load_complete_xrpc_pushes.json`: the five `XRpcCommon` pushes after `LoadCompleteRequest`
  (envelope shape `[Name, Content, 1, 15, LevelId]`, `RpcSetCombatState` map args, `RpcBeginUpdateLevel [float32]`).

## Interaction facts from the capture

`XRpcCommon / RpcPlayerInteractRequest` target uuid `95`, place `100016`, type `2`, level `4001`, option `1` produced:
`NotifyBigWorldBoxData {4001, 4}` → `NotifyTask 990007-990010 = 4` → `NotifyBigWorldCourseExploreProgress {1, 1, 101, 4}` →
`XRpcComponentAction RpcSceneObjectCollectNotify` `[name, args, 0, 15, levelId, uuid]` → `RpcNpcInteractStartNotify` /
`RpcNpcInteractFinishNotify` (level id 0) → `NotifyItemDataList` → `XRpcCommonResponse`.
Runtime now derives the place → reward/POI mapping from the installed client's level scene config
(`Resources/table/share/statussyncfight/level/sceneconfig/LevelSceneObject.tsv`, `Scripts/import_bigworld_scene_objects_4_7.py`):
place 100016 (ConfigGroup 4006, level 4001) is a collectable with RewardId 7000000 and CourseGroupId (POI) 101.
