using System;

#if IL2CPP
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Storage;
#else
using ScheduleOne.ItemFramework;
using ScheduleOne.Storage;
#endif

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Filter-aware storage operations that respect both hard filters
    /// (e.g. ItemFilter_Category, ItemFilter_PackagedProduct) and player-configured
    /// slot filters (whitelist/blacklist/quality).
    ///
    /// The game's StorageEntity.HowManyCanFit() and InsertItem() do NOT check
    /// filters — they only check lock status and stacking compatibility.
    /// These helpers mirror the game's ITransitEntity.InsertItemIntoInput pattern
    /// which uses slot.GetCapacityForItem(item, checkPlayerFilters: true).
    /// </summary>
    internal static class StorageFilterHelper
    {
        /// <summary>
        /// Filter-aware replacement for StorageEntity.HowManyCanFit().
        /// Sums per-slot capacity that passes both hard and player filters.
        /// </summary>
        internal static int HowManyCanFitFiltered(StorageEntity storage, ItemInstance item)
        {
            if (storage?.ItemSlots == null || item == null) return 0;
            int total = 0;
            for (int i = 0; i < storage.ItemSlots.Count; i++)
            {
                try
                {
                    var slot = storage.ItemSlots[i];
                    if (slot == null || slot.IsLocked || slot.IsAddLocked) continue;
                    total += slot.GetCapacityForItem(item, true);
                }
                catch { }
            }
            return total;
        }

        /// <summary>
        /// Filter-aware replacement for StorageEntity.InsertItem().
        /// Returns the quantity actually inserted; remainder is NOT placed.
        /// Pattern: ITransitEntity.InsertItemIntoInput (Mono_Reference).
        /// </summary>
        internal static int InsertItemFiltered(StorageEntity storage, ItemInstance item)
        {
            if (storage?.ItemSlots == null || item == null) return 0;
            int remaining = item.Quantity;

            for (int i = 0; i < storage.ItemSlots.Count; i++)
            {
                if (remaining <= 0) break;
                try
                {
                    var slot = storage.ItemSlots[i];
                    if (slot == null || slot.IsLocked || slot.IsAddLocked) continue;

                    int cap = slot.GetCapacityForItem(item, true);
                    if (cap <= 0) continue;

                    int toPlace = Math.Min(cap, remaining);
                    slot.InsertItem(item.GetCopy(toPlace));
                    remaining -= toPlace;
                }
                catch { }
            }
            return item.Quantity - remaining;
        }

        /// <summary>
        /// Counts empty unlocked slots that accept a specific item (filter-aware).
        /// Used by BuildShoppingList and ComputeStorageReservations for per-item
        /// free slot counting.
        /// </summary>
        internal static int CountFilteredFreeSlots(StorageEntity storage, ItemInstance testItem)
        {
            if (storage?.ItemSlots == null || testItem == null) return 0;
            int count = 0;
            for (int i = 0; i < storage.ItemSlots.Count; i++)
            {
                try
                {
                    var slot = storage.ItemSlots[i];
                    if (slot == null || slot.IsLocked || slot.IsAddLocked) continue;
                    if (slot.ItemInstance != null) continue;
                    if (slot.GetCapacityForItem(testItem, true) > 0)
                        count++;
                }
                catch { }
            }
            return count;
        }
    }
}
