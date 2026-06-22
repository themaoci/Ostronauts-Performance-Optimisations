using System;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using Ostranauts.InputControl;

namespace OstronautsPerfOpt
{
    // ========================================
    // STEAM INPUT CACHE — eliminate per-call InputHandle_t[16] allocation
    // ========================================
    // Vanilla InputManager.GetControllerType() allocates a new
    // InputHandle_t[16] array on every call via:
    //     InputHandle_t[] array = new InputHandle_t[16];
    //     SteamInput.GetConnectedControllers(array);
    //
    // GetControllerType is called from GetGlyphString, which is called
    // on every QAB refresh, SetBindingLabel, etc. The 16-element array
    // allocation adds GC pressure for no benefit — the array is only used
    // as an output buffer for GetConnectedControllers and can be reused.
    //
    // Fix: Prefix that uses a cached static array, replicates the original
    // logic, and returns false to skip the original method.

    [HarmonyPatch]
    public class Patch_GetControllerType_Cache : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(InputManager), "GetControllerType");
        }

        private static readonly InputHandle_t[] _cachedControllers = new InputHandle_t[16];

        static bool Prefix(ref string __result)
        {
            if (SteamManager.Initialized)
            {
                if (SteamInput.GetConnectedControllers(_cachedControllers) != 0)
                {
                    ESteamInputType type = SteamInput.GetInputTypeForHandle(_cachedControllers[0]);
                    if (type <= ESteamInputType.k_ESteamInputType_PS4Controller)
                    {
                        if (type - ESteamInputType.k_ESteamInputType_SteamController <= 2)
                        {
                            __result = "xbox_";
                            return false;
                        }
                        if (type != ESteamInputType.k_ESteamInputType_PS4Controller)
                            goto Fallback;
                    }
                    else if (type != ESteamInputType.k_ESteamInputType_PS5Controller)
                    {
                        if (type == ESteamInputType.k_ESteamInputType_SteamDeckController)
                        {
                            __result = "deck_";
                            return false;
                        }
                        goto Fallback;
                    }
                    __result = "ps_";
                    return false;
                }
            }

            Fallback:
            if (Gamepad.current == null)
            {
                __result = "xbox_";
                return false;
            }
            if (Gamepad.current is DualShockGamepad)
            {
                __result = "ps_";
                return false;
            }
            __result = "xbox_";
            return false;
        }
    }

    // ========================================
    // FPS LOCK FIX — game's SetFPS multiplies slider value by 10
    // ========================================
    // Vanilla GUIOptions.SetFPS(float fps) does:
    //     int num = (int)fps * 10;
    //     Application.targetFrameRate = num;
    // So setting the slider to 60 gives targetFrameRate = 600, not 60.
    // This is a game bug — the *10 was likely meant for a 0-10 slider
    // range but the slider goes 0-100.
    //
    // Fix: Prefix that corrects the multiplication. The slider value is
    // already in FPS (0-100), so just cast directly.

    [HarmonyPatch]
    public class Patch_SetFPS_Fix : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(GUIOptions), "SetFPS");
        }

        static bool Prefix(float fps)
        {
            int target = (int)fps;
            if (fps == 16f)
            {
                // "Unlimited" option — set to 1000 as vanilla intended
                target = 1000;
            }
            else
            {
                // Clamp to reasonable range
                if (target < 15) target = 15;
                if (target > 1000) target = 1000;
            }

            PlayerPrefs.SetInt("TargetFPS", target);
            Application.targetFrameRate = target;
            return false; // skip original
        }
    }
}
