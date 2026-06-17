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
                Priority = ThreadPriority.Highest
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
                try
                {
                    if (_mainThread != null && _stackTraceCtor != null)
                    {
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

                Thread.Sleep(10);

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
                    $"[STACK-PROF] {spikeInfo} — NO SAMPLES captured (spike in native/unmanaged code)");
                return;
            }

            var sb = new StringBuilder(2048);
            sb.AppendLine($"[STACK-PROF] {spikeInfo} — {samples.Length} samples in last 3s:");

            // Aggregate by deepest method (first in chain)
            var stackCounts = new Dictionary<string, int>();
            foreach (var s in samples)
            {
                if (string.IsNullOrEmpty(s.Stack)) continue;
                string sig = GetSignature(s.Stack);
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
                sb.AppendLine($"  {kvp.Value,4}x ({pct,5:F1}%) {kvp.Key}");
            }

            // Show the most recent full stack
            if (samples.Length > 0)
            {
                var last = samples[samples.Length - 1];
                if (!string.IsNullOrEmpty(last.Stack))
                {
                    sb.AppendLine("  Last sample full stack:");
                    sb.AppendLine($"    {last.Stack}");
                }
            }

            PerfOptPlugin.Log.LogWarning(sb.ToString());
        }

        private static string GetSignature(string fullStack)
        {
            int arrow = fullStack.IndexOf(" -> ");
            return arrow >= 0 ? fullStack.Substring(0, arrow) : fullStack;
        }
    }
}