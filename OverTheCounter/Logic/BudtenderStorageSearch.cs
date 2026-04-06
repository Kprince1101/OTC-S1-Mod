using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using System;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.ObjectScripts;
using StorageEntity = Il2CppScheduleOne.Storage.StorageEntity;
using PlaceableStorageEntity = Il2CppScheduleOne.ObjectScripts.PlaceableStorageEntity;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
using Grid = Il2CppScheduleOne.Tiles.Grid;
#else
using ScheduleOne.ItemFramework;
using ScheduleOne.Product;
using ScheduleOne.Storage;
using ScheduleOne.ObjectScripts;
using StorageEntity = ScheduleOne.Storage.StorageEntity;
using PlaceableStorageEntity = ScheduleOne.ObjectScripts.PlaceableStorageEntity;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
using Grid = ScheduleOne.Tiles.Grid;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Product sourcing for budtender NPCs and recommendation scanning.
    /// Three-tier fetch priority:
    /// 1. Counter's own storage
    /// 2. Non-closet building storage (display shelves + backroom)
    /// 3. Closets (last resort — bigger storage)
    ///
    /// Uses greedy packaging: largest packaging first, floor-divide to fill.
    /// </summary>
    internal static class BudtenderStorageSearch
    {
        // Storage IDs excluded from budtender sourcing (only lockers)
        private static readonly HashSet<string> ExcludedIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "locker"
        };

        // Storage IDs that are checkout counters (skip when searching building storage)
        private static readonly HashSet<string> CounterIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "otc_checkout_counter"
        };

        // Storage ID substrings for closets
        private static readonly string[] ClosetSubstrings = { "storagecloset" };

        /// <summary>
        /// Returns ALL storage entities accessible from this counter.
        /// Counter storage first, then non-closets, then closets.
        /// Used for product recommendation scanning.
        /// </summary>
        internal static List<(StorageEntity entity, Vector3 worldPos)> GetAllAccessibleStorages(
            CheckoutCounterInstance counter)
        {
            var result = new List<(StorageEntity, Vector3)>();
            if (counter == null) return result;

            // Counter's own storage first
            if (counter.CounterStorageEntity != null)
                result.Add((counter.CounterStorageEntity, counter.CounterPosition ?? Vector3.zero));

            // Non-closet building storage
            result.AddRange(GetBuildingStorages(counter, closetsOnly: false));

            // Closets last
            result.AddRange(GetBuildingStorages(counter, closetsOnly: true));

            return result;
        }

        /// <summary>
        /// Finds products from storage to fulfill a customer's order.
        /// Returns a list of FetchTasks describing where to walk and what to consume.
        /// </summary>
        internal static List<BudtenderInstance.FetchTask> FindProducts(
            CheckoutCounterInstance counter,
            List<CustomerInstance.SelectedProduct> requested)
        {
            var tasks = new List<BudtenderInstance.FetchTask>();
            if (counter == null || requested == null) return tasks;

            foreach (var selection in requested)
            {
                int unitsNeeded = selection.Quantity > 0 ? selection.Quantity : 1;
                int unitsFound = 0;

                // Priority 1: counter's own storage
                var counterStorage = counter.CounterStorageEntity;
                if (counterStorage != null)
                    unitsFound += CollectFromStorage(counterStorage, counter,
                        selection, unitsNeeded, tasks, isCounterStorage: true);

                if (unitsFound >= unitsNeeded) continue;

                // Priority 2: non-closet building storage (display shelves + backroom)
                var nonCloset = GetBuildingStorages(counter, closetsOnly: false);
                foreach (var (entity, worldPos) in nonCloset)
                {
                    if (unitsFound >= unitsNeeded) break;
                    unitsFound += CollectFromStorage(entity, counter,
                        selection, unitsNeeded - unitsFound, tasks, isCounterStorage: false,
                        overrideWorldPos: worldPos);
                }

                if (unitsFound >= unitsNeeded) continue;

                // Priority 3: closets (last resort — bigger storage)
                var closets = GetBuildingStorages(counter, closetsOnly: true);
                foreach (var (entity, worldPos) in closets)
                {
                    if (unitsFound >= unitsNeeded) break;
                    unitsFound += CollectFromStorage(entity, counter,
                        selection, unitsNeeded - unitsFound, tasks, isCounterStorage: false,
                        overrideWorldPos: worldPos);
                }
            }

            return tasks;
        }

        /// <summary>
        /// Collects product candidates from a single StorageEntity using greedy packaging.
        /// Returns the number of units found.
        /// </summary>
        private static int CollectFromStorage(
            StorageEntity storage,
            CheckoutCounterInstance counter,
            CustomerInstance.SelectedProduct selection,
            int unitsNeeded,
            List<BudtenderInstance.FetchTask> tasks,
            bool isCounterStorage,
            Vector3? overrideWorldPos = null)
        {
            if (storage?.ItemSlots == null || unitsNeeded <= 0) return 0;

            // Collect all candidates matching this ProductId
            var candidates = new List<(ItemSlot slot, string pkgId, int mult, int available, ProductDefinition prodDef)>();

            for (int j = 0; j < storage.ItemSlots.Count; j++)
            {
                var slot = storage.ItemSlots[j];
                if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

                ProductItemInstance productItem = null;
                ProductDefinition prodDef = null;
                try
                {
#if IL2CPP
                    productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
#else
                    productItem = slot.ItemInstance as ProductItemInstance;
#endif
                    if (productItem?.AppliedPackaging == null) continue;
#if IL2CPP
                    prodDef = productItem.Definition?.TryCast<ProductDefinition>();
#else
                    prodDef = productItem.Definition as ProductDefinition;
#endif
                }
                catch { continue; }

                if (prodDef?.ID != selection.ProductId) continue;

                candidates.Add((slot, productItem.AppliedPackaging.ID,
                    productItem.AppliedPackaging.Quantity, slot.Quantity, prodDef));
            }

            if (candidates.Count == 0) return 0;

            // Sort by packaging multiplier descending (greedy: largest first)
            candidates.Sort((a, b) => b.mult.CompareTo(a.mult));

            // Greedy fill with floor division
            int unitsFound = 0;
            Vector3 storageWorldPos = overrideWorldPos ?? storage.transform.position;

            foreach (var c in candidates)
            {
                if (unitsFound >= unitsNeeded) break;

                int pkgsNeeded = (unitsNeeded - unitsFound) / c.mult;
                int pkgsToTake = Math.Min(pkgsNeeded, c.available);
                if (pkgsToTake <= 0) continue;

                for (int u = 0; u < pkgsToTake; u++)
                {
                    tasks.Add(new BudtenderInstance.FetchTask
                    {
                        Storage = storage,
                        SourceSlot = c.slot,
                        WorldPosition = storageWorldPos,
                        ProductId = selection.ProductId,
                        PackagingId = c.pkgId,
                        ProductName = selection.ProductName,
                        Price = selection.Price * c.mult, // per-unit price × mult = per-package
                        QualityLevel = selection.QualityLevel,
                        UnitCount = c.mult,
                        ProductDef = c.prodDef
                    });
                    unitsFound += c.mult;
                }
            }

            return unitsFound;
        }

        /// <summary>
        /// Gets storage entities in the building that the counter belongs to.
        /// Excludes lockers and other checkout counters.
        /// If closetsOnly is true, returns only closets. If false, returns only non-closets.
        /// </summary>
        private static List<(StorageEntity entity, Vector3 worldPos)> GetBuildingStorages(
            CheckoutCounterInstance counter, bool closetsOnly)
        {
            var result = new List<(StorageEntity, Vector3)>();
            var grid = counter.ParentGrid;
            if (grid == null) return result;

            if (!BuildingGridFactory.GridContainers.TryGetValue(grid, out var root))
                return result;

            try
            {
                var storages = root.GetComponentsInChildren<PlaceableStorageEntity>(true);
                if (storages == null) return result;

                for (int i = 0; i < storages.Length; i++)
                {
                    var storage = storages[i];
                    if (storage == null || storage.transform == null) continue;

                    var id = storage.ItemInstance?.ID;
                    if (id == null) continue;

                    // Exclude lockers
                    if (ExcludedIds.Contains(id)) continue;

                    // Exclude other checkout counters
                    if (CounterIds.Contains(id)) continue;

                    // Determine if this is a closet
                    bool isCloset = false;
                    for (int c = 0; c < ClosetSubstrings.Length; c++)
                    {
                        if (id.IndexOf(ClosetSubstrings[c], StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            isCloset = true;
                            break;
                        }
                    }

                    // Filter: closetsOnly=true → only closets, closetsOnly=false → only non-closets
                    if (closetsOnly && !isCloset) continue;
                    if (!closetsOnly && isCloset) continue;

                    // Get the underlying StorageEntity
                    var storageEntity = storage.gameObject.GetComponentInChildren<StorageEntity>(true);
                    if (storageEntity?.ItemSlots == null) continue;

                    result.Add((storageEntity, storage.transform.position));
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"BudtenderStorageSearch: error scanning building storage: {ex.Message}");
            }

            return result;
        }
    }
}
