# GridLayout.cs

**Source path:** `Ostranauts.Inventory/GridLayout.cs`

## O(n²) `FindNearestUnoccupiedTile`; O(n) grid scan per CO lookup

**Location:** `FindNearestUnoccupiedTile()` (line 203) + `GetInventoryItemFromCO()` (line 77).

**Issue:** `FindNearestUnoccupiedTile` is O(n²) with distance calculation per cell, and `GetInventoryItemFromCO` does a full grid scan (O(width*height)) for every CO lookup.

**Root cause:** `FindNearestUnoccupiedTile` iterates every cell computing squared distance and calls `IsGridRectangleUnoccupied` (which itself loops the item footprint) making it O(gridW * gridH * itemW * itemH); `GetInventoryItemFromCO` is called from `GUIInventory.GetInventoryItemFromCO` which iterates all windows.

**Fix:** Maintain a `Dictionary<string, PairXY>` mapping CO `strID` to grid position for O(1) lookups instead of scanning the entire grid.
