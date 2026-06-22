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
    public class Patch_AdvanceSim : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "AdvanceSim");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static bool Prefix(ref long __state)
        {
            __state = Tick();

            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = Mem();
                _gcBefore = GCs();
            }

            PerfOptPlugin.SimStepsThisFrame++;
            return true;
        }

        static void Postfix(long __state)
        {
            long Elapsed = Stopwatch.GetTimestamp() - __state;

            PerfOptPlugin.TAdvanceSim += Elapsed;
            PerfOptPlugin.CAdvanceSim++;

            float Ms = ToMs(Elapsed);
            if (Ms > 100f)
            {
                Log.LogWarning($"[SIM-DIAG] AdvanceSim {Ms:F1}ms GC={(GCs() != _gcBefore ? "Y" : "N")} MemDelta={((Mem() - _memBefore) / 1048576L)}MB");
            }

            if (GCs() == _gcBefore)
            {
                long d = Mem() - _memBefore;
                if (d > 0) PerfOptPlugin.AllocAdvanceSim += d;
            }
        }
    }

    [HarmonyPatch]
    public class Patch_UpdateICOs : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "UpdateICOs");
        }

        static void Prefix(ref long __state)
        {
            __state = Tick();
        }

        static void Postfix(long __state)
        {
            long Elapsed = Stopwatch.GetTimestamp() - __state;

            PerfOptPlugin.TICO += Elapsed;
            PerfOptPlugin.CICO++;

            float Ms = ToMs(Elapsed);
            if (Ms > 50f)
            {
                Log.LogWarning($"[SIM-DIAG] UpdateICOs {Ms:F1}ms");
            }
        }
    }

    [HarmonyPatch]
    public class Patch_EndTurn : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "EndTurn");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static void Prefix(CondOwner __instance, ref long __state)
        {
            __state = Tick();
            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = Mem();
                _gcBefore = GCs();
            }
        }

        static void Postfix(CondOwner __instance, long __state)
        {
            long Elapsed = Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.TEndTurn += Elapsed;
            PerfOptPlugin.CEndTurn++;
            float Ms = ToMs(Elapsed);
            if (Ms > 20f)
            {
                Log.LogInfo(
                    $"[SIM-DIAG] EndTurn {Ms:F1}ms CO={__instance.strName} ({__instance.strNameFriendly}) type={__instance.GetType().Name}");
            }

            if (PerfOptPlugin.IsProfiling && GCs() == _gcBefore)
            {
                long d = Mem() - _memBefore;
                if (d > 0) PerfOptPlugin.AllocEndTurn += d;
            }
        }
    }

    [HarmonyPatch]
    public class Patch_GetMove2 : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "GetMove2");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static void Prefix(CondOwner __instance, ref long __state)
        {
            __state = Tick();
            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = Mem();
                _gcBefore = GCs();
            }
        }

        static void Postfix(CondOwner __instance, long __state)
        {
            long Elapsed = Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.TGetMove2 += Elapsed;
            PerfOptPlugin.CGetMove2++;
            float Ms = ToMs(Elapsed);
            if (Ms > 20f)
            {
                Log.LogWarning(
                    $"[SIM-DIAG] GetMove2 {Ms:F1}ms CO={__instance.strName} ({__instance.strNameFriendly}) type={__instance.GetType().Name}");
            }

            if (PerfOptPlugin.IsProfiling && GCs() == _gcBefore)
            {
                long d = Mem() - _memBefore;
                if (d > 0) PerfOptPlugin.AllocGetMove2 += d;
            }
        }
    }

    [HarmonyPatch]
    public class Patch_GetWork : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "GetWork");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static void Prefix(object __instance, ref long __state)
        {
            __state = Tick();
            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = Mem();
                _gcBefore = GCs();
            }
        }

        static void Postfix(object __instance, long __state)
        {
            long Elapsed = Stopwatch.GetTimestamp() - __state;

            PerfOptPlugin.TGetWork += Elapsed;
            PerfOptPlugin.CGetWork++;

            float Ms = ToMs(Elapsed);
            if (Ms > 50f)
            {
                Log.LogWarning($"[SIM-DIAG] GetWork {Ms:F1}ms {(__instance != null ? __instance.GetType().Name : "null")}");
            }

            if (PerfOptPlugin.IsProfiling && GCs() == _gcBefore)
            {
                long d = Mem() - _memBefore;
                if (d > 0) PerfOptPlugin.AllocGetWork += d;
            }
        }
    }

    [HarmonyPatch]
    public class Patch_ParseCondLoot : PatchBase
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
            __state = Tick();
            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = Mem();
                _gcBefore = GCs();
            }
        }

        static void Postfix(long __state)
        {
            if (!PerfOptPlugin.IsProfiling) return;
            PerfOptPlugin.TParseCL += Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.CParseCL++;

            if (GCs() == _gcBefore)
            {
                long d = Mem() - _memBefore;
                if (d > 0) PerfOptPlugin.AllocParseCL += d;
            }
        }
    }

    [HarmonyPatch]
    public class Patch_Cleanup : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "Cleanup");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static void Prefix(ref long __state)
        {
            __state = Tick();
            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = Mem();
                _gcBefore = GCs();
            }
        }

        static void Postfix(long __state)
        {
            if (!PerfOptPlugin.IsProfiling) return;
            PerfOptPlugin.TCleanup += Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.CCleanup++;

            if (GCs() == _gcBefore)
            {
                long d = Mem() - _memBefore;
                if (d > 0) PerfOptPlugin.AllocCleanup += d;
            }
        }
    }

    [HarmonyPatch]
    public class Patch_UpdateStats : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "UpdateStats");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static void Prefix(ref long __state)
        {
            __state = Tick();
            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = Mem();
                _gcBefore = GCs();
            }
        }

        static void Postfix(long __state)
        {
            if (!PerfOptPlugin.IsProfiling) return;
            PerfOptPlugin.TUpdateStats += Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.CUpdateStats++;

            if (GCs() == _gcBefore)
            {
                long d = Mem() - _memBefore;
                if (d > 0) PerfOptPlugin.AllocUpdateStats += d;
            }
        }
    }

    [HarmonyPatch]
    public class Patch_StarSystemUpdate : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(StarSystem), "Update");
        }

        private static long _memBefore;
        private static int _gcBefore;

        static void Prefix(ref long __state, StarSystem __instance)
        {
            __state = Tick();

            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = Mem();
                _gcBefore = GCs();
            }

            if (!PerfOptPlugin.GameLoaded)
            {
                PerfOptPlugin.GameLoaded = true;
                LogLoadPhase("GAME", "StarSystem.Update — game loaded detected");
            }
        }

        static void Postfix(long __state)
        {
            if (!PerfOptPlugin.IsProfiling) return;

            PerfOptPlugin.TStarSys += Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.CStarSys++;

            if (GCs() == _gcBefore)
            {
                long d = Mem() - _memBefore;
                if (d > 0) PerfOptPlugin.AllocStarSys += d;
            }
        }
    }
}