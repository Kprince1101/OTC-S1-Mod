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
        private const int SpawnStartHour = 8;
        private const int SpawnEndHour = 20;
        private const float DespawnDistance = 30f;

        private const float QueueSpacing = 1.0f;

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

                // Update switch messages when hour boundaries change (8am/8pm)
                if (currentMinute == 0 && (currentHour == 8 || currentHour == 20))
                    WestvilleShack.UpdateOpenCloseSwitchMessages();

                // Process deferred deals at opening time — morning rush
                if (currentMinute == 0 && currentHour == 8)
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
                            customer.SendToInterior(customer.Target.RoomCenterLocal, () =>
                            {
                                customer.ArrivedAtDestination = true;
                            });
                        }
                        break;

                    case CustomerState.EnteringStore:
                        if (customer.ArrivedAtDestination)
                        {
                            customer.ArrivedAtDestination = false;
                            var occupied = GetOccupiedShelfPositions(customer);
                            var browsePositions = customer.Target.GetInteriorBrowsePositions(out var shelfCenters, occupied);
                            if (browsePositions.Count > 0)
                            {
                                customer.StartBrowsing(browsePositions, shelfCenters);
                                customer.State = CustomerState.Browsing;
                            }
                            else
                            {
                                customer.State = CustomerState.LookingAround;
                                customer.SendToInterior(customer.Target.RoomCenterLocal, () =>
                                {
                                    customer.ArrivedAtDestination = true;
                                });
                            }
                        }
                        break;

                    case CustomerState.LookingAround:
                        if (customer.ArrivedAtDestination && customer.LookAroundEndTime == 0f)
                        {
                            customer.LookAroundEndTime = Time.time + 8f;
                        }
                        if (customer.LookAroundEndTime > 0f && Time.time >= customer.LookAroundEndTime)
                        {
                            customer.LookAroundEndTime = 0f;
                            customer.ArrivedAtDestination = false;
                            var retryOccupied = GetOccupiedShelfPositions(customer);
                            var retryPositions = customer.Target.GetInteriorBrowsePositions(out var retryShelfCenters, retryOccupied);
                            if (retryPositions.Count > 0)
                            {
                                customer.StartBrowsing(retryPositions, retryShelfCenters);
                                customer.State = CustomerState.Browsing;
                            }
                            else
                            {
                                customer.State = CustomerState.ExitingStore;
                                customer.RecallFromBuilding();
                            }
                        }
                        break;

                    case CustomerState.Browsing:
                        if (customer.TickBrowsing())
                        {
                            customer.ArrivedAtDestination = false;

                            // All shelves visited — decide what to buy based on what was observed
                            customer.DecidePurchases();

                            // Customer found nothing they liked — leave without buying
                            if (customer.SelectedProducts.Count == 0)
                            {
                                customer.ShowDisappointed();
                                customer.State = CustomerState.ExitingStore;
                                customer.RecallFromBuilding();
                                break;
                            }

                            var bestCounter = FindBestCounter(customer.Position ?? Vector3.zero, customer.Target?.Grid);
                            if (bestCounter != null)
                            {
                                customer.State = CustomerState.CheckingOut;
                                customer.CheckoutStartHour = TimeManager.CurrentTime / 100;
                                customer.AssignedCounter = bestCounter;
                                bestCounter.Queue.Add(customer.Id);
                                int queueIdx = bestCounter.Queue.Count - 1;
                                customer.SendToInterior(GetQueuePositionLocal(bestCounter, queueIdx, customer.Target?.NavBuilder), () =>
                                {
                                    customer.ArrivedAtDestination = true;
                                });
                            }
                            else
                            {
                                // No counter — exit the store
                                customer.State = CustomerState.ExitingStore;
                                customer.RecallFromBuilding();
                            }
                        }
                        break;

                    case CustomerState.CheckingOut:
                        // 4-hour timeout — customer gives up waiting
                        int currentHour = TimeManager.CurrentTime / 100;
                        if (customer.CheckoutStartHour > 0 && currentHour >= customer.CheckoutStartHour + 4)
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

                        // Only the front-of-queue customer is eligible for checkout
                        var assignedCounter = customer.AssignedCounter;
                        if (assignedCounter != null && assignedCounter.Queue.Count > 0 &&
                            assignedCounter.Queue[0] == customer.Id)
                        {
                            if (customer.ArrivedAtDestination && customer.CheckoutArrivalTime == 0f)
                            {
                                customer.CheckoutArrivalTime = Time.time;
                                var counterPos1 = assignedCounter.CounterPosition;
                                if (counterPos1.HasValue)
                                    customer.FaceAndAnimate(counterPos1.Value);
                                assignedCounter.Screen?.ShowCheckoutInfo(customer.SelectedProducts);
                            }
                        }
                        break;

                    case CustomerState.ExitingStore:
                        // RecallNPC handles exit via doorway — poll until NPC is outside
                        if (customer.Position.HasValue)
                        {
                            var nav = customer.Target?.NavBuilder;
                            if (nav == null || !nav.IsNPCInside(customer.GameNpc.Movement))
                            {
                                customer.State = CustomerState.LeavingStore;
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
        /// Returns the world position for a given queue index at a specific counter.
        /// Index 0 = front (at counter), subsequent customers stand further back.
        /// </summary>
        private static Vector3 GetQueuePosition(CheckoutCounterInstance counter, int queueIndex)
        {
            var counterPos = counter.CounterPosition.Value;
            var counterForward = counter.CounterTransform.forward;
            return counterPos - counterForward * (1.0f + queueIndex * QueueSpacing);
        }

        /// <summary>
        /// Returns the LOCAL position for a given queue index (for SendNPCToPosition).
        /// </summary>
        private static Vector3 GetQueuePositionLocal(CheckoutCounterInstance counter, int queueIndex,
            S1MAPI.Building.NavigationBuilder nav)
        {
            var worldPos = GetQueuePosition(counter, queueIndex);
            return nav != null ? nav.WorldToLocal(worldPos) : worldPos;
        }

        /// <summary>
        /// Re-sends all queued customers to their updated positions at the given counter.
        /// </summary>
        private void AdvanceQueue(CheckoutCounterInstance counter)
        {
            for (int i = 0; i < counter.Queue.Count; i++)
            {
                if (!CustomerInstance.Active.TryGetValue(counter.Queue[i], out var c)) continue;
                c.ArrivedAtDestination = false;
                c.CheckoutArrivalTime = 0f;
                c.SendToInterior(GetQueuePositionLocal(counter, i, c.Target?.NavBuilder), () =>
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
        /// CheckingOut customers include counter index and optional products:
        /// "id:seed:spawnIdx:state:netObjId:counterIdx:prodId,pkgId,qty,name,price,quality~prod2~..."
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
                    entry += $":{counterIdx}";

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

                    // Parse counter index (6th field) and products (7th field) for CheckingOut
                    int counterIdx = -1;
                    List<CustomerInstance.SelectedProduct> products = null;
                    if (parts.Length > 5)
                    {
                        int.TryParse(parts[5], out counterIdx);
                        if (parts.Length > 6 && !string.IsNullOrEmpty(parts[6]))
                            products = ParseSelectedProducts(parts[6]);
                    }

                    // Already tracked — update state, counter assignment, and products
                    if (CustomerInstance.Active.TryGetValue(customerId, out var existing))
                    {
                        existing.State = state;
                        if (counterIdx >= 0)
                            existing.AssignedCounter = CheckoutCounter.GetCounterByIndex(counterIdx);
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
                    counter.Screen?.ShowCheckoutInfo(front.SelectedProducts);
                }
            }
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
