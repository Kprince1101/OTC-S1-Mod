using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

#if IL2CPP
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppFishNet;
using Il2CppFishNet.Object;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Storage;
#else
using FishNet;
using FishNet.Object;
using ScheduleOne.Dialogue;
using ScheduleOne.Economy;
using ScheduleOne.Map;
using ScheduleOne.Storage;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Creates SupplierLocation stands inside the OTC Warehouse so suppliers
    /// can idle there with full shop functionality when not meeting elsewhere.
    /// </summary>
    public static class OTCSupplierArea
    {
        // Stand positions inside warehouse (local coords relative to building origin at SW corner).
        // Along the south wall, facing into the interior.
        private static readonly Vector3[] StandPositions =
        {
            new Vector3(10.6f, 0f, 7.6f),
            new Vector3(7.8f, 0f, 7.6f),
            new Vector3(2.6f, 0f, 7.0f),
            new Vector3(1.6f, 0f, 1.3f),
        };

        // Y rotation for each stand (degrees)
        private static readonly float[] StandYRotations = { 90f, 90f, 40f, 0f };

        private static readonly List<SupplierLocation> _locations = new List<SupplierLocation>();
        private static Transform _warehouseTransform;
        private static bool _initialized;
        private static bool _routineActive;
        private static WorldStorageEntity _deliveryBay;

        // Fixed GUID so warehouse inventory persists across sessions
        private const string DeliveryBayGuid = "0a1c0000-dead-ba00-0000-000000000001";

#if !IL2CPP
        // Cached reflection for Mono — private fields on Supplier
        private static FieldInfo _meetingGreetingField;
        private static FieldInfo _meetingChoiceField;
#endif

        /// <summary>All supplier stand locations inside the warehouse.</summary>
        public static List<SupplierLocation> WarehouseLocations => _locations;

        /// <summary>Returns the GameObject for a supplier stand by index, or null if out of range.</summary>
        public static GameObject GetStandObject(int index)
        {
            if (index < 0 || index >= _locations.Count) return null;
            return _locations[index]?.gameObject;
        }

        /// <summary>Returns the delivery bay GameObject, or null if not yet created.</summary>
        public static GameObject GetDeliveryBayObject()
        {
            return _deliveryBay?.gameObject;
        }

        /// <summary>Creates supplier stands and starts the idle-warp routine.</summary>
        public static void Initialize(Transform warehouseTransform)
        {
            if (_initialized) return;
            if (warehouseTransform == null) return;
            _warehouseTransform = warehouseTransform;

            try
            {
#if !IL2CPP
                _meetingGreetingField = typeof(Supplier).GetField("meetingGreeting",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _meetingChoiceField = typeof(Supplier).GetField("meetingChoice",
                    BindingFlags.NonPublic | BindingFlags.Instance);
#endif

                for (int i = 0; i < StandPositions.Length; i++)
                {
                    var loc = CreateSupplierLocation(warehouseTransform, i);
                    if (loc != null) _locations.Add(loc);
                }

                _routineActive = true;
                MelonCoroutines.Start(IdleSupplierWarpRoutine());
                _initialized = true;
                OTCLog.Msg(OTCLog.Systems.Patch, $"OTCSupplierArea: {_locations.Count} supplier stands created");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"OTCSupplierArea.Initialize failed: {ex.Message}");
            }
        }

        /// <summary>Stops routines and resets all static state.</summary>
        public static void Cleanup()
        {
            _routineActive = false;
            _locations.Clear();
            _deliveryBay = null;
            _warehouseTransform = null;
            _initialized = false;
        }

        private static SupplierLocation CreateSupplierLocation(Transform parent, int index)
        {
            // Create GO disabled — prevents Awake from firing before fields are set
            var locGO = new GameObject($"OTC_SupplierLoc_{index}");
            locGO.SetActive(false);
            locGO.transform.SetParent(parent);
            locGO.transform.localPosition = StandPositions[index];
            locGO.transform.localRotation = Quaternion.Euler(0, StandYRotations[index], 0);

            // GenericContainer — visual props, toggled by SetActiveSupplier.
            // Starts empty; SetupGenericContainers populates it after game load.
            var containerGO = new GameObject("GenericContainer");
            containerGO.transform.SetParent(locGO.transform);
            containerGO.transform.localPosition = Vector3.zero;

            // SupplierStandPoint — where the NPC stands + faces
            var standPointGO = new GameObject("SupplierStandPoint");
            standPointGO.transform.SetParent(locGO.transform);
            standPointGO.transform.localPosition = Vector3.zero;
            standPointGO.transform.localRotation = Quaternion.Euler(0, 90, 0);

            // POI on a disabled child — SetMainText still works (just stores string),
            // but OnEnable never fires so no UIPrefab crash.
            var poiGO = new GameObject("POI");
            poiGO.SetActive(false);
            poiGO.transform.SetParent(locGO.transform);
            var poi = poiGO.AddComponent<POI>();

            // Add the game's SupplierLocation component
            var supplierLoc = locGO.AddComponent<SupplierLocation>();
            supplierLoc.LocationName = "OTC Warehouse";
            supplierLoc.LocationDescription = "the OTC Warehouse";
            supplierLoc.GenericContainer = containerGO.transform;
            supplierLoc.SupplierStandPoint = standPointGO.transform;
            supplierLoc.PoI = poi;

#if IL2CPP
            supplierLoc.DeliveryBays = new Il2CppReferenceArray<WorldStorageEntity>(0);
#else
            supplierLoc.DeliveryBays = new WorldStorageEntity[0];
#endif

            // Activate — Awake fires: adds to AllLocations, hides GenericContainer, gets configs
            locGO.SetActive(true);

            return supplierLoc;
        }

        /// <summary>
        /// Warp a supplier to the warehouse and set up full shop interaction.
        /// The supplier stays Idle (so new meetup requests still work) but gets
        /// GenericContainer shown, delivery bays connected, and dialogue enabled.
        /// </summary>
        public static void WarpSupplierToWarehouse(Supplier supplier)
        {
            if (!_initialized || _warehouseTransform == null || _locations.Count == 0) return;
            if (supplier == null) return;

            int slot = GetSlotForSupplier(supplier);
            if (slot < 0) return;

            var location = _locations[slot];
            var standPoint = location.SupplierStandPoint;

            // Switch to employee NavMesh so the agent can exist on the indoor surface
            try
            {
                var agent = supplier.GetComponent<NavMeshAgent>();
                if (agent != null && ManagerSpawner.TryGetEmployeeNavMeshSettings(
                        out int empAgentType, out int empAreaMask))
                {
                    agent.agentTypeID = empAgentType;
                    agent.areaMask = empAreaMask;
                }
            }
            catch { }

            // Position the supplier
            supplier.Movement.Warp(standPoint.position);
            supplier.Movement.FaceDirection(standPoint.forward, 0f);
            supplier.SetVisible(true, false);

            // Show GenericContainer (table, props) and set POI text
            location.SetActiveSupplier(supplier);

            // Connect shop to warehouse delivery bays
            try
            {
                var shop = supplier.Shop;
                if (shop != null && location.DeliveryBays != null)
                {
#if IL2CPP
                    var srcBays = location.DeliveryBays;
                    var shopBays = new Il2CppReferenceArray<StorageEntity>(srcBays.Length);
                    for (int j = 0; j < srcBays.Length; j++)
                        shopBays[j] = srcBays[j];
                    shop.DeliveryBays = shopBays;
#else
                    shop.DeliveryBays = location.DeliveryBays;
#endif
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"WarpSupplier shop setup: {ex.Message}");
            }

            // Enable meeting dialogue so player can interact and open shop
            EnableWarehouseDialogue(supplier);

            OTCLog.Msg(OTCLog.Systems.Patch,
                $"Supplier {supplier.fullName} stationed at warehouse slot {slot}");
        }

        /// <summary>
        /// Cleanup warehouse state when a supplier leaves for a real meetup elsewhere.
        /// Called from SupplierWarehousePatch.MeetAtLocation_Prefix.
        /// </summary>
        public static void CleanupWarehouseSupplier(Supplier supplier)
        {
            if (supplier == null) return;

            for (int i = 0; i < _locations.Count; i++)
            {
                if (_locations[i].ActiveSupplier == supplier)
                {
                    _locations[i].SetActiveSupplier(null);
                    DisableWarehouseDialogue(supplier);
                    OTCLog.Msg(OTCLog.Systems.Patch,
                        $"Supplier {supplier.fullName} left warehouse for meetup");
                    break;
                }
            }
        }

        /// <summary>
        /// Enable the supplier's meeting dialogue so the shop GUI opens on interaction.
        /// </summary>
        private static void EnableWarehouseDialogue(Supplier supplier)
        {
            try
            {
#if IL2CPP
                var greeting = supplier.meetingGreeting;
                var choice = supplier.meetingChoice;
                if (greeting != null) greeting.ShouldShow = true;
                if (choice != null) choice.Enabled = true;
#else
                var greeting = _meetingGreetingField?.GetValue(supplier)
                    as DialogueController.GreetingOverride;
                var choice = _meetingChoiceField?.GetValue(supplier)
                    as DialogueController.DialogueChoice;
                if (greeting != null) greeting.ShouldShow = true;
                if (choice != null) choice.Enabled = true;
#endif
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"EnableWarehouseDialogue: {ex.Message}");
            }
        }

        /// <summary>
        /// Disable the supplier's meeting dialogue flags.
        /// </summary>
        private static void DisableWarehouseDialogue(Supplier supplier)
        {
            try
            {
#if IL2CPP
                var greeting = supplier.meetingGreeting;
                var choice = supplier.meetingChoice;
                if (greeting != null) greeting.ShouldShow = false;
                if (choice != null) choice.Enabled = false;
#else
                var greeting = _meetingGreetingField?.GetValue(supplier)
                    as DialogueController.GreetingOverride;
                var choice = _meetingChoiceField?.GetValue(supplier)
                    as DialogueController.DialogueChoice;
                if (greeting != null) greeting.ShouldShow = false;
                if (choice != null) choice.Enabled = false;
#endif
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"DisableWarehouseDialogue: {ex.Message}");
            }
        }

        /// <summary>
        /// Maps a supplier to a consistent warehouse slot based on their order in the scene.
        /// </summary>
        private static int GetSlotForSupplier(Supplier supplier)
        {
            var allSuppliers = FindAllSuppliers();
            for (int i = 0; i < allSuppliers.Count; i++)
            {
                if (allSuppliers[i] == supplier)
                    return i < _locations.Count ? i : -1;
            }
            return -1;
        }

        private static List<Supplier> FindAllSuppliers()
        {
            var result = new List<Supplier>();
            var found = UnityEngine.Object.FindObjectsOfType<Supplier>();
            if (found == null) return result;
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] != null)
                    result.Add(found[i]);
            }
            return result;
        }

        /// <summary>
        /// Clones the GenericContainer visuals (table, briefcase, lamp, etc.) from a
        /// vanilla SupplierLocation into each warehouse location so suppliers have props.
        /// </summary>
        private static void SetupGenericContainers()
        {
            // Find a vanilla SupplierLocation with GenericContainer children to clone from
            Transform sourceContainer = null;
            var allLocs = SupplierLocation.AllLocations;
            for (int i = 0; i < allLocs.Count; i++)
            {
                var loc = allLocs[i];
                if (loc == null || _locations.Contains(loc)) continue;
                if (loc.GenericContainer != null && loc.GenericContainer.childCount > 0)
                {
                    sourceContainer = loc.GenericContainer;
                    break;
                }
            }

            if (sourceContainer == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, "No vanilla GenericContainer found to clone");
                return;
            }

            int clonedCount = 0;
            for (int i = 0; i < _locations.Count; i++)
            {
                var loc = _locations[i];
                if (loc == null) continue;

                var oldContainer = loc.GenericContainer;

                // Clone the entire vanilla GenericContainer subtree
                var clone = UnityEngine.Object.Instantiate(sourceContainer.gameObject, loc.transform);
                clone.name = "GenericContainer";
                clone.transform.localPosition = new Vector3(0.8f, 0f, 0f);
                clone.transform.localRotation = Quaternion.Euler(0, 180, 0);

                // Strip game logic — keep only visual components
                foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                    UnityEngine.Object.Destroy(mb);
                foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
                    UnityEngine.Object.Destroy(rb);

                // Update the location's GenericContainer reference
                loc.GenericContainer = clone.transform;

                // Hide it initially (SetActiveSupplier will show it)
                clone.SetActive(false);

                // Destroy old empty container
                if (oldContainer != null)
                    UnityEngine.Object.Destroy(oldContainer.gameObject);

                clonedCount++;
            }

            OTCLog.Msg(OTCLog.Systems.Patch,
                $"GenericContainers cloned for {clonedCount} warehouse locations");
        }

        /// <summary>
        /// Clones a WorldStorageEntity from a vanilla SupplierLocation to serve as
        /// a shared delivery bay for all warehouse supplier stands.
        /// </summary>
        private static void SetupDeliveryBay()
        {
            if (_deliveryBay != null || _warehouseTransform == null) return;

            try
            {
                // Find a vanilla SupplierLocation with at least one delivery bay to clone from
                WorldStorageEntity sourceBay = null;
                var allLocations = SupplierLocation.AllLocations;
                for (int i = 0; i < allLocations.Count; i++)
                {
                    var loc = allLocations[i];
                    if (loc == null || _locations.Contains(loc)) continue;
                    if (loc.DeliveryBays != null && loc.DeliveryBays.Length > 0)
                    {
                        sourceBay = loc.DeliveryBays[0];
                        break;
                    }
                }

                if (sourceBay == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, "No vanilla delivery bay found to clone");
                    return;
                }

                // Clone under a disabled parent so Awake doesn't fire yet
                var tempParent = new GameObject("OTC_TempParent");
                tempParent.SetActive(false);

                var cloneGO = UnityEngine.Object.Instantiate(sourceBay.gameObject, tempParent.transform);
                cloneGO.name = "OTC_DeliveryBay";
                cloneGO.SetActive(false); // ensure it stays inactive during reparent

                var entity = cloneGO.GetComponent<WorldStorageEntity>();

                // Set unique GUID before Awake (IL2CPP exposes public setter, Mono needs reflection)
#if IL2CPP
                entity.BakedGUID = DeliveryBayGuid;
#else
                typeof(WorldStorageEntity)
                    .GetField("BakedGUID", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.SetValue(entity, DeliveryBayGuid);
#endif
                entity.StorageEntityName = "Warehouse Storage";
                entity.EmptyOnSleep = false;

                // Clear cloned item slots — Awake will create fresh ones from SlotCount
                try { entity.ItemSlots.Clear(); } catch { }

                // Reparent into warehouse (still inactive)
                cloneGO.transform.SetParent(_warehouseTransform);
                cloneGO.transform.localPosition = new Vector3(2.1f, 0f, 3.3f);
                cloneGO.transform.localRotation = Quaternion.identity;

                // Activate — Awake fires with our GUID, creates 20 fresh ItemSlots
                cloneGO.SetActive(true);
                UnityEngine.Object.Destroy(tempParent);

                // Network-spawn so FishNet RPCs work in multiplayer
                try
                {
                    var netObj = cloneGO.GetComponent<NetworkObject>();
                    if (netObj != null && InstanceFinder.ServerManager != null)
                        InstanceFinder.ServerManager.Spawn(netObj);
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, $"DeliveryBay network spawn: {ex.Message}");
                }

                _deliveryBay = entity;

                // Point all warehouse SupplierLocations at the shared bay
                for (int i = 0; i < _locations.Count; i++)
                {
#if IL2CPP
                    _locations[i].DeliveryBays = new Il2CppReferenceArray<WorldStorageEntity>(
                        new WorldStorageEntity[] { entity });
#else
                    _locations[i].DeliveryBays = new WorldStorageEntity[] { entity };
#endif
                }

                OTCLog.Msg(OTCLog.Systems.Patch,
                    $"OTC delivery bay created ({entity.SlotCount} slots)");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"SetupDeliveryBay failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Periodically checks for idle invisible suppliers and warps them to the warehouse.
        /// Handles initial game load, post-sleep, and edge cases.
        /// </summary>
        private static IEnumerator IdleSupplierWarpRoutine()
        {
            // Wait for the game to fully load (NPCs, network, etc.)
            yield return new WaitForSeconds(10f);

            SetupGenericContainers();
            SetupDeliveryBay();

            while (_routineActive && _initialized)
            {
                try
                {
                    var suppliers = FindAllSuppliers();
                    for (int i = 0; i < suppliers.Count && i < _locations.Count; i++)
                    {
                        var supplier = suppliers[i];
                        if (supplier == null) continue;

                        // Warp idle invisible suppliers to the warehouse
                        if (supplier.Status == Supplier.ESupplierStatus.Idle
                            && !supplier.isVisible)
                        {
                            WarpSupplierToWarehouse(supplier);
                        }
                    }
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, $"IdleSupplierWarpRoutine: {ex.Message}");
                }

                yield return new WaitForSeconds(30f);
            }
        }
    }
}
