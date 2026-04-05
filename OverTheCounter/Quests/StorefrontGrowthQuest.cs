using MelonLoader.Utils;
using OverTheCounter.Logic;
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
    /// UI highlight targets exposed by the quest for GreenTabApp to pulse.
    /// </summary>
    public enum QuestHighlight
    {
        None,
        InventoryNav,
        PricingArea,
        ProductRows,
        OverviewNav,
        StoreToggle
    }

    /// <summary>
    /// Guides the player through setting up their first storefront after purchasing
    /// the Westville Shack: place storage → stock product → set prices → view product →
    /// turn on lights → open store → make a sale.
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

        // 0=not started, 1=place storage, 2=stock product, 3=set prices, 4=view product,
        // 5=lights, 6=open store, 7=make sale, 8=done
        [SaveableField("storefront_quest_stage")]
        private int _stage;

        [SaveableField("storefront_quest_touched_pricing")]
        private bool _hasTouchedPricing;

        [SaveableField("storefront_quest_viewed_product")]
        private bool _hasViewedProduct;

        private QuestEntry _placeStorageEntry;
        private QuestEntry _stockProductEntry;
        private QuestEntry _setPricesEntry;
        private QuestEntry _viewProductEntry;
        private QuestEntry _turnOnLightsEntry;
        private QuestEntry _openStoreEntry;
        private QuestEntry _makeSaleEntry;

        public static StorefrontGrowthQuest Instance { get; private set; }
        internal static void ResetInstance() => Instance = null;

        /// <summary>Current quest stage index (0=not started, 1-7=in progress, 8=done).</summary>
        public int Stage => _stage;

        /// <summary>
        /// Returns the current highlight target for GreenTabApp to pulse.
        /// When on the Inventory tab, the app upgrades InventoryNav to PricingArea or ProductRows.
        /// </summary>
        public static QuestHighlight ActiveHighlight
        {
            get
            {
                if (Instance == null) return QuestHighlight.None;
                return Instance._stage switch
                {
                    3 => QuestHighlight.InventoryNav,
                    4 => QuestHighlight.InventoryNav,
                    6 => QuestHighlight.OverviewNav,
                    _ => QuestHighlight.None
                };
            }
        }

        // Approximate center of the Westville Shack
        private static readonly Vector3 ShackPosition = new(-167.4f, -3f, 73.5f);

        private float _lastTickTime;
        private const float TickInterval = 3f;
        private bool _tutorialCustomerSpawned;
        private string _tutorialCustomerId;

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
                BuildEntries();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StorefrontGrowth Initialize failed: {ex.Message}");
            }
        }

        private void BuildEntries()
        {
            _placeStorageEntry = AddEntry("Place a Display Cabinet or any storage in your new dispensary", ShackPosition);
            _stockProductEntry = AddEntry("Stock your shelves with packaged weed", ShackPosition);
            _setPricesEntry = AddEntry("Open the GreenTab app and set your product prices", ShackPosition);
            _viewProductEntry = AddEntry("Click on a product in the Inventory tab to view its details", ShackPosition);
            _turnOnLightsEntry = AddEntry("Turn on the lights using the light switch on the wall", ShackPosition);
            _openStoreEntry = AddEntry("Open the store using the GreenTab app on your phone", ShackPosition);
            _makeSaleEntry = AddEntry("Make your first sale", ShackPosition);
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

        // ==================================================================
        //  Callbacks from GreenTabApp
        // ==================================================================

        /// <summary>Called by GreenTabApp when player interacts with auto-pricing or markup.</summary>
        public void OnPricingTouched()
        {
            if (_stage != 3) return;
            _hasTouchedPricing = true;
        }

        /// <summary>Called by GreenTabApp when player clicks a product row in inventory.</summary>
        public void OnProductViewed()
        {
            if (_stage != 4) return;
            _hasViewedProduct = true;
        }

        // ==================================================================
        //  Tick (polled from Core.OnLateUpdate)
        // ==================================================================

        /// <summary>
        /// Polled from Core.OnLateUpdate. Checks quest conditions for stages 1–6.
        /// Stage 7 is event-driven via OnSaleRecorded.
        /// </summary>
        public void Tick()
        {
            if (_stage < 1 || _stage > 7) return;

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
                        if (_hasTouchedPricing)
                            AdvanceToStage4();
                        break;
                    case 4:
                        if (_hasViewedProduct)
                            AdvanceToStage5();
                        break;
                    case 5:
                        if (WestvilleShack.AreLightsOn)
                            AdvanceToStage6();
                        break;
                    case 6:
                        if (WestvilleShack.IsStoreOpen)
                            AdvanceToStage7();
                        break;
                    case 7:
                        // Respawn if tutorial customer left without buying
                        if (_tutorialCustomerSpawned && _tutorialCustomerId != null
                            && !CustomerInstance.Active.ContainsKey(_tutorialCustomerId))
                        {
                            _tutorialCustomerSpawned = false;
                            _tutorialCustomerId = null;
                        }
                        if (S1API.GameTime.TimeManager.CurrentTime < 2000)
                            TrySpawnTutorialCustomer();
                        break;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StorefrontGrowth Tick failed: {ex.Message}");
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
            _stockProductEntry?.Begin();
        }

        private void AdvanceToStage3()
        {
            _stage = 3;
            _stockProductEntry?.Begin();
            _stockProductEntry?.Complete();
            _setPricesEntry?.Begin();
        }

        private void AdvanceToStage4()
        {
            _stage = 4;
            _setPricesEntry?.Begin();
            _setPricesEntry?.Complete();
            _viewProductEntry?.Begin();
        }

        private void AdvanceToStage5()
        {
            _stage = 5;
            _viewProductEntry?.Begin();
            _viewProductEntry?.Complete();
            _turnOnLightsEntry?.Begin();
        }

        private void AdvanceToStage6()
        {
            _stage = 6;
            _turnOnLightsEntry?.Begin();
            _turnOnLightsEntry?.Complete();
            _openStoreEntry?.Begin();
        }

        private void AdvanceToStage7()
        {
            _stage = 7;
            _openStoreEntry?.Begin();
            _openStoreEntry?.Complete();
            _makeSaleEntry?.Begin();
        }

        private void TrySpawnTutorialCustomer()
        {
            if (_tutorialCustomerSpawned) return;
            if (!WestvilleShack.IsStoreOpen) return;

            try
            {
                var spawnPoint = CustomerSpawnPoints.GetRandomSpawnPoint(PropertySaveData.ShackId);
                if (spawnPoint == null) return;

                string id = $"tutorial_{UnityEngine.Random.Range(1000, 9999)}";
                int seed = UnityEngine.Random.Range(0, int.MaxValue);
                var customer = CustomerInstance.Create(id, seed, spawnPoint);

                if (customer != null)
                {
                    // Boost budget + lower standards so tutorial customer is very likely to buy
                    var prefs = customer.Preferences;
                    prefs.MaxBudgetPerItem = 200f;
                    prefs.QualityExpectation = 0f;
                    customer.Preferences = prefs;

                    customer.WalkTo(WestvilleShack.Target.ExteriorApproachPosition);
                    _tutorialCustomerId = id;
                    _tutorialCustomerSpawned = true;
                    OTCLog.Msg(OTCLog.Systems.Quest, "Spawned tutorial customer for first sale");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"TrySpawnTutorialCustomer failed: {ex.Message}");
            }
        }

        private void CompleteQuest()
        {
            try
            {
                _stage = 8;
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

        // ==================================================================
        //  Sale event
        // ==================================================================

        private void OnSaleRecorded()
        {
            if (_stage == 7)
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
                    _stockProductEntry?.Begin();
                }
                if (_stage >= 3)
                {
                    _stockProductEntry?.Complete();
                    _setPricesEntry?.Begin();
                }
                if (_stage >= 4)
                {
                    _setPricesEntry?.Complete();
                    _viewProductEntry?.Begin();
                }
                if (_stage >= 5)
                {
                    _viewProductEntry?.Complete();
                    _turnOnLightsEntry?.Begin();
                }
                if (_stage >= 6)
                {
                    _turnOnLightsEntry?.Complete();
                    _openStoreEntry?.Begin();
                }
                if (_stage >= 7)
                {
                    _openStoreEntry?.Complete();
                    _makeSaleEntry?.Begin();
                    SubscribeSaleEvent();
                }
                if (_stage >= 8)
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
