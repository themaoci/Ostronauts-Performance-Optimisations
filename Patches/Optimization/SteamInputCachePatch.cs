using System;
using System.Reflection;
using HarmonyLib;
using Steamworks;
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
}
