using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.NPCs;
using MelonLoader;
using OverTheCounter.Utilities;
using System;
using System.Collections.Generic;
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
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("ManagerDistribution");

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

        public DistributionState State { get; private set; } = DistributionState.Idle;

        // Round-robin persistence across runs
        private int _lastRouteIndex = -1;

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

        // NavMesh switching — civilian (outdoor) vs employee (indoor) settings
        private int _savedAgentTypeID;
        private int _savedAreaMask;
        private bool _usingEmployeeNavMesh;
        private Vector3? _currentPropertyExterior; // saved so we can walk back out after indoor interaction
        private Action _exitBuildingContinuation;  // action to run after exiting building on employee NavMesh

        // IL2CPP callback references (prevent GC collection)
        private Il2CppSystem.Action<NPCMovement.WalkResult> _sourcePropertyWalkCallback;
        private Il2CppSystem.Action<NPCMovement.WalkResult> _sourceWalkCallback;
        private Il2CppSystem.Action<NPCMovement.WalkResult> _destPropertyWalkCallback;
        private Il2CppSystem.Action<NPCMovement.WalkResult> _destWalkCallback;
        private Il2CppSystem.Action<NPCMovement.WalkResult> _propertyExitWalkCallback;
        private Il2CppSystem.Action<NPCMovement.WalkResult> _idleWalkCallback;

        public ManagerDistributionBehaviour(ManagerInstance manager)
        {
            _manager = manager;
        }

        // ==================================================================
        // Entry point
        // ==================================================================

        /// <summary>
        /// Entry point — called from ManagerController when supply has nothing to do.
        /// Returns true if a distribution run was started.
        /// </summary>
        public bool TryStartDistributionRun()
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

            // Build route plan starting from round-robin position.
            // Include ALL configured routes — not just those with items now — so the plan
            // stays dynamic. Sources may gain items mid-run (chained deliveries, player
            // actions, other managers). StartNextRoute() re-validates each route on arrival.
            var routes = _manager.Configuration.Routes;
            _routePlan = new List<int>();
            bool anyRouteHasItems = false;

            for (int offset = 0; offset < routes.Length; offset++)
            {
                int idx = (_lastRouteIndex + 1 + offset) % routes.Length;
                var route = routes[idx];
                if (!route.IsConfigured) continue;
                _routePlan.Add(idx);
                if (!anyRouteHasItems && SourceHasItems(route.Source)
                    && DestinationCanAcceptSourceItems(route.Source, route.Destination))
                    anyRouteHasItems = true;
            }

            // Only start if at least one route has transferable items right now
            if (_routePlan.Count == 0 || !anyRouteHasItems) return false;

            _routePlanStep = 0;
            _consecutiveWalkFailures = 0;
            _manager.State = ManagerState.DistributionRun;

            Logger.Msg($"Manager {_manager.Id}: starting distribution run ({_routePlan.Count} routes)");
            StartNextRoute();
            return true;
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
                    Logger.Msg($"Manager {_manager.Id}: route {_currentRouteIndex} skipped (no longer valid/has items/dest full)");
                    _routePlanStep++;
                    continue;
                }

                WalkToSource();
                return;
            }

            // All routes processed — check for immediate work before walking home
            Logger.Msg($"Manager {_manager.Id}: all distribution routes processed, checking for more work");
            State = DistributionState.Idle;
            _routePlan = null;
            _currentRoute = null;
            _manager.State = ManagerState.Idle;

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
                Logger.Warning($"Manager {_manager.Id}: can't find source position for route {_currentRouteIndex}");
                _routePlanStep++;
                StartNextRoute();
                return;
            }

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
                    Logger.Warning($"Manager {_manager.Id}: source not reachable and no property exterior, attempting direct walk");
                    State = DistributionState.WalkingToSource;
                    IssueWalkToSource(accessPos.Value);
                    return;
                }

                Logger.Msg($"Manager {_manager.Id}: source is indoor, walking to property exterior first");
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
                                Logger.Msg($"Manager {_manager.Id}: arrived at property exterior, switching to employee NavMesh for indoor walk");
                                if (SwitchToEmployeeNavMesh())
                                {
                                    State = DistributionState.WalkingToSource;
                                    IssueWalkToSource(capturedAccessPos);
                                }
                                else
                                {
                                    // No employee settings available — warp as fallback
                                    Logger.Warning($"Manager {_manager.Id}: employee NavMesh unavailable, warping to source");
                                    WarpToPosition(capturedAccessPos);
                                    State = DistributionState.AtSource;
                                    _sourceArrivalTime = UnityEngine.Time.time;
                                }
                            }
                            else if (result == NPCMovement.WalkResult.Failed)
                            {
                                _consecutiveWalkFailures++;
                                Logger.Warning($"Manager {_manager.Id}: source property walk failed ({_consecutiveWalkFailures}/{MAX_WALK_FAILURES})");
                                if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                                {
                                    Logger.Warning($"Manager {_manager.Id}: warping to source storage after {MAX_WALK_FAILURES} failures");
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
                    Logger.Error($"Manager {_manager.Id}: WalkToSourceProperty failed: {ex.Message}");
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
                            Logger.Warning($"Manager {_manager.Id}: source walk failed ({_consecutiveWalkFailures}/{MAX_WALK_FAILURES})");
                            if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                            {
                                Logger.Warning($"Manager {_manager.Id}: warping to source after {MAX_WALK_FAILURES} failures");
                                WarpToPosition(target);
                                State = DistributionState.AtSource;
                                _sourceArrivalTime = UnityEngine.Time.time;
                                _consecutiveWalkFailures = 0;
                            }
                        }
                    });

                _manager.GameNpc.Movement.SetDestination(target, _sourceWalkCallback, 2f, 1f);
                Logger.Msg($"Manager {_manager.Id}: walking to source for route {_currentRouteIndex}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Manager {_manager.Id}: IssueWalkToSource failed: {ex.Message}");
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
            var npcInventory = GetNpcInventory();
            int totalPickedUp = 0;

            if (source?.StorageEntity != null && npcInventory != null)
            {
                int freeSlots = GetFreeNpcSlots(npcInventory);

                for (int i = 0; i < source.StorageEntity.ItemSlots.Count; i++)
                {
                    if (freeSlots <= 0) break;

                    try
                    {
                        var slot = source.StorageEntity.ItemSlots[i];
                        if (slot?.ItemInstance == null) continue;

                        // Skip cash
                        if (slot.ItemInstance.TryCast<CashInstance>() != null) continue;

                        int qty = slot.Quantity;

                        // Clone the actual item (GetCopy preserves product type, GetDefaultInstance does not)
                        var copy = slot.ItemInstance.GetCopy(qty);
                        if (copy == null) continue;

                        // Remove from source, then add copy to NPC inventory
                        slot.ChangeQuantity(-qty);
                        npcInventory.InsertItem(copy);

                        totalPickedUp += qty;
                        freeSlots--;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Manager {_manager.Id}: pickup slot {i} failed: {ex.Message}");
                    }
                }
            }

            Logger.Msg($"Manager {_manager.Id}: picked up {totalPickedUp} items from route {_currentRouteIndex} source");

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
                Logger.Warning($"Manager {_manager.Id}: can't find dest position for route {_currentRouteIndex}");
                ClearNonCashNpcInventory();
                _routePlanStep++;
                StartNextRoute();
                return;
            }

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
                    Logger.Warning($"Manager {_manager.Id}: dest not reachable and no property exterior, attempting direct walk");
                    State = DistributionState.WalkingToDest;
                    IssueWalkToDest(accessPos.Value);
                    return;
                }

                Logger.Msg($"Manager {_manager.Id}: dest is indoor, walking to property exterior first");
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
                                Logger.Msg($"Manager {_manager.Id}: arrived at property exterior, switching to employee NavMesh for indoor walk");
                                if (SwitchToEmployeeNavMesh())
                                {
                                    State = DistributionState.WalkingToDest;
                                    IssueWalkToDest(capturedAccessPos);
                                }
                                else
                                {
                                    // No employee settings available — warp as fallback
                                    Logger.Warning($"Manager {_manager.Id}: employee NavMesh unavailable, warping to dest");
                                    WarpToPosition(capturedAccessPos);
                                    State = DistributionState.AtDest;
                                    _destArrivalTime = UnityEngine.Time.time;
                                }
                            }
                            else if (result == NPCMovement.WalkResult.Failed)
                            {
                                _consecutiveWalkFailures++;
                                Logger.Warning($"Manager {_manager.Id}: dest property walk failed ({_consecutiveWalkFailures}/{MAX_WALK_FAILURES})");
                                if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                                {
                                    Logger.Warning($"Manager {_manager.Id}: warping to dest storage after {MAX_WALK_FAILURES} failures");
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
                    Logger.Error($"Manager {_manager.Id}: WalkToDestProperty failed: {ex.Message}");
                    ClearNonCashNpcInventory();
                    _routePlanStep++;
                    StartNextRoute();
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
                            Logger.Warning($"Manager {_manager.Id}: dest walk failed ({_consecutiveWalkFailures}/{MAX_WALK_FAILURES})");
                            if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                            {
                                Logger.Warning($"Manager {_manager.Id}: warping to dest after {MAX_WALK_FAILURES} failures");
                                WarpToPosition(target);
                                State = DistributionState.AtDest;
                                _destArrivalTime = UnityEngine.Time.time;
                                _consecutiveWalkFailures = 0;
                            }
                        }
                    });

                _manager.GameNpc.Movement.SetDestination(target, _destWalkCallback, 2f, 1f);
                Logger.Msg($"Manager {_manager.Id}: walking to destination for route {_currentRouteIndex}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Manager {_manager.Id}: IssueWalkToDest failed: {ex.Message}");
                ClearNonCashNpcInventory();
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

            if (destination?.StorageEntity != null && npcInventory != null)
            {
                for (int i = 0; i < npcInventory.ItemSlots.Count; i++)
                {
                    try
                    {
                        var slot = npcInventory.ItemSlots[i];
                        if (slot?.ItemInstance == null) continue;

                        // Skip cash
                        if (slot.ItemInstance.TryCast<CashInstance>() != null) continue;

                        int qty = slot.Quantity;

                        // Use GetCopy to preserve actual product type (jar, brick, etc.)
                        int canFit = destination.StorageEntity.HowManyCanFit(slot.ItemInstance);
                        if (canFit <= 0)
                        {
                            totalOverflow += qty;
                            slot.ClearStoredInstance();
                            continue;
                        }

                        if (canFit < qty)
                        {
                            // Partial deposit — copy only what fits
                            var partialCopy = slot.ItemInstance.GetCopy(canFit);
                            destination.StorageEntity.InsertItem(partialCopy, true);
                            totalDeposited += canFit;
                            totalOverflow += (qty - canFit);
                            slot.ChangeQuantity(-qty); // clear the rest (overflow)
                        }
                        else
                        {
                            var copy = slot.ItemInstance.GetCopy(qty);
                            slot.ChangeQuantity(-qty);
                            destination.StorageEntity.InsertItem(copy, true);
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
                Logger.Warning($"Manager {_manager.Id}: destination storage or NPC inventory gone, clearing items");
                ClearNonCashNpcInventory();
            }

            if (totalOverflow > 0)
                Logger.Warning($"Manager {_manager.Id}: {totalOverflow} items didn't fit in destination for route {_currentRouteIndex}");

            Logger.Msg($"Manager {_manager.Id}: deposited {totalDeposited} items at route {_currentRouteIndex} destination");

            // Walk out of building on employee NavMesh before restoring civilian for outdoor travel
            _lastRouteIndex = _currentRouteIndex;
            _routePlanStep++;
            ExitBuildingThen(() => StartNextRoute());
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
                Logger.Warning($"Manager {_manager.Id}: no business location after distribution");
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
                Logger.Msg($"Manager {_manager.Id}: walking to idle point after distribution");
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

        private void FinishRun()
        {
            State = DistributionState.Idle;
            _manager.State = ManagerState.Idle;
            _routePlan = null;
            _currentRoute = null;
            Logger.Msg($"Manager {_manager.Id}: distribution run complete");

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

            Logger.Msg($"Manager {_manager.Id}: distribution run cancelled (was {State})");

            RestoreCivilianNavMesh();
            ClearNonCashNpcInventory();
            State = DistributionState.Idle;
            _routePlan = null;
            _currentRoute = null;
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
                        Logger.Msg($"Manager {_manager.Id}: resuming distribution walk (dist={dist:F1}m)");
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
                Logger.Warning($"Manager {_manager.Id}: EnsureMovingDuringRun failed: {ex.Message}");
            }
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
                    return $"I'm heading to pick up product for route {_currentRouteIndex + 1}.";
                case DistributionState.AtSource:
                    return "I'm loading up product for delivery.";
                case DistributionState.WalkingToDestProperty:
                case DistributionState.WalkingToDest:
                    return $"I'm delivering product for route {_currentRouteIndex + 1}.";
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
                Logger.Warning($"Manager {_manager.Id}: no employee NavMesh settings available");
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
                Logger.Msg($"Manager {_manager.Id}: switched to employee NavMesh");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Manager {_manager.Id}: SwitchToEmployeeNavMesh failed: {ex.Message}");
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
                Logger.Msg($"Manager {_manager.Id}: restored civilian NavMesh");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Manager {_manager.Id}: RestoreCivilianNavMesh failed: {ex.Message}");
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

            Logger.Msg($"Manager {_manager.Id}: walking to property exterior before continuing");
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
                            Logger.Msg($"Manager {_manager.Id}: reached property exterior, restoring civilian NavMesh");
                            RestoreCivilianNavMesh();
                            _currentPropertyExterior = null;
                            _exitBuildingContinuation?.Invoke();
                            _exitBuildingContinuation = null;
                        }
                        else if (result == NPCMovement.WalkResult.Failed)
                        {
                            _consecutiveWalkFailures++;
                            Logger.Warning($"Manager {_manager.Id}: property exit walk failed ({_consecutiveWalkFailures}/{MAX_WALK_FAILURES})");
                            if (_consecutiveWalkFailures >= MAX_WALK_FAILURES)
                            {
                                Logger.Warning($"Manager {_manager.Id}: warping to property exterior");
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
                Logger.Error($"Manager {_manager.Id}: ExitBuildingThen walk failed: {ex.Message}");
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
        /// Checks if a storage entity has any non-cash items.
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
                    if (slot.ItemInstance.TryCast<CashInstance>() != null) continue;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Checks if the destination can accept at least one non-cash item from the source.
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
                    if (slot.ItemInstance.TryCast<CashInstance>() != null) continue;

                    // Use actual ItemInstance (not GetDefaultInstance) to preserve product type
                    if (destination.StorageEntity.HowManyCanFit(slot.ItemInstance) > 0)
                        return true;
                }
            }
            catch { }
            return false;
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
        /// Clears all non-cash items from NPC inventory.
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
                    if (slot.ItemInstance.TryCast<CashInstance>() != null) continue;
                    slot.ClearStoredInstance();
                }
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
                Logger.Warning($"Manager {_manager.Id}: Warp failed: {ex.Message}");
            }
        }
    }
}
