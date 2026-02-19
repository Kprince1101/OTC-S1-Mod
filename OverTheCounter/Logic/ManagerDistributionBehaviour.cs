using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.NPCs;
using MelonLoader;
using OverTheCounter.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Distribution run state machine for a Manager NPC.
    /// Host-only execution — walks items between storage containers along configured routes.
    /// Uses two-phase navigation for indoor storages: walk to property exterior on civilian
    /// NavMesh, switch to employee NavMesh settings, walk inside, interact, walk back out
    /// to the property exterior, then restore civilian NavMesh for outdoor travel.
    /// </summary>
    public class ManagerDistributionBehaviour
    {

        public enum DistributionState
        {
            Idle,
            WalkingToSourceProperty,  // walking to property idle point near source (Humanoid NavMesh)
            WalkingToSource,          // walking to source container (direct for outdoor)
            AtSource,                 // 0.5s grab animation delay, then pickup
            WalkingToDestProperty,    // walking to property idle point near destination (Humanoid NavMesh)
            WalkingToDest,            // carrying items to destination (direct for outdoor)
            AtDest,                   // 0.5s grab animation delay, then deposit
            WalkingToPropertyExit,    // walking back to property exterior on employee NavMesh
            WalkingToIdle             // returning to business idle point
        }

        private readonly ManagerInstance _manager;

        private DistributionState _state = DistributionState.Idle;
        public DistributionState State
        {
            get => _state;
            private set
            {
                if (_state != value) { _state = value; ManagerInstance.StatePublishNeeded = true; }
            }
        }
        public int CurrentRouteDisplay => Math.Max(1, _currentRouteIndex + 1); // 1-indexed for UI, clamped during resume runs

        // Current run plan
        private List<int> _routePlan;
        private int _routePlanStep;
        private int _currentRouteIndex;
        private ManagerConfiguration.DistributionRoute _currentRoute;

        // Timing
        private float _sourceArrivalTime;
        private float _destArrivalTime;

        // Walk failure tracking (warp after 5 consecutive failures, vanilla pattern)
        private int _consecutiveWalkFailures;
        private const int MAX_WALK_FAILURES = 5;

        // Walk resume state
        private Vector3 _currentWalkTarget;
        private float _lastEnsureMovingLog;

        // Stuck detection — warp if stationary for too long during walks
        private Vector3 _lastMovedPosition;
        private float _lastMovedTime;
        private Vector3 _stuckCheckTarget;
        private Vector3 _currentAccessTarget; // inner access point for property walks
        private const float STUCK_WARP_TIMEOUT = 8f;
        private const float STUCK_MOVE_THRESHOLD = 0.5f;

        // Maps NPC inventory slot index → destination storage GUID.
        // DepositItems only processes slots whose destination matches the current target,
        // preventing leftover items from previous routes being deposited at the wrong destination.
        // Persists through saves via ManagerSaveData.
        private readonly Dictionary<int, string> _slotDestinations = new();

        // NavMesh switching — civilian (outdoor) vs employee (indoor) settings
        private int _savedAgentTypeID;
        private int _savedAreaMask;
        private bool _usingEmployeeNavMesh;
        private Vector3? _currentPropertyExterior; // saved so we can walk back out after indoor interaction
        private Action _exitBuildingContinuation;  // action to run after exiting building on employee NavMesh

        // Resume delivery state — for delivering items restored from save
        private bool _isResuming;
        private bool _returningToSource; // true when retrying delivery back to source storage
        private Queue<string> _resumeDestQueue;
        private string _resumeDestGuid;

        // IL2CPP callback references (prevent GC collection)
        private Il2CppSystem.Action<NPCMovement.WalkResult> _sourcePropertyWalkCallback;
        private Il2CppSystem.Action<NPCMovement.WalkResult> _sourceWalkCallback;
        private Il2CppSystem.Action<NPCMovement.WalkResult> _destPropertyWalkCallback;
        private Il2CppSystem.Action<NPCMovement.WalkResult> _destWalkCallback;
        private Il2CppSystem.Action<NPCMovement.WalkResult> _propertyExitWalkCallback;
        private Il2CppSystem.Action<NPCMovement.WalkResult> _idleWalkCallback;

        /// <summary>
        /// Read-only view of the slot→destination mapping. Used by save system.
        /// </summary>
        internal IReadOnlyDictionary<int, string> SlotDestinations => _slotDestinations;

        /// <summary>
        /// Records a destination GUID for a specific NPC inventory slot.
        /// Called by save restore to rebuild the mapping after load.
        /// </summary>
        internal void SetSlotDestination(int slotIndex, string destGuid)
        {
            if (!string.IsNullOrEmpty(destGuid))
                _slotDestinations[slotIndex] = destGuid;
        }

        /// <summary>
        /// Returns true if the specified NPC slot has a destination reservation (distribution delivery).
        /// Used by ManagerSupplyBehaviour to skip destination-tagged slots.
        /// </summary>
        public bool IsSlotReservedForDelivery(int slotIndex) => _slotDestinations.ContainsKey(slotIndex);

        public ManagerDistributionBehaviour(ManagerInstance manager)
        {
            _manager = manager;
        }

        // ==================================================================
        // Entry point
        // ==================================================================

        /// <summary>
        /// Starts a distribution run for a single route by index.
        /// Called by the job rotation in ManagerInstance.TryStartNextJob().
        /// Returns false if the route is unconfigured, has no items, or dest is full.
        /// </summary>
        public bool TryStartSingleRoute(int routeIndex)
        {
            if (State != DistributionState.Idle) return false;
            if (!_manager.PaidForToday) return false;
            if (_manager.State != ManagerState.Idle) return false;

            // Don't start if in dialogue
            try
            {
                var dialogueHandler = _manager.GameNpc?.DialogueHandler;
                if (dialogueHandler != null && dialogueHandler.IsDialogueInProgress) return false;
            }
            catch { }

            var routes = _manager.Configuration.Routes;
            if (routeIndex < 0 || routeIndex >= routes.Length) return false;

            var route = routes[routeIndex];
            if (!route.IsConfigured) return false;
            if (!SourceHasItems(route.Source)) return false;
            if (!DestinationCanAcceptSourceItems(route.Source, route.Destination)) return false;

            _routePlan = new List<int> { routeIndex };
            _routePlanStep = 0;
            _consecutiveWalkFailures = 0;
            _manager.State = ManagerState.DistributionRun;
            _manager.DisableIdleBehaviour();

            StartNextRoute();
            return true;
        }

        /// <summary>
        /// Resumes delivery of items that were in NPC inventory when the game was saved.
        /// Each item has a recorded destination GUID from its original pickup.
        /// Returns true if a delivery run was started.
        /// </summary>
        public bool TryResumeDeliveries()
        {
            if (_slotDestinations.Count == 0) return false;
            if (State != DistributionState.Idle) return false;
            if (!_manager.PaidForToday) return false;
            if (_manager.State != ManagerState.Idle) return false;

            // Don't resume while in dialogue
            try
            {
                var dialogueHandler = _manager.GameNpc?.DialogueHandler;
                if (dialogueHandler != null && dialogueHandler.IsDialogueInProgress) return false;
            }
            catch { }

            // Collect unique destination GUIDs
            var uniqueDests = new HashSet<string>(_slotDestinations.Values);
            _resumeDestQueue = new Queue<string>(uniqueDests);
            _isResuming = true;
            _returningToSource = false;
            _consecutiveWalkFailures = 0;
            _manager.State = ManagerState.DistributionRun;
            _manager.DisableIdleBehaviour();

            _manager.Log($"resuming deliveries for {uniqueDests.Count} destinations ({_slotDestinations.Count} slots) | inventory: [{_manager.GetInventorySummary()}]");
            DeliverNextResumeDest();
            return true;
        }

        /// <summary>
        /// Advances to the next resume destination, or finishes the resume run.
        /// </summary>
        private void DeliverNextResumeDest()
        {
            while (_resumeDestQueue != null && _resumeDestQueue.Count > 0)
            {
                string destGuid = _resumeDestQueue.Dequeue();
                var storage = ManagerConfiguration.ResolveStorage(destGuid);
                if (storage?.StorageEntity == null)
                {
                    _manager.LogWarning($"resume dest GUID '{destGuid}' not found, dropping items");
                    // Remove entries for this unreachable destination
                    var toRemove = new List<int>();
                    foreach (var kv in _slotDestinations)
                        if (kv.Value == destGuid) toRemove.Add(kv.Key);
                    foreach (var idx in toRemove)
                        _slotDestinations.Remove(idx);
                    continue;
                }

                // Use a synthetic route with just the destination (no source — items already in NPC)
                _resumeDestGuid = destGuid;
                _currentRoute = new ManagerConfiguration.DistributionRoute { Destination = storage };
                _currentRouteIndex = -1;

                // Walk to destination using existing infrastructure
                WalkToDest();
                return;
            }

            // All resume destinations processed — clean up
            _isResuming = false;
            _resumeDestQueue = null;
            _resumeDestGuid = null;
            _currentRoute = null;

            // If slots still have destination mappings, the destinations were full.
            // Try returning items to their source storage before giving up.
            if (_slotDestinations.Count > 0)
            {
                if (!_returningToSource)
                {
                    // Reverse-lookup: destination GUID → source GUID via configured routes
                    var routes = _manager.Configuration.Routes;
                    var destToSource = new Dictionary<string, string>();
                    foreach (var route in routes)
                    {
                        if (!route.IsConfigured) continue;
                        string dstGuid = ManagerConfiguration.GetGuid(route.Destination);
                        string srcGuid = ManagerConfiguration.GetGuid(route.Source);
                        if (!string.IsNullOrEmpty(dstGuid) && !string.IsNullOrEmpty(srcGuid))
                            destToSource[dstGuid] = srcGuid;
                    }

                    // Remap slot destinations to point at source storages
                    var keys = new List<int>(_slotDestinations.Keys);
                    foreach (var key in keys)
                    {
                        if (destToSource.TryGetValue(_slotDestinations[key], out var srcGuid))
                            _slotDestinations[key] = srcGuid;
                        else
                            _slotDestinations.Remove(key); // route deleted — keep item, drop mapping
                    }

                    if (_slotDestinations.Count > 0)
                    {
                        _manager.Log($"returning {_slotDestinations.Count} undeliverable items to source storage");
                        _returningToSource = true;
                        _isResuming = true;
                        _resumeDestQueue = new Queue<string>(new HashSet<string>(_slotDestinations.Values));
                        DeliverNextResumeDest();
                        return;
                    }
                }

                // Already tried returning or source also full — give up, keep items in inventory
                _returningToSource = false;
                _manager.LogWarning($"{_slotDestinations.Count} slots undeliverable, keeping items in inventory");
                _slotDestinations.Clear();
            }

            if (Config.ManagerVerboseLogging.Value)
                _manager.Log($"resume deliveries complete");

            State = DistributionState.Idle;
            _manager.State = ManagerState.Idle;
            _manager.EnableIdleBehaviour();
            _manager.TryStartNextJob();
        }

        // ==================================================================
        // Route progression
        // ==================================================================

        /// <summary>
        /// Advances to the next route in the plan, or walks home if done.
        /// Re-validates the current route in case config changed during the run.
        /// </summary>
        private void StartNextRoute()
        {
            while (_routePlanStep < _routePlan.Count)
            {
                _currentRouteIndex = _routePlan[_routePlanStep];
                var routes = _manager.Configuration.Routes;

                if (_currentRouteIndex < 0 || _currentRouteIndex >= routes.Length)
                {
                    _routePlanStep++;
                    continue;
                }

                _currentRoute = routes[_currentRouteIndex];

                // Re-validate: config may have changed during the run
                if (!_currentRoute.IsConfigured || !SourceHasItems(_currentRoute.Source)
                    || !DestinationCanAcceptSourceItems(_currentRoute.Source, _currentRoute.Destination))
                {
                    if (Config.ManagerVerboseLogging.Value)
                        _manager.Log($"distribution route {CurrentRouteDisplay} skipped (no longer valid/has items/dest full)");
                    // route skipped — advance plan step
                    _routePlanStep++;
                    continue;
                }

                _manager.Log($"=== starting distribution route {CurrentRouteDisplay} ===");
                WalkToSource();
                return;
            }

            // All routes processed — check for immediate work before walking home
            _manager.Log($"all distribution routes processed, checking for more work");
            State = DistributionState.Idle;
            _routePlan = null;
            _currentRoute = null;
            _manager.State = ManagerState.Idle;
            _manager.EnableIdleBehaviour();

            if (_manager.TryStartNextJob()) return;

            // Nothing to do — walk home
            _manager.State = ManagerState.DistributionRun;
            WalkToIdle();
        }

        // ==================================================================
        // Walking to source (two-phase for indoor)
        // ==================================================================

        private void WalkToSource()
        {
            _consecutiveWalkFailures = 0;

            var accessPos = GetStorageAccessPosition(_currentRoute.Source, out bool isReachable);
            if (accessPos == null)
            {
                _manager.LogWarning($"can't find source position for route {CurrentRouteDisplay}");
                _routePlanStep++;
                StartNextRoute();
                return;
            }

            _currentAccessTarget = accessPos.Value;

            if (isReachable)
            {
                // Direct walk — storage is reachable on current NavMesh
                State = DistributionState.WalkingToSource;
                IssueWalkToSource(accessPos.Value);
            }
            else
            {
                // Two-phase: walk to property exterior, switch to employee NavMesh, walk inside
                var propertyPos = GetPropertyExteriorPoint(_currentRoute.Source);
                if (propertyPos == null)
                {
                    // No property context — fall back to direct walk (will warp if needed)
                    _manager.LogWarning($"source not reachable and no property exterior, attempting direct walk");
                    State = DistributionState.WalkingToSource;
                    IssueWalkToSource(accessPos.Value);
                    return;
                }

                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"entering building (route {CurrentRouteDisplay} source)");
                _currentPropertyExterior = propertyPos.Value;
                State = DistributionState.WalkingToSourceProperty;
                _currentWalkTarget = propertyPos.Value;

                // Capture access position for use in callback
                var capturedAccessPos = accessPos.Value;

                try
                {
                    _sourcePropertyWalkCallback = (Il2CppSystem.Action<NPCMovement.WalkResult>)
                        new Action<NPCMovement.WalkResult>(result =>
                        {
                            if (result == NPCMovement.WalkResult.Success ||
                                result == NPCMovement.WalkResult.Partial)
                            {
                                _consecutiveWalkFailures = 0;
                                // Arrived near property — switch to employee NavMesh and walk inside
                                if (SwitchToEmployeeNavMesh())
                                {
                                    State = DistributionState.WalkingToSource;
                                    IssueWalkToSource(capturedAccessPos);
                                }
                                else
                                {
                                    // No employee settings available — warp as fallback
                                    _manager.LogWarning($"employee NavMesh unavailable, warping to source");
                                    WarpToPosition(capturedAccessPos);
                                    State = DistributionState.AtSource;
                                    _sourceArrivalTime = UnityEngine.Time.time;
                                }
                            }
                            else if (result == NPCMovement.WalkResult.Failed)
                            {
                                _consecutiveWalkFailures++;
                                _manager.LogWarning($"source property walk failed ({_consecutiveWalkFailures}/{MAX_WALK_FAILURES})");
                                if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                                {
                                    _manager.LogWarning($"warping to source storage after {MAX_WALK_FAILURES} failures");
                                    WarpToPosition(capturedAccessPos);
                                    _consecutiveWalkFailures = 0;
                                    State = DistributionState.AtSource;
                                    _sourceArrivalTime = UnityEngine.Time.time;
                                }
                            }
                        });

                    _manager.GameNpc.Movement.SetDestination(propertyPos.Value, _sourcePropertyWalkCallback, 3f, 1f);
                }
                catch (Exception ex)
                {
                    _manager.LogWarning($"WalkToSourceProperty failed: {ex.Message}");
                    _routePlanStep++;
                    StartNextRoute();
                }
            }
        }

        /// <summary>
        /// Issues the actual walk-to-source command (used for both direct and post-property walks).
        /// </summary>
        private void IssueWalkToSource(Vector3 target)
        {
            _currentWalkTarget = target;
            _consecutiveWalkFailures = 0;

            try
            {
                _sourceWalkCallback = (Il2CppSystem.Action<NPCMovement.WalkResult>)
                    new Action<NPCMovement.WalkResult>(result =>
                    {
                        if (result == NPCMovement.WalkResult.Success ||
                            result == NPCMovement.WalkResult.Partial)
                        {
                            State = DistributionState.AtSource;
                            _sourceArrivalTime = UnityEngine.Time.time;
                            _consecutiveWalkFailures = 0;
                        }
                        else if (result == NPCMovement.WalkResult.Failed)
                        {
                            _consecutiveWalkFailures++;
                            _manager.LogWarning($"source walk failed ({_consecutiveWalkFailures}/{MAX_WALK_FAILURES})");
                            if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                            {
                                _manager.LogWarning($"warping to source after {MAX_WALK_FAILURES} failures");
                                WarpToPosition(target);
                                State = DistributionState.AtSource;
                                _sourceArrivalTime = UnityEngine.Time.time;
                                _consecutiveWalkFailures = 0;
                            }
                        }
                    });

                _manager.GameNpc.Movement.SetDestination(target, _sourceWalkCallback, 2f, 1f);
                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"walking to source for route {CurrentRouteDisplay} | inventory: [{_manager.GetInventorySummary()}]");
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"IssueWalkToSource failed: {ex.Message}");
                _routePlanStep++;
                StartNextRoute();
            }
        }

        // ==================================================================
        // Picking up items
        // ==================================================================

        /// <summary>
        /// Called when AtSource timer fires (0.5s). Faces source, plays grab animation,
        /// transfers items from source container to NPC inventory.
        /// </summary>
        private void PickUpItems()
        {
            // Face toward the source
            try
            {
                var sourceTransform = _currentRoute.Source?.transform;
                if (sourceTransform != null)
                {
                    var npcPos = _manager.Position ?? Vector3.zero;
                    var dir = (sourceTransform.position - npcPos).normalized;
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
            catch { }

            var source = _currentRoute.Source;
            var destination = _currentRoute.Destination;
            var npcInventory = GetNpcInventory();
            int totalPickedUp = 0;
            string destGuid = ManagerConfiguration.GetGuid(destination);

            if (source?.StorageEntity != null && npcInventory != null)
            {
                int freeSlots = GetFreeNpcSlots(npcInventory);
                int totalNpcSlots = npcInventory.ItemSlots?.Count ?? 0;

                // Check how many items the destination can actually hold so we don't
                // pick up more than it can accept (avoids overflow → return → repeat loop)
                int destCapacity = int.MaxValue;
                if (destination?.StorageEntity != null)
                {
                    for (int s = 0; s < source.StorageEntity.ItemSlots.Count; s++)
                    {
                        var si = source.StorageEntity.ItemSlots[s]?.ItemInstance;
                        if (si != null)
                        {
                            destCapacity = StorageFilterHelper.HowManyCanFitFiltered(destination.StorageEntity, si);
                            break;
                        }
                    }
                }

                if (Config.ManagerVerboseLogging.Value)
                {
                    int sourceOccupied = 0;
                    for (int s = 0; s < source.StorageEntity.ItemSlots.Count; s++)
                        if (source.StorageEntity.ItemSlots[s]?.ItemInstance != null) sourceOccupied++;
                    _manager.Log($"[PickUp] npcSlots={totalNpcSlots} free={freeSlots} destCapacity={destCapacity} sourceOccupied={sourceOccupied}");
                }

                for (int i = 0; i < source.StorageEntity.ItemSlots.Count; i++)
                {
                    if (freeSlots <= 0 || destCapacity <= 0) break;

                    try
                    {
                        var slot = source.StorageEntity.ItemSlots[i];
                        if (slot?.ItemInstance == null) continue;

                        // Skip items the destination won't accept (filter pre-check)
                        if (destination?.StorageEntity != null &&
                            StorageFilterHelper.HowManyCanFitFiltered(
                                destination.StorageEntity, slot.ItemInstance) <= 0)
                            continue;

                        int npcSlotIdx = FindEmptyNpcSlot(npcInventory);
                        if (npcSlotIdx < 0) break;

                        // Cash needs special handling: GetCopy loses the Balance
                        var sourceCash = slot.ItemInstance.TryCast<CashInstance>();
                        if (sourceCash != null)
                        {
                            float balance = sourceCash.Balance;
                            if (balance <= 0f) continue;

                            slot.ChangeQuantity(-slot.Quantity);
                            var newCash = NetworkSingleton<Il2CppScheduleOne.Money.MoneyManager>.Instance
                                .GetCashInstance(balance);
                            npcInventory.ItemSlots[npcSlotIdx].InsertItem(newCash);
                            _slotDestinations[npcSlotIdx] = destGuid;
                            totalPickedUp++;
                            destCapacity--;
                            freeSlots--;
                            continue;
                        }

                        int qty = Math.Min(slot.Quantity, destCapacity);
                        if (qty <= 0) continue;

                        // Clone the actual item (GetCopy preserves product type, GetDefaultInstance does not)
                        var copy = slot.ItemInstance.GetCopy(qty);
                        if (copy == null) continue;

                        // Place into an empty NPC slot (not InsertItem which may stack with
                        // leftover items from a previous route, mixing ownership)
                        slot.ChangeQuantity(-qty);
                        npcInventory.ItemSlots[npcSlotIdx].InsertItem(copy);
                        _slotDestinations[npcSlotIdx] = destGuid;

                        totalPickedUp += qty;
                        destCapacity -= qty;
                        freeSlots--;
                    }
                    catch (Exception ex)
                    {
                        _manager.LogWarning($"pickup slot {i} failed: {ex.Message}");
                    }
                }

                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"[PickUp] done: picked={totalPickedUp} freeAfter={freeSlots} destCapAfter={destCapacity}");
            }

            _manager.Log($"picked up {totalPickedUp} items from route {CurrentRouteDisplay} source | inventory: [{_manager.GetInventorySummary()}]");

            if (totalPickedUp > 0)
            {
                ExitBuildingThen(() => WalkToDest());
            }
            else
            {
                // Source was emptied during walk
                ExitBuildingThen(() => { _routePlanStep++; StartNextRoute(); });
            }
        }

        // ==================================================================
        // Walking to destination (two-phase for indoor)
        // ==================================================================

        private void WalkToDest()
        {
            _consecutiveWalkFailures = 0;

            var accessPos = GetStorageAccessPosition(_currentRoute.Destination, out bool isReachable);
            if (accessPos == null)
            {
                _manager.LogWarning($"can't find dest position for route {CurrentRouteDisplay}, items retained for retry");
                AdvanceAfterDestError();
                return;
            }

            _currentAccessTarget = accessPos.Value;

            if (isReachable)
            {
                // Direct walk
                State = DistributionState.WalkingToDest;
                IssueWalkToDest(accessPos.Value);
            }
            else
            {
                // Two-phase: walk to property exterior, switch to employee NavMesh, walk inside
                var propertyPos = GetPropertyExteriorPoint(_currentRoute.Destination);
                if (propertyPos == null)
                {
                    _manager.LogWarning($"dest not reachable and no property exterior, attempting direct walk");
                    State = DistributionState.WalkingToDest;
                    IssueWalkToDest(accessPos.Value);
                    return;
                }

                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"entering building (route {CurrentRouteDisplay} dest)");
                _currentPropertyExterior = propertyPos.Value;
                State = DistributionState.WalkingToDestProperty;
                _currentWalkTarget = propertyPos.Value;

                // Capture access position for use in callback
                var capturedAccessPos = accessPos.Value;

                try
                {
                    _destPropertyWalkCallback = (Il2CppSystem.Action<NPCMovement.WalkResult>)
                        new Action<NPCMovement.WalkResult>(result =>
                        {
                            if (result == NPCMovement.WalkResult.Success ||
                                result == NPCMovement.WalkResult.Partial)
                            {
                                _consecutiveWalkFailures = 0;
                                // Arrived near property — switch to employee NavMesh and walk inside
                                if (SwitchToEmployeeNavMesh())
                                {
                                    State = DistributionState.WalkingToDest;
                                    IssueWalkToDest(capturedAccessPos);
                                }
                                else
                                {
                                    // No employee settings available — warp as fallback
                                    _manager.LogWarning($"employee NavMesh unavailable, warping to dest");
                                    WarpToPosition(capturedAccessPos);
                                    State = DistributionState.AtDest;
                                    _destArrivalTime = UnityEngine.Time.time;
                                }
                            }
                            else if (result == NPCMovement.WalkResult.Failed)
                            {
                                _consecutiveWalkFailures++;
                                _manager.LogWarning($"dest property walk failed ({_consecutiveWalkFailures}/{MAX_WALK_FAILURES})");
                                if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                                {
                                    _manager.LogWarning($"warping to dest storage after {MAX_WALK_FAILURES} failures");
                                    WarpToPosition(capturedAccessPos);
                                    _consecutiveWalkFailures = 0;
                                    State = DistributionState.AtDest;
                                    _destArrivalTime = UnityEngine.Time.time;
                                }
                            }
                        });

                    _manager.GameNpc.Movement.SetDestination(propertyPos.Value, _destPropertyWalkCallback, 3f, 1f);
                }
                catch (Exception ex)
                {
                    _manager.LogWarning($"WalkToDestProperty failed: {ex.Message}, items retained for retry");
                    AdvanceAfterDestError();
                }
            }
        }

        /// <summary>
        /// Issues the actual walk-to-destination command (used for both direct and post-property walks).
        /// </summary>
        private void IssueWalkToDest(Vector3 target)
        {
            _currentWalkTarget = target;
            _consecutiveWalkFailures = 0;

            try
            {
                _destWalkCallback = (Il2CppSystem.Action<NPCMovement.WalkResult>)
                    new Action<NPCMovement.WalkResult>(result =>
                    {
                        if (result == NPCMovement.WalkResult.Success ||
                            result == NPCMovement.WalkResult.Partial)
                        {
                            State = DistributionState.AtDest;
                            _destArrivalTime = UnityEngine.Time.time;
                            _consecutiveWalkFailures = 0;
                        }
                        else if (result == NPCMovement.WalkResult.Failed)
                        {
                            _consecutiveWalkFailures++;
                            _manager.LogWarning($"dest walk failed ({_consecutiveWalkFailures}/{MAX_WALK_FAILURES})");
                            if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                            {
                                _manager.LogWarning($"warping to dest after {MAX_WALK_FAILURES} failures");
                                WarpToPosition(target);
                                State = DistributionState.AtDest;
                                _destArrivalTime = UnityEngine.Time.time;
                                _consecutiveWalkFailures = 0;
                            }
                        }
                    });

                _manager.GameNpc.Movement.SetDestination(target, _destWalkCallback, 2f, 1f);
                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"walking to destination for route {CurrentRouteDisplay} | inventory: [{_manager.GetInventorySummary()}]");
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"IssueWalkToDest failed: {ex.Message}, items retained for retry");
                AdvanceAfterDestError();
            }
        }

        /// <summary>
        /// Advances after a destination walk error. Routes to the correct continuation
        /// depending on whether this is a normal route run or a save-resume delivery.
        /// </summary>
        private void AdvanceAfterDestError()
        {
            if (_isResuming)
                DeliverNextResumeDest();
            else
            {
                _routePlanStep++;
                StartNextRoute();
            }
        }

        // ==================================================================
        // Depositing items
        // ==================================================================

        /// <summary>
        /// Called when AtDest timer fires (0.5s). Faces destination, plays grab animation,
        /// deposits NPC inventory items into destination container.
        /// </summary>
        private void DepositItems()
        {
            // Face toward the destination
            try
            {
                var destTransform = _currentRoute.Destination?.transform;
                if (destTransform != null)
                {
                    var npcPos = _manager.Position ?? Vector3.zero;
                    var dir = (destTransform.position - npcPos).normalized;
                    if (dir.sqrMagnitude > 0.01f)
                        _manager.GameNpc?.Movement?.FaceDirection(dir);
                }
            }
            catch { }

            // Play GrabItem animation (networked)
            try
            {
                _manager.GameNpc?.SetAnimationTrigger_Networked(null, "GrabItem");
            }
            catch { }

            var destination = _currentRoute.Destination;
            var npcInventory = GetNpcInventory();
            int totalDeposited = 0;
            int totalOverflow = 0;
            string currentDestGuid = _isResuming ? _resumeDestGuid : ManagerConfiguration.GetGuid(destination);

            if (destination?.StorageEntity != null && npcInventory != null)
            {
                // Only deposit items whose destination matches the current target.
                // Leftover items for other destinations stay untouched in their slots.
                for (int i = 0; i < npcInventory.ItemSlots.Count; i++)
                {
                    if (!_slotDestinations.TryGetValue(i, out var slotDest) || slotDest != currentDestGuid)
                        continue;

                    try
                    {
                        var slot = npcInventory.ItemSlots[i];
                        if (slot?.ItemInstance == null)
                        {
                            _slotDestinations.Remove(i); // stale entry
                            continue;
                        }

                        // Cash needs special handling: GetCopy loses the Balance
                        var cashItem = slot.ItemInstance.TryCast<CashInstance>();
                        if (cashItem != null)
                        {
                            float balance = cashItem.Balance;
                            if (balance <= 0f) { _slotDestinations.Remove(i); continue; }

                            float placed = DepositCash(destination.StorageEntity, balance);
                            if (placed >= balance)
                            {
                                slot.ChangeQuantity(-slot.Quantity);
                                _slotDestinations.Remove(i);
                                totalDeposited++;
                            }
                            else if (placed > 0f)
                            {
                                cashItem.SetBalance(balance - placed, true);
                                totalDeposited++;
                                totalOverflow++;
                            }
                            else
                            {
                                totalOverflow++;
                            }
                            continue;
                        }

                        int qty = slot.Quantity;

                        // Use GetCopy to preserve actual product type (jar, brick, etc.)
                        int canFit = StorageFilterHelper.HowManyCanFitFiltered(destination.StorageEntity, slot.ItemInstance);
                        if (canFit <= 0)
                        {
                            // Items stay in NPC inventory — will be retried on the next cycle
                            totalOverflow += qty;
                            continue;
                        }

                        if (canFit < qty)
                        {
                            // Partial deposit — only remove what was actually placed
                            var partialCopy = slot.ItemInstance.GetCopy(canFit);
                            StorageFilterHelper.InsertItemFiltered(destination.StorageEntity, partialCopy);
                            totalDeposited += canFit;
                            totalOverflow += (qty - canFit);
                            slot.ChangeQuantity(-canFit);
                            // Destination mapping stays — remaining items still need this dest
                        }
                        else
                        {
                            var copy = slot.ItemInstance.GetCopy(qty);
                            slot.ChangeQuantity(-qty);
                            StorageFilterHelper.InsertItemFiltered(destination.StorageEntity, copy);
                            totalDeposited += qty;
                            _slotDestinations.Remove(i); // slot fully emptied
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
                // Don't destroy items — leave them in NPC inventory for TryResumeDeliveries
                // to retry on the next job cycle. _slotDestinations stay intact.
                _manager.LogWarning($"destination storage or NPC inventory unavailable, items retained for retry");
            }

            if (totalOverflow > 0)
            {
                _manager.LogWarning($"{totalOverflow} items didn't fit in destination for route {CurrentRouteDisplay} (retained in NPC inventory)");
                // Diagnostic: log destination slot breakdown to help debug capacity issues
                try
                {
                    var destSlots = destination.StorageEntity?.ItemSlots;
                    if (destSlots != null)
                    {
                        int total = destSlots.Count;
                        int occupied = 0, locked = 0;
                        for (int s = 0; s < total; s++)
                        {
                            var ds = destSlots[s];
                            if (ds == null) continue;
                            if (ds.IsLocked || ds.IsAddLocked) locked++;
                            else if (ds.ItemInstance != null) occupied++;
                        }
                        int free = total - occupied - locked;
                        _manager.Log($"[DestDiag] slots: {total} total, {occupied} occupied, {locked} locked, {free} free");
                    }
                }
                catch { }
            }

            _manager.Log($"deposited {totalDeposited} items at route {CurrentRouteDisplay} destination");

            if (_isResuming)
            {
                // Resume mode — advance to next destination or finish
                ExitBuildingThen(() => DeliverNextResumeDest());
            }
            else
            {
                // Normal route mode — advance to next route
                _routePlanStep++;
                ExitBuildingThen(() => StartNextRoute());
            }
        }

        // ==================================================================
        // Walking to idle point
        // ==================================================================

        private void WalkToIdle()
        {
            State = DistributionState.WalkingToIdle;

            var location = ManagerLocations.GetLocation(_manager.BusinessPropertyCode);
            if (location == null)
            {
                _manager.LogWarning($"no business location after distribution");
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
                        if (result == NPCMovement.WalkResult.Success ||
                            result == NPCMovement.WalkResult.Partial)
                        {
                            try { _manager.GameNpc?.Movement?.FaceDirection(location.DestRotation * Vector3.forward); }
                            catch { }
                            FinishRun();
                        }
                        else if (result == NPCMovement.WalkResult.Failed)
                        {
                            _consecutiveWalkFailures++;
                            if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                            {
                                _manager.LogWarning($"warping to idle point");
                                WarpToPosition(location.Destination);
                                try { _manager.GameNpc?.Movement?.FaceDirection(location.DestRotation * Vector3.forward); }
                                catch { }
                                FinishRun();
                            }
                        }
                    });

                _manager.GameNpc.Movement.SetDestination(location.Destination, _idleWalkCallback, 3f, 1f);
                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"walking to idle point after distribution | inventory: [{_manager.GetInventorySummary()}]");
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

        private void FinishRun()
        {
            State = DistributionState.Idle;
            _manager.State = ManagerState.Idle;
            _routePlan = null;
            _currentRoute = null;
            _isResuming = false;
            _resumeDestQueue = null;
            _resumeDestGuid = null;
            _manager.EnableIdleBehaviour();
            _manager.Log($"distribution run complete");

            // Immediately check for next job instead of waiting for next tick
            _manager.TryStartNextJob();
        }

        /// <summary>
        /// Cancels an active distribution run (called when fired/despawned).
        /// Clears non-cash NPC inventory to prevent item duplication.
        /// </summary>
        public void Cancel()
        {
            if (State == DistributionState.Idle) return;

            _manager.Log($"distribution run cancelled (was {State})");

            RestoreCivilianNavMesh();
            ClearNonCashNpcInventory();
            State = DistributionState.Idle;
            _routePlan = null;
            _currentRoute = null;
            _isResuming = false;
            _resumeDestQueue = null;
            _resumeDestGuid = null;
            _manager.EnableIdleBehaviour();
        }

        // ==================================================================
        // Tick (called every frame from Core.OnLateUpdate, host only)
        // ==================================================================

        public void Tick()
        {
            if (State == DistributionState.Idle) return;

            // AtSource → PickUpItems after 0.5s delay
            if (State == DistributionState.AtSource && _sourceArrivalTime > 0f &&
                UnityEngine.Time.time - _sourceArrivalTime >= 0.5f)
            {
                _sourceArrivalTime = 0f; // prevent re-entry
                PickUpItems();
                return;
            }

            // AtDest → DepositItems after 0.5s delay
            if (State == DistributionState.AtDest && _destArrivalTime > 0f &&
                UnityEngine.Time.time - _destArrivalTime >= 0.5f)
            {
                _destArrivalTime = 0f; // prevent re-entry
                DepositItems();
                return;
            }

            // Resume interrupted walks (all walking states)
            if (State == DistributionState.WalkingToSourceProperty ||
                State == DistributionState.WalkingToSource ||
                State == DistributionState.WalkingToDestProperty ||
                State == DistributionState.WalkingToDest ||
                State == DistributionState.WalkingToPropertyExit ||
                State == DistributionState.WalkingToIdle)
            {
                CheckStuckDuringWalk();
                EnsureMovingDuringRun();
            }
        }

        /// <summary>
        /// Resumes interrupted walks during distribution run.
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
                    if (UnityEngine.Time.time - _lastEnsureMovingLog > 10f)
                    {
                        if (Config.ManagerVerboseLogging.Value)
                            _manager.Log($"resuming distribution walk (dist={dist:F1}m)");
                        _lastEnsureMovingLog = UnityEngine.Time.time;
                    }

                    Il2CppSystem.Action<NPCMovement.WalkResult> callback = State switch
                    {
                        DistributionState.WalkingToSourceProperty => _sourcePropertyWalkCallback,
                        DistributionState.WalkingToSource => _sourceWalkCallback,
                        DistributionState.WalkingToDestProperty => _destPropertyWalkCallback,
                        DistributionState.WalkingToDest => _destWalkCallback,
                        DistributionState.WalkingToPropertyExit => _propertyExitWalkCallback,
                        DistributionState.WalkingToIdle => _idleWalkCallback,
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
        /// and warps to the appropriate target, transitioning state to match
        /// the existing MAX_WALK_FAILURES warp patterns.
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
                case DistributionState.WalkingToSourceProperty:
                case DistributionState.WalkingToSource:
                    WarpToPosition(_currentAccessTarget);
                    State = DistributionState.AtSource;
                    _sourceArrivalTime = UnityEngine.Time.time;
                    break;

                case DistributionState.WalkingToDestProperty:
                case DistributionState.WalkingToDest:
                    WarpToPosition(_currentAccessTarget);
                    State = DistributionState.AtDest;
                    _destArrivalTime = UnityEngine.Time.time;
                    break;

                case DistributionState.WalkingToPropertyExit:
                    WarpToPosition(_currentWalkTarget);
                    RestoreCivilianNavMesh();
                    _currentPropertyExterior = null;
                    _exitBuildingContinuation?.Invoke();
                    _exitBuildingContinuation = null;
                    break;

                case DistributionState.WalkingToIdle:
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
        /// Human-readable status for dialogue. Returns null if not on a distribution run.
        /// </summary>
        public string GetStatusDescription()
        {
            switch (State)
            {
                case DistributionState.WalkingToSourceProperty:
                case DistributionState.WalkingToSource:
                    return $"I'm heading to pick up product for route {CurrentRouteDisplay}.";
                case DistributionState.AtSource:
                    return "I'm loading up product for delivery.";
                case DistributionState.WalkingToDestProperty:
                case DistributionState.WalkingToDest:
                    return $"I'm delivering product for route {CurrentRouteDisplay}.";
                case DistributionState.AtDest:
                    return "I'm unloading the delivery.";
                case DistributionState.WalkingToPropertyExit:
                    return "I'm heading out after the delivery.";
                case DistributionState.WalkingToIdle:
                    return "Finished deliveries, heading back to my post.";
                default:
                    return null;
            }
        }

        // ==================================================================
        // NavMesh switching (civilian ↔ employee)
        // ==================================================================

        /// <summary>
        /// Switches the manager's NavMeshAgent to employee settings for indoor navigation.
        /// Saves the civilian settings for later restoration.
        /// Must be called at a position where both NavMesh surfaces overlap (near building entrance).
        /// </summary>
        private bool SwitchToEmployeeNavMesh()
        {
            if (_usingEmployeeNavMesh) return true;

            if (!ManagerSpawner.TryGetEmployeeNavMeshSettings(out int empAgentType, out int empAreaMask))
            {
                _manager.LogWarning($"no employee NavMesh settings available");
                return false;
            }

            try
            {
                var agent = _manager.GameNpc?.Movement?.Agent;
                if (agent == null) return false;

                // Save civilian settings
                _savedAgentTypeID = agent.agentTypeID;
                _savedAreaMask = agent.areaMask;

                // Apply employee settings
                agent.agentTypeID = empAgentType;
                agent.areaMask = empAreaMask;

                // Reanchor agent on the new NavMesh surface at current position
                _manager.GameNpc.Movement.Warp(_manager.GameNpc.transform.position);

                _usingEmployeeNavMesh = true;
                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"switched to employee NavMesh");
                return true;
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"SwitchToEmployeeNavMesh failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Restores the manager's NavMeshAgent to civilian settings for outdoor navigation.
        /// </summary>
        private void RestoreCivilianNavMesh()
        {
            if (!_usingEmployeeNavMesh) return;

            try
            {
                var agent = _manager.GameNpc?.Movement?.Agent;
                if (agent == null) return;

                agent.agentTypeID = _savedAgentTypeID;
                agent.areaMask = _savedAreaMask;

                // Reanchor on civilian NavMesh
                _manager.GameNpc.Movement.Warp(_manager.GameNpc.transform.position);

                _usingEmployeeNavMesh = false;
                if (Config.ManagerVerboseLogging.Value)
                    _manager.Log($"restored civilian NavMesh");
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"RestoreCivilianNavMesh failed: {ex.Message}");
                _usingEmployeeNavMesh = false;
            }
        }

        // ==================================================================
        // Building exit — walk out on employee NavMesh, then restore civilian
        // ==================================================================

        /// <summary>
        /// If currently on employee NavMesh (indoor), walks back to the property exterior
        /// before restoring civilian NavMesh and executing the continuation action.
        /// If already on civilian NavMesh (outdoor storage), executes the continuation immediately.
        /// </summary>
        private void ExitBuildingThen(Action continuation)
        {
            if (!_usingEmployeeNavMesh || _currentPropertyExterior == null)
            {
                // Not inside a building — just run the continuation
                continuation?.Invoke();
                return;
            }

            if (Config.ManagerVerboseLogging.Value)
                _manager.Log($"walking to property exterior before continuing");
            _exitBuildingContinuation = continuation;
            State = DistributionState.WalkingToPropertyExit;
            _currentWalkTarget = _currentPropertyExterior.Value;
            _consecutiveWalkFailures = 0;

            try
            {
                _propertyExitWalkCallback = (Il2CppSystem.Action<NPCMovement.WalkResult>)
                    new Action<NPCMovement.WalkResult>(result =>
                    {
                        if (result == NPCMovement.WalkResult.Success ||
                            result == NPCMovement.WalkResult.Partial)
                        {
                            _consecutiveWalkFailures = 0;
                            if (Config.ManagerVerboseLogging.Value)
                                _manager.Log($"exited building");
                            RestoreCivilianNavMesh();
                            _currentPropertyExterior = null;
                            _exitBuildingContinuation?.Invoke();
                            _exitBuildingContinuation = null;
                        }
                        else if (result == NPCMovement.WalkResult.Failed)
                        {
                            _consecutiveWalkFailures++;
                            _manager.LogWarning($"property exit walk failed ({_consecutiveWalkFailures}/{MAX_WALK_FAILURES})");
                            if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                            {
                                _manager.LogWarning($"warping to property exterior");
                                WarpToPosition(_currentPropertyExterior.Value);
                                _consecutiveWalkFailures = 0;
                                RestoreCivilianNavMesh();
                                _currentPropertyExterior = null;
                                _exitBuildingContinuation?.Invoke();
                                _exitBuildingContinuation = null;
                            }
                        }
                    });

                _manager.GameNpc.Movement.SetDestination(_currentPropertyExterior.Value, _propertyExitWalkCallback, 3f, 1f);
            }
            catch (Exception ex)
            {
                _manager.LogWarning($"ExitBuildingThen walk failed: {ex.Message}");
                RestoreCivilianNavMesh();
                _currentPropertyExterior = null;
                _exitBuildingContinuation?.Invoke();
                _exitBuildingContinuation = null;
            }
        }

        // ==================================================================
        // Helper methods
        // ==================================================================

        /// <summary>
        /// Gets the position to walk to for accessing a storage entity.
        /// Uses NavMeshUtility.GetReachableAccessPoint to check pathability on the current NavMesh.
        /// Sets isReachable=true if NavMesh pathfinding found a route, false if falling back.
        /// </summary>
        private Vector3? GetStorageAccessPosition(
            Il2CppScheduleOne.ObjectScripts.PlaceableStorageEntity storage, out bool isReachable)
        {
            isReachable = false;
            if (storage == null) return null;

            try
            {
                var transit = storage.TryCast<Il2CppScheduleOne.Management.ITransitEntity>();
                if (transit != null && _manager.GameNpc != null)
                {
                    var reachable = NavMeshUtility.GetReachableAccessPoint(transit, _manager.GameNpc);
                    if (reachable != null)
                    {
                        isReachable = true;
                        return reachable.position;
                    }

                    // Not reachable — return raw access point as fallback position
                    var accessPoints = transit.AccessPoints;
                    if (accessPoints != null && accessPoints.Length > 0 && accessPoints[0] != null)
                        return accessPoints[0].position;
                }
            }
            catch { }

            try
            {
                return storage.transform.position;
            }
            catch { return null; }
        }

        /// <summary>
        /// Gets the property's exterior position for a storage entity's parent property.
        /// Uses SpawnPoint as the waypoint where civilian and employee NavMesh surfaces
        /// overlap, enabling the two-phase NavMesh switch for indoor navigation.
        /// </summary>
        private static Vector3? GetPropertyExteriorPoint(
            Il2CppScheduleOne.ObjectScripts.PlaceableStorageEntity storage)
        {
            if (storage == null) return null;

            try
            {
                var buildable = storage.TryCast<Il2CppScheduleOne.EntityFramework.BuildableItem>();
                if (buildable == null) return null;

                var property = buildable.ParentProperty;
                if (property == null) return null;

                // Use SpawnPoint (exterior entrance) if available
                var spawnPoint = property.SpawnPoint;
                if (spawnPoint != null)
                    return spawnPoint.position;

                // Fall back to building's own transform (ground-level exterior)
                return property.transform.position;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Checks if a storage entity has any items (including cash).
        /// </summary>
        private static bool SourceHasItems(Il2CppScheduleOne.ObjectScripts.PlaceableStorageEntity storage)
        {
            if (storage?.StorageEntity?.ItemSlots == null) return false;

            try
            {
                for (int i = 0; i < storage.StorageEntity.ItemSlots.Count; i++)
                {
                    var slot = storage.StorageEntity.ItemSlots[i];
                    if (slot?.ItemInstance == null) continue;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Checks if the destination can accept at least one item from the source (including cash).
        /// Prevents wasted trips where the manager picks up items only to find the destination full.
        /// </summary>
        private static bool DestinationCanAcceptSourceItems(
            Il2CppScheduleOne.ObjectScripts.PlaceableStorageEntity source,
            Il2CppScheduleOne.ObjectScripts.PlaceableStorageEntity destination)
        {
            if (source?.StorageEntity?.ItemSlots == null) return false;
            if (destination?.StorageEntity == null) return false;

            try
            {
                for (int i = 0; i < source.StorageEntity.ItemSlots.Count; i++)
                {
                    var slot = source.StorageEntity.ItemSlots[i];
                    if (slot?.ItemInstance == null) continue;

                    if (StorageFilterHelper.HowManyCanFitFiltered(destination.StorageEntity, slot.ItemInstance) > 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Deposits cash into a storage entity, returning the amount actually deposited.
        /// Phase 1: fills existing cash slots (up to $1000 each).
        /// Phase 2: creates new cash in empty filter-compatible slots via direct insert
        /// (bypasses GetCopy which loses the Balance on CashInstance).
        /// </summary>
        private static float DepositCash(Il2CppScheduleOne.Storage.StorageEntity storage, float amount)
        {
            const float MAX_PER_SLOT = 1000f;
            float deposited = 0f;

            // Phase 1: top up existing cash slots
            for (int i = 0; i < storage.ItemSlots.Count; i++)
            {
                if (deposited >= amount) break;
                try
                {
                    var slot = storage.ItemSlots[i];
                    if (slot?.ItemInstance == null) continue;
                    var existing = slot.ItemInstance.TryCast<CashInstance>();
                    if (existing == null) continue;
                    float space = MAX_PER_SLOT - existing.Balance;
                    if (space <= 0f) continue;
                    float add = Math.Min(amount - deposited, space);
                    existing.ChangeBalance(add);
                    deposited += add;
                }
                catch { }
            }

            // Phase 2: create new cash in empty slots
            while (deposited < amount)
            {
                float slotAmount = Math.Min(amount - deposited, MAX_PER_SLOT);
                try
                {
                    var newCash = NetworkSingleton<Il2CppScheduleOne.Money.MoneyManager>.Instance
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
                    deposited += slotAmount;
                }
                catch { break; }
            }

            return deposited;
        }

        private Il2CppScheduleOne.NPCs.NPCInventory GetNpcInventory()
        {
            try
            {
                return _manager.GameNpc?.GetComponent<Il2CppScheduleOne.NPCs.NPCInventory>();
            }
            catch { return null; }
        }

        /// <summary>
        /// Clears distribution items from NPC inventory. Preserves supply cash
        /// (cash without a destination tag, used for Night Market purchases).
        /// </summary>
        private void ClearNonCashNpcInventory()
        {
            var inventory = GetNpcInventory();
            if (inventory?.ItemSlots == null) return;

            try
            {
                for (int i = 0; i < inventory.ItemSlots.Count; i++)
                {
                    var slot = inventory.ItemSlots[i];
                    if (slot?.ItemInstance == null) continue;

                    // Preserve supply cash (cash without a distribution destination)
                    if (slot.ItemInstance.TryCast<CashInstance>() != null && !_slotDestinations.ContainsKey(i))
                        continue;

                    slot.ClearStoredInstance();
                }
            }
            catch { }
            _slotDestinations.Clear();
        }

        /// <summary>
        /// Counts free (empty, unlocked) NPC inventory slots.
        /// </summary>
        private static int GetFreeNpcSlots(Il2CppScheduleOne.NPCs.NPCInventory inventory)
        {
            if (inventory?.ItemSlots == null) return 0;
            int free = 0;
            try
            {
                for (int i = 0; i < inventory.ItemSlots.Count; i++)
                {
                    var slot = inventory.ItemSlots[i];
                    if (slot != null && slot.ItemInstance == null && !slot.IsLocked && !slot.IsAddLocked)
                        free++;
                }
            }
            catch { }
            return free;
        }

        /// <summary>
        /// Returns the index of the first empty, unlocked NPC inventory slot, or -1 if full.
        /// Used instead of InsertItem to prevent stacking with leftover items from other routes.
        /// </summary>
        private static int FindEmptyNpcSlot(Il2CppScheduleOne.NPCs.NPCInventory inventory)
        {
            if (inventory?.ItemSlots == null) return -1;
            try
            {
                for (int i = 0; i < inventory.ItemSlots.Count; i++)
                {
                    var slot = inventory.ItemSlots[i];
                    if (slot != null && slot.ItemInstance == null && !slot.IsLocked && !slot.IsAddLocked)
                        return i;
                }
            }
            catch { }
            return -1;
        }

        private void WarpToPosition(Vector3 position)
        {
            try
            {
                // Sample NavMesh position before warping (vanilla Employee.SetDestination pattern)
                // areaMask -1 = all NavMesh areas including indoor areas
                if (NavMeshUtility.SamplePosition(position, out NavMeshHit hit, 5f, -1))
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
    }
}
