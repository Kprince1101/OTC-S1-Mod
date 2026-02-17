using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.Property;
using MelonLoader;
using OverTheCounter.Utilities;
using S1API.GameTime;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Data wrapper for a single Manager NPC assigned to a player-owned business.
    /// Tracks NPC reference, assigned property, cash pool, and lifecycle state.
    /// </summary>
    public class ManagerInstance
    {
        internal static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:Manager");

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

        // Lifecycle state
        public ManagerState State { get; set; } = ManagerState.Idle;
        public bool PaidForToday { get; set; }
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
            Logger.Msg($"Manager {Id}: {message}");
            AppendToBuffer($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        public void LogWarning(string message)
        {
            Logger.Warning($"Manager {Id}: {message}");
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
                var inventory = GameNpc?.GetComponent<Il2CppScheduleOne.NPCs.NPCInventory>();
                if (inventory?.ItemSlots == null) return "empty";

                var counts = new Dictionary<string, int>();
                float cashTotal = 0f;

                for (int i = 0; i < inventory.ItemSlots.Count; i++)
                {
                    var item = inventory.ItemSlots[i]?.ItemInstance;
                    if (item == null) continue;

                    var cash = item.TryCast<Il2CppScheduleOne.ItemFramework.CashInstance>();
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
                Logger.Warning($"Manager {id} already exists, returning existing instance");
                return Active[id];
            }

            // Check if this business already has an active manager
            foreach (var existing in Active.Values)
            {
                if (existing.State == ManagerState.Fired) continue;
                if (existing.BusinessPropertyCode == business.PropertyCode)
                {
                    Logger.Warning($"Business {business.PropertyCode} already has a manager ({existing.Id})");
                    return null;
                }
            }

            // Use ManagerLocations if available, otherwise fall back to business spawn point
            if (Config.ManagerVerboseLogging.Value)
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
            instance.SupplyBehaviour = new ManagerSupplyBehaviour(instance);
            instance.DistributionBehaviour = new ManagerDistributionBehaviour(instance);

            // Capture FishNet ObjectId for client-side adoption
            try { instance.NetworkObjectId = npc.NetworkObject.ObjectId; }
            catch { Logger.Warning($"Could not get NetworkObjectId for {id}"); }

            Active[id] = instance;

            // Set up dialogue choices (fire/transfer)
            ManagerSpawner.SetupDialogueChoices(instance);

            // Generate proper mugshot from the applied avatar settings
            instance.GenerateMugshot();

            // Always-on map marker (like dealers)
            instance.SetupMapMarker();

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

            instance.SupplyBehaviour = new ManagerSupplyBehaviour(instance);
            instance.DistributionBehaviour = new ManagerDistributionBehaviour(instance);

            Active[id] = instance;

            // Set up inventory and dialogue choices
            ManagerSpawner.SetupInventory(existingNpc);
            ManagerSpawner.SetupDialogueChoices(instance);

            // 1.8x default NPC walk speed
            try
            {
                var speedCtrl = existingNpc.Movement?.SpeedController;
                speedCtrl?.AddSpeedControl(
                    new Il2CppScheduleOne.NPCs.NPCSpeedController.SpeedControl("manager", 1, 0.144f));
            }
            catch { }

            // Generate proper mugshot from the applied avatar settings
            instance.GenerateMugshot();

            // Always-on map marker (like dealers)
            instance.SetupMapMarker();

            Logger.Msg($"Adopted FishNet NPC for manager {id} ({firstName} {lastName}) at {business.PropertyCode}");
            return instance;
        }

        // Hold references to IL2CPP callbacks to prevent GC collection
        internal Il2CppSystem.Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult> _destCallback;
        internal Il2CppSystem.Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult> _transferCallback;
        private Il2CppSystem.Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult> _fireCallback;
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

                _destCallback = (Il2CppSystem.Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult>)
                    new Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult>(result =>
                    {
                        if (result == Il2CppScheduleOne.NPCs.NPCMovement.WalkResult.Success ||
                            result == Il2CppScheduleOne.NPCs.NPCMovement.WalkResult.Partial)
                        {
                            ArrivedAtDestination = true;
                            TargetLocation = null;
                            FaceDirection(location.DestRotation);
                        }
                        // On Stopped (e.g. dialogue interrupt), leave TargetLocation set so EnsureMoving can resume
                    });

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
        /// Generates a mugshot from the NPC's current avatar settings via MugshotUtility.
        /// Updates NPC.MugshotSprite, the locker display, phone messaging icon, and map POI.
        /// </summary>
        public void GenerateMugshot()
        {
            if (Config.ManagerVerboseLogging.Value)
                Log("GenerateMugshot() called");
            MugshotUtility.Generate(GameNpc, $"Manager {Id}", sprite =>
            {
                if (sprite == null) return;

                GameNpc.MugshotSprite = sprite;

                // Locker
                if (AssignedLocker?.MugshotSprite != null)
                {
                    AssignedLocker.MugshotSprite.sprite = sprite;
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
            });
        }

        /// <summary>
        /// Pushes the current MugshotSprite into the phone messaging entry list icon.
        /// Safe to call at any time — no-ops if conversation UI doesn't exist yet.
        /// </summary>
        private void RefreshPhoneIcon()
        {
            try
            {
                var conv = GameNpc?.MSGConversation;
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
                var iconTransform = ((Il2CppScheduleOne.Map.POI)MapPoI).IconContainer?.Find("Outline/Icon");
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

                _fireCallback = (Il2CppSystem.Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult>)
                    new Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult>(result =>
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
                    Log($"clearing existing employee '{locker.AssignedEmployee.fullName}' from locker");
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
                float dailyWage = Config.ManagerDailyWage.Value;
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
                    locker.MugshotSprite.sprite = GameNpc.MugshotSprite;
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
                                locker.MugshotSprite.sprite = GameNpc.MugshotSprite;
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
                Logger.Warning("Locker band: EmployeeSpecificMeshes is null/empty");
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
                Logger.Warning("No employee-specific material found on locker");
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
                var conv = GameNpc.MSGConversation;
                if (conv == null)
                {
                    LogWarning("MSGConversation is null, cannot send text");
                    return;
                }
                var msg = new Il2CppScheduleOne.Messaging.Message(
                    message,
                    Il2CppScheduleOne.Messaging.Message.ESenderType.Other,
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
                var conv = GameNpc?.MSGConversation;
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
                Logger.Msg($"SerializeManagerState: {Active.Count} managers → '{result}'");
            return result;
        }

        /// <summary>Escapes config separators for safe embedding in entry strings.</summary>
        internal static string EncodeConfig(string raw) => raw.Replace('|', '~').Replace(';', '^');
        /// <summary>Restores config separators from escaped form.</summary>
        internal static string DecodeConfig(string encoded) => encoded.Replace('~', '|').Replace('^', ';');

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
        }

        /// <summary>
        /// Applies manager state from host on client. Finds FishNet NPCs by ObjectId
        /// and adopts them (applies appearance, name, messaging).
        /// Queues pending adoptions for NPCs that haven't replicated yet.
        /// Does NOT modify SyncedManagerBusinesses — that's handled by OnManagerStateChanged in ConfigSyncData.
        /// </summary>
        public static void ApplyManagerState(string stateString)
        {
            if (Config.ManagerVerboseLogging.Value)
                Logger.Msg($"ApplyManagerState: processing '{stateString ?? ""}' (Active={Active.Count}, Pending={_pendingAdoptions.Count})");

            // Collect IDs present in the incoming host state
            var incomingIds = new HashSet<string>();

            if (!string.IsNullOrEmpty(stateString))
            {
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
                    incomingIds.Add(id);

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
                    // Rejoin from index 4 — config data contains ':' separators (item:threshold)
                    // that get split along with the entry-level ':' separators
                    string configStr = parts.Length > 4 ? string.Join(":", parts, 4, parts.Length - 4) : "";

                    // Already adopted or pending
                    if (Active.ContainsKey(id))
                    {
                        // Skip SyncVar echo while the client is actively editing this manager's clipboard.
                        // Steam lobby data truncation can shorten the echoed value, corrupting the config.
                        if (!NetworkHelper.IsHost && UI.ManagerConfigPanel.IsOpen
                            && string.Equals(UI.ManagerConfigPanel.CurrentManagerId, id))
                        {
                            if (Config.ManagerVerboseLogging.Value)
                                Logger.Msg($"ApplyManagerState: {id} skipped config update (clipboard open on client)");
                            continue;
                        }

                        var existing = Active[id];

                        // Update business assignment if manager was transferred
                        if (!string.Equals(existing.BusinessPropertyCode, bizCode, StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var biz in Il2CppScheduleOne.Property.Business.OwnedBusinesses)
                            {
                                if (biz != null && string.Equals(biz.PropertyCode, bizCode, StringComparison.OrdinalIgnoreCase))
                                {
                                    existing.AssignedBusiness = biz;
                                    existing.BusinessPropertyCode = bizCode;
                                    Logger.Msg($"ApplyManagerState: {id} transferred to {bizCode}");
                                    break;
                                }
                            }
                        }

                        // Update config on existing managers (host may have changed it)
                        if (!string.IsNullOrEmpty(configStr))
                        {
                            existing.Configuration.Deserialize(DecodeConfig(configStr));
                            existing.ReconcileLockerFromConfig();
                        }
                        if (Config.ManagerVerboseLogging.Value)
                            Logger.Msg($"ApplyManagerState: {id} already in Active, updated config");
                        continue;
                    }
                    if (_pendingAdoptions.ContainsKey(id))
                    {
                        _pendingAdoptions[id].ConfigStr = configStr;
                        if (Config.ManagerVerboseLogging.Value)
                            Logger.Msg($"ApplyManagerState: {id} already pending, updated config");
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
                            NetObjId = netObjId, CreatedTime = UnityEngine.Time.time,
                            ConfigStr = configStr
                        };
                        if (Config.ManagerVerboseLogging.Value)
                            Logger.Msg($"ApplyManagerState: NPC ObjectId {netObjId} not found yet, queued adoption for {id}");
                        continue;
                    }

                    TryAdopt(id, seed, bizCode, netObjId, npc, configStr);
                }
            }

            // Remove managers from Active that are no longer in the host state (e.g. fired by host)
            var staleIds = new List<string>();
            foreach (var id in Active.Keys)
            {
                if (!incomingIds.Contains(id))
                    staleIds.Add(id);
            }
            foreach (var id in staleIds)
            {
                Logger.Msg($"ApplyManagerState: removing stale manager {id} (not in host state)");
                if (Active.TryGetValue(id, out var stale))
                {
                    stale.ClearMessages();
                    stale.Despawn();
                }
            }

            // Also remove stale pending adoptions
            var stalePending = new List<string>();
            foreach (var id in _pendingAdoptions.Keys)
            {
                if (!incomingIds.Contains(id))
                    stalePending.Add(id);
            }
            foreach (var id in stalePending)
            {
                if (Config.ManagerVerboseLogging.Value)
                    Logger.Msg($"ApplyManagerState: removing stale pending adoption {id}");
                _pendingAdoptions.Remove(id);
            }
        }

        private static void TryAdopt(string id, int seed, string bizCode, int netObjId, NPC npc, string configStr = "")
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

                // Apply config from host
                if (!string.IsNullOrEmpty(configStr))
                {
                    instance.Configuration.Deserialize(DecodeConfig(configStr));
                    instance.ReconcileLockerFromConfig();
                }

                // Send greeting text locally (host sends its own during HireManager)
                instance.SendGreeting($"Hey boss! I'm your new manager at {business.PropertyName}. Use the clipboard to assign me a locker and I'll get to work.");

                Logger.Msg($"Adopted manager {id} at {bizCode} (NetObjId={netObjId}, config={!string.IsNullOrEmpty(configStr)})");

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
                    TryAdopt(pa.Id, pa.Seed, pa.BizCode, pa.NetObjId, npc, pa.ConfigStr);
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
        NoFunds,
        Transferring
    }
}
