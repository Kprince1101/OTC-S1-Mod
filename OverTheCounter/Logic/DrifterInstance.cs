using MelonLoader;
using OverTheCounter.Utilities;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using System;
using System.Collections.Generic;

#if IL2CPP
using Il2CppScheduleOne.AvatarFramework.Equipping;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.VoiceOver;
#else
using ScheduleOne.AvatarFramework.Equipping;
using ScheduleOne.ItemFramework;
using ScheduleOne.NPCs;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Product;
using ScheduleOne.VoiceOver;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Wrapper class for drifter NPCs spawned via IL2CPP.
    /// Does NOT extend S1API.NPC to avoid singleton issues.
    /// </summary>
    public class DrifterInstance
    {

        private static readonly EVOLineType[] DismissalSounds = { EVOLineType.Annoyed, EVOLineType.No };

        /// <summary>
        /// All active drifter instances, keyed by ID.
        /// </summary>
        public static Dictionary<string, DrifterInstance> Active { get; } = new Dictionary<string, DrifterInstance>();

        // Identity
        public string Id { get; }
        public DrifterType Type { get; }
        public DrifterHotspots.Hotspot Hotspot { get; }
        public int SpawnSeed { get; }

        // Game reference
        public NPC GameNpc { get; private set; }

        // Lifecycle state
        public DrifterState State { get; set; } = DrifterState.Spawned;
        public bool DealAccepted { get; set; }
        public bool DealCompleted { get; set; }
        public bool IsWalkingBack { get; set; }
        public bool IsConsuming { get; set; }
        public bool IsAttacking { get; set; }
        public bool IsKnockedOut { get; set; }
        /// <summary>True when this instance wraps a FishNet-replicated NPC on the client.</summary>
        public bool IsAdopted { get; private set; }
        public bool ArrivedAtDestination { get; set; }

        // Hold references to IL2CPP callbacks to prevent GC from collecting them before arrival
        private GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult> _destCallback;
        private GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult> _spawnCallback;

        // Stuck detection: if NPC hasn't moved significantly in StuckCheckInterval, warp to destination
        private Vector3? _lastStuckCheckPos;
        private float _lastStuckCheckTime;
        private const float StuckCheckInterval = 8f;  // seconds between stuck checks
        private const float StuckThreshold = 1.5f;    // minimum movement in meters to not be "stuck"
        private float _lastEnsureMovingLog;            // throttle EnsureMoving log spam
        private int _stuckCount;

        public bool IsValid => GameNpc != null && GameNpc.gameObject != null;

        public Vector3? Position
        {
            get
            {
                try { return GameNpc?.transform?.position; }
                catch { return null; }
            }
        }

        private DrifterInstance(string id, DrifterType type, DrifterHotspots.Hotspot hotspot, int seed)
        {
            Id = id;
            Type = type;
            Hotspot = hotspot;
            SpawnSeed = seed;
        }

        /// <summary>
        /// Creates and spawns a new drifter instance.
        /// </summary>
        public static DrifterInstance Create(string id, DrifterType type, DrifterHotspots.Hotspot hotspot, int seed)
        {
            if (Active.ContainsKey(id))
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"{id} already exists, returning existing instance");
                return Active[id];
            }

            // Determine gender from seed (matches first RNG draw in GenerateRandomAppearance)
            float gender = DrifterSpawner.DetermineGender(seed);
            bool isFemale = gender >= 0.5f;

            // Generate gender-appropriate name from seed
            string firstName = GetRandomFirstName(seed, isFemale);
            string lastName = GetRandomLastName(seed);

            // Spawn at the entry point (SpawnPosition), NPC will walk to destination (Position)
            var gameNpc = DrifterSpawner.Spawn(
                id,
                firstName,
                lastName,
                hotspot.SpawnPosition,
                hotspot.SpawnRotation
            );

            if (gameNpc == null)
            {
                OTCLog.Error(OTCLog.Systems.Drifter, $"Failed to spawn {id}");
                return null;
            }

            var instance = new DrifterInstance(id, type, hotspot, seed)
            {
                GameNpc = gameNpc
            };

            // Initialize NPC systems
            DrifterSpawner.GenerateRandomAppearance(gameNpc, seed);
            DrifterSpawner.InitializeMessaging(gameNpc);
            DrifterSpawner.EnsureVoiceDatabase(gameNpc);

            Active[id] = instance;

            // Set drifter icon for messaging profile (synchronous, always ready)
            DrifterSpawner.SetDrifterIcon(gameNpc);

            // Generate real mugshot asynchronously (replaces generic icon when ready)
            EnqueueMugshot(gameNpc, id);

            OTCLog.Msg(OTCLog.Systems.Drifter, $"Created {id}: Type={type}, Hotspot={hotspot.Name}, Position={hotspot.Position}");
            return instance;
        }

        /// <summary>
        /// Adopts an existing NPC (e.g. FishNet-replicated on client) as a drifter.
        /// Applies appearance, messaging, and icon without spawning a new clone.
        /// Used by clients to claim the network-replicated NPC instead of double-spawning.
        /// </summary>
        public static DrifterInstance Adopt(string id, DrifterType type, DrifterHotspots.Hotspot hotspot, int seed, NPC existingNpc)
        {
            if (Active.ContainsKey(id))
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"{id} already exists, returning existing instance");
                return Active[id];
            }

            // Set NPC identity fields (FishNet doesn't sync these)
            var (firstName, lastName) = GetDrifterName(seed);
            existingNpc.ID = id;
            existingNpc.FirstName = firstName;
            existingNpc.LastName = lastName;

            var instance = new DrifterInstance(id, type, hotspot, seed)
            {
                GameNpc = existingNpc,
                IsAdopted = true
            };

            var avatar = existingNpc.Avatar;

            // Apply appearance (FishNet doesn't sync avatar settings)
            DrifterSpawner.GenerateRandomAppearance(existingNpc, seed);

            // Set InitialAvatarSettings so Avatar re-loads our settings if it re-initializes
            try
            {
                if (avatar != null && avatar.CurrentSettings != null)
                    // NOTE: Avatar no longer exposes InitialAvatarSettings as a separate
                    // slot to persist across re-init; CurrentSettings is the only
                    // settings store now, so there is nothing to re-assign here.
                    // If Avatar re-initializes and drops settings, this will need
                    // a LoadAvatarSettings(avatar.CurrentSettings) call instead.
            }
            catch { }

            // Clear any stale MSGConversation from PlayerSpawned() — it was created
            // with the prefab's default empty name before Adopt set the real identity.
            // InitializeMessaging will create a fresh one with the correct name.
            try { existingNpc.SetMSGConversation(null); } catch { }

            // Ensure messaging, voice, and icon are set up
            DrifterSpawner.InitializeMessaging(existingNpc);
            DrifterSpawner.EnsureVoiceDatabase(existingNpc);
            DrifterSpawner.SetDrifterIcon(existingNpc);

            // Generate real mugshot asynchronously (replaces generic icon when ready)
            EnqueueMugshot(existingNpc, id);

            // Schedule delayed re-apply: FishNet may not have fully initialized
            // rendering components when Adopt runs immediately after NPC discovery
            MelonCoroutines.Start(DelayedAppearanceReapply(existingNpc, seed));

            Active[id] = instance;
            OTCLog.Msg(OTCLog.Systems.Drifter, $"Adopted FishNet NPC for {id} ({firstName} {lastName}): Type={type}, Hotspot={hotspot.Name}");
            return instance;
        }

        /// <summary>
        /// Delays and re-applies appearance for adopted NPCs.
        /// Handles the case where FishNet hasn't fully initialized rendering when Adopt runs.
        /// </summary>
        private static IEnumerator DelayedAppearanceReapply(NPC npc, int seed)
        {
            yield return null; // Wait one frame
            yield return null; // Wait another frame for FishNet sync

            if (npc != null && npc.gameObject != null)
            {
                try
                {
                    DrifterSpawner.GenerateRandomAppearance(npc, seed);
                    OTCLog.Msg(OTCLog.Systems.Drifter, $"[Adopt] Delayed appearance re-apply completed for {npc.ID}");
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"[Adopt] Delayed re-apply failed for {npc.ID}: {ex.Message}");
                }
            }
        }

        private static void EnqueueMugshot(NPC gameNpc, string id)
        {
            var settings = gameNpc.Avatar?.CurrentSettings;
            if (settings == null) return;

            MugshotUtility.Generate(gameNpc, $"Drifter:{id}", sprite =>
            {
                if (sprite == null || gameNpc == null) return;
                gameNpc.MugshotSprite = sprite;

                // Update the Messages app list entry icon — it was cached with the
                // generic drifter icon when CreateConversationUI ran before the
                // async mugshot was ready.
                try
                {
                    var entry = gameNpc.GetMSGConversation()?.entry;
                    if (entry != null)
                    {
                        var iconImage = ((UnityEngine.Component)((UnityEngine.Transform)entry)
                            .Find("IconMask/Icon"))?.GetComponent<UnityEngine.UI.Image>();
                        if (iconImage != null)
                            iconImage.sprite = sprite;
                    }
                }
                catch { }
            }, UnityEngine.Object.Instantiate(settings));
        }

        /// <summary>
        /// Sends a text message from this drifter to the player.
        /// </summary>
        public void SendTextMessage(string message)
        {
            if (GameNpc == null)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: cannot send text - GameNpc is null");
                return;
            }

            try
            {
                GameNpc.SendTextMessage(message);
                OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id} sent text: \"{message}\"");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Drifter, $"Failed to send text from {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Delivers a text message locally (no FishNet networking).
        /// Used by SyncVar callback on clients to display synced messages.
        /// </summary>
        public void DeliverTextLocally(string message)
        {
            if (GameNpc == null) return;

            try
            {
                var conversation = GameNpc.GetMSGConversation();
                if (conversation == null) return;

                var msg = new ScheduleOne.Messaging.Message(
                    message,
                    ScheduleOne.Messaging.Message.ESenderType.Other,
                    true);
                conversation.SendMessage(msg, false, false); // local only, no network
                OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id} delivered synced text locally: \"{message}\"");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"DeliverTextLocally failed for {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Plays a dismissal sound.
        /// </summary>
        public void PlayDismissalSound()
        {
            try
            {
                var pick = DismissalSounds[UnityEngine.Random.Range(0, DismissalSounds.Length)];
                GameNpc?.PlayVO(pick);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"Failed to play dismissal sound: {ex.Message}");
            }
        }

        /// <summary>
        /// Warps the drifter to a position.
        /// </summary>
        public void WarpTo(Vector3 position)
        {
            try
            {
                GameNpc?.Movement?.Warp(position);
                GameNpc?.Movement?.Stop();
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"WarpTo failed for {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Commands the drifter to walk to the destination (hangout) position.
        /// On arrival, faces the direction specified by the hotspot rotation.
        /// </summary>
        public void WalkToDestination()
        {
            try
            {
                if (GameNpc?.Movement == null) return;

                _destCallback = (GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult>)
                    new Action<ScheduleOne.NPCs.NPCMovement.WalkResult>(result =>
                    {
                        OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id} arrived at destination (result={result})");
                        if (result == ScheduleOne.NPCs.NPCMovement.WalkResult.Success ||
                            result == ScheduleOne.NPCs.NPCMovement.WalkResult.Partial)
                        {
                            ArrivedAtDestination = true;
                            FaceDirection(Hotspot.Rotation);
                        }
                    });

                GameNpc.Movement.SetDestination(Hotspot.Position, _destCallback, 3f, 1f);
                _lastStuckCheckTime = Time.time;
                _lastStuckCheckPos = null;
                _stuckCount = 0;
                OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id} walking to destination: {Hotspot.Position}");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"WalkToDestination failed for {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if the drifter is stuck (hasn't moved significantly).
        /// Call this periodically from the lifecycle tick.
        /// Returns true if the drifter was warped to unstick it.
        /// </summary>
        public bool CheckStuck()
        {
            if (IsConsuming || IsAttacking || IsKnockedOut || !IsValid) return false;

            // Determine target position based on current movement direction
            Vector3 target = IsWalkingBack ? Hotspot.SpawnPosition : Hotspot.Position;
            var currentPos = Position;
            if (currentPos == null) return false;

            // Check if already close enough to target
            float distToTarget = Vector3.Distance(currentPos.Value, target);
            if (distToTarget < 3f) return false;

            // Only check at intervals
            if (Time.time - _lastStuckCheckTime < StuckCheckInterval) return false;

            if (_lastStuckCheckPos != null)
            {
                float moved = Vector3.Distance(currentPos.Value, _lastStuckCheckPos.Value);
                if (moved < StuckThreshold)
                {
                    _stuckCount++;
                    if (_stuckCount >= 2) // stuck for 2 consecutive checks (~16s)
                    {
                        OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id} stuck at {currentPos.Value} (moved {moved:F1}m in {StuckCheckInterval}s), warping to target");
                        WarpTo(target);
                        _stuckCount = 0;
                        _lastStuckCheckPos = null;

                        // Re-face direction after warp
                        var faceRot = IsWalkingBack ? Hotspot.SpawnRotation : Hotspot.Rotation;
                        FaceDirection(faceRot);
                        return true;
                    }
                }
                else
                {
                    _stuckCount = 0;
                }
            }

            _lastStuckCheckPos = currentPos.Value;
            _lastStuckCheckTime = Time.time;
            return false;
        }

        /// <summary>
        /// Checks if the drifter should be walking but was interrupted (e.g. pickpocket, ragdoll).
        /// If so, re-issues the appropriate SetDestination command.
        /// Call this from the lifecycle tick.
        /// </summary>
        public void EnsureMoving()
        {
            if (!IsValid || IsConsuming || IsAttacking || IsKnockedOut) return;

            try
            {
                var movement = GameNpc?.Movement;
                if (movement == null) return;

                // If the NPC still has an active destination, nothing to do
                if (movement.HasDestination) return;

                if (IsWalkingBack)
                {
                    var dist = Vector3.Distance(Position ?? Vector3.zero, Hotspot.SpawnPosition);
                    if (dist > 3f)
                    {
                        if (Time.time - _lastEnsureMovingLog > 10f)
                        {
                            OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id}: resuming walk to spawn (interrupted, dist={dist:F1}m)");
                            _lastEnsureMovingLog = Time.time;
                        }
                        GameNpc.Movement.SetDestination(Hotspot.SpawnPosition, _spawnCallback, 3f, 1f);
                    }
                }
                else if (!ArrivedAtDestination)
                {
                    var dist = Vector3.Distance(Position ?? Vector3.zero, Hotspot.Position);
                    if (dist > 3f)
                    {
                        if (Time.time - _lastEnsureMovingLog > 10f)
                        {
                            OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id}: resuming walk to destination (interrupted, dist={dist:F1}m)");
                            _lastEnsureMovingLog = Time.time;
                        }
                        GameNpc.Movement.SetDestination(Hotspot.Position, _destCallback, 3f, 1f);
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"EnsureMoving failed for {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Faces the drifter in the given rotation direction.
        /// </summary>
        private void FaceDirection(Quaternion rotation)
        {
            try
            {
                if (GameNpc?.Movement == null) return;
                Vector3 forward = rotation * Vector3.forward;
                GameNpc.Movement.FaceDirection(forward, 0.5f);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"FaceDirection failed for {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Commands the drifter to walk back to the spawn point (for lingering/despawn).
        /// </summary>
        public void WalkToSpawn()
        {
            try
            {
                if (GameNpc?.Movement == null) return;
                IsWalkingBack = true;

                _spawnCallback = (GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult>)
                    new Action<ScheduleOne.NPCs.NPCMovement.WalkResult>(result =>
                    {
                        OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id} arrived at spawn (result={result})");
                        if (result == ScheduleOne.NPCs.NPCMovement.WalkResult.Success ||
                            result == ScheduleOne.NPCs.NPCMovement.WalkResult.Partial)
                            FaceDirection(Hotspot.SpawnRotation);
                    });

                GameNpc.Movement.SetDestination(Hotspot.SpawnPosition, _spawnCallback, 3f, 1f);
                OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id} walking back to spawn: {Hotspot.SpawnPosition}");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"WalkToSpawn failed for {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the drifter's movement speed to run/chase speed (0.9 scale).
        /// Used for narcs fleeing after a sting.
        /// </summary>
        public void SetRunSpeed()
        {
            try
            {
                if (GameNpc?.Movement == null) return;
                GameNpc.Movement.MoveSpeedMultiplier = 0.9f;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"SetRunSpeed failed for {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Plays the consume animation after a deal completes.
        /// Falls back to WalkToSpawn on any failure.
        /// </summary>
        public void PlayConsumeAnimation(string productId)
        {
            try
            {
                if (GameNpc?.Behaviour == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: no Behaviour component, skipping consume");
                    WalkToSpawn();
                    return;
                }

                var consumeBehaviour = GameNpc.Behaviour.ConsumeProductBehaviour;
                if (consumeBehaviour == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: ConsumeProductBehaviour is null, skipping consume");
                    WalkToSpawn();
                    return;
                }

                // Look up the product definition
                ProductDefinition productDef = FindProductDefinition(productId);
                if (productDef == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: ProductDefinition not found for '{productId}', skipping consume");
                    WalkToSpawn();
                    return;
                }

                // Create a product instance for the consume behaviour
                // NOTE: C# 'as' cast doesn't work for IL2CPP types — must use .TryCast<>()
                var defaultInstance = productDef.GetDefaultInstance(1);
                var productInstance = defaultInstance?.TryCast<ProductItemInstance>();
                if (productInstance == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: Failed to create ProductItemInstance (raw type={defaultInstance?.GetType().Name}), skipping consume");
                    WalkToSpawn();
                    return;
                }

                IsConsuming = true;

                // Walk to spawn when consume animation finishes
                try
                {
                    if (consumeBehaviour.onConsumeDone == null)
                        consumeBehaviour.onConsumeDone = new UnityEvent();

                    consumeBehaviour.onConsumeDone.AddListener((UnityAction)(() =>
                    {
                        IsConsuming = false;
                        OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id}: consume animation finished, walking to spawn");
                        WalkToSpawn();
                    }));
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: Failed to hook onConsumeDone: {ex.Message}");
                }

                // Send product and activate the behaviour
                try
                {
                    consumeBehaviour.SendProduct(productInstance, false);
                    consumeBehaviour.Activate();
                    OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id}: started consume animation for {productId}");
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: SendProduct/Activate failed ({ex.Message}), trying Activate only");
                    try
                    {
                        consumeBehaviour.Activate();
                    }
                    catch
                    {
                        OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: Activate also failed, falling back to WalkToSpawn");
                        IsConsuming = false;
                        WalkToSpawn();
                        return;
                    }
                }

                // Safety timeout: if consume gets stuck, force walk-to-spawn after 10 seconds
                MelonCoroutines.Start(ConsumeTimeoutCoroutine());
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: PlayConsumeAnimation failed: {ex.Message}");
                IsConsuming = false;
                WalkToSpawn();
            }
        }

        private IEnumerator ConsumeTimeoutCoroutine()
        {
            yield return new WaitForSeconds(10f);

            if (IsConsuming)
            {
                IsConsuming = false;
                OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: consume timeout after 10s, forcing WalkToSpawn");
                WalkToSpawn();
            }
        }

        private static ProductDefinition FindProductDefinition(string productId)
        {
            try
            {
                var listedProducts = ProductManager.ListedProducts;
                if (listedProducts == null) return null;

                for (int i = 0; i < listedProducts.Count; i++)
                {
                    var p = listedProducts[i];
                    if (p != null && p.ID == productId)
                        return p;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"FindProductDefinition failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Stocks the drifter's inventory with copies of the handed-over items.
        /// Clones each item so the NPC's inventory is independent of the
        /// HandoverScreen slots (which get cleared after the callback for
        /// non-robber drifters, invalidating the original references).
        /// </summary>
        public void StockInventory(GameSystem.Collections.Generic.List<ItemInstance> items)
        {
            try
            {
                var inventory = GameNpc?.Inventory;
                if (inventory == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: no Inventory component, skipping stock");
                    return;
                }

                int count = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item != null)
                    {
                        var copy = item.GetCopy(item.Quantity);
                        if (copy != null)
                        {
                            inventory.InsertItem(copy, true);
                            count++;
                        }
                    }
                }

                OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id}: stocked inventory with {count} items from handover");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: StockInventory failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Stocks the drifter's inventory with cash for body search recovery.
        /// Uses the vanilla NPCInventory.AddCash which creates CashInstance items.
        /// </summary>
        public void StockCash(float amount)
        {
            try
            {
                var inventory = GameNpc?.Inventory;
                if (inventory == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: no Inventory component, skipping cash stock");
                    return;
                }

                inventory.AddCash(amount);
                OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id}: stocked ${amount:F0} cash in inventory");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: StockCash failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the default weapon for the drifter's CombatBehaviour.
        /// Uses the prefab directly (no Instantiate) — CombatBehaviour only reads
        /// DefaultWeapon.AssetPath to call SetWeapon() when combat starts.
        /// Null weaponPath = fists (clears DefaultWeapon).
        /// </summary>
        public void EquipWeapon(string weaponPath)
        {
            try
            {
                var combatBehaviour = GameNpc?.Behaviour?.CombatBehaviour;
                if (combatBehaviour == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: CombatBehaviour is null, cannot equip weapon");
                    return;
                }

                if (string.IsNullOrEmpty(weaponPath))
                {
                    combatBehaviour.SetDefaultWeapon(null);
                    return;
                }

                // Load the prefab and read its AvatarWeapon component directly.
                // Do NOT Instantiate — that creates an orphan clone whose Awake can
                // corrupt state. CombatBehaviour only reads DefaultWeapon.AssetPath.
                var prefab = Resources.Load(weaponPath);
                var prefabGo = prefab?.TryCast<GameObject>();
                if (prefabGo == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: weapon prefab not found at '{weaponPath}'");
                    return;
                }

                var avatarWeapon = prefabGo.GetComponent<AvatarWeapon>();
                if (avatarWeapon == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: no AvatarWeapon component on '{weaponPath}'");
                    return;
                }

                // IL2CPP prefabs may have empty AssetPath (serialized field not loaded).
                // CombatBehaviour.StartCombat reads DefaultWeapon.AssetPath to call SetWeapon(),
                // so an empty path means the weapon silently fails to equip.
                var assetPath = avatarWeapon.AssetPath;
                if (string.IsNullOrEmpty(assetPath))
                {
                    avatarWeapon.AssetPath = weaponPath;
                    OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id}: fixed empty AssetPath → '{weaponPath}'");
                }

                combatBehaviour.SetDefaultWeapon(avatarWeapon);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: EquipWeapon failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Commands the drifter to attack a player (defaults to local player).
        /// Must be called on host (SetTargetAndEnable_Server requires server authority).
        /// </summary>
        public void AttackPlayer(Player targetPlayer = null)
        {
            try
            {
                var combatBehaviour = GameNpc?.Behaviour?.CombatBehaviour;
                if (combatBehaviour == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: CombatBehaviour is null, cannot attack");
                    return;
                }

                var player = targetPlayer ?? Player.Local;
                if (player == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: target player is null, cannot attack");
                    return;
                }

                // Configure persistent pursuit so robber chases aggressively
                combatBehaviour.GiveUpRange = 200f;
                combatBehaviour.DefaultSearchTime = 120f;

                // Engage combat targeting the player (requires server authority)
                combatBehaviour.SetTargetAndEnable_Server(player.NetworkObject);
                IsAttacking = true;

                OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id}: attacking player {player.PlayerCode}!");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"{Id}: AttackPlayer failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Despawns and cleans up this drifter.
        /// </summary>
        public void Despawn()
        {
            OTCLog.Msg(OTCLog.Systems.Drifter, $"Despawning {Id}");

            Active.Remove(Id);

            if (GameNpc != null)
            {
                if (IsAdopted)
                {
                    // FishNet-adopted NPC: release reference only, server handles destroy
                    OTCLog.Msg(OTCLog.Systems.Drifter, $"{Id}: releasing adopted FishNet NPC");
                }
                else
                {
                    DrifterSpawner.Despawn(GameNpc);
                }
                GameNpc = null;
            }
        }

        /// <summary>
        /// Gets the intro text message with deal details and location.
        /// </summary>
        public string GetIntroTextMessage(string productName, int quantity, float payment, string locationHint)
        {
            string paymentStr = $"${payment:F0}";

            return Type switch
            {
                DrifterType.Whale => $"Looking for a big score. Got {paymentStr} for {quantity} {productName}. I'm {locationHint}.",
                DrifterType.Fiend => $"NEED {productName} NOW. {quantity} for {paymentStr}. I'm {locationHint}. HURRY.",
                DrifterType.Narc => $"Friend gave me your number. Need {quantity} {productName}, got {paymentStr}. Meet me {locationHint}?",
                _ => $"Hey, need {quantity} {productName}. Paying {paymentStr}. I'm {locationHint}. You in?"
            };
        }

        public string GetExpiryTextMessage()
        {
            return Type switch
            {
                DrifterType.Whale => "Too slow. Found someone else to do business with.",
                DrifterType.Fiend => "FORGET IT. Got my fix elsewhere. Don't bother.",
                DrifterType.Narc => "Nevermind. This isn't working out.",
                _ => "You snooze you lose. I'm gone."
            };
        }

        // Name generation (gender-specific pools)
        private static readonly string[] MaleFirstNames = {
            "Mike", "Dave", "Tony", "Jimmy", "Frank", "Eddie", "Rick", "Steve",
            "Ray", "Nick", "Marco", "Luis", "Javier", "Tyler", "Brandon", "Kyle",
            "Scott"
        };

        private static readonly string[] FemaleFirstNames = {
            "Lisa", "Maria", "Jenny", "Sarah", "Angela", "Diane", "Rosa", "Carmen",
            "Kelly", "Tanya", "Monique", "Crystal", "Amber", "Jade", "Nikki", "Brianna"
        };

        private static readonly string[] LastNames = {
            "Smith", "Jones", "Garcia", "Martinez", "Brown", "Davis", "Wilson",
            "Moore", "Taylor", "Anderson", "Thomas", "Jackson", "White", "Harris",
            "Clark", "Lewis", "Walker", "Hall", "Young", "King", "Wright", "Hill",
            "Miller"
        };

        /// <summary>
        /// Derives the deterministic first/last name for a drifter from its seed.
        /// Used to identify FishNet-replicated NPCs on the client.
        /// </summary>
        public static (string firstName, string lastName) GetDrifterName(int seed)
        {
            float gender = DrifterSpawner.DetermineGender(seed);
            bool isFemale = gender >= 0.5f;
            return (GetRandomFirstName(seed, isFemale), GetRandomLastName(seed));
        }

        private static string GetRandomFirstName(int seed, bool isFemale)
        {
            var state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            var pool = isFemale ? FemaleFirstNames : MaleFirstNames;
            var name = pool[UnityEngine.Random.Range(0, pool.Length)];
            UnityEngine.Random.state = state;
            return name;
        }

        private static string GetRandomLastName(int seed)
        {
            var state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed + 1000);
            var name = LastNames[UnityEngine.Random.Range(0, LastNames.Length)];
            UnityEngine.Random.state = state;
            return name;
        }

        /// <summary>
        /// Cleans up all active drifters.
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
                    OTCLog.Warning(OTCLog.Systems.Drifter, $"Failed to cleanup {id}: {ex.Message}");
                }
            }
            Active.Clear();
        }
    }

    /// <summary>
    /// Lifecycle states for a drifter.
    /// </summary>
    public enum DrifterState
    {
        Spawned,
        DealAccepted,
        DealCompleted,
        Lingering,
        Despawning
    }
}
