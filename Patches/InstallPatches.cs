using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Ostranauts.Core;
using Ostranauts.InputControl;
using Ostranauts.UI.Quickbar.Models;
using UnityEngine;

namespace OstronautsPerfOpt
{
    // ========================================
    // BUG FIX: Keep inventory open when starting an install
    // ========================================
    // Vanilla CrewSim.InstallStart (decompiled CrewSim.cs:6083-6086) does:
    //     if (inventoryGUI.IsOpen)
    //         CommandInventory.ToggleInventory(GetSelectedCrew());
    // This force-closes the inventory (and, via the close path, hides the
    // paper-doll / active windows) the moment the player clicks "install"
    // on an item. Players expect the inventory to stay open so they can
    // queue more installs or grab another item.
    //
    // Fix: transpiler on CrewSim.InstallStart that replaces the
    // CommandInventory.ToggleInventory call with two Pop instructions
    // (one for the implicit false bForce argument, one for the CondOwner
    // from GetSelectedCrew()). The surrounding `if (inventoryGUI.IsOpen)`
    // branch still evaluates but does nothing, so no branch-target repair
    // is needed. Stack stays balanced in both the open and not-open paths.

    [HarmonyPatch]
    public class Patch_InstallStart_KeepInventoryOpen : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "InstallStart");
        }

        private static readonly MethodInfo ToggleInventoryMethod =
            AccessTools.Method(typeof(CommandInventory), "ToggleInventory",
                new Type[] { typeof(CondOwner), typeof(bool) });

        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int patchCount = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if ((codes[i].opcode == OpCodes.Call ||
                     codes[i].opcode == OpCodes.Callvirt) &&
                    codes[i].operand is MethodInfo mi &&
                    mi == ToggleInventoryMethod)
                {
                    codes[i] = new CodeInstruction(OpCodes.Pop, null);
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Pop, null));
                    patchCount++;
                    i++;
                }
            }

            if (patchCount > 0)
                PerfOptPlugin.Log.LogInfo(
                    $"[BUG-FIX] CrewSim.InstallStart: neutralized {patchCount} ToggleInventory call(s) — inventory stays open during install");
            else
                PerfOptPlugin.Log.LogInfo(
                    "[BUG-FIX] CrewSim.InstallStart: ToggleInventory call not found in IL (game may have updated)");

            return codes;
        }
    }

    // ========================================
    // BUG FIX: Keep tooltip actions clickable while a task is in progress
    // ========================================
    // Vanilla CrewSim.GetAvailActionsForCO (decompiled CrewSim.cs:4865) sets
    // `flag = true` when the crew has a current non-Walk interaction, then adds
    // "CancelAction" to the list. Every OTHER action is then added with
    // `IsClickable = !flag` (= false) — see lines 4941, 4956, 4972, 5008.
    // Result: while a task runs, the tooltip/context menu shows "Cancel Action"
    // as the only clickable option; all other actions are greyed out.
    //
    // This disable-gating was designed for the old interrupt model, where
    // clicking another action would call AIIssueOrder and override the current
    // task. With the queue-stack patch (Patch_ClaimTaskDirect_QueueStack),
    // clicking another action now appends to the back of the crew's aQueue —
    // so there is no longer a reason to disable them. Re-enable all options.
    //
    // Fix: Postfix on GetAvailActionsForCO that sets IsClickable = true on
    // every returned action, so the player can queue additional orders while
    // a task is in progress.

    [HarmonyPatch]
    public class Patch_GetAvailActions_KeepClickable : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "GetAvailActionsForCO");
        }

        static void Postfix(ref List<AvailableActionDTO> __result)
        {
            if (__result == null) return;
            for (int i = 0; i < __result.Count; i++)
            {
                if (__result[i] != null)
                    __result[i].IsClickable = true;
            }
        }
    }
}

