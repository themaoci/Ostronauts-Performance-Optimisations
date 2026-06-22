using System;
using System.Reflection;
using HarmonyLib;
using Ostranauts.Core;

namespace OstronautsPerfOpt
{
    // ========================================
    // STATE RESET — reset mod state when returning to main menu
    // ========================================
    // When the player exits to main menu (character death, manual exit),
    // CrewSim and StarSystem are destroyed but the mod's static state
    // persists. On the next load attempt, patches see GameLoaded=true
    // and skip initialization, causing a silent crash.
    //
    // Fix: Hook StarSystem.OnDestroy to reset all mod state flags.

    [HarmonyPatch]
    public class Patch_StateReset : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(StarSystem), "OnDestroy");
        }

        static void Postfix()
        {
            PerfOptPlugin.GameLoaded = false;
            PerfOptPlugin.IsLoading = false;
            PerfOptPlugin.SuppressDebugLog = false;

            LogLoadPhase("STATE", "Mod state reset (StarSystem destroyed — returning to main menu)");
        }
    }
}
