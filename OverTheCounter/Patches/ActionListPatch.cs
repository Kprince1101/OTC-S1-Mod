using HarmonyLib;
using MelonLoader;
using OverTheCounter.Utilities;
using System;
using System.Collections;
using UnityEngine;

#if IL2CPP
using Il2Cpp;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Fixes game bug in ActionList.InvokeAllStaggered where the coroutine
    /// invokes via the live list (list[i]) instead of the snapshot (listCache[i]).
    /// When subscribers are removed during iteration (e.g. NPC destruction during
    /// scene transitions), the live list shrinks and list[i] throws
    /// IndexOutOfRangeException — causing 20,000+ error spam per scene change.
    /// IL2CPP uses the same snapshot + stagger + isolated invoke path as Mono; calling
    /// InvokeAll() here was wrong (no staggering, one bad subscriber aborted the whole list
    /// and produced repeated Patch Error logs / lag).
    /// </summary>
    public static class ActionListPatch
    {
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var method = AccessTools.Method(typeof(ActionList), "InvokeAllStaggered");
                if (method != null)
                {
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(ActionListPatch), nameof(Prefix)));
                    OTCLog.Msg(OTCLog.Systems.Patch, "ActionList.InvokeAllStaggered patched (snapshot fix)");
                }
                else
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, "ActionList.InvokeAllStaggered not found — patch skipped");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"ActionListPatch.Apply failed: {ex.Message}");
            }
        }

        private static bool Prefix(ActionList __instance, float staggerTime)
        {
            try
            {
                var list = __instance.GetInvocationList();
                if (list == null || list.Count == 0) return false;

#if IL2CPP
                var snapshot = list.ToArray();
                MelonCoroutines.Start(StaggeredInvokeCore(staggerTime, snapshot.Length, i =>
                {
                    var act = snapshot[i];
                    if (act == null) return;
                    try { act.Invoke(); }
                    catch (Exception ex)
                    {
                        OTCLog.Warning(OTCLog.Systems.Patch, $"Staggered subscriber invoke: {ex.Message}");
                    }
                }));
#else
                var snapshot = list.ToArray();
                MelonCoroutines.Start(StaggeredInvokeCore(staggerTime, snapshot.Length, i =>
                {
                    if (snapshot[i] == null) return;
                    try { snapshot[i](); }
                    catch (Exception ex)
                    {
                        OTCLog.Warning(OTCLog.Systems.Patch, $"Staggered subscriber invoke: {ex.Message}");
                    }
                }));
#endif
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"ActionListPatch.Prefix setup failed: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Staggers calls over <paramref name="staggerTime"/>; <paramref name="invokeAtIndex"/> runs once per snapshot index (subscribers handle their own exceptions).
        /// </summary>
        private static IEnumerator StaggeredInvokeCore(float staggerTime, int count, Action<int> invokeAtIndex)
        {
            if (count == 0) yield break;

            float perDelay = staggerTime / count;
            float waitOverflow = 0f;

            for (int i = 0; i < count; i++)
            {
                float delay = perDelay - waitOverflow;
                float before = Time.timeSinceLevelLoad;
                if (delay > 0f)
                    yield return new WaitForSeconds(delay);
                waitOverflow += Time.timeSinceLevelLoad - before - perDelay;

                invokeAtIndex(i);
            }
        }
    }
}
