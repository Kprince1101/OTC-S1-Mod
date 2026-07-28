using HarmonyLib;
using OverTheCounter.Logic;
using OverTheCounter.Utilities;

#if IL2CPP
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.ItemFramework;
using ScheduleOne.NPCs;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Managers used to be nightly-clear-immune via NPCInventory flags that no longer exist
    /// on the current game API (see ManagerSpawner.SetupInventory). Rather than fight to
    /// restore immunity, this patch leans into the clear: right before NPCInventory.Clear()
    /// wipes a manager's held cash/product for the night, it sweeps everything into the
    /// manager's assigned locker (EmployeeHome.Storage) first, then lets the vanilla clear
    /// proceed on the now-empty inventory as normal.
    ///
    /// IL2CPP-only: relies on the Framework-config accessors in PrivateAccess.cs and the
    /// NPCInventory API shape confirmed via Cecil dump against the current game version.
    /// </summary>
    [HarmonyPatch(typeof(NPCInventory), nameof(NPCInventory.Clear))]
    public static class ManagerOvernightDepositPatch
    {
        [HarmonyPrefix]
        public static void Prefix(NPCInventory __instance)
        {
            try
            {
                if (__instance == null) return;

                NPC npc;
                try { npc = __instance._npc; } catch { return; }
                if (npc == null) return;

                var manager = ManagerInstance.GetByNpc(npc);
                if (manager == null || !manager.HasLocker) return;

                var storage = manager.AssignedLocker.Storage;
                if (storage == null) return;

                // --- Cash ---
                float cash = __instance.GetCashInInventory();
                if (cash > 0f)
                {
                    __instance.RemoveCash(cash);
                    ManagerSupplyBehaviour.DepositCashToStorage(storage, cash);
                    if (Config.ManagerVerboseLogging.Value)
                        OTCLog.Msg(OTCLog.Systems.Manager, $"{npc.ID}: deposited ${cash:F0} into locker before nightly clear");
                }

                // --- Product / items ---
                var slots = __instance.ItemSlots;
                if (slots == null) return;

                for (int i = 0; i < slots.Count; i++)
                {
                    var slot = slots[i];
                    var item = slot?.ItemInstance;
                    if (item == null) continue;
                    if (item.TryCast<CashInstance>() != null) continue; // handled above

                    int placed = StorageFilterHelper.InsertItemFiltered(storage, item);
                    if (placed < item.Quantity && Config.ManagerVerboseLogging.Value)
                        OTCLog.Warning(OTCLog.Systems.Manager,
                            $"{npc.ID}: locker full, lost {item.Quantity - placed}x {item.ID} to nightly clear");
                }
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"ManagerOvernightDepositPatch failed: {ex.Message}");
            }
        }
    }
}
