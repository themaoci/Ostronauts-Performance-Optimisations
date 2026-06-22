using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Ostranauts.Core;
using Ostranauts.Ships;

namespace OstronautsPerfOpt
{
    // ========================================
    // PARALLEL VISITCOS — parallelize CO iteration during loading
    // ========================================
    // Ship.VisitCOs iterates over all COs on a ship and calls
    // visitor.Visit() on each. During save loading, this is called
    // 3x per ship (621 calls, 362ms total in the log).
    //
    // The inner foreach loop over mapICOs.Values.ToArray() is a
    // read-only operation on independent COs. During loading mode,
    // we replace the sequential foreach with Parallel.ForEach.
    //
    // Safety:
    //   - ToArray() creates a snapshot — no concurrent modification
    //   - Each CO is independent — no shared state between iterations
    //   - Only parallelized during IsLoading — no effect on runtime
    //   - Rooms and docked ships loops stay sequential (small counts)

    [HarmonyPatch]
    public class Patch_VisitCOs_Parallel : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Ship), "VisitCOs",
                new[] { typeof(CondOwnerVisitor), typeof(bool), typeof(bool), typeof(bool) });
        }

        private static readonly FieldInfo _mapICOsField =
            AccessTools.Field(typeof(Ship), "mapICOs");
        private static readonly FieldInfo _aDockedField =
            AccessTools.Field(typeof(Ship), "aDocked");

        static bool Prefix(Ship __instance, CondOwnerVisitor visitor,
            bool bSubObjects, bool bAllowDocked, bool bAllowLocked)
        {
            // Only parallelize during loading
            if (!PerfOptPlugin.IsLoading)
                return true;

            var mapICOs = _mapICOsField?.GetValue(__instance) as Dictionary<string, CondOwner>;
            if (mapICOs == null)
                return false;

            // Snapshot the COs array (same as vanilla ToArray)
            var cos = mapICOs.Values.ToArray();

            if (cos.Length < 8)
                return true; // too few COs, run vanilla

            // Parallel.ForEach over independent COs
            Parallel.ForEach(cos, condOwner =>
            {
                try
                {
                    if (condOwner?.objCOParent == null)
                    {
                        if (bSubObjects)
                            condOwner.VisitCOs(visitor, bAllowLocked);
                        visitor.Visit(condOwner);
                    }
                }
                catch (Exception ex)
                {
                    LogError("PAR-VISIT", $"VisitCO failed for {condOwner?.strName ?? "?"}", ex);
                }
            });

            // Rooms loop (sequential — small count)
            if (__instance.aRooms != null)
            {
                for (int i = 0; i < __instance.aRooms.Count; i++)
                {
                    var room = __instance.aRooms[i];
                    if (room?.CO != null)
                    {
                        try { visitor.Visit(room.CO); }
                        catch (Exception ex)
                        {
                            LogError("PAR-VISIT", $"Room visit failed", ex);
                        }
                    }
                }
            }

            // Docked ships loop (sequential — recursive, avoid complexity)
            if (bAllowDocked)
            {
                var aDocked = _aDockedField?.GetValue(__instance) as Dictionary<string, Ship>;
                if (aDocked != null)
                {
                    foreach (var kvp in aDocked)
                    {
                        if (kvp.Value != null)
                        {
                            try { kvp.Value.VisitCOs(visitor, bSubObjects, false, bAllowLocked); }
                            catch (Exception ex)
                            {
                                LogError("PAR-VISIT", $"Docked visit failed for {kvp.Key ?? "?"}", ex);
                            }
                        }
                    }
                }
            }

            return false; // skip vanilla
        }
    }
}
