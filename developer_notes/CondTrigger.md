# CondTrigger.cs

## `nRecursion` never decremented — permanently breaks `RequiresHumans`

**Location:** `CheckRequiresHumans` (lines 471–504).

**Issue:** `nRecursion` is an instance field that is incremented (`nRecursion++`) but never decremented, so every call to `CheckRequiresHumans` on the same `CondTrigger` accumulates depth; once `PostInit` is called again (e.g. via `CloneDeep` at line 604 or `AllPostLoadAsync` re-running) the counter crosses 3 and the method returns false with a spurious "Possible recursion found" warning, permanently breaking the `RequiresHumans` flag.

**Root cause:** Recursion depth is tracked as mutable instance state without a matching decrement or reset; the guard `if (nRecursion > 3) return false;` thus fires on legitimate re-entries, not just true cycles.

**Fix:** Pass the depth as a parameter (`CheckRequiresHumans(int depth)`) or use a `ThreadLocal<int>`/parameter-based counter that naturally unwinds with the call stack, and remove the instance field; alternatively reset `nRecursion = 0` at the start of each top-level call and decrement on return.
