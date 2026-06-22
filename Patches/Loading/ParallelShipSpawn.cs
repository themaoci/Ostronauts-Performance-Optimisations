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
    public class Patch_StarInit_ParallelShipSpawn : PatchBase
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
                LogLoadPhase("PAR-SPAWN", "StarSystem.Init enumerator was null — no ship spawning");
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
            long phaseStart = Tick();
            long memBefore = Mem();
            int gcBefore = GCs();

            LogLoadPhase("PAR-SPAWN", $"Starting parallel ship spawn: {shipCount} ships");

            // Phase 1: Consume the original enumerator's yields
            {
                int shipYields = 0;
                int nonNullYields = 0;
                object lastNonNull = null;
                long yieldStart = Tick();

                while (inner.MoveNext())
                {
                    object yielded = inner.Current;

                    if (yielded == null)
                    {
                        shipYields++;
                        continue;
                    }

                    nonNullYields++;
                    lastNonNull = yielded;
                    yield return yielded;
                }

                LogLoadPhaseTimed("PAR-SPAWN",
                    $"Phase 1 done: {shipYields} ship yields, {nonNullYields} non-null yields",
                    yieldStart);

                if (shipYields > 0 && lastNonNull == null)
                    yield return true;
            }

            // Phase 2: Parallel post-processing (RectifyBrokenIDs)
            if (shipCount >= MinParallelBatch && _rectifyMethod != null)
            {
                long phase2Start = Tick();
                try
                {
                    var system = CrewSim.system;
                    if (system == null)
                    {
                        LogLoadPhase("PAR-SPAWN", "CrewSim.system is null — skipping parallel post-process");
                        yield break;
                    }

                    var dict = _dictShipsField?.GetValue(system)
                        as Dictionary<string, Ship>;
                    if (dict == null || dict.Count == 0)
                    {
                        LogLoadPhase("PAR-SPAWN", "dictShips is null/empty — skipping parallel post-process");
                        yield break;
                    }

                    var keys = new List<string>(dict.Keys);
                    int processed = 0;
                    int errors = 0;
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
                            System.Threading.Interlocked.Increment(ref errors);
                            LogError("PAR-SPAWN", $"Post-process failed for {regID}", ex);
                        }
                    });

                    LogLoadPhaseTimed("PAR-SPAWN",
                        $"Phase 2 done: parallel post-processed {processed} ships, {errors} errors",
                        phase2Start);
                }
                catch (Exception ex)
                {
                    LogError("PAR-SPAWN", "Parallel phase", ex);
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

            LogTimedMB("PAR-SPAWN", $"Complete: {shipCount} ships", phaseStart, memBefore, gcBefore);
        }
    }
}
