using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using System.Linq;
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
        private static readonly HashSet<string> _missing =
            new HashSet<string>();
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

            if (strName == null)
            {
                __result = null;
                return false;
            }
            if (_missing.Contains(strName))
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
                _missing.Add(strName);
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
        private static readonly MethodInfo _updateStatsMethod =
            AccessTools.Method(typeof(CondOwner), "UpdateStats");
        private static CrewSim _cachedCrewSim;
        private static int _cachedCrewSimFrame = -1;
        private static readonly List<CondOwner> _coBuffer = new List<CondOwner>(256);

        static void Prefix()
        {
            if (!PerfOptPlugin.GameLoaded) return;

            bool doStats = PerfOptPlugin.CfgParallelUpdateStats
                && PerfOptPlugin.CfgVisualDeferral;
            bool doCleanup = PerfOptPlugin.CfgParallelCleanupExpiry;
            if (!doStats && !doCleanup) return;

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
            _coBuffer.Clear();
            for (int i = 0; i < tickerList.Count; i++)
            {
                if (tickerList[i] is CondOwner co)
                    _coBuffer.Add(co);
            }

            if (_coBuffer.Count == 0) return;

            double epoch = StarSystem.fEpoch;

            if (doStats && doCleanup)
            {
                PerfOptPlugin.RunParallelOrSafe(_coBuffer, co =>
                {
                    try
                    {
                        _updateStatsMethod.Invoke(co, null);
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
            else if (doStats)
            {
                PerfOptPlugin.RunParallelOrSafe(_coBuffer, co =>
                {
                    try { _updateStatsMethod.Invoke(co, null); }
                    catch { }
                }, minBatch);
            }
            else if (doCleanup)
            {
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

        [ThreadStatic]
        private static List<string> _tlsKeysBuffer;

        private static List<string> GetKeysBuffer()
        {
            if (_tlsKeysBuffer == null)
                _tlsKeysBuffer = new List<string>(32);
            _tlsKeysBuffer.Clear();
            return _tlsKeysBuffer;
        }

        public static void RunExpireOnly(CondOwner co)
        {
            try
            {
                var dict = _dictRecentlyTriedField?.GetValue(co)
                    as IDictionary;
                if (dict == null || dict.Count == 0) return;

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
            catch { }
        }
    }

    // ========================================
    // AI: Eliminate UpdateICOs aTickersTemp copy
    // ========================================

    [HarmonyPatch]
    public static class Patch_UpdateICOs_NoCopy
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CrewSim), "UpdateICOs");
        }

        static void Prefix(CrewSim __instance)
        {
            if (!PerfOptPlugin.CfgEliminateAIAllocs)
                return;

            try
            {
                var aTickersField = AccessTools.Field(typeof(CrewSim), "aTickers");
                var aTickersTempField = AccessTools.Field(typeof(CrewSim), "aTickersTemp");

                var aTickers = aTickersField?.GetValue(__instance) as List<CondOwner>;
                var aTickersTemp = aTickersTempField?.GetValue(__instance) as List<CondOwner>;

                if (aTickers == null || aTickersTemp == null) return;

                if (aTickers.Count > aTickersTemp.Capacity)
                    aTickersTemp.Capacity = aTickers.Count + 16;

                PerfOptPlugin.TickerPreSized++;
            }
            catch { }
        }
    }
}