# AStarSearch.cs

**Source path:** `Ostranauts.Pathing/AStarSearch.cs`

## Hardcoded magic `3333` iteration cap; `Debug.Log` on >1511 tiles

**Location:** `GetPath()` (line 31).

**Issue:** Hardcoded magic number `3333` as max iteration count with `i == 3332` check, and logs a debug message if iterations exceed 1511.

**Root cause:** The loop `for (int i = 0; i < 3333; i++)` uses an arbitrary fixed cap that silently fails on large or complex ships; `Debug.Log(coUs.strName + " pathfinder searched " + i + " tiles.")` fires whenever > 1511 tiles are searched, which is common on large ships.

**Fix:** Replace the fixed cap with a configurable max based on ship size (e.g., `coUs.ship.aTiles.Count * 4`), and remove or downgrade the `Debug.Log` to a one-shot warning.
