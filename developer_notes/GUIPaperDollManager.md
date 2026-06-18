# GUIPaperDollManager.cs

## Per-frame `List<PixelPos>` + nested `GetComponent`; per-click `PointerEventData` alloc

**Location:** `CheckHit()` (line 983) + `GetPixelCluster()` (line 1058) + `IsObstructedByUI()` (line 918).

**Issue:** Per-frame allocation of a 25-element `List<PixelPos>` via `GetPixelCluster` for every slot hit-test, plus `GetComponent<RawImage>()` called in a nested loop over `mapCOIDsToGO`; `IsObstructedByUI` allocates `new PointerEventData` and `new List<RaycastResult>` per click.

**Root cause:** `CheckHit()` is called from `Update()` every frame the inventory is visible; `GetPixelCluster` creates `new List<PixelPos>()` with 25 entries each call; `AlphaHit` calls `img.GetPixel()` (slow CPU texture readback) per pixel; `IsObstructedByUI` allocates `new PointerEventData` and `new List<RaycastResult>` on every pointer-down.

**Fix:** Cache the `PixelPos` cluster as a static readonly array (it's always the same 25 offsets), replace `GetComponent<RawImage>()` calls with cached references on the GameObjects, and pool the `PointerEventData`/`List<RaycastResult>` for `IsObstructedByUI`.
