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
        "Ostronauts Performance Optimizer", "5.2.1")]
    public class PerfOptPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static bool GameLoaded;
        internal static bool IsLoading;

        internal const bool CfgFirstOrDefault = true;
        internal const bool CfgInteractionCache = true;
        internal const float CfgMaxDeltaTime = 0.1f;
        internal const int CfgHeapExpansionMB = 128;
        internal const int CfgMemCeilingMB = 2048;
        internal const int CfgGCIntervalSec = 0;
        internal const int CfgMinFreeMB = 0;
        internal const float CfgGCTriggerRatio = 0.70f;
        internal const float CfgGradualGCIntervalSec = 30f;
        internal const bool CfgProfiling = true;
        internal const int CfgProfileIntervalSec = 5;
        internal const bool CfgParallelCleanupExpiry = true;
        internal const int CfgParallelMinBatch = 8;
        internal const bool CfgLowLatencyGC = true;
        internal const bool CfgEliminateToList = true;
        internal const bool CfgParallelLoading = true;
        internal const int CfgParallelLoadThreads = 4;
        internal const int CfgSaveLoadBatchSize = 0;
        internal const bool CfgEliminateAIAllocs = true;
        internal const bool CfgOptimizeTickers = true;
        internal const bool CfgThreadedSave = true;
        internal const bool CfgSkipSaveScreenshot = true;
        internal const bool CfgSaveBackup = true;

        internal static long FrameStartTimestamp;

        private static float _lastForcedGC;
        private static int _forcedGCCount;
        private static long _lastHeapAfterGC;
        private static int _ceilingFailStreak;
        private static long _effectiveCeilingMB;

        private static bool _heapExpanded;
        private static byte[][] _lohPool;
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

        internal static volatile bool SuppressDebugLog;
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
        public static int UpdateStatsSkipped;
        public static int CondTempPreSized;

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

            // SpikeProfiler: background stack sampling. Captures main-thread
            // call stacks every 10ms. When a spike >200ms is detected in
            // LateUpdate, dumps aggregated stack traces showing which
            // methods consumed the frame time.
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
                // typeof(Patch_SuppressInteractionLog), // disabled — suspected of breaking multi-item purchases
                typeof(Patch_CleanupExpire),
                typeof(Patch_UpdateICOsParallelPrepass),
                typeof(Patch_StarSystemUpdate_ToList),
                typeof(Patch_CollisionManager_ToList),
                typeof(Patch_ParallelLoad),
                typeof(Patch_DoLoadGame_BatchYields),
                typeof(Patch_DoLoadGame_FastOrphanScan),
                typeof(Patch_UpdateICOs_NoCopy),
                typeof(Patch_DebugLog_Suppress),
                typeof(Patch_DebugLogWarning_Passthrough),
                typeof(Patch_DebugLogError_Passthrough),
                typeof(Patch_SaveGame_Threaded),
                typeof(Patch_SaveGame_BeforeSave),
                typeof(Patch_OnCreateSave_Guard),
                typeof(Patch_OnOverwrite_Guard),
                typeof(Patch_SaveScreenShot_Defer),
                typeof(Patch_SaveCrewPortraits_Defer),
                typeof(Patch_Sparks_CacheFlicker),
                typeof(Patch_DamageOverTime_Skip),
                typeof(Patch_UpdateShip_FirstBO_NoAlloc),
                typeof(Patch_UpdateManual_NoTickerLog),
                typeof(Patch_InstallStart_KeepInventoryOpen),
                typeof(Patch_GetAvailActions_KeepClickable),
                // typeof(Patch_GetMove2_Cache), // disabled — suspected of causing AI movement ping-pong
                typeof(Patch_EndTurn_Throttle),
                typeof(Patch_LogHandler_IsDuplicate),
                typeof(Patch_LogHandler_TrimLog),
                typeof(Patch_InteractionObjectTracker_RemoveNulls),
                typeof(Patch_Ship_UpdateCrewSkills_NoAlloc),
                typeof(Patch_EndTurn_PreSizeCondsTemp),
                typeof(Patch_CheckCollisions_DockedRegIDs),
                typeof(Patch_DeliverMessages_NoAlloc),
                typeof(Patch_OnApplicationQuit_FastExit),
                typeof(Patch_SkipDuplicateStationSpawn),
                typeof(Patch_StarInit_ParallelShipSpawn),
                // typeof(Patch_ClaimTaskDirect_QueueStack), // disabled — movement still queued incorrectly
                // typeof(Patch_AICancelAll_StackSkip),
                typeof(Patch_GetControllerType_Cache),
                typeof(Patch_SetFPS_Fix),
                typeof(Patch_StateReset)
                // Patch_VisitCOs_Parallel disabled: visitors call Debug.Log/Debug.Break
                // which are not thread-safe. Re-enable after adding suppression.
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

            // Register loading profiler patches directly (can't use Postfix on Awake
            // because Awake only runs once and Postfix only applies to future calls)
            try
            {
                LoadingProfiler.RegisterPatches(harmony);
            }
            catch (Exception ex)
            {
                Log.LogWarning($"  [FAIL] LoadingProfiler.RegisterPatches threw: {ex.GetType().Name}: {ex.Message}");
            }

            Log.LogInfo($"=== PerfOpt v5.2.1 ({ok}/{patches.Length} patches) ===");
            int disabled = patches.Length - ok;
            if (disabled > 0)
                Log.LogInfo($"  {ok} patches active, {disabled} disabled (SuppressInteractionLog, GetMove2_Cache)");
            else
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

            if (Input.GetKeyDown(KeyCode.Insert))
                _overlayVisible = !_overlayVisible;

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

            // Rolling FPS window (always, even before GameLoaded)
            _fpsWindow[_fpsWindowIdx] = ms;
            _fpsWindowIdx = (_fpsWindowIdx + 1) % FpsWindowSize;
            float total = 0f;
            int count = 0;
            float worst = 0f;
            for (int i = 0; i < FpsWindowSize; i++)
            {
                float v = _fpsWindow[i];
                if (v > 0f) { total += v; count++; if (v > worst) worst = v; }
            }
            _fpsDisplay = count > 0 ? 1000f / (total / count) : 60f;
            _worstDisplay = worst;

            // Skip all profiling on main menu (no game loaded)
            if (!GameLoaded) return;

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
                {
                    int gen1Total = GC.CollectionCount(1);
                    int gen2Total = GC.CollectionCount(2);
                    SB.Append(" [GCx").Append(gcDelta)
                      .Append(" g1=").Append(gen1Total)
                      .Append(" g2=").Append(gen2Total).Append("]");
                }
                SB.Append(" Sim:").Append(SimStepsThisFrame);
                SB.Append(" Alloc:").Append((fAlloc / 1024).ToString()).Append("KB");
                if (IsLoading) SB.Append(" [LOADING]");
                if (paused) SB.Append(" [PAUSED]");
                Log.LogWarning(SB.ToString());

                if (ms > 200f)
                    SpikeProfiler.DumpAndClear(SB.ToString());
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
                BuildOverlayText();
                ResetCounters();
            }
        }

        private void CheckMemoryCeiling()
        {
            if (IsLoading) return;

            bool paused = false;
            try
            {
                if (_fTimeCoeffPause != null)
                    paused = (float)_fTimeCoeffPause.GetValue(null) == 0f;
            }
            catch { }
            if (paused) return;

            float now = Time.realtimeSinceStartup;
            float elapsed = now - _lastForcedGC;

            if (elapsed < 10f) return;

            long managed = GC.GetTotalMemory(false);
            long heapMB = managed / 1048576;

            long ceiling = _effectiveCeilingMB > 0
                ? _effectiveCeilingMB : CfgMemCeilingMB;
            int interval = CfgGCIntervalSec;

            long triggerThreshold = (long)(ceiling * CfgGCTriggerRatio);

            if (ceiling > 0 && heapMB > triggerThreshold
                && heapMB <= ceiling
                && elapsed >= CfgGradualGCIntervalSec)
            {
                try { GCSettings.LatencyMode = GCLatencyMode.LowLatency; }
                catch { }
                GC.Collect(0, GCCollectionMode.Optimized, false);
                _lastForcedGC = now;
                _forcedGCCount++;
                long gradAfterMB = GC.GetTotalMemory(false) / 1048576;
                Log.LogInfo($"[GC-GRAD] #{_forcedGCCount} M:{heapMB}>{gradAfterMB}MB" +
                    $" trigger@{triggerThreshold}MB ceiling={ceiling}MB (gen0)");
                return;
            }

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
                var pool = new byte[largeCount][];
                for (int i = 0; i < largeCount; i++)
                    pool[i] = new byte[LargeBlockSize];

                long peak = GC.GetTotalMemory(false);

                // Keep the pool alive in a static field so Boehm never needs
                // to trace through 1024 large objects during a GC sweep.
                // Nulling the local and collecting (as v4.3 did) made all
                // blocks unreachable at once, causing Boehm's mark stack to
                // overflow -> "Fatal error in GC: Unexpected mark stack overflow".
                _lohPool = pool;

                long after = GC.GetTotalMemory(false);
                long delta = peak - before;
                _heapExpandResult = $"M:{before / 1048576}>{after / 1048576}MB" +
                    $" +{delta / 1048576}MB (LOH pool retained)";

                Log.LogInfo($"[HEAP] Before: M={before / 1048576}MB");
                Log.LogInfo($"[HEAP] Peak:   M={peak / 1048576}MB");
                Log.LogInfo($"[HEAP] After:  M={after / 1048576}MB");
                Log.LogInfo($"[HEAP] Expanded +{delta / 1048576}MB. " +
                    $"{largeCount} x {LargeBlockSize >> 10}KB LOH blocks retained in static pool.");
            }
            catch (Exception ex)
            {
                _heapExpandResult = "FAIL: " + ex.Message;
                Log.LogError($"[HEAP] Expansion failed: {ex}");
            }
        }

        private void LogReport()
        {
            // Disabled — perf data now shown in OnGUI overlay (toggle with INSERT)
            // Kept for potential future debug use
        }

        private void BuildOverlayText()
        {
            long mem = GC.GetTotalMemory(false);
            int gc = GC.CollectionCount(0);
            float avgMs = _frameCount > 0 ? _totalFrameMs / _frameCount : 0;
            float fps = avgMs > 0 ? 1000f / avgMs : 0;
            int totalGC = gc - _gcAtReport;
            int active = _frameCount - _pausedFrames;
            float avgSim = _frameCount > 0 ? (float)SimStepsTotal / _frameCount : 0;

            var sb = new StringBuilder(2048);
            sb.Append($"PerfOpt v5.0 | Fr:{_frameCount} ({active}act) FPS:{fps:F0} Worst:{_worstFrameMs:F1}ms\n");
            sb.Append($"Sp:{_spikeCount} ({_gcSpikeCount}gc) GC:{totalGC} M:{mem / 1048576.0:F0}MB Sim:{SimStepsTotal} ({avgSim:F1}/f) Max:{SimStepsMax}\n");
            sb.Append($"AdvSim:{FmtTiming(TAdvanceSim, CAdvanceSim)} ");
            sb.Append($"ICO:{FmtTiming(TICO, CICO)} ");
            sb.Append($"EndTurn:{FmtTiming(TEndTurn, CEndTurn)}\n");
            sb.Append($"GetMove2:{FmtTiming(TGetMove2, CGetMove2)} ");
            sb.Append($"GetWork:{FmtTiming(TGetWork, CGetWork)}\n");
            sb.Append($"ParseCL:{FmtTiming(TParseCL, CParseCL)} ");
            sb.Append($"Cleanup:{FmtTiming(TCleanup, CCleanup)}\n");
            sb.Append($"UpdStats:{FmtTiming(TUpdateStats, CUpdateStats)} ");
            sb.Append($"StarSys:{FmtTiming(TStarSys, CStarSys)} ");
            sb.Append($"Orbits:{FmtTiming(TOrbits, COrbits)}\n");
            sb.Append($"Alloc:{FrameAllocTotal / 1024.0:F0}KB ({FrameAllocTotal / (CfgProfileIntervalSec * 1024.0):F0}KB/s)");
            long other = FrameAllocTotal - AllocAdvanceSim - AllocStarSys;
            sb.Append($" AS:{AllocStarSys / 1024.0:F0}KB ET:{AllocEndTurn / 1024.0:F0}KB GM2:{AllocGetMove2 / 1024.0:F0}KB O:{other / 1024.0:F0}KB\n");
            if (ToListEliminated > 0) sb.Append($"ToListSkip:{ToListEliminated} ");
            if (ComponentCacheHits > 0) sb.Append($"CompCache:{ComponentCacheHits} ");
            if (IACacheHits > 0) sb.Append($"IA:{IACacheHits} ");
            if (ParallelBatchesRun > 0) sb.Append($"Par:{ParallelBatchesRun} ");
            if (UpdateStatsSkipped > 0) sb.Append($"UpdSkip:{UpdateStatsSkipped} ");
            if (TickerPreSized > 0) sb.Append($"TickPre:{TickerPreSized} ");
            if (CondTempPreSized > 0) sb.Append($"CondPre:{CondTempPreSized} ");
            if (FirstOrDefaultSkipped > 0) sb.Append($"FODefault:{FirstOrDefaultSkipped}");
            if (_forcedGCCount > 0)
            {
                long eCeil = _effectiveCeilingMB > 0 ? _effectiveCeilingMB : CfgMemCeilingMB;
                sb.Append($"\nGC-Ceil:{_forcedGCCount}x cur:{mem / 1048576}/{eCeil}MB Lat:{GCSettings.LatencyMode}");
            }

            _metricsText = sb.ToString();
        }

        private static string FmtTiming(long ticks, int count)
        {
            if (count == 0) return "--";
            float ms = (float)((double)ticks / Stopwatch.Frequency * 1000.0);
            float avg = ms / count;
            string avgStr = avg < 1f ? avg.ToString("F3") : avg.ToString("F1");
            return $"{count}x{ms:F0}ms({avgStr}ms)";
        }

        private void ResetCounters()
        {
            long mem = GC.GetTotalMemory(false);
            int gc = GC.CollectionCount(0);
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
            UpdateStatsSkipped = 0;
            CondTempPreSized = 0;
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

            // Restore GC latency mode (no guard — always try)
            try { GCSettings.LatencyMode = _originalGCLatencyMode; }
            catch { }

            // Release Texture2D GPU memory
            if (_bgTex != null)
            {
                UnityEngine.Object.Destroy(_bgTex);
                _bgTex = null;
            }
            _overlayStyle = null;

            // Release LOH pool so Boehm GC can reclaim the memory
            if (_lohPool != null)
            {
                _lohPool = null;
                Log.LogInfo("[HEAP] LOH pool released on destroy");
            }
        }

        // Rolling FPS window: track last 60 frames for stable display
        private const int FpsWindowSize = 60;
        private static readonly float[] _fpsWindow = new float[FpsWindowSize];
        private static int _fpsWindowIdx;
        private static float _fpsDisplay;
        private static float _worstDisplay;
        private static float _fpsUpdateTimer;
        private static string _fpsLine = "";
        private static string _metricsText = "";
        private static GUIStyle _overlayStyle;
        private static Texture2D _bgTex;
        private static bool _overlayVisible = true;

        private void OnGUI()
        {
            if (!_overlayVisible) return;
            if (Event.current.type != EventType.Repaint) return;

            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.label);
                _overlayStyle.fontSize = 13;
                _overlayStyle.normal.textColor = new Color(0.9f, 1f, 0.9f, 0.95f);
                _overlayStyle.alignment = TextAnchor.UpperCenter;
                _overlayStyle.padding = new RectOffset(4, 4, 2, 2);
                _overlayStyle.wordWrap = false;
                _overlayStyle.richText = false;

                _bgTex = new Texture2D(1, 1);
                _bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.5f));
                _bgTex.Apply();
            }

            // Update FPS line every 0.25s from rolling window (calculated in LateUpdate)
            _fpsUpdateTimer += Time.unscaledDeltaTime;
            if (_fpsUpdateTimer >= 0.25f)
            {
                _fpsUpdateTimer = 0f;
                long mem = GC.GetTotalMemory(false) / (1024 * 1024);
                _fpsLine = $"FPS:{_fpsDisplay:F0}  Worst:{_worstDisplay:F0}ms  M:{mem}MB  Sim:{SimStepsThisFrame}";
            }

            // Combine FPS line + metrics into one display string
            string display = _fpsLine;
            if (!string.IsNullOrEmpty(_metricsText))
                display += "\n" + _metricsText;
            if (string.IsNullOrEmpty(display)) return;

            // Center-top panel scaled to text content, with padding
            float sw = Screen.width;
            Vector2 textSize = _overlayStyle.CalcSize(new GUIContent(display));
            float panelW = Mathf.Min(textSize.x + 16f, sw * 0.6f);
            float panelH = _overlayStyle.CalcHeight(
                new GUIContent(display), panelW);
            float panelX = (sw - panelW) * 0.5f;
            float panelY = 8f;

            GUI.DrawTexture(
                new Rect(panelX - 4f, panelY - 2f, panelW + 8f, panelH + 4f),
                _bgTex);

            GUI.Label(
                new Rect(panelX, panelY, panelW, panelH),
                display, _overlayStyle);
        }
    }
}