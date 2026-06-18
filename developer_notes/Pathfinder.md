# Pathfinder.cs

## Per-frame `new List<CondOwner>`; `GetPath` allocs List+HashSet per search

**Location:** `Update()` (line 363) + `GetPath()` (line 652).

**Issue:** Per-frame allocation of `new List<CondOwner>()` when pathing through portal tiles, and `GetPath()` allocates `new List<PathResult>()` + `new HashSet<Tile>()` on every path search call.

**Root cause:** `Update()` creates a fresh `List<CondOwner> list = new List<CondOwner>()` inside the portal-wall branch which fires every frame a crew member walks near a closed door; `GetPath()` is called from `SetGoal2`/`CheckGoal` and allocates a List and HashSet each time.

**Fix:** Hoist the `List<CondOwner>` to a reusable field on `Pathfinder` (clear + reuse), and pool the `List<PathResult>` and `HashSet<Tile>` as reusable fields, clearing them at the start of each `GetPath` call.
