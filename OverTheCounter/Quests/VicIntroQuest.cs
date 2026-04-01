using MelonLoader.Utils;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.Quests;
using S1API.Saveables;
using S1API.Utils;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace OverTheCounter.Quests
{
    public class VicIntroQuest : Quest
    {
        protected override string Title => "Rinse Cycle";
        protected override string Description => "Help Vic with his party supplies and he'll help you clean some cash.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => Core.OtcIconDir != null
            ? ImageUtils.LoadImage(Path.Combine(Core.OtcIconDir, "RinseCycle.png"))
            : null;

        [SaveableField("vic_quest_stage")]
        private int _stage; // 0=not started, 1=obj1 (meet Vic), 2=obj2 (bring weed), 3=done

        private QuestEntry _meetVicEntry;
        private QuestEntry _bringWeedEntry;

        public static VicIntroQuest Instance { get; private set; }
        internal static void ResetInstance() => Instance = null;

        public int Stage => _stage;

        private static readonly Vector3 VicPosition = new Vector3(67.75f, 0.97f, 32.36f);

        private void TriggerInternalInit()
        {
            try
            {
                var s1QuestField = typeof(Quest).GetField("S1Quest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (s1QuestField == null) return;

                var s1Quest = s1QuestField.GetValue(this) as ScheduleOne.Quests.Quest;
                if (s1Quest == null) return;

                s1Quest.InitializeQuest(Title, Description, System.Array.Empty<ScheduleOne.Persistence.Datas.QuestEntryData>(), s1Quest.StaticGUID);
            }
            catch (System.Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"TriggerInternalInit failed: {ex.Message}");
            }
        }

        public void Initialize()
        {
            try
            {
                TriggerInternalInit();

                _meetVicEntry = AddEntry("Meet Vic in the alleyway behind the bank", VicPosition);
                _bringWeedEntry = AddEntry(GetWeedText(), VicPosition);
            }
            catch (System.Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"Initialize failed: {ex.Message}");
            }
        }

        private static string GetWeedText() =>
            $"Bring Vic {Config.VicIntroWeedGrams.Value} grams of weed";

        public void RefreshEntryText()
        {
            if (_bringWeedEntry != null && _stage >= 1 && _stage < 3)
                _bringWeedEntry.Title = GetWeedText();
        }

        public void StartQuest()
        {
            try
            {
                _stage = 1;
                Begin();
                _meetVicEntry?.Begin();
            }
            catch (System.Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StartQuest failed: {ex.Message}");
            }
        }

        public void CompleteObj1()
        {
            try
            {
                _stage = 2;
                _meetVicEntry?.Begin();
                _meetVicEntry?.Complete();
                _bringWeedEntry?.Begin();
            }
            catch (System.Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"CompleteObj1 failed: {ex.Message}");
            }
        }

        public void CompleteObj2()
        {
            try
            {
                _stage = 3;
                _bringWeedEntry?.Begin();
                _bringWeedEntry?.Complete();
                Complete();
            }
            catch (System.Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"CompleteObj2 failed: {ex.Message}");
            }
        }

        protected override void OnCreated()
        {
            base.OnCreated();
            Instance = this;
        }

        protected override void OnLoaded()
        {
            base.OnLoaded();
            Instance = this;

            try
            {
                // VicSaveData is the authority — host state may have advanced
                // via SyncVar before this quest's save was loaded.
                if (VicSaveData.Instance != null)
                {
                    int hostStage = VicSaveData.Instance.Unlocked ? 3
                        : VicSaveData.Instance.QuestAccepted ? 2
                        : VicSaveData.Instance.HasBeenTexted ? 1 : 0;
                    if (hostStage > _stage)
                        _stage = hostStage;
                }

                // Entries aren't restored from save — rebuild them
                QuestEntries.Clear();
                _meetVicEntry = AddEntry("Meet Vic in the alleyway behind the bank", VicPosition);
                _bringWeedEntry = AddEntry(GetWeedText(), VicPosition);

                // Restore entry states based on saved stage
                if (_stage >= 1)
                    _meetVicEntry?.Begin();
                if (_stage >= 2)
                {
                    _meetVicEntry?.Complete();
                    _bringWeedEntry?.Begin();
                }
                if (_stage >= 3)
                {
                    _bringWeedEntry?.Complete();
                    Complete();
                }
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"OnLoaded rebuild failed: {ex.Message}");
            }
        }
    }
}
