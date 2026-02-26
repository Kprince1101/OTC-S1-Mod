using MelonLoader;
using OverTheCounter.Logic.Placement;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.ObjectScripts;
#else
using ScheduleOne.ObjectScripts;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Spawn/despawn locations and interior browse positions for store customers
    /// near the Westville Shack.
    /// </summary>
    public static class CustomerSpawnPoints
    {
        private static readonly MelonLogger.Instance Logger = new("OTC:CustomerSpawns");

        /// <summary>A named spawn/despawn position with facing rotation.</summary>
        public class SpawnPoint
        {
            public string Name { get; }
            public Vector3 Position { get; }
            public Quaternion Rotation { get; }

            public SpawnPoint(string name, Vector3 position, Quaternion rotation)
            {
                Name = name;
                Position = position;
                Rotation = rotation;
            }
        }

        // WestvilleShack: origin (-167.4, -4, 73.5), foundation 1.1m, room 6x5m, east-facing
        // Room floor at Y = -2.9. Door at local (6, 0, 1.3) = world (-161.4, -2.9, 74.8)
        // Stairs descend to ground level east of door

        /// <summary>Bottom of the stairs — NPC entry/exit point (ground NavMesh).</summary>
        public static readonly Vector3 EntrancePosition = new(-160.0f, -4.0f, 74.8f);

        /// <summary>Just inside the door — first point on interior NavMesh after traversing the link.</summary>
        public static readonly Vector3 DoorInteriorPosition = new(-162.4f, -2.9f, 74.8f);

        /// <summary>Center of the room — used when no storage is placed.</summary>
        public static readonly Vector3 RoomCenterPosition = new(-164.4f, -2.9f, 75.5f);

        /// <summary>Spawn points on sidewalk east of the building.</summary>
        private static readonly SpawnPoint[] _spawnPoints =
        {
            new("East_Near",  new Vector3(-150.0f, -3.5f, 72.0f), Quaternion.Euler(0f, 270f, 0f)),
            new("East_Mid",   new Vector3(-148.0f, -3.5f, 78.0f), Quaternion.Euler(0f, 270f, 0f)),
            new("East_Far",   new Vector3(-145.0f, -3.5f, 75.0f), Quaternion.Euler(0f, 270f, 0f)),
        };


        /// <summary>
        /// Picks a random spawn point for a new customer.
        /// </summary>
        public static SpawnPoint GetRandomSpawnPoint()
        {
            if (_spawnPoints.Length == 0) return null;
            return _spawnPoints[UnityEngine.Random.Range(0, _spawnPoints.Length)];
        }

        /// <summary>
        /// Finds 2-3 positions near storage entities inside the OTC building.
        /// Returns an empty list when no storage is placed (triggers LookingAround state).
        /// </summary>
        public static List<Vector3> GetInteriorBrowsePositions()
        {
            var positions = new List<Vector3>();

            try
            {
                // Find PlaceableStorageEntity instances inside OTC buildings
                foreach (var kvp in BuildingGridFactory.GridContainers)
                {
                    var root = kvp.Value;
                    if (root == null) continue;

                    var storages = root.GetComponentsInChildren<PlaceableStorageEntity>(true);
                    if (storages == null) continue;

                    for (int i = 0; i < storages.Length; i++)
                    {
                        var storage = storages[i];
                        if (storage != null && storage.transform != null)
                            positions.Add(storage.transform.position);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Logger.Warning($"Error scanning for storage entities: {ex.Message}");
            }

            if (positions.Count == 0)
                return positions;

            // Pick 2-3 random storage positions
            Shuffle(positions);
            int count = Mathf.Min(positions.Count, UnityEngine.Random.Range(2, 4));
            return positions.GetRange(0, count);
        }

        /// <summary>
        /// Returns the index of a spawn point in the array, or 0 if not found.
        /// </summary>
        public static int GetSpawnPointIndex(SpawnPoint point)
        {
            if (point == null) return 0;
            for (int i = 0; i < _spawnPoints.Length; i++)
            {
                if (_spawnPoints[i] == point) return i;
            }
            return 0;
        }

        /// <summary>
        /// Returns a spawn point by index, clamped to valid range.
        /// </summary>
        public static SpawnPoint GetSpawnPointByIndex(int index)
        {
            if (_spawnPoints.Length == 0) return null;
            index = Mathf.Clamp(index, 0, _spawnPoints.Length - 1);
            return _spawnPoints[index];
        }

        private static void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
