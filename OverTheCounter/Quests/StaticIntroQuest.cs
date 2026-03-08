using MelonLoader.Utils;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.Quests;
using S1API.Quests.Constants;
using S1API.Saveables;
using S1API.Utils;
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace OverTheCounter.Quests
{
    public class StaticIntroQuest : Quest
    {
        protected override string Title => "Crimeware as a Service";
        protected override string Description => "Someone at the casino noticed your deposits. Find Static after 4 PM when the casino opens.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => ImageUtils.LoadImage(
            Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "CrimeWareQuest.png"));

        [SaveableField("static_quest_stage")]
        private int _stage; // 0=not started, 1=talk to Static, 2=bring supplies, 3=done

        private QuestEntry _talkToStaticEntry;
        private QuestEntry _bringSuppliesEntry;

        public static StaticIntroQuest Instance { get; private set; }
        internal static void ResetInstance() => Instance = null;

        public int Stage => _stage;

        private static readonly Vector3 StaticPosition = new Vector3(13.72f, 5.16f, 95.96f);

        private void TriggerInternalInit()
        {
            try
            {
                var s1QuestField = typeof(Quest).GetField("S1Quest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (s1QuestField == null) return;

                var s1Quest = s1QuestField.GetValue(this) as ScheduleOne.Quests.Quest;
                if (s1Quest == null) return;

                s1Quest.InitializeQuest(Title, Description, Array.Empty<ScheduleOne.Persistence.Datas.QuestEntryData>(), s1Quest.StaticGUID);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"TriggerInternalInit failed: {ex.Message}");
            }
        }

        public void Initialize()
        {
            try
            {
                TriggerInternalInit();

                _talkToStaticEntry = AddEntry("Talk to Static in the casino after 4 PM", StaticPosition);
                _bringSuppliesEntry = AddEntry(GetSuppliesText(), StaticPosition);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"Initialize failed: {ex.Message}");
            }
        }

        private static string GetSuppliesText() =>
            $"Bring Static ${Config.StaticTier1BankCost.Value:N0} and {Config.StaticTier1WeedGrams.Value} grams of weed";

        public void RefreshEntryText()
        {
            if (_bringSuppliesEntry != null && _stage >= 1 && _stage < 3)
                _bringSuppliesEntry.Title = GetSuppliesText();
        }

        public void StartQuest()
        {
            try
            {
                _stage = 1;
                Begin();
                _talkToStaticEntry?.Begin();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StartQuest failed: {ex.Message}");
            }
        }

        public void CompleteObj1()
        {
            try
            {
                _stage = 2;
                _talkToStaticEntry?.Complete();
                _bringSuppliesEntry?.Begin();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"CompleteObj1 failed: {ex.Message}");
            }
        }

        public void CompleteObj2()
        {
            try
            {
                _stage = 3;
                _bringSuppliesEntry?.Complete();
            }
            catch (Exception ex)
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
                // StaticSaveData is the authority — host state may have advanced
                // via SyncVar before this quest's save was loaded.
                if (StaticSaveData.Instance != null)
                {
                    int hostStage = StaticSaveData.Instance.CrmTier >= 1 ? 3
                        : StaticSaveData.Instance.IntroCompleted ? 2
                        : StaticSaveData.Instance.QuestTriggered ? 1 : 0;
                    if (hostStage > _stage)
                        _stage = hostStage;
                }

                // Entries aren't restored from save — rebuild them
                QuestEntries.Clear();
                _talkToStaticEntry = AddEntry("Talk to Static in the casino after 4 PM", StaticPosition);
                _bringSuppliesEntry = AddEntry(GetSuppliesText(), StaticPosition);

                // Restore entry states based on saved stage
                if (_stage >= 1)
                    _talkToStaticEntry?.Begin();
                if (_stage >= 2)
                {
                    _talkToStaticEntry?.Complete();
                    _bringSuppliesEntry?.Begin();
                }
                if (_stage >= 3)
                    _bringSuppliesEntry?.Complete();
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"OnLoaded rebuild failed: {ex.Message}");
            }
        }
    }
}
