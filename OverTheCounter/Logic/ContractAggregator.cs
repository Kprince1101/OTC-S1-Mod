using MelonLoader;
using OverTheCounter.Utilities;
using S1API.GameTime;
using System;
using System.Collections.Generic;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Quests;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Product;
using ScheduleOne.Quests;
#endif

namespace OverTheCounter.Logic
{
    public class ManifestRequirement
    {
        public string ProductID { get; set; }
        public string ProductName { get; set; }
        /// <summary>Total product units needed across all contracts.</summary>
        public int AmountNeeded { get; set; }
        /// <summary>Total product units the player already has (accounting for packaging multipliers).</summary>
        public int AmountInInventory { get; set; }
        public int Deficit => Math.Max(0, AmountNeeded - AmountInInventory);
    }

    /// <summary>
    /// A single contract's product requirement (not aggregated across contracts).
    /// Used by smart fill to ensure each contract gets properly packaged items.
    /// </summary>
    public class ContractProductNeed
    {
        public string ProductID { get; set; }
        public int Quantity { get; set; }
    }

    public static class ContractAggregator
    {
        /// <summary>
        /// Gets the product-unit multiplier for a packaged item.
        /// Jars = 5, Baggies = 1, etc. Returns 0 for unpackaged items so callers can skip them.
        /// </summary>
        public static int GetPackagingMultiplier(ScheduleOne.ItemFramework.ItemInstance item)
        {
            try
            {
#if IL2CPP
                var productItem = item.TryCast<ProductItemInstance>();
#else
                var productItem = item as ProductItemInstance;
#endif
                if (productItem?.AppliedPackaging != null && productItem.AppliedPackaging.Quantity > 0)
                    return productItem.AppliedPackaging.Quantity;
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Calculates the delivery manifest by aggregating all active contract requirements
        /// and subtracting what the player already has in their hotbar inventory.
        /// Quantities are in product units (e.g. 1 jar = 5 units, 1 baggie = 1 unit).
        /// </summary>
        public static List<ManifestRequirement> CalculateManifest(bool includeFuture)
        {
            var productTotals = new Dictionary<string, int>();

            try
            {
                var contracts = Contract.Contracts;
                if (contracts == null)
                    return new List<ManifestRequirement>();

                int currentTime = TimeManager.CurrentTime;

                for (int i = 0; i < contracts.Count; i++)
                {
                    var contract = contracts[i];
                    if (contract == null) continue;
                    try { if (contract.Dealer != null) continue; } catch { }

                    if (!includeFuture)
                    {
                        var deliveryWindow = contract.DeliveryWindow;
                        if (deliveryWindow != null)
                        {
                            // Always include desperation contracts
                            bool isImmediate = false;
                            try { isImmediate = deliveryWindow.TryCast<ImmediateQuestWindowConfig>() != null; }
                            catch { }

                            if (!isImmediate)
                            {
                                int start = deliveryWindow.WindowStartTime;
                                int end = deliveryWindow.WindowEndTime;

                                // Only include contracts whose window is currently active.
                                // The old WindowStartTime > currentTime check failed for
                                // overnight windows (0-600) where start=0 always passes.
                                if (currentTime < start || currentTime >= end)
                                    continue;
                            }
                        }
                    }

                    if (contract.ProductList?.entries == null) continue;

                    var entries = contract.ProductList.entries;
                    for (int j = 0; j < entries.Count; j++)
                    {
                        var entry = entries[j];
                        if (entry == null || string.IsNullOrEmpty(entry.ProductID)) continue;

                        if (productTotals.ContainsKey(entry.ProductID))
                            productTotals[entry.ProductID] += entry.Quantity;
                        else
                            productTotals[entry.ProductID] = entry.Quantity;
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Notification, $"Error aggregating contracts: {ex.Message}");
            }

            // Include accepted drifter deals (not in Contract.Contracts)
            try
            {
                var drifterDeals = DrifterManager.Instance?.GetAcceptedDealRequirements();
                if (drifterDeals != null)
                {
                    foreach (var (productId, quantity) in drifterDeals)
                    {
                        if (productTotals.ContainsKey(productId))
                            productTotals[productId] += quantity;
                        else
                            productTotals[productId] = quantity;
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Notification, $"Error reading drifter deals: {ex.Message}");
            }

            // Count what the player has in product units (jar=5, baggie=1)
            var inventoryCounts = new Dictionary<string, int>();
            try
            {
                var playerInv = PlayerSingleton<ScheduleOne.PlayerScripts.PlayerInventory>.Instance;
                if (playerInv != null)
                {
                    var slots = playerInv.hotbarSlots;
                    if (slots != null)
                    {
                        for (int i = 0; i < slots.Count; i++)
                        {
                            var slot = slots[i];
                            if (slot == null || slot.ItemInstance == null) continue;

                            string id = slot.ItemInstance.ID;
                            if (string.IsNullOrEmpty(id)) continue;

                            int multiplier = GetPackagingMultiplier(slot.ItemInstance);
                            if (multiplier <= 0) continue; // skip unpackaged product
                            int productUnits = slot.Quantity * multiplier;

                            if (inventoryCounts.ContainsKey(id))
                                inventoryCounts[id] += productUnits;
                            else
                                inventoryCounts[id] = productUnits;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Notification, $"Error reading player inventory: {ex.Message}");
            }

            var manifest = new List<ManifestRequirement>();
            foreach (var kvp in productTotals)
            {
                int inInventory = inventoryCounts.ContainsKey(kvp.Key) ? inventoryCounts[kvp.Key] : 0;
                var req = new ManifestRequirement
                {
                    ProductID = kvp.Key,
                    ProductName = FormatUtils.GetProductDisplayName(kvp.Key),
                    AmountNeeded = kvp.Value,
                    AmountInInventory = inInventory
                };

                if (req.Deficit > 0)
                    manifest.Add(req);
            }

            return manifest;
        }

        /// <summary>
        /// Returns per-contract product needs (not aggregated) with player inventory
        /// already allocated. Used by smart fill so each contract gets independently
        /// optimal packaging (e.g. 6 units = 1 jar + 1 baggie, not half of 2 jars).
        /// </summary>
        public static List<ContractProductNeed> CalculatePerContractNeeds(bool includeFuture)
        {
            var needs = new List<ContractProductNeed>();

            try
            {
                var contracts = Contract.Contracts;
                if (contracts == null)
                    return needs;

                int currentTime = TimeManager.CurrentTime;

                for (int i = 0; i < contracts.Count; i++)
                {
                    var contract = contracts[i];
                    if (contract == null) continue;
                    try { if (contract.Dealer != null) continue; } catch { }

                    if (!includeFuture)
                    {
                        var deliveryWindow = contract.DeliveryWindow;
                        if (deliveryWindow != null)
                        {
                            // Always include desperation contracts
                            bool isImmediate = false;
                            try { isImmediate = deliveryWindow.TryCast<ImmediateQuestWindowConfig>() != null; }
                            catch { }

                            if (!isImmediate)
                            {
                                int start = deliveryWindow.WindowStartTime;
                                int end = deliveryWindow.WindowEndTime;
                                // Same window-active check as CalculateManifest
                                if (currentTime < start || currentTime >= end)
                                    continue;
                            }
                        }
                    }

                    if (contract.ProductList?.entries == null) continue;

                    var entries = contract.ProductList.entries;
                    for (int j = 0; j < entries.Count; j++)
                    {
                        var entry = entries[j];
                        if (entry == null || string.IsNullOrEmpty(entry.ProductID)) continue;

                        needs.Add(new ContractProductNeed
                        {
                            ProductID = entry.ProductID,
                            Quantity = entry.Quantity
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Notification, $"Error reading contracts: {ex.Message}");
            }

            // Include accepted drifter deals (not in Contract.Contracts)
            try
            {
                var drifterDeals = DrifterManager.Instance?.GetAcceptedDealRequirements();
                if (drifterDeals != null)
                {
                    foreach (var (productId, quantity) in drifterDeals)
                    {
                        needs.Add(new ContractProductNeed
                        {
                            ProductID = productId,
                            Quantity = quantity
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Notification, $"Error reading drifter deals: {ex.Message}");
            }

            // Subtract player inventory (greedy allocation across contracts)
            var inventoryCounts = new Dictionary<string, int>();
            try
            {
                var playerInv = PlayerSingleton<ScheduleOne.PlayerScripts.PlayerInventory>.Instance;
                if (playerInv != null)
                {
                    var slots = playerInv.hotbarSlots;
                    if (slots != null)
                    {
                        for (int i = 0; i < slots.Count; i++)
                        {
                            var slot = slots[i];
                            if (slot == null || slot.ItemInstance == null) continue;

                            string id = slot.ItemInstance.ID;
                            if (string.IsNullOrEmpty(id)) continue;

                            int multiplier = GetPackagingMultiplier(slot.ItemInstance);
                            if (multiplier <= 0) continue; // skip unpackaged product
                            int productUnits = slot.Quantity * multiplier;

                            if (inventoryCounts.ContainsKey(id))
                                inventoryCounts[id] += productUnits;
                            else
                                inventoryCounts[id] = productUnits;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Notification, $"Error reading player inventory: {ex.Message}");
            }

            foreach (var need in needs)
            {
                if (inventoryCounts.TryGetValue(need.ProductID, out int available) && available > 0)
                {
                    int allocated = Math.Min(available, need.Quantity);
                    need.Quantity -= allocated;
                    inventoryCounts[need.ProductID] -= allocated;
                }
            }

            needs.RemoveAll(n => n.Quantity <= 0);
            return needs;
        }
    }
}
