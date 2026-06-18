# AudioManager.cs

## `bLoadingMusic` stuck true on exception (no `try/finally`)

**Location:** `_CueMusic` coroutine (lines 234–282).

**Issue:** `bLoadingMusic = true` is set at the top but the flag is only cleared at line 281 with no `try/finally`; if `www.GetAudioClip` or the clip-swap block throws, `bLoadingMusic` stays true forever and `UpdateMusic` will never queue another track for the rest of the session.

**Root cause:** No exception handling around the WWW/clip operations, so any exception (file read error, AudioClip null, `srcMusic.clip.length` on a null clip) leaves the gate flag stuck.

**Fix:** Wrap the body between `bLoadingMusic = true` and the end in `try { ... } finally { bLoadingMusic = false; }` so the flag is always cleared; also replace the deprecated `WWW` with `UnityWebRequestAssetBundle.GetAudioClip`.
