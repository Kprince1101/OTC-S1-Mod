using HarmonyLib;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Quests;
using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.UI;
using S1API.GameTime;
using System;
using UnityEngine;

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
                    FinalizeDesperationDeal(__instance, locationGuid);
                });

                return false; // Skip the original method (don't show time window)
            }

            return true; // Continue to original method for normal deals
        }

        /// <summary>
        /// Finalizes the desperation deal with the selected location.
        /// </summary>
        private static void FinalizeDesperationDeal(Customer customer, string locationGuid)
        {
            try
            {
                if (customer.OfferedContractInfo == null)
                {
                    Melon<Core>.Logger.Error("[AcceptContractClickedPatch] No offered contract to finalize");
                    return;
                }

                var offeredWindow = customer.OfferedContractInfo.DeliveryWindow;
                if (offeredWindow == null)
                {
                    Melon<Core>.Logger.Error("[AcceptContractClickedPatch] OfferedContractInfo.DeliveryWindow is NULL!");
                    return;
                }

                int immediateStart = offeredWindow.WindowStartTime;
                int immediateEnd = offeredWindow.WindowEndTime;

                // Update the contract with the selected location
                customer.OfferedContractInfo.DeliveryLocationGUID = locationGuid;

                // ContractAccepted overrides window times; we restore them below
                customer.ContractAccepted(EDealWindow.Morning, true, dealer: null);

                if (customer.CurrentContract != null)
                {
                    var contract = customer.CurrentContract;
                    var currentWindow = contract.DeliveryWindow;

                    if (currentWindow != null)
                    {
                        currentWindow.WindowStartTime = immediateStart;
                        currentWindow.WindowEndTime = immediateEnd;
                    }
                    else
                    {
                        Melon<Core>.Logger.Error("[AcceptContractClickedPatch] CurrentContract.DeliveryWindow is NULL after accept!");
                    }

                    // Set expiry to current time + deadline so the HUD countdown is correct
                    try
                    {
                        int currentDay = TimeManager.ElapsedDays;
                        int currentTime = TimeManager.CurrentTime;

                        int expiryTime = TimeManager.Get24HourTimeFromMinutes(
                            TimeManager.GetMinutesFrom24HourTime(currentTime) + Config.DeadlineMinutes.Value);

                        int expiryDay = currentDay;
                        if (expiryTime < currentTime)
                            expiryDay++;

                        var expiryDate = new Il2CppScheduleOne.GameTime.GameDateTime(expiryDay, expiryTime);
                        contract.ConfigureExpiry(true, expiryDate);
                    }
                    catch (Exception ex)
                    {
                        Melon<Core>.Logger.Error($"[AcceptContractClickedPatch] Failed to set expiry: {ex.Message}");
                    }
                }
                else
                {
                    Melon<Core>.Logger.Error("[AcceptContractClickedPatch] CurrentContract is NULL after accept!");
                }

                // Clear the message responses (the accept/decline buttons)
                if (customer.NPC.MSGConversation != null)
                {
                    customer.NPC.MSGConversation.ClearResponses(true);
                }

                // Update desperation deadline: 120 minutes from now to deliver
                DesperationManager.OnContractAccepted(customer.NPC.ID);

                // Send confirmation text from customer
                SendConfirmationText(customer);
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[AcceptContractClickedPatch] FinalizeDesperationDeal failed: {ex.Message}");
                Melon<Core>.Logger.Error($"[AcceptContractClickedPatch] Stack trace: {ex.StackTrace}");
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
    /// Patches PlayerAcceptedContract to preserve immediate delivery window for desperation deals.
    /// This is a backup in case the window times still get overwritten.
    /// </summary>
    [HarmonyPatch(typeof(Customer), "PlayerAcceptedContract")]
    public static class PlayerAcceptedContractPatch
    {
        // Store window times before the method runs
        private static int _preservedStart = -1;
        private static int _preservedEnd = -1;
        private static bool _shouldPreserve = false;

        /// <summary>
        /// Prefix: Store immediate window times for desperation deals before they get overwritten.
        /// </summary>
        public static void Prefix(Customer __instance, EDealWindow window)
        {
            _shouldPreserve = false;

            if (__instance == null || __instance.NPC == null || __instance.OfferedContractInfo == null)
                return;

            string customerId = __instance.NPC.ID;

            // Check if this is a desperation deal
            if (DesperationManager.IsDesperate(customerId))
            {
                // Store the immediate window times
                _preservedStart = __instance.OfferedContractInfo.DeliveryWindow.WindowStartTime;
                _preservedEnd = __instance.OfferedContractInfo.DeliveryWindow.WindowEndTime;
                _shouldPreserve = true;

            }
        }

        /// <summary>
        /// Postfix: Restore immediate window times after the game tried to override them.
        /// </summary>
        public static void Postfix(Customer __instance, EDealWindow window)
        {
            if (!_shouldPreserve || __instance == null)
                return;

            try
            {
                // Restore on the OfferedContractInfo (might still be there briefly)
                if (__instance.OfferedContractInfo?.DeliveryWindow != null)
                {
                    __instance.OfferedContractInfo.DeliveryWindow.WindowStartTime = _preservedStart;
                    __instance.OfferedContractInfo.DeliveryWindow.WindowEndTime = _preservedEnd;
                }

                // Restore on the CurrentContract (this is where it matters)
                if (__instance.CurrentContract?.DeliveryWindow != null)
                {
                    __instance.CurrentContract.DeliveryWindow.WindowStartTime = _preservedStart;
                    __instance.CurrentContract.DeliveryWindow.WindowEndTime = _preservedEnd;
                }
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[PlayerAcceptedContractPatch] Failed to restore window: {ex.Message}");
            }
            finally
            {
                _shouldPreserve = false;
            }
        }
    }
}
