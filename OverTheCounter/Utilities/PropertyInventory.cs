using OverTheCounter.Logic.Placement;
using System.Collections.Generic;

#if IL2CPP
using Il2CppScheduleOne.Storage;
using Grid = Il2CppScheduleOne.Tiles.Grid;
#else
using ScheduleOne.Storage;
using Grid = ScheduleOne.Tiles.Grid;
#endif

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Shared inventory utility for OTC buildings. Enumerates display storage
    /// (shelves, cabinets, tables, etc.) while excluding checkout counter storage.
    /// Used by both StorefrontGrowthQuest and the GreenTab POS app.
    /// </summary>
    public static class PropertyInventory
    {
        /// <summary>
        /// Returns all StorageEntity instances on a building grid,
        /// excluding checkout counter storage.
        /// </summary>
        public static List<StorageEntity> GetDisplayStorages(Grid grid)
        {
            var result = new List<StorageEntity>();
            if (grid == null) return result;
            if (!BuildingGridFactory.GridContainers.TryGetValue(grid, out var root))
                return result;

            var allStorages = root.GetComponentsInChildren<StorageEntity>(true);
            if (allStorages == null) return result;

            // Build exclusion set from all checkout counters
            var counterStorages = new HashSet<StorageEntity>();
            foreach (var counter in CheckoutCounter.AllCounters)
                if (counter.CounterStorageEntity != null)
                    counterStorages.Add(counter.CounterStorageEntity);

            foreach (var s in allStorages)
                if (s != null && !counterStorages.Contains(s))
                    result.Add(s);

            return result;
        }

        /// <summary>
        /// Returns true if any non-counter storage on this grid contains at least one item.
        /// </summary>
        public static bool HasAnyProduct(Grid grid)
        {
            var storages = GetDisplayStorages(grid);
            foreach (var storage in storages)
            {
                if (storage.ItemSlots == null) continue;
                for (int i = 0; i < storage.ItemSlots.Count; i++)
                {
                    var slot = storage.ItemSlots[i];
                    if (slot?.ItemInstance != null)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns the total item count across all non-counter storage on this grid.
        /// </summary>
        public static int GetTotalProductCount(Grid grid)
        {
            int total = 0;
            var storages = GetDisplayStorages(grid);
            foreach (var storage in storages)
            {
                if (storage.ItemSlots == null) continue;
                for (int i = 0; i < storage.ItemSlots.Count; i++)
                {
                    var slot = storage.ItemSlots[i];
                    if (slot?.ItemInstance != null)
                        total += slot.Quantity;
                }
            }
            return total;
        }

        /// <summary>
        /// Returns true if any non-counter StorageEntity exists on this grid.
        /// (Checks for placed storage furniture, regardless of whether it has items.)
        /// </summary>
        public static bool HasAnyDisplayStorage(Grid grid)
        {
            return GetDisplayStorages(grid).Count > 0;
        }
    }
}
