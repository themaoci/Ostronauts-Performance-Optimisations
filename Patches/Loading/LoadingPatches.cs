using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Ostranauts.Core;
using Ostranauts.Ships;

namespace OstronautsPerfOpt
{
    // ========================================
    // LOADING: Parallel All-FileLoader Loading
    // ========================================
    // Replaces LoadManager.LoadDataHandlerDelegates() with a two-phase approach
    // that preserves the original flow exactly:
    // Phase 1 (Parallel ships only): ships distributed across N LoaderThreads
    //   (same as original — ships use thread-safe JsonToData file overload)
    // Phase 2 (Sequential fileLoaders on calling thread): fileLoaders run their
    //   loadDelegate() sequentially on the main/calling thread, matching
    //   original behavior. Many loadDelegate()s call Unity APIs that are
    //   not thread-safe (Resources.Load, Instantiate, GetComponent).
    // Phase 3 (Sequential mod post-load): PerModPostLoadAsyncOkay callbacks,
    //   mod name tracking, complete flags — identical to original.

    [HarmonyPatch]
    public static class Patch_ParallelLoad
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(LoadManager), "LoadDataHandlerDelegates");
        }

        private static readonly Stopwatch s_loadSW = new Stopwatch();

        static bool Prefix(LoadManager __instance)
        {
            if (!PerfOptPlugin.CfgParallelLoading)
                return true;

            s_loadSW.Restart();
            PerfOptPlugin.Log.LogInfo("[PARLOAD] Starting parallel loading...");

            PerfOptPlugin.SuppressDebugLog = true;
            bool prevSuppressErrors = DataHandler.bSuppressGetErrors;
            DataHandler.bSuppressGetErrors = true;

            DataHandler.loaded = 0;
            DataHandler.percentComplete = 0f;

            int ThreadCount = PerfOptPlugin.CfgParallelLoadThreads;
            if (ThreadCount < 1) ThreadCount = 1;
            if (ThreadCount > 16) ThreadCount = 16;

            __instance.loaderThreads.Clear();
            List<LoaderThread> Threads = new List<LoaderThread>(ThreadCount);
            for (int i = 0; i < ThreadCount; i++)
            {
                LoaderThread Lt = new LoaderThread();
                Threads.Add(Lt);
                __instance.loaderThreads.Add(Lt);
            }

            // Distribute ONLY ships across LoaderThreads (original behavior)
            int RoundRobin = 0;
            int TotalShips = 0;
            DataHandler.toLoad = 0;

            for (int j = 0; j < LoadManager.LoadingQueue.Count; j++)
            {
                ModLoader Mod = LoadManager.LoadingQueue[j];
                for (int k = 0; k < Mod.ships.Count; k++)
                {
                    Threads[RoundRobin % ThreadCount].fileLoaders.Add(Mod.ships[k]);
                    RoundRobin++;
                    TotalShips++;
                    DataHandler.toLoad++;
                }
            }

            PerfOptPlugin.Log.LogInfo("[PARLOAD] Distributing " + TotalShips.ToString()
                + " ships across " + ThreadCount.ToString() + " threads (fileLoaders stay sequential)");

            // Start ship-loading threads
            for (int i = 0; i < ThreadCount; i++)
            {
                Threads[i].t = new Thread(Threads[i].Run);
                Threads[i].t.IsBackground = true;
                Threads[i].t.Start();
            }

            // Process fileLoaders sequentially on calling thread (original behavior)
            // Ships run in parallel in the background; fileLoaders run here.
            // LoadingQueue is emptied in original order with per-file progress tracking.
            int PrevLoaded = 0;
            while (LoadManager.LoadingQueue.Count > 0)
            {
                ModLoader Mod = LoadManager.LoadingQueue[0];
                LoadManager.LoadingQueue.RemoveAt(0);

                if (Mod != null && Mod.JsonModInfo != null && !string.IsNullOrEmpty(Mod.JsonModInfo.strName))
                {
                    lock (LoadManager.outputLock)
                    {
                        LoadManager.modNamesStartedLoading.Add(Mod.JsonModInfo.strName);
                    }
                }

                for (int l = 0; l < Mod.fileLoaders.Count; l++)
                {
                    lock (LoadManager.mainLoadLock)
                    {
                        if (LoadManager.mainLoadTerminate)
                        {
                            for (int i = 0; i < ThreadCount; i++)
                            {
                                lock (Threads[i].handle)
                                    Threads[i].terminate = true;
                            }
                            for (int i = 0; i < ThreadCount; i++)
                            {
                                if (Threads[i].t != null && Threads[i].t.IsAlive)
                                    Threads[i].t.Join();
                            }
                            return false;
                        }
                    }

                    FileLoader fileLoader = Mod.fileLoaders[l];
                    try { fileLoader.loadDelegate(); }
                    catch (Exception ex) { PerfOptPlugin.Log.LogWarning($"[PARLOAD] fileLoader failed: {ex.Message}"); }

                    if (fileLoader != null && !string.IsNullOrEmpty(fileLoader.fileName))
                    {
                        lock (LoadManager.outputLock)
                        {
                            LoadManager.fileNamesLoaded.Add(Path.GetFileNameWithoutExtension(fileLoader.fileName));
                            DataHandler.loaded++;
                        }
                    }
                }

                for (int m = 0; m < Mod.PerModPostLoadAsyncOkay.Count; m++)
                    Mod.PerModPostLoadAsyncOkay[m]();

                if (Mod != null && Mod.JsonModInfo != null && !string.IsNullOrEmpty(Mod.JsonModInfo.strName))
                {
                    lock (LoadManager.outputLock)
                    {
                        LoadManager.modNamesCompletedLoading.Add(Mod.JsonModInfo.strName);
                    }
                }
                Mod.complete = true;

                // Update progress from parallel ship loading too
                int CurrentLoaded;
                lock (LoadManager.outputLock)
                {
                    CurrentLoaded = LoadManager.fileNamesLoaded.Count;
                }
                if (CurrentLoaded != PrevLoaded)
                {
                    DataHandler.loaded = CurrentLoaded;
                    PrevLoaded = CurrentLoaded;
                }
            }

            // Join ship-loading threads
            for (int i = 0; i < ThreadCount; i++)
            {
                if (Threads[i].t != null && Threads[i].t.IsAlive)
                    Threads[i].t.Join();
            }

            lock (LoadManager.outputLock)
            {
                DataHandler.loaded = LoadManager.fileNamesLoaded.Count;
            }

            DataHandler.AllPostLoadAsync();
            DataHandler.bAsyncLoaded = true;

            PerfOptPlugin.SuppressDebugLog = false;
            DataHandler.bSuppressGetErrors = prevSuppressErrors;

            s_loadSW.Stop();
            PerfOptPlugin.Log.LogInfo("[PARLOAD] Complete in "
                + ((double)s_loadSW.ElapsedMilliseconds / 1000.0).ToString("F2")
                + "s (" + TotalShips.ToString() + " ships parallel, fileLoaders sequential)");

            return false;
        }
    }

    // ========================================
    // SAVE LOADING: Batch Coroutine Yields
    // ========================================
    // Replaces CrewSim.LoadGame(string, string, dict) to manually pump the
    // DoLoadGame coroutine, processing multiple MoveNext() calls per frame
    // instead of one. This eliminates the "1 ship per frame" bottleneck
    // during save loading.
    //
    // IMPORTANT: dictFiles is NOT modified. Ships parse sequentially via
    // JsonToData (same as original), but the per-ship yield return null
    // is coalesced into batched yields. Each batch yields a single null
    // to Unity, allowing N MoveNext() calls per frame.
    //
    // Nested IEnumerators (e.g. system.Init) are pumped inline but we
    // still honor batchSize by yielding their Current to Unity periodically.
    // This preserves Unity's frame-step timing for ship spawning while
    // eliminating the 1-ship-per-frame bottleneck.

    [HarmonyPatch]
    public static class Patch_DoLoadGame_BatchYields
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "LoadGame",
                new Type[] { typeof(string), typeof(string), typeof(Dictionary<string, byte[]>) });
        }

        const int BatchSize = 3;

        static bool Prefix(CrewSim __instance, string fileName, string strShipsFolder, Dictionary<string, byte[]> dictFiles)
        {
            var sw = Stopwatch.StartNew();
            PerfOptPlugin.Log.LogInfo("[SAVE-LOAD] Starting save loading...");
            LoadingProfiler.Start();

            PerfOptPlugin.SuppressDebugLog = true;
            bool prevSuppress = DataHandler.bSuppressGetErrors;
            DataHandler.bSuppressGetErrors = true;
            PerfOptPlugin.IsLoading = true;

            long memBefore = GC.GetTotalMemory(false);
            PerfOptPlugin.Log.LogInfo($"[SAVE-LOAD] Pre-load: M={memBefore / 1048576}MB GC={GC.CollectionCount(0)}");

            try
            {
                GCSettings.LatencyMode = GCLatencyMode.LowLatency;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long memClean = GC.GetTotalMemory(false);
                PerfOptPlugin.Log.LogInfo($"[SAVE-LOAD] Cleaned: M={memClean / 1048576}MB (freed {(memBefore - memClean) / 1048576}MB)");
            }
            catch { }

            var enumerator = (IEnumerator)AccessTools.Method(typeof(CrewSim), "DoLoadGame")
                .Invoke(__instance, new object[] { fileName, strShipsFolder, dictFiles });

            if (enumerator != null)
            {
                __instance.StartCoroutine(BatchedCoroutineLoad(enumerator, sw, prevSuppress));
                __instance.StartCoroutine(LoadingGcSweep());
            }
            return false;
        }

        private static IEnumerator BatchedCoroutineLoad(IEnumerator inner,
            Stopwatch sw, bool prevSuppress)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(inner);
            int steps = 0;

            while (stack.Count > 0)
            {
                IEnumerator current = stack.Peek();
                bool moved;
                try { moved = current.MoveNext(); }
                catch (Exception ex)
                {
                    PerfOptPlugin.Log.LogWarning($"[LOAD-BATCH] Coroutine exception: {ex.Message}");
                    stack.Pop();
                    continue;
                }

                if (!moved)
                {
                    stack.Pop();
                    continue;
                }

                object yielded = current.Current;
                steps++;

                if (yielded is IEnumerator nested)
                {
                    stack.Push(nested);
                    continue;
                }

                if (yielded != null)
                {
                    steps = 0;
                    yield return yielded;
                }
                else if (steps >= BatchSize)
                {
                    steps = 0;
                    yield return null;
                }
            }

            PerfOptPlugin.SuppressDebugLog = false;
            DataHandler.bSuppressGetErrors = prevSuppress;
            PerfOptPlugin.IsLoading = false;

            long memAfter = GC.GetTotalMemory(false);
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long memFreed = memAfter - GC.GetTotalMemory(false);
                PerfOptPlugin.Log.LogInfo($"[SAVE-LOAD] Post-clean: M={memAfter / 1048576}>{GC.GetTotalMemory(false) / 1048576}MB (freed {memFreed / 1048576}MB)");
            }
            catch { }

            sw.Stop();
            LoadingProfiler.Stop();
            PerfOptPlugin.Log.LogInfo($"[SAVE-LOAD] Complete in "
                + ((double)sw.ElapsedMilliseconds / 1000.0).ToString("F2") + "s");
        }

        private static IEnumerator LoadingGcSweep()
        {
            var gen1Timer = new Stopwatch();
            gen1Timer.Start();
            while (PerfOptPlugin.IsLoading)
            {
                yield return new WaitForSeconds(0.25f);
                GC.Collect(0);

                long memMB = GC.GetTotalMemory(false) / 1048576;
                if (gen1Timer.ElapsedMilliseconds >= 1500 || memMB > 1500)
                {
                    gen1Timer.Restart();
                    GC.Collect(1);
                }
            }
        }
    }

    // ========================================
    // LOADING: Skip duplicate station spawns during save load
    // ========================================
    // StarSystem.Init(JsonStarSystemSave, JsonShip[]) does TWO passes:
    //   1. objSystem.aSpawnStations → creates stations from TEMPLATES
    //   2. aShips → creates ALL ships from SAVE DATA (includes stations)
    //
    // Stations like BCER, BCRS, OKLG appear in BOTH lists. Each station
    // gets InitShip(Shallow) called twice — first from template, then
    // overwritten by saved data. The first instance's GameObject + tiles
    // + COs are orphaned (leaked memory + wasted CPU).
    //
    // Fix: Null out aSpawnStations when aShips is present. Station
    // BodyOrbits already exist from objSystem.aBOs (processed earlier).
    // Station ships come from aShips (processed later).

    [HarmonyPatch]
    public static class Patch_SkipDuplicateStationSpawn
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(StarSystem), "Init",
                new[] { typeof(JsonStarSystemSave), typeof(JsonShip[]) });
        }

        static void Prefix(ref JsonStarSystemSave objSystem, JsonShip[] aShips)
        {
            try
            {
                if (objSystem == null) return;
                if (aShips == null || aShips.Length == 0) return;
                if (objSystem.aSpawnStations == null) return;

                int stationCount = objSystem.aSpawnStations.Length;

                var shipRegIDs = new HashSet<string>();
                for (int i = 0; i < aShips.Length; i++)
                {
                    if (aShips[i] != null && aShips[i].strRegID != null)
                        shipRegIDs.Add(aShips[i].strRegID);
                }

                int duplicates = 0;
                foreach (var ss in objSystem.aSpawnStations)
                {
                    if (ss != null && ss.strName != null && shipRegIDs.Contains(ss.strName))
                        duplicates++;
                }

                if (duplicates > 0)
                {
                    objSystem.aSpawnStations = null;
                    PerfOptPlugin.Log.LogInfo(
                        $"[LOAD-SKIP] Skipping {stationCount} station spawns ({duplicates} already in aShips) — saves {duplicates}x InitShip(Shallow)");
                }
            }
            catch (Exception ex)
            {
                PerfOptPlugin.Log.LogWarning($"[LOAD-SKIP] Failed: {ex.Message}");
            }
        }
    }

    // ========================================
    // LOADING: Eliminate O(N×M) orphaned CO scan in DoLoadGame
    // ========================================
    // Vanilla DoLoadGame (CrewSim.cs:1487) does, for each CO save:
    //   if (string.IsNullOrEmpty(jcos.strRegIDLast) ||
    //       !aShips.Any((JsonShip x) => x.strRegID == jcos.strRegIDLast))
    //       num++;  // orphaned
    //   else
    //       DataHandler.dictCOSaves[jcos.strID] = jcos;
    //
    // aShips.Any(lambda) is O(M) per CO → O(N×M) total. For a save with
    // 3000 COs and 300 ships that's 900,000 string comparisons, right
    // after "Finished reading ships". This is the long pause the user
    // sees between the read phase and system init.
    //
    // Fix: Transpiler on CrewSim.DoLoadGame replaces the
    // Enumerable.Any<JsonShip>(aShips, lambda) call with a call to
    // ShipRegIDContains(aShips, strRegID). The helper builds a
    // HashSet<string> of ship RegIDs lazily on first call (or when the
    // aShips array reference changes) and caches it in a [ThreadStatic]
    // field. O(M) build once, then O(1) per CO lookup.
    //
    // The lambda closure captures jcos.strRegIDLast; the transpiler
    // relies on the Any() call site pushing (aShips, delegate) and
    // our helper accepting (aShips, strRegID) with matching stack
    // depth. If the IL pattern doesn't match at runtime, Harmony logs
    // a warning and vanilla runs unchanged.

    [HarmonyPatch]
    public static class Patch_DoLoadGame_FastOrphanScan
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "DoLoadGame");
        }

        [ThreadStatic]
        private static HashSet<string> _tlsShipRegIDs;
        [ThreadStatic]
        private static JsonShip[] _tlsShipRef;

        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var codes = new List<CodeInstruction>(instructions);
            var containsMethod = AccessTools.Method(
                typeof(Patch_DoLoadGame_FastOrphanScan), "ShipRegIDContains");

            int patchCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Call &&
                    codes[i].operand is MethodInfo mi &&
                    mi.Name == "Any" &&
                    mi.DeclaringType == typeof(Enumerable))
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, containsMethod);
                    patchCount++;
                }
            }

            if (patchCount > 0)
                PerfOptPlugin.Log.LogInfo(
                    $"[LOAD-CO] DoLoadGame: replaced {patchCount} aShips.Any() with cached HashSet.Contains()");
            else
                PerfOptPlugin.Log.LogInfo("[LOAD-CO] DoLoadGame: Any() call not found in IL");

            return codes;
        }

        public static bool ShipRegIDContains(JsonShip[] aShips, string strRegID)
        {
            var set = _tlsShipRegIDs;
            if (set == null || !ReferenceEquals(aShips, _tlsShipRef))
            {
                set = new HashSet<string>();
                _tlsShipRegIDs = set;
                _tlsShipRef = aShips;
                if (aShips != null)
                {
                    for (int i = 0; i < aShips.Length; i++)
                    {
                        if (aShips[i] != null && aShips[i].strRegID != null)
                            set.Add(aShips[i].strRegID);
                    }
                }
            }
            if (strRegID == null) return false;
            return set.Contains(strRegID);
        }
    }

    // ========================================
    // SAVING: Force threaded save for manual saves
    // ========================================
    // The game's OnCreateSave/OnOverwrite call SaveGame(saveName) with
    // useThreading=false (default). This runs the entire save (JSON
    // serialization + disk write + zip compression) synchronously on the
    // main thread, freezing the game for several seconds.
    // AutoSave already defaults to useThreading=true.
    // This patch forces useThreading=true for ALL SaveGame calls.

    [HarmonyPatch]
    public static class Patch_SaveGame_Threaded
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(LoadManager), "SaveGame",
                new Type[] { typeof(string), typeof(int), typeof(bool) });
        }

        static void Prefix(ref bool useThreading)
        {
            if (!PerfOptPlugin.CfgThreadedSave)
                return;

            if (!useThreading)
            {
                useThreading = true;
                PerfOptPlugin.Log.LogInfo("[SAVE] Forcing threaded save (was synchronous)");
            }
        }
    }
}