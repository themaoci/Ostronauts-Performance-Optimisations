using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace OstronautsPerfOpt
{
    // ========================================
    // SPIKE PROFILER: Background stack sampling
    // ========================================
    // A background thread samples the main thread's call stack every 10ms
    // using reflection to call StackTrace(Thread, bool) — this constructor
    // exists in Mono runtime but not in netstandard2.1 API surface.
    // When a spike >200ms is detected in LateUpdate, the captured samples
    // are dumped, showing which methods consumed the frame time.
    // The StackTrace(Thread) constructor internally suspends the target
    // thread briefly to read its stack, then resumes it.

    internal static class SpikeProfiler
    {
        private static readonly ConcurrentQueue<Sample> _samples = new ConcurrentQueue<Sample>();
        private static Thread _samplerThread;
        private static volatile bool _running;
        private static Thread _mainThread;

        private static readonly ConstructorInfo _stackTraceCtor =
            typeof(StackTrace).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(Thread), typeof(bool) },
                null);

        internal struct Sample
        {
            public long TimestampTicks;
            public string Stack;
        }

        internal static bool HasSamples => !_samples.IsEmpty;

        internal static void Start()
        {
            if (_running) return;
            _mainThread = Thread.CurrentThread;
            _running = true;
            _samplerThread = new Thread(SamplerLoop)
            {
                Name = "SpikeProfiler",
                IsBackground = true,
                // Normal (not Highest) so we don't preempt the main thread
                // during native save/load code — preempting and suspending
                // a thread inside native Unity code can crash the process.
                Priority = ThreadPriority.BelowNormal
            };
            _samplerThread.Start();
            PerfOptPlugin.Log.LogInfo(
                "[STACK-PROF] Sampler started (StackTrace(Thread) ctor: "
                + (_stackTraceCtor != null ? "found" : "NOT found") + ")");
        }

        internal static void Stop()
        {
            _running = false;
            if (_samplerThread != null && _samplerThread.IsAlive)
                _samplerThread.Join(500);
        }

        private static void SamplerLoop()
        {
            while (_running)
            {
                // Skip sampling during save/load — the main thread is in
                // native Unity compression/serialization code and suspending
                // it there can crash the process. Sleep instead.
                if (PerfOptPlugin.IsLoading)
                {
                    Thread.Sleep(100);
                    continue;
                }

                try
                {
                    if (_mainThread != null && _stackTraceCtor != null)
                    {
                        // Check if main thread is still alive before suspending.
                        // A terminated thread makes StackTrace(Thread) throw
                        // ThreadStateException, which our catch handles, but
                        // avoiding the call is faster and safer.
                        if (!_mainThread.IsAlive)
                        {
                            _mainThread = null;
                            continue;
                        }

                        var trace = (StackTrace)_stackTraceCtor.Invoke(
                            new object[] { _mainThread, false });

                        if (trace != null && trace.FrameCount > 0)
                        {
                            string stack = SimplifyTrace(trace);
                            if (stack != null)
                            {
                                _samples.Enqueue(new Sample
                                {
                                    TimestampTicks = Stopwatch.GetTimestamp(),
                                    Stack = stack
                                });
                            }
                        }
                    }
                }
                catch
                {
                    // Thread suspension can throw if thread is in unsafe state
                }

                // Sample faster during loading to catch shorter spikes
                int interval = (PerfOptPlugin.IsLoading || !PerfOptPlugin.GameLoaded) ? 5 : 10;
                Thread.Sleep(interval);

                // Trim samples older than 3 seconds
                long cutoff = Stopwatch.GetTimestamp() - (Stopwatch.Frequency * 3);
                while (_samples.TryPeek(out var s) && s.TimestampTicks < cutoff)
                {
                    _samples.TryDequeue(out _);
                }
            }
        }

        private static string SimplifyTrace(StackTrace trace)
        {
            int count = Math.Min(trace.FrameCount, 15);
            var sb = new StringBuilder(512);
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                var frame = trace.GetFrame(i);
                if (frame == null) continue;

                var method = frame.GetMethod();
                if (method == null) continue;

                string typeName = method.DeclaringType?.Name ?? "?";
                string methodName = method.Name;

                // Skip Harmony wrapper noise
                if (methodName.Contains("DMD<") || methodName.Contains("Patched<"))
                    continue;

                // Skip PerfOpt frames
                if (typeName.Contains("PerfOpt") || typeName.Contains("Patch_"))
                    continue;

                if (sb.Length > 0) sb.Append(" -> ");
                sb.Append(typeName).Append(".").Append(methodName);
                found = true;
            }

            return found ? sb.ToString() : null;
        }

        internal static void DumpAndClear(string spikeInfo)
        {
            var samples = _samples.ToArray();
            _samples.Clear();
            if (samples.Length == 0)
            {
                PerfOptPlugin.Log.LogWarning(
                    $"[STACK-PROF] {spikeInfo} — NO SAMPLES captured (spike in native/unmanaged code, or sampler thread blocked)");
                return;
            }

            var sb = new StringBuilder(4096);
            sb.AppendLine($"[STACK-PROF] {spikeInfo} — {samples.Length} samples in last 3s:");

            // Aggregate by first 3 methods in chain for more context
            var stackCounts = new Dictionary<string, int>();
            foreach (var s in samples)
            {
                if (string.IsNullOrEmpty(s.Stack)) continue;
                string sig = GetSignature(s.Stack, 3);
                if (stackCounts.TryGetValue(sig, out int c))
                    stackCounts[sig] = c + 1;
                else
                    stackCounts[sig] = 1;
            }

            // Sort by count descending
            var sorted = stackCounts.OrderByDescending(kvp => kvp.Value);
            foreach (var kvp in sorted)
            {
                double pct = 100.0 * kvp.Value / samples.Length;
                double estMs = pct * 0.01 * ParseSpikeMs(spikeInfo);
                sb.Append($"  {kvp.Value,4}x ({pct,5:F1}%)");
                if (estMs > 0) sb.Append($" ~{estMs,6:F0}ms");
                sb.AppendLine($"  {kvp.Key}");
            }

            // Show the 3 most recent full stacks for context
            int showCount = Math.Min(3, samples.Length);
            sb.AppendLine($"  Last {showCount} full stacks:");
            for (int i = samples.Length - showCount; i < samples.Length; i++)
            {
                if (!string.IsNullOrEmpty(samples[i].Stack))
                    sb.AppendLine($"    [{i - (samples.Length - showCount) + 1}] {samples[i].Stack}");
            }

            PerfOptPlugin.Log.LogWarning(sb.ToString());
        }

        private static float ParseSpikeMs(string spikeInfo)
        {
            // Extract ms value from "[SPIKE] 515.0ms ..."
            int idx = spikeInfo.IndexOf("[SPIKE]");
            if (idx < 0) return 0f;
            int start = idx + 7;
            int end = spikeInfo.IndexOf("ms", start);
            if (end < 0) return 0f;
            float.TryParse(spikeInfo.Substring(start, end - start).Trim(),
                out float result);
            return result;
        }

        private static string GetSignature(string fullStack, int depth)
        {
            // Take first N methods from the chain "A -> B -> C"
            if (depth <= 1)
            {
                int arrow = fullStack.IndexOf(" -> ");
                return arrow >= 0 ? fullStack.Substring(0, arrow) : fullStack;
            }

            int pos = 0;
            for (int d = 1; d < depth; d++)
            {
                int arrow = fullStack.IndexOf(" -> ", pos);
                if (arrow < 0) break;
                pos = arrow + 4;
            }
            // Find next arrow after pos to cut at depth
            int nextArrow = fullStack.IndexOf(" -> ", pos);
            return nextArrow >= 0 ? fullStack.Substring(0, nextArrow) : fullStack;
        }
    }
}