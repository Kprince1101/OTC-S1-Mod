using HarmonyLib;
using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.SaveData;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using S1API.GameTime;
using System;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Quests;
#else
using ScheduleOne.Economy;
using ScheduleOne.Quests;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Patches the contract notification system to customize desperation deals.
    /// - Removes Counter-offer option for desperation deals
    /// - Replaces time window selector with location picker
    /// - Preserves immediate delivery window for desperation deals
    /// </summary>
    [HarmonyPatch(typeof(Customer), "NotifyPlayerOfContract")]
    public static class NotifyPlayerOfContractPatch
    {
        /// <summary>
        /// Prefix: Modify canCounterOffer to false for desperation deals.
        /// </summary>
        public static void Prefix(
            Customer __instance,
            ref bool canCounterOffer)
        {
            if (__instance == null || __instance.NPC == null)
                return;

            string customerId = __instance.NPC.ID;

            // Check if this is a desperation deal
            if (DesperationManager.IsDesperate(customerId))
            {
                // Disable counter-offer for desperation deals
                canCounterOffer = false;
            }
        }
    }

    /// <summary>
    /// Patches AcceptContractClicked to show location picker instead of time window for desperation deals.
    /// </summary>
    [HarmonyPatch(typeof(Customer), "AcceptContractClicked")]
    public static class AcceptContractClickedPatch
    {
        /// <summary>
        /// Prefix: For desperation deals, show location picker and skip the original method.
        /// </summary>
        public static bool Prefix(Customer __instance)
        {
            if (__instance == null || __instance.NPC == null)
                return true; // Continue to original method

            string customerId = __instance.NPC.ID;

            // Check if this is a desperation deal
            if (DesperationManager.IsDesperate(customerId))
            {
                LocationPickerUI.Show(__instance, (locationGuid) =>
                {
                    if (NetworkHelper.IsHost)
                    {
                        FinalizeDesperationDeal(__instance, locationGuid);
                    }
                    else
                    {
                        // Client can't create contracts — forward to host
                        ConfigSyncData.SendQuestAction($"DESP_ACCEPT:{customerId}:{locationGuid}");
                        __instance.NPC.GetMSGConversation()?.ClearResponses(true);
                    }
                });

                return false;
            }

            return true; // Continue to original method for normal deals
        }

        /// <summary>
        /// Finds a customer by NPC ID and finalizes. Called by host when client accepts.
        /// </summary>
        internal static void FinalizeDesperationDealRemote(string customerId, string locationGuid)
        {
            var unlocked = Customer.UnlockedCustomers;
            if (unlocked == null) return;

            for (int i = 0; i < unlocked.Count; i++)
            {
                var c = unlocked[i];
                if (c?.NPC != null && c.NPC.ID == customerId)
                {
                    FinalizeDesperationDeal(c, locationGuid);
                    return;
                }
            }

            Melon<Core>.Logger.Warning($"[AcceptContractClickedPatch] Remote accept: customer {customerId} not found");
        }

        private static void FinalizeDesperationDeal(Customer customer, string locationGuid)
        {
            try
            {
                if (customer.GetOfferedContractInfo() == null)
                {
                    Melon<Core>.Logger.Error("[AcceptContractClickedPatch] No offered contract to finalize");
                    return;
                }

                customer.GetOfferedContractInfo().DeliveryLocationGUID = locationGuid;

                // ContractAccepted overwrites window times with Morning, but
                // QuestManagerContractAcceptedPatch restores our deadline before
                // the contract is created and synced to clients.
                customer.ContractAccepted(EDealWindow.Morning, true, dealer: null);

                if (customer.NPC.GetMSGConversation() != null)
                    customer.NPC.GetMSGConversation().ClearResponses(true);

                DesperationManager.OnContractAccepted(customer.NPC.ID);
                SendConfirmationText(customer);
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[AcceptContractClickedPatch] FinalizeDesperationDeal failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Sends a confirmation text from the customer after accepting the deal.
        /// </summary>
        private static void SendConfirmationText(Customer customer)
        {
            try
            {
                string[] messages = new[]
                {
                    "See ya then. Better not be late!!!",
                    "Bet. Don't keep me waiting!",
                    "Finally! Get here ASAP!",
                    "About time. Clock's ticking!",
                    "Good. Hurry up!"
                };

                string message = messages[UnityEngine.Random.Range(0, messages.Length)];
                customer.NPC.SendTextMessage(message);
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Warning($"[AcceptContractClickedPatch] Failed to send confirmation text: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patches Contract.UpdateTiming to show urgent red text for desperation contracts.
    /// </summary>
    [HarmonyPatch(typeof(Contract), "UpdateTiming")]
    public static class ContractUpdateTimingPatch
    {
        private static bool _errorLogged;

        /// <summary>
        /// Postfix: Override the subtitle for desperation contracts with urgent red text.
        /// </summary>
        public static void Postfix(Contract __instance)
        {
            if (__instance == null) return;

            try
            {
                var customerObj = __instance.Customer;
                if (customerObj == null) return;

                var customer = customerObj.TryCast<Customer>();
                if (customer?.NPC == null) return;

                if (!DesperationManager.IsDesperate(customer.NPC.ID))
                    return;

                int minsUntilExpiry = __instance.GetMinsUntilExpiry();
                int hours = Mathf.FloorToInt((float)minsUntilExpiry / 60f);
                int mins = minsUntilExpiry % 60;

                string timeText = hours > 0 ? $"{hours}h {mins}m" : $"{mins} min";
                __instance.SetSubtitle($"<color=#FF4444>URGENT - {timeText} left!</color>");
            }
            catch (Exception ex)
            {
                if (!_errorLogged)
                {
                    Melon<Core>.Logger.Warning($"[ContractUpdateTimingPatch] Error: {ex.Message}");
                    _errorLogged = true;
                }
            }
        }
    }

    /// <summary>
    /// Patches Customer.ContractRejected to clean up desperation events on decline.
    /// Postfix so the game's own rejection logic (dialogue, clearing offer) runs first.
    /// </summary>
    [HarmonyPatch(typeof(Customer), "ContractRejected")]
    public static class ContractRejectedPatch
    {
        public static void Postfix(Customer __instance)
        {
            if (__instance?.NPC == null) return;

            string customerId = __instance.NPC.ID;
            if (DesperationManager.IsDesperate(customerId))
            {
                DesperationManager.OnContractRejected(customerId);
            }
        }
    }

    /// <summary>
    /// Patches QuestManager.ContractAccepted to restore the desperation delivery window
    /// before the contract is created and synced. Customer.ContractAccepted overwrites
    /// window times with the EDealWindow (Morning), so we fix them here — right before
    /// QuestManager uses them to calculate the expiry that gets broadcast to all clients.
    /// </summary>
    [HarmonyPatch]
    public static class QuestManagerContractAcceptedPatch
    {
        public static System.Reflection.MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("ScheduleOne.Quests.QuestManager");
            return type != null ? AccessTools.Method(type, "ContractAccepted") : null;
        }

        public static void Prefix(Customer customer, ContractInfo contractData)
        {
            if (customer?.NPC == null || contractData?.DeliveryWindow == null)
                return;

            if (!DesperationManager.IsDesperate(customer.NPC.ID))
                return;

            int currentTime = TimeManager.CurrentTime;
            int endTime = TimeManager.Get24HourTimeFromMinutes(
                TimeManager.GetMinutesFrom24HourTime(currentTime) + Config.DeadlineMinutes.Value);

            contractData.DeliveryWindow.WindowStartTime = 0;
            contractData.DeliveryWindow.WindowEndTime = endTime;
        }
    }
}
