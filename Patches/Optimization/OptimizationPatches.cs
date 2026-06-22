using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using System.Linq;
using Ostranauts.Core;
using Ostranauts.Ships.Comms;

namespace OstronautsPerfOpt
{
    // ========================================
    // OPTIMIZATION PATCHES
    // ========================================

    // ========================================
    // GC ELIMINATION: StarSystem.Update ToList
    // ========================================

    [HarmonyPatch]
    public class Patch_StarSystemUpdate_ToList : PatchBase
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
    public class Patch_CollisionManager_ToList : PatchBase
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
    // GC ELIMINATION: InteractionObjectTracker.RemoveNullsFromDictionary
    // ========================================
    // Vanilla RemoveNullsFromDictionary (v2 InteractionObjectTracker.cs:68):
    //     return (from x in this._dictTrackedInteractions
    //             where x.Value != null
    //             select x).ToDictionary(k => k.Key, v => v.Value);
    // This LINQ expression allocates: IEnumerable wrapper, lambda closure
    // objects, a new Dictionary<Guid, Interaction>, and copies all
    // surviving KeyValuePair structs. Called from ReleaseObject when a
    // null interaction is encountered.
    //
    // Fix: Prefix that collects null keys into a reusable TLS buffer and
    // removes them in-place from the existing dict, then returns the same
    // (now-cleaned) dict as __result. Avoids new dict + all copies.

