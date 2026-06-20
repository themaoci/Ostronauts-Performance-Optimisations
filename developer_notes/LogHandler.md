# LogHandler.cs

**Source path:** `Ostranauts.Core/LogHandler.cs`

## `string.Split` on every log message (O(n) in log size)

**Location:** `IsDuplicate()` (line 55) + `TrimLog()` (line 81).

**Issue:** Both methods allocate `new string[1] { _lineStart }` array and call `Log.Split()` which creates a new string array on every log message.

**Root cause:** Every `LogMessage()` call (which fires frequently during gameplay) triggers `IsDuplicate` and `TrimLog`, both of which split the entire log string into an array of strings; this is O(n) in log size and allocates heavily.

**Fix:** Cache the separator array as a static readonly field, and for `IsDuplicate` use `Log.LastIndexOf(_lineStart)` + `Log.Contains(logString)` on the tail instead of splitting the entire log.

**Mod patches:** `Patch_LogHandler_IsDuplicate` replaces `Split` with `LastIndexOf` + `IndexOf` (zero allocation on the common "not a duplicate" path). `Patch_LogHandler_TrimLog` replaces `Split` with manual `IndexOf` counting + a single `Substring`.
