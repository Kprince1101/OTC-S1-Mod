using MelonLoader;
using OverTheCounter.Utilities;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.Economy;
using ScheduleOne.Quests;
using ScheduleOne.NPCs;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Handles the decision making: When to hide original HUDs and when to show the summary.
    /// Consolidates contracts that share the same delivery window when the group size
    /// meets or exceeds the configured threshold. Multiple windows can be consolidated
    /// simultaneously, each with its own summary quest.
    /// Desperation contracts (ImmediateQuestWindowConfig) are never consolidated.
    /// </summary>
    public class NotificationManager
    {
        private readonly MelonLogger.Instance _logger;

        /// <summary>
        /// Per-window consolidated group state.
        /// </summary>
        private class ConsolidatedGroup
        {
            public ConsolidatedQuest Quest;
            public int GameQuestInstanceId = -1;
            public int LastContractCount = -1;
            public string LastProductHash = "";
            public bool DebugUpdateLogged;
        }

        // Active consolidated groups, keyed by (windowStart, windowEnd)
        private readonly Dictionary<(int, int), ConsolidatedGroup> _activeGroups = new();

        // Contracts whose HUDs we've hidden — only these get restored
        private readonly HashSet<int> _hiddenContractIds = new();

        private bool _staleCleaned;
        private int _staleCleanupFrame;

        public NotificationManager(MelonLogger.Instance logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Main logic loop. Checks contract count and switches modes accordingly.
        /// Groups contracts by delivery window and consolidates each qualifying group.
        /// </summary>
        public void ProcessContractState()
        {
            if (!Config.ConsolidationEnabled.Value)
            {
                // Deactivate all groups and restore HUDs when toggled off
                if (_activeGroups.Count > 0)
                    Cleanup();
                return;
            }

            // Stale cleanup: dismiss any ConsolidatedQuest left in the game's
            // Quest.Quests registry from a previous session's save data.
            // S1API registers loaded quests via QuestStart (Unity Start lifecycle)
            // which fires AFTER Quest.Quests is populated, so we scan repeatedly
            // every ~0.5s for 5 seconds to catch late arrivals.
            if (!_staleCleaned)
            {
                try
                {
                    var gameQuests = ScheduleOne.Quests.Quest.Quests;
                    if (gameQuests == null || gameQuests.Count == 0)
                        return; // game not loaded yet

                    _staleCleanupFrame++;

                    // Scan every 30 frames (~0.5s)
                    bool shouldScan = (_staleCleanupFrame % 30 == 0);
                    // Give up after 300 frames (~5s)
                    bool timedOut = (_staleCleanupFrame >= 300);

                    if (timedOut)
                    {
                        _staleCleaned = true;
                        // Window closed without finding stale quests — nothing to do.
                    }
                    else if (shouldScan)
                    {
                        // Collect instance IDs of our own active quests to avoid failing them.
                        // Read from ConsolidatedGroup (pure managed class), NOT from the quest
                        // object (IL2CPP-inherited, fields get clobbered).
                        var ownQuestIds = new HashSet<int>();
                        foreach (var g in _activeGroups.Values)
                        {
                            if (g.GameQuestInstanceId != -1)
                                ownQuestIds.Add(g.GameQuestInstanceId);
                        }

                        for (int i = gameQuests.Count - 1; i >= 0; i--)
                        {
                            try
                            {
                                var quest = gameQuests[i];
                                if (quest?.Title == null) continue;

                                if (quest.Title.Contains("Deliveries"))
                                {
                                    int questId = quest.GetInstanceID();

                                    // Skip our own active quests
                                    if (ownQuestIds.Contains(questId))
                                    {
                                        continue;
                                    }

                                    quest.Fail(false);
                                }
                            }
                            catch (System.Exception ex)
                            {
                                _logger.Warning($"[StaleCleanup] Quest inspection threw: {ex.Message}");
                            }
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    _logger.Warning($"[StaleCleanup] Exception: {ex.Message}");
                }
            }

            // Get the list of active contracts from the game
            var contracts = Contract.Contracts;
            if (contracts == null) return;

            // Group contracts by their delivery window (excluding desperation/immediate contracts)
            var windowGroups = GroupContractsByWindow(contracts);

            int threshold = Config.ConsolidationThreshold.Value;

            // Determine which windows currently qualify for consolidation
            var qualifyingWindows = new HashSet<(int, int)>();
            foreach (var group in windowGroups)
            {
                if (group.Value.Count >= threshold && group.Value.Count > 0)
                    qualifyingWindows.Add(group.Key);
            }

            // Deactivate groups whose window no longer qualifies
            var toRemove = new List<(int, int)>();
            foreach (var kvp in _activeGroups)
            {
                if (!qualifyingWindows.Contains(kvp.Key))
                    toRemove.Add(kvp.Key);
            }
            foreach (var key in toRemove)
            {
                DeactivateGroup(key);
            }

            // Activate or update each qualifying group
            foreach (var windowKey in qualifyingWindows)
            {
                var groupContracts = windowGroups[windowKey];

                if (!_activeGroups.ContainsKey(windowKey))
                {
                    ActivateGroup(windowKey, groupContracts);
                }

                if (_activeGroups.TryGetValue(windowKey, out var group))
                {
                    HideGroupHUDs(groupContracts);
                    UpdateGroupSummary(windowKey, group, groupContracts);
                }
            }
        }

        /// <summary>
        /// Groups contracts by their delivery window (start time, end time).
        /// Excludes desperation contracts (ImmediateQuestWindowConfig).
        /// </summary>
        private Dictionary<(int, int), List<Contract>> GroupContractsByWindow(
            GameSystem.Collections.Generic.List<Contract> contracts)
        {
            var groups = new Dictionary<(int, int), List<Contract>>();

            for (int i = 0; i < contracts.Count; i++)
            {
                var contract = contracts[i];
                if (contract == null) continue;

                // Skip contracts assigned to dealers (only consolidate player contracts)
                try
                {
                    if (contract.Dealer != null)
                        continue;
                }
                catch { }

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
        /// Creates a new ConsolidatedQuest for a window group.
        /// </summary>
        private void ActivateGroup((int, int) windowKey, List<Contract> contracts)
        {
            if (Config.VerboseLogging.Value)
                _logger.Msg($"[Activate] Creating ConsolidatedQuest for {contracts.Count} contracts, window {windowKey.Item1}-{windowKey.Item2}");

            var quest = (ConsolidatedQuest)S1API.Quests.QuestManager.CreateQuest<ConsolidatedQuest>();
            if (quest != null)
            {
                quest.Initialize();
                if (Config.VerboseLogging.Value)
                    _logger.Msg("[Activate] Quest created and initialized.");
            }
            else
            {
                _logger.Error("[Activate] CreateQuest<ConsolidatedQuest> returned null!");
                return;
            }

            // Read the instance ID right now while it's fresh — storing it in our
            // own managed class avoids IL2CPP field clobbering on the quest object.
            int instanceId = quest.GameQuestInstanceId;
            if (Config.VerboseLogging.Value)
                _logger.Msg($"[Activate] Stored GameQuestInstanceId={instanceId} for window {windowKey}");

            _activeGroups[windowKey] = new ConsolidatedGroup { Quest = quest, GameQuestInstanceId = instanceId };
        }

        /// <summary>
        /// Removes a consolidated group and restores its contract HUDs.
        /// </summary>
        private void DeactivateGroup((int, int) windowKey)
        {
            if (!_activeGroups.TryGetValue(windowKey, out var group))
                return;

            if (Config.VerboseLogging.Value)
                _logger.Msg($"[Deactivate] Removing group window {windowKey.Item1}-{windowKey.Item2}");

            // Restore all contract HUDs — the next frame's HideGroupHUDs will
            // re-hide contracts that are still in other active groups.
            RestoreAllContractHUDs();

            if (group.Quest != null)
            {
                group.Quest.Dismiss();
            }

            _activeGroups.Remove(windowKey);
            if (Config.VerboseLogging.Value)
                _logger.Msg("[Deactivate] Group removed.");
        }

        /// <summary>
        /// Hides the HUDs for contracts in the consolidated group.
        /// Uses CanvasGroup + LayoutElement instead of SetActive(false) to keep
        /// GameObjects active (prevents the game from destroying them).
        /// </summary>
        private void HideGroupHUDs(List<Contract> contracts)
        {
            foreach (var contract in contracts)
            {
                if (contract?.hudUI?.gameObject == null) continue;

                try
                {
                    var go = contract.hudUI.gameObject;
                    _hiddenContractIds.Add(contract.GetInstanceID());

                    // Make invisible (keep active so game doesn't destroy the HUD)
                    var cg = go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>();
                    cg.alpha = 0f;
                    cg.blocksRaycasts = false;
                    cg.interactable = false;

                    // Collapse layout space
                    var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                    le.ignoreLayout = true;
                }
                catch { }
            }
        }

        /// <summary>
        /// Re-enables HUDs only for contracts we previously hid.
        /// Skips contracts we never touched (e.g. dealer contracts the game hides).
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
                    if (contract?.hudUI?.gameObject == null) continue;
                    if (!_hiddenContractIds.Contains(contract.GetInstanceID())) continue;

                    var go = contract.hudUI.gameObject;

                    // Restore CanvasGroup visibility
                    var cg = go.GetComponent<CanvasGroup>();
                    if (cg != null)
                    {
                        cg.alpha = 1f;
                        cg.blocksRaycasts = true;
                        cg.interactable = true;
                    }

                    // Restore layout participation
                    var le = go.GetComponent<LayoutElement>();
                    if (le != null)
                        le.ignoreLayout = false;
                }
                catch { }
            }

            _hiddenContractIds.Clear();
        }

        /// <summary>
        /// Updates the summary quest for a specific consolidated group.
        /// Only updates if the data has actually changed.
        /// </summary>
        private void UpdateGroupSummary((int, int) windowKey, ConsolidatedGroup group, List<Contract> contracts)
        {
            if (group.Quest == null)
            {
                if (!group.DebugUpdateLogged) { _logger.Warning($"[UpdateGroupSummary] Quest is null for window {windowKey}"); group.DebugUpdateLogged = true; }
                return;
            }

            // Get product summaries for individual entries
            var productSummaries = FormatUtils.GetProductSummaries(contracts);

            // If there are no products to display, deconsolidate instead of showing a blank quest.
            if (productSummaries.Count == 0 || contracts.Count == 0)
            {
                _logger.Warning($"[UpdateGroupSummary] Empty products for window {windowKey}, deactivating group");
                DeactivateGroup(windowKey);
                return;
            }

            // Create a hash of the current state to detect changes
            string currentHash = string.Join("|", productSummaries.Select(p => $"{p.ProductID}:{p.Quantity}"));

            if (contracts.Count != group.LastContractCount || currentHash != group.LastProductHash)
            {
                if (!group.DebugUpdateLogged)
                {
                    if (Config.VerboseLogging.Value)
                        _logger.Msg($"[UpdateGroupSummary] Window {windowKey}: data changed, count={contracts.Count}, products={productSummaries.Count}");
                    group.DebugUpdateLogged = true;
                }
                // Data changed — update tracking and quest content
                group.LastContractCount = contracts.Count;
                group.LastProductHash = currentHash;
                group.Quest.UpdateSummary(contracts.Count, productSummaries, windowKey.Item1, windowKey.Item2);
            }
            else
            {
                // Data unchanged — still update timing (countdown text)
                group.Quest.UpdateTiming(windowKey.Item1, windowKey.Item2);
            }

            // Always call Show(): on the first frame after quest creation the game's
            // hudUI doesn't exist yet so Show() silently no-ops. Calling every frame
            // retries until the HUD is actually visible. Once visible this is a no-op.
            group.Quest.Show();
        }

        /// <summary>
        /// Force cleanup (used when mod unloads).
        /// </summary>
        public void Cleanup()
        {
            RestoreAllContractHUDs();

            foreach (var group in _activeGroups.Values)
            {
                group.Quest?.Dismiss();
            }
            _activeGroups.Clear();
        }
    }
}
