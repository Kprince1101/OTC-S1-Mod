using MelonLoader;
using OverTheCounter.Logic.Placement;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Effects;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.VoiceOver;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Effects;
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
        //  Preferences
        // =====================================================================

        public struct CustomerPreferences
        {
            public float QualityExpectation;    // 0.0 (Trash) to 0.75 (Premium)
            public string[] PreferredEffectIds; // 3 effect ID strings (lowercased ScriptableObject names)
            public float MaxBudgetPerItem;      // max $ they'll spend on a single product
            public float WeedAffinity;          // -1 to 1, drug type affinity
        }

        // Effect IDs loaded dynamically from game resources (same as vanilla's RandomizeFavouriteEffects)
        private static string[] _allEffectIds;

        /// <summary>
        /// Loads all effect IDs from game resources (Properties/Tier1..5), matching
        /// vanilla's CustomerData.RandomizeFavouriteEffects approach.
        /// </summary>
        private static string[] GetAllEffectIds()
        {
            if (_allEffectIds != null) return _allEffectIds;

            try
            {
                var effects = new List<string>();
                for (int tier = 1; tier <= 5; tier++)
                {
                    var loaded = Resources.LoadAll<Effect>($"Properties/Tier{tier}");
                    if (loaded == null) continue;
                    for (int i = 0; i < loaded.Length; i++)
                    {
                        if (loaded[i] != null)
                            effects.Add(loaded[i].name.ToLower());
                    }
                }

                if (effects.Count > 0)
                {
                    _allEffectIds = effects.ToArray();
                    return _allEffectIds;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load effect IDs from resources: {ex.Message}");
            }

            // Fallback — should never be needed, but just in case resources aren't ready
            Logger.Warning("[PREF] Using hardcoded effect ID fallback");
            _allEffectIds = new[]
            {
                "antigravity", "athletic", "balding", "brighteyed", "calming",
                "caloriedense", "cyclopean", "disorienting", "electrifying", "energizing",
                "euphoric", "explosive", "focused", "foggy", "gingeritis",
                "glowie", "jennerising", "laxative", "lethal", "longfaced",
                "munchies", "paranoia", "refreshing", "schizophrenic", "sedating",
                "seizure", "shrinking", "slippery", "smelly", "sneaky",
                "spicy", "thoughtprovoking", "toxic", "tropicthunder", "zombifying"
            };
            return _allEffectIds;
        }

        // =====================================================================
        //  Identity
        // =====================================================================

        public string Id { get; }
        public int SpawnSeed { get; }
        public CustomerSpawnPoints.SpawnPoint SpawnPoint { get; }
        public CustomerPreferences Preferences { get; private set; }

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
            public int QualityLevel; // 0=Trash, 1=Poor, 2=Standard, 3=Premium, 4=Heavenly
            public int Quantity;     // how many units to buy
        }

        /// <summary>Products the customer decided to buy after browsing all shelves.</summary>
        public List<SelectedProduct> SelectedProducts { get; } = new();

        /// <summary>A product observed on a display shelf during browsing.</summary>
        private struct ObservedProduct
        {
            public string ProductId;
            public string PackagingId;
            public string ProductName;
            public float Price;
            public float MarketValue;
            public int QualityLevel;
            public int AvailableQuantity;
            public List<string> EffectIds;
        }
        private readonly List<ObservedProduct> _seenProducts = new();

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
            Preferences = GeneratePreferences(seed);
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

        /// <summary>
        /// Generates deterministic customer preferences from the spawn seed.
        /// Quality skewed low (shack clientele), effects picked from all 35, budget $15-$60.
        /// </summary>
        private static CustomerPreferences GeneratePreferences(int seed)
        {
            var rng = new System.Random(seed);

            // Quality expectation — low-end shack distribution
            double qualBucket = rng.NextDouble();
            float qualityExpectation;
            if (qualBucket < 0.30)
                qualityExpectation = (float)(rng.NextDouble() * 0.12);         // VeryLow (Trash)
            else if (qualBucket < 0.65)
                qualityExpectation = 0.13f + (float)(rng.NextDouble() * 0.17); // Low (Poor)
            else if (qualBucket < 0.90)
                qualityExpectation = 0.31f + (float)(rng.NextDouble() * 0.24); // Moderate (Standard)
            else
                qualityExpectation = 0.56f + (float)(rng.NextDouble() * 0.19); // High (Premium)

            // Pick 3 random effects from all 35 (Fisher-Yates partial shuffle)
            var allEffects = GetAllEffectIds();
            var pool = new string[allEffects.Length];
            Array.Copy(allEffects, pool, allEffects.Length);
            for (int i = 0; i < 3; i++)
            {
                int j = i + rng.Next(pool.Length - i);
                var tmp = pool[i];
                pool[i] = pool[j];
                pool[j] = tmp;
            }

            // Budget: $40-$100 (OG Kush base is ~$38/g before markup)
            float budget = 40f + (float)(rng.NextDouble() * 60.0);

            return new CustomerPreferences
            {
                QualityExpectation = qualityExpectation,
                PreferredEffectIds = new[] { pool[0], pool[1], pool[2] },
                MaxBudgetPerItem = budget,
                WeedAffinity = 0.8f
            };
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

            _seenProducts.Clear();
            SelectedProducts.Clear();
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

                // Memorize products on this shelf (decisions come after all shelves visited)
                ObserveShelf();

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
        /// and memorizes all products. No scoring — just observing.
        /// </summary>
        private void ObserveShelf()
        {
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

                    try
                    {
#if IL2CPP
                        var prodDef = productItem.Definition?.TryCast<ProductDefinition>();
#else
                        var prodDef = productItem.Definition as ProductDefinition;
#endif
                        if (prodDef == null) continue;

                        var effectIds = new List<string>();
                        if (prodDef.Properties != null)
                        {
                            for (int ei = 0; ei < prodDef.Properties.Count; ei++)
                            {
                                var e = prodDef.Properties[ei];
                                if (e != null) effectIds.Add(e.name.ToLower());
                            }
                        }

                        _seenProducts.Add(new ObservedProduct
                        {
                            ProductId = prodDef.ID,
                            PackagingId = productItem.AppliedPackaging?.ID,
                            ProductName = prodDef.name ?? "Product",
                            Price = prodDef.Price > 0 ? prodDef.Price : prodDef.MarketValue,
                            MarketValue = prodDef.MarketValue,
                            QualityLevel = (int)productItem.Quality,
                            AvailableQuantity = slot.Quantity,
                            EffectIds = effectIds
                        });
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"ObserveShelf failed for {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// After browsing all shelves, scores all observed products using vanilla's
        /// GetProductEnjoyment formula and selects what to buy via weighted random.
        /// </summary>
        public void DecidePurchases()
        {
            if (_seenProducts.Count == 0)
            {
                Logger.Msg($"[PREF] {Id} saw no products while browsing");
                return;
            }

            // 1. Deduplicate by ProductId — keep best quality, sum available quantity
            var unique = new Dictionary<string, ObservedProduct>();
            foreach (var obs in _seenProducts)
            {
                if (unique.TryGetValue(obs.ProductId, out var existing))
                {
                    var updated = obs.QualityLevel > existing.QualityLevel ? obs : existing;
                    updated.AvailableQuantity = existing.AvailableQuantity + obs.AvailableQuantity;
                    unique[obs.ProductId] = updated;
                }
                else
                {
                    unique[obs.ProductId] = obs;
                }
            }

            // 2. Score each unique product using vanilla GetProductEnjoyment formula
            var scored = new List<(ObservedProduct product, float appeal)>();
            var rng = new System.Random(SpawnSeed + 7919); // deterministic but different from pref gen

            Logger.Msg($"[PREF] {Id} deciding purchases ({unique.Count} unique products) | QualExp={Preferences.QualityExpectation:F2} Effects=[{string.Join(",", Preferences.PreferredEffectIds)}] Budget=${Preferences.MaxBudgetPerItem:F0}");

            foreach (var obs in unique.Values)
            {
                // --- Vanilla GetProductEnjoyment ---
                // Drug affinity * 0.3
                float drugScore = Preferences.WeedAffinity * 0.3f;

                // Effect match: (matchCount / preferredCount) * 0.4
                int matchCount = 0;
                var matchedEffects = new List<string>();
                foreach (string wanted in Preferences.PreferredEffectIds)
                {
                    for (int i = 0; i < obs.EffectIds.Count; i++)
                    {
                        if (obs.EffectIds[i] == wanted)
                        {
                            matchCount++;
                            matchedEffects.Add(wanted);
                            break;
                        }
                    }
                }
                float effectScore = (float)matchCount / Preferences.PreferredEffectIds.Length * 0.4f;

                // Quality delta (stepped) * 0.3
                float qualityScalar = obs.QualityLevel * 0.25f;
                float qualityDelta = qualityScalar - Preferences.QualityExpectation;
                float qualityStep;
                if (qualityDelta >= 0.25f) qualityStep = 1.0f;
                else if (qualityDelta >= 0f) qualityStep = 0.5f;
                else if (qualityDelta >= -0.25f) qualityStep = -0.5f;
                else qualityStep = -1.0f;
                float qualityScore = qualityStep * 0.3f;

                float rawEnjoyment = drugScore + effectScore + qualityScore;

                // Normalize: InverseLerp(-0.6, 1.0, rawScore) → 0–1
                float enjoyment = Mathf.InverseLerp(-0.6f, 1.0f, rawEnjoyment);

                // --- Vanilla price factor ---
                // priceRatio = price / marketValue
                // priceScalar = Lerp(1, -1, priceRatio / 2) → cheap=+1, expensive=-1
                float marketVal = obs.MarketValue > 0 ? obs.MarketValue : obs.Price;
                float priceRatio = obs.Price / marketVal;
                float priceScalar = Mathf.Lerp(1f, -1f, priceRatio / 2f);

                float appeal = enjoyment + priceScalar;

                Logger.Msg($"[PREF]   {obs.ProductName}({obs.ProductId}) Q={obs.QualityLevel} ${obs.Price:F0} mv=${marketVal:F0} fx=[{string.Join(",", obs.EffectIds)}] | drug={drugScore:F2} eff={effectScore:F2}(matched:[{string.Join(",", matchedEffects)}]) qual={qualityScore:F2} => enjoy={enjoyment:F3} price={priceScalar:F2} appeal={appeal:F3}");

                // Budget hard cutoff
                if (obs.Price > Preferences.MaxBudgetPerItem) continue;

                if (appeal > 0f)
                    scored.Add((obs, appeal));
            }

            if (scored.Count == 0)
            {
                Logger.Msg($"[PREF]   => NO PRODUCTS APPEALING");
                return;
            }

            // 3. Sort by appeal descending
            scored.Sort((a, b) => b.appeal.CompareTo(a.appeal));

            // 4. Weighted random selection (vanilla: 50% pick top, 50% random from rest)
            int totalUnitCap = 4;
            int totalUnits = 0;
            var remaining = new List<(ObservedProduct product, float appeal)>(scored);

            while (remaining.Count > 0 && totalUnits < totalUnitCap)
            {
                int pickIndex;
                if (remaining.Count == 1 || rng.NextDouble() < 0.5)
                    pickIndex = 0; // top pick
                else
                    pickIndex = 1 + rng.Next(remaining.Count - 1); // random from rest

                var pick = remaining[pickIndex];
                remaining.RemoveAt(pickIndex);

                // High appeal (> 0.7) → buy 2 if available, else 1
                int qty = 1;
                if (pick.appeal > 0.7f && pick.product.AvailableQuantity >= 2)
                    qty = 2;
                qty = Math.Min(qty, totalUnitCap - totalUnits);

                Logger.Msg($"[PREF]   => PICKED: {pick.product.ProductName} x{qty} (appeal={pick.appeal:F3})");

                SelectedProducts.Add(new SelectedProduct
                {
                    ProductId = pick.product.ProductId,
                    PackagingId = pick.product.PackagingId,
                    ProductName = pick.product.ProductName,
                    Price = pick.product.Price,
                    QualityLevel = pick.product.QualityLevel,
                    Quantity = qty
                });

                totalUnits += qty;
            }

            Logger.Msg($"[PREF]   => TOTAL: {SelectedProducts.Count} products, {totalUnits} units");
        }

        // =====================================================================
        //  Voice lines
        // =====================================================================

        private static readonly string[] DisappointedLines =
        {
            "Nothing for me...",
            "Not what I'm looking for.",
            "I'll pass.",
            "Maybe next time.",
            "Nah, I'm good."
        };

        /// <summary>
        /// Shows a disappointed speech bubble and plays an annoyed voice line
        /// when the customer leaves without buying anything.
        /// </summary>
        public void ShowDisappointed()
        {
            if (!IsValid) return;
            try
            {
                var rng = new System.Random(SpawnSeed + 42);
                string line = DisappointedLines[rng.Next(DisappointedLines.Length)];
                GameNpc.DialogueHandler?.WorldspaceRend?.ShowText(line, 3f);
                GameNpc.VoiceOverEmitter?.Play(EVOLineType.Annoyed);
            }
            catch { }
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
