using System.Collections.Generic;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;
using UnityEngine.AI;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Hardcoded spawn locations for drifter NPCs, organized by region.
    /// </summary>
    public static class DrifterHotspots
    {
        /// <summary>
        /// Hotspot data containing position and a descriptive name.
        /// </summary>
        public class Hotspot
        {
            public string Name { get; }
            public string Region { get; }
            public Vector3 Position { get; }
            public Quaternion Rotation { get; }

            public Hotspot(string name, string region, Vector3 position, float yRotation = 0f)
            {
                Name = name;
                Region = region;
                Position = position;
                Rotation = Quaternion.Euler(0f, yRotation, 0f);
            }
        }

        private static readonly List<Hotspot> _allHotspots = new List<Hotspot>
        {
            // Northtown - Behind the Motel
            new Hotspot("Motel Alley", "Northtown", new Vector3(-50f, 1.06f, 70f), 180f),

            // Westville - Skatepark area
            new Hotspot("Skatepark", "Westville", new Vector3(100f, 2.0f, -80f), 90f),

            // Downtown - HAM Legal Alley
            new Hotspot("Legal Alley", "Downtown", new Vector3(-120f, 1.0f, 50f), 270f),

            // Docks - Container Yard
            new Hotspot("Container Yard", "Docks", new Vector3(200f, 1.0f, -150f), 0f),

            // Suburbia - Construction Site
            new Hotspot("Construction Site", "Suburbia", new Vector3(-80f, 1.0f, 120f), 45f)
        };

        private static readonly Dictionary<string, List<Hotspot>> _hotspotsByRegion;

        static DrifterHotspots()
        {
            _hotspotsByRegion = new Dictionary<string, List<Hotspot>>();

            foreach (var hotspot in _allHotspots)
            {
                if (!_hotspotsByRegion.ContainsKey(hotspot.Region))
                    _hotspotsByRegion[hotspot.Region] = new List<Hotspot>();

                _hotspotsByRegion[hotspot.Region].Add(hotspot);
            }
        }

        /// <summary>
        /// Gets all available hotspots.
        /// </summary>
        public static IReadOnlyList<Hotspot> AllHotspots => _allHotspots;

        /// <summary>
        /// Gets hotspots for a specific region.
        /// </summary>
        public static List<Hotspot> GetHotspotsForRegion(string region)
        {
            if (_hotspotsByRegion.TryGetValue(region, out var hotspots))
                return hotspots;

            return new List<Hotspot>();
        }

        /// <summary>
        /// Gets a random hotspot from all available locations.
        /// </summary>
        public static Hotspot GetRandomHotspot()
        {
            if (_allHotspots.Count == 0)
                return null;

            int index = UnityEngine.Random.Range(0, _allHotspots.Count);
            return _allHotspots[index];
        }

        /// <summary>
        /// Gets a random hotspot from a specific region.
        /// Falls back to any hotspot if region has none.
        /// </summary>
        public static Hotspot GetRandomHotspotInRegion(string region)
        {
            var regionHotspots = GetHotspotsForRegion(region);
            if (regionHotspots.Count > 0)
            {
                int index = UnityEngine.Random.Range(0, regionHotspots.Count);
                return regionHotspots[index];
            }

            return GetRandomHotspot();
        }

        /// <summary>
        /// Gets a hotspot by name (for serialization/deserialization).
        /// </summary>
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
        /// Creates a dynamic hotspot near the player's current position.
        /// Used for debugging. Returns null if player position cannot be found.
        /// </summary>
        public static Hotspot CreateHotspotNearPlayer(float distance = 5f)
        {
            try
            {
                var playerMovement = PlayerSingleton<PlayerMovement>.Instance;
                if (playerMovement == null)
                    return null;

                Vector3 playerPos = playerMovement.transform.position;
                Vector3 playerForward = playerMovement.transform.forward;

                // Spawn in front of player at specified distance
                Vector3 spawnPos = playerPos + playerForward * distance;

                // Try to find a valid NavMesh position
                if (NavMesh.SamplePosition(spawnPos, out NavMeshHit hit, 10f, NavMesh.AllAreas))
                {
                    spawnPos = hit.position;
                }

                // Face toward player
                float yRotation = playerMovement.transform.eulerAngles.y + 180f;

                return new Hotspot("Player Nearby", "Debug", spawnPos, yRotation);
            }
            catch
            {
                return null;
            }
        }
    }
}
