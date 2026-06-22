using System;
using System.Reflection;
using System.Runtime;
using System.Threading;
using HarmonyLib;
using Ostranauts.Core;
using UnityEngine;

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
    //
    // Also hooks Application.quitting as a fallback in case OnApplicationQuit
    // is not called (e.g., crash during teardown).

    [HarmonyPatch]
    public class Patch_OnApplicationQuit_FastExit : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "OnApplicationQuit");
        }

        private static volatile bool _exitFired;
        private static bool _hookRegistered;

        static void Postfix()
        {
            if (_exitFired) return;
            _exitFired = true;

            // Register Application.quitting fallback on first call
            if (!_hookRegistered)
            {
                _hookRegistered = true;
                try
                {
                    Application.quitting += OnApplicationQuitting;
                }
                catch { }
            }

            DoFastExit();
        }

        private static void OnApplicationQuitting()
        {
            if (_exitFired) return;
            _exitFired = true;
            DoFastExit();
        }

        private static void DoFastExit()
        {
            try
            {
                GCSettings.LatencyMode = GCLatencyMode.Interactive;
            }
            catch { }

            PerfOptPlugin.Log.LogInfo("[QUIT] Fast exit — terminating process");

            // Reset save-guard flag so nothing blocks during teardown
            Interlocked.Exchange(ref Patch_SaveGuard._saveInProgress, 0);

            // Small delay to let the log flush
            System.Threading.Thread.Sleep(50);

            // Use Process.Kill() first — it terminates immediately without
            // running any cleanup code. Environment.Exit(0) from within
            // Unity's OnApplicationQuit callback can deadlock because
            // Unity is already in its own shutdown sequence.
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
            catch
            {
                // Fallback: FailFast is more aggressive than Environment.Exit
                try
                {
                    Environment.FailFast("PerfOpt fast exit");
                }
                catch
                {
                    // Last resort
                    try { Environment.Exit(0); }
                    catch { }
                }
            }
        }
    }
}
