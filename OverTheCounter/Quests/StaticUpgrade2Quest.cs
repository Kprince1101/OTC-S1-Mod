using MelonLoader.Utils;
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
    public class StaticUpgrade2Quest : Quest
    {
        protected override string Title => "Full Scale";
        protected override string Description => "Static has the final tier-3 enterprise upgrade available. Bring premium meth and go all in.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => ImageUtils.LoadImage(
            Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "CrimeWareQuest.png"));

        [SaveableField("static_upgrade2_stage")]
        private int _stage; // 0=not started, 1=active, 2=done

        private QuestEntry _bringSuppliesEntry;

        public static StaticUpgrade2Quest Instance { get; private set; }
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

                _bringSuppliesEntry = AddEntry(GetSuppliesText(), StaticPosition);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"Initialize failed: {ex.Message}");
            }
        }

        private static string GetSuppliesText() =>
            $"Bring Static ${Config.StaticTier3BankCost.Value:N0} and {Config.StaticTier3PremiumMethGrams.Value} grams of premium meth";

        public void RefreshEntryText()
        {
            if (_bringSuppliesEntry != null && _stage >= 1 && _stage < 2)
                _bringSuppliesEntry.Title = GetSuppliesText();
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
                OTCLog.Error(OTCLog.Systems.Quest, $"StartQuest failed: {ex.Message}");
            }
        }

        public void CompleteObj1()
        {
            try
            {
                _stage = 2;
                _bringSuppliesEntry?.Begin();
                _bringSuppliesEntry?.Complete();
                Complete();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"CompleteObj1 failed: {ex.Message}");
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
                _bringSuppliesEntry = AddEntry(GetSuppliesText(), StaticPosition);

                // Restore entry states based on saved stage
                if (_stage >= 1)
                    _bringSuppliesEntry?.Begin();
                if (_stage >= 2)
                {
                    _bringSuppliesEntry?.Complete();
                    Complete();
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"OnLoaded rebuild failed: {ex.Message}");
            }
        }
    }
}
