using OverTheCounter.Utilities;
using S1MAPI.Building;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Storage;
using Grid = Il2CppScheduleOne.Tiles.Grid;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
#else
using ScheduleOne.ObjectScripts;
using ScheduleOne.Storage;
using Grid = ScheduleOne.Tiles.Grid;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Abstracts per-building references so CustomerInstance and CustomerManager
    /// work with any OTC building (Shack, Dispensary, etc.) without hardcoding.
    /// </summary>
    internal class BuildingTarget
    {
        /// <summary>S1MAPI NavigationBuilder for interior A* pathfinding.</summary>
        public NavigationBuilder NavBuilder;

        /// <summary>Building root transform for world-local coordinate conversion.</summary>
        public Transform BuildingTransform;

        /// <summary>Placement grid for this building (used by PropertyInventory).</summary>
        public Grid Grid;

        /// <summary>World position where NPCs walk to from exterior NavMesh (e.g., stair base).</summary>
        public Vector3 ExteriorApproachPosition;

        /// <summary>World position where NPCs walk after exiting the building.</summary>
        public Vector3 ExitWalkPosition;

        /// <summary>World position of the room center (used for initial interior entry).</summary>
        public Vector3 RoomCenterWorld;

        /// <summary>World position of the building (for warp point distance sorting).</summary>
        public Vector3 BuildingPosition;

        /// <summary>Building name for logging.</summary>
        public string Name;

        /// <summary>Room center in building-local coordinates for SendNPCToPosition.</summary>
        public Vector3 RoomCenterLocal =>
            NavBuilder != null ? NavBuilder.WorldToLocal(RoomCenterWorld) : Vector3.zero;

        /// <summary>Storage IDs excluded from browsing (checkout counter, locker, etc.).</summary>
        private static readonly HashSet<string> ExcludedStorageIds = new()
        {
            "otc_checkout_counter",
            "locker",
        };

        private const float ShelfStandOffset = 0.25f;

        /// <summary>
        /// Finds display shelves with packaged product inside this building and returns
        /// LOCAL stand positions (in front of each shelf) along with LOCAL shelf centers.
        /// Prefers shelves not already occupied by other browsing customers.
        /// Returns empty list when no storage with product is placed.
        /// </summary>
        /// <param name="shelfCenters">Output: local shelf center positions (for facing).</param>
        /// <param name="occupiedShelfPositions">Shelf centers already assigned to other customers (local coords).</param>
        public List<Vector3> GetInteriorBrowsePositions(out List<Vector3> shelfCenters,
            ICollection<Vector3> occupiedShelfPositions = null)
        {
            var standPositions = new List<Vector3>();
            shelfCenters = new List<Vector3>();

            if (NavBuilder == null || Grid == null) return standPositions;
            if (!BuildingGridFactory.GridContainers.TryGetValue(Grid, out var root))
                return standPositions;

            try
            {
                var storages = root.GetComponentsInChildren<PlaceableStorageEntity>(true);
                if (storages == null) return standPositions;

                for (int i = 0; i < storages.Length; i++)
                {
                    var storage = storages[i];
                    if (storage == null || storage.transform == null) continue;

                    var id = storage.ItemInstance?.ID;
                    if (id == null || ExcludedStorageIds.Contains(id))
                        continue;

                    if (!HasPackagedProductInEntity(storage.gameObject))
                        continue;

                    var worldPos = storage.transform.position;
                    var worldStandPos = worldPos + storage.transform.forward * ShelfStandOffset;
                    standPositions.Add(NavBuilder.WorldToLocal(worldStandPos));
                    shelfCenters.Add(NavBuilder.WorldToLocal(worldPos));
                }
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"Error scanning storage in {Name}: {ex.Message}");
            }

            if (standPositions.Count == 0)
                return standPositions;

            int count = Mathf.Min(standPositions.Count, UnityEngine.Random.Range(2, 4));

            // Partition into unoccupied and occupied shelves, preferring unoccupied
            if (occupiedShelfPositions != null && occupiedShelfPositions.Count > 0)
            {
                var freeStand = new List<Vector3>();
                var freeShelf = new List<Vector3>();
                var busyStand = new List<Vector3>();
                var busyShelf = new List<Vector3>();

                for (int i = 0; i < shelfCenters.Count; i++)
                {
                    if (IsPositionOccupied(shelfCenters[i], occupiedShelfPositions))
                    {
                        busyStand.Add(standPositions[i]);
                        busyShelf.Add(shelfCenters[i]);
                    }
                    else
                    {
                        freeStand.Add(standPositions[i]);
                        freeShelf.Add(shelfCenters[i]);
                    }
                }

                // Shuffle each group independently
                ShufflePaired(freeStand, freeShelf);
                ShufflePaired(busyStand, busyShelf);

                // Take from free first, then fill remainder from busy
                standPositions.Clear();
                shelfCenters.Clear();
                int fromFree = Mathf.Min(count, freeStand.Count);
                for (int i = 0; i < fromFree; i++)
                {
                    standPositions.Add(freeStand[i]);
                    shelfCenters.Add(freeShelf[i]);
                }
                int remaining = count - fromFree;
                for (int i = 0; i < remaining && i < busyStand.Count; i++)
                {
                    standPositions.Add(busyStand[i]);
                    shelfCenters.Add(busyShelf[i]);
                }
            }
            else
            {
                // No occupancy data — shuffle and pick randomly as before
                ShufflePaired(standPositions, shelfCenters);
                shelfCenters = shelfCenters.GetRange(0, count);
                standPositions = standPositions.GetRange(0, count);
            }

            return standPositions;
        }

        /// <summary>
        /// Returns true if a local position is within snap distance of any occupied position.
        /// </summary>
        private static bool IsPositionOccupied(Vector3 pos, ICollection<Vector3> occupied)
        {
            const float threshold = 0.1f;
            foreach (var o in occupied)
            {
                if (Vector3.SqrMagnitude(pos - o) < threshold * threshold)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if a storage GameObject contains at least one packaged product.
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
