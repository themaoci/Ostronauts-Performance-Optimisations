using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Ostranauts.Core;
using Ostranauts.Ships;

namespace OstronautsPerfOpt
{
    // ========================================
    // LOADING PROFILER: Method-level instrumentation
    // ========================================
    // The stack profiler can't sample during loading (main thread is in
    // native JSON/file I/O code where thread suspension fails). This
    // profiler instruments the actual heavy loading methods directly:
    //   - StarSystem.Init (IEnumerator) — builds all ships from JSON
    //   - Ship.InitShip — full ship initialization (blocks, items, COs)
    //   - Ship.VisitCOs — traverses all COs on ship
    //   - CrewSim.UpdateICOs — updates all interaction-capable owners
    //   - DataHandler.JsonToData — JSON deserialization (per-file)

    internal static class LoadingProfiler
    {
        internal struct Entry
        {
            public string Phase;
            public string Detail;
            public long Ms;
            public long AllocKB;
            public int GcDelta;
        }

        internal static readonly List<Entry> Entries = new List<Entry>(256);
        internal static readonly Stopwatch TotalSW = new Stopwatch();
        internal static bool Active;

        internal static void Start()
        {
            Entries.Clear();
            TotalSW.Restart();
            Active = true;
            PerfOptPlugin.Log.LogInfo("[LOAD-PROF] Profiler started");
        }

        internal static void Stop()
        {
            Active = false;
            TotalSW.Stop();
            PerfOptPlugin.Log.LogInfo($"[LOAD-PROF] Profiler stopped — {Entries.Count} entries captured");
            Dump();
        }

        internal static void RegisterPatches(HarmonyLib.Harmony harmony)
        {
            var phasePre = new HarmonyMethod(AccessTools.Method(
                typeof(LoadingProfilerPatches), "Phase_Pre"));

            // 1. StarSystem.Init(JsonStarSystemSave, JsonShip[])
            var starInit = AccessTools.Method(typeof(StarSystem), "Init",
                new[] { typeof(JsonStarSystemSave), typeof(JsonShip[]) });
            if (starInit != null)
            {
                harmony.Patch(starInit, phasePre,
                    new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "StarInit_Post")));
                PerfOptPlugin.Log.LogInfo("[LOAD-PROF] Patched StarSystem.Init");
            }
            else
                PerfOptPlugin.Log.LogWarning("[LOAD-PROF] StarSystem.Init NOT FOUND");

