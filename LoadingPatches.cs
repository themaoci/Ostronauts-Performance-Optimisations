using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Ostranauts.Core;

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

        static bool Prefix(CrewSim __instance, string fileName, string strShipsFolder, Dictionary<string, byte[]> dictFiles)
        {
            int batchSize = PerfOptPlugin.CfgSaveLoadBatchSize;
            if (batchSize == 1)
                return true;

            var sw = Stopwatch.StartNew();
            PerfOptPlugin.Log.LogInfo("[SAVE-LOAD] Starting batched save loading...");

            PerfOptPlugin.SuppressDebugLog = true;
            bool prevSuppressErrors = DataHandler.bSuppressGetErrors;
            DataHandler.bSuppressGetErrors = true;

            var enumerator = (IEnumerator)AccessTools.Method(typeof(CrewSim), "DoLoadGame")
                .Invoke(__instance, new object[] { fileName, strShipsFolder, dictFiles });

            if (enumerator == null)
            {
                PerfOptPlugin.SuppressDebugLog = false;
                DataHandler.bSuppressGetErrors = prevSuppressErrors;
                return true;
            }

            if (batchSize <= 0)
                batchSize = int.MaxValue;

            __instance.StartCoroutine(BatchedCoroutineLoad(enumerator, batchSize, sw, prevSuppressErrors));
            return false;
        }

        private static IEnumerator BatchedCoroutineLoad(IEnumerator inner, int batchSize,
            Stopwatch sw, bool prevSuppressErrors)
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

                // Non-IEnumerator yield (null, WaitForEndOfFrame, etc.)
                // Yield it to Unity every batchSize steps
                if (steps >= batchSize)
                {
                    steps = 0;
                    yield return yielded;
                }
            }

            PerfOptPlugin.SuppressDebugLog = false;
            DataHandler.bSuppressGetErrors = prevSuppressErrors;
            sw.Stop();
            PerfOptPlugin.Log.LogInfo("[SAVE-LOAD] Complete in "
                + ((double)sw.ElapsedMilliseconds / 1000.0).ToString("F2") + "s");
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