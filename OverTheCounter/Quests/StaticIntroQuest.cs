using MelonLoader;
using MelonLoader.Utils;
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
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("StaticIntroQuest");

        protected override string Title => "Crimeware as a Service";
        protected override string Description => "Someone at the casino noticed your deposits. Find Static after 4 PM when the casino opens.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => ImageUtils.LoadImage(
            Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "RinseCycle.png"));

        [SaveableField("static_quest_stage")]
        private int _stage; // 0=not started, 1=talk to Static, 2=bring supplies, 3=done

        private QuestEntry _talkToStaticEntry;
        private QuestEntry _bringSuppliesEntry;

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
                _bringSuppliesEntry = AddEntry("Bring Static $3,000 and 20 grams of weed", StaticPosition);
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
                _bringSuppliesEntry?.Begin();
            }
            catch (Exception ex)
            {
                Logger.Error($"CompleteObj1 failed: {ex.Message}");
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
                Logger.Error($"CompleteObj2 failed: {ex.Message}");
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
                // Entries aren't restored from save — rebuild them
                QuestEntries.Clear();
                _talkToStaticEntry = AddEntry("Talk to Static in the casino after 4 PM", StaticPosition);
                _bringSuppliesEntry = AddEntry("Bring Static $3,000 and 20 grams of weed", StaticPosition);

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
                Logger.Warning($"OnLoaded rebuild failed: {ex.Message}");
            }
        }
    }
}
