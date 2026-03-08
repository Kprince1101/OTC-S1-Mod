using MelonLoader;
using OverTheCounter.Utilities;
using S1API.Quests;
using S1API.GameTime;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// A custom Quest that acts as a container for our summary data.
    /// Uses S1API to render natively in the game's phone/HUD.
    /// Title: Dynamic count (e.g., "8 Deliveries")
    /// Entries: Each product as a separate line (e.g., "60x Granddaddy Purple")
    /// </summary>
    public class ConsolidatedQuest : Quest
    {
        // Dynamic state
        private string _description = "Consolidated delivery summary";
        private int _currentCount = 0;

        // The title that appears in the quest log (includes count)
        protected override string Title => _currentCount > 0 ? $"{_currentCount} Deliveries" : "Deliveries";

        // The description (journal only)
        protected override string Description => _description;

        // Auto-begin when created (we control creation timing in NotificationManager)
        protected override bool AutoBegin => true;

        // Flag to track if we've initialized
        private bool _initialized = false;

        private ScheduleOne.Quests.Quest GetS1Quest()
        {
            var field = typeof(Quest).GetField("S1Quest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(this) as ScheduleOne.Quests.Quest;
        }

        /// <summary>
        /// Cached instance ID of the underlying game quest, set during Initialize().
        /// Used by NotificationManager to distinguish our active quests from stale ones.
        /// Explicit backing field required — auto-properties lose values in IL2CPP-inherited classes.
        /// </summary>
        private int _gameQuestInstanceId = -1;
        public int GameQuestInstanceId => _gameQuestInstanceId;

        /// <summary>
        /// Manually trigger internal initialization that QuestManager should have done.
        /// This is a workaround for QuestManager.CreateQuest not calling CreateInternal.
        /// </summary>
        private void TriggerInternalInit()
        {
            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null) return;

                // Call InitializeQuest to register with the game's UI
                s1Quest.InitializeQuest(Title, Description, System.Array.Empty<ScheduleOne.Persistence.Datas.QuestEntryData>(), s1Quest.StaticGUID);
            }
            catch (System.Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"TriggerInternalInit failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize the quest entry after creation.
        /// Must be called after QuestManager.CreateQuest returns.
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;

            try
            {
                // Manually trigger internal initialization that QuestManager should have done
                TriggerInternalInit();

                // If quest is still inactive, explicitly begin it
                if (QuestState == S1API.Quests.Constants.QuestState.Inactive)
                {
                    Begin();
                }

                _initialized = true;

                // Cache the instance ID for stale cleanup identification
                try
                {
                    var s1Quest = GetS1Quest();
                    if (s1Quest != null)
                    {
                        _gameQuestInstanceId = s1Quest.GetInstanceID();
                        OTCLog.Msg(OTCLog.Systems.Quest, $"Cached GameQuestInstanceId={_gameQuestInstanceId}");
                    }
                }
                catch { }
            }
            catch (System.Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"Initialize failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the quest with current delivery information.
        /// Uses QuestEntries to show each product as a separate line.
        /// </summary>
        /// <param name="deliveryCount">Number of pending deliveries</param>
        /// <param name="productSummaries">List of products and their quantities</param>
        public void UpdateSummary(int deliveryCount, List<FormatUtils.ProductSummary> productSummaries)
        {
            UpdateSummary(deliveryCount, productSummaries, -1, -1);
        }

        /// <summary>
        /// Updates the quest with current delivery information and window timing.
        /// Reuses existing entries where possible to avoid destroy/recreate issues
        /// with the game's QuestHUDUI referencing stale entry UIs.
        /// </summary>
        public void UpdateSummary(int deliveryCount, List<FormatUtils.ProductSummary> productSummaries, int windowStart, int windowEnd)
        {
            if (!_initialized)
                Initialize();

            _currentCount = deliveryCount;
            UpdateTitle();
            OTCLog.Msg(OTCLog.Systems.Quest, $"UpdateSummary: count={deliveryCount}, products={productSummaries.Count}, title='{Title}'");

            try
            {
                int existingCount = QuestEntries.Count;
                int newCount = productSummaries.Count;

                // Update existing entries in place (no destroy/recreate needed)
                for (int i = 0; i < System.Math.Min(existingCount, newCount); i++)
                {
                    string newTitle = $"{productSummaries[i].Quantity}x {productSummaries[i].DisplayName}";
                    if (QuestEntries[i].Title != newTitle)
                        QuestEntries[i].Title = newTitle;
                }

                // Add new entries if we need more
                for (int i = existingCount; i < newCount; i++)
                {
                    var entry = AddEntry($"{productSummaries[i].Quantity}x {productSummaries[i].DisplayName}");
                    entry.Begin();
                }

                // Remove excess entries from the end
                if (existingCount > newCount)
                    RemoveExcessEntries(newCount);

                // Update description for journal
                var descParts = new List<string>();
                foreach (var summary in productSummaries)
                    descParts.Add($"{summary.Quantity}x {summary.DisplayName}");
                _description = descParts.Count > 0
                    ? "Products needed:\n" + string.Join("\n", descParts)
                    : "No products pending";

                if (windowStart >= 0 && windowEnd >= 0)
                    UpdateTiming(windowStart, windowEnd);
            }
            catch (System.Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"UpdateSummary failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the time subtitle based on delivery window.
        /// Mimics the game's Contract.UpdateTiming() behavior.
        /// </summary>
        /// <param name="windowStart">Window start time (game time units)</param>
        /// <param name="windowEnd">Window end time (game time units)</param>
        public void UpdateTiming(int windowStart, int windowEnd)
        {
            try
            {
                // Get current game time
                int currentTime = TimeManager.CurrentTime;

                // Convert times to minutes for comparison
                int currentMinutes = TimeManager.GetMinutesFrom24HourTime(currentTime);
                int startMinutes = TimeManager.GetMinutesFrom24HourTime(windowStart);
                int endMinutes = TimeManager.GetMinutesFrom24HourTime(windowEnd);

                // Determine if we're inside the window
                bool isInsideWindow;
                if (startMinutes < endMinutes)
                {
                    // Normal case (e.g., 600-1200): window doesn't wrap around midnight
                    isInsideWindow = currentMinutes >= startMinutes && currentMinutes < endMinutes;
                }
                else if (startMinutes > endMinutes)
                {
                    // Wrapped case (e.g., 1800-600): window wraps around midnight
                    isInsideWindow = currentMinutes >= startMinutes || currentMinutes < endMinutes;
                }
                else
                {
                    // Edge case: start == end (shouldn't happen, but treat as not inside)
                    isInsideWindow = false;
                }

                // Calculate minutes until window end (expiry) and start
                int minsUntilExpiry = CalculateMinutesUntil(currentTime, windowEnd);
                int minsUntilStart = CalculateMinutesUntil(currentTime, windowStart);

                // Format the subtitle like the game does
                string subtitle;
                if (!isInsideWindow && minsUntilStart > 0)
                {
                    // Window hasn't started yet
                    int hours = minsUntilStart / 60;
                    if (hours > 0)
                    {
                        subtitle = $"<color=#c0c0c0ff> (Begins in {hours} hrs)</color>";
                    }
                    else
                    {
                        subtitle = $"<color=#c0c0c0ff> (Begins in {minsUntilStart} min)</color>";
                    }
                }
                else if (minsUntilExpiry < 120)
                {
                    // Critical time - less than 2 hours until expiry
                    int hours = minsUntilExpiry / 60;
                    if (hours > 0)
                    {
                        subtitle = $"<color=#ff6b6b> (Expires in {hours} hrs)</color>";
                    }
                    else
                    {
                        subtitle = $"<color=#ff6b6b> (Expires in {minsUntilExpiry} min)</color>";
                    }
                }
                else
                {
                    // Normal - show time until expiry in green
                    int hours = minsUntilExpiry / 60;
                    if (hours > 0)
                    {
                        subtitle = $"<color=green> (Expires in {hours} hrs)</color>";
                    }
                    else
                    {
                        subtitle = $"<color=green> (Expires in {minsUntilExpiry} min)</color>";
                    }
                }

                // Set the subtitle via reflection
                SetSubtitleViaReflection(subtitle);
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"UpdateTiming failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculates minutes until a target time, handling day wraparound.
        /// Times are in 24-hour format (HHMM), not minutes.
        /// </summary>
        private int CalculateMinutesUntil(int currentTime, int targetTime)
        {
            // Convert 24-hour format (HHMM) to minutes since midnight
            int currentMinutes = TimeManager.GetMinutesFrom24HourTime(currentTime);
            int targetMinutes = TimeManager.GetMinutesFrom24HourTime(targetTime);
            
            if (targetMinutes >= currentMinutes)
            {
                // Target is today
                return targetMinutes - currentMinutes;
            }
            else
            {
                // Target is tomorrow (wrapped around midnight)
                // 1440 = 24 hours * 60 minutes
                return (1440 - currentMinutes) + targetMinutes;
            }
        }

        /// <summary>
        /// Updates the quest title directly on the underlying S1Quest without
        /// reinitializing. The HUD picks up the new title on the next UI refresh
        /// (triggered by SetSubtitle in UpdateTiming or SetEntryTitle on entries).
        /// </summary>
        private void UpdateTitle()
        {
            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null) return;
                s1Quest.SetTitle(Title);
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"UpdateTitle failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the quest subtitle via reflection on the underlying S1Quest.
        /// </summary>
        private bool _subtitleDebugLogged;

        private void SetSubtitleViaReflection(string subtitle)
        {
            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null) return;

                // Set subtitle via the game's method (fires onSubtitleChanged event)
                s1Quest.SetSubtitle(subtitle);

                // Force-update the HUD label as a fallback in case the
                // onSubtitleChanged event wasn't properly wired up.
                if (s1Quest.hudUI != null)
                    s1Quest.hudUI.UpdateMainLabel();

                if (!_subtitleDebugLogged)
                {
                    OTCLog.Msg(OTCLog.Systems.Quest, $"SetSubtitle: '{subtitle}', hudUI={s1Quest.hudUI != null}, Subtitle='{s1Quest.Subtitle}'");
                    _subtitleDebugLogged = true;
                }
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"SetSubtitleViaReflection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes entries from the end when fewer products are needed.
        /// Uses DestroyImmediate so the QuestHUDUI doesn't reference stale objects.
        /// </summary>
        private void RemoveExcessEntries(int keepCount)
        {
            try
            {
                var s1Quest = GetS1Quest();
                for (int i = QuestEntries.Count - 1; i >= keepCount; i--)
                {
                    try
                    {
                        if (s1Quest != null && s1Quest.Entries != null && i < s1Quest.Entries.Count)
                        {
                            var entry = s1Quest.Entries[i];
                            if (entry != null)
                            {
                                if (entry.GetEntryUI() != null && entry.GetEntryUI().gameObject != null)
                                    UnityEngine.Object.DestroyImmediate(entry.GetEntryUI().gameObject);
                                if (entry.gameObject != null)
                                    UnityEngine.Object.DestroyImmediate(entry.gameObject);
                            }
                            s1Quest.Entries.RemoveAt(i);
                        }
                    }
                    catch { }

                    if (i < QuestEntries.Count)
                        QuestEntries.RemoveAt(i);
                }
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"RemoveExcessEntries failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears all quest entries. Uses DestroyImmediate to avoid stale
        /// QuestEntryHUDUI references within the same frame.
        /// </summary>
        private void ClearAllEntries()
        {
            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null || s1Quest.Entries == null)
                {
                    QuestEntries.Clear();
                    return;
                }

                int count = s1Quest.Entries.Count;
                for (int i = count - 1; i >= 0; i--)
                {
                    var entry = s1Quest.Entries[i];
                    if (entry != null)
                    {
                        if (entry.GetEntryUI() != null && entry.GetEntryUI().gameObject != null)
                            UnityEngine.Object.DestroyImmediate(entry.GetEntryUI().gameObject);
                        if (entry.gameObject != null)
                            UnityEngine.Object.DestroyImmediate(entry.gameObject);
                    }
                }
                s1Quest.Entries.Clear();
                QuestEntries.Clear();
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"ClearAllEntries failed: {ex.Message}");
                try { QuestEntries.Clear(); } catch { }
            }
        }

        /// <summary>
        /// Hides the quest HUD without destroying the quest.
        /// Uses CanvasGroup + LayoutElement so the quest can be shown again later.
        /// </summary>
        public void Hide()
        {
            _shown = false;

            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest?.hudUI?.gameObject == null) return;

                var go = s1Quest.hudUI.gameObject;
                var cg = go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>();
                cg.alpha = 0f;
                cg.blocksRaycasts = false;
                cg.interactable = false;

                var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                le.ignoreLayout = true;
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"Hide failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows a previously hidden quest HUD.
        /// Tracks visibility state to avoid per-frame overhead once visible.
        /// Reset by Hide() so re-showing works correctly.
        /// </summary>
        private bool _showDebugLogged;
        private bool _shown;

        public void Show()
        {
            if (_shown) return;

            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null)
                {
                    if (!_showDebugLogged) { OTCLog.Warning(OTCLog.Systems.Quest, "Show: s1Quest is null"); _showDebugLogged = true; }
                    return;
                }
                if (s1Quest.hudUI == null)
                {
                    if (!_showDebugLogged) { OTCLog.Warning(OTCLog.Systems.Quest, "Show: hudUI is null (HUD not created yet by game)"); _showDebugLogged = true; }
                    return;
                }
                if (s1Quest.hudUI.gameObject == null)
                {
                    if (!_showDebugLogged) { OTCLog.Warning(OTCLog.Systems.Quest, "Show: hudUI.gameObject is null"); _showDebugLogged = true; }
                    return;
                }

                var go = s1Quest.hudUI.gameObject;

                if (!_showDebugLogged)
                {
                    var cg0 = go.GetComponent<CanvasGroup>();
                    OTCLog.Msg(OTCLog.Systems.Quest, $"Show: hudUI exists, active={go.activeSelf}, alpha={cg0?.alpha}, title='{s1Quest.GetTitle()}', entries={s1Quest.Entries?.Count}");
                    _showDebugLogged = true;
                }

                if (!go.activeSelf)
                    go.SetActive(true);

                var cg = go.GetComponent<CanvasGroup>();
                if (cg != null)
                {
                    cg.alpha = 1f;
                    cg.blocksRaycasts = true;
                    cg.interactable = true;
                }

                var le = go.GetComponent<LayoutElement>();
                if (le != null)
                    le.ignoreLayout = false;

                _shown = true;
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"Show failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Cleanly removes the quest when the mod unloads.
        /// Clears entries first to avoid stale HUD references during Fail().
        /// </summary>
        public void Dismiss()
        {
            OTCLog.Msg(OTCLog.Systems.Quest, "Dismiss() called");

            try { ClearAllEntries(); }
            catch (System.Exception ex) { OTCLog.Warning(OTCLog.Systems.Quest, $"Dismiss: ClearAllEntries threw: {ex.Message}"); }

            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Quest, "Dismiss: GetS1Quest() returned null — cannot Fail");
                    return;
                }

                int stateBefore = (int)s1Quest.State;
                string titleBefore = s1Quest.GetTitle() ?? "(null)";
                OTCLog.Msg(OTCLog.Systems.Quest, $"Dismiss: calling Fail(false) — state={stateBefore}, title='{titleBefore}', GUID='{s1Quest.StaticGUID}'");

                s1Quest.Fail(false);

                int stateAfter = (int)s1Quest.State;
                OTCLog.Msg(OTCLog.Systems.Quest, $"Dismiss: Fail(false) returned — state changed {stateBefore} → {stateAfter}");

#if DEBUG
                // Verify removal from game registries
                bool inQuestQuests = false;
                try
                {
                    var gameQuests = ScheduleOne.Quests.Quest.Quests;
                    if (gameQuests != null)
                    {
                        for (int i = 0; i < gameQuests.Count; i++)
                        {
                            if (gameQuests[i]?.GetInstanceID() == s1Quest.GetInstanceID())
                            { inQuestQuests = true; break; }
                        }
                    }
                }
                catch { }

                OTCLog.Msg(OTCLog.Systems.Quest, $"Dismiss: post-Fail registry check — stillInGameQuestsList={inQuestQuests}");
#endif
            }
            catch (System.Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"Dismiss: Fail threw: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
