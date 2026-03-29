using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.Quests;
using S1API.Saveables;
using System;
using OverTheCounter.Logic.Placement;
using UnityEngine;

namespace OverTheCounter.Quests
{
    public class StaticUpgrade2Quest : Quest
    {
        protected override string Title => "Full Scale";
        protected override string Description => "Static has the final tier-3 enterprise upgrade available. Pay and deliver via the OTC app.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => QuestIconHelper.Load();

        [SaveableField("static_upgrade2_stage")]
        private int _stage; // 0=not started, 1=pay+drop, 2=done

        private QuestEntry _payEntry;
        private QuestEntry _dropOffEntry;

        public static StaticUpgrade2Quest Instance { get; private set; }
        internal static void ResetInstance() => Instance = null;

        public int Stage => _stage;


        public void Initialize()
        {
            try
            {
                _payEntry = AddEntry(GetPayText());
                _dropOffEntry = AddEntry(GetDropOffText(), CasinoDeadDrop.Position);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"Initialize failed: {ex.Message}");
            }
        }

        private static string GetPayText() =>
            $"Pay ${Config.StaticTier3BankCost.Value:N0} via OTC app";

        private static string GetDropOffText() =>
            $"Drop off {Config.StaticTier3PremiumMethGrams.Value}g premium meth at the casino dead drop";

        public void RefreshEntryText()
        {
            if (_payEntry != null && _stage >= 1 && _stage < 2)
                _payEntry.Title = GetPayText();
            if (_dropOffEntry != null && _stage >= 1 && _stage < 2)
                _dropOffEntry.Title = GetDropOffText();
        }

        public void StartQuest()
        {
            try
            {
                _stage = 1;
                Begin();
                _payEntry?.Begin();
                _dropOffEntry?.Begin();
                QuestPoiFixer.FixPosition(_dropOffEntry, CasinoDeadDrop.Position);
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
                _payEntry?.Begin();
                _payEntry?.Complete();
                _dropOffEntry?.Begin();
                _dropOffEntry?.Complete();
                Complete();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"CompleteObj1 failed: {ex.Message}");
            }
        }

        public void CompletePay()
        {
            _payEntry?.Begin();
            _payEntry?.Complete();
            _dropOffEntry?.Begin(); // Now show dead drop marker
        }

        public void CompleteDropOff()
        {
            _dropOffEntry?.Begin();
            _dropOffEntry?.Complete();
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
                QuestEntries.Clear();
                _payEntry = AddEntry(GetPayText());
                _dropOffEntry = AddEntry(GetDropOffText(), CasinoDeadDrop.Position);

                if (_stage >= 1)
                {
                    _payEntry?.Begin();
                    _dropOffEntry?.Begin();
                    QuestPoiFixer.FixPosition(_dropOffEntry, CasinoDeadDrop.Position);
                }

                if (_stage >= 1 && _stage < 2 && StaticSaveData.Instance != null)
                {
                    if (StaticSaveData.Instance.UpgradeMoneyPaid)
                        CompletePay(); // Also begins _dropOffEntry
                    if (StaticSaveData.Instance.UpgradeProductDelivered)
                        CompleteDropOff();
                }

                if (_stage >= 2)
                {
                    _payEntry?.Complete();
                    _dropOffEntry?.Complete();
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
