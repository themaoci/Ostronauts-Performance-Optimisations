using System;
using System.Reflection;
using System.Runtime;
using HarmonyLib;
using Ostranauts.Core;

namespace OstronautsPerfOpt
{
    // ========================================
    // BUGFIX: Eliminate black screen hang on quit
    // ========================================
    // When the game is exited (Alt+F4 or Menu->Quit), Unity's Application.Quit
    // triggers OnDestroy on every MonoBehaviour. The Ostranauts scene teardown
    // takes 10-30 seconds due to serial cleanup of thousands of ships, COs,
    // and system objects. Unity then waits for all pending async operations
    // before actually terminating the process.
    //
    // Fix: Prefix on CrewSim.OnApplicationQuit that forces Environment.Exit(0)
    // after the game's quit handler runs. The game handles state save/PDA
    // commit in the OnApplicationQuit flow — this runs after that, so no
    // save data is lost. The Environment.Exit bypasses Unity's slow scene
    // teardown entirely, terminating the process in <100ms.

    [HarmonyPatch]
    public static class Patch_OnApplicationQuit_FastExit
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "OnApplicationQuit");
        }

        private static bool _exitFired;

        static void Postfix()
        {
            if (_exitFired) return;
            _exitFired = true;

            try
            {
                GCSettings.LatencyMode = GCLatencyMode.Interactive;
            }
            catch { }

            PerfOptPlugin.Log.LogInfo("[QUIT] Fast exit — terminating process");
            System.Threading.Thread.Sleep(100);
            Environment.Exit(0);
        }
    }
}
