using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OstronautsPerfOpt
{
    // ========================================
    // LOG SUPPRESSION: Suppress Debug.Log during loading and gameplay
    // ========================================
    // Debug.Log takes 0.1-1ms per call (string format + Unity console I/O +
    // BepInEx file write). The game logs hundreds of messages during loading
    // and during music transitions, NPC updates, and ship interactions.
    // Debug.LogError and Debug.LogWarning are NOT suppressed (different methods).

    [HarmonyPatch]
    public static class Patch_DebugLog_Suppress
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Debug), "Log",
                new Type[] { typeof(string) });
        }

        static bool Prefix(string message)
        {
            return false;
        }
    }
}