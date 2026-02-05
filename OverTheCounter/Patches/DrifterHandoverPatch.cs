using HarmonyLib;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.UI.Handover;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.PlayerScripts;
using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.Utilities;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Harmony patch to detect handover completion near drifter NPCs and trigger narc stings.
    /// </summary>
    [HarmonyPatch(typeof(Customer), "ProcessHandover")]
    public static class DrifterHandoverPatch
    {
        // Distance threshold for considering a handover "near" a drifter
        private const float HANDOVER_DISTANCE_THRESHOLD = 15f;

        // Thread-local storage to track drifter involvement between prefix and postfix
        private static readonly System.Threading.ThreadLocal<DrifterHandoverContext> _pendingContext = new();

        /// <summary>
        /// Prefix: Check if the handover is occurring near an active drifter with an accepted deal.
        /// </summary>
        public static void Prefix(
            Customer __instance,
            HandoverScreen.EHandoverOutcome outcome,
            Contract contract,
            Il2CppSystem.Collections.Generic.List<ItemInstance> items,
            bool handoverByPlayer,
            bool giveBonuses)
        {
            _pendingContext.Value = null;

            // Only process finalized handovers initiated by the player
            if (outcome != HandoverScreen.EHandoverOutcome.Finalize || !handoverByPlayer)
                return;

            if (__instance == null || __instance.NPC == null || contract == null)
                return;

            try
            {
                // Get player position using PlayerMovement
                var playerMovement = PlayerSingleton<PlayerMovement>.Instance;
                if (playerMovement == null) return;

                Vector3 playerPos = playerMovement.transform.position;

                // Check all active drifters with accepted deals
                var acceptedDeals = DrifterManager.Instance?.GetAcceptedDeals();
                if (acceptedDeals == null) return;

                foreach (var evt in acceptedDeals)
                {
                    if (!DrifterInstance.Active.TryGetValue(evt.DrifterId, out var drifter))
                        continue;

                    if (drifter == null || drifter.DealCompleted)
                        continue;

                    // Check distance from player to drifter
                    Vector3? drifterPos = drifter.Position;

                    if (!drifterPos.HasValue)
                        continue;

                    float distance = Vector3.Distance(playerPos, drifterPos.Value);
                    if (distance <= HANDOVER_DISTANCE_THRESHOLD)
                    {
                        // This handover is near a drifter - store context for postfix
                        _pendingContext.Value = new DrifterHandoverContext
                        {
                            DrifterId = evt.DrifterId,
                            DrifterType = evt.Type,
                            Distance = distance
                        };

                        Melon<Core>.Logger.Msg($"[DrifterHandoverPatch] Detected handover near drifter {evt.DrifterId} (distance: {distance:F1}m)");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[DrifterHandoverPatch] Prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix: After successful handover, complete drifter deal and trigger narc sting if applicable.
        /// </summary>
        public static void Postfix(
            Customer __instance,
            HandoverScreen.EHandoverOutcome outcome,
            Contract contract,
            Il2CppSystem.Collections.Generic.List<ItemInstance> items,
            bool handoverByPlayer,
            bool giveBonuses)
        {
            var context = _pendingContext.Value;
            _pendingContext.Value = null;

            if (context == null)
                return;

            // Only host processes drifter completions
            if (!NetworkHelper.IsHost)
                return;

            try
            {
                // Complete the drifter deal
                bool isNarc = DrifterManager.Instance?.OnDealCompleted(context.DrifterId) ?? false;

                if (isNarc)
                {
                    // Trigger narc sting
                    TriggerNarcSting(context);
                }

                Melon<Core>.Logger.Msg($"[DrifterHandoverPatch] Completed drifter deal {context.DrifterId}. IsNarc={isNarc}");
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[DrifterHandoverPatch] Postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Triggers a police sting for narc drifters.
        /// </summary>
        private static void TriggerNarcSting(DrifterHandoverContext context)
        {
            try
            {
                Melon<Core>.Logger.Msg($"[DrifterHandoverPatch] NARC STING! Triggering police response.");

                // Get the LawManager singleton
                var lawManager = Singleton<LawManager>.Instance;
                if (lawManager == null)
                {
                    Melon<Core>.Logger.Warning("[DrifterHandoverPatch] LawManager singleton not found");
                    return;
                }

                // Use reflection to call SetWantedLevel with the "Arresting" wanted level
                // EWantedLevel is an enum where Arresting = 3 (highest level)
                try
                {
                    var setWantedMethod = typeof(LawManager).GetMethod("SetWantedLevel",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (setWantedMethod != null)
                    {
                        // Get EWantedLevel.Arresting (value = 3)
                        var ewantedType = typeof(LawManager).Assembly.GetType("Il2CppScheduleOne.Law.EWantedLevel");
                        if (ewantedType != null)
                        {
                            var arrestingValue = Enum.ToObject(ewantedType, 3);
                            setWantedMethod.Invoke(lawManager, new object[] { arrestingValue });
                            Melon<Core>.Logger.Msg("[DrifterHandoverPatch] Set wanted level to Arresting");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Melon<Core>.Logger.Warning($"[DrifterHandoverPatch] SetWantedLevel failed: {ex.Message}");
                }

                // Call police to player location using reflection
                var playerMovement = PlayerSingleton<PlayerMovement>.Instance;
                if (playerMovement != null)
                {
                    try
                    {
                        var callPoliceMethod = typeof(LawManager).GetMethod("CallPolice",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                        if (callPoliceMethod != null)
                        {
                            var parameters = callPoliceMethod.GetParameters();
                            if (parameters.Length >= 1)
                            {
                                // Try calling with just position, or position + number
                                if (parameters.Length == 1)
                                    callPoliceMethod.Invoke(lawManager, new object[] { playerMovement.transform.position });
                                else
                                    callPoliceMethod.Invoke(lawManager, new object[] { playerMovement.transform.position, 3 });

                                Melon<Core>.Logger.Msg("[DrifterHandoverPatch] Called police to player location");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Melon<Core>.Logger.Warning($"[DrifterHandoverPatch] CallPolice failed: {ex.Message}");
                    }
                }

                // Send a taunting text from the drifter
                if (DrifterInstance.Active.TryGetValue(context.DrifterId, out var drifter))
                {
                    try
                    {
                        drifter.SendTextMessage("Nice doing business with you. Oh wait - that was the POLICE. Enjoy county!");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[DrifterHandoverPatch] TriggerNarcSting failed: {ex.Message}");
            }
        }

        private class DrifterHandoverContext
        {
            public string DrifterId { get; set; }
            public DrifterType DrifterType { get; set; }
            public float Distance { get; set; }
        }
    }
}
