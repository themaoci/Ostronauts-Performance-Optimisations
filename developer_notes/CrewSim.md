# CrewSim.cs

## Per-frame allocations in `Update` / `GetMouseOverCO`

**Location:** `Update()` (line ~1334) → `GetMouseOverCO()` (lines 3235–3251); tick loop lines 1281–1284; Canvas reads lines 1328, 1364.

**Issue:** Three distinct per-frame allocation sources on the main simulation hot path:

1. `GetMouseOverCO()` calls `Physics.RaycastAll` (allocates `RaycastHit[]`), builds a `List<CondOwner>`, runs LINQ `OrderBy` to sort hits, and calls `GetComponent<CondOwner>()` in a loop — but most callers (e.g. the `Update` hover check) only need a boolean "is anything there".
2. Inside the `while (num2 > 0.0)` ticker substep loop, `aTickers.FirstOrDefault()` is invoked **twice per iteration** (once in the condition, once in the body) — LINQ on every sim substep instead of an indexer.
3. `CanvasManager.goCanvasGUI.GetComponent<Canvas>().scaleFactor` is called every frame (twice) to re-read the same Canvas.

**Root cause:** No non-allocating query path; no caching of the first ticker reference; Canvas component fetched via `GetComponent` per-frame instead of cached on init.

**Fix:**
- Add an `IsMouseOverCO(...)` overload that uses the non-allocating single-hit `Physics.Raycast` (with a reusable `RaycastHit` buffer) and returns true on the first valid `CondOwner`.
- Replace `aTickers.FirstOrDefault()` with `var first = aTickers.Count > 0 ? aTickers[0] : null;` and reuse `first` in both the condition and body.
- Cache the `Canvas` reference once in `Awake`/`OnEnable` and read `scaleFactor` from the cached field.

**Mod patch:** `Patch_CrewSim_CacheComponents` caches the `Canvas.scaleFactor` and `Audio_VacuumController` lookups (issue #3 above). The `GetMouseOverCO` raycast/LINQ (#1) and `aTickers.FirstOrDefault()` (#2) issues are not yet patched.
