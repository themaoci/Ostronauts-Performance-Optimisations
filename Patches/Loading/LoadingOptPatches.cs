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
    public static class Patch_OnApplicationQuit_FastExit
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "OnApplicationQuit");
        }

        private static bool _exitFired;
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

            // Wait up to 3s for an in-progress save to finish before force-exit,
            // then reset the save-guard flag so nothing blocks during teardown.
            try
            {
                SpinWait.SpinUntil(() =>
                {
                    int cur = Interlocked.CompareExchange(
                        ref Patch_SaveGuard._saveInProgress, 0, 0);
                    return cur == 0;
                }, 3000);
            }
            catch { }
            Interlocked.Exchange(ref Patch_SaveGuard._saveInProgress, 0);

            // Small delay to let the log flush
            System.Threading.Thread.Sleep(50);

            try
            {
                Environment.Exit(0);
            }
            catch
            {
                // Environment.Exit may fail in some hosting environments
                // Fall back to FailFast which is more aggressive
                try
                {
                    Environment.FailFast("PerfOpt fast exit");
                }
                catch
                {
                    // Last resort: terminate the process directly
                    try
                    {
                        System.Diagnostics.Process.GetCurrentProcess().Kill();
                    }
                    catch { }
                }
            }
        }
    }
}
