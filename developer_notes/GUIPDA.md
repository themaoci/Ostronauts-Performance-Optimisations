# GUIPDA.cs

## `GetKnownSocialContacts` is O(n²) via `Insert(0, ...)`

**Location:** `GetKnownSocialContacts` (line 1298).

**Issue:** `list.Insert(0, allPerson)` is called inside a `foreach` over every person the crew knows, producing O(n²) behavior because each insert at index 0 shifts the entire underlying array.

**Root cause:** `List<T>.Insert(0, ...)` is O(n); combined with the outer O(n) iteration, the method is O(n²) in the number of known contacts. `ShowSocials` additionally calls `OrderBy/OrderByDescending(...).ToList()` (line 1238) plus `Resources.Load<GameObject>(...)` (line 1236) on every open.

**Fix:** Append matches with `list.Add(allPerson)` and reverse the list once at the end (or use two lists and concat), and cache the `Resources.Load` prefab lookup in a static field.
