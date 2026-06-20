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
                MonoSingleton<LoadManager>.Instance.StartCoroutine(
                    CaptureScreenshotCoroutine(folderPath));
            }
            catch (Exception ex)
            {
                PerfOptPlugin.Log.LogWarning($"[SAVE-SHOT] Could not start coroutine: {ex.Message}");
            }
            __result = null;
            return false;
        }

        private static IEnumerator CaptureScreenshotCoroutine(string folderPath)
        {
            yield return null;

            Camera mainCamera = GameRenderer.MainCamera;
            if (mainCamera == null) yield break;

            int width = GameRenderer.Width;
            int height = GameRenderer.Height;
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                rt = mainCamera.targetTexture = new RenderTexture(width, height, 24);
                tex = new Texture2D(width, height, TextureFormat.RGB24, mipChain: false);
                mainCamera.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                tex.Apply();
                mainCamera.targetTexture = null;
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
                MonoSingleton<LoadManager>.Instance.StartCoroutine(
                    CapturePortraitsCoroutine(folderPath));
            }
            catch (Exception ex)
            {
                PerfOptPlugin.Log.LogWarning($"[SAVE-PORTRAIT] Could not start coroutine: {ex.Message}");
            }
            __result = new List<Texture2D>();
            return false;
        }

        private static IEnumerator CapturePortraitsCoroutine(string folderPath)
        {
            yield return null;

            if (CrewSim.coPlayer == null || CrewSim.coPlayer.Company == null
                || CrewSim.coPlayer.Company.mapRoster == null)
                yield break;

            foreach (string key in CrewSim.coPlayer.Company.mapRoster.Keys)
            {
                if (key == CrewSim.coPlayer.strID) continue;
                CondOwner co = null;
                if (!DataHandler.mapCOs.TryGetValue(key, out co)) continue;

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
                    while (start > 0 &&
                           codes[start - 1].opcode != OpCodes.Newarr)
                    {
                        start--;
                    }
                    if (start > 0 && codes[start - 1].opcode == OpCodes.Newarr)
                    {
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