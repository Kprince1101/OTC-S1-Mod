using MelonLoader;
using OverTheCounter.Utilities;
using S1API.Quests;
using S1API.GameTime;
using System.Collections.Generic;
using System.Reflection;

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

        private Il2CppScheduleOne.Quests.Quest GetS1Quest()
        {
            var field = typeof(Quest).GetField("S1Quest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(this) as Il2CppScheduleOne.Quests.Quest;
        }

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
                s1Quest.InitializeQuest(Title, Description, System.Array.Empty<Il2CppScheduleOne.Persistence.Datas.QuestEntryData>(), s1Quest.StaticGUID);
            }
            catch (System.Exception ex)
            {
                Melon<Core>.Logger.Error($"[ConsolidatedQuest] TriggerInternalInit failed: {ex.Message}");
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
            }
            catch (System.Exception ex)
            {
                Melon<Core>.Logger.Error($"[ConsolidatedQuest] Initialize failed: {ex.Message}");
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
        /// Uses QuestEntries to show each product as a separate line.
        /// </summary>
        /// <param name="deliveryCount">Number of pending deliveries</param>
        /// <param name="productSummaries">List of products and their quantities</param>
        /// <param name="windowStart">Window start time (game time, e.g. 600 = 10:00)</param>
        /// <param name="windowEnd">Window end time (game time, e.g. 1200 = 20:00)</param>
        public void UpdateSummary(int deliveryCount, List<FormatUtils.ProductSummary> productSummaries, int windowStart, int windowEnd)
        {
            // Ensure initialized
            if (!_initialized)
            {
                Initialize();
            }

            // Update count for title
            _currentCount = deliveryCount;
            UpdateTitle();

            // Clear existing entries and add new ones
            try
            {
                // Clear both the wrapper list AND the game's internal entries list
                ClearAllEntries();

                // Add an entry for each product and activate it
                foreach (var summary in productSummaries)
                {
                    var entry = AddEntry($"{summary.Quantity}x {summary.DisplayName}");
                    entry.Begin();
                }

                // Update description for journal
                var descParts = new List<string>();
                foreach (var summary in productSummaries)
                {
                    descParts.Add($"{summary.Quantity}x {summary.DisplayName}");
                }
                _description = descParts.Count > 0
                    ? "Products needed:\n" + string.Join("\n", descParts)
                    : "No products pending";

                // Update timing subtitle
                if (windowStart >= 0 && windowEnd >= 0)
                {
                    UpdateTiming(windowStart, windowEnd);
                }
            }
            catch (System.Exception ex)
            {
                Melon<Core>.Logger.Error($"[ConsolidatedQuest] UpdateSummary failed: {ex.Message}");
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
                Melon<Core>.Logger.Warning($"[ConsolidatedQuest] UpdateTiming failed: {ex.Message}");
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
        /// Updates the quest title on the underlying S1Quest.
        /// Needed because the game caches the title at initialization time.
        /// </summary>
        private void UpdateTitle()
        {
            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null) return;

                s1Quest.InitializeQuest(Title, _description,
                    System.Array.Empty<Il2CppScheduleOne.Persistence.Datas.QuestEntryData>(),
                    s1Quest.StaticGUID);
            }
            catch (System.Exception ex)
            {
                Melon<Core>.Logger.Warning($"[ConsolidatedQuest] UpdateTitle failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the quest subtitle via reflection on the underlying S1Quest.
        /// </summary>
        private void SetSubtitleViaReflection(string subtitle)
        {
            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null) return;

                // Call SetSubtitle on the game's Quest object
                s1Quest.SetSubtitle(subtitle);
            }
            catch (System.Exception ex)
            {
                Melon<Core>.Logger.Warning($"[ConsolidatedQuest] SetSubtitleViaReflection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears all quest entries from both the wrapper list and the game's internal list.
        /// </summary>
        private void ClearAllEntries()
        {
            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null)
                {
                    QuestEntries.Clear();
                    return;
                }

                if (s1Quest.Entries == null)
                {
                    QuestEntries.Clear();
                    return;
                }

                // Destroy each entry's GameObject and remove from list
                int count = s1Quest.Entries.Count;
                for (int i = count - 1; i >= 0; i--)
                {
                    var entry = s1Quest.Entries[i];
                    if (entry != null)
                    {
                        // Destroy the entry's UI if it exists
                        if (entry.entryUI != null && entry.entryUI.gameObject != null)
                        {
                            UnityEngine.Object.Destroy(entry.entryUI.gameObject);
                        }
                        // Destroy the entry's GameObject
                        if (entry.gameObject != null)
                        {
                            UnityEngine.Object.Destroy(entry.gameObject);
                        }
                    }
                }
                s1Quest.Entries.Clear();

                // Clear the S1API wrapper list
                QuestEntries.Clear();
            }
            catch (System.Exception ex)
            {
                Melon<Core>.Logger.Warning($"[ConsolidatedQuest] ClearAllEntries failed: {ex.Message}");
                // Still try to clear the wrapper list
                try { QuestEntries.Clear(); } catch { }
            }
        }

        /// <summary>
        /// Helper to cleanly remove the quest when we switch back to normal mode.
        /// </summary>
        public void Dismiss()
        {
            try
            {
                this.Fail();
            }
            catch
            {
                // Quest may not have been fully initialized, ignore
            }
        }
    }
}
