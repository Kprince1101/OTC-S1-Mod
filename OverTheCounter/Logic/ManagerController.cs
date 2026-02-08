using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Employees;
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

        public ManagerController(MelonLogger.Instance logger)
        {
            _logger = logger;
            Instance = this;

            TimeManager.OnDayPass += OnDayPass;
        }

        /// <summary>
        /// Called once per day. Deducts wages from each manager's cash pool.
        /// Host-only — clients receive state via FishNet sync.
        /// </summary>
        private void OnDayPass()
        {
            if (!NetworkHelper.IsHost) return;

            _logger.Msg($"[OnDayPass] Processing wages for {ManagerInstance.Active.Count} managers");

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

            // Auto-assign nearest available locker
            var locker = FindNearestAvailableLocker(business);
            if (locker != null)
            {
                instance.AssignLocker(locker);
                string lockerType = locker.HomeType?.ToLower() ?? "locker";
                instance.SendGreeting($"Hey boss! I'm your new manager at {business.PropertyName}. Drop some cash in my {lockerType} and I'll get to work.");
            }
            else
            {
                _logger.Warning($"No available locker found for manager {id} at {business.PropertyCode}");
                instance.SendGreeting($"Hey boss! I'm at {business.PropertyName}, but I don't have a locker assigned yet. Place one nearby and I'll get to work.");
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
        /// Fires a manager and despawns the NPC.
        /// </summary>
        public void FireManager(string managerId)
        {
            if (!ManagerInstance.Active.TryGetValue(managerId, out var instance))
            {
                _logger.Warning($"FireManager: no manager with id {managerId}");
                return;
            }

            instance.State = ManagerState.Fired;
            instance.SendTextMessage("Alright, I'm done here. Good luck, boss.");
            instance.Despawn();

            // Sync manager state to clients via dedicated SyncVar
            ConfigSyncData.Instance?.PublishManagerState();

            _logger.Msg($"Fired manager {managerId}");
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

        /// <summary>
        /// Finds the nearest unoccupied EmployeeHome at a business.
        /// Scans all EmployeeHome components in the scene and picks the closest one
        /// to the business that isn't claimed by a vanilla employee or another manager.
        /// </summary>
        public static EmployeeHome FindNearestAvailableLocker(Business business)
        {
            if (business == null) return null;

            try
            {
                var businessPos = business.transform.position;
                var allHomes = UnityEngine.Object.FindObjectsOfType<EmployeeHome>();

                EmployeeHome nearest = null;
                float nearestDist = float.MaxValue;

                foreach (var home in allHomes)
                {
                    if (home == null || home.Storage == null) continue;

                    // Skip if already assigned to a vanilla employee
                    if (home.AssignedEmployee != null) continue;

                    // Skip if already claimed by another manager
                    bool claimedByManager = false;
                    foreach (var mgr in ManagerInstance.Active.Values)
                    {
                        if (mgr.AssignedLocker == home)
                        {
                            claimedByManager = true;
                            break;
                        }
                    }
                    if (claimedByManager) continue;

                    // Only consider lockers close to the business (within 10m)
                    float dist = Vector3.Distance(businessPos, home.transform.position);
                    if (dist > 10f) continue;

                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = home;
                    }
                }

                return nearest;
            }
            catch (Exception ex)
            {
                Melon<Core>.Logger.Warning($"FindNearestAvailableLocker error: {ex.Message}");
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
            TimeManager.OnDayPass -= OnDayPass;
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
                var locker = FindNearestAvailableLocker(business);
                if (locker != null)
                {
                    instance.AssignLocker(locker);
                    Instance._logger.Msg($"[Debug] Auto-assigned locker for manager {id}");
                }

                instance.SendTextMessage($"[DEBUG] Spawned at {business.PropertyName}. Locker: {(locker != null ? "assigned" : "none")}.");
                Instance._logger.Msg($"[Debug] Spawned manager {id} at {business.PropertyCode}");
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
