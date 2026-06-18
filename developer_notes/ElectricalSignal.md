# ElectricalSignal.cs

**Source path:** `Ostranauts.Electrical/ElectricalSignal.cs`

## `int.Parse`/`bool.Parse` throw on malformed save (no try-catch)

**Location:** `FromString()` (line 32).

**Issue:** `int.Parse(array[1])` and `bool.Parse(array[3])` will throw on malformed save data with no try-catch.

**Root cause:** `FromString` assumes well-formed input; if the save file is corrupted or the format changes, `int.Parse`/`double.Parse`/`bool.Parse` throw unhandled exceptions that propagate up the load chain.

**Fix:** Use `int.TryParse`/`double.TryParse`/`bool.TryParse` with fallback defaults, consistent with how `array.Length >= 4` is already guarded.
