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
#if IL2CPP
                // On IL2CPP, delegate types differ (Il2CppSystem.Action vs System.Action).
                // Fall back to InvokeAll which already uses a snapshot correctly.
                __instance.InvokeAll();
#else
                var list = __instance.GetInvocationList();
                if (list == null || list.Count == 0) return false;

                var snapshot = list.ToArray();
                MelonCoroutines.Start(StaggeredInvokeFixed(snapshot, staggerTime));
#endif
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"ActionListPatch.Prefix failed: {ex.Message}");
            }
            return false;
        }

#if !IL2CPP
        private static IEnumerator StaggeredInvokeFixed(Action[] snapshot, float staggerTime)
        {
            int count = snapshot.Length;
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

                if (snapshot[i] != null)
                {
                    try { snapshot[i](); }
                    catch { /* individual failures don't corrupt iteration */ }
                }
            }
        }
#endif
    }
}
