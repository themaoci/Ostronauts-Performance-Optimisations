using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Ostranauts;
using Ostranauts.Core;

namespace OstronautsPerfOpt
{
    // ========================================
    // LOADING: Parallel ship spawning in StarSystem.Init
    // ========================================
    // StarSystem.Init(JsonStarSystemSave, JsonShip[]) spawns ALL ships one at a
    // time, yielding once per ship (Decompiled lines 242-256). For 300 ships
    // that's 300 frames of ship spawning at 1 ship/frame.
    //
    // Patch_StarInit_BatchShipSpawn already batches null yields to 10/frame,
    // but the actual _SpawnShip + InitShip(Shallow) work runs sequentially
    // on the main thread inside each MoveNext() call.
    //
    // This patch replaces Patch_StarInit_BatchShipSpawn with a version that:
    //   1. Processes ALL ships in one batch by consuming all null yields
    //      without yielding to Unity. 300 ships become ~1 frame.
    //   2. After spawning, runs RectifyBrokenIDs in parallel using
    //      Parallel.ForEach. This is a pure data operation on each ship's
    //      internal CO list — thread-safe because each ship owns its own COs.
    //   3. A single yield at the end tells the outer BatchedCoroutineLoad
    //      (3 yields/frame) to pass through to Unity.

    [HarmonyPatch]
    public static class Patch_StarInit_ParallelShipSpawn
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(StarSystem), "Init",
                new[] { typeof(JsonStarSystemSave), typeof(JsonShip[]) });
        }

        static IEnumerator Postfix(IEnumerator result,
            JsonStarSystemSave objSystem, JsonShip[] aShips)
        {
            if (result == null) return null;
            return ParallelBatchedInit(result, aShips);
        }

        private static readonly FieldInfo _dictShipsField =
            AccessTools.Field(typeof(StarSystem), "dictShips");

        private static readonly MethodInfo _rectifyMethod =
            AccessTools.Method(typeof(Ship), "RectifyBrokenIDs",
                new[] { typeof(List<CondOwner>) });

        private const int MinParallelBatch = 8;

        private static IEnumerator ParallelBatchedInit(
            IEnumerator inner, JsonShip[] aShips)
        {
            int shipCount = aShips?.Length ?? 0;

            // Phase 1: Consume the original enumerator's yields.
            // Per-ship yields (null) are consumed without yielding to Unity.
            // Non-null yields (progress bars, the final yield return true)
            // pass through to the outer BatchedCoroutineLoad.
            //
            // The ship spawn work (_SpawnShip + InitShip Shallow) happens
            // inside inner.MoveNext() on the main thread. Since there are no
            // per-frame yields, all ships are spawned in a tight loop.
            {
                int steps = 0;
                while (inner.MoveNext())
                {
                    object yielded = inner.Current;
                    steps++;

                    if (yielded != null)
                    {
                        // Non-null yield — progress bar or final yield return true.
                        steps = 0;
                        yield return yielded;
                    }
                    else if (steps >= shipCount && shipCount > 0)
                    {
                        // All ship-spawn yields consumed in one batch.
                        // Yield a non-null value so the outer BatchedCoroutineLoad
                        // (which batches only null yields) passes this through.
                        steps = 0;
                        yield return true;
                    }
                    // else: null yield consumed by batching — no yield to Unity
                }
            }

            // Phase 2: Parallel post-processing.
            // RectifyBrokenIDs is a pure data operation on each ship's CO
            // list. The initial sequential run already happened inside
            // InitShip(Shallow) during Phase 1. This parallel pass re-runs
            // it as a safety net for the rare case where the sequential run
            // had partial results (e.g., from the ship yield-consumption).
            //
            // FUTURE: With a Harmony Transpiler on InitShip, Phase 1 could
            // skip the sequential RectifyBrokenIDs and Phase 2 would be
            // the ONLY cleanup pass, running in true parallel.
            if (shipCount >= MinParallelBatch && _rectifyMethod != null)
            {
                try
                {
                    var system = CrewSim.system;
                    var dict = _dictShipsField?.GetValue(system)
                        as Dictionary<string, Ship>;
                    if (dict == null || dict.Count == 0)
                        yield break;

                    // Snapshot keys to avoid concurrent modification
                    var keys = new List<string>(dict.Keys);
                    int processed = 0;
                    System.Threading.Tasks.Parallel.ForEach(keys, regID =>
                    {
                        Ship ship = null;
                        lock (dict) { dict.TryGetValue(regID, out ship); }
                        if (ship == null) return;
                        if (ship.LoadState < Ship.Loaded.Shallow) return;

                        try
                        {
                            var cosList = ship.GetCOs(null, true, false, true);
                            _rectifyMethod.Invoke(ship, new object[] { cosList });
                            System.Threading.Interlocked.Increment(ref processed);
                        }
                        catch (Exception ex)
                        {
                            PerfOptPlugin.Log.LogWarning(
                                $"[PAR-SPAWN] Post-process failed for {regID}: {ex.Message}");
                        }
                    });

                    if (processed > 0)
                        PerfOptPlugin.Log.LogInfo(
                            $"[PAR-SPAWN] Parallel post-processed {processed} ships");
                }
                catch (Exception ex)
                {
                    PerfOptPlugin.Log.LogWarning(
                        $"[PAR-SPAWN] Parallel phase failed: {ex.Message}");
                }
            }

            // Progress bar fixup
            if (shipCount > 0)
            {
                try
                {
                    LoadingScreen.SetProgressBar(
                        LoadingScreen.GetProgress() + 0.01f,
                        "Spawning System Ships (done)");
                }
                catch { }
            }
        }
    }
}
