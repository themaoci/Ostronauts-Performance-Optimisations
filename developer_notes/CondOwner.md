# CondOwner.cs

## Double `GetComponent` in `GetJobActions`

**Location:** `GetJobActions()` (lines 8686–8688).

**Issue:** `GetComponent<COOverlay>()` is called twice — once to null-check, once to use the result — doubling the cost of every call.

**Fix:** `var overlay = GetComponent<COOverlay>(); if (overlay != null) return overlay.GetJobActions(strJobType);`

## `Cleanup` allocates `new List<string>(dictRecentlyTried.Keys)` per call

**Location:** `Cleanup()` (line 1426).

**Issue:** Every 2 sim-seconds per CondOwner, `Cleanup` copies the entire `dictRecentlyTried.Keys` collection into a fresh `List<string>` just to find and remove a single expired key. With many CondOwners this is a steady per-frame allocation source (the "A.Oth" bucket in the perf profiler).

**Fix:** Iterate `dictRecentlyTried` directly with a reusable thread-local `List<string>` scratch buffer: collect expired keys into the buffer, then remove them. Never materialize the full keys collection. (This is what the mod's `Patch_CleanupExpire` transpiler does.)
