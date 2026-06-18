# ContextMenuPool.cs

## Fixed pool of 50, no growth, returns null + `Debug.Log` spam

**Location:** `GetMenuObject()` (line 87).

**Issue:** `Debug.Log("Ran out of menu pool - should probably actually implement the ability to add more pool objects, eh Michael?")` fires every time the pool is exhausted, and returns null silently.

**Root cause:** Pool is fixed at 50 items (line 52) with no growth strategy; when exhausted, it logs a debug message every call and returns null, causing callers to null-reference or silently fail.

**Fix:** Dynamically expand the pool by instantiating new items when exhausted (like `CreateButtonPool` does), and remove the `Debug.Log` or convert it to a one-time warning.

**Mod patch:** `Patch_DebugLog_Suppress` suppresses the `Debug.Log` spam when the pool is exhausted. The fixed pool size and null return are not yet patched.
