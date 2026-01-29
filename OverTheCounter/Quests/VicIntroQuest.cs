using MelonLoader;
using MelonLoader.Utils;
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
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("VicIntroQuest");

        protected override string Title => "Rinse Cycle";
        protected override string Description => "Help Vic with his party supplies and he'll loosen your deposit limits.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => ImageUtils.LoadImage(
            Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "RinseCycle.png"));

        [SaveableField("vic_quest_stage")]
        private int _stage; // 0=not started, 1=obj1 (meet Vic), 2=obj2 (bring weed), 3=done

        private QuestEntry _meetVicEntry;
        private QuestEntry _bringWeedEntry;

        public static VicIntroQuest Instance { get; private set; }

        public int Stage => _stage;

        private static readonly Vector3 VicPosition = new Vector3(72.08f, 0.97f, 31.71f);

        private void TriggerInternalInit()
        {
            try
            {
                var s1QuestField = typeof(Quest).GetField("S1Quest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (s1QuestField == null) return;

                var s1Quest = s1QuestField.GetValue(this) as Il2CppScheduleOne.Quests.Quest;
                if (s1Quest == null) return;

                s1Quest.InitializeQuest(Title, Description, System.Array.Empty<Il2CppScheduleOne.Persistence.Datas.QuestEntryData>(), s1Quest.StaticGUID);
            }
            catch (System.Exception ex)
            {
                Logger.Error($"TriggerInternalInit failed: {ex.Message}");
            }
        }

        public void Initialize()
        {
            try
            {
                TriggerInternalInit();

                _meetVicEntry = AddEntry("Meet Vic in the alleyway behind the bank", VicPosition);
                _bringWeedEntry = AddEntry("Bring Vic 40 grams of weed", VicPosition);
            }
            catch (System.Exception ex)
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
                _meetVicEntry?.Begin();
            }
            catch (System.Exception ex)
            {
                Logger.Error($"StartQuest failed: {ex.Message}");
            }
        }

        public void CompleteObj1()
        {
            try
            {
                _stage = 2;
                _meetVicEntry?.Complete();
                _bringWeedEntry?.Begin();
            }
            catch (System.Exception ex)
            {
                Logger.Error($"CompleteObj1 failed: {ex.Message}");
            }
        }

        public void CompleteObj2()
        {
            try
            {
                _stage = 3;
                _bringWeedEntry?.Complete();
            }
            catch (System.Exception ex)
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

            // Reconnect entry references from the persisted QuestEntries list
            if (QuestEntries.Count >= 2)
            {
                _meetVicEntry = QuestEntries[0];
                _bringWeedEntry = QuestEntries[1];
            }
            else if (QuestEntries.Count == 1)
            {
                _meetVicEntry = QuestEntries[0];
            }

            // Safety-sync entry states with saved stage
            try
            {
                if (_stage >= 2 && _meetVicEntry != null
                    && _meetVicEntry.State != S1API.Quests.Constants.QuestState.Completed)
                {
                    _meetVicEntry.Complete();
                }

                if (_stage == 2 && _bringWeedEntry != null
                    && _bringWeedEntry.State == S1API.Quests.Constants.QuestState.Inactive)
                {
                    _bringWeedEntry.Begin();
                }

                if (_stage >= 3 && _bringWeedEntry != null
                    && _bringWeedEntry.State != S1API.Quests.Constants.QuestState.Completed)
                {
                    _bringWeedEntry.Complete();
                }
            }
            catch (System.Exception ex)
            {
                Logger.Warning($"OnLoaded safety-sync failed: {ex.Message}");
            }
        }
    }
}
