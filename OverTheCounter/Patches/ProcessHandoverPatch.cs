using HarmonyLib;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.UI.Handover;
using MelonLoader;
using OverTheCounter.Logic;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Harmony patch to inject the Desperation Premium bonus into the ProcessHandover method.
    /// </summary>
    [HarmonyPatch(typeof(Customer), "ProcessHandover")]
    public static class ProcessHandoverPatch
    {
        // Thread-local storage to track desperation state between prefix and postfix
        private static readonly System.Threading.ThreadLocal<PendingBonus> _pendingBonus = new();

        /// <summary>
        /// Prefix: Check for desperation state and store bonus info for postfix.
        /// </summary>
        public static void Prefix(
            Customer __instance,
            HandoverScreen.EHandoverOutcome outcome,
            Contract contract,
            Il2CppSystem.Collections.Generic.List<ItemInstance> items,
            bool handoverByPlayer,
            bool giveBonuses)
        {
            // Clear any previous pending bonus
            _pendingBonus.Value = null;

            // Only process if this is a finalize with bonuses enabled
            if (!giveBonuses || outcome != HandoverScreen.EHandoverOutcome.Finalize)
                return;

            if (__instance == null || __instance.NPC == null || contract == null)
                return;

            try
            {
                string customerId = __instance.NPC.ID;

                // Check if this customer is in a desperation state
                if (DesperationManager.IsDesperate(customerId))
                {
                    float bonusAmount = contract.Payment * DesperationManager.GetBonusMultiplier();

                    _pendingBonus.Value = new PendingBonus
                    {
                        CustomerId = customerId,
                        CustomerName = __instance.NPC.fullName,
                        BonusAmount = bonusAmount,
                        BasePayment = contract.Payment
                    };
                }
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[ProcessHandoverPatch] Prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets pending bonus info for the DealCompletionPopup patch.
        /// </summary>
        public static PendingBonus GetPendingBonus()
        {
            return _pendingBonus.Value;
        }

        /// <summary>
        /// Clears pending bonus after it's been applied.
        /// </summary>
        public static void ClearPendingBonus()
        {
            _pendingBonus.Value = null;
        }

        public class PendingBonus
        {
            public string CustomerId { get; set; }
            public string CustomerName { get; set; }
            public float BonusAmount { get; set; }
            public float BasePayment { get; set; }
        }
    }

    /// <summary>
    /// Patch the DealCompletionPopup.PlayPopup to inject our bonus into the display.
    /// This ensures the player sees the Desperation Premium on the receipt.
    /// </summary>
    [HarmonyPatch]
    public static class DealCompletionPopupPatch
    {
        /// <summary>
        /// Target the PlayPopup method dynamically.
        /// </summary>
        public static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Il2CppScheduleOne.UI.DealCompletionPopup");
            if (type == null)
            {
                Melon<Core>.Logger.Warning("[DealCompletionPopupPatch] Could not find DealCompletionPopup type.");
                return null;
            }

            var method = AccessTools.Method(type, "PlayPopup");
            if (method == null)
            {
                Melon<Core>.Logger.Warning("[DealCompletionPopupPatch] Could not find PlayPopup method.");
            }
            return method;
        }

        /// <summary>
        /// Prefix: Inject our desperation bonus into the bonus list before popup displays.
        /// </summary>
        public static void Prefix(
            Customer customer,
            float satisfaction,
            float originalRelationshipDelta,
            float basePayment,
            ref Il2CppSystem.Collections.Generic.List<Contract.BonusPayment> bonuses)
        {
            // Check if we have a pending desperation bonus from ProcessHandover
            var pending = ProcessHandoverPatch.GetPendingBonus();
            if (pending == null)
                return;

            // Verify this is the same customer
            if (customer == null || customer.NPC == null)
                return;

            if (customer.NPC.ID != pending.CustomerId)
                return;

            try
            {
                // Create and add our bonus
                var desperationBonus = new Contract.BonusPayment("Desperation Premium", pending.BonusAmount);

                if (bonuses == null)
                {
                    bonuses = new Il2CppSystem.Collections.Generic.List<Contract.BonusPayment>();
                }

                bonuses.Add(desperationBonus);

                // Resolve the desperation event (successful delivery)
                DesperationManager.ResolveEvent(pending.CustomerId);

                // Clear the pending bonus
                ProcessHandoverPatch.ClearPendingBonus();

            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[DealCompletionPopupPatch] Error: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    // TODO: This is a backup patch to ensure the bonus is applied to the actual payment. Remove if unnecessary.
    /// <summary>
    /// Patch Contract.SubmitPayment to add desperation bonus to actual payment.
    /// This is a backup to ensure the money is actually added.
    /// </summary>
    [HarmonyPatch(typeof(Contract), "SubmitPayment")]
    public static class ContractSubmitPaymentPatch
    {
        public static void Prefix(Contract __instance, ref float bonusTotal)
        {
            // Check if we have a pending desperation bonus
            var pending = ProcessHandoverPatch.GetPendingBonus();
            if (pending == null)
                return;

            try
            {
                // Add the desperation bonus to the total
                bonusTotal += pending.BonusAmount;

            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[ContractSubmitPaymentPatch] Error: {ex.Message}");
            }
        }
    }
}
