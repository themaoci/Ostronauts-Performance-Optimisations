# GUIInventoryWindow.cs

**Source path:** `Ostranauts.Inventory/GUIInventoryWindow.cs`

## `Physics.OverlapBox` + 25 `List<CondOwner>` allocs per frame per ground window

**Location:** `UpdateWindow()` (line 477) + `RedrawGroundWindow()` (line 261).

**Issue:** `UpdateWindow()` is called every frame from `GUIInventory.Update()` for every active window, and `RedrawGroundWindow` (called for `Ground` type) does `Physics.OverlapBox` + allocates new lists each frame.

**Root cause:** `UpdateWindow()` unconditionally calls `Redraw()` for `Ground`-type windows every frame, which calls `RedrawGroundWindow()` that loops 5×5 calling `FillGroundInventory` which allocates `new List<CondOwner>()` per tile (25 allocations per frame per ground window).

**Fix:** Throttle ground window redraws to every N frames or only when the crew position changes, and reuse the `List<CondOwner>` across calls by clearing instead of allocating.
