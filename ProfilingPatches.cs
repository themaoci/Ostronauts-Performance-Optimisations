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
    // PROFILING PATCHES — observation only
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

        static void Prefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();

            if (PerfOptPlugin.IsProfiling)
            {
                _memBefore = GC.GetTotalMemory(false);
                _gcBefore = GC.CollectionCount(0);
            }

            PerfOptPlugin.SimStepsThisFrame++;
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

        static void Prefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        static void Postfix(long __state)
        {
            long Elapsed = Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.TEndTurn += Elapsed;
            PerfOptPlugin.CEndTurn++;
            float Ms = (float)((double)Elapsed / (double)Stopwatch.Frequency * 1000.0);
            if (Ms > 20f)
                PerfOptPlugin.Log.LogInfo("[SIM-DIAG] EndTurn " + Ms.ToString("F1") + "ms");
        }
    }

    [HarmonyPatch]
    public static class Patch_GetMove2
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "GetMove2");
        }

        static void Prefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        static void Postfix(long __state)
        {
            long Elapsed = Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.TGetMove2 += Elapsed;
            PerfOptPlugin.CGetMove2++;
            float Ms = (float)((double)Elapsed / (double)Stopwatch.Frequency * 1000.0);
            if (Ms > 20f)
                PerfOptPlugin.Log.LogInfo("[SIM-DIAG] GetMove2 " + Ms.ToString("F1") + "ms");
        }
    }

    [HarmonyPatch]
    public static class Patch_GetWork
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "GetWork");
        }

        static void Prefix(object __instance, ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
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

        static void Prefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        static void Postfix(long __state)
        {
            if (!PerfOptPlugin.IsProfiling) return;
            PerfOptPlugin.TParseCL += Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.CParseCL++;
        }
    }

    [HarmonyPatch]
    public static class Patch_Cleanup
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "Cleanup");
        }

        static void Prefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        static void Postfix(long __state)
        {
            if (!PerfOptPlugin.IsProfiling) return;
            PerfOptPlugin.TCleanup += Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.CCleanup++;
        }
    }

    [HarmonyPatch]
    public static class Patch_UpdateStats
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "UpdateStats");
        }

        static void Prefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        static void Postfix(long __state)
        {
            if (!PerfOptPlugin.IsProfiling) return;
            PerfOptPlugin.TUpdateStats += Stopwatch.GetTimestamp() - __state;
            PerfOptPlugin.CUpdateStats++;
        }
    }

    [HarmonyPatch]
    public static class Patch_StarSystemUpdate
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(StarSystem), "Update");
        }

        private static readonly FieldInfo _aBOsField =
            AccessTools.Field(typeof(StarSystem), "aBOs");

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

            if (PerfOptPlugin.CfgParallelOrbits && PerfOptPlugin.GameLoaded)
            {
                try
                {
                    var aBOs = _aBOsField?.GetValue(__instance)
                        as IDictionary;
                    if (aBOs != null && aBOs.Count > 0)
                    {
                        double epoch = StarSystem.fEpoch;
                        var orbits = new BodyOrbit[aBOs.Count];
                        int idx = 0;
                        foreach (DictionaryEntry entry in aBOs)
                        {
                            if (entry.Value is BodyOrbit bo)
                                orbits[idx++] = bo;
                        }

                        if (idx >= PerfOptPlugin.CfgParallelMinBatch)
                        {
                            long ts = Stopwatch.GetTimestamp();
                            PerfOptPlugin.ParallelBatchesRun++;

                            // Build depth levels so parents process before children
                            var byDepth = new List<List<BodyOrbit>>();
                            var depth = new Dictionary<BodyOrbit, int>();
                            for (int i = 0; i < idx; i++)
                            {
                                var bo = orbits[i];
                                if (!depth.TryGetValue(bo, out var d))
                                {
                                    d = 0;
                                    var p = bo.boParent;
                                    while (p != null) { d++; p = p.boParent; }
                                    depth[bo] = d;
                                }
                                while (byDepth.Count <= d)
                                    byDepth.Add(new List<BodyOrbit>());
                                byDepth[d].Add(bo);
                            }

                            foreach (var level in byDepth)
                            {
                                if (level.Count >= PerfOptPlugin.CfgParallelMinBatch)
                                {
                                    Parallel.ForEach(level, bo =>
                                    {
                                        try { bo.UpdateTime(epoch); }
                                        catch { }
                                    });
                                }
                                else
                                {
                                    for (int i = 0; i < level.Count; i++)
                                    {
                                        try { level[i].UpdateTime(epoch); }
                                        catch { }
                                    }
                                }
                            }

                            PerfOptPlugin.TOrbits +=
                                Stopwatch.GetTimestamp() - ts;
                            PerfOptPlugin.COrbits++;
                        }
                        else
                        {
                            for (int i = 0; i < idx; i++)
                            {
                                try { orbits[i].UpdateTime(epoch); }
                                catch { }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    PerfOptPlugin.Log.LogWarning(
                        $"[PAR-ORB] Parallel orbit update failed: {ex.Message}");
                }
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