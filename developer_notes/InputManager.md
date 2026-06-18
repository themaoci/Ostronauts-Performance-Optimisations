# InputManager.cs

**Source path:** `Ostranauts.InputControl/InputManager.cs`

## `+=` string building in `GetGlyphString`; `SteamInput` array alloc per `GetControllerType`

**Location:** `GetGlyphString(string cmdName)` (line 416) + `GetControllerType()` (line 567).

**Issue:** `GetGlyphString` iterates through all bindings allocating strings via `+=` on every call, and `GetControllerType` calls `SteamInput.GetConnectedControllers` allocating a new `InputHandle_t[16]` array each time.

**Root cause:** `GetGlyphString` is called frequently (every QAB refresh, every `SetBindingLabel`, etc.) and builds a string with `+=` in a loop; `GetControllerType` allocates a 16-element array and calls Steam API on every call.

**Fix:** Use `StringBuilder` in `GetGlyphString`, and cache the controller type string (invalidate only on `OnDeviceChanged`).
