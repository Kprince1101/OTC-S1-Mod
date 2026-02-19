using HarmonyLib;
using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using System;

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
    /// Harmony patch to detect handover completion for drifter NPCs.
    /// Identifies drifters by NPC ID (not proximity). Payment and narc sting
    /// are handled by DrifterManager's HandoverScreen callback — this marks
    /// the deal completed as a safety net.
    /// </summary>
    [HarmonyPatch(typeof(Customer), "ProcessHandover")]
    public static class DrifterHandoverPatch
    {
        // Thread-local storage to track drifter ID between prefix and postfix
        private static readonly System.Threading.ThreadLocal<string> _pendingDrifterId = new();

        /// <summary>
        /// Prefix: Check if this handover's customer IS a drifter with an active deal.
        /// </summary>
        public static void Prefix(
            Customer __instance,
            HandoverScreen.EHandoverOutcome outcome,
            Contract contract,
            GameSystem.Collections.Generic.List<ItemInstance> items,
            bool handoverByPlayer,
            bool giveBonuses)
        {
            _pendingDrifterId.Value = null;

            // Only process finalized handovers initiated by the player
            if (outcome != HandoverScreen.EHandoverOutcome.Finalize || !handoverByPlayer)
                return;

            if (__instance?.NPC == null)
                return;

            try
            {
                string npcId = __instance.NPC.ID;

                // Check if this customer IS a drifter with an accepted deal
                if (!DrifterInstance.Active.TryGetValue(npcId, out var drifter))
                    return;

                if (drifter.DealCompleted)
                    return;

                _pendingDrifterId.Value = npcId;
                if (Config.VerboseLogging.Value)
                    Melon<Core>.Logger.Msg($"[DrifterHandoverPatch] Detected drifter handover for {npcId}");
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[DrifterHandoverPatch] Prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix: Mark the drifter deal as completed (idempotent safety net).
        /// </summary>
        public static void Postfix(
            Customer __instance,
            HandoverScreen.EHandoverOutcome outcome,
            Contract contract,
            GameSystem.Collections.Generic.List<ItemInstance> items,
            bool handoverByPlayer,
            bool giveBonuses)
        {
            var drifterId = _pendingDrifterId.Value;
            _pendingDrifterId.Value = null;

            if (drifterId == null)
                return;

            // Only host processes drifter completions
            if (!NetworkHelper.IsHost)
                return;

            try
            {
                DrifterManager.Instance?.OnDealCompleted(drifterId);
                Melon<Core>.Logger.Msg($"[DrifterHandoverPatch] Completed drifter deal {drifterId}");
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Error($"[DrifterHandoverPatch] Postfix error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patches HandoverScreen.Open to clear stale CustomerSlots before every open
    /// and show the Smart Fill overlay in Contract mode.
    /// Close(Finalize) does NOT clear slots, so items from a previous handover
    /// (e.g. a drifter deal) bleed into the next handover (e.g. a desperation deal).
    /// </summary>
    [HarmonyPatch(typeof(HandoverScreen), nameof(HandoverScreen.Open))]
    public static class HandoverScreenOpenPatch
    {
        public static void Prefix(HandoverScreen __instance)
        {
            try
            {
                __instance.ClearCustomerSlots(false);
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Warning($"[HandoverScreenOpenPatch] ClearCustomerSlots failed: {ex.Message}");
            }
        }

        public static void Postfix(HandoverScreen __instance, HandoverScreen.EMode mode)
        {
            try
            {
                Melon<Core>.Logger.Msg($"[HandoverScreenOpenPatch] Open fired, mode={mode}");
                if (mode == HandoverScreen.EMode.Contract)
                    HandoverFillUI.Show();
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Warning($"[HandoverScreenOpenPatch] Show overlay failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Hides the Smart Fill overlay when the HandoverScreen closes.
    /// </summary>
    [HarmonyPatch(typeof(HandoverScreen), nameof(HandoverScreen.Close))]
    public static class HandoverScreenClosePatch
    {
        public static void Postfix()
        {
            try
            {
                HandoverFillUI.Hide();
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Warning($"[HandoverScreenClosePatch] Hide overlay failed: {ex.Message}");
            }
        }
    }
}
