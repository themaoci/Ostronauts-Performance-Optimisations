using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;
using Ostranauts.Core;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using System.Linq;
using System.Threading;

namespace OstronautsPerfOpt
{
    [BepInPlugin("com.ostronauts.perfopt",
        "Ostronauts Performance Optimizer", "4.4.0")]
    public class PerfOptPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static bool GameLoaded;

        internal const bool CfgFirstOrDefault = true;
        internal const bool CfgInteractionCache = true;
        internal const float CfgMaxDeltaTime = 0.1f;
        internal const int CfgHeapExpansionMB = 128;
        internal const int CfgMemCeilingMB = 3072;
        internal const int CfgGCIntervalSec = 0;
        internal const int CfgMinFreeMB = 0;
        internal const bool CfgProfiling = true;
        internal const int CfgProfileIntervalSec = 5;
        internal const bool CfgParallelOrbits = true;
        internal const bool CfgParallelUpdateStats = true;
        internal const bool CfgParallelCleanupExpiry = true;
        internal const int CfgParallelMinBatch = 8;
        internal const bool CfgLowLatencyGC = true;
        internal const bool CfgEliminateToList = true;
        internal const bool CfgCacheComponents = true;
        internal const bool CfgParallelLoading = true;
        internal const int CfgParallelLoadThreads = 4;
        internal const int CfgSaveLoadBatchSize = 10;
        internal const bool CfgEliminateAIAllocs = true;
        internal const bool CfgOptimizeTickers = true;
        internal const bool CfgThreadedSave = true;
        internal const bool CfgSkipSaveScreenshot = true;

        internal static long FrameStartTimestamp;

        private static float _lastForcedGC;
        private static int _forcedGCCount;
        private static long _lastHeapAfterGC;
        private static int _ceilingFailStreak;
        private static long _effectiveCeilingMB;

        private static bool _heapExpanded;
        private static float _loadedTime = -1f;
        private static string _heapExpandResult = "";

        private static readonly Stopwatch _frameSW = new Stopwatch();
        private static int _frameCount;
        private static float _reportTimer;
        private static float _totalFrameMs, _worstFrameMs;
        private static int _spikeCount, _gcSpikeCount;
        private static long _memAtReport;
        private static int _gcAtReport;
        private static long _fMemStart;
        private static int _fGCStart;
        private static int _pausedFrames;

        private static GCLatencyMode _originalGCLatencyMode;
        private static bool _gcModeSet;

        public static int SimStepsThisFrame;
        public static int SimStepsTotal, SimStepsMax;

        public static long TAdvanceSim; public static int CAdvanceSim;
        public static long TICO;         public static int CICO;
        public static long TEndTurn;     public static int CEndTurn;
        public static long TGetMove2;    public static int CGetMove2;
        public static long TGetWork;     public static int CGetWork;
        public static long TParseCL;     public static int CParseCL;
        public static long TStarSys;     public static int CStarSys;

        internal static bool SuppressDebugLog;
        public static long TCleanup;     public static int CCleanup;
        public static long TUpdateStats; public static int CUpdateStats;
        public static long TOrbits;      public static int COrbits;
        public static long TNoGC;        public static int CNoGC;
        public static int IACacheHits;
        public static int ParallelBatchesRun;
        public static int ToListEliminated;
        public static int ComponentCacheHits;
        public static int TickerPreSized;
        public static int FirstOrDefaultSkipped;

        public static long FrameAllocTotal;
        public static long AllocAdvanceSim;
        public static long AllocStarSys;
        public static long AllocEndTurn;
        public static long AllocGetMove2;
        public static long AllocGetWork;
        public static long AllocParseCL;
        public static long AllocCleanup;
        public static long AllocUpdateStats;

        private static FieldInfo _fTimeCoeffPause;
        private float _lastDiag;

        private static readonly StringBuilder SB = new StringBuilder(4096);

