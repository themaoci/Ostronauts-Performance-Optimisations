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
            if (result == null)
            {
                PerfOptPlugin.Log.LogWarning(
                    "[PAR-SPAWN] StarSystem.Init enumerator was null — no ship spawning");
                return null;
            }
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
            // Track ship-specific yields and preserve the last non-null value
            // for proper outer-coroutine protocol.
            {
                int shipYields = 0;
                object lastNonNull = null;
                while (inner.MoveNext())
                {
                    object yielded = inner.Current;

                    if (yielded == null)
                    {
                        shipYields++;
                        continue;
                    }

                    // Non-null: progress bar update or final yield return true
                    lastNonNull = yielded;
                    yield return yielded;
                }

                // If we consumed all ship yields without a final non-null yield,
                // emit the preserved lastNonNull (or true as fallback)
                if (shipYields > 0 && lastNonNull == null)
                {
                    yield return true;
                }
            }

            // Phase 2: Parallel post-processing (RectifyBrokenIDs)
            if (shipCount >= MinParallelBatch && _rectifyMethod != null)
            {
                try
                {
                    var system = CrewSim.system;
                    if (system == null)
                    {
                        PerfOptPlugin.Log.LogWarning(
                            "[PAR-SPAWN] CrewSim.system is null — skipping parallel post-process");
                        yield break;
                    }

                    var dict = _dictShipsField?.GetValue(system)
                        as Dictionary<string, Ship>;
                    if (dict == null || dict.Count == 0)
                        yield break;

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
