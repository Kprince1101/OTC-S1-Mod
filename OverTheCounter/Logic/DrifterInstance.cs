using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.VoiceOver;
using MelonLoader;
using UnityEngine;
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

        // Hold references to IL2CPP callbacks to prevent GC from collecting them before arrival
        private Il2CppSystem.Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult> _destCallback;
        private Il2CppSystem.Action<Il2CppScheduleOne.NPCs.NPCMovement.WalkResult> _spawnCallback;

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

            // Generate name from seed
            string firstName = GetRandomFirstName(seed);
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

            // Register
            Active[id] = instance;

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
                        if (result == Il2CppScheduleOne.NPCs.NPCMovement.WalkResult.Success)
                            FaceDirection(Hotspot.Rotation);
                    });

                GameNpc.Movement.SetDestination(Hotspot.Position, _destCallback, 1f, 1f);
                Logger.Msg($"Drifter {Id} walking to destination: {Hotspot.Position}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"WalkToDestination failed for drifter {Id}: {ex.Message}");
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
                        if (result == Il2CppScheduleOne.NPCs.NPCMovement.WalkResult.Success)
                            FaceDirection(Hotspot.SpawnRotation);
                    });

                GameNpc.Movement.SetDestination(Hotspot.SpawnPosition, _spawnCallback, 1f, 1f);
                Logger.Msg($"Drifter {Id} walking back to spawn: {Hotspot.SpawnPosition}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"WalkToSpawn failed for drifter {Id}: {ex.Message}");
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

        // Name generation
        private static readonly string[] FirstNames = {
            "Mike", "Dave", "Tony", "Jimmy", "Frank", "Eddie", "Rick", "Steve",
            "Lisa", "Maria", "Jenny", "Sam", "Alex", "Chris", "Pat", "Casey",
            "Ray", "Nick", "Marco", "Luis", "Javier", "Tyler", "Brandon", "Kyle"
        };

        private static readonly string[] LastNames = {
            "Smith", "Jones", "Garcia", "Martinez", "Brown", "Davis", "Wilson",
            "Moore", "Taylor", "Anderson", "Thomas", "Jackson", "White", "Harris",
            "Clark", "Lewis", "Walker", "Hall", "Young", "King", "Wright", "Hill"
        };

        private static string GetRandomFirstName(int seed)
        {
            var state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            var name = FirstNames[UnityEngine.Random.Range(0, FirstNames.Length)];
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
