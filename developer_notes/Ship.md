# Ship.cs

## `GetPeople` allocates a new `List<CondOwner>` on every call

**Location:** `GetPeople(bool)` (line 3655) and `GetPeople(CondTrigger, bool)` (line 3687).

**Issue:** Both overloads allocate a brand-new `List<CondOwner>` on every call (lines 3657, 3689). They're invoked from many per-tick and per-collision hot paths: `UpdateCrewSkills` (Ship.cs 4037, 4054), `ToggleCrewVisibility` (6405), `CollisionManager.ProcessCollision` (834, 835, 840, 841) and `ProcessCollision(Ship,BodyOrbit,...)` (1002, 1005) — multiple allocations per collision.

**Root cause:** `GetPeople` has no result-buffer reuse, so each caller pays a fresh heap allocation proportional to crew count.

**Fix:** Add a `GetPeople(List<CondOwner> buffer)` overload that clears and fills a caller-supplied buffer. Callers in `CollisionManager` and `UpdateCrewSkills` can hold a reused buffer field, eliminating the per-call allocation.
