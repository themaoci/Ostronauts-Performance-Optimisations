# Ostronauts Performance Optimizer

> **A BepInEx plugin for Ostranauts that eliminates per-frame allocations,
> parallelizes CPU-heavy loops, fixes memory leaks, and accelerates save/load
> — without changing gameplay behavior.**
>
> _I was testing a new military-grade AI system while making this mod._

[![Release](https://img.shields.io/badge/release-v4.3.0-blue)](../../releases)
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
  inflating reported memory by the full expansion size. Disabled — `ExpandHeap`
  is a no-op.
- `GC.TryStartNoGCRegion` was called per frame. On Boehm this suppresses
  collection, allocations accumulate, and the inevitable forced GC is larger
  than it would have been without the region. Removed.
- A `MemCeiling`-triggered full GC on a 3 GB Boehm heap takes 5–13 seconds.
  The ceiling is removed; Boehm self-manages with
  `GCSettings.LatencyMode = LowLatency` instead.
- All GC work uses `GC.Collect(0, GCCollectionMode.Optimized, false)` — a
  single generation-0 sweep — never a full `GC.Collect()`.

### Visual deferral bucket
- `CondOwner.UpdateManual` registers the CO into a round-robin bucket instead
  of running `RefreshAnim` + `UpdateStats` + `VisualizeOverlays` inline every
  frame.
- The bucket is processed in `LateUpdate` with per-CO intervals:
  selected = every frame, visible = every 3 frames, offscreen = every 10
  frames. A max of 64 visual updates per frame prevents bucket-sweep spikes.
- Note: the `Patch_UpdateManual_VisualDeferral` transpiler has an IL compile
  error and is currently **omitted from the patch registry**. The bucket system
  itself is wired up and will activate the moment that transpiler is fixed.

### FPS overlay + spike profiler
- A top-right `OnGUI` label shows `FPS / Worst-frame-ms / managed-MB / Sim`
  updated 4× per second.
- When a frame exceeds 200 ms, `SpikeProfiler` dumps the last 3 seconds of
  main-thread stack samples (aggregated by deepest method) to the BepInEx log.
  Sampling uses the Mono-internal `StackTrace(Thread, bool)` constructor via
  reflection — a background `ThreadPriority.Highest` thread takes 10 ms samples.
- Per-method timing for `AdvanceSim`, `UpdateICOs`, `EndTurn`, `GetMove2`,
  `GetWork`, `ParseCondLoot`, `Cleanup`, `UpdateStats`, `StarSystem.Update`,
  and orbit updates is aggregated every 5 seconds.

## Requirements

- Ostranauts (Steam)
- BepInEx x64 (tested with v5.4.21) at `<Ostranauts>\BepInEx\`

## Install

1. Download `OstronautsPerfOpt-v4.3.0.zip` from the
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
| 12 | `Patch_CleanupExpire` | `CondOwner.Cleanup` | Thread-safe expiry, TLS buffers |
| 13 | `Patch_UpdateICOsParallelPrepass` | `CrewSim.UpdateICOs` | Parallel UpdateStats + cleanup |
| 14 | `Patch_StarSystemUpdate_ToList` | `StarSystem.Update` | Reusable ship buffer |
| 15 | `Patch_CollisionManager_ToList` | `CollisionManager.CheckProjectileCollisions` | Reusable ship + projectile buffers |
| 16 | `Patch_CrewSim_CacheComponents` | `CrewSim.Update` | Cache Canvas + VacuumController |
| 17 | `Patch_ParallelLoad` | `LoadManager.LoadDataHandlerDelegates` | Parallel ship JSON parse |
| 18 | `Patch_DoLoadGame_BatchYields` | `CrewSim.LoadGame` | Batched coroutine yields |
| 19 | `Patch_UpdateICOs_NoCopy` | `CrewSim.UpdateICOs` | Pre-size `aTickersTemp` |
| 20 | `Patch_DebugLog_Suppress` | `Debug.Log(string)` | Suppress spam |
| 21 | `Patch_SaveGame_Threaded` | `LoadManager.SaveGame` | Force `useThreading=true` |
| 22 | `Patch_UpdateShip_DefaultGravBO` | `StarSystem.UpdateShip` | Cache default gravity BO |
| 23 | `Patch_SaveScreenShot_Skip` | `LoadManager.SaveScreenShot` | Skip `RenderTexture` capture |
| 24 | `Patch_SaveCrewPortraits_Skip` | `LoadManager.SaveCrewPortraits` | Skip portrait capture |
| 25 | `Patch_Sparks_CacheFlicker` | `Ship.Sparks` | Cache flicker; skip at 4× time |
| 26 | `Patch_DamageOverTime_Skip` | `Ship.DamageOverTime` | Skip when not due (300s gate) |
| 27 | `Patch_UpdateCrewSkills_Throttle` | `Ship.UpdateCrewSkills` | Throttle during time accel |

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
