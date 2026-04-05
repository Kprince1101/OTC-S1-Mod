using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.GameTime;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.NPCs;
using Grid = Il2CppScheduleOne.Tiles.Grid;
#else
using ScheduleOne.NPCs;
using Grid = ScheduleOne.Tiles.Grid;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Manages store customer lifecycle: hourly spawning, browse state machine,
    /// client-side adoption, and despawning.
    /// </summary>
    public class CustomerManager
    {
        public static CustomerManager Instance { get; private set; }

        private int _lastSpawnSlot = -1;  // tracks half-hour slots (hour*2 + 0or1)
        private bool _statePublishNeeded;

        // Customer lifecycle constants
        private const int MaxActiveCustomers = 5;
        private const int MaxCustomersInBuilding = 3;
        private const int SpawnStartHour = StoreHours.OpenHour;
        private const int SpawnEndHour = StoreHours.CloseHour;
        private const float DespawnDistance = 30f;
        private const float NavStuckTimeout = 30f;
        private const int MaxNavRetries = 2;


        // Client-side adoption
        private readonly Dictionary<string, PendingAdoption> _pendingAdoptions = new();
        private float _lastAdoptionRetry;

        private class PendingAdoption
        {
            public string CustomerId;
            public int Seed;
            public int SpawnPointIndex;
            public CustomerState State;
            public int NetworkObjectId;
            public float CreatedTime;
        }

        /// <summary>
        /// Creates the manager and hooks into game time events.
        /// </summary>
        public CustomerManager()
        {
            Instance = this;

            TimeManager.OnTick += OnTimeTick;
            TimeManager.OnDayPass += OnDayPass;
        }

        private void OnTimeTick()
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                int currentTime = TimeManager.CurrentTime;
                int currentHour = currentTime / 100;
                int currentMinute = currentTime % 100;
                int spawnSlot = currentHour * 2 + (currentMinute >= 30 ? 1 : 0);

                if (spawnSlot != _lastSpawnSlot && currentHour >= SpawnStartHour && currentHour < SpawnEndHour)
                    _lastSpawnSlot = spawnSlot;

                // Process deferred deals 30 min before open — NPCs start walking early
                if (currentMinute == 30 && currentHour == StoreHours.OpenHour - 1)
                    DispensaryDealManager.ProcessDeferredDeals();

                ProcessCustomerLifecycles();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Customer, $"OnTimeTick failed: {ex.Message}");
            }
        }

        private void OnDayPass()
        {
            _lastSpawnSlot = -1;
            DispensaryDealManager.OnDayPass();
            foreach (var counter in CheckoutCounter.AllCounters)
            {
                counter.Queue.Clear();
                counter.Screen?.HideCheckoutInfo();
            }

            foreach (var customer in CustomerInstance.Active.Values.ToList())
            {
                try { customer.Despawn(); }
                catch { }
            }

            _statePublishNeeded = true;
        }

        private void ProcessCustomerLifecycles()
        {
            var toRemove = new List<string>();
            var snapshot = CustomerInstance.Active.Values.ToList();

            foreach (var customer in snapshot)
            {
                if (!customer.IsValid)
                {
                    toRemove.Add(customer.Id);
                    continue;
                }

                customer.CheckStuck();
                customer.EnsureMoving();

                var prevState = customer.State;

                switch (customer.State)
                {
                    case CustomerState.WalkingToStore:
                        if (customer.ArrivedAtDestination)
                        {
                            customer.ArrivedAtDestination = false;
                            customer.State = CustomerState.EnteringStore;
                            customer.DisableObstacleAvoidance();
                            customer.SendToInterior(customer.Target.RoomCenterLocal, () =>
                            {
                                customer.ArrivedAtDestination = true;
                            });
                        }
                        break;

                    case CustomerState.EnteringStore:
                        if (!customer.ArrivedAtDestination && customer.TimeInCurrentState > NavStuckTimeout)
                        {
                            if (customer.NavRetries >= MaxNavRetries)
                            {
                                OTCLog.Warning(OTCLog.Systems.Customer, $"{customer.Id}: EnteringStore failed after {MaxNavRetries} retries, resetting to WalkingToStore");
                                customer.RestoreObstacleAvoidance();
                                var approach = customer.Target?.ExteriorApproachPosition ?? Vector3.zero;
                                customer.WarpTo(approach);
                                customer.State = CustomerState.WalkingToStore;
                                customer.WalkTo(approach);
                                break;
                            }
                            OTCLog.Warning(OTCLog.Systems.Customer, $"{customer.Id}: stuck in EnteringStore for {customer.TimeInCurrentState:F0}s, resending to interior (retry {customer.NavRetries + 1}/{MaxNavRetries})");
                            customer.SendToInterior(customer.Target.RoomCenterLocal, () =>
                            {
                                customer.ArrivedAtDestination = true;
                            });
                            customer.ResetStateTimer();
                            break;
                        }
                        if (customer.ArrivedAtDestination)
                        {
                            customer.ArrivedAtDestination = false;

                            // Go directly to checkout counter — budtender/player handles product selection
                            var bestCounter = FindBestCounter(customer.Position ?? Vector3.zero, customer.Target?.Grid);
                            if (bestCounter != null)
                            {
                                customer.State = CustomerState.CheckingOut;
                                customer.CheckoutStartHour = TimeManager.CurrentTime / 100;
                                customer.CheckoutStartTime = TimeManager.CurrentTime;
                                customer.AssignedCounter = bestCounter;
                                bestCounter.Queue.Add(customer.Id);
                                int queueIdx = bestCounter.Queue.Count - 1;
                                customer.SendToInterior(GetQueuePositionLocal(bestCounter, queueIdx, customer.Target), () =>
                                {
                                    customer.ArrivedAtDestination = true;
                                });
                            }
                            else
                            {
                                // No enabled counter available — leave
                                customer.ShowDisappointed();
                                customer.State = CustomerState.ExitingStore;
                                customer.RecallFromBuilding();
                            }
                        }
                        break;

                    case CustomerState.CheckingOut:
                        // 4-hour timeout — customer gives up waiting (per-customer, minute-level)
                        if (customer.CheckoutStartTime > 0)
                        {
                            int startMins = (customer.CheckoutStartTime / 100) * 60 + (customer.CheckoutStartTime % 100);
                            int nowMins = (TimeManager.CurrentTime / 100) * 60 + (TimeManager.CurrentTime % 100);
                            if (nowMins < startMins) nowMins += 24 * 60;
                            if (nowMins - startMins >= 240)
                            {
                                bool beingCheckedOut = CheckoutProcess.Instance != null &&
                                                       CheckoutProcess.Instance.CustomerId == customer.Id;
                                if (!beingCheckedOut)
                                {
                                    customer.State = CustomerState.ExitingStore;
                                    customer.RecallFromBuilding();
                                    break;
                                }
                            }
                        }

                        // Face the counter when arriving at any queue position
                        var assignedCounter = customer.AssignedCounter;
                        if (assignedCounter != null && customer.ArrivedAtDestination && customer.CheckoutArrivalTime == 0f)
                        {
                            var counterPos1 = assignedCounter.CounterPosition;
                            bool isFront = assignedCounter.Queue.Count > 0 &&
                                           assignedCounter.Queue[0] == customer.Id;

                            if (isFront)
                            {
                                customer.CheckoutArrivalTime = Time.time;
                                if (counterPos1.HasValue)
                                    customer.FaceAndAnimate(counterPos1.Value);

                                // Only show POS if customer already has products (save/reload edge case)
                                // Otherwise budtender/player will handle via consultation
                                if (customer.SelectedProducts.Count > 0)
                                {
                                    if (assignedCounter.IsStaffed)
                                        ShowBudtenderPOS(assignedCounter, customer);
                                    else
                                        assignedCounter.Screen?.ShowCheckoutInfo(customer.SelectedProducts);
                                }
                            }
                            else
                            {
                                // Mark as faced so we don't repeat; use -1 to distinguish from front
                                customer.CheckoutArrivalTime = -1f;

                                // Face the person ahead in line (use their actual NPC position)
                                int myIdx = assignedCounter.Queue.IndexOf(customer.Id);
                                if (myIdx > 0)
                                {
                                    string aheadId = assignedCounter.Queue[myIdx - 1];
                                    if (CustomerInstance.Active.TryGetValue(aheadId, out var ahead) && ahead.Position.HasValue)
                                        customer.FaceToward(ahead.Position.Value);
                                    else if (counterPos1.HasValue)
                                        customer.FaceToward(counterPos1.Value);
                                }
                                else if (counterPos1.HasValue)
                                    customer.FaceToward(counterPos1.Value);
                            }
                        }
                        break;

                    case CustomerState.ExitingStore:
                        if (customer.TimeInCurrentState > NavStuckTimeout)
                        {
                            if (customer.NavRetries >= MaxNavRetries)
                            {
                                OTCLog.Warning(OTCLog.Systems.Customer, $"{customer.Id}: ExitingStore failed after {MaxNavRetries} retries, warping outside");
                                customer.RestoreObstacleAvoidance();
                                customer.SetAvoidancePriority(50);
                                var exitTarget = customer.WarpReturnPosition
                                    ?? customer.SpawnPoint?.Position
                                    ?? customer.Target?.ExitWalkPosition
                                    ?? Vector3.zero;
                                customer.WarpTo(exitTarget);
                                customer.State = CustomerState.LeavingStore;
                                customer.WalkTo(exitTarget);
                                break;
                            }
                            OTCLog.Warning(OTCLog.Systems.Customer, $"{customer.Id}: stuck in ExitingStore for {customer.TimeInCurrentState:F0}s, retrying recall ({customer.NavRetries + 1}/{MaxNavRetries})");
                            customer.RecallFromBuilding();
                            customer.ResetStateTimer();
                            break;
                        }
                        // RecallNPC handles exit via doorway — poll until NPC is outside
                        if (customer.Position.HasValue)
                        {
                            var nav = customer.Target?.NavBuilder;
                            if (nav == null || !nav.IsNPCInside(customer.GameNpc.Movement))
                            {
                                customer.State = CustomerState.LeavingStore;
                                customer.RestoreObstacleAvoidance();
                                customer.SetAvoidancePriority(50);

                                // Deal customers walk back to their warp point; random customers to spawn point
                                var exitTarget = customer.WarpReturnPosition
                                    ?? customer.SpawnPoint?.Position
                                    ?? customer.Target?.ExitWalkPosition
                                    ?? Vector3.zero;
                                customer.WalkTo(exitTarget);
                            }
                        }
                        break;

                    case CustomerState.LeavingStore:
                        if (customer.ArrivedAtDestination)
                        {
                            toRemove.Add(customer.Id);
                        }
                        else if (customer.Position.HasValue &&
                                 !DrifterHotspots.IsAnyPlayerNearby(customer.Position.Value, DespawnDistance))
                        {
                            toRemove.Add(customer.Id);
                        }
                        break;
                }

                if (customer.State != prevState)
                    _statePublishNeeded = true;
            }

            // Clean up stale queue entries across all counters
            foreach (var counter in CheckoutCounter.AllCounters)
            {
                int queueBefore = counter.Queue.Count;
                counter.Queue.RemoveAll(id =>
                {
                    if (!CustomerInstance.Active.TryGetValue(id, out var c)) return true;
                    return c.State != CustomerState.CheckingOut;
                });
                if (counter.Queue.Count != queueBefore)
                    AdvanceQueue(counter);
            }

            if (toRemove.Count > 0)
            {
                _statePublishNeeded = true;
                foreach (var id in toRemove)
                {
                    if (CustomerInstance.Active.TryGetValue(id, out var c))
                        c.Despawn();
                }
            }
        }

        /// <summary>
        /// Counts customers currently inside the building (EnteringStore through ExitingStore).
        /// </summary>
        private static int CountCustomersInBuilding()
        {
            int count = 0;
            foreach (var c in CustomerInstance.Active.Values)
            {
                if (c.State >= CustomerState.EnteringStore && c.State <= CustomerState.ExitingStore)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Collects shelf center positions currently assigned to other browsing customers
        /// in the same building, so new customers prefer unoccupied shelves.
        /// </summary>
        private static List<Vector3> GetOccupiedShelfPositions(CustomerInstance excludeCustomer)
        {
            var occupied = new List<Vector3>();
            foreach (var c in CustomerInstance.Active.Values)
            {
                if (c == excludeCustomer) continue;
                if (c.State != CustomerState.Browsing) continue;
                if (c.Target != excludeCustomer.Target) continue;
                if (c.BrowseShelfPositions == null) continue;

                for (int i = 0; i < c.BrowseShelfPositions.Count; i++)
                    occupied.Add(c.BrowseShelfPositions[i]);
            }
            return occupied;
        }

        /// <summary>
        /// Returns the LOCAL queue position for a given index, using cached BFS queue slots.
        /// Falls back to dogpiling on the last slot if index exceeds available slots.
        /// </summary>
        private static Vector3 GetQueuePositionLocal(CheckoutCounterInstance counter, int queueIndex,
            BuildingTarget target)
        {
            if (target == null) return Vector3.zero;

            var slots = counter.CachedQueueSlots;
            if (slots == null || slots.Count == 0)
            {
                slots = QueueSlotCalculator.Compute(target, counter);
                counter.CachedQueueSlots = slots;
            }

            if (slots.Count == 0) return Vector3.zero;
            if (queueIndex < slots.Count)
                return slots[queueIndex];

            // Dogpile: stand on last valid slot
            return slots[slots.Count - 1];
        }

        /// <summary>
        /// Re-sends all queued customers to their updated positions at the given counter.
        /// </summary>
        internal void AdvanceQueue(CheckoutCounterInstance counter)
        {
            for (int i = 0; i < counter.Queue.Count; i++)
            {
                if (!CustomerInstance.Active.TryGetValue(counter.Queue[i], out var c)) continue;
                c.ArrivedAtDestination = false;
                c.CheckoutArrivalTime = 0f;
                c.SendToInterior(GetQueuePositionLocal(counter, i, c.Target), () =>
                {
                    c.ArrivedAtDestination = true;
                });
            }

            if (counter.Queue.Count == 0)
                counter.Screen?.HideCheckoutInfo();
        }

        /// <summary>
        /// Called by CheckoutProcess when a checkout finishes (complete or abort).
        /// Removes the customer from the queue and advances remaining customers.
        /// </summary>
        public void OnCheckoutComplete(string customerId)
        {
            // Find which counter this customer was at
            foreach (var counter in CheckoutCounter.AllCounters)
            {
                if (counter.Queue.Remove(customerId))
                {
                    counter.Screen?.HideCheckoutInfo();
                    AdvanceQueue(counter);
                    break;
                }
            }

            // Clear assigned counter on the customer instance
            if (CustomerInstance.Active.TryGetValue(customerId, out var customer))
                customer.AssignedCounter = null;

            _statePublishNeeded = true;
        }

        /// <summary>
        /// Finds the counter with the shortest queue. Random tiebreak if equal.
        /// Returns null if no counters are registered.
        /// </summary>
        private static CheckoutCounterInstance FindBestCounter(Vector3 customerWorldPos, Grid targetGrid)
        {
            var counters = CheckoutCounter.AllCounters;

            int minQueue = int.MaxValue;
            var candidates = new List<CheckoutCounterInstance>();

            for (int i = 0; i < counters.Count; i++)
            {
                if (targetGrid != null && counters[i].ParentGrid != targetGrid) continue;
                if (!counters[i].IsEnabled) continue;

                // Skip counters whose GO is at the origin (not yet positioned by FishNet)
                var cPos = counters[i].CounterPosition;
                if (!cPos.HasValue || cPos.Value.sqrMagnitude < 1f) continue;

                int qLen = counters[i].Queue.Count;
                if (qLen < minQueue)
                {
                    minQueue = qLen;
                    candidates.Clear();
                    candidates.Add(counters[i]);
                }
                else if (qLen == minQueue)
                {
                    candidates.Add(counters[i]);
                }
            }

            return candidates.Count > 0 ? candidates[UnityEngine.Random.Range(0, candidates.Count)] : null;
        }

        // =====================================================================
        //  Serialization (host → client sync)
        // =====================================================================

        /// <summary>
        /// Publishes customer state to clients if changes occurred.
        /// Called from Core.OnLateUpdate on host.
        /// </summary>
        public void PublishIfNeeded()
        {
            if (!_statePublishNeeded) return;
            _statePublishNeeded = false;
            ConfigSyncData.Instance?.PublishCustomerState();
        }

        /// <summary>
        /// Serializes all active customers for SyncVar transmission.
        /// Format: "id:seed:spawnIdx:state:netObjId;id2:seed2:..."
        /// CheckingOut customers include counter index, start time, and optional products:
        /// "id:seed:spawnIdx:state:netObjId:counterIdx:startTime:prodId,pkgId,qty,name,price,quality~prod2~..."
        /// </summary>
        public string SerializeCustomerState()
        {
            if (CustomerInstance.Active.Count == 0) return "";

            var parts = new List<string>();
            foreach (var kvp in CustomerInstance.Active)
            {
                var c = kvp.Value;
                int spawnIdx = CustomerSpawnPoints.GetSpawnPointIndex(c.SpawnPoint);
                string entry = $"{c.Id}:{c.SpawnSeed}:{spawnIdx}:{(int)c.State}:{c.NetworkObjectId}";

                // Include counter index and products for CheckingOut customers
                if (c.State == CustomerState.CheckingOut)
                {
                    int counterIdx = CheckoutCounter.GetCounterIndex(c.AssignedCounter);
                    entry += $":{counterIdx}:{c.CheckoutStartTime}";

                    if (c.SelectedProducts.Count > 0)
                    {
                        var prods = new List<string>();
                        foreach (var p in c.SelectedProducts)
                            prods.Add($"{p.ProductId},{p.PackagingId},{p.Quantity},{p.ProductName},{p.Price.ToString(System.Globalization.CultureInfo.InvariantCulture)},{p.QualityLevel}");
                        entry += ":" + string.Join("~", prods);
                    }
                }

                parts.Add(entry);
            }
            return string.Join(";", parts);
        }

        // =====================================================================
        //  Client-side adoption
        // =====================================================================

        /// <summary>
        /// Applies customer state received from the host SyncVar.
        /// Creates or updates CustomerInstance wrappers on the client.
        /// </summary>
        public void ApplyHostCustomerState(string payload)
        {
            if (NetworkHelper.IsHost) return;

            var hostCustomers = new HashSet<string>();

            if (!string.IsNullOrEmpty(payload))
            {
                var entries = payload.Split(';');
                foreach (var entry in entries)
                {
                    var parts = entry.Split(':');
                    if (parts.Length < 5) continue;

                    string customerId = parts[0];
                    if (!int.TryParse(parts[1], out int seed)) continue;
                    if (!int.TryParse(parts[2], out int spawnIdx)) continue;
                    if (!int.TryParse(parts[3], out int stateInt)) continue;
                    if (!int.TryParse(parts[4], out int netObjId)) continue;

                    var state = (CustomerState)stateInt;
                    hostCustomers.Add(customerId);

                    // Parse counter index (6th), checkout start time (7th), products (8th) for CheckingOut
                    int counterIdx = -1;
                    int checkoutStartTime = 0;
                    List<CustomerInstance.SelectedProduct> products = null;
                    if (parts.Length > 5)
                    {
                        int.TryParse(parts[5], out counterIdx);
                        if (parts.Length > 6)
                            int.TryParse(parts[6], out checkoutStartTime);
                        if (parts.Length > 7 && !string.IsNullOrEmpty(parts[7]))
                            products = ParseSelectedProducts(parts[7]);
                    }

                    // Already tracked — update state, counter assignment, and products
                    if (CustomerInstance.Active.TryGetValue(customerId, out var existing))
                    {
                        existing.State = state;
                        if (counterIdx >= 0)
                            existing.AssignedCounter = CheckoutCounter.GetCounterByIndex(counterIdx);
                        if (checkoutStartTime > 0)
                            existing.CheckoutStartTime = checkoutStartTime;
                        if (products != null && products.Count > 0 && existing.SelectedProducts.Count == 0)
                        {
                            existing.SelectedProducts.Clear();
                            existing.SelectedProducts.AddRange(products);
                        }
                        continue;
                    }

                    // Already pending — update
                    if (_pendingAdoptions.TryGetValue(customerId, out var existingPa))
                    {
                        existingPa.State = state;
                        if (netObjId > 0)
                            existingPa.NetworkObjectId = netObjId;
                        continue;
                    }

                    // Try to adopt
                    var spawnPoint = CustomerSpawnPoints.GetSpawnPointByIndex(spawnIdx);
                    var npc = FindNetworkCustomer(netObjId);
                    if (npc != null)
                    {
                        var adopted = CustomerInstance.Adopt(customerId, seed, spawnPoint, state, npc);
                        if (adopted != null)
                        {
                            if (counterIdx >= 0)
                                adopted.AssignedCounter = CheckoutCounter.GetCounterByIndex(counterIdx);
                            if (checkoutStartTime > 0)
                                adopted.CheckoutStartTime = checkoutStartTime;
                            if (products != null)
                                adopted.SelectedProducts.AddRange(products);
                        }
                    }
                    else
                    {
                        _pendingAdoptions[customerId] = new PendingAdoption
                        {
                            CustomerId = customerId,
                            Seed = seed,
                            SpawnPointIndex = spawnIdx,
                            State = state,
                            NetworkObjectId = netObjId,
                            CreatedTime = Time.time
                        };
                    }
                }
            }

            // Remove customers not on host
            var localIds = CustomerInstance.Active.Keys.ToList();
            foreach (var id in localIds)
            {
                if (!hostCustomers.Contains(id))
                {
                    try { CustomerInstance.Active[id].Despawn(); }
                    catch { }
                }
            }
            foreach (var id in _pendingAdoptions.Keys.ToList())
            {
                if (!hostCustomers.Contains(id))
                    _pendingAdoptions.Remove(id);
            }

            // Client POS: show checkout info for front-of-queue customer with products
            UpdateClientPOS();
        }

        /// <summary>
        /// On client, shows POS checkout info for each counter's front-of-queue customer.
        /// </summary>
        private void UpdateClientPOS()
        {
            if (NetworkHelper.IsHost) return;

            foreach (var counter in CheckoutCounter.AllCounters)
            {
                // Rebuild queue from state if empty
                if (counter.Queue.Count == 0)
                {
                    foreach (var c in CustomerInstance.Active.Values)
                    {
                        if (c.State == CustomerState.CheckingOut &&
                            c.AssignedCounter == counter &&
                            !counter.Queue.Contains(c.Id))
                        {
                            counter.Queue.Add(c.Id);
                        }
                    }
                }

                if (counter.Queue.Count > 0 &&
                    CustomerInstance.Active.TryGetValue(counter.Queue[0], out var front) &&
                    front.State == CustomerState.CheckingOut &&
                    front.SelectedProducts.Count > 0 &&
                    front.CheckoutArrivalTime == 0f)
                {
                    front.CheckoutArrivalTime = Time.time;
                    if (counter.IsStaffed)
                        ShowBudtenderPOS(counter, front);
                    else
                        counter.Screen?.ShowCheckoutInfo(front.SelectedProducts);
                }
            }
        }

        /// <summary>
        /// Shows the POS screen with what the budtender can find in storage,
        /// rather than what the player has in inventory.
        /// </summary>
        private static void ShowBudtenderPOS(CheckoutCounterInstance counter, CustomerInstance customer)
        {
            var fetchTasks = BudtenderStorageSearch.FindProducts(counter, customer.SelectedProducts);

            var placedUnitCounts = new Dictionary<string, int>();
            float placedTotal = 0f;

            foreach (var task in fetchTasks)
            {
                if (placedUnitCounts.ContainsKey(task.ProductId))
                    placedUnitCounts[task.ProductId] += task.UnitCount;
                else
                    placedUnitCounts[task.ProductId] = task.UnitCount;
                placedTotal += task.Price;
            }

            var missingKeys = new HashSet<string>();
            foreach (var sel in customer.SelectedProducts)
            {
                int needed = sel.Quantity > 0 ? sel.Quantity : 1;
                if (!placedUnitCounts.TryGetValue(sel.ProductId, out int found) || found < needed)
                    missingKeys.Add(sel.ProductId);
            }

            counter.Screen?.ShowBudtendingStatus(
                customer.SelectedProducts, missingKeys, placedUnitCounts, placedTotal);
        }

        /// <summary>
        /// Parses "productId,packagingId,qty,name,price,quality~prod2~..." into SelectedProduct list.
        /// </summary>
        private static List<CustomerInstance.SelectedProduct> ParseSelectedProducts(string data)
        {
            var result = new List<CustomerInstance.SelectedProduct>();
            var items = data.Split('~');
            foreach (var item in items)
            {
                var fields = item.Split(',');
                if (fields.Length < 6) continue;

                if (!int.TryParse(fields[2], out int qty)) qty = 1;
                if (!float.TryParse(fields[4], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float price)) price = 0f;
                if (!int.TryParse(fields[5], out int quality)) quality = 0;

                result.Add(new CustomerInstance.SelectedProduct
                {
                    ProductId = fields[0],
                    PackagingId = fields[1],
                    Quantity = qty,
                    ProductName = fields[3],
                    Price = price,
                    QualityLevel = quality
                });
            }
            return result;
        }

        /// <summary>
        /// Retries pending adoptions for customers whose FishNet NPC hadn't arrived yet.
        /// Called from Core.OnLateUpdate.
        /// </summary>
        public void RetryPendingAdoptions()
        {
            if (_pendingAdoptions.Count == 0) return;

            float now = Time.time;
            if (now - _lastAdoptionRetry < 0.5f) return;
            _lastAdoptionRetry = now;

            var completed = new List<string>();
            foreach (var kv in _pendingAdoptions)
            {
                var pa = kv.Value;

                if (now - pa.CreatedTime > 120f)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer, $"Giving up adoption for {pa.CustomerId} (timeout)");
                    completed.Add(kv.Key);
                    continue;
                }

                var npc = FindNetworkCustomer(pa.NetworkObjectId);
                if (npc == null) continue;

                var spawnPoint = CustomerSpawnPoints.GetSpawnPointByIndex(pa.SpawnPointIndex);
                var customer = CustomerInstance.Adopt(pa.CustomerId, pa.Seed, spawnPoint, pa.State, npc);
                if (customer != null)
                    completed.Add(kv.Key);
            }

            foreach (var id in completed)
                _pendingAdoptions.Remove(id);
        }

        /// <summary>
        /// Finds a FishNet-replicated NPC by its NetworkObject.ObjectId.
        /// </summary>
        private NPC FindNetworkCustomer(int objectId)
        {
            if (objectId <= 0) return null;

            var registry = NPCManager.NPCRegistry;
            if (registry == null) return null;

            // Build set of already-tracked NPCs
            var trackedNpcs = new HashSet<NPC>();
            foreach (var c in CustomerInstance.Active.Values)
            {
                if (c.GameNpc != null) trackedNpcs.Add(c.GameNpc);
            }
            foreach (var d in DrifterInstance.Active.Values)
            {
                if (d.GameNpc != null) trackedNpcs.Add(d.GameNpc);
            }

            for (int i = 0; i < registry.Count; i++)
            {
                var npc = registry[i];
                if (npc == null || npc.gameObject == null) continue;
                if (trackedNpcs.Contains(npc)) continue;

                try
                {
                    var netObj = npc.NetworkObject;
                    if (netObj != null && netObj.ObjectId == objectId)
                        return npc;
                }
                catch { }
            }

            return null;
        }

        // =====================================================================
        //  Cleanup
        // =====================================================================

        /// <summary>
        /// Unhooks time events and despawns all customers. Call on shutdown.
        /// </summary>
        public void Cleanup()
        {
            TimeManager.OnTick -= OnTimeTick;
            TimeManager.OnDayPass -= OnDayPass;

            _pendingAdoptions.Clear();
            CustomerInstance.CleanupAll();
            Instance = null;
        }
    }
}
