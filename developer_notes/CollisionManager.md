# CollisionManager.cs

## `.ToList()` + O(n) `Contains`/`IndexOf` in collision hot path

**Location:** `CheckProjectileCollisions()` (lines 127, 129); `CheckCollisions()` (lines 269–270, 296, 344, 457).

**Issue:** Both hot-path methods allocate new lists every call via `.ToList()` on dictionary values and via LINQ `select...ToList()` for docked-ship IDs. `CheckCollisions` then does `list.Contains(value2.strRegID)` (line 296) and `aProxIgnores.IndexOf(...)` (lines 344, 457) — O(n) lookups inside the per-ship loop, making the whole method O(n²) in ship count.

**Root cause:** Collections are materialized into lists instead of iterated directly, and docked-ship membership is checked with `List.Contains`/`IndexOf` instead of a `HashSet`.

**Fix:** Iterate `dictShips.Values`/`dictProjectiles.Values` directly; build a `HashSet<string>` of docked-ship RegIDs once at the top of `CheckCollisions` for O(1) membership tests; replace `aProxIgnores.IndexOf(...) < 0` with `HashSet.Contains`.

## `Debug.Log` in the projectile-collision hot path

**Location:** `CheckProjectileCollisions()` (lines 181, 233).

**Issue:** `Debug.Log` with string concatenation runs in the projectile-collision hot path (called per-tick from `StarSystem.Update`); these execute even in release builds and allocate strings on every hit/miss.

**Fix:** Guard with `if (AIShipManager.ShowDebugLogs)` (already used elsewhere in the same file, e.g. lines 597, 880) so the `Debug.Log` and its string concatenation only execute when explicitly enabled.

**Mod patches:** `Patch_CollisionManager_ToList` replaces both `.ToList()` calls with reusable buffers. `Patch_DebugLog_Suppress` suppresses the `Debug.Log` info spam; `Patch_DebugLogWarning_Passthrough` / `Patch_DebugLogError_Passthrough` re-emit warnings/errors through BepInEx.

**Also:** `Patch_CheckCollisions_DockedRegIDs` (Patches/Optimization/OptimizationPatches.cs) replaces the LINQ `select...ToList` in `CheckCollisions` with a reusable TLS `List<string>` buffer.
