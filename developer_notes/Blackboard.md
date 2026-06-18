# Blackboard.cs

**Source path:** `Ostranauts.Core/Blackboard.cs`

## `Split`+LINQ `LastOrDefault` per deserialized entry

**Location:** `Deserialize()` (line 139) + `GetJson()` (line 44).

**Issue:** `Deserialize()` calls `text.Split('.').LastOrDefault()` using LINQ on every deserialized entry, and `GetJson()` builds a `List<string>` then converts to array with `.ToArray()`.

**Root cause:** Each deserialization splits the type string and uses LINQ `LastOrDefault` to get the last segment; `GetJson` allocates a List then copies to array; both are called during save/load which processes hundreds of entries.

**Fix:** Use `text.Substring(text.LastIndexOf('.') + 1)` instead of `Split`+LINQ, and write directly to a pre-sized array or reuse a `StringBuilder`.
