using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using System;
using System.Collections.Generic;

#if IL2CPP
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Storage;
using Grid = Il2CppScheduleOne.Tiles.Grid;
using EDrugType = Il2CppScheduleOne.Product.EDrugType;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
#else
using ScheduleOne.Product;
using ScheduleOne.Storage;
using Grid = ScheduleOne.Tiles.Grid;
using EDrugType = ScheduleOne.Product.EDrugType;
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
#endif

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Shared inventory utility for OTC buildings. Enumerates storage entities
    /// with optional filtering of private storage (checkout counters, lockers).
    /// Used by both StorefrontGrowthQuest and the GreenTab POS app.
    /// </summary>
    public static class PropertyInventory
    {
        /// <summary>
        /// Returns StorageEntity instances on a building grid.
        /// When includePrivate is false (default), excludes checkout counter storage
        /// so only browsable/display storage is returned (shelves, cabinets, tables).
        /// When true, returns ALL storage including counters and lockers.
        /// </summary>
        public static List<StorageEntity> GetStorages(Grid grid, bool includePrivate = false)
        {
            var result = new List<StorageEntity>();
            if (grid == null) return result;
            if (!BuildingGridFactory.GridContainers.TryGetValue(grid, out var root))
                return result;

            var allStorages = root.GetComponentsInChildren<StorageEntity>(true);
            if (allStorages == null) return result;

            if (includePrivate)
            {
                foreach (var s in allStorages)
                    if (s != null) result.Add(s);
                return result;
            }

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
        /// Returns all display (non-private) StorageEntity instances on a building grid.
        /// Convenience wrapper matching the old GetDisplayStorages signature.
        /// </summary>
        public static List<StorageEntity> GetDisplayStorages(Grid grid) => GetStorages(grid, includePrivate: false);

        /// <summary>
        /// Returns true if any browsable storage on this grid contains at least one item.
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
        /// Returns the total item count across storage on this grid.
        /// When includePrivate is true, includes checkout counter and locker storage.
        /// </summary>
        public static int GetTotalProductCount(Grid grid, bool includePrivate = false)
        {
            int total = 0;
            var storages = GetStorages(grid, includePrivate);
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

        /// <summary>
        /// Returns true if at least one checkout counter is placed on this grid.
        /// </summary>
        public static bool HasCheckoutCounter(Grid grid)
        {
            if (grid == null) return false;
            foreach (var counter in CheckoutCounter.AllCounters)
                if (counter.ParentGrid == grid)
                    return true;
            return false;
        }

        /// <summary>
        /// Scans all OTC building grids for unique packaged product IDs currently in stock.
        /// Filters out products disabled via PricingSaveData.
        /// Used as a replacement for ProductManager.ListedProducts.
        /// </summary>
        public static List<(string productId, string productName)> GetAvailableProductIds()
        {
            var seen = new HashSet<string>();
            var result = new List<(string, string)>();

            foreach (var kvp in BuildingGridFactory.GridRegistry)
            {
                try
                {
                    var storages = GetStorages(kvp.Key, includePrivate: true);
                    foreach (var storage in storages)
                    {
                        if (storage?.ItemSlots == null) continue;
                        for (int i = 0; i < storage.ItemSlots.Count; i++)
                        {
                            var slot = storage.ItemSlots[i];
                            if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

#if IL2CPP
                            var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
                            var prodDef = productItem?.Definition?.TryCast<ProductDefinition>();
#else
                            var productItem = slot.ItemInstance as ProductItemInstance;
                            var prodDef = productItem?.Definition as ProductDefinition;
#endif
                            if (prodDef == null || productItem.AppliedPackaging == null) continue;
                            if (string.IsNullOrEmpty(prodDef.ID)) continue;
                            if (PricingSaveData.Instance != null &&
                                PricingSaveData.Instance.IsSellingDisabled(prodDef.ID))
                                continue;
                            if (seen.Add(prodDef.ID))
                                result.Add((prodDef.ID, prodDef.Name ?? prodDef.name ?? "Product"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.General,
                        $"GetAvailableProductIds: error scanning grid: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Returns true if any display storage on this grid contains a packaged product
        /// matching the given drug type.
        /// </summary>
        public static bool HasDrugType(Grid grid, EDrugType drugType)
        {
            var storages = GetDisplayStorages(grid);
            foreach (var storage in storages)
            {
                if (storage?.ItemSlots == null) continue;
                for (int i = 0; i < storage.ItemSlots.Count; i++)
                {
                    var slot = storage.ItemSlots[i];
                    if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

#if IL2CPP
                    var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
                    var prodDef = productItem?.Definition?.TryCast<ProductDefinition>();
#else
                    var productItem = slot.ItemInstance as ProductItemInstance;
                    var prodDef = productItem?.Definition as ProductDefinition;
#endif
                    if (prodDef != null && prodDef.DrugType == drugType
                        && productItem.AppliedPackaging != null)
                        return true;
                }
            }
            return false;
        }

    }
}
