using MelonLoader;
using UnityEngine;
using UnityEngine.AI;
using System;

#if IL2CPP
using Il2CppFishNet.Object;
using Il2CppFishNet.Managing;
using Il2CppFishNet;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Economy;
#else
using FishNet.Object;
using FishNet.Managing;
using FishNet;
using ScheduleOne.NPCs;
using ScheduleOne.DevUtilities;
using ScheduleOne.AvatarFramework;
using ScheduleOne.Economy;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Shared NPC spawning infrastructure. Clones the game's CivilianNPC prefab,
    /// configures identity and appearance, and handles FishNet network spawning.
    /// Used by both Drifters and Customers.
    /// </summary>
    public static class NpcSpawner
    {
        private static readonly MelonLogger.Instance Logger = new("OTC:NpcSpawner");

        private static NetworkObject _cachedBasePrefab;
        private static bool _prefabSearched;

        // Cached CustomerData — created once and reused for all spawned NPCs
        private static CustomerData _customerData;

        // =====================================================================
        //  Prefab cache
        // =====================================================================

        /// <summary>
        /// Finds and caches the CivilianNPC prefab from FishNet's spawnable prefabs.
        /// </summary>
        private static NetworkObject GetBasePrefab()
        {
            if (_prefabSearched && _cachedBasePrefab != null)
                return _cachedBasePrefab;

            _prefabSearched = true;

            try
            {
                var networkManager = InstanceFinder.NetworkManager;
                if (networkManager == null)
                {
                    Logger.Error("NetworkManager not found");
                    return null;
                }

                var spawnablePrefabs = networkManager.SpawnablePrefabs;
                if (spawnablePrefabs == null)
                {
                    Logger.Error("SpawnablePrefabs not available");
                    return null;
                }

                int count = spawnablePrefabs.GetObjectCount();

                for (int i = 0; i < count; i++)
                {
                    var obj = spawnablePrefabs.GetObject(true, i);
                    if (obj == null || obj.gameObject == null)
                        continue;

                    if (obj.gameObject.name == "CivilianNPC")
                    {
                        _cachedBasePrefab = obj;
                        return _cachedBasePrefab;
                    }
                }

                // Fallback: find any prefab with NPC component
                for (int i = 0; i < count; i++)
                {
                    var obj = spawnablePrefabs.GetObject(true, i);
                    if (obj?.gameObject?.GetComponent<NPC>() != null)
                    {
                        _cachedBasePrefab = obj;
                        return _cachedBasePrefab;
                    }
                }

                Logger.Error("No suitable NPC prefab found");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"GetBasePrefab failed: {ex.Message}");
                return null;
            }
        }

        // =====================================================================
        //  Spawn / Despawn
        // =====================================================================

        /// <summary>
        /// Spawns a new civilian NPC at the specified position. Clones the CivilianNPC
        /// prefab, sets identity, snaps to NavMesh, adds Customer component, activates,
        /// isolates from vanilla deal system, and network-spawns via FishNet.
        /// </summary>
        /// <param name="id">Unique identifier for this NPC.</param>
        /// <param name="gameObjectName">Name for the cloned GameObject.</param>
        /// <param name="firstName">First name for the NPC.</param>
        /// <param name="lastName">Last name for the NPC.</param>
        /// <param name="position">World position to spawn at.</param>
        /// <param name="rotation">Facing rotation.</param>
        /// <returns>The spawned NPC, or null on failure.</returns>
        public static NPC SpawnCivilianNpc(string id, string gameObjectName,
            string firstName, string lastName, Vector3 position, Quaternion rotation)
        {
            try
            {
                var basePrefab = GetBasePrefab();
                if (basePrefab == null)
                {
                    Logger.Error($"Cannot spawn {id}: no base prefab");
                    return null;
                }

                // Clone the prefab
                var clone = UnityEngine.Object.Instantiate(basePrefab);
                if (clone == null || clone.gameObject == null)
                {
                    Logger.Error($"Failed to instantiate prefab for {id}");
                    return null;
                }

                clone.gameObject.name = gameObjectName;
                clone.gameObject.SetActive(false);

                // Parent to NPC container
                var npcManager = NetworkSingleton<NPCManager>.Instance;
                if (npcManager?.NPCContainer != null)
                    clone.gameObject.transform.SetParent(npcManager.NPCContainer, false);

                // Get NPC component
                var npc = clone.gameObject.GetComponent<NPC>();
                if (npc == null)
                {
                    Logger.Error($"Cloned object missing NPC component for {id}");
                    UnityEngine.Object.Destroy(clone.gameObject);
                    return null;
                }

                // Configure identity
                npc.ID = id;
                npc.FirstName = firstName;
                npc.LastName = lastName;

                // Remove from registry if auto-added (we'll add manually)
                try
                {
                    if (NPCManager.NPCRegistry != null && NPCManager.NPCRegistry.Contains(npc))
                        NPCManager.NPCRegistry.Remove(npc);
                }
                catch { }

                // Snap position to NavMesh
                Vector3 spawnPos = position;
                if (NavMesh.SamplePosition(position, out NavMeshHit hit, 10f, NavMesh.AllAreas))
                    spawnPos = hit.position;

                clone.gameObject.transform.position = spawnPos;
                clone.gameObject.transform.rotation = rotation;

                // Add Customer component BEFORE activation (customerData must be set before Awake)
                try
                {
                    AddCustomerComponent(npc);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to add Customer component for {id}: {ex.Message}");
                }

                // Activate (triggers Awake + Start on all components)
                clone.gameObject.SetActive(true);

                // Isolate Customer component from vanilla deal system
                IsolateCustomerComponent(npc);

                // Register with game
                try
                {
                    if (NPCManager.NPCRegistry != null && !NPCManager.NPCRegistry.Contains(npc))
                        NPCManager.NPCRegistry.Add(npc);
                }
                catch { }

                // Network-spawn so FishNet initializes all NetworkBehaviour components
                // (Movement, Combat, etc.). Without this the NPC can't move or render.
                try
                {
                    if (InstanceFinder.ServerManager != null)
                        InstanceFinder.ServerManager.Spawn(clone);
                    else
                        Logger.Warning($"ServerManager is null, {id} may not function correctly");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"ServerManager.Spawn failed for {id}: {ex.Message}");
                }

                // Warp to position
                try
                {
                    npc.Movement?.Warp(spawnPos);
                    npc.Movement?.Stop();
                    npc.Movement?.FaceDirection(rotation * Vector3.forward);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Movement setup failed for {id}: {ex.Message}");
                }

                // Set agent type to Humanoid — cloned prefab starts at agentTypeID=0 (Unity default)
                // but game NPCs use "Humanoid" which may resolve to a different ID via NavMeshUtility
                try
                {
                    npc.Movement?.SetAgentType(NPCMovement.EAgentType.Humanoid);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"SetAgentType failed for {id}: {ex.Message}");
                }

                // Enable off-mesh link traversal (game prefab ships with it disabled)
                try
                {
                    var agent = npc.gameObject.GetComponent<NavMeshAgent>();
                    if (agent != null)
                    {
                        if (Config.VerboseLogging.Value)
                            Logger.Msg($"[NavDebug] {id} agentTypeID={agent.agentTypeID} autoTraverseOffMeshLink={agent.autoTraverseOffMeshLink} isOnNavMesh={agent.isOnNavMesh} areaMask={agent.areaMask}");
                        if (!agent.autoTraverseOffMeshLink)
                        {
                            agent.autoTraverseOffMeshLink = true;
                            if (Config.VerboseLogging.Value)
                                Logger.Msg($"[NavDebug] {id} set autoTraverseOffMeshLink=true");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"NavMeshAgent debug log failed for {id}: {ex.Message}");
                }

                return npc;
            }
            catch (Exception ex)
            {
                Logger.Error($"Spawn failed for {id}: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Despawns and destroys an NPC. Removes from registry and FishNet.
        /// </summary>
        public static void Despawn(NPC npc)
        {
            if (npc == null) return;

            try
            {
                string id = npc.ID ?? "unknown";

                try { NPCManager.NPCRegistry?.Remove(npc); }
                catch { }

                try
                {
                    var netObj = npc.gameObject?.GetComponent<NetworkObject>();
                    if (netObj != null && InstanceFinder.ServerManager != null && netObj.IsSpawned)
                        InstanceFinder.ServerManager.Despawn(netObj);
                    else if (npc.gameObject != null)
                        UnityEngine.Object.Destroy(npc.gameObject);
                }
                catch
                {
                    if (npc.gameObject != null)
                        UnityEngine.Object.Destroy(npc.gameObject);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Despawn failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Resets the prefab cache. Call on scene transitions.
        /// </summary>
        public static void ResetCache()
        {
            _cachedBasePrefab = null;
            _prefabSearched = false;
        }

        // =====================================================================
        //  Customer component management
        // =====================================================================

        /// <summary>
        /// Gets or creates a shared CustomerData ScriptableObject for spawned NPCs.
        /// </summary>
        private static CustomerData GetOrCreateCustomerData()
        {
            if (_customerData != null)
                return _customerData;

            try
            {
                _customerData = ScriptableObject.CreateInstance<CustomerData>();
                _customerData.MinWeeklySpend = 100f;
                _customerData.MaxWeeklySpend = 500f;
                _customerData.MinOrdersPerWeek = 1;
                _customerData.MaxOrdersPerWeek = 1;
                _customerData.Standards = ECustomerStandard.Moderate;
                _customerData.CanBeDirectlyApproached = false;
                _customerData.BaseAddiction = 0f;
                _customerData.DependenceMultiplier = 0f;
                _customerData.CallPoliceChance = 0f;
                _customerData.DefaultAffinityData = new CustomerAffinityData();
                return _customerData;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to create CustomerData: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Adds a Customer component to an inactive NPC with proper CustomerData setup.
        /// Must be called while the GameObject is inactive.
        /// </summary>
        public static Customer AddCustomerComponent(NPC npc)
        {
            if (npc == null || npc.gameObject == null)
                return null;

            try
            {
                if (npc.gameObject.activeSelf)
                {
                    Logger.Warning($"Cannot add Customer to active GameObject for {npc.ID}");
                    return null;
                }

                var existing = npc.gameObject.GetComponent<Customer>();
                if (existing != null)
                    return existing;

                var customerData = GetOrCreateCustomerData();
                if (customerData == null)
                {
                    Logger.Error("Cannot add Customer — no CustomerData available");
                    return null;
                }

                var customer = npc.gameObject.AddComponent<Customer>();
                customer.SetCustData(customerData);
                return customer;
            }
            catch (Exception ex)
            {
                Logger.Error($"AddCustomerComponent failed for {npc?.ID}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Adds a Customer component to an already-active NPC. Briefly deactivates
        /// the GameObject so customerData can be set before Awake fires.
        /// </summary>
        public static Customer AddCustomerComponentToActive(NPC npc)
        {
            if (npc == null || npc.gameObject == null)
                return null;

            try
            {
                var existing = npc.gameObject.GetComponent<Customer>();
                if (existing != null)
                    return existing;

                var customerData = GetOrCreateCustomerData();
                if (customerData == null)
                {
                    Logger.Error("Cannot add Customer to active NPC — no CustomerData available");
                    return null;
                }

                npc.gameObject.SetActive(false);
                var customer = npc.gameObject.AddComponent<Customer>();
                customer.SetCustData(customerData);
                npc.gameObject.SetActive(true);

                IsolateCustomerComponent(npc);
                return customer;
            }
            catch (Exception ex)
            {
                try { if (npc?.gameObject != null && !npc.gameObject.activeSelf) npc.gameObject.SetActive(true); }
                catch { }
                Logger.Error($"AddCustomerComponentToActive failed for {npc?.ID}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Isolates an NPC's Customer component from the vanilla deal system.
        /// Removes from customer tracking lists and unsubscribes from TimeManager
        /// events so the NPC doesn't affect the global deal throttle.
        /// </summary>
        public static void IsolateCustomerComponent(NPC npc)
        {
            try
            {
                var customer = npc?.gameObject?.GetComponent<Customer>();
                if (customer == null) return;

                Customer.UnlockedCustomers.Remove(customer);
                Customer.LockedCustomers.Remove(customer);

                try
                {
                    var tm = NetworkSingleton<ScheduleOne.GameTime.TimeManager>.Instance;
                    if (tm != null)
                    {
                        RemoveActionsForTarget(tm.onMinutePass, customer);
                        RemoveActionsForTarget(tm.onTick, customer);
                    }
                }
                catch { }

                customer.SetTimeSinceLastDealOffered(0);
                customer.SetTimeSinceLastDealCompleted(0);
            }
            catch (Exception ex)
            {
                Logger.Warning($"IsolateCustomerComponent failed for {npc?.ID}: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the Customer component from an NPC.
        /// </summary>
        public static Customer GetCustomerComponent(NPC npc) =>
            npc?.gameObject?.GetComponent<Customer>();

        /// <summary>
        /// Removes all Action delegates from an ActionList whose target matches
        /// the given object. Used to unsubscribe NPCs from TimeManager events.
        /// </summary>
#if IL2CPP
        private static void RemoveActionsForTarget(Il2Cpp.ActionList actionList, GameSystem.Object target)
#else
        private static void RemoveActionsForTarget(ActionList actionList, object target)
#endif
        {
            if (actionList == null) return;
            try
            {
                var list = actionList.GetInvocationList();
                if (list == null) return;

                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var action = list[i];
                    if (action?.Target == target)
                        list.RemoveAt(i);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"RemoveActionsForTarget failed: {ex.Message}");
            }
        }

        // =====================================================================
        //  Appearance generation
        // =====================================================================

        // Face expressions (required to avoid black face)
        internal static readonly string[] FaceExpressions = {
            "Avatar/Layers/Face/Face_Neutral",
            "Avatar/Layers/Face/Face_NeutralPout",
            "Avatar/Layers/Face/Face_SlightSmile",
            "Avatar/Layers/Face/Face_SlightFrown",
            "Avatar/Layers/Face/Face_SmugPout"
        };

        // Safe clothing options (excludes police apparel)
        private static readonly string[] SafeShirts = {
            "Avatar/Layers/Top/T-Shirt",
            "Avatar/Layers/Top/V-Neck",
            "Avatar/Layers/Top/Buttonup",
            "Avatar/Layers/Top/RolledButtonUp",
            "Avatar/Layers/Top/FlannelButtonUp",
            "Avatar/Layers/Top/Tucked T-Shirt"
        };

        private static readonly string[] SafePants = {
            "Avatar/Layers/Bottom/Jeans",
            "Avatar/Layers/Bottom/CargoPants",
            "Avatar/Layers/Bottom/Jorts"
        };

        internal static readonly string[] MaleHairStyles = {
            "Avatar/Hair/buzzcut/BuzzCut",
            "Avatar/Hair/closebuzzcut/CloseBuzzCut",
            "Avatar/Hair/franklin/Franklin",
            "Avatar/Hair/spiky/Spiky",
            "Avatar/Hair/peaked/Peaked",
            "Avatar/Hair/tony/Tony",
            "Avatar/Hair/mohawk/Mohawk",
            "Avatar/Hair/receding/Receding",
            "Avatar/Hair/afro/Afro",
            "Avatar/Hair/bowlcut/BowlCut",
        };

        internal static readonly string[] FemaleHairStyles = {
            "Avatar/Hair/bun/Bun",
            "Avatar/Hair/highbun/HighBun",
            "Avatar/Hair/lowbun/LowBun",
            "Avatar/Hair/fringeponytail/FringePonyTail",
            "Avatar/Hair/messybob/MessyBob",
            "Avatar/Hair/sidepartbob/SidePartBob",
            "Avatar/Hair/shoulderlength/ShoulderLength",
            "Avatar/Hair/longcurly/LongCurly",
            "Avatar/Hair/doubletopknot/DoubleTopKnot",
            "Avatar/Hair/afro/Afro",
            "Avatar/Hair/midfringe/MidFringe",
        };

        private static readonly string[] ShoeStyles = {
            "Avatar/Accessories/Feet/Sneakers/Sneakers",
            "Avatar/Accessories/Feet/CombatBoots/CombatBoots",
            "Avatar/Accessories/Feet/DressShoes/DressShoes",
            "Avatar/Accessories/Feet/Sandals/Sandals",
        };

        internal static readonly Color[] SkinTones = {
            new Color(0.96f, 0.87f, 0.78f),
            new Color(0.92f, 0.80f, 0.70f),
            new Color(0.85f, 0.70f, 0.55f),
            new Color(0.75f, 0.58f, 0.42f),
            new Color(0.65f, 0.48f, 0.35f),
            new Color(0.55f, 0.40f, 0.28f),
            new Color(0.48f, 0.35f, 0.25f),
            new Color(0.42f, 0.30f, 0.22f),
        };

        /// <summary>
        /// Determines gender from a seed (first RNG draw). Returns 0-1 float where >= 0.5 is female.
        /// Uses isolated RNG state so it can be called independently from GenerateRandomAppearance.
        /// </summary>
        public static float DetermineGender(int seed)
        {
            var state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            float gender = UnityEngine.Random.Range(0f, 1f);
            UnityEngine.Random.state = state;
            return gender;
        }

        /// <summary>
        /// Generates a deterministic random appearance for an NPC using the given seed.
        /// Excludes police apparel to prevent confusion with game officers.
        /// </summary>
        public static void GenerateRandomAppearance(NPC npc, int seed)
        {
            if (npc?.Avatar == null)
            {
                Logger.Warning("Cannot generate appearance: NPC or Avatar is null");
                return;
            }

            try
            {
                var state = UnityEngine.Random.state;
                UnityEngine.Random.InitState(seed);

                var settings = npc.Avatar.CurrentSettings;
                if (settings == null)
                    settings = ScriptableObject.CreateInstance<AvatarSettings>();

                // Ensure IL2CPP lists are initialized
                if (settings.FaceLayerSettings == null)
                    settings.FaceLayerSettings = new GameSystem.Collections.Generic.List<AvatarSettings.LayerSetting>();
                if (settings.BodyLayerSettings == null)
                    settings.BodyLayerSettings = new GameSystem.Collections.Generic.List<AvatarSettings.LayerSetting>();
                if (settings.AccessorySettings == null)
                    settings.AccessorySettings = new GameSystem.Collections.Generic.List<AvatarSettings.AccessorySetting>();

                // Gender MUST be the first draw (matches DetermineGender for host/client sync)
                settings.Gender = UnityEngine.Random.Range(0f, 1f);
                bool isFemale = settings.Gender >= 0.5f;

                var skinTone = SkinTones[UnityEngine.Random.Range(0, SkinTones.Length)];
                settings.SkinColor = skinTone;

                var faceColor = new Color(
                    skinTone.r * 0.92f,
                    skinTone.g * 0.88f,
                    skinTone.b * 0.85f);

                settings.Height = UnityEngine.Random.Range(0.9f, 1.1f);
                settings.Weight = UnityEngine.Random.Range(0.3f, 0.7f);

                // Hair color (natural tones)
                float hairHue = UnityEngine.Random.value;
                Color hairColor;
                if (hairHue < 0.4f)
                    hairColor = new Color(0.1f, 0.08f, 0.06f);
                else if (hairHue < 0.7f)
                    hairColor = new Color(0.35f, 0.22f, 0.12f);
                else if (hairHue < 0.9f)
                    hairColor = new Color(0.7f, 0.55f, 0.35f);
                else
                    hairColor = new Color(0.55f, 0.25f, 0.15f);
                settings.HairColor = hairColor;

                var hairPool = isFemale ? FemaleHairStyles : MaleHairStyles;
                settings.HairPath = hairPool[UnityEngine.Random.Range(0, hairPool.Length)];

                settings.EyeBallTint = Color.white;
                settings.PupilDilation = UnityEngine.Random.Range(0.5f, 0.8f);
                settings.EyebrowScale = UnityEngine.Random.Range(0.8f, 1.1f);
                settings.EyebrowThickness = UnityEngine.Random.Range(0.7f, 1.2f);

                settings.LeftEyeLidColor = skinTone;
                settings.RightEyeLidColor = skinTone;

                var eyeConfig = new Eye.EyeLidConfiguration
                {
                    topLidOpen = 0.5f,
                    bottomLidOpen = 0.5f
                };
                settings.LeftEyeRestingState = eyeConfig;
                settings.RightEyeRestingState = eyeConfig;

                settings.FaceLayerSettings?.Clear();
                settings.BodyLayerSettings?.Clear();
                settings.AccessorySettings?.Clear();

                // Face layer (prevents black face)
                if (settings.FaceLayerSettings != null && FaceExpressions.Length > 0)
                {
                    var faceLayer = new AvatarSettings.LayerSetting();
                    faceLayer.layerPath = FaceExpressions[UnityEngine.Random.Range(0, FaceExpressions.Length)];
                    faceLayer.layerTint = faceColor;
                    settings.FaceLayerSettings.Add(faceLayer);
                }

                // Clothing
                var shirtColor = RandomClothingColor();
                var pantsColor = RandomClothingColor();

                if (settings.BodyLayerSettings != null && SafeShirts.Length > 0)
                {
                    var shirtLayer = new AvatarSettings.LayerSetting();
                    shirtLayer.layerPath = SafeShirts[UnityEngine.Random.Range(0, SafeShirts.Length)];
                    shirtLayer.layerTint = shirtColor;
                    settings.BodyLayerSettings.Add(shirtLayer);
                }

                if (settings.BodyLayerSettings != null && SafePants.Length > 0)
                {
                    var pantsLayer = new AvatarSettings.LayerSetting();
                    pantsLayer.layerPath = SafePants[UnityEngine.Random.Range(0, SafePants.Length)];
                    pantsLayer.layerTint = pantsColor;
                    settings.BodyLayerSettings.Add(pantsLayer);
                }

                if (settings.AccessorySettings != null && ShoeStyles.Length > 0)
                {
                    var shoeColor = RandomClothingColor();
                    var shoeSetting = new AvatarSettings.AccessorySetting();
                    shoeSetting.path = ShoeStyles[UnityEngine.Random.Range(0, ShoeStyles.Length)];
                    shoeSetting.color = shoeColor;
                    settings.AccessorySettings.Add(shoeSetting);
                }

                npc.Avatar.LoadAvatarSettings(settings);
                UnityEngine.Random.state = state;
            }
            catch (Exception ex)
            {
                Logger.Warning($"GenerateRandomAppearance failed for {npc.ID}: {ex.Message}");
            }
        }

        /// <summary>
        /// Generates muted, realistic clothing colors.
        /// </summary>
        private static Color RandomClothingColor()
        {
            float colorType = UnityEngine.Random.value;

            if (colorType < 0.25f)
            {
                float gray = UnityEngine.Random.Range(0.1f, 0.5f);
                return new Color(gray, gray, gray);
            }
            else if (colorType < 0.45f)
            {
                return new Color(
                    UnityEngine.Random.Range(0.1f, 0.3f),
                    UnityEngine.Random.Range(0.15f, 0.35f),
                    UnityEngine.Random.Range(0.3f, 0.5f));
            }
            else if (colorType < 0.6f)
            {
                return new Color(
                    UnityEngine.Random.Range(0.4f, 0.6f),
                    UnityEngine.Random.Range(0.3f, 0.45f),
                    UnityEngine.Random.Range(0.2f, 0.35f));
            }
            else if (colorType < 0.75f)
            {
                return new Color(
                    UnityEngine.Random.Range(0.2f, 0.4f),
                    UnityEngine.Random.Range(0.3f, 0.45f),
                    UnityEngine.Random.Range(0.15f, 0.3f));
            }
            else if (colorType < 0.85f)
            {
                return new Color(
                    UnityEngine.Random.Range(0.4f, 0.6f),
                    UnityEngine.Random.Range(0.15f, 0.25f),
                    UnityEngine.Random.Range(0.15f, 0.25f));
            }
            else
            {
                float white = UnityEngine.Random.Range(0.8f, 0.95f);
                return new Color(white, white * 0.97f, white * 0.93f);
            }
        }
    }
}
