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
    public class StaticUpgrade1Quest : Quest
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("StaticUpgrade1Quest");

        protected override string Title => "Premium Tier";
        protected override string Description => "Static has the premium tier upgrade available. Check your texts and bring him what he needs.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => ImageUtils.LoadImage(
            Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "RinseCycle.png"));

        [SaveableField("static_upgrade1_stage")]
        private int _stage; // 0=not started, 1=active, 2=done

        private QuestEntry _bringSuppliesEntry;

        public static StaticUpgrade1Quest Instance { get; private set; }

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

                _bringSuppliesEntry = AddEntry("Bring Static $6,000 and 5 grams of meth", StaticPosition);
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
                _bringSuppliesEntry?.Begin();
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
                _bringSuppliesEntry?.Complete();
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

            try
            {
                // Entries aren't restored from save — rebuild them
                QuestEntries.Clear();
                _bringSuppliesEntry = AddEntry("Bring Static $6,000 and 5 grams of meth", StaticPosition);

                // Restore entry states based on saved stage
                if (_stage >= 1)
                    _bringSuppliesEntry?.Begin();
                if (_stage >= 2)
                    _bringSuppliesEntry?.Complete();
            }
            catch (Exception ex)
            {
                Logger.Warning($"OnLoaded rebuild failed: {ex.Message}");
            }
        }
    }
}
