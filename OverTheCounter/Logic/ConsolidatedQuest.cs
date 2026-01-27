using MelonLoader;
using S1API.Quests;
using System.Reflection;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// A custom Quest that acts as a container for our summary data.
    /// Uses S1API to render natively in the game's phone/HUD.
    /// </summary>
    public class ConsolidatedQuest : Quest
    {
        // Dynamic title backing field
        private string _title = "Deliveries Pending";
        private string _description = "Loading...";

        // The title that appears in the quest log (dynamic)
        protected override string Title => _title;

        // The description showing what's owed
        protected override string Description => _description;

        // Auto-begin when created (we control creation timing in NotificationManager)
        protected override bool AutoBegin => true;

        // Reference to the objective entry for the product breakdown
        private QuestEntry _productEntry;

        // Flag to track if we've initialized
        private bool _initialized = false;

        /// <summary>
        /// Manually trigger internal initialization that QuestManager should have done.
        /// This is a workaround for QuestManager.CreateQuest not calling CreateInternal.
        /// </summary>
        private void TriggerInternalInit()
        {
            try
            {
                // Get the internal S1Quest field via reflection
                var s1QuestField = typeof(Quest).GetField("S1Quest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (s1QuestField == null) return;

                var s1Quest = s1QuestField.GetValue(this) as Il2CppScheduleOne.Quests.Quest;
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

                // Create entry for product list
                _productEntry = AddEntry("Calculating...");

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
        /// </summary>
        /// <param name="deliveryCount">Number of pending deliveries</param>
        /// <param name="productBreakdown">Formatted string of products owed</param>
        public void UpdateSummary(int deliveryCount, string productBreakdown)
        {
            // Ensure initialized
            if (!_initialized)
            {
                Initialize();
            }

            // Update the title with delivery count
            _title = $"{deliveryCount} Deliveries Pending";

            // Update the entry with product breakdown
            if (_productEntry != null)
            {
                _productEntry.Title = productBreakdown;
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
