using HarmonyLib;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.UI.Compass;
#else
using ScheduleOne.UI.Compass;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Prevents compass markers with NULL transforms from being created.
    /// NULL transforms default to (0,0,0) causing unwanted "0m" distance markers.
    /// </summary>
    [HarmonyPatch(typeof(CompassManager), nameof(CompassManager.AddElement))]
    public static class CompassManagerAddElementPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Transform transform)
        {
            return transform != null;
        }
    }
}
