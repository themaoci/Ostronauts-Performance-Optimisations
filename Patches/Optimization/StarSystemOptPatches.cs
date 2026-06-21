using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Ostranauts.Core;
using Ostranauts;

namespace OstronautsPerfOpt
{
    // ========================================
    // LOADMANAGER SAVESCREENSHOT: defer to coroutine, clean up after use
    // ========================================
    // Vanilla SaveScreenShot (LoadManager.cs:709) does RenderTexture capture +
    // ReadPixels + EncodeToPNG + File.WriteAllBytes synchronously on the main
    // thread before the threaded save job starts. This causes 100-500ms
    // freezes on every save.
    //
    // Patch: Prefix returns false immediately and sets __result = null
    // (zero cost on the save frame) and starts a coroutine on LoadManager.
    // The coroutine yields one frame (so the save frame completes), then
    // does the GPU capture + PNG encode + file write, then destroys the
    // RenderTexture and Texture2D. The save-list
    // UI reads screenshot.png from disk later (_LoadSaveInfoImages), so the
    // in-memory _loadedSave.ScreenShot being null is fine.

    [HarmonyPatch]
    public static class Patch_SaveScreenShot_Defer
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(LoadManager), "SaveScreenShot");
        }

        static bool Prefix(ref Texture2D __result, string folderPath)
        {
            try
            {
                LoadManager host = MonoSingleton<LoadManager>.Instance;
                if (host != null)
                {
                    host.StartCoroutine(CaptureScreenshotCoroutine(host, folderPath));
                }
            }
            catch (Exception ex)
            {
                PerfOptPlugin.Log.LogWarning($"[SAVE-SHOT] Could not start coroutine: {ex.Message}");
            }
            __result = null;
            return false;
        }

        private static IEnumerator CaptureScreenshotCoroutine(LoadManager host, string folderPath)
        {
            yield return null;
            if (host == null) yield break;

            Camera mainCamera = GameRenderer.MainCamera;
            if (mainCamera == null) yield break;
            if (mainCamera.targetTexture != null) yield break; // camera in use by something else

            int width = GameRenderer.Width;
            int height = GameRenderer.Height;
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                rt = new RenderTexture(width, height, 24);
                RenderTexture prevTarget = mainCamera.targetTexture;
                mainCamera.targetTexture = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, mipChain: false);
                mainCamera.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                tex.Apply();
                // Restore previous target rather than null — vanilla leaked
                // the original target texture (see developer_notes/LoadManager.md).
                mainCamera.targetTexture = prevTarget;
                RenderTexture.active = null;

                string path = folderPath + "screenshot.png";
                byte[] png = tex.EncodeToPNG();
                File.WriteAllBytes(path, png);
            }
            catch (Exception ex)
            {
                PerfOptPlugin.Log.LogWarning($"[SAVE-SHOT] Capture/write failed: {ex.Message}");
            }
            finally
            {
                if (rt != null) UnityEngine.Object.Destroy(rt);
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
        }
    }

    // ========================================
    // LOADMANAGER SAVECREWPORTRAITS: defer to coroutine, clean up after use
    // ========================================
    // Same pattern: Prefix returns empty list immediately, coroutine yields a
    // frame, then captures each crew portrait via FaceAnim2.GetPNG, encodes,
    // writes, and destroys the texture. Zero cost on the save frame.

    [HarmonyPatch]
    public static class Patch_SaveCrewPortraits_Defer
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(LoadManager), "SaveCrewPortraits");
        }

        static bool Prefix(ref List<Texture2D> __result, string folderPath)
        {
            try
            {
                LoadManager host = MonoSingleton<LoadManager>.Instance;
                if (host != null)
                {
                    host.StartCoroutine(CapturePortraitsCoroutine(host, folderPath));
                }
            }
            catch (Exception ex)
            {
                PerfOptPlugin.Log.LogWarning($"[SAVE-PORTRAIT] Could not start coroutine: {ex.Message}");
            }
            __result = new List<Texture2D>();
            return false;
        }

        private static IEnumerator CapturePortraitsCoroutine(LoadManager host, string folderPath)
        {
            yield return null;
            if (host == null) yield break;
            if (CrewSim.coPlayer == null || CrewSim.coPlayer.Company == null
                || CrewSim.coPlayer.Company.mapRoster == null)
                yield break;

            // Snapshot the roster keys to a local list — iterating the dict
            // while yielding allows the game to mutate the collection.
            var keys = new List<string>(CrewSim.coPlayer.Company.mapRoster.Keys);
            foreach (string key in keys)
            {
                if (key == CrewSim.coPlayer.strID) continue;
                if (CrewSim.coPlayer == null) yield break; // coPlayer gone mid-iteration
                CondOwner co = null;
                if (!DataHandler.mapCOs.TryGetValue(key, out co)) continue;
                if (co == null) continue;

                Texture2D png = null;
                try
                {
                    png = FaceAnim2.GetPNG(co);
                    if (png == null) continue;
                    byte[] bytes = png.EncodeToPNG();
                    File.WriteAllBytes(folderPath + co.strName + "_portrait_crew.png", bytes);
                }
                catch (Exception ex)
                {
                    PerfOptPlugin.Log.LogWarning($"[SAVE-PORTRAIT] Failed for {co?.strName}: {ex.Message}");
                }
                finally
                {
                    if (png != null) UnityEngine.Object.Destroy(png);
                }

                yield return null;
            }
        }
    }

    // ========================================
    // STARSYSTEM.UPDATESHIP: eliminate FirstOrDefault enumerator alloc
    // ========================================
    // Vanilla UpdateShip (StarSystem.cs:1772) does:
    //   this.temp_boGrav = this.aBOs.FirstOrDefault<KeyValuePair<string,BodyOrbit>>().Value;
    // Enumerable.FirstOrDefault on Dictionary allocates an IEnumerator on the heap
    // every call. Called per ship per frame.
    //
    // Fix: Transpiler replaces the FirstOrDefault + get_Value pair with a single
    // call to GetFirstBOValue which uses Dictionary's struct enumerator (no alloc).

    [HarmonyPatch]
    public static class Patch_UpdateShip_FirstBO_NoAlloc
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(StarSystem), "UpdateShip");
        }

        private static readonly MethodInfo _firstBOResultMethod =
            AccessTools.Method(typeof(Patch_UpdateShip_FirstBO_NoAlloc), "GetFirstBOValue");

        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int patchCount = 0;

            for (int i = 0; i < codes.Count - 1; i++)
            {
                if (codes[i].opcode == OpCodes.Call &&
                    codes[i].operand is MethodInfo mi &&
                    mi.Name == "FirstOrDefault" &&
                    mi.DeclaringType == typeof(Enumerable))
                {
                    if (codes[i + 1].opcode == OpCodes.Call &&
                        codes[i + 1].operand is MethodInfo mi2 &&
                        mi2.Name == "get_Value")
                    {
                        codes[i] = new CodeInstruction(OpCodes.Call, _firstBOResultMethod);
                        codes[i + 1] = new CodeInstruction(OpCodes.Nop);
                        patchCount++;
                        i++;
                    }
                }
            }

            if (patchCount > 0)
                PerfOptPlugin.Log.LogInfo(
                    $"[GC-BO] StarSystem.UpdateShip: replaced {patchCount} FirstOrDefault().Value with no-alloc helper");
            else
                PerfOptPlugin.Log.LogInfo(
                    "[GC-BO] StarSystem.UpdateShip: FirstOrDefault pattern not found");

            return codes;
        }

        public static BodyOrbit GetFirstBOValue(Dictionary<string, BodyOrbit> dict)
        {
            if (dict == null) return null;
            foreach (var kvp in dict)
                return kvp.Value;
            return null;
        }
    }

    // ========================================
    // CONDOWNER.UPDATEMANUAL: eliminate Debug.Log alloc on ticker overflow
    // ========================================
    // Vanilla UpdateManual (CondOwner.cs:362) does:
    //   Debug.Log(string.Concat(new string[] { "#Info# ", this.strName, ... }));
    // The new string[] + string.Concat allocates even though Debug.Log is
    // suppressed by Patch_DebugLog_Suppress (argument evaluates before Prefix).
    // Called when a ticker exceeds maxRepeats — frequent during x4 speed.
    //
    // Fix: Transpiler removes the entire newarr + element fills + Concat + Log
    // sequence so no allocation occurs.

    [HarmonyPatch]
    public static class Patch_UpdateManual_NoTickerLog
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "UpdateManual");
        }

        private static readonly MethodInfo _debugLogMethod =
            AccessTools.Method(typeof(UnityEngine.Debug), "Log", new[] { typeof(string) });
        private static readonly MethodInfo _stringConcatMethod =
            AccessTools.Method(typeof(string), "Concat", new[] { typeof(string[]) });

        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);

            for (int i = codes.Count - 1; i >= 0; i--)
            {
                if (codes[i].opcode == OpCodes.Call &&
                    codes[i].operand is MethodInfo mi &&
                    mi == _debugLogMethod)
                {
                    int start = i;
                    // Walk back to Ldc_I4 (the count pushed for the newarr)
                    // NOPing only Newarr..Log leaves the count on the stack
                    // with no consumer -> invalid IL -> InvalidProgramException
                    // on next stack checkpoint (loop back-edge, return, throw).
                    while (start > 0)
                    {
                        var op = codes[start - 1].opcode;
                        if (op == OpCodes.Ldc_I4_0 || op == OpCodes.Ldc_I4_1 ||
                            op == OpCodes.Ldc_I4_2 || op == OpCodes.Ldc_I4_3 ||
                            op == OpCodes.Ldc_I4_4 || op == OpCodes.Ldc_I4_5 ||
                            op == OpCodes.Ldc_I4_6 || op == OpCodes.Ldc_I4_7 ||
                            op == OpCodes.Ldc_I4_8 || op == OpCodes.Ldc_I4_S ||
                            op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_M1 ||
                            op == OpCodes.Ldc_I4_S)
                        {
                            break;
                        }
                        start--;
                    }

                    for (int j = start; j <= i; j++)
                        codes[j] = new CodeInstruction(OpCodes.Nop);
                    break;
                }
            }

            return codes;
        }
    }

    // ========================================
    // LOADING: Batch ship spawning in StarSystem.Init
    // ========================================
    // StarSystem.Init(JsonStarSystemSave, JsonShip[]) spawns ALL ships one at a
    // time, yielding once per ship (lines 242-256). For 300 ships that's 300
    // frames of ship spawning at 1 ship/frame.
    //
    // The outer Patch_DoLoadGame_BatchYields already batches the DoLoadGame
    // coroutine's yields (3 per frame), but InitShip(Shallow) itself is expensive
    // (creates GameObjects, tiles, COs). Even with 3 ships per frame, 300 ships
    // = 100 frames of heavy work.
    //
    // Fix: Postfix on StarSystem.Init wraps the returned IEnumerator in a
    // batched wrapper that processes N ships per frame instead of 1. The wrapper
    // yields a non-null value (true) every N ships so the outer batch logic
    // (which only batches null yields) passes it through immediately.

    [HarmonyPatch]
    public static class Patch_StarInit_BatchShipSpawn
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(StarSystem), "Init",
                new[] { typeof(JsonStarSystemSave), typeof(JsonShip[]) });
        }

        const int ShipBatchSize = 10;

        static IEnumerator Postfix(IEnumerator result,
            JsonStarSystemSave objSystem, JsonShip[] aShips)
        {
            if (result == null) return null;
            return BatchedInit(result, aShips);
        }

        private static IEnumerator BatchedInit(IEnumerator inner, JsonShip[] aShips)
        {
            int steps = 0;
            int shipCount = aShips?.Length ?? 0;

            while (inner.MoveNext())
            {
                object yielded = inner.Current;
                steps++;

                if (yielded != null)
                {
                    // Non-null yield (progress bar update, yield return true, etc.)
                    // Pass through immediately — reset batch counter so the next
                    // batch of null yields starts fresh.
                    steps = 0;
                    yield return yielded;
                }
                else if (steps >= ShipBatchSize)
                {
                    // N ships processed — yield a non-null value so the outer
                    // BatchedCoroutineLoad (which only batches null yields)
                    // passes this through to Unity immediately.
                    steps = 0;
                    yield return true;
                }
                // else: null yield consumed by batching, continue without yielding
            }

            // Progress bar: if we consumed the final yield, update it manually
            if (shipCount > 0)
            {
                LoadingScreen.SetProgressBar(
                    LoadingScreen.GetProgress() + 0.01f, "Spawning System Ships (done)");
            }
        }
    }

    // ========================================
    // SHIP: Cache GetUserSettings in Sparks()
    // ========================================
    // Ship.Sparks() calls DataHandler.GetUserSettings() every frame
    // for every ship just to read nFlickerAmount. GetUserSettings()
    // does dictSettings["UserSettings"] lookup. Cache the int.

    [HarmonyPatch]
    public static class Patch_Sparks_CacheFlicker
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Ship), "Sparks");
        }

        private static float _cachedFlicker = -999f;
        private static int _cachedFlickerFrame = -1;

        static bool Prefix(Ship __instance)
        {
            if (Time.timeScale >= 4f)
                return false;

            int fc = Time.frameCount;
            if (fc != _cachedFlickerFrame)
            {
                _cachedFlickerFrame = fc;
                try
                {
                    _cachedFlicker = DataHandler.GetUserSettings().nFlickerAmount;
                }
                catch { _cachedFlicker = 0f; }
            }

            if (__instance.LoadState < Ship.Loaded.Edit
                || CrewSim.Paused
                || _cachedFlicker < 0f)
            {
                return false;
            }
            return true;
        }
    }

    // ========================================
    // SHIP: Skip DamageOverTime when not due
    // ========================================
    // DamageOverTime only acts when StarSystem.fEpoch - fLastWearEpoch >= 300.
    // The method is called for every ship every frame but returns early 99% of the time.
    // Patch: check the condition in Prefix and skip the method call entirely.

    [HarmonyPatch]
    public static class Patch_DamageOverTime_Skip
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Ship), "DamageOverTime");
        }

        private static readonly FieldInfo _fLastWearEpochField =
            AccessTools.Field(typeof(Ship), "fLastWearEpoch");

        static bool Prefix(Ship __instance)
        {
            double lastWear = (double)(_fLastWearEpochField?.GetValue(__instance) ?? 0.0);
            if (lastWear == 0.0)
                return true;
            return (StarSystem.fEpoch - lastWear) >= 300.0;
        }
    }
}