using HarmonyLib;
using MelonLoader;
using System;

#if IL2CPP
using ItemInstanceType = Il2CppScheduleOne.ItemFramework.ItemInstance;
#else
using ItemInstanceType = ScheduleOne.ItemFramework.ItemInstance;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Scales the stack limit of all stackable items by Config.StackSizeMultiplier.
    /// Patches ItemInstance.StackLimit — the single access point used by all inventory
    /// and storage logic. Non-stackable items (StackLimit == 1) are never affected.
    /// </summary>
    public static class StackSizePatch
    {
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var getter = AccessTools.Property(typeof(ItemInstanceType), "StackLimit")?.GetGetMethod();
                if (getter == null) return;
                harmony.Patch(getter,
                    postfix: new HarmonyMethod(typeof(StackSizePatch), nameof(StackLimit_Postfix)));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[OTC] StackSizePatch failed to apply: " + ex.Message);
            }
        }

        private static void StackLimit_Postfix(ref int __result)
        {
            int mult = Config.StackSizeMultiplier.Value;
            if (mult <= 1) return;   // Default — zero overhead
            if (__result <= 1) return; // Non-stackable — leave alone
            __result *= mult;
        }
    }
}
