using MelonLoader.Utils;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.Quests;
using S1API.Saveables;
using S1API.Utils;
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace OverTheCounter.Quests
{
    public class BellaProtocolQuest : Quest
    {
        protected override string Title => "Executive Privilege";
        protected override string Description => "Someone at the Fixer's mentioned a contact who can help with warehouse access.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => ImageUtils.LoadImage(
            Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "ExecutivePrivilege.png"));

        [SaveableField("bella_quest_stage")]
        private int _stage; // 0=not started, 1=visit Bella, 2=bring weed, 3=bring meth, 4=bring coke, 5=done

        private QuestEntry _visitEntry;
        private QuestEntry _weedEntry;
        private QuestEntry _methEntry;
        private QuestEntry _cokeEntry;

        public static BellaProtocolQuest Instance { get; private set; }
        internal static void ResetInstance() => Instance = null;

        public int Stage => _stage;

        private static readonly Vector3 BellaBuilding = new Vector3(74.1f, 1.0f, 57.2f);

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

                _visitEntry = AddEntry("Visit Bella at the downtown apartment", BellaBuilding);
                _weedEntry = AddEntry(GetWeedText(), BellaBuilding);
                _methEntry = AddEntry(GetMethText(), BellaBuilding);
                _cokeEntry = AddEntry(GetCokeText(), BellaBuilding);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"Initialize failed: {ex.Message}");
            }
        }

        private static string GetWeedText() =>
            $"Bring Bella a weed mix worth ${Config.BellaWeedValue.Value:N0}+";

        private static string GetMethText() =>
            $"Bring Bella a meth mix worth ${Config.BellaMethValue.Value:N0}+";

        private static string GetCokeText() =>
            $"Bring Bella a cocaine mix worth ${Config.BellaCokeValue.Value:N0}+";

        public void RefreshEntryText()
        {
            if (_weedEntry != null && _stage == 2)
                _weedEntry.Title = GetWeedText();
            if (_methEntry != null && _stage == 3)
                _methEntry.Title = GetMethText();
            if (_cokeEntry != null && _stage == 4)
                _cokeEntry.Title = GetCokeText();
        }

        public void StartQuest()
        {
            try
            {
                _stage = 1;
                Begin();
                _visitEntry?.Begin();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StartQuest failed: {ex.Message}");
            }
        }

        public void AdvanceToWeedRequest()
        {
            try
            {
                _stage = 2;
                _visitEntry?.Complete();
                _weedEntry?.Begin();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"AdvanceToWeedRequest failed: {ex.Message}");
            }
        }

        public void AdvanceToMethRequest()
        {
            try
            {
                _stage = 3;
                _weedEntry?.Complete();
                _methEntry?.Begin();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"AdvanceToMethRequest failed: {ex.Message}");
            }
        }

        public void AdvanceToCocaineRequest()
        {
            try
            {
                _stage = 4;
                _methEntry?.Complete();
                _cokeEntry?.Begin();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"AdvanceToCocaineRequest failed: {ex.Message}");
            }
        }

        public void CompleteQuest()
        {
            try
            {
                _stage = 5;
                _cokeEntry?.Complete();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"CompleteQuest failed: {ex.Message}");
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
                int saveStage = _stage;
                int bellaStage = BellaSaveData.Instance?.Stage ?? -1;
                OTCLog.Msg(OTCLog.Systems.Quest, $"OnLoaded: save _stage={saveStage}, BellaSaveData.Stage={bellaStage}");

                // BellaSaveData is the authority — host state may have advanced
                // the stage via SyncVar before this quest's save was loaded.
                if (BellaSaveData.Instance != null && BellaSaveData.Instance.Stage > _stage)
                    _stage = BellaSaveData.Instance.Stage;

                OTCLog.Msg(OTCLog.Systems.Quest, $"OnLoaded: rebuilding entries at _stage={_stage}");

                QuestEntries.Clear();
                _visitEntry = AddEntry("Visit Bella at the downtown apartment", BellaBuilding);
                _weedEntry = AddEntry(GetWeedText(), BellaBuilding);
                _methEntry = AddEntry(GetMethText(), BellaBuilding);
                _cokeEntry = AddEntry(GetCokeText(), BellaBuilding);

                if (_stage >= 1)
                    _visitEntry?.Begin();
                if (_stage >= 2)
                {
                    _visitEntry?.Complete();
                    _weedEntry?.Begin();
                }
                if (_stage >= 3)
                {
                    _weedEntry?.Complete();
                    _methEntry?.Begin();
                }
                if (_stage >= 4)
                {
                    _methEntry?.Complete();
                    _cokeEntry?.Begin();
                }
                if (_stage >= 5)
                    _cokeEntry?.Complete();
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"OnLoaded rebuild failed: {ex.Message}");
            }
        }
    }
}
