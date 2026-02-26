using MelonLoader;
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

        // Dev-phase constants (move to Config later)
        private const int MaxActiveCustomers = 5;
        private const int SpawnStartHour = 8;
        private const int SpawnEndHour = 20;
        private const float DespawnDistance = 30f;

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

            if (CustomerInstance.Active.Count >= MaxActiveCustomers)
                return;

            try
            {
                int day = TimeManager.ElapsedDays;

                string id = $"customer_{day}_{_customerIdCounter++}";
                int seed = id.GetHashCode();

                var spawnPoint = CustomerSpawnPoints.GetRandomSpawnPoint();
                if (spawnPoint == null)
                {
                    Logger.Warning("No spawn point available for customer");
                    return;
                }

                var customer = CustomerInstance.Create(id, seed, spawnPoint);
                if (customer == null) return;

                customer.State = CustomerState.WalkingToStore;
                customer.WalkTo(CustomerSpawnPoints.EntrancePosition);

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
                            // Arrived at stair base — now walk through NavMeshLink into interior
                            customer.ArrivedAtDestination = false;
                            customer.State = CustomerState.EnteringStore;
                            customer.WalkTo(CustomerSpawnPoints.DoorInteriorPosition);
                        }
                        break;

                    case CustomerState.EnteringStore:
                        if (customer.ArrivedAtDestination)
                        {
                            customer.ArrivedAtDestination = false;
                            var browsePositions = CustomerSpawnPoints.GetInteriorBrowsePositions();
                            if (browsePositions.Count > 0)
                            {
                                customer.StartBrowsing(browsePositions);
                                customer.State = CustomerState.Browsing;
                            }
                            else
                            {
                                // No storage — walk to room center and look around
                                customer.State = CustomerState.LookingAround;
                                customer.WalkTo(CustomerSpawnPoints.RoomCenterPosition);
                            }
                        }
                        break;

                    case CustomerState.LookingAround:
                        if (customer.ArrivedAtDestination && customer.LookAroundEndTime == 0f)
                        {
                            // Just arrived at room center — start looking around
                            customer.LookAroundEndTime = Time.time + 8f;
                        }
                        if (customer.LookAroundEndTime > 0f && Time.time >= customer.LookAroundEndTime)
                        {
                            // Done looking — check for storage again
                            customer.LookAroundEndTime = 0f;
                            customer.ArrivedAtDestination = false;
                            var retryPositions = CustomerSpawnPoints.GetInteriorBrowsePositions();
                            if (retryPositions.Count > 0)
                            {
                                customer.StartBrowsing(retryPositions);
                                customer.State = CustomerState.Browsing;
                            }
                            else
                            {
                                customer.State = CustomerState.LeavingStore;
                                customer.WalkTo(customer.SpawnPoint.Position);
                            }
                        }
                        break;

                    case CustomerState.Browsing:
                        if (customer.TickBrowsing())
                        {
                            customer.State = CustomerState.LeavingStore;
                            customer.ArrivedAtDestination = false;
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
                {
                    if (Config.VerboseLogging.Value)
                        Logger.Msg($"[State] {customer.Id}: {prevState} → {customer.State} pos={customer.Position}");
                    _statePublishNeeded = true;
                }
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

            _pendingAdoptions.Clear();
            CustomerInstance.CleanupAll();
            Instance = null;
        }
    }
}