    [HarmonyPatch]
    public class Patch_InteractionObjectTracker_RemoveNulls : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(InteractionObjectTracker),
                "RemoveNullsFromDictionary");
        }

        private static readonly FieldInfo _dictField =
            AccessTools.Field(typeof(InteractionObjectTracker),
                "_dictTrackedInteractions");

        [ThreadStatic]
        private static List<Guid> _tlsNullKeys;

        static bool Prefix(InteractionObjectTracker __instance,
            ref Dictionary<Guid, Interaction> __result)
        {
            if (_dictField == null)
                return true;

            try
            {
                var dict = _dictField.GetValue(__instance)
                    as Dictionary<Guid, Interaction>;
                if (dict == null)
                {
                    __result = null;
                    return false;
                }

                var keys = _tlsNullKeys;
                if (keys == null)
                {
                    keys = new List<Guid>(64);
                    _tlsNullKeys = keys;
                }
                keys.Clear();

                foreach (var kvp in dict)
                {
                    if (kvp.Value == null)
                        keys.Add(kvp.Key);
                }

                for (int i = 0; i < keys.Count; i++)
                    dict.Remove(keys[i]);

                __result = dict;
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    // ========================================
    // GC ELIMINATION: Ship.UpdateCrewSkills — eliminate GetPeople alloc
    // ========================================
    // Ship.UpdateCrewSkills (v2 Ship.cs:3229) calls GetPeople(false) which
    // allocates new List<CondOwner> + copies all crew references every call.
    // Called per-ship per-frame from StarSystem.UpdateShip. For 50 ships with
    // 10 crew each, that's 50 List<CondOwner> + 500 reference copies per frame.
    //
    // Fix: Prefix that replaces the entire method body. Iterates aPeople
    // directly (private field via reflection) and calls personSpec.GetCO()
    // inline. Uses a compiled Expression setter for the private
    // fFuelEfficiencyMod field to avoid boxing. Zero List allocations.
    //
    // Note: This patch does NOT throttle UpdateCrewSkills during time
    // acceleration, even though the method runs per-ship per-frame at x4.
    // UpdateCrewSkills sets STATIC fields (WeaponsSystem.fRangeModGunner,
    // fFuelEfficiencyMod) that reflect per-ship crew state. Throttling it
    // per-ship with a shared static timestamp would corrupt these values
    // (only one ship per frame would update the statics). The method is
    // cheap enough (HasCond checks + direct field iteration) to leave
    // running every frame.

    [HarmonyPatch]
    public class Patch_Ship_UpdateCrewSkills_NoAlloc : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Ship), "UpdateCrewSkills");
        }

        private static readonly FieldInfo _aPeopleField =
            AccessTools.Field(typeof(Ship), "aPeople");

        private static readonly Action<Ship, double> _setFuelEff =
            CreateFieldSetter("fFuelEfficiencyMod");

        private static Action<Ship, double> CreateFieldSetter(string fieldName)
        {
            var field = AccessTools.Field(typeof(Ship), fieldName);
            if (field == null) return null;
            var paramObj = Expression.Parameter(typeof(Ship), "obj");
            var paramVal = Expression.Parameter(typeof(double), "val");
            var access = Expression.Field(paramObj, field);
            var assign = Expression.Assign(access, paramVal);
            return Expression.Lambda<Action<Ship, double>>(
                assign, paramObj, paramVal).Compile();
        }

        static bool Prefix(Ship __instance)
        {
            try
            {
                var weaponsSystem = __instance.WeaponsSystem;
                var shipCO = __instance.ShipCO;

                double prevRangeMod = weaponsSystem != null
                    ? weaponsSystem.fRangeModGunner : 1.0;
                if (weaponsSystem != null)
                    weaponsSystem.fRangeModGunner = 1.0;
                if (_setFuelEff != null)
                    _setFuelEff(__instance, 1.0);

                if (shipCO != null)
                {
                    if (shipCO.HasCond("SkillOpsGunnery"))
                    {
                        if (weaponsSystem != null)
                            weaponsSystem.fRangeModGunner = 4.0;
                    }
                    if (shipCO.HasCond("SkillOpsSpaceship"))
                    {
                        if (_setFuelEff != null)
                            _setFuelEff(__instance, 0.75);
                    }
                }

                if (__instance.IsStation(true) || __instance.IsStationHidden(true))
                {
                    if (weaponsSystem != null
                        && prevRangeMod != weaponsSystem.fRangeModGunner)
                        GUIOrbitDraw.TriggerArcRedraw(__instance.strRegID);
                    return false;
                }

                var aPeople = _aPeopleField?.GetValue(__instance)
                    as List<PersonSpec>;
                if (aPeople == null || aPeople.Count == 0)
                {
                    if (weaponsSystem != null
                        && prevRangeMod != weaponsSystem.fRangeModGunner)
                        GUIOrbitDraw.TriggerArcRedraw(__instance.strRegID);
                    return false;
                }

                bool shallow = __instance.LoadState <= Ship.Loaded.Shallow;

                for (int i = 0; i < aPeople.Count; i++)
                {
                    CondOwner co = aPeople[i].GetCO();
                    if (co == null || !co.Kill) continue;
                    if (co.HasCond("Unconscious")) continue;

                    if (shallow)
                    {
                        if (co.HasCond("SkillOpsGunnery"))
                        {
                            if (weaponsSystem != null)
                                weaponsSystem.fRangeModGunner = 4.0;
                        }
                        if (co.HasCond("SkillOpsSpaceship"))
                        {
                            if (_setFuelEff != null)
                                _setFuelEff(__instance, 0.75);
                        }
                    }
                    else
                    {
                        Interaction ia = co.GetInteractionCurrent();
                        if (ia == null || ia.strName != "GUINavStationAllow")
                            continue;

                        if (co.HasCond("SkillOpsGunnery"))
                        {
                            if (weaponsSystem != null)
                                weaponsSystem.fRangeModGunner = 4.0;
                        }
                        if (co.HasCond("SkillOpsSpaceship"))
                        {
                            if (_setFuelEff != null)
                                _setFuelEff(__instance, 0.75);
                        }
                    }
                }

                if (weaponsSystem != null
                    && prevRangeMod != weaponsSystem.fRangeModGunner)
                    GUIOrbitDraw.TriggerArcRedraw(__instance.strRegID);

                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    // ========================================
    // GC ELIMINATION: CondOwner.EndTurn — pre-size aCondsTemp
    // ========================================
    // CondOwner.EndTurn (v2 CondOwner.cs:2232) does:
    //     this.aCondsTemp.AddRange(this.aCondsTimed);
    //     foreach (Condition condition in this.aCondsTemp) { ... }
    //     this.aCondsTemp.Clear();
    // AddRange may resize aCondsTemp's internal array if Capacity < Count.
    // Same pattern as our Patch_UpdateICOs_NoCopy for aTickersTemp.

    [HarmonyPatch]
    public class Patch_EndTurn_PreSizeCondsTemp : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "EndTurn");
        }

        private static readonly FieldInfo _aCondsTempField =
            AccessTools.Field(typeof(CondOwner), "aCondsTemp");
        private static readonly FieldInfo _aCondsTimedField =
            AccessTools.Field(typeof(CondOwner), "aCondsTimed");

        static void Prefix(CondOwner __instance)
        {
            if (!PerfOptPlugin.GameLoaded) return;
            if (_aCondsTempField == null || _aCondsTimedField == null) return;

            try
            {
                var aCondsTemp = _aCondsTempField.GetValue(__instance)
                    as List<Condition>;
                var aCondsTimed = _aCondsTimedField.GetValue(__instance)
                    as List<Condition>;
                if (aCondsTemp == null || aCondsTimed == null) return;

                if (aCondsTemp.Capacity < aCondsTimed.Count + 16)
                    aCondsTemp.Capacity = aCondsTimed.Count + 16;

                PerfOptPlugin.CondTempPreSized++;
            }
            catch { }
        }
    }

    // ========================================
    // GC ELIMINATION: CollisionManager.CheckCollisions — replace LINQ ToList
    // ========================================
    // CollisionManager.CheckCollisions (v2 CollisionManager.cs:251) does:
    //     List<string> list = (from x in shipCheck.GetAllDockedShips(null)
    //                          select x.strRegID).ToList<string>();
    // This LINQ expression allocates: Select iterator (closure), List<string>,
    // and internal string[] array. Called per-ship per-frame via
    // StarSystem.Update → CheckCollisions.
    //
    // Fix: Transpiler that replaces Enumerable.ToList<string> with our
    // reusable TLS List<string> buffer. The Select iterator still allocates
    // (~40 bytes) but the List + array allocation is eliminated.

    [HarmonyPatch]
    public class Patch_CheckCollisions_DockedRegIDs : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CollisionManager),
                "CheckCollisions");
        }

        [ThreadStatic]
        private static List<string> _tlsRegIDsBuffer;

        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            if (!PerfOptPlugin.CfgEliminateToList)
                return instructions;

            var codes = new List<CodeInstruction>(instructions);
            var bufferMethod = AccessTools.Method(
                typeof(Patch_CheckCollisions_DockedRegIDs),
                "CopyRegIDsToBuffer");

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
                    $"[GC-TOLIST] CollisionManager.CheckCollisions: replaced {patchCount} ToList() with reusable TLS buffer");
            else
                PerfOptPlugin.Log.LogInfo(
                    "[GC-TOLIST] CollisionManager.CheckCollisions: no ToList() calls found in IL");

            return codes;
        }

        public static List<string> CopyRegIDsToBuffer(IEnumerable<string> values)
        {
            var buf = _tlsRegIDsBuffer;
            if (buf == null)
            {
                buf = new List<string>(32);
                _tlsRegIDsBuffer = buf;
            }
            buf.Clear();
            foreach (string s in values)
                buf.Add(s);
            PerfOptPlugin.ToListEliminated++;
            return buf;
        }
    }

    // ========================================
    // GC ELIMINATION: StarSystem.DeliverMessages — eliminate Tuple allocs
    // ========================================
    // StarSystem.DeliverMessages (v2 StarSystem.cs:343) does:
    //     List<Tuple<string, ShipMessage>> list = new List<Tuple<...>>();
    //     foreach (kvp in _messagesEnRoute)
    //         if (epoch > value.AvailableTime)
    //             list.Add(new Tuple<string, ShipMessage>(key, value));
    //     foreach (tuple in list)
    //         _messagesEnRoute.Remove(tuple.Item1);
    //         OnNewShipCommsMessage.Invoke(tuple.Item2);
    // Each Tuple is a heap allocation. The List is a heap allocation.
    //
    // Fix: Prefix that replaces the method body with two parallel reusable
    // TLS lists (keys + messages). Zero Tuple + List allocations.

    [HarmonyPatch]
    public class Patch_DeliverMessages_NoAlloc : PatchBase
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(StarSystem), "DeliverMessages");
        }

        private static readonly FieldInfo _messagesEnRouteField =
            AccessTools.Field(typeof(StarSystem), "_messagesEnRoute");

        [ThreadStatic]
        private static List<string> _tlsKeys;
        [ThreadStatic]
        private static List<ShipMessage> _tlsMessages;

        static bool Prefix(StarSystem __instance)
        {
            if (_messagesEnRouteField == null)
                return true;

            try
            {
                var messagesEnRoute = _messagesEnRouteField.GetValue(__instance)
                    as IDictionary;
                if (messagesEnRoute == null || messagesEnRoute.Count == 0)
                    return false;

                var keys = _tlsKeys;
                if (keys == null)
                {
                    keys = new List<string>(16);
                    _tlsKeys = keys;
                }
                var messages = _tlsMessages;
                if (messages == null)
                {
                    messages = new List<ShipMessage>(16);
                    _tlsMessages = messages;
                }
                keys.Clear();
                messages.Clear();

                double epoch = StarSystem.fEpoch;
                foreach (DictionaryEntry entry in messagesEnRoute)
                {
                    var msg = entry.Value as ShipMessage;
                    if (msg == null) continue;
                    if (epoch > msg.AvailableTime)
                    {
                        keys.Add((string)entry.Key);
                        messages.Add(msg);
                    }
                }

                for (int i = 0; i < keys.Count; i++)
                {
                    messagesEnRoute.Remove(keys[i]);
                    try
                    {
                        StarSystem.OnNewShipCommsMessage?.Invoke(messages[i]);
                    }
                    catch { }
                }

                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}