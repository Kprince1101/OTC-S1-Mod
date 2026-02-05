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

            // Spawn via IL2CPP
            var gameNpc = DrifterSpawner.Spawn(
                id,
                firstName,
                lastName,
                hotspot.Position,
                hotspot.Rotation
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

        // Type-specific text message getters
        public string GetIntroTextMessage()
        {
            return Type switch
            {
                DrifterType.Whale => "Yo, I'm looking for a big score. Got cash. Meet me and we'll talk.",
                DrifterType.Fiend => "HELP. Need product NOW. Will pay extra. Where are you???",
                DrifterType.Narc => "Hey, friend gave me your number. Said you could help me out. Let's meet.",
                _ => "Hey, you around? I need to score. One time thing. Hit me back."
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
