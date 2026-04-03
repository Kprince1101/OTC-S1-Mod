using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Effects;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.VoiceOver;
using Customer = Il2CppScheduleOne.Economy.Customer;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Economy;
using ScheduleOne.Effects;
using ScheduleOne.Employees;
using ScheduleOne.NPCs;
using ScheduleOne.Storage;
using ScheduleOne.VoiceOver;
using Customer = ScheduleOne.Economy.Customer;
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
            public float TotalOrderBudget;      // total $ to spend this visit (deal customers only, 0 = uncapped)
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
                OTCLog.Warning(OTCLog.Systems.Customer,$"Failed to load effect IDs from resources: {ex.Message}");
            }

            // Fallback — should never be needed, but just in case resources aren't ready
            OTCLog.Warning(OTCLog.Systems.Customer,"[PREF] Using hardcoded effect ID fallback");
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

        /// <summary>0.0 to 1.0 — how many products the budtender recommends.
        /// Random customers: seeded random. Deal customers: vanilla relationship level.</summary>
        public float Familiarity { get; private set; }

        // =====================================================================
        //  Building target
        // =====================================================================

        /// <summary>The building this customer is visiting. Defaults to WestvilleShack for random customers.</summary>
        internal BuildingTarget Target { get; private set; }

        // =====================================================================
        //  Deal customer fields
        // =====================================================================

        /// <summary>True if this customer was redirected from the vanilla deal system.</summary>
        public bool IsDealCustomer { get; private set; }

        /// <summary>The vanilla Customer component (set for deal customers only).</summary>
        public Customer VanillaCustomer { get; private set; }

        /// <summary>Position the NPC warped from — walk back here after exiting (deal customers).</summary>
        public Vector3? WarpReturnPosition { get; private set; }

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
        internal IReadOnlyList<Vector3> BrowseShelfPositions => _browseShelfPositions;
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

        /// <summary>Full HHMM game time when the customer joined the checkout queue (minute precision).</summary>
        public int CheckoutStartTime { get; set; }

        /// <summary>The counter this customer is queued at, or null.</summary>
        internal CheckoutCounterInstance AssignedCounter { get; set; }

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
            public List<string> EffectIds; // effect IDs on this product (for tip calculation)
        }

        /// <summary>Products the customer decided to buy after browsing all shelves.</summary>
        public List<SelectedProduct> SelectedProducts { get; } = new();

        /// <summary>A product observed on a display shelf during browsing.</summary>
        private struct ObservedProduct
        {
            public string ProductId;
            public string PackagingId;
            public string ProductName;
            public float Price;           // per-unit price
            public float MarketValue;     // per-unit market value
            public int QualityLevel;
            public int AvailableQuantity; // packages on shelf (converted to units during dedup)
            public int PkgMultiplier;     // units per package (baggie=1, jar=5, brick=20)
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

        // =====================================================================
        //  Constructor + Factory
        // =====================================================================

        private CustomerInstance(string id, int seed, CustomerSpawnPoints.SpawnPoint spawnPoint, NPC npc,
            BuildingTarget target = null)
        {
            Id = id;
            SpawnSeed = seed;
            SpawnPoint = spawnPoint;
            GameNpc = npc;
            Target = target ?? WestvilleShack.Target;
            State = CustomerState.WalkingToStore;
            Preferences = GeneratePreferences(seed);
            Familiarity = (float)new System.Random(seed + 5501).NextDouble();
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
                    OTCLog.Error(OTCLog.Systems.Customer,$"Failed to spawn customer NPC for {id}");
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
                OTCLog.Error(OTCLog.Systems.Customer,$"CustomerInstance.Create failed for {id}: {ex.Message}");
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
        /// Wraps an existing vanilla NPC (e.g., Jesse, Beth) as a deal-driven dispensary customer.
        /// Does NOT spawn a new NPC or change appearance — keeps the real NPC's identity.
        /// </summary>
        internal static CustomerInstance CreateFromDealNPC(Customer vanillaCustomer, BuildingTarget target,
            Vector3 warpReturnPosition)
        {
            try
            {
                var npc = vanillaCustomer.NPC;
                if (npc == null)
                {
                    OTCLog.Error(OTCLog.Systems.Customer, "CreateFromDealNPC: vanilla Customer has no NPC");
                    return null;
                }

                int seed = npc.FirstName.GetHashCode();
                string id = $"deal_{npc.FirstName}_{npc.LastName}_{UnityEngine.Random.Range(0, 9999)}";

                // Extract relationship as familiarity (0-1 range)
                float familiarity = 0.5f;
                try { familiarity = Mathf.Clamp01(vanillaCustomer.NPC.RelationData.RelationDelta / 5f); }
                catch { }

                var instance = new CustomerInstance(id, seed, null, npc, target)
                {
                    IsDealCustomer = true,
                    VanillaCustomer = vanillaCustomer,
                    WarpReturnPosition = warpReturnPosition,
                    Preferences = ExtractVanillaPreferences(vanillaCustomer),
                    Familiarity = familiarity
                };

                Active[id] = instance;
                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"Deal customer created: {npc.FirstName} {npc.LastName} → {target.Name}");
                return instance;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Customer, $"CreateFromDealNPC failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extracts customer preferences from a vanilla Customer's affinity and standard data.
        /// </summary>
        private static CustomerPreferences ExtractVanillaPreferences(Customer customer)
        {
            try
            {
#if IL2CPP
                var data = customer.customerData;
#else
                var dataField = HarmonyLib.AccessTools.Field(typeof(Customer), "customerData");
                var data = dataField?.GetValue(customer) as ScheduleOne.Economy.CustomerData;
#endif
                if (data == null) return GeneratePreferences(0);

                // Quality expectation from customer standards
                float qualityExpectation = 0.3f; // default: Standard
#if IL2CPP
                qualityExpectation = ScheduleOne.Economy.CustomerData.GetQualityScalar(
                    data.Standards.GetCorrespondingQuality());
#else
                var getQualMethod = typeof(ScheduleOne.Economy.CustomerData).GetMethod("GetQualityScalar",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var corrQual = data.Standards.GetCorrespondingQuality();
                if (getQualMethod != null)
                    qualityExpectation = (float)getQualMethod.Invoke(null, new object[] { corrQual });
#endif

                // Preferred effects from customerData.PreferredProperties
                var effectIds = new List<string>();
                if (data.PreferredProperties != null)
                {
                    for (int i = 0; i < data.PreferredProperties.Count && i < 3; i++)
                    {
                        var effect = data.PreferredProperties[i];
                        if (effect != null)
                            effectIds.Add(effect.name.ToLower());
                    }
                }
                // Pad to 3 effects if less
                while (effectIds.Count < 3)
                    effectIds.Add("calming");

                // Vanilla spend scaling: Lerp(Min, Max, relationship) × rankMultiplier / ordersPerWeek
                float normalizedRelation = 0.4f; // default (RelationDelta 2.0 / 5.0)
                try { normalizedRelation = customer.NPC.RelationData.RelationDelta / 5f; }
                catch { }

                float weeklyBase = Mathf.Lerp(data.MinWeeklySpend, data.MaxWeeklySpend, normalizedRelation);

                float rankMultiplier = 1f;
                try
                {
                    if (S1API.Leveling.LevelManager.Exists)
                        rankMultiplier = S1API.Leveling.LevelManager.GetOrderLimitMultiplier(
                            S1API.Leveling.LevelManager.CurrentRank);
                }
                catch { }

                float scaledWeekly = weeklyBase * rankMultiplier;
                float totalBudget = scaledWeekly / Mathf.Max(1, data.MaxOrdersPerWeek);
                float budget = totalBudget;

                // Weed affinity from current affinity data
                float weedAffinity = 0.5f;
                try
                {
#if IL2CPP
                    weedAffinity = customer.currentAffinityData.GetAffinity(
                        Il2CppScheduleOne.Product.EDrugType.Marijuana);
#else
                    var affinityField = HarmonyLib.AccessTools.Field(typeof(Customer), "currentAffinityData");
                    var affinityData = affinityField?.GetValue(customer) as ScheduleOne.Economy.CustomerAffinityData;
                    if (affinityData != null)
                        weedAffinity = affinityData.GetAffinity(ScheduleOne.Product.EDrugType.Marijuana);
#endif
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer, $"Failed to read weed affinity: {ex.Message}");
                }

                return new CustomerPreferences
                {
                    QualityExpectation = qualityExpectation,
                    PreferredEffectIds = effectIds.ToArray(),
                    MaxBudgetPerItem = budget,
                    TotalOrderBudget = totalBudget,
                    WeedAffinity = weedAffinity
                };
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"ExtractVanillaPreferences failed: {ex.Message}");
                return GeneratePreferences(0);
            }
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

            // Budget: $40-$100 base, scaled by player rank
            float budget = 40f + (float)(rng.NextDouble() * 60.0);
            float rankMultiplier = 1f;
            try
            {
                if (S1API.Leveling.LevelManager.Exists)
                    rankMultiplier = S1API.Leveling.LevelManager.GetOrderLimitMultiplier(
                        S1API.Leveling.LevelManager.CurrentRank);
            }
            catch { }
            budget *= rankMultiplier;

            return new CustomerPreferences
            {
                QualityExpectation = qualityExpectation,
                PreferredEffectIds = new[] { pool[0], pool[1], pool[2] },
                MaxBudgetPerItem = budget,
                TotalOrderBudget = budget,
                WeedAffinity = 0.8f
            };
        }

        // =====================================================================
        //  Movement
        // =====================================================================

        /// <summary>
        /// Commands the NPC to walk to the given world position on EXTERIOR NavMesh.
        /// Use only for WalkingToStore and LeavingStore (outside the building).
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
        /// Sends the NPC to a LOCAL building position via S1MAPI NavigationBuilder.
        /// Handles doorway entry, A* interior pathing, and arrival callback.
        /// </summary>
        public void SendToInterior(Vector3 localTarget, Action onArrival = null)
        {
            if (!IsValid) return;

            var nav = Target?.NavBuilder;
            if (nav == null)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"SendToInterior: NavBuilder is null for {Id}");
                return;
            }

            ArrivedAtDestination = false;
            _currentWalkTarget = null; // prevent CheckStuck/EnsureMoving from fighting S1MAPI
            OTCLog.Msg(OTCLog.Systems.Customer, $"[Nav] SendToInterior {Id} localTarget={localTarget} worldPos={Position} state={State}");
            nav.SendNPCToPosition(GameNpc.Movement, localTarget, () =>
            {
                OTCLog.Msg(OTCLog.Systems.Customer, $"[Nav] SendToInterior ARRIVED {Id} worldPos={Position} state={State}");
                onArrival?.Invoke();
            });
        }

        /// <summary>
        /// Recalls the NPC from the building via S1MAPI NavigationBuilder.
        /// NPC exits through the doorway and is released back to exterior NavMesh.
        /// </summary>
        public void RecallFromBuilding()
        {
            if (!IsValid) return;

            var nav = Target?.NavBuilder;
            if (nav == null) return;

            _currentWalkTarget = null; // prevent CheckStuck/EnsureMoving from fighting S1MAPI

            // Snap rotation toward exit so NPC doesn't walk backward while S1MAPI rotates them
            if (Target != null && Target.ExteriorApproachPosition != Vector3.zero)
                FacePosition(Target.ExteriorApproachPosition);

            OTCLog.Msg(OTCLog.Systems.Customer, $"[Nav] RecallFromBuilding {Id} worldPos={Position} state={State}");
            nav.RecallNPC(GameNpc.Movement);
        }

        /// <summary>
        /// Re-sends the customer to a world position after a nav rebuild.
        /// Called by SafeRebuildNavigation to restore NPC position after S1MAPI's
        /// Rebuild() releases all tracked NPCs.
        /// </summary>
        internal void ResendToPosition(Vector3 worldPos)
        {
            var nav = Target?.NavBuilder;
            if (nav == null || GameNpc?.Movement == null) return;

            var localTarget = nav.WorldToLocal(worldPos);
            nav.SendNPCToPosition(GameNpc.Movement, localTarget, null);
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
                OTCLog.Warning(OTCLog.Systems.Customer,$"WarpTo failed for {Id}: {ex.Message}");
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

        public void FaceToward(Vector3 target)
        {
            if (!IsValid) return;
            var dir = target - GameNpc.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
                GameNpc.Movement?.FaceDirection(dir, 0.3f);
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

        private int _savedAvoidanceType = -1;

        /// <summary>
        /// Disables NavMeshAgent obstacle avoidance so NPCs don't block each other
        /// in tight building interiors. Saves the current type for restoration.
        /// </summary>
        public void DisableObstacleAvoidance()
        {
            try
            {
                var agent = GameNpc?.Movement?.Agent;
                if (agent == null) return;
                _savedAvoidanceType = (int)agent.obstacleAvoidanceType;
                agent.obstacleAvoidanceType = UnityEngine.AI.ObstacleAvoidanceType.NoObstacleAvoidance;
            }
            catch { }
        }

        /// <summary>
        /// Restores the NPC's obstacle avoidance type to what it was before entering the building.
        /// </summary>
        public void RestoreObstacleAvoidance()
        {
            try
            {
                var agent = GameNpc?.Movement?.Agent;
                if (agent == null || _savedAvoidanceType < 0) return;
                agent.obstacleAvoidanceType = (UnityEngine.AI.ObstacleAvoidanceType)_savedAvoidanceType;
                _savedAvoidanceType = -1;
            }
            catch { }
        }

        // =====================================================================
        //  Browsing
        // =====================================================================

        /// <summary>
        /// Initializes the browse phase with LOCAL stand positions (in front of shelves)
        /// and LOCAL shelf centers (for facing). Each stand position gets a random XZ offset
        /// so multiple customers don't compete for the exact same spot.
        /// Positions are in building-local coordinates for SendNPCToPosition.
        /// </summary>
        private const float BrowseRadius = 0.3f;

        public void StartBrowsing(List<Vector3> localStandPositions, List<Vector3> localShelfCenters)
        {
            _browseShelfPositions = new List<Vector3>(localShelfCenters);
            _browsePositions = new List<Vector3>(localStandPositions.Count);
            for (int i = 0; i < localStandPositions.Count; i++)
            {
                var offset = UnityEngine.Random.insideUnitCircle * BrowseRadius;
                _browsePositions.Add(localStandPositions[i] + new Vector3(offset.x, 0f, offset.y));
            }

            _seenProducts.Clear();
            SelectedProducts.Clear();
            _browseTargetIndex = 0;
            _browsePauseEndTime = 0f;
            ArrivedAtDestination = false;

            if (_browsePositions.Count > 0)
                SendToInteriorBrowseTarget(0);
        }

        private void SendToInteriorBrowseTarget(int index)
        {
            _browseTargetIndex = index;
            SendToInterior(_browsePositions[index], OnBrowsePositionReached);
        }

        private void OnBrowsePositionReached()
        {
            ArrivedAtDestination = true;
        }

        /// <summary>
        /// Advances the browse state machine. Returns true when all targets have been visited.
        /// Arrival is detected via callback from SendNPCToPosition, not polling.
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
                int next = _browseTargetIndex + 1;

                if (next >= _browsePositions.Count)
                    return true;

                ArrivedAtDestination = false;
                SendToInteriorBrowseTarget(next);
                return false;
            }

            // Arrived at current target — face the shelf and start pause
            if (ArrivedAtDestination)
            {
                // Stop the NPC so they stand still during the pause (prevents twitching)
                try { GameNpc.Movement?.Stop(); } catch { }

                // Convert local shelf center to world for FacePosition
                if (_browseShelfPositions != null && _browseTargetIndex < _browseShelfPositions.Count)
                {
                    var bt = Target?.BuildingTransform;
                    if (bt != null)
                        FacePosition(bt.TransformPoint(_browseShelfPositions[_browseTargetIndex]));
                }
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
        /// (Legacy — used only if browsing is ever re-enabled.)
        /// </summary>
        private void ObserveShelf()
        {
            if (_browseShelfPositions == null || _browseTargetIndex >= _browseShelfPositions.Count) return;

            var localShelfPos = _browseShelfPositions[_browseTargetIndex];
            var bt = Target?.BuildingTransform;
            if (bt == null) return;
            var worldShelfPos = bt.TransformPoint(localShelfPos);

            try
            {
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

                        float dist = Vector3.Distance(storage.transform.position, worldShelfPos);
                        if (dist < closestDist)
                        {
                            closestDist = dist;
                            closestStorage = storage;
                        }
                    }
                }

                if (closestStorage != null)
                    ScanStorageEntity(closestStorage);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"ObserveShelf failed for {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Scans a single StorageEntity and adds all packaged products to _seenProducts.
        /// </summary>
        private void ScanStorageEntity(StorageEntity storage)
        {
            if (storage?.ItemSlots == null) return;

            for (int j = 0; j < storage.ItemSlots.Count; j++)
            {
                var slot = storage.ItemSlots[j];
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

                    // Skip products the player has disabled from selling
                    if (PricingSaveData.Instance != null &&
                        PricingSaveData.Instance.IsSellingDisabled(prodDef.ID))
                        continue;

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
                        Price = PricingSaveData.Instance?.GetPrice(prodDef) ?? prodDef.MarketValue,
                        MarketValue = prodDef.MarketValue,
                        QualityLevel = (int)productItem.Quality,
                        AvailableQuantity = slot.Quantity,
                        PkgMultiplier = productItem.AppliedPackaging.Quantity,
                        EffectIds = effectIds
                    });
                }
                catch { }
            }
        }

        /// <summary>
        /// Scans a list of storage entities and memorizes all products.
        /// Used by the budtender recommendation system instead of physical browsing.
        /// </summary>
        internal void ObserveFromStorageList(List<(StorageEntity entity, Vector3 worldPos)> storages)
        {
            _seenProducts.Clear();
            SelectedProducts.Clear();
            foreach (var (storage, _) in storages)
            {
                try { ScanStorageEntity(storage); }
                catch { }
            }
        }

        /// <summary>
        /// Limits _seenProducts to only N unique products based on Familiarity.
        /// Low familiarity = fewer recommendations; 1.0 = full menu.
        /// </summary>
        internal void FilterByFamiliarity()
        {
            var uniqueIds = new HashSet<string>();
            foreach (var p in _seenProducts)
                uniqueIds.Add(p.ProductId);

            int totalUnique = uniqueIds.Count;
            if (totalUnique <= 2) return; // 2 or fewer — show everything

            int recommendCount = Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Lerp(2f, totalUnique, Familiarity)),
                2, totalUnique);

            if (recommendCount >= totalUnique) return; // sees everything

            // Randomly select which product IDs the budtender "mentions"
            var rng = new System.Random(SpawnSeed + 8831);
            var allIds = new List<string>(uniqueIds);
            for (int i = allIds.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (allIds[i], allIds[j]) = (allIds[j], allIds[i]);
            }
            var recommendedIds = new HashSet<string>();
            for (int i = 0; i < recommendCount; i++)
                recommendedIds.Add(allIds[i]);

            _seenProducts.RemoveAll(p => !recommendedIds.Contains(p.ProductId));
        }

        /// <summary>
        /// After browsing all shelves, scores all observed products using vanilla's
        /// GetProductEnjoyment formula and selects what to buy via weighted random.
        /// </summary>
        public void DecidePurchases()
        {
            if (_seenProducts.Count == 0)
                return;

            // 1. Deduplicate by ProductId — keep best quality, sum available UNITS across all packagings
            var unique = new Dictionary<string, ObservedProduct>();
            foreach (var obs in _seenProducts)
            {
                int units = obs.AvailableQuantity * obs.PkgMultiplier;
                if (unique.TryGetValue(obs.ProductId, out var existing))
                {
                    var updated = obs.QualityLevel > existing.QualityLevel ? obs : existing;
                    updated.AvailableQuantity = existing.AvailableQuantity + units;
                    unique[obs.ProductId] = updated;
                }
                else
                {
                    var entry = obs;
                    entry.AvailableQuantity = units;
                    unique[obs.ProductId] = entry;
                }
            }

            // 2. Score each unique product using vanilla GetProductEnjoyment formula
            var scored = new List<(ObservedProduct product, float appeal)>();
            var rng = new System.Random(SpawnSeed + 7919); // deterministic but different from pref gen

            foreach (var obs in unique.Values)
            {
                // --- Vanilla GetProductEnjoyment ---
                // Drug affinity * 0.3
                float drugScore = Preferences.WeedAffinity * 0.3f;

                // Effect match: (matchCount / preferredCount) * 0.4
                int matchCount = 0;
                foreach (string wanted in Preferences.PreferredEffectIds)
                {
                    for (int i = 0; i < obs.EffectIds.Count; i++)
                    {
                        if (obs.EffectIds[i] == wanted)
                        {
                            matchCount++;
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

                // Budget hard cutoff
                if (obs.Price > Preferences.MaxBudgetPerItem) continue;

                if (appeal > 0f)
                    scored.Add((obs, appeal));
            }

            if (scored.Count == 0)
                return;

            // 3. Sort by appeal descending
            scored.Sort((a, b) => b.appeal.CompareTo(a.appeal));

            // 4. Track observed packaging multipliers per product
            //    pkgMults: all distinct multipliers seen (e.g. [1, 5] for baggies+jars)
            //    Random customers coin-flip between these when choosing qty.
            //    minMult: smallest observed (qty must be a multiple of this — only jars? must buy 5,10,15)
            var pkgMults = new Dictionary<string, List<int>>();
            var minMult = new Dictionary<string, int>();
            foreach (var obs in _seenProducts)
            {
                if (!pkgMults.TryGetValue(obs.ProductId, out var list))
                {
                    list = new List<int>();
                    pkgMults[obs.ProductId] = list;
                }
                if (!list.Contains(obs.PkgMultiplier))
                    list.Add(obs.PkgMultiplier);
                if (!minMult.TryGetValue(obs.ProductId, out var mn) || obs.PkgMultiplier < mn)
                    minMult[obs.ProductId] = obs.PkgMultiplier;
            }

            // 5. Selection — budget-driven purchasing.
            //    Quantities are in raw product UNITS (not packages).
            //    Customers don't care about packaging — checkout determines that.
            float remainingBudget = Preferences.TotalOrderBudget;
            bool useBudget = remainingBudget > 0;
            var remaining = new List<(ObservedProduct product, float appeal)>(scored);

            while (remaining.Count > 0)
            {
                if (useBudget && remainingBudget <= 0) break;

                int pickIndex;
                if (remaining.Count == 1 || rng.NextDouble() < 0.5)
                    pickIndex = 0; // top pick
                else
                    pickIndex = 1 + rng.Next(remaining.Count - 1); // random from rest

                var pick = remaining[pickIndex];
                remaining.RemoveAt(pickIndex);

                // Get observed packaging sizes for this product (e.g. [1, 5] for baggies+jars)
                var mults = pkgMults.TryGetValue(pick.product.ProductId, out var ml) ? ml : null;

                int qty;
                if (useBudget)
                {
                    // Deal customers: buy as many units as budget allows
                    int canAfford = Mathf.Max(1, Mathf.FloorToInt(remainingBudget / pick.product.Price));
                    qty = Mathf.Min(canAfford, pick.product.AvailableQuantity);
                }
                else
                {
                    // Random customers: coin-flip between observed packaging sizes
                    // Saw jars+baggies? 50/50 pick between 5 and 1 as the step.
                    // High appeal → grab 2 of that packaging size.
                    int step = (mults != null && mults.Count > 0)
                        ? mults[rng.Next(mults.Count)]
                        : 1;
                    qty = step;
                    if (pick.appeal > 0.7f && pick.product.AvailableQuantity >= step * 2)
                        qty = step * 2;
                    qty = Math.Min(qty, pick.product.AvailableQuantity);
                }
                if (qty <= 0) continue;

                // Vanilla ceiling: Clamp to [1, 1000] per product, then
                // round large orders to multiples of 5 (same as Customer.DecidePurchases)
                qty = Mathf.Clamp(qty, 1, 1000);
                if (qty >= 14)
                    qty = Mathf.RoundToInt(qty / 5f) * 5;

                // Round qty down to a multiple of the smallest observed packaging
                // (only saw jars? can only order 5, 10, 15... not 1 or 3)
                int minStep = minMult.TryGetValue(pick.product.ProductId, out var ms) ? ms : 1;
                if (minStep > 1)
                {
                    qty = (qty / minStep) * minStep;
                    if (qty <= 0) continue;
                }

                SelectedProducts.Add(new SelectedProduct
                {
                    ProductId = pick.product.ProductId,
                    PackagingId = null, // customer doesn't care — checkout determines packaging
                    ProductName = pick.product.ProductName,
                    Price = pick.product.Price, // per-unit price
                    QualityLevel = pick.product.QualityLevel,
                    Quantity = qty,
                    EffectIds = pick.product.EffectIds
                });

                remainingBudget -= pick.product.Price * qty;
            }
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

        internal static void SetVoiceDatabase(NPC npc, int seed)
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
                OTCLog.Warning(OTCLog.Systems.Customer,$"SetVoiceDatabase failed: {ex.Message}");
            }
        }

        // =====================================================================
        //  Cleanup
        // =====================================================================

        /// <summary>
        /// Removes this customer from tracking. Destroys random-spawned NPCs;
        /// releases deal NPCs back to vanilla behavior.
        /// </summary>
        public void Despawn()
        {
            Active.Remove(Id);

            if (IsDealCustomer)
            {
                // Deal customers: release NPC, don't destroy. Apply cooldown reset.
                DispensaryDealManager.ReleaseDealNPC(this);
                if (SelectedProducts.Count == 0)
                    DispensaryDealManager.ApplyDealCooldownOnly(this);
                GameNpc = null;
                return;
            }

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
                if (customer.IsDealCustomer)
                {
                    DispensaryDealManager.ReleaseDealNPC(customer);
                    customer.GameNpc = null;
                    continue;
                }
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
