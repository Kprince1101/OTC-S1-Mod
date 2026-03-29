using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.Quests;
using System;

namespace OverTheCounter.Quests
{
    public class DispensaryAlertQuest : BuildingAlertQuest
    {
        protected override string Title => "Big Dispensary";
        protected override string BuildingId => PropertySaveData.DispensaryId;

        public static DispensaryAlertQuest Instance { get; private set; }

        protected override void SetInstance() => Instance = this;
        protected override void ClearInstance() => Instance = null;

        public static void EnsureExists()
        {
            if (Instance != null) return;

            try
            {
                var quest = (DispensaryAlertQuest)QuestManager.CreateQuest<DispensaryAlertQuest>();
                if (quest == null)
                {
                    OTCLog.Error(OTCLog.Systems.Quest, "CreateQuest<DispensaryAlertQuest> returned null");
                    return;
                }

                quest.InitAndBegin();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"DispensaryAlertQuest EnsureExists failed: {ex.Message}");
            }
        }
    }
}
