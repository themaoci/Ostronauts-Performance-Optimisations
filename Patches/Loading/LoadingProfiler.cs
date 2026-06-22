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
        private static readonly object _entriesLock = new object();
        internal static readonly Stopwatch TotalSW = new Stopwatch();
        internal static bool Active;

        internal static void Start()
        {
            lock (_entriesLock)
            {
                Entries.Clear();
            }
            TotalSW.Restart();
            Active = true;
            PerfOptPlugin.Log.LogInfo("[LOAD-PROF] Profiler started");
        }

        internal static void Stop()
        {
            Active = false;
            TotalSW.Stop();
            PerfOptPlugin.Log.LogInfo($"[LOAD-PROF] Profiler stopped");
            Dump();
        }

        internal static void RegisterPatches(HarmonyLib.Harmony harmony)
        {
            int ok = 0, fail = 0;

            void TryPatch(string name, MethodBase target, HarmonyMethod prefix, HarmonyMethod postfix)
            {
                if (target == null)
                {
                    PerfOptPlugin.Log.LogWarning($"[LOAD-PROF] {name}: target NOT FOUND");
                    fail++;
                    return;
                }
                try
                {
                    harmony.Patch(target, prefix, postfix);
                    ok++;
                    PerfOptPlugin.Log.LogInfo($"[LOAD-PROF] Patched {name}");
                }
                catch (Exception ex)
                {
                    fail++;
                    PerfOptPlugin.Log.LogWarning($"[LOAD-PROF] {name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            var phasePre = new HarmonyMethod(AccessTools.Method(
                typeof(LoadingProfilerPatches), "Phase_Pre"));

            TryPatch("StarSystem.Init",
                AccessTools.Method(typeof(StarSystem), "Init",
                    new[] { typeof(JsonStarSystemSave), typeof(JsonShip[]) }),
                phasePre,
                new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "StarInit_Post")));

            TryPatch("Ship.InitShip",
                AccessTools.Method(typeof(Ship), "InitShip",
                    new[] { typeof(bool), typeof(Ship.Loaded), typeof(string) }),
                new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "InitShip_Pre")),
                new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "InitShip_Post")));

            TryPatch("Ship.VisitCOs",
                AccessTools.Method(typeof(Ship), "VisitCOs",
                    new[] { typeof(CondOwnerVisitor), typeof(bool), typeof(bool), typeof(bool) }),
                phasePre,
                new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "VisitCOs_Post")));

            TryPatch("CrewSim.UpdateICOs",
                AccessTools.Method(typeof(CrewSim), "UpdateICOs"),
                phasePre,
                new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "UpdateICOs_Post")));

            TryPatch("LoadSaveFile(string)",
                AccessTools.Method(typeof(DataHandler), "LoadSaveFile",
                    new[] { typeof(string) }),
                new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "LoadSaveFile_Pre")),
                new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "LoadSaveFile_Post")));

            TryPatch("LoadSaveFile(string, Dict<byte>)",
                AccessTools.Method(typeof(DataHandler), "LoadSaveFile",
                    new[] { typeof(string), typeof(Dictionary<string, byte[]>) }),
                new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "LoadSaveFile_Pre")),
                new HarmonyMethod(AccessTools.Method(typeof(LoadingProfilerPatches), "LoadSaveFile_Post")));

            // JsonToData patches removed — HarmonyMethod with __originalMethod
            // causes IL Compile Error on generic methods. Minor profiling loss.

            if (fail > 0)
                PerfOptPlugin.Log.LogInfo($"[LOAD-PROF] {ok} ok, {fail} failed");
        }

        internal static void Record(string phase, string detail, long ms, long allocKB, int gcDelta)
        {
            if (!Active) return;
            lock (_entriesLock)
            {
                Entries.Add(new Entry
                {
                    Phase = phase,
                    Detail = detail,
                    Ms = ms,
                    AllocKB = allocKB,
                    GcDelta = gcDelta
                });
            }
        }

        private static void Dump()
        {
            Entry[] snapshot;
            lock (_entriesLock)
            {
                if (Entries.Count == 0)
                {
                    PerfOptPlugin.Log.LogWarning("[LOAD-PROF] No entries recorded");
                    return;
                }
                snapshot = Entries.ToArray();
            }

            int totalEntries = snapshot.Length;
            var sb = new StringBuilder(4096);
            sb.AppendLine($"[LOAD-PROF] {totalEntries} entries in {TotalSW.ElapsedMilliseconds / 1000.0:F1}s:");

            sb.AppendLine("  Timeline:");
            for (int i = 0; i < snapshot.Length; i++)
            {
                var e = snapshot[i];
                sb.Append($"    {e.Ms,7}ms  +{e.AllocKB / 1024.0,7:F1}MB  GCx{e.GcDelta}");
                sb.AppendLine($"  {e.Phase}  {e.Detail}");
            }

            var byTime = snapshot.OrderByDescending(e => e.Ms).Take(15);
            sb.AppendLine("  Top 15 slowest:");
            foreach (var e in byTime)
            {
                sb.Append($"    {e.Ms,7}ms  +{e.AllocKB / 1024.0,7:F1}MB  GCx{e.GcDelta}");
                sb.AppendLine($"  {e.Phase}  {e.Detail}");
            }

            var byPhase = snapshot.GroupBy(e => e.Phase)
                .Select(g => new { Phase = g.Key, Count = g.Count(), TotalMs = g.Sum(e => e.Ms), TotalKB = g.Sum(e => e.AllocKB) })
                .OrderByDescending(x => x.TotalMs);
            sb.AppendLine("  By phase:");
            foreach (var p in byPhase)
                sb.AppendLine($"    {p.Count,4}x  {p.TotalMs,8}ms  +{p.TotalKB / 1024.0:F1}MB  {p.Phase}");

            long totalMs = snapshot.Sum(e => e.Ms);
            long totalKB = snapshot.Sum(e => e.AllocKB);
            int totalGC = snapshot.Sum(e => e.GcDelta);
            sb.AppendLine($"  TOTAL: {totalMs}ms +{totalKB / 1024.0:F1}MB GCx{totalGC}");

            PerfOptPlugin.Log.LogWarning(sb.ToString());
        }
    }

    internal static class LoadingProfilerPatches
    {
        [ThreadStatic] internal static string CurDetail;
        [ThreadStatic] internal static long CurStart;
        [ThreadStatic] internal static long CurMemBefore;
        [ThreadStatic] internal static int CurGcBefore;
        [ThreadStatic] internal static string CurJsonType;

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
            string regID = __instance?.strRegID ?? "null";
            CurDetail = $"{regID} (load={nLoad})";
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
            string detail = __instance?.strRegID ?? "null";
            long ms = (Stopwatch.GetTimestamp() - CurStart) * 1000 / Stopwatch.Frequency;
            long allocKB = (GC.GetTotalMemory(false) - CurMemBefore) / 1024;
            int gcDelta = GC.CollectionCount(0) - CurGcBefore;
            LoadingProfiler.Record("Ship.VisitCOs", detail, ms, allocKB, gcDelta);
        }

        internal static void UpdateICOs_Post()
        {
            long ms = (Stopwatch.GetTimestamp() - CurStart) * 1000 / Stopwatch.Frequency;
            long allocKB = (GC.GetTotalMemory(false) - CurMemBefore) / 1024;
            int gcDelta = GC.CollectionCount(0) - CurGcBefore;
            LoadingProfiler.Record("CrewSim.UpdateICOs", "", ms, allocKB, gcDelta);
        }

        internal static void JsonToData_Pre(string __0)
        {
            CurDetail = System.IO.Path.GetFileName(__0);
            CurStart = Stopwatch.GetTimestamp();
            CurMemBefore = GC.GetTotalMemory(false);
            CurGcBefore = GC.CollectionCount(0);
            // Type name unavailable from Harmony prefix (IL Compile Error
            // with __originalMethod on generic methods). Use "?" placeholder.
            CurJsonType = "?";
        }

        internal static void JsonToData_Post()
        {
            long ms = (Stopwatch.GetTimestamp() - CurStart) * 1000 / Stopwatch.Frequency;
            long allocKB = (GC.GetTotalMemory(false) - CurMemBefore) / 1024;
            int gcDelta = GC.CollectionCount(0) - CurGcBefore;
            string type = CurJsonType ?? "?";
            LoadingProfiler.Record($"JsonToData<{type}>", CurDetail, ms, allocKB, gcDelta);
        }

        internal static void JsonToData3_Post()
        {
            JsonToData_Post();
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
