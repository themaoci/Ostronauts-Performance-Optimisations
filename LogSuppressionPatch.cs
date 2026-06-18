using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OstronautsPerfOpt
{
    // ========================================
    // LOG SUPPRESSION: suppress info logs, pass warnings/errors through
    // ========================================
    // Debug.Log takes 0.1-1ms per call (string format + Unity console I/O +
    // BepInEx file write). The game issues hundreds of Debug.Log calls per
    // frame during loading, music transitions, NPC updates, and ship
    // interactions — pure info spam.
    //
    // Behavior:
    //   - Debug.Log(string)             -> SUPPRESSED (info spam)
    //   - Debug.LogWarning(string)      -> PASSED THROUGH + re-emitted via
    //                                       BepInEx LogWarning so it lands in
    //                                       the BepInEx log file immediately
    //   - Debug.LogError(string)        -> PASSED THROUGH + re-emitted via
    //                                       BepInEx LogError so it lands in
    //                                       the BepInEx log file immediately
    //
    // The BepInEx re-emit matters because Unity buffers its own console log
    // and on a hard crash may not flush Player.log before the process dies.
    // BepInEx's ManualLogSource writes to LogOutput.log synchronously, so
    // re-emitting here guarantees the warning/error appears in the log the
    // user actually reads — including the entries right before a crash.

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

    [HarmonyPatch]
    public static class Patch_DebugLogWarning_Passthrough
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Debug), "LogWarning",
                new Type[] { typeof(string) });
        }

        static void Postfix(string message)
        {
            try { PerfOptPlugin.Log.LogWarning("[game] " + message); }
            catch { }
        }
    }

    [HarmonyPatch]
    public static class Patch_DebugLogError_Passthrough
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Debug), "LogError",
                new Type[] { typeof(string) });
        }

        static void Postfix(string message)
        {
            try { PerfOptPlugin.Log.LogError("[game] " + message); }
            catch { }
        }
    }
}