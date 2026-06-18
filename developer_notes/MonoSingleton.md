# MonoSingleton.cs

**Source path:** `Ostranauts.Core/MonoSingleton.cs`

## `Awake` continues after `Destroy` (no `return`); `FindObjectOfType` per access

**Location:** `Instance` getter (line 9) + `Awake()` (line 30).

**Issue:** `Awake()` does not return after `Object.Destroy(this)` when a duplicate singleton is found, and the `Instance` getter calls `FindObjectOfType` (O(n) scene scan) when `_instance` is null.

**Root cause:** If `_instance != null`, `Awake` destroys `this` but continues executing (no `return`); if the existing singleton is destroyed (scene unload), `_instance` becomes a fake-null Unity object and the getter calls `FindObjectOfType` on every access until a new one is created.

**Fix:** Add `return;` after `Object.Destroy(this)` in `Awake`, and in the `Instance` getter use `_instance == null` (Unity's overloaded `==` catches destroyed objects) but also set `_instance = null` in `OnDestroy`.