        private void Awake()
        {
            Log = Logger;

            _fTimeCoeffPause = AccessTools.Field(typeof(CrewSim), "fTimeCoeffPause");

            Time.maximumDeltaTime = CfgMaxDeltaTime;
            Log.LogInfo($"[CFG] maxDeltaTime -> {CfgMaxDeltaTime}");

            try
            {
                _originalGCLatencyMode = GCSettings.LatencyMode;
                GCSettings.LatencyMode = GCLatencyMode.LowLatency;
                _gcModeSet = true;
                Log.LogInfo($"[GC] LatencyMode: {_originalGCLatencyMode} -> LowLatency");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[GC] Failed to set LowLatency: {ex.Message}");
            }

            Log.LogInfo($"[MONO] heap={GC.GetTotalMemory(false) / 1048576}MB (managed)");
            Log.LogInfo($"[THREADS] Available: {Environment.ProcessorCount}");

            try { SpikeProfiler.Start(); }
            catch (Exception ex)
            {
                Log.LogWarning($"[STACK-PROF] Failed to start sampler: {ex.Message}");
            }

            var harmony = new Harmony("com.ostronauts.perfopt");
            int ok = 0;
            Type[] patches = new Type[]
            {
                typeof(Patch_AdvanceSim),
                typeof(Patch_UpdateICOs),
                typeof(Patch_EndTurn),
                typeof(Patch_GetMove2),
                typeof(Patch_GetWork),
                typeof(Patch_ParseCondLoot),
                typeof(Patch_StarSystemUpdate),
                typeof(Patch_Cleanup),
                typeof(Patch_FirstOrDefault),
                typeof(Patch_UpdateStats),
                typeof(Patch_SuppressInteractionLog),
                typeof(Patch_CleanupExpire),
                typeof(Patch_UpdateICOsParallelPrepass),
                typeof(Patch_StarSystemUpdate_ToList),
                typeof(Patch_CollisionManager_ToList),
                typeof(Patch_CrewSim_CacheComponents),
                typeof(Patch_ParallelLoad),
                typeof(Patch_DoLoadGame_BatchYields),
                typeof(Patch_UpdateICOs_NoCopy),
                typeof(Patch_DebugLog_Suppress),
                typeof(Patch_SaveGame_Threaded),
                typeof(Patch_UpdateShip_DefaultGravBO),
                typeof(Patch_SaveScreenShot_Skip),
                typeof(Patch_SaveCrewPortraits_Skip),
                typeof(Patch_Sparks_CacheFlicker),
                typeof(Patch_DamageOverTime_Skip),
                typeof(Patch_InstallStart_KeepInventoryOpen),
                typeof(Patch_ClaimTaskDirect_QueueStack),
                typeof(Patch_GetAvailActions_KeepClickable)
            };

            for (int i = 0; i < patches.Length; i++)
            {
                try
                {
                    harmony.CreateClassProcessor(patches[i]).Patch();
                    ok++;
                    Log.LogInfo($"  [OK] {patches[i].Name}");
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"  [FAIL] {patches[i].Name}: {ex.Message}");
                }
            }

