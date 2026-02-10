using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Property;
using MelonLoader;
using S1API.GameTime;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Lifecycle controller for the Manager system.
    /// Handles hiring, daily wage deduction, and tick-based state updates.
    /// </summary>
    public class ManagerController
    {
        private readonly MelonLogger.Instance _logger;

        private int _managerIdCounter;

        public static ManagerController Instance { get; private set; }

        private int _lastSupplyCheckMinute = -1;

        public ManagerController(MelonLogger.Instance logger)
        {
            _logger = logger;
            Instance = this;

            TimeManager.OnSleepEnd += OnSleepEnd;
            TimeManager.OnTick += OnTimeTick;
        }

        /// <summary>
        /// Supply check — triggers supply runs for idle, paid managers.
        /// Called on every game tick; throttled to once per ~10 in-game minutes.
        /// </summary>
        private void OnTimeTick()
        {
            if (!NetworkHelper.IsHost) return;

            int currentTime = TimeManager.CurrentTime;
            int totalMinutes = (currentTime / 100) * 60 + (currentTime % 100);

            if (_lastSupplyCheckMinute >= 0)
            {
                int diff = totalMinutes - _lastSupplyCheckMinute;
                if (diff < 0) diff += 1440; // wrap around midnight
                if (diff < 10) return;
            }
            _lastSupplyCheckMinute = totalMinutes;

            try
            {
                foreach (var mgr in ManagerInstance.Active.Values)
                {
                    if (mgr.State != ManagerState.Idle) continue;
                    if (!mgr.PaidForToday) continue;

                    // Supply takes priority
                    bool supplyStarted = mgr.SupplyBehaviour?.TryStartSupplyRun() ?? false;
                    if (supplyStarted) continue;

                    // Supply didn't trigger (fully stocked) — try distribution
                    mgr.DistributionBehaviour?.TryStartDistributionRun();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[ManagerController] OnTimeTick error: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when the player wakes up (matches vanilla Employee pay timing via onSleepEnd).
        /// Resets pay status and deducts wages from each manager's locker.
        /// </summary>
        private void OnSleepEnd(int skippedMinutes)
        {
            if (!NetworkHelper.IsHost) return;

            _lastSupplyCheckMinute = -1;

            _logger.Msg($"[OnSleepEnd] Processing wages for {ManagerInstance.Active.Count} managers");

            try
            {
                // Reset pay status for the new day — everyone starts unpaid
                foreach (var mgr in ManagerInstance.Active.Values)
                    mgr.PaidForToday = false;

                foreach (var mgr in ManagerInstance.Active.Values)
                {
                    if (mgr.State == ManagerState.Fired) continue;

                    float wage = Config.ManagerDailyWage.Value;

                    if (!mgr.HasLocker)
                    {
                        mgr.State = ManagerState.NoFunds;
                        _logger.Msg($"Manager {mgr.Id}: no locker assigned, cannot pay wage");
                        if (!mgr.NoLockerTextSent)
                        {
                            mgr.NoLockerTextSent = true;
                            mgr.SendTextMessage("Boss, I don't have a locker assigned. Place one nearby and I'll get to work.");
                        }
                        continue;
                    }

                    float available = mgr.GetLockerCash();
                    if (available >= wage)
                    {
                        mgr.RemoveLockerCash(wage);
                        mgr.PaidForToday = true;
                        mgr.NoFundsTextSent = false;
                        mgr.State = ManagerState.Idle;
                        _logger.Msg($"Manager {mgr.Id}: paid ${wage} wage from locker (remaining: ${available - wage:F0})");
                    }
                    else
                    {
                        mgr.State = ManagerState.NoFunds;
                        _logger.Msg($"Manager {mgr.Id}: insufficient funds in locker (has: ${available:F0}, need: ${wage})");
                        if (!mgr.NoFundsTextSent)
                        {
                            mgr.NoFundsTextSent = true;
                            string homeType = mgr.AssignedLocker?.HomeType?.ToLower() ?? "locker";
                            if (available <= 0f)
                                mgr.SendTextMessage($"Boss, there's no money in my {homeType}! I need ${wage:F0} for today's wage.");
                            else
                                mgr.SendTextMessage($"Boss, my {homeType} only has ${available:F0} but I need ${wage:F0} for today's wage. Drop some more cash in!");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[ManagerController] OnDayPass error: {ex.Message}");
            }
        }

        /// <summary>
        /// Hires a manager at the specified business. Returns the created instance, or null on failure.
        /// </summary>
        public ManagerInstance HireManager(Business business)
        {
            if (business == null)
            {
                _logger.Warning("HireManager: business is null");
                return null;
            }

            if (ManagerInstance.HasManager(business.PropertyCode))
            {
                _logger.Warning($"HireManager: {business.PropertyCode} already has a manager");
                return null;
            }

            _managerIdCounter++;
            int seed = (int)(DateTime.Now.Ticks % int.MaxValue) ^ _managerIdCounter;
            string id = $"mgr_{business.PropertyCode}_{_managerIdCounter}";

            var instance = ManagerInstance.Create(id, seed, business);
            if (instance == null)
            {
                _logger.Error($"HireManager: failed to create manager for {business.PropertyCode}");
                return null;
            }

            // Deduct signing fee from player
            try
            {
                float signingFee = Config.ManagerSigningFee.Value;
                Il2CppScheduleOne.Money.MoneyManager moneyManager =
                    NetworkSingleton<Il2CppScheduleOne.Money.MoneyManager>.Instance;

                if (moneyManager != null && moneyManager.cashBalance >= signingFee)
                {
                    moneyManager.ChangeCashBalance(-signingFee, true, true);
                    _logger.Msg($"Deducted ${signingFee} signing fee for manager {id}");
                }
                else
                {
                    _logger.Warning($"HireManager: insufficient cash for signing fee (${signingFee})");
                    instance.Despawn();
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"HireManager: signing fee deduction failed: {ex.Message}");
                instance.Despawn();
                return null;
            }

            instance.SendGreeting($"Hey boss! I'm your new manager at {business.PropertyName}. Use the clipboard to assign me a locker and I'll get to work.");

            // Grant the clipboard tool if not already acquired (same as vanilla Employee.RpcLogic___Initialize)
            try
            {
                var varDb = NetworkSingleton<Il2CppScheduleOne.Variables.VariableDatabase>.Instance;
                if (varDb != null && !varDb.GetValue<bool>("ClipboardAcquired"))
                    varDb.SetVariableValue("ClipboardAcquired", true.ToString());
            }
            catch (Exception ex)
            {
                _logger.Warning($"HireManager: clipboard grant check failed: {ex.Message}");
            }

            // Sync manager state to clients via dedicated SyncVar
            ConfigSyncData.Instance?.PublishManagerState();

            // FishNet may assign ObjectId asynchronously — re-publish after a short delay
            if (instance.NetworkObjectId == 0)
                MelonLoader.MelonCoroutines.Start(DelayedResync(instance));

            return instance;
        }

        private static System.Collections.IEnumerator DelayedResync(ManagerInstance instance)
        {
            yield return new WaitForSeconds(1f);
            try
            {
                if (instance.GameNpc?.NetworkObject != null)
                {
                    instance.NetworkObjectId = instance.GameNpc.NetworkObject.ObjectId;
                    ConfigSyncData.Instance?.PublishManagerState();
                }
            }
            catch { }
        }

        /// <summary>
        /// Hires a manager at a business by property code. Called on the host
        /// when a remote client sends MANAGER_HIRE via SyncVar.
        /// </summary>
        public void HireManagerRemote(string propertyCode)
        {
            foreach (var biz in Business.OwnedBusinesses)
            {
                if (biz != null && string.Equals(biz.PropertyCode, propertyCode, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Msg($"HireManagerRemote: hiring at {propertyCode} (client request)");
                    HireManager(biz);
                    return;
                }
            }
            _logger.Warning($"HireManagerRemote: business '{propertyCode}' not found in OwnedBusinesses");
        }

        /// <summary>
        /// Fires a manager. Walks them back to spawn, then despawns.
        /// </summary>
        public void FireManager(string managerId)
        {
            if (!ManagerInstance.Active.TryGetValue(managerId, out var instance))
            {
                _logger.Warning($"FireManager: no manager with id {managerId}");
                return;
            }

            instance.State = ManagerState.Fired;

            // Clear text message thread (stale after despawn)
            instance.ClearMessages();

            // Clear locker assignment and config before walk-away
            if (instance.AssignedLocker != null)
            {
                try
                {
                    instance.AssignedLocker.Storage.StorageEntityName = instance.AssignedLocker.HomeType;
                    instance.AssignedLocker.Storage.StorageEntitySubtitle = string.Empty;
                }
                catch { }
            }
            instance.ClearLocker();
            instance.Configuration.ClearAll();

            // Walk to spawn point then despawn
            instance.WalkAwayAndDespawn();

            // Sync manager state to clients via dedicated SyncVar
            ConfigSyncData.Instance?.PublishManagerState();

            _logger.Msg($"Fired manager {managerId}");
        }

        /// <summary>
        /// Transfers a manager to a new business. Walks the NPC to the new location.
        /// </summary>
        public void TransferManager(string managerId, string targetPropertyCode)
        {
            if (!ManagerInstance.Active.TryGetValue(managerId, out var instance))
            {
                _logger.Warning($"TransferManager: no manager with id {managerId}");
                return;
            }

            // Find target business
            Business targetBusiness = null;
            foreach (var biz in Business.OwnedBusinesses)
            {
                if (biz != null && string.Equals(biz.PropertyCode, targetPropertyCode, StringComparison.OrdinalIgnoreCase))
                {
                    targetBusiness = biz;
                    break;
                }
            }

            if (targetBusiness == null)
            {
                _logger.Warning($"TransferManager: target business '{targetPropertyCode}' not found");
                return;
            }

            // Check target doesn't already have a manager
            if (ManagerInstance.HasManager(targetPropertyCode))
            {
                _logger.Warning($"TransferManager: {targetPropertyCode} already has a manager");
                return;
            }

            // Set state to Transferring
            instance.State = ManagerState.Transferring;

            // Clear configuration (not valid for new business)
            instance.Configuration.ClearAll();

            // Release old locker
            if (instance.AssignedLocker != null)
            {
                try
                {
                    instance.AssignedLocker.Storage.StorageEntityName = instance.AssignedLocker.HomeType;
                    instance.AssignedLocker.Storage.StorageEntitySubtitle = string.Empty;
                }
                catch { }
            }
            instance.ClearLocker();

            // Update business assignment
            string oldBizCode = instance.BusinessPropertyCode;
            instance.AssignedBusiness = targetBusiness;
            instance.BusinessPropertyCode = targetBusiness.PropertyCode;

            // Send text message
            instance.SendTextMessage($"On my way to {targetBusiness.PropertyName}. I'll get set up there shortly.");

            // Walk to new business
            var location = ManagerLocations.GetLocation(targetPropertyCode);
            if (location != null)
            {
                WalkToNewBusiness(instance, location, targetBusiness);
            }
            else
            {
                // No registered location — just teleport
                _logger.Warning($"No ManagerLocation for {targetPropertyCode}, teleporting manager");
                CompleteTransfer(instance, targetBusiness);
            }

            // Sync state to clients
            ConfigSyncData.Instance?.PublishManagerState();

            _logger.Msg($"Manager {managerId} transferring from {oldBizCode} to {targetPropertyCode}");
        }

        /// <summary>
        /// Walks manager NPC to a new business location, then completes the transfer.
        /// </summary>
        private void WalkToNewBusiness(ManagerInstance instance, ManagerLocations.BusinessLocation location, Business targetBusiness)
        {
            try
            {
                if (instance.GameNpc?.Movement == null)
                {
                    CompleteTransfer(instance, targetBusiness);
                    return;
                }

                // Track target location for EnsureMoving resume
                instance.TargetLocation = location;
                instance.ArrivedAtDestination = false;

                // Hold reference to prevent GC
                Il2CppSystem.Action<NPCMovement.WalkResult> callback =
                    (Il2CppSystem.Action<NPCMovement.WalkResult>)
                    new Action<NPCMovement.WalkResult>(result =>
                    {
                        _logger.Msg($"Manager {instance.Id} arrived at new business (result={result})");
                        CompleteTransfer(instance, targetBusiness);
                        if (result == NPCMovement.WalkResult.Success ||
                            result == NPCMovement.WalkResult.Partial)
                        {
                            try
                            {
                                instance.GameNpc?.Movement?.FaceDirection(location.DestRotation * Vector3.forward);
                            }
                            catch { }
                        }
                    });

                // Store callback reference on the instance to prevent GC
                instance._transferCallback = callback;

                instance.GameNpc.Movement.SetDestination(location.Destination, callback, 3f, 1f);
                _logger.Msg($"Manager {instance.Id} walking to {targetBusiness.PropertyCode}");
            }
            catch (Exception ex)
            {
                _logger.Error($"WalkToNewBusiness failed: {ex.Message}");
                CompleteTransfer(instance, targetBusiness);
            }
        }

        /// <summary>
        /// Completes a transfer: assigns new locker, resets state, syncs.
        /// </summary>
        private void CompleteTransfer(ManagerInstance instance, Business targetBusiness)
        {
            try
            {
                instance.TargetLocation = null;
                instance.ArrivedAtDestination = true;
                instance.State = ManagerState.Idle;

                instance.SendTextMessage($"I've arrived at {targetBusiness.PropertyName}. Use the clipboard to assign me a locker and I'll get started.");

                // Reset daily pay status
                instance.PaidForToday = false;
                instance.NoLockerTextSent = false;
                instance.NoFundsTextSent = false;

                // Sync updated state
                ConfigSyncData.Instance?.PublishManagerState();

                _logger.Msg($"Manager {instance.Id} transfer to {targetBusiness.PropertyCode} complete");
            }
            catch (Exception ex)
            {
                _logger.Error($"CompleteTransfer failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles remote transfer request from a client.
        /// </summary>
        public void TransferManagerRemote(string managerId, string targetPropertyCode)
        {
            _logger.Msg($"TransferManagerRemote: {managerId} to {targetPropertyCode}");
            TransferManager(managerId, targetPropertyCode);
        }

        /// <summary>
        /// Handles remote fire request from a client.
        /// </summary>
        public void FireManagerRemote(string managerId)
        {
            _logger.Msg($"FireManagerRemote: {managerId}");
            FireManager(managerId);
        }

        /// <summary>
        /// Finds the nearest owned business to the player.
        /// </summary>
        public static Business FindNearestOwnedBusiness()
        {
            try
            {
                var player = PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                if (player == null) return null;

                var playerPos = player.transform.position;
                Business nearest = null;
                float nearestDist = float.MaxValue;

                foreach (var biz in Business.OwnedBusinesses)
                {
                    if (biz == null) continue;
                    float dist = Vector3.Distance(playerPos, biz.transform.position);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = biz;
                    }
                }

                return nearest;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Wage check throttle
        private float _lastWageCheckTime;
        private const float WAGE_CHECK_INTERVAL = 2f;

        /// <summary>
        /// Checks unpaid managers and immediately deducts wages if cash is available.
        /// Called from Core.OnLateUpdate on the host. Sets the manager to Idle (working)
        /// once paid, so they start working as soon as cash is deposited.
        /// </summary>
        public void CheckImmediateWages()
        {
            if (!NetworkHelper.IsHost) return;

            float now = UnityEngine.Time.time;
            if (now - _lastWageCheckTime < WAGE_CHECK_INTERVAL) return;
            _lastWageCheckTime = now;

            try
            {
                foreach (var mgr in ManagerInstance.Active.Values)
                {
                    if (mgr.PaidForToday) continue;
                    if (mgr.State == ManagerState.Fired) continue;
                    if (!mgr.HasLocker) continue;

                    float wage = Config.ManagerDailyWage.Value;
                    float available = mgr.GetLockerCash();
                    if (available >= wage)
                    {
                        mgr.RemoveLockerCash(wage);
                        mgr.PaidForToday = true;
                        mgr.NoFundsTextSent = false;
                        mgr.State = ManagerState.Idle;
                        _logger.Msg($"Manager {mgr.Id}: immediate wage payment ${wage} (remaining: ${available - wage:F0})");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[ManagerController] CheckImmediateWages error: {ex.Message}");
            }
        }

        /// <summary>
        /// Cleanup — unsubscribe from events.
        /// </summary>
        public void Cleanup()
        {
            TimeManager.OnSleepEnd -= OnSleepEnd;
            TimeManager.OnTick -= OnTimeTick;
            ManagerInstance.CleanupAll();
        }

        // ── Debug helpers ──

        /// <summary>
        /// Debug: spawns a manager at the nearest owned business.
        /// </summary>
        public static void DebugSpawnManager()
        {
            if (Instance == null) return;

            var business = FindNearestOwnedBusiness();
            if (business == null)
            {
                Instance._logger.Warning("[Debug] No owned businesses found");
                return;
            }

            if (ManagerInstance.HasManager(business.PropertyCode))
            {
                Instance._logger.Warning($"[Debug] {business.PropertyCode} already has a manager");
                return;
            }

            // Bypass signing fee for debug
            Instance._managerIdCounter++;
            int seed = (int)(DateTime.Now.Ticks % int.MaxValue) ^ Instance._managerIdCounter;
            string id = $"mgr_{business.PropertyCode}_{Instance._managerIdCounter}";

            var instance = ManagerInstance.Create(id, seed, business);
            if (instance != null)
            {
                Instance._logger.Msg($"Spawned manager {id} at {business.PropertyCode}");
            }
        }

        /// <summary>
        /// Debug: gets a status summary of all active managers.
        /// </summary>
        public static string DebugGetStatus()
        {
            var lines = new List<string>();
            lines.Add($"Active managers: {ManagerInstance.Active.Count}");

            foreach (var mgr in ManagerInstance.Active.Values)
            {
                var pos = mgr.Position;
                string posStr = pos.HasValue ? $"({pos.Value.x:F0},{pos.Value.z:F0})" : "?";
                string lockerStr = mgr.HasLocker ? $"locker=${mgr.GetLockerCash():F0}" : "no locker";
                lines.Add($"  {mgr.Id}: {mgr.State} {lockerStr} at {posStr}");
            }

            return string.Join("\n", lines);
        }
    }
}
