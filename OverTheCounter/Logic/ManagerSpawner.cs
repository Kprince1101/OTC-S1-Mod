using Il2CppFishNet.Object;
using Il2CppFishNet;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Messaging;
using MelonLoader;
using UnityEngine;
using UnityEngine.AI;
using System;
using System.Collections.Generic;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Handles spawning Manager NPCs via IL2CPP prefab cloning.
    /// Follows the same proven pattern as DrifterSpawner but without Customer components.
    /// </summary>
    public static class ManagerSpawner
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("ManagerSpawner");

        private static NetworkObject _cachedBasePrefab;
        private static bool _prefabSearched;

        // Name pools — distinct from drifter names to avoid confusion
        private static readonly string[] MaleFirstNames = {
            "Gordon", "Arthur", "Walter", "Dennis", "Harold", "Bernard", "Leonard",
            "Stanley", "Gerald", "Raymond", "Albert", "Warren", "Eugene", "Kenneth"
        };

        private static readonly string[] FemaleFirstNames = {
            "Helen", "Dorothy", "Margaret", "Ruth", "Patricia", "Beverly", "Shirley",
            "Gloria", "Mildred", "Eleanor", "Virginia", "Lorraine", "Evelyn", "Constance"
        };

        private static readonly string[] LastNames = {
            "Palmer", "Fletcher", "Whitfield", "Shelton", "Hartley", "Barton",
            "Crawford", "Thornton", "Langley", "Prescott", "Ashford", "Waverly",
            "Pemberton", "Calloway", "Drummond", "Hadley", "Sinclair", "Beckett"
        };

        /// <summary>
        /// Finds and caches the CivilianNPC prefab from the network manager's spawnable prefabs.
        /// Shared approach with DrifterSpawner but maintains its own cache.
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
                        Logger.Msg("Found CivilianNPC prefab");
                        return _cachedBasePrefab;
                    }
                }

                // Fallback: any prefab with NPC component
                for (int i = 0; i < count; i++)
                {
                    var obj = spawnablePrefabs.GetObject(true, i);
                    if (obj?.gameObject?.GetComponent<NPC>() != null)
                    {
                        _cachedBasePrefab = obj;
                        Logger.Msg($"Using fallback NPC prefab: {obj.gameObject.name}");
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

        /// <summary>
        /// Determines gender from a seed. Returns 0-1 float where >= 0.5 is female.
        /// Uses isolated RNG state for deterministic host/client sync.
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
        /// Derives the deterministic first/last name for a manager from its seed.
        /// Used by ManagerInstance.Create and ManagerInstance.Adopt for host/client sync.
        /// </summary>
        public static (string firstName, string lastName) GetManagerName(int seed)
        {
            float gender = DetermineGender(seed);
            bool isFemale = gender >= 0.5f;
            return (GetRandomFirstName(seed, isFemale), GetRandomLastName(seed));
        }

        private static string GetRandomFirstName(int seed, bool isFemale)
        {
            // Collect first names already in use by active managers
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mgr in ManagerInstance.Active.Values)
            {
                if (mgr.GameNpc != null && !string.IsNullOrEmpty(mgr.GameNpc.FirstName))
                    usedNames.Add(mgr.GameNpc.FirstName);
            }

            var state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            var pool = isFemale ? FemaleFirstNames : MaleFirstNames;

            // Try seeded pick first, fall back to scanning for unused name
            var name = pool[UnityEngine.Random.Range(0, pool.Length)];
            if (usedNames.Contains(name))
            {
                int startIdx = UnityEngine.Random.Range(0, pool.Length);
                for (int i = 0; i < pool.Length; i++)
                {
                    string candidate = pool[(startIdx + i) % pool.Length];
                    if (!usedNames.Contains(candidate))
                    {
                        name = candidate;
                        break;
                    }
                }
            }

            UnityEngine.Random.state = state;
            return name;
        }

        private static string GetRandomLastName(int seed)
        {
            var state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed + 2000); // offset from drifter seeds (+1000)
            var name = LastNames[UnityEngine.Random.Range(0, LastNames.Length)];
            UnityEngine.Random.state = state;
            return name;
        }

        /// <summary>
        /// Spawns a new Manager NPC at the specified position.
        /// </summary>
        public static NPC Spawn(string id, int seed, Vector3 position, Quaternion rotation)
        {
            try
            {
                var basePrefab = GetBasePrefab();
                if (basePrefab == null)
                {
                    Logger.Error($"Cannot spawn manager {id}: no base prefab");
                    return null;
                }

                var (firstName, lastName) = GetManagerName(seed);

                // Clone the prefab
                var clone = UnityEngine.Object.Instantiate(basePrefab);
                if (clone == null || clone.gameObject == null)
                {
                    Logger.Error($"Failed to instantiate prefab for manager {id}");
                    return null;
                }

                clone.gameObject.name = $"Manager_{id}";
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
                    Logger.Error($"Cloned object missing NPC component for manager {id}");
                    UnityEngine.Object.Destroy(clone.gameObject);
                    return null;
                }

                // Configure identity
                npc.ID = id;
                npc.FirstName = firstName;
                npc.LastName = lastName;

                // Remove from registry if auto-added
                try
                {
                    if (NPCManager.NPCRegistry != null && NPCManager.NPCRegistry.Contains(npc))
                        NPCManager.NPCRegistry.Remove(npc);
                }
                catch { }

                // Snap position to NavMesh
                Vector3 spawnPos = position;
                if (NavMesh.SamplePosition(position, out NavMeshHit hit, 10f, NavMesh.AllAreas))
                {
                    spawnPos = hit.position;
                }

                clone.gameObject.transform.position = spawnPos;
                clone.gameObject.transform.rotation = rotation;

                // Activate (triggers Awake on all components)
                clone.gameObject.SetActive(true);

                // Register with game
                try
                {
                    if (NPCManager.NPCRegistry != null && !NPCManager.NPCRegistry.Contains(npc))
                        NPCManager.NPCRegistry.Add(npc);
                }
                catch { }

                // Network-spawn via FishNet for multiplayer replication
                try
                {
                    if (InstanceFinder.ServerManager != null)
                    {
                        InstanceFinder.ServerManager.Spawn(clone);
                        Logger.Msg($"ServerManager.Spawn completed for manager {id}");
                    }
                    else
                    {
                        Logger.Warning($"ServerManager is null, manager {id} may not replicate");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"ServerManager.Spawn failed for manager {id}: {ex.Message}");
                }

                // Apply appearance
                ApplyAppearance(npc, seed);

                // Initialize messaging and voice
                InitializeMessaging(npc);
                EnsureVoiceDatabase(npc);

                // Warp to position
                try
                {
                    npc.Movement?.Warp(spawnPos);
                    npc.Movement?.Stop();
                    npc.Movement?.FaceDirection(rotation * Vector3.forward);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Movement setup failed for manager {id}: {ex.Message}");
                }

                Logger.Msg($"Spawned manager {id} ({firstName} {lastName}) at {spawnPos}");
                return npc;
            }
            catch (Exception ex)
            {
                Logger.Error($"Spawn failed for manager {id}: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        // Manager outfit: button-up shirt in professional colors with dark slacks and dress shoes
        private static readonly Color[] ShirtColors = {
            new Color(0.15f, 0.20f, 0.35f), // Navy
            new Color(0.20f, 0.20f, 0.22f), // Charcoal
            new Color(0.25f, 0.15f, 0.15f), // Dark burgundy
            new Color(0.12f, 0.25f, 0.18f), // Forest green
            new Color(0.85f, 0.82f, 0.75f), // Off-white/cream
            new Color(0.40f, 0.35f, 0.30f), // Brown
        };

        private static readonly Color[] PantsColors = {
            new Color(0.12f, 0.12f, 0.14f), // Near-black
            new Color(0.18f, 0.18f, 0.20f), // Dark charcoal
            new Color(0.22f, 0.20f, 0.18f), // Dark brown
            new Color(0.15f, 0.18f, 0.25f), // Dark navy
        };

        /// <summary>
        /// Applies deterministic Handler-style appearance to a Manager NPC.
        /// Uses button-up shirt, dark slacks, and dress shoes for a professional look.
        /// Biometrics (skin, hair, eyes) are randomized from seed for variety.
        /// </summary>
        public static void ApplyAppearance(NPC npc, int seed)
        {
            if (npc?.Avatar == null)
            {
                Logger.Warning("Cannot apply appearance: NPC or Avatar is null");
                return;
            }

            try
            {
                var state = UnityEngine.Random.state;
                UnityEngine.Random.InitState(seed);

                var settings = npc.Avatar.CurrentSettings;
                if (settings == null)
                    settings = ScriptableObject.CreateInstance<AvatarSettings>();

                if (settings.FaceLayerSettings == null)
                    settings.FaceLayerSettings = new Il2CppSystem.Collections.Generic.List<AvatarSettings.LayerSetting>();
                if (settings.BodyLayerSettings == null)
                    settings.BodyLayerSettings = new Il2CppSystem.Collections.Generic.List<AvatarSettings.LayerSetting>();
                if (settings.AccessorySettings == null)
                    settings.AccessorySettings = new Il2CppSystem.Collections.Generic.List<AvatarSettings.AccessorySetting>();

                // Gender MUST be the first draw (matches DetermineGender for host/client sync)
                settings.Gender = UnityEngine.Random.Range(0f, 1f);
                bool isFemale = settings.Gender >= 0.5f;

                // Biometrics — reuse DrifterSpawner skin tones and hair logic
                var skinTone = DrifterSpawner.SkinTones[UnityEngine.Random.Range(0, DrifterSpawner.SkinTones.Length)];
                settings.SkinColor = skinTone;

                var faceColor = new Color(skinTone.r * 0.92f, skinTone.g * 0.88f, skinTone.b * 0.85f);

                settings.Height = UnityEngine.Random.Range(0.95f, 1.1f);
                settings.Weight = UnityEngine.Random.Range(0.3f, 0.55f);

                // Hair
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

                var hairPool = isFemale ? DrifterSpawner.FemaleHairStyles : DrifterSpawner.MaleHairStyles;
                settings.HairPath = hairPool[UnityEngine.Random.Range(0, hairPool.Length)];

                settings.EyeBallTint = Color.white;
                settings.PupilDilation = UnityEngine.Random.Range(0.5f, 0.8f);
                settings.EyebrowScale = UnityEngine.Random.Range(0.8f, 1.1f);
                settings.EyebrowThickness = UnityEngine.Random.Range(0.7f, 1.2f);
                settings.LeftEyeLidColor = skinTone;
                settings.RightEyeLidColor = skinTone;

                var eyeConfig = new Eye.EyeLidConfiguration { topLidOpen = 0.5f, bottomLidOpen = 0.5f };
                settings.LeftEyeRestingState = eyeConfig;
                settings.RightEyeRestingState = eyeConfig;

                // Clear existing layers
                settings.FaceLayerSettings.Clear();
                settings.BodyLayerSettings.Clear();
                settings.AccessorySettings.Clear();

                // Face layer
                var faceLayer = new AvatarSettings.LayerSetting();
                faceLayer.layerPath = DrifterSpawner.FaceExpressions[UnityEngine.Random.Range(0, DrifterSpawner.FaceExpressions.Length)];
                faceLayer.layerTint = faceColor;
                settings.FaceLayerSettings.Add(faceLayer);

                // Manager outfit: button-up shirt (Handler-style)
                var shirtLayer = new AvatarSettings.LayerSetting();
                shirtLayer.layerPath = UnityEngine.Random.value < 0.5f
                    ? "Avatar/Layers/Top/Buttonup"
                    : "Avatar/Layers/Top/RolledButtonUp";
                shirtLayer.layerTint = ShirtColors[UnityEngine.Random.Range(0, ShirtColors.Length)];
                settings.BodyLayerSettings.Add(shirtLayer);

                // Dark slacks
                var pantsLayer = new AvatarSettings.LayerSetting();
                pantsLayer.layerPath = "Avatar/Layers/Bottom/Jeans";
                pantsLayer.layerTint = PantsColors[UnityEngine.Random.Range(0, PantsColors.Length)];
                settings.BodyLayerSettings.Add(pantsLayer);

                // Dress shoes
                var shoeSetting = new AvatarSettings.AccessorySetting();
                shoeSetting.path = "Avatar/Accessories/Feet/DressShoes/DressShoes";
                shoeSetting.color = new Color(0.12f, 0.10f, 0.08f); // Dark leather
                settings.AccessorySettings.Add(shoeSetting);

                npc.Avatar.LoadAvatarSettings(settings);
                UnityEngine.Random.state = state;

                Logger.Msg($"Applied manager appearance for {npc.ID}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"ApplyAppearance failed for manager {npc.ID}: {ex.Message}");
            }
        }

        /// <summary>
        /// Initializes the NPC's messaging system so it can send text messages to the player.
        /// </summary>
        public static void InitializeMessaging(NPC npc)
        {
            if (npc == null) return;

            try
            {
                if (npc.MSGConversation != null)
                {
                    Logger.Msg($"Messaging already initialized for manager {npc.ID}");
                    return;
                }

                var conversation = new MSGConversation(npc, npc.fullName);
                npc.MSGConversation = conversation;
                conversation.SetIsKnown(true);
                Logger.Msg($"Initialized messaging for manager {npc.ID}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"InitializeMessaging failed for manager {npc.ID}: {ex.Message}");
            }
        }

        /// <summary>
        /// Borrows a voice database from an existing NPC.
        /// </summary>
        public static void EnsureVoiceDatabase(NPC npc)
        {
            if (npc?.VoiceOverEmitter == null) return;
            if (npc.VoiceOverEmitter.Database != null) return;

            try
            {
                var allNpcs = UnityEngine.Object.FindObjectsOfType<NPC>();
                foreach (var other in allNpcs)
                {
                    if (other.GetInstanceID() == npc.GetInstanceID())
                        continue;

                    if (other.VoiceOverEmitter?.Database != null)
                    {
                        npc.VoiceOverEmitter.SetDatabase(other.VoiceOverEmitter.Database, false);
                        Logger.Msg($"Borrowed voice database for manager {npc.ID}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"EnsureVoiceDatabase failed for manager {npc.ID}: {ex.Message}");
            }
        }

        /// <summary>
        /// Despawns and destroys a Manager NPC.
        /// </summary>
        public static void Despawn(NPC npc)
        {
            if (npc == null) return;

            try
            {
                string id = npc.ID ?? "unknown";

                // Remove from registry
                try { NPCManager.NPCRegistry?.Remove(npc); }
                catch { }

                // Network despawn if FishNet-spawned (host path)
                try
                {
                    var netObj = npc.gameObject?.GetComponent<NetworkObject>();
                    if (netObj != null && InstanceFinder.ServerManager != null && netObj.IsSpawned)
                    {
                        InstanceFinder.ServerManager.Despawn(netObj);
                        Logger.Msg($"ServerManager.Despawn completed for manager {id}");
                    }
                    else if (npc.gameObject != null)
                    {
                        UnityEngine.Object.Destroy(npc.gameObject);
                    }
                }
                catch
                {
                    if (npc.gameObject != null)
                        UnityEngine.Object.Destroy(npc.gameObject);
                }

                Logger.Msg($"Despawned manager {id}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Despawn failed: {ex.Message}");
            }
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
