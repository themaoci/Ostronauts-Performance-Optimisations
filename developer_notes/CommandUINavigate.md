# CommandUINavigate.cs

**Source path:** `Ostranauts.InputControl/CommandUINavigate.cs`

## Iterates all `Selectable`s with `GetComponent` per nav input

**Location:** `Execute()` (line 17) + `TrySelectClosest()` (line 49).

**Issue:** `Selectable.allSelectablesArray` is iterated with `GetComponent` calls and `RectTransformUtility.WorldToScreenPoint` per selectable on every navigation input.

**Root cause:** `TrySelectClosest` loops through ALL selectables in the scene computing screen positions and dot products; this runs on every navigation input event, and `allSelectablesArray` can contain hundreds of UI elements.

**Fix:** Cache the selectables list and only refresh when UI changes, or limit the search to the current canvas/panel rather than all selectables globally.
