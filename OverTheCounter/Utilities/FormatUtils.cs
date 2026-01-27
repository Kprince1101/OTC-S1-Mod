using Il2CppScheduleOne.Quests;
using S1API.Items;
using S1API.Products;
using System.Collections.Generic;
using System.Text;

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Static helper methods for string formatting and data aggregation.
    /// </summary>
    public static class FormatUtils
    {
        /// <summary>
        /// Aggregates all contracts into a single formatted string.
        /// Groups by Product ID only (ignores quality for simpler display).
        /// </summary>
        public static string BuildProductBreakdownString(Il2CppSystem.Collections.Generic.List<Contract> contracts)
        {
            // Aggregate by ProductID only
            var productTotals = new Dictionary<string, int>();

            // Use index-based loops for Il2Cpp collections to avoid enumeration issues
            for (int i = 0; i < contracts.Count; i++)
            {
                var contract = contracts[i];
                if (contract?.ProductList?.entries == null) continue;

                var entries = contract.ProductList.entries;
                for (int j = 0; j < entries.Count; j++)
                {
                    var entry = entries[j];
                    if (entry == null || string.IsNullOrEmpty(entry.ProductID)) continue;

                    if (productTotals.ContainsKey(entry.ProductID))
                    {
                        productTotals[entry.ProductID] += entry.Quantity;
                    }
                    else
                    {
                        productTotals[entry.ProductID] = entry.Quantity;
                    }
                }
            }

            // Convert aggregated data into display string
            var productStrings = new List<string>();
            foreach (var kvp in productTotals)
            {
                string productName = GetProductDisplayName(kvp.Key);
                productStrings.Add($"{kvp.Value}x {productName}");
            }

            return string.Join(", ", productStrings);
        }

        /// <summary>
        /// Gets the display name for a product using S1API's ItemManager.
        /// </summary>
        private static string GetProductDisplayName(string productID)
        {
            if (string.IsNullOrEmpty(productID)) return "Unknown";

            try
            {
                // Use S1API to get the product definition and its display name
                var def = ItemManager.GetItemDefinition(productID) as ProductDefinition;
                if (def != null && !string.IsNullOrEmpty(def.Name))
                {
                    return def.Name;
                }
            }
            catch
            {
                // Fallback to manual formatting if lookup fails
            }

            // Fallback: Just capitalize the first letter
            return char.ToUpper(productID[0]) + productID.Substring(1);
        }
    }
}
