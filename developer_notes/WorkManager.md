# WorkManager.cs

## `Task2.UpdateTint` allocates `MaterialPropertyBlock` + `GetComponent` per task per frame

**Location:** `Update()` (lines 67–74) → `Task2.UpdateTint()` (Task2.cs lines 167–183).

**Issue:** Per-frame, for every active task, `Task2.UpdateTint` allocates a brand-new `MaterialPropertyBlock` (Task2.cs line 177) and calls `constructionSign.GetComponent<MeshRenderer>()` (line 172) — once per task per frame.

**Root cause:** `MaterialPropertyBlock` and `MeshRenderer` references are not cached on the `Task2` instance; both are reacquired every frame for every task.

**Fix:** Cache `MaterialPropertyBlock` and `MeshRenderer` as fields on `Task2` (fetch once in `SetConstructionSign`), then reuse them — `SetPropertyBlock` on the cached block each frame.
