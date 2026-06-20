using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using System.Linq;
using System.Runtime.CompilerServices;
using Ostranauts.Condowner;
using Ostranauts.Components;
using Ostranauts.Core;
using Ostranauts.Ships;
using UnityEngine;

namespace OstronautsPerfOpt
{
    // ========================================
    // AI PATCHES
    // ========================================

    [HarmonyPatch]
    public static class Patch_FirstOrDefault
    {
        static MethodBase TargetMethod()
        {
            Type ulist = typeof(Ostranauts.Core.Models.UniqueList<CondOwner>);
            return AccessTools.Method(ulist, "FirstOrDefault");
        }

        private static readonly FieldInfo _listField =
            AccessTools.Field(
                typeof(Ostranauts.Core.Models.UniqueList<CondOwner>),
                "_list");

        static bool Prefix(object __instance, ref object __result)
        {
            if (!PerfOptPlugin.CfgFirstOrDefault)
                return true;

            try
            {
                var list = _listField?.GetValue(__instance) as IList;
                __result = (list != null && list.Count > 0)
                    ? list[0] : null;
                return false;
            }
            catch { return true; }
        }
    }

    [HarmonyPatch]
    public static class Patch_SuppressInteractionLog
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(DataHandler), "GetInteraction");
        }

        private const int MAX_MISSING = 4096;
        private static readonly ConcurrentDictionary<string, byte> _missing =
            new ConcurrentDictionary<string, byte>();
        private static readonly FieldInfo _dictField =
            AccessTools.Field(typeof(DataHandler), "dictInteractions");

        public static void ClearCache()
        {
            int n = _missing.Count;
            _missing.Clear();
            if (n > 0)
                PerfOptPlugin.Log.LogInfo($"[IA] Cache cleared ({n} entries)");
        }

        static bool Prefix(string strName, ref object __result)
        {
            if (!PerfOptPlugin.CfgInteractionCache)
                return true;

            if (!PerfOptPlugin.GameLoaded)
                return true;

            if (strName == null)
            {
                __result = null;
                return false;
            }
            if (_missing.ContainsKey(strName))
            {
                __result = null;
                PerfOptPlugin.IACacheHits++;
                return false;
            }
            if (_missing.Count >= MAX_MISSING)
                _missing.Clear();
            IDictionary dict = _dictField?.GetValue(null) as IDictionary;
            if (dict != null && !dict.Contains(strName))
            {
                _missing.TryAdd(strName, 0);
                __result = null;
                PerfOptPlugin.IACacheHits++;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_UpdateICOsParallelPrepass
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "UpdateICOs");
        }

        private static readonly FieldInfo _aTickersField =
            AccessTools.Field(typeof(CrewSim), "aTickers");
        private static readonly FieldInfo _fLastCleanupField =
            AccessTools.Field(typeof(CondOwner), "fLastCleanup");
        private static CrewSim _cachedCrewSim;
        private static int _cachedCrewSimFrame = -1;
        private static readonly List<CondOwner> _coBuffer = new List<CondOwner>(256);
        private static bool _coBufferInUse;

        static void Prefix()
        {
            if (!PerfOptPlugin.GameLoaded) return;
            if (!PerfOptPlugin.CfgParallelCleanupExpiry) return;
            if (_coBufferInUse) return;

            int fc = Time.frameCount;
            CrewSim instance = _cachedCrewSim;
            if (instance == null || fc != _cachedCrewSimFrame)
            {
                _cachedCrewSimFrame = fc;
                try
                {
                    _cachedCrewSim = CrewSim.objInstance;
                    instance = _cachedCrewSim;
                }
                catch { return; }
            }
            if (instance == null) return;

            object tickersObj = _aTickersField?.GetValue(instance);
            if (tickersObj == null) return;

            var tickerList = tickersObj as IList;
            if (tickerList == null || tickerList.Count == 0) return;

            int minBatch = PerfOptPlugin.CfgParallelMinBatch;
            _coBufferInUse = true;
            try
            {
                _coBuffer.Clear();
                for (int i = 0; i < tickerList.Count; i++)
                {
                    if (tickerList[i] is CondOwner co)
                        _coBuffer.Add(co);
                }

                if (_coBuffer.Count == 0) return;

                double epoch = StarSystem.fEpoch;

                PerfOptPlugin.RunParallelOrSafe(_coBuffer, co =>
                {
                    try
                    {
                        if (_fLastCleanupField != null)
                        {
                            double lastCleanup = (double)_fLastCleanupField.GetValue(co);
                            if (epoch - lastCleanup > 2.0)
                                Patch_CleanupExpire.RunExpireOnly(co);
                        }
                    }
                    catch { }
                }, minBatch);
            }
            finally
            {
                _coBufferInUse = false;
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_CleanupExpire
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "Cleanup");
        }

        private static readonly FieldInfo _dictRecentlyTriedField =
            AccessTools.Field(typeof(CondOwner), "dictRecentlyTried");

        private static readonly System.Runtime.CompilerServices
            .ConditionalWeakTable<CondOwner, object> _coLocks =
            new System.Runtime.CompilerServices
                .ConditionalWeakTable<CondOwner, object>();

        [ThreadStatic]
        private static List<string> _tlsKeysBuffer;

        [ThreadStatic]
        private static List<string> _tlsKeysSnapshot;

        private static List<string> GetKeysBuffer()
        {
            if (_tlsKeysBuffer == null)
                _tlsKeysBuffer = new List<string>(32);
            _tlsKeysBuffer.Clear();
            return _tlsKeysBuffer;
        }

        private static List<string> GetKeysSnapshot(IEnumerable<string> keys)
        {
            var buf = _tlsKeysSnapshot;
            if (buf == null)
            {
                buf = new List<string>(32);
                _tlsKeysSnapshot = buf;
            }
            buf.Clear();
            if (keys != null)
            {
                foreach (var k in keys)
                    buf.Add(k);
            }
            return buf;
        }

        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var snapshotMethod = AccessTools.Method(
                typeof(Patch_CleanupExpire), "GetKeysSnapshot");

            int patchCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj &&
                    codes[i].operand is ConstructorInfo ctor &&
                    ctor.DeclaringType == typeof(List<string>) &&
                    ctor.GetParameters().Length == 1)
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, snapshotMethod);
                    patchCount++;
                }
            }

            if (patchCount > 0)
                PerfOptPlugin.Log.LogInfo(
                    $"[GC-CLEANUP] CondOwner.Cleanup: replaced {patchCount} new List<string>(Keys) with reusable TLS buffer");
            else
                PerfOptPlugin.Log.LogInfo(
                    "[GC-CLEANUP] CondOwner.Cleanup: no List<string>(IEnumerable) ctor found in IL");

            return codes;
        }

        public static void RunExpireOnly(CondOwner co)
        {
            try
            {
                var dict = _dictRecentlyTriedField?.GetValue(co)
                    as IDictionary;
                if (dict == null || dict.Count == 0) return;

                var lockObj = _coLocks.GetValue(co, _ => new object());
                lock (lockObj)
                {
                    double epoch = StarSystem.fEpoch;
                    var keys = GetKeysBuffer();
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (epoch - (double)entry.Value > 60.0)
                            keys.Add((string)entry.Key);
                    }
                    for (int i = 0; i < keys.Count; i++)
                        dict.Remove(keys[i]);
                }
            }
            catch { }
        }
    }

    // ========================================
    // AI: GetMove2 full rewrite — zero alloc, fast path
    // ========================================

    [HarmonyPatch]
    public static class Patch_GetMove2_Cache
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "GetMove2");
        }

        // Private field + method access
        private static readonly FieldInfo _aPrioritiesField =
            AccessTools.Field(typeof(CondOwner), "aPriorities");
        private static readonly FieldInfo _dictRecentlyTriedField =
            AccessTools.Field(typeof(CondOwner), "dictRecentlyTried");
        private static readonly FieldInfo _aMyShipsField =
            AccessTools.Field(typeof(CondOwner), "aMyShips");

        private static HashSet<string> GetMyShips(CondOwner co)
        {
            if (co == null || _aMyShipsField == null) return null;
            return _aMyShipsField.GetValue(co) as HashSet<string>;
        }

        // Static members on CondOwner (private or inaccessible at compile time)
        private static string[] _cachedAIRandomAvoid;
        private static MethodInfo _getCOScoreMethod;
        [ThreadStatic] private static object[] _getCOScoreArgs;

        // CondTrigger is a Unity Object that can be destroyed on save/load
        // or scene change — a stale reference becomes "fake null" and calling
        // .Triggered() on it crashes the Unity main thread. Always re-fetch
        // from DataHandler (cheap dict lookup) and skip the static cache.
        private static CondTrigger GetCTPlayerCrew()
        {
            return DataHandler.GetCondTrigger("TIsPlayerCrew");
        }

        private static string[] GetAIRandomAvoid()
        {
            if (_cachedAIRandomAvoid == null)
            {
                var f = AccessTools.Field(typeof(CondOwner), "aAIRandomAvoid");
                if (f != null) _cachedAIRandomAvoid = f.GetValue(null) as string[];
            }
            return _cachedAIRandomAvoid;
        }

        private static float GetCOScore(CondOwner co, CondOwner target, InteractionHistory hist)
        {
            if (_getCOScoreMethod == null)
                _getCOScoreMethod = AccessTools.Method(typeof(CondOwner), "GetCOScore",
                    new[] { typeof(CondOwner), typeof(InteractionHistory) });
            if (_getCOScoreMethod != null)
            {
                // Use the internal Invoke with pre-allocated args array
                var args = _getCOScoreArgs;
                if (args == null) { args = new object[2]; _getCOScoreArgs = args; }
                args[0] = target;
                args[1] = hist;
                return (float)_getCOScoreMethod.Invoke(co, args);
            }
            return 0f;
        }

        // Priority.objCond (internal class)
        private static Type _priorityType;
        private static FieldInfo _priorityCondField;

        private static Condition GetPriorityCond(object priority)
        {
            if (_priorityCondField == null)
            {
                if (_priorityType == null)
                    _priorityType = typeof(CondOwner).Assembly.GetType("Ostranauts.Condowner.Priority");
                if (_priorityType != null)
                    _priorityCondField = _priorityType.GetField("objCond",
                        BindingFlags.Public | BindingFlags.Instance);
            }
            return _priorityCondField?.GetValue(priority) as Condition;
        }

        // TLS reusable buffers
        [ThreadStatic] private static List<InteractionHistory> _tlsHistories;
        [ThreadStatic] private static List<CondOwner> _tlsTargets;
        [ThreadStatic] private static Dictionary<string, List<CondOwner>> _tlsDict;
        [ThreadStatic] private static bool _tlsDictReady;

        private static void EnsureBuf()
        {
            if (_tlsHistories == null) _tlsHistories = new List<InteractionHistory>(64);
            if (_tlsTargets == null) _tlsTargets = new List<CondOwner>(64);
            if (!_tlsDictReady) { _tlsDict = new Dictionary<string, List<CondOwner>>(32); _tlsDictReady = true; }
            _tlsHistories.Clear();
            _tlsTargets.Clear();
            _tlsDict.Clear();
        }

        static bool Prefix(CondOwner __instance)
        {
            if (!PerfOptPlugin.GameLoaded) return true;

            try
            {
                // Early outs
                if (__instance.debugStop) return false;
                if (__instance.bDestroyed) return false;
                if (__instance.aInteractions == null || __instance.aInteractions.Count == 0) return false;
                var ship = __instance.ship;
                if (ship == null || !__instance.IsHumanOrRobot) return false;

                // Local state
                float bestScore = 0f;
                CondOwner bestTarget = null;
                Interaction bestIA = null;
                bool foundBest = false; // true when bestScore < 0
                string strRef = null;

                // Unity "fake null" guard: __instance.gameObject may be destroyed
                // even though C# reference is non-null. AddComponent on a
                // destroyed GameObject throws MissingReferenceException.
                if (__instance.socUs == null)
                {
                    if (__instance.gameObject == null)
                        return false;
                    __instance.socUs = __instance.gameObject.AddComponent<Social>();
                }

                var ctPlayerCrew = GetCTPlayerCrew();
                bool isPlayerCrew = ctPlayerCrew != null && ctPlayerCrew.Triggered(__instance, null, true);
                bool hasAirlockPerm = __instance.HasAirlockPermission(false);

                // Cached singletons
                var selectedCrew = CrewSim.GetSelectedCrew();
                var mapConds = __instance.mapConds;

                // Get private fields
                var aPriorities = _aPrioritiesField?.GetValue(__instance) as IList;
                var dictRecentlyTried = _dictRecentlyTriedField?.GetValue(__instance) as IDictionary;

                EnsureBuf();

                // --- PRIORITY LOOP ---
                // Emulates vanilla: creates a List copy with sleep inserted at front,
                // iterates in order. We iterate directly + evaluate sleep first.

                // Check sleep urgency (vanilla inserts Priority(-500, mapConds["StatSleep"]) at [0])
                Condition sleepCond = null;
                var sleepTrigger = DataHandler.GetCondTrigger("TIsSleepy");
                bool hasSleep = __instance.jsShiftLast != null && __instance.jsShiftLast.nID == 1
                    && __instance.HasCond("StatSleep") && mapConds != null
                    && mapConds.TryGetValue("StatSleep", out sleepCond) && sleepCond != null
                    && sleepTrigger != null && sleepTrigger.Triggered(__instance, null, true);

                if (hasSleep && !foundBest)
                    EvaluatePriority(__instance, ship, sleepCond, isPlayerCrew, hasAirlockPerm,
                        dictRecentlyTried, selectedCrew, ref bestScore, ref bestTarget, ref bestIA, ref foundBest, ref strRef);

                // Iterate aPriorities via IList (no alloc)
                if (aPriorities != null && !foundBest)
                {
                    for (int pi = 0; pi < aPriorities.Count; pi++)
                    {
                        if (foundBest) break;
                        if (mapConds == null) break;

                        var prio = aPriorities[pi];
                        if (prio == null) continue;

                        var cond = GetPriorityCond(prio);
                        if (cond == null || !mapConds.ContainsValue(cond)) continue;

                        // Skip sleep if already processed
                        if (hasSleep && cond == sleepCond) continue;

                        EvaluatePriority(__instance, ship, cond, isPlayerCrew, hasAirlockPerm,
                            dictRecentlyTried, selectedCrew, ref bestScore, ref bestTarget, ref bestIA, ref foundBest, ref strRef);
                    }
                }

                // --- FALLBACK: random pick ---
                if (bestIA == null)
                    FallbackPick(__instance, ship, isPlayerCrew, hasAirlockPerm,
                        dictRecentlyTried, selectedCrew, ref bestTarget, ref bestIA, ref strRef);

                // --- POST-PROCESS ---
                CondOwner.FreeWillLoot.ApplyCondLoot(__instance, 1f, null, 0f);
                if (bestIA == null || bestTarget == null)
                    return false;

                if (strRef == null)
                    strRef = bestTarget.strID + bestIA.strName;
                if (strRef != null && dictRecentlyTried != null && !dictRecentlyTried.Contains(strRef))
                    dictRecentlyTried[strRef] = StarSystem.fEpoch;

                if (bestTarget == selectedCrew && __instance != bestTarget
                    && bestIA.bSocial && bestIA.strRaiseUI == null && bestIA.strRaiseUIThem == null)
                    BeatManager.GenerateSocial(bestIA);

                if (__instance.CheckWalk(bestIA, bestTarget))
                {
                    DataHandler.KeepInteraction(bestIA);
                    __instance.QueueInteraction(bestTarget, bestIA, false);
                }
                else
                    DataHandler.ReleaseTrackedInteraction(bestIA);

                return false;
            }
            catch
            {
                return true;
            }
        }

        // -------------------------------------------------------
        // Evaluate one priority condition: find best interaction
        // -------------------------------------------------------
        private static void EvaluatePriority(CondOwner co, Ship ship, Condition cond,
            bool isPlayerCrew, bool hasAirlockPerm, IDictionary dictRecentlyTried,
            CondOwner selectedCrew,
            ref float bestScore, ref CondOwner bestTarget, ref Interaction bestIA,
            ref bool foundBest, ref string strRef)
        {
            var condHist = co.GetCH(cond.strName);
            if (condHist == null) return;
            var mapInteractions = condHist.mapInteractions;
            if (mapInteractions == null) return;

            // Build interaction candidate list (reusable TLS)
            _tlsHistories.Clear();
            foreach (var kvp in mapInteractions)
            {
                var iName = kvp.Key;
                var avoid = GetAIRandomAvoid();
                if (avoid != null && Array.IndexOf(avoid, iName) >= 0) continue;

                var ia = DataHandler.GetInteraction(iName, null, true);
                if (ia == null || ia.bHumanOnly || !ia.bOpener
                    || ia.CTTestUs == null || !ia.CTTestUs.Triggered(co, null, true))
                {
                    DataHandler.ReleaseTrackedInteraction(ia);
                    continue;
                }

                var hist = kvp.Value;
                if (_tlsHistories.Count == 0 && hist.fAverage < 0f)
                    _tlsHistories.Add(hist);
                else if (_tlsHistories.Count > 0 && hist.fAverage <= _tlsHistories[_tlsHistories.Count - 1].fAverage)
                    _tlsHistories.Add(hist);

                DataHandler.ReleaseTrackedInteraction(ia);
            }

            // Evaluate each candidate
            for (int hi = 0; hi < _tlsHistories.Count; hi++)
            {
                if (foundBest) break;
                var hist = _tlsHistories[hi];
                var ia = DataHandler.GetInteraction(hist.strName, null, true);
                if (ia == null) continue;

                if (ia.CTTestThem != null)
                    ia.CTTestThem.logReason = false;

                // Build target list
                _tlsTargets.Clear();
                BuildTargets(co, ship, ia, _tlsTargets);

                if (ia.CTTestThem != null)
                    ia.CTTestThem.logReason = true;

                // Filter targets
                int tc = _tlsTargets.Count;
                for (int ti = tc - 1; ti >= 0; ti--)
                {
                    var t = _tlsTargets[ti];
                    if (t == null || t.bBusy) { _tlsTargets.RemoveAt(ti); continue; }
                    if (!isPlayerCrew && t.ship != null && CrewSim.coPlayer != null)
                    {
                        var myShips = GetMyShips(CrewSim.coPlayer);
                        if (myShips != null && myShips.Contains(t.ship.strRegID))
                        { _tlsTargets.RemoveAt(ti); continue; }
                    }
                    var chk = t.strID + ia.strName;
                    if (dictRecentlyTried != null && dictRecentlyTried.Contains(chk))
                    { _tlsTargets.RemoveAt(ti); continue; }
                    if (t != co)
                    {
                        if (t == selectedCrew)
                        {
                            if (GUISocialCombat2.coUs == t || GUISocialCombat2.coThem == t)
                            { _tlsTargets.RemoveAt(ti); continue; }
                            _tlsTargets.RemoveAt(ti);
                            continue;
                        }
                        if (t.jsShiftLast != null && t.jsShiftLast.nID > 0)
                        { _tlsTargets.RemoveAt(ti); }
                    }
                }

                // Score up to 10 random targets
                int maxT = Mathf.Min(10, _tlsTargets.Count);
                for (int tj = 0; tj < maxT; tj++)
                {
                    int idx = Mathf.RoundToInt(UnityEngine.Random.value * (_tlsTargets.Count - 1));
                    var tgt = _tlsTargets[idx];
                    if (co.socUs != null && tgt.socUs != null && co.socUs != tgt.socUs)
                    {
                        var rel = co.socUs.GetRelationship(tgt.strID);
                        if (rel != null) co.SwitchRELConds(rel, true);
                    }
                    if (ia.Triggered(co, tgt, false, false, false, true, null)
                        && (hasAirlockPerm || ia.strName != "MSPortalOpenStart"
                            || !Pathfinder.CheckPressure(tgt.GetPos("use", false), tgt.ship, tgt.currentRoom)))
                    {
                        float score = hist.fAverage + GetCOScore(co, tgt, hist);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestTarget = tgt;
                            bestIA = ia;
                        }
                    }
                }

                if (bestIA != ia)
                    DataHandler.ReleaseTrackedInteraction(ia);

                if (bestScore < 0f) { foundBest = true; break; }
            }
        }

        // -------------------------------------------------------
        // Build list of interaction targets (reuses TLS buffer)
        // -------------------------------------------------------
        private static void BuildTargets(CondOwner co, Ship ship, Interaction ia, List<CondOwner> targets)
        {
            if (ia.strThemType == Interaction.TARGET_SELF)
            {
                if (ia.CTTestThem == null || ia.CTTestThem.Triggered(co, null, true))
                    targets.Add(co);
                return;
            }

            if (ia.strThemType != Interaction.TARGET_OTHER) return;

            if (ia.PSpecTestThem != null)
            {
                var person = ship.GetPerson(ia.PSpecTestThem, co.socUs, false, null);
                if (person != null)
                    targets.Add(person.MakeCondOwner(PersonSpec.StartShip.OLD, null));
                return;
            }

            // Get COs safe (always non-null in practice)
            var safeList = co.GetCOsSafe(true, ia.CTTestThem);
            if (safeList != null)
            {
                for (int si = 0; si < safeList.Count; si++)
                    targets.Add(safeList[si]);
            }

            // Merge cached GetCOs results
            List<CondOwner> cached;
            if (_tlsDict.TryGetValue(ia.CTTestThem.strName, out cached))
            {
                for (int ci = 0; ci < cached.Count; ci++)
                    targets.Add(cached[ci]);
            }
            else
            {
                var cos = ship.GetCOs(ia.CTTestThem, false, true, false);
                _tlsDict[ia.CTTestThem.strName] = cos;
                if (cos != null)
                {
                    for (int ci = 0; ci < cos.Count; ci++)
                        targets.Add(cos[ci]);
                }
            }

            targets.Remove(co);
        }

        // -------------------------------------------------------
        // Fallback: 3 random picks from aInteractions
        // -------------------------------------------------------
        private static void FallbackPick(CondOwner co, Ship ship, bool isPlayerCrew,
            bool hasAirlockPerm, IDictionary dictRecentlyTried, CondOwner selectedCrew,
            ref CondOwner bestTarget, ref Interaction bestIA, ref string strRef)
        {
            var aInts = co.aInteractions;
            if (aInts == null || aInts.Count == 0) return;

            for (int k = 0; k < 3; k++)
            {
                int idx = Mathf.RoundToInt(UnityEngine.Random.value * (aInts.Count - 1));
                var ia = DataHandler.GetInteraction(aInts[idx], null, true);
                if (ia == null) continue;

                if (!ia.bOpener || ia.bHumanOnly || ia.CTTestUs == null
                    || !ia.CTTestUs.Triggered(co, null, true))
                { DataHandler.ReleaseTrackedInteraction(ia); continue; }

                _tlsTargets.Clear();
                BuildTargets(co, ship, ia, _tlsTargets);

                if (_tlsTargets.Count == 0)
                { DataHandler.ReleaseTrackedInteraction(ia); continue; }

                int pickIdx = Mathf.RoundToInt(UnityEngine.Random.value * (_tlsTargets.Count - 1));
                var tgt = _tlsTargets[pickIdx];

                // Airlock pressure check
                if (!hasAirlockPerm && ia.strName == "MSPortalOpenStart"
                    && Pathfinder.CheckPressure(tgt.GetPos("use", false), tgt.ship, tgt.currentRoom))
                { DataHandler.ReleaseTrackedInteraction(ia); continue; }

                // Vanilla fallback ordering:
                // 1. Triggered + bBusy + net result check → discard
                if (!ia.Triggered(co, tgt, false, false, false, true, null)
                    || tgt.bBusy || co.GetNetInteractionResult(ia, false) > 0f)
                { DataHandler.ReleaseTrackedInteraction(ia); continue; }

                // 2. Social skip (selected crew, not in combat)
                bool skipFlag = false;
                if (tgt == selectedCrew && tgt != co)
                {
                    if (GUISocialCombat2.coUs == tgt || GUISocialCombat2.coThem == tgt)
                    { DataHandler.ReleaseTrackedInteraction(ia); continue; }
                    skipFlag = true;
                }
                else if (tgt != co && tgt.jsShiftLast != null && tgt.jsShiftLast.nID > 0)
                { skipFlag = true; }

                if (skipFlag)
                { DataHandler.ReleaseTrackedInteraction(ia); continue; }

                // 3. dictRecentlyTried check → success if not found
                strRef = tgt.strID + ia.strName;
                if (dictRecentlyTried == null || !dictRecentlyTried.Contains(strRef))
                {
                    bestIA = ia;
                    bestTarget = tgt;
                    return;
                }
                DataHandler.ReleaseTrackedInteraction(ia);
            }
        }
    }

    // ========================================
    // AI: EndTurn — placeholder (no-op)
    // ========================================
    // A tiered throttling optimization was attempted (skip vanilla's
    // expensive completion path when interaction.fDuration > 0), but
    // the implementation broke progress bar activation and queue
    // advancement — vanilla activates the progress bar inside the
    // "expensive" Tier 3 block (CondOwner.cs:2456-2461), and skipping
    // it meant progress bars never appeared and interactions never
    // completed. Reverted to `=> true` (run vanilla). Kept as a
    // placeholder for a future, safer optimization.

    [HarmonyPatch]
    public static class Patch_EndTurn_Throttle
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "EndTurn");
        }

        static bool Prefix(CondOwner __instance) => true;
    }

    // ========================================
    // AI: UpdateICOs — eliminate UniqueList enumerator boxing
    // ========================================
    // Vanilla UpdateICOs (CrewSim.cs:2853) does:
    //   CrewSim.aTickersTemp.AddRange(CrewSim.aTickers);
    // aTickers is UniqueList<CondOwner>. AddRange takes IEnumerable<T>,
    // and UniqueList.GetEnumerator() returns _list.GetEnumerator() as
    // IEnumerator<T> — boxing the struct List.Enumerator every call.
    // At x4 speed this is 4x boxing allocations per frame.
    //
    // Fix: Replace the entire method. Copy directly from UniqueList's
    // internal _list field (no IEnumerable, no boxing). Pre-size the temp
    // list. Then iterate aTickersTemp (List<CondOwner> foreach uses the
    // struct enumerator directly — no boxing).

    [HarmonyPatch]
    public static class Patch_UpdateICOs_NoCopy
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "UpdateICOs");
        }

        private static readonly FieldInfo _aTickersField =
            AccessTools.Field(typeof(CrewSim), "aTickers");
        private static readonly FieldInfo _aTickersTempField =
            AccessTools.Field(typeof(CrewSim), "aTickersTemp");
        private static readonly FieldInfo _ulListField =
            AccessTools.Field(
                typeof(Ostranauts.Core.Models.UniqueList<CondOwner>),
                "_list");
        private static readonly FieldInfo _fUIUpdateLastField =
            AccessTools.Field(typeof(CrewSim), "fUIUpdateLast");
        private static readonly FieldInfo _fUIUpdateHeartbeatField =
            AccessTools.Field(typeof(CrewSim), "fUIUpdateHeartbeat");

        static bool Prefix(CrewSim __instance)
        {
            if (!PerfOptPlugin.CfgEliminateAIAllocs)
                return true;

            try
            {
                CondOwner.nEndTurnsThisFrame = 0;

                var aTickers = _aTickersField?.GetValue(null)
                    as Ostranauts.Core.Models.UniqueList<CondOwner>;
                var aTickersTemp = _aTickersTempField?.GetValue(null)
                    as List<CondOwner>;
                if (aTickers == null || aTickersTemp == null)
                    return true;

                var tickersList = _ulListField?.GetValue(aTickers) as List<CondOwner>;
                if (tickersList == null)
                    return true;

                if (aTickersTemp.Capacity < tickersList.Count)
                    aTickersTemp.Capacity = tickersList.Count + 16;

                for (int i = 0; i < tickersList.Count; i++)
                    aTickersTemp.Add(tickersList[i]);

                for (int i = 0; i < aTickersTemp.Count; i++)
                {
                    CondOwner condOwner = aTickersTemp[i];
                    if (condOwner == null || condOwner.ship == null
                        || condOwner.ship.bDestroyed
                        || !condOwner.ship.gameObject.activeInHierarchy)
                    {
                        CrewSim.RemoveTicker(condOwner);
                    }
                    else
                    {
                        condOwner.UpdateManual(10);
                    }
                }
                aTickersTemp.Clear();

                double uiUpdateLast = _fUIUpdateLastField != null
                    ? (double)_fUIUpdateLastField.GetValue(__instance) : 0.0;
                double uiHeartbeat = _fUIUpdateHeartbeatField != null
                    ? (double)_fUIUpdateHeartbeatField.GetValue(__instance) : 0.0;
                if (CrewSim.fTotalGameSec - uiUpdateLast > uiHeartbeat)
                {
                    if (_fUIUpdateLastField != null)
                        _fUIUpdateLastField.SetValue(__instance, CrewSim.fTotalGameSec);
                }

                PerfOptPlugin.TickerPreSized++;
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    // ========================================
    // AI: Queue-Stack — orders append to back instead of interrupting
    // ========================================
    // Despite the class name (kept for README continuity), this patch
    // targets CondOwner.AIIssueOrder — the method that dispatches every
    // player-issued order. Vanilla AIIssueOrder calls AICancelAll(null)
    // which cancels all queued interactions before queueing the new one,
    // so every new player order interrupts the current task.
    //
    // Fix: When the crew's aQueue is non-empty and Left Alt is NOT held,
    // increment a thread-local stack-depth counter. The companion patch
    // Patch_AICancelAll_StackSkip reads that counter and skips the
    // AICancelAll call, so the new order appends to the back of aQueue.
    // Hold Left Alt for vanilla interrupt behavior.
    //
    // Combined with Patch_GetAvailActions_KeepClickable, this lets the
    // player click multiple actions to queue them up. They process one
    // by one as the queue empties.

    [HarmonyPatch]
    public static class Patch_ClaimTaskDirect_QueueStack
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "AIIssueOrder");
        }

        [ThreadStatic]
        internal static int _stackDepth;

        static void Prefix(CondOwner __instance, bool bPlayerOrdered)
        {
            bool queueHasItems = __instance.aQueue != null && __instance.aQueue.Count > 0;
            bool altHeld = Input.GetKey(KeyCode.LeftAlt);
            if (bPlayerOrdered && queueHasItems && !altHeld)
                _stackDepth++;
        }

        static void Postfix()
        {
            if (_stackDepth > 0)
                _stackDepth--;
        }
    }

    [HarmonyPatch]
    public static class Patch_AICancelAll_StackSkip
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CondOwner), "AICancelAll");
        }

        static bool Prefix()
        {
            if (Patch_ClaimTaskDirect_QueueStack._stackDepth > 0)
                return false;
            return true;
        }
    }
}