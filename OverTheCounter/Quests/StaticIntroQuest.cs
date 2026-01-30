using MelonLoader;
using S1API.Quests;
using S1API.Quests.Constants;
using S1API.Saveables;
using System;
using System.Reflection;
using UnityEngine;

namespace OverTheCounter.Quests
{
    public class StaticIntroQuest : Quest
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("StaticIntroQuest");

        protected override string Title => "Crimeware as a Service";
        protected override string Description => "Someone at the casino noticed your deposits. Find Static after 4 PM when the casino opens.";
        protected override bool AutoBegin => false;

        [SaveableField("static_quest_stage")]
        private int _stage; // 0=not started, 1=obj1 active (talk to Static), 2=done

        private QuestEntry _talkToStaticEntry;

        public static StaticIntroQuest Instance { get; private set; }

        public int Stage => _stage;

        private static readonly Vector3 StaticPosition = new Vector3(13.72f, 5.16f, 95.96f);

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

        public void Initialize()
        {
            try
            {
                TriggerInternalInit();

                _talkToStaticEntry = AddEntry("Talk to Static in the casino after 4 PM", StaticPosition);
            }
            catch (Exception ex)
            {
                Logger.Error($"Initialize failed: {ex.Message}");
            }
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
                Logger.Error($"StartQuest failed: {ex.Message}");
            }
        }

        public void CompleteObj1()
        {
            try
            {
                _stage = 2;
                _talkToStaticEntry?.Complete();
            }
            catch (Exception ex)
            {
                Logger.Error($"CompleteObj1 failed: {ex.Message}");
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

            if (QuestEntries.Count >= 1)
            {
                _talkToStaticEntry = QuestEntries[0];
            }

            try
            {
                if (_stage >= 2 && _talkToStaticEntry != null
                    && _talkToStaticEntry.State != QuestState.Completed)
                {
                    _talkToStaticEntry.Complete();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"OnLoaded safety-sync failed: {ex.Message}");
            }
        }
    }
}
