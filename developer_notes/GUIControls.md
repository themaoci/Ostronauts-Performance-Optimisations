# GUIControls.cs

**Source path:** `Ostranauts.InputControl/GUIControls.cs`

## Duplicate condition makes middle-page branch dead code

**Location:** `SetPage()` (lines 172–181).

**Issue:** Duplicate condition `currentPage == deviceGroups.Length - 1` on both line 172 and line 177, making the middle case (both buttons visible) unreachable dead code.

**Root cause:** The second `else if` checks the exact same condition as the first, so the intended three-state logic (first page / middle page / last page) is broken — with only 2 device groups this happens to work, but the middle-page branch is dead code and would be wrong if a third group were added.

**Fix:** Change line 177 to `else` (no condition) or `else if (currentPage < deviceGroups.Length - 1)` to handle the middle case.
