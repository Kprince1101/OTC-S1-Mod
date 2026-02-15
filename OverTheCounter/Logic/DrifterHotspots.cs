using System.Collections.Generic;
using System.Linq;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using MelonLoader;
using UnityEngine;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Spawn locations for drifter NPCs. Each hotspot has two positions:
    /// - SpawnPosition: where the NPC appears (away from players)
    /// - Position: where the NPC walks to and hangs out for the deal
    /// </summary>
    public static class DrifterHotspots
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:DrifterHotspots");

        /// <summary>
        /// Minimum distance from any player for a spawn point to be considered safe.
        /// </summary>
        private const float MinPlayerDistance = 60f;

        /// <summary>
        /// Maximum distance a spawn point can be from its destination.
        /// </summary>
        private const float MaxSpawnToDestDistance = 150f;

        /// <summary>
        /// Distance from any player at which a lingering NPC can despawn.
        /// </summary>
        public const float DespawnSafeDistance = 30f;

        public class Hotspot
        {
            public string Name { get; }
            /// <summary>Short, recognizable location hint for the player (e.g., "behind the motel").</summary>
            public string Description { get; }
            /// <summary>Where the NPC hangs out and waits for the deal.</summary>
            public Vector3 Position { get; }
            /// <summary>Direction the NPC faces at the destination.</summary>
            public Quaternion Rotation { get; }
            /// <summary>Where the NPC initially spawns in (away from players, walks to Position).</summary>
            public Vector3 SpawnPosition { get; }
            /// <summary>Direction the NPC faces at the spawn point (when lingering before despawn).</summary>
            public Quaternion SpawnRotation { get; }

            public Hotspot(string name, string description,
                Vector3 destination, float destYRotation,
                Vector3 spawnPoint, float spawnYRotation)
            {
                Name = name;
                Description = description;
                Position = destination;
                Rotation = Quaternion.Euler(0f, destYRotation, 0f);
                SpawnPosition = spawnPoint;
                SpawnRotation = Quaternion.Euler(0f, spawnYRotation, 0f);
            }
        }

        // Hotspots will be populated using the in-game Hotspot Editor (F8 debug menu).
        // Walk to spawn → mark → walk to dest → mark → type description → save.
        // Paste the logged constructor here.
        private static readonly List<Hotspot> _allHotspots = new List<Hotspot>
        {
            new Hotspot("Spot 1", "outside Thompson Construction",
                new Vector3(-41.82f, 1.07f, 95.27f), 181.6f,
                new Vector3(-28.28f, 1.06f, 97.79f), 271.2f),
            new Hotspot("Spot 2", "by motel room #5",
                new Vector3(-63.77f, 1.06f, 108.38f), 142.8f,
                new Vector3(-51.56f, 1.06f, 97.15f), 85.6f),
            new Hotspot("Spot 3", "near the motel office",
                new Vector3(-62.95f, 1.06f, 81.07f), 52.8f,
                new Vector3(-74.85f, 0.97f, 93.65f), 263.8f),
            new Hotspot("Spot 4", "in the alley behind the Chinese restaurant",
                new Vector3(-61.13f, -3.04f, 150.82f), 199.6f,
                new Vector3(-80.18f, -2.94f, 152.82f), 180.7f),
            new Hotspot("Spot 5", "by the water near Sauerkraut Supreme",
                new Vector3(-42.24f, -2.93f, 171.13f), 143.1f,
                new Vector3(-66.79f, -4.03f, 168.53f), 89.3f),
            new Hotspot("Spot 6", "outside Sauerkraut Supreme Pizzeria",
                new Vector3(-34.04f, -2.93f, 148.10f), 240.6f,
                new Vector3(-19.11f, -4.04f, 176.26f), 182.2f),
            new Hotspot("Spot 7", "in front of Dan's Hardware",
                new Vector3(-17.27f, -2.94f, 133.24f), 233.6f,
                new Vector3(-11.64f, -3.07f, 157.39f), 175.5f),
            new Hotspot("Spot 8", "out front of Hyland Range",
                new Vector3(-14.06f, -2.42f, 121.09f), 298.3f,
                new Vector3(-3.11f, -3.02f, 125.84f), 176.8f),
            new Hotspot("Spot 9", "at the Stash & Dash bus stop",
                new Vector3(-14.91f, 1.07f, 93.49f), 272.8f,
                new Vector3(4.48f, 0.96f, 103.45f), 154.1f),
            new Hotspot("Spot 10", "near taco ticklers",
                new Vector3(-22.90f, 1.06f, 61.52f), 142.9f,
                new Vector3(-33.64f, 1.07f, 72.21f), 131.5f),
            new Hotspot("Spot 11", "next to the pawn shop",
                new Vector3(-67.23f, 1.01f, 55.03f), 355.3f,
                new Vector3(-63.60f, -1.54f, 20.19f), 178.1f),
            new Hotspot("Spot 12", "at the camp near Gas Mart",
                new Vector3(-94.81f, -3.05f, 57.40f), 324.0f,
                new Vector3(-83.59f, -3.42f, 67.50f), 277.6f),
            new Hotspot("Spot 13", "outside Gas Mart",
                new Vector3(-120.08f, -2.94f, 65.64f), 358.2f,
                new Vector3(-100.96f, -2.74f, 47.07f), 9.1f),
            new Hotspot("Spot 14", "outside the tattoo parlor",
                new Vector3(-130.55f, -2.93f, 69.83f), 92.3f,
                new Vector3(-162.77f, -4.34f, 41.76f), 121.9f),
            new Hotspot("Spot 15", "in the apartment parking lot",
                new Vector3(-169.14f, -2.94f, 80.49f), 22.3f,
                new Vector3(-139.68f, -2.96f, 75.43f), 4.2f),
            new Hotspot("Spot 16", "near the chemical company",
                new Vector3(-109.70f, -2.94f, 82.56f), 203.7f,
                new Vector3(-136.10f, -2.35f, 109.04f), 185.1f),
            new Hotspot("Spot 17", "at the bus stop behind the construction site",
                new Vector3(-136.90f, -2.93f, 118.44f), 8.5f,
                new Vector3(-180.34f, -2.71f, 102.74f), 53.9f),
            new Hotspot("Spot 18", "by the shipping containers behind the chemical company",
                new Vector3(-114.16f, -2.61f, 118.69f), 347.8f,
                new Vector3(-109.26f, -2.91f, 142.58f), 356.9f),
            new Hotspot("Spot 19", "down by the canal behind the motel",
                new Vector3(-83.01f, -2.91f, 99.52f), 268.8f,
                new Vector3(-80.11f, -2.94f, 152.18f), 184.2f),
            new Hotspot("Spot 20", "in the alley behind the supermarket",
                new Vector3(5.99f, 1.07f, 57.29f), 153.9f,
                new Vector3(55.0f, 1.06f, 60.0f), 270.0f),
            new Hotspot("Spot 21", "at the supermarket",
                new Vector3(27.12f, 1.06f, 61.82f), 115.6f,
                new Vector3(11.81f, 1.06f, 69.22f), 69.4f),
            new Hotspot("Spot 22", "next to the Crimson Canary",
                new Vector3(43.89f, 1.16f, 76.49f), 225.4f,
                new Vector3(27.99f, 1.01f, 100.45f), 139.0f),
            new Hotspot("Spot 23", "in front of the mayor's house",
                new Vector3(73.19f, 1.31f, 79.45f), 11.3f,
                new Vector3(87.34f, 1.22f, 106.77f), 202.0f),
            new Hotspot("Spot 24", "behind ham legal services",
                new Vector3(94.15f, 1.06f, 76.24f), 333.0f,
                new Vector3(74.55f, 1.01f, 101.07f), 0.8f),
            new Hotspot("Spot 25", "next to hyland medical",
                new Vector3(109.31f, 1.06f, 65.02f), 94.7f,
                new Vector3(118.29f, 1.03f, 85.21f), 249.8f),
            new Hotspot("Spot 26", "at the piss hut",
                new Vector3(-38.62f, 1.06f, 47.56f), 5.5f,
                new Vector3(-38.98f, -1.54f, 20.89f), 187.3f),
            new Hotspot("Spot 27", "outside the barbershop",
                new Vector3(-23.21f, 1.06f, 30.36f), 93.6f,
                new Vector3(3.08f, 1.06f, 25.74f), 272.4f),
            new Hotspot("Spot 28", "at top dog car wash",
                new Vector3(-12.74f, 1.07f, -14.74f), 274.9f,
                new Vector3(-0.50f, 1.21f, 17.98f), 90.2f),
            new Hotspot("Spot 29", "by the shipping containers near the docks",
                new Vector3(-54.30f, -1.54f, -76.77f), 332.7f,
                new Vector3(-32.65f, -1.54f, -40.24f), 272.3f),
            new Hotspot("Spot 30", "at the end of the docks",
                new Vector3(-75.13f, -1.53f, -25.15f), 13.6f,
                new Vector3(-98.76f, -1.53f, -36.57f), 279.5f),
            new Hotspot("Spot 31", "under the south overpass",
                new Vector3(-23.13f, 1.17f, -99.28f), 81.6f,
                new Vector3(-32.49f, 0.93f, -75.85f), 139.7f),
            new Hotspot("Spot 32", "near the old RV",
                new Vector3(22.19f, 0.92f, -83.05f), 310.7f,
                new Vector3(39.92f, 3.20f, -77.19f), 320.6f),
            new Hotspot("Spot 33", "at the boutique",
                new Vector3(72.80f, 1.06f, -7.47f), 5.2f,
                new Vector3(85.68f, 0.96f, -20.93f), 158.2f),
            new Hotspot("Spot 34", "behind the post office",
                new Vector3(42.00f, 1.06f, -7.41f), 192.8f,
                new Vector3(67.57f, 0.98f, -19.97f), 207.5f),
            new Hotspot("Spot 35", "at the car dealership",
                new Vector3(17.32f, 1.06f, -30.87f), 318.0f,
                new Vector3(40.43f, 1.07f, -41.48f), 341.9f),
        };

        public static IReadOnlyList<Hotspot> AllHotspots => _allHotspots;

        public static Hotspot GetHotspotByName(string name)
        {
            foreach (var hotspot in _allHotspots)
            {
                if (hotspot.Name == name)
                    return hotspot;
            }
            return null;
        }

        /// <summary>
        /// Finds the best available hotspot whose spawn point is far from all players.
        /// Tries up to 3 random picks, then falls back to the farthest valid option.
        /// </summary>
        /// <param name="occupiedNames">Hotspot names already in use by active drifters.</param>
        public static Hotspot GetSafeHotspot(HashSet<string> occupiedNames)
        {
            var candidates = _allHotspots
                .Where(h => !occupiedNames.Contains(h.Name))
                .ToList();

            if (candidates.Count == 0)
                return null;

            var playerPositions = GetAllPlayerPositions();

            // Try up to 3 random picks
            for (int attempt = 0; attempt < 3 && candidates.Count > 0; attempt++)
            {
                int idx = UnityEngine.Random.Range(0, candidates.Count);
                var pick = candidates[idx];

                if (IsSpawnSafeFromPlayers(pick.SpawnPosition, playerPositions))
                {
                    if (Config.VerboseLogging.Value)
                        Logger.Msg($"Safe hotspot found on attempt {attempt + 1}: {pick.Name}");
                    return pick;
                }
            }

            // Fallback: shuffle candidates so we don't always pick the same geographic extreme
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }

            // Pick randomly from candidates that are at least half the safe distance away
            var halfSafe = candidates.Where(h =>
                GetMinDistanceToPlayers(h.SpawnPosition, playerPositions) >= MinPlayerDistance / 2f).ToList();

            if (halfSafe.Count > 0)
            {
                var pick = halfSafe[UnityEngine.Random.Range(0, halfSafe.Count)];
                if (Config.VerboseLogging.Value)
                    Logger.Msg($"No perfectly safe hotspot found. Picked from {halfSafe.Count} half-safe candidates: {pick.Name}");
                return pick;
            }

            // Last resort: random from all remaining candidates
            var fallback = candidates[0];
            Logger.Msg($"No safe hotspots at all. Random fallback: {fallback.Name}");
            return fallback;
        }

        /// <summary>
        /// Checks if a position is safe (far enough from all players).
        /// </summary>
        public static bool IsSpawnSafeFromPlayers(Vector3 position, List<Vector3> playerPositions)
        {
            foreach (var pp in playerPositions)
            {
                if (Vector3.Distance(position, pp) < MinPlayerDistance)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Checks if any player is within the despawn-safe distance of a position.
        /// </summary>
        public static bool IsAnyPlayerNearby(Vector3 position, float distance = -1f)
        {
            if (distance < 0f) distance = DespawnSafeDistance;
            var playerPositions = GetAllPlayerPositions();

            foreach (var pp in playerPositions)
            {
                if (Vector3.Distance(position, pp) < distance)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Gets the minimum distance from a position to any player.
        /// </summary>
        public static float GetMinDistanceToPlayers(Vector3 position, List<Vector3> playerPositions)
        {
            float min = float.MaxValue;
            foreach (var pp in playerPositions)
            {
                float dist = Vector3.Distance(position, pp);
                if (dist < min) min = dist;
            }
            return min;
        }

        /// <summary>
        /// Gets world positions of all connected players.
        /// </summary>
        public static List<Vector3> GetAllPlayerPositions()
        {
            var positions = new List<Vector3>();
            try
            {
                var playerList = Il2CppScheduleOne.PlayerScripts.Player.PlayerList;
                if (playerList != null)
                {
                    for (int i = 0; i < playerList.Count; i++)
                    {
                        var player = playerList[i];
                        if (player?.transform != null)
                            positions.Add(player.transform.position);
                    }
                }
            }
            catch
            {
                // Fallback to local player only
                try
                {
                    var localPlayer = PlayerSingleton<PlayerMovement>.Instance;
                    if (localPlayer?.transform != null)
                        positions.Add(localPlayer.transform.position);
                }
                catch { }
            }

            return positions;
        }

    }
}
