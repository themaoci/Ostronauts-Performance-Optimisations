# DataHandler.cs

## `lock (new object())` never serializes — race condition

**Location:** `JsonToData<TJson>(string, Dictionary)` exception handler (line 1282).

**Issue:** `lock (new object())` creates a brand-new lock object every invocation, so the critical section is never actually serialized — concurrent loader threads can mutate `LoadManager.JsonLogErrorExceptions` simultaneously.

**Root cause:** A new `object()` is constructed inline as the lock target instead of using the existing `dictWriteLock` static field, so each thread locks on a different instance.

**Fix:** Replace `lock (new object())` with `lock (dictWriteLock)` (or a dedicated `jsonLogLock`) so all threads contend on the same monitor.

## `GetCondTrigger` clones on every call

**Location:** `GetCondTrigger` (line 2604).

**Issue:** Returns `value.Clone()` on every call, allocating a new `CondTrigger` (with all its arrays/dicts) each time it is invoked from hot paths like `CondTrigger.GetTrigger`, `Interaction.SetData`, and `GUIPDA.CTSocials` — heavy per-frame GC pressure.

**Fix:** Cache the clone per consumer or return a shared read-only instance and clone only when mutation is needed.
