using HarmonyLib;
using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Quests;
using Contract = Il2CppScheduleOne.Quests.Contract;
using Customer = Il2CppScheduleOne.Economy.Customer;
using Dealer = Il2CppScheduleOne.Economy.Dealer;
using EDrugType = Il2CppScheduleOne.Product.EDrugType;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
using Registry = Il2CppScheduleOne.Registry;
#else
using ScheduleOne.Economy;
using ScheduleOne.Product;
using ScheduleOne.Quests;
using Contract = ScheduleOne.Quests.Contract;
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
            return !DealInterceptHelper.TryRedirect(__instance, info, dealer);
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
        /// <param name="associatedDealer">
        /// When non-null (OfferContractToDealer path), any matching active dealer contract for this
        /// offer is voided so the dealer does not attend a meet the customer will not use.
        /// </param>
        internal static bool TryRedirect(Customer customer, ContractInfo info, Dealer associatedDealer = null)
        {
            if (info?.Products?.entries == null || info.Products.entries.Count == 0)
                return false;

            if (customer?.NPC == null)
                return false;

            // Desperation deals require in-person delivery — never redirect to store
            if (DesperationManager.IsDesperate(customer.NPC.ID))
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
            if (!DispensaryDealManager.Redirect(customer, drugType, target))
                return false;

            if (associatedDealer != null)
            {
                VoidDealerDealMatchingOffer(associatedDealer, customer, info);
                // If vanilla still queued a dealer contract after this prefix (ordering / IL2CPP edge cases),
                // void again next frame so attend-deal behaviour does not keep running for a hijacked meet.
                MelonCoroutines.Start(VoidDealerDealNextFrame(associatedDealer, customer, info));
            }

            return true;
        }

        private static IEnumerator VoidDealerDealNextFrame(Dealer dealer, Customer customer, ContractInfo info)
        {
            yield return null;
            if (dealer != null && customer != null)
                VoidDealerDealMatchingOffer(dealer, customer, info);
        }

        /// <summary>
        /// Fails dealer-side contracts that match the intercepted offer so attend-deal AI and journal
        /// state reflect that the meet is cancelled (customer is shopping at OTC instead).
        /// </summary>
        private static void VoidDealerDealMatchingOffer(Dealer dealer, Customer customer, ContractInfo info)
        {
            if (dealer?.ActiveContracts == null || customer?.NetworkObject == null)
                return;

            if (info?.Products?.entries == null || info.Products.entries.Count == 0)
                return;

            var customerNob = customer.NetworkObject;
            string offerProductId = info.Products.entries[0].ProductID;
            int offerQty = info.Products.entries[0].Quantity;
            float offerPayment = info.Payment;

            var active = dealer.ActiveContracts;
            if (active.Count == 0)
                return;

            var toFail = new List<Contract>();
            for (int i = 0; i < active.Count; i++)
            {
                var c = active[i];
                if (c == null || c.Customer != customerNob)
                    continue;
                if (!Mathf.Approximately(c.Payment, offerPayment))
                    continue;
                if (c.ProductList?.entries == null || c.ProductList.entries.Count == 0)
                    continue;
                var e = c.ProductList.entries[0];
                if (e.ProductID != offerProductId || e.Quantity != offerQty)
                    continue;
                toFail.Add(c);
            }

            for (int i = 0; i < toFail.Count; i++)
            {
                try
                {
                    toFail[i].Fail();
                }
                catch (System.Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"DealIntercept: failed to void dealer contract for {dealer.fullName}: {ex.Message}");
                }
            }
        }
    }
}
