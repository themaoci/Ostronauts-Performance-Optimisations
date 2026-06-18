using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using System.Linq;

namespace OstronautsPerfOpt
{
    // ========================================
    // OPTIMIZATION PATCHES
    // ========================================

    // ========================================
    // GC ELIMINATION: StarSystem.Update ToList
    // ========================================

    [HarmonyPatch]
    public static class Patch_StarSystemUpdate_ToList
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(StarSystem), "Update");
        }

        private static readonly List<Ship> _shipBuffer = new List<Ship>(64);

        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            if (!PerfOptPlugin.CfgEliminateToList)
                return instructions;

            var codes = new List<CodeInstruction>(instructions);
            var bufferMethod = AccessTools.Method(
                typeof(Patch_StarSystemUpdate_ToList),
                "CopyShipsToBuffer");

            int patchCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Call &&
                    codes[i].operand is MethodInfo mi &&
                    mi.Name == "ToList" &&
                    mi.DeclaringType == typeof(Enumerable))
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, bufferMethod);
                    patchCount++;
                }
            }

            if (patchCount > 0)
                PerfOptPlugin.Log.LogInfo(
                    $"[GC-TOLIST] StarSystem.Update: replaced {patchCount} ToList() with reusable buffer");
            else
                PerfOptPlugin.Log.LogInfo(
                    "[GC-TOLIST] StarSystem.Update: no ToList() calls found in IL");

            return codes;
        }

        public static List<Ship> CopyShipsToBuffer(IEnumerable<Ship> values)
        {
            _shipBuffer.Clear();
            foreach (Ship ship in values)
                _shipBuffer.Add(ship);
            PerfOptPlugin.ToListEliminated++;
            return _shipBuffer;
        }
    }

    // ========================================
    // GC ELIMINATION: CollisionManager ToList
    // ========================================

    [HarmonyPatch]
    public static class Patch_CollisionManager_ToList
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CollisionManager),
                "CheckProjectileCollisions");
        }

        private static readonly List<Ship> _shipsBuffer = new List<Ship>(64);
        private static readonly List<Ship> _projBuffer = new List<Ship>(32);

        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            if (!PerfOptPlugin.CfgEliminateToList)
                return instructions;

            var codes = new List<CodeInstruction>(instructions);
            var shipsMethod = AccessTools.Method(
                typeof(Patch_CollisionManager_ToList),
                "CopyShipsToBuffer");
            var projMethod = AccessTools.Method(
                typeof(Patch_CollisionManager_ToList),
                "CopyProjectilesToBuffer");

            int callIdx = 0;
            int patchCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Call &&
                    codes[i].operand is MethodInfo mi &&
                    mi.Name == "ToList" &&
                    mi.DeclaringType == typeof(Enumerable))
                {
                    if (callIdx == 0)
                        codes[i] = new CodeInstruction(OpCodes.Call, shipsMethod);
                    else
                        codes[i] = new CodeInstruction(OpCodes.Call, projMethod);
                    callIdx++;
                    patchCount++;
                }
            }

            if (patchCount > 0)
                PerfOptPlugin.Log.LogInfo(
                    $"[GC-TOLIST] CollisionManager: replaced {patchCount} ToList() with reusable buffers");
            else
                PerfOptPlugin.Log.LogInfo(
                    "[GC-TOLIST] CollisionManager: no ToList() calls found in IL");

            return codes;
        }

        public static List<Ship> CopyShipsToBuffer(IEnumerable<Ship> values)
        {
            _shipsBuffer.Clear();
            foreach (Ship ship in values)
                _shipsBuffer.Add(ship);
            PerfOptPlugin.ToListEliminated++;
            return _shipsBuffer;
        }

        public static List<Ship> CopyProjectilesToBuffer(IEnumerable<Ship> values)
        {
            _projBuffer.Clear();
            foreach (Ship ship in values)
                _projBuffer.Add(ship);
            PerfOptPlugin.ToListEliminated++;
            return _projBuffer;
        }
    }

    // ========================================
    // GC ELIMINATION: Cache GetComponent calls
    // ========================================

    [HarmonyPatch]
    public static class Patch_CrewSim_CacheComponents
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "Update");
        }

        private static float _cachedScaleFactor = 1f;
        private static int _cachedScaleFactorFrame = -1;
        private static Audio_VacuumController _cachedVacCtrl;
        private static CondOwner _cachedVacCtrlCO;
        private static int _cachedVacFrame = -1;

        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            if (!PerfOptPlugin.CfgCacheComponents)
                return instructions;

            var codes = new List<CodeInstruction>(instructions);
            var getScaleFactorMethod = AccessTools.Property(typeof(Canvas), "scaleFactor")?.GetGetMethod();

            int canvasPatchCount = 0;
            int vacPatchCount = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt &&
                    codes[i].operand is MethodInfo mi &&
                    mi.Name == "get_scaleFactor" &&
                    mi.DeclaringType == typeof(Canvas) &&
                    i >= 2 &&
                    codes[i - 1].opcode == OpCodes.Callvirt &&
                    codes[i - 1].operand is MethodInfo miPrev &&
                    miPrev.Name == "GetComponent" &&
                    miPrev.IsGenericMethod &&
                    miPrev.GetGenericArguments().Length == 1 &&
                    miPrev.GetGenericArguments()[0] == typeof(Canvas))
                {
                    var cachedMethod = AccessTools.Method(
                        typeof(Patch_CrewSim_CacheComponents),
                        "GetCachedScaleFactor");

                    int startReplace = i - 1;
                    while (startReplace > 0 &&
                           (codes[startReplace - 1].opcode == OpCodes.Call ||
                            codes[startReplace - 1].opcode == OpCodes.Callvirt ||
                            codes[startReplace - 1].opcode == OpCodes.Ldsfld ||
                            codes[startReplace - 1].opcode == OpCodes.Ldfld))
                    {
                        startReplace--;
                    }

                    for (int j = startReplace; j <= i; j++)
                        codes[j] = new CodeInstruction(OpCodes.Nop);

                    codes[startReplace] = new CodeInstruction(OpCodes.Call, cachedMethod);
                    canvasPatchCount++;
                    i++;
                }
            }

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt &&
                    codes[i].operand is MethodInfo mi &&
                    mi.Name == "GetComponent" &&
                    mi.IsGenericMethod &&
                    mi.GetGenericArguments().Length == 1 &&
                    mi.GetGenericArguments()[0] == typeof(Audio_VacuumController))
                {
                    var cachedMethod = AccessTools.Method(
                        typeof(Patch_CrewSim_CacheComponents),
                        "GetCachedVacuumController");
                    codes[i] = new CodeInstruction(OpCodes.Call, cachedMethod);
                    vacPatchCount++;
                }
            }

            if (canvasPatchCount > 0 || vacPatchCount > 0)
                PerfOptPlugin.Log.LogInfo(
                    $"[GC-CACHE] CrewSim.Update: replaced {canvasPatchCount} Canvas.scaleFactor + {vacPatchCount} Audio_VacuumController calls");
            else
                PerfOptPlugin.Log.LogInfo(
                    "[GC-CACHE] CrewSim.Update: no target patterns found in IL");

            return codes;
        }

        public static float GetCachedScaleFactor()
        {
            int fc = Time.frameCount;
            if (fc != _cachedScaleFactorFrame)
            {
                _cachedScaleFactorFrame = fc;
                try
                {
                    var cm = CanvasManager.instance;
                    var go = cm != null ? cm.goCanvasGUI : null;
                    if (go != null)
                    {
                        var canvas = go.GetComponent<Canvas>();
                        _cachedScaleFactor = canvas != null ? canvas.scaleFactor : 1f;
                    }
                }
                catch { _cachedScaleFactor = 1f; }
            }
            PerfOptPlugin.ComponentCacheHits++;
            return _cachedScaleFactor;
        }

        public static Audio_VacuumController GetCachedVacuumController(CondOwner co)
        {
            if (co == null) return null;
            int fc = Time.frameCount;
            if (fc != _cachedVacFrame || co != _cachedVacCtrlCO || _cachedVacCtrl == null)
            {
                _cachedVacFrame = fc;
                _cachedVacCtrlCO = co;
                try { _cachedVacCtrl = co.GetComponent<Audio_VacuumController>(); }
                catch { _cachedVacCtrl = null; }
            }
            PerfOptPlugin.ComponentCacheHits++;
            return _cachedVacCtrl;
        }
    }
}