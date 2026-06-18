# AIShipManager.cs

## `RunAIQueue` allocates two `List<AIShip>` per frame

**Location:** `RunAIQueue` (line 1864), called from `Update` (line 241).

**Issue:** Called every frame; whenever the queue is empty it allocates two brand-new `List<AIShip>` instances via `GetShipsOfTypeForRegion(strATCLast, AIType.All)` and `GetShipsOfTypeForRegion("INTERREGIONAL", AIType.All)`, then `AddRange`s into a third — pure per-frame GC pressure during normal gameplay.

**Root cause:** `GetShipsOfTypeForRegion` always constructs a fresh list, and `RunAIQueue` calls it twice and immediately passes the result to `_aIQueue.Fill(...)` with no caching.

**Fix:** Hoist two static reusable `List<AIShip>` buffers, `Clear()` them at the top of `RunAIQueue`, have `GetShipsOfTypeForRegion` accept an output list (or have `RunAIQueue` iterate `dictAIs` directly), and skip the refill when the queue already has pending items.

## `FFWD` calls `.ToArray()` on each regional list every fast-forward tick

Same allocate-and-discard pattern; iterate the list directly or use a reusable array.
