using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Ostranauts.Core;

namespace OstronautsPerfOpt
{
    // ========================================
    // FFWD / TIME ACCEL OPTIMIZATIONS
    // ========================================
    // During time acceleration (Time.timeScale up to 16x), CrewSim.Update
    // runs every frame with a large deltaTime. StarSystem.UpdateShip runs
    // per ship per frame, calling Ship.UpdateCrewSkills() which iterates
    // GetPeople() and checks HasCond for every crew member.
    // UpdateCrewSkills only changes outputs when crew skills change (rare
    // — happens on level-up, role change, or unconsciousness). Throttling
    // to once per ~5 sim seconds during time accel cuts 16x per-second
    // cost to ~3x with zero gameplay impact during travel.

    [HarmonyPatch]
    public static class Patch_UpdateCrewSkills_Throttle
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Ship), "UpdateCrewSkills");
        }

        private static double _lastRunEpoch = double.MinValue;

        static bool Prefix(Ship __instance)
        {
            double epoch = StarSystem.fEpoch;
            if (Time.timeScale > 1f && _lastRunEpoch > double.MinValue
                && epoch - _lastRunEpoch < 5.0)
            {
                return false;
            }
            _lastRunEpoch = epoch;
            return true;
        }
    }

}
