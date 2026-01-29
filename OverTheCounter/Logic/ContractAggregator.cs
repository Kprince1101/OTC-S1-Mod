using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Quests;
using MelonLoader;
using OverTheCounter.Utilities;
using S1API.GameTime;
using System;
using System.Collections.Generic;

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

    public static class ContractAggregator
    {
        /// <summary>
        /// Gets the product-unit multiplier for a packaged item.
        /// Jars = 5, Baggies = 1, etc. Falls back to 1 if not a ProductItemInstance.
        /// </summary>
        public static int GetPackagingMultiplier(Il2CppScheduleOne.ItemFramework.ItemInstance item)
        {
            try
            {
                var productItem = item.TryCast<ProductItemInstance>();
                if (productItem != null)
                {
                    var packaging = productItem.AppliedPackaging;
                    if (packaging != null && packaging.Quantity > 0)
                        return packaging.Quantity;
                }
            }
            catch { }
            return 1;
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

                    if (!includeFuture)
                    {
                        var deliveryWindow = contract.DeliveryWindow;
                        if (deliveryWindow != null && deliveryWindow.WindowStartTime > currentTime)
                            continue;
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
                Melon<Core>.Logger.Error($"[ContractAggregator] Error aggregating contracts: {ex.Message}");
            }

            // Count what the player has in product units (jar=5, baggie=1)
            var inventoryCounts = new Dictionary<string, int>();
            try
            {
                var playerInv = PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerInventory>.Instance;
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
                Melon<Core>.Logger.Error($"[ContractAggregator] Error reading player inventory: {ex.Message}");
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
    }
}
