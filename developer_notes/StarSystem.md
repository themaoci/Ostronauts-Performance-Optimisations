# StarSystem.cs

## `Update` allocates `dictShips.Values.ToList()` every frame

**Location:** `Update()` (line 860).

**Issue:** `foreach (Ship item in dictShips.Values.ToList())` allocates a new `List<Ship>` from the dictionary values every Update tick (called every frame from `CrewSim.Update`), creating per-frame GC pressure proportional to ship count.

**Root cause:** `.ToList()` materializes a copy so `dictShips.Remove(...)` (line 909) can run during iteration, but the copy is paid for every frame even when nothing is removed.

**Fix:** Iterate `dictShips.Values` directly and queue removals into the already-existing `temp_aDestroyed` list (cleared at line 912), removing after iteration — no `ToList` needed. `Dictionary.ValueCollection` enumerates with a struct enumerator (zero heap allocation).

## Additional `ToList()` calls outside `Update`

**Location:** Lines 1047 (`ship.GetPeople(...).ToList()`) and 1066 (`dictShips.Values.ToList()`).

**Issue:** Two more `ToList()` calls in methods that run on the per-frame hot path.

**Fix:** Iterate the underlying collections directly, or use reusable field buffers cleared per call.

**Mod patch:** `Patch_StarSystemUpdate_ToList` replaces the `Update` `.ToList()` with a reusable `List<Ship>` buffer. The lines 1047 and 1066 `ToList()` calls are not yet patched.
