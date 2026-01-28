using Il2CppScheduleOne.Quests;
using S1API.Items;
using S1API.Products;
using System.Collections.Generic;

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Static helper methods for string formatting and data aggregation.
    /// </summary>
    public static class FormatUtils
    {
        /// <summary>
        /// Represents a product entry with its display name and quantity.
        /// </summary>
        public class ProductSummary
        {
            public string ProductID { get; set; }
            public string DisplayName { get; set; }
            public int Quantity { get; set; }
        }

        /// <summary>
        /// Aggregates all contracts into a list of product summaries.
        /// Groups by Product ID only (ignores quality for simpler display).
        /// </summary>
        public static List<ProductSummary> GetProductSummaries(Il2CppSystem.Collections.Generic.List<Contract> contracts)
        {
            var contractList = new List<Contract>();
            for (int i = 0; i < contracts.Count; i++)
            {
                contractList.Add(contracts[i]);
            }
            return GetProductSummaries(contractList);
        }

        /// <summary>
        /// Aggregates all contracts into a list of product summaries (System.Collections version).
        /// </summary>
        public static List<ProductSummary> GetProductSummaries(List<Contract> contracts)
        {
            // Aggregate by ProductID only
            var productTotals = new Dictionary<string, int>();

            foreach (var contract in contracts)
            {
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

            // Convert to list of ProductSummary
            var summaries = new List<ProductSummary>();
            foreach (var kvp in productTotals)
            {
                summaries.Add(new ProductSummary
                {
                    ProductID = kvp.Key,
                    DisplayName = GetProductDisplayName(kvp.Key),
                    Quantity = kvp.Value
                });
            }

            return summaries;
        }

        /// <summary>
        /// Aggregates all contracts into a single formatted string.
        /// Groups by Product ID only (ignores quality for simpler display).
        /// </summary>
        public static string BuildProductBreakdownString(Il2CppSystem.Collections.Generic.List<Contract> contracts)
        {
            var contractList = new List<Contract>();
            for (int i = 0; i < contracts.Count; i++)
            {
                contractList.Add(contracts[i]);
            }
            return BuildProductBreakdownString(contractList);
        }

        /// <summary>
        /// Aggregates all contracts into a single formatted string (System.Collections version).
        /// </summary>
        public static string BuildProductBreakdownString(List<Contract> contracts)
        {
            var summaries = GetProductSummaries(contracts);
            var productStrings = new List<string>();

            foreach (var summary in summaries)
            {
                productStrings.Add($"{summary.Quantity}x {summary.DisplayName}");
            }

            return string.Join(", ", productStrings);
        }

        /// <summary>
        /// Gets the display name for a product using S1API's ItemManager.
        /// </summary>
        public static string GetProductDisplayName(string productID)
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
