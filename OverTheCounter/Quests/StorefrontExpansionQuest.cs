using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.Quests;
using S1API.Saveables;
using S1API.Utils;
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

#if IL2CPP
using EDrugType = Il2CppScheduleOne.Product.EDrugType;
#else
using EDrugType = ScheduleOne.Product.EDrugType;
#endif

namespace OverTheCounter.Quests
{
    /// <summary>
    /// Guides the player through setting up the Big Dispensary: place storage →
    /// place checkout counter → stock product → turn on lights → open store → make a sale.
    /// </summary>
    public class StorefrontExpansionQuest : Quest
    {
        protected override string Title => "Storefront Expansion";
        protected override string Description =>
            "Your new dispensary has room to grow. Get it running.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => Core.OtcIconDir != null
            ? ImageUtils.LoadImage(Path.Combine(Core.OtcIconDir, "StoreAlertIcon.png"))
            : null;

        // 0=not started, 1=place storage, 2=place checkout, 3=stock product,
        // 4=lights, 5=open store, 6=make sale, 7=done
        [SaveableField("expansion_quest_stage")]
        private int _stage;

        private QuestEntry _placeStorageEntry;
        private QuestEntry _placeCheckoutEntry;
        private QuestEntry _stockProductEntry;
        private QuestEntry _turnOnLightsEntry;
        private QuestEntry _openStoreEntry;
        private QuestEntry _makeSaleEntry;

        public static StorefrontExpansionQuest Instance { get; private set; }
        internal static void ResetInstance() => Instance = null;

        /// <summary>Current quest stage index (0=not started, 1-6=in progress, 7=done).</summary>
        public int Stage => _stage;

        // Center of the Big Dispensary (origin + half room dimensions)
        private static readonly Vector3 DispensaryPosition = new(121.16f, 0f, -3.6f);

        private float _lastTickTime;
        private const float TickInterval = 3f;

        private TutorialCustomerHelper _tutorialCustomer;

        private TutorialCustomerHelper GetTutorialCustomer() =>
            _tutorialCustomer ??= new TutorialCustomerHelper(
                PropertySaveData.DispensaryId,
                () => Dispensary.IsStoreOpen,
                () => Dispensary.Target);

