# JumpPointSearch.cs

**Source path:** `Ostranauts.Pathing/JumpPointSearch.cs`

## Allocates `bool?[,]` 2D array (80KB+) per path search

**Location:** `GetPath()` (line 79) + `FindPath()` (line 112).

**Issue:** Allocates a new `bool?[,]` 2D array of size `_gridXMax * _gridYMax` on every single path search, plus new `JPSPriorityQueue`, `HashSet`, and `Dictionary` per call.

**Root cause:** `_walkableGrid = GetGridSize()` reassigns the grid every call, allocating a large nullable-bool 2D array (`bool?` is 2 bytes + null flag, so a 200×200 ship = 80KB+ per path search); `FindPath` also allocates a new priority queue, closed set, and node map each time.

**Fix:** Reuse a single `_walkableGrid` array sized to the maximum ship dimensions, clear it (set to `false`/default) at the start of each search instead of reallocating, and reuse the priority queue, `HashSet`, and `Dictionary` as fields with `.Clear()`. Use `bool[,]` (non-nullable) to halve memory and avoid the null-flag overhead.
