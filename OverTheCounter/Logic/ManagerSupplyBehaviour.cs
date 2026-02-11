using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.UI.Shop;
using MelonLoader;
using OverTheCounter.Utilities;
using S1API.GameTime;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

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
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("ManagerSupply");

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

        public SupplyState State { get; private set; } = SupplyState.Idle;

        // Route planning — rebuilt after each store visit
        private StoreVisit _nextVisit;
        private float _storeArrivalTime;
        private float _storageArrivalTime;

        // Walk failure tracking (warp after 5 consecutive failures, vanilla pattern)
        private int _consecutiveWalkFailures;
        private const int MAX_WALK_FAILURES = 5;
        private const float MAX_CASH_WITHDRAWAL = 1000f;

        // Walk resume state
        private Vector3 _currentWalkTarget;
        private float _lastEnsureMovingLog;

        // IL2CPP callback references (prevent GC collection)
        private Il2CppSystem.Action<NPCMovement.WalkResult> _storeWalkCallback;
        private Il2CppSystem.Action<NPCMovement.WalkResult> _storageWalkCallback;
        private Il2CppSystem.Action<NPCMovement.WalkResult> _idleWalkCallback;

        // Online payment "can't afford" flag — reset on successful online purchase
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
            public Il2CppScheduleOne.UI.Phone.PhoneShopInterface.Listing SupplierListing; // for Night Market phone items
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
            public Il2CppScheduleOne.UI.Phone.PhoneShopInterface.Listing SupplierListing;
        }

        public ManagerSupplyBehaviour(ManagerInstance manager)
        {
            _manager = manager;
        }

        // ==================================================================
        // Entry point
        // ==================================================================

        /// <summary>
        /// Entry point — called every 10 in-game minutes from ManagerController.
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
                    Logger.Msg($"Manager {_manager.Id}: resuming deposit of leftover items from previous run");
                    _manager.State = ManagerState.SupplyRun;
                    WalkToStorage();
                    return true;
                }
                // Storage still full — stay idle, don't withdraw more cash or buy more
                return false;
            }

            _manager.State = ManagerState.SupplyRun;

            // Fresh run — allow new online "can't afford" warnings
            _cantAffordOnlineTextSent = false;

            // Plan first leg from current position
            if (!PlanAndContinue())
            {
                _manager.State = ManagerState.Idle;
                return false;
            }

            Logger.Msg($"Manager {_manager.Id}: starting supply run");
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
                        Logger.Msg($"Manager {_manager.Id}: storage full, idling with items until space opens up");
                        ReturnCashToLocker();
                        State = SupplyState.Idle;
                        _manager.State = ManagerState.Idle;
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
                        Logger.Msg($"Manager {_manager.Id}: storage full (no reachable stores), idling with items");
                        ReturnCashToLocker();
                        State = SupplyState.Idle;
                        _manager.State = ManagerState.Idle;
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
                Logger.Msg($"Manager {_manager.Id}: can't afford any items at {visit.Location.DisplayName}, trying next store");
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
                    Logger.Warning($"Manager {_manager.Id}: hit retry limit in affordability check");
                else
                    Logger.Msg($"Manager {_manager.Id}: no affordable stores remaining");

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
                        _manager.SendTextMessage($"Boss, I ran out of cash while trying to buy {itemNames}. {cashPhrase}.");
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

            {
                var reservationLog = string.Join(", ", reservedSlots.Select(kv => $"{kv.Key}={kv.Value}free"));
                Logger.Msg($"Manager {_manager.Id}: [BuildList] storageSlots={totalStorageSlots}, time={currentTime}, reservations: {reservationLog}");
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
                    var itemDef = Il2CppScheduleOne.Registry.GetItem(itemId);
                    var storable = itemDef?.TryCast<Il2CppScheduleOne.ItemFramework.StorableItemDefinition>();
                    if (storable != null && !storable.IsUnlocked)
                    {
                        Logger.Msg($"Manager {_manager.Id}: skipping locked item '{itemId}'");
                        continue;
                    }
                }
                catch { }

                int threshold = config.StockedThresholds[i];
                int inStorage = GetStorageQuantity(storage.StorageEntity, itemId);
                int inNpc = npcInventory != null ? GetNpcInventoryQuantity(npcInventory, itemId) : 0;
                int deficit = threshold - inStorage - inNpc;
                if (deficit > 0)
                {
                    int stackLimit = GetItemStackLimit(itemId);

                    // Split capacity: how many can stack into existing slots vs needing free slots
                    int stackableCapacity = GetStackableCapacity(storage.StorageEntity, itemId, stackLimit);
                    int newSlotCapacity = remainingFreeSlots * stackLimit;
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
                    Logger.Msg($"Manager {_manager.Id}: [BuildList] {itemId}: threshold={threshold}, inStorage={inStorage}, inNpc={inNpc}, deficit={deficit}, stackLimit={stackLimit}, resFree={resFreeSlots}, stackable={stackableCapacity}, freeSlots={remainingFreeSlots}, physicalCap={physicalCapacity}, effectiveCap={effectiveCapacity}");

                    deficit = Math.Min(deficit, effectiveCapacity);
                    if (deficit > 0)
                    {
                        // Deduct free slots this item will consume from the shared budget,
                        // capped by this item's reservation to protect other items' reserved slots
                        int needsBeyondStack = Math.Max(0, deficit - stackableCapacity);
                        int freeSlotsConsumed = (needsBeyondStack + stackLimit - 1) / stackLimit;
                        if (reservedSlots != null)
                            freeSlotsConsumed = Math.Min(freeSlotsConsumed, resFreeSlots);
                        remainingFreeSlots = Math.Max(0, remainingFreeSlots - freeSlotsConsumed);

                        deficits.Add((i, itemId, deficit));
                    }
                }
            }

            if (deficits.Count == 0) return new List<ShoppingItem>();

            // Virtual slots for purchases, capped by NPC inventory space
            const int MAX_VIRTUAL_SLOTS = 5;
            int freeNpcSlots = GetFreeNpcSlots(npcInventory);
            int availableSlots = Math.Min(MAX_VIRTUAL_SLOTS, freeNpcSlots);
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
            bool nightMarketOpen = currentTime >= 1800;
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

            // Log allocations
            foreach (var (itemId, totalQty) in purchases)
            {
                int stackLimit = stackLimits.GetValueOrDefault(itemId, 20);
                int slotsNeeded = (totalQty + stackLimit - 1) / stackLimit;
                Logger.Msg($"Manager {_manager.Id}: [BuildList] allocated {itemId}: qty={totalQty} ({slotsNeeded} NPC slots, stackLimit={stackLimit})");
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
                    Logger.Warning($"Manager {_manager.Id}: item '{itemId}' not found in any shop, skipping");
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

                        Logger.Msg($"Manager {_manager.Id}: found '{itemId}' at shop '{shopName}' (type={storeType}, price=${listing.Price:F0})");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Manager {_manager.Id}: AllShops lookup error: {ex.Message}");
            }

            // Check Night Market suppliers (physical shop + online/phone items)
            try
            {
                var suppliers = UnityEngine.Object.FindObjectsOfType<Il2CppScheduleOne.Economy.Supplier>();
                if (suppliers != null)
                {
                    foreach (var supplier in suppliers)
                    {
                        if (supplier == null) continue;

                        bool found = false;
                        float price = 0f;
                        ShopListing shopListing = null;
                        Il2CppScheduleOne.UI.Phone.PhoneShopInterface.Listing supplierListing = null;

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
                Logger.Warning($"Manager {_manager.Id}: Supplier lookup error: {ex.Message}");
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
            bool nightMarketOpen = currentTime >= 1800;
            bool hardwareOpen = currentTime < 2000; // Hardware Store closes at 8 PM

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

            Logger.Msg($"Manager {_manager.Id}: next visit → {nearestLoc.DisplayName} ({visit.Purchases.Count} items, {nearestDist:F0}m away)");
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
                _storeWalkCallback = (Il2CppSystem.Action<NPCMovement.WalkResult>)
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
                            Logger.Warning($"Manager {_manager.Id}: walk failed ({_consecutiveWalkFailures}/{MAX_WALK_FAILURES}) to {visit.Location.DisplayName}");
                            if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                            {
                                Logger.Warning($"Manager {_manager.Id}: warping to store after {MAX_WALK_FAILURES} walk failures");
                                WarpToPosition(visit.Location.Position);
                                State = SupplyState.AtStore;
                                _storeArrivalTime = UnityEngine.Time.time;
                                _consecutiveWalkFailures = 0;
                                FaceStoreDirection(visit.Location);
                            }
                        }
                        // On Stopped: leave state as WalkingToStore for EnsureMovingDuringRun() to resume
                    });

                _manager.GameNpc.Movement.SetDestination(visit.Location.Position, _storeWalkCallback, 3f, 1f);
                Logger.Msg($"Manager {_manager.Id}: walking to {visit.Location.DisplayName}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Manager {_manager.Id}: WalkToStore failed: {ex.Message}");
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
            bool nightMarketOpen = currentTime >= 1800;
            bool hardwareOpen = currentTime < 2000;

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

            Logger.Msg($"Manager {_manager.Id}: refreshed visit at {_nextVisit.Location.DisplayName} → {_nextVisit.Purchases.Count} items");
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
            Logger.Msg($"Manager {_manager.Id}: purchasing at {visit.Location.DisplayName} ({visit.Purchases.Count} items)");

            // Build queue — only check storage capacity upfront.
            // Affordability is checked per-item in ProcessNextPurchase (after previous purchases deduct funds).
            _purchaseQueue = new List<PlannedPurchase>();
            var supplyStorage = _manager.Configuration.SupplyStorage;

            foreach (var purchase in visit.Purchases)
            {
                int buyQty = purchase.Quantity;
                if (supplyStorage?.StorageEntity != null)
                {
                    int physCap = GetStorageCapacityForItem(supplyStorage.StorageEntity, purchase.ItemId);
                    if (buyQty > physCap)
                    {
                        Logger.Msg($"Manager {_manager.Id}: capping {purchase.ItemName} from {buyQty} to {physCap} (physical storage limit)");
                        buyQty = physCap;
                    }
                    if (buyQty <= 0)
                    {
                        Logger.Msg($"Manager {_manager.Id}: skipping {purchase.ItemName}, no storage capacity left");
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
                        Logger.Msg($"Manager {_manager.Id}: can't afford {buyQty}x {purchase.ItemName} (${totalCost:F0}), buying {reducedQty} instead (${purchase.UnitPrice * reducedQty:F0})");
                        buyQty = reducedQty;
                        totalCost = purchase.UnitPrice * buyQty;
                    }
                    else
                    {
                        if (!_manager.NoNightMarketCashTextSent)
                        {
                            _manager.NoNightMarketCashTextSent = true;
                            _manager.LockerCashAtWarning = _manager.GetLockerCash();
                            float totalCashOnHand = npcCash + _manager.GetLockerCash();
                            string cashPhrase = totalCashOnHand > 0f ? $"I only have ${totalCashOnHand:F0} left" : "I don't have any cash left";
                            _manager.SendTextMessage($"Boss, I ran out of cash while trying to buy {purchase.ItemName}. {cashPhrase}.");
                        }
                        Logger.Msg($"Manager {_manager.Id}: can't afford {purchase.ItemName} (need ${purchase.UnitPrice * stackLimit:F0} for one stack, have ${npcCash:F0})");
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
                        Logger.Warning($"Manager {_manager.Id}: RemoveCash failed for {purchase.ItemName}: {ex.Message}");
                    }
                }
            }
            else
            {
                // Gas Mart / Hardware: check online balance NOW
                try
                {
                    var moneyManager = NetworkSingleton<Il2CppScheduleOne.Money.MoneyManager>.Instance;
                    if (moneyManager == null)
                    {
                        Logger.Warning($"Manager {_manager.Id}: MoneyManager not available");
                    }
                    else if (moneyManager.onlineBalance < totalCost)
                    {
                        // Can't afford full quantity — reduce to whole stacks we can afford
                        int stackLimit = GetItemStackLimit(purchase.ItemId);
                        int affordableUnits = (int)(moneyManager.onlineBalance / purchase.UnitPrice);
                        int reducedQty = (affordableUnits / stackLimit) * stackLimit;

                        if (reducedQty > 0)
                        {
                            Logger.Msg($"Manager {_manager.Id}: can't afford {buyQty}x {purchase.ItemName} online (${totalCost:F0}), buying {reducedQty} instead (${purchase.UnitPrice * reducedQty:F0})");
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
                            if (!_cantAffordOnlineTextSent)
                            {
                                _cantAffordOnlineTextSent = true;
                                string storeName = visit?.Location?.DisplayName ?? "the store";
                                _manager.SendTextMessage($"Boss, I don't have enough in the bank to purchase {purchase.ItemName} from {storeName}.");
                            }
                            Logger.Msg($"Manager {_manager.Id}: can't afford {purchase.ItemName} online (need ${purchase.UnitPrice * stackLimit:F0} for one stack, have ${moneyManager.onlineBalance:F0})");
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
                    Logger.Error($"Manager {_manager.Id}: online transaction failed for {purchase.ItemName}: {ex.Message}");
                }
            }

            if (success)
            {
                // Reset online "can't afford" flag on successful purchase
                if (visit?.StoreType != StoreType.NightMarket)
                    _cantAffordOnlineTextSent = false;
                // NM cash flag resets only when player deposits cash into locker (CheckImmediateWages)

                if (purchase.ShopListing != null && !purchase.ShopListing.IsUnlimitedStock)
                {
                    try { purchase.ShopListing.RemoveStock(buyQty); }
                    catch (Exception ex) { Logger.Warning($"Manager {_manager.Id}: RemoveStock failed: {ex.Message}"); }
                }

                AddToNpcInventory(npcInventory, purchase.ItemId, buyQty, _manager.Id);
                Logger.Msg($"Manager {_manager.Id}: purchased {buyQty}x {purchase.ItemName} (${totalCost:F0})");
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
                    var moneyManager = NetworkSingleton<Il2CppScheduleOne.Money.MoneyManager>.Instance;
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
            if (!PlanAndContinue())
            {
                if (HasItemsInNpcInventory())
                {
                    WalkToStorage();
                }
                else
                {
                    // Run complete — return cash and walk home (mirrors DepositItems flow)
                    ReturnCashToLocker();
                    _purchaseQueue = null;
                    State = SupplyState.Idle;
                    _manager.State = ManagerState.Idle;
                    _nextVisit = null;

                    if (!_manager.TryStartNextJob())
                    {
                        _manager.State = ManagerState.SupplyRun;
                        WalkToIdle();
                    }
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
                Logger.Warning($"Manager {_manager.Id}: can't find supply storage position, depositing immediately");
                DepositItems();
                return;
            }

            _currentWalkTarget = storagePos.Value;
            _consecutiveWalkFailures = 0;

            try
            {
                _storageWalkCallback = (Il2CppSystem.Action<NPCMovement.WalkResult>)
                    new Action<NPCMovement.WalkResult>(result =>
                    {
                        Logger.Msg($"Manager {_manager.Id}: storage walk callback (result={result})");
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
                            if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                            {
                                Logger.Warning($"Manager {_manager.Id}: warping to storage after {MAX_WALK_FAILURES} walk failures");
                                WarpToPosition(storagePos.Value);
                                State = SupplyState.AtStorage;
                                _storageArrivalTime = UnityEngine.Time.time;
                                _consecutiveWalkFailures = 0;
                            }
                        }
                    });

                _manager.GameNpc.Movement.SetDestination(storagePos.Value, _storageWalkCallback, 2f, 1f);
                Logger.Msg($"Manager {_manager.Id}: walking to supply storage");
            }
            catch (Exception ex)
            {
                Logger.Error($"Manager {_manager.Id}: WalkToStorage failed: {ex.Message}");
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
                var transit = storage.TryCast<Il2CppScheduleOne.Management.ITransitEntity>();
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
                        Logger.Warning($"Manager {_manager.Id}: no reachable access point for supply storage, using AccessPoints[0] fallback");
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
                Logger.Warning($"Manager {_manager.Id}: GrabItem animation failed: {ex.Message}");
            }

            // Transfer items from NPC inventory to supply storage
            var storage = _manager.Configuration.SupplyStorage;
            var npcInventory = GetNpcInventory();

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

                        var def = slot.ItemInstance.Definition;
                        if (def == null) continue;

                        var storableDef = def.TryCast<StorableItemDefinition>();
                        if (storableDef == null) { totalSkipped += slot.Quantity; continue; }

                        int qty = slot.Quantity;
                        var newInstance = storableDef.GetDefaultInstance(qty);
                        if (newInstance == null) { totalSkipped += qty; continue; }

                        int canFit = storage.StorageEntity.HowManyCanFit(newInstance);
                        if (canFit <= 0)
                        {
                            Logger.Msg($"Manager {_manager.Id}: storage full, can't fit {def.ID} x{qty}");
                            totalSkipped += qty;
                            continue;
                        }

                        if (canFit < qty)
                        {
                            // Partial deposit
                            var partialInstance = storableDef.GetDefaultInstance(canFit);
                            storage.StorageEntity.InsertItem(partialInstance, true);
                            slot.ChangeQuantity(-(canFit), true);
                            totalDeposited += canFit;
                            totalSkipped += (qty - canFit);
                        }
                        else
                        {
                            storage.StorageEntity.InsertItem(newInstance, true);
                            slot.ClearStoredInstance();
                            totalDeposited += qty;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Manager {_manager.Id}: deposit slot {i} failed: {ex.Message}");
                    }
                }
            }
            else
            {
                Logger.Warning($"Manager {_manager.Id}: supply storage or NPC inventory gone, discarding items");
                ClearNpcInventory(npcInventory);
            }

            if (totalSkipped > 0)
                Logger.Warning($"Manager {_manager.Id}: {totalSkipped} items could not be deposited (storage full or error)");

            Logger.Msg($"Manager {_manager.Id}: deposited {totalDeposited} items, skipped {totalSkipped}");

            // Return any remaining cash to the locker
            ReturnCashToLocker();

            // Check for immediate work (distribution routes, more supplies) before walking home
            Logger.Msg($"Manager {_manager.Id}: supply deposit complete, checking for more work");
            _purchaseQueue = null;
            State = SupplyState.Idle;
            _manager.State = ManagerState.Idle;
            _nextVisit = null;

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
                Logger.Warning($"Manager {_manager.Id}: no business location after deposit");
                FinishRun();
                return;
            }

            _currentWalkTarget = location.Destination;
            _consecutiveWalkFailures = 0;

            try
            {
                _idleWalkCallback = (Il2CppSystem.Action<NPCMovement.WalkResult>)
                    new Action<NPCMovement.WalkResult>(result =>
                    {
                        Logger.Msg($"Manager {_manager.Id}: idle walk callback (result={result})");
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
                            if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                            {
                                Logger.Warning($"Manager {_manager.Id}: warping to idle point");
                                WarpToPosition(location.Destination);
                                try { _manager.GameNpc?.Movement?.FaceDirection(location.DestRotation * Vector3.forward); }
                                catch { }
                                FinishRun();
                            }
                        }
                    });

                _manager.GameNpc.Movement.SetDestination(location.Destination, _idleWalkCallback, 3f, 1f);
                Logger.Msg($"Manager {_manager.Id}: walking to idle point");
            }
            catch (Exception ex)
            {
                Logger.Error($"Manager {_manager.Id}: WalkToIdle failed: {ex.Message}");
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

            // Immediately check for next job instead of waiting for next tick
            _manager.TryStartNextJob();
        }

        /// <summary>
        /// Cancels an active supply run (called when fired/despawned).
        /// </summary>
        public void Cancel()
        {
            if (State == SupplyState.Idle) return;

            Logger.Msg($"Manager {_manager.Id}: supply run cancelled (was {State})");

            _purchaseQueue = null;
            ReturnCashToLocker();
            State = SupplyState.Idle;
            _nextVisit = null;
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

            // Resume interrupted walks
            if (State == SupplyState.WalkingToStore ||
                State == SupplyState.WalkingToStorage ||
                State == SupplyState.WalkingToIdle)
            {
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
                    Logger.Msg($"Manager {_manager.Id}: NPC inventory empty during walk to storage, heading to idle");
                    WalkToIdle();
                }
                return;
            }

            Vector3 currentPos = _manager.Position ?? Vector3.zero;
            var visit = PlanNextVisit(items, currentPos);
            if (visit == null) return;

            float storeDist = Vector3.Distance(currentPos, visit.Location.Position);
            float destDist = Vector3.Distance(currentPos, _currentWalkTarget);

            // Only redirect if the store is meaningfully closer AND we can afford something there
            if (storeDist < destDist - 10f && CanAffordAnyItem(visit))
            {
                Logger.Msg($"Manager {_manager.Id}: reconsidering route — {visit.Location.DisplayName} ({storeDist:F0}m) is closer than current dest ({destDist:F0}m)");
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

                // Don't resume while in dialogue
                var dialogueHandler = _manager.GameNpc.DialogueHandler;
                if (dialogueHandler != null && dialogueHandler.IsDialogueInProgress) return;

                // Already has a destination
                if (movement.HasDestination) return;

                var pos = _manager.Position ?? Vector3.zero;
                float dist = Vector3.Distance(pos, _currentWalkTarget);
                if (dist > 3f)
                {
                    if (UnityEngine.Time.time - _lastEnsureMovingLog > 10f)
                    {
                        Logger.Msg($"Manager {_manager.Id}: resuming supply run walk (dist={dist:F1}m)");
                        _lastEnsureMovingLog = UnityEngine.Time.time;
                    }

                    Il2CppSystem.Action<NPCMovement.WalkResult> callback = State switch
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
                Logger.Warning($"Manager {_manager.Id}: EnsureMovingDuringRun failed: {ex.Message}");
            }
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

        private Il2CppScheduleOne.NPCs.NPCInventory GetNpcInventory()
        {
            try
            {
                return _manager.GameNpc?.GetComponent<Il2CppScheduleOne.NPCs.NPCInventory>();
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
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Adds purchased items to the NPC inventory.
        /// Skips slots containing cash.
        /// </summary>
        internal static void AddToNpcInventory(Il2CppScheduleOne.NPCs.NPCInventory inventory, string itemId, int quantity, string mgrId)
        {
            if (inventory == null) return;

            try
            {
                var itemDef = Il2CppScheduleOne.Registry.GetItem(itemId);
                if (itemDef == null) { Logger.Warning($"Manager {mgrId}: Registry.GetItem('{itemId}') returned null"); return; }

                var storableDef = itemDef.TryCast<StorableItemDefinition>();
                if (storableDef == null) { Logger.Warning($"Manager {mgrId}: item '{itemId}' is not StorableItemDefinition"); return; }

                var instance = storableDef.GetDefaultInstance(quantity);
                if (instance == null) { Logger.Warning($"Manager {mgrId}: GetDefaultInstance returned null for '{itemId}'"); return; }

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
                    Logger.Warning($"Manager {mgrId}: NPC inventory full, couldn't fit {remaining}x {itemId}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Manager {mgrId}: AddToNpcInventory failed for {itemId}: {ex.Message}");
            }
        }

        private static int GetNpcInventoryQuantity(Il2CppScheduleOne.NPCs.NPCInventory inventory, string itemId)
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

        private static void ClearNpcInventory(Il2CppScheduleOne.NPCs.NPCInventory inventory)
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
        private static int GetFreeNpcSlots(Il2CppScheduleOne.NPCs.NPCInventory inventory)
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
                    Logger.Warning($"Manager {_manager.Id}: RemoveLockerCash(${withdraw:F0}) returned false");
                    return;
                }

                var npcInventory = GetNpcInventory();
                if (npcInventory == null)
                {
                    Logger.Warning($"Manager {_manager.Id}: NPC inventory is null during cash withdrawal");
                    try
                    {
                        DepositCashToStorage(_manager.AssignedLocker.Storage, withdraw);
                    }
                    catch { }
                    return;
                }

                npcInventory.AddCash(withdraw);
                Logger.Msg($"Manager {_manager.Id}: withdrew ${withdraw:F0} cash from locker (total on hand: ${npcInventory.GetCashInInventory():F0})");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Manager {_manager.Id}: WithdrawCashFromLocker failed: {ex.Message}");
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
                Logger.Msg($"Manager {_manager.Id}: returned ${cash:F0} cash to locker");

                // Bump the warning threshold so returned change doesn't false-trigger a flag reset
                if (_manager.NoNightMarketCashTextSent)
                    _manager.LockerCashAtWarning = _manager.GetLockerCash();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Manager {_manager.Id}: ReturnCashToLocker failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Deposits cash into a storage entity, filling existing cash slots (up to $1000 each)
        /// before creating new slots for any remainder.
        /// </summary>
        internal static void DepositCashToStorage(Il2CppScheduleOne.Storage.StorageEntity storage, float amount)
        {
            const float MAX_PER_SLOT = 1000f;
            float remaining = amount;

            // Phase 1: Fill existing cash slots
            for (int i = 0; i < storage.ItemSlots.Count && remaining > 0f; i++)
            {
                var slot = storage.ItemSlots[i];
                if (slot?.ItemInstance == null) continue;

                var existingCash = slot.ItemInstance.TryCast<Il2CppScheduleOne.ItemFramework.CashInstance>();
                if (existingCash == null) continue;

                float space = MAX_PER_SLOT - existingCash.Balance;
                if (space <= 0f) continue;

                float add = Math.Min(remaining, space);
                existingCash.ChangeBalance(add);
                slot.ReplicateStoredInstance();
                remaining -= add;
            }

            // Phase 2: Create new slot(s) for any remainder
            while (remaining > 0f)
            {
                float slotAmount = Math.Min(remaining, MAX_PER_SLOT);
                var newCash = NetworkSingleton<Il2CppScheduleOne.Money.MoneyManager>.Instance
                    .GetCashInstance(slotAmount);
                storage.InsertItem(newCash, true);
                remaining -= slotAmount;
            }
        }

        // ==================================================================
        // Storage quantity helper
        // ==================================================================

        private static int GetStorageQuantity(Il2CppScheduleOne.Storage.StorageEntity storage, string itemId)
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
                Logger.Warning($"GetStorageQuantity error for '{itemId}': {ex.Message}");
            }

            return total;
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
                Logger.Warning($"Manager {_manager.Id}: Warp failed: {ex.Message}");
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

                    var def = slot.ItemInstance.Definition?.TryCast<StorableItemDefinition>();
                    if (def == null) continue;

                    var testInstance = def.GetDefaultInstance(1);
                    if (testInstance != null && storage.StorageEntity.HowManyCanFit(testInstance) > 0)
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
                var itemDef = Il2CppScheduleOne.Registry.GetItem(itemId);
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
        private static int GetStackableCapacity(Il2CppScheduleOne.Storage.StorageEntity storage, string itemId, int stackLimit)
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
                var suppliers = UnityEngine.Object.FindObjectsOfType<Il2CppScheduleOne.Economy.Supplier>();
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
        private static int GetStorageCapacityForItem(Il2CppScheduleOne.Storage.StorageEntity storage, string itemId)
        {
            try
            {
                var itemDef = Il2CppScheduleOne.Registry.GetItem(itemId);
                if (itemDef == null) return int.MaxValue;

                var storableDef = itemDef.TryCast<StorableItemDefinition>();
                if (storableDef == null) return int.MaxValue;

                var testInstance = storableDef.GetDefaultInstance(1);
                if (testInstance == null) return int.MaxValue;

                return storage.HowManyCanFit(testInstance);
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
            Il2CppScheduleOne.Storage.StorageEntity storageEntity,
            HashSet<string> nightMarketOnlyItems,
            Il2CppScheduleOne.NPCs.NPCInventory npcInventory)
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
            if (totalNpcItemSlots > 0)
                Logger.Msg($"Manager {_manager.Id}: [Reservations] rawFree={rawFreeSlots}, npcItems={totalNpcItemSlots}, npcPerItem=[{string.Join(", ", npcSlotsPerItem.Select(kv => $"{kv.Key}={kv.Value}"))}]");

            // Pre-deduct NPC slots for fully-served items (deficit <= 0 but holding NPC items).
            // These items won't participate in distribution but their NPC slots claim raw free slots.
            int reservedByFullyServed = 0;
            for (int i = 0; i < config.StockedItemIds.Length; i++)
            {
                string itemId = config.StockedItemIds[i];
                if (string.IsNullOrEmpty(itemId) || result.ContainsKey(itemId)) continue;

                int threshold = config.StockedThresholds[i];
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

                int threshold = config.StockedThresholds[i];
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
