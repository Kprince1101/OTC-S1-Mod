using MelonLoader;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.GameTime;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Money;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.Property;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Map;
using ScheduleOne.Money;
using ScheduleOne.NPCs;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.Property;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Data wrapper for a single Manager NPC assigned to a player-owned business.
    /// Tracks NPC reference, assigned property, cash pool, and lifecycle state.
    /// </summary>
    public class ManagerInstance
    {
        /// <summary>
        /// All active manager instances, keyed by ID.
        /// </summary>
        public static Dictionary<string, ManagerInstance> Active { get; } = new Dictionary<string, ManagerInstance>();

        /// <summary>
        /// Business property codes with managers, synced from host to client.
        /// Used by HasManager() on clients where Active may not be populated yet.
        /// </summary>
        internal static HashSet<string> SyncedManagerBusinesses { get; } = new HashSet<string>();

        /// <summary>
        /// Finds the ManagerInstance wrapping a given game NPC, if any. Used by Harmony
        /// patches that only have the NPC/NPCInventory to work from (e.g. nightly clear,
        /// pickpocket checks) and need to know whether it's a manager-controlled NPC.
        /// Compares by NPC.ID rather than reference equality since Il2Cpp wrapper
        /// instances for the same underlying object aren't guaranteed reference-equal.
        /// </summary>
        public static ManagerInstance GetByNpc(NPC npc)
        {
            if (npc == null) return null;
            string id;
            try { id = npc.ID; } catch { return null; }
            if (string.IsNullOrEmpty(id)) return null;

            foreach (var m in Active.Values)
            {
                if (m.GameNpc != null && m.GameNpc.ID == id)
                    return m;
            }
            return null;
        }

        // Per-manager SyncVar slot tracking
        private static readonly Dictionary<string, int> _managerSlot = new();  // managerId → slot index
        private static readonly string[] _slotManagerId = new string[NetworkSyncBridge.ManagerSlotCount]; // slot → managerId

        // Identity
        public string Id { get; }
        public int SpawnSeed { get; }

        // Game references
        public NPC GameNpc { get; private set; }
        public Business AssignedBusiness { get; internal set; }
        public string BusinessPropertyCode { get; internal set; }

        // Configuration (supply storage + distribution routes)
        public ManagerConfiguration Configuration { get; } = new ManagerConfiguration();

        // Supply run behaviour (host-only state machine)
        public ManagerSupplyBehaviour SupplyBehaviour { get; private set; }

        // Distribution run behaviour (host-only state machine)
        public ManagerDistributionBehaviour DistributionBehaviour { get; private set; }

        // Locker — EmployeeHome used for cash storage (player deposits cash here)
        public EmployeeHome AssignedLocker { get; private set; }

        // Lifecycle state — setters flag for network publish so the host
        // pushes changes to clients automatically on the next frame.
        private ManagerState _state = ManagerState.Idle;
        public ManagerState State
        {
            get => _state;
            set
            {
                if (_state != value) { _state = value; StatePublishNeeded = true; }
            }
        }

        private bool _paidForToday;
        public bool PaidForToday
        {
            get => _paidForToday;
            set
            {
                if (_paidForToday != value) { _paidForToday = value; StatePublishNeeded = true; }
            }
        }

        /// <summary>
        /// Set by State/PaidForToday setters when a change occurs.
        /// Checked in Core.OnLateUpdate to trigger per-slot SyncVar publish.
        /// </summary>
        public static bool StatePublishNeeded { get; set; }

        // Sub-phase code synced from host for client-side display (0=none, 1=depositing, 2=purchasing/picking up)
        internal int _subPhaseCode;
        public bool IsAdopted { get; private set; }
        public int NetworkObjectId { get; set; }
        public bool GreetingSent { get; set; }
        public bool NoLockerTextSent { get; set; }
        public bool NoFundsTextSent { get; set; }
        public bool NoNightMarketCashTextSent { get; set; }

        /// <summary>
        /// Queued text message for client delivery via SyncVar.
        /// Set by SendTextMessage on host, cleared after serialization.
        /// </summary>
        public string PendingClientMessage { get; set; }

        /// <summary>
        /// True when any manager has a PendingClientMessage waiting to be published.
        /// Checked in the lifecycle tick to trigger PublishManagerState.
        /// </summary>
        public static bool HasPendingMessages { get; set; }

        /// <summary>
        /// Locker cash balance when the NM warning was sent.
        /// Flag only resets when locker cash rises above this (player deposited cash).
        /// </summary>
        public float LockerCashAtWarning { get; set; } = -1f;

        // Walk resume state — tracks where the manager should be walking
        internal ManagerLocations.BusinessLocation TargetLocation { get; set; }
        public bool ArrivedAtDestination { get; set; }
        private float _lastEnsureMovingLog;
        private int _consecutiveWalkFailures;
        private const int WALK_AVOIDANCE_PRIORITY = 5;
        private const int IDLE_AVOIDANCE_PRIORITY = 50;

        // Job rotation: supply → route0 → route1 → route2 → supply → ...
        // Step 0 = supply, 1-3 = distribution routes 0-2
        private int _jobRotationStep = 0;

        // Map marker — always-on POI like dealers have
        public NPCPoI MapPoI { get; private set; }

        // True while the player is viewing this manager's inventory via StorageMenu.
        // Prevents walk resume and new job starts during the interaction.
        public bool IsPlayerInteracting { get; set; }

        // Mugshot lifecycle — lets locker assignment and other systems react once ready
        public bool IsMugshotReady { get; private set; }
        public event Action OnMugshotReady;

        // In-memory log ring buffer for debug page
        private const int LOG_BUFFER_CAPACITY = 200;
        private readonly List<string> _logBuffer = new(LOG_BUFFER_CAPACITY);
        public IReadOnlyList<string> LogBuffer => _logBuffer;

        public void Log(string message)
        {
            OTCLog.Msg(OTCLog.Systems.Manager, $"{Id}: {message}");
            AppendToBuffer($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        public void LogWarning(string message)
        {
            OTCLog.Warning(OTCLog.Systems.Manager, $"{Id}: {message}");
            AppendToBuffer($"[{DateTime.Now:HH:mm:ss}] [!] {message}");
        }

        private void AppendToBuffer(string entry)
        {
            if (_logBuffer.Count >= LOG_BUFFER_CAPACITY)
                _logBuffer.RemoveAt(0);
            _logBuffer.Add(entry);
        }

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

        /// <summary>
        /// Builds a human-readable summary of the NPC's current inventory (e.g. "20x Baggy, 15x OG Kush Jar, $500 cash").
        /// Returns "empty" if the NPC has nothing.
        /// </summary>
        public string GetInventorySummary()
        {
            try
            {
                var inventory = GameNpc?.GetComponent<ScheduleOne.NPCs.NPCInventory>();
                if (inventory?.ItemSlots == null) return "empty";

                var counts = new Dictionary<string, int>();
                float cashTotal = 0f;

                for (int i = 0; i < inventory.ItemSlots.Count; i++)
                {
                    var item = inventory.ItemSlots[i]?.ItemInstance;
                    if (item == null) continue;

                    var cash = item.TryCast<ScheduleOne.ItemFramework.CashInstance>();
                    if (cash != null)
                    {
                        cashTotal += cash.Balance;
                        continue;
                    }

                    string name = item.Name ?? item.ID ?? "Unknown";
                    int qty = inventory.ItemSlots[i].Quantity;
                    if (counts.ContainsKey(name))
                        counts[name] += qty;
                    else
                        counts[name] = qty;
                }

                var parts = new List<string>();
                foreach (var kv in counts)
                    parts.Add($"{kv.Value}x {kv.Key}");
                if (cashTotal > 0f)
                    parts.Add($"${cashTotal:F0} cash");

                return parts.Count > 0 ? string.Join(", ", parts) : "empty";
            }
            catch { return "unknown"; }
        }

        // ==================================================================
        // Upgrade system — speed tiers & inventory capacity
        // ==================================================================

        /// <summary>
        /// Calculates this manager's daily wage based on upgrade tiers.
        /// </summary>
        public float GetDailyWage()
            => ManagerUpgrades.CalculateDailyWage(Configuration.SpeedTier, Configuration.ExtraInventorySlots);

        /// <summary>
        /// Applies the current speed tier to the NPC's SpeedController.
        /// Removes and re-adds the "manager" speed control with the tier's value.
        /// Safe to call at any time (spawn, adopt, upgrade purchase, load).
        /// </summary>
        public void ApplySpeedUpgrade()
        {
            try
            {
                var speedCtrl = GameNpc?.Movement?.SpeedController;
                if (speedCtrl == null) return;

                speedCtrl.RemoveSpeedControl("manager");
                float speedVal = ManagerUpgrades.GetSpeedControlValue(Configuration.SpeedTier);
                speedCtrl.AddSpeedControl(
                    new ScheduleOne.NPCs.NPCSpeedController.SpeedControl("manager", 1, speedVal));

                if (Config.ManagerVerboseLogging.Value)
                    Log($"ApplySpeedUpgrade: tier={Configuration.SpeedTier} speed={speedVal:F3}");
            }
            catch (Exception ex)
            {
                LogWarning($"ApplySpeedUpgrade failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures the NPC's inventory has enough slots for the current upgrade level.
        /// Creates new ItemSlot instances if the current count is below the target.
        /// </summary>
        public void ApplyInventoryCapacity()
        {
            try
            {
                var inventory = GameNpc?.GetComponent<ScheduleOne.NPCs.NPCInventory>();
                if (inventory == null) return;

                int target = ManagerUpgrades.GetTotalSlots(Configuration.ExtraInventorySlots);
                var slots = inventory.ItemSlots ?? new Il2CppSystem.Collections.Generic.List<ScheduleOne.ItemFramework.ItemSlot>();
                int current = slots.Count;

                if (current >= target) return;

                for (int i = current; i < target; i++)
                {
                    var slot = new ScheduleOne.ItemFramework.ItemSlot();
                    slot.SetSlotOwner(inventory.Cast<ScheduleOne.ItemFramework.IItemSlotOwner>());
                    slots.Add(slot);
                }

                inventory.ItemSlots = slots;

                if (Config.ManagerVerboseLogging.Value)
                    Log($"ApplyInventoryCapacity: {current}→{target} slots");
            }
            catch (Exception ex)
            {
                LogWarning($"ApplyInventoryCapacity failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Attempts to purchase the next speed tier using the player's bank balance.
        /// Returns true if the upgrade was purchased successfully.
        /// Host-only: bank transactions are server-side RPCs.
        /// </summary>
        public bool TryPurchaseSpeedUpgrade()
        {
            if (ManagerUpgrades.IsSpeedMaxed(Configuration.SpeedTier))
            {
                Log("TryPurchaseSpeedUpgrade: already maxed");
                return false;
            }

            float cost = ManagerUpgrades.GetNextSpeedBuyIn(Configuration.SpeedTier);
            var moneyMgr = NetworkSingleton<MoneyManager>.Instance;
            if (moneyMgr == null || moneyMgr.onlineBalance < cost)
            {
                Log($"TryPurchaseSpeedUpgrade: insufficient funds (need ${cost:F0}, have ${moneyMgr?.onlineBalance ?? 0:F0})");
                return false;
            }

            moneyMgr.CreateOnlineTransaction("Manager Speed Upgrade", -cost, 1f, "OTC Managers");
            Configuration.SpeedTier++;
            ApplySpeedUpgrade();
            StatePublishNeeded = true;

            Log($"Purchased speed tier {Configuration.SpeedTier} ({ManagerUpgrades.GetSpeedLabel(Configuration.SpeedTier)}) for ${cost:F0}");
            return true;
        }

        /// <summary>
        /// Attempts to purchase the next inventory slot using the player's bank balance.
        /// Returns true if the upgrade was purchased successfully.
        /// Host-only: bank transactions are server-side RPCs.
        /// </summary>
        public bool TryPurchaseInventoryUpgrade()
        {
            if (ManagerUpgrades.IsInventoryMaxed(Configuration.ExtraInventorySlots))
            {
                Log("TryPurchaseInventoryUpgrade: already maxed");
                return false;
            }

            float cost = ManagerUpgrades.GetNextSlotBuyIn(Configuration.ExtraInventorySlots);
            var moneyMgr = NetworkSingleton<MoneyManager>.Instance;
            if (moneyMgr == null || moneyMgr.onlineBalance < cost)
            {
                Log($"TryPurchaseInventoryUpgrade: insufficient funds (need ${cost:F0}, have ${moneyMgr?.onlineBalance ?? 0:F0})");
                return false;
            }

            moneyMgr.CreateOnlineTransaction("Manager Inventory Upgrade", -cost, 1f, "OTC Managers");
            Configuration.ExtraInventorySlots++;
            ApplyInventoryCapacity();
            StatePublishNeeded = true;

            Log($"Purchased inventory slot {ManagerUpgrades.GetTotalSlots(Configuration.ExtraInventorySlots)} for ${cost:F0}");
            return true;
        }

        /// <summary>
        /// Disables the vanilla IdleBehaviour during active runs so it doesn't
        /// overwrite our walk destination after dialogue ends.
        /// </summary>
        public void DisableIdleBehaviour()
        {
            try
            {
                var idle = GameNpc?.GetComponent<IdleBehaviour>();
                if (idle != null && idle.Enabled)
                    idle.Disable();
            }
            catch { }
        }

        /// <summary>
        /// Re-enables the vanilla IdleBehaviour after a run completes so the NPC
        /// returns to its idle point naturally.
        /// </summary>
        public void EnableIdleBehaviour()
        {
            try
            {
                var idle = GameNpc?.GetComponent<IdleBehaviour>();
                if (idle != null && !idle.Enabled)
                    idle.Enable();
            }
            catch { }
        }

        /// <summary>
        /// Immediately checks for and starts the next available job.
        /// Resumes pending deliveries first, then rotates through a fixed cycle:
        /// supply → route 0 → route 1 → route 2 → supply → ...
        /// Skips steps with no work and wraps around once per call.
        /// </summary>
        public bool TryStartNextJob()
        {
            if (State != ManagerState.Idle) return false;
            if (!PaidForToday) return false;

            // Game time freezes at 4 AM — don't start new jobs, let active ones finish
            if (TimeManager.CurrentTime == 400) return false;

            // Resume pending deliveries first (items from a previous session with recorded destinations)
            if (DistributionBehaviour?.TryResumeDeliveries() ?? false) return true;

            // Rotation: supply(0) → route0(1) → route1(2) → route2(3) → supply(0) → ...
            // Try each slot in order; skip slots that have no work; wrap around once.
            int slots = 1 + Configuration.Routes.Length; // 1 supply + 3 routes = 4
            for (int attempt = 0; attempt < slots; attempt++)
            {
                int step = _jobRotationStep % slots;
                _jobRotationStep = (_jobRotationStep + 1) % slots;

                if (step == 0)
                {
                    // Supply run
                    if (SupplyBehaviour?.TryStartSupplyRun() ?? false) return true;
                }
                else
                {
                    // Distribution route (step 1 = route index 0, etc.)
                    int routeIndex = step - 1;
                    if (DistributionBehaviour?.TryStartSingleRoute(routeIndex) ?? false) return true;
                }
            }

            return false;
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
                OTCLog.Warning(OTCLog.Systems.Manager, $"{id} already exists, returning existing instance");
                return Active[id];
            }

            // Check if this business already has an active manager
            foreach (var existing in Active.Values)
            {
                if (existing.State == ManagerState.Fired) continue;
                if (existing.BusinessPropertyCode == business.PropertyCode)
                {
                    OTCLog.Warning(OTCLog.Systems.Manager, $"Business {business.PropertyCode} already has a manager ({existing.Id})");
                    return null;
                }
            }

            // Use ManagerLocations if available, otherwise fall back to business spawn point
            if (Config.ManagerVerboseLogging.Value)
                OTCLog.Msg(OTCLog.Systems.Manager, $"Looking up ManagerLocation for PropertyCode=\"{business.PropertyCode}\"");
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
                OTCLog.Warning(OTCLog.Systems.Manager, $"No ManagerLocation for {business.PropertyCode}, using business spawn point");
            }

            var (npc, avatarSettings) = ManagerSpawner.Spawn(id, seed, spawnPos, spawnRot);
            if (npc == null)
            {
                OTCLog.Error(OTCLog.Systems.Manager, $"Failed to spawn manager NPC for {id}");
                return null;
            }

            var instance = new ManagerInstance(id, seed, business)
            {
                GameNpc = npc
            };
            instance.SupplyBehaviour = new ManagerSupplyBehaviour(instance);
            instance.DistributionBehaviour = new ManagerDistributionBehaviour(instance);

            // Capture FishNet ObjectId for client-side adoption
            try { instance.NetworkObjectId = npc.NetworkObject.ObjectId; }
            catch { OTCLog.Warning(OTCLog.Systems.Manager, $"Could not get NetworkObjectId for {id}"); }

            Active[id] = instance;

            // Set up dialogue choices (fire/transfer)
            ManagerSpawner.SetupDialogueChoices(instance);

            // Generate mugshot using the exact settings from ApplyAppearance
            instance.GenerateMugshot(avatarSettings);

            // Always-on map marker (like dealers)
            instance.SetupMapMarker();

            // Apply upgrade tiers (base tier 0 on fresh hire, restored tiers on load)
            instance.ApplySpeedUpgrade();
            instance.ApplyInventoryCapacity();

            // Walk to destination if we have a registered location
            if (location != null)
            {
                instance.WalkToDestination(location);
            }

            OTCLog.Msg(OTCLog.Systems.Manager, $"Created manager {id} at business {business.PropertyCode} (NetObjId={instance.NetworkObjectId})");
            return instance;
        }

        /// <summary>
        /// Adopts an existing FishNet-replicated NPC as a Manager (client path).
        /// </summary>
        public static ManagerInstance Adopt(string id, int seed, Business business, NPC existingNpc)
        {
            if (Active.ContainsKey(id))
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"{id} already exists, returning existing instance");
                return Active[id];
            }

            var (firstName, lastName) = ManagerSpawner.GetManagerName(seed);
            var basicInfo = existingNpc.GetBasicInfoConfig();
            if (basicInfo != null)
            {
                basicInfo.ID = id;
                basicInfo.FirstName = firstName;
                basicInfo.LastName = lastName;
            }

            // Clear stale mugshot from the source prefab so MugshotUtility
            // polling detects our freshly generated sprite, not the clone's.
            existingNpc.SetMSGConversation(null);
            var clearAppearance = existingNpc.GetAppearanceConfig();
            if (clearAppearance != null) clearAppearance.Mugshot = null;

            var avatarSettings = ManagerSpawner.ApplyAppearance(existingNpc, seed);
            ManagerSpawner.InitializeMessaging(existingNpc);
            ManagerSpawner.EnsureVoiceDatabase(existingNpc);

            var instance = new ManagerInstance(id, seed, business)
            {
                GameNpc = existingNpc,
                IsAdopted = true
            };

            instance.SupplyBehaviour = new ManagerSupplyBehaviour(instance);
            instance.DistributionBehaviour = new ManagerDistributionBehaviour(instance);

            Active[id] = instance;

            // Set up inventory and dialogue choices
            ManagerSpawner.SetupInventory(existingNpc);
            ManagerSpawner.SetupDialogueChoices(instance);

            // Apply upgrade tiers (config will be deserialized shortly after by ApplyManagerSlot)
            instance.ApplySpeedUpgrade();
            instance.ApplyInventoryCapacity();

            // Generate mugshot using the exact settings from ApplyAppearance
            instance.GenerateMugshot(avatarSettings);

            // Always-on map marker (like dealers)
            instance.SetupMapMarker();

            OTCLog.Msg(OTCLog.Systems.Manager, $"Adopted FishNet NPC for manager {id} ({firstName} {lastName}) at {business.PropertyCode}");
            return instance;
        }

        // Hold references to IL2CPP callbacks to prevent GC collection
        internal GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult> _destCallback;
        internal GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult> _transferCallback;
        private GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult> _fireCallback;
        // _mugshotCallback removed — mugshot generation now uses direct IconGenerator capture

        /// <summary>
        /// Walks the Manager NPC from its spawn point to the business destination.
        /// </summary>
        public void WalkToDestination(ManagerLocations.BusinessLocation location)
        {
            try
            {
                if (GameNpc?.Movement == null) return;

                TargetLocation = location;
                ArrivedAtDestination = false;
                _consecutiveWalkFailures = 0;

                _destCallback = (GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult>)
                    new Action<ScheduleOne.NPCs.NPCMovement.WalkResult>(result =>
                    {
                        if (result == ScheduleOne.NPCs.NPCMovement.WalkResult.Success ||
                            result == ScheduleOne.NPCs.NPCMovement.WalkResult.Partial)
                        {
                            ArrivedAtDestination = true;
                            TargetLocation = null;
                            _consecutiveWalkFailures = 0;
                            RestoreIdlePriority();
                            FaceDirection(location.DestRotation);
                        }
                        else if (result == ScheduleOne.NPCs.NPCMovement.WalkResult.Failed)
                        {
                            _consecutiveWalkFailures++;
                            if (!TryWalkEscalation(location.Destination, _destCallback))
                            {
                                LogWarning("escalation exhausted, warping to destination");
                                WarpToPosition(location.Destination);
                                ArrivedAtDestination = true;
                                TargetLocation = null;
                                _consecutiveWalkFailures = 0;
                                RestoreIdlePriority();
                                FaceDirection(location.DestRotation);
                            }
                        }
                        // On Stopped (e.g. dialogue interrupt), leave TargetLocation set so EnsureMoving can resume
                    });

                SetWalkingPriority();
                GameNpc.Movement.SetDestination(location.Destination, _destCallback, 3f, 1f);
                if (Config.ManagerVerboseLogging.Value)
                    Log($"walking to destination: {location.Destination}");
            }
            catch (Exception ex)
            {
                LogWarning($"WalkToDestination failed: {ex.Message}");
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
        /// Cascading fallback chain for walk failures (hire walk).
        /// Returns true if an escalation was attempted, false if all levels exhausted.
        /// </summary>
        private bool TryWalkEscalation(Vector3 target,
            GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult> callback)
        {
            var movement = GameNpc?.Movement;
            if (movement == null) return false;

            switch (_consecutiveWalkFailures)
            {
                case 1:
                    if (Config.ManagerVerboseLogging.Value)
                        Log("walk failed, retrying with IgnoreCosts");
                    movement.SetAgentType(ScheduleOne.NPCs.NPCMovement.EAgentType.IgnoreCosts);
                    movement.SetDestination(target, callback, 3f, 1f);
                    return true;

                case 2:
                    if (Config.ManagerVerboseLogging.Value)
                        Log("walk failed with IgnoreCosts, retrying on Humanoid");
                    movement.SetAgentType(ScheduleOne.NPCs.NPCMovement.EAgentType.Humanoid);
                    movement.SetDestination(target, callback, 3f, 1f);
                    return true;

                case 3:
                    if (Config.ManagerVerboseLogging.Value)
                        Log("walk failed, IgnoreCosts from current position");
                    movement.SetAgentType(ScheduleOne.NPCs.NPCMovement.EAgentType.IgnoreCosts);
                    movement.SetDestination(target, callback, 3f, 1f);
                    return true;

                default:
                    if (Config.ManagerVerboseLogging.Value)
                        Log("all walk escalation attempts exhausted");
                    movement.SetAgentType(ScheduleOne.NPCs.NPCMovement.EAgentType.Humanoid);
                    return false;
            }
        }

        private void SetWalkingPriority()
        {
            try
            {
                var agent = GameNpc?.Movement?.Agent;
                if (agent != null) agent.avoidancePriority = WALK_AVOIDANCE_PRIORITY;
            }
            catch { }
        }

        private void RestoreIdlePriority()
        {
            try
            {
                var agent = GameNpc?.Movement?.Agent;
                if (agent != null) agent.avoidancePriority = IDLE_AVOIDANCE_PRIORITY;
            }
            catch { }
        }

        private void WarpToPosition(Vector3 position)
        {
            try
            {
                if (NavMeshUtility.SamplePosition(position, out UnityEngine.AI.NavMeshHit hit, 5f, -1))
                    GameNpc?.Movement?.Warp(hit.position);
                else
                    GameNpc?.Movement?.Warp(position);
            }
            catch (Exception ex)
            {
                LogWarning($"WarpToPosition failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Generates a mugshot from the NPC's avatar settings via MugshotUtility.
        /// If explicit settings are provided, those are used directly for the capture
        /// (ensures host/client determinism). Otherwise falls back to Avatar.CurrentSettings.
        /// Updates NPC.MugshotSprite, the locker display, phone messaging icon, and map POI.
        /// </summary>
        public void GenerateMugshot(ScheduleOne.AvatarFramework.AvatarSettings explicitSettings = null)
        {
            if (Config.ManagerVerboseLogging.Value)
                Log($"GenerateMugshot() called (explicitSettings={explicitSettings != null})");
            MugshotUtility.Generate(GameNpc, $"Manager {Id}", sprite =>
            {
                if (sprite == null) return;

                var appearance = GameNpc.GetAppearanceConfig();
                if (appearance != null) appearance.Mugshot = sprite;

                // Locker — S1API creates sprites with Vector2.zero pivot (bottom-left),
                // but the 3D SpriteRenderer on the clipboard needs centered pivot.
                if (AssignedLocker?.MugshotSprite != null)
                {
                    AssignedLocker.MugshotSprite.sprite = CreateCenteredSprite(sprite);
                    if (Config.ManagerVerboseLogging.Value)
                        Log("mugshot applied to locker");
                }

                // Phone messaging entry icon
                RefreshPhoneIcon();

                // Map marker POI icon
                RefreshMapPoiIcon();

                IsMugshotReady = true;
                try { OnMugshotReady?.Invoke(); } catch { }

                if (Config.ManagerVerboseLogging.Value)
                    Log("mugshot applied to NPC + messaging");
            }, explicitSettings);
        }

        /// <summary>
        /// Pushes the current MugshotSprite into the phone messaging entry list icon.
        /// Safe to call at any time — no-ops if conversation UI doesn't exist yet.
        /// </summary>
        private void RefreshPhoneIcon()
        {
            try
            {
                var conv = GameNpc?.GetMSGConversation();
                if (conv?.entry == null)
                {
                    if (Config.ManagerVerboseLogging.Value)
                        Log("RefreshPhoneIcon — conv.entry is null, skipping");
                    return;
                }
                var iconImg = conv.entry.Find("IconMask/Icon")?.GetComponent<Image>();
                if (iconImg != null)
                {
                    iconImg.sprite = GameNpc.MugshotSprite;
                    if (Config.ManagerVerboseLogging.Value)
                        Log($"RefreshPhoneIcon — entry icon updated (sprite null={GameNpc.MugshotSprite == null})");
                }
                else
                {
                    LogWarning("RefreshPhoneIcon — IconMask/Icon Image not found");
                }
            }
            catch (Exception ex)
            {
                LogWarning($"RefreshPhoneIcon failed: {ex.Message}");
            }
        }

        private void RefreshMapPoiIcon()
        {
            try
            {
                if (MapPoI == null || GameNpc?.MugshotSprite == null) return;
                var iconTransform = ((ScheduleOne.Map.POI)MapPoI).IconContainer?.Find("Outline/Icon");
                if (iconTransform != null)
                {
                    var img = iconTransform.GetComponent<Image>();
                    if (img != null)
                    {
                        img.sprite = GameNpc.MugshotSprite;
                        if (Config.ManagerVerboseLogging.Value)
                            Log("map POI icon updated");
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarning($"RefreshMapPoiIcon failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-creates a sprite with centered pivot (0.5, 0.5) for 3D SpriteRenderers like the locker clipboard.
        /// S1API creates mugshot sprites with Vector2.zero pivot which offsets them on world-space renderers.
        /// </summary>
        private static Sprite CreateCenteredSprite(Sprite source)
        {
            return Sprite.Create(source.texture,
                new Rect(0, 0, source.texture.width, source.texture.height),
                new Vector2(0.5f, 0.5f));
        }

        /// <summary>
        /// Creates an always-on map marker for this manager, identical to how dealers appear.
        /// </summary>
        public void SetupMapMarker()
        {
            try
            {
                if (MapPoI != null || GameNpc == null) return;

                var npcManager = NetworkSingleton<NPCManager>.Instance;
                if (npcManager?.NPCPoIPrefab == null)
                {
                    LogWarning("NPCManager or NPCPoIPrefab not available, skipping map marker");
                    return;
                }

                MapPoI = UnityEngine.Object.Instantiate(npcManager.NPCPoIPrefab, GameNpc.transform);
                MapPoI.transform.localPosition = Vector3.zero;
                MapPoI.SetMainText($"{GameNpc.FirstName} {GameNpc.LastName}\n(Manager)");
                MapPoI.SetNPC(GameNpc);
                MapPoI.enabled = true;

                if (Config.ManagerVerboseLogging.Value)
                    Log("map marker created");
            }
            catch (Exception ex)
            {
                LogWarning($"failed to create map marker: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-issues the walk command if the manager was interrupted (e.g. by dialogue).
        /// Call this from the lifecycle tick.
        /// </summary>
        public void EnsureMoving()
        {
            if (!IsValid || State == ManagerState.Fired) return;
            if (State == ManagerState.SupplyRun) return; // supply behaviour handles its own walks
            if (State == ManagerState.DistributionRun) return; // distribution behaviour handles its own walks

            try
            {
                var movement = GameNpc?.Movement;
                if (movement == null) return;

                // Don't resume walking while in dialogue or player is viewing inventory
                var dialogueHandler = GameNpc.DialogueHandler;
                if (dialogueHandler != null && dialogueHandler.IsDialogueInProgress) return;
                if (IsPlayerInteracting) return;

                // Also check server-side GenericDialogueBehaviour (active when client initiates dialogue
                // via Enable_Server RPC — IsDialogueInProgress is only set on the client)
                try
                {
                    var dialogueBeh = GameNpc.Behaviour?.GenericDialogueBehaviour;
                    if (dialogueBeh != null && dialogueBeh.Active) return;
                }
                catch { }

                // If the NPC still has an active destination, nothing to do
                if (movement.HasDestination) return;

                if (TargetLocation != null && !ArrivedAtDestination)
                {
                    var pos = Position ?? Vector3.zero;
                    var dist = Vector3.Distance(pos, TargetLocation.Destination);
                    if (dist > 3f)
                    {
                        if (Config.ManagerVerboseLogging.Value && UnityEngine.Time.time - _lastEnsureMovingLog > 10f)
                        {
                            Log($"resuming walk to destination (interrupted, dist={dist:F1}m)");
                            _lastEnsureMovingLog = UnityEngine.Time.time;
                        }
                        GameNpc.Movement.SetDestination(TargetLocation.Destination, _destCallback, 3f, 1f);
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarning($"EnsureMoving failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Walks the fired Manager back to their spawn point, then despawns.
        /// Falls back to immediate despawn if no location or movement is unavailable.
        /// </summary>
        public void WalkAwayAndDespawn()
        {
            SupplyBehaviour?.Cancel();
            DistributionBehaviour?.Cancel();

            try
            {
                var location = ManagerLocations.GetLocation(BusinessPropertyCode);
                if (location == null || GameNpc?.Movement == null)
                {
                    Despawn();
                    return;
                }

                // Clear any existing walk target so EnsureMoving doesn't fight us
                TargetLocation = null;
                ArrivedAtDestination = true;

                _fireCallback = (GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult>)
                    new Action<ScheduleOne.NPCs.NPCMovement.WalkResult>(result =>
                    {
                        Log($"fired, reached spawn (result={result}), despawning");
                        Despawn();
                    });

                GameNpc.Movement.SetDestination(location.SpawnPosition, _fireCallback, 3f, 1f);
                if (Config.ManagerVerboseLogging.Value)
                    Log("fired, walking to spawn before despawn");
            }
            catch (Exception ex)
            {
                LogWarning($"WalkAwayAndDespawn failed: {ex.Message}, despawning immediately");
                Despawn();
            }
        }

        /// <summary>
        /// Assigns a locker (EmployeeHome) to this Manager and updates its display.
        /// </summary>
        public void AssignLocker(EmployeeHome locker)
        {
            if (locker == null) return;

            // Clear the old locker display if switching to a different one
            if (AssignedLocker != null && AssignedLocker != locker)
                ClearLocker();

            // Clear any vanilla employee already assigned to this locker (e.g. from BusinessEmployment mod)
            if (locker.AssignedEmployee != null)
            {
                if (Config.ManagerVerboseLogging.Value)
                    Log($"clearing existing employee '{locker.AssignedEmployee.FullName}' from locker");
                locker.SetAssignedEmployee(null);
            }

            // Clear any other manager already assigned to this locker
            foreach (var other in Active.Values)
            {
                if (other != this && other.AssignedLocker == locker)
                {
                    if (Config.ManagerVerboseLogging.Value)
                        Log($"clearing other manager {other.Id} from same locker");
                    other.ClearLocker();
                }
            }

            AssignedLocker = locker;
            NoLockerTextSent = false;

            try
            {
                // Mirror vanilla SetAssignedEmployee + UpdateStorageText (we can't call them since we're not an Employee)
                float dailyWage = GetDailyWage();
                string wageStr = $"<color=#54E717>${dailyWage:F0}</color>";
                string homeType = locker.HomeType ?? "Briefcase";
                string fullName = GameNpc != null ? $"{GameNpc.FirstName} {GameNpc.LastName}" : "Manager";

                // Physical locker model: name label, mugshot, clipboard
                if (locker.NameLabel != null && GameNpc != null)
                    locker.NameLabel.text = $"{GameNpc.FirstName}\n{GameNpc.LastName}";

                if (Config.ManagerVerboseLogging.Value)
                    Log($"locker mugshot state: locker.MugshotSprite={locker.MugshotSprite != null}, GameNpc.MugshotSprite={GameNpc?.MugshotSprite != null}, IsMugshotReady={IsMugshotReady}");
                if (locker.MugshotSprite != null && GameNpc?.MugshotSprite != null)
                {
                    locker.MugshotSprite.sprite = CreateCenteredSprite(GameNpc.MugshotSprite);
                    if (Config.ManagerVerboseLogging.Value)
                        Log("set locker mugshot sprite immediately");
                }
                else if (!IsMugshotReady && locker.MugshotSprite != null)
                {
                    // Mugshot coroutine still running — subscribe to update locker when it finishes
                    if (Config.ManagerVerboseLogging.Value)
                        Log("mugshot not ready, subscribing to OnMugshotReady for locker");
                    OnMugshotReady += () =>
                    {
                        try
                        {
                            if (AssignedLocker == locker && locker.MugshotSprite != null && GameNpc?.MugshotSprite != null)
                            {
                                locker.MugshotSprite.sprite = CreateCenteredSprite(GameNpc.MugshotSprite);
                                if (Config.ManagerVerboseLogging.Value)
                                    Log("locker mugshot updated via OnMugshotReady");
                            }
                        }
                        catch { }
                    };
                }

                if (locker.Clipboard != null)
                    locker.Clipboard.gameObject.SetActive(true);

                // Storage menu display text
                locker.Storage.StorageEntityName = $"{GameNpc?.FirstName}'s {homeType}";
                locker.Storage.StorageEntitySubtitle =
                    $"{fullName} will draw a daily wage of {wageStr} from this {homeType.ToLower()}";

                // Recolor locker band to red (vanilla uses colored bands per employee type)
                ApplyLockerRecoloring(locker);

                if (Config.ManagerVerboseLogging.Value)
                    Log($"assigned locker at {locker.transform.position}");
            }
            catch (Exception ex)
            {
                LogWarning($"failed to update locker display: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears the locker assignment and resets display to default.
        /// </summary>
        public void ClearLocker()
        {
            if (AssignedLocker != null)
            {
                try
                {
                    ResetLockerRecoloring(AssignedLocker);

                    // Reset display text (vanilla does this when employee is null)
                    string homeType = AssignedLocker.HomeType ?? "Briefcase";
                    AssignedLocker.Storage.StorageEntityName = homeType;
                    AssignedLocker.Storage.StorageEntitySubtitle = string.Empty;

                    // Hide clipboard and clear visual refs on the locker model
                    if (AssignedLocker.Clipboard != null)
                        AssignedLocker.Clipboard.gameObject.SetActive(false);
                    if (AssignedLocker.NameLabel != null)
                        AssignedLocker.NameLabel.text = "";
                    if (AssignedLocker.MugshotSprite != null)
                        AssignedLocker.MugshotSprite.sprite = null;
                }
                catch (Exception ex)
                {
                    LogWarning($"ClearLocker display reset failed: {ex.Message}");
                }
            }
            AssignedLocker = null;
        }

        /// <summary>
        /// Reconciles the locker assignment from the deserialized config.
        /// Called on the host when receiving config from a client.
        /// </summary>
        public void ReconcileLockerFromConfig()
        {
            try
            {
                var configLocker = Configuration.Locker;
                if (configLocker != null)
                {
                    var home = configLocker.GetComponent<EmployeeHome>();
                    if (home == null)
                        home = configLocker.GetComponentInParent<EmployeeHome>();
                    if (home != null && home != AssignedLocker)
                        AssignLocker(home);
                }
                else if (AssignedLocker != null)
                {
                    ClearLocker();
                }
            }
            catch (Exception ex)
            {
                LogWarning($"ReconcileLockerFromConfig failed: {ex.Message}");
            }
        }

        private static MaterialPropertyBlock _managerBandBlock;

        private static void ApplyLockerRecoloring(EmployeeHome locker)
        {
            if (locker.EmployeeSpecificMeshes == null || locker.EmployeeSpecificMeshes.Length == 0)
            {
                OTCLog.Warning(OTCLog.Systems.Manager, "Locker band: EmployeeSpecificMeshes is null/empty");
                return;
            }

            // Use a vanilla employee material (has correct band-only texture/alpha).
            // Then override just the color via MaterialPropertyBlock to keep
            // the shader/texture intact and only change the tint.
            Material bandMat = locker.SpecificMat_Packager
                ?? locker.SpecificMat_Botanist
                ?? locker.SpecificMat_Chemist;

            if (bandMat == null)
            {
                OTCLog.Warning(OTCLog.Systems.Manager, "No employee-specific material found on locker");
                return;
            }

            if (_managerBandBlock == null)
            {
                _managerBandBlock = new MaterialPropertyBlock();
                _managerBandBlock.SetColor("_BaseColor", new Color(0.85f, 0.1f, 0.1f));
            }

            foreach (var renderer in locker.EmployeeSpecificMeshes)
            {
                if (renderer != null)
                {
                    renderer.sharedMaterial = bandMat;
                    renderer.SetPropertyBlock(_managerBandBlock);
                }
            }
        }

        private static void ResetLockerRecoloring(EmployeeHome locker)
        {
            if (locker?.EmployeeSpecificMeshes == null) return;

            Material resetMat = locker.SpecificMat_Default;
            if (resetMat == null) return;

            foreach (var renderer in locker.EmployeeSpecificMeshes)
            {
                if (renderer != null)
                {
                    renderer.sharedMaterial = resetMat;
                    renderer.SetPropertyBlock(null);
                }
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
                LogWarning($"RemoveLockerCash failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends a text message from this Manager to the player.
        /// Uses network=false to avoid FishNet's RunLocally double-delivery on the host.
        /// Each player sends their own local text (host from HireManager, client from TryAdopt).
        /// </summary>
        public void SendTextMessage(string message, bool queueForClient = true)
        {
            if (GameNpc == null)
            {
                LogWarning("cannot send text - GameNpc is null");
                return;
            }

            try
            {
                var conv = GameNpc.GetMSGConversation();
                if (conv == null)
                {
                    LogWarning("MSGConversation is null, cannot send text");
                    return;
                }
                var msg = new ScheduleOne.Messaging.Message(
                    message,
                    ScheduleOne.Messaging.Message.ESenderType.Other,
                    true,
                    UnityEngine.Random.Range(int.MinValue, int.MaxValue));
                conv.SendMessage(msg, true, false);

                // Queue for client delivery via SyncVar (host-only warning texts)
                if (queueForClient && NetworkHelper.IsHost)
                {
                    PendingClientMessage = message;
                    HasPendingMessages = true;
                }
            }
            catch (Exception ex)
            {
                LogWarning($"failed to send text: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a greeting text exactly once (prevents duplicate greetings from host/client paths).
        /// Greetings bypass SyncVar queue because both host and client send them locally.
        /// </summary>
        public void SendGreeting(string message)
        {
            if (GreetingSent) return;
            GreetingSent = true;
            SendTextMessage(message, queueForClient: false);
        }

        /// <summary>
        /// Clears and hides the text message conversation for this Manager.
        /// Called on fire to remove stale threads.
        /// </summary>
        public void ClearMessages()
        {
            try
            {
                var conv = GameNpc?.GetMSGConversation();
                if (conv == null) return;

                conv.messageHistory?.Clear();

                // Only hide the phone entry if the conversation has one
                // (our cloned NPCs skip CreateConversationUI so entry is null)
                if (conv.entry != null)
                    conv.SetEntryVisibility(false);

                if (Config.ManagerVerboseLogging.Value)
                    Log("cleared text messages");
            }
            catch (Exception ex)
            {
                LogWarning($"ClearMessages failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Despawns and cleans up this Manager.
        /// </summary>
        public void Despawn()
        {
            SupplyBehaviour?.Cancel();
            DistributionBehaviour?.Cancel();

            // Remove map marker
            if (MapPoI != null)
            {
                MapPoI.enabled = false;
                try { UnityEngine.Object.Destroy(MapPoI.gameObject); } catch { }
                MapPoI = null;
            }

            Log("despawning");
            Active.Remove(Id);

            // Clear configuration
            Configuration.ClearAll();

            // Clean up dialogue choices
            ManagerSpawner.CleanupDialogueChoices(Id);

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
                    if (Config.ManagerVerboseLogging.Value)
                        Log("releasing adopted FishNet NPC");
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
                if (mgr.State == ManagerState.Fired) continue;
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
            {
                if (mgr.State == ManagerState.Fired) continue;
                codes.Add(mgr.BusinessPropertyCode);
            }
            return string.Join(",", codes);
        }

        /// <summary>
        /// Serializes full manager state for client adoption.
        /// Format: id:seed:biz:netObjId:configData;id:seed:biz:netObjId:configData
        /// Config data escapes | → ~ and ; → ^ to avoid conflicting with entry/SyncVar separators.
        /// </summary>
        public static string SerializeManagerState()
        {
            if (Active.Count == 0) return "";
            var parts = new List<string>();
            foreach (var mgr in Active.Values)
            {
                // Skip fired managers — they're walking away and shouldn't persist
                if (mgr.State == ManagerState.Fired) continue;

                string configStr = EncodeConfig(mgr.Configuration.Serialize());
                parts.Add($"{mgr.Id}:{mgr.SpawnSeed}:{mgr.BusinessPropertyCode}:{mgr.NetworkObjectId}:{configStr}");
            }
            string result = string.Join(";", parts);
            if (Config.ManagerVerboseLogging.Value)
                OTCLog.Msg(OTCLog.Systems.Manager, $"SerializeManagerState: {Active.Count} managers → '{result}'");
            return result;
        }

        /// <summary>Escapes config separators for safe embedding in entry strings.</summary>
        internal static string EncodeConfig(string raw) => raw.Replace('|', '~').Replace(';', '^');
        /// <summary>Restores config separators from escaped form.</summary>
        internal static string DecodeConfig(string encoded) => encoded.Replace('~', '|').Replace('^', ';');

        // ==================================================================
        // Per-manager SyncVar slot management
        // ==================================================================

        /// <summary>
        /// Serializes a single manager for its SyncVar slot.
        /// Format: id:seed:biz:netObjId:stateCode:encodedConfig
        /// State codes: tens=state, ones=sub-phase (e.g. 21=supply-depositing, 32=distribution-picking up)
        /// </summary>
        internal static string SerializeSingleManager(ManagerInstance mgr)
        {
            string configStr = EncodeConfig(mgr.Configuration.Serialize());
            int stateCode = mgr.State switch
            {
                ManagerState.Idle when mgr.PaidForToday => 10,
                ManagerState.SupplyRun => 20 + GetSupplySubPhase(mgr),
                ManagerState.DistributionRun => 30 + GetDistributionSubPhase(mgr),
                ManagerState.Transferring => 40,
                _ => 0 // Idle+unpaid or NoFunds
            };
            return $"{mgr.Id}:{mgr.SpawnSeed}:{mgr.BusinessPropertyCode}:{mgr.NetworkObjectId}:{stateCode}:{configStr}";
        }

        // Sub-phase: 0=generic, 1=depositing, 2=purchasing
        private static int GetSupplySubPhase(ManagerInstance mgr)
        {
            if (mgr.SupplyBehaviour == null) return 0;
            return mgr.SupplyBehaviour.State switch
            {
                ManagerSupplyBehaviour.SupplyState.WalkingToStorage
                    or ManagerSupplyBehaviour.SupplyState.AtStorage => 1,
                ManagerSupplyBehaviour.SupplyState.WalkingToStore
                    or ManagerSupplyBehaviour.SupplyState.AtStore => 2,
                _ => 0,
            };
        }

        // Sub-phase: 0=generic, 1=depositing, 2=picking up
        private static int GetDistributionSubPhase(ManagerInstance mgr)
        {
            if (mgr.DistributionBehaviour == null) return 0;
            return mgr.DistributionBehaviour.State switch
            {
                ManagerDistributionBehaviour.DistributionState.WalkingToDestProperty
                    or ManagerDistributionBehaviour.DistributionState.WalkingToDest
                    or ManagerDistributionBehaviour.DistributionState.AtDest => 1,
                ManagerDistributionBehaviour.DistributionState.WalkingToSourceProperty
                    or ManagerDistributionBehaviour.DistributionState.WalkingToSource
                    or ManagerDistributionBehaviour.DistributionState.AtSource => 2,
                _ => 0,
            };
        }

        private static int AllocateSlot(string managerId)
        {
            if (_managerSlot.TryGetValue(managerId, out int existing))
                return existing;
            for (int i = 0; i < _slotManagerId.Length; i++)
            {
                if (_slotManagerId[i] == null)
                {
                    _slotManagerId[i] = managerId;
                    _managerSlot[managerId] = i;
                    return i;
                }
            }
            OTCLog.Warning(OTCLog.Systems.Manager, $"AllocateSlot: no free slots for {managerId} (all {_slotManagerId.Length} in use)");
            return -1;
        }

        private static void FreeSlot(string managerId)
        {
            if (_managerSlot.TryGetValue(managerId, out int slot))
            {
                _slotManagerId[slot] = null;
                _managerSlot.Remove(managerId);
            }
        }

        /// <summary>
        /// Publishes each active manager to its own SyncVar slot.
        /// Clears slots for managers that no longer exist.
        /// Called from ConfigSyncData.PublishManagerState (host only).
        /// </summary>
        public static void PublishAllSlots()
        {
            // Assign slots to active (non-fired) managers and write their data
            var activeIds = new HashSet<string>();
            foreach (var mgr in Active.Values)
            {
                if (mgr.State == ManagerState.Fired) continue;
                activeIds.Add(mgr.Id);

                int slot = AllocateSlot(mgr.Id);
                if (slot < 0) continue;

                string payload = SerializeSingleManager(mgr);
                NetworkSyncBridge.PushManagerSlot(slot, payload);

                if (Config.ManagerVerboseLogging.Value)
                    OTCLog.Msg(OTCLog.Systems.Manager, $"PublishSlot[{slot}]: {mgr.Id} ({payload.Length} chars)");
            }

            // Clear slots for managers that are no longer active
            for (int i = 0; i < _slotManagerId.Length; i++)
            {
                if (_slotManagerId[i] != null && !activeIds.Contains(_slotManagerId[i]))
                {
                    if (Config.ManagerVerboseLogging.Value)
                        OTCLog.Msg(OTCLog.Systems.Manager, $"PublishSlot[{i}]: clearing (was {_slotManagerId[i]})");
                    NetworkSyncBridge.ClearManagerSlot(i);
                    _managerSlot.Remove(_slotManagerId[i]);
                    _slotManagerId[i] = null;
                }
            }
        }

        /// <summary>
        /// Resets slot tracking state. Called on scene transitions / cleanup.
        /// </summary>
        internal static void ResetSlots()
        {
            _managerSlot.Clear();
            for (int i = 0; i < _slotManagerId.Length; i++)
                _slotManagerId[i] = null;
        }

        /// <summary>
        /// Applies a state code from the host to a client-side manager.
        /// Uses direct field access to avoid setting StatePublishNeeded on the client.
        /// State codes: tens=state, ones=sub-phase (e.g. 21=supply-depositing)
        /// </summary>
        private static void ApplyStateCode(ManagerInstance mgr, int stateCode)
        {
            int top = stateCode / 10;
            mgr._subPhaseCode = stateCode % 10;
            switch (top)
            {
                case 1: mgr._state = ManagerState.Idle; mgr._paidForToday = true; break;
                case 2: mgr._state = ManagerState.SupplyRun; mgr._paidForToday = true; break;
                case 3: mgr._state = ManagerState.DistributionRun; mgr._paidForToday = true; break;
                case 4: mgr._state = ManagerState.Transferring; mgr._paidForToday = false; break;
                default: mgr._state = ManagerState.Idle; mgr._paidForToday = false; break; // 0 = NoFunds
            }
        }

        // ==================================================================
        // Client adoption
        // ==================================================================

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
            public string ConfigStr;
            public int StateCode;
        }

        /// <summary>
        /// Applies a single manager slot update from the host.
        /// Called from ConfigSyncData.HandleManagerSlotChanged for each slot individually.
        /// </summary>
        public static void ApplyManagerSlot(int slot, string data)
        {
            if (Config.ManagerVerboseLogging.Value)
                OTCLog.Msg(OTCLog.Systems.Manager, $"ApplyManagerSlot[{slot}]: '{data}' (Active={Active.Count}, Pending={_pendingAdoptions.Count})");

            // Empty slot → manager was removed/fired
            if (string.IsNullOrEmpty(data))
            {
                string oldId = _slotManagerId[slot];
                if (oldId != null)
                {
                    _slotManagerId[slot] = null;
                    _managerSlot.Remove(oldId);

                    // Despawn if active
                    if (Active.TryGetValue(oldId, out var stale))
                    {
                        OTCLog.Msg(OTCLog.Systems.Manager, $"ApplyManagerSlot[{slot}]: despawning {oldId} (slot cleared by host)");
                        stale.ClearMessages();
                        stale.Despawn();
                    }
                    // Remove pending adoption
                    if (_pendingAdoptions.ContainsKey(oldId))
                    {
                        _pendingAdoptions.Remove(oldId);
                    }
                }
                RebuildSyncedBusinesses();
                return;
            }

            // Parse single entry: id:seed:biz:netObjId:stateCode:config
            var parts = data.Split(':');
            if (parts.Length < 5)
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"ApplyManagerSlot[{slot}]: malformed data '{data}' (parts={parts.Length})");
                return;
            }

            string id = parts[0];
            if (!int.TryParse(parts[1], out int seed))
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"ApplyManagerSlot[{slot}]: bad seed in '{data}'");
                return;
            }
            string bizCode = parts[2];
            if (!int.TryParse(parts[3], out int netObjId))
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"ApplyManagerSlot[{slot}]: bad netObjId in '{data}'");
                return;
            }
            int.TryParse(parts[4], out int stateCode);
            string configStr = parts.Length > 5 ? string.Join(":", parts, 5, parts.Length - 5) : "";

            // Track slot assignment
            string prevId = _slotManagerId[slot];
            if (prevId != null && prevId != id)
            {
                // Slot was reassigned to a different manager — despawn old one
                _managerSlot.Remove(prevId);
                if (Active.TryGetValue(prevId, out var old))
                {
                    old.ClearMessages();
                    old.Despawn();
                }
                _pendingAdoptions.Remove(prevId);
            }
            _slotManagerId[slot] = id;
            _managerSlot[id] = slot;

            // Already adopted — update config/business
            if (Active.ContainsKey(id))
            {
                // Skip SyncVar echo while the client is actively editing this manager's clipboard
                if (!NetworkHelper.IsHost && UI.ManagerConfigPanel.IsOpen
                    && string.Equals(UI.ManagerConfigPanel.CurrentManagerId, id))
                {
                    if (Config.ManagerVerboseLogging.Value)
                        OTCLog.Msg(OTCLog.Systems.Manager, $"ApplyManagerSlot[{slot}]: {id} skipped config update (clipboard open on client)");
                    RebuildSyncedBusinesses();
                    return;
                }

                var existing = Active[id];

                // Update business assignment if manager was transferred
                if (!string.Equals(existing.BusinessPropertyCode, bizCode, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var biz in ScheduleOne.Property.Business.OwnedBusinesses)
                    {
                        if (biz != null && string.Equals(biz.PropertyCode, bizCode, StringComparison.OrdinalIgnoreCase))
                        {
                            existing.AssignedBusiness = biz;
                            existing.BusinessPropertyCode = bizCode;
                            OTCLog.Msg(OTCLog.Systems.Manager, $"ApplyManagerSlot[{slot}]: {id} transferred to {bizCode}");
                            break;
                        }
                    }
                }

                // Update config
                if (!string.IsNullOrEmpty(configStr))
                {
                    existing.Configuration.Deserialize(DecodeConfig(configStr));
                    existing.ReconcileLockerFromConfig();
                    existing.ApplySpeedUpgrade();
                    existing.ApplyInventoryCapacity();
                }

                // Apply host state (bypass setter to avoid setting StatePublishNeeded on client)
                ApplyStateCode(existing, stateCode);

                if (Config.ManagerVerboseLogging.Value)
                    OTCLog.Msg(OTCLog.Systems.Manager, $"ApplyManagerSlot[{slot}]: {id} already in Active, updated config+state");
                RebuildSyncedBusinesses();
                return;
            }

            // Already pending — update config + state
            if (_pendingAdoptions.ContainsKey(id))
            {
                _pendingAdoptions[id].ConfigStr = configStr;
                _pendingAdoptions[id].StateCode = stateCode;
                if (Config.ManagerVerboseLogging.Value)
                    OTCLog.Msg(OTCLog.Systems.Manager, $"ApplyManagerSlot[{slot}]: {id} already pending, updated config");
                RebuildSyncedBusinesses();
                return;
            }

            // Find FishNet NPC by ObjectId
            NPC npc = FindNetworkNpc(netObjId);
            if (npc == null)
            {
                _pendingAdoptions[id] = new PendingAdoption
                {
                    Id = id, Seed = seed, BizCode = bizCode,
                    NetObjId = netObjId, CreatedTime = UnityEngine.Time.time,
                    ConfigStr = configStr, StateCode = stateCode
                };
                if (Config.ManagerVerboseLogging.Value)
                    OTCLog.Msg(OTCLog.Systems.Manager, $"ApplyManagerSlot[{slot}]: NPC ObjectId {netObjId} not found yet, queued adoption for {id}");
                RebuildSyncedBusinesses();
                return;
            }

            TryAdopt(id, seed, bizCode, netObjId, npc, configStr, stateCode);
            RebuildSyncedBusinesses();
        }

        /// <summary>
        /// Rebuilds SyncedManagerBusinesses from current slot + active state.
        /// </summary>
        private static void RebuildSyncedBusinesses()
        {
            SyncedManagerBusinesses.Clear();
            foreach (var mgr in Active.Values)
            {
                if (mgr.State != ManagerState.Fired)
                    SyncedManagerBusinesses.Add(mgr.BusinessPropertyCode);
            }
            // Also include pending adoptions (manager exists but NPC not found yet)
            foreach (var pa in _pendingAdoptions.Values)
                SyncedManagerBusinesses.Add(pa.BizCode);
        }

        private static void TryAdopt(string id, int seed, string bizCode, int netObjId, NPC npc, string configStr = "", int stateCode = 1)
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
                OTCLog.Warning(OTCLog.Systems.Manager, $"TryAdopt: business '{bizCode}' not found for manager {id}");
                return;
            }

            var instance = Adopt(id, seed, business, npc);
            if (instance != null)
            {
                instance.NetworkObjectId = netObjId;

                // Apply config from host
                if (!string.IsNullOrEmpty(configStr))
                {
                    instance.Configuration.Deserialize(DecodeConfig(configStr));
                    instance.ReconcileLockerFromConfig();
                    instance.ApplySpeedUpgrade();
                    instance.ApplyInventoryCapacity();
                }

                // Apply host state (bypass setter to avoid setting StatePublishNeeded on client)
                ApplyStateCode(instance, stateCode);

                // Send greeting text locally (host sends its own during HireManager)
                instance.SendGreeting($"Hey boss! I'm your new manager at {business.PropertyName}. Use the clipboard to assign me a locker and I'll get to work.");

                OTCLog.Msg(OTCLog.Systems.Manager, $"Adopted manager {id} at {bizCode} (NetObjId={netObjId}, config={!string.IsNullOrEmpty(configStr)})");
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
                    OTCLog.Warning(OTCLog.Systems.Manager, $"Giving up adoption for manager {pa.Id} (timeout)");
                    completed.Add(kv.Key);
                    continue;
                }

                NPC npc = FindNetworkNpc(pa.NetObjId);
                if (npc != null)
                {
                    TryAdopt(pa.Id, pa.Seed, pa.BizCode, pa.NetObjId, npc, pa.ConfigStr, pa.StateCode);
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

            var registry = ScheduleOne.NPCs.NPCManager.NPCRegistry;
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
                    var netObj = npc.gameObject.GetComponent<FishNet.Object.NetworkObject>();
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
                    OTCLog.Warning(OTCLog.Systems.Manager, $"Failed to cleanup manager {id}: {ex.Message}");
                }
            }
            Active.Clear();
            ResetSlots();
        }
    }

    public enum ManagerState
    {
        Idle,
        SupplyRun,
        DistributionRun,
        Fired,
        NoFunds,
        Transferring
    }
}
