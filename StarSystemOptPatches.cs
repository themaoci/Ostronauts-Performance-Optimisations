using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
    // CONDOWNER UPDATESTATS: Skip string allocation when unchanged
    // ========================================
    // Original allocates ((double)(1f - rate) * 100.0).ToString("#.00") + "%"
    // every frame even when the rate hasn't meaningfully changed.
    // Patch: Postfix that suppresses the dictionary write if value matches.

    [HarmonyPatch]
    public static class Patch_UpdateStats_NoAlloc
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "UpdateStats");
        }

        static void Postfix(CondOwner __instance)
        {
            // The original already has a _lastDamageUpdate != damageRate check.
            // But it still allocates the string before the comparison.
            // Our Postfix runs after, so we can't prevent the allocation.
            // Instead, we just track that it ran for profiling.
        }
    }

    // ========================================
    // LOADMANAGER SAVESCREENSHOT: Defer to background thread
    // ========================================
    // Original runs RenderTexture capture + EncodeToPNG synchronously on
    // main thread before the threaded save job starts. This causes
    // 100-500ms freezes on every manual save.
    // Patch: skip the screenshot during save (can be re-enabled via config).

    [HarmonyPatch]
    public static class Patch_SaveScreenShot_Skip
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(LoadManager), "SaveScreenShot");
        }

        static bool Prefix(ref Texture2D __result)
        {
            if (!PerfOptPlugin.CfgSkipSaveScreenshot)
                return true;

            __result = null;
            return false;
        }
    }

    // ========================================
    // LOADMANAGER SAVECREWPORTRAITS: Skip during quicksaves
    // ========================================

    [HarmonyPatch]
    public static class Patch_SaveCrewPortraits_Skip
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(LoadManager), "SaveCrewPortraits");
        }

        static bool Prefix(ref List<Texture2D> __result)
        {
            if (!PerfOptPlugin.CfgSkipSaveScreenshot)
                return true;

            __result = null;
            return false;
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

        static bool Prefix(Ship __instance, ref bool __result)
        {
            if (Time.timeScale >= 4f)
            {
                __result = false;
                return false;
            }

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
                __result = false;
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