using HarmonyLib;
using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using System;
using System.Reflection;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.UI.Handover;
#else
using ScheduleOne.Economy;
using ScheduleOne.ItemFramework;
using ScheduleOne.Quests;
using ScheduleOne.UI.Handover;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Harmony patch to inject the Desperation Premium bonus into the ProcessHandover method.
    /// </summary>
    [HarmonyPatch(typeof(Customer), "ProcessHandover")]
    public static class ProcessHandoverPatch
    {
        private static readonly System.Threading.ThreadLocal<PendingBonus> _pendingBonus = new();

        /// <summary>
        /// Prefix: Check for desperation state and store bonus info for postfix.
        /// </summary>
        public static void Prefix(
            Customer __instance,
            HandoverScreen.EHandoverOutcome outcome,
            Contract contract,
            GameSystem.Collections.Generic.List<ItemInstance> items,
            bool handoverByPlayer,
            bool giveBonuses)
        {
            _pendingBonus.Value = null;

            if (!giveBonuses || outcome != HandoverScreen.EHandoverOutcome.Finalize)
                return;

            if (__instance == null || __instance.NPC == null || contract == null)
                return;

            try
            {
                string customerId = __instance.NPC.ID;

                if (DesperationManager.IsDesperate(customerId))
                {
                    float bonusAmount = contract.Payment * DesperationManager.GetBonusMultiplier();

                    _pendingBonus.Value = new PendingBonus
                    {
                        CustomerId = customerId,
                        BonusAmount = bonusAmount
                    };
                }
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[ProcessHandoverPatch] Prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix: Resolve the desperation event after a successful handover.
        /// This is the authoritative resolution path - the popup patch is only for UI display.
        /// </summary>
        public static void Postfix(
            Customer __instance,
            HandoverScreen.EHandoverOutcome outcome)
        {
            if (outcome != HandoverScreen.EHandoverOutcome.Finalize)
                return;

            if (__instance?.NPC == null)
                return;

            try
            {
                string customerId = __instance.NPC.ID;
                if (DesperationManager.IsDesperate(customerId))
                {
                    if (NetworkHelper.IsHost)
                        DesperationManager.ResolveEvent(customerId);
                    else
                        ConfigSyncData.SendQuestAction($"DESP_RESOLVE:{customerId}");
                }
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[ProcessHandoverPatch] Postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets pending bonus info for the DealCompletionPopup patch.
        /// </summary>
        public static PendingBonus GetPendingBonus()
        {
            return _pendingBonus.Value;
        }

        public class PendingBonus
        {
            public string CustomerId { get; set; }
            public float BonusAmount { get; set; }
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
            var type = AccessTools.TypeByName("ScheduleOne.UI.DealCompletionPopup");
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
            ref GameSystem.Collections.Generic.List<Contract.BonusPayment> bonuses)
        {
            var pending = ProcessHandoverPatch.GetPendingBonus();
            if (pending == null)
                return;

            if (customer == null || customer.NPC == null)
                return;

            if (customer.NPC.ID != pending.CustomerId)
                return;

            try
            {
                var desperationBonus = new Contract.BonusPayment("Desperation Premium", pending.BonusAmount);

                if (bonuses == null)
                {
                    bonuses = new GameSystem.Collections.Generic.List<Contract.BonusPayment>();
                }

                bonuses.Add(desperationBonus);
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[DealCompletionPopupPatch] Error: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    /// <summary>
    /// Patch Contract.SubmitPayment to add desperation bonus to actual payment.
    /// </summary>
    [HarmonyPatch(typeof(Contract), "SubmitPayment")]
    public static class ContractSubmitPaymentPatch
    {
        public static void Prefix(Contract __instance, ref float bonusTotal)
        {
            var pending = ProcessHandoverPatch.GetPendingBonus();
            if (pending == null)
                return;

            try
            {
                bonusTotal += pending.BonusAmount;
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[ContractSubmitPaymentPatch] Error: {ex.Message}");
            }
        }
    }
}
