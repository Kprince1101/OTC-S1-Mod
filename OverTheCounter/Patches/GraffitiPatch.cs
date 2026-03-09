using HarmonyLib;
using OverTheCounter.Utilities;
using System;

#if IL2CPP
using SpraySurfaceType = Il2CppScheduleOne.Graffiti.SpraySurface;
using SpraySurfaceInteractionType = Il2CppScheduleOne.Graffiti.SpraySurfaceInteraction;
using PlayerInventoryType = Il2CppScheduleOne.PlayerScripts.PlayerInventory;
#else
using SpraySurfaceType = ScheduleOne.Graffiti.SpraySurface;
using SpraySurfaceInteractionType = ScheduleOne.Graffiti.SpraySurfaceInteraction;
using PlayerInventoryType = ScheduleOne.PlayerScripts.PlayerInventory;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Allows re-editing spray paint surfaces and prevents spray can consumption.
    /// Patch A: SpraySurface.CanBeEdited — bypass the "no existing drawing" check.
    /// Patch B: SpraySurfaceInteraction.Close + PlayerInventory.RemoveAmountOfItem —
    ///          flag-based interception to skip spray can removal.
    /// </summary>
    public static class GraffitiPatch
    {
        private static bool _skipSprayRemoval;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var canBeEdited = AccessTools.Method(typeof(SpraySurfaceType), "CanBeEdited");
                if (canBeEdited != null)
                    harmony.Patch(canBeEdited,
                        prefix: new HarmonyMethod(typeof(GraffitiPatch), nameof(CanBeEdited_Prefix)));

                var close = AccessTools.Method(typeof(SpraySurfaceInteractionType), "Close");
                if (close != null)
                    harmony.Patch(close,
                        prefix: new HarmonyMethod(typeof(GraffitiPatch), nameof(Close_Prefix)),
                        postfix: new HarmonyMethod(typeof(GraffitiPatch), nameof(Close_Postfix)));

                var removeItem = AccessTools.Method(typeof(PlayerInventoryType), "RemoveAmountOfItem",
                    new Type[] { typeof(string), typeof(uint) });
                if (removeItem != null)
                    harmony.Patch(removeItem,
                        prefix: new HarmonyMethod(typeof(GraffitiPatch), nameof(RemoveItem_Prefix)));
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, "GraffitiPatch failed to apply: " + ex.Message);
            }
        }

        /// <summary>
        /// Bypass the DrawingStrokeCount check so already-painted surfaces can be re-edited.
        /// Still respects CurrentEditor lock and Editable flag.
        /// </summary>
        private static bool CanBeEdited_Prefix(SpraySurfaceType __instance, bool checkEditor, ref bool __result)
        {
            if (!Config.GraffitiReEdit.Value) return true;

            __result = (!checkEditor || __instance.CurrentEditor == null) && __instance.Editable;
            return false;
        }

        /// <summary>
        /// Set flag before Close() runs so RemoveItem_Prefix can intercept the spray can removal.
        /// </summary>
        private static void Close_Prefix()
        {
            if (Config.GraffitiReEdit.Value)
                _skipSprayRemoval = true;
        }

        private static void Close_Postfix() => _skipSprayRemoval = false;

        /// <summary>
        /// Skip spray can removal when the flag is set.
        /// </summary>
        private static bool RemoveItem_Prefix(string ID)
        {
            if (_skipSprayRemoval && ID == "spraypaint")
            {
                _skipSprayRemoval = false;
                return false;
            }
            return true;
        }
    }
}
