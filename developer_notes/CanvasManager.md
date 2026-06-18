# CanvasManager.cs

## NRE inside the null-guard branch

**Location:** `ShowCanvasQuit` (lines 297–303).

**Issue:** When `component` is null the error logger dereferences `component.name`, throwing a `NullReferenceException` inside the null-guard branch and hiding the original "button not found" condition.

**Root cause:** `Debug.Log("Button not found:" + component.name)` references the just-null-checked variable instead of the path string `n`; the null check is immediately followed by a member access on the null reference.

**Fix:** Log the path instead: `Debug.Log("Button not found: " + n);` — and remove the no-op `component.interactable = false; component.interactable = true;` toggle that runs unconditionally on every other button.
