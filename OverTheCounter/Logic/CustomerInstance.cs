using MelonLoader;
using OverTheCounter.Logic.Placement;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.VoiceOver;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.NPCs;
using ScheduleOne.Storage;
using ScheduleOne.VoiceOver;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Wraps a game NPC as a store customer with a browse-and-leave lifecycle.
    /// </summary>
    public class CustomerInstance
    {
        private static readonly MelonLogger.Instance Logger = new("OTC:CustomerInstance");

        /// <summary>All active customer instances keyed by ID.</summary>
        public static readonly Dictionary<string, CustomerInstance> Active = new();

        // =====================================================================
        //  Identity
        // =====================================================================

        public string Id { get; }
        public int SpawnSeed { get; }
        public CustomerSpawnPoints.SpawnPoint SpawnPoint { get; }

        // =====================================================================
        //  Game reference
        // =====================================================================

        public NPC GameNpc { get; private set; }

        /// <summary>FishNet NetworkObject.ObjectId for client-side adoption.</summary>
        public int NetworkObjectId { get; set; }

        /// <summary>True if this instance was adopted from a FishNet-replicated NPC (client-side).</summary>
        public bool IsAdopted { get; private set; }

        /// <summary>Whether the underlying NPC is still alive and valid.</summary>
        public bool IsValid => GameNpc != null && GameNpc.gameObject != null;

        /// <summary>Current world position of the NPC, or null if invalid.</summary>
        public Vector3? Position => IsValid ? GameNpc.transform.position : null;

        // =====================================================================
        //  State machine
        // =====================================================================

        public CustomerState State { get; set; }
        public bool ArrivedAtDestination { get; set; }

        // Browse tracking
        private List<Vector3> _browsePositions;
        private List<Vector3> _browseShelfPositions; // original shelf centers (for facing)
        private int _browseTargetIndex;
        private float _browsePauseEndTime;
        private Vector3? _currentWalkTarget;

        private const float BrowsePauseDuration = 5f;

        // Voice line tracking (30% of customers vocalize once while browsing)
        private bool _willVocalizeWhileBrowsing;
        private bool _hasVocalized;

        // Look-around tracking (when no storage found)
        public float LookAroundEndTime { get; set; }

        /// <summary>Time.time when the customer arrived at the checkout counter.</summary>
        public float CheckoutArrivalTime { get; set; }

        /// <summary>Game hour (0-23) when the customer joined the checkout queue.</summary>
        public int CheckoutStartHour { get; set; }

        // =====================================================================
        //  Product selection (picked during browsing)
        // =====================================================================

        /// <summary>A product the customer selected from a display cabinet while browsing.</summary>
        public struct SelectedProduct
        {
            public string ProductId;
            public string PackagingId;
            public string ProductName;
            public float Price;
        }

        /// <summary>Products the customer selected while browsing display cabinets.</summary>
        public List<SelectedProduct> SelectedProducts { get; } = new();

        private const int MaxSelectedProducts = 3;

        // =====================================================================
        //  Movement (GC-pinned callbacks)
        // =====================================================================

        private GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult> _walkCallback;

        // Stuck detection — escalates avoidance priority before warping
        private Vector3? _lastStuckCheckPos;
        private float _lastStuckCheckTime;
        private float _stuckStartTime;
        private int _stuckEscalation; // 0=normal, 1=priority30, 2=priority10
        private const float StuckCheckInterval = 2f;
        private const float StuckMovementThreshold = 0.5f;
        private const float StuckWarpTime = 8f;

        // NavMesh switching (civilian ↔ employee)
        private int _savedAgentTypeID;
        private int _savedAreaMask;
        private bool _usingEmployeeNavMesh;

        // =====================================================================
        //  Constructor + Factory
        // =====================================================================

        private CustomerInstance(string id, int seed, CustomerSpawnPoints.SpawnPoint spawnPoint, NPC npc)
        {
            Id = id;
            SpawnSeed = seed;
            SpawnPoint = spawnPoint;
            GameNpc = npc;
            State = CustomerState.WalkingToStore;
            _willVocalizeWhileBrowsing = (seed % 10) < 3; // ~30% chance
        }

        /// <summary>
        /// Spawns a new customer NPC and registers it in the Active dictionary.
        /// </summary>
        public static CustomerInstance Create(string id, int seed, CustomerSpawnPoints.SpawnPoint spawnPoint)
        {
            try
            {
                var (firstName, lastName) = DrifterInstance.GetDrifterName(seed);

                var npc = NpcSpawner.SpawnCivilianNpc(
                    id, $"Customer_{id}",
                    firstName, lastName,
                    spawnPoint.Position, spawnPoint.Rotation);

                if (npc == null)
                {
                    Logger.Error($"Failed to spawn customer NPC for {id}");
                    return null;
                }

                NpcSpawner.GenerateRandomAppearance(npc, seed);
                SetVoiceDatabase(npc, seed);

                var instance = new CustomerInstance(id, seed, spawnPoint, npc);

                // Capture FishNet ObjectId for client-side adoption
                try
                {
                    var nob = npc.GetComponent<FishNet.Object.NetworkObject>();
                    if (nob != null)
                        instance.NetworkObjectId = (int)nob.ObjectId;
                }
                catch { }

                Active[id] = instance;
                return instance;
            }
            catch (Exception ex)
            {
                Logger.Error($"CustomerInstance.Create failed for {id}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Adopts an existing FishNet-replicated NPC as a customer on the client.
        /// Applies appearance without spawning a new clone.
        /// </summary>
        public static CustomerInstance Adopt(string id, int seed, CustomerSpawnPoints.SpawnPoint spawnPoint, CustomerState state, NPC existingNpc)
        {
            if (Active.ContainsKey(id))
                return Active[id];

            var (firstName, lastName) = DrifterInstance.GetDrifterName(seed);
            existingNpc.ID = id;
            existingNpc.FirstName = firstName;
            existingNpc.LastName = lastName;

            NpcSpawner.GenerateRandomAppearance(existingNpc, seed);
            SetVoiceDatabase(existingNpc, seed);

            var instance = new CustomerInstance(id, seed, spawnPoint, existingNpc)
            {
                IsAdopted = true,
                State = state
            };

            Active[id] = instance;
            return instance;
        }

        // =====================================================================
        //  Movement
        // =====================================================================

        /// <summary>
        /// Commands the NPC to walk to the given position.
        /// </summary>
        public void WalkTo(Vector3 target)
        {
            if (!IsValid) return;

            ArrivedAtDestination = false;
            _currentWalkTarget = target;

            _walkCallback = (GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult>)
                new Action<ScheduleOne.NPCs.NPCMovement.WalkResult>(result =>
                {
                    if (result == ScheduleOne.NPCs.NPCMovement.WalkResult.Success ||
                        result == ScheduleOne.NPCs.NPCMovement.WalkResult.Partial)
                        ArrivedAtDestination = true;
                });

            GameNpc.Movement.SetDestination(target, _walkCallback, 2f, 1f);

            ResetStuckState();
        }

        /// <summary>
        /// Warps the NPC instantly to the given position.
        /// </summary>
        public void WarpTo(Vector3 target)
        {
            if (!IsValid) return;

            try
            {
                GameNpc.Movement?.Warp(target);
                GameNpc.Movement?.Stop();
            }
            catch (Exception ex)
            {
                Logger.Warning($"WarpTo failed for {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if the NPC is stuck and escalates: priority 30 → priority 10 → warp.
        /// Reverts avoidance priority once movement resumes.
        /// </summary>
        public void CheckStuck()
        {
            if (!IsValid || _currentWalkTarget == null) return;
            if (ArrivedAtDestination)
            {
                ResetStuckState();
                return;
            }
            if (Time.time - _lastStuckCheckTime < StuckCheckInterval) return;

            _lastStuckCheckTime = Time.time;
            var pos = GameNpc.transform.position;

            if (_lastStuckCheckPos.HasValue)
            {
                float moved = Vector3.Distance(pos, _lastStuckCheckPos.Value);
                if (moved > StuckMovementThreshold)
                {
                    // Moving again — revert any escalation
                    ResetStuckState();
                    _lastStuckCheckPos = pos;
                    _lastStuckCheckTime = Time.time;
                    return;
                }
            }

            _lastStuckCheckPos = pos;

            // First detection — start the clock
            if (_stuckStartTime == 0f)
            {
                _stuckStartTime = Time.time;
                return;
            }

            float stuckDuration = Time.time - _stuckStartTime;

            if (stuckDuration >= StuckWarpTime)
            {
                WarpTo(_currentWalkTarget.Value);
                ArrivedAtDestination = true;
                ResetStuckState();
            }
            else if (_stuckEscalation == 0)
            {
                SetAvoidancePriority(30);
                _stuckEscalation = 1;
                ReissueMovement();
            }
            else if (_stuckEscalation == 1 && stuckDuration >= 4f)
            {
                SetAvoidancePriority(10);
                _stuckEscalation = 2;
                ReissueMovement();
            }
        }

        /// <summary>
        /// Re-sends SetDestination to the current walk target without resetting stuck state.
        /// Used after avoidance priority changes so the NavMeshAgent re-plans its path
        /// with the new priority and starts pushing through.
        /// </summary>
        private void ReissueMovement()
        {
            if (!IsValid || _currentWalkTarget == null) return;
            GameNpc.Movement.SetDestination(_currentWalkTarget.Value, _walkCallback, 2f, 1f);
        }

        /// <summary>
        /// Resets stuck tracking and reverts avoidance priority to default.
        /// </summary>
        private void ResetStuckState()
        {
            if (_stuckEscalation > 0)
            {
                SetAvoidancePriority(50);
                _stuckEscalation = 0;
            }
            _stuckStartTime = 0f;
            _lastStuckCheckPos = null;
            _lastStuckCheckTime = Time.time;
        }

        /// <summary>
        /// Re-issues movement if the NPC stopped mid-walk (pickpocket, ragdoll, etc.).
        /// </summary>
        public void EnsureMoving()
        {
            if (!IsValid || ArrivedAtDestination || _currentWalkTarget == null) return;

            try
            {
                var movement = GameNpc.Movement;
                if (movement == null) return;

                float dist = Vector3.Distance(GameNpc.transform.position, _currentWalkTarget.Value);
                if (dist > 3f && !movement.IsMoving)
                    WalkTo(_currentWalkTarget.Value);
            }
            catch { }
        }

        /// <summary>
        /// Rotates the NPC to face the given world position (Y-axis only).
        /// </summary>
        private void FacePosition(Vector3 target)
        {
            if (!IsValid) return;
            var dir = target - GameNpc.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
                GameNpc.transform.rotation = Quaternion.LookRotation(dir);
        }

        /// <summary>
        /// Faces a world position and plays the GrabItem animation (same as manager interact).
        /// </summary>
        public void FaceAndAnimate(Vector3 target)
        {
            if (!IsValid) return;
            var dir = target - GameNpc.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
                GameNpc.Movement?.FaceDirection(dir, 0.3f);
            GameNpc.SetAnimationTrigger_Networked(null, "GrabItem");
        }

        // =====================================================================
        //  NavMesh switching (civilian ↔ employee)
        // =====================================================================

        /// <summary>
        /// Switches the NPC's NavMeshAgent to employee settings for indoor navigation.
        /// Must be called near the building entrance where both surfaces overlap.
        /// </summary>
        public bool SwitchToEmployeeNavMesh()
        {
            if (_usingEmployeeNavMesh) return true;

            if (!ManagerSpawner.TryGetEmployeeNavMeshSettings(out int empAgentType, out int empAreaMask))
            {
                Logger.Warning($"{Id}: no employee NavMesh settings available");
                return false;
            }

            try
            {
                var agent = GameNpc?.Movement?.Agent;
                if (agent == null) return false;

                _savedAgentTypeID = agent.agentTypeID;
                _savedAreaMask = agent.areaMask;

                agent.agentTypeID = empAgentType;
                agent.areaMask = empAreaMask;

                // Don't Warp here — caller warps to the target position on the
                // runtime Employee NavMesh (baked and runtime are separate instances).

                _usingEmployeeNavMesh = true;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"{Id}: SwitchToEmployeeNavMesh failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Restores the NPC's NavMeshAgent to civilian settings for outdoor navigation.
        /// </summary>
        public void RestoreCivilianNavMesh()
        {
            if (!_usingEmployeeNavMesh) return;

            try
            {
                var agent = GameNpc?.Movement?.Agent;
                if (agent == null) return;

                agent.agentTypeID = _savedAgentTypeID;
                agent.areaMask = _savedAreaMask;

                GameNpc.Movement.Warp(GameNpc.transform.position);

                _usingEmployeeNavMesh = false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"{Id}: RestoreCivilianNavMesh failed: {ex.Message}");
                _usingEmployeeNavMesh = false;
            }
        }

        /// <summary>
        /// Sets the NPC's avoidance priority. Lower values = higher importance
        /// (pushes other agents aside). Default civilian NPCs are ~50.
        /// </summary>
        public void SetAvoidancePriority(int priority)
        {
            try
            {
                var agent = GameNpc?.Movement?.Agent;
                if (agent != null)
                    agent.avoidancePriority = priority;
            }
            catch { }
        }

        // =====================================================================
        //  Browsing
        // =====================================================================

        /// <summary>
        /// Initializes the browse phase with stand positions (in front of shelves)
        /// and shelf centers (for facing). Each stand position gets a random XZ offset
        /// so multiple customers don't compete for the exact same spot.
        /// </summary>
        private const float BrowseRadius = 0.8f;

        public void StartBrowsing(List<Vector3> standPositions, List<Vector3> shelfCenters)
        {
            _browseShelfPositions = new List<Vector3>(shelfCenters);
            _browsePositions = new List<Vector3>(standPositions.Count);
            for (int i = 0; i < standPositions.Count; i++)
            {
                var offset = UnityEngine.Random.insideUnitCircle * BrowseRadius;
                _browsePositions.Add(standPositions[i] + new Vector3(offset.x, 0f, offset.y));
            }

            _browseTargetIndex = 0;
            _browsePauseEndTime = 0f;
            ArrivedAtDestination = false;

            if (_browsePositions.Count > 0)
                WalkTo(_browsePositions[0]);
        }

        /// <summary>
        /// Advances the browse state machine. Returns true when all targets have been visited.
        /// </summary>
        public bool TickBrowsing()
        {
            if (_browsePositions == null || _browsePositions.Count == 0)
                return true;

            // Currently pausing at a storage entity
            if (_browsePauseEndTime > 0f)
            {
                if (Time.time < _browsePauseEndTime)
                    return false;

                // Pause ended — advance to next target
                _browsePauseEndTime = 0f;
                _browseTargetIndex++;

                if (_browseTargetIndex >= _browsePositions.Count)
                    return true;

                ArrivedAtDestination = false;
                WalkTo(_browsePositions[_browseTargetIndex]);
                return false;
            }

            // Arrived at current target — face the shelf and start pause
            if (ArrivedAtDestination)
            {
                if (_browseShelfPositions != null && _browseTargetIndex < _browseShelfPositions.Count)
                    FacePosition(_browseShelfPositions[_browseTargetIndex]);
                _browsePauseEndTime = Time.time + BrowsePauseDuration;

                // Pick a product from this shelf
                PickProductFromShelf();

                // Occasional "hmm" while looking at products (30% of customers, once per visit)
                if (_willVocalizeWhileBrowsing && !_hasVocalized)
                {
                    _hasVocalized = true;
                    try { GameNpc?.VoiceOverEmitter?.Play(EVOLineType.Think); }
                    catch { }
                }

                return false;
            }

            return false;
        }

        /// <summary>
        /// Scans the display cabinet nearest to the current browse shelf position
        /// and picks one random packaged product to add to SelectedProducts.
        /// </summary>
        private void PickProductFromShelf()
        {
            if (SelectedProducts.Count >= MaxSelectedProducts) return;
            if (_browseShelfPositions == null || _browseTargetIndex >= _browseShelfPositions.Count) return;

            var shelfPos = _browseShelfPositions[_browseTargetIndex];

            try
            {
                // Find the closest display cabinet storage entity to this shelf position
                StorageEntity closestStorage = null;
                float closestDist = float.MaxValue;

                foreach (var kvp in BuildingGridFactory.GridContainers)
                {
                    var root = kvp.Value;
                    if (root == null) continue;

                    var storages = root.GetComponentsInChildren<StorageEntity>(true);
                    if (storages == null) continue;

                    for (int i = 0; i < storages.Length; i++)
                    {
                        var storage = storages[i];
                        if (storage?.transform == null) continue;

                        float dist = Vector3.Distance(storage.transform.position, shelfPos);
                        if (dist < closestDist)
                        {
                            closestDist = dist;
                            closestStorage = storage;
                        }
                    }
                }

                if (closestStorage?.ItemSlots == null) return;

                // Collect all packaged products in this storage
                var candidates = new List<SelectedProduct>();
                for (int j = 0; j < closestStorage.ItemSlots.Count; j++)
                {
                    var slot = closestStorage.ItemSlots[j];
                    if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

#if IL2CPP
                    var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
#else
                    var productItem = slot.ItemInstance as ProductItemInstance;
#endif
                    if (productItem == null || productItem.AppliedPackaging == null) continue;

                    string productId = null;
                    string productName = "Product";
                    float price = 0f;

                    try
                    {
#if IL2CPP
                        var prodDef = productItem.Definition?.TryCast<ProductDefinition>();
#else
                        var prodDef = productItem.Definition as ProductDefinition;
#endif
                        if (prodDef != null)
                        {
                            productId = prodDef.ID;
                            productName = prodDef.name ?? "Product";
                            price = prodDef.Price > 0 ? prodDef.Price : prodDef.MarketValue;
                        }
                    }
                    catch { }

                    candidates.Add(new SelectedProduct
                    {
                        ProductId = productId,
                        PackagingId = productItem.AppliedPackaging?.ID,
                        ProductName = productName,
                        Price = price
                    });
                }

                if (candidates.Count == 0) return;

                // Pick one random product from this shelf
                var picked = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                SelectedProducts.Add(picked);
            }
            catch (Exception ex)
            {
                Logger.Warning($"PickProductFromShelf failed for {Id}: {ex.Message}");
            }
        }

        // =====================================================================
        //  Voice setup
        // =====================================================================

        private static void SetVoiceDatabase(NPC npc, int seed)
        {
            try
            {
                var emitter = npc.VoiceOverEmitter;
                if (emitter == null) return;

                var empMgr = NetworkSingleton<EmployeeManager>.Instance;
                if (empMgr == null) return;

                bool isMale = NpcSpawner.DetermineGender(seed) < 0.5f;
                var voiceDb = empMgr.GetVoice(isMale, Math.Abs(seed % 100));
                emitter.SetDatabase(voiceDb, true);

                // Vary pitch slightly based on seed (same approach as Employee.cs)
                float basePitch = isMale ? 0.8f : 1.3f;
                float variation = 0.2f;
                float offset = -variation / 2f + Mathf.Clamp01((seed % 10) / 10f) * variation;
                emitter.PitchMultiplier = basePitch + offset;
            }
            catch (Exception ex)
            {
                Logger.Warning($"SetVoiceDatabase failed: {ex.Message}");
            }
        }

        // =====================================================================
        //  Cleanup
        // =====================================================================

        /// <summary>
        /// Removes this customer from tracking and destroys the NPC.
        /// </summary>
        public void Despawn()
        {
            Active.Remove(Id);

            RestoreCivilianNavMesh();

            if (GameNpc != null)
            {
                if (!IsAdopted)
                    NpcSpawner.Despawn(GameNpc);
                GameNpc = null;
            }
        }

        /// <summary>
        /// Despawns all active customers. Called on scene transitions.
        /// </summary>
        public static void CleanupAll()
        {
            foreach (var customer in Active.Values)
            {
                if (customer.GameNpc != null)
                {
                    if (!customer.IsAdopted)
                        NpcSpawner.Despawn(customer.GameNpc);
                    customer.GameNpc = null;
                }
            }
            Active.Clear();
        }
    }
}
