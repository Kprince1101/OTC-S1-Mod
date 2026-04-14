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
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.VoiceOver;
using Customer = Il2CppScheduleOne.Economy.Customer;
using ProductManager = Il2CppScheduleOne.Product.ProductManager;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
using ItemInstance = Il2CppScheduleOne.ItemFramework.ItemInstance;
using QualityItemInstance = Il2CppScheduleOne.ItemFramework.QualityItemInstance;
using EQuality = Il2CppScheduleOne.ItemFramework.EQuality;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Economy;
using ScheduleOne.Effects;
using ScheduleOne.Employees;
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Storage;
using ScheduleOne.VoiceOver;
using Customer = ScheduleOne.Economy.Customer;
using ProductManager = ScheduleOne.Product.ProductManager;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
using ItemInstance = ScheduleOne.ItemFramework.ItemInstance;
using QualityItemInstance = ScheduleOne.ItemFramework.QualityItemInstance;
using EQuality = ScheduleOne.ItemFramework.EQuality;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Wraps a game NPC as a store customer with a browse-and-leave lifecycle.
    /// </summary>
    public class CustomerInstance
    {


        /// <summary>
        /// Vanilla <c>CustomerData.MinWeeklySpend</c> class-level default.
        /// Used as the walk-in fallback budget when the NPC's vanilla Customer
        /// component is unavailable. Actual per-NPC values come from their
        /// CustomerData ScriptableObject and are read at runtime when possible.
        /// </summary>
        private const float VanillaMinWeeklySpend = 200f;

        /// <summary>Walk-in daily budget divisor (casual buyer, a few visits/week).</summary>
        private const float WalkInBudgetDivisor = 3f;

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
        public CustomerPreferences Preferences { get; internal set; }

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

        public CustomerState State
        {
            get => _state;
            set
            {
                _state = value;
                _stateEnteredTime = Time.time;
                NavRetries = 0;
            }
        }
        private CustomerState _state;
        private float _stateEnteredTime;

        /// <summary>Seconds the customer has been in the current state.</summary>
        public float TimeInCurrentState => Time.time - _stateEnteredTime;

        /// <summary>Number of nav-stuck resend attempts in the current state.</summary>
        public int NavRetries { get; private set; }

        /// <summary>Resets the state timer without changing state (e.g., after a retry).</summary>
        public void ResetStateTimer()
        {
            _stateEnteredTime = Time.time;
            NavRetries++;
        }

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

        /// <summary>
        /// Enjoy premium accumulated during product selection. Vanilla embeds a
        /// second enjoyScale factor in the per-unit price markup; dispensaries
        /// charge flat price, so that factor becomes the tip base instead.
        /// Modulated by the checkout skill check zone multiplier.
        /// </summary>
        public float EnjoyPremium { get; private set; }

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

        internal enum RejectionReason { None, EmptyShelves, TooExpensive, LowAppeal }
        internal RejectionReason LastRejection { get; private set; }

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
        public static CustomerInstance Create(string id, int seed, CustomerSpawnPoints.SpawnPoint spawnPoint) =>
            Create(id, seed, spawnPoint, null);

        /// <summary>
        /// Spawns a new customer NPC targeting a specific building.
        /// </summary>
        internal static CustomerInstance Create(string id, int seed, CustomerSpawnPoints.SpawnPoint spawnPoint,
            BuildingTarget target)
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

                var instance = new CustomerInstance(id, seed, spawnPoint, npc, target);

                // Capture FishNet ObjectId for client-side adoption
                try
                {
                    var nob = npc.GetComponent<FishNet.Object.NetworkObject>();
                    if (nob != null)
                        instance.NetworkObjectId = (int)nob.ObjectId;
                }
                catch (Exception nobEx)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"{id}: failed to read NetworkObjectId: {nobEx.Message}");
                }

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
                catch (Exception relEx)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"{id}: failed to read RelationDelta for familiarity: {relEx.Message}");
                }

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

                // Match vanilla Customer.TryGenerateContract (Customer.cs:776-777)
                // exactly so deal-customer per-visit budget parity holds:
                //   count = GetOrderDays(addiction, relation).Count
                //   num   = GetAdjustedWeeklySpend(relation) / count
                // NOT MaxOrdersPerWeek — that's always 5 and massively
                // undershoots real per-visit budget for low-relation NPCs.
                float totalBudget = TryGetVanillaDailyBudget(customer);
                if (totalBudget <= 0f)
                {
                    // Fallback if daily budget couldn't be resolved
                    float normalizedRelation = 0.4f;
                    try { normalizedRelation = customer.NPC.RelationData.RelationDelta / 5f; }
                    catch (Exception relEx)
                    {
                        OTCLog.Warning(OTCLog.Systems.Customer,
                            $"ExtractVanillaPreferences fallback: RelationDelta read failed: {relEx.Message}");
                    }
                    float weeklyBase = Mathf.Lerp(data.MinWeeklySpend, data.MaxWeeklySpend, normalizedRelation);
                    float rankMultiplier = 1f;
                    try
                    {
                        if (S1API.Leveling.LevelManager.Exists)
                            rankMultiplier = S1API.Leveling.LevelManager.GetOrderLimitMultiplier(
                                S1API.Leveling.LevelManager.CurrentRank);
                    }
                    catch (Exception rankEx)
                    {
                        OTCLog.Warning(OTCLog.Systems.Customer,
                            $"ExtractVanillaPreferences fallback: rank multiplier read failed: {rankEx.Message}");
                    }
                    totalBudget = weeklyBase * rankMultiplier / Mathf.Max(1, data.MaxOrdersPerWeek);
                }
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

            // Walk-in budget: vanilla first-deal daily budget at zero relation.
            // At zero relation GetAdjustedWeeklySpend returns MinWeeklySpend,
            // and with MinOrdersPerWeek=1 the order day count is 1, so
            // dailyBudget = MinWeeklySpend * rankMultiplier.
            // This is a fallback — DecidePurchases reads the NPC's real
            // CustomerData when available and overrides this value.
            float budget = VanillaMinWeeklySpend;
            float rankMultiplier = 1f;
            try
            {
                if (S1API.Leveling.LevelManager.Exists)
                    rankMultiplier = S1API.Leveling.LevelManager.GetOrderLimitMultiplier(
                        S1API.Leveling.LevelManager.CurrentRank);
            }
            catch (Exception rankEx)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"GeneratePreferences: rank multiplier read failed: {rankEx.Message}");
            }
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
        /// Computes a world-space exit target guaranteed to lie OUTSIDE S1MAPI's
        /// interior AABB. Preference order: WarpReturnPosition (deal customers) →
        /// SpawnPoint.Position (walk-ins) → Target.ExitWalkPosition (door-approach
        /// fallback, always outside by design).
        /// <para>
        /// Any preferred option that overlaps the interior AABB is skipped, because
        /// sending the NPC there would re-trigger S1MAPI's SetDestination prefix and
        /// re-capture them into interior navigation — trapping them in the building.
        /// </para>
        /// </summary>
        public Vector3 GetSafeExitTarget()
        {
            var t = Target;

            if (WarpReturnPosition.HasValue)
            {
                var p = WarpReturnPosition.Value;
                if (t == null || !t.IsWorldPositionInsideInterior(p))
                    return p;
            }

            if (SpawnPoint != null)
            {
                var p = SpawnPoint.Position;
                if (t == null || !t.IsWorldPositionInsideInterior(p))
                    return p;
            }

            if (t != null)
            {
                var exit = t.ExitWalkPosition;
                if (!t.IsWorldPositionInsideInterior(exit))
                    return exit;
            }

            OTCLog.Warning(OTCLog.Systems.Customer,
                $"{Id}: GetSafeExitTarget fell through to Vector3.zero " +
                $"(target={(t != null ? t.Name : "null")}, " +
                $"warp={WarpReturnPosition?.ToString() ?? "null"}, " +
                $"spawn={(SpawnPoint != null ? SpawnPoint.Position.ToString() : "null")})");
            return Vector3.zero;
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
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"{Id}: EnsureMoving failed: {ex.Message}");
            }
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
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"{Id}: SetAvoidancePriority({priority}) failed: {ex.Message}");
            }
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
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"{Id}: DisableObstacleAvoidance failed: {ex.Message}");
            }
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
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"{Id}: RestoreObstacleAvoidance failed: {ex.Message}");
            }
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
                try { GameNpc.Movement?.Stop(); }
                catch (Exception stopEx)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"{Id}: Movement.Stop failed during browse pause: {stopEx.Message}");
                }

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
                    catch (Exception voiceEx)
                    {
                        OTCLog.Warning(OTCLog.Systems.Customer,
                            $"{Id}: Think vocalization failed: {voiceEx.Message}");
                    }
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
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"ScanStorageEntity: slot scan failed: {ex.Message}");
                }
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
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"{Id}: ObserveFromStorageList: ScanStorageEntity failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Scans player hotbar slots and adds any packaged products to _seenProducts.
        /// Called after ObserveFromStorageList so items in the player's hands count
        /// during consultation.
        /// </summary>
        internal void ObservePlayerInventory()
        {
            try
            {
                var inventory = PlayerSingleton<PlayerInventory>.Instance;
                if (inventory?.hotbarSlots == null) return;

                for (int i = 0; i < inventory.hotbarSlots.Count; i++)
                {
                    var slot = inventory.hotbarSlots[i];
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
                    catch (Exception slotEx)
                    {
                        OTCLog.Warning(OTCLog.Systems.Customer, $"ObservePlayerInventory slot {i} failed: {slotEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"ObservePlayerInventory failed: {ex.Message}");
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
            LastRejection = RejectionReason.None;
            if (_seenProducts.Count == 0)
            {
                LastRejection = RejectionReason.EmptyShelves;
                return;
            }

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
            int overBudgetCount = 0;
            int rejectedByChanceCount = 0;

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

                // --- Ranking only ---
                // Accept/reject now happens per-qty in step 5 via vanilla
                // GetOfferSuccessChance, which matches the phone/street-deal
                // acceptance curve exactly. Enjoyment is used purely to rank
                // which product the customer prefers among the visible menu.
                float appeal = enjoyment;

                // Budget hard cutoff — MaxBudgetPerItem is the walk-in's upper
                // tolerance for a single product slot. Vanilla's daily-budget
                // ratio check (hard cap at 3x) still fires in step 5.
                if (obs.Price > Preferences.MaxBudgetPerItem) { overBudgetCount++; continue; }

                scored.Add((obs, appeal));
            }

            if (scored.Count == 0)
            {
                LastRejection = overBudgetCount > 0
                    ? RejectionReason.TooExpensive
                    : RejectionReason.LowAppeal;
                return;
            }

            // 3. Sort by appeal descending
            scored.Sort((a, b) => b.appeal.CompareTo(a.appeal));

            // 4. Track observed packaging multipliers per product
            //    pkgMults: all distinct multipliers seen (e.g. [1, 5] for baggies+jars).
            //    Used only by the step-based fallback heuristic when the vanilla
            //    budget path is unreachable — see fallback in the walk-in branch.
            var pkgMults = new Dictionary<string, List<int>>();
            foreach (var obs in _seenProducts)
            {
                if (!pkgMults.TryGetValue(obs.ProductId, out var list))
                {
                    list = new List<int>();
                    pkgMults[obs.ProductId] = list;
                }
                if (!list.Contains(obs.PkgMultiplier))
                    list.Add(obs.PkgMultiplier);
            }

            // 5. Selection — budget-driven purchasing.
            //    Quantities are in raw product UNITS (not packages).
            //    Customers don't care about packaging — checkout determines that.
            //
            // Both deal and walk-in customers have a per-visit spend cap.
            // Deal customers use their vanilla-derived TotalOrderBudget.
            // Walk-ins get a casual daily slice: the weekly budget at zero
            // relationship / zero addiction, divided by 3 (a few visits per
            // week rather than blowing the entire weekly spend in one go).
            float remainingBudget;
            if (IsDealCustomer)
            {
                remainingBudget = Preferences.TotalOrderBudget;
            }
            else
            {
                remainingBudget = TryGetFirstDealBudget();
                if (remainingBudget <= 0f)
                    remainingBudget = Preferences.TotalOrderBudget / WalkInBudgetDivisor;
            }
            bool useBudget = remainingBudget > 0;
            EnjoyPremium = 0f;
            var remaining = new List<(ObservedProduct product, float appeal)>(scored);

#if DEBUG
            // --- Audit: header ---
            float auditVanillaDailyBudget = 0f;
            float auditRankMult = 1f;
            string auditRankName = "?";
            float auditOtcTotalSpend = 0f;
            int auditOtcTotalQty = 0;
            try
            {
                Customer vc = VanillaCustomer;
                if (vc == null && GameNpc != null)
                    vc = GameNpc.GetComponent<Customer>();
                if (vc != null)
                    auditVanillaDailyBudget = TryGetVanillaDailyBudget(vc);

                if (S1API.Leveling.LevelManager.Exists)
                {
                    var rank = S1API.Leveling.LevelManager.CurrentRank;
                    auditRankMult = S1API.Leveling.LevelManager.GetOrderLimitMultiplier(rank);
                    auditRankName = rank.ToString();
                }
            }
            catch (Exception ex)
            {
                OTCLog.Msg(OTCLog.Systems.Customer, $"[AUDIT] header error: {ex.Message}");
            }

            string custName = GameNpc != null ? $"{GameNpc.FirstName} {GameNpc.LastName}" : Id;
            string custType = IsDealCustomer ? "Deal" : "Walk-in";
            OTCLog.Warning(OTCLog.Systems.Customer,
                $"[AUDIT] ═══════════════════════════════════════");
            OTCLog.Warning(OTCLog.Systems.Customer,
                $"[AUDIT] {custName} | {custType} | Rank: {auditRankName} (x{auditRankMult:F2})");
            OTCLog.Warning(OTCLog.Systems.Customer,
                $"[AUDIT] Budget: ${remainingBudget:F0} ({(IsDealCustomer ? "deal TotalOrderBudget" : $"walkin daily (weekly/orderDays/{WalkInBudgetDivisor})")}) | VanillaDailyBudget(actual rel): ${auditVanillaDailyBudget:F0} | GeneratePrefs: ${Preferences.TotalOrderBudget:F0}");
#endif

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

                // Vanilla qty formula (Customer.cs L800-801):
                //   qty = round(dailyBudget * enjoyScale / unitPrice)
                // Vanilla's second enjoy factor scales per-unit price (markup).
                // Dispensaries charge flat price, so that factor becomes the
                // enjoy premium tip — accumulated in EnjoyPremium below.
                float enjoyScale = Mathf.Lerp(0.66f, 1.5f, Mathf.Clamp01(pick.appeal));
                float scaledBudget = remainingBudget * enjoyScale;
                int qty = Mathf.Max(1, Mathf.RoundToInt(scaledBudget / pick.product.Price));
                qty = Mathf.Min(qty, pick.product.AvailableQuantity);
                if (qty <= 0) continue;

                // Vanilla ceiling: clamp to [1, 1000] per product. The
                // vanilla /5 rounding above 14 is intentionally omitted —
                // it creates sharp boundary artifacts when OTC's asking
                // price lands raw qty just under the threshold (e.g. 11.4
                // → 11) while the BM baseline lands just above (13.5 →
                // 14 → 15), giving vanilla a free +4 unit bump OTC can't
                // match. Skipping /5 rounding on OTC's side keeps spend
                // tracking the dollar target set by offMult² above.
                qty = Mathf.Clamp(qty, 1, 1000);

                // NOTE: We intentionally do NOT round qty to a multiple of the
                // smallest *observed* packaging. Pre-breakdown this was needed
                // (seeing only jars → must buy 5/10/15) but the checkout now
                // splits any package into a largest-fit packaging partition via
                // EmitBreakdownFragments, so a customer asking for 13g against a
                // brick-only stock gets 2× 5g jars + 3× 1g baggies and the 7g
                // remainder is repackaged back to storage. If we rounded here,
                // a $60 walk-in wanting qty=11 against brick-only stock would
                // floor to 0 and silently skip every product, causing the
                // "customers refuse to buy bricks" symptom.

#if DEBUG
                // --- Audit: per-product ---
                try
                {
                    float otcTotal = pick.product.Price * qty;
                    auditOtcTotalSpend += otcTotal;
                    auditOtcTotalQty += qty;

                    // What qty would the OTC GeneratePrefs budget produce?
                    float otcBudget = Preferences.TotalOrderBudget;
                    int otcBudgetQty = pick.product.Price > 0
                        ? Mathf.Clamp(Mathf.RoundToInt(otcBudget * enjoyScale / pick.product.Price), 1, 1000)
                        : 0;

                    string qualName = ((EQuality)pick.product.QualityLevel).ToString();
                    string budgetSrc = IsDealCustomer ? "deal-budget" : "walkin-firstdeal";

                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"[AUDIT]   {pick.product.ProductName} ({qualName}) | {budgetSrc} | enjoy={pick.appeal:F2} scale={enjoyScale:F2}");
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"[AUDIT]     OTC price=${pick.product.Price:F2} mkt=${pick.product.MarketValue:F2} | qty={qty} → ${otcTotal:F0}");
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"[AUDIT]     If OTC budget (${otcBudget:F0}) were used: qty≈{otcBudgetQty} → ${otcBudgetQty * pick.product.Price:F0}");
                }
                catch (Exception ex)
                {
                    OTCLog.Msg(OTCLog.Systems.Customer, $"[AUDIT] per-product error: {ex.Message}");
                }
#endif

                // --- Vanilla acceptance gate ---
                // Use the exact same curve as Customer.GetOfferSuccessChance
                // (the one vanilla dealer/phone deals roll against) so walk-in
                // and deal acceptance stay in parity at the same price ratio.
                float totalPrice = pick.product.Price * qty;
                var eval = EvaluateVanillaOffer(pick.product, qty, totalPrice);

                if (eval.Evaluated)
                {
                    double roll = rng.NextDouble();
                    if (roll > eval.AcceptChance)
                    {
                        rejectedByChanceCount++;
                        continue;
                    }
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

                // Accumulate the enjoy premium (vanilla's second enjoy factor
                // on per-unit markup). Only positive: low-enjoyment products
                // reduce qty via the scaled budget, not the tip.
                float enjoyDelta = enjoyScale - 1f;
                if (enjoyDelta > 0f)
                    EnjoyPremium += pick.product.Price * qty * enjoyDelta;
            }

#if DEBUG
            // --- Audit: summary ---
            if (SelectedProducts.Count > 0)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"[AUDIT] RESULT → {SelectedProducts.Count} products, {auditOtcTotalQty}g total, ${auditOtcTotalSpend:F0} spent");
            }
            else
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"[AUDIT] RESULT → Nothing selected (overBudget={overBudgetCount}, rejected={rejectedByChanceCount})");
            }
            OTCLog.Warning(OTCLog.Systems.Customer,
                $"[AUDIT] ═══════════════════════════════════════");
