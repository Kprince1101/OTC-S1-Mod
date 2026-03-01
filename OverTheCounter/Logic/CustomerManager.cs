using MelonLoader;
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
#else
using ScheduleOne.NPCs;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Manages store customer lifecycle: hourly spawning, browse state machine,
    /// client-side adoption, and despawning.
    /// </summary>
    public class CustomerManager
    {
        private static readonly MelonLogger.Instance Logger = new("OTC:CustomerManager");

        public static CustomerManager Instance { get; private set; }

        private int _lastSpawnSlot = -1;  // tracks half-hour slots (hour*2 + 0or1)
        private int _customerIdCounter;
        private bool _statePublishNeeded;

        // Customer lifecycle constants
        private const int MaxActiveCustomers = 5;
        private const int MaxCustomersInBuilding = 3;
        private const int SpawnStartHour = 8;
        private const int SpawnEndHour = 20;
        private const float DespawnDistance = 30f;

        // Checkout queue (ordered customer IDs waiting at counter)
        private readonly List<string> _checkoutQueue = new();
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
        public CustomerManager(MelonLogger.Instance logger)
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
                {
                    _lastSpawnSlot = spawnSlot;
                    TrySpawnCustomer();
                }

                // Update switch messages when hour boundaries change (8am/8pm)
                if (currentMinute == 0 && (currentHour == 8 || currentHour == 20))
                    WestvilleShack.UpdateOpenCloseSwitchMessages();

                ProcessCustomerLifecycles();
            }
            catch (Exception ex)
            {
                Logger.Error($"OnTimeTick failed: {ex.Message}");
            }
        }

        private void OnDayPass()
        {
            _lastSpawnSlot = -1;
            _checkoutQueue.Clear();
            ComputerScreen.HideCheckoutInfo();

            foreach (var customer in CustomerInstance.Active.Values.ToList())
            {
                try { customer.Despawn(); }
                catch { }
            }

            _statePublishNeeded = true;
        }

        private void TrySpawnCustomer()
        {
            if (!(SaveData.PropertySaveData.Instance?.IsPropertyOwned(SaveData.PropertySaveData.ShackId) ?? false))
                return;

            if (!WestvilleShack.IsStoreOpen)
                return;

            if (!CustomerSpawnPoints.HasPackagedProduct())
                return;

            if (CustomerInstance.Active.Count >= MaxActiveCustomers)
                return;

            if (CountCustomersInBuilding() >= MaxCustomersInBuilding - 1)
                return;

            try
            {
                int day = TimeManager.ElapsedDays;
                int time = TimeManager.CurrentTime;

                string id = $"customer_{day}_{time}_{_customerIdCounter++}";
                int seed = id.GetHashCode() ^ UnityEngine.Random.Range(int.MinValue, int.MaxValue);

                var spawnPoint = CustomerSpawnPoints.GetRandomSpawnPoint();
                if (spawnPoint == null)
                {
                    Logger.Warning("No spawn point available for customer");
                    return;
                }

                var customer = CustomerInstance.Create(id, seed, spawnPoint);
                if (customer == null) return;

                customer.State = CustomerState.WalkingToStore;
                customer.WalkTo(CustomerSpawnPoints.StairApproachPosition);

                _statePublishNeeded = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"TrySpawnCustomer failed: {ex.Message}");
            }
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
                            if (!customer.SwitchToEmployeeNavMesh())
                                Logger.Warning($"{customer.Id} failed to switch to employee NavMesh");
                            // Warp to ramp base (runtime Employee mesh), then walk up
                            // through door to room center. Target must be >2m inside the
                            // wall (X=-161.4) so the 2m walk tolerance doesn't trigger
                            // before the NPC enters the building.
                            customer.WarpTo(CustomerSpawnPoints.RampBottomPosition);
                            customer.WalkTo(CustomerSpawnPoints.RoomCenterPosition);
                        }
                        break;

                    case CustomerState.EnteringStore:
                        if (customer.ArrivedAtDestination)
                        {
                            customer.ArrivedAtDestination = false;
                            var browsePositions = CustomerSpawnPoints.GetInteriorBrowsePositions(out var shelfCenters);
                            if (browsePositions.Count > 0)
                            {
                                customer.StartBrowsing(browsePositions, shelfCenters);
                                customer.State = CustomerState.Browsing;
                            }
                            else
                            {
                                customer.State = CustomerState.LookingAround;
                                customer.WalkTo(CustomerSpawnPoints.RoomCenterPosition);
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
                            var retryPositions = CustomerSpawnPoints.GetInteriorBrowsePositions(out var retryShelfCenters);
                            if (retryPositions.Count > 0)
                            {
                                customer.StartBrowsing(retryPositions, retryShelfCenters);
                                customer.State = CustomerState.Browsing;
                            }
                            else
                            {
                                // No storage — exit the store
                                customer.State = CustomerState.ExitingStore;
                                customer.SetAvoidancePriority(10);
                                customer.WalkTo(CustomerSpawnPoints.RampBottomPosition);
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
                                customer.SetAvoidancePriority(10);
                                customer.WalkTo(CustomerSpawnPoints.RampBottomPosition);
                                break;
                            }

                            var counterPos = CheckoutCounter.CustomerStandPosition;
                            if (counterPos.HasValue)
                            {
                                customer.State = CustomerState.CheckingOut;
                                customer.CheckoutStartHour = TimeManager.CurrentTime / 100;
                                _checkoutQueue.Add(customer.Id);
                                int queueIdx = _checkoutQueue.Count - 1;
                                customer.WalkTo(GetQueuePosition(queueIdx));
                            }
                            else
                            {
                                // No counter — exit the store
                                customer.State = CustomerState.ExitingStore;
                                customer.SetAvoidancePriority(10);
                                customer.WalkTo(CustomerSpawnPoints.RampBottomPosition);
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
                                customer.SetAvoidancePriority(10);
                                customer.WalkTo(CustomerSpawnPoints.RampBottomPosition);
                                break;
                            }
                        }

                        // Only the front-of-queue customer is eligible for checkout
                        if (_checkoutQueue.Count > 0 && _checkoutQueue[0] == customer.Id)
                        {
                            if (customer.ArrivedAtDestination && customer.CheckoutArrivalTime == 0f)
                            {
                                customer.CheckoutArrivalTime = Time.time;
                                var counterPos1 = CheckoutCounter.CounterPosition;
                                if (counterPos1.HasValue)
                                    customer.FaceAndAnimate(counterPos1.Value);
                                ComputerScreen.ShowCheckoutInfo(customer.SelectedProducts);
                            }
                        }
                        break;

                    case CustomerState.ExitingStore:
                        if (customer.ArrivedAtDestination)
                        {
                            customer.ArrivedAtDestination = false;
                            customer.State = CustomerState.LeavingStore;
                            // Warp to ground in front of stairs, then restore civilian mesh
                            customer.WarpTo(CustomerSpawnPoints.StairApproachPosition);
                            customer.RestoreCivilianNavMesh();
                            customer.SetAvoidancePriority(50);
                            customer.WalkTo(customer.SpawnPoint.Position);
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

            // Clean up stale queue entries (customers that left CheckingOut or became invalid)
            int queueBefore = _checkoutQueue.Count;
            _checkoutQueue.RemoveAll(id =>
            {
                if (!CustomerInstance.Active.TryGetValue(id, out var c)) return true;
                return c.State != CustomerState.CheckingOut;
            });
            if (_checkoutQueue.Count != queueBefore)
                AdvanceQueue();

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
        /// Returns the world position for a given queue index (0 = front, at counter).
        /// Each subsequent customer stands further from the counter.
        /// </summary>
        private static Vector3 GetQueuePosition(int queueIndex)
        {
            var counterPos = CheckoutCounter.CounterPosition.Value;
            var counterForward = CheckoutCounter.CounterTransform.forward;
            return counterPos - counterForward * (1.0f + queueIndex * QueueSpacing);
        }

        /// <summary>
        /// Re-walks all queued customers to their updated positions after the front changes.
        /// </summary>
        private void AdvanceQueue()
        {
            for (int i = 0; i < _checkoutQueue.Count; i++)
            {
                if (!CustomerInstance.Active.TryGetValue(_checkoutQueue[i], out var c)) continue;
                c.ArrivedAtDestination = false;
                c.CheckoutArrivalTime = 0f;
                c.WalkTo(GetQueuePosition(i));
            }

            if (_checkoutQueue.Count == 0)
                ComputerScreen.HideCheckoutInfo();
        }

        /// <summary>
        /// Called by CheckoutProcess when a checkout finishes (complete or abort).
        /// Removes the customer from the queue and advances remaining customers.
        /// </summary>
        public void OnCheckoutComplete(string customerId)
        {
            _checkoutQueue.Remove(customerId);
            ComputerScreen.HideCheckoutInfo();
            AdvanceQueue();
            _statePublishNeeded = true;
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
        /// </summary>
        public string SerializeCustomerState()
        {
            if (CustomerInstance.Active.Count == 0) return "";

            var parts = new List<string>();
            foreach (var kvp in CustomerInstance.Active)
            {
                var c = kvp.Value;
                int spawnIdx = CustomerSpawnPoints.GetSpawnPointIndex(c.SpawnPoint);
                parts.Add($"{c.Id}:{c.SpawnSeed}:{spawnIdx}:{(int)c.State}:{c.NetworkObjectId}");
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

                    // Already tracked — update state
                    if (CustomerInstance.Active.TryGetValue(customerId, out var existing))
                    {
                        existing.State = state;
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
                        CustomerInstance.Adopt(customerId, seed, spawnPoint, state, npc);
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
                    Logger.Warning($"Giving up adoption for {pa.CustomerId} (timeout)");
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

            _checkoutQueue.Clear();
            _pendingAdoptions.Clear();
            CustomerInstance.CleanupAll();
            Instance = null;
        }
    }
}
