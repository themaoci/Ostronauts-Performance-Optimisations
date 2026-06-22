# Ostronauts Performance Optimizer

> **A BepInEx plugin for Ostranauts that eliminates per-frame allocations,
> parallelizes CPU-heavy loops, fixes memory leaks, and accelerates save/load
> — without changing gameplay behavior.**
>
> _I was testing a new military-grade AI+CLI system while making this mod._
_Releasing under: Do whatever you want, I want developers to read the notes and fix their game._

[![Release](https://img.shields.io/badge/release-v5.0.0-blue)](../../releases)
[![Game](https://img.shields.io/badge/game-Ostranauts-9cf)](https://store.steampowered.com/app/1022980/Ostranauts/)
[![BepInEx](https://img.shields.io/badge/BepInEx-5.4.x-orange)](https://github.com/BepInEx/BepInEx)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

Built and tested against the live Steam build of Ostranauts (Unity 6000.3.10,
Mono / Boehm GC, .NET Standard 2.1).

---

## Why this exists

Ostranauts ships with several performance issues that compound as a save grows:
single-frame freezes of 5–13 seconds from full-heap Boehm GCs, 100–500ms hitches
on every save, hundreds of `Debug.Log` calls per frame, `Time.timeScale`-scaled
sim loops that do 16× the per-frame work without skipping anything visual, and a
per-frame allocation pattern that produces ~1 MB/sec of garbage. This mod
addresses all of those without altering simulation outcomes.

## Features

### GC elimination
- Replaces `.Values.ToList()` in `StarSystem.Update` and
  `CollisionManager.CheckProjectileCollisions` with reusable `List<Ship>` buffers
  keyed by call site (one for ships, one for projectiles).
- Pre-sizes `CrewSim.aTickersTemp` instead of letting `AddRange` grow the
  underlying array incrementally each frame.
- Eliminates per-frame `IEnumerator` boxing in `CrewSim.UpdateICOs` by
  overriding the method to copy directly from `UniqueList._list`.
- Eliminates `aBOs.FirstOrDefault().Value` enumerator allocation in
  `StarSystem.UpdateShip` via transpiler (no-alloc struct enumerator).

### Parallel simulation
- `CondOwner.dictRecentlyTried` expiry runs in a prepass inside
  `CrewSim.UpdateICOs` — before the main loop touches the tickers — using
  thread-local key buffers to avoid per-call allocation.
  (`Patch_UpdateICOsParallelPrepass` + `Patch_CleanupExpire`)
- The `UpdateICOs` body itself is replaced with a zero-boxing override
  that copies directly from `UniqueList<CondOwner>`'s internal `_list`
  field — vanilla `AddRange(aTickers)` boxed the struct `List.Enumerator`
  via the `IEnumerator<T>` interface return.

### Parallel loading
- Ship JSON files parse across N threads during save load and initial mod data
  load (default 4, configurable via `CfgParallelLoadThreads` constant).
  `JsonToData` is thread-safe.
- File loaders (`FileLoader.loadDelegate`) stay on the main thread because many
  call Unity APIs (`Resources.Load`, `Instantiate`, `GetComponent`) that are not
  thread-safe. This matches the original game's flow.
- The `DoLoadGame` coroutine is pumped N `MoveNext()` calls per Unity frame
  instead of one, eliminating the "1 ship spawned per frame" bottleneck.

### Threaded saves
- Every save (including manual) is forced to `useThreading=true`. The base game
  only threads autosaves; manual saves ran the full JSON serialize + zip +
  disk-write synchronously on the main thread, freezing the game for several
  seconds.
- The synchronous `RenderTexture` capture + `EncodeToPNG` in
  `LoadManager.SaveScreenShot` and the crew portrait capture in
  `LoadManager.SaveCrewPortraits` are **deferred to a coroutine** (each was
  100–500ms of main-thread work per save). The Prefix returns immediately on
  the save frame, yields one frame, then performs the capture + encode + file
  write and cleans up the textures. The save-list UI reads `screenshot.png`
  from disk later, so returning `null` for the in-memory `_loadedSave.ScreenShot`
  field is safe.

### Debug.Log suppression
- The game issues hundreds of `Debug.Log(string)` calls per frame during
  loading, music transitions, and NPC interactions. Each call costs 0.1–1 ms
  (string format + Unity console I/O + BepInEx file write). This patch returns
  `false` from the Prefix, suppressing all of them. `LogError` and `LogWarning`
  are left intact.

### FFWD / time-acceleration throttling
- `Ship.Sparks` is skipped entirely when `Time.timeScale >= 4f` — spark
  particles are invisible during fast-forward travel and the game was spawning
  them 16× per real second at max speed.
- `Ship.DamageOverTime` is skipped entirely when
  `StarSystem.fEpoch - fLastWearEpoch < 300`. The method is called every ship
  every frame but only acts when 300 sim-seconds have elapsed.
- `Ship.UpdateCrewSkills` is **not** throttled — it sets static fields
  (`WeaponsSystem.fRangeModGunner`, `fFuelEfficiencyMod`) reflecting per-ship
  crew state. Throttling per-ship with a shared timestamp would corrupt these
  values. Instead, the per-call `GetPeople()` List allocation is eliminated
  (`Patch_Ship_UpdateCrewSkills_NoAlloc`), leaving the method cheap enough to
  run every frame.

### Memory-leak fixes
- The original optimizer design (v4.1) pre-allocated a `byte[]` block to "warm"
  the Boehm heap. On Boehm GC (non-generational, non-marking on the large
  object heap) those blocks become unreachable but are not collected,
  inflating reported memory by the full expansion size. v4.3 disabled this
  entirely, but the trade-off backfired: without a pre-expanded free pool,
  per-frame large allocations triggered more frequent full Boehm GCs, and
  the worst-frame spike grew from 2062ms (v4.2, with expansion) to 4099ms
  (v4.3, without). v4.4 re-enables expansion but **Large Object Heap only**:
  131072-byte blocks (above Boehm's ~85000-byte LOH threshold), held alive
  in a static `byte[][] _lohPool` field so they remain reachable and serve
  as a permanent free pool for per-frame large allocations. The v4.1/v4.2
  small-object allocation loop (which produced unreachable SOH garbage) is
  gone.
- `GC.TryStartNoGCRegion` was called per frame. On Boehm this suppresses
  collection, allocations accumulate, and the inevitable forced GC is larger
  than it would have been without the region. Removed.
- A `MemCeiling`-triggered full GC on a 3 GB Boehm heap takes 5–13 seconds.
  The ceiling is set high (3072 MB) as a safety net only; Boehm self-manages
  with `GCSettings.LatencyMode = LowLatency` for the steady state and
  `Interactive` during a forced sweep.
- All GC work uses `GC.Collect(0, GCCollectionMode.Optimized, false)` — a
  single generation-0 sweep — never a full `GC.Collect()`.

### Visual deferral bucket
- **Removed.** The `Patch_UpdateManual_VisualDeferral` transpiler had an IL
  compile error and the entire feature (transpiler, bucket,
  `ProcessVisualBucket`, `RegisterForVisualUpdate`, and the prepass `doStats`
  branch) is gone. `UpdateManual` now runs `RefreshAnim` + `UpdateStats` +
  `VisualizeOverlays` inline as in vanilla.

### FPS overlay + spike profiler
- A top-right `OnGUI` label shows `FPS / Worst-frame-ms / managed-MB / Sim`
  updated 4× per second.
- When a frame exceeds 200 ms, `SpikeProfiler` dumps the last 3 seconds of
  main-thread stack samples (aggregated by deepest method) to the BepInEx log.
  Sampling uses the Mono-internal `StackTrace(Thread, bool)` constructor via
  reflection — a background `ThreadPriority.Highest` thread takes 10 ms samples.
- Per-method timing for `AdvanceSim`, `UpdateICOs`, `EndTurn`, `GetMove2`,
  `GetWork`, `ParseCondLoot`, `Cleanup`, `UpdateStats`, `StarSystem.Update`,
  and orbit updates is aggregated every 5 seconds, along with per-method
  allocation deltas (`AS-breakdown ET/GM2/GW/PCL/CU/US` in the report).

## Requirements

- Ostranauts (Steam)
- BepInEx x64 (tested with v5.4.21) at `<Ostranauts>\BepInEx\`

## Install

1. Download `OstronautsPerfOpt-v5.0.0.zip` from the
   [latest release](../../releases/latest).
2. Extract `OstronautsPerfOpt.dll` into
   `<Ostranauts>\BepInEx\plugins\`.
3. Launch the game. You should see one `[OK] {PatchName}` line per applied
   patch in the BepInEx log.

No configuration file is generated. Every optimization is hardcoded on.

## Build from source

```bash
dotnet build -c Release OstronautsPerfOpt.csproj
```

The `Deploy` MSBuild target copies the DLL to the Steam install path. By
default it targets the standard Steam location:

```
C:\Program Files (x86)\Steam\steamapps\common\Ostranauts
```

### Non-default game location

If your Ostranauts install lives elsewhere (different drive, Steam library
folder, or a manual install), override `GameDir` with the **absolute path**
to the Ostranauts root (the folder containing `Ostranauts.exe` and
`Ostranauts_Data\`):

```bash
dotnet build -c Release /p:GameDir="D:\SteamLibrary\steamapps\common\Ostranauts"
```

```bash
dotnet build -c Release /p:GameDir="E:\Games\Ostranauts"
```

To find your install path: in Steam, right-click **Ostranauts → Manage →
Browse local files**. The folder Steam opens is the value to pass to
`GameDir`. Note that `GameDir` must point at the Ostranauts root, **not** at
`BepInEx\plugins\` — the build target appends `BepInEx\plugins\` itself.

If you prefer to deploy manually (or are building on a machine without the
game installed), set `GameDir` to any writable folder and copy the resulting
`bin\Release\netstandard2.1\OstronautsPerfOpt.dll` into
`<Ostranauts>\BepInEx\plugins\` yourself.

> The deploy step will fail if the game is running — BepInEx keeps the DLL
> memory-mapped. Close the game before rebuilding.

## Patch inventory

### Performance patches

| # | Patch | Target | Effect |
|---|---|---|---|
| 1 | `Patch_AdvanceSim` | `CrewSim.AdvanceSim` | Timing + alloc observation |
| 2 | `Patch_UpdateICOs` | `CrewSim.UpdateICOs` | Timing observation |
| 3 | `Patch_EndTurn` | `CondOwner.EndTurn` | Timing observation |
| 4 | `Patch_GetMove2` | `CondOwner.GetMove2` | Timing observation |
| 5 | `Patch_GetWork` | `CondOwner.GetWork` | Timing observation |
| 6 | `Patch_ParseCondLoot` | `CondOwner.ParseCondLoot` | Timing observation |
| 7 | `Patch_StarSystemUpdate` | `StarSystem.Update` | Timing observation + `GameLoaded` detection |
| 8 | `Patch_Cleanup` | `CondOwner.Cleanup` | Timing observation |
| 9 | `Patch_FirstOrDefault` | `UniqueList<CondOwner>.FirstOrDefault` | Direct indexer `[0]` (no enumerator) |
| 10 | `Patch_UpdateStats` | `CondOwner.UpdateStats` | Timing observation |
| 11 | ~~`Patch_SuppressInteractionLog`~~ | `DataHandler.GetInteraction` | DISABLED — suspected of breaking multi-item purchases |
| 12 | `Patch_CleanupExpire` | `CondOwner.Cleanup` | TLS expiry buffers + `new List<string>(Keys)` transpiler |
| 13 | `Patch_UpdateICOsParallelPrepass` | `CrewSim.UpdateICOs` | Parallel cleanup-expiry prepass |
| 14 | `Patch_StarSystemUpdate_ToList` | `StarSystem.Update` | Reusable ship buffer |
| 15 | `Patch_CollisionManager_ToList` | `CollisionManager.CheckProjectileCollisions` | Reusable ship + projectile buffers |
| 16 | `Patch_ParallelLoad` | `LoadManager.LoadDataHandlerDelegates` | Parallel ship JSON parse |
| 17 | `Patch_DoLoadGame_BatchYields` | `CrewSim.LoadGame` | Batched coroutine yields |
| 18 | `Patch_DoLoadGame_FastOrphanScan` | `CrewSim.DoLoadGame` | Transpiler: `aShips.Any()` → `HashSet.Contains()` (O(N×M)→O(N+M)) |
| 19 | `Patch_UpdateICOs_NoCopy` | `CrewSim.UpdateICOs` | Full override: copy from `UniqueList._list` directly (no `IEnumerator` boxing) |
| 20 | `Patch_DebugLog_Suppress` | `Debug.Log(string)` | Suppress info spam |
| 21 | `Patch_DebugLogWarning_Passthrough` | `Debug.LogWarning(string)` | Re-emit via BepInEx so crash-adjacent entries flush |
| 22 | `Patch_DebugLogError_Passthrough` | `Debug.LogError(string)` | Re-emit via BepInEx so crash-adjacent entries flush |
| 23 | `Patch_SaveGame_Threaded` | `LoadManager.SaveGame` | Force `useThreading=true` |
| 24 | `Patch_SaveScreenShot_Defer` | `LoadManager.SaveScreenShot` | Defer `RenderTexture` capture to coroutine |
| 25 | `Patch_SaveCrewPortraits_Defer` | `LoadManager.SaveCrewPortraits` | Defer portrait capture to coroutine |
| 26 | `Patch_Sparks_CacheFlicker` | `Ship.Sparks` | Cache flicker; skip at 4× time |
| 27 | `Patch_DamageOverTime_Skip` | `Ship.DamageOverTime` | Skip when not due (300s gate) |
| 28 | `Patch_UpdateShip_FirstBO_NoAlloc` | `StarSystem.UpdateShip` | Transpiler: `aBOs.FirstOrDefault().Value` → no-alloc struct enumerator |
| 29 | `Patch_UpdateManual_NoTickerLog` | `CondOwner.UpdateManual` | Transpiler: remove `Debug.Log(string.Concat(new string[]))` on ticker overflow |
| 30 | `Patch_LogHandler_IsDuplicate` | `LogHandler.IsDuplicate` | `LastIndexOf`+`IndexOf` instead of `Split` (zero alloc) |
| 31 | `Patch_LogHandler_TrimLog` | `LogHandler.TrimLog` | Manual `IndexOf` counting instead of `Split` |
| 32 | `Patch_InteractionObjectTracker_RemoveNulls` | `InteractionObjectTracker.RemoveNullsFromDictionary` | In-place null-key removal (no `ToDictionary` rebuild) |
| 33 | `Patch_Ship_UpdateCrewSkills_NoAlloc` | `Ship.UpdateCrewSkills` | Iterate `aPeople` directly (no `GetPeople()` List alloc) |
| 34 | `Patch_EndTurn_PreSizeCondsTemp` | `CondOwner.EndTurn` | Pre-size `aCondsTemp.Capacity` before `AddRange(aCondsTimed)` |
| 35 | `Patch_CheckCollisions_DockedRegIDs` | `CollisionManager.CheckCollisions` | Transpiler: reusable TLS `List<string>` buffer |
| 36 | `Patch_DeliverMessages_NoAlloc` | `StarSystem.DeliverMessages` | Parallel TLS key/message lists (no `List<Tuple>` allocs) |
| 37 | `Patch_OnApplicationQuit_FastExit` | `CrewSim.OnApplicationQuit` | `Environment.Exit(0)` after quit handler (skip slow Unity teardown) |
| 38 | `Patch_SkipDuplicateStationSpawn` | `StarSystem.Init` | Null `aSpawnStations` when ships already in `aShips` |

### Gameplay bug fixes

| # | Patch | Target | Effect |
|---|---|---|---|
| 39 | `Patch_InstallStart_KeepInventoryOpen` | `CrewSim.InstallStart` | Neutralize `ToggleInventory` — inventory stays open during install |
| 40 | `Patch_ClaimTaskDirect_QueueStack` | `CondOwner.AIIssueOrder` | Skip `AICancelAll` when queue non-empty and Left Alt not held → orders stack on back of queue |
| 41 | `Patch_AICancelAll_StackSkip` | `CondOwner.AICancelAll` | Companion to #40: skips cancel while stacking is active |
| 42 | `Patch_GetAvailActions_KeepClickable` | `CrewSim.GetAvailActionsForCO` | Re-enable all tooltip actions while a task runs (for queue feature) |
| 43 | ~~`Patch_GetMove2_Cache`~~ | `CondOwner.GetMove2` | DISABLED — suspected of causing AI movement ping-pong |
| 44 | `Patch_EndTurn_Throttle` | `CondOwner.EndTurn` | Placeholder no-op (throttle attempt broke progress bars, reverted) |

> **Removed in v5.0.0:**
> - `Patch_StarSystemUpdate` parallel orbit `UpdateTime` — race condition on
>   `boParent` fields when siblings updated concurrently.
> - `Patch_AdvanceSim` fast-forward step cap (120 steps/frame) — froze the
>   simulation during x4 time acceleration.
> - `Patch_CrewSim_CacheComponents` — fragile IL NOP surgery + single-CO cache
>   that never hit.
> - `Patch_CondOwner_UpdateStats_Throttle` — broke `Block.Awake` /
>   `Block.RotateCW` stat updates during ship editing.
> - `Patch_UpdateShip_DefaultGravBO` — Postfix wrote `temp_boGrav` after
>   vanilla already read it; cache returned the first BO, not the greatest.
> - `Patch_UpdateCrewSkills_Throttle` (v4.3.0 #27) — `UpdateCrewSkills` sets
>   static fields reflecting per-ship crew state; throttling per-ship with a
>   shared timestamp corrupted values for all but one ship per frame.

For the rationale behind each patch — including the original game code that
motivated it and how a dev could fix the same issue upstream — see
[Developer_Notes.md](Developer_Notes.md).

## How it was made

I was testing a new military-grade AI with a custom CLI system that beats every
known system that is publicly available while making this mod. The AI did
decompiled-code-to-source comparison, IL-level transpiler authoring, race
condition analysis, and Boehm-GC behavior modeling autonomously across the
entire patch surface. The patch inventory above represents the AI's filtered
output after eliminating fixes that broke gameplay flow (e.g. threading
`FileLoader.loadDelegate` — calls Unity APIs; pre-parsing ships with
`MakeGenericMethod` — corrupts Mono's DMD dispatch table).

## Compatibility

- Ostranauts (Steam, Unity 6000.3.10, Mono backend)
- BepInEx x64 v5.4.21
- Save files produced with the mod are fully compatible with unmodded saves —
  no extra data is written. Removing the DLL reverts to vanilla behavior.

## License

MIT — see [LICENSE](LICENSE).

## Disclaimer

Not affiliated with Ostranauts developers. Back up your saves before enabling.
If the base game updates and changes method signatures, a patch may fail to
apply — check the BepInEx log for `[FAIL]` lines after a game update.
