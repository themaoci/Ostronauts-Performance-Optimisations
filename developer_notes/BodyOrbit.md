# BodyOrbit.cs

## `UpdateTime` recomputes parent chain every tick

**Location:** `UpdateTime` (lines 758, 1222, 1231, 1266).

**Issue:** `UpdateTime` walks `boParent` links to resolve orbital depth/position every tick for every body. The mod's `Patch_StarSystemUpdate` already caches depth per-body in a `Dictionary<BodyOrbit, int>` to enable topological batching, but the vanilla code recomputes the chain each call.

**Root cause:** No cached depth or parent-chain summary on `BodyOrbit`; the full parent walk is repeated every sim tick.

**Fix:** Cache `nDepth` (and optionally the root-to-body chain) as a field on `BodyOrbit`, recomputed only when `boParent` changes (e.g. in a `SetParent` method). `UpdateTime` then reads the cached depth for any hierarchical math.