            // 2. Ship.InitShip(bool, Ship.Loaded, string)
            var initShip = AccessTools.Method(typeof(Ship), "InitShip",
                new[] { typeof(bool), typeof(Ship.Loaded), typeof(string) });
            if (initShip != null)
            {
                harmony.Patch(initShip,
                    new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "InitShip_Pre")),
                    new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "InitShip_Post")));
                PerfOptPlugin.Log.LogInfo("[LOAD-PROF] Patched Ship.InitShip");
            }
            else
                PerfOptPlugin.Log.LogWarning("[LOAD-PROF] Ship.InitShip NOT FOUND");

            // 3. Ship.VisitCOs(CondOwnerVisitor, bool, bool, bool)
            var visitCOs = AccessTools.Method(typeof(Ship), "VisitCOs",
                new[] { typeof(CondOwnerVisitor), typeof(bool), typeof(bool), typeof(bool) });
            if (visitCOs != null)
            {
                harmony.Patch(visitCOs, phasePre,
                    new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "VisitCOs_Post")));
                PerfOptPlugin.Log.LogInfo("[LOAD-PROF] Patched Ship.VisitCOs");
            }
            else
                PerfOptPlugin.Log.LogWarning("[LOAD-PROF] Ship.VisitCOs NOT FOUND");

            // 4. CrewSim.UpdateICOs
            var updateICOs = AccessTools.Method(typeof(CrewSim), "UpdateICOs");
            if (updateICOs != null)
            {
                harmony.Patch(updateICOs, phasePre,
                    new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "UpdateICOs_Post")));
                PerfOptPlugin.Log.LogInfo("[LOAD-PROF] Patched CrewSim.UpdateICOs");
            }
            else
                PerfOptPlugin.Log.LogWarning("[LOAD-PROF] CrewSim.UpdateICOs NOT FOUND");

            // 5. LoadSaveFile(string)
            var loadSave1 = AccessTools.Method(typeof(DataHandler), "LoadSaveFile",
                new[] { typeof(string) });
            if (loadSave1 != null)
            {
                harmony.Patch(loadSave1,
                    new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "LoadSaveFile_Pre")),
                    new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "LoadSaveFile_Post")));
                PerfOptPlugin.Log.LogInfo("[LOAD-PROF] Patched LoadSaveFile(string)");
            }

            // 6. LoadSaveFile(string, Dict<byte>)
            var loadSave2 = AccessTools.Method(typeof(DataHandler), "LoadSaveFile",
                new[] { typeof(string), typeof(Dictionary<string, byte[]>) });
            if (loadSave2 != null)
            {
                harmony.Patch(loadSave2,
                    new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "LoadSaveFile_Pre")),
                    new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "LoadSaveFile_Post")));
                PerfOptPlugin.Log.LogInfo("[LOAD-PROF] Patched LoadSaveFile(string, Dict<byte>)");
            }

            // 7. JsonToData<T>(string, Dict<T>) — 2-param generic
            var jsonToData2 = typeof(DataHandler).GetMethods()
                .Where(m => m.Name == "JsonToData" && m.IsGenericMethod && m.GetParameters().Length == 2)
                .FirstOrDefault();
            if (jsonToData2 != null)
            {
                harmony.Patch(jsonToData2,
                    new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "JsonToData_Pre")),
                    new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "JsonToData_Post")));
                PerfOptPlugin.Log.LogInfo("[LOAD-PROF] Patched JsonToData<T>(string, Dict<T>)");
            }

            // 8. JsonToData<T>(string, Dict<T>, Dict<byte>) — 3-param
            var jsonToData3 = typeof(DataHandler).GetMethods()
                .Where(m => m.Name == "JsonToData" && m.IsGenericMethod && m.GetParameters().Length == 3)
                .FirstOrDefault();
            if (jsonToData3 != null)
            {
                harmony.Patch(jsonToData3,
                    new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "JsonToData_Pre")),
                    new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "JsonToData3_Post")));
                PerfOptPlugin.Log.LogInfo("[LOAD-PROF] Patched JsonToData<T>(string, Dict<T>, Dict<byte>)");
            }
        }

        internal static void Record(string phase, string detail, long ms, long allocKB, int gcDelta)
        {
            if (!Active) return;
            Entries.Add(new Entry
            {
                Phase = phase,
                Detail = detail,
                Ms = ms,
                AllocKB = allocKB,
                GcDelta = gcDelta
            });
        }

        private static void Dump()
        {
            if (Entries.Count == 0)
            {
                PerfOptPlugin.Log.LogWarning("[LOAD-PROF] No entries recorded");
                return;
            }

            var sb = new StringBuilder(4096);
            sb.AppendLine($"[LOAD-PROF] {Entries.Count} entries in {TotalSW.ElapsedMilliseconds / 1000.0:F1}s:");

            // Timeline (in call order)
            sb.AppendLine("  Timeline:");
            foreach (var e in Entries)
            {
                sb.Append($"    {e.Ms,7}ms  +{e.AllocKB / 1024.0,7:F1}MB  GCx{e.GcDelta}");
                sb.AppendLine($"  {e.Phase}  {e.Detail}");
            }

            // Top 15 slowest
            var byTime = Entries.OrderByDescending(e => e.Ms).Take(15);
            sb.AppendLine("  Top 15 slowest:");
            foreach (var e in byTime)
            {
                sb.Append($"    {e.Ms,7}ms  +{e.AllocKB / 1024.0,7:F1}MB  GCx{e.GcDelta}");
                sb.AppendLine($"  {e.Phase}  {e.Detail}");
            }

            // Aggregate by phase
            var byPhase = Entries.GroupBy(e => e.Phase)
                .Select(g => new { Phase = g.Key, Count = g.Count(), TotalMs = g.Sum(e => e.Ms), TotalKB = g.Sum(e => e.AllocKB) })
                .OrderByDescending(x => x.TotalMs);
            sb.AppendLine("  By phase:");
            foreach (var p in byPhase)
            {
                sb.AppendLine($"    {p.Count,4}x  {p.TotalMs,8}ms  +{p.TotalKB / 1024.0:F1}MB  {p.Phase}");
            }

            long totalMs = Entries.Sum(e => e.Ms);
            long totalKB = Entries.Sum(e => e.AllocKB);
            int totalGC = Entries.Sum(e => e.GcDelta);
            sb.AppendLine($"  TOTAL: {totalMs}ms +{totalKB / 1024.0:F1}MB GCx{totalGC}");

            PerfOptPlugin.Log.LogWarning(sb.ToString());
        }
    }

    // ========================================
    // LOADING PROFILER PATCHES
    // ========================================

    internal static class LoadingProfilerPatches
    {
        // --- ThreadStatic timing state ---
        [ThreadStatic] internal static string CurDetail;
        [ThreadStatic] internal static long CurStart;
        [ThreadStatic] internal static long CurMemBefore;
        [ThreadStatic] internal static int CurGcBefore;

        internal static void Phase_Pre()
        {
            CurStart = Stopwatch.GetTimestamp();
            CurMemBefore = GC.GetTotalMemory(false);
            CurGcBefore = GC.CollectionCount(0);
            CurDetail = "";
        }

        internal static void StarInit_Post()
        {
            long ms = (Stopwatch.GetTimestamp() - CurStart) * 1000 / Stopwatch.Frequency;
            long allocKB = (GC.GetTotalMemory(false) - CurMemBefore) / 1024;
            int gcDelta = GC.CollectionCount(0) - CurGcBefore;
            LoadingProfiler.Record("StarSystem.Init", "", ms, allocKB, gcDelta);
        }

        internal static void InitShip_Pre(Ship __instance, bool bTemplateOnly, Ship.Loaded nLoad, string strRegIDNew)
        {
            CurDetail = $"{__instance.strRegID} (load={nLoad})";
            CurStart = Stopwatch.GetTimestamp();
            CurMemBefore = GC.GetTotalMemory(false);
            CurGcBefore = GC.CollectionCount(0);
        }

        internal static void InitShip_Post(Ship __instance, bool bTemplateOnly, Ship.Loaded nLoad, string strRegIDNew)
        {
            long ms = (Stopwatch.GetTimestamp() - CurStart) * 1000 / Stopwatch.Frequency;
            long allocKB = (GC.GetTotalMemory(false) - CurMemBefore) / 1024;
            int gcDelta = GC.CollectionCount(0) - CurGcBefore;
            LoadingProfiler.Record("Ship.InitShip", CurDetail, ms, allocKB, gcDelta);
        }

        internal static void VisitCOs_Post(Ship __instance)
        {
            long ms = (Stopwatch.GetTimestamp() - CurStart) * 1000 / Stopwatch.Frequency;
            long allocKB = (GC.GetTotalMemory(false) - CurMemBefore) / 1024;
            int gcDelta = GC.CollectionCount(0) - CurGcBefore;
            LoadingProfiler.Record("Ship.VisitCOs", __instance.strRegID, ms, allocKB, gcDelta);
        }

        internal static void UpdateICOs_Post()
        {
            long ms = (Stopwatch.GetTimestamp() - CurStart) * 1000 / Stopwatch.Frequency;
            long allocKB = (GC.GetTotalMemory(false) - CurMemBefore) / 1024;
            int gcDelta = GC.CollectionCount(0) - CurGcBefore;
            LoadingProfiler.Record("CrewSim.UpdateICOs", "", ms, allocKB, gcDelta);
        }

        internal static void JsonToData_Pre(string strFile)
        {
            CurDetail = System.IO.Path.GetFileName(strFile);
            CurStart = Stopwatch.GetTimestamp();
            CurMemBefore = GC.GetTotalMemory(false);
            CurGcBefore = GC.CollectionCount(0);
        }

        internal static void JsonToData_Post(MethodBase __originalMethod)
        {
            long ms = (Stopwatch.GetTimestamp() - CurStart) * 1000 / Stopwatch.Frequency;
            long allocKB = (GC.GetTotalMemory(false) - CurMemBefore) / 1024;
            int gcDelta = GC.CollectionCount(0) - CurGcBefore;
            string type = __originalMethod.IsGenericMethod
                ? __originalMethod.GetGenericArguments().FirstOrDefault()?.Name ?? "?"
                : "?";
            LoadingProfiler.Record($"JsonToData<{type}>", CurDetail, ms, allocKB, gcDelta);
        }

        internal static void JsonToData3_Post(MethodBase __originalMethod)
        {
            JsonToData_Post(__originalMethod);
        }

        internal static void LoadSaveFile_Pre(string strFileName)
        {
            CurDetail = System.IO.Path.GetFileName(strFileName);
            CurStart = Stopwatch.GetTimestamp();
            CurMemBefore = GC.GetTotalMemory(false);
            CurGcBefore = GC.CollectionCount(0);
        }

        internal static void LoadSaveFile_Post()
        {
            long ms = (Stopwatch.GetTimestamp() - CurStart) * 1000 / Stopwatch.Frequency;
            long allocKB = (GC.GetTotalMemory(false) - CurMemBefore) / 1024;
            int gcDelta = GC.CollectionCount(0) - CurGcBefore;
            LoadingProfiler.Record("LoadSaveFile", CurDetail, ms, allocKB, gcDelta);
        }
    }
}