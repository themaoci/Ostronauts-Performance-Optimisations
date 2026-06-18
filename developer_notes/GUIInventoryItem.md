# GUIInventoryItem.cs

**Source path:** `Ostranauts.Inventory/GUIInventoryItem.cs`

## Per-frame dict lookups in `Update`; `Debug.Log` on every destroy

**Location:** `Update()` (line 342) + `OnDestroy()` (line 122).

**Issue:** Every inventory item runs `Update()` every frame calling the `CO` property getter which does a dictionary lookup and null checks (`_co.bDestroyed`/`_co.ship == null` checks plus `DataHandler.mapCOs.TryGetValue`) every frame even when idle; `OnDestroy` logs `"Destroying " + CO.strName` for every item destroyed.

**Root cause:** `Update()` runs on every spawned inventory item (potentially dozens), and the `CO` getter performs lookups every frame; `Debug.Log` on destroy fires for every trashed/moved item.

**Fix:** Guard `Update()` with an early return when not selected and `_isMouseOverUI` is false (cache the `CO` reference on spawn instead of re-resolving), and remove the `Debug.Log("Destroying " + CO.strName)` line or wrap it in `#if UNITY_EDITOR`.
