using MelonLoader;
using MelonLoader.Utils;
using S1API.Quests;
using S1API.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace OverTheCounter.Quests
{
    public class DrifterDealQuest : Quest
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("DrifterDealQuest");

        protected override string Title => _title ?? "Drifter Deal";
        protected override string Description => _description ?? "Complete a deal with a drifter.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => ImageUtils.LoadImage(
            Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "RinseCycle.png"));

        // Dynamic fields (not saved - drifter quests are ephemeral)
        private string _title;
        private string _description;
        private QuestEntry _deliverEntry;

        // Track active quest per drifter
        public string DrifterId { get; private set; }
        public static Dictionary<string, DrifterDealQuest> ActiveQuests { get; } = new();

        private void TriggerInternalInit()
        {
            try
            {
                var s1QuestField = typeof(Quest).GetField("S1Quest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (s1QuestField == null) return;

                var s1Quest = s1QuestField.GetValue(this) as Il2CppScheduleOne.Quests.Quest;
                if (s1Quest == null) return;

                s1Quest.InitializeQuest(Title, Description, Array.Empty<Il2CppScheduleOne.Persistence.Datas.QuestEntryData>(), s1Quest.StaticGUID);
            }
            catch (Exception ex)
            {
                Logger.Error($"TriggerInternalInit failed: {ex.Message}");
            }
        }

        public void Initialize(string drifterId, string productName, int quantity,
                              float payment, Vector3 destination, string locationDesc)
        {
            DrifterId = drifterId;
            _title = "Drifter Deal";
            _description = $"Deliver {quantity} {productName} for ${payment:F0}";

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

        public void CompleteDeal()
        {
            try
            {
                _deliverEntry?.Complete();
                Complete();
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
