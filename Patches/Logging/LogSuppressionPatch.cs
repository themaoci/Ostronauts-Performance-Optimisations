using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Ostranauts.Core;

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

        static bool Prefix(string message)
        {
            if (PerfOptPlugin.SuppressDebugLog)
                return false;

            if (message != null &&
                (message.IndexOf("SysLootSpawnerLot", StringComparison.Ordinal) >= 0 ||
                 message.IndexOf("missing save data", StringComparison.Ordinal) >= 0))
                return false;

            return true;
        }

        static void Postfix(string message)
        {
            if (PerfOptPlugin.SuppressDebugLog) return;
            if (message != null &&
                (message.IndexOf("SysLootSpawnerLot", StringComparison.Ordinal) >= 0 ||
                 message.IndexOf("missing save data", StringComparison.Ordinal) >= 0))
                return;
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

    // ========================================
    // LOG HANDLER: IsDuplicate alloc-free replacement
    // ========================================
    // Vanilla LogHandler.IsDuplicate (v2 LogHandler.cs:63) does:
    //     string[] array = this.Log.Split(new string[]{ _lineStart }, ...);
    //     return array.Length - 1 > 0 && array[array.Length - 1].Contains(logString);
    // Split allocates a string array + N substring slices every call. With 258
    // LogMessage call sites and every Debug.LogWarning/Error routed through
    // LogNonStandardLogs -> LogMessage -> IsDuplicate, this fires hundreds of
    // times per frame during loading and combat.
    //
    // Fix: Prefix that finds the last _lineStart marker via LastIndexOf and
    // checks IndexOf(logString) from that offset. Zero allocations on the
    // common "not a duplicate" path; no Split array.

    [HarmonyPatch]
    public static class Patch_LogHandler_IsDuplicate
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(LogHandler), "IsDuplicate");
        }

        private static readonly FieldInfo _logField =
            AccessTools.Field(typeof(LogHandler), "<Log>k__BackingField")
            ?? AccessTools.Field(typeof(LogHandler), "Log");
        private static readonly FieldInfo _lineStartField =
            AccessTools.Field(typeof(LogHandler), "_lineStart");

        static bool Prefix(LogHandler __instance, string logString,
            ref bool __result)
        {
            if (_logField == null || _lineStartField == null)
                return true;

            try
            {
                string log = _logField.GetValue(__instance) as string;
                if (string.IsNullOrEmpty(log) || string.IsNullOrEmpty(logString))
                {
                    __result = false;
                    return false;
                }

                string lineStart = _lineStartField.GetValue(__instance) as string
                    ?? "* ";
                int markerLen = lineStart.Length;

                int lastMarker = log.LastIndexOf(lineStart,
                    StringComparison.Ordinal);
                if (lastMarker < 0)
                {
                    __result = false;
                    return false;
                }

                int foundAt = log.IndexOf(logString, lastMarker + markerLen,
                    StringComparison.Ordinal);
                if (foundAt < 0)
                {
                    __result = false;
                    return false;
                }

                int nextMarker = log.IndexOf(lineStart, foundAt,
                    StringComparison.Ordinal);
                __result = nextMarker < 0
                    || foundAt < nextMarker;
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    // ========================================
    // LOG HANDLER: TrimLog alloc-free replacement
    // ========================================
    // Vanilla LogHandler.TrimLog (v2 LogHandler.cs:93) does:
    //     string[] array = this.Log.Split(new string[]{ _lineStart }, ...);
    //     if (array.Length < MaxLogSize) return;
    //     int num = (int)(MaxLogSize * 0.8f);
    //     string value = array[array.Length - num];
    //     int num2 = this.Log.IndexOf(value, StringComparison.Ordinal);
    //     this.Log = this.Log.Substring(num2, ...);
    // Same Split-based pattern, fires after every LogMessage that grows the log.
    //
    // Fix: Prefix that counts _lineStart occurrences manually with IndexOf,
    // finds the cutoff marker position, and Substrings once. Zero array alloc.

    [HarmonyPatch]
    public static class Patch_LogHandler_TrimLog
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(LogHandler), "TrimLog");
        }

        private static readonly FieldInfo _logField =
            AccessTools.Field(typeof(LogHandler), "<Log>k__BackingField")
            ?? AccessTools.Field(typeof(LogHandler), "Log");
        private static readonly FieldInfo _lineStartField =
            AccessTools.Field(typeof(LogHandler), "_lineStart");
        private static readonly FieldInfo _maxLogSizeField =
            AccessTools.Field(typeof(LogHandler), "MaxLogSize");

        static bool Prefix(LogHandler __instance)
        {
            if (_logField == null || _lineStartField == null
                || _maxLogSizeField == null)
                return true;

            try
            {
                string log = _logField.GetValue(__instance) as string;
                if (string.IsNullOrEmpty(log))
                    return false;

                string lineStart = _lineStartField.GetValue(__instance) as string
                    ?? "* ";
                int maxLogSize = (int)(_maxLogSizeField.GetValue(__instance) ?? 200);
                int markerLen = lineStart.Length;

                int count = 0;
                int idx = 0;
                while ((idx = log.IndexOf(lineStart, idx,
                    StringComparison.Ordinal)) >= 0)
                {
                    count++;
                    idx += markerLen;
                }

                if (count < maxLogSize)
                    return false;

                int keepCount = (int)(maxLogSize * 0.8f);
                int skipCount = count - keepCount;
                if (skipCount <= 0)
                    return false;

                int cutIdx = -1;
                int found = 0;
                idx = 0;
                while ((idx = log.IndexOf(lineStart, idx,
                    StringComparison.Ordinal)) >= 0)
                {
                    found++;
                    if (found == skipCount)
                    {
                        cutIdx = idx;
                        break;
                    }
                    idx += markerLen;
                }

                if (cutIdx <= 0)
                    return false;

                string trimmed = log.Substring(cutIdx,
                    log.Length - cutIdx);
                _logField.SetValue(__instance, trimmed);
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}