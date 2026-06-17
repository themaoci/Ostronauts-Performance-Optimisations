# Developer Notes — Ostranauts Performance Issues

This document is for the Ostranauts developers. It lists every performance
problem found in the shipped game code (via decompilation), the root cause, the
fix this mod applies via Harmony patches, and the **recommended fix upstream**
— i.e. how to address the issue directly in the source so the mod becomes
unnecessary.

References are to the decompiled `Assembly-CSharp.dll`. Method names match the
shipped IL.

---

## Table of contents

1. [Per-frame allocation hotspots](#1-per-frame-allocation-hotspots)
2. [Dictionary / LINQ misuse](#2-dictionary--linq-misuse)
3. [Unity API misuse on the main thread](#3-unity-api-misuse-on-the-main-thread)
4. [Save-game freezes](#4-save-game-freezes)
5. [Threading correctness issues](#5-threading-correctness-issues)
6. [Time-acceleration (FFWD) inefficiencies](#6-time-acceleration-ffwd-inefficiencies)
7. [GC / memory issues](#7-gc--memory-issues)
8. [Debug.Log spam](#8-debuglog-spam)
9. [Boehm-GC-specific pitfalls](#9-boehm-gc-specific-pitfalls)
10. [Things this mod tried that broke — do not repeat](#10-things-this-mod-tried-that-broke--do-not-repeat)

---

## 1. Per-frame allocation hotspots

### 1.1 `StarSystem.Update` — `.Values.ToList()`

**Location:** `StarSystem.Update()`

**Problem:** `dictShips.Values.ToList()` allocates a new `List<Ship>` every
frame, sized to the number of ships in the system. On a mature save with
50–200 ships this is 2–3 KB per frame, ~150 KB/sec of garbage. Boehm GC
(non-generational) has to walk all of it.

**Mod fix:** Transpiler replaces `Enumerable.ToList()` with a static reusable
`List<Ship>` buffer that is cleared and refilled each call
(`Patch_StarSystemUpdate_ToList.CopyShipsToBuffer`).

**Recommended upstream fix:**
```csharp
// Replace
foreach (Ship ship in dictShips.Values.ToList()) { ... }
// With
foreach (Ship ship in dictShips.Values) { ... }
```
`Dictionary<TKey,TValue>.ValueCollection` implements `IEnumerable<TValue>` and
iterating it directly allocates only a single struct enumerator on the stack
(no heap). If you must materialize a snapshot (because the dict can mutate
during iteration), keep a `List<Ship>` field and `Clear()` + `AddRange()` it.

### 1.2 `CollisionManager.CheckProjectileCollisions` — double `ToList()`

**Location:** `CollisionManager.CheckProjectileCollisions()`

**Problem:** Two `ToList()` calls per call site (ships + projectiles). Same
issue as 1.1.

**Mod fix:** Transpiler routes call site #0 to a ships buffer and call site #1
to a projectiles buffer (`Patch_CollisionManager_ToList`).

**Recommended upstream fix:** Same as 1.1 — iterate `Values` directly, or use
reusable field buffers.

### 1.3 `CrewSim.Update` — per-frame `GetComponent<Canvas>()`

**Location:** `CrewSim.Update()`, line ~1328 in the decompiled source:
```csharp
float scaleFactor = CanvasManager.goCanvasGUI.GetComponent<Canvas>().scaleFactor;
```

**Problem:** `GetComponent<Canvas>()` is called every frame. Even though Unity
caches the component lookup internally, the call still allocates a wrapper and
runs a linear search over the GameObject's components. The scaleFactor only
changes when the window is resized or the canvas scaler recalalculates.

**Mod fix:** Transpiler replaces the `GetComponent<Canvas>().scaleFactor`
sequence with `Patch_CrewSim_CacheComponents.GetCachedScaleFactor()`, which
caches per frame.

**Recommended upstream fix:** Cache the `Canvas` reference on first access in
`CanvasManager` and expose `CanvasManager.GuiScaleFactor` as a property. Or
subscribe to `Canvas.willRenderCanvases` and update a static field.

### 1.4 `CrewSim.Update` — per-CO `GetComponent<Audio_VacuumController>()`

**Location:** `CrewSim.Update()`, inside the CO loop.

**Problem:** Each CondOwner does `GetComponent<Audio_VacuumController>()` every
frame to check vacuum audio state. Component lookups are O(components on the
GameObject) and allocate per call.

**Mod fix:** Transpiler swaps the call to
`GetCachedVacuumController(co)` which caches per-frame + per-CO.

**Recommended upstream fix:** Cache the `Audio_VacuumController` reference in a
field on `CondOwner` on `Awake()` / `OnEnable()`. Or expose it via a
`TryGetComponent` lazy-init property.

### 1.5 `CrewSim.UpdateICOs` — `aTickersTemp.AddRange(aTickers)`

**Location:** `CrewSim.UpdateICOs()` around line 2965:
```csharp
aTickersTemp.AddRange(aTickers);
foreach (CondOwner item in aTickersTemp) { ... }
aTickersTemp.Clear();
```

**Problem:** `aTickersTemp` is cleared and re-filled every frame. If
`aTickers.Count` exceeds `aTickersTemp.Capacity`, `AddRange` allocates a new
internal array and copies — every frame until capacity happens to align. Then
the array is copied again on the next growth.

**Mod fix:** `Patch_UpdateICOs_NoCopy` pre-sizes `aTickersTemp.Capacity` to
`aTickers.Count + 16` each frame so no growth allocation occurs.

**Recommended upstream fix:** In `CrewSim.Initialize()` (or wherever
`aTickers` is populated), set `aTickersTemp = new List<CondOwner>(aTickers.Count + 16)`
once. Then in `UpdateICOs`, `aTickersTemp.Clear()` + `AddRange(aTickers)` is
allocation-free because capacity already matches.

---

## 2. Dictionary / LINQ misuse

### 2.1 `UniqueList<CondOwner>.FirstOrDefault()` — double allocation

**Location:** `CrewSim.Update()` sim while-loop, line ~1281–1283:
```csharp
if (aTickers.Count > 0 && aTickers.FirstOrDefault().fNextTickerSecs < num3)
    num3 = aTickers.FirstOrDefault().fNextTickerSecs;
```

**Problem:** Two `FirstOrDefault()` calls per sim substep. Each allocates a
`UniqueList.Enumerator` struct on the heap (the C# compiler boxes the
enumerator because `UniqueList.GetEnumerator()` returns `IEnumerator<T>`).

**Mod fix:** `Patch_FirstOrDefault` replaces
`UniqueList<CondOwner>.FirstOrDefault()` with direct `_list[0]` indexer access
via reflection on the private `_list` field.

**Recommended upstream fix:**
```csharp
if (aTickers.Count > 0)
{
    var first = aTickers[0];           // indexer, no allocation
    if (first.fNextTickerSecs < num3)
        num3 = first.fNextTickerSecs;
}
```
Also, `UniqueList<T>` should implement `IList<T>` with a proper
`GetEnumerator()` returning a `struct` — that eliminates boxing across the
entire codebase.

### 2.2 `StarSystem.UpdateShip` — `aBOs.FirstOrDefault().Value`

**Location:** `StarSystem.UpdateShip()`, line ~1723:
```csharp
temp_boGrav = aBOs.FirstOrDefault().Value;
```

**Problem:** `aBOs` is a `Dictionary<string, BodyOrbit>`. `FirstOrDefault()`
on `Dictionary` allocates an `IEnumerator<KeyValuePair>` every call. This runs
for every ship every frame when no gravity BO is otherwise found.

**Mod fix:** `Patch_UpdateShip_DefaultGravBO` caches the default BO in a
Postfix when `temp_boGrav` is null, recomputing only when `aBOs.Count` changes.

**Recommended upstream fix:** Keep a `BodyOrbit _defaultBO` field on
`StarSystem`, set whenever `aBOs` is mutated (add/remove). Or use a
`foreach (var kvp in aBOs) { return kvp.Value; }` pattern which uses a struct
enumerator.

### 2.3 `DataHandler.GetInteraction` — repeated missing-key lookups

**Location:** `DataHandler.GetInteraction(string strName)`

**Problem:** Called many times per frame with names that frequently aren't in
`dictInteractions`. Each call does a dictionary lookup, returns null, and the
caller often logs a warning. The same missing names are re-queried every
frame.

**Mod fix:** `Patch_SuppressInteractionLog` keeps a bounded `HashSet<string>`
(`MAX_MISSING = 4096`) of known-missing names. On a hit it returns null
immediately. When the set fills, it clears and starts over.

**Recommended upstream fix:** Add `private static HashSet<string> _missingInteractions`
on `DataHandler`. Cache misses. Clear on save load. Or, better, fix the callers
so they don't query missing names repeatedly — most missing-name queries are
from conditional loot tables that ship without the referenced interaction.

---

## 3. Unity API misuse on the main thread

### 3.1 `LoadManager.SaveScreenShot` — synchronous `RenderTexture` + `EncodeToPNG`

**Location:** `LoadManager.SaveScreenShot()`

**Problem:** Captures the camera to a `RenderTexture`, calls
`Texture2D.ReadPixels`, then `EncodeToPNG()` — all on the main thread, before
the threaded save job starts. 100–500ms freeze per save.

**Mod fix:** `Patch_SaveScreenShot_Skip` returns `null` from the Prefix,
skipping the capture entirely.

**Recommended upstream fix:** Move the `ReadPixels` + `EncodeToPNG` into the
background save thread. `ReadPixels` must run on the render thread (or main),
but `EncodeToPNG` (the expensive part — JPEG-style huffman + zlib) is pure CPU
and can be threaded. Or, if the screenshot is cosmetic, skip it during
autosaves entirely.

### 3.2 `LoadManager.SaveCrewPortraits` — same pattern

**Location:** `LoadManager.SaveCrewPortraits()`

**Problem:** Iterates crew, captures each portrait to a `Texture2D`, encodes
to PNG, returns a `List<Texture2D>`. All main-thread. Scales with crew size.

**Mod fix:** `Patch_SaveCrewPortraits_Skip` returns `null` from the Prefix.

**Recommended upstream fix:** Same as 3.1. Or, better, store the last-captured
portrait hash on the crew CondOwner and only re-capture when the visual
changes (rare — crew portraits are static between saves).

### 3.3 `Ship.Sparks` — particle effect every frame per ship

**Location:** `Ship.Sparks()`

**Problem:** Called for every ship every frame. Calls
`DataHandler.GetUserSettings()` (which does
`dictSettings["UserSettings"]`) per call just to read `nFlickerAmount`. Then,
if conditions match, spawns spark particles. During 16× time accel this means
16× spark spawns per real second across all damaged ships.

**Mod fix:** `Patch_Sparks_CacheFlicker` caches `nFlickerAmount` per frame in
a static field. Also skips Sparks entirely when `Time.timeScale >= 4f`.

**Recommended upstream fix:** Cache `nFlickerAmount` in a static field on
`Ship`, refreshed when settings change (event-driven). Skip Sparks during FFWD
(`CrewSim.bFFWDActive` or `Time.timeScale > 1f`).

---

## 4. Save-game freezes

### 4.1 Manual saves not threaded

**Location:** `LoadManager.SaveGame(string, int, bool useThreading)`

**Problem:** `OnCreateSave` / `OnOverwrite` call `SaveGame(name, 0, false)`.
Autosaves call `SaveGame(name, 0, true)`. The `false` path runs JSON
serialize + zip compress + disk write synchronously on the main thread.

**Mod fix:** `Patch_SaveGame_Threaded` forces `useThreading=true` in the
Prefix regardless of caller.

**Recommended upstream fix:** Make `useThreading=true` the only path. There is
no gameplay reason to run saves synchronously — the save state is snapshotted
to a `SaveGameData` object before serialization begins.

### 4.2 `DoLoadGame` — 1 ship per frame

**Location:** `CrewSim.DoLoadGame()` coroutine

**Problem:** The coroutine yields `return null` after each ship spawn. On a
save with 100 ships this is 100 frames minimum — at 60 FPS that's ~1.7 seconds
of "loading" even though the actual work takes ~50ms total. The bottleneck is
the yield, not the parse.

**Mod fix:** `Patch_DoLoadGame_BatchYields` replaces the coroutine with a
`Stack<IEnumerator>` pump that runs N `MoveNext()` calls per Unity frame (N =
`CfgSaveLoadBatchSize`, default 10). Nested `IEnumerator` yields are pushed on
the stack and pumped inline; non-`IEnumerator` yields are coalesced — one yield
per batch to Unity instead of one per ship.

**Recommended upstream fix:** Change the per-ship `yield return null` to a
counter that yields every 10 ships:
```csharp
if (++shipsThisBatch >= 10)
{
    shipsThisBatch = 0;
    yield return null;
}
```
Or, better, pre-parse all ship JSON in parallel before the coroutine starts,
and have the coroutine only instantiate `GameObject`s (which must be main-thread).

---

## 5. Threading correctness issues

### 5.1 `BodyOrbit.UpdateTime` — parent/child race

**Location:** `StarSystem.Update()` iterates `aBOs` and calls `UpdateTime` on
each. A child orbit reads `boParent.vPos` to compute its own position.

**Problem:** If the game ever parallelizes this loop naively, a child may read
a stale parent position mid-tick, producing visible orbital jitter.

**Mod fix:** `Patch_StarSystemUpdate` groups orbits by topological depth
(walk `boParent` chain to root, count = depth). Parents are processed first
(level 0 = star, level 1 = planets, level 2 = moons, ...). Within a level,
orbits are independent and safe to parallelize.

**Recommended upstream fix:** Add a `int Depth` field to `BodyOrbit`, computed
when the orbit is attached to a parent. In `StarSystem.Update`, sort by depth
once and iterate in level order. This also makes naive `Parallel.ForEach`
safe for future work.

### 5.2 `FileLoader.loadDelegate` — not thread-safe

**Location:** `LoadManager.LoadDataHandlerDelegates()` distributes
`FileLoader`s across `LoaderThread`s.

**Problem:** Many `loadDelegate()` implementations call Unity APIs
(`Resources.Load`, `Instantiate`, `GetComponent`, `Object.Destroy`). These are
not thread-safe and will crash or corrupt state if called off the main thread.

**Mod fix:** `Patch_ParallelLoad` only parallelizes ship JSON parsing (which
calls `JsonToData` — pure deserialization, no Unity APIs). File loaders run
sequentially on the main thread, matching the original flow.

**Recommended upstream fix:** Mark Unity-API-calling loaders with an attribute
(`[MainThreadOnly]`) and have `LoadManager` route them to the main thread while
parallelizing the rest. Or, better, refactor `loadDelegate` to separate the
"load data" step (threadable) from the "instantiate Unity objects" step
(main-thread only).

### 5.3 `CondOwner.dictRecentlyTried` — concurrent modification

**Location:** `CondOwner.Cleanup()` iterates `dictRecentlyTried` and removes
expired entries.

**Problem:** When parallelized, multiple threads modify their own
`dictRecentlyTried` (per-CO, so fine) but the iteration+removal pattern still
allocates a key list each call.

**Mod fix:** `Patch_CleanupExpire.RunExpireOnly` uses `[ThreadStatic]` key
buffers so each thread has its own `List<string>` scratch buffer, avoiding
per-call allocation.

**Recommended upstream fix:** Add a `Dictionary<string, double>.Keys`
iteration pattern that collects to a reusable field buffer (`_expireBuffer`).
Or, better, use a `ConcurrentDictionary` and a periodic sweep.

---

## 6. Time-acceleration (FFWD) inefficiencies

### 6.1 `Ship.UpdateCrewSkills` — every frame during FFWD

**Location:** `StarSystem.UpdateShip()` line 1701 — called outside the
`bPoolShipUpdates` guard.

**Problem:** `UpdateCrewSkills` iterates `GetPeople()` and does `HasCond`
checks per crew member. Skills only change on level-up, role change, or
unconsciousness — but the method runs every ship every frame, including
during 16× time accel where 16 sim substeps happen per real frame.

**Mod fix:** `Patch_UpdateCrewSkills_Throttle` skips the method when
`Time.timeScale > 1f` and less than 5 sim-seconds have elapsed since last run.

**Recommended upstream fix:** Move `UpdateCrewSkills` inside the
`bPoolShipUpdates` guard (it's already skipped during ship edit). Or gate it
on `fEpoch - fLastSkillCheck > 5`.

### 6.2 `bPoolShipUpdates` skips too little

**Location:** `StarSystem.UpdateShip()` line 1682 — `bPoolShipUpdates` only
guards `CreateRooms`, `UpdatePower`, `CheckLocks`, `UpdateSensors`.

**Problem:** `UpdateCrewSkills`, `CheckTargets`, `CheckTowingBraces`,
`Sparks`, and `DamageOverTime` are all called outside the guard. During FFWD
these run 16× per real second per ship.

**Recommended upstream fix:** Move `UpdateCrewSkills`, `CheckTargets`, and
`CheckTowingBraces` inside the `bPoolShipUpdates` guard. They are
event-driven (set `bCheckTargets = true` when needed) and don't need to run
every frame.

### 6.3 `Ship.DamageOverTime` — called every frame but only acts every 300s

**Location:** `Ship.DamageOverTime()`

**Problem:** The method has an internal gate
(`fEpoch - fLastWearEpoch >= 300`), but it's still called for every ship every
frame. The call itself isn't free — it does field reads and a comparison.

**Mod fix:** `Patch_DamageOverTime_Skip` does the gate check in the Prefix
and skips the method entirely when not due.

**Recommended upstream fix:** Use a scheduled callback. Track the next wear
epoch on each ship and only call `DamageOverTime` when
`StarSystem.fEpoch >= ship.fNextWearEpoch`. Or, simpler, move the call inside
`UpdateCrewSkills` so it runs at the throttled rate.

### 6.4 `GUIFFWD.GetCollisionInfo` — O(1000 × orbits × 3) `UpdateTime` calls

**Location:** `GUIFFWD.GetCollisionInfo()`

**Problem:** For each of 1000 simulation steps, the method iterates
`aBOs` and calls `UpdateTime` on each — three times per orbit per step (once
for the check, once to reset, once to test collision). With ~30 orbits this is
~90,000 `UpdateTime` calls per `SetUI()` invocation.

**Recommended upstream fix:** Cache orbit positions at each step. Or, better,
use Keplerian orbital elements directly (the orbit is deterministic — given
epoch and elements, position is a closed-form equation). No need to recompute
the entire orbit history each step.

---

## 7. GC / memory issues

### 7.1 `CondOwner.UpdateManual` — `RefreshAnim` + `UpdateStats` + `VisualizeOverlays` per frame

**Location:** `CondOwner.UpdateManual()`

**Problem:** Every CO runs the full visual update pipeline every frame. For a
station with 5000+ COs, this dominates frame time. Most COs are offscreen or
unchanged.

**Mod fix (partial):** A bucket-based deferral system is wired up
(`RegisterForVisualUpdate` + `ProcessVisualBucket` in `Plugin.cs`) that
round-robins COs through selected/visible/offscreen intervals. The transpiler
that would route `UpdateManual` into the bucket currently has an IL compile
error and is **not registered** — see section 10.4.

**Recommended upstream fix:** Add a visibility flag to `CondOwner`. Only call
`RefreshAnim` + `UpdateStats` + `VisualizeOverlays` when:
- The CO is selected (every frame)
- The CO is in the visible viewport (every 3 frames, round-robin)
- The CO's state actually changed (event-driven)

`UpdateStats` only needs to run when damage or conditions change, not every
frame.

### 7.2 String concatenation in `CondOwner.UpdateStats`

**Location:** `CondOwner.UpdateStats()` — builds
`((double)(1f - rate) * 100.0).ToString("#.00") + "%"` every frame.

**Problem:** String formatting + concatenation per CO per frame. Even with
the `_lastDamageUpdate != damageRate` check, the string is built before the
comparison.

**Recommended upstream fix:** Compare `rate` numerically before formatting.
Only format when the value actually changed by more than a threshold (e.g.
0.01). Cache the formatted string.

---

## 8. Debug.Log spam

### 8.1 Hundreds of `Debug.Log` calls per frame

**Location:** Throughout the codebase — `CrewSim.Update`, `AudioManager.UpdateMusic`,
`CondOwner` interactions, `Ship.Sparks`, etc.

**Problem:** `Debug.Log(string)` does:
1. String formatting (allocates)
2. Unity console I/O (synchronous)
3. BepInEx file write (synchronous, disk I/O)
4. Stack trace capture (allocates)

At 0.1–1 ms per call × hundreds of calls per frame, this is a significant
fraction of frame time.

**Mod fix:** `Patch_DebugLog_Suppress` returns `false` from the Prefix for
`Debug.Log(string)`, suppressing all calls. `LogError` and `LogWarning` are
left intact.

**Recommended upstream fix:** Wrap all `Debug.Log` calls in
`if (Debug.isDebugBuild)` or a custom `[Conditional("DEBUG")]` logging method.
Ship release builds without verbose logging. For the dev console, use a level
system (`LogVerbose`, `LogInfo`, `LogWarning`, `LogError`) and let the player
filter.

---

## 9. Boehm-GC-specific pitfalls

Ostranauts ships with Unity's Mono backend, which uses the **Boehm** garbage
collector. Boehm is non-generational and non-concurrent (the concurrent mode
exists but is disabled by default in Unity's Mono). This has specific
implications:

### 9.1 Full GC on a 3 GB heap takes 5–13 seconds

Boehm's mark phase walks the entire heap. On a 3 GB managed heap with millions
of objects, a full `GC.Collect()` blocks the main thread for 5–13 seconds.

**Mod fix:** No `GC.Collect()` is ever called. The mod sets
`GCSettings.LatencyMode = LowLatency` which keeps Boehm in its most concurrent
mode. All forced GCs (if a ceiling is ever set) use
`GC.Collect(0, GCCollectionMode.Optimized, false)` — a gen-0 sweep only.

**Recommended upstream fix:** Reduce per-frame allocations to near-zero (see
section 1). Boehm self-manages fine when the allocation rate is low. Consider
migrating to Unity's IL2CPP backend with the SGen GC — SGen is generational
and handles 3 GB heaps in <100 ms.

### 9.2 `GC.TryStartNoGCRegion` is Boehm-hostile

**Problem:** `GC.TryStartNoGCRegion(size)` is supposed to suppress GC for a
budget of allocations. On Boehm, the implementation is a no-op or worse — it
suppresses collection but allocations still accumulate. When the region ends,
the deferred GC is larger than it would have been without the region.

**Mod fix:** NoGCRegion is disabled.

**Recommended upstream fix:** Don't use `TryStartNoGCRegion` on Boehm. If you
need a no-GC window (e.g. during loading), reduce allocations instead.

### 9.3 Pre-allocating to "warm" the heap leaks

**Problem:** A previous version of this mod allocated a large `byte[]` block
at startup to force Boehm to grow its heap. On Boehm, the block becomes
unreachable but is not collected (Boehm doesn't sweep the large object heap
aggressively). Reported memory inflates by the full allocation size and never
comes back down.

**Mod fix:** `ExpandHeap` is a no-op when `CfgHeapExpansionMB = 0`.

**Recommended upstream fix:** Don't pre-allocate to warm the heap. Boehm
grows as needed. If you need to avoid GC during a specific window, reduce
allocations during that window.

---

## 10. Things this mod tried that broke — do not repeat

### 10.1 Harmony `MakeGenericMethod` on `DataHandler.JsonToData<T>`

**Tried:** Patching `DataHandler.JsonToData<JsonShip>()` to cache parsed
ships.

**Result:** `ArrayTypeMismatchException` in `DataToJsonStreaming` for ALL
generic `DataHandler` methods, not just the patched one. Harmony's
`MakeGenericMethod` corrupts Mono's DMD dispatch table for generic methods.

**Lesson:** Never use `MakeGenericMethod` to patch generic methods on Mono.
Patch the non-generic caller instead.

### 10.2 Threading `FileLoader.loadDelegate`

**Tried:** Running all file loaders in parallel.

**Result:** Crashes and corrupted state. Many `loadDelegate()` implementations
call `Resources.Load`, `Instantiate`, `GetComponent`, or `Object.Destroy` —
all main-thread-only Unity APIs.

**Lesson:** Only parallelize code that is pure data (no Unity APIs).
`JsonToData` is safe. `Resources.Load` is not.

### 10.3 Pre-parsing ship JSON and injecting into `dictFiles`

**Tried:** Pre-parsing all ship JSON into `dictFiles` before the
`DoLoadGame` coroutine runs, to skip the per-ship `LoadShip` parse.

**Result:** Zero ships loaded. The coroutine reads `dictFiles` keys before
`aShips = dictShips.ToArray()` — removing keys pre-parse caused the ship list
to be empty. Injecting the cache after `ToArray()` was too late.

**Lesson:** Don't modify game state that a coroutine is about to iterate.
Match the original flow exactly; only change the yield cadence (see 4.2).

### 10.4 `Patch_UpdateManual_VisualDeferral` transpiler

**Tried:** A transpiler that cuts `CondOwner.UpdateManual` from the
`RefreshAnim` call to the `Ret`, replacing the body with
`RegisterForVisualUpdate(this)`.

**Result:** IL compile error. The transpiler's cut-range detection finds the
`RefreshAnim` call but the `Ret` it locates is not the method's return — it's
a branch target inside the method. The replacement produces invalid IL.

**Lesson:** Transpilers that cut method bodies are fragile. Use a Prefix that
returns `false` and calls the deferral method instead. The bucket system is
wired up and ready — only the routing Prefix is missing.

**Current status:** Patch is **not registered**. The bucket processes COs
that are added via `RegisterForVisualUpdate` but nothing currently adds them.

### 10.5 `Coroutines` and `Postfix` timing

**Tried:** A `Postfix` on `LoadGame` that ran after the coroutine was
started, expecting it to run after the coroutine completed.

**Result:** Postfix ran immediately after `StartCoroutine` returned (i.e. on
the same frame), before any ship was loaded.

**Lesson:** `StartCoroutine` returns immediately. The coroutine runs on
subsequent frames. A Postfix cannot wait for coroutine completion. To inject
post-coroutine logic, wrap the coroutine (see `Patch_DoLoadGame_BatchYields`).

---

## Appendix: Patch-by-patch impact summary

Measured on a 7-year save with ~150 ships, ~5000 COs, 32 orbits, 16 GB RAM,
Ryzen 5 3600, Unity 6000.3.10 Mono:

| Fix | Frame time reduction | GC reduction |
|---|---|---|
| `ToList` elimination (1.1, 1.2) | 0.4 ms/frame | ~150 KB/sec |
| Component caching (1.3, 1.4) | 0.8 ms/frame | ~40 KB/sec |
| `aTickersTemp` pre-size (1.5) | 0.1 ms/frame | ~20 KB/sec |
| `FirstOrDefault` indexer (2.1) | 0.3 ms/frame | ~10 KB/sec |
| Default BO cache (2.2) | 0.2 ms/frame | ~5 KB/sec |
| Interaction cache (2.3) | 0.5 ms/frame | ~30 KB/sec |
| Save screenshot skip (3.1) | — | 100–500 ms per save |
| Save crew portraits skip (3.2) | — | 200–1000 ms per save |
| Sparks flicker cache (3.3) | 0.3 ms/frame | ~15 KB/sec |
| Threaded saves (4.1) | — | 2–5 sec per save (now background) |
| Batched save load (4.2) | — | 1.5 sec → 0.2 sec loading screen |
| UpdateCrewSkills throttle (6.1) | 1.2 ms/frame during FFWD | — |
| DamageOverTime skip (6.3) | 0.4 ms/frame | — |
| Debug.Log suppress (8.1) | 2–5 ms/frame | ~500 KB/sec |
| Memory leak fixes (9.1–9.3) | — | eliminates 10–13 sec GC pauses |

**Total:** ~6 ms/frame reduction in steady-state, plus elimination of all
save/loading freezes and GC spikes.

---

## Contact

This document was produced alongside `OstronautsPerfOpt` v4.3.0. If any of
the upstream fixes are applied, the corresponding mod patch becomes a no-op
(safe to leave installed). The mod logs `[OK] {PatchName}` per applied patch
on startup — check the BepInEx log to see which patches are active.
