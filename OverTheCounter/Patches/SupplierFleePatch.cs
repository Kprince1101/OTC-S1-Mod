using HarmonyLib;
using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using System;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.NPCs.Behaviour;
#else
using ScheduleOne.Economy;
using ScheduleOne.NPCs.Behaviour;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Suppresses flee behaviour for suppliers inside the warehouse when the door is closed.
    /// Prevents them from pathing out during scare events.
    /// </summary>
    public static class SupplierFleePatch
    {
        /// <summary>Patches FleeBehaviour.Activate to suppress flee for locked-in warehouse suppliers.</summary>
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var activate = AccessTools.Method(typeof(FleeBehaviour), "Activate");
                if (activate != null)
                    harmony.Patch(activate,
                        prefix: new HarmonyMethod(typeof(SupplierFleePatch), nameof(Activate_Prefix)));

                OTCLog.Msg(OTCLog.Systems.Patch, "SupplierFleePatch applied");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"SupplierFleePatch failed: {ex.Message}");
            }
        }

        private static bool Activate_Prefix(FleeBehaviour __instance)
        {
            try
            {
                var supplier = __instance.GetComponentInParent<Supplier>();
                if (supplier == null) return true;
                if (!OTCSupplierArea.IsWarehouseSupplier(supplier)) return true;
                if (OTCWarehouse.IsDoorOpen) return true;

                OTCLog.Msg(OTCLog.Systems.Patch,
                    $"Suppressed flee for warehouse supplier {supplier.FullName} (door closed)");
                return false;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"SupplierFleePatch prefix: {ex.Message}");
                return true;
            }
        }
    }
}
