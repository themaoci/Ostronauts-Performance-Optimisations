# Ostronauts Performance Optimizer

A BepInEx plugin for **Ostranauts** that eliminates per-frame allocations,
parallelizes CPU-heavy loops, fixes memory leaks, and accelerates save/load
without changing gameplay behavior.

Built and tested against the live Steam build of Ostranauts (Unity 6000.3.10,
Mono/Boehm GC, .NET Standard 2.1).

## Features

- **GC elimination** — replaces `.Values.ToList()`, `aTickersTemp` copies, and
  per-frame `GetComponent` lookups with reusable buffers and caches.
- **Parallel simulation** — `BodyOrbit.UpdateTime`, `CondOwner.UpdateStats`,
  and `dictRecentlyTried` expiry run on `Parallel.ForEach` with a topological
  parent-before-child ordering for orbits.
- **Parallel loading** — ship JSON files parse across N threads during save
  load and initial mod data load. File-loaders stay on the main thread to keep
  Unity API calls safe.
- **Threaded saves** — every save (including manual) runs on a background
  thread. The base game only threads autosaves.
- **Save screenshot / portrait skip** — drops the main-thread
  `RenderTexture` + PNG encode freeze on save (100–500ms).
- **Debug.Log suppression** — silences the hundreds of `Debug.Log` calls the
  game issues during loading, music transitions, and NPC updates.
- **FFWD / time-accel throttling** — skips `Ship.Sparks` and throttles
  `Ship.UpdateCrewSkills` during 4x+ time acceleration.
- **Memory-leak fixes** — disables the Boehm-hostile `ExpandHeap`,
  `NoGCRegion`, and `MemCeiling` defaults that caused 10–13s full-GC pauses.
- **Built-in FPS overlay** — top-right corner, 25px from top.
- **Spike profiler** — dumps aggregated main-thread stack samples when a frame
  exceeds 200ms (uses Mono's `StackTrace(Thread, bool)` reflection ctor).

## Requirements

- Ostranauts (Steam)
- BepInEx x64 (tested with v5.4.21) installed at
  `<Ostranauts>\BepInEx\`

## Install

1. Download `OstronautsPerfOpt.zip` from the [latest release](../../releases).
2. Extract `OstronautsPerfOpt.dll` into
   `<Ostranauts>\BepInEx\plugins\`.
3. Launch the game. A `[OK] {PatchName}` line per applied patch confirms
   successful load.

No configuration file is generated — every optimization is hardcoded on.

## Build from source

```bash
dotnet build -c Release OstronautsPerfOpt.csproj
```

The `Deploy` MSBuild target copies the DLL to the default Steam install path.
Override with:

```bash
dotnet build -c Release /p:GameDir="D:\Games\Ostranauts"
```

## Patch inventory

| Patch | Target | Effect |
|---|---|---|
| `Patch_StarSystemUpdate_ToList` | `StarSystem.Update` | Reusable ship buffer |
| `Patch_CollisionManager_ToList` | `CollisionManager.CheckProjectileCollisions` | Reusable ship + projectile buffers |
| `Patch_CrewSim_CacheComponents` | `CrewSim.Update` | Cache `Canvas.scaleFactor` + `Audio_VacuumController` |
| `Patch_FirstOrDefault` | `UniqueList<CondOwner>.FirstOrDefault` | Direct indexer access |
| `Patch_SuppressInteractionLog` | `DataHandler.GetInteraction` | Bounded missing-key cache |
| `Patch_UpdateICOsParallelPrepass` | `CrewSim.UpdateICOs` | Parallel `UpdateStats` + cleanup expiry |
| `Patch_UpdateICOs_NoCopy` | `CrewSim.UpdateICOs` | Pre-size `aTickersTemp` |
| `Patch_CleanupExpire` | `CondOwner.Cleanup` | Thread-safe expiry with TLS buffers |
| `Patch_ParallelLoad` | `LoadManager.LoadDataHandlerDelegates` | Parallel ship JSON parse |
| `Patch_DoLoadGame_BatchYields` | `CrewSim.LoadGame` | Batched coroutine yields |
| `Patch_SaveGame_Threaded` | `LoadManager.SaveGame` | Force `useThreading=true` |
| `Patch_SaveScreenShot_Skip` | `LoadManager.SaveScreenShot` | Skip `RenderTexture` capture |
| `Patch_SaveCrewPortraits_Skip` | `LoadManager.SaveCrewPortraits` | Skip portrait capture |
| `Patch_UpdateShip_DefaultGravBO` | `StarSystem.UpdateShip` | Cache default gravity `BodyOrbit` |
| `Patch_Sparks_CacheFlicker` | `Ship.Sparks` | Cache `nFlickerAmount`; skip at 4x+ |
| `Patch_DamageOverTime_Skip` | `Ship.DamageOverTime` | Skip when not due (300s gate) |
| `Patch_UpdateCrewSkills_Throttle` | `Ship.UpdateCrewSkills` | Throttle during time accel |
| `Patch_DebugLog_Suppress` | `Debug.Log(string)` | Suppress spam |
| `Patch_StarSystemUpdate` | `StarSystem.Update` | Parallel orbit `UpdateTime` |
| Profiling patches | `AdvanceSim`, `UpdateICOs`, `EndTurn`, `GetMove2`, `GetWork`, `ParseCondLoot`, `Cleanup`, `UpdateStats` | Timing / allocation observation |

## License

MIT — see [LICENSE](LICENSE).

## Disclaimer

Not affiliated with Ostranauts developers. Use at your own risk. Back up your
saves before enabling.
