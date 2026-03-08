using MelonLoader;
using MelonLoader.Utils;
using OverTheCounter.Utilities;
using S1API.Utils;
using UnityEngine;
using UnityEngine.AI;
using System;
using System.Collections.Generic;
using System.IO;

#if IL2CPP
using Il2CppFishNet.Object;
using Il2CppFishNet.Managing;
using Il2CppFishNet;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Messaging;
#else
using FishNet.Object;
using FishNet.Managing;
using FishNet;
using ScheduleOne.NPCs;
using ScheduleOne.DevUtilities;
using ScheduleOne.AvatarFramework;
using ScheduleOne.Economy;
using ScheduleOne.Messaging;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Handles spawning drifter NPCs via IL2CPP directly, bypassing S1API's singleton pattern.
    /// Clones the game's "CivilianNPC" prefab for each new drifter instance.
    /// </summary>
    public static class DrifterSpawner
    {
        private static NetworkObject _cachedBasePrefab;
        private static bool _prefabSearched;

        internal static readonly Dictionary<string, MSGConversation> DrifterConversations = new();

        /// <summary>
        /// Finds and caches the CivilianNPC prefab from the network manager's spawnable prefabs.
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
                    OTCLog.Error(OTCLog.Systems.Drifter, "NetworkManager not found");
                    return null;
                }

                var spawnablePrefabs = networkManager.SpawnablePrefabs;
                if (spawnablePrefabs == null)
                {
                    OTCLog.Error(OTCLog.Systems.Drifter, "SpawnablePrefabs not available");
                    return null;
                }

                int count = spawnablePrefabs.GetObjectCount();
                OTCLog.Msg(OTCLog.Systems.Drifter, $"Searching {count} spawnable prefabs for CivilianNPC...");

                for (int i = 0; i < count; i++)
                {
                    var obj = spawnablePrefabs.GetObject(true, i);
                    if (obj == null || obj.gameObject == null)
                        continue;

                    string name = obj.gameObject.name;
                    if (name == "CivilianNPC")
                    {
                        _cachedBasePrefab = obj;
                        OTCLog.Msg(OTCLog.Systems.Drifter, "Found CivilianNPC prefab");
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
                        OTCLog.Msg(OTCLog.Systems.Drifter, $"Using fallback NPC prefab: {obj.gameObject.name}");
                        return _cachedBasePrefab;
                    }
                }

                OTCLog.Error(OTCLog.Systems.Drifter, "No suitable NPC prefab found");
                return null;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Drifter, $"GetBasePrefab failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Spawns a new drifter NPC at the specified position.
        /// </summary>
        /// <param name="id">Unique identifier for this drifter</param>
        /// <param name="firstName">First name for the NPC</param>
        /// <param name="lastName">Last name for the NPC</param>
        /// <param name="position">World position to spawn at</param>
        /// <param name="rotation">Facing rotation</param>
        /// <returns>The spawned NPC, or null on failure</returns>
        public static NPC Spawn(string id, string firstName, string lastName, Vector3 position, Quaternion rotation)
        {
            try
            {
                var basePrefab = GetBasePrefab();
                if (basePrefab == null)
                {
                    OTCLog.Error(OTCLog.Systems.Drifter, $"Cannot spawn {id}: no base prefab");
                    return null;
                }

                // Clone the prefab
                var clone = UnityEngine.Object.Instantiate(basePrefab);
                if (clone == null || clone.gameObject == null)
                {
                    OTCLog.Error(OTCLog.Systems.Drifter, $"Failed to instantiate prefab for {id}");
                    return null;
                }

                clone.gameObject.name = $"Drifter_{id}";
                clone.gameObject.SetActive(false);

                // Parent to NPC container
                var npcManager = NetworkSingleton<NPCManager>.Instance;
                if (npcManager?.NPCContainer != null)
                {
                    clone.gameObject.transform.SetParent(npcManager.NPCContainer, false);
                }

                // Get NPC component
                var npc = clone.gameObject.GetComponent<NPC>();
                if (npc == null)
                {
                    OTCLog.Error(OTCLog.Systems.Drifter, $"Cloned object missing NPC component for {id}");
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
                    {
                        NPCManager.NPCRegistry.Remove(npc);
                    }
                }
                catch { }

                // Snap position to NavMesh
                Vector3 spawnPos = position;
                if (NavMesh.SamplePosition(position, out NavMeshHit hit, 10f, NavMesh.AllAreas))
                {
                    spawnPos = hit.position;
                }

                // Set position
                clone.gameObject.transform.position = spawnPos;
                clone.gameObject.transform.rotation = rotation;

                // Add Customer component BEFORE activation (so customerData can be set before Awake)
                try
                {
                    AddCustomerComponent(npc);
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"Failed to add Customer component for {id}: {ex.Message}");
                }

                // Activate (this triggers Awake + Start on all components)
                clone.gameObject.SetActive(true);

                // Isolate the Customer component from the vanilla deal system.
                // Customer.Start() subscribes to onMinutePass and adds itself to
                // LockedCustomers/UnlockedCustomers — both can interfere with
                // the global deal throttle (MinsSinceLastDealOfferedAllCustomers)
                // and the staggered minute-tick invocation chain.
                IsolateDrifterCustomer(npc);

                // Register with game
                try
                {
                    if (NPCManager.NPCRegistry != null && !NPCManager.NPCRegistry.Contains(npc))
                    {
                        NPCManager.NPCRegistry.Add(npc);
                    }
                }
                catch { }

                // Network-spawn the NPC so FishNet initializes all NetworkBehaviour
                // components (Movement, Combat, etc.). Without this, the NPC can't move
                // or render. FishNet replicates a "bare" copy to clients — appearance is
                // applied client-side in ApplyDrifterState via DrifterInstance.Adopt().
                try
                {
                    if (InstanceFinder.ServerManager != null)
                    {
                        InstanceFinder.ServerManager.Spawn(clone);
                        OTCLog.Msg(OTCLog.Systems.Drifter, $"ServerManager.Spawn completed for {id}");
                    }
                    else
                    {
                        OTCLog.Warning(OTCLog.Systems.Drifter, $"ServerManager is null, {id} may not function correctly");
                    }
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"ServerManager.Spawn failed for {id}: {ex.Message}");
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
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"Movement setup failed for {id}: {ex.Message}");
                }

                OTCLog.Msg(OTCLog.Systems.Drifter, $"Spawned {id} ({firstName} {lastName}) at {spawnPos}");
                return npc;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Drifter, $"Spawn failed for {id}: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

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

        // Hair style pools per gender
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

        // Shoe options (AccessorySettings, loaded via Resources.Load)
        private static readonly string[] ShoeStyles = {
            "Avatar/Accessories/Feet/Sneakers/Sneakers",
            "Avatar/Accessories/Feet/CombatBoots/CombatBoots",
            "Avatar/Accessories/Feet/DressShoes/DressShoes",
            "Avatar/Accessories/Feet/Sandals/Sandals",
        };

        // Realistic skin tone presets (darkest tones removed — face features become indistinguishable)
        internal static readonly Color[] SkinTones = {
            new Color(0.96f, 0.87f, 0.78f), // Very light/fair
            new Color(0.92f, 0.80f, 0.70f), // Light
            new Color(0.85f, 0.70f, 0.55f), // Light-medium
            new Color(0.75f, 0.58f, 0.42f), // Medium tan
            new Color(0.65f, 0.48f, 0.35f), // Medium
            new Color(0.55f, 0.40f, 0.28f), // Medium-dark
            new Color(0.48f, 0.35f, 0.25f), // Dark
            new Color(0.42f, 0.30f, 0.22f), // Darker
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
        /// Generates random appearance for an NPC using IL2CPP AvatarSettings.
        /// Excludes police apparel to prevent confusion.
        /// </summary>
        public static void GenerateRandomAppearance(NPC npc, int seed)
        {
            if (npc?.Avatar == null)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, "Cannot generate appearance: NPC or Avatar is null");
                return;
            }

            try
            {
                // Use seed for deterministic randomization (host/client sync)
                var state = UnityEngine.Random.state;
                UnityEngine.Random.InitState(seed);

                // Get or create avatar settings
                var settings = npc.Avatar.CurrentSettings;
                if (settings == null)
                {
                    settings = ScriptableObject.CreateInstance<AvatarSettings>();
                }

                // Ensure IL2CPP lists are initialized (ScriptableObject.CreateInstance
                // doesn't auto-init list fields, and prefab settings may also have nulls)
                if (settings.FaceLayerSettings == null)
                    settings.FaceLayerSettings = new GameSystem.Collections.Generic.List<AvatarSettings.LayerSetting>();
                if (settings.BodyLayerSettings == null)
                    settings.BodyLayerSettings = new GameSystem.Collections.Generic.List<AvatarSettings.LayerSetting>();
                if (settings.AccessorySettings == null)
                    settings.AccessorySettings = new GameSystem.Collections.Generic.List<AvatarSettings.AccessorySetting>();

                // Gender MUST be the first draw (matches DetermineGender for host/client sync)
                settings.Gender = UnityEngine.Random.Range(0f, 1f);
                bool isFemale = settings.Gender >= 0.5f;

                // Pick a realistic skin tone
                var skinTone = SkinTones[UnityEngine.Random.Range(0, SkinTones.Length)];
                settings.SkinColor = skinTone;

                // Face color should match skin tone (slightly adjusted for face shading)
                var faceColor = new Color(
                    skinTone.r * 0.92f,
                    skinTone.g * 0.88f,
                    skinTone.b * 0.85f
                );

                // Basic body properties
                settings.Height = UnityEngine.Random.Range(0.9f, 1.1f);
                settings.Weight = UnityEngine.Random.Range(0.3f, 0.7f);

                // Hair color (natural tones: black, brown, blonde, red)
                float hairHue = UnityEngine.Random.value;
                Color hairColor;
                if (hairHue < 0.4f) // Black/dark brown
                    hairColor = new Color(0.1f, 0.08f, 0.06f);
                else if (hairHue < 0.7f) // Brown
                    hairColor = new Color(0.35f, 0.22f, 0.12f);
                else if (hairHue < 0.9f) // Blonde
                    hairColor = new Color(0.7f, 0.55f, 0.35f);
                else // Red
                    hairColor = new Color(0.55f, 0.25f, 0.15f);
                settings.HairColor = hairColor;

                // Hair style based on gender
                var hairPool = isFemale ? FemaleHairStyles : MaleHairStyles;
                settings.HairPath = hairPool[UnityEngine.Random.Range(0, hairPool.Length)];

                settings.EyeBallTint = Color.white;
                settings.PupilDilation = UnityEngine.Random.Range(0.5f, 0.8f);
                settings.EyebrowScale = UnityEngine.Random.Range(0.8f, 1.1f);
                settings.EyebrowThickness = UnityEngine.Random.Range(0.7f, 1.2f);

                // Set eyelid colors to match skin tone
                settings.LeftEyeLidColor = skinTone;
                settings.RightEyeLidColor = skinTone;

                // CRITICAL: Set eyes to be open (0.5 = normal open)
                var eyeConfig = new Eye.EyeLidConfiguration
                {
                    topLidOpen = 0.5f,
                    bottomLidOpen = 0.5f
                };
                settings.LeftEyeRestingState = eyeConfig;
                settings.RightEyeRestingState = eyeConfig;

                // Clear existing layers to start fresh
                settings.FaceLayerSettings?.Clear();
                settings.BodyLayerSettings?.Clear();
                settings.AccessorySettings?.Clear();

                // CRITICAL: Add face layer (prevents black face)
                if (settings.FaceLayerSettings != null && FaceExpressions.Length > 0)
                {
                    var faceLayer = new AvatarSettings.LayerSetting();
                    faceLayer.layerPath = FaceExpressions[UnityEngine.Random.Range(0, FaceExpressions.Length)];
                    faceLayer.layerTint = faceColor;
                    settings.FaceLayerSettings.Add(faceLayer);
                }

                // Add clothing layers (safe options only - no police apparel)
                var shirtColor = RandomClothingColor();
                var pantsColor = RandomClothingColor();

                // Add shirt
                if (settings.BodyLayerSettings != null && SafeShirts.Length > 0)
                {
                    var shirtLayer = new AvatarSettings.LayerSetting();
                    shirtLayer.layerPath = SafeShirts[UnityEngine.Random.Range(0, SafeShirts.Length)];
                    shirtLayer.layerTint = shirtColor;
                    settings.BodyLayerSettings.Add(shirtLayer);
                }

                // Add pants
                if (settings.BodyLayerSettings != null && SafePants.Length > 0)
                {
                    var pantsLayer = new AvatarSettings.LayerSetting();
                    pantsLayer.layerPath = SafePants[UnityEngine.Random.Range(0, SafePants.Length)];
                    pantsLayer.layerTint = pantsColor;
                    settings.BodyLayerSettings.Add(pantsLayer);
                }

                // Add shoes as accessory
                if (settings.AccessorySettings != null && ShoeStyles.Length > 0)
                {
                    var shoeColor = RandomClothingColor();
                    var shoeSetting = new AvatarSettings.AccessorySetting();
                    shoeSetting.path = ShoeStyles[UnityEngine.Random.Range(0, ShoeStyles.Length)];
                    shoeSetting.color = shoeColor;
                    settings.AccessorySettings.Add(shoeSetting);
                }

                // Apply settings to avatar
                npc.Avatar.LoadAvatarSettings(settings);

                // Restore random state
                UnityEngine.Random.state = state;

                OTCLog.Msg(OTCLog.Systems.Drifter, $"Generated random appearance for {npc.ID}");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"GenerateRandomAppearance failed for {npc.ID}: {ex.Message}");
            }
        }

        /// <summary>
        /// Generates muted, realistic clothing colors.
        /// </summary>
        private static Color RandomClothingColor()
        {
            // Pick from common clothing color families
            float colorType = UnityEngine.Random.value;

            if (colorType < 0.25f) // Grays/blacks
            {
                float gray = UnityEngine.Random.Range(0.1f, 0.5f);
                return new Color(gray, gray, gray);
            }
            else if (colorType < 0.45f) // Blues (denim, navy)
            {
                return new Color(
                    UnityEngine.Random.Range(0.1f, 0.3f),
                    UnityEngine.Random.Range(0.15f, 0.35f),
                    UnityEngine.Random.Range(0.3f, 0.5f)
                );
            }
            else if (colorType < 0.6f) // Browns/tans
            {
                return new Color(
                    UnityEngine.Random.Range(0.4f, 0.6f),
                    UnityEngine.Random.Range(0.3f, 0.45f),
                    UnityEngine.Random.Range(0.2f, 0.35f)
                );
            }
            else if (colorType < 0.75f) // Greens (olive, forest)
            {
                return new Color(
                    UnityEngine.Random.Range(0.2f, 0.4f),
                    UnityEngine.Random.Range(0.3f, 0.45f),
                    UnityEngine.Random.Range(0.15f, 0.3f)
                );
            }
            else if (colorType < 0.85f) // Reds/burgundy (muted)
            {
                return new Color(
                    UnityEngine.Random.Range(0.4f, 0.6f),
                    UnityEngine.Random.Range(0.15f, 0.25f),
                    UnityEngine.Random.Range(0.15f, 0.25f)
                );
            }
            else // White/cream
            {
                float white = UnityEngine.Random.Range(0.8f, 0.95f);
                return new Color(white, white * 0.97f, white * 0.93f);
            }
        }

        /// <summary>
        /// Initializes the NPC's messaging system so it can send text messages.
        /// </summary>
        public static void InitializeMessaging(NPC npc)
        {
            if (npc == null)
                return;

            try
            {
                // Check if conversation already exists
                if (npc.GetMSGConversation() != null)
                {
                    OTCLog.Msg(OTCLog.Systems.Drifter, $"Messaging already initialized for {npc.ID}");
                    return;
                }

                // Create a new conversation for this NPC
                var conversation = new MSGConversation(npc, npc.fullName);
                npc.SetMSGConversation(conversation);

                // Set as known so it shows up in phone
                conversation.SetIsKnown(true);
                DrifterConversations[npc.ID] = conversation;

                OTCLog.Msg(OTCLog.Systems.Drifter, $"Initialized messaging for {npc.ID}");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"InitializeMessaging failed for {npc.ID}: {ex.Message}");
            }
        }

        /// <summary>
        /// Borrows a voice database from an existing NPC.
        /// </summary>
        public static void EnsureVoiceDatabase(NPC npc)
        {
            if (npc?.VoiceOverEmitter == null)
                return;

            if (npc.VoiceOverEmitter.GetDatabase() != null)
                return;

            try
            {
                var allNpcs = UnityEngine.Object.FindObjectsOfType<NPC>();
                foreach (var other in allNpcs)
                {
                    if (other.GetInstanceID() == npc.GetInstanceID())
                        continue;

                    if (other.VoiceOverEmitter?.GetDatabase() != null)
                    {
                        npc.VoiceOverEmitter.SetDatabase(other.VoiceOverEmitter.GetDatabase(), false);
                        OTCLog.Msg(OTCLog.Systems.Drifter, $"Borrowed voice database for {npc.ID}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"EnsureVoiceDatabase failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Cached drifter profile icon sprite loaded from DrifterProfileIcon.png.
        /// </summary>
        private static Sprite _drifterIcon;

        /// <summary>
        /// Sets the drifter profile icon on the NPC's MugshotSprite for messaging.
        /// </summary>
        public static void SetDrifterIcon(NPC npc)
        {
            if (npc == null) return;
            var icon = GetDrifterIcon();
            if (icon != null)
                npc.MugshotSprite = icon;
        }

        private static Sprite GetDrifterIcon()
        {
            if (_drifterIcon != null)
                return _drifterIcon;

            try
            {
                string iconPath = Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "DrifterProfileIcon.png");
                _drifterIcon = ImageUtils.LoadImage(iconPath);
                if (_drifterIcon != null)
                {
                    OTCLog.Msg(OTCLog.Systems.Drifter, "Loaded ProfileIcon.png");
                }
                else
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, "ProfileIcon.png not found or failed to load");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"Failed to load icon: {ex.Message}");
            }

            return _drifterIcon;
        }

        /// <summary>
        /// Despawns and destroys a drifter NPC.
        /// </summary>
        public static void Despawn(NPC npc)
        {
            if (npc == null)
                return;

            try
            {
                string id = npc.ID ?? "unknown";

                // Remove from registry
                try
                {
                    NPCManager.NPCRegistry?.Remove(npc);
                }
                catch { }

                // Network despawn if this is a FishNet-spawned object (host)
                try
                {
                    var netObj = npc.gameObject?.GetComponent<NetworkObject>();
                    if (netObj != null && InstanceFinder.ServerManager != null && netObj.IsSpawned)
                    {
                        InstanceFinder.ServerManager.Despawn(netObj);
                        OTCLog.Msg(OTCLog.Systems.Drifter, $"ServerManager.Despawn completed for {id}");
                    }
                    else if (npc.gameObject != null)
                    {
                        UnityEngine.Object.Destroy(npc.gameObject);
                    }
                }
                catch
                {
                    // Fallback: destroy directly
                    if (npc.gameObject != null)
                        UnityEngine.Object.Destroy(npc.gameObject);
                }

                OTCLog.Msg(OTCLog.Systems.Drifter, $"Despawned {id}");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Drifter, $"Despawn failed: {ex.Message}");
            }
        }

        // Cached CustomerData for drifters - created once and reused
        private static ScheduleOne.Economy.CustomerData _drifterCustomerData;

        /// <summary>
        /// Gets or creates a CustomerData ScriptableObject for drifters.
        /// </summary>
        private static ScheduleOne.Economy.CustomerData GetOrCreateDrifterCustomerData()
        {
            if (_drifterCustomerData != null)
                return _drifterCustomerData;

            try
            {
                _drifterCustomerData = ScriptableObject.CreateInstance<ScheduleOne.Economy.CustomerData>();

                // Set minimal defaults
                _drifterCustomerData.MinWeeklySpend = 100f;
                _drifterCustomerData.MaxWeeklySpend = 500f;
                _drifterCustomerData.MinOrdersPerWeek = 1;
                _drifterCustomerData.MaxOrdersPerWeek = 1;
                _drifterCustomerData.Standards = ScheduleOne.Economy.ECustomerStandard.Moderate;
                _drifterCustomerData.CanBeDirectlyApproached = false;
                _drifterCustomerData.BaseAddiction = 0f;
                _drifterCustomerData.DependenceMultiplier = 0f;
                _drifterCustomerData.CallPoliceChance = 0f;

                // Create default affinity data
                _drifterCustomerData.DefaultAffinityData = new ScheduleOne.Economy.CustomerAffinityData();

                OTCLog.Msg(OTCLog.Systems.Drifter, "Created CustomerData");
                return _drifterCustomerData;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Drifter, $"Failed to create CustomerData: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Adds a Customer component to the NPC with proper CustomerData setup.
        /// MUST be called while the GameObject is inactive!
        /// </summary>
        public static Customer AddCustomerComponent(NPC npc)
        {
            if (npc == null || npc.gameObject == null)
                return null;

            try
            {
                // Verify object is inactive (Awake won't run until activated)
                if (npc.gameObject.activeSelf)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"Cannot add Customer to active GameObject for {npc.ID}");
                    return null;
                }

                // Check if already has Customer component
                var existing = npc.gameObject.GetComponent<Customer>();
                if (existing != null)
                {
                    OTCLog.Msg(OTCLog.Systems.Drifter, $"{npc.ID} already has Customer component");
                    return existing;
                }

                // Get or create CustomerData
                var customerData = GetOrCreateDrifterCustomerData();
                if (customerData == null)
                {
                    OTCLog.Error(OTCLog.Systems.Drifter, $"Cannot add Customer - no CustomerData available");
                    return null;
                }

                // Add Customer component (Awake won't run yet because object is inactive)
                var customer = npc.gameObject.AddComponent<Customer>();

                // Set customerData directly (IL2CPP exposes this as a property)
                customer.SetCustData(customerData);

                OTCLog.Msg(OTCLog.Systems.Drifter, $"Set CustomerData for {npc.ID}");

                return customer;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Drifter, $"AddCustomerComponent failed for {npc?.ID}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Adds a Customer component to an already-active NPC (client adoption path).
        /// Customer.Awake crashes on null customerData, so we briefly deactivate the
        /// GameObject, add the component + set data, then reactivate.
        /// </summary>
        public static Customer AddCustomerComponentToActive(NPC npc)
        {
            if (npc == null || npc.gameObject == null)
                return null;

            try
            {
                var existing = npc.gameObject.GetComponent<Customer>();
                if (existing != null)
                {
                    OTCLog.Msg(OTCLog.Systems.Drifter, $"{npc.ID} already has Customer component (active)");
                    return existing;
                }

                var customerData = GetOrCreateDrifterCustomerData();
                if (customerData == null)
                {
                    OTCLog.Error(OTCLog.Systems.Drifter, $"Cannot add Customer to active NPC - no CustomerData available");
                    return null;
                }

                // Briefly deactivate so AddComponent doesn't trigger Awake immediately.
                // Customer.Awake reads customerData which must be set first.
                npc.gameObject.SetActive(false);

                var customer = npc.gameObject.AddComponent<Customer>();
                customer.SetCustData(customerData);

                npc.gameObject.SetActive(true);

                // Isolate from vanilla deal system (see IsolateDrifterCustomer)
                IsolateDrifterCustomer(npc);

                OTCLog.Msg(OTCLog.Systems.Drifter, $"Added Customer component to active NPC {npc.ID}");
                return customer;
            }
            catch (Exception ex)
            {
                // Ensure we reactivate even on failure
                try { if (npc?.gameObject != null && !npc.gameObject.activeSelf) npc.gameObject.SetActive(true); }
                catch { }
                OTCLog.Error(OTCLog.Systems.Drifter, $"AddCustomerComponentToActive failed for {npc?.ID}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Isolates a drifter's Customer component from the vanilla deal system.
        /// Customer.Awake adds to LockedCustomers, and Start() may add to
        /// UnlockedCustomers depending on NPC.RelationData.Unlocked (which is
        /// cloned from the CivilianNPC prefab). Being in either list lets the
        /// drifter participate in MinsSinceLastDealOfferedAllCustomers() — the
        /// global 5-minute throttle that gates ALL customer deal generation.
        /// The drifter's onMinutePass handler also occupies a slot in the
        /// staggered ActionList invocation chain; removing mid-chain during
        /// drifter destruction can shift indices and skip real customers.
        /// This method removes the drifter from both lists and unsubscribes
        /// from all TimeManager events, making it invisible to vanilla systems
        /// while keeping the component alive for the HandoverScreen.
        /// </summary>
        private static void IsolateDrifterCustomer(NPC npc)
        {
            try
            {
                var customer = npc?.gameObject?.GetComponent<Customer>();
                if (customer == null) return;

                // Remove from vanilla customer tracking lists.
                // This prevents the drifter from affecting the global deal throttle.
                Customer.UnlockedCustomers.Remove(customer);
                Customer.LockedCustomers.Remove(customer);

                // Unsubscribe from TimeManager events that drive vanilla deal logic.
                // onMinutePass/onTick are ActionLists. We iterate the internal list
                // and remove any delegates targeting this Customer component, since
                // the protected OnMinPass/OnTick methods aren't directly accessible.
                try
                {
                    var tm = NetworkSingleton<ScheduleOne.GameTime.TimeManager>.Instance;
                    if (tm != null)
                    {
                        RemoveActionsForTarget(tm.onMinutePass, customer);
                        RemoveActionsForTarget(tm.onTick, customer);
                    }
                }
                catch { /* TimeManager may not exist yet on clients */ }

                // Zero deal timers as belt-and-suspenders — even though we removed
                // from the lists, this ensures ShouldTryGenerateDeal returns false
                // if somehow the handler still fires.
                customer.SetTimeSinceLastDealOffered(0);
                customer.SetTimeSinceLastDealCompleted(0);

                OTCLog.Msg(OTCLog.Systems.Drifter, $"Isolated Customer from vanilla deal system: {npc.ID}");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"IsolateCustomer failed for {npc?.ID}: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes all Action delegates from an ActionList whose target is the
        /// given object. ActionList exposes its internal List via GetInvocationList().
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
                    {
                        list.RemoveAt(i);
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"RemoveActionsForTarget failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the Customer component from a drifter NPC.
        /// </summary>
        public static Customer GetCustomerComponent(NPC npc)
        {
            return npc?.gameObject?.GetComponent<Customer>();
        }

        /// <summary>
        /// Resets the prefab cache (call on scene transitions).
        /// </summary>
        public static void ResetCache()
        {
            _cachedBasePrefab = null;
            _prefabSearched = false;
        }
    }
}
