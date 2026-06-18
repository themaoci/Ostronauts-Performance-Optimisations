# GUIQuickBar.cs

## LINQ `ToList` + O(n²) `Remove`/`Insert` reordering

**Location:** `BuildButtonList()` (lines 599–603, 616–626).

**Issue:** Multiple LINQ `.ToList()` calls on every dirty refresh, plus an O(n²) reordering loop using `Remove()` + `Insert(0, ...)`.

**Root cause:** Lines 599–603 use LINQ `where...orderby...ThenByDescending...ThenBy...ToList()` and `.Where(...).ToList()` creating 2 new lists; lines 616–626 loop backward and for each clickable item call `availActionsForCO.Remove(item)` (O(n)) then `Insert(0, item)` (O(n)), making this O(n²).

**Fix:** Filter and sort in-place using a single pass into a pre-allocated list, and for the clickable reordering, build a new list with clickable items first followed by non-clickable, rather than using `Remove`/`Insert`.
