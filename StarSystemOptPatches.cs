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
{    // ========================================
    // STARSYSTEM UPDATESHIP: Cache default gravity BO
    // ========================================
    // Original: temp_boGrav = aBOs.FirstOrDefault().Value;
    // This allocates an enumerator every call for every ship every frame.
    // Can't transpile Dictionary.First() easily (multiple overloads).
    // Instead, we cache the default BO in a static field and expose it
    // for the game's code via a Postfix that patches the result field.

    [HarmonyPatch]
    public static class Patch_UpdateShip_DefaultGravBO
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(StarSystem), "UpdateShip");
        }

        private static readonly FieldInfo _aBOsField =
            AccessTools.Field(typeof(StarSystem), "aBOs");
        private static readonly FieldInfo _tempBOGravField =
            AccessTools.Field(typeof(StarSystem), "temp_boGrav");
        private static IDictionary _cachedABOs;
        private static object _cachedDefaultBO;
        private static int _cachedABOsCount = -1;

        static void Postfix(StarSystem __instance)
        {
            if (!PerfOptPlugin.GameLoaded) return;

            var tempBOGrav = _tempBOGravField?.GetValue(__instance);
            if (tempBOGrav != null) return;

            var aBOs = _aBOsField?.GetValue(__instance) as IDictionary;
            if (aBOs == null || aBOs.Count == 0) return;

            if (aBOs != _cachedABOs || aBOs.Count != _cachedABOsCount)
            {
                _cachedABOs = aBOs;
                _cachedABOsCount = aBOs.Count;
                _cachedDefaultBO = null;
                foreach (DictionaryEntry entry in aBOs)
                {
                    _cachedDefaultBO = entry.Value;
                    break;
                }
            }

            if (_cachedDefaultBO != null && _tempBOGravField != null)
                _tempBOGravField.SetValue(__instance, _cachedDefaultBO);
        }
    }

    // ========================================
    // LOADMANAGER SAVESCREENSHOT: defer to coroutine, clean up after use
    // ========================================
    // Vanilla SaveScreenShot (LoadManager.cs:709) does RenderTexture capture +
    // ReadPixels + EncodeToPNG + File.WriteAllBytes synchronously on the main
    // thread before the threaded save job starts. This causes 100-500ms
    // freezes on every save.
    //
    // Patch: Prefix returns null immediately (zero cost on the save frame)
    // and starts a coroutine on LoadManager. The coroutine yields one frame
    // (so the save frame completes), then does the GPU capture + PNG encode +
    // file write, then destroys the RenderTexture and Texture2D. The save-list
    // UI reads screenshot.png from disk later (_LoadSaveInfoImages), so the
    // in-memory _loadedSave.ScreenShot being null is fine.

    [HarmonyPatch]
    public static class Patch_SaveScreenShot_Skip
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
    public static class Patch_SaveCrewPortraits_Skip
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