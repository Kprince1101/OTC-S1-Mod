using MelonLoader.Utils;
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
    /// Guides the player through setting up their first storefront after purchasing
    /// the Westville Shack: place storage → stock product → turn on lights → open store → make a sale.
    /// </summary>
    public class StorefrontGrowthQuest : Quest
    {
        protected override string Title => "Storefront Growth";
        protected override string Description =>
            "You've got the keys. Now turn this place into a working storefront.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => Core.OtcIconDir != null
            ? ImageUtils.LoadImage(Path.Combine(Core.OtcIconDir, "StoreAlertIcon.png"))
            : null;

        [SaveableField("storefront_quest_stage")]
        private int _stage; // 0=not started, 1=place storage, 2=stock product, 3=lights, 4=open store, 5=make sale, 6=done

        private QuestEntry _placeStorageEntry;
        private QuestEntry _stockProductEntry;
        private QuestEntry _turnOnLightsEntry;
        private QuestEntry _openStoreEntry;
        private QuestEntry _makeSaleEntry;

        public static StorefrontGrowthQuest Instance { get; private set; }
        internal static void ResetInstance() => Instance = null;

        public int Stage => _stage;

        // Approximate center of the Westville Shack
        private static readonly Vector3 ShackPosition = new(-167.4f, -3f, 73.5f);

        private float _lastTickTime;
        private const float TickInterval = 3f;

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
                OTCLog.Error(OTCLog.Systems.Quest, $"StorefrontGrowth TriggerInternalInit failed: {ex.Message}");
            }
        }

        public void Initialize()
        {
            try
            {
                TriggerInternalInit();

                _placeStorageEntry = AddEntry("Place a Display Cabinet or any storage in your new dispensary", ShackPosition);
                _stockProductEntry = AddEntry("Stock your shelves with packaged weed", ShackPosition);
                _turnOnLightsEntry = AddEntry("Turn on the lights using the light switch on the wall", ShackPosition);
                _openStoreEntry = AddEntry("Open the store using the GreenTab app on your phone", ShackPosition);
                _makeSaleEntry = AddEntry("Make your first sale", ShackPosition);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StorefrontGrowth Initialize failed: {ex.Message}");
            }
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
                OTCLog.Error(OTCLog.Systems.Quest, $"StorefrontGrowth StartQuest failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Polled from Core.OnLateUpdate. Checks quest conditions for stages 1–4.
        /// Stage 5 is event-driven via OnSaleRecorded.
        /// </summary>
        public void Tick()
        {
            if (_stage < 1 || _stage > 4) return;

            float now = Time.time;
            if (now - _lastTickTime < TickInterval) return;
            _lastTickTime = now;

            try
            {
                var grid = WestvilleShack.ShackGrid;
                if (grid == null) return;

                switch (_stage)
                {
                    case 1:
                        if (PropertyInventory.HasAnyDisplayStorage(grid))
                            AdvanceToStage2();
                        break;
                    case 2:
                        if (PropertyInventory.HasDrugType(grid, EDrugType.Marijuana))
                            AdvanceToStage3();
                        break;
                    case 3:
                        if (WestvilleShack.AreLightsOn)
                            AdvanceToStage4();
                        break;
                    case 4:
                        if (WestvilleShack.IsStoreOpen)
                            AdvanceToStage5();
                        break;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StorefrontGrowth Tick failed: {ex.Message}");
            }
        }

        private void AdvanceToStage2()
        {
            _stage = 2;
            _placeStorageEntry?.Begin();
            _placeStorageEntry?.Complete();
            _stockProductEntry?.Begin();
        }

        private void AdvanceToStage3()
        {
            _stage = 3;
            _stockProductEntry?.Begin();
            _stockProductEntry?.Complete();
            _turnOnLightsEntry?.Begin();
        }

        private void AdvanceToStage4()
        {
            _stage = 4;
            _turnOnLightsEntry?.Begin();
            _turnOnLightsEntry?.Complete();
            _openStoreEntry?.Begin();
        }

        private void AdvanceToStage5()
        {
            _stage = 5;
            _openStoreEntry?.Begin();
            _openStoreEntry?.Complete();
            _makeSaleEntry?.Begin();
        }

        private void CompleteQuest()
        {
            try
            {
                _stage = 6;
                _makeSaleEntry?.Begin();
                _makeSaleEntry?.Complete();
                Complete();
                UnsubscribeSaleEvent();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StorefrontGrowth CompleteQuest failed: {ex.Message}");
            }
        }

        private void OnSaleRecorded()
        {
            if (_stage == 5)
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
                _placeStorageEntry = AddEntry("Place a Display Cabinet or any storage in your new dispensary", ShackPosition);
                _stockProductEntry = AddEntry("Stock your shelves with packaged weed", ShackPosition);
                _turnOnLightsEntry = AddEntry("Turn on the lights using the light switch on the wall", ShackPosition);
                _openStoreEntry = AddEntry("Open the store using the GreenTab app on your phone", ShackPosition);
                _makeSaleEntry = AddEntry("Make your first sale", ShackPosition);

                // Restore entry states based on saved stage
                if (_stage >= 1)
                    _placeStorageEntry?.Begin();
                if (_stage >= 2)
                {
                    _placeStorageEntry?.Complete();
                    _stockProductEntry?.Begin();
                }
                if (_stage >= 3)
                {
                    _stockProductEntry?.Complete();
                    _turnOnLightsEntry?.Begin();
                }
                if (_stage >= 4)
                {
                    _turnOnLightsEntry?.Complete();
                    _openStoreEntry?.Begin();
                }
                if (_stage >= 5)
                {
                    _openStoreEntry?.Complete();
                    _makeSaleEntry?.Begin();
                    SubscribeSaleEvent();
                }
                if (_stage >= 6)
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
                OTCLog.Warning(OTCLog.Systems.Quest, $"StorefrontGrowth OnLoaded rebuild failed: {ex.Message}");
            }
        }
    }
}