#endif

            // If we picked nothing because every roll came up reject, surface
            // that as a price rejection (customer saw things they liked, but
            // the markup was too high on the day).
            if (SelectedProducts.Count == 0 && rejectedByChanceCount > 0)
                LastRejection = RejectionReason.TooExpensive;
        }

        // =====================================================================
        //  Vanilla acceptance evaluation
        // =====================================================================

        /// <summary>
        /// Result of the vanilla acceptance probe for a single product.
        /// </summary>
        private struct VanillaOfferEval
        {
            /// <summary>
            /// False when the vanilla <see cref="Customer"/> component couldn't
            /// be resolved (no NPC, missing product definition, packaging
            /// issues). Callers fall back to unconditionally accepting.
            /// </summary>
            public bool Evaluated;

            /// <summary>
            /// 0–1 probability that vanilla would accept this offer at
            /// <c>askingPrice</c>, as returned by
            /// <see cref="Customer.GetOfferSuccessChance"/>.
            /// </summary>
            public float AcceptChance;
        }

        /// <summary>
        /// Resolves the NPC's vanilla <see cref="Customer"/> component, builds
        /// a minimal <see cref="ItemInstance"/> for the product at the right
        /// packaging+quality, and calls
        /// <see cref="Customer.GetOfferSuccessChance"/> so OTC's acceptance
        /// gate matches the curve vanilla dealer/phone deals roll against.
        /// Returns a default result (Evaluated=false) if any step fails.
        /// </summary>
        private VanillaOfferEval EvaluateVanillaOffer(ObservedProduct obs, int qty, float askingPrice)
        {
            var result = default(VanillaOfferEval);
            try
            {
                Customer customer = VanillaCustomer;
                if (customer == null && GameNpc != null)
                    customer = GameNpc.GetComponent<Customer>();
                if (customer == null) return result;

                ProductDefinition prodDef = FindProductDefinition(obs.ProductId);
                if (prodDef == null) return result;

                var validPack = prodDef.ValidPackaging;
                if (validPack == null || validPack.Length == 0) return result;

                // Use the smallest packaging (baggie=1 in most cases) and a
                // stack size that makes Quantity*Amount == qty so the vanilla
                // curve sees the right total unit count.
                var pkg = validPack[0];
                if (pkg == null || pkg.Quantity <= 0) return result;
                int stack = Mathf.Max(1, qty / pkg.Quantity);

                var defaultInstance = prodDef.GetDefaultInstance(stack);
#if IL2CPP
                var prodInstance = defaultInstance?.TryCast<ProductItemInstance>();
#else
                var prodInstance = defaultInstance as ProductItemInstance;
#endif
                if (prodInstance == null) return result;

                prodInstance.SetPackaging(pkg);

#if IL2CPP
                var qInst = defaultInstance?.TryCast<QualityItemInstance>();
#else
                var qInst = defaultInstance as QualityItemInstance;
#endif
                EQuality quality = (EQuality)obs.QualityLevel;
                if (qInst != null)
                {
                    try { qInst.SetQuality(quality); }
                    catch (Exception qEx)
                    {
                        // Quality is cosmetic for acceptance — log and continue.
                        OTCLog.Warning(OTCLog.Systems.Customer,
                            $"SetQuality failed for '{obs.ProductId}': {qEx.Message}");
                    }
                }

#if IL2CPP
                var items = new Il2CppSystem.Collections.Generic.List<ItemInstance>();
#else
                var items = new List<ItemInstance>();
#endif
                items.Add(prodInstance);

                result.AcceptChance = customer.GetOfferSuccessChance(items, askingPrice);
                result.Evaluated = true;
                return result;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"EvaluateVanillaOffer failed for '{obs.ProductId}': {ex.Message}");
                return default;
            }
        }

        /// <summary>
        /// Returns the vanilla daily budget for a first-time deal: zero
        /// relationship, zero addiction. This gives walk-in customers a
        /// spending cap that matches what a brand-new vanilla customer
        /// would spend on their very first contract.
        /// Returns 0 when the vanilla Customer component is unavailable.
        /// </summary>
        private float TryGetFirstDealBudget()
        {
            try
            {
                Customer customer = VanillaCustomer;
                if (customer == null && GameNpc != null)
                    customer = GameNpc.GetComponent<Customer>();
                if (customer == null) return 0f;

                var data = customer.CustomerData;
                if (data == null) return 0f;

                // Zero relationship → GetAdjustedWeeklySpend returns
                // MinWeeklySpend * rankMultiplier (Lerp at t=0).
                float weekly = data.GetAdjustedWeeklySpend(0f);

                // Zero addiction + zero relation → GetOrderDays uses
                // t = max(0,0) = 0, so numOrders = MinOrdersPerWeek.
                var orderDays = data.GetOrderDays(0f, 0f);
                int days = orderDays != null ? orderDays.Count : 1;
                if (days <= 0) days = 1;

                // Casual daily slice: weekly / orderDays / 3.
                return weekly / days / WalkInBudgetDivisor;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"TryGetFirstDealBudget failed: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// Mirrors the vanilla daily-budget calc from <c>Customer.TryGenerateContract</c>:
        /// <c>GetAdjustedWeeklySpend(relationDelta/5) / orderDays.Count</c>.
        /// Returns 0 if any part of the chain is unavailable (no customerData,
        /// no NPC, empty order days, etc.).
        /// </summary>
        private static float TryGetVanillaDailyBudget(Customer customer)
        {
            try
            {
                if (customer == null) return 0f;
                var data = customer.CustomerData;
                if (data == null) return 0f;
                float relationDelta = customer.NPC != null ? customer.NPC.RelationData.RelationDelta / 5f : 0f;
                float weekly = data.GetAdjustedWeeklySpend(relationDelta);
                var orderDays = data.GetOrderDays(customer.CurrentAddiction, relationDelta);
                int days = orderDays != null ? orderDays.Count : 0;
                if (days <= 0) return 0f;
                return weekly / days;
            }
            catch (Exception ex)
            {
                // Caller treats 0f as "vanilla unreachable, use fallback path" — log so
                // recurring reachability issues aren't silent.
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"TryGetVanillaDailyBudget failed for {customer?.NPC?.FirstName ?? "?"}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>Lookup helper for <see cref="ProductDefinition"/> by ID.</summary>
        private static ProductDefinition FindProductDefinition(string productId)
        {
            try
            {
                var listed = ProductManager.ListedProducts;
                if (listed == null) return null;
                for (int i = 0; i < listed.Count; i++)
                {
                    var p = listed[i];
                    if (p != null && p.ID == productId) return p;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"FindProductDefinition failed for '{productId}': {ex.Message}");
            }
            return null;
        }

        // =====================================================================
        //  Voice lines
        // =====================================================================

        private static readonly string[] EmptyShelvesLines =
        {
            "You don't have anything to sell?",
            "The shelves are empty...",
            "There's nothing here."
        };

        private static readonly string[] TooExpensiveLines =
        {
            "Everything here is way too expensive.",
            "These prices are insane.",
            "I can't afford any of this.",
            "Way out of my budget."
        };

        private static readonly string[] LowAppealLines =
        {
            "Not really my thing.",
            "The quality isn't great...",
            "I don't see anything I like.",
            "Not what I'm looking for."
        };

        private static readonly string[] GenericDisappointedLines =
        {
            "Nothing for me...",
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
                string[] lines = LastRejection switch
                {
                    RejectionReason.EmptyShelves => EmptyShelvesLines,
                    RejectionReason.TooExpensive => TooExpensiveLines,
                    RejectionReason.LowAppeal => LowAppealLines,
                    _ => GenericDisappointedLines
                };
                string line = lines[rng.Next(lines.Length)];
                GameNpc.DialogueHandler?.WorldspaceRend?.ShowText(line, 3f);
                GameNpc.VoiceOverEmitter?.Play(EVOLineType.Annoyed);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"{Id}: ShowDisappointed failed: {ex.Message}");
            }
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
