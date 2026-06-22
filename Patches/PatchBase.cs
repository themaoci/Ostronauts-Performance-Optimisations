using System;
using System.Diagnostics;
using HarmonyLib;
using BepInEx.Logging;

namespace OstronautsPerfOpt
{
    // ========================================
    // PATCH BASE — shared logging + timing infrastructure
    // ========================================
    // All patch classes inherit from this to get:
    //   - Timing helpers (Tick, ToMs)
    //   - Memory tracking (Mem, GCs)
    //   - Timed operation logging (LogTimed)
    //   - Slow-operation warnings (LogSlow)
    //   - Load-phase logging (LogLoadPhase)
    //   - Error logging (LogError)
    //
    // Child classes override virtual hooks to add custom behavior
    // while keeping the main logging functionality in the base.
    //
    // Usage in a Harmony patch:
    //   [HarmonyPatch]
    //   public class MyPatch : PatchBase
    //   {
    //       static MethodBase TargetMethod() => ...;
    //       static void Prefix(ref long __state) {
    //           __state = Tick();
    //       }
    //       static void Postfix(long __state) {
    //           LogTimed("TAG", "detail", __state, Mem(), GCs());
    //       }
    //   }

    public abstract class PatchBase
    {
        // ========================================
        // STATIC HELPERS — usable from static Harmony methods
        // ========================================

        /// <summary>BepInEx logger (writes to LogOutput.log)</summary>
        protected static ManualLogSource Log => PerfOptPlugin.Log;

        /// <summary>High-resolution timestamp for measurements</summary>
        protected static long Tick() => Stopwatch.GetTimestamp();

        /// <summary>Convert ticks to milliseconds</summary>
        protected static float ToMs(long ticks) =>
            (float)((double)ticks / (double)Stopwatch.Frequency * 1000.0);

        /// <summary>Current managed heap size in bytes</summary>
        protected static long Mem() => GC.GetTotalMemory(false);

        /// <summary>Current GC collection count (gen0)</summary>
        protected static int GCs() => GC.CollectionCount(0);

        /// <summary>Managed heap in MB</summary>
        protected static long MemMB() => Mem() / 1048576L;

        /// <summary>Allocation delta in KB since a prior Mem() call</summary>
        protected static long AllocKB(long memBefore) =>
            (GC.GetTotalMemory(false) - memBefore) / 1024L;

        /// <summary>GC delta since a prior GCs() call</summary>
        protected static int GCDelta(int gcBefore) =>
            GC.CollectionCount(0) - gcBefore;

        // ========================================
        // LOGGING HELPERS
        // ========================================

        /// <summary>Log a timed operation with memory + GC delta</summary>
        protected static void LogTimed(string tag, string detail,
            long startTicks, long memBefore, int gcBefore)
        {
            long elapsed = Stopwatch.GetTimestamp() - startTicks;
            float ms = ToMs(elapsed);
            long allocKB = AllocKB(memBefore);
            int gcDelta = GCDelta(gcBefore);
            Log.LogInfo($"[{tag}] {detail} — {ms:F1}ms +{allocKB}KB GCx{gcDelta}");
        }

        /// <summary>Log a timed operation with full context (MB alloc)</summary>
        protected static void LogTimedMB(string tag, string detail,
            long startTicks, long memBefore, int gcBefore)
        {
            long elapsed = Stopwatch.GetTimestamp() - startTicks;
            float ms = ToMs(elapsed);
            long allocMB = (GC.GetTotalMemory(false) - memBefore) / 1048576L;
            int gcDelta = GCDelta(gcBefore);
            Log.LogInfo($"[{tag}] {detail} — {ms:F1}ms +{allocMB}MB GCx{gcDelta}");
        }

        /// <summary>Log a warning if operation exceeded threshold</summary>
        protected static void LogSlow(string tag, string detail,
            long startTicks, float thresholdMs)
        {
            float ms = ToMs(Stopwatch.GetTimestamp() - startTicks);
            if (ms > thresholdMs)
                Log.LogWarning($"[{tag}] SLOW: {detail} — {ms:F1}ms (threshold: {thresholdMs:F0}ms)");
        }

        /// <summary>Log a load-phase transition with heap state</summary>
        protected static void LogLoadPhase(string phase, string detail)
        {
            long mem = Mem();
            int gc = GC.CollectionCount(0);
            Log.LogInfo($"[LOAD] {phase}: {detail} [M={mem / 1048576L}MB GC={gc}]");
        }

        /// <summary>Log a load-phase transition with timing</summary>
        protected static void LogLoadPhaseTimed(string phase, string detail,
            long startTicks)
        {
            float ms = ToMs(Stopwatch.GetTimestamp() - startTicks);
            long mem = Mem();
            int gc = GC.CollectionCount(0);
            Log.LogInfo($"[LOAD] {phase}: {detail} — {ms:F1}ms [M={mem / 1048576L}MB GC={gc}]");
        }

        /// <summary>Log an error with context</summary>
        protected static void LogError(string tag, string context, Exception ex)
        {
            Log.LogWarning($"[{tag}] ERROR in {context}: {ex.GetType().Name}: {ex.Message}");
        }

        /// <summary>Log a patch result (OK/FAIL/SKIP)</summary>
        protected static void LogPatchResult(string patchName, bool ok, string detail = "")
        {
            string status = ok ? "OK" : "FAIL";
            string suffix = string.IsNullOrEmpty(detail) ? "" : $" — {detail}";
            Log.LogInfo($"[PATCH] {patchName}: {status}{suffix}");
        }

        // ========================================
        // VIRTUAL HOOKS — override in child classes
        // ========================================

        /// <summary>Called when a load phase begins</summary>
        public virtual void OnLoadPhaseStart(string phase) { }

        /// <summary>Called when a load phase ends</summary>
        public virtual void OnLoadPhaseEnd(string phase, long elapsedMs,
            long allocKB, int gcDelta) { }

        /// <summary>Called when a patch encounters an error</summary>
        public virtual void OnPatchError(string context, Exception ex)
        {
            Log.LogWarning($"[{PatchName}] Error in {context}: {ex.Message}");
        }

        /// <summary>Patch name for log prefixes (override to customize)</summary>
        public virtual string PatchName => GetType().Name;
    }
}
