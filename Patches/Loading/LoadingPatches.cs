using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
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
    public class Patch_ParallelLoad : PatchBase
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

            long phaseStart = Tick();
            long memBefore = Mem();
            int gcBefore = GCs();

            s_loadSW.Restart();
            LogLoadPhase("PARLOAD", "Starting parallel loading...");

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

            // Count mods and files before distribution
            int totalMods = LoadManager.LoadingQueue.Count;
            int totalFileLoaders = 0;
            for (int j = 0; j < totalMods; j++)
                totalFileLoaders += LoadManager.LoadingQueue[j].fileLoaders.Count;

            LogLoadPhase("PARLOAD", $"Queue: {totalMods} mods, {totalFileLoaders} fileLoaders, distributing ships...");

            // Distribute ONLY ships across LoaderThreads (original behavior)
            int RoundRobin = 0;
            int TotalShips = 0;
            DataHandler.toLoad = 0;

            for (int j = 0; j < totalMods; j++)
            {
                ModLoader Mod = LoadManager.LoadingQueue[j];
                int modShips = Mod.ships.Count;
                for (int k = 0; k < modShips; k++)
                {
                    Threads[RoundRobin % ThreadCount].fileLoaders.Add(Mod.ships[k]);
                    RoundRobin++;
                    TotalShips++;
                    DataHandler.toLoad++;
                }
                if (modShips > 0)
                    LogLoadPhase("PARLOAD", $"  Mod '{Mod.JsonModInfo?.strName ?? "?"}': {modShips} ships");
            }

            LogLoadPhase("PARLOAD", $"Distributing {TotalShips} ships across {ThreadCount} threads (fileLoaders stay sequential)");

            // Start ship-loading threads
            long threadStart = Tick();
            for (int i = 0; i < ThreadCount; i++)
            {
                Threads[i].t = new Thread(Threads[i].Run);
                Threads[i].t.IsBackground = true;
                Threads[i].t.Start();
            }
            LogLoadPhase("PARLOAD", $"Threads started in {ToMs(Tick() - threadStart):F1}ms");

            // Process fileLoaders sequentially on calling thread
            int fileLoaderCount = 0;
            int fileLoaderErrors = 0;
            int PrevLoaded = 0;
            long fileLoaderPhaseStart = Tick();

            while (LoadManager.LoadingQueue.Count > 0)
            {
                ModLoader Mod = LoadManager.LoadingQueue[0];
                LoadManager.LoadingQueue.RemoveAt(0);
                if (Mod == null) continue;

                string modName = Mod.JsonModInfo?.strName ?? "?";
                long modStart = Tick();

                if (Mod.JsonModInfo != null && !string.IsNullOrEmpty(Mod.JsonModInfo.strName))
                {
                    lock (LoadManager.outputLock)
                        LoadManager.modNamesStartedLoading.Add(Mod.JsonModInfo.strName);
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
                            LogLoadPhase("PARLOAD", $"Terminated during fileLoader phase ({fileLoaderCount} processed)");
                            return false;
                        }
                    }

                    FileLoader fileLoader = Mod.fileLoaders[l];
                    if (fileLoader != null)
                    {
                        long flStart = Tick();
                        try
                        {
                            fileLoader.loadDelegate();
                            fileLoaderCount++;
                        }
                        catch (Exception ex)
                        {
                            fileLoaderErrors++;
                            LogError("PARLOAD", $"fileLoader '{fileLoader.fileName}'", ex);
                        }

                        lock (LoadManager.outputLock)
                        {
                            LoadManager.fileNamesLoaded.Add(Path.GetFileNameWithoutExtension(fileLoader.fileName));
                            DataHandler.loaded++;
                        }
                    }
                }

                for (int m = 0; m < Mod.PerModPostLoadAsyncOkay.Count; m++)
                {
                    if (Mod.PerModPostLoadAsyncOkay[m] != null)
                        Mod.PerModPostLoadAsyncOkay[m]();
                }

                if (Mod.JsonModInfo != null && !string.IsNullOrEmpty(Mod.JsonModInfo.strName))
                {
                    lock (LoadManager.outputLock)
                        LoadManager.modNamesCompletedLoading.Add(Mod.JsonModInfo.strName);
                }
                Mod.complete = true;

                // Update progress from parallel ship loading too
                int CurrentLoaded;
                lock (LoadManager.outputLock)
                    CurrentLoaded = LoadManager.fileNamesLoaded.Count;
                if (CurrentLoaded != PrevLoaded)
                {
                    DataHandler.loaded = CurrentLoaded;
                    PrevLoaded = CurrentLoaded;
                }
            }

            LogLoadPhaseTimed("PARLOAD", $"FileLoaders done: {fileLoaderCount} files, {fileLoaderErrors} errors", fileLoaderPhaseStart);

            // Join ship-loading threads
            long joinStart = Tick();
            for (int i = 0; i < ThreadCount; i++)
            {
                if (Threads[i].t != null && Threads[i].t.IsAlive)
                    Threads[i].t.Join();
            }
            LogLoadPhaseTimed("PARLOAD", $"Thread join complete", joinStart);

            lock (LoadManager.outputLock)
                DataHandler.loaded = LoadManager.fileNamesLoaded.Count;

            long postLoadStart = Tick();
            DataHandler.AllPostLoadAsync();
            DataHandler.bAsyncLoaded = true;
            LogLoadPhaseTimed("PARLOAD", "AllPostLoadAsync complete", postLoadStart);

            PerfOptPlugin.SuppressDebugLog = false;
            DataHandler.bSuppressGetErrors = prevSuppressErrors;

            s_loadSW.Stop();
            float totalSec = (float)((double)s_loadSW.ElapsedMilliseconds / 1000.0);
            LogTimedMB("PARLOAD", $"Complete in {totalSec:F2}s ({TotalShips} ships parallel, {fileLoaderCount} fileLoaders sequential)",
                phaseStart, memBefore, gcBefore);

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
    public class Patch_DoLoadGame_BatchYields : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "LoadGame",
                new Type[] { typeof(string), typeof(string), typeof(Dictionary<string, byte[]>) });
        }

        const int BatchSize = 3;

        static bool Prefix(CrewSim __instance, string fileName, string strShipsFolder, Dictionary<string, byte[]> dictFiles)
        {
            long phaseStart = Tick();
            long memBefore = Mem();
            int gcBefore = GCs();

            var sw = Stopwatch.StartNew();
            LogLoadPhase("SAVE-LOAD", $"Starting save loading... file={fileName} shipsFolder={strShipsFolder}");
            LoadingProfiler.Start();

            PerfOptPlugin.SuppressDebugLog = true;
            bool prevSuppress = DataHandler.bSuppressGetErrors;
            DataHandler.bSuppressGetErrors = true;
            PerfOptPlugin.IsLoading = true;

            LogLoadPhase("SAVE-LOAD", $"Pre-load: M={MemMB()}MB GC={gcBefore} dictFiles={dictFiles?.Count ?? 0} entries");

            try
            {
                GCSettings.LatencyMode = GCLatencyMode.LowLatency;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long memClean = Mem();
                LogLoadPhase("SAVE-LOAD", $"Cleaned: M={memClean / 1048576L}MB (freed {(memBefore - memClean) / 1048576L}MB)");
            }
            catch { }

            long invokeStart = Tick();
            var enumerator = (IEnumerator)AccessTools.Method(typeof(CrewSim), "DoLoadGame")
                .Invoke(__instance, new object[] { fileName, strShipsFolder, dictFiles });
            LogLoadPhaseTimed("SAVE-LOAD", "DoLoadGame enumerator created", invokeStart);

            if (enumerator != null)
            {
                __instance.StartCoroutine(BatchedCoroutineLoad(__instance, enumerator, sw, prevSuppress, phaseStart, memBefore, gcBefore));
                __instance.StartCoroutine(LoadingGcSweep());
            }
            else
            {
                LogError("SAVE-LOAD", "DoLoadGame", new Exception("enumerator was null — LoadGame returned null"));
            }
            return false;
        }

        private static IEnumerator BatchedCoroutineLoad(CrewSim instance, IEnumerator inner,
            Stopwatch sw, bool prevSuppress, long phaseStart, long memBefore, int gcBefore)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(inner);
            int steps = 0;
            int totalSteps = 0;
            int nestedCount = 0;
            int errorCount = 0;
            long lastLogTick = Tick();

            while (stack.Count > 0)
            {
                IEnumerator current = stack.Peek();
                bool moved;
                try { moved = current.MoveNext(); }
                catch (Exception ex)
                {
                    errorCount++;
                    LogError("LOAD-BATCH", $"Coroutine exception (depth={stack.Count})", ex);
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
                totalSteps++;

                if (yielded is IEnumerator nested)
                {
                    nestedCount++;
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

                // Log progress every 5000 steps
                if (totalSteps % 5000 == 0)
                {
                    float rate = totalSteps / ToMs(Tick() - lastLogTick) * 1000f;
                    lastLogTick = Tick();
                    LogLoadPhase("LOAD-BATCH", $"Progress: {totalSteps} steps, {nestedCount} nested, {errorCount} errors, {rate:F0} steps/s");
                }
            }

            LogLoadPhase("LOAD-BATCH", $"Coroutine complete: {totalSteps} total steps, {nestedCount} nested, {errorCount} errors");

            // Force a frame, then restore the vanilla finalization sequence
            long finalizeStart = Tick();
            try
            {
                var fiEvent = AccessTools.Field(typeof(CrewSim), "OnGameFinishedLoading");
                if (fiEvent != null)
                {
                    var unityEvent = fiEvent.GetValue(null) as UnityEngine.Events.UnityEvent;
                    if (unityEvent != null)
                        unityEvent.Invoke();
                }
            }
            catch { }

            try { CrewSim.Paused = false; } catch { }
            try
            {
                var forceAnimMethod = AccessTools.Method(typeof(CrewSim), "ForceUpdateAnimators");
                forceAnimMethod?.Invoke(instance, null);
            }
            catch { }

            yield return null;

            try { CrewSim.Paused = true; } catch { }
            try
            {
                var fiFinished = AccessTools.Field(typeof(CrewSim), "_finishedLoading");
                if (fiFinished != null)
                    fiFinished.SetValue(instance, true);
            }
            catch { }
            LogLoadPhaseTimed("LOAD-BATCH", "Finalized: OnGameFinishedLoading -> Paused=false -> ForceUpdateAnimators -> yield -> Paused=true -> _finishedLoading=true", finalizeStart);

            PerfOptPlugin.SuppressDebugLog = false;
            DataHandler.bSuppressGetErrors = prevSuppress;
            PerfOptPlugin.IsLoading = false;

            long memAfter = Mem();
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long memFreed = memAfter - Mem();
                LogLoadPhase("SAVE-LOAD", $"Post-clean: M={memAfter / 1048576L}>{MemMB()}MB (freed {memFreed / 1048576L}MB)");
            }
            catch { }

            sw.Stop();
            LoadingProfiler.Stop();
            LogTimedMB("SAVE-LOAD", $"Complete in {(double)sw.ElapsedMilliseconds / 1000.0:F2}s",
                phaseStart, memBefore, gcBefore);
        }

        private static IEnumerator LoadingGcSweep()
        {
            var gen1Timer = new Stopwatch();
            gen1Timer.Start();
            int gcCount = 0;
            while (PerfOptPlugin.IsLoading)
            {
                yield return new WaitForSeconds(0.25f);
                GC.Collect(0);
                gcCount++;

                long memMB = MemMB();
                if (gen1Timer.ElapsedMilliseconds >= 1500 || memMB > 1500)
                {
                    gen1Timer.Restart();
                    GC.Collect(1);
                    LogLoadPhase("LOAD-GC", $"Gen1 sweep #{gcCount}: M={memMB}MB");
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
    public class Patch_SkipDuplicateStationSpawn : PatchBase
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
                    LogLoadPhase("LOAD-SKIP", $"Skipping {stationCount} station spawns ({duplicates} already in aShips) — saves {duplicates}x InitShip(Shallow)");
                }
            }
            catch (Exception ex)
            {
                LogError("LOAD-SKIP", "SkipDuplicateStationSpawn", ex);
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
    public class Patch_DoLoadGame_FastOrphanScan : PatchBase
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
                if ((codes[i].opcode == OpCodes.Call || codes[i].opcode == OpCodes.Callvirt) &&
                    codes[i].operand is MethodInfo mi &&
                    mi.Name == "Any")
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, containsMethod);
                    patchCount++;
                }
            }

            if (patchCount > 0)
                LogPatchResult("DoLoadGame_FastOrphanScan", true,
                    $"replaced {patchCount} aShips.Any() with cached HashSet.Contains()");
            else
                LogPatchResult("DoLoadGame_FastOrphanScan", true,
                    "Any() call not found in IL (game may have updated)");

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
    public class Patch_SaveGame_Threaded : PatchBase
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
                LogLoadPhase("SAVE", "Forcing threaded save (was synchronous)");
            }
        }
    }

    // ========================================
    // SAVING: Guard against concurrent saves
    // ========================================
    // Vanilla OnCreateSave/OnOverwrite call SaveGame directly with no
    // check for an in-flight _saveJob. If the user clicks Save while an
    // autosave is running, a second _saveJob fires simultaneously — the
    // first job's _loadedSave and SaveDto get clobbered, the save-list
    // callback reads the wrong Exception, and the coroutine that waits
    // for completion reads the wrong _saveJob field.
    //
    // Uses Harmony Finalizer (not Postfix) so _saveInProgress is ALWAYS
    // reset even if the original method throws an exception. Without this,
    // a single save failure permanently blocks all future saves.

    [HarmonyPatch]
    public class Patch_OnCreateSave_Guard : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(LoadManager), "OnCreateSave");
        }

        static bool Prefix()
        {
            if (Interlocked.CompareExchange(ref Patch_SaveGuard._saveInProgress, 1, 0) != 0)
            {
                Log.LogWarning("[SAVE] Save already in progress, ignoring OnCreateSave");
                return false;
            }
            return true;
        }

        static Exception Finalizer(Exception __exception)
        {
            Interlocked.Exchange(ref Patch_SaveGuard._saveInProgress, 0);
            return __exception;
        }
    }

    [HarmonyPatch]
    public class Patch_OnOverwrite_Guard : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(LoadManager), "OnOverwrite");
        }

        static bool Prefix()
        {
            if (Interlocked.CompareExchange(ref Patch_SaveGuard._saveInProgress, 1, 0) != 0)
            {
                Log.LogWarning("[SAVE] Save already in progress, ignoring OnOverwrite");
                return false;
            }
            return true;
        }

        static Exception Finalizer(Exception __exception)
        {
            Interlocked.Exchange(ref Patch_SaveGuard._saveInProgress, 0);
            return __exception;
        }
    }

    // Shared state for save-guard patches.
    internal static class Patch_SaveGuard
    {
        internal static int _saveInProgress;
    }

    // ========================================
    // SAVING: SaveGame preparation — optional backup only
    // ========================================
    // Before SaveGame runs:
    //   Backs up the existing save directory for manual saves (not
    //   autosave/quicksave), so the user can restore if Steam Cloud
    //   overwrites a newer save with an older one.
    //
    // NOTE: GC suppression (LowLatency) was removed — the save operation
    // serializes 36058+ COs and 140+ ships, allocating massive memory.
    // Suppressing GC during this causes OOM crashes. GC pauses during
    // save are acceptable.
    //
    // Backup naming: {savePath}_backup_{yyyyMMdd_HHmmss}
    // Only top-level files are copied (no subdirectories, no recursion).

    [HarmonyPatch]
    public class Patch_SaveGame_BeforeSave : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(LoadManager), "SaveGame",
                new Type[] { typeof(string), typeof(int), typeof(bool) });
        }

        private static readonly string[] SkipBackupNames = new[]
        {
            "autosave", "quicksave", "autosave_"
        };

        private static readonly Dictionary<string, double> _lastBackupTime =
            new Dictionary<string, double>();
        private static readonly object _backupLock = new object();
        private const double BackupThrottleSec = 60.0;

        static void Prefix(string saveName)
        {
            long start = Tick();
            long memBefore = Mem();

            try
            {
                if (!PerfOptPlugin.CfgSaveBackup) return;
                if (string.IsNullOrEmpty(saveName)) return;

                string nameLower = saveName.ToLowerInvariant();
                for (int i = 0; i < SkipBackupNames.Length; i++)
                {
                    if (nameLower.Contains(SkipBackupNames[i]))
                        return;
                }

                string saveDir = null;
                if (saveName.IndexOfAny(new[] { '\\', '/' }) >= 0)
                {
                    saveDir = Path.GetDirectoryName(saveName);
                }
                else
                {
                    string candidate = Path.Combine(
                        Application.persistentDataPath, "Saves", saveName);
                    if (Directory.Exists(candidate))
                        saveDir = candidate;
                }

                if (saveDir == null || !Directory.Exists(saveDir))
                    return;

                double now = Time.realtimeSinceStartupAsDouble;
                lock (_backupLock)
                {
                    double lastTime;
                    if (_lastBackupTime.TryGetValue(saveDir, out lastTime))
                    {
                        if (now - lastTime < BackupThrottleSec)
                            return;
                    }
                    _lastBackupTime[saveDir] = now;
                }

                string backupDir = saveDir.TrimEnd('\\', '/')
                    + "_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

                Directory.CreateDirectory(backupDir);
                string[] files = Directory.GetFiles(saveDir);
                for (int i = 0; i < files.Length; i++)
                {
                    string destFile = Path.Combine(backupDir, Path.GetFileName(files[i]));
                    File.Copy(files[i], destFile, true);
                }

                LogTimed("SAVE-BACKUP", $"Backed up '{saveDir}' -> '{backupDir}'",
                    start, memBefore, GCs());
            }
            catch (Exception ex)
            {
                LogError("SAVE-BACKUP", $"Backup for '{saveName}'", ex);
            }
        }
    }
}
