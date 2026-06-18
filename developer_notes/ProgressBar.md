# ProgressBar.cs

**Source path:** `Ostranauts.Components/ProgressBar.cs`

## `Color.Lerp` divisor hardcoded `100f` (logic bug on non-100 max)

**Location:** `SetLongBarProgressStat()` (line 185).

**Issue:** Color Lerp divisor is hardcoded `100f` instead of `_longBarLengthMax`, causing incorrect color interpolation when max is not 100.

**Root cause:** `Color.Lerp(LBSSTARTCOLOR, LBSENDCOLOR, (_longBarLengthMax - _longBarCurrentLength) / 100f)` uses a magic number `100f` as the divisor; if `_longBarLengthMax` was set to a fallback of 100 (line 179) this works, but when the `Destructable` provides a real max (e.g., 1000 HP), the Lerp t-value exceeds 1.0 and clamps, so the bar color never transitions.

**Fix:** Replace `/ 100f` with `/ _longBarLengthMax` and `Mathf.Clamp01` the result.
