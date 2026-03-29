using HarmonyLib;
using OverTheCounter.Logic;
using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Quests;
using Customer = Il2CppScheduleOne.Economy.Customer;
using Dealer = Il2CppScheduleOne.Economy.Dealer;
using EDrugType = Il2CppScheduleOne.Product.EDrugType;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
using Registry = Il2CppScheduleOne.Registry;
#else
using ScheduleOne.Economy;
using ScheduleOne.Product;
using ScheduleOne.Quests;
using Customer = ScheduleOne.Economy.Customer;
using Dealer = ScheduleOne.Economy.Dealer;
using EDrugType = ScheduleOne.Product.EDrugType;
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
using Registry = ScheduleOne.Registry;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Intercepts vanilla deal offers (OfferContract + OfferContractToDealer) to redirect
    /// unlocked NPCs to OTC buildings when the dispensary stocks a matching drug type.
    /// Runs on host only (vanilla OnMinPass is server-gated).
    /// </summary>
    [HarmonyPatch(typeof(Customer), "OfferContract")]
    public static class OfferContractPatch
    {
        public static bool Prefix(Customer __instance, ContractInfo info)
        {
            return !DealInterceptHelper.TryRedirect(__instance, info);
        }
    }

    [HarmonyPatch(typeof(Customer), "OfferContractToDealer")]
    public static class OfferContractToDealerPatch
    {
        public static bool Prefix(Customer __instance, ContractInfo info, Dealer dealer)
        {
            return !DealInterceptHelper.TryRedirect(__instance, info);
        }
    }

    /// <summary>
    /// Shared redirect logic for both OfferContract and OfferContractToDealer patches.
    /// </summary>
    internal static class DealInterceptHelper
    {
        /// <summary>
        /// Attempts to redirect the NPC to an OTC building instead of offering a deal.
        /// Returns true if redirected (caller should skip original), false if vanilla should proceed.
        /// </summary>
        internal static bool TryRedirect(Customer customer, ContractInfo info)
        {
            if (info?.Products?.entries == null || info.Products.entries.Count == 0)
                return false;

            if (customer?.NPC == null)
                return false;

            // Already redirected this NPC — don't create duplicate CustomerInstances
            if (DispensaryDealManager.IsNpcRedirected(customer.NPC.ID))
                return false;

            // Only redirect unlocked NPCs
            if (!customer.NPC.RelationData.Unlocked)
                return false;

            // Extract drug type from the contract's product
            EDrugType drugType;
            try
            {
                string productId = info.Products.entries[0].ProductID;
                if (string.IsNullOrEmpty(productId))
                    return false;

                var prodDef = Registry.GetItem<ProductDefinition>(productId);
                if (prodDef == null)
                    return false;

                drugType = prodDef.DrugType;
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"DealIntercept: failed to extract drug type: {ex.Message}");
                return false;
            }

            // Check that the player has listed a product of this drug type in the Products app
            bool hasListedProduct = false;
            var listedProducts = ProductManager.ListedProducts;
            if (listedProducts != null)
            {
                for (int i = 0; i < listedProducts.Count; i++)
                {
                    if (listedProducts[i] != null && listedProducts[i].DrugType == drugType)
                    {
                        hasListedProduct = true;
                        break;
                    }
                }
            }
            if (!hasListedProduct)
                return false;

            // Check operating hours (including 30-min cutoff buffer)
            if (!DispensaryDealManager.IsWithinOperatingHours())
            {
                // Pre-open window (e.g. 7am): defer to opening time for a morning rush
                if (DispensaryDealManager.IsInPreOpenWindow())
                    return DispensaryDealManager.DeferDeal(customer, drugType);

                return false;
            }

            // Find a building that stocks the matching drug type (physical shelf stock)
            var target = DispensaryDealManager.FindAvailableBuilding(drugType, customer.NPC.Region);
            if (target == null)
                return false;

            // Redirect the NPC to the building
            return DispensaryDealManager.Redirect(customer, drugType, target);
        }
    }
}
