using Il2CppFishNet.Object;
using Il2CppFishNet;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.Property;
using Il2CppScheduleOne.UI;
using MelonLoader;
using OverTheCounter.Utilities;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;
using System;
using System.Collections;
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

                // Add persistent inventory (while inactive, Awake deferred to SetActive)
                SetupInventory(npc);

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

                // 1.8x default NPC walk speed
                try
                {
                    var speedCtrl = npc.Movement?.SpeedController;
                    speedCtrl?.AddSpeedControl(
                        new Il2CppScheduleOne.NPCs.NPCSpeedController.SpeedControl("manager", 1, 0.144f));
                }
                catch { }

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

        // Track dialogue choices for cleanup
        private static readonly Dictionary<string, List<DialogueController.DialogueChoice>> _dialogueChoices = new();

        // Transfer sub-menu state
        private static readonly Dictionary<string, bool> _transferMenuActive = new();
        private static readonly Dictionary<string, float> _transferMenuTime = new();
        private static readonly Dictionary<string, List<DialogueController.DialogueChoice>> _transferChoices = new();
        private const float TRANSFER_MENU_TIMEOUT = 30f;

        // Fire confirmation sub-menu state
        private static readonly Dictionary<string, bool> _fireConfirmActive = new();
        private static readonly Dictionary<string, float> _fireConfirmTime = new();
        private static readonly Dictionary<string, List<DialogueController.DialogueChoice>> _fireConfirmChoices = new();

        // Flag to distinguish our programmatic reopens from fresh player-initiated dialogue
        private static bool _isReopening;

        // Greeting text overrides per manager (for fire confirmation prompt)
        private static readonly Dictionary<string, DialogueController.GreetingOverride> _greetingOverrides = new();

        /// <summary>
        /// Sets up dialogue choices on a Manager NPC in this exact order:
        /// 1. "I need to trade some items" — opens persistent inventory
        /// 2. "Why aren't you working?" — status explanation
        /// 3. "I need to transfer you to another property" — transfer sub-menu
        /// 4. "Your services are no longer required." — fire with confirmation
        /// Called after spawn/adopt once the NPC is fully initialized.
        /// </summary>
        public static void SetupDialogueChoices(ManagerInstance mgr)
        {
            if (mgr?.GameNpc == null) return;

            try
            {
                var dialogueController = mgr.GameNpc.DialogueHandler?.GetComponent<DialogueController>();
                if (dialogueController == null)
                {
                    Logger.Warning($"DialogueController not found for manager {mgr.Id}");
                    return;
                }

                var choices = new List<DialogueController.DialogueChoice>();
                string managerId = mgr.Id;
                var capturedDc = dialogueController;

                // ── Choice 1: Trade items ──
                var tradeChoice = new DialogueController.DialogueChoice();
                tradeChoice.ChoiceText = "I need to trade some items";
                tradeChoice.Enabled = true;
                tradeChoice.Conversation = null;
                tradeChoice.onChoosen = new UnityEvent();
                tradeChoice.onChoosen.AddListener((UnityAction)(() =>
                {
                    try
                    {
                        if (!ManagerInstance.Active.TryGetValue(managerId, out var m)) return;
                        if (m.GameNpc == null) return;

                        var inventory = m.GameNpc.GetComponent<Il2CppScheduleOne.NPCs.NPCInventory>();
                        if (inventory == null)
                        {
                            Logger.Warning($"No inventory component on manager {managerId}");
                            return;
                        }

                        // Freeze manager while viewing inventory.
                        // SkipNextDialogueBehaviourEnd keeps GenericDialogueBehaviour.Active true
                        // on the host so the manager stays frozen for client interactions too.
                        m.IsPlayerInteracting = true;
                        m.GameNpc.DialogueHandler?.SkipNextDialogueBehaviourEnd();
                        m.GameNpc.DialogueHandler?.EndDialogue();
                        MelonCoroutines.Start(OpenStorageDelayed(m, inventory));
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Trade choice error for {managerId}: {ex.Message}");
                    }
                }));
                tradeChoice.shouldShowCheck = (Func<bool, bool>)((bool enabled) =>
                    ShouldShowMainChoice(managerId));

                dialogueController.AddDialogueChoice(tradeChoice);
                choices.Add(tradeChoice);

                // ── Choice 2: Why aren't you working? ──
                var whyChoice = new DialogueController.DialogueChoice();
                whyChoice.ChoiceText = "Why aren't you working?";
                whyChoice.Enabled = true;
                whyChoice.Conversation = null;
                whyChoice.onChoosen = new UnityEvent();
                whyChoice.onChoosen.AddListener((UnityAction)(() =>
                {
                    string response = GetStatusExplanation(managerId);
                    var greeting = new DialogueController.GreetingOverride();
                    greeting.Greeting = response;
                    greeting.ShouldShow = true;
                    greeting.PlayVO = false;
                    capturedDc.AddGreetingOverride(greeting);
                    MelonCoroutines.Start(ReopenDialogue(capturedDc));
                }));
                whyChoice.shouldShowCheck = (Func<bool, bool>)((bool enabled) =>
                    ShouldShowMainChoice(managerId));

                dialogueController.AddDialogueChoice(whyChoice);
                choices.Add(whyChoice);

                // ── Choice 3: Transfer ──
                var transferChoice = new DialogueController.DialogueChoice();
                transferChoice.ChoiceText = "I need to transfer you to another property";
                transferChoice.Enabled = true;
                transferChoice.Conversation = null;
                transferChoice.onChoosen = new UnityEvent();
                transferChoice.onChoosen.AddListener((UnityAction)(() =>
                {
                    try
                    {
                        if (!ManagerInstance.Active.TryGetValue(managerId, out var m))
                            return;

                        CleanupTransferChoices(managerId, capturedDc);

                        var bizChoices = new List<DialogueController.DialogueChoice>();

                        foreach (var biz in Business.OwnedBusinesses)
                        {
                            if (biz == null) continue;
                            if (string.Equals(biz.PropertyCode, m.BusinessPropertyCode, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (ManagerInstance.HasManager(biz.PropertyCode))
                                continue;

                            string bizCode = biz.PropertyCode;
                            string bizName = biz.PropertyName ?? bizCode;
                            string capturedMgrId = managerId;

                            var bizChoice = new DialogueController.DialogueChoice();
                            bizChoice.ChoiceText = bizName;
                            bizChoice.Enabled = true;
                            bizChoice.Conversation = null;
                            bizChoice.onChoosen = new UnityEvent();
                            bizChoice.onChoosen.AddListener((UnityAction)(() =>
                            {
                                ClearTransferMenu(capturedMgrId, capturedDc);
                                if (NetworkHelper.IsHost)
                                    ManagerController.Instance?.TransferManager(capturedMgrId, bizCode);
                                else
                                    SaveData.ConfigSyncData.SendQuestAction($"MANAGER_TRANSFER:{capturedMgrId}:{bizCode}");
                            }));
                            bizChoice.shouldShowCheck = (Func<bool, bool>)((bool e) =>
                                IsTransferMenuActive(capturedMgrId));

                            capturedDc.AddDialogueChoice(bizChoice);
                            bizChoices.Add(bizChoice);
                        }

                        var cancelChoice = new DialogueController.DialogueChoice();
                        cancelChoice.ChoiceText = "Never mind.";
                        cancelChoice.Enabled = true;
                        cancelChoice.Conversation = null;
                        cancelChoice.onChoosen = new UnityEvent();
                        cancelChoice.onChoosen.AddListener((UnityAction)(() =>
                        {
                            ClearTransferMenu(managerId, capturedDc);
                            MelonCoroutines.Start(ReopenDialogue(capturedDc));
                        }));
                        cancelChoice.shouldShowCheck = (Func<bool, bool>)((bool e) =>
                            IsTransferMenuActive(managerId));

                        capturedDc.AddDialogueChoice(cancelChoice);
                        bizChoices.Add(cancelChoice);

                        _transferChoices[managerId] = bizChoices;
                        _transferMenuActive[managerId] = true;
                        _transferMenuTime[managerId] = UnityEngine.Time.time;

                        _isReopening = true;
                        MelonCoroutines.Start(ReopenDialogue(capturedDc));
                        Logger.Msg($"Opened transfer sub-menu for manager {managerId} ({bizChoices.Count - 1} businesses)");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Transfer menu setup failed for {managerId}: {ex.Message}");
                    }
                }));
                transferChoice.shouldShowCheck = (Func<bool, bool>)((bool enabled) =>
                {
                    if (!ShouldShowMainChoice(managerId)) return false;
                    if (!ManagerInstance.Active.TryGetValue(managerId, out var m)) return false;
                    int availableBusinesses = 0;
                    foreach (var biz in Business.OwnedBusinesses)
                    {
                        if (biz == null) continue;
                        if (string.Equals(biz.PropertyCode, m.BusinessPropertyCode, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (ManagerInstance.HasManager(biz.PropertyCode))
                            continue;
                        availableBusinesses++;
                    }
                    return availableBusinesses > 0;
                });

                dialogueController.AddDialogueChoice(transferChoice);
                choices.Add(transferChoice);

                // ── Choice 4: Fire (with confirmation) ──
                var fireChoice = new DialogueController.DialogueChoice();
                fireChoice.ChoiceText = "Your services are no longer required.";
                fireChoice.Enabled = true;
                fireChoice.Conversation = null;
                fireChoice.onChoosen = new UnityEvent();
                fireChoice.onChoosen.AddListener((UnityAction)(() =>
                {
                    try
                    {
                        if (!ManagerInstance.Active.TryGetValue(managerId, out var m))
                            return;

                        CleanupFireConfirmChoices(managerId, capturedDc);
                        var confirmChoices = new List<DialogueController.DialogueChoice>();

                        // "Yes" — confirm fire
                        var yesChoice = new DialogueController.DialogueChoice();
                        yesChoice.ChoiceText = "Yes";
                        yesChoice.Enabled = true;
                        yesChoice.Conversation = null;
                        yesChoice.onChoosen = new UnityEvent();
                        yesChoice.onChoosen.AddListener((UnityAction)(() =>
                        {
                            ClearGreetingOverride(capturedDc, managerId);
                            ClearFireConfirm(managerId, capturedDc);
                            if (NetworkHelper.IsHost)
                                ManagerController.Instance?.FireManager(managerId);
                            else
                                SaveData.ConfigSyncData.SendQuestAction($"MANAGER_FIRE:{managerId}");
                        }));
                        yesChoice.shouldShowCheck = (Func<bool, bool>)((bool e) =>
                            IsFireConfirmActive(managerId));

                        capturedDc.AddDialogueChoice(yesChoice);
                        confirmChoices.Add(yesChoice);

                        // "Actually, nevermind" — cancel fire
                        var nevermindChoice = new DialogueController.DialogueChoice();
                        nevermindChoice.ChoiceText = "Actually, nevermind";
                        nevermindChoice.Enabled = true;
                        nevermindChoice.Conversation = null;
                        nevermindChoice.onChoosen = new UnityEvent();
                        nevermindChoice.onChoosen.AddListener((UnityAction)(() =>
                        {
                            ClearGreetingOverride(capturedDc, managerId);
                            ClearFireConfirm(managerId, capturedDc);
                            MelonCoroutines.Start(ReopenDialogue(capturedDc));
                        }));
                        nevermindChoice.shouldShowCheck = (Func<bool, bool>)((bool e) =>
                            IsFireConfirmActive(managerId));

                        capturedDc.AddDialogueChoice(nevermindChoice);
                        confirmChoices.Add(nevermindChoice);

                        _fireConfirmChoices[managerId] = confirmChoices;
                        _fireConfirmActive[managerId] = true;
                        _fireConfirmTime[managerId] = UnityEngine.Time.time;

                        SetGreetingOverride(capturedDc, managerId,
                            "Are you sure? I'll drop off any items at the supply drop and leave.");

                        _isReopening = true;
                        MelonCoroutines.Start(ReopenDialogue(capturedDc));
                        Logger.Msg($"Opened fire confirmation for manager {managerId}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Fire confirm setup failed for {managerId}: {ex.Message}");
                    }
                }));
                fireChoice.shouldShowCheck = (Func<bool, bool>)((bool enabled) =>
                    ShouldShowMainChoice(managerId));

                dialogueController.AddDialogueChoice(fireChoice);
                choices.Add(fireChoice);

                _dialogueChoices[mgr.Id] = choices;
                Logger.Msg($"Set up dialogue choices for manager {mgr.Id}");
            }
            catch (Exception ex)
            {
                Logger.Error($"SetupDialogueChoices failed for {mgr.Id}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Returns a human-readable status explanation for the "Why aren't you working?" dialogue.
        /// </summary>
        private static string GetStatusExplanation(string managerId)
        {
            if (!ManagerInstance.Active.TryGetValue(managerId, out var mgr))
                return "I'm not sure what's going on.";

            // Check wages — do a real-time cash check to avoid race with CheckImmediateWages
            if (!mgr.PaidForToday)
            {
                bool cashAvailable = mgr.HasLocker &&
                    mgr.GetLockerCash() >= Config.ManagerDailyWage.Value;
                if (!cashAvailable)
                {
                    if (!mgr.HasLocker)
                        return "I need a locker assigned before I can get to work, boss.";
                    return "You haven't paid my daily wages yet. Add cash to my locker.";
                }
                // Cash is there but CheckImmediateWages hasn't ticked yet — don't complain
            }

            var supplyStatus = mgr.SupplyBehaviour?.GetStatusDescription();
            if (supplyStatus != null) return supplyStatus;

            var distributionStatus = mgr.DistributionBehaviour?.GetStatusDescription();
            if (distributionStatus != null) return distributionStatus;

            return mgr.State switch
            {
                ManagerState.Idle => "Everything's stocked up! I'll check again shortly.",
                ManagerState.SupplyRun => "I'm out picking up supplies for the business.",
                ManagerState.DistributionRun => "I'm out delivering product to our sellers.",
                ManagerState.NoFunds => "I can't work without pay, boss. Add cash to my locker.",
                ManagerState.Transferring => "I'm on my way to a new assignment.",
                _ => "I'm working on it!"
            };
        }

        /// <summary>
        /// Checks if the transfer sub-menu is currently active for a manager.
        /// Includes timeout-based cleanup (30s) to handle stale state from walked-away dialogue.
        /// </summary>
        private static bool IsTransferMenuActive(string managerId)
        {
            if (!_transferMenuActive.TryGetValue(managerId, out bool active) || !active)
                return false;

            // Timeout cleanup — if transfer menu has been active too long, clear it
            if (_transferMenuTime.TryGetValue(managerId, out float startTime) &&
                UnityEngine.Time.time - startTime > TRANSFER_MENU_TIMEOUT)
            {
                _transferMenuActive.Remove(managerId);
                _transferMenuTime.Remove(managerId);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Clears the transfer sub-menu state and removes dynamic business choices.
        /// </summary>
        private static void ClearTransferMenu(string managerId, DialogueController dc)
        {
            _transferMenuActive.Remove(managerId);
            _transferMenuTime.Remove(managerId);
            CleanupTransferChoices(managerId, dc);
        }

        /// <summary>
        /// Checks if the fire confirmation sub-menu is active for a manager.
        /// Includes timeout cleanup (30s).
        /// </summary>
        private static bool IsFireConfirmActive(string managerId)
        {
            if (!_fireConfirmActive.TryGetValue(managerId, out bool active) || !active)
                return false;

            if (_fireConfirmTime.TryGetValue(managerId, out float startTime) &&
                UnityEngine.Time.time - startTime > TRANSFER_MENU_TIMEOUT)
            {
                _fireConfirmActive.Remove(managerId);
                _fireConfirmTime.Remove(managerId);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Clears the fire confirmation sub-menu state.
        /// </summary>
        private static void ClearFireConfirm(string managerId, DialogueController dc)
        {
            _fireConfirmActive.Remove(managerId);
            _fireConfirmTime.Remove(managerId);
            CleanupFireConfirmChoices(managerId, dc);
        }

        /// <summary>
        /// Determines if a main dialogue choice should show. Returns false when a sub-menu
        /// (transfer/fire confirm) is active. When the player re-initiates dialogue after
        /// walking away, detects the fresh start (not our reopen) and clears stale sub-menu state.
        /// </summary>
        private static bool ShouldShowMainChoice(string managerId)
        {
            if (!ManagerInstance.Active.TryGetValue(managerId, out var m)) return false;
            if (m.State == ManagerState.Fired || m.State == ManagerState.Transferring) return false;

            bool transferActive = IsTransferMenuActive(managerId);
            bool fireActive = IsFireConfirmActive(managerId);

            if (transferActive || fireActive)
            {
                if (!_isReopening)
                {
                    ClearStaleSubMenuState(managerId);
                    return true;
                }
                return false;
            }
            return true;
        }

        /// <summary>
        /// Clears stale sub-menu state and greeting overrides for a manager.
        /// Called when a fresh dialogue is detected after the player walked away.
        /// </summary>
        private static void ClearStaleSubMenuState(string managerId)
        {
            _transferMenuActive.Remove(managerId);
            _transferMenuTime.Remove(managerId);
            _fireConfirmActive.Remove(managerId);
            _fireConfirmTime.Remove(managerId);

            try
            {
                if (ManagerInstance.Active.TryGetValue(managerId, out var mgr) && mgr.GameNpc != null)
                {
                    var dc = mgr.GameNpc.DialogueHandler?.GetComponent<DialogueController>();
                    ClearGreetingOverride(dc, managerId);
                }
            }
            catch { }
        }

        /// <summary>
        /// Adds a greeting override to the DialogueController so the NPC says custom text
        /// instead of a random greeting. Used for fire confirmation prompt.
        /// </summary>
        private static void SetGreetingOverride(DialogueController dc, string managerId, string text)
        {
            ClearGreetingOverride(dc, managerId);

            var greeting = new DialogueController.GreetingOverride();
            greeting.Greeting = text;
            greeting.ShouldShow = true;
            greeting.PlayVO = false;
            dc.AddGreetingOverride(greeting);
            _greetingOverrides[managerId] = greeting;
        }

        /// <summary>
        /// Removes the greeting override for a manager.
        /// </summary>
        private static void ClearGreetingOverride(DialogueController dc, string managerId)
        {
            if (_greetingOverrides.TryGetValue(managerId, out var existing))
            {
                existing.ShouldShow = false;
                try { dc?.GreetingOverrides?.Remove(existing); } catch { }
                _greetingOverrides.Remove(managerId);
            }
        }

        /// <summary>
        /// Removes fire confirmation choices from the DialogueController.
        /// </summary>
        private static void CleanupFireConfirmChoices(string managerId, DialogueController dc)
        {
            if (!_fireConfirmChoices.TryGetValue(managerId, out var choices))
                return;

            foreach (var choice in choices)
            {
                try { choice.Enabled = false; } catch { }
                try { dc?.Choices?.Remove(choice); } catch { }
            }
            _fireConfirmChoices.Remove(managerId);
        }

        /// <summary>
        /// Removes dynamic transfer business choices from the DialogueController.
        /// </summary>
        private static void CleanupTransferChoices(string managerId, DialogueController dc)
        {
            if (!_transferChoices.TryGetValue(managerId, out var choices))
                return;

            foreach (var choice in choices)
            {
                try { choice.Enabled = false; } catch { }
                try { dc?.Choices?.Remove(choice); } catch { }
            }
            _transferChoices.Remove(managerId);
        }

        /// <summary>
        /// Re-opens dialogue next frame so GetActiveChoices() picks up dynamically added choices.
        /// Used after adding transfer business choices to the dialogue tree.
        /// </summary>
        private static IEnumerator ReopenDialogue(DialogueController dc)
        {
            yield return null;
            _isReopening = true;
            try
            {
                if (dc != null && dc.GenericDialogue != null)
                    dc.StartGenericDialogue();
            }
            catch (Exception ex)
            {
                Logger.Warning($"ReopenDialogue failed: {ex.Message}");
            }
            _isReopening = false;
        }

        /// <summary>
        /// Opens the StorageMenu with the manager's inventory after a frame delay
        /// (allows dialogue UI to close first).
        /// </summary>
        private static IEnumerator OpenStorageDelayed(ManagerInstance mgr, Il2CppScheduleOne.NPCs.NPCInventory inventory)
        {
            yield return null;
            try
            {
                if (mgr?.GameNpc == null || inventory == null)
                {
                    if (mgr != null)
                    {
                        mgr.IsPlayerInteracting = false;
                        try { mgr.GameNpc?.Behaviour?.GenericDialogueBehaviour?.Disable_Server(); }
                        catch { }
                    }
                    yield break;
                }

                string title = mgr.GameNpc.fullName + "'s Inventory";
                var storageMenu = Singleton<StorageMenu>.Instance;
                UnityAction closeAction = null;
                closeAction = (UnityAction)(() =>
                {
                    mgr.IsPlayerInteracting = false;
                    // Release the server-side dialogue lock so the host resumes the manager
                    try { mgr.GameNpc?.Behaviour?.GenericDialogueBehaviour?.Disable_Server(); }
                    catch { }
                    storageMenu.onClosed.RemoveListener(closeAction);
                });
                storageMenu.onClosed.AddListener(closeAction);
                storageMenu.Open(
                    inventory.Cast<Il2CppScheduleOne.ItemFramework.IItemSlotOwner>(),
                    title,
                    "");
            }
            catch (Exception ex)
            {
                Logger.Warning($"OpenStorageDelayed failed: {ex.Message}");
                if (mgr != null)
                {
                    mgr.IsPlayerInteracting = false;
                    try { mgr.GameNpc?.Behaviour?.GenericDialogueBehaviour?.Disable_Server(); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Adds a persistent NPCInventory component to the manager NPC.
        /// Disables nightly clearing and pickpocketing. Called during spawn (inactive GO)
        /// and adopt (temporarily deactivates GO to avoid Awake crash).
        /// </summary>
        public static void SetupInventory(NPC npc)
        {
            try
            {
                var existing = npc.GetComponent<Il2CppScheduleOne.NPCs.NPCInventory>();
                if (existing != null)
                {
                    existing.ClearInventoryEachNight = false;
                    existing.RandomCash = false;
                    existing.RandomItems = false;
                    existing.CanBePickpocketed = false;
                    existing.SlotCount = 5;

                    Logger.Msg($"Inventory already exists on manager {npc.ID}, configured (slots={existing.ItemSlots?.Count ?? existing.SlotCount})");
                    return;
                }

                bool wasActive = npc.gameObject.activeSelf;
                if (wasActive) npc.gameObject.SetActive(false);

                var inventory = npc.gameObject.AddComponent<Il2CppScheduleOne.NPCs.NPCInventory>();
                inventory.SlotCount = 5;
                inventory.ClearInventoryEachNight = false;
                inventory.RandomCash = false;
                inventory.RandomItems = false;
                inventory.CanBePickpocketed = false;

                var intObj = npc.GetComponentInChildren<InteractableObject>(true);
                if (intObj != null)
                    inventory.PickpocketIntObj = intObj;

                if (wasActive) npc.gameObject.SetActive(true);

                Logger.Msg($"Added inventory to manager {npc.ID} (slots={inventory.SlotCount})");
            }
            catch (Exception ex)
            {
                Logger.Warning($"SetupInventory failed for {npc.ID}: {ex.Message}");
            }
        }

        /// <summary>
        /// Cached employee NavMeshAgent settings for indoor navigation.
        /// The CivilianNPC prefab has outdoor-only settings; employees have settings that
        /// include indoor NavMesh areas. We cache these for on-demand switching.
        /// </summary>
        private static int? _cachedEmployeeAgentTypeID;
        private static int? _cachedEmployeeAreaMask;

        /// <summary>
        /// Gets the employee NavMesh settings (agentTypeID + areaMask) for indoor navigation.
        /// Looks up from any vanilla employee and caches the result.
        /// </summary>
        public static bool TryGetEmployeeNavMeshSettings(out int agentTypeID, out int areaMask)
        {
            if (_cachedEmployeeAgentTypeID.HasValue)
            {
                agentTypeID = _cachedEmployeeAgentTypeID.Value;
                areaMask = _cachedEmployeeAreaMask.Value;
                return true;
            }

            agentTypeID = 0;
            areaMask = -1;

            try
            {
                var props = Il2CppScheduleOne.Property.Property.OwnedProperties;
                if (props == null) return false;

                for (int p = 0; p < props.Count; p++)
                {
                    var prop = props[p];
                    if (prop?.Employees == null) continue;
                    for (int e = 0; e < prop.Employees.Count; e++)
                    {
                        var emp = prop.Employees[e];
                        if (emp?.Movement?.Agent == null) continue;

                        var empAgent = emp.Movement.Agent;
                        _cachedEmployeeAgentTypeID = empAgent.agentTypeID;
                        _cachedEmployeeAreaMask = empAgent.areaMask;
                        agentTypeID = empAgent.agentTypeID;
                        areaMask = empAgent.areaMask;
                        Logger.Msg($"Cached employee NavMesh settings: agentTypeID={agentTypeID}, areaMask={areaMask}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"TryGetEmployeeNavMeshSettings failed: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Cleans up dialogue choices for a manager (on despawn).
        /// </summary>
        public static void CleanupDialogueChoices(string managerId)
        {
            if (_dialogueChoices.TryGetValue(managerId, out var choices))
            {
                foreach (var choice in choices)
                {
                    try { choice.Enabled = false; } catch { }
                }
                _dialogueChoices.Remove(managerId);
            }

            // Clean up transfer sub-menu
            _transferMenuActive.Remove(managerId);
            _transferMenuTime.Remove(managerId);
            _transferChoices.Remove(managerId);

            // Clean up fire confirmation sub-menu
            _fireConfirmActive.Remove(managerId);
            _fireConfirmTime.Remove(managerId);
            _fireConfirmChoices.Remove(managerId);

            // Clean up greeting override
            _greetingOverrides.Remove(managerId);
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
