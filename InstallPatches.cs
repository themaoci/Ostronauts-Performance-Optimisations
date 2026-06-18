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
    public static class Patch_InstallStart_KeepInventoryOpen
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
    // BUG FIX: Stack installs on the back of the crew's queue
    // ========================================
    // Vanilla WorkManager.ClaimTaskDirect (decompiled WorkManager.cs:679-730)
    // ends the success branch with:
    //     iact.objUs.AIIssueOrder(iact.objThem, iact, bPlayerOrdered: true, null);
    // AIIssueOrder (CondOwner.cs:7268) calls AICancelAll() at line 7280 BEFORE
    // QueueInteraction — it wipes the crew's entire aQueue, then adds the new
    // order. So installing an item while a crewmember is busy interrupts and
    // overrides whatever they were doing.
    //
    // Fix: Prefix on WorkManager.ClaimTaskDirect that, after AddTask succeeds
    // and iact.Triggered is true, calls iact.objUs.QueueInteraction(iact.objThem,
    // iact, bInsert: false) directly (appends to the back of aQueue without
    // cancelling) and skips the original method body. QueueInteraction is the
    // exact same append path AIIssueOrder uses at line 7304, minus the cancel.
    //
    // ALT-MODIFIER OVERRIDE: hold Left Alt while issuing the order to invoke
    // the vanilla interrupt/override behavior (AIIssueOrder wipes the queue).
    // The game already uses Input.GetKey(KeyCode.LeftAlt) elsewhere
    // (CrewSim.cs:7242), so the legacy Input Manager is enabled — safe to call.
    //
    // HUMAN-LIKE SETTLE DELAY: when stacking behind an existing queue
    // (aQueue.Count > 0), insert a "Wait" interaction with a randomized
    // fDuration of 500-1250ms before the new task, so crew pause briefly
    // between consecutive queued tasks instead of snapping instantly. When
    // the queue is empty (immediate start) no delay is inserted — the crew
    // is idle and should pick up work at once. Wait fDuration is in hours
    // (500ms ≈ 0.0001389h, 1250ms ≈ 0.0003472h). MathUtils.Rand with
    // RandType.High returns a human-skewed random in [min,max].

    [HarmonyPatch]
    public static class Patch_ClaimTaskDirect_QueueStack
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(WorkManager), "ClaimTaskDirect");
        }

        private const float SettleDelayMinMs = 500f;
        private const float SettleDelayMaxMs = 1250f;

        private static readonly MethodInfo AddTaskMethod =
            AccessTools.Method(typeof(WorkManager), "AddTask",
                new Type[] { typeof(Task2), typeof(int) });

        private static readonly FieldInfo _taskUpstreamField =
            AccessTools.Field(typeof(WorkManager), "taskUpstream");
        private static readonly FieldInfo _dictTasks2Field =
            AccessTools.Field(typeof(WorkManager), "dictTasks2");
        private static readonly FieldInfo _aTasksActiveField =
            AccessTools.Field(typeof(WorkManager), "aTasksActive");
        private static readonly MethodInfo _idleRemoveMethod =
            AccessTools.Method(typeof(WorkManager), "IdleRemove",
            new Type[] { typeof(CondOwner) });

        static bool Prefix(WorkManager __instance, Interaction iact)
        {
            if (iact == null || iact.objUs == null || iact.objThem == null)
                return false;

            if (Input.GetKey(KeyCode.LeftAlt))
            {
                PerfOptPlugin.Log.LogInfo(
                    $"[QUEUE] Alt held — vanilla interrupt for {iact.objUs.strName} ({iact.strName})");
                return true;
            }

            CondOwner objUs = iact.objUs;

            Task2 task = new Task2();
            task.strDuty = iact.strDuty;
            task.strInteraction = iact.strName;
            task.strName = iact.strTitle;
            task.strTargetCOID = iact.objThem.strID;
            task.bManual = true;
            task.SetIA(iact);
            task.AddOwner(objUs.strID);

            if (iact.objThem != null && iact.objThem.GetComponent<Placeholder>() != null)
                iact.CTTestThem = null;

            _taskUpstreamField?.SetValue(__instance, task);

            bool added = (bool)AddTaskMethod.Invoke(__instance,
                new object[] { task, 1 });
            bool triggered = iact.Triggered(objUs, iact.objThem);

            if (added && triggered)
            {
                _taskUpstreamField?.SetValue(__instance, null);

                var dictTasks2 = _dictTasks2Field?.GetValue(__instance)
                    as IDictionary;
                if (dictTasks2 != null && dictTasks2.Contains(task.strDuty))
                {
                    var list = dictTasks2[task.strDuty] as IList;
                    if (list != null && list.Contains(task))
                        list.Remove(task);
                }

                var aTasksActive = _aTasksActiveField?.GetValue(__instance) as IList;
                if (aTasksActive != null && !aTasksActive.Contains(task))
                    aTasksActive.Add(task);

                int queueDepth = objUs.aQueue != null ? objUs.aQueue.Count : 0;
                if (queueDepth > 0)
                {
                    float delayMs = MathUtils.Rand(SettleDelayMinMs,
                        SettleDelayMaxMs, MathUtils.RandType.High);
                    double delayHours = (double)delayMs / 1000.0 / 3600.0;
                    Interaction wait = DataHandler.GetInteraction("Wait");
                    if (wait != null)
                    {
                        wait.objUs = objUs;
                        wait.objThem = iact.objThem;
                        wait.fDuration = delayHours;
                        wait.fDurationOrig = delayHours;
                        objUs.QueueInteraction(iact.objThem, wait, false);
                    }
                }

                objUs.QueueInteraction(iact.objThem, iact, false);
                task.strStatus = "Queued by " + objUs.strNameFriendly;
                CrewSim.guiPDA.AddTask(task);
                _idleRemoveMethod?.Invoke(__instance, new object[] { objUs });
            }
            else
            {
                _taskUpstreamField?.SetValue(__instance, null);
                string text = iact.FailReasons(true, true, false);
                objUs.LogMessage(text, "Bad", objUs.strName);
                task.strStatus = "Last attempt by " + objUs.strNameFriendly + "\n";
                task.strStatus += text;
                CrewSim.guiPDA.AddTask(task);
                JsonTicker jsonTicker = new JsonTicker();
                jsonTicker.strName = "AINudge";
                jsonTicker.bQueue = true;
                jsonTicker.fPeriod = 0.0;
                jsonTicker.SetTimeLeft(jsonTicker.fPeriod);
                objUs.AddTicker(jsonTicker);
            }

            return false;
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
    public static class Patch_GetAvailActions_KeepClickable
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

