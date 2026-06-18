# Ostronauts Performance Optimizer

> **A BepInEx plugin for Ostranauts that eliminates per-frame allocations,
> parallelizes CPU-heavy loops, fixes memory leaks, and accelerates save/load
> — without changing gameplay behavior.**
>
> _I was testing a new military-grade AI+CLI system while making this mod._
_Releasing under: Do whatever you want, I want developers to read the notes and fix their game._

[![Release](https://img.shields.io/badge/release-v4.4.0-blue)](../../releases)
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
- Caches `Canvas.scaleFactor` and `Audio_VacuumController` lookups per frame in
  `CrewSim.Update` — the originals call `GetComponent<T>()` every frame per CO.
- Pre-sizes `CrewSim.aTickersTemp` instead of letting `AddRange` grow the
  underlying array incrementally each frame.

### Parallel simulation
- `BodyOrbit.UpdateTime` runs on `Parallel.ForEach`, batched by **topological
  depth level** so parent orbits always process before their children (avoids
  reading a parent's stale `vPos` mid-tick).
- `CondOwner.UpdateStats` and `dictRecentlyTried` expiry run in a prepass inside
  `CrewSim.UpdateICOs` — before the main loop touches the tickers — using
  thread-local key buffers to avoid per-call allocation.

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
  `LoadManager.SaveCrewPortraits` are skipped (each was 100–500ms of main-thread
  work per save).

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
- `Ship.UpdateCrewSkills` is throttled to once per ~5 sim-seconds during time
  acceleration. Skills only change on level-up, role change, or
  unconsciousness — safe to skip during travel.
- `Ship.DamageOverTime` is skipped entirely when
  `StarSystem.fEpoch - fLastWearEpoch < 300`. The method is called every ship
  every frame but only acts when 300 sim-seconds have elapsed.

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
- **Removed in v4.4.** The `Patch_UpdateManual_VisualDeferral` transpiler
  had an IL compile error (see Developer_Notes §10.4) and was omitted from
  the patch registry, but `CfgVisualDeferral=true` still gated the
  `Patch_UpdateICOsParallelPrepass` `UpdateStats` branch — risking a
  double-`UpdateStats` invocation per CondOwner per frame. The entire
  feature (transpiler, bucket, `ProcessVisualBucket`, `RegisterForVisualUpdate`,
  and the prepass `doStats` branch) is removed. `UpdateManual` now runs
  `RefreshAnim` + `UpdateStats` + `VisualizeOverlays` inline as in vanilla.

### FPS overlay + spike profiler
- A top-right `OnGUI` label shows `FPS / Worst-frame-ms / managed-MB / Sim`
  updated 4× per second.
- When a frame exceeds 200 ms, `SpikeProfiler` dumps the last 3 seconds of
  main-thread stack samples (aggregated by deepest method) to the BepInEx log.
  Sampling uses the Mono-internal `StackTrace(Thread, bool)` constructor via
  reflection — a background `ThreadPriority.Highest` thread takes 10 ms samples.
  (v4.4.0: the sampler is now actually started in `Awake()` — v4.3.0 called
  `Stop()` in `OnDestroy()` but never `Start()`, so the entire feature was
  dead code and spikes produced zero stack samples.)
- Per-method timing for `AdvanceSim`, `UpdateICOs`, `EndTurn`, `GetMove2`,
  `GetWork`, `ParseCondLoot`, `Cleanup`, `UpdateStats`, `StarSystem.Update`,
  and orbit updates is aggregated every 5 seconds, along with per-method
  allocation deltas (`AS-breakdown ET/GM2/GW/PCL/CU/US` in the report).

## Requirements

- Ostranauts (Steam)
- BepInEx x64 (tested with v5.4.21) at `<Ostranauts>\BepInEx\`

## Install

1. Download `OstronautsPerfOpt-v4.4.0.zip` from the
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

The `Deploy` MSBuild target copies the DLL to the default Steam install path.
Override with:

```bash
dotnet build -c Release /p:GameDir="D:\Games\Ostranauts"
```

> The deploy step will fail if the game is running — BepInEx keeps the DLL
> memory-mapped. Close the game before rebuilding.

## Patch inventory

### Performance patches (1–26)

| # | Patch | Target | Effect |
|---|---|---|---|
| 1 | `Patch_AdvanceSim` | `CrewSim.AdvanceSim` | Timing + alloc observation |
| 2 | `Patch_UpdateICOs` | `CrewSim.UpdateICOs` | Timing observation |
| 3 | `Patch_EndTurn` | `CondOwner.EndTurn` | Timing observation |
| 4 | `Patch_GetMove2` | `CondOwner.GetMove2` | Timing observation |
| 5 | `Patch_GetWork` | `CondOwner.GetWork` | Timing observation |
| 6 | `Patch_ParseCondLoot` | `CondOwner.ParseCondLoot` | Timing observation |
| 7 | `Patch_StarSystemUpdate` | `StarSystem.Update` | Parallel orbit `UpdateTime` |
| 8 | `Patch_Cleanup` | `CondOwner.Cleanup` | Timing observation |
| 9 | `Patch_FirstOrDefault` | `UniqueList<CondOwner>.FirstOrDefault` | Direct indexer `[0]` |
| 10 | `Patch_UpdateStats` | `CondOwner.UpdateStats` | Timing observation |
| 11 | `Patch_SuppressInteractionLog` | `DataHandler.GetInteraction` | Bounded missing-key cache |
| 12 | `Patch_CleanupExpire` | `CondOwner.Cleanup` | TLS expiry buffers + `new List<string>(Keys)` transpiler |
| 13 | `Patch_UpdateICOsParallelPrepass` | `CrewSim.UpdateICOs` | Parallel cleanup-expiry prepass |
| 14 | `Patch_StarSystemUpdate_ToList` | `StarSystem.Update` | Reusable ship buffer |
| 15 | `Patch_CollisionManager_ToList` | `CollisionManager.CheckProjectileCollisions` | Reusable ship + projectile buffers |
| 16 | `Patch_CrewSim_CacheComponents` | `CrewSim.Update` | Cache Canvas + VacuumController |
| 17 | `Patch_ParallelLoad` | `LoadManager.LoadDataHandlerDelegates` | Parallel ship JSON parse |
| 18 | `Patch_DoLoadGame_BatchYields` | `CrewSim.LoadGame` | Batched coroutine yields |
| 19 | `Patch_UpdateICOs_NoCopy` | `CrewSim.UpdateICOs` | Pre-size `aTickersTemp` |
| 20 | `Patch_DebugLog_Suppress` | `Debug.Log(string)` | Suppress info spam |
| 21 | `Patch_DebugLogWarning_Passthrough` | `Debug.LogWarning(string)` | Re-emit via BepInEx so crash-adjacent entries flush |
| 22 | `Patch_DebugLogError_Passthrough` | `Debug.LogError(string)` | Re-emit via BepInEx so crash-adjacent entries flush |
| 23 | `Patch_SaveGame_Threaded` | `LoadManager.SaveGame` | Force `useThreading=true` |
| 24 | `Patch_UpdateShip_DefaultGravBO` | `StarSystem.UpdateShip` | Cache default gravity BO |
| 25 | `Patch_SaveScreenShot_Skip` | `LoadManager.SaveScreenShot` | Skip `RenderTexture` capture |
| 26 | `Patch_SaveCrewPortraits_Skip` | `LoadManager.SaveCrewPortraits` | Skip portrait capture |
| 27 | `Patch_Sparks_CacheFlicker` | `Ship.Sparks` | Cache flicker; skip at 4× time |
| 28 | `Patch_DamageOverTime_Skip` | `Ship.DamageOverTime` | Skip when not due (300s gate) |

### Gameplay bug fixes (29–31)

| # | Patch | Target | Effect |
|---|---|---|---|
| 29 | `Patch_InstallStart_KeepInventoryOpen` | `CrewSim.InstallStart` | Neutralize `ToggleInventory` — inventory stays open during install |
| 30 | `Patch_ClaimTaskDirect_QueueStack` | `WorkManager.ClaimTaskDirect` | Stack orders on back of queue instead of interrupting; hold Left Alt for vanilla interrupt; randomized 500–1250ms human-like settle delay when stacking behind a non-empty queue |
| 31 | `Patch_GetAvailActions_KeepClickable` | `CrewSim.GetAvailActionsForCO` | Re-enable all tooltip actions while a task runs (obsolete `IsClickable=!flag` gating) |

> **Removed in v4.4.0:** `Patch_UpdateCrewSkills_Throttle` (v4.3.0 #27) —
> `UpdateCrewSkills` sets static fields (`WeaponsSystem.fRangeModGunner`,
> `fFuelEfficiencyMod`) reflecting per-ship crew state. Throttling per-ship
> with a shared static timestamp meant only one ship per frame updated the
> statics, corrupting values for all others.

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
