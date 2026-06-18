# GUIInventory.cs

**Source path:** `Ostranauts.Inventory/GUIInventory.cs`

## Per-frame `+=` tooltip string building; `Debug.Log` on selection

**Location:** `Update()` (lines 594–618) + `Selected` setter (line 126).

**Issue:** Per-frame string concatenation building tooltip text by iterating `coTooltip.mapConds.Values` with `text +=` in a loop, plus `Debug.Log("Selected: " + ...)` fires on every selection change.

**Root cause:** Tooltip string is rebuilt every frame while `coTooltip != null && Selected == null`, using `+=` on strings inside a `foreach` over Conditions (allocates new strings each iteration); the `Debug.Log` in the `Selected` setter is unconditional dev logging.

**Fix:** Use `StringBuilder` to build the tooltip text, cache it when `coTooltip` changes rather than every frame, and remove or guard the `Debug.Log` in the `Selected` setter.

**Mod patch:** `Patch_DebugLog_Suppress` suppresses the `Debug.Log` on selection change. The per-frame `+=` tooltip string building is not yet patched.