            Log.LogInfo($"=== PerfOpt v4.3.0 ({ok}/{patches.Length} patches) ===");
            Log.LogInfo("  All optimizations hardcoded ON. No config.");
        }

        private void Update()
        {
            FrameStartTimestamp = Stopwatch.GetTimestamp();
            _fMemStart = GC.GetTotalMemory(false);
            _fGCStart = GC.CollectionCount(0);
            SimStepsThisFrame = 0;
            _frameSW.Reset();
            _frameSW.Start();

            if (GameLoaded && !_heapExpanded)
            {
                if (_loadedTime < 0f)
                    _loadedTime = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - _loadedTime >= 3f)
                {
                    _heapExpanded = true;
                    ExpandHeap(CfgHeapExpansionMB);
                }
            }

            if (GameLoaded && _heapExpanded)
                CheckMemoryCeiling();
        }

        private void LateUpdate()
        {
            _frameSW.Stop();
            float ms = (float)_frameSW.Elapsed.TotalMilliseconds;
            _frameCount++;
            _totalFrameMs += ms;
            if (ms > _worstFrameMs)
                _worstFrameMs = ms;

            bool paused = false;
            try
            {
                if (_fTimeCoeffPause != null)
                    paused = (float)_fTimeCoeffPause.GetValue(null) == 0f;
            }
            catch { }
            if (paused) _pausedFrames++;

            SimStepsTotal += SimStepsThisFrame;
            if (SimStepsThisFrame > SimStepsMax)
                SimStepsMax = SimStepsThisFrame;

            long memAfter = GC.GetTotalMemory(false);
            int gcDelta = GC.CollectionCount(0) - _fGCStart;
            long fAlloc = memAfter - _fMemStart;
            if (fAlloc > 0 && gcDelta == 0)
                FrameAllocTotal += fAlloc;

            if (ms > 33f)
            {
                _spikeCount++;
                if (gcDelta > 0) _gcSpikeCount++;
                SB.Length = 0;
                SB.Append("[SPIKE] ").Append(ms.ToString("F1")).Append("ms");
                if (gcDelta > 0)
                    SB.Append(" [GCx").Append(gcDelta).Append("]");
                SB.Append(" Sim:").Append(SimStepsThisFrame);
                if (paused) SB.Append(" [PAUSED]");
                Log.LogWarning(SB.ToString());

                if (ms > 200f && SpikeProfiler.HasSamples)
                {
                    SpikeProfiler.DumpAndClear(SB.ToString());
                }
            }

            if (Time.realtimeSinceStartup - _lastDiag >= 5f)
            {
                _lastDiag = Time.realtimeSinceStartup;
                Log.LogInfo($"[DIAG] loaded={GameLoaded} paused={paused}" +
                    $" Sim={SimStepsThisFrame}" +
                    $" ToListSkip={ToListEliminated}");
            }

            _reportTimer += Time.unscaledDeltaTime;
            if (_reportTimer >= CfgProfileIntervalSec)
            {
                _reportTimer = 0f;
                LogReport();
            }
        }

        private void CheckMemoryCeiling()
        {
            float now = Time.realtimeSinceStartup;
            float elapsed = now - _lastForcedGC;

            if (elapsed < 10f) return;

            long managed = GC.GetTotalMemory(false);
            long heapMB = managed / 1048576;

            long ceiling = _effectiveCeilingMB > 0
                ? _effectiveCeilingMB : CfgMemCeilingMB;
            int interval = CfgGCIntervalSec;

            bool shouldGC = false;
            string reason = "";

            if (ceiling > 0 && heapMB > ceiling)
            {
                shouldGC = true;
                reason = $"CEILING({heapMB}>{ceiling}MB)";
            }
            else if (interval > 0 && elapsed >= interval)
            {
                shouldGC = true;
                reason = $"PERIODIC({elapsed:F0}s)";
            }

            if (CfgMinFreeMB > 0 && !shouldGC)
            {
                long freeEstimate = ceiling > 0 ? ceiling - heapMB : 0;
                if (freeEstimate > 0 && freeEstimate < CfgMinFreeMB)
                {
                    shouldGC = true;
                    reason = $"MINFREE({freeEstimate}<{CfgMinFreeMB}MB)";
                }
            }

            if (!shouldGC) return;

            try { GCSettings.LatencyMode = GCLatencyMode.Interactive; }
            catch { }

            long before = managed;
            GC.Collect(0, GCCollectionMode.Optimized, false);
            long after = GC.GetTotalMemory(false);
            long reclaimed = before - after;
            long afterMB = after / 1048576;

            try { GCSettings.LatencyMode = GCLatencyMode.LowLatency; }
            catch { }

            _lastForcedGC = now;
            _forcedGCCount++;
            _lastHeapAfterGC = after;

            Log.LogInfo($"[GC-CEIL] #{_forcedGCCount} {reason}" +
                $" M:{heapMB}>{afterMB}MB Reclaimed:{reclaimed / 1048576}MB");

            if (ceiling > 0 && afterMB > ceiling)
            {
                _ceilingFailStreak++;
                if (_ceilingFailStreak >= 3)
                {
                    long newCeiling = afterMB + 512;
                    Log.LogWarning($"[GC-CEIL] Managed {afterMB}MB stuck above " +
                        $"{ceiling}MB after {_ceilingFailStreak} GCs. " +
                        $"Auto-raising to {newCeiling}MB");
                    _effectiveCeilingMB = newCeiling;
                    _ceilingFailStreak = 0;
                }
            }
            else
            {
                _ceilingFailStreak = 0;
            }
        }

        private static void ExpandHeap(int targetMB)
        {
            if (targetMB <= 0)
            {
                _heapExpandResult = "disabled";
                Log.LogInfo("[HEAP] Expansion disabled");
                return;
            }

            long before = GC.GetTotalMemory(false);
            Log.LogInfo($"[HEAP] Expanding LOH free-list by ~{targetMB}MB...");

            try
            {
                const int LargeBlockSize = 131072;
                int largeCount = targetMB * 8;
                byte[][] blocks = new byte[largeCount][];
                for (int i = 0; i < largeCount; i++)
                    blocks[i] = new byte[LargeBlockSize];

                long peak = GC.GetTotalMemory(false);

                blocks = null;
                GC.Collect(0, GCCollectionMode.Optimized, false);

                long after = GC.GetTotalMemory(false);
                long delta = peak - before;
                _heapExpandResult = $"M:{before / 1048576}>{after / 1048576}MB" +
                    $" +{delta / 1048576}MB (LOH freed to pool)";

                Log.LogInfo($"[HEAP] Before: M={before / 1048576}MB");
                Log.LogInfo($"[HEAP] Peak:   M={peak / 1048576}MB");
                Log.LogInfo($"[HEAP] After:  M={after / 1048576}MB");
                Log.LogInfo($"[HEAP] Expanded +{delta / 1048576}MB. " +
                    $"{largeCount} x {LargeBlockSize >> 10}KB LOH blocks released to free-list.");
            }
            catch (Exception ex)
            {
                _heapExpandResult = "FAIL: " + ex.Message;
                Log.LogError($"[HEAP] Expansion failed: {ex}");
            }
        }

        private void LogReport()
        {
            long mem = GC.GetTotalMemory(false);
            int gc = GC.CollectionCount(0);
            float avgMs = _frameCount > 0 ? _totalFrameMs / _frameCount : 0;
            float fps = avgMs > 0 ? 1000f / avgMs : 0;
            int totalGC = gc - _gcAtReport;
            int active = _frameCount - _pausedFrames;
            float avgSim = _frameCount > 0 ? (float)SimStepsTotal / _frameCount : 0;

            SB.Length = 0;
            SB.Append($"\n=== PERF v4.4.0 ({CfgProfileIntervalSec}s) ===\n");
            SB.Append($"  Fr: {_frameCount} ({active}act)");
            SB.Append($" FPS:{fps:F0}");
            SB.Append($" Worst:{_worstFrameMs:F1}ms\n");
            SB.Append($"  Sp:{_spikeCount} ({_gcSpikeCount}gc)");
            SB.Append($" GC:{totalGC}");
            SB.Append($" M:{mem / 1048576.0:F0}MB");
            SB.Append($" NoGC:{CNoGC}\n");
            SB.Append($"  Sim:{SimStepsTotal} ({avgSim:F1}/f) Max:{SimStepsMax}\n");

            AppendTiming("  AS ", TAdvanceSim, CAdvanceSim);
            AppendTiming("  ICO", TICO, CICO);
            AppendTiming("  ET ", TEndTurn, CEndTurn);
            AppendTiming("  GM2", TGetMove2, CGetMove2);
            AppendTiming("  GW ", TGetWork, CGetWork);
            AppendTiming("  PCL", TParseCL, CParseCL);
            AppendTiming("  CU ", TCleanup, CCleanup);
            AppendTiming("  US ", TUpdateStats, CUpdateStats);
            AppendTiming("  SS ", TStarSys, CStarSys);
            AppendTiming("  ORB", TOrbits, COrbits);

            if (ToListEliminated > 0)
                SB.Append($"  ToListSkip:{ToListEliminated}\n");
            if (ComponentCacheHits > 0)
                SB.Append($"  CompCache:{ComponentCacheHits}\n");
            if (TickerPreSized > 0)
                SB.Append($" TickPre:{TickerPreSized}");
            if (FirstOrDefaultSkipped > 0)
                SB.Append($" FOSkip:{FirstOrDefaultSkipped}");

            if (IACacheHits > 0)
                SB.Append($" ia:{IACacheHits}");
            if (ParallelBatchesRun > 0)
                SB.Append($" par:{ParallelBatchesRun}");

            SB.Append($"\n  Alloc:{FrameAllocTotal / 1024.0:F0}KB" +
                $" ({FrameAllocTotal / (CfgProfileIntervalSec * 1024.0):F0}KB/s)");

            long other = FrameAllocTotal - AllocAdvanceSim - AllocStarSys;
            SB.Append($"\n  A.AS:{AllocAdvanceSim / 1024.0:F0}KB" +
                $" A.SS:{AllocStarSys / 1024.0:F0}KB" +
                $" A.Oth:{other / 1024.0:F0}KB");
            SB.Append($"\n  AS-breakdown ET:{AllocEndTurn / 1024.0:F0}KB" +
                $" GM2:{AllocGetMove2 / 1024.0:F0}KB" +
                $" GW:{AllocGetWork / 1024.0:F0}KB" +
                $" PCL:{AllocParseCL / 1024.0:F0}KB" +
                $" CU:{AllocCleanup / 1024.0:F0}KB" +
                $" US:{AllocUpdateStats / 1024.0:F0}KB");

            if (_heapExpandResult.Length > 0)
                SB.Append($"\n  Heap: {_heapExpandResult}");

            if (_forcedGCCount > 0)
            {
                long eCeil = _effectiveCeilingMB > 0
                    ? _effectiveCeilingMB : CfgMemCeilingMB;
                SB.Append($"\n  GC-Ceil: {_forcedGCCount}x forced" +
                    $" cur:{mem / 1048576}/{eCeil}MB");
                if (_effectiveCeilingMB > 0)
                    SB.Append("(auto)");
            }

            SB.Append($"\n  GCLatency: {GCSettings.LatencyMode}");

            SB.Append("\n=================");
            Log.LogInfo(SB.ToString());

            _memAtReport = mem;
            _gcAtReport = gc;
            _frameCount = 0; _totalFrameMs = 0; _worstFrameMs = 0;
            _spikeCount = 0; _gcSpikeCount = 0; _pausedFrames = 0;
            SimStepsTotal = 0; SimStepsMax = 0;
            TAdvanceSim = 0; CAdvanceSim = 0;
            TICO = 0; CICO = 0;
            TEndTurn = 0; CEndTurn = 0;
            TGetMove2 = 0; CGetMove2 = 0;
            TGetWork = 0; CGetWork = 0;
            TParseCL = 0; CParseCL = 0;
            TStarSys = 0; CStarSys = 0;
            TCleanup = 0; CCleanup = 0;
            TUpdateStats = 0; CUpdateStats = 0;
            TOrbits = 0; COrbits = 0;
            TNoGC = 0; CNoGC = 0;
            IACacheHits = 0;
            ParallelBatchesRun = 0;
            ToListEliminated = 0; ComponentCacheHits = 0;
            TickerPreSized = 0; FirstOrDefaultSkipped = 0;
            FrameAllocTotal = 0;
            AllocAdvanceSim = 0; AllocStarSys = 0;
            AllocEndTurn = 0; AllocGetMove2 = 0; AllocGetWork = 0;
            AllocParseCL = 0; AllocCleanup = 0; AllocUpdateStats = 0;
        }

        private void AppendTiming(string label, long ticks, int count)
        {
            SB.Append(label);
            if (count == 0) { SB.Append(" --\n"); return; }
            float ms = (float)((double)ticks / Stopwatch.Frequency * 1000.0);
            SB.Append($" {count}x {ms:F1}ms (");
            float avg = ms / count;
            SB.Append(avg < 1f ? avg.ToString("F3") : avg.ToString("F1"));
            SB.Append("ms/c)\n");
        }

        internal static bool IsProfiling => CfgProfiling;

        internal static void RunParallelOrSafe<T>(
            List<T> items, Action<T> action, int minBatch)
        {
            if (items.Count < minBatch || Environment.ProcessorCount <= 1)
            {
                for (int i = 0; i < items.Count; i++)
                    action(items[i]);
            }
            else
            {
                ParallelBatchesRun++;
                Parallel.ForEach(items, action);
            }
        }

        internal static void RunParallelOrSafe_Array<T>(
            T[] items, Action<T> action, int minBatch)
        {
            if (items.Length < minBatch || Environment.ProcessorCount <= 1)
            {
                for (int i = 0; i < items.Length; i++)
                    action(items[i]);
            }
            else
            {
                ParallelBatchesRun++;
                Parallel.ForEach(items, action);
            }
        }

        private void OnDestroy()
        {
            SpikeProfiler.Stop();
            if (_gcModeSet)
            {
                try { GCSettings.LatencyMode = _originalGCLatencyMode; }
                catch { }
            }
        }

        private static readonly string[] _fpsLabels = new string[60];
        private static int _fpsLabelIdx;
        private static float _fpsUpdateTimer;
        private static string _fpsDisplay = "";
        private static GUIStyle _fpsStyle;

        private void OnGUI()
        {
            if (_fpsStyle == null)
            {
                _fpsStyle = new GUIStyle(GUI.skin.label);
                _fpsStyle.fontSize = 14;
                _fpsStyle.normal.textColor = new Color(1f, 1f, 0.4f, 0.9f);
                _fpsStyle.alignment = TextAnchor.UpperRight;
            }

            _fpsUpdateTimer += Time.unscaledDeltaTime;
            if (_fpsUpdateTimer >= 0.25f)
            {
                _fpsUpdateTimer = 0f;
                float fps = _frameCount > 0 ? 1000f / (_totalFrameMs / _frameCount) : 0;
                float worst = _worstFrameMs;
                long mem = GC.GetTotalMemory(false) / (1024 * 1024);
                _fpsDisplay = $"FPS:{fps:F0} Worst:{worst:F0}ms M:{mem}MB Sim:{SimStepsThisFrame}";
                _frameCount = 0; _totalFrameMs = 0; _worstFrameMs = 0;
            }

            float x = Screen.width - 220;
            float y = 25;
            GUI.Label(new Rect(x, y, 200, 20), _fpsDisplay, _fpsStyle);
        }
    }
}