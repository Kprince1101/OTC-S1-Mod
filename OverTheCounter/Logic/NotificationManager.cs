using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.UI;
using MelonLoader;
using OverTheCounter.Utilities;
using System.Collections.Generic;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Handles the decision making: When to hide original HUDs and when to show the summary.
    /// </summary>
    public class NotificationManager
    {
        private const int CONSOLIDATION_THRESHOLD = 5;
        private readonly MelonLogger.Instance _logger;

        // State tracking
        private bool _isConsolidated = false;

        // Keep track of which HUDs we have hidden so we can restore them later
        private readonly List<QuestHUDUI> _hiddenHUDs = new();

        // Reference to our S1API Quest
        private ConsolidatedQuest _summaryQuest;

        public NotificationManager(MelonLogger.Instance logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Main logic loop. Checks contract count and switches modes accordingly.
        /// </summary>
        public void ProcessContractState()
        {
            // Get the list of active contracts from the game
            var contracts = Contract.Contracts;
            if (contracts == null) return;

            int contractCount = contracts.Count;

            // MODE 1: Too many contracts -> Consolidate into one UI
            if (contractCount > CONSOLIDATION_THRESHOLD)
            {
                // If we aren't consolidated yet, switch modes
                if (!_isConsolidated)
                {
                    ActivateConsolidatedMode(contracts);
                }

                // If we are consolidated, we need to constantly refresh the data
                // because contract progress changes every frame
                if (_isConsolidated)
                {
                    HideIndividualHUDs(contracts);
                    UpdateSummaryData(contracts);
                }
            }
            // MODE 2: Few contracts -> Show individual notifications
            else if (_isConsolidated)
            {
                DeactivateConsolidatedMode();
            }
        }

        /// <summary>
        /// Switches to the custom summary quest.
        /// </summary>
        private void ActivateConsolidatedMode(Il2CppSystem.Collections.Generic.List<Contract> contracts)
        {
            // Create the S1API quest using the proper registration method
            _summaryQuest = (ConsolidatedQuest)S1API.Quests.QuestManager.CreateQuest<ConsolidatedQuest>();

            if (_summaryQuest != null)
            {
                // Initialize the quest entry after creation
                _summaryQuest.Initialize();
            }

            _isConsolidated = true;
            _logger.Msg($"Consolidated {contracts.Count} delivery notifications.");
        }

        /// <summary>
        /// Switches back to standard game behavior.
        /// </summary>
        private void DeactivateConsolidatedMode()
        {
            // Dismiss the quest
            _summaryQuest?.Dismiss();
            _summaryQuest = null;

            RestoreIndividualHUDs();
            _isConsolidated = false;
            _logger.Msg("Restored individual notifications.");
        }

        /// <summary>
        /// Hides the game's default quest notifications.
        /// </summary>
        private void HideIndividualHUDs(Il2CppSystem.Collections.Generic.List<Contract> contracts)
        {
            // Use index-based loop to avoid potential Il2Cpp enumeration issues
            for (int i = 0; i < contracts.Count; i++)
            {
                var contract = contracts[i];
                if (contract == null) continue;

                // Check if the HUD exists, is valid, and is currently visible
                if (contract.hudUI != null && contract.hudUI.gameObject != null && contract.hudUI.gameObject.activeSelf)
                {
                    contract.hudUI.gameObject.SetActive(false);

                    // Add to our tracker so we can turn it back on later
                    if (!_hiddenHUDs.Contains(contract.hudUI))
                    {
                        _hiddenHUDs.Add(contract.hudUI);
                    }
                }
            }
        }

        /// <summary>
        /// Re-enables the game's default quest notifications.
        /// </summary>
        private void RestoreIndividualHUDs()
        {
            foreach (var hud in _hiddenHUDs)
            {
                if (hud != null && hud.gameObject != null)
                {
                    hud.gameObject.SetActive(true);
                }
            }
            _hiddenHUDs.Clear();
        }

        /// <summary>
        /// Calculates totals and updates the quest text.
        /// </summary>
        private void UpdateSummaryData(Il2CppSystem.Collections.Generic.List<Contract> contracts)
        {
            if (_summaryQuest == null) return;

            // Use the utility to generate the product breakdown string
            string productBreakdown = FormatUtils.BuildProductBreakdownString(contracts);

            // Update the quest with count and product info
            _summaryQuest.UpdateSummary(contracts.Count, productBreakdown);
        }

        /// <summary>
        /// Force cleanup (used when mod unloads).
        /// </summary>
        public void Cleanup()
        {
            if (_isConsolidated)
            {
                DeactivateConsolidatedMode();
            }
        }
    }
}
