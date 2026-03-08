using MelonLoader;
using S1API.GameTime;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Property;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.NPCs;
using ScheduleOne.Property;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Lifecycle controller for the Manager system.
    /// Handles hiring, daily wage deduction, and tick-based state updates.
    /// </summary>
    public class ManagerController
    {
        private int _managerIdCounter;

        public int GetIdCounter() => _managerIdCounter;

        public void RestoreIdCounter(int value)
        {
            if (value > _managerIdCounter)
                _managerIdCounter = value;
        }

        public static ManagerController Instance { get; private set; }

        private int _lastSupplyCheckMinute = -1;

        public ManagerController()
        {
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
                    mgr.TryStartNextJob();
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Manager, $"OnTimeTick error: {ex.Message}");
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

            OTCLog.Msg(OTCLog.Systems.Manager, $"[OnSleepEnd] Processing wages for {ManagerInstance.Active.Count} managers");

            try
            {
                // Reset pay status for the new day — everyone starts unpaid
                foreach (var mgr in ManagerInstance.Active.Values)
                    mgr.PaidForToday = false;

                foreach (var mgr in ManagerInstance.Active.Values)
                {
                    if (mgr.State == ManagerState.Fired) continue;

                    float wage = mgr.GetDailyWage();

                    bool midRun = mgr.State == ManagerState.SupplyRun || mgr.State == ManagerState.DistributionRun || mgr.State == ManagerState.Transferring;

                    if (!mgr.HasLocker)
                    {
                        if (!midRun) mgr.State = ManagerState.NoFunds;
                        OTCLog.Msg(OTCLog.Systems.Manager, $"{mgr.Id}: no locker assigned, cannot pay wage");
                        if (!mgr.NoLockerTextSent)
                        {
                            mgr.NoLockerTextSent = true;
                            mgr.SendTextMessage("Boss, I don't have a locker assigned. Place one nearby and I'll get to work.");
                        }
                        continue;
                    }

                    // Return any cash the NPC is carrying back to locker before wage check
                    // (manager may have been mid-supply-run when day passed)
                    mgr.SupplyBehaviour?.ReturnCashToLocker();

                    // Collect Night Market-only item IDs and names for this manager
                    var nightMarketItems = new List<(string id, string name)>();
                    for (int i = 0; i < mgr.Configuration.StockedItemIds.Length; i++)
                    {
                        string itemId = mgr.Configuration.StockedItemIds[i];
                        if (!string.IsNullOrEmpty(itemId) && !ManagerSupplyBehaviour.HasDaytimeStoreOption(itemId))
                        {
                            var def = ScheduleOne.Registry.GetItem(itemId);
                            if (def != null) nightMarketItems.Add((itemId, def.Name));
                        }
                    }

                    float available = mgr.GetLockerCash();
                    if (available >= wage)
                    {
                        mgr.RemoveLockerCash(wage);
                        mgr.PaidForToday = true;
                        mgr.NoFundsTextSent = false;
                        if (!midRun) mgr.State = ManagerState.Idle;
                        OTCLog.Msg(OTCLog.Systems.Manager, $"{mgr.Id}: paid ${wage} wage from locker (remaining: ${available - wage:F0})");

                        // Case 3: Wages paid — check which Night Market items we can't afford
                        if (!mgr.NoNightMarketCashTextSent)
                        {
                            float remaining = mgr.GetLockerCash();
                            var unaffordable = GetUnaffordableNightMarketItems(nightMarketItems, remaining);
                            if (unaffordable.Count > 0)
                            {
                                mgr.NoNightMarketCashTextSent = true;
                                mgr.LockerCashAtWarning = remaining;
                                string itemList = FormatItemList(unaffordable);
                                string wageWarning = remaining < wage ? $" I also won't have enough for tomorrow's ${wage:F0} wage." : "";
                                if (remaining < wage) mgr.NoFundsTextSent = true;
                                mgr.SendTextMessage($"Boss, I paid my wages for today but I don't have enough to buy {itemList} from Oscar's store.{wageWarning}");
                            }
                        }
                    }
                    else
                    {
                        if (!midRun) mgr.State = ManagerState.NoFunds;
                        OTCLog.Msg(OTCLog.Systems.Manager, $"{mgr.Id}: insufficient funds in locker (has: ${available:F0}, need: ${wage})");
                        if (!mgr.NoFundsTextSent && !mgr.NoNightMarketCashTextSent)
                        {
                            mgr.NoFundsTextSent = true;
                            mgr.NoNightMarketCashTextSent = true;
                            mgr.LockerCashAtWarning = available;
                            string homeType = mgr.AssignedLocker?.HomeType?.ToLower() ?? "locker";

                            if (nightMarketItems.Count > 0)
                            {
                                string itemList = FormatItemList(nightMarketItems.ConvertAll(x => x.name));
                                if (available <= 0f)
                                    mgr.SendTextMessage($"Boss, there's no money in my {homeType}! I need ${wage:F0} for today's wage and I won't have enough to buy {itemList} from Oscar's store.");
                                else
                                    mgr.SendTextMessage($"Boss, my {homeType} only has ${available:F0} but I need ${wage:F0} for today's wage. I also won't have enough to buy {itemList} from Oscar's store.");
                            }
                            else
                            {
                                if (available <= 0f)
                                    mgr.SendTextMessage($"Boss, there's no money in my {homeType}! I need ${wage:F0} for today's wage.");
                                else
                                    mgr.SendTextMessage($"Boss, my {homeType} only has ${available:F0} but I need ${wage:F0} for today's wage. Drop some more cash in!");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Manager, $"OnDayPass error: {ex.Message}");
            }
        }

        /// <summary>
        /// Hires a manager at the specified business. Returns the created instance, or null on failure.
        /// </summary>
        public ManagerInstance HireManager(Business business)
        {
            if (business == null)
            {
                OTCLog.Warning(OTCLog.Systems.Manager, "HireManager: business is null");
                return null;
            }

            if (ManagerInstance.HasManager(business.PropertyCode))
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"HireManager: {business.PropertyCode} already has a manager");
                return null;
            }

            _managerIdCounter++;
            int seed = (int)(DateTime.Now.Ticks % int.MaxValue) ^ _managerIdCounter;
            string id = $"mgr_{business.PropertyCode}_{_managerIdCounter}";

            var instance = ManagerInstance.Create(id, seed, business);
            if (instance == null)
            {
                OTCLog.Error(OTCLog.Systems.Manager, $"HireManager: failed to create manager for {business.PropertyCode}");
                return null;
            }

            // Deduct signing fee from player
            try
            {
                float signingFee = Config.ManagerSigningFee.Value;
                ScheduleOne.Money.MoneyManager moneyManager =
                    NetworkSingleton<ScheduleOne.Money.MoneyManager>.Instance;

                if (moneyManager != null && moneyManager.cashBalance >= signingFee)
                {
                    moneyManager.ChangeCashBalance(-signingFee, true, true);
                    OTCLog.Msg(OTCLog.Systems.Manager, $"Deducted ${signingFee} signing fee for manager {id}");
                }
                else
                {
                    OTCLog.Warning(OTCLog.Systems.Manager, $"HireManager: insufficient cash for signing fee (${signingFee})");
                    instance.Despawn();
                    return null;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Manager, $"HireManager: signing fee deduction failed: {ex.Message}");
                instance.Despawn();
                return null;
            }

            instance.SendGreeting($"Hey boss! I'm your new manager at {business.PropertyName}. Use the clipboard to assign me a locker and I'll get to work.");

            // Grant the clipboard tool if not already acquired (same as vanilla Employee.RpcLogic___Initialize)
            try
            {
                var varDb = NetworkSingleton<ScheduleOne.Variables.VariableDatabase>.Instance;
                if (varDb != null && !varDb.GetValue<bool>("ClipboardAcquired"))
                    varDb.SetVariableValue("ClipboardAcquired", true.ToString());
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"HireManager: clipboard grant check failed: {ex.Message}");
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
                    OTCLog.Msg(OTCLog.Systems.Manager, $"HireManagerRemote: hiring at {propertyCode} (client request)");
                    HireManager(biz);
                    return;
                }
            }
            OTCLog.Warning(OTCLog.Systems.Manager, $"HireManagerRemote: business '{propertyCode}' not found in OwnedBusinesses");
        }

        /// <summary>
        /// Fires a manager. Walks them back to spawn, then despawns.
        /// </summary>
        public void FireManager(string managerId)
        {
            if (!ManagerInstance.Active.TryGetValue(managerId, out var instance))
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"FireManager: no manager with id {managerId}");
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

            OTCLog.Msg(OTCLog.Systems.Manager, $"Fired manager {managerId}");
        }

        /// <summary>
        /// Transfers a manager to a new business. Walks the NPC to the new location.
        /// </summary>
        public void TransferManager(string managerId, string targetPropertyCode)
        {
            if (!ManagerInstance.Active.TryGetValue(managerId, out var instance))
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"TransferManager: no manager with id {managerId}");
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
                OTCLog.Warning(OTCLog.Systems.Manager, $"TransferManager: target business '{targetPropertyCode}' not found");
                return;
            }

            // Check target doesn't already have a manager
            if (ManagerInstance.HasManager(targetPropertyCode))
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"TransferManager: {targetPropertyCode} already has a manager");
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

            // Walk to new business
            var location = ManagerLocations.GetLocation(targetPropertyCode);
            if (location != null)
            {
                WalkToNewBusiness(instance, location, targetBusiness);
            }
            else
            {
                // No registered location — just teleport
                OTCLog.Warning(OTCLog.Systems.Manager, $"No ManagerLocation for {targetPropertyCode}, teleporting manager");
                CompleteTransfer(instance, targetBusiness);
            }

            // Sync state to clients
            ConfigSyncData.Instance?.PublishManagerState();

            OTCLog.Msg(OTCLog.Systems.Manager, $"{managerId} transferring from {oldBizCode} to {targetPropertyCode}");
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

                // Ensure manager is on civilian NavMesh before cross-town walk
                instance.DistributionBehaviour?.EnsureCivilianNavMesh();
                instance.SupplyBehaviour?.EnsureCivilianNavMesh();

                // Track target location for EnsureMoving resume
                instance.TargetLocation = location;
                instance.ArrivedAtDestination = false;

                // Use _destCallback so EnsureMoving re-issues with the same transfer-aware callback
                instance._destCallback = (GameSystem.Action<NPCMovement.WalkResult>)
                    new Action<NPCMovement.WalkResult>(result =>
                    {
                        OTCLog.Msg(OTCLog.Systems.Manager, $"{instance.Id} transfer walk callback (result={result})");
                        if (result == NPCMovement.WalkResult.Success ||
                            result == NPCMovement.WalkResult.Partial)
                        {
                            CompleteTransfer(instance, targetBusiness);
                            try
                            {
                                instance.GameNpc?.Movement?.FaceDirection(location.DestRotation * Vector3.forward);
                            }
                            catch { }
                        }
                        // On Stopped (e.g. punch): leave TargetLocation set so EnsureMoving resumes walk
                    });

                // Also store on _transferCallback to prevent GC
                instance._transferCallback = instance._destCallback;

                instance.GameNpc.Movement.SetDestination(location.Destination, instance._destCallback, 3f, 1f);
                OTCLog.Msg(OTCLog.Systems.Manager, $"{instance.Id} walking to {targetBusiness.PropertyCode}");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Manager, $"WalkToNewBusiness failed: {ex.Message}");
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

                OTCLog.Msg(OTCLog.Systems.Manager, $"{instance.Id} transfer to {targetBusiness.PropertyCode} complete");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Manager, $"CompleteTransfer failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles remote transfer request from a client.
        /// </summary>
        public void TransferManagerRemote(string managerId, string targetPropertyCode)
        {
            OTCLog.Msg(OTCLog.Systems.Manager, $"TransferManagerRemote: {managerId} to {targetPropertyCode}");
            TransferManager(managerId, targetPropertyCode);
        }

        /// <summary>
        /// Handles remote fire request from a client.
        /// </summary>
        public void FireManagerRemote(string managerId)
        {
            OTCLog.Msg(OTCLog.Systems.Manager, $"FireManagerRemote: {managerId}");
            FireManager(managerId);
        }

        /// <summary>
        /// Finds the nearest owned business to the player.
        /// </summary>
        public static Business FindNearestOwnedBusiness()
        {
            try
            {
                var player = PlayerSingleton<ScheduleOne.PlayerScripts.PlayerMovement>.Instance;
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
                    if (mgr.State == ManagerState.Fired) continue;
                    if (!mgr.HasLocker) continue;

                    // Detect cash deposits — reset warning flags when locker cash exceeds warning level
                    if (mgr.NoNightMarketCashTextSent && mgr.GetLockerCash() > mgr.LockerCashAtWarning)
                    {
                        mgr.NoNightMarketCashTextSent = false;
                        mgr.NoFundsTextSent = false;
                        mgr.LockerCashAtWarning = -1f;
                        mgr.SupplyBehaviour?.ClearUnaffordableItems();
                    }

                    // Immediate wage payment for unpaid managers
                    if (mgr.PaidForToday) continue;

                    float wage = mgr.GetDailyWage();
                    float available = mgr.GetLockerCash();
                    if (available >= wage)
                    {
                        mgr.RemoveLockerCash(wage);
                        mgr.PaidForToday = true;
                        mgr.NoFundsTextSent = false;
                        mgr.NoNightMarketCashTextSent = false;
                        mgr.LockerCashAtWarning = -1f;
                        mgr.SupplyBehaviour?.ClearUnaffordableItems();
                        mgr.State = ManagerState.Idle;
                        OTCLog.Msg(OTCLog.Systems.Manager, $"{mgr.Id}: immediate wage payment ${wage} (remaining: ${available - wage:F0})");
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Manager, $"CheckImmediateWages error: {ex.Message}");
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

        /// <summary>
        /// Returns display names of Night Market items the manager can't afford even 1 unit of.
        /// </summary>
        private static List<string> GetUnaffordableNightMarketItems(
            List<(string id, string name)> nightMarketItems, float availableCash)
        {
            var unaffordable = new List<string>();
            foreach (var (id, name) in nightMarketItems)
            {
                float unitPrice = ManagerSupplyBehaviour.GetNightMarketUnitPrice(id);
                if (availableCash < unitPrice)
                    unaffordable.Add(name);
            }
            return unaffordable;
        }

        /// <summary>
        /// Formats a list of item names as "A, B, and C" for text messages.
        /// </summary>
        private static string FormatItemList(List<string> names)
        {
            if (names.Count == 1) return names[0];
            if (names.Count == 2) return $"{names[0]} and {names[1]}";
            return string.Join(", ", names.GetRange(0, names.Count - 1)) + $", and {names[names.Count - 1]}";
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
                OTCLog.Warning(OTCLog.Systems.Manager, "[Debug] No owned businesses found");
                return;
            }

            if (ManagerInstance.HasManager(business.PropertyCode))
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"[Debug] {business.PropertyCode} already has a manager");
                return;
            }

            // Bypass signing fee for debug
            Instance._managerIdCounter++;
            int seed = (int)(DateTime.Now.Ticks % int.MaxValue) ^ Instance._managerIdCounter;
            string id = $"mgr_{business.PropertyCode}_{Instance._managerIdCounter}";

            var instance = ManagerInstance.Create(id, seed, business);
            if (instance != null)
            {
                OTCLog.Msg(OTCLog.Systems.Manager, $"Spawned manager {id} at {business.PropertyCode}");
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
