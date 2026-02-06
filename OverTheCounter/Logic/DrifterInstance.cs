using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.VoiceOver;
using MelonLoader;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using System;
using System.Collections.Generic;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Wrapper class for drifter NPCs spawned via IL2CPP.
    /// Does NOT extend S1API.NPC to avoid singleton issues.
    /// </summary>
    public class DrifterInstance
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("DrifterInstance");
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
        public bool ArrivedAtDestination { get; set; }

        // Hold references to IL2CPP callbacks to prevent GC from collecting them before arrival
        private Il2CppSystem.Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult> _destCallback;
        private Il2CppSystem.Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult> _spawnCallback;

        // Stuck detection: if NPC hasn't moved significantly in StuckCheckInterval, warp to destination
        private Vector3? _lastStuckCheckPos;
        private float _lastStuckCheckTime;
        private const float StuckCheckInterval = 8f;  // seconds between stuck checks
        private const float StuckThreshold = 1.5f;    // minimum movement in meters to not be "stuck"
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
                Logger.Warning($"Drifter {id} already exists, returning existing instance");
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
                Logger.Error($"Failed to spawn drifter {id}");
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

            Logger.Msg($"Created drifter {id}: Type={type}, Hotspot={hotspot.Name}, Position={hotspot.Position}");
            return instance;
        }

        /// <summary>
        /// Sends a text message from this drifter to the player.
        /// </summary>
        public void SendTextMessage(string message)
        {
            if (GameNpc == null)
            {
                Logger.Warning($"Drifter {Id}: cannot send text - GameNpc is null");
                return;
            }

            try
            {
                GameNpc.SendTextMessage(message);
                Logger.Msg($"Drifter {Id} sent text: \"{message}\"");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to send text from drifter {Id}: {ex.Message}");
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
                Logger.Warning($"Failed to play dismissal sound: {ex.Message}");
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
                Logger.Warning($"WarpTo failed for drifter {Id}: {ex.Message}");
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

                _destCallback = (Il2CppSystem.Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult>)
                    new Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult>(result =>
                    {
                        Logger.Msg($"Drifter {Id} arrived at destination (result={result})");
                        if (result == Il2CppScheduleOne.NPCs.NPCMovement.WalkResult.Success ||
                            result == Il2CppScheduleOne.NPCs.NPCMovement.WalkResult.Partial)
                        {
                            ArrivedAtDestination = true;
                            FaceDirection(Hotspot.Rotation);
                        }
                    });

                GameNpc.Movement.SetDestination(Hotspot.Position, _destCallback, 3f, 1f);
                _lastStuckCheckTime = Time.time;
                _lastStuckCheckPos = null;
                _stuckCount = 0;
                Logger.Msg($"Drifter {Id} walking to destination: {Hotspot.Position}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"WalkToDestination failed for drifter {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if the drifter is stuck (hasn't moved significantly).
        /// Call this periodically from the lifecycle tick.
        /// Returns true if the drifter was warped to unstick it.
        /// </summary>
        public bool CheckStuck()
        {
            if (IsConsuming || !IsValid) return false;

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
                        Logger.Warning($"Drifter {Id} stuck at {currentPos.Value} (moved {moved:F1}m in {StuckCheckInterval}s), warping to target");
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
            if (!IsValid || IsConsuming) return;

            try
            {
                var movement = GameNpc?.Movement;
                if (movement == null) return;

                // If the NPC still has an active destination, nothing to do
                if (movement.HasDestination) return;

                if (IsWalkingBack)
                {
                    // Should be walking to spawn but movement was interrupted
                    var dist = Vector3.Distance(Position ?? Vector3.zero, Hotspot.SpawnPosition);
                    if (dist > 3f)
                    {
                        Logger.Msg($"Drifter {Id}: resuming walk to spawn (interrupted, dist={dist:F1}m)");
                        GameNpc.Movement.SetDestination(Hotspot.SpawnPosition, _spawnCallback, 3f, 1f);
                    }
                }
                else if (!ArrivedAtDestination)
                {
                    // Should be walking to destination but movement was interrupted
                    var dist = Vector3.Distance(Position ?? Vector3.zero, Hotspot.Position);
                    if (dist > 3f)
                    {
                        Logger.Msg($"Drifter {Id}: resuming walk to destination (interrupted, dist={dist:F1}m)");
                        GameNpc.Movement.SetDestination(Hotspot.Position, _destCallback, 3f, 1f);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"EnsureMoving failed for drifter {Id}: {ex.Message}");
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
                Logger.Warning($"FaceDirection failed for drifter {Id}: {ex.Message}");
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

                _spawnCallback = (Il2CppSystem.Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult>)
                    new Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult>(result =>
                    {
                        Logger.Msg($"Drifter {Id} arrived at spawn (result={result})");
                        if (result == Il2CppScheduleOne.NPCs.NPCMovement.WalkResult.Success ||
                            result == Il2CppScheduleOne.NPCs.NPCMovement.WalkResult.Partial)
                            FaceDirection(Hotspot.SpawnRotation);
                    });

                GameNpc.Movement.SetDestination(Hotspot.SpawnPosition, _spawnCallback, 3f, 1f);
                Logger.Msg($"Drifter {Id} walking back to spawn: {Hotspot.SpawnPosition}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"WalkToSpawn failed for drifter {Id}: {ex.Message}");
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
                GameNpc.Movement.MovementSpeedScale = 0.9f;
                Logger.Msg($"Drifter {Id}: set to run speed");
            }
            catch (Exception ex)
            {
                Logger.Warning($"SetRunSpeed failed for drifter {Id}: {ex.Message}");
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
                    Logger.Warning($"Drifter {Id}: no Behaviour component, skipping consume");
                    WalkToSpawn();
                    return;
                }

                var consumeBehaviour = GameNpc.Behaviour.ConsumeProductBehaviour;
                if (consumeBehaviour == null)
                {
                    Logger.Warning($"Drifter {Id}: ConsumeProductBehaviour is null, skipping consume");
                    WalkToSpawn();
                    return;
                }

                // Look up the product definition
                ProductDefinition productDef = FindProductDefinition(productId);
                if (productDef == null)
                {
                    Logger.Warning($"Drifter {Id}: ProductDefinition not found for '{productId}', skipping consume");
                    WalkToSpawn();
                    return;
                }

                // Create a product instance for the consume behaviour
                // NOTE: C# 'as' cast doesn't work for IL2CPP types — must use .TryCast<>()
                var defaultInstance = productDef.GetDefaultInstance(1);
                var productInstance = defaultInstance?.TryCast<ProductItemInstance>();
                if (productInstance == null)
                {
                    Logger.Warning($"Drifter {Id}: Failed to create ProductItemInstance (raw type={defaultInstance?.GetType().Name}), skipping consume");
                    WalkToSpawn();
                    return;
                }

                IsConsuming = true;

                // Hook onConsumeDone to walk away after consuming
                try
                {
                    if (consumeBehaviour.onConsumeDone == null)
                        consumeBehaviour.onConsumeDone = new UnityEvent();

                    consumeBehaviour.onConsumeDone.AddListener((UnityAction)(() =>
                    {
                        Logger.Msg($"Drifter {Id}: consume animation finished, walking to spawn");
                        IsConsuming = false;
                        WalkToSpawn();
                    }));
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Drifter {Id}: Failed to hook onConsumeDone: {ex.Message}");
                }

                // Send product and activate the behaviour
                try
                {
                    consumeBehaviour.SendProduct(productInstance);
                    consumeBehaviour.Activate();
                    Logger.Msg($"Drifter {Id}: started consume animation for {productId}");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Drifter {Id}: SendProduct/Activate failed ({ex.Message}), trying Activate only");
                    try
                    {
                        consumeBehaviour.Activate();
                    }
                    catch
                    {
                        Logger.Warning($"Drifter {Id}: Activate also failed, falling back to WalkToSpawn");
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
                Logger.Warning($"Drifter {Id}: PlayConsumeAnimation failed: {ex.Message}");
                IsConsuming = false;
                WalkToSpawn();
            }
        }

        private IEnumerator ConsumeTimeoutCoroutine()
        {
            yield return new WaitForSeconds(10f);

            if (IsConsuming)
            {
                Logger.Warning($"Drifter {Id}: consume timeout after 10s, forcing WalkToSpawn");
                IsConsuming = false;
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
                MelonLogger.Warning($"FindProductDefinition failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Stocks the drifter's inventory with the actual items from the handover.
        /// Preserves packaging so pickpocketed items match what the player delivered.
        /// </summary>
        public void StockInventory(Il2CppSystem.Collections.Generic.List<ItemInstance> items)
        {
            try
            {
                var inventory = GameNpc?.Inventory;
                if (inventory == null)
                {
                    Logger.Warning($"Drifter {Id}: no Inventory component, skipping stock");
                    return;
                }

                int count = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item != null)
                    {
                        inventory.InsertItem(item, true);
                        count++;
                    }
                }

                Logger.Msg($"Drifter {Id}: stocked inventory with {count} items from handover");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Drifter {Id}: StockInventory failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Despawns and cleans up this drifter.
        /// </summary>
        public void Despawn()
        {
            Logger.Msg($"Despawning drifter {Id}");

            Active.Remove(Id);

            if (GameNpc != null)
            {
                DrifterSpawner.Despawn(GameNpc);
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
            "Ray", "Nick", "Marco", "Luis", "Javier", "Tyler", "Brandon", "Kyle"
        };

        private static readonly string[] FemaleFirstNames = {
            "Lisa", "Maria", "Jenny", "Sarah", "Angela", "Diane", "Rosa", "Carmen",
            "Kelly", "Tanya", "Monique", "Crystal", "Amber", "Jade", "Nikki", "Brianna"
        };

        private static readonly string[] LastNames = {
            "Smith", "Jones", "Garcia", "Martinez", "Brown", "Davis", "Wilson",
            "Moore", "Taylor", "Anderson", "Thomas", "Jackson", "White", "Harris",
            "Clark", "Lewis", "Walker", "Hall", "Young", "King", "Wright", "Hill"
        };

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
                    Logger.Warning($"Failed to cleanup drifter {id}: {ex.Message}");
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
