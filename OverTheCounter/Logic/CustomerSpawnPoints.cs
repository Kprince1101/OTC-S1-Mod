using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Storage;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
#else
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using ScheduleOne.Storage;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Spawn/despawn locations and interior browse positions for store customers
    /// near the Westville Shack.
    /// </summary>
    public static class CustomerSpawnPoints
    {
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

        /// <summary>In front of stairs — stop before climbing (civilian/ground NavMesh).
        /// X=-159.2 is just east of the first ramp step (X=-159.4).</summary>
        public static readonly Vector3 StairApproachPosition = new(-159.2f, -4.0f, 74.8f);

        /// <summary>Base of the ramp — first step (runtime Employee NavMesh). Y is set
        /// above the baked ground so Warp anchors on the runtime mesh, not baked.</summary>
        public static readonly Vector3 RampBottomPosition = new(-159.5f, -3.4f, 74.8f);

        /// <summary>Top of the ramp, just outside the door (runtime Employee NavMesh).</summary>
        public static readonly Vector3 RampTopPosition = new(-160.9f, -2.9f, 74.8f);

        /// <summary>Just inside the door — first point on interior floor (runtime Employee NavMesh).</summary>
        public static readonly Vector3 DoorInteriorPosition = new(-162.4f, -2.9f, 74.8f);

        /// <summary>Center of the room — used when no storage is placed.</summary>
        public static readonly Vector3 RoomCenterPosition = new(-164.4f, -2.9f, 75.5f);

        /// <summary>Spawn points on sidewalk east of the building.</summary>
        private static readonly SpawnPoint[] _spawnPoints =
        {
            new("East_Near",  new Vector3(-151.0f, -3.5f, 72.0f), Quaternion.Euler(0f, 270f, 0f)),
            new("East_Mid",   new Vector3(-149.0f, -3.5f, 78.0f), Quaternion.Euler(0f, 270f, 0f)),
            new("East_Far",   new Vector3(-146.0f, -3.5f, 75.0f), Quaternion.Euler(0f, 270f, 0f)),
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
        /// Finds 2-3 display cabinets inside the OTC building and returns stand positions
        /// (in front of each shelf) along with shelf centers for facing.
        /// Returns an empty list when no storage is placed (triggers LookingAround state).
        /// </summary>
        private const string DisplayCabinetId = "displaycabinet";
        private const float ShelfStandOffset = 1.0f;

        public static List<Vector3> GetInteriorBrowsePositions(out List<Vector3> shelfCenters)
        {
            var standPositions = new List<Vector3>();
            shelfCenters = new List<Vector3>();

            try
            {
                // Only browse display cabinets with packaged product inside OTC buildings
                foreach (var kvp in BuildingGridFactory.GridContainers)
                {
                    var root = kvp.Value;
                    if (root == null) continue;

                    var storages = root.GetComponentsInChildren<PlaceableStorageEntity>(true);
                    if (storages == null) continue;

                    for (int i = 0; i < storages.Length; i++)
                    {
                        var storage = storages[i];
                        if (storage == null || storage.transform == null) continue;

                        // Only browse display cabinets
                        if (storage.ItemInstance == null ||
                            storage.ItemInstance.ID != DisplayCabinetId)
                            continue;

                        // Only browse shelves that have packaged product
                        if (!HasPackagedProductInEntity(storage.gameObject))
                            continue;

                        // Stand in front of the shelf
                        var pos = storage.transform.position;
                        var standPos = pos + storage.transform.forward * ShelfStandOffset;
                        standPositions.Add(standPos);
                        shelfCenters.Add(pos);
                    }
                }
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,$"Error scanning for storage entities: {ex.Message}");
            }

            if (standPositions.Count == 0)
                return standPositions;

            // Pick 2-3 random storage positions (shuffle both lists in sync)
            ShufflePaired(standPositions, shelfCenters);
            int count = Mathf.Min(standPositions.Count, UnityEngine.Random.Range(2, 4));
            shelfCenters = shelfCenters.GetRange(0, count);
            return standPositions.GetRange(0, count);
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

        /// <summary>
        /// Returns true if any storage entity inside OTC building grids
        /// contains at least one packaged product (ProductItemInstance with AppliedPackaging).
        /// </summary>
        public static bool HasPackagedProduct()
        {
            try
            {
                foreach (var kvp in BuildingGridFactory.GridContainers)
                {
                    var root = kvp.Value;
                    if (root == null) continue;

                    var storages = root.GetComponentsInChildren<StorageEntity>(true);
                    if (storages == null) continue;

                    for (int i = 0; i < storages.Length; i++)
                    {
                        var storage = storages[i];
                        if (storage?.ItemSlots == null) continue;

                        for (int j = 0; j < storage.ItemSlots.Count; j++)
                        {
                            var slot = storage.ItemSlots[j];
                            if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

#if IL2CPP
                            var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
#else
                            var productItem = slot.ItemInstance as ProductItemInstance;
#endif
                            if (productItem != null && productItem.AppliedPackaging != null)
                                return true;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,$"Error scanning for packaged product: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Returns true if a specific storage GameObject contains at least one packaged product.
        /// </summary>
        private static bool HasPackagedProductInEntity(GameObject go)
        {
            if (go == null) return false;

            var storages = go.GetComponentsInChildren<StorageEntity>(true);
            if (storages == null) return false;

            for (int i = 0; i < storages.Length; i++)
            {
                var storage = storages[i];
                if (storage?.ItemSlots == null) continue;

                for (int j = 0; j < storage.ItemSlots.Count; j++)
                {
                    var slot = storage.ItemSlots[j];
                    if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

#if IL2CPP
                    var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
#else
                    var productItem = slot.ItemInstance as ProductItemInstance;
#endif
                    if (productItem != null && productItem.AppliedPackaging != null)
                        return true;
                }
            }

            return false;
        }

        private static void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static void ShufflePaired<T1, T2>(List<T1> list1, List<T2> list2)
        {
            for (int i = list1.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (list1[i], list1[j]) = (list1[j], list1[i]);
                (list2[i], list2[j]) = (list2[j], list2[i]);
            }
        }
    }
}
