# LoadManager.cs

## Synchronous `File.ReadAllBytes` on main thread during save listing

**Location:** `_LoadSaveInfoImages` (line 555) + `LoadImageFromFile` (line 487).

**Issue:** Synchronous `Directory.GetFiles` + `File.ReadAllBytes` per save directory per portrait/screenshot runs on the main thread inside a coroutine, freezing the frame for each save listed.

**Root cause:** `LoadImageFromFile` calls `File.ReadAllBytes` directly and allocates a `new Texture2D` per file, and the coroutine drives one save per `yield return null` frame but does all I/O synchronously within that frame. `GetSaveInfos()` (line 1092) also allocates a fresh `List<SaveInfo>` + `AddRange` on every call, and `saveInfos.OrderByDescending(...)` (line 562) allocates another enumerable.

**Fix:** Move the `Directory.GetFiles`/`File.ReadAllBytes`/`Texture2D.LoadImage` work onto a background `Task` (or `UnityWebRequestTexture`), cache the `GetSaveInfos()` result for the duration of the loop, and replace `OrderByDescending` with an in-place sort on a reused list.

## `SaveScreenShot` leaks `mainCamera.targetTexture`

**Location:** `SaveScreenShot()` (line 718).

**Issue:** Sets `mainCamera.targetTexture` to a new `RenderTexture` and sets it to `null` after, never restoring the original — losing the original target texture (could break post-processing or other camera logic).

**Fix:** Save the original `mainCamera.targetTexture` value, assign the `RenderTexture`, capture, then restore the original value in a `finally` block.

## Unnecessary `.ToList()` on `Directory.GetFiles`

**Location:** `LoadImageFolder()` (line 511).

**Issue:** `.ToList()` on the `Directory.GetFiles` result is unnecessary since it's already an array and is only iterated once.

**Fix:** Remove the `.ToList()` call.