        private void TriggerInternalInit()
        {
            try
            {
                var s1QuestField = typeof(Quest).GetField("S1Quest",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (s1QuestField == null) return;

                var s1Quest = s1QuestField.GetValue(this) as ScheduleOne.Quests.Quest;
                if (s1Quest == null) return;

                s1Quest.InitializeQuest(Title, Description,
                    Array.Empty<ScheduleOne.Persistence.Datas.QuestEntryData>(),
                    s1Quest.StaticGUID);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StorefrontExpansion TriggerInternalInit failed: {ex.Message}");
            }
        }

        public void Initialize()
        {
            try
            {
                TriggerInternalInit();
                BuildEntries();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StorefrontExpansion Initialize failed: {ex.Message}");
            }
        }

        private void BuildEntries()
        {
            _placeStorageEntry = AddEntry("Place storage in the showroom or backroom of your dispensary", DispensaryPosition);
            _placeCheckoutEntry = AddEntry("Buy and place a Checkout Counter from the hardware store", DispensaryPosition);
            _stockProductEntry = AddEntry("Stock your shelves with packaged weed", DispensaryPosition);
            _turnOnLightsEntry = AddEntry("Turn on the lights using the light switch on the wall", DispensaryPosition);
            _openStoreEntry = AddEntry("Open the store using the GreenTab app on your phone", DispensaryPosition);
            _makeSaleEntry = AddEntry("Make your first sale at the dispensary. A customer is on their way!", DispensaryPosition);
        }

        public void StartQuest()
        {
            try
            {
                _stage = 1;
                Begin();
                _placeStorageEntry?.Begin();
                SubscribeSaleEvent();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StorefrontExpansion StartQuest failed: {ex.Message}");
            }
        }

        // ==================================================================
        //  Tick (polled from Core.OnLateUpdate)
        // ==================================================================

        /// <summary>
        /// Polled from Core.OnLateUpdate. Checks quest conditions for stages 1-5.
        /// Stage 6 is event-driven via OnSaleRecorded.
        /// </summary>
        public void Tick()
        {
            if (_stage < 1 || _stage > 6) return;

            float now = Time.time;
            if (now - _lastTickTime < TickInterval) return;
            _lastTickTime = now;

            try
            {
                var grid = Dispensary.DispensaryGrid;
                if (grid == null) return;

                switch (_stage)
                {
                    case 1:
                        if (PropertyInventory.HasAnyDisplayStorage(grid))
                            AdvanceToStage2();
                        break;
                    case 2:
                        if (PropertyInventory.HasCheckoutCounter(grid))
                            AdvanceToStage3();
                        break;
                    case 3:
                        if (PropertyInventory.HasDrugType(grid, EDrugType.Marijuana))
                            AdvanceToStage4();
                        break;
                    case 4:
                        if (Dispensary.AreLightsOn)
                            AdvanceToStage5();
                        break;
                    case 5:
                        if (Dispensary.IsStoreOpen)
                            AdvanceToStage6();
                        break;
                    case 6:
                        GetTutorialCustomer().Tick();
                        break;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StorefrontExpansion Tick failed: {ex.Message}");
            }
        }

        // ==================================================================
        //  Stage advancement
        // ==================================================================

        private void AdvanceToStage2()
        {
            _stage = 2;
            _placeStorageEntry?.Begin();
            _placeStorageEntry?.Complete();
            _placeCheckoutEntry?.Begin();
        }

        private void AdvanceToStage3()
        {
            _stage = 3;
            _placeCheckoutEntry?.Begin();
            _placeCheckoutEntry?.Complete();
            _stockProductEntry?.Begin();
        }

        private void AdvanceToStage4()
        {
            _stage = 4;
            _stockProductEntry?.Begin();
            _stockProductEntry?.Complete();
            _turnOnLightsEntry?.Begin();
        }

        private void AdvanceToStage5()
        {
            _stage = 5;
            _turnOnLightsEntry?.Begin();
            _turnOnLightsEntry?.Complete();
            _openStoreEntry?.Begin();
        }

        private void AdvanceToStage6()
        {
            _stage = 6;
            _openStoreEntry?.Begin();
            _openStoreEntry?.Complete();
            _makeSaleEntry?.Begin();
        }

        private void CompleteQuest()
        {
            try
            {
                _stage = 7;
                _makeSaleEntry?.Begin();
                _makeSaleEntry?.Complete();
                Complete();
                UnsubscribeSaleEvent();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StorefrontExpansion CompleteQuest failed: {ex.Message}");
            }
        }

        // ==================================================================
        //  Sale event
        // ==================================================================

        private void OnSaleRecorded(string buildingId)
        {
            if (_stage == 6 && buildingId == PropertySaveData.DispensaryId)
                CompleteQuest();
        }

        private void SubscribeSaleEvent()
        {
            PropertySaveData.OnSaleRecorded -= OnSaleRecorded;
            PropertySaveData.OnSaleRecorded += OnSaleRecorded;
        }

        private void UnsubscribeSaleEvent()
        {
            PropertySaveData.OnSaleRecorded -= OnSaleRecorded;
        }

        // ==================================================================
        //  Lifecycle
        // ==================================================================

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
                BuildEntries();

                // Restore entry states based on saved stage
                if (_stage >= 1)
                    _placeStorageEntry?.Begin();
                if (_stage >= 2)
                {
                    _placeStorageEntry?.Complete();
                    _placeCheckoutEntry?.Begin();
                }
                if (_stage >= 3)
                {
                    _placeCheckoutEntry?.Complete();
                    _stockProductEntry?.Begin();
                }
                if (_stage >= 4)
                {
                    _stockProductEntry?.Complete();
                    _turnOnLightsEntry?.Begin();
                }
                if (_stage >= 5)
                {
                    _turnOnLightsEntry?.Complete();
                    _openStoreEntry?.Begin();
                }
                if (_stage >= 6)
                {
                    _openStoreEntry?.Complete();
                    _makeSaleEntry?.Begin();
                    SubscribeSaleEvent();
                }
                if (_stage >= 7)
                {
                    _makeSaleEntry?.Complete();
                    Complete();
                }
                else if (_stage >= 1)
                {
                    // Quest still in progress — subscribe for sale event
                    SubscribeSaleEvent();
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"StorefrontExpansion OnLoaded rebuild failed: {ex.Message}");
            }
        }
    }
}
