using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.Economy;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
#else
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using ScheduleOne.Storage;
using ScheduleOne.Economy;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Spawn/despawn locations and interior browse positions for store customers.
    /// Each OTC building gets 5 nearby vanilla DeliveryLocation spawn points,
    /// discovered at runtime by proximity to the building center.
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

        /// <summary>Center of the room (world coords) — used when no storage is placed.</summary>
        public static readonly Vector3 RoomCenterPosition = new(-164.4f, -2.9f, 75.5f);

        /// <summary>Center of the room in building-local coordinates for SendNPCToPosition.</summary>
        public static Vector3 RoomCenterLocal
        {
            get
            {
                var nav = Placement.WestvilleShack.NavBuilder;
                return nav != null ? nav.WorldToLocal(RoomCenterPosition) : Vector3.zero;
            }
        }

        // OTCWarehouse: origin (66.4, 0.25, -34.0), 12.7 x 10.2m
        private static readonly Vector3 WarehouseCenterPosition = new(72.75f, 0.25f, -28.9f);

        // Dispensary: origin set at runtime, but spawn points are hardcoded around its known location
        private static readonly SpawnPoint[] DispensarySpawnPoints =
        {
            new("Disp_SE",    new Vector3(95.6f,  0.1f, -23.8f), Quaternion.Euler(0f, 0f, 0f)),
            new("Disp_East",  new Vector3(70.2f,  0.1f, -7.0f),  Quaternion.Euler(0f, 0f, 0f)),
            new("Disp_NE",    new Vector3(70.8f,  0.1f, 29.2f),  Quaternion.Euler(0f, 180f, 0f)),
            new("Disp_North", new Vector3(95.5f,  0.1f, 37.5f),  Quaternion.Euler(0f, 180f, 0f)),
            new("Disp_NW",    new Vector3(118.5f, 0.1f, 37.3f),  Quaternion.Euler(0f, 180f, 0f)),
        };

        // Dynamic pools — 5 nearest vanilla DeliveryLocation TeleportPoints per building
        private static List<SpawnPoint> _dynamicShackPoints;
        private static List<SpawnPoint> _dynamicWarehousePoints;
        private static SpawnPoint[] _allSpawnPointsCached;

        private static List<SpawnPoint> ShackPoints
        {
            get
            {
                if (_dynamicShackPoints == null)
                    _dynamicShackPoints = FindNearbyVanillaSpawnPoints(RoomCenterPosition, 5, "Shack_");
                return _dynamicShackPoints;
            }
        }

        private static List<SpawnPoint> WarehousePoints
        {
            get
            {
                if (_dynamicWarehousePoints == null)
                    _dynamicWarehousePoints = FindNearbyVanillaSpawnPoints(WarehouseCenterPosition, 5, "Warehouse_");
                return _dynamicWarehousePoints;
            }
        }

        private static SpawnPoint[] AllSpawnPoints
        {
            get
            {
                if (_allSpawnPointsCached == null)
                {
                    var list = new List<SpawnPoint>();
                    list.AddRange(DispensarySpawnPoints);
                    list.AddRange(ShackPoints);
                    list.AddRange(WarehousePoints);
                    _allSpawnPointsCached = list.ToArray();
                }
                return _allSpawnPointsCached;
            }
        }

        /// <summary>Resets dynamic spawn point caches. Call on scene change.</summary>
        internal static void Cleanup()
        {
            _dynamicShackPoints = null;
            _dynamicWarehousePoints = null;
            _allSpawnPointsCached = null;
        }

        private static List<SpawnPoint> FindNearbyVanillaSpawnPoints(Vector3 origin, int count, string prefix)
        {
            var results = new List<SpawnPoint>();
            var allLocations = new List<DeliveryLocation>();

#if IL2CPP
            var locs = UnityEngine.Object.FindObjectsOfType<DeliveryLocation>();
            if (locs != null)
            {
                for (int i = 0; i < locs.Length; i++)
                {
                    var loc = locs[i];
                    if (loc != null && loc.TeleportPoint != null)
                    {
                        allLocations.Add(loc);
                    }
                }
            }
#else
            var locs = UnityEngine.Object.FindObjectsOfType<DeliveryLocation>();
            if (locs != null)
            {
                foreach (var loc in locs)
                {
                    if (loc != null && loc.TeleportPoint != null)
                    {
                        allLocations.Add(loc);
                    }
                }
            }
#endif

            // Sort by distance to origin USING TELEPORT POINT (Point A)
            allLocations.Sort((a, b) =>
            {
                // We use TeleportPoint because it represents the hidden spawn location (Point A)
                float distA = Vector3.Distance(origin, a.TeleportPoint.position);
                float distB = Vector3.Distance(origin, b.TeleportPoint.position);
                return distA.CompareTo(distB);
            });

            int limit = Mathf.Min(count, allLocations.Count);
            for (int i = 0; i < limit; i++)
            {
                var loc = allLocations[i];
                var pt = loc.TeleportPoint; // Point A (hidden spawn)
                float dist = Vector3.Distance(origin, pt.position);
                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"SpawnPoint {prefix}{i}: {loc.name} at ({pt.position.x:F1}, {pt.position.y:F1}, {pt.position.z:F1}) dist={dist:F0}");
                results.Add(new SpawnPoint($"{prefix}{i}", pt.position, pt.rotation));
            }

            OTCLog.Msg(OTCLog.Systems.Customer,
                $"Found {results.Count} spawn points for {prefix} (from {allLocations.Count} total DeliveryLocations)");
            return results;
        }

        /// <summary>
        /// Picks a random spawn point for a customer targeting the specified building.
        /// Falls back to shack points for unknown building IDs.
        /// </summary>
        public static SpawnPoint GetRandomSpawnPoint(string buildingId = null)
        {
            if (buildingId == Placement.Dispensary.DispensaryId)
                return PickRandom(DispensarySpawnPoints);

            if (buildingId == PropertySaveData.WarehouseId)
                return PickRandom(WarehousePoints);

            // Default: shack
            return PickRandom(ShackPoints);
        }

        private static SpawnPoint PickRandom(IList<SpawnPoint> points)
        {
            if (points == null || points.Count == 0) return null;
            return points[UnityEngine.Random.Range(0, points.Count)];
        }

        /// <summary>
        /// Finds 2-3 display cabinets inside the OTC building and returns LOCAL stand positions
        /// (in front of each shelf) along with LOCAL shelf centers (for facing).
        /// All coordinates are building-local for use with SendNPCToPosition.
        /// Returns an empty list when no storage is placed (triggers LookingAround state).
        /// </summary>
        private static readonly HashSet<string> ExcludedStorageIds = new()
        {
            "otc_checkout_counter",
            "locker",
        };
        private const float ShelfStandOffset = 0.25f;

        public static List<Vector3> GetInteriorBrowsePositions(out List<Vector3> shelfCenters)
        {
            var standPositions = new List<Vector3>();
            shelfCenters = new List<Vector3>();

            var nav = Placement.WestvilleShack.NavBuilder;
            if (nav == null) return standPositions;

            // Only scan the shack grid — not all OTC buildings
            var shackGrid = Placement.WestvilleShack.ShackGrid;
            if (shackGrid == null || !BuildingGridFactory.GridContainers.TryGetValue(shackGrid, out var root))
                return standPositions;

            try
            {
                var storages = root.GetComponentsInChildren<PlaceableStorageEntity>(true);
                if (storages == null) return standPositions;

                for (int i = 0; i < storages.Length; i++)
                {
                    var storage = storages[i];
                    if (storage == null || storage.transform == null) continue;

                    // Skip excluded storage types (checkout counter, locker)
                    var id = storage.ItemInstance?.ID;
                    if (id == null || ExcludedStorageIds.Contains(id))
                        continue;

                    // Only browse shelves that have packaged product
                    if (!HasPackagedProductInEntity(storage.gameObject))
                        continue;

                    // Stand in front of the shelf (local coords via S1MAPI)
                    var worldPos = storage.transform.position;
                    var worldStandPos = worldPos + storage.transform.forward * ShelfStandOffset;
                    standPositions.Add(nav.WorldToLocal(worldStandPos));
                    shelfCenters.Add(nav.WorldToLocal(worldPos));
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
            var points = AllSpawnPoints;
            for (int i = 0; i < points.Length; i++)
            {
                if (points[i] == point) return i;
            }
            return 0;
        }

        /// <summary>
        /// Returns a spawn point by index, clamped to valid range.
        /// </summary>
        public static SpawnPoint GetSpawnPointByIndex(int index)
        {
            var points = AllSpawnPoints;
            if (points.Length == 0) return null;
            index = Mathf.Clamp(index, 0, points.Length - 1);
            return points[index];
        }

        /// <summary>
        /// Returns true if any storage entity on the shack grid
        /// contains at least one packaged product (ProductItemInstance with AppliedPackaging).
        /// </summary>
        public static bool HasPackagedProduct()
        {
            var shackGrid = Placement.WestvilleShack.ShackGrid;
            if (shackGrid == null || !BuildingGridFactory.GridContainers.TryGetValue(shackGrid, out var root))
                return false;

            try
            {
                var storages = root.GetComponentsInChildren<StorageEntity>(true);
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
