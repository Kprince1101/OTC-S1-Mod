using Il2CppFishNet.Object;
using Il2CppFishNet.Managing;
using Il2CppFishNet;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Messaging;
using MelonLoader;
using UnityEngine;
using UnityEngine.AI;
using System;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Handles spawning drifter NPCs via IL2CPP directly, bypassing S1API's singleton pattern.
    /// Clones the game's "CivilianNPC" prefab for each new drifter instance.
    /// </summary>
    public static class DrifterSpawner
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("DrifterSpawner");

        private static NetworkObject _cachedBasePrefab;
        private static bool _prefabSearched;

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
                Logger.Msg($"Searching {count} spawnable prefabs for CivilianNPC...");

                for (int i = 0; i < count; i++)
                {
                    var obj = spawnablePrefabs.GetObject(true, i);
                    if (obj == null || obj.gameObject == null)
                        continue;

                    string name = obj.gameObject.name;
                    if (name == "CivilianNPC")
                    {
                        _cachedBasePrefab = obj;
                        Logger.Msg("Found CivilianNPC prefab");
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
                    Logger.Error($"Cannot spawn drifter {id}: no base prefab");
                    return null;
                }

                // Clone the prefab
                var clone = UnityEngine.Object.Instantiate(basePrefab);
                if (clone == null || clone.gameObject == null)
                {
                    Logger.Error($"Failed to instantiate prefab for drifter {id}");
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
                    Logger.Error($"Cloned object missing NPC component for drifter {id}");
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

                // Activate
                clone.gameObject.SetActive(true);

                // Register with game
                try
                {
                    if (NPCManager.NPCRegistry != null && !NPCManager.NPCRegistry.Contains(npc))
                    {
                        NPCManager.NPCRegistry.Add(npc);
                    }
                }
                catch { }

                // Network spawn (host only)
                try
                {
                    var networkManager = InstanceFinder.NetworkManager;
                    if (networkManager != null && networkManager.IsServer)
                    {
                        var netObj = clone.gameObject.GetComponent<NetworkObject>();
                        if (netObj != null)
                        {
                            networkManager.ServerManager.Spawn(netObj);
                            Logger.Msg($"Network spawned drifter {id}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Network spawn failed for drifter {id}: {ex.Message}");
                }

                // Warp to position (syncs to clients)
                try
                {
                    npc.Movement?.Warp(spawnPos);
                    npc.Movement?.Stop();
                    npc.Movement?.FaceDirection(rotation * Vector3.forward);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Movement setup failed for drifter {id}: {ex.Message}");
                }

                Logger.Msg($"Spawned drifter {id} ({firstName} {lastName}) at {spawnPos}");
                return npc;
            }
            catch (Exception ex)
            {
                Logger.Error($"Spawn failed for drifter {id}: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        // Face expressions (required to avoid black face)
        private static readonly string[] FaceExpressions = {
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

        private static readonly string[] SafeFeet = {
            "Avatar/Accessories/Feet/Sneakers/Sneakers",
            "Avatar/Accessories/Feet/DressShoes/DressShoes",
            "Avatar/Accessories/Feet/Boots/Boots"
        };

        // Safe head accessories (NO PoliceCap) - used sparingly
        private static readonly string[] SafeHead = {
            "Avatar/Accessories/Head/Cap/Cap",
            "Avatar/Accessories/Head/Beanie/Beanie",
            "Avatar/Accessories/Head/FlatCap/FlatCap"
        };

        // Safe chest accessories (NO BulletProofVest_Police)
        private static readonly string[] SafeChest = {
            "Avatar/Accessories/Chest/Blazer/Blazer",
            "Avatar/Accessories/Chest/CollarJacket/CollarJacket",
            "Avatar/Accessories/Chest/OpenVest/OpenVest"
        };

        // Realistic skin tone presets - more diverse distribution
        private static readonly Color[] SkinTones = {
            new Color(0.96f, 0.87f, 0.78f), // Very light/fair
            new Color(0.92f, 0.80f, 0.70f), // Light
            new Color(0.85f, 0.70f, 0.55f), // Light-medium
            new Color(0.75f, 0.58f, 0.42f), // Medium tan
            new Color(0.65f, 0.48f, 0.35f), // Medium
            new Color(0.55f, 0.40f, 0.28f), // Medium-dark
            new Color(0.48f, 0.35f, 0.25f), // Dark
            new Color(0.40f, 0.28f, 0.20f), // Darker
            new Color(0.32f, 0.22f, 0.16f), // Very dark
            new Color(0.25f, 0.18f, 0.13f)  // Deep dark
        };

        /// <summary>
        /// Generates random appearance for an NPC using IL2CPP AvatarSettings.
        /// Excludes police apparel to prevent confusion.
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
                // Use seed for deterministic randomization (host/client sync)
                var state = UnityEngine.Random.state;
                UnityEngine.Random.InitState(seed);

                // Get or create avatar settings
                var settings = npc.Avatar.CurrentSettings;
                if (settings == null)
                {
                    settings = ScriptableObject.CreateInstance<AvatarSettings>();
                }

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
                settings.Gender = UnityEngine.Random.Range(0f, 1f);
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

                // Add shoes
                if (settings.AccessorySettings != null && SafeFeet.Length > 0)
                {
                    var feetAccessory = new AvatarSettings.AccessorySetting();
                    feetAccessory.path = SafeFeet[UnityEngine.Random.Range(0, SafeFeet.Length)];
                    feetAccessory.color = RandomClothingColor();
                    settings.AccessorySettings.Add(feetAccessory);
                }

                // Optionally add hat (15% chance - less frequent)
                if (settings.AccessorySettings != null && SafeHead.Length > 0 && UnityEngine.Random.value < 0.15f)
                {
                    var headAccessory = new AvatarSettings.AccessorySetting();
                    headAccessory.path = SafeHead[UnityEngine.Random.Range(0, SafeHead.Length)];
                    headAccessory.color = RandomClothingColor();
                    settings.AccessorySettings.Add(headAccessory);
                }

                // Optionally add jacket (15% chance)
                if (settings.AccessorySettings != null && SafeChest.Length > 0 && UnityEngine.Random.value < 0.15f)
                {
                    var chestAccessory = new AvatarSettings.AccessorySetting();
                    chestAccessory.path = SafeChest[UnityEngine.Random.Range(0, SafeChest.Length)];
                    chestAccessory.color = RandomClothingColor();
                    settings.AccessorySettings.Add(chestAccessory);
                }

                // Apply settings to avatar
                npc.Avatar.LoadAvatarSettings(settings);

                // Restore random state
                UnityEngine.Random.state = state;

                Logger.Msg($"Generated random appearance for {npc.ID}");
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
                if (npc.MSGConversation != null)
                {
                    Logger.Msg($"Messaging already initialized for {npc.ID}");
                    return;
                }

                // Create a new conversation for this NPC
                var conversation = new MSGConversation(npc, npc.fullName);
                npc.MSGConversation = conversation;

                // Set as known so it shows up in phone
                conversation.SetIsKnown(true);

                Logger.Msg($"Initialized messaging for {npc.ID}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"InitializeMessaging failed for {npc.ID}: {ex.Message}");
            }
        }

        /// <summary>
        /// Borrows a voice database from an existing NPC.
        /// </summary>
        public static void EnsureVoiceDatabase(NPC npc)
        {
            if (npc?.VoiceOverEmitter == null)
                return;

            if (npc.VoiceOverEmitter.Database != null)
                return;

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
                        Logger.Msg($"Borrowed voice database for {npc.ID}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"EnsureVoiceDatabase failed: {ex.Message}");
            }
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

                // Network despawn
                try
                {
                    var netObj = npc.gameObject?.GetComponent<NetworkObject>();
                    var networkManager = InstanceFinder.NetworkManager;
                    if (netObj != null && networkManager?.IsServer == true)
                    {
                        networkManager.ServerManager.Despawn(netObj);
                    }
                }
                catch { }

                // Destroy GameObject
                if (npc.gameObject != null)
                {
                    UnityEngine.Object.Destroy(npc.gameObject);
                }

                Logger.Msg($"Despawned drifter {id}");
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
