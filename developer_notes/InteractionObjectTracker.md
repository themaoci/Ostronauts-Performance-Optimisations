# InteractionObjectTracker.cs

**Source path:** `Ostranauts.Core/InteractionObjectTracker.cs`

## `Keys.ToArray()` + LINQ `ToDictionary` rebuild on trim

**Location:** `CheckTrackingSize()` (line 71) + `RemoveNullsFromDictionary()` (line 66).

**Issue:** `Keys.ToArray()` allocates a full array copy of all keys whenever the dict hits 500 entries, then removes half; `RemoveNullsFromDictionary` uses LINQ `.Where().ToDictionary()` to rebuild the entire dictionary.

**Root cause:** `CheckTrackingSize` fires every time a new interaction is tracked and count >= 500, allocating an array and removing 250 entries (O(n) removals); `RemoveNullsFromDictionary` creates a brand-new `Dictionary` via LINQ which is expensive and called whenever a null interaction is released.

**Fix:** Use a `Queue<Guid>` or track insertion order to remove oldest entries without array allocation, and replace the LINQ `ToDictionary` with an in-place `RemoveWhere` pattern or iterate-and-remove.

**Mod patch:** `Patch_InteractionObjectTracker_RemoveNulls` (Patches/Optimization/OptimizationPatches.cs) replaces the LINQ `ToDictionary` rebuild with in-place null-key removal using a reusable TLS `List<Guid>` buffer. The `Keys.ToArray()` allocation in `CheckTrackingSize` is not yet patched.
