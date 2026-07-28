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
            new Vector3(10.1f, 0f, 7.35f),
            new Vector3(7.8f, 0f, 7.35f),
            new Vector3(2.6f, 0f, 7.0f),
            new Vector3(1.6f, 0f, 1.3f),
        };

        // Y rotation for each stand (degrees)
        private static readonly float[] StandYRotations = { 90f, 90f, 40f, 0f };

        private static readonly List<SupplierLocation> _locations = new List<SupplierLocation>();
        private static readonly Dictionary<Supplier, int> _assignedSuppliers = new Dictionary<Supplier, int>();

        /// <summary>
        /// Supplier IDs that have completed at least one successful <see cref="WarpSupplierToWarehouse"/>.
        /// After <see cref="CleanupWarehouseSupplier"/> for a meetup, <see cref="IsWarehouseSupplier"/> is false;
        /// without this, the idle routine would treat them as "never stationed" and re-warp them every 10s,
        /// fighting <c>MeetAtLocation</c> (table/shop at meet site, body stuck at warehouse).
        /// </summary>
        private static readonly HashSet<string> _stationedAtWarehouseEver = new HashSet<string>();
        private static readonly Dictionary<string, int> _meetupExpireBySupplier = new Dictionary<string, int>();

        /// <summary>
        /// Suppliers whose stand position has been synced to clients via <see cref="NPCMovement.Warp"/>
        /// after S1MAPI finished walking them to the stand. Cleared when the supplier leaves for a meetup.
        /// </summary>
        private static readonly HashSet<string> _syncedToStand = new HashSet<string>();

        private static Transform _warehouseTransform;
        private static bool _initialized;
        private static bool _routineActive;
        private static WorldStorageEntity _deliveryBay;

        // Fixed GUID so warehouse inventory persists across sessions
        private const string DeliveryBayGuid = "0a1c0000-dead-ba00-0000-000000000001";

        /// <summary>
        /// For suppliers <b>already assigned</b> to a warehouse stand: only re-warp while invisible if still near the door.
        /// (First-time placement uses unlimited distance — vanilla spawns are far from the warehouse.)
        /// </summary>
        private const float InvisibleRescueMaxDoorDistance = 28f;

        /// <summary>
        /// Re-warp assigned suppliers only if they are still near their stand (nav drift inside the building).
        /// Beyond this, they left for a meetup or another task — clear assignment instead of warping.
        /// </summary>
        private const float DriftRecenterMaxDistanceFromStand = 42f;

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

        /// <summary>Returns true if the given supplier is currently assigned to a warehouse stand.</summary>
        public static bool IsWarehouseSupplier(Supplier supplier) =>
            supplier != null && _assignedSuppliers.ContainsKey(supplier);

        /// <summary>Returns a snapshot of all suppliers currently assigned to warehouse stands.</summary>
        public static List<Supplier> GetAssignedSuppliers() =>
            new List<Supplier>(_assignedSuppliers.Keys);

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
            _assignedSuppliers.Clear();
            _stationedAtWarehouseEver.Clear();
            _meetupExpireBySupplier.Clear();
            _syncedToStand.Clear();
            _deliveryBay = null;
            _warehouseTransform = null;
            _initialized = false;
        }

        private static int GetCurrentElapsedMinutes()
        {
            int hhmm = S1API.GameTime.TimeManager.CurrentTime;
            int hours = hhmm / 100;
            int mins = hhmm % 100;
            return (S1API.GameTime.TimeManager.ElapsedDays * 1440) + (hours * 60) + mins;
        }

        /// <summary>
        /// Tracks when a meetup should expire, so overdue suppliers can be forced back to OTC.
        /// </summary>
        public static void MarkMeetupStart(Supplier supplier, int expireInMinutes)
        {
            if (supplier == null || expireInMinutes <= 0) return;
            string key = GetSupplierKey(supplier);
            if (string.IsNullOrEmpty(key)) return;
            _meetupExpireBySupplier[key] = GetCurrentElapsedMinutes() + expireInMinutes;
        }

        private static void ClearMeetupTimer(Supplier supplier)
        {
            if (supplier == null) return;
            string key = GetSupplierKey(supplier);
            if (!string.IsNullOrEmpty(key))
                _meetupExpireBySupplier.Remove(key);
        }

        private static string GetSupplierKey(Supplier supplier)
        {
            if (supplier == null) return string.Empty;
            try
            {
                if (!string.IsNullOrEmpty(supplier.ID))
                    return supplier.ID;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"GetSupplierKey ID access failed: {ex.Message}");
            }
            return $"{supplier.name}_{supplier.GetInstanceID()}";
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
            standPointGO.transform.localPosition = new Vector3(-0.5f, 0f, 0f);
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

            _assignedSuppliers[supplier] = slot;

            var location = _locations[slot];
            var standPoint = location.SupplierStandPoint;

            // Warp to just outside the doorway (on valid outdoor NavMesh, not inside carved zone)
            var doorExterior = OTCWarehouse.GetDoorExteriorPosition();
            supplier.Movement.Warp(doorExterior);
            // Navigate in via S1MAPI's interior pathfinding (must use 4-param overload)
            supplier.Movement.SetDestination(standPoint.position, null, 1f, 1f);

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

            _stationedAtWarehouseEver.Add(GetSupplierKey(supplier));
            ClearMeetupTimer(supplier);

            OTCLog.Msg(OTCLog.Systems.Patch,
                $"Supplier {supplier.FullName} stationed at warehouse slot {slot}");
        }

        /// <summary>
        /// Cleanup warehouse state when a supplier leaves for a real meetup elsewhere.
        /// Called from SupplierWarehousePatch.MeetAtLocation_Prefix.
        /// </summary>
        public static void CleanupWarehouseSupplier(Supplier supplier)
        {
            if (supplier == null) return;
            _assignedSuppliers.Remove(supplier);

            string key = GetSupplierKey(supplier);
            if (!string.IsNullOrEmpty(key))
                _syncedToStand.Remove(key);

            for (int i = 0; i < _locations.Count; i++)
            {
                if (_locations[i].ActiveSupplier == supplier)
                {
                    _locations[i].SetActiveSupplier(null);
                    DisableWarehouseDialogue(supplier);
                    OTCLog.Msg(OTCLog.Systems.Patch,
                        $"Supplier {supplier.FullName} left warehouse for meetup");
                    break;
                }
            }
        }

        private static bool ForceReturnIfMeetupOverdue(Supplier supplier)
        {
            if (supplier == null) return false;
            string key = GetSupplierKey(supplier);
            if (string.IsNullOrEmpty(key)) return false;
            if (!_meetupExpireBySupplier.TryGetValue(key, out int expireAt)) return false;
            if (GetCurrentElapsedMinutes() < expireAt) return false;

            _meetupExpireBySupplier.Remove(key);
            OTCLog.Warning(OTCLog.Systems.Patch,
                $"Supplier meetup overdue: forcing end + warehouse return for {supplier.FullName}");
            bool ended = true;
            try { supplier.EndMeeting(); } catch { ended = false; }
            if (!ended)
                WarpSupplierToWarehouse(supplier);
            return true;
        }

        /// <summary>
        /// Meetup handoff: recall supplier from S1MAPI interior, wait for full release,
        /// then warp to the meetup location. Vanilla's MeetAtLocation runs concurrently but
        /// S1MAPI overrides its warp on the server — this coroutine does the authoritative
        /// server-side warp once S1MAPI is no longer managing the NPC.
        /// </summary>
        public static void ReleaseAndWarpSupplierToMeetup(Supplier supplier, Vector3 meetupWorldPos, Vector3 meetupForward)
        {
            if (supplier?.Movement == null) return;
            if (meetupWorldPos == Vector3.zero) return;
            MelonCoroutines.Start(ReleaseAndWarpSupplierToMeetupRoutine(supplier, meetupWorldPos, meetupForward));
        }

        private static IEnumerator ReleaseAndWarpSupplierToMeetupRoutine(Supplier supplier, Vector3 meetupWorldPos, Vector3 meetupForward)
        {
            if (supplier?.Movement == null) yield break;
            if (meetupWorldPos == Vector3.zero) yield break;

            var nav = OTCWarehouse.NavBuilder;
            bool wasTracked = false;
            try
            {
                if (nav != null && nav.IsNPCInside(supplier.Movement))
                {
                    wasTracked = true;
                    OTCLog.Msg(OTCLog.Systems.Patch,
                        $"Supplier meetup handoff: {supplier.FullName} inside S1MAPI interior, issuing RecallNPC");
                    nav.RecallNPC(supplier.Movement);
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"ReleaseAndWarpSupplierToMeetup (recall): {ex.Message}");
            }

            // Wait for S1MAPI to fully release the NPC before warping.
            // Any warp attempt while S1MAPI is tracking will be overridden by its position
            // enforcement (InteriorNavigatorCore restores LastValidPos every frame).
            if (wasTracked && nav != null)
            {
                float timeoutAt = Time.time + 15f;
                bool released = false;
                while (Time.time < timeoutAt)
                {
                    if (supplier?.Movement == null) yield break;
                    try
                    {
                        released = !nav.IsNPCInside(supplier.Movement);
                        if (released) break;
                    }
                    catch (Exception ex)
                    {
                        OTCLog.Warning(OTCLog.Systems.Patch,
                            $"ReleaseAndWarpSupplierToMeetup (poll): {ex.Message}");
                        break;
                    }

                    yield return new WaitForSeconds(0.2f);
                }

                if (!released)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,
                        $"Supplier meetup handoff: {supplier.FullName} S1MAPI release timed out after 15s — " +
                        $"supplier may walk to meetup via NavMesh (PendingExteriorDestination)");
                    yield break;
                }

                OTCLog.Msg(OTCLog.Systems.Patch,
                    $"Supplier meetup handoff: {supplier.FullName} released from S1MAPI, warping to meetup");
            }

            // S1MAPI no longer tracking — warp is safe.
            // Stop() first: S1MAPI's ReleaseNPC restores HasDestination=true which
            // causes the game's UpdateDestination to path the NPC on the next FixedUpdate.
            // Without Stop(), the NPC walks away from the meetup immediately after warp.
            try
            {
                if (supplier?.Movement == null) yield break;
                OTCWarehouse.SetDoorColliderEnabled(true);
                supplier.Movement.Stop();
                supplier.Movement.Warp(meetupWorldPos);
                if (meetupForward.sqrMagnitude > 0.001f)
                {
                    supplier.Movement.FaceDirection(meetupForward, 0f);
                    // Re-apply facing after a short settle (agent path can override rotation)
                    MelonCoroutines.Start(ApplyFacingAfterDelay(supplier, meetupForward, 0.35f));
                }
                OTCLog.Msg(OTCLog.Systems.Patch,
                    $"Supplier meetup handoff: {supplier.FullName} warped to meetup at {meetupWorldPos}");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,
                    $"ReleaseAndWarpSupplierToMeetup (warp): {ex.Message}");
            }
        }

        private static IEnumerator ApplyFacingAfterDelay(Supplier supplier, Vector3 forward, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (supplier?.Movement == null) yield break;
            if (forward.sqrMagnitude <= 0.001f) yield break;
            try { supplier.Movement.FaceDirection(forward, 0f); }
            catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.Patch, $"ApplyFacingAfterDelay: {ex.Message}"); }
        }

        /// <summary>
        /// Clears warehouse stand assignment without moving the supplier (off-site / meetup).
        /// </summary>
        private static void UnassignSupplierWithoutWarp(Supplier supplier)
        {
            if (supplier == null || !_assignedSuppliers.Remove(supplier))
                return;

            string key = GetSupplierKey(supplier);
            if (!string.IsNullOrEmpty(key))
                _syncedToStand.Remove(key);

            for (int i = 0; i < _locations.Count; i++)
            {
                if (_locations[i].ActiveSupplier == supplier)
                {
                    _locations[i].SetActiveSupplier(null);
                    DisableWarehouseDialogue(supplier);
                    OTCLog.Msg(OTCLog.Systems.Patch,
                        $"Supplier {supplier.FullName} unassigned from warehouse stand (off-site)");
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
            // Wait a frame for Unity to process new colliders before rebuilding nav
            yield return null;
            OTCWarehouse.RebuildNavigation();

            // Disable door collider so suppliers can walk through
            OTCWarehouse.SetDoorColliderEnabled(false);

            while (_routineActive && _initialized)
            {
                // Only the host should warp NPCs / mutate local stand assignment; clients receive synced state.
                if (!NetworkHelper.IsHost)
                {
                    yield return new WaitForSeconds(10f);
                    continue;
                }

                try
                {
                    bool anySent = false;
                    var suppliers = FindAllSuppliers();
                    var doorExterior = OTCWarehouse.GetDoorExteriorPosition();
                    // Process every supplier (not capped at stand count) so each gets a slot via GetSlotForSupplier.
                    for (int i = 0; i < suppliers.Count; i++)
                    {
                        var supplier = suppliers[i];
                        if (supplier == null) continue;

                        if (ForceReturnIfMeetupOverdue(supplier))
                        {
                            anySent = true;
                            continue;
                        }

                        // Meeting / dead-drop prep — never pull these back to the warehouse
                        if (supplier.Status != Supplier.ESupplierStatus.Idle)
                            continue;

                        // First station only: suppliers who have never had a successful warp stay not-assigned until we pull them in.
                        // Do NOT treat "unassigned after CleanupWarehouseSupplier for meetup" as first station — that would
                        // re-warp every 10s and override MeetAtLocation (NPC stuck at door, props at meet site).
                        if (!IsWarehouseSupplier(supplier))
                        {
                            if (!_stationedAtWarehouseEver.Contains(GetSupplierKey(supplier)))
                            {
                                OTCWarehouse.SetDoorColliderEnabled(false);
                                WarpSupplierToWarehouse(supplier);
                                anySent = true;
                            }
                            continue;
                        }

                        // Already assigned — invisible: only re-pull if near door, else unassign (meetup elsewhere).
                        if (!supplier.isVisible)
                        {
                            float distDoor = Vector3.Distance(supplier.transform.position, doorExterior);
                            if (distDoor <= InvisibleRescueMaxDoorDistance)
                            {
                                OTCWarehouse.SetDoorColliderEnabled(false);
                                WarpSupplierToWarehouse(supplier);
                                anySent = true;
                            }
                            else
                            {
                                UnassignSupplierWithoutWarp(supplier);
                            }
                        }
                        // Visible + assigned: drift correction near stand only
                        else if (_assignedSuppliers.TryGetValue(supplier, out int slot)
                            && slot < _locations.Count)
                        {
                            var stand = _locations[slot].SupplierStandPoint;
                            float dist = Vector3.Distance(supplier.transform.position, stand.position);
                            if (dist > DriftRecenterMaxDistanceFromStand)
                            {
                                UnassignSupplierWithoutWarp(supplier);
                            }
                            else if (dist > 3f)
                            {
                                OTCWarehouse.SetDoorColliderEnabled(false);
                                WarpSupplierToWarehouse(supplier);
                                anySent = true;
                            }
                            else if (dist < 1.5f)
                            {
                                supplier.Movement.FaceDirection(stand.forward, 0f);

                                // Bug fix: re-show delivery bays after sleep.
                                // SupplierLocation.OnSleep hides delivery bays, and
                                // SetActiveSupplier is the only way to re-show them.
                                _locations[slot].SetActiveSupplier(supplier);

                                // Bug fix: one-time position sync to clients.
                                // S1MAPI walks the supplier to the stand via direct
                                // transform moves (no ReceiveWarp RPC), so clients
                                // never see the final stand position. Warp here sends
                                // ReceiveWarp to confirm the position on all clients.
                                string syncKey = GetSupplierKey(supplier);
                                if (!string.IsNullOrEmpty(syncKey) && _syncedToStand.Add(syncKey))
                                {
                                    supplier.Movement.Warp(stand.position);
                                    OTCLog.Msg(OTCLog.Systems.Patch,
                                        $"Synced {supplier.FullName} stand position to clients");
                                }
                            }
                        }
                    }

                    // Re-enable door collider after suppliers have had time to enter
                    if (!anySent)
                        OTCWarehouse.SetDoorColliderEnabled(true);
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, $"IdleSupplierWarpRoutine: {ex.Message}");
                }

                yield return new WaitForSeconds(10f);
            }
        }
    }
}
