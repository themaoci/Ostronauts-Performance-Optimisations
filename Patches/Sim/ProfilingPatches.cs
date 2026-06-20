using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Ostranauts.Core;
using System.Threading.Tasks;

namespace OstronautsPerfOpt
{
    // ========================================
    // PROFILING PATCHES — timing + alloc observation
    // (Patch_StarSystemUpdate also detects GameLoaded and clears the
    //  interaction-log cache on first StarSystem.Update call)
    // ========================================

    [HarmonyPatch]
    public static class Patch_AdvanceSim
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "AdvanceSim");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static bool Prefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();

            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = GC.GetTotalMemory(false);
                _gcBefore = GC.CollectionCount(0);
            }

            PerfOptPlugin.SimStepsThisFrame++;
            return true;
        }

        static void Postfix(long __state)
        {
            long Elapsed = Stopwatch.GetTimestamp() - __state;

            PerfOptPlugin.TAdvanceSim += Elapsed;
            PerfOptPlugin.CAdvanceSim++;

            float Ms = (float)((double)Elapsed / (double)Stopwatch.Frequency * 1000.0);
            if (Ms > 100f)
            {
                PerfOptPlugin.Log.LogWarning("[SIM-DIAG] AdvanceSim " + Ms.ToString("F1") + "ms GC=" + (GC.CollectionCount(0) != _gcBefore ? "Y" : "N") + " MemDelta=" + ((GC.GetTotalMemory(false) - _memBefore) / 1048576L).ToString() + "MB");
            }

            if (GC.CollectionCount(0) == _gcBefore)
            {
                long d = GC.GetTotalMemory(false) - _memBefore;
                if (d > 0) PerfOptPlugin.AllocAdvanceSim += d;
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_UpdateICOs
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "UpdateICOs");
        }

        static void Prefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        static void Postfix(long __state)
        {
            long Elapsed = Stopwatch.GetTimestamp() - __state;

            PerfOptPlugin.TICO += Elapsed;
            PerfOptPlugin.CICO++;

            float Ms = (float)((double)Elapsed / (double)Stopwatch.Frequency * 1000.0);
            if (Ms > 50f)
            {
                PerfOptPlugin.Log.LogWarning("[SIM-DIAG] UpdateICOs " + Ms.ToString("F1") + "ms");
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_EndTurn
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "EndTurn");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static void Prefix(CondOwner __instance, ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = GC.GetTotalMemory(false);
                _gcBefore = GC.CollectionCount(0);
            }
        }

        static void Postfix(CondOwner __instance, long __state)
        {
            long Elapsed = Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.TEndTurn += Elapsed;
            PerfOptPlugin.CEndTurn++;
            float Ms = (float)((double)Elapsed / (double)Stopwatch.Frequency * 1000.0);
            if (Ms > 20f)
            {
                PerfOptPlugin.Log.LogInfo(
                    $"[SIM-DIAG] EndTurn {Ms:F1}ms CO={__instance.strName} ({__instance.strNameFriendly}) type={__instance.GetType().Name}");
            }

            if (PerfOptPlugin.IsProfiling && GC.CollectionCount(0) == _gcBefore)
            {
                long d = GC.GetTotalMemory(false) - _memBefore;
                if (d > 0) PerfOptPlugin.AllocEndTurn += d;
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_GetMove2
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "GetMove2");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static void Prefix(CondOwner __instance, ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = GC.GetTotalMemory(false);
                _gcBefore = GC.CollectionCount(0);
            }
        }

        static void Postfix(CondOwner __instance, long __state)
        {
            long Elapsed = Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.TGetMove2 += Elapsed;
            PerfOptPlugin.CGetMove2++;
            float Ms = (float)((double)Elapsed / (double)Stopwatch.Frequency * 1000.0);
            if (Ms > 20f)
            {
                PerfOptPlugin.Log.LogWarning(
                    $"[SIM-DIAG] GetMove2 {Ms:F1}ms CO={__instance.strName} ({__instance.strNameFriendly}) type={__instance.GetType().Name}");
            }

            if (PerfOptPlugin.IsProfiling && GC.CollectionCount(0) == _gcBefore)
            {
                long d = GC.GetTotalMemory(false) - _memBefore;
                if (d > 0) PerfOptPlugin.AllocGetMove2 += d;
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_GetWork
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "GetWork");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static void Prefix(object __instance, ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = GC.GetTotalMemory(false);
                _gcBefore = GC.CollectionCount(0);
            }
        }

        static void Postfix(object __instance, long __state)
        {
            long Elapsed = Stopwatch.GetTimestamp() - __state;

            PerfOptPlugin.TGetWork += Elapsed;
            PerfOptPlugin.CGetWork++;

            float Ms = (float)((double)Elapsed / (double)Stopwatch.Frequency * 1000.0);
            if (Ms > 50f)
            {
                PerfOptPlugin.Log.LogWarning("[SIM-DIAG] GetWork " + Ms.ToString("F1") + "ms " + (__instance != null ? __instance.GetType().Name : "null"));
            }

            if (PerfOptPlugin.IsProfiling && GC.CollectionCount(0) == _gcBefore)
            {
                long d = GC.GetTotalMemory(false) - _memBefore;
                if (d > 0) PerfOptPlugin.AllocGetWork += d;
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_ParseCondLoot
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "ParseCondLoot",
                new Type[] { typeof(string), typeof(double) });
        }

        private static long _memBefore;
        private static int _gcBefore;

        static void Prefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = GC.GetTotalMemory(false);
                _gcBefore = GC.CollectionCount(0);
            }
        }

        static void Postfix(long __state)
        {
            if (!PerfOptPlugin.IsProfiling) return;
            PerfOptPlugin.TParseCL += Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.CParseCL++;

            if (GC.CollectionCount(0) == _gcBefore)
            {
                long d = GC.GetTotalMemory(false) - _memBefore;
                if (d > 0) PerfOptPlugin.AllocParseCL += d;
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_Cleanup
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "Cleanup");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static void Prefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = GC.GetTotalMemory(false);
                _gcBefore = GC.CollectionCount(0);
            }
        }

        static void Postfix(long __state)
        {
            if (!PerfOptPlugin.IsProfiling) return;
            PerfOptPlugin.TCleanup += Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.CCleanup++;

            if (GC.CollectionCount(0) == _gcBefore)
            {
                long d = GC.GetTotalMemory(false) - _memBefore;
                if (d > 0) PerfOptPlugin.AllocCleanup += d;
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_UpdateStats
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "UpdateStats");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static void Prefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = GC.GetTotalMemory(false);
                _gcBefore = GC.CollectionCount(0);
            }
        }

        static void Postfix(long __state)
        {
            if (!PerfOptPlugin.IsProfiling) return;
            PerfOptPlugin.TUpdateStats += Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.CUpdateStats++;

            if (GC.CollectionCount(0) == _gcBefore)
            {
                long d = GC.GetTotalMemory(false) - _memBefore;
                if (d > 0) PerfOptPlugin.AllocUpdateStats += d;
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_StarSystemUpdate
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(StarSystem), "Update");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static void Prefix(ref long __state, StarSystem __instance)
        {
            __state = Stopwatch.GetTimestamp();

            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = GC.GetTotalMemory(false);
                _gcBefore = GC.CollectionCount(0);
            }

            if (!PerfOptPlugin.GameLoaded)
            {
                PerfOptPlugin.GameLoaded = true;
                Patch_SuppressInteractionLog.ClearCache();
                PerfOptPlugin.Log.LogInfo(
                    "[GAME] StarSystem.Update — game loaded detected");
            }
        }

        static void Postfix(long __state)
        {
            if (!PerfOptPlugin.IsProfiling) return;

            PerfOptPlugin.TStarSys += Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.CStarSys++;

            if (GC.CollectionCount(0) == _gcBefore)
            {
                long d = GC.GetTotalMemory(false) - _memBefore;
                if (d > 0) PerfOptPlugin.AllocStarSys += d;
            }
        }
    }
}