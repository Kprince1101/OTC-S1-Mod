using MelonLoader;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1MAPI.Building;
using S1MAPI.Building.Config;
using S1MAPI.Building.Interior;
using S1MAPI.Building.Structural;
using S1MAPI.Gltf;
using S1MAPI.S1;
using S1MAPI.Utils;
using S1API.GameTime;
using S1API.Misc;
using System;
using System.Reflection;
using UnityEngine;

#if IL2CPP
using Il2CppFishNet;
using Il2CppFishNet.Object;
using Il2CppFishNet.Observing;
using Il2CppFishNet.Component.Ownership;
using Il2CppScheduleOne.Doors;
using Grid = Il2CppScheduleOne.Tiles.Grid;
#else
using FishNet;
using FishNet.Object;
using FishNet.Observing;
using FishNet.Component.Ownership;
using ScheduleOne.Doors;
using Grid = ScheduleOne.Tiles.Grid;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Permanent OTC building — east-facing shack in Westville with placement grid.
    /// </summary>
    public static class WestvilleShack
    {
        // Room dimensions (smaller than B1's 8x9)
        private const float RoomWidth = 6f;
        private const float RoomHeight = 3.5f;
        private const float RoomDepth = 5f;
        private const float FoundationHeight = 0.4f;

        // SW corner of building footprint — ground slopes ~-3 to -3.5 here
        private static readonly Vector3 BuildingOrigin = new(-167.4f, -4f, 73.5f);

        private static GameObject _building;
        private static NavMeshRepairer _navMeshRepairer;
        private static GameObject _lightsFolder;
        private static ModularSwitch _lightSwitch;
        private static ModularSwitch _openCloseSwitch;
        private static bool _initialized;
        private static bool _suppressSwitchSync; // prevents re-entrancy during sync apply
        // Networked objects tracked for explicit cleanup (NOT parented to _building — parenting
        // changes local position which FishNet sends to clients instead of world position).
        private static readonly System.Collections.Generic.List<GameObject> _networkedObjects = new();
        internal static DoorController Door;
        internal static bool SuppressDoorSync;
        // Cached by DoorSyncPatch postfix — avoids reading Door.IsOpen which may
        // not reflect the new state by the time PublishGameState serializes it.
        internal static bool CachedDoorIsOpen;
        internal static EDoorSide CachedDoorSide;

        /// <summary>Whether the store is currently open for customers. Toggled by the open/close switch.</summary>
        public static bool IsStoreOpen { get; private set; }

        /// <summary>Whether the interior lights are currently on.</summary>
        public static bool AreLightsOn { get; private set; }

        /// <summary>Current door open/closed state — written by DoorSyncPatch, read by game state serializer.</summary>
        internal static bool IsDoorOpen => CachedDoorIsOpen;

        /// <summary>Last door side that triggered open — serialized as int (Interior=0, Exterior=1).</summary>
        internal static int DoorSideValue => (int)CachedDoorSide;

        /// <summary>The placement grid inside the shack. Set after build.</summary>
        internal static Grid ShackGrid { get; private set; }


        /// <summary>
        /// Builds and positions the Westville shack with placement grid and exterior props.
        /// </summary>
        public static void SpawnBuilding()
        {
            if (_initialized) return;

            try
            {
                BuildRoom();
                _initialized = true;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"SpawnBuilding failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>Destroys the building and resets state for scene reload.</summary>
        public static void Cleanup()
        {
            _navMeshRepairer?.Remove();
            _navMeshRepairer = null;
            _lightsFolder = null;
            _lightSwitch = null;
            _openCloseSwitch = null;
            Door = null;
            IsStoreOpen = false;
            AreLightsOn = false;
            ShackGrid = null;
            if (_building != null) GameObject.Destroy(_building);
            _building = null;
            // Networked objects are NOT children of _building — destroy them explicitly.
            foreach (var go in _networkedObjects)
                if (go != null) GameObject.Destroy(go);
            _networkedObjects.Clear();
            _initialized = false;
            _suppressSwitchSync = false;
            SuppressDoorSync = false;
            CachedDoorIsOpen = false;
            CachedDoorSide = default;
        }

        /// <summary>
        /// Enables or disables Light components under the lights folder.
        /// Keeps the GameObjects active so light fixtures remain visible.
        /// </summary>
        internal static void SetLightsEnabled(bool enabled)
        {
            AreLightsOn = enabled;
            if (_lightsFolder == null) return;
            foreach (var light in _lightsFolder.GetComponentsInChildren<Light>(true))
                light.enabled = enabled;
        }

        /// <summary>
        /// Sets lights state from multiplayer sync. Updates both the switch visual
        /// and the actual Light components, with re-entrancy guard to prevent sync loops.
        /// </summary>
        internal static void SetLightsFromSync(bool enabled)
        {
            _suppressSwitchSync = true;
            try
            {
                SetLightsEnabled(enabled);
                if (_lightSwitch != null)
                {
                    if (enabled) _lightSwitch.SwitchOn();
                    else _lightSwitch.SwitchOff();
                }
            }
            finally { _suppressSwitchSync = false; }
        }

        /// <summary>
        /// Clears terrain trees and scene objects around the shack. Deferred to onLoadComplete
        /// so terrain data is fully populated on both host and client before clearing.
        /// </summary>
        public static void ClearTerrain()
        {
            if (_building == null) return;
            TerrainClearer.ClearAroundBuilding(_building, new Vector3(RoomWidth, RoomHeight, RoomDepth),
                new ClearingOptions { Padding = 4f });
            TerrainFlattener.FlattenUnder(_building, new Vector3(RoomWidth, RoomHeight, RoomDepth), BuildingOrigin.y, blendDistance: 3f);
        }

        /// <summary>
        /// Instantiates a networked prefab with its world position set BEFORE calling Spawn().
        /// This guarantees FishNet captures the correct transform in the spawn packet regardless
        /// of whether it batches at Spawn() time or at NetworkLateUpdate time.
        /// InstantiateNetworked() sets position AFTER Spawn — if FishNet reads transform at
        /// Spawn() time the client receives the object at world origin (0,0,0).
        /// </summary>
        private static GameObject SpawnNetworkedAt(S1MAPI.Core.PrefabRef prefab, Vector3 worldPos, Quaternion worldRot, Action<GameObject> preSpawnConfigure = null)
        {
            var prefabGo = prefab.Find();
            if (prefabGo == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,$"SpawnNetworkedAt: prefab not found: {prefab.Name}");
                return null;
            }

            bool originalActive = prefabGo.activeSelf;
            prefabGo.SetActive(false);
            var instance = UnityEngine.Object.Instantiate(prefabGo);
            prefabGo.SetActive(originalActive);
            if (instance == null) return null;

            var netMgr = InstanceFinder.NetworkManager;
            if (netMgr != null && netMgr.IsServer)
            {
                var netObj = instance.GetComponent<NetworkObject>();
                if (netObj != null)
                {
                    // Remove NetworkObserver before Spawn. Prefabs like ClassicalWoodenDoor and
                    // ModularSwitch carry a DistanceCondition that fails for objects placed far from
                    // world origin (our shack is ~180m out). The prefab is instantiated inactive so
                    // Awake has not run and NetworkObject has not cached a reference to the observer —
                    // DestroyImmediate is safe here. Without NetworkObserver, FishNet broadcasts the
                    // spawn packet to ALL connected clients unconditionally.
                    var networkObserver = instance.GetComponent<NetworkObserver>();
                    if (networkObserver != null)
                        UnityEngine.Object.DestroyImmediate(networkObserver);

                    // Add PredictedSpawn if not already present. Without it, FishNet defers or drops
                    // spawn packets for late-joining clients when the object is spawned while the client
                    // is still loading the scene. PredictedSpawn bypasses this deferral, matching the
                    // behavior of BuildableItem (e.g. TrashCan) which always has PredictedSpawn.
                    if (instance.GetComponent<PredictedSpawn>() == null)
                        instance.AddComponent<PredictedSpawn>();

                    // Set world position before Spawn so the initial spawn packet carries correct coords.
                    instance.transform.SetPositionAndRotation(worldPos, worldRot);
                    // Apply pre-spawn configuration (e.g. activating child objects) so the
                    // initial state is baked into the FishNet spawn packet sent to clients.
                    preSpawnConfigure?.Invoke(instance);
                    netMgr.ServerManager.Spawn(netObj);
                }
                else
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,$"[SpawnNetworkedAt] '{prefab.Name}': no NetworkObject on root — not spawned!");
                    instance.transform.SetPositionAndRotation(worldPos, worldRot);
                }
            }
            else
            {
                // On client: IsServer=false, so we only instantiate locally (no FishNet spawn).
                // This is intentional for prefabs that don't replicate via the game's ReplicationQueue.
                instance.transform.SetPositionAndRotation(worldPos, worldRot);
                // FishNet's NetworkObject.Start() calls TryStartDeactivation() which will SetActive(false)
                // when NetworkManager is null (never set for non-spawned instances). We re-enable via
                // coroutine after Start() has run — the second SetActive(true) only triggers OnEnable,
                // not Start again, so TryStartDeactivation does not fire a second time.
                MelonLoader.MelonCoroutines.Start(ReactivateAfterFishNetStart(instance, prefab.Name));
            }

            instance.SetActive(true);
            return instance;
        }

        /// <summary>
        /// Waits two frames after the first SetActive(true), then re-enables the object.
        /// Needed for client-local NetworkObject instances: FishNet's NetworkObject.Start() calls
        /// TryStartDeactivation() which calls SetActive(false) when NetworkManager is null.
        /// After Start() has run once, re-enabling only triggers OnEnable — not Start — so
        /// TryStartDeactivation will not fire again and the object remains active.
        /// </summary>
        private static System.Collections.IEnumerator ReactivateAfterFishNetStart(GameObject go, string name)
        {
            yield return null;  // wait for Start() to run (deactivates the object)
            yield return null;  // extra safety frame
            if (go != null && !go.activeSelf)
                go.SetActive(true);
        }

        /// <summary>
        /// Spawns/instantiates shack objects when the game is ready.
        /// Door and switches: host spawns networked via FishNet; client instantiates locally
        ///   (DoorController and ModularSwitch are not BuildableItems and don't replicate to
        ///   late-joining clients via FishNet's normal late-join path — the game's ReplicationQueue
        ///   only covers BuildableItems, so we give each side its own local instance).
        /// TrashCan: host-only networked spawn (BuildableItem, arrives on client via FishNet).
        /// </summary>
        public static void SpawnNetworkedObjects()
        {
            if (_building == null) return;

            // Door — host spawns via FishNet; client polls for the FishNet-delivered instance.
            // Using the FishNet-managed door directly means FishNet's ObserversRpc handles
            // host→client and SetIsOpen_Server handles client→host — identical to ModularSwitch.
            try
            {
                var doorLocalPos = new Vector3(RoomWidth, 0f, RoomDepth / 2f - 1.2f);
                var doorWorldPos = _building.transform.TransformPoint(doorLocalPos);
                var doorRot = _building.transform.rotation * Quaternion.Euler(0f, 270f, 0f);

                var doorGo = SpawnNetworkedAt(Prefabs.ClassicalWoodenDoor, doorWorldPos, doorRot);
                if (doorGo != null)
                {
                    _networkedObjects.Add(doorGo);
                    ConfigureDoor(doorGo);
                }

                if (!NetworkHelper.IsHost)
                {
                    // Client also scans for the FishNet-managed door from the host.
                    // If found, we switch WestvilleShack.Door to it so FishNet's ObserversRpc
                    // (host→client) works natively — identical to how ModularSwitch syncs.
                    MelonLoader.MelonCoroutines.Start(WaitForFishNetDoor(doorWorldPos));
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"Door spawn failed: {ex.Message}");
            }

            // Light switch
            try
            {
                var lightLocalPos = new Vector3(RoomWidth - 0.1f, 1.2f, 2.10f);
                var switchGo = SpawnNetworkedAt(Prefabs.ModularSwitch,
                    _building.transform.TransformPoint(lightLocalPos),
                    _building.transform.rotation * Quaternion.Euler(0f, 270f, 0f));
                if (switchGo != null)
                {
                    _networkedObjects.Add(switchGo);
                    _lightSwitch = new ModularSwitch(switchGo);
                    _lightSwitch.SetInteractionMessages("Turn Off Lights", "Turn On Lights");
                    _lightSwitch.OnToggled += isOn =>
                    {
                        SetLightsEnabled(isOn);
                        if (!_suppressSwitchSync)
                        {
                            if (NetworkHelper.IsHost)
                                ConfigSyncData.Instance?.PublishGameState();
                            else
                                ConfigSyncData.SendQuestAction($"SHACK_LIGHTS:{(isOn ? 1 : 0)}");
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"Light switch spawn failed: {ex.Message}");
            }

            // Open/Close switch
            try
            {
                var storeLocalPos = new Vector3(RoomWidth - 0.1f, 1.2f, 2.35f);
                var openSwitchGo = SpawnNetworkedAt(Prefabs.ModularSwitch,
                    _building.transform.TransformPoint(storeLocalPos),
                    _building.transform.rotation * Quaternion.Euler(0f, 270f, 0f));
                if (openSwitchGo != null)
                {
                    _networkedObjects.Add(openSwitchGo);
                    _openCloseSwitch = new ModularSwitch(openSwitchGo);
                    _openCloseSwitch.OnToggled += isOn =>
                    {
                        IsStoreOpen = isOn;
                        UpdateOpenCloseSwitchMessages();
                        if (!_suppressSwitchSync)
                        {
                            if (NetworkHelper.IsHost)
                                ConfigSyncData.Instance?.PublishGameState();
                            else
                                ConfigSyncData.SendQuestAction($"SHACK_STORE:{(isOn ? 1 : 0)}");
                        }
                    };
                    UpdateOpenCloseSwitchMessages();
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"Open/Close switch spawn failed: {ex.Message}");
            }

            // Trash can — HOST ONLY. BuildableItem, so FishNet delivers it to clients via the
            // game's ReplicationQueue late-join path. No need to instantiate locally on client.
            if (NetworkHelper.IsHost)
            {
                try
                {
                    float extGround = -FoundationHeight;
                    var trashLocalPos = new Vector3(7.0f, extGround, -1.0f);
                    var trashGo = SpawnNetworkedAt(Prefabs.TrashCan,
                        _building.transform.TransformPoint(trashLocalPos),
                        _building.transform.rotation,
                        preSpawnConfigure: inst =>
                        {
                            foreach (var t in inst.GetComponentsInChildren<Transform>(true))
                            {
                                if (t.name.StartsWith("Lid"))
                                    t.gameObject.SetActive(true);
                                else if (t.name == "DetectionArea" || t.name == "FootprintTiles" || t.name == "CircleProjector")
                                    t.gameObject.SetActive(false);
                            }
                        });
                    if (trashGo != null)
                        _networkedObjects.Add(trashGo);
                }
                catch (Exception ex)
                {
                    OTCLog.Error(OTCLog.Systems.Patch,$"Trash can spawn failed: {ex.Message}");
                }
            }
            else
            {
                // CLIENT: TrashCan arrives via FishNet (BuildableItem), but FishNet's spawn packet
                // doesn't sync child GameObject active states — the client instantiates from the
                // prefab's default (lid inactive). Poll until the TrashCan appears near the shack,
                // then activate its lid.
                float extGround = -FoundationHeight;
                var trashWorldPos = _building.transform.TransformPoint(new Vector3(7.0f, extGround, -1.0f));
                MelonLoader.MelonCoroutines.Start(WaitAndFixTrashCanLid(trashWorldPos));
            }
        }

        /// <summary>
        /// Polls for the FishNet-delivered DoorController near the expected world position and
        /// calls ConfigureDoor on it. FishNet replicates the host's spawned door to clients;
        /// using that instance directly gives us ObserversRpc updates (host→client) and
        /// SetIsOpen_Server (client→host) for free — same as ModularSwitch.
        /// </summary>
        private static System.Collections.IEnumerator WaitForFishNetDoor(Vector3 expectedWorldPos)
        {
            float timeout = 30f;
            float elapsed = 0f;
            while (elapsed < timeout)
            {
                yield return new WaitForSeconds(0.5f);
                elapsed += 0.5f;
                foreach (var dc in Resources.FindObjectsOfTypeAll<DoorController>())
                {
                    var no = dc.GetComponentInParent<NetworkObject>();
                    if (no == null || no.ObjectId == 0) continue;
                    // Use the NetworkObject root's position — DoorController is a child and may be offset.
                    if (Vector3.Distance(no.transform.position, expectedWorldPos) > 2f) continue;
                    // Found the FishNet-managed door. Replace local clone with it so
                    // FishNet's ObserversRpc fires on WestvilleShack.Door directly.
                    var oldDoor = Door?.gameObject;
                    ConfigureDoor(no.gameObject);
                    if (oldDoor != null && oldDoor != no.gameObject)
                        GameObject.Destroy(oldDoor);
                    yield break;
                }
            }
            OTCLog.Warning(OTCLog.Systems.Patch,"[WestvilleShack] FishNet door not found within 30s — using local clone, host→client door sync relies on Steam");
        }

        /// <summary>
        /// Polls for the OTC TrashCan_Built near the shack and activates its Lid child.
        /// FishNet's spawn packet doesn't propagate child GameObject active states, so the lid
        /// is always inactive when the client first receives the object.
        /// </summary>
        private static System.Collections.IEnumerator WaitAndFixTrashCanLid(Vector3 expectedPos)
        {
            float timeout = 30f;
            float elapsed = 0f;
            while (elapsed < timeout)
            {
                yield return new WaitForSeconds(0.5f);
                elapsed += 0.5f;
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go.name == "TrashCan_Built(Clone)" && Vector3.Distance(go.transform.position, expectedPos) < 2f)
                    {
                        foreach (var t in go.GetComponentsInChildren<Transform>(true))
                            if (t.name.StartsWith("Lid")) t.gameObject.SetActive(true);
                        yield break;
                    }
                }
            }
            OTCLog.Warning(OTCLog.Systems.Patch,"[WestvilleShack] TrashCan not found within 30s — lid not activated");
        }

        /// <summary>
        /// Rebuilds interior NavMesh after furniture is placed or moved.
        /// </summary>
        public static void RebuildNavMesh()
        {
            _navMeshRepairer?.Rebuild();
        }

        /// <summary>
        /// Sets door access based on property ownership. Called by BuildingBuilder.AddPrefab callback.
        /// </summary>
        private static void ConfigureDoor(GameObject doorGo)
        {
            var doorCtrl = doorGo.GetComponentInChildren<DoorController>(true);
            Door = doorCtrl;
            if (doorCtrl != null)
            {
                bool purchased = PropertySaveData.Instance?.IsPropertyOwned(PropertySaveData.ShackId) ?? false;
                doorCtrl.PlayerAccess = purchased ? EDoorAccess.Open : EDoorAccess.Locked;

                // Door sync is handled by DoorSyncPatch — a Harmony postfix on DoorController.SetIsOpen(bool, EDoorSide).
                // Let NPCs open the door naturally when they approach
                try
                {
#if IL2CPP
                    doorCtrl.OpenableByNPCs = true;
#else
                    var field = doorCtrl.GetType().GetField("OpenableByNPCs",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    if (field != null) field.SetValue(doorCtrl, true);
#endif
                }
                catch { }
            }
            else
                OTCLog.Warning(OTCLog.Systems.Patch,$"Door '{doorGo.name}' has no DoorController");
        }

        /// <summary>
        /// Unlocks and opens the shack door at runtime (called after purchase).
        /// </summary>
        public static void UnlockDoor()
        {
            var doorCtrl = Door;
            if (doorCtrl != null)
            {
                doorCtrl.PlayerAccess = EDoorAccess.Open;
                try
                {
#if IL2CPP
                    doorCtrl.OpenableByNPCs = true;
#else
                    var field = doorCtrl.GetType().GetField("OpenableByNPCs",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    if (field != null) field.SetValue(doorCtrl, true);
#endif
                }
                catch { }
            }
        }

        /// <summary>
        /// Restores switch states from save data after load.
        /// </summary>
        public static void ApplySavedState(bool lightsOn, bool storeOpen)
        {
            if (_lightSwitch != null)
            {
                if (lightsOn) _lightSwitch.SwitchOn();
                else _lightSwitch.SwitchOff();
            }
            else
                SetLightsEnabled(lightsOn);

            if (_openCloseSwitch != null)
            {
                if (storeOpen) _openCloseSwitch.SwitchOn();
                else _openCloseSwitch.SwitchOff();
            }
            else
                IsStoreOpen = storeOpen;
        }

        /// <summary>
        /// Sets the store open/closed state and updates the switch visual.
        /// Used by multiplayer sync to apply host state on client.
        /// </summary>
        internal static void SetStoreOpen(bool open)
        {
            _suppressSwitchSync = true;
            try
            {
                IsStoreOpen = open;
                if (_openCloseSwitch != null)
                {
                    if (open) _openCloseSwitch.SwitchOn();
                    else _openCloseSwitch.SwitchOff();
                }
                UpdateOpenCloseSwitchMessages();
            }
            finally { _suppressSwitchSync = false; }
        }

        /// <summary>
        /// Updates the open/close switch interaction messages based on current state and time of day.
        /// After closing time (8pm), shows a note that the store closes at 8:00.
        /// </summary>
        public static void UpdateOpenCloseSwitchMessages()
        {
            if (_openCloseSwitch == null) return;

            // Always show operating hours so the player knows the schedule
            const string hours = " (8AM - 8PM)";

            // messageWhenOn = shown when switch is ON (store is open) → action is to close
            // messageWhenOff = shown when switch is OFF (store is closed) → action is to open
            _openCloseSwitch.SetInteractionMessages(
                $"Close Store{hours}",
                $"Open Store{hours}");
        }

        /// <summary>
        /// Called on host when a client toggles the shack door.
        /// Applies the state to the host's networked door. DoorSyncPatch detects this
        /// via Harmony postfix and publishes the updated game state to all clients.
        /// </summary>
        internal static void ApplyRemoteDoorToggle(bool isOpen, int sideValue)
        {
            if (Door == null) return;
            Door.SetIsOpen(isOpen, (EDoorSide)sideValue);
        }

        /// <summary>
        /// Applies door open/close state received from host sync.
        /// SuppressDoorSync prevents the DoorSyncPatch postfix from re-broadcasting.
        /// </summary>
        internal static void SetDoorFromSync(bool isOpen, int sideValue)
        {
            SuppressDoorSync = true;
            try
            {
                if (Door != null)
                    Door.SetIsOpen(isOpen, (EDoorSide)sideValue);
            }
            finally { SuppressDoorSync = false; }
        }

        /// <summary>
        /// Clone an existing scene prop by name (for objects whose meshes are on child GameObjects).
        /// </summary>
        private static void CloneSceneProp(string goName, Transform parent, Vector3 localPos, Quaternion localRot)
        {
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go.name == goName && go.GetComponentInChildren<MeshRenderer>(true) != null)
                {
                    var clone = GameObject.Instantiate(go, parent);
                    clone.name = $"OTC_{goName}";

                    // Strip game scripts and Rigidbody — keep Collider for collision, remove physics so they stay put
                    foreach (var comp in clone.GetComponentsInChildren<MonoBehaviour>(true))
                        GameObject.Destroy(comp);
                    foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
                        GameObject.Destroy(rb);

                    clone.transform.localPosition = localPos;
                    clone.transform.localRotation = localRot;
                    clone.SetActive(true);
                    foreach (var renderer in clone.GetComponentsInChildren<MeshRenderer>(true))
                        renderer.enabled = true;
                    foreach (var child in clone.GetComponentsInChildren<Transform>(true))
                        child.gameObject.SetActive(true);
                    return;
                }
            }
            OTCLog.Warning(OTCLog.Systems.Patch,$"Scene prop '{goName}' not found");
        }

        private static void BuildRoom()
        {
            var palette = new BuildingPalette
            {
                FloorMaterial = Materials.Find("carpet blue"),
                WallMaterial = Materials.BrickWallRed,
                CeilingMaterial = Materials.ConcreteLightGrey,
                TrimMaterial = Materials.MetalDarkGrey,
            };

            // East wall: door on south/left, window on north/right
            // leftWindow = south side, rightWindow = north side (confirmed via testing)
            // Negative offset = shifts door south (confirmed: -0.5 put door on left)
            var foundationMat = Materials.Find("concrete light beige");
            var eastOpening = WallOpening.DoorWithWindows(
                doorWidth: 1.05f, doorHeight: 2.1f,
                leftWindow: WallOpening.Window(width: 1.9f, height: 1.2f, sillHeight: 0.8f, frameMaterial: foundationMat),
                rightWindow: WallOpening.Window(width: 1.9f, height: 1.2f, sillHeight: 0.8f, frameMaterial: foundationMat));
            eastOpening.Offset = -1.2f;

            // North wall: window offset toward east side
            var northOpening = WallOpening.Window(width: 1.9f, height: 1.2f, sillHeight: 0.8f, offset: 1.5f, frameMaterial: foundationMat);

            var builder = new BuildingBuilder("WestvilleShack")
                .DefineRoom(RoomWidth, RoomHeight, RoomDepth)
                .WithPalette(palette)
                .AddFloor()
                .AddCeiling()
                .AddWalls(
                    north: northOpening,
                    south: null,
                    east: eastOpening,
                    west: null)
                .AddDoorFrames(Materials.Find("concrete light beige"))
                .AddLights(intensity: 1.2f)
                .AddFoundation(height: FoundationHeight, expandX: 0.25f, expandZ: 0.25f, material: Materials.Find("concrete light beige"))
                .AddAmbientLighting()
                // Door and switches are deferred to SpawnNetworkedObjects() (called from OnGameLoaded)
                // so FishNet is fully initialized before Spawn() is called.
                .AddStairs(wall: WallSide.East, foundationHeight: FoundationHeight, style: StairStyle.ClosedRiser)
                .AddParapetRoof(ParapetPreset.Shallow, parapetMaterial: Materials.Find("concrete light beige"), capMaterial: Materials.Find("concrete light beige"));

            _building = builder.Build();

            // Cache lights folder — start with Light components off (fixtures visible, no glow)
            var lightsTransform = _building.transform.Find("Lights");
            if (lightsTransform != null)
            {
                _lightsFolder = lightsTransform.gameObject;
                SetLightsEnabled(false);
            }

            // NavMesh repairer — must be created after Build() (needs building root)
            // Use employee agent type so the indoor NavMesh is visible to employee-type agents
            int navAgentType = 0;
            if (ManagerSpawner.TryGetEmployeeNavMeshSettings(out int empAgentType, out int empAreaMask))
            {
                navAgentType = empAgentType;
            }
            else
            {
                OTCLog.Warning(OTCLog.Systems.Patch,"No employee NavMesh settings found — using default agentTypeID=0");
            }
            _navMeshRepairer = builder.CreateNavMeshRepairer(navAgentType);

            // Position: room sits on top of foundation
            _building.transform.position = new Vector3(
                BuildingOrigin.x, BuildingOrigin.y + FoundationHeight, BuildingOrigin.z);

            // Build NavMesh after positioning (uses world position for bake)
            _navMeshRepairer.Build();

            // Placement grid — no interior walls, just exclude exterior wall edges
            ShackGrid = BuildingGridFactory.CreateGrid(_building, RoomWidth, RoomDepth, "WestvilleShack_Floor1",
                tileFilter: (x, z) =>
                {
                    if (x == 0 || z == 0) return false;
                    return true;
                });

            // Register the grid with a fixed GUID so FishNet can look it up on clients.
            // Grid.Awake (which normally calls SetGUID) is skipped for OTC grids, so we
            // must register manually. The same GUID is used on both host and client.
            try
            {
#if IL2CPP
                ShackGrid.SetGUID(new Il2CppSystem.Guid("a47e3c2b-0f8d-4e9a-b5c1-7d6e8f9a0b2c"));
#else
                ShackGrid.SetGUID(new System.Guid("a47e3c2b-0f8d-4e9a-b5c1-7d6e8f9a0b2c"));
#endif
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,$"Failed to register ShackGrid GUID: {ex.Message}");
            }

            // Door and switches are spawned in SpawnNetworkedObjects() after onLoadComplete.
            // Checkout counter is spawned later via LoadManager.onLoadComplete (needs FishNet ready)

            // "CANNABIS" sign on east wall exterior — 3D model
            try
            {
                byte[] signData = EmbeddedResourceLoader.LoadBytes(
                    "OverTheCounter.Resources.ShackSign.glb",
                    Assembly.GetExecutingAssembly());
                if (signData != null)
                {
                    var sign = GltfLoader.LoadGlb(signData);
                    if (sign != null)
                    {
                        sign.name = "OTC_ShackSign";
                        sign.transform.SetParent(_building.transform);
                        // East wall, below the leaf sign
                        sign.transform.localPosition = new Vector3(RoomWidth + 0.1f, 2.7f, RoomDepth / 2f - 0.2f);
                        sign.transform.localRotation = Quaternion.Euler(90f, 90f, 0f);
                        sign.transform.localScale = Vector3.one * 0.633f;

                        // Same approach as leaf — just set color, don't replace material
                        var green = new Color(0.15f, 0.4f, 0.15f);
                        foreach (var r in sign.GetComponentsInChildren<MeshRenderer>(true))
                            r.material.color = green;
                    }
                    else
                        OTCLog.Warning(OTCLog.Systems.Patch,"GltfLoader returned null for ShackSign.glb");
                }
                else
                    OTCLog.Warning(OTCLog.Systems.Patch,"Could not load ShackSign.glb embedded resource");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"ShackSign load failed: {ex.Message}");
            }

            // TerrainClearer is deferred to OnGameLoaded — terrain tree instances are not
            // yet populated during OnSceneWasInitialized on the client side.

            // Exterior props — ground is at -FoundationHeight in local space (terrain flattened to BuildingOrigin.y)
            float extGround = -FoundationHeight;
            new InteriorBuilder(_building.transform, "WestvilleShack_Props")
                // Dumpster on north side
                .AddCustomMesh(Meshes.Dumpster, "Dumpster",
                    new Vector3(2f, extGround, RoomDepth + 2.5f), Quaternion.Euler(-90f, 0f, 0f))
                // Two dumpster lids side by side on top of opening
                .AddCustomMesh(Meshes.DumpsterCover, "DumpsterLid1",
                    new Vector3(1.5f, extGround + 1.2f, RoomDepth + 1.3f), Quaternion.Euler(-90f, 0f, 0f))
                .AddCustomMesh(Meshes.DumpsterCover, "DumpsterLid2",
                    new Vector3(2.5f, extGround + 1.2f, RoomDepth + 1.3f), Quaternion.Euler(-90f, 0f, 0f))
                .Build(_building);

            // Wooden crates — scene cloned, decorative only
            CloneSceneProp("Wood Crate Prop", _building.transform, new Vector3(-1.2f, extGround, 4.2f), Quaternion.identity);
            CloneSceneProp("Wood Crate Prop", _building.transform, new Vector3(-1.2f, extGround, 5.4f), Quaternion.identity);
            CloneSceneProp("Wood Crate Prop", _building.transform, new Vector3(-1.2f, extGround + 0.8f, 4.8f), Quaternion.Euler(0f, 15f, 0f));
            // Lone rotated crate on north-east side
            CloneSceneProp("Wood Crate Prop", _building.transform, new Vector3(7.4f, extGround, 5.1f), Quaternion.Euler(0f, 35f, 0f));

            // Marijuana leaf sign on north wall exterior
            try
            {
                byte[] glbData = EmbeddedResourceLoader.LoadBytes(
                    "OverTheCounter.Resources.LeafSign.glb",
                    Assembly.GetExecutingAssembly());
                if (glbData != null)
                {
                    var leaf = GltfLoader.LoadGlb(glbData);
                    if (leaf != null)
                    {
                        leaf.name = "OTC_LeafSign";
                        leaf.transform.SetParent(_building.transform);
                        // East wall, right of the text sign
                        leaf.transform.localPosition = new Vector3(RoomWidth + 0.2f, 2.7f, RoomDepth / 2f + 1.7f);
                        leaf.transform.localRotation = Quaternion.Euler(90f, 90f, 0f);
                        leaf.transform.localScale = Vector3.one * 0.5f;

                        // Apply green color to all mesh renderers
                        var green = new Color(0.15f, 0.4f, 0.15f);
                        foreach (var r in leaf.GetComponentsInChildren<MeshRenderer>(true))
                        {
                            r.material.color = green;
                        }
                    }
                    else
                        OTCLog.Warning(OTCLog.Systems.Patch,"GltfLoader returned null for LeafSign.glb");
                }
                else
                    OTCLog.Warning(OTCLog.Systems.Patch,"Could not load LeafSign.glb embedded resource");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"LeafSign load failed: {ex.Message}");
            }

        }
    }
}
