using MelonLoader;
using MelonLoader.Utils;
using S1API.Quests;
using S1API.GameTime;
using S1API.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace OverTheCounter.Quests
{
    public class DrifterDealQuest : Quest
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:DrifterDealQuest");

        protected override string Title => _title ?? "Drifter Deal";
        protected override string Description => _description ?? "Complete a deal with a drifter.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => ImageUtils.LoadImage(
            Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "DrifterQuestIcon.png"));

        // Dynamic fields (not saved - drifter quests are ephemeral)
        private string _title;
        private string _description;
        private QuestEntry _deliverEntry;

        // Timing: deadline in elapsed minutes (same format as DrifterManager)
        private int _deadlineElapsedMinutes;

        // Track active quest per drifter
        public string DrifterId { get; private set; }
        public static Dictionary<string, DrifterDealQuest> ActiveQuests { get; } = new();

        private Il2CppScheduleOne.Quests.Quest GetS1Quest()
        {
            var field = typeof(Quest).GetField("S1Quest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(this) as Il2CppScheduleOne.Quests.Quest;
        }

        private void TriggerInternalInit()
        {
            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null) return;

                s1Quest.InitializeQuest(Title, Description, Array.Empty<Il2CppScheduleOne.Persistence.Datas.QuestEntryData>(), s1Quest.StaticGUID);
            }
            catch (Exception ex)
            {
                Logger.Error($"TriggerInternalInit failed: {ex.Message}");
            }
        }

        public void Initialize(string drifterId, string productName, int quantity,
                              float payment, Vector3 destination, string locationDesc,
                              int deadlineElapsedMinutes)
        {
            DrifterId = drifterId;
            _title = "Drifter Deal";
            _description = $"Deliver {quantity} {productName} for ${payment:F0}";
            _deadlineElapsedMinutes = deadlineElapsedMinutes;

            TriggerInternalInit();

            _deliverEntry = AddEntry($"Deliver {quantity} {productName} to the drifter {locationDesc}", destination);

            ActiveQuests[drifterId] = this;
        }

        public void StartQuest()
        {
            try
            {
                Begin();
                _deliverEntry?.Begin();
            }
            catch (Exception ex)
            {
                Logger.Error($"StartQuest failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the timing subtitle. Call this from the lifecycle tick.
        /// Shows "Expires in X hrs" (green) or "Expires in X min" (red when < 2 hrs).
        /// </summary>
        public void UpdateTiming()
        {
            try
            {
                if (_deadlineElapsedMinutes <= 0) return;

                int currentElapsed = GetCurrentElapsedMinutes();
                int minsRemaining = _deadlineElapsedMinutes - currentElapsed;

                if (minsRemaining < 0) minsRemaining = 0;

                string subtitle;
                int hours = minsRemaining / 60;
                int mins = minsRemaining % 60;

                if (minsRemaining < 120)
                {
                    // Urgent - less than 2 hours
                    if (hours > 0)
                        subtitle = $"<color=#ff6b6b> (Expires in {hours}h {mins}m)</color>";
                    else
                        subtitle = $"<color=#ff6b6b> (Expires in {minsRemaining} min)</color>";
                }
                else
                {
                    // Normal - show in green
                    subtitle = $"<color=green> (Expires in {hours}h {mins}m)</color>";
                }

                SetSubtitleViaReflection(subtitle);
            }
            catch (Exception ex)
            {
                Logger.Warning($"UpdateTiming failed: {ex.Message}");
            }
        }

        private void SetSubtitleViaReflection(string subtitle)
        {
            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null) return;

                s1Quest.SetSubtitle(subtitle);

                if (s1Quest.hudUI != null)
                    s1Quest.hudUI.UpdateMainLabel();
            }
            catch (Exception ex)
            {
                Logger.Warning($"SetSubtitleViaReflection failed: {ex.Message}");
            }
        }

        private static int GetCurrentElapsedMinutes()
        {
            int days = TimeManager.ElapsedDays;
            int time24h = TimeManager.CurrentTime;
            int hours = time24h / 100;
            int minutes = time24h % 100;
            return (days * 1440) + (hours * 60) + minutes;
        }

        public void CompleteDeal()
        {
            try
            {
                _deliverEntry?.Complete();
                Complete();
                // Explicitly end the quest to ensure HUD and journal entry are destroyed.
                // IL2CPP Complete() may not reliably call End() internally.
                End();
                ActiveQuests.Remove(DrifterId);
            }
            catch (Exception ex)
            {
                Logger.Error($"CompleteDeal failed: {ex.Message}");
            }
        }

        public void FailDeal()
        {
            try
            {
                _deliverEntry?.Complete();
                Fail();
                ActiveQuests.Remove(DrifterId);
            }
            catch (Exception ex)
            {
                Logger.Error($"FailDeal failed: {ex.Message}");
            }
        }

        public void CancelDeal()
        {
            try
            {
                Cancel();
                ActiveQuests.Remove(DrifterId);
            }
            catch (Exception ex)
            {
                Logger.Error($"CancelDeal failed: {ex.Message}");
            }
        }

    }
}
