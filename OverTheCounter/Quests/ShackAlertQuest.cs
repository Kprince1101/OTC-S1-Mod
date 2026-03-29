using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.Quests;
using System;

namespace OverTheCounter.Quests
{
    public class ShackAlertQuest : BuildingAlertQuest
    {
        protected override string Title => "Westville Shack";
        protected override string BuildingId => PropertySaveData.ShackId;

        public static ShackAlertQuest Instance { get; private set; }

        protected override void SetInstance() => Instance = this;
        protected override void ClearInstance() => Instance = null;

        public static void EnsureExists()
        {
            if (Instance != null) return;

            try
            {
                var quest = (ShackAlertQuest)QuestManager.CreateQuest<ShackAlertQuest>();
                if (quest == null)
                {
                    OTCLog.Error(OTCLog.Systems.Quest, "CreateQuest<ShackAlertQuest> returned null");
                    return;
                }

                quest.InitAndBegin();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"ShackAlertQuest EnsureExists failed: {ex.Message}");
            }
        }
    }
}
