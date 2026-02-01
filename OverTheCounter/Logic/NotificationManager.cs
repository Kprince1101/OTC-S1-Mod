using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.NPCs;
using MelonLoader;
using OverTheCounter.Utilities;
using System.Collections.Generic;
using System.Linq;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Handles the decision making: When to hide original HUDs and when to show the summary.
    /// Only consolidates contracts that share the same delivery window.
    /// Desperation contracts (ImmediateQuestWindowConfig) are never consolidated.
    /// </summary>
    public class NotificationManager
    {
        private readonly MelonLogger.Instance _logger;

        private bool _isConsolidated = false;
        private int _pendingRestoreFrames;

        // Reference to our S1API Quest
        private ConsolidatedQuest _summaryQuest;

        // Track the current consolidated window for comparison
        private int _consolidatedWindowStart = -1;
        private int _consolidatedWindowEnd = -1;

        // Track last contract count to avoid unnecessary updates
        private int _lastContractCount = -1;
        private string _lastProductHash = "";

        public NotificationManager(MelonLogger.Instance logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Main logic loop. Checks contract count and switches modes accordingly.
        /// Groups contracts by delivery window and only consolidates matching windows.
        /// </summary>
        public void ProcessContractState()
        {
            // Multi-frame restore retry: after deconsolidation, the game may
            // recreate HUD objects over several frames. Keep restoring until done.
            if (_pendingRestoreFrames > 0)
            {
                _pendingRestoreFrames--;
                RestoreAllContractHUDs();
            }

            // Get the list of active contracts from the game
            var contracts = Contract.Contracts;
            if (contracts == null) return;

            // Group contracts by their delivery window (excluding desperation/immediate contracts)
            var windowGroups = GroupContractsByWindow(contracts);

            // Find the largest group that exceeds the threshold
            KeyValuePair<(int, int), List<Contract>>? largestGroup = null;
            int largestCount = 0;

            foreach (var group in windowGroups)
            {
                if (group.Value.Count > largestCount)
                {
                    largestCount = group.Value.Count;
                    largestGroup = group;
                }
            }

            // MODE 1: A window group exceeds threshold -> Consolidate that group
            if (largestGroup.HasValue && largestCount > Config.ConsolidationThreshold.Value)
            {
                var windowKey = largestGroup.Value.Key;
                var groupContracts = largestGroup.Value.Value;

                // Check if we need to switch to a different window group
                bool windowChanged = _consolidatedWindowStart != windowKey.Item1 ||
                                     _consolidatedWindowEnd != windowKey.Item2;

                // If not consolidated or window changed, (re)activate consolidated mode
                if (!_isConsolidated || windowChanged)
                {
                    if (_isConsolidated)
                    {
                        // Deactivate first if switching windows
                        DeactivateConsolidatedMode();
                    }
                    ActivateConsolidatedMode(groupContracts, windowKey.Item1, windowKey.Item2);
                }

                // Update the consolidated view
                if (_isConsolidated)
                {
                    HideGroupHUDs(groupContracts);
                    UpdateSummaryData(groupContracts);
                }
            }
            // MODE 2: No group exceeds threshold -> Show individual notifications
            else if (_isConsolidated)
            {
                DeactivateConsolidatedMode();
            }
        }

        /// <summary>
        /// Groups contracts by their delivery window (start time, end time).
        /// Excludes desperation contracts (ImmediateQuestWindowConfig).
        /// </summary>
        private Dictionary<(int, int), List<Contract>> GroupContractsByWindow(
            Il2CppSystem.Collections.Generic.List<Contract> contracts)
        {
            var groups = new Dictionary<(int, int), List<Contract>>();

            for (int i = 0; i < contracts.Count; i++)
            {
                var contract = contracts[i];
                if (contract == null) continue;

                // Skip desperation deals tracked by DesperationManager
                try
                {
                    var customerObj = contract.Customer;
                    if (customerObj != null)
                    {
                        var customer = customerObj.TryCast<Customer>();
                        if (customer?.NPC != null)
                        {
                            if (DesperationManager.IsDesperate(customer.NPC.ID))
                            {
                                continue;
                            }
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    // Only log actual errors
                    _logger.Warning($"[GroupContractsByWindow] Customer check failed for contract {i}: {ex.Message}");
                }

                // Check delivery window
                var deliveryWindow = contract.DeliveryWindow;
                if (deliveryWindow == null) continue;

                // Check if this is an ImmediateQuestWindowConfig (desperation contract)
                try
                {
                    var immediateWindow = deliveryWindow.TryCast<ImmediateQuestWindowConfig>();
                    if (immediateWindow != null)
                    {
                        continue;
                    }
                }
                catch (System.Exception)
                {
                    // TryCast failed - this is expected and fine
                }

                // Group by window times
                var windowKey = (deliveryWindow.WindowStartTime, deliveryWindow.WindowEndTime);

                if (!groups.ContainsKey(windowKey))
                {
                    groups[windowKey] = new List<Contract>();
                }
                groups[windowKey].Add(contract);
            }

            return groups;
        }

        /// <summary>
        /// Switches to the custom summary quest for a specific window group.
        /// </summary>
        private void ActivateConsolidatedMode(List<Contract> contracts, int windowStart, int windowEnd)
        {
            // Create the S1API quest using the proper registration method
            _summaryQuest = (ConsolidatedQuest)S1API.Quests.QuestManager.CreateQuest<ConsolidatedQuest>();

            if (_summaryQuest != null)
            {
                // Initialize the quest entry after creation
                _summaryQuest.Initialize();
            }

            _consolidatedWindowStart = windowStart;
            _consolidatedWindowEnd = windowEnd;
            _isConsolidated = true;
            _logger.Msg($"Consolidated {contracts.Count} delivery notifications for window {windowStart}-{windowEnd}.");
        }

        /// <summary>
        /// Switches back to standard game behavior.
        /// Restores individual HUDs BEFORE dismissing the summary quest so
        /// Fail()/Dismiss() cannot destroy the contract HUD objects first.
        /// </summary>
        private void DeactivateConsolidatedMode()
        {
            RestoreAllContractHUDs();

            _summaryQuest?.Dismiss();
            _summaryQuest = null;

            _consolidatedWindowStart = -1;
            _consolidatedWindowEnd = -1;
            _lastContractCount = -1;
            _lastProductHash = "";
            _isConsolidated = false;

            // Retry restoration for a few frames in case HUDs are rebuilt by the game.
            _pendingRestoreFrames = 3;

            _logger.Msg("Restored individual notifications.");
        }

        /// <summary>
        /// Hides the HUDs for contracts in the consolidated group.
        /// </summary>
        private void HideGroupHUDs(List<Contract> contracts)
        {
            foreach (var contract in contracts)
            {
                if (contract == null) continue;

                try
                {
                    if (contract.hudUI != null && contract.hudUI.gameObject != null && contract.hudUI.gameObject.activeSelf)
                        contract.hudUI.gameObject.SetActive(false);
                }
                catch { }
            }
        }

        /// <summary>
        /// Re-enables HUDs on all live contracts. Scans the game's contract list
        /// directly to avoid holding stale Il2Cpp references.
        /// </summary>
        private void RestoreAllContractHUDs()
        {
            var contracts = Contract.Contracts;
            if (contracts == null) return;

            for (int i = 0; i < contracts.Count; i++)
            {
                try
                {
                    var contract = contracts[i];
                    if (contract == null) continue;
                    if (contract.hudUI != null && contract.hudUI.gameObject != null && !contract.hudUI.gameObject.activeSelf)
                        contract.hudUI.gameObject.SetActive(true);
                }
                catch { }
            }
        }

        /// <summary>
        /// Calculates totals and updates the quest text for the consolidated group.
        /// Only updates if the data has actually changed.
        /// </summary>
        private void UpdateSummaryData(List<Contract> contracts)
        {
            if (_summaryQuest == null) return;

            // Get product summaries for individual entries
            var productSummaries = FormatUtils.GetProductSummaries(contracts);

            // Create a hash of the current state to detect changes
            string currentHash = string.Join("|", productSummaries.Select(p => $"{p.ProductID}:{p.Quantity}"));

            // Only update if count or products have changed
            if (contracts.Count == _lastContractCount && currentHash == _lastProductHash)
            {
                // Still update timing even if products haven't changed
                _summaryQuest.UpdateTiming(_consolidatedWindowStart, _consolidatedWindowEnd);
                return;
            }

            // Update tracking
            _lastContractCount = contracts.Count;
            _lastProductHash = currentHash;

            // Update the quest with count, product summaries, and window times
            _summaryQuest.UpdateSummary(contracts.Count, productSummaries, _consolidatedWindowStart, _consolidatedWindowEnd);
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
