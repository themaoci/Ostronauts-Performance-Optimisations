# BeatManager.cs

## `GenerateTension` re-runs every frame when no event fires

**Location:** `GenerateTension` (lines 269–334), called from `Update` (lines 116–118).

**Issue:** `ResetTensionTimer()` is only called when `flag` is true (line 332); if every event generator returns false (the common case when the player is in a safe area), `fTensionRemain` stays negative, so `GenerateTension` is re-invoked every single frame thereafter, running the full 9-event pipeline plus dozens of `Debug.Log` calls per frame until some event finally succeeds.

**Root cause:** The `if (flag) ResetTensionTimer();` guard at lines 330–333 means a "no event fired" tick leaves the timer expired; `Update` then re-enters `GenerateTension` on the next tick because `fTensionRemain < 0.0` is still true, creating an inadvertent per-frame hot path out of what should be a 9-minute timer.

**Fix:** Always call `ResetTensionTimer()` at the end of `GenerateTension` (move it out of the `if (flag)` block), matching the pattern already used by `GenerateRelease` at line 373.
