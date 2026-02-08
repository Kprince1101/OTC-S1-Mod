using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Property;
using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Data wrapper for a single Manager NPC assigned to a player-owned business.
    /// Tracks NPC reference, assigned property, cash pool, and lifecycle state.
    /// </summary>
    public class ManagerInstance
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("ManagerInstance");

        /// <summary>
        /// All active manager instances, keyed by ID.
        /// </summary>
        public static Dictionary<string, ManagerInstance> Active { get; } = new Dictionary<string, ManagerInstance>();

        /// <summary>
        /// Business property codes with managers, synced from host to client.
        /// Used by HasManager() on clients where Active may not be populated yet.
        /// </summary>
        internal static HashSet<string> SyncedManagerBusinesses { get; } = new HashSet<string>();

        // Identity
        public string Id { get; }
        public int SpawnSeed { get; }

        // Game references
        public NPC GameNpc { get; private set; }
        public Business AssignedBusiness { get; }
        public string BusinessPropertyCode { get; }

        // Locker — EmployeeHome used for cash storage (player deposits cash here)
        public EmployeeHome AssignedLocker { get; private set; }

        // Lifecycle state
        public ManagerState State { get; set; } = ManagerState.Idle;
        public bool PaidForToday { get; set; }
        public bool IsAdopted { get; private set; }
        public int NetworkObjectId { get; set; }
        public bool GreetingSent { get; set; }
        public bool NoLockerTextSent { get; set; }
        public bool NoFundsTextSent { get; set; }

        public bool IsValid => GameNpc != null && GameNpc.gameObject != null;
        public bool HasLocker => AssignedLocker != null && AssignedLocker.Storage != null;

        public Vector3? Position
        {
            get
            {
                try { return GameNpc?.transform?.position; }
                catch { return null; }
            }
        }

        private ManagerInstance(string id, int seed, Business business)
        {
            Id = id;
            SpawnSeed = seed;
            AssignedBusiness = business;
            BusinessPropertyCode = business.PropertyCode;
        }

        /// <summary>
        /// Creates a new Manager instance and spawns the NPC at the business.
        /// </summary>
        public static ManagerInstance Create(string id, int seed, Business business)
        {
            if (Active.ContainsKey(id))
            {
                Logger.Warning($"Manager {id} already exists, returning existing instance");
                return Active[id];
            }

            // Check if this business already has a manager
            foreach (var existing in Active.Values)
            {
                if (existing.BusinessPropertyCode == business.PropertyCode)
                {
                    Logger.Warning($"Business {business.PropertyCode} already has a manager ({existing.Id})");
                    return null;
                }
            }

            // Use ManagerLocations if available, otherwise fall back to business spawn point
            Logger.Msg($"Looking up ManagerLocation for PropertyCode=\"{business.PropertyCode}\"");
            var location = ManagerLocations.GetLocation(business.PropertyCode);

            Vector3 spawnPos;
            Quaternion spawnRot;

            if (location != null)
            {
                spawnPos = location.SpawnPosition;
                spawnRot = location.SpawnRotation;
            }
            else
            {
                spawnPos = business.NPCSpawnPoint != null
                    ? business.NPCSpawnPoint.position
                    : business.transform.position;
                spawnRot = business.NPCSpawnPoint != null
                    ? business.NPCSpawnPoint.rotation
                    : business.transform.rotation;
                Logger.Warning($"No ManagerLocation for {business.PropertyCode}, using business spawn point");
            }

            var npc = ManagerSpawner.Spawn(id, seed, spawnPos, spawnRot);
            if (npc == null)
            {
                Logger.Error($"Failed to spawn manager NPC for {id}");
                return null;
            }

            var instance = new ManagerInstance(id, seed, business)
            {
                GameNpc = npc
            };

            // Capture FishNet ObjectId for client-side adoption
            try { instance.NetworkObjectId = npc.NetworkObject.ObjectId; }
            catch { Logger.Warning($"Could not get NetworkObjectId for {id}"); }

            Active[id] = instance;

            // Walk to destination if we have a registered location
            if (location != null)
            {
                instance.WalkToDestination(location);
            }

            Logger.Msg($"Created manager {id} at business {business.PropertyCode} (NetObjId={instance.NetworkObjectId})");
            return instance;
        }

        /// <summary>
        /// Adopts an existing FishNet-replicated NPC as a Manager (client path).
        /// </summary>
        public static ManagerInstance Adopt(string id, int seed, Business business, NPC existingNpc)
        {
            if (Active.ContainsKey(id))
            {
                Logger.Warning($"Manager {id} already exists, returning existing instance");
                return Active[id];
            }

            var (firstName, lastName) = ManagerSpawner.GetManagerName(seed);
            existingNpc.ID = id;
            existingNpc.FirstName = firstName;
            existingNpc.LastName = lastName;

            ManagerSpawner.ApplyAppearance(existingNpc, seed);
            ManagerSpawner.InitializeMessaging(existingNpc);
            ManagerSpawner.EnsureVoiceDatabase(existingNpc);

            var instance = new ManagerInstance(id, seed, business)
            {
                GameNpc = existingNpc,
                IsAdopted = true
            };

            Active[id] = instance;
            Logger.Msg($"Adopted FishNet NPC for manager {id} ({firstName} {lastName}) at {business.PropertyCode}");
            return instance;
        }

        // Hold reference to IL2CPP callback to prevent GC collection
        private Il2CppSystem.Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult> _destCallback;

        /// <summary>
        /// Walks the Manager NPC from its spawn point to the business destination.
        /// </summary>
        public void WalkToDestination(ManagerLocations.BusinessLocation location)
        {
            try
            {
                if (GameNpc?.Movement == null) return;

                _destCallback = (Il2CppSystem.Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult>)
                    new Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult>(result =>
                    {
                        Logger.Msg($"Manager {Id} arrived at destination (result={result})");
                        if (result == Il2CppScheduleOne.NPCs.NPCMovement.WalkResult.Success ||
                            result == Il2CppScheduleOne.NPCs.NPCMovement.WalkResult.Partial)
                        {
                            FaceDirection(location.DestRotation);
                        }
                    });

                GameNpc.Movement.SetDestination(location.Destination, _destCallback, 3f, 1f);
                Logger.Msg($"Manager {Id} walking to destination: {location.Destination}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"WalkToDestination failed for manager {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Faces the NPC in a specific direction.
        /// </summary>
        private void FaceDirection(Quaternion rotation)
        {
            try
            {
                GameNpc?.Movement?.FaceDirection(rotation * Vector3.forward);
            }
            catch { }
        }

        /// <summary>
        /// Assigns a locker (EmployeeHome) to this Manager and updates its display.
        /// </summary>
        public void AssignLocker(EmployeeHome locker)
        {
            if (locker == null) return;

            AssignedLocker = locker;
            NoLockerTextSent = false;

            try
            {
                // Update storage display text (we can't call SetAssignedEmployee since we're not an Employee)
                float dailyWage = Config.ManagerDailyWage.Value;
                string wageStr = $"<color=#54E717>${dailyWage:F0}</color>";
                string homeType = locker.HomeType ?? "Briefcase";
                locker.Storage.StorageEntityName = $"{GameNpc?.FirstName}'s {homeType}";
                locker.Storage.StorageEntitySubtitle =
                    $"Manager will draw a daily wage of {wageStr} from this {homeType.ToLower()}";

                Logger.Msg($"Manager {Id}: assigned locker at {locker.transform.position}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Manager {Id}: failed to update locker display: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the total cash in the assigned locker.
        /// </summary>
        public float GetLockerCash()
        {
            try
            {
                return HasLocker ? AssignedLocker.GetCashSum() : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// Removes cash from the assigned locker. Returns true if enough was available.
        /// </summary>
        public bool RemoveLockerCash(float amount)
        {
            if (!HasLocker) return false;

            try
            {
                if (AssignedLocker.GetCashSum() < amount)
                    return false;

                AssignedLocker.RemoveCash(amount);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Manager {Id}: RemoveLockerCash failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends a text message from this Manager to the player.
        /// Uses network=false to avoid FishNet's RunLocally double-delivery on the host.
        /// Each player sends their own local text (host from HireManager, client from TryAdopt).
        /// </summary>
        public void SendTextMessage(string message)
        {
            if (GameNpc == null)
            {
                Logger.Warning($"Manager {Id}: cannot send text - GameNpc is null");
                return;
            }

            try
            {
                var conv = GameNpc.MSGConversation;
                if (conv == null)
                {
                    Logger.Warning($"Manager {Id}: MSGConversation is null, cannot send text");
                    return;
                }
                var msg = new Il2CppScheduleOne.Messaging.Message(
                    message,
                    Il2CppScheduleOne.Messaging.Message.ESenderType.Other,
                    true,
                    UnityEngine.Random.Range(int.MinValue, int.MaxValue));
                conv.SendMessage(msg, true, false);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to send text from manager {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a greeting text exactly once (prevents duplicate greetings from host/client paths).
        /// </summary>
        public void SendGreeting(string message)
        {
            if (GreetingSent) return;
            GreetingSent = true;
            SendTextMessage(message);
        }

        /// <summary>
        /// Despawns and cleans up this Manager.
        /// </summary>
        public void Despawn()
        {
            Logger.Msg($"Despawning manager {Id}");
            Active.Remove(Id);

            // Release locker — reset storage display
            if (AssignedLocker != null)
            {
                try
                {
                    AssignedLocker.Storage.StorageEntityName = AssignedLocker.HomeType;
                    AssignedLocker.Storage.StorageEntitySubtitle = string.Empty;
                }
                catch { }
                AssignedLocker = null;
            }

            if (GameNpc != null)
            {
                if (IsAdopted)
                {
                    Logger.Msg($"Manager {Id}: releasing adopted FishNet NPC");
                }
                else
                {
                    ManagerSpawner.Despawn(GameNpc);
                }
                GameNpc = null;
            }
        }

        /// <summary>
        /// Checks whether a business already has a manager assigned.
        /// </summary>
        public static bool HasManager(string propertyCode)
        {
            foreach (var mgr in Active.Values)
            {
                if (mgr.BusinessPropertyCode == propertyCode)
                    return true;
            }
            return SyncedManagerBusinesses.Contains(propertyCode);
        }

        /// <summary>
        /// Gets the manager for a specific business, if any.
        /// </summary>
        public static ManagerInstance GetForBusiness(string propertyCode)
        {
            foreach (var mgr in Active.Values)
            {
                if (mgr.BusinessPropertyCode == propertyCode)
                    return mgr;
            }
            return null;
        }

        /// <summary>
        /// Gets comma-separated list of business property codes with active managers (for sync).
        /// </summary>
        public static string GetManagedBusinessCodes()
        {
            var codes = new List<string>();
            foreach (var mgr in Active.Values)
                codes.Add(mgr.BusinessPropertyCode);
            return string.Join(",", codes);
        }

        /// <summary>
        /// Serializes full manager state for client adoption.
        /// Format: id:seed:biz:netObjId;id:seed:biz:netObjId
        /// </summary>
        public static string SerializeManagerState()
        {
            if (Active.Count == 0) return "";
            var parts = new List<string>();
            foreach (var mgr in Active.Values)
                parts.Add($"{mgr.Id}:{mgr.SpawnSeed}:{mgr.BusinessPropertyCode}:{mgr.NetworkObjectId}");
            string result = string.Join(";", parts);
            Logger.Msg($"SerializeManagerState: {Active.Count} managers → '{result}'");
            return result;
        }

        // Pending adoptions: managers whose FishNet NPC hasn't replicated yet
        private static readonly Dictionary<string, PendingAdoption> _pendingAdoptions = new();
        private static float _lastAdoptionRetry;

        private class PendingAdoption
        {
            public string Id;
            public int Seed;
            public string BizCode;
            public int NetObjId;
            public float CreatedTime;
        }

        /// <summary>
        /// Applies manager state from host on client. Finds FishNet NPCs by ObjectId
        /// and adopts them (applies appearance, name, messaging).
        /// Queues pending adoptions for NPCs that haven't replicated yet.
        /// Does NOT modify SyncedManagerBusinesses — that's handled by OnManagerStateChanged in ConfigSyncData.
        /// </summary>
        public static void ApplyManagerState(string stateString)
        {
            if (string.IsNullOrEmpty(stateString))
            {
                Logger.Msg("ApplyManagerState: stateString is empty, skipping");
                return;
            }

            Logger.Msg($"ApplyManagerState: processing '{stateString}' (Active={Active.Count}, Pending={_pendingAdoptions.Count})");

            var entries = stateString.Split(';');
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var parts = entry.Split(':');
                if (parts.Length < 4)
                {
                    Logger.Warning($"ApplyManagerState: skipping malformed entry '{entry}' (parts={parts.Length})");
                    continue;
                }

                string id = parts[0];
                if (!int.TryParse(parts[1], out int seed))
                {
                    Logger.Warning($"ApplyManagerState: bad seed in entry '{entry}'");
                    continue;
                }
                string bizCode = parts[2];
                if (!int.TryParse(parts[3], out int netObjId))
                {
                    Logger.Warning($"ApplyManagerState: bad netObjId in entry '{entry}'");
                    continue;
                }

                // Already adopted or pending
                if (Active.ContainsKey(id))
                {
                    Logger.Msg($"ApplyManagerState: {id} already in Active, skipping");
                    continue;
                }
                if (_pendingAdoptions.ContainsKey(id))
                {
                    Logger.Msg($"ApplyManagerState: {id} already pending, skipping");
                    continue;
                }

                // Find FishNet NPC by ObjectId
                NPC npc = FindNetworkNpc(netObjId);
                if (npc == null)
                {
                    // Queue for retry — NPC likely hasn't replicated yet
                    _pendingAdoptions[id] = new PendingAdoption
                    {
                        Id = id, Seed = seed, BizCode = bizCode,
                        NetObjId = netObjId, CreatedTime = UnityEngine.Time.time
                    };
                    Logger.Msg($"ApplyManagerState: NPC ObjectId {netObjId} not found yet, queued adoption for {id}");
                    continue;
                }

                TryAdopt(id, seed, bizCode, netObjId, npc);
            }
        }

        private static void TryAdopt(string id, int seed, string bizCode, int netObjId, NPC npc)
        {
            Business business = null;
            foreach (var biz in Business.OwnedBusinesses)
            {
                if (biz != null && string.Equals(biz.PropertyCode, bizCode, StringComparison.OrdinalIgnoreCase))
                {
                    business = biz;
                    break;
                }
            }

            if (business == null)
            {
                Logger.Warning($"TryAdopt: business '{bizCode}' not found for manager {id}");
                return;
            }

            var instance = Adopt(id, seed, business, npc);
            if (instance != null)
            {
                instance.NetworkObjectId = netObjId;

                // Find and assign locker for display text (mirrors host auto-assignment)
                var locker = ManagerController.FindNearestAvailableLocker(business);
                if (locker != null)
                    instance.AssignLocker(locker);

                // Send greeting text locally (host sends its own during HireManager)
                string lockerType = locker?.HomeType?.ToLower() ?? "locker";
                instance.SendGreeting(locker != null
                    ? $"Hey boss! I'm your new manager at {business.PropertyName}. Drop some cash in my {lockerType} and I'll get to work."
                    : $"Hey boss! I'm at {business.PropertyName}, but I don't have a locker assigned yet. Place one nearby and I'll get to work.");

                Logger.Msg($"Adopted manager {id} at {bizCode} (NetObjId={netObjId}, locker={locker != null})");
            }
        }

        /// <summary>
        /// Retries pending adoptions for managers whose FishNet NPC hadn't arrived yet.
        /// Called periodically from Core.OnLateUpdate.
        /// </summary>
        public static void RetryPendingAdoptions()
        {
            if (_pendingAdoptions.Count == 0) return;

            float now = UnityEngine.Time.time;
            if (now - _lastAdoptionRetry < 0.5f) return;
            _lastAdoptionRetry = now;

            var completed = new List<string>();
            foreach (var kv in _pendingAdoptions)
            {
                var pa = kv.Value;

                // Give up after 15 seconds
                if (now - pa.CreatedTime > 15f)
                {
                    Logger.Warning($"Giving up adoption for manager {pa.Id} (timeout)");
                    completed.Add(kv.Key);
                    continue;
                }

                NPC npc = FindNetworkNpc(pa.NetObjId);
                if (npc != null)
                {
                    TryAdopt(pa.Id, pa.Seed, pa.BizCode, pa.NetObjId, npc);
                    completed.Add(kv.Key);
                }
            }

            foreach (var id in completed)
                _pendingAdoptions.Remove(id);
        }

        /// <summary>
        /// Finds a FishNet-replicated NPC by its NetworkObject.ObjectId.
        /// </summary>
        private static NPC FindNetworkNpc(int objectId)
        {
            if (objectId <= 0) return null;

            var registry = Il2CppScheduleOne.NPCs.NPCManager.NPCRegistry;
            if (registry == null) return null;

            // Skip NPCs already tracked as managers
            var tracked = new HashSet<int>();
            foreach (var mgr in Active.Values)
            {
                if (mgr.GameNpc != null)
                    tracked.Add(mgr.GameNpc.GetInstanceID());
            }

            for (int i = 0; i < registry.Count; i++)
            {
                var npc = registry[i];
                if (npc == null || npc.gameObject == null) continue;
                if (tracked.Contains(npc.GetInstanceID())) continue;

                try
                {
                    var netObj = npc.gameObject.GetComponent<Il2CppFishNet.Object.NetworkObject>();
                    if (netObj != null && netObj.ObjectId == objectId)
                        return npc;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Cleans up all active managers.
        /// </summary>
        public static void CleanupAll()
        {
            var ids = new List<string>(Active.Keys);
            foreach (var id in ids)
            {
                try
                {
                    if (Active.TryGetValue(id, out var instance))
                        instance.Despawn();
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to cleanup manager {id}: {ex.Message}");
                }
            }
            Active.Clear();
        }
    }

    public enum ManagerState
    {
        Idle,
        SupplyRun,
        DistributionRun,
        Fired,
        NoFunds
    }
}
