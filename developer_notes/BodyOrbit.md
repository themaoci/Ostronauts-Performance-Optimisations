# BodyOrbit.cs

## `UpdateTime` recomputes parent chain every tick

**Location:** `UpdateTime` (lines 758, 1222, 1231, 1266).

**Issue:** `UpdateTime` walks `boParent` links to resolve orbital depth/position every tick for every body. The vanilla code recomputes the chain each call.

**Root cause:** No cached depth or parent-chain summary on `BodyOrbit`; the full parent walk is repeated every sim tick.

**Fix:** Cache `nDepth` (and optionally the root-to-body chain) as a field on `BodyOrbit`, recomputed only when `boParent` changes (e.g. in a `SetParent` method). `UpdateTime` then reads the cached depth for any hierarchical math.

**Mod patch:** v4.x parallelized `UpdateTime` across orbits via `Patch_StarSystemUpdate` (topological-depth batching), but this was **removed in v5.0.0** because sibling orbits at the same depth write to shared `boParent.dXReal`/`dTimeCalcLast` fields concurrently — a race condition that corrupted orbit positions. The vanilla sequential `UpdateTime` path now runs unchanged. The parent-chain recomputation cost remains unmitigated.
