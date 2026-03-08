using MelonLoader;
using OverTheCounter.Utilities;
using S1API.GameTime;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.UI.Shop;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.NPCs;
using ScheduleOne.UI.Shop;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Supply run state machine for a Manager NPC.
    /// Host-only execution — walks to stores, purchases configured items, deposits into supply storage.
    /// After each store visit the route is re-planned from the current position so items
    /// available at multiple stores (e.g. baggies) are always purchased at the nearest one.
    /// </summary>
    public class ManagerSupplyBehaviour
    {

        public enum SupplyState
        {
            Idle,
            WalkingToStore,
            AtStore,
            WalkingToStorage,   // walking to supply container for deposit
            AtStorage,          // playing grab animation + depositing
            WalkingToIdle       // returning to business idle point
        }

        private readonly ManagerInstance _manager;

        private SupplyState _state = SupplyState.Idle;
        public SupplyState State
        {
            get => _state;
            private set
            {
                if (_state == value) return;
                _state = value;
                ManagerInstance.StatePublishNeeded = true;
                if (value == SupplyState.Idle || value == SupplyState.AtStore || value == SupplyState.AtStorage)
                    RestoreIdlePriority();
            }
        }

        // Route planning — rebuilt after each store visit
        private StoreVisit _nextVisit;
        private float _storeArrivalTime;
        private float _storageArrivalTime;

        // Walk failure tracking — cascading fallback chain replaces hard warp
        private int _consecutiveWalkFailures;
        private const int WALK_AVOIDANCE_PRIORITY = 5;
        private const int IDLE_AVOIDANCE_PRIORITY = 50;
        private const float MAX_CASH_WITHDRAWAL = 1000f;

        // Walk resume state
        private Vector3 _currentWalkTarget;
        private float _lastEnsureMovingLog;

        // Stuck detection — warp if stationary for too long during walks
        private Vector3 _lastMovedPosition;
        private float _lastMovedTime;
        private Vector3 _stuckCheckTarget;
        private const float STUCK_WARP_TIMEOUT = 8f;
        private const float STUCK_MOVE_THRESHOLD = 0.5f;

        // IL2CPP callback references (prevent GC collection)
        private GameSystem.Action<NPCMovement.WalkResult> _storeWalkCallback;
        private GameSystem.Action<NPCMovement.WalkResult> _storageWalkCallback;
        private GameSystem.Action<NPCMovement.WalkResult> _idleWalkCallback;

        // Online payment "can't afford" — 24h delay before texting, resets on successful purchase
        private int _cantAffordOnlineDay = -1;       // game day of first failure (-1 = not tracking)
        private bool _cantAffordOnlineTextSent;

        // Items that couldn't be afforded — skip until next in-game hour to prevent loops
        private readonly HashSet<string> _unaffordableItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _lastUnaffordableClearHour = -1;

        /// <summary>
        /// Clears the unaffordable items set, allowing immediate retry.
        /// Called by ManagerController when cash is deposited (flag reset).
        /// </summary>
        public void ClearUnaffordableItems() => _unaffordableItems.Clear();

        // Animated purchasing — one GrabItem animation per item at each store
        private List<PlannedPurchase> _purchaseQueue;
        private int _purchaseIndex;
        private float _purchaseAnimTime;
        private const float PURCHASE_ANIM_DELAY = 0.8f; // seconds between grab animations

        // Periodic route reconsideration during walk-back phases
        private float _lastReconsiderTime;

        // ------------------------------------------------------------------
        // Data structures
        // ------------------------------------------------------------------

        /// <summary>An item the manager needs to buy, with all stores that sell it.</summary>
        private class ShoppingItem
        {
            public string ItemId;
            public string ItemName;
            public int Quantity;
            public List<PurchaseOption> Options = new();
        }

        /// <summary>One store that sells a given item.</summary>
        private class PurchaseOption
        {
            public StoreType StoreType;
            public float UnitPrice;
            public ShopListing ShopListing;     // null for online-only Night Market items
            public ScheduleOne.UI.Phone.PhoneShopInterface.Listing SupplierListing; // for Night Market phone items
            public Vector3? OverridePosition;   // dynamic supplier NPC position (Night Market)
            public string StoreName;            // display name (e.g. "Oscar's Store")
        }

        /// <summary>A planned visit to a single store, with the items to buy there.</summary>
        private class StoreVisit
        {
            public StoreLocation Location;
            public StoreType StoreType;
            public List<PlannedPurchase> Purchases = new();
        }

        private class PlannedPurchase
        {
            public string ItemId;
            public string ItemName;
            public int Quantity;
            public float UnitPrice;
            public ShopListing ShopListing;
            public ScheduleOne.UI.Phone.PhoneShopInterface.Listing SupplierListing;
        }

        public ManagerSupplyBehaviour(ManagerInstance manager)
        {
            _manager = manager;
        }

        // ==================================================================
        // Entry point
        // ==================================================================

        /// <summary>
        /// Entry point — called by the job rotation in ManagerInstance.TryStartNextJob().
        /// Returns true if a supply run was started.
        /// </summary>
        public bool TryStartSupplyRun()
        {
            if (State != SupplyState.Idle) return false;
            if (!_manager.PaidForToday) return false;
            if (_manager.State != ManagerState.Idle) return false;
            if (_manager.Configuration.SupplyStorage == null) return false;

            // Check at least 1 stocked item configured
            bool hasItem = false;
            for (int i = 0; i < _manager.Configuration.StockedItemIds.Length; i++)
            {
                if (!string.IsNullOrEmpty(_manager.Configuration.StockedItemIds[i]))
                {
                    hasItem = true;
                    break;
                }
            }
            if (!hasItem) return false;

            // Don't start if in dialogue
            try
            {
                var dialogueHandler = _manager.GameNpc?.DialogueHandler;
                if (dialogueHandler != null && dialogueHandler.IsDialogueInProgress) return false;
            }
            catch { }

            _consecutiveWalkFailures = 0;

            // Only clear unaffordable list once per in-game hour (prevents rapid re-try loops)
            int currentHour = TimeManager.CurrentTime / 100;
            if (currentHour != _lastUnaffordableClearHour)
            {
                _lastUnaffordableClearHour = currentHour;
                _unaffordableItems.Clear();
            }
            _lastReconsiderTime = UnityEngine.Time.time;

            // If we have leftover items from a previous run (storage was full), try depositing first
            if (HasItemsInNpcInventory())
            {
                if (CanStorageAcceptAnyItem())
                {
                    _manager.Log($"resuming deposit of leftover items from previous run | inventory: [{_manager.GetInventorySummary()}]");
                    _manager.State = ManagerState.SupplyRun;
                    _manager.DisableIdleBehaviour();
                    WalkToStorage();
                    return true;
                }
                // Storage still full — stay idle, don't withdraw more cash or buy more
                return false;
            }

            // 24h bank affordability warning — fires once, resets when a purchase succeeds
            if (_cantAffordOnlineDay >= 0 && !_cantAffordOnlineTextSent
                && TimeManager.ElapsedDays - _cantAffordOnlineDay >= 1)
            {
                _cantAffordOnlineTextSent = true;
                try
                {
                    float balance = NetworkSingleton<ScheduleOne.Money.MoneyManager>.Instance?.onlineBalance ?? 0f;
                    _manager.SendTextMessage($"Boss, the bank balance is too low to buy what I need. Can you top it up? Balance: ${balance:F0}.");
                }
                catch { _manager.SendTextMessage("Boss, the bank balance is too low to buy what I need. Can you top it up?"); }
            }

            _manager.State = ManagerState.SupplyRun;
            _manager.DisableIdleBehaviour();

            // Plan first leg from current position
            if (!PlanAndContinue())
            {
                _manager.State = ManagerState.Idle;
                _manager.EnableIdleBehaviour();
                return false;
            }

            _manager.Log($"starting supply run");
            return true;
        }

        // ==================================================================
        // Route planning (re-run after every store visit)
        // ==================================================================

        /// <summary>
        /// Builds a shopping list, plans the next store visit, and starts walking.
        /// Returns false if nothing to do.
        /// </summary>
        private bool PlanAndContinue()
        {
            var items = BuildShoppingList();

            // Only withdraw cash from locker if any items require Night Market (cash-only) purchase
            bool needsCash = false;
            foreach (var item in items)
            {
                foreach (var opt in item.Options)
                {
                    if (opt.StoreType == StoreType.NightMarket) { needsCash = true; break; }
                }
                if (needsCash) break;
            }
            if (needsCash) WithdrawCashFromLocker();

            if (items.Count == 0)
            {
                // Nothing (more) to buy — go deposit if we have items, otherwise finish
                if (HasItemsInNpcInventory())
                {
                    if (!CanStorageAcceptAnyItem())
                    {
                        // Storage is full — idle and wait for space to open up
                        _manager.Log($"storage full, idling with items until space opens up");
                        ReturnCashToLocker();
                        State = SupplyState.Idle;
                        _manager.State = ManagerState.Idle;
                        _manager.EnableIdleBehaviour();
                        // Don't clear _nextVisit — TryStartSupplyRun will re-plan when triggered
                        return false;
                    }
                    WalkToStorage();
                    return true;
                }
                return false;
            }

            Vector3 fromPos = _manager.Position ?? Vector3.zero;
            var visit = PlanNextVisit(items, fromPos);
            if (visit == null)
            {
                // No reachable stores — go deposit or finish
                if (HasItemsInNpcInventory())
                {
                    if (!CanStorageAcceptAnyItem())
                    {
                        _manager.Log($"storage full (no reachable stores), idling with items");
                        ReturnCashToLocker();
                        State = SupplyState.Idle;
                        _manager.State = ManagerState.Idle;
                        _manager.EnableIdleBehaviour();
                        return false;
                    }
                    WalkToStorage();
                    return true;
                }
                return false;
            }

            // Pre-flight affordability check — if the nearest store is unaffordable,
            // strip that store type from all items' options and re-plan with what remains.
            var excludedStoreTypes = new HashSet<StoreType>();
            const int MAX_RETRIES = 5; // safety cap (only 3 store types exist)
            int retries = 0;
            while (visit != null && !CanAffordAnyItem(visit) && retries < MAX_RETRIES)
            {
                retries++;
                _manager.Log($"can't afford any items at {visit.Location.DisplayName}, trying next store");
                excludedStoreTypes.Add(visit.StoreType);

                // Rebuild items with excluded store options stripped
                var remainingItems = new List<ShoppingItem>();
                foreach (var item in items)
                {
                    var filteredOptions = new List<PurchaseOption>();
                    foreach (var opt in item.Options)
                    {
                        if (!excludedStoreTypes.Contains(opt.StoreType))
                            filteredOptions.Add(opt);
                    }
                    if (filteredOptions.Count > 0)
                    {
                        remainingItems.Add(new ShoppingItem
                        {
                            ItemId = item.ItemId,
                            ItemName = item.ItemName,
                            Quantity = item.Quantity,
                            Options = filteredOptions
                        });
                    }
                }

                if (remainingItems.Count == 0) { visit = null; break; }
                items = remainingItems;
                visit = PlanNextVisit(items, fromPos);
            }

            if (visit == null || retries >= MAX_RETRIES)
            {
                if (retries >= MAX_RETRIES)
                    _manager.LogWarning($"hit retry limit in affordability check");
                else
                    _manager.Log($"no affordable stores remaining");

                // Case 1: ran out of cash for Night Market items mid-day
                var nmItems = items.Where(i => i.Options.Any(o => o.StoreType == StoreType.NightMarket)).ToList();
                if (nmItems.Count > 0)
                {
                    // Mark NM items as unaffordable so BuildShoppingList skips them (prevents 10s retry loop)
                    foreach (var item in nmItems)
                        _unaffordableItems.Add(item.ItemId);

                    if (!_manager.NoNightMarketCashTextSent)
                    {
                        float npcCash = GetNpcInventory()?.GetCashInInventory() ?? 0f;
                        float lockerCash = _manager.GetLockerCash();
                        _manager.NoNightMarketCashTextSent = true;
                        _manager.LockerCashAtWarning = lockerCash;
                        string itemNames = string.Join(", ", nmItems.Select(i => i.ItemName));
                        float totalCash = npcCash + lockerCash;
                        string cashPhrase = totalCash > 0f ? $"I only have ${totalCash:F0} left" : "I don't have any cash left";
                        float wage = Config.ManagerDailyWage.Value;
                        string wageWarning = totalCash < wage ? $" I also won't have enough for tomorrow's ${wage:F0} wage." : "";
                        if (totalCash < wage) _manager.NoFundsTextSent = true;
                        _manager.SendTextMessage($"Boss, I ran out of cash while trying to buy {itemNames}. {cashPhrase}.{wageWarning}");
                    }
                }

                if (HasItemsInNpcInventory())
                {
                    if (CanStorageAcceptAnyItem())
                    {
                        WalkToStorage();
                        return true;
                    }
                }
                return false;
            }

            _nextVisit = visit;
            State = SupplyState.WalkingToStore;
            WalkToStore(visit);
            return true;
        }

        /// <summary>
        /// Analyzes deficits and builds a shopping list.
        /// Subtracts items already in the NPC inventory (purchased but not yet deposited).
        /// Uses 5 virtual slots with priority: one stack per deficient item round-robin.
        /// Each item includes ALL stores that sell it, for nearest-store assignment.
        ///
        /// Storage slot reservation (always active):
        /// Each configured item gets a fair share of FREE storage slots based on actual
        /// deficit. Items that can stack into existing slots don't consume free slots.
        /// This prevents daytime items from filling storage before Night Market opens.
        /// </summary>
        private List<ShoppingItem> BuildShoppingList()
        {
            var config = _manager.Configuration;
            var storage = config.SupplyStorage;
            if (storage?.StorageEntity == null) return new List<ShoppingItem>();

            var npcInventory = GetNpcInventory();

            int totalStorageSlots = storage.StorageEntity.ItemSlots?.Count ?? 0;
            int currentTime = TimeManager.CurrentTime;

            // Identify Night Market-only items (no daytime store option) — they get reservation priority
            var nightMarketOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < config.StockedItemIds.Length; i++)
            {
                string id = config.StockedItemIds[i];
                if (!string.IsNullOrEmpty(id) && !HasDaytimeStoreOption(id))
                    nightMarketOnly.Add(id);
            }

            // Reservations always active — ensures Night Market items always have protected storage slots.
            // Time restrictions only affect route planning (which stores to visit), not slot reservations.
            var reservedSlots = ComputeStorageReservations(config, storage.StorageEntity, nightMarketOnly, npcInventory);

            if (Config.ManagerVerboseLogging.Value)
            {
                var reservationLog = string.Join(", ", reservedSlots.Select(kv => $"{kv.Key}={kv.Value}free"));
                _manager.Log($"[BuildList] storageSlots={totalStorageSlots}, time={currentTime}, reservations: {reservationLog}");
            }

            // Count free storage slots — shared budget across ALL items.
            // Items that stack into existing slots don't consume free slots.
            // Subtract NPC inventory items (non-cash) — they will fill storage slots when deposited.
            int freeStorageSlots = 0;
            {
                var storageSlots = storage.StorageEntity.ItemSlots;
                if (storageSlots != null)
                {
                    for (int s = 0; s < storageSlots.Count; s++)
                    {
                        try { if (storageSlots[s]?.ItemInstance == null) freeStorageSlots++; }
                        catch { }
                    }
                }
            }
            int npcItemSlotsUsed = 0;
            if (npcInventory?.ItemSlots != null)
            {
                for (int s = 0; s < npcInventory.ItemSlots.Count; s++)
                {
                    try
                    {
                        var item = npcInventory.ItemSlots[s]?.ItemInstance;
                        if (item != null && item.TryCast<CashInstance>() == null)
                            npcItemSlotsUsed++;
                    }
                    catch { }
                }
            }
            int remainingFreeSlots = Math.Max(0, freeStorageSlots - npcItemSlotsUsed);

            // Calculate deficits per slot
            var deficits = new List<(int slotIndex, string itemId, int deficit)>();
            for (int i = 0; i < config.StockedItemIds.Length; i++)
            {
                string itemId = config.StockedItemIds[i];
                if (string.IsNullOrEmpty(itemId)) continue;
                if (_unaffordableItems.Contains(itemId)) continue;

                // Skip items locked behind player rank progression
                try
                {
                    var itemDef = ScheduleOne.Registry.GetItem(itemId);
                    var storable = itemDef?.TryCast<ScheduleOne.ItemFramework.StorableItemDefinition>();
                    if (storable != null && !storable.IsUnlocked)
                    {
                        if (Config.ManagerVerboseLogging.Value)
                            _manager.Log($"skipping locked item '{itemId}'");
                        continue;
                    }
                }
                catch { }

                int threshold = config.StockedThresholds[i] * Config.StackSizeMultiplier.Value;
                int inStorage = GetStorageQuantity(storage.StorageEntity, itemId);
                int inNpc = npcInventory != null ? GetNpcInventoryQuantity(npcInventory, itemId) : 0;
                int deficit = threshold - inStorage - inNpc;
                if (deficit > 0)
                {
                    int stackLimit = GetItemStackLimit(itemId);

                    // Split capacity: how many can stack into existing slots vs needing free slots
                    int stackableCapacity = GetStackableCapacity(storage.StorageEntity, itemId, stackLimit);

                    // Filter-aware: only count empty slots that accept this item type
                    int filteredFreeForItem = remainingFreeSlots;
                    try
                    {
                        var filterDef = ScheduleOne.Registry.GetItem(itemId);
                        var filterStorable = filterDef?.TryCast<StorableItemDefinition>();
                        var testInst = filterStorable?.GetDefaultInstance(1);
                        if (testInst != null)
                            filteredFreeForItem = Math.Min(remainingFreeSlots,
                                StorageFilterHelper.CountFilteredFreeSlots(storage.StorageEntity, testInst));
                    }
                    catch { }

                    int newSlotCapacity = filteredFreeForItem * stackLimit;
                    int physicalCapacity = stackableCapacity + newSlotCapacity;

                    // Cap by storage capacity — reservation limits how many free slots this item can use
                    int effectiveCapacity;
                    int resFreeSlots = 0;
                    if (reservedSlots != null && reservedSlots.TryGetValue(itemId, out resFreeSlots))
                    {
                        int reservedCapacity = stackableCapacity + resFreeSlots * stackLimit;
                        effectiveCapacity = Math.Min(reservedCapacity, physicalCapacity);

                        // No safety floor needed — the reservation algorithm accounts for
                        // existing storage + NPC slots in totalClaim, ensuring fair distribution.
                        // Items with resFree=0 either already have their fair share or there's no room.
                    }
                    else
                    {
                        effectiveCapacity = physicalCapacity;
                    }
                    if (Config.ManagerVerboseLogging.Value)
                        _manager.Log($"[BuildList] {itemId}: threshold={threshold}, inStorage={inStorage}, inNpc={inNpc}, deficit={deficit}, stackLimit={stackLimit}, resFree={resFreeSlots}, stackable={stackableCapacity}, freeSlots={remainingFreeSlots}, physicalCap={physicalCapacity}, effectiveCap={effectiveCapacity}");

                    deficit = Math.Min(deficit, effectiveCapacity);
                    if (deficit > 0)
                    {
                        // Deduct free slots this item will consume from the shared budget,
                        // capped by this item's reservation to protect other items' reserved slots
                        int needsBeyondStack = Math.Max(0, deficit - stackableCapacity);
                        int freeSlotsConsumed = (needsBeyondStack + stackLimit - 1) / stackLimit;
                        freeSlotsConsumed = Math.Min(freeSlotsConsumed, filteredFreeForItem);
                        if (reservedSlots != null)
                            freeSlotsConsumed = Math.Min(freeSlotsConsumed, resFreeSlots);
                        remainingFreeSlots = Math.Max(0, remainingFreeSlots - freeSlotsConsumed);

                        deficits.Add((i, itemId, deficit));
                    }
                }
            }

            if (deficits.Count == 0) return new List<ShoppingItem>();

            // Virtual slots for purchases, capped by NPC inventory space (scales with inventory upgrades)
            int maxVirtualSlots = ManagerUpgrades.GetTotalSlots(_manager.Configuration.ExtraInventorySlots);
            int freeNpcSlots = GetFreeNpcSlots(npcInventory);
            int availableSlots = Math.Min(maxVirtualSlots, freeNpcSlots);
            if (availableSlots <= 0) return new List<ShoppingItem>();

            // Cache actual stack limits per item (e.g. soil = 10, baggies = 20)
            var stackLimits = new Dictionary<string, int>();
            foreach (var (_, itemId, _) in deficits)
            {
                if (!stackLimits.ContainsKey(itemId))
                    stackLimits[itemId] = GetItemStackLimit(itemId);
            }

            int slotsUsed = 0;
            var purchases = new Dictionary<string, int>(); // itemId → total quantity

            // During daytime, skip Night Market-only items in round-robin so NPC slots
            // go to items that can actually be purchased now (Gas Mart, Hardware).
            bool nightMarketOpen = currentTime >= 1800 || SaveData.BellaSaveData.IsNightMarketUnlocked;
            var skipDaytime = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!nightMarketOpen)
            {
                foreach (var id in nightMarketOnly)
                    skipDaytime.Add(id);
            }

            // Round-robin allocation: cycle through deficient items, one stack each,
            // using actual stack limits. This ensures even distribution.
            // Pass 1: allocate daytime-purchasable items first
            // Pass 2: if slots remain and NM is open, allocate NM-only items
            bool anyAllocated = true;
            while (slotsUsed < availableSlots && anyAllocated)
            {
                anyAllocated = false;
                foreach (var (slotIndex, itemId, deficit) in deficits)
                {
                    if (slotsUsed >= availableSlots) break;
                    if (skipDaytime.Contains(itemId)) continue; // defer NM-only items during daytime
                    int alreadyBuying = purchases.GetValueOrDefault(itemId);
                    int remaining = deficit - alreadyBuying;
                    if (remaining <= 0) continue;

                    int stackLimit = stackLimits.GetValueOrDefault(itemId, 20);
                    int qty = Math.Min(stackLimit, remaining);
                    purchases[itemId] = alreadyBuying + qty;
                    slotsUsed++;
                    anyAllocated = true;
                }
            }

            // Pass 2: if Night Market is open and slots remain, allocate NM-only items
            if (nightMarketOpen && slotsUsed < availableSlots)
            {
                anyAllocated = true;
                while (slotsUsed < availableSlots && anyAllocated)
                {
                    anyAllocated = false;
                    foreach (var (slotIndex, itemId, deficit) in deficits)
                    {
                        if (slotsUsed >= availableSlots) break;
                        if (!nightMarketOnly.Contains(itemId)) continue; // only NM items in pass 2
                        int alreadyBuying = purchases.GetValueOrDefault(itemId);
                        int remaining = deficit - alreadyBuying;
                        if (remaining <= 0) continue;

                        int stackLimit = stackLimits.GetValueOrDefault(itemId, 20);
                        int qty = Math.Min(stackLimit, remaining);
                        purchases[itemId] = alreadyBuying + qty;
                        slotsUsed++;
                        anyAllocated = true;
                    }
                }
            }

            // Log shopping list summary
            {
                var summary = string.Join(", ", purchases.Select(kv => $"{kv.Value}x {kv.Key}"));
                _manager.Log($"shopping list: [{summary}] ({slotsUsed}/{availableSlots} NPC slots)");
            }
            if (Config.ManagerVerboseLogging.Value)
            {
                foreach (var (itemId, totalQty) in purchases)
                {
                    int stackLimit = stackLimits.GetValueOrDefault(itemId, 20);
                    int slotsNeeded = (totalQty + stackLimit - 1) / stackLimit;
                    _manager.Log($"[BuildList] allocated {itemId}: qty={totalQty} ({slotsNeeded} NPC slots, stackLimit={stackLimit})");
                }
            }

            // Resolve each item to available store options
            var result = new List<ShoppingItem>();
            foreach (var (itemId, totalQty) in purchases)
            {
                var options = FindStoreOptions(itemId, totalQty);
                if (options.Count > 0)
                {
                    result.Add(new ShoppingItem
                    {
                        ItemId = itemId,
                        ItemName = options[0].ShopListing?.Item?.Name
                                 ?? options[0].SupplierListing?.Item?.Name
                                 ?? itemId,
                        Quantity = totalQty,
                        Options = options
                    });
                }
                else
                {
                    _manager.LogWarning($"item '{itemId}' not found in any shop, skipping");
                }
            }

            return result;
        }

        /// <summary>
        /// Finds ALL stores that sell an item (Gas Mart, Hardware, Night Market).
        /// Returns multiple options so the route planner can pick the nearest.
        /// </summary>
        private List<PurchaseOption> FindStoreOptions(string itemId, int quantity)
        {
            var options = new List<PurchaseOption>();

            // Check physical shops (Gas Mart, Hardware Store, Night Market stalls)
            try
            {
                var allShops = ShopInterface.AllShops;
                if (allShops != null)
                {
                    for (int i = 0; i < allShops.Count; i++)
                    {
                        var shop = allShops[i];
                        if (shop == null) continue;

                        var listing = shop.GetListing(itemId);
                        if (listing == null || listing.Item == null) continue;

                        string shopName = shop.ShopName ?? "";
                        StoreType storeType;
                        if (shopName.Contains("Gas", StringComparison.OrdinalIgnoreCase))
                            storeType = StoreType.GasMart;
                        else if (shopName.Contains("Hardware", StringComparison.OrdinalIgnoreCase))
                            storeType = StoreType.Hardware;
                        else
                            storeType = StoreType.NightMarket; // Oscar's stall and other Night Market shops

                        // Check stock for limited-stock items
                        if (!listing.IsUnlimitedStock && listing.CurrentStock <= 0)
                            continue;

                        // Avoid duplicate store types (e.g. two Gas Mart listings)
                        if (options.Any(o => o.StoreType == storeType)) continue;

                        // Night Market shops from AllShops: don't use shop.transform.position
                        // (it may be a UI canvas, not the physical store). StoreLocations has correct coords.
                        // Supplier-based Night Market items still use the supplier NPC's world position.
                        options.Add(new PurchaseOption
                        {
                            StoreType = storeType,
                            UnitPrice = listing.Price,
                            ShopListing = listing,
                            SupplierListing = null,
                            OverridePosition = null,
                            StoreName = shopName
                        });

                        if (Config.ManagerVerboseLogging.Value)
                            _manager.Log($"found '{itemId}' at shop '{shopName}' (type={storeType}, price=${listing.Price:F0})");
                    }
                }
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"AllShops lookup error: {ex.Message}");
            }

            // Check Night Market suppliers (physical shop + online/phone items)
            try
            {
                var suppliers = UnityEngine.Object.FindObjectsOfType<ScheduleOne.Economy.Supplier>();
                if (suppliers != null)
                {
                    foreach (var supplier in suppliers)
                    {
                        if (supplier == null) continue;

                        bool found = false;
                        float price = 0f;
                        ShopListing shopListing = null;
                        ScheduleOne.UI.Phone.PhoneShopInterface.Listing supplierListing = null;

                        // Try supplier's physical ShopInterface first
                        try
                        {
                            var shop = supplier.Shop;
                            if (shop != null)
                            {
                                var listing = shop.GetListing(itemId);
                                if (listing?.Item != null && (listing.IsUnlimitedStock || listing.CurrentStock > 0))
                                {
                                    found = true;
                                    price = listing.Price;
                                    shopListing = listing;
                                }
                            }
                        }
                        catch { }

                        // Fall back to online shop items (phone delivery)
                        if (!found)
                        {
                            try
                            {
                                if (supplier.OnlineShopItems != null)
                                {
                                    foreach (var listing in supplier.OnlineShopItems)
                                    {
                                        if (listing?.Item == null) continue;
                                        if (string.Equals(listing.Item.ID, itemId, StringComparison.OrdinalIgnoreCase))
                                        {
                                            found = true;
                                            price = listing.Price;
                                            supplierListing = listing;
                                            break;
                                        }
                                    }
                                }
                            }
                            catch { }
                        }

                        if (found && !options.Any(o => o.StoreType == StoreType.NightMarket))
                        {
                            // Use supplier NPC's position for walk destination
                            Vector3? supplierPos = null;
                            string supplierName = "Supplier";
                            try
                            {
                                supplierPos = supplier.transform.position;
                                supplierName = supplier.fullName ?? "Supplier";
                            }
                            catch { }

                            options.Add(new PurchaseOption
                            {
                                StoreType = StoreType.NightMarket,
                                UnitPrice = price,
                                ShopListing = shopListing,
                                SupplierListing = supplierListing,
                                OverridePosition = supplierPos,
                                StoreName = supplierName
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"Supplier lookup error: {ex.Message}");
            }

            return options;
        }

        /// <summary>
        /// Picks the single nearest store to visit next.
        /// Consolidates items: if multiple items are available at the nearest store, buys them all there.
        /// Night Market excluded before 6 PM. Hardware Store excluded after 8 PM.
        /// Supports dynamic supplier positions for Night Market stores.
        /// </summary>
        private StoreVisit PlanNextVisit(List<ShoppingItem> items, Vector3 fromPosition)
        {
            int currentTime = TimeManager.CurrentTime;
            bool nightMarketOpen = currentTime >= 1800 || SaveData.BellaSaveData.IsNightMarketUnlocked;
            // Hardware Store hours: 8 AM (0800) – 8 PM (2000).
            // Decision check uses strict closing time — managers cannot START a trip after the store closes.
            // En-route and at-store checks use a 1-hour grace (< 2100) so managers who left before
            // closing aren't forced to abort immediately. See RefreshCurrentVisit and Tick mid-walk abort.
            bool hardwareOpen = currentTime >= 800 && currentTime < 2000;

            // Step 1: Collect all viable (storeType → location) candidates from items that need buying.
            // Each item independently determines which stores it needs, so we know which store types to evaluate.
            var candidateStores = new Dictionary<StoreType, StoreLocation>();

            foreach (var item in items)
            {
                foreach (var opt in item.Options)
                {
                    if (opt.StoreType == StoreType.NightMarket && !nightMarketOpen) continue;
                    if (opt.StoreType == StoreType.Hardware && !hardwareOpen) continue;
                    if (candidateStores.ContainsKey(opt.StoreType)) continue;

                    if (opt.OverridePosition.HasValue)
                    {
                        // Dynamic supplier position
                        var dynamicLoc = new StoreLocation(opt.StoreType, opt.OverridePosition.Value, 0f,
                            opt.StoreName ?? "Supplier");
                        candidateStores[opt.StoreType] = dynamicLoc;
                    }
                    else
                    {
                        var loc = StoreLocations.GetNearest(opt.StoreType, fromPosition);
                        if (loc != null)
                            candidateStores[opt.StoreType] = loc;
                    }
                }
            }

            if (candidateStores.Count == 0) return null;

            // Step 2: Pick the nearest store
            StoreType nearestType = default;
            float nearestDist = float.MaxValue;
            StoreLocation nearestLoc = null;

            foreach (var (storeType, loc) in candidateStores)
            {
                float dist = Vector3.Distance(fromPosition, loc.Position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestType = storeType;
                    nearestLoc = loc;
                }
            }

            if (nearestLoc == null) return null;

            // Step 3: Consolidate — assign ALL items available at the nearest store to this visit.
            // If we're going to Hardware anyway, buy everything available there (baggies + fertilizer).
            var visit = new StoreVisit
            {
                Location = nearestLoc,
                StoreType = nearestType,
            };

            foreach (var item in items)
            {
                var opt = item.Options.FirstOrDefault(o => o.StoreType == nearestType);
                if (opt == null) continue;

                // Re-check time restrictions for this specific option
                if (opt.StoreType == StoreType.NightMarket && !nightMarketOpen) continue;
                if (opt.StoreType == StoreType.Hardware && !hardwareOpen) continue;

                int buyQty = item.Quantity;
                if (opt.ShopListing != null && !opt.ShopListing.IsUnlimitedStock)
                    buyQty = Math.Min(buyQty, opt.ShopListing.CurrentStock);
                if (buyQty <= 0) continue;

                visit.Purchases.Add(new PlannedPurchase
                {
                    ItemId = item.ItemId,
                    ItemName = item.ItemName,
                    Quantity = buyQty,
                    UnitPrice = opt.UnitPrice,
                    ShopListing = opt.ShopListing,
                    SupplierListing = opt.SupplierListing
                });
            }

            if (visit.Purchases.Count == 0) return null;

            _manager.Log($"next visit → {nearestLoc.DisplayName} ({visit.Purchases.Count} items, {nearestDist:F0}m away)");
            return visit;
        }

        // ==================================================================
        // Walking to store
        // ==================================================================

        private void WalkToStore(StoreVisit visit)
        {
            _consecutiveWalkFailures = 0;
            _currentWalkTarget = visit.Location.Position;

            try
            {
                _storeWalkCallback = (GameSystem.Action<NPCMovement.WalkResult>)
                    new Action<NPCMovement.WalkResult>(result =>
                    {
                        if (result == NPCMovement.WalkResult.Success ||
                            result == NPCMovement.WalkResult.Partial)
                        {
                            State = SupplyState.AtStore;
                            _storeArrivalTime = UnityEngine.Time.time;
                            _consecutiveWalkFailures = 0;
                            FaceStoreDirection(visit.Location);
                        }
                        else if (result == NPCMovement.WalkResult.Failed)
                        {
                            _consecutiveWalkFailures++;
                            if (!TryWalkEscalation(visit.Location.Position, _storeWalkCallback))
                            {
                                _manager.LogWarning("escalation exhausted, warping to store");
                                WarpToPosition(visit.Location.Position);
                                State = SupplyState.AtStore;
                                _storeArrivalTime = UnityEngine.Time.time;
                                _consecutiveWalkFailures = 0;
                                FaceStoreDirection(visit.Location);
                            }
                        }
                        // On Stopped: leave state as WalkingToStore for EnsureMovingDuringRun() to resume
                    });

                SetWalkingPriority();
                _manager.GameNpc.Movement.SetDestination(visit.Location.Position, _storeWalkCallback, 3f, 1f);
                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"walking to {visit.Location.DisplayName} | inventory: [{_manager.GetInventorySummary()}]");
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"WalkToStore failed: {ex.Message}");
                FinishRun();
            }
        }

        // ==================================================================
        // Purchasing
        // ==================================================================

        /// <summary>
        /// Rebuilds the purchase list for the current store from a fresh BuildShoppingList().
        /// Called right before SimulatePurchase so config changes during the walk are picked up.
        /// </summary>
        private void RefreshCurrentVisit()
        {
            if (_nextVisit == null) return;

            var items = BuildShoppingList();
            var storeType = _nextVisit.StoreType;
            _nextVisit.Purchases.Clear();

            if (items.Count == 0) return;

            int currentTime = TimeManager.CurrentTime;
            bool nightMarketOpen = currentTime >= 1800 || SaveData.BellaSaveData.IsNightMarketUnlocked;
            // 1h grace period: manager already committed to this store before it closed,
            // so allow shopping up to 9 PM (2100). Decision check in PlanNextVisit uses strict 2000.
            bool hardwareOpen = currentTime >= 800 && currentTime < 2100;

            foreach (var item in items)
            {
                var opt = item.Options.FirstOrDefault(o => o.StoreType == storeType);
                if (opt == null) continue;
                if (opt.StoreType == StoreType.NightMarket && !nightMarketOpen) continue;
                if (opt.StoreType == StoreType.Hardware && !hardwareOpen) continue;

                int buyQty = item.Quantity;
                if (opt.ShopListing != null && !opt.ShopListing.IsUnlimitedStock)
                    buyQty = Math.Min(buyQty, opt.ShopListing.CurrentStock);
                if (buyQty <= 0) continue;

                _nextVisit.Purchases.Add(new PlannedPurchase
                {
                    ItemId = item.ItemId,
                    ItemName = item.ItemName,
                    Quantity = buyQty,
                    UnitPrice = opt.UnitPrice,
                    ShopListing = opt.ShopListing,
                    SupplierListing = opt.SupplierListing
                });
            }

            if (Config.ManagerVerboseLogging.Value)
                _manager.Log($"refreshed visit at {_nextVisit.Location.DisplayName} → {_nextVisit.Purchases.Count} items");
        }

        /// <summary>
        /// Begins the purchasing sequence at the current store.
        /// Builds a queue of validated purchases, then processes them one at a time
        /// with a GrabItem animation between each.
        /// </summary>
        private void SimulatePurchase()
        {
            if (_nextVisit == null)
            {
                if (HasItemsInNpcInventory()) WalkToStorage();
                else FinishRun();
                return;
            }

            var visit = _nextVisit;
            _manager.Log($"purchasing at {visit.Location.DisplayName} ({visit.Purchases.Count} items)");

            // Build queue — only check storage capacity upfront.
            // Affordability is checked per-item in ProcessNextPurchase (after previous purchases deduct funds).
            _purchaseQueue = new List<PlannedPurchase>();
            var supplyStorage = _manager.Configuration.SupplyStorage;

            var npcInv = GetNpcInventory();
            foreach (var purchase in visit.Purchases)
            {
                int buyQty = purchase.Quantity;

                // Enforce threshold cap — never buy beyond what the threshold allows
                int threshold = GetThresholdForItem(_manager.Configuration, purchase.ItemId);
                if (threshold > 0 && supplyStorage?.StorageEntity != null)
                {
                    int inStorage = GetStorageQuantity(supplyStorage.StorageEntity, purchase.ItemId);
                    int inNpc = npcInv != null ? GetNpcInventoryQuantity(npcInv, purchase.ItemId) : 0;
                    int headroom = Math.Max(0, threshold - inStorage - inNpc);
                    if (buyQty > headroom)
                    {
                        if (Config.ManagerVerboseLogging.Value)
                            _manager.Log($"capping {purchase.ItemName} from {buyQty} to {headroom} (threshold {threshold}, inStorage {inStorage}, inNpc {inNpc})");
                        buyQty = headroom;
                    }
                }

                if (supplyStorage?.StorageEntity != null)
                {
                    int physCap = GetStorageCapacityForItem(supplyStorage.StorageEntity, purchase.ItemId);
                    if (buyQty > physCap)
                    {
                        if (Config.ManagerVerboseLogging.Value)
                            _manager.Log($"capping {purchase.ItemName} from {buyQty} to {physCap} (physical storage limit)");
                        buyQty = physCap;
                    }
                    if (buyQty <= 0)
                    {
                        if (Config.ManagerVerboseLogging.Value)
                            _manager.Log($"skipping {purchase.ItemName}, no storage capacity left");
                        continue;
                    }
                }

                _purchaseQueue.Add(new PlannedPurchase
                {
                    ItemId = purchase.ItemId,
                    ItemName = purchase.ItemName,
                    Quantity = buyQty,
                    UnitPrice = purchase.UnitPrice,
                    ShopListing = purchase.ShopListing,
                    SupplierListing = purchase.SupplierListing
                });
            }

            if (_purchaseQueue.Count == 0)
            {
                _purchaseQueue = null;

                // If the visit had planned purchases but all were capped to 0 (storage full),
                // finish the run to avoid an infinite plan→buy→0→plan loop.
                if (visit.Purchases.Count > 0)
                {
                    _manager.Log($"all purchases blocked (storage full), ending supply run");
                    if (HasItemsInNpcInventory()) WalkToStorage();
                    else FinishRun();
                    return;
                }

                AfterPurchaseComplete();
                return;
            }

            // Start animated purchasing: play first grab, Tick() drives the rest
            _purchaseIndex = 0;
            PlayGrabAnimation();
            _purchaseAnimTime = UnityEngine.Time.time;
        }

        /// <summary>
        /// Processes a single purchase from the queue (called from Tick after animation delay).
        /// Checks affordability at execution time so previous purchases are already deducted.
        /// If the full quantity can't be afforded, reduces to the most whole stacks that can.
        /// </summary>
        private void ProcessNextPurchase()
        {
            if (_purchaseQueue == null || _purchaseIndex >= _purchaseQueue.Count)
            {
                _purchaseQueue = null;
                AfterPurchaseComplete();
                return;
            }

            var visit = _nextVisit;
            var purchase = _purchaseQueue[_purchaseIndex];
            var npcInventory = GetNpcInventory();
            int buyQty = purchase.Quantity;
            float totalCost = purchase.UnitPrice * buyQty;

            bool success = false;
            if (visit?.StoreType == StoreType.NightMarket)
            {
                // If running low on cash, try to withdraw more from locker (remote top-up)
                float npcCash = npcInventory?.GetCashInInventory() ?? 0f;
                if (npcCash < totalCost)
                {
                    WithdrawCashFromLocker();
                    npcCash = npcInventory?.GetCashInInventory() ?? 0f;
                }

                if (npcCash < totalCost)
                {
                    // Can't afford full quantity — reduce to whole stacks we can afford
                    int stackLimit = GetItemStackLimit(purchase.ItemId);
                    int affordableUnits = (int)(npcCash / purchase.UnitPrice);
                    int reducedQty = (affordableUnits / stackLimit) * stackLimit;

                    if (reducedQty > 0)
                    {
                        if (Config.ManagerVerboseLogging.Value)
                            _manager.Log($"can't afford {buyQty}x {purchase.ItemName} (${totalCost:F0}), buying {reducedQty} instead (${purchase.UnitPrice * reducedQty:F0})");
                        buyQty = reducedQty;
                        totalCost = purchase.UnitPrice * buyQty;
                    }
                    else
                    {
                        if (!_manager.NoNightMarketCashTextSent)
                        {
                            float lockerCash = _manager.GetLockerCash();
                            _manager.NoNightMarketCashTextSent = true;
                            _manager.LockerCashAtWarning = lockerCash;
                            float totalCashOnHand = npcCash + lockerCash;
                            string cashPhrase = totalCashOnHand > 0f ? $"I only have ${totalCashOnHand:F0} left" : "I don't have any cash left";
                            float wage = Config.ManagerDailyWage.Value;
                            string wageWarning = totalCashOnHand < wage ? $" I also won't have enough for tomorrow's ${wage:F0} wage." : "";
                            if (totalCashOnHand < wage) _manager.NoFundsTextSent = true;
                            _manager.SendTextMessage($"Boss, I ran out of cash while trying to buy {purchase.ItemName}. {cashPhrase}.{wageWarning}");
                        }
                        if (Config.ManagerVerboseLogging.Value)
                            _manager.Log($"can't afford {purchase.ItemName} (need ${purchase.UnitPrice * stackLimit:F0} for one stack, have ${npcCash:F0})");
                        _unaffordableItems.Add(purchase.ItemId);
                    }
                }

                if (buyQty > 0 && !_unaffordableItems.Contains(purchase.ItemId))
                {
                    try
                    {
                        npcInventory.RemoveCash(totalCost);
                        success = true;
                    }
                    catch (Exception ex)
                    {
                        _manager.LogWarning($"RemoveCash failed for {purchase.ItemName}: {ex.Message}");
                    }
                }
            }
            else
            {
                // Gas Mart / Hardware: check online balance NOW
                try
                {
                    var moneyManager = NetworkSingleton<ScheduleOne.Money.MoneyManager>.Instance;
                    if (moneyManager == null)
                    {
                        _manager.LogWarning($"MoneyManager not available");
                    }
                    else if (moneyManager.onlineBalance < totalCost)
                    {
                        // Can't afford full quantity — reduce to whole stacks we can afford
                        int stackLimit = GetItemStackLimit(purchase.ItemId);
                        int affordableUnits = (int)(moneyManager.onlineBalance / purchase.UnitPrice);
                        int reducedQty = (affordableUnits / stackLimit) * stackLimit;

                        if (reducedQty > 0)
                        {
                            if (Config.ManagerVerboseLogging.Value)
                                _manager.Log($"can't afford {buyQty}x {purchase.ItemName} online (${totalCost:F0}), buying {reducedQty} instead (${purchase.UnitPrice * reducedQty:F0})");
                            buyQty = reducedQty;
                            totalCost = purchase.UnitPrice * buyQty;
                            moneyManager.CreateOnlineTransaction(
                                purchase.ItemName,
                                -purchase.UnitPrice,
                                buyQty,
                                $"Supply purchase by {_manager.GameNpc?.fullName ?? "Manager"}"
                            );
                            success = true;
                        }
                        else
                        {
                            // Start 24h timer on first online purchase failure (text sent after delay)
                            if (_cantAffordOnlineDay < 0)
                                _cantAffordOnlineDay = TimeManager.ElapsedDays;
                            if (Config.ManagerVerboseLogging.Value)
                                _manager.Log($"can't afford {purchase.ItemName} online (need ${purchase.UnitPrice * stackLimit:F0} for one stack, have ${moneyManager.onlineBalance:F0})");
                            _unaffordableItems.Add(purchase.ItemId);
                        }
                    }
                    else
                    {
                        moneyManager.CreateOnlineTransaction(
                            purchase.ItemName,
                            -purchase.UnitPrice,
                            buyQty,
                            $"Supply purchase by {_manager.GameNpc?.fullName ?? "Manager"}"
                        );
                        success = true;
                    }
                }
                catch (Exception ex)
                {
                    _manager.LogWarning($"online transaction failed for {purchase.ItemName}: {ex.Message}");
                }
            }

            if (success)
            {
                // Successful online purchase — reset 24h bank affordability timer
                if (visit?.StoreType != StoreType.NightMarket)
                {
                    _cantAffordOnlineDay = -1;
                    _cantAffordOnlineTextSent = false;
                }

                if (purchase.ShopListing != null && !purchase.ShopListing.IsUnlimitedStock)
                {
                    try { purchase.ShopListing.RemoveStock(buyQty); }
                    catch (Exception ex) { _manager.LogWarning($"RemoveStock failed: {ex.Message}"); }
                }

                AddToNpcInventory(npcInventory, purchase.ItemId, buyQty, _manager.Id);
                _manager.Log($"purchased {buyQty}x {purchase.ItemName} (${totalCost:F0})");
            }

            // Advance to next item
            _purchaseIndex++;
            if (_purchaseIndex < _purchaseQueue.Count)
            {
                // More items — play another grab animation
                PlayGrabAnimation();
                _purchaseAnimTime = UnityEngine.Time.time;
            }
            else
            {
                _purchaseQueue = null;
                AfterPurchaseComplete();
            }
        }

        /// <summary>
        /// Checks if the manager can afford at least one stack of any item at the planned store visit.
        /// </summary>
        private bool CanAffordAnyItem(StoreVisit visit)
        {
            try
            {
                if (visit.StoreType == StoreType.NightMarket)
                {
                    // Include locker cash — manager can withdraw remotely during purchase
                    float npcCash = GetNpcInventory()?.GetCashInInventory() ?? 0f;
                    float lockerCash = _manager.HasLocker ? _manager.GetLockerCash() : 0f;
                    float totalCash = npcCash + lockerCash;
                    foreach (var p in visit.Purchases)
                    {
                        int stackLimit = GetItemStackLimit(p.ItemId);
                        float oneStackCost = p.UnitPrice * Math.Min(stackLimit, p.Quantity);
                        if (totalCash >= oneStackCost) return true;
                    }
                    return false;
                }
                else
                {
                    var moneyManager = NetworkSingleton<ScheduleOne.Money.MoneyManager>.Instance;
                    if (moneyManager == null) return false;
                    float balance = moneyManager.onlineBalance;
                    foreach (var p in visit.Purchases)
                    {
                        int stackLimit = GetItemStackLimit(p.ItemId);
                        float oneStackCost = p.UnitPrice * Math.Min(stackLimit, p.Quantity);
                        if (balance >= oneStackCost) return true;
                    }
                    return false;
                }
            }
            catch { return true; } // on error, allow the trip (fail at purchase time)
        }

        private void PlayGrabAnimation()
        {
            try { _manager.GameNpc?.Avatar?.Animation?.SetTrigger("GrabItem"); }
            catch { }
        }

        private void FaceStoreDirection(StoreLocation location)
        {
            try
            {
                var forward = location.Rotation * Vector3.forward;
                _manager.GameNpc?.Movement?.FaceDirection(forward, 0.3f);
            }
            catch { }
        }

        /// <summary>
        /// Called after all purchases at the current store are complete.
        /// Re-plans route for remaining items.
        /// </summary>
        private void AfterPurchaseComplete()
        {
            _nextVisit = null;

            // Always deposit items before considering more shopping. This ensures
            // the manager finishes one load before going back for more, keeping the
            // supply storage fed and enabling distribution routes to start sooner.
            // After depositing, TryStartNextJob → TryStartSupplyRun will naturally
            // re-trigger another supply run if items remain on the shopping list.
            if (HasItemsInNpcInventory())
            {
                if (CanStorageAcceptAnyItem())
                {
                    WalkToStorage();
                }
                else
                {
                    _manager.Log($"storage full after purchase, idling with items");
                    ReturnCashToLocker();
                    _purchaseQueue = null;
                    State = SupplyState.Idle;
                    _manager.State = ManagerState.Idle;
                    _manager.EnableIdleBehaviour();
                }
                return;
            }

            // No items in inventory — check if more shopping is needed
            if (!PlanAndContinue())
            {
                // Run complete — return cash and walk home
                ReturnCashToLocker();
                _purchaseQueue = null;
                State = SupplyState.Idle;
                _manager.State = ManagerState.Idle;
                _nextVisit = null;
                _manager.EnableIdleBehaviour();

                if (!_manager.TryStartNextJob())
                {
                    _manager.State = ManagerState.SupplyRun;
                    WalkToIdle();
                }
            }
        }

        // ==================================================================
        // Walking to supply storage (deposit phase)
        // ==================================================================

        /// <summary>
        /// Walks to the supply storage access point for item deposit.
        /// </summary>
        private void WalkToStorage()
        {
            State = SupplyState.WalkingToStorage;

            var storagePos = GetStorageAccessPosition();
            if (storagePos == null)
            {
                _manager.LogWarning($"can't find supply storage position, depositing immediately");
                DepositItems();
                return;
            }

            _currentWalkTarget = storagePos.Value;
            _consecutiveWalkFailures = 0;

            try
            {
                _storageWalkCallback = (GameSystem.Action<NPCMovement.WalkResult>)
                    new Action<NPCMovement.WalkResult>(result =>
                    {
                        if (result == NPCMovement.WalkResult.Success ||
                            result == NPCMovement.WalkResult.Partial)
                        {
                            State = SupplyState.AtStorage;
                            _storageArrivalTime = UnityEngine.Time.time;
                            _consecutiveWalkFailures = 0;
                        }
                        else if (result == NPCMovement.WalkResult.Failed)
                        {
                            _consecutiveWalkFailures++;
                            if (!TryWalkEscalation(storagePos.Value, _storageWalkCallback))
                            {
                                _manager.LogWarning("escalation exhausted, warping to storage");
                                WarpToPosition(storagePos.Value);
                                State = SupplyState.AtStorage;
                                _storageArrivalTime = UnityEngine.Time.time;
                                _consecutiveWalkFailures = 0;
                            }
                        }
                    });

                SetWalkingPriority();
                _manager.GameNpc.Movement.SetDestination(storagePos.Value, _storageWalkCallback, 2f, 1f);
                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"walking to supply storage | inventory: [{_manager.GetInventorySummary()}]");
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"WalkToStorage failed: {ex.Message}");
                DepositItems();
            }
        }

        /// <summary>
        /// Gets the position to walk to for accessing the supply storage.
        /// Uses ITransitEntity.AccessPoints if available, otherwise falls back to transform position.
        /// </summary>
        private Vector3? GetStorageAccessPosition()
        {
            var storage = _manager.Configuration.SupplyStorage;
            if (storage == null) return null;

            try
            {
                var transit = storage.TryCast<ScheduleOne.Management.ITransitEntity>();
                if (transit != null && _manager.GameNpc != null)
                {
                    // Vanilla pattern: find closest reachable access point via NavMesh pathability check
                    var reachable = NavMeshUtility.GetReachableAccessPoint(transit, _manager.GameNpc);
                    if (reachable != null)
                        return reachable.position;

                    // Fallback: try raw access points (may not be pathable but better than nothing)
                    var accessPoints = transit.AccessPoints;
                    if (accessPoints != null && accessPoints.Length > 0 && accessPoints[0] != null)
                    {
                        _manager.LogWarning($"no reachable access point for supply storage, using AccessPoints[0] fallback");
                        return accessPoints[0].position;
                    }
                }
            }
            catch { }

            // Fall back to storage transform position
            try
            {
                return storage.transform.position;
            }
            catch { return null; }
        }

        // ==================================================================
        // Depositing items
        // ==================================================================

        /// <summary>
        /// Faces storage, plays grab animation, transfers NPC inventory → supply storage.
        /// Called when AtStorage timer fires.
        /// </summary>
        private void DepositItems()
        {
            State = SupplyState.AtStorage;

            // Face toward the storage
            try
            {
                var storageTransform = _manager.Configuration.SupplyStorage?.transform;
                if (storageTransform != null)
                {
                    var npcPos = _manager.Position ?? Vector3.zero;
                    var dir = (storageTransform.position - npcPos).normalized;
                    if (dir.sqrMagnitude > 0.01f)
                        _manager.GameNpc?.Movement?.FaceDirection(dir);
                }
            }
            catch { }

            // Play GrabItem animation (networked so clients see it)
            try
            {
                _manager.GameNpc?.SetAnimationTrigger_Networked(null, "GrabItem");
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"GrabItem animation failed: {ex.Message}");
            }

            // Transfer items from NPC inventory to supply storage
            var storage = _manager.Configuration.SupplyStorage;
            var npcInventory = GetNpcInventory();
            var config = _manager.Configuration;

            int totalDeposited = 0;
            int totalSkipped = 0;

            if (storage?.StorageEntity != null && npcInventory != null)
            {
                for (int i = 0; i < npcInventory.ItemSlots.Count; i++)
                {
                    try
                    {
                        var slot = npcInventory.ItemSlots[i];
                        if (slot?.ItemInstance == null) continue;

                        // Skip cash slots (cash is returned to locker separately)
                        if (slot.ItemInstance.TryCast<CashInstance>() != null) continue;

                        // Skip slots reserved for distribution delivery
                        if (_manager.DistributionBehaviour?.IsSlotReservedForDelivery(i) == true)
                            continue;

                        var def = slot.ItemInstance.Definition;
                        if (def == null) continue;

                        var storableDef = def.TryCast<StorableItemDefinition>();
                        if (storableDef == null) { totalSkipped += slot.Quantity; continue; }

                        string itemId = def.ID;
                        int qty = slot.Quantity;

                        // Enforce threshold cap — never deposit beyond configured limit
                        int threshold = GetThresholdForItem(config, itemId);
                        if (threshold > 0)
                        {
                            int currentInStorage = GetStorageQuantity(storage.StorageEntity, itemId);
                            int headroom = Math.Max(0, threshold - currentInStorage);
                            if (headroom <= 0)
                            {
                                if (Config.ManagerVerboseLogging.Value)
                                    _manager.Log($"threshold reached for {itemId} ({currentInStorage}/{threshold}), skipping deposit of {qty}");
                                totalSkipped += qty;
                                continue;
                            }
                            if (qty > headroom)
                            {
                                if (Config.ManagerVerboseLogging.Value)
                                    _manager.Log($"capping {itemId} deposit from {qty} to {headroom} (threshold {threshold}, inStorage {currentInStorage})");
                                qty = headroom;
                            }
                        }

                        var newInstance = storableDef.GetDefaultInstance(qty);
                        if (newInstance == null) { totalSkipped += qty; continue; }

                        int canFit = StorageFilterHelper.HowManyCanFitFiltered(storage.StorageEntity, newInstance);
                        if (canFit <= 0)
                        {
                            if (Config.ManagerVerboseLogging.Value)
                                _manager.Log($"storage full, can't fit {itemId} x{qty}");
                            totalSkipped += qty;
                            continue;
                        }

                        int depositQty = Math.Min(qty, canFit);
                        if (depositQty < slot.Quantity)
                        {
                            // Partial deposit
                            var partialInstance = storableDef.GetDefaultInstance(depositQty);
                            StorageFilterHelper.InsertItemFiltered(storage.StorageEntity, partialInstance);
                            slot.ChangeQuantity(-depositQty, true);
                            totalDeposited += depositQty;
                        }
                        else
                        {
                            StorageFilterHelper.InsertItemFiltered(storage.StorageEntity, newInstance);
                            slot.ClearStoredInstance();
                            totalDeposited += depositQty;
                        }
                    }
                    catch (Exception ex)
                    {
                        _manager.LogWarning($"deposit slot {i} failed: {ex.Message}");
                    }
                }
            }
            else
            {
                _manager.LogWarning($"supply storage or NPC inventory gone, discarding items");
                ClearNpcInventory(npcInventory);
            }

            if (totalSkipped > 0)
                _manager.LogWarning($"{totalSkipped} items could not be deposited (storage full or error)");

            _manager.Log($"deposited {totalDeposited} items, skipped {totalSkipped}");

            // Return any remaining cash to the locker
            ReturnCashToLocker();

            // Check for immediate work (distribution routes, more supplies) before walking home
            _purchaseQueue = null;
            State = SupplyState.Idle;
            _manager.State = ManagerState.Idle;
            _nextVisit = null;
            _manager.EnableIdleBehaviour();

            if (_manager.TryStartNextJob()) return;

            // Nothing to do — walk home
            _manager.State = ManagerState.SupplyRun;
            WalkToIdle();
        }

        // ==================================================================
        // Walking to idle point (after deposit)
        // ==================================================================

        /// <summary>
        /// Walks back to the business idle point after depositing items.
        /// </summary>
        private void WalkToIdle()
        {
            State = SupplyState.WalkingToIdle;

            var location = ManagerLocations.GetLocation(_manager.BusinessPropertyCode);
            if (location == null)
            {
                _manager.LogWarning($"no business location after deposit");
                FinishRun();
                return;
            }

            _currentWalkTarget = location.Destination;
            _consecutiveWalkFailures = 0;

            try
            {
                _idleWalkCallback = (GameSystem.Action<NPCMovement.WalkResult>)
                    new Action<NPCMovement.WalkResult>(result =>
                    {
                        if (result == NPCMovement.WalkResult.Success ||
                            result == NPCMovement.WalkResult.Partial)
                        {
                            // Face the correct idle direction
                            try { _manager.GameNpc?.Movement?.FaceDirection(location.DestRotation * Vector3.forward); }
                            catch { }
                            FinishRun();
                        }
                        else if (result == NPCMovement.WalkResult.Failed)
                        {
                            _consecutiveWalkFailures++;
                            if (!TryWalkEscalation(location.Destination, _idleWalkCallback))
                            {
                                _manager.LogWarning("escalation exhausted, warping to idle point");
                                WarpToPosition(location.Destination);
                                try { _manager.GameNpc?.Movement?.FaceDirection(location.DestRotation * Vector3.forward); }
                                catch { }
                                _consecutiveWalkFailures = 0;
                                FinishRun();
                            }
                        }
                    });

                SetWalkingPriority();
                _manager.GameNpc.Movement.SetDestination(location.Destination, _idleWalkCallback, 3f, 1f);
                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"walking to idle point (supply) | inventory: [{_manager.GetInventorySummary()}]");
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"WalkToIdle failed: {ex.Message}");
                FinishRun();
            }
        }

        // ==================================================================
        // Finish / Cancel
        // ==================================================================

        /// <summary>
        /// Ends the supply run and returns to Idle.
        /// </summary>
        private void FinishRun()
        {
            _purchaseQueue = null;
            ReturnCashToLocker();
            State = SupplyState.Idle;
            _manager.State = ManagerState.Idle;
            _nextVisit = null;
            _manager.EnableIdleBehaviour();

            // Immediately check for next job instead of waiting for next tick
            _manager.TryStartNextJob();
        }

        /// <summary>
        /// Cancels an active supply run (called when fired/despawned).
        /// </summary>
        public void Cancel()
        {
            if (State == SupplyState.Idle) return;

            _manager.Log($"supply run cancelled (was {State})");

            _purchaseQueue = null;
            ReturnCashToLocker();
            State = SupplyState.Idle;
            _nextVisit = null;
            _manager.EnableIdleBehaviour();
        }

        // ==================================================================
        // Tick (called every frame from Core.OnLateUpdate, host only)
        // ==================================================================

        public void Tick()
        {
            if (State == SupplyState.Idle) return;

            // Animated purchasing — process next item after animation delay
            if (State == SupplyState.AtStore && _purchaseQueue != null && _purchaseAnimTime > 0f)
            {
                if (UnityEngine.Time.time - _purchaseAnimTime >= PURCHASE_ANIM_DELAY)
                {
                    ProcessNextPurchase();
                }
                return; // don't run other AtStore logic while purchasing
            }

            // AtStore → SimulatePurchase after 1.5s delay (starts the purchase queue)
            if (State == SupplyState.AtStore && UnityEngine.Time.time - _storeArrivalTime >= 1.5f)
            {
                RefreshCurrentVisit(); // re-evaluate what to buy (config may have changed while walking)
                SimulatePurchase();
                return;
            }

            // AtStorage → DepositItems after 0.5s delay (time for grab animation to play)
            if (State == SupplyState.AtStorage && _storageArrivalTime > 0f &&
                UnityEngine.Time.time - _storageArrivalTime >= 0.5f)
            {
                _storageArrivalTime = 0f; // prevent re-entry
                DepositItems();
                return;
            }

            // Abort walk if store closed beyond the 1h grace period.
            // Hardware: open 0800–2000, grace until 2100. Decision check in PlanNextVisit uses strict 2000.
            if (State == SupplyState.WalkingToStore && _nextVisit != null)
            {
                int currentTime = TimeManager.CurrentTime;
                bool storeClosed =
                    (_nextVisit.StoreType == StoreType.Hardware && (currentTime < 800 || currentTime >= 2100)) ||
                    (_nextVisit.StoreType == StoreType.NightMarket && currentTime < 1800
                        && !SaveData.BellaSaveData.IsNightMarketUnlocked);

                if (storeClosed)
                {
                    _manager.Log($"{_nextVisit.Location.DisplayName} closed mid-walk, aborting");
                    _nextVisit = null;
                    if (HasItemsInNpcInventory()) WalkToStorage();
                    else FinishRun();
                    return;
                }
            }

            // Resume interrupted walks
            if (State == SupplyState.WalkingToStore ||
                State == SupplyState.WalkingToStorage ||
                State == SupplyState.WalkingToIdle)
            {
                CheckStuckDuringWalk();
                EnsureMovingDuringRun();
            }

            // Periodic route reconsideration during walk-back phases (every 15s real-time).
            // If items were removed from NPC inventory (e.g. via Trade) or new deficits appeared,
            // and a store is closer than current destination, redirect to the store.
            if (State == SupplyState.WalkingToStorage || State == SupplyState.WalkingToIdle)
            {
                if (UnityEngine.Time.time - _lastReconsiderTime >= 15f)
                {
                    _lastReconsiderTime = UnityEngine.Time.time;
                    ReconsiderRoute();
                }
            }
        }

        /// <summary>
        /// Re-evaluates whether the manager should go to a store instead of continuing
        /// to storage/idle. Called periodically during walk-back phases.
        /// </summary>
        private void ReconsiderRoute()
        {
            var items = BuildShoppingList();
            if (items.Count == 0)
            {
                // No deficits — but if NPC inventory is now empty (items removed), skip deposit
                if (State == SupplyState.WalkingToStorage && !HasItemsInNpcInventory())
                {
                    _manager.Log($"NPC inventory empty during walk to storage, heading to idle");
                    WalkToIdle();
                }
                return;
            }

            // Don't redirect to a store while carrying items to deposit — finish the deposit first.
            // New deficits (e.g. from an inventory upgrade) will be picked up after deposit.
            if (State == SupplyState.WalkingToStorage && HasItemsInNpcInventory())
                return;

            Vector3 currentPos = _manager.Position ?? Vector3.zero;
            var visit = PlanNextVisit(items, currentPos);
            if (visit == null) return;

            float storeDist = Vector3.Distance(currentPos, visit.Location.Position);
            float destDist = Vector3.Distance(currentPos, _currentWalkTarget);

            // Only redirect if the store is meaningfully closer AND we can afford something there
            if (storeDist < destDist - 10f && CanAffordAnyItem(visit))
            {
                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"reconsidering route — {visit.Location.DisplayName} ({storeDist:F0}m) is closer than current dest ({destDist:F0}m)");
                _nextVisit = visit;
                State = SupplyState.WalkingToStore;
                WalkToStore(visit);
            }
        }

        /// <summary>
        /// Resumes interrupted walks during supply run (same pattern as ManagerInstance.EnsureMoving).
        /// </summary>
        private void EnsureMovingDuringRun()
        {
            if (!_manager.IsValid) return;

            try
            {
                var movement = _manager.GameNpc?.Movement;
                if (movement == null) return;

                // Don't resume while in dialogue or player is viewing inventory
                var dialogueHandler = _manager.GameNpc.DialogueHandler;
                if (dialogueHandler != null && dialogueHandler.IsDialogueInProgress) return;
                if (_manager.IsPlayerInteracting) return;

                // Already has a destination
                if (movement.HasDestination) return;

                var pos = _manager.Position ?? Vector3.zero;
                float dist = Vector3.Distance(pos, _currentWalkTarget);
                if (dist > 3f)
                {
                    if (Config.ManagerVerboseLogging.Value && UnityEngine.Time.time - _lastEnsureMovingLog > 10f)
                    {
                        _manager.Log($"resuming supply run walk (dist={dist:F1}m)");
                        _lastEnsureMovingLog = UnityEngine.Time.time;
                    }

                    GameSystem.Action<NPCMovement.WalkResult> callback = State switch
                    {
                        SupplyState.WalkingToStore => _storeWalkCallback,
                        SupplyState.WalkingToStorage => _storageWalkCallback,
                        SupplyState.WalkingToIdle => _idleWalkCallback,
                        _ => null
                    };
                    if (callback != null)
                        movement.SetDestination(_currentWalkTarget, callback, 3f, 1f);
                }
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"EnsureMovingDuringRun failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Detects if the manager is stuck (hasn't moved for STUCK_WARP_TIMEOUT seconds)
        /// and warps to the current walk target (short warp) then transitions state.
        /// </summary>
        private void CheckStuckDuringWalk()
        {
            var pos = _manager.Position ?? Vector3.zero;

            // Reset timer when walk target changes (new walk segment started)
            if (_currentWalkTarget != _stuckCheckTarget)
            {
                _stuckCheckTarget = _currentWalkTarget;
                _lastMovedPosition = pos;
                _lastMovedTime = UnityEngine.Time.time;
                return;
            }

            // Check if NPC has moved
            if (Vector3.Distance(pos, _lastMovedPosition) > STUCK_MOVE_THRESHOLD)
            {
                _lastMovedPosition = pos;
                _lastMovedTime = UnityEngine.Time.time;
                return;
            }

            // NPC hasn't moved — check timeout
            if (_lastMovedTime <= 0f || UnityEngine.Time.time - _lastMovedTime < STUCK_WARP_TIMEOUT)
                return;

            _manager.LogWarning($"stuck for {STUCK_WARP_TIMEOUT}s during {State}, warping");
            _consecutiveWalkFailures = 0;

            switch (State)
            {
                case SupplyState.WalkingToStore:
                    WarpToPosition(_currentWalkTarget);
                    State = SupplyState.AtStore;
                    _storeArrivalTime = UnityEngine.Time.time;
                    break;

                case SupplyState.WalkingToStorage:
                    WarpToPosition(_currentWalkTarget);
                    State = SupplyState.AtStorage;
                    _storageArrivalTime = UnityEngine.Time.time;
                    break;

                case SupplyState.WalkingToIdle:
                    WarpToPosition(_currentWalkTarget);
                    FinishRun();
                    break;
            }

            _lastMovedTime = UnityEngine.Time.time;
        }

        // ==================================================================
        // Status description (for NPC dialogue)
        // ==================================================================

        /// <summary>
        /// Human-readable status for dialogue. Returns null if not on a supply run.
        /// </summary>
        public string GetStatusDescription()
        {
            switch (State)
            {
                case SupplyState.WalkingToStore:
                    if (_nextVisit != null)
                        return $"I'm heading to the {_nextVisit.Location.DisplayName} to pick up supplies.";
                    return "I'm on my way to pick up supplies.";

                case SupplyState.AtStore:
                    if (_nextVisit != null)
                        return $"I'm shopping at the {_nextVisit.Location.DisplayName}.";
                    return "I'm picking up some supplies.";

                case SupplyState.WalkingToStorage:
                    return "I've got the supplies, heading back to put them away.";

                case SupplyState.AtStorage:
                    return "Just putting the supplies away.";

                case SupplyState.WalkingToIdle:
                    return "Finished restocking, heading back to my post.";

                default:
                    return null;
            }
        }

        // ==================================================================
        // NPC Inventory helpers
        // ==================================================================

        private ScheduleOne.NPCs.NPCInventory GetNpcInventory()
        {
            try
            {
                return _manager.GameNpc?.GetComponent<ScheduleOne.NPCs.NPCInventory>();
            }
            catch { return null; }
        }

        private bool HasItemsInNpcInventory()
        {
            var inventory = GetNpcInventory();
            if (inventory?.ItemSlots == null) return false;

            for (int i = 0; i < inventory.ItemSlots.Count; i++)
            {
                var item = inventory.ItemSlots[i]?.ItemInstance;
                if (item != null && item.TryCast<CashInstance>() == null)
                {
                    // Skip slots reserved for distribution delivery — those items
                    // have a recorded destination and should not be deposited at supply storage
                    if (_manager.DistributionBehaviour?.IsSlotReservedForDelivery(i) == true)
                        continue;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Adds purchased items to the NPC inventory.
        /// Skips slots containing cash.
        /// </summary>
        internal static void AddToNpcInventory(ScheduleOne.NPCs.NPCInventory inventory, string itemId, int quantity, string mgrId)
        {
            if (inventory == null) return;

            try
            {
                var itemDef = ScheduleOne.Registry.GetItem(itemId);
                if (itemDef == null) { ManagerInstance.Logger.Warning($"Manager {mgrId}: Registry.GetItem('{itemId}') returned null"); return; }

                var storableDef = itemDef.TryCast<StorableItemDefinition>();
                if (storableDef == null) { ManagerInstance.Logger.Warning($"Manager {mgrId}: item '{itemId}' is not StorableItemDefinition"); return; }

                var instance = storableDef.GetDefaultInstance(quantity);
                if (instance == null) { ManagerInstance.Logger.Warning($"Manager {mgrId}: GetDefaultInstance returned null for '{itemId}'"); return; }

                int itemSlotCount = inventory.ItemSlots.Count;
                int remaining = quantity;

                // Stack into existing compatible slots first (skip cash)
                for (int i = 0; i < itemSlotCount && remaining > 0; i++)
                {
                    var slot = inventory.ItemSlots[i];
                    if (slot == null || slot.IsLocked || slot.IsAddLocked) continue;
                    if (slot.ItemInstance != null && slot.ItemInstance.CanStackWith(instance))
                    {
                        int space = instance.StackLimit - slot.Quantity;
                        if (space > 0)
                        {
                            int add = Math.Min(space, remaining);
                            slot.ChangeQuantity(add);
                            remaining -= add;
                        }
                    }
                }

                // Place in empty slots
                for (int i = 0; i < itemSlotCount && remaining > 0; i++)
                {
                    var slot = inventory.ItemSlots[i];
                    if (slot == null || slot.IsLocked || slot.IsAddLocked) continue;
                    if (slot.ItemInstance == null)
                    {
                        var newInstance = storableDef.GetDefaultInstance(Math.Min(remaining, instance.StackLimit));
                        slot.SetStoredItem(newInstance);
                        remaining -= newInstance.Quantity;
                    }
                }

                if (remaining > 0)
                    ManagerInstance.Logger.Warning($"Manager {mgrId}: NPC inventory full, couldn't fit {remaining}x {itemId}");
            }
            catch (Exception ex)
            {
                ManagerInstance.Logger.Warning($"Manager {mgrId}: AddToNpcInventory failed for {itemId}: {ex.Message}");
            }
        }

        private static int GetNpcInventoryQuantity(ScheduleOne.NPCs.NPCInventory inventory, string itemId)
        {
            if (inventory?.ItemSlots == null) return 0;

            int total = 0;
            try
            {
                for (int i = 0; i < inventory.ItemSlots.Count; i++)
                {
                    var slot = inventory.ItemSlots[i];
                    if (slot?.ItemInstance == null) continue;
                    var def = slot.ItemInstance.Definition;
                    if (def != null && string.Equals(def.ID, itemId, StringComparison.OrdinalIgnoreCase))
                        total += slot.Quantity;
                }
            }
            catch { }
            return total;
        }

        private static void ClearNpcInventory(ScheduleOne.NPCs.NPCInventory inventory)
        {
            if (inventory?.ItemSlots == null) return;
            try
            {
                for (int i = 0; i < inventory.ItemSlots.Count; i++)
                    inventory.ItemSlots[i]?.ClearStoredInstance();
            }
            catch { }
        }

        /// <summary>
        /// Counts free (empty, unlocked) NPC inventory slots.
        /// </summary>
        private static int GetFreeNpcSlots(ScheduleOne.NPCs.NPCInventory inventory)
        {
            if (inventory?.ItemSlots == null) return 0;
            int free = 0;
            int itemSlotCount = inventory.ItemSlots.Count;
            try
            {
                for (int i = 0; i < itemSlotCount; i++)
                {
                    var slot = inventory.ItemSlots[i];
                    if (slot != null && slot.ItemInstance == null && !slot.IsLocked && !slot.IsAddLocked)
                        free++;
                }
            }
            catch { }
            return free;
        }

        // ==================================================================
        // Cash management (locker ↔ NPC inventory)
        // ==================================================================

        /// <summary>
        /// Withdraws up to $1000 cash from the manager's locker into the NPC inventory.
        /// Uses standard AddCash which places a CashInstance in any available slot.
        /// This cash is used for Night Market purchases.
        /// </summary>
        private void WithdrawCashFromLocker()
        {
            if (!_manager.HasLocker) return;

            try
            {
                // Always try to carry a full $1000 — top up if already holding some cash
                var npcInv = GetNpcInventory();
                float alreadyCarrying = npcInv?.GetCashInInventory() ?? 0f;
                float needed = MAX_CASH_WITHDRAWAL - alreadyCarrying;
                if (needed <= 0f) return; // Already carrying enough

                float available = _manager.GetLockerCash();
                float withdraw = Math.Min(needed, available);
                if (withdraw <= 0f) return; // No cash in locker — silent, not an error

                if (!_manager.RemoveLockerCash(withdraw))
                {
                    _manager.LogWarning($"RemoveLockerCash(${withdraw:F0}) returned false");
                    return;
                }

                var npcInventory = GetNpcInventory();
                if (npcInventory == null)
                {
                    _manager.LogWarning($"NPC inventory is null during cash withdrawal");
                    try
                    {
                        DepositCashToStorage(_manager.AssignedLocker.Storage, withdraw);
                    }
                    catch { }
                    return;
                }

                npcInventory.AddCash(withdraw);
                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"withdrew ${withdraw:F0} cash from locker (total on hand: ${npcInventory.GetCashInInventory():F0})");
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"WithdrawCashFromLocker failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns any remaining cash in the NPC inventory back to the locker.
        /// Called at the end of a supply run or on cancellation.
        /// </summary>
        internal void ReturnCashToLocker()
        {
            if (!_manager.HasLocker) return;

            try
            {
                var npcInventory = GetNpcInventory();
                if (npcInventory == null) return;

                float cash = npcInventory.GetCashInInventory();
                if (cash <= 0f) return;

                npcInventory.RemoveCash(cash);
                DepositCashToStorage(_manager.AssignedLocker.Storage, cash);
                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"returned ${cash:F0} cash to locker");

                // Bump the warning threshold so returned change doesn't false-trigger a flag reset
                if (_manager.NoNightMarketCashTextSent)
                    _manager.LockerCashAtWarning = _manager.GetLockerCash();
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"ReturnCashToLocker failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Deposits cash into a storage entity, filling existing cash slots (up to $1000 each)
        /// before creating new slots for any remainder.
        /// </summary>
        internal static void DepositCashToStorage(ScheduleOne.Storage.StorageEntity storage, float amount)
        {
            const float MAX_PER_SLOT = 1000f;
            float remaining = amount;

            // Phase 1: Fill existing cash slots
            for (int i = 0; i < storage.ItemSlots.Count && remaining > 0f; i++)
            {
                var slot = storage.ItemSlots[i];
                if (slot?.ItemInstance == null) continue;

                var existingCash = slot.ItemInstance.TryCast<ScheduleOne.ItemFramework.CashInstance>();
                if (existingCash == null) continue;

                float space = MAX_PER_SLOT - existingCash.Balance;
                if (space <= 0f) continue;

                float add = Math.Min(remaining, space);
                existingCash.ChangeBalance(add);
                slot.ReplicateStoredInstance();
                remaining -= add;
            }

            // Phase 2: Create new slot(s) for any remainder.
            // Cannot use InsertItemFiltered here — it calls GetCopy() internally,
            // which creates a CashInstance with Balance=0 (GetCopy doesn't copy Balance).
            while (remaining > 0f)
            {
                float slotAmount = Math.Min(remaining, MAX_PER_SLOT);
                var newCash = NetworkSingleton<ScheduleOne.Money.MoneyManager>.Instance
                    .GetCashInstance(slotAmount);
                bool placed = false;
                for (int i = 0; i < storage.ItemSlots.Count; i++)
                {
                    var slot = storage.ItemSlots[i];
                    if (slot == null || slot.IsLocked || slot.IsAddLocked) continue;
                    if (slot.ItemInstance != null) continue;
                    if (slot.GetCapacityForItem(newCash, true) <= 0) continue;
                    slot.InsertItem(newCash);
                    // InsertItem goes through the networked SetStoredItem path which can
                    // lose CashInstance.Balance — re-apply it on the stored instance.
                    var stored = slot.ItemInstance?.TryCast<CashInstance>();
                    stored?.SetBalance(slotAmount, true);
                    placed = true;
                    break;
                }
                if (!placed) break;
                remaining -= slotAmount;
            }
        }

        // ==================================================================
        // Threshold + Storage quantity helpers
        // ==================================================================

        /// <summary>
        /// Returns the configured stock threshold for a given item ID.
        /// If the item appears in multiple slots, returns the highest threshold.
        /// Returns 0 if the item is not configured (non-stocked item).
        /// </summary>
        private static int GetThresholdForItem(ManagerConfiguration config, string itemId)
        {
            int maxThreshold = 0;
            for (int i = 0; i < config.StockedItemIds.Length; i++)
            {
                if (string.Equals(config.StockedItemIds[i], itemId, StringComparison.OrdinalIgnoreCase))
                {
                    if (config.StockedThresholds[i] > maxThreshold)
                        maxThreshold = config.StockedThresholds[i];
                }
            }
            return maxThreshold * Config.StackSizeMultiplier.Value;
        }

        private static int GetStorageQuantity(ScheduleOne.Storage.StorageEntity storage, string itemId)
        {
            if (storage?.ItemSlots == null) return 0;

            int total = 0;
            try
            {
                for (int i = 0; i < storage.ItemSlots.Count; i++)
                {
                    var slot = storage.ItemSlots[i];
                    if (slot?.ItemInstance == null) continue;
                    var def = slot.ItemInstance.Definition;
                    if (def != null && string.Equals(def.ID, itemId, StringComparison.OrdinalIgnoreCase))
                        total += slot.Quantity;
                }
            }
            catch (Exception ex)
            {
                ManagerInstance.Logger.Warning($"GetStorageQuantity error for '{itemId}': {ex.Message}");
            }

            return total;
        }

        // ==================================================================
        // Walk escalation & avoidance priority
        // ==================================================================

        /// <summary>
        /// Cascading fallback chain for walk failures.
        /// Returns true if an escalation was attempted, false if all levels exhausted.
        /// Supply runs are outdoor-only so no NavMesh switching is needed.
        /// </summary>
        private bool TryWalkEscalation(Vector3 target,
            GameSystem.Action<NPCMovement.WalkResult> callback)
        {
            var movement = _manager.GameNpc?.Movement;
            if (movement == null) return false;

            switch (_consecutiveWalkFailures)
            {
                case 1:
                    if (Config.ManagerVerboseLogging.Value)
                        _manager.Log("walk failed, retrying with IgnoreCosts");
                    movement.SetAgentType(NPCMovement.EAgentType.IgnoreCosts);
                    movement.SetDestination(target, callback, 3f, 1f);
                    return true;

                case 2:
                    if (Config.ManagerVerboseLogging.Value)
                        _manager.Log("walk failed with IgnoreCosts, retrying on Humanoid");
                    movement.SetAgentType(NPCMovement.EAgentType.Humanoid);
                    movement.SetDestination(target, callback, 3f, 1f);
                    return true;

                case 3:
                    if (Config.ManagerVerboseLogging.Value)
                        _manager.Log("walk failed, IgnoreCosts from current position");
                    movement.SetAgentType(NPCMovement.EAgentType.IgnoreCosts);
                    movement.SetDestination(target, callback, 3f, 1f);
                    return true;

                default:
                    if (Config.ManagerVerboseLogging.Value)
                        _manager.Log("all walk escalation attempts exhausted");
                    movement.SetAgentType(NPCMovement.EAgentType.Humanoid);
                    return false;
            }
        }

        /// <summary>
        /// Sets high avoidance priority so the manager pushes through NPC crowds while walking.
        /// </summary>
        private void SetWalkingPriority()
        {
            try
            {
                var agent = _manager.GameNpc?.Movement?.Agent;
                if (agent != null) agent.avoidancePriority = WALK_AVOIDANCE_PRIORITY;
            }
            catch { }
        }

        /// <summary>
        /// Restores default avoidance priority when idle (yields to other NPCs).
        /// </summary>
        private void RestoreIdlePriority()
        {
            try
            {
                var agent = _manager.GameNpc?.Movement?.Agent;
                if (agent != null) agent.avoidancePriority = IDLE_AVOIDANCE_PRIORITY;
            }
            catch { }
        }

        /// <summary>
        /// Ensures the manager is on civilian NavMesh for outdoor travel.
        /// Safe to call at any time — restores Humanoid agent type.
        /// </summary>
        public void EnsureCivilianNavMesh()
        {
            try
            {
                _manager.GameNpc?.Movement?.SetAgentType(NPCMovement.EAgentType.Humanoid);
            }
            catch { }
        }

        // ==================================================================
        // Utility
        // ==================================================================

        private void WarpToPosition(Vector3 position)
        {
            try
            {
                // Sample NavMesh position before warping (vanilla Employee.SetDestination pattern)
                // areaMask -1 = all NavMesh areas including indoor areas
                if (NavMeshUtility.SamplePosition(position, out UnityEngine.AI.NavMeshHit hit, 5f, -1))
                {
                    _manager.GameNpc?.Movement?.Warp(hit.position);
                }
                else
                {
                    _manager.GameNpc?.Movement?.Warp(position);
                }
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"Warp failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks whether the supply storage can accept at least one item currently in the NPC inventory.
        /// Used to avoid the deposit loop when storage is completely full.
        /// </summary>
        private bool CanStorageAcceptAnyItem()
        {
            var storage = _manager.Configuration.SupplyStorage;
            if (storage?.StorageEntity == null) return false;

            var npcInventory = GetNpcInventory();
            if (npcInventory?.ItemSlots == null) return false;

            for (int i = 0; i < npcInventory.ItemSlots.Count; i++)
            {
                try
                {
                    var slot = npcInventory.ItemSlots[i];
                    if (slot?.ItemInstance == null) continue;
                    if (slot.ItemInstance.TryCast<CashInstance>() != null) continue;

                    // Skip slots reserved for distribution delivery
                    if (_manager.DistributionBehaviour?.IsSlotReservedForDelivery(i) == true)
                        continue;

                    var def = slot.ItemInstance.Definition?.TryCast<StorableItemDefinition>();
                    if (def == null) continue;

                    var testInstance = def.GetDefaultInstance(1);
                    if (testInstance != null && StorageFilterHelper.HowManyCanFitFiltered(storage.StorageEntity, testInstance) > 0)
                        return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>
        /// Gets the real stack limit for an item (e.g. soil=10, baggies=20).
        /// Falls back to 20 if lookup fails.
        /// </summary>
        private static int GetItemStackLimit(string itemId)
        {
            try
            {
                var itemDef = ScheduleOne.Registry.GetItem(itemId);
                if (itemDef == null) return 20;

                var storableDef = itemDef.TryCast<StorableItemDefinition>();
                if (storableDef == null) return 20;

                var testInstance = storableDef.GetDefaultInstance(1);
                return testInstance?.StackLimit ?? 20;
            }
            catch { return 20; }
        }

        /// <summary>
        /// Returns how many more of an item can fit into EXISTING stacks in storage
        /// (without needing free slots). This is the "stackable" portion of capacity.
        /// </summary>
        private static int GetStackableCapacity(ScheduleOne.Storage.StorageEntity storage, string itemId, int stackLimit)
        {
            if (storage?.ItemSlots == null) return 0;
            int total = 0;
            try
            {
                for (int i = 0; i < storage.ItemSlots.Count; i++)
                {
                    var slot = storage.ItemSlots[i];
                    if (slot?.ItemInstance == null) continue;
                    var def = slot.ItemInstance.Definition;
                    if (def != null && string.Equals(def.ID, itemId, StringComparison.OrdinalIgnoreCase))
                    {
                        int space = stackLimit - slot.Quantity;
                        if (space > 0) total += space;
                    }
                }
            }
            catch { }
            return total;
        }

        /// <summary>
        /// Returns true if an item is sold at any daytime store (Gas Mart, Hardware Store).
        /// Items that return false are Night Market-only and should get reservation priority.
        /// </summary>
        internal static bool HasDaytimeStoreOption(string itemId)
        {
            try
            {
                var allShops = ShopInterface.AllShops;
                if (allShops == null) return false;
                for (int i = 0; i < allShops.Count; i++)
                {
                    var shop = allShops[i];
                    if (shop == null) continue;
                    var listing = shop.GetListing(itemId);
                    if (listing?.Item == null) continue;
                    string shopName = shop.ShopName ?? "";
                    if (shopName.Contains("Gas", StringComparison.OrdinalIgnoreCase) ||
                        shopName.Contains("Hardware", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Returns the cheapest Night Market unit price for an item, or float.MaxValue if not found.
        /// Checks both shop listings and supplier NPCs.
        /// </summary>
        internal static float GetNightMarketUnitPrice(string itemId)
        {
            float cheapest = float.MaxValue;
            try
            {
                var allShops = ShopInterface.AllShops;
                if (allShops != null)
                {
                    for (int i = 0; i < allShops.Count; i++)
                    {
                        var shop = allShops[i];
                        if (shop == null) continue;
                        var listing = shop.GetListing(itemId);
                        if (listing?.Item == null) continue;
                        string shopName = shop.ShopName ?? "";
                        if (shopName.Contains("Gas", StringComparison.OrdinalIgnoreCase) ||
                            shopName.Contains("Hardware", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (listing.Price < cheapest) cheapest = listing.Price;
                    }
                }
            }
            catch { }

            try
            {
                var suppliers = UnityEngine.Object.FindObjectsOfType<ScheduleOne.Economy.Supplier>();
                if (suppliers != null)
                {
                    foreach (var supplier in suppliers)
                    {
                        if (supplier == null) continue;
                        try
                        {
                            var shop = supplier.Shop;
                            if (shop != null)
                            {
                                var listing = shop.GetListing(itemId);
                                if (listing?.Item != null && listing.Price < cheapest)
                                    cheapest = listing.Price;
                            }
                        }
                        catch { }
                        try
                        {
                            if (supplier.OnlineShopItems != null)
                            {
                                foreach (var listing in supplier.OnlineShopItems)
                                {
                                    if (listing?.Item == null) continue;
                                    if (string.Equals(listing.Item.ID, itemId, StringComparison.OrdinalIgnoreCase)
                                        && listing.Price < cheapest)
                                        cheapest = listing.Price;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return cheapest;
        }

        /// <summary>
        /// Checks how many more of an item the supply storage can accept.
        /// </summary>
        private static int GetStorageCapacityForItem(ScheduleOne.Storage.StorageEntity storage, string itemId)
        {
            try
            {
                var itemDef = ScheduleOne.Registry.GetItem(itemId);
                if (itemDef == null) return int.MaxValue;

                var storableDef = itemDef.TryCast<StorableItemDefinition>();
                if (storableDef == null) return int.MaxValue;

                var testInstance = storableDef.GetDefaultInstance(1);
                if (testInstance == null) return int.MaxValue;

                return StorageFilterHelper.HowManyCanFitFiltered(storage, testInstance);
            }
            catch { return int.MaxValue; }
        }

        /// <summary>
        /// Computes per-item FREE storage slot reservations based on actual deficit.
        /// Only distributes empty slots among items that need NEW slots (beyond what
        /// can stack into existing occupied slots). This prevents items with existing
        /// stock from over-reserving and starving other items of free slots.
        ///
        /// Night Market-only items get priority in redistribution since they have
        /// limited purchase windows and shouldn't be starved by daytime-available items.
        ///
        /// Returns: itemId → number of FREE slots reserved for that item.
        /// Items with no deficit or that can fully stack get 0.
        /// </summary>
        private Dictionary<string, int> ComputeStorageReservations(
            ManagerConfiguration config,
            ScheduleOne.Storage.StorageEntity storageEntity,
            HashSet<string> nightMarketOnlyItems,
            ScheduleOne.NPCs.NPCInventory npcInventory)
        {
            var result = new Dictionary<string, int>();
            int totalSlots = storageEntity.ItemSlots?.Count ?? 0;

            // Count raw free storage slots and existing storage slots per item
            int rawFreeSlots = 0;
            var storageSlotsPerItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int s = 0; s < totalSlots; s++)
            {
                try
                {
                    var slotItem = storageEntity.ItemSlots[s]?.ItemInstance;
                    if (slotItem == null)
                        rawFreeSlots++;
                    else
                    {
                        string id = slotItem.ID;
                        if (!string.IsNullOrEmpty(id))
                        {
                            storageSlotsPerItem.TryGetValue(id, out int count);
                            storageSlotsPerItem[id] = count + 1;
                        }
                    }
                }
                catch { }
            }

            // Count NPC inventory slots per item (non-cash).
            // Each NPC slot will consume one raw free storage slot on deposit.
            var npcSlotsPerItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (npcInventory?.ItemSlots != null)
            {
                for (int s = 0; s < npcInventory.ItemSlots.Count; s++)
                {
                    try
                    {
                        var item = npcInventory.ItemSlots[s]?.ItemInstance;
                        if (item != null && item.TryCast<CashInstance>() == null)
                        {
                            string id = item.ID;
                            if (!string.IsNullOrEmpty(id))
                            {
                                npcSlotsPerItem.TryGetValue(id, out int count);
                                npcSlotsPerItem[id] = count + 1;
                            }
                        }
                    }
                    catch { }
                }
            }

            int totalNpcItemSlots = 0;
            foreach (var kv in npcSlotsPerItem) totalNpcItemSlots += kv.Value;
            if (totalNpcItemSlots > 0 && Config.ManagerVerboseLogging.Value)
                _manager.Log($"[Reservations] rawFree={rawFreeSlots}, npcItems={totalNpcItemSlots}, npcPerItem=[{string.Join(", ", npcSlotsPerItem.Select(kv => $"{kv.Key}={kv.Value}"))}]");

            // Pre-deduct NPC slots for fully-served items (deficit <= 0 but holding NPC items).
            // These items won't participate in distribution but their NPC slots claim raw free slots.
            int reservedByFullyServed = 0;
            for (int i = 0; i < config.StockedItemIds.Length; i++)
            {
                string itemId = config.StockedItemIds[i];
                if (string.IsNullOrEmpty(itemId) || result.ContainsKey(itemId)) continue;

                int threshold = config.StockedThresholds[i] * Config.StackSizeMultiplier.Value;
                int inStorage = GetStorageQuantity(storageEntity, itemId);
                int inNpc = npcInventory != null ? GetNpcInventoryQuantity(npcInventory, itemId) : 0;
                int deficit = threshold - inStorage - inNpc;

                if (deficit <= 0)
                {
                    result[itemId] = 0;
                    npcSlotsPerItem.TryGetValue(itemId, out int npcSlots);
                    reservedByFullyServed += npcSlots;
                }
            }

            // The distributable pool = raw free slots minus those claimed by fully-served NPC items
            int adjustedPool = Math.Max(0, rawFreeSlots - reservedByFullyServed);

            // For items with remaining deficit, compute their total claim on the pool.
            // totalClaim includes existing storage slots + NPC slots + new slots needed.
            // Items already occupying storage have "consumed" part of their fair share,
            // so they yield free slots to items with nothing yet.
            // After distribution, resFreeSlots = allocated - existingSlots - npcSlots.
            var items = new List<(string itemId, int existingSlots, int npcSlots, int newSlotsNeeded, int totalClaim)>();
            int competingExistingSlots = 0;
            for (int i = 0; i < config.StockedItemIds.Length; i++)
            {
                string itemId = config.StockedItemIds[i];
                if (string.IsNullOrEmpty(itemId) || result.ContainsKey(itemId)) continue;

                int threshold = config.StockedThresholds[i] * Config.StackSizeMultiplier.Value;
                int stackLimit = GetItemStackLimit(itemId);
                int inStorage = GetStorageQuantity(storageEntity, itemId);
                int inNpc = npcInventory != null ? GetNpcInventoryQuantity(npcInventory, itemId) : 0;
                int deficit = threshold - inStorage - inNpc;

                storageSlotsPerItem.TryGetValue(itemId, out int existingSlots);
                npcSlotsPerItem.TryGetValue(itemId, out int npcSlots);
                int stackableCapacity = GetStackableCapacity(storageEntity, itemId, stackLimit);
                int needsBeyondStack = Math.Max(0, deficit - stackableCapacity);
                int newSlotsNeeded = needsBeyondStack > 0
                    ? (needsBeyondStack + stackLimit - 1) / stackLimit
                    : 0;

                // Cap by filter-compatible empty slots for this item type
                try
                {
                    var resDef = ScheduleOne.Registry.GetItem(itemId);
                    var resStorable = resDef?.TryCast<StorableItemDefinition>();
                    var resTest = resStorable?.GetDefaultInstance(1);
                    if (resTest != null)
                        newSlotsNeeded = Math.Min(newSlotsNeeded,
                            StorageFilterHelper.CountFilteredFreeSlots(storageEntity, resTest));
                }
                catch { }

                int totalClaim = existingSlots + npcSlots + newSlotsNeeded;
                if (totalClaim == 0) { result[itemId] = 0; continue; }
                competingExistingSlots += existingSlots;
                items.Add((itemId, existingSlots, npcSlots, newSlotsNeeded, totalClaim));
            }

            if (items.Count == 0) return result;

            // Expand pool to include existing slots of competing items so the distribution
            // accounts for them as "already allocated". The net resFreeSlots across all items
            // will still sum to exactly the available free slots.
            adjustedPool += competingExistingSlots;

            int equalShare = adjustedPool / items.Count;
            var allocated = new int[items.Count];

            // Pass 1: Each item gets min(totalClaim, equalShare)
            for (int i = 0; i < items.Count; i++)
                allocated[i] = Math.Min(items[i].totalClaim, equalShare);

            int used = 0;
            for (int i = 0; i < allocated.Length; i++) used += allocated[i];
            int leftover = adjustedPool - used;

            // Pass 2: Redistribute leftover slots left-to-right
            for (int i = 0; i < items.Count && leftover > 0; i++)
            {
                int stillNeeds = items[i].totalClaim - allocated[i];
                if (stillNeeds <= 0) continue;
                int give = Math.Min(stillNeeds, leftover);
                allocated[i] += give;
                leftover -= give;
            }

            // Convert allocation to resFreeSlots: subtract existing + NPC slots already held
            for (int i = 0; i < items.Count; i++)
                result[items[i].itemId] = Math.Max(0, allocated[i] - items[i].existingSlots - items[i].npcSlots);

            return result;
        }
    }
}
