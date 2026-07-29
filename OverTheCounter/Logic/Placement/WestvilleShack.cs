using MelonLoader;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1MAPI.Building;
using S1MAPI.Building.Components;
using S1MAPI.Building.Config;
using S1MAPI.Building.Interior;
using S1MAPI.Building.Structural;
using S1MAPI.Gltf;
using S1MAPI.S1;
using S1MAPI.Utils;
using S1API.GameTime;
using S1API.Misc;
using System;
using System.Collections.Generic;
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
        private const float RoomWidth = 6f;
        private const float RoomHeight = 3.5f;
        private const float RoomDepth = 5f;
        private const float FoundationHeight = 0.4f;

        private static readonly Vector3 BuildingOrigin = new(-167.4f, -4f, 73.5f);

        private static GameObject _building;
        private static BuildingPartRegistry _registry;
        private static NavigationBuilder _navigationBuilder;

        /// <summary>S1MAPI NavigationBuilder for interior A* pathfinding.</summary>
        internal static NavigationBuilder NavBuilder => _navigationBuilder;

        /// <summary>Building root transform for world↔local coordinate conversion.</summary>
        internal static Transform BuildingTransform => _building?.transform;
        private static ModularSwitch _lightSwitch;
        private static bool _initialized;
        private static bool _suppressSwitchSync; // prevents re-entrancy during sync apply
        // Networked objects tracked for explicit cleanup (NOT parented to _building — parenting
        // changes local position which FishNet sends to clients instead of world position).
        private static readonly List<GameObject> _networkedObjects = new();
        private static readonly List<GameObject> _lightFixtures = new();
        private static readonly List<LightMatSwap> _materialSwaps = new();
        private static readonly List<(Light light, float baseIntensity)> TrackedLights = new();
        private static float _lastBrightnessCheck;
        private static Color _neonEmissionColor = Color.black;
        internal static DoorController Door;

        /// <summary>Tracks a renderer whose material should be swapped between on/off states.</summary>
        private class LightMatSwap
        {
            public MeshRenderer Renderer;
            public Material OnMat;
            public Material OffMat;
        }

        /// <summary>Maps mesh base name (without _on/_off) to [onMaterial, offMaterial].</summary>
        private static readonly Dictionary<string, string[]> _meshLightMats = new()
        {
            { "otc_mansion_light",            new[] { "warmbulb_on_mat",  "lightbulb_off_mat" } },
            { "otc_flurobar",                 new[] { "warmfluro_on_mat", "fluro_off_mat" } },
            { "otc_round_ceiling_light",      new[] { "fluro_on_mat",     "fluro_off_mat" } },
            { "otc_wall_lantern",             new[] { "warmbulb_on_mat",  "lamppost light off mat" } },
            { "otc_segmented_light_bar",      new[] { "warmfluro_on_mat", "segmented light off" } },
            { "otc_industrial_hanging_light", new[] { "warmbulb_on_mat",  "lightbulb_off_mat" } },
        };

        /// <summary>Current lighting style ID.</summary>
        public static string CurrentLightingStyleId { get; internal set; }

        /// <summary>Current exterior wall style ID.</summary>
        public static string CurrentExteriorWallStyleId { get; internal set; }

        /// <summary>Current interior wall style ID.</summary>
        public static string CurrentInteriorWallStyleId { get; internal set; }

        /// <summary>Current floor style ID.</summary>
        public static string CurrentFloorStyleId { get; internal set; }

        /// <summary>Whether the store is currently open for customers. Toggled by the open/close switch.</summary>
        public static bool IsStoreOpen { get; private set; }

        /// <summary>Whether the interior lights are currently on.</summary>
        public static bool AreLightsOn { get; private set; }

        /// <summary>Current door open/closed state — read directly from DoorController.</summary>
        internal static bool IsDoorOpen => Door != null && Door.IsOpen;

        /// <summary>Last door side that triggered open — serialized as int (Interior=0, Exterior=1).</summary>
        internal static int DoorSideValue
        {
            get
            {
                if (Door == null) return 0;
#if IL2CPP
                return (int)Door.lastOpenSide;
#else
                var fi = Door.GetType().GetField("lastOpenSide",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return fi != null ? (int)fi.GetValue(Door) : 0;
#endif
            }
        }

        /// <summary>The placement grid inside the shack. Set after build.</summary>
        internal static Grid ShackGrid { get; private set; }

        /// <summary>BuildingTarget for the customer system (set after Build).</summary>
        internal static BuildingTarget Target { get; private set; }

        // Exterior furniture placed via MeshVault (positions are local to building root)
        private static readonly FurnitureSlot[] Furniture =
        {
            new() { SlotId = "ext_dumpster", DefaultMeshId = "dumpster",
                    LocalPosition = new(0.9412f, 0.485f, -1.0444f), EulerAngles = Vector3.zero },
            new() { SlotId = "ext_bench", DefaultMeshId = "outdoor_bench",
                    LocalPosition = new(7.0903f, 0.0873f, 3.7998f), EulerAngles = new(0f, 90f, 0f) },
            new() { SlotId = "ext_rubbishbin", DefaultMeshId = "rubbishbin",
                    LocalPosition = new(5.8993f, 0.345f, -0.8644f), EulerAngles = Vector3.zero },
        };

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
            _navigationBuilder?.Remove();
            _navigationBuilder = null;
            _registry = null;
            _lightSwitch = null;
            Door = null;
            IsStoreOpen = false;
            AreLightsOn = false;
            ShackGrid = null;
            Target = null;
            foreach (var go in _lightFixtures)
                if (go != null) GameObject.Destroy(go);
            _lightFixtures.Clear();
            _materialSwaps.Clear();
            TrackedLights.Clear();
            if (_building != null) GameObject.Destroy(_building);
            _building = null;
            // Networked objects are NOT children of _building — destroy them explicitly.
            foreach (var go in _networkedObjects)
                if (go != null) GameObject.Destroy(go);
            _networkedObjects.Clear();
            FurnitureManager.CleanupFurniture("WestvilleShack");
            CurrentLightingStyleId = null;
            CurrentExteriorWallStyleId = null;
            CurrentInteriorWallStyleId = null;
            CurrentFloorStyleId = null;
            _initialized = false;
            _suppressSwitchSync = false;
            _awaitingClientDoor = false;
        }

        /// <summary>
        /// Enables or disables Light components on fixture GameObjects.
        /// Keeps the GameObjects active so light fixtures remain visible.
        /// </summary>
        internal static void SetLightsEnabled(bool enabled)
        {
            AreLightsOn = enabled;

            // Toggle fixture point lights
            foreach (var go in _lightFixtures)
            {
                if (go == null) continue;
                foreach (var light in go.GetComponentsInChildren<Light>(true))
                    light.enabled = enabled;
            }

            // Toggle neon strip emissive materials
            foreach (var go in _lightFixtures)
            {
                if (go == null || go.name != "NeonStrips") continue;
                foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mat = renderer.material;
                    if (enabled)
                    {
                        mat.EnableKeyword("_EMISSION");
                        mat.SetColor("_EmissionColor", _neonEmissionColor);
                    }
                    else
                    {
                        mat.DisableKeyword("_EMISSION");
                    }
                }
            }

            // Swap fixture mesh materials between on/off states
            foreach (var swap in _materialSwaps)
            {
                if (swap.Renderer == null) continue;
                var mat = enabled ? swap.OnMat : swap.OffMat;
                if (mat != null) swap.Renderer.material = mat;
            }

            // Apply time-based brightness immediately when turning on
            if (enabled && TrackedLights.Count > 0)
            {
                float mult = StoreHours.GetLightBrightness();
                for (int i = 0; i < TrackedLights.Count; i++)
                {
                    var (light, baseIntensity) = TrackedLights[i];
                    if (light != null)
                        light.intensity = baseIntensity * mult;
                }
                _lastBrightnessCheck = Time.time;
            }

            // Toggle task lights on counters in this building
            foreach (var counter in CheckoutCounter.AllCounters)
                if (counter.BuildingId == SaveData.PropertySaveData.ShackId)
                    counter.SetTaskLightEnabled(enabled);
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
            if (_networkedObjects.Count > 0) return;

            // Door — placed via S1MAPI PrefabPlacer for proper FishNet replication.
            // Server spawns networked; FishNet replicates to clients; S1MAPI's linker
            // parents the client copy to the building. Press-E interaction calls
            // SetIsOpen_Server (RunLocally=true, RequireOwnership=false) which syncs
            // door state to all peers automatically.
            try
            {
                var placer = new PrefabPlacer(_building.transform);
                var doorLocalPos = new Vector3(RoomWidth, 0f, RoomDepth / 2f - 1.2f);
                var doorLocalRot = Quaternion.Euler(0f, 270f, 0f);
                var doorGo = placer.Place(Prefabs.ClassicalWoodenDoor, doorLocalPos, doorLocalRot, networked: true);
                if (doorGo != null)
                {
                    // Host: Place returns the instance — configure and track for cleanup
                    _networkedObjects.Add(doorGo);
                    ConfigureDoor(doorGo);
                }
                else if (!NetworkHelper.IsHost)
                {
                    // Client: door arrives via FishNet replication, S1MAPI's linker
                    // parents it to the building. TickClientDoorSetup() polls each frame.
                    _awaitingClientDoor = true;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"Door spawn failed: {ex.Message}");
            }

            // Light switch — S1MAPI's built-in ModularSwitch prefab isn't always registered
            // the instant onLoadComplete fires, so retry a few times instead of giving up
            // after one miss.
            MelonCoroutines.Start(SpawnLightSwitchRoutine());

            // Apply lighting style (default or saved)
            ApplyLightingStyle(LightingStyle.Get(CurrentLightingStyleId));

            // Re-apply saved wall/floor styles (may have been set before building existed)
            if (!string.IsNullOrEmpty(CurrentExteriorWallStyleId))
            {
                var extStyle = WallStyle.GetExterior(CurrentExteriorWallStyleId);
                var extMat = Materials.Find(extStyle.MaterialName);
                if (extMat != null) SwapExteriorWallMaterial(extMat);
            }
            if (!string.IsNullOrEmpty(CurrentInteriorWallStyleId))
            {
                var intStyle = WallStyle.GetInterior(CurrentInteriorWallStyleId);
                var intMat = Materials.Find(intStyle.MaterialName);
                if (intMat != null) SwapInteriorWallMaterial(intMat);
            }
            if (!string.IsNullOrEmpty(CurrentFloorStyleId))
            {
                var floorStyle = FloorStyle.Get(CurrentFloorStyleId);
                var floorMat = Materials.Find(floorStyle.MaterialName);
                if (floorMat != null) SwapFloorMaterial(floorMat);
            }
        }

        private static System.Collections.IEnumerator SpawnLightSwitchRoutine()
        {
            const int maxAttempts = 5;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (TrySpawnLightSwitch())
                    yield break;

                if (attempt < maxAttempts)
                    yield return new WaitForSeconds(1f);
            }
            OTCLog.Warning(OTCLog.Systems.Patch,
                $"Shack light switch: ModularSwitch prefab still not found after {maxAttempts} attempts — giving up.");
        }

        private static bool TrySpawnLightSwitch()
        {
            try
            {
                var lightLocalPos = new Vector3(RoomWidth - 0.1f, 1.2f, 2.10f);
                var switchGo = SpawnNetworkedAt(Prefabs.ModularSwitch,
                    _building.transform.TransformPoint(lightLocalPos),
                    _building.transform.rotation * Quaternion.Euler(0f, 270f, 0f));
                if (switchGo == null)
                    return false;

                _networkedObjects.Add(switchGo);
                _lightSwitch = new ModularSwitch(switchGo);
                _lightSwitch.SetInteractionMessages("Turn Off Lights", "Turn On Lights");
                _lightSwitch.OnToggled += isOn =>
                {
                    SetLightsEnabled(isOn);
                    if (!_suppressSwitchSync)
                    {
                        if (NetworkHelper.IsHost)
                            ConfigSyncData.MarkGameStateDirty();
                        else
                            ConfigSyncData.SendQuestAction($"SHACK_LIGHTS:{(isOn ? 1 : 0)}");
                    }
                };
                return true;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Shack light switch spawn failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Client: true while waiting for the FishNet-replicated door to arrive.</summary>
        private static bool _awaitingClientDoor;

        /// <summary>
        /// Client-side frame tick: checks if the FishNet-replicated door has been parented
        /// to the building by S1MAPI's linker. Called from Core.OnLateUpdate every frame
        /// until the door is found (no timeout — deterministic).
        /// </summary>
        internal static void TickClientDoorSetup()
        {
            if (!_awaitingClientDoor || _building == null) return;

            var dc = _building.GetComponentInChildren<DoorController>(true);
            if (dc == null) return;

            _awaitingClientDoor = false;
            ConfigureDoor(dc.gameObject);
        }


        /// <summary>
        /// Rebuilds interior pathfinding after furniture is placed or moved.
        /// </summary>
        public static void RebuildNavigation()
        {
            _navigationBuilder?.Rebuild();
        }

        /// <summary>
        /// Sets door access based on property ownership.
        /// </summary>
        private static void ConfigureDoor(GameObject doorGo)
        {
            var doorCtrl = doorGo.GetComponentInChildren<DoorController>(true);
            Door = doorCtrl;
            if (doorCtrl != null)
            {
                bool purchased = PropertySaveData.Instance?.IsPropertyOwned(PropertySaveData.ShackId) ?? false;
                doorCtrl.PlayerAccess = purchased ? EDoorAccess.Open : EDoorAccess.Locked;
                doorCtrl.AutoOpenForPlayer = false;

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
        /// Restores switch states and styles from save data after load.
        /// </summary>
        public static void ApplySavedState(bool lightsOn, bool storeOpen, string lightingStyleId = null,
            string exteriorWallStyleId = null, string interiorWallStyleId = null, string floorStyleId = null)
        {
            if (!string.IsNullOrEmpty(lightingStyleId))
            {
                if (_lightFixtures.Count > 0 && lightingStyleId != CurrentLightingStyleId)
                    ApplyLightingStyle(LightingStyle.Get(lightingStyleId));
                else
                    CurrentLightingStyleId = lightingStyleId;
            }

            if (!string.IsNullOrEmpty(exteriorWallStyleId))
            {
                CurrentExteriorWallStyleId = exteriorWallStyleId;
                var style = WallStyle.GetExterior(exteriorWallStyleId);
                var mat = Materials.Find(style.MaterialName);
                if (mat != null && _building != null) SwapExteriorWallMaterial(mat);
            }

            if (!string.IsNullOrEmpty(interiorWallStyleId))
            {
                CurrentInteriorWallStyleId = interiorWallStyleId;
                var style = WallStyle.GetInterior(interiorWallStyleId);
                var mat = Materials.Find(style.MaterialName);
                if (mat != null && _building != null) SwapInteriorWallMaterial(mat);
            }

            if (!string.IsNullOrEmpty(floorStyleId))
            {
                CurrentFloorStyleId = floorStyleId;
                var style = FloorStyle.Get(floorStyleId);
                var mat = Materials.Find(style.MaterialName);
                if (mat != null && _building != null) SwapFloorMaterial(mat);
            }

            if (_lightSwitch != null)
            {
                if (lightsOn) _lightSwitch.SwitchOn();
                else _lightSwitch.SwitchOff();
            }
            else
                SetLightsEnabled(lightsOn);

            IsStoreOpen = storeOpen;
        }

        /// <summary>
        /// Sets the store open/closed state.
        /// Used by multiplayer sync to apply host state on client.
        /// </summary>
        internal static void SetStoreOpen(bool open)
        {
            IsStoreOpen = open;
        }

        /// <summary>Toggle store state from GreenTab UI and sync over network.</summary>
        public static void ToggleStoreFromUI(bool open)
        {
            SetStoreOpen(open);
            if (NetworkHelper.IsHost)
                ConfigSyncData.MarkGameStateDirty();
            else
                ConfigSyncData.SendQuestAction($"SHACK_STORE:{(open ? 1 : 0)}");
        }

        /// <summary>
        /// Applies door open/close state received from host sync (save/load restore).
        /// </summary>
        internal static void SetDoorFromSync(bool isOpen, int sideValue)
        {
            if (Door != null)
                Door.SetIsOpen(isOpen, (EDoorSide)sideValue);
        }


        private static void BuildRoom()
        {
            var intWallMat = Materials.Find("wall stripes charcoal");
            var palette = new BuildingPalette
            {
                FloorMaterial = Materials.Find("carpet blue"),
                WallMaterial = Materials.BrickWallRed,
                InteriorWallMaterial = intWallMat,
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
                .AddFoundation(height: FoundationHeight, expandX: 0.25f, expandZ: 0.25f, material: Materials.Find("concrete light beige"))
                .AddAmbientLighting()
                // Door and switches are deferred to SpawnNetworkedObjects() (called from OnGameLoaded)
                // so FishNet is fully initialized before Spawn() is called.
                .AddStairs(wall: WallSide.East, foundationHeight: FoundationHeight, style: StairStyle.ClosedRiser)
                .AddParapetRoof(ParapetPreset.Shallow, parapetMaterial: Materials.Find("concrete light beige"), capMaterial: Materials.Find("concrete light beige"));

            _building = builder.Build();
            _registry = builder.Registry;

            // NavMesh repairer — must be created after Build() (needs building root)
            // Use employee agent type so the indoor NavMesh is visible to employee-type agents
            _navigationBuilder = builder.CreateNavigationBuilder();

            // Position: room sits on top of foundation
            _building.transform.position = new Vector3(
                BuildingOrigin.x, BuildingOrigin.y + FoundationHeight, BuildingOrigin.z);

            builder.FlattenTerrain();

            // Placement grid — no grid offset so exterior walls sit at tile centers
            // on the SW edges. Without offset, NE edge tiles are 0.25m from the wall
            // (collider doesn't reach wall), so no NE filter needed.
            ShackGrid = BuildingGridFactory.CreateGrid(_building, RoomWidth, RoomDepth, "WestvilleShack_Floor1",
                tileFilter: (x, z) =>
                {
                    if (x == 0 || z == 0) return false; // west/south wall tiles
                    return true;
                },
                gridCellSize: 0);
            BuildingGridFactory.RegisterGrid(ShackGrid, PropertySaveData.ShackId,
                PropertySaveData.ShackId, RebuildNavigation);

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

            // Build customer routing target
            // Shack faces east — door at local (6, 0, 1.3), stairs descend east
            Target = new BuildingTarget
            {
                NavBuilder = _navigationBuilder,
                BuildingTransform = _building.transform,
                Grid = ShackGrid,
                BuildingPosition = _building.transform.position,
                ExteriorApproachPosition = CustomerSpawnPoints.StairApproachPosition,
                ExitWalkPosition = CustomerSpawnPoints.RampBottomPosition,
                RoomCenterWorld = CustomerSpawnPoints.RoomCenterPosition,
                RoomSize = new Vector3(RoomWidth, RoomHeight, RoomDepth),
                Name = "WestvilleShack",
                BuildingId = SaveData.PropertySaveData.ShackId
            };

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

            // Exterior furniture via MeshVault
            FurnitureManager.SpawnFurniture("WestvilleShack", _building.transform, Furniture);

            // Static decals
            try
            {
                MeshVault.MeshVaultAPI.Init();
                var barsBanPos = _building.transform.TransformPoint(new Vector3(3.69f, 2.08f, -0.11f));
                var barsBanRot = _building.transform.rotation * Quaternion.Euler(0f, 0f, 0f);
                MeshVault.MeshVaultAPI.SpawnDecal("otc_BarsBan", barsBanPos, barsBanRot);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Furniture, $"WestvilleShack decal spawn failed: {ex.Message}");
            }

            _navigationBuilder.Build();

            UI.MapBuildingOverlay.Register(BuildingOrigin, RoomWidth, RoomDepth);
            Patches.WeatherPatches.RegisterBuilding(
                new Vector3(BuildingOrigin.x, BuildingOrigin.y + FoundationHeight, BuildingOrigin.z),
                RoomWidth, RoomHeight, RoomDepth);
        }

        // ==================================================================
        //  Lighting style
        // ==================================================================

        /// <summary>
        /// Applies a lighting style to the shack interior.
        /// Destroys existing fixtures and spawns new ones.
        /// Big fixtures: 1 ceiling light. Small fixtures (FlushMount): 2 ceiling lights.
        /// </summary>
        public static void ApplyLightingStyle(LightingStyle style)
        {
            if (_building == null) return;

            // Destroy existing light fixtures
            foreach (var go in _lightFixtures)
            {
                if (go != null) UnityEngine.Object.Destroy(go);
            }
            _lightFixtures.Clear();
            _materialSwaps.Clear();
            TrackedLights.Clear();

            CurrentLightingStyleId = style.Id;

            float fixtureY = style.FixtureY;
            // Clamp fixture Y to shack ceiling height
            if (fixtureY > RoomHeight) fixtureY = RoomHeight - 0.2f;

            float midX = RoomWidth / 2f;
            float midZ = RoomDepth / 2f;

            // Small fixtures (ShowroomColumns >= 4): 2 lights spread along depth
            // Big fixtures: 1 centered light
            if (style.ShowroomColumns >= 4)
            {
                float z1 = RoomDepth * 0.3f;
                float z2 = RoomDepth * 0.7f;
                SpawnFixture(style.CeilingMeshId, "Shack_C0",
                    new Vector3(midX, fixtureY, z1),
                    style.LightOffset, style.LightColor, style.Range, style.Intensity,
                    scale: style.FixtureScale, emissiveOverride: style.CeilingEmissiveColor);
                SpawnFixture(style.CeilingMeshId, "Shack_C1",
                    new Vector3(midX, fixtureY, z2),
                    style.LightOffset, style.LightColor, style.Range, style.Intensity,
                    scale: style.FixtureScale, emissiveOverride: style.CeilingEmissiveColor);
            }
            else
            {
                SpawnFixture(style.CeilingMeshId, "Shack_C0",
                    new Vector3(midX, fixtureY, midZ),
                    style.LightOffset, style.LightColor, style.Range, style.Intensity,
                    scale: style.FixtureScale, emissiveOverride: style.CeilingEmissiveColor);
            }

            // --- Wall lights ---
            if (style.WallMeshId != null)
            {
                float wallY = 2.2f;
                var wallLightOff = new Vector3(0f, 0.1f, 0f);

                // West wall (solid)
                SpawnFixture(style.WallMeshId, "Wall_W",
                    new Vector3(0.25f, wallY, midZ), wallLightOff, style.WallLightColor, 5f, 0.8f,
                    rotation: Quaternion.Euler(0f, 90f, 0f));

                // South wall (solid)
                SpawnFixture(style.WallMeshId, "Wall_S",
                    new Vector3(midX, wallY, 0.25f), wallLightOff, style.WallLightColor, 5f, 0.8f,
                    rotation: Quaternion.Euler(0f, 0f, 0f));
            }

            // --- Neon strips ---
            if (style.HasNeonStrips)
            {
                SpawnNeonStrips(style.NeonColor);
            }

            OTCLog.Msg(OTCLog.Systems.Furniture, $"Shack: applied lighting style '{style.DisplayName}' ({_lightFixtures.Count} fixtures)");

            // Sync light visual state with current switch state
            SetLightsEnabled(AreLightsOn);
        }

        private static void SpawnNeonStrips(Color neonColor)
        {
            if (_building == null) return;

            _neonEmissionColor = neonColor * 2f;

            float ceilingY = RoomHeight - 0.1f;
            float stripH = 0.03f;
            float stripD = 0.05f;
            float inset = 0.15f;
            float stripY = ceilingY - stripH / 2f;
            var neonMat = MaterialPresets.Emissive(neonColor, 2f);
            var neonParent = new GameObject("NeonStrips");
            neonParent.transform.SetParent(_building.transform, false);
            _lightFixtures.Add(neonParent);

            float x0 = inset;
            float x1 = RoomWidth - inset;
            float z0 = inset;
            float z1 = RoomDepth - inset;
            float centerX = RoomWidth / 2f;
            float centerZ = RoomDepth / 2f;
            float roomWid = x1 - x0;
            float roomDep = z1 - z0;

            // South strip
            var south = S1MAPI.ProceduralMesh.PrimitiveBuilder.CreateBox("Neon_S",
                new Vector3(centerX, stripY, z0), new Vector3(roomWid, stripH, stripD),
                neonColor, neonParent.transform);
            south.GetComponent<MeshRenderer>().material = neonMat;
            TrackLight(S1MAPI.ProceduralMesh.PrimitiveBuilder.CreatePointLight("Neon_S_L", new Vector3(0f, -0.1f, 0f),
                neonColor, range: 6f, intensity: 0.8f, parent: south.transform), 0.8f);

            // North strip
            var north = S1MAPI.ProceduralMesh.PrimitiveBuilder.CreateBox("Neon_N",
                new Vector3(centerX, stripY, z1), new Vector3(roomWid, stripH, stripD),
                neonColor, neonParent.transform);
            north.GetComponent<MeshRenderer>().material = neonMat;
            TrackLight(S1MAPI.ProceduralMesh.PrimitiveBuilder.CreatePointLight("Neon_N_L", new Vector3(0f, -0.1f, 0f),
                neonColor, range: 6f, intensity: 0.8f, parent: north.transform), 0.8f);

            // West strip
            var west = S1MAPI.ProceduralMesh.PrimitiveBuilder.CreateBox("Neon_W",
                new Vector3(x0, stripY, centerZ), new Vector3(stripD, stripH, roomDep),
                neonColor, neonParent.transform);
            west.GetComponent<MeshRenderer>().material = neonMat;
            TrackLight(S1MAPI.ProceduralMesh.PrimitiveBuilder.CreatePointLight("Neon_W_L", new Vector3(0f, -0.1f, 0f),
                neonColor, range: 6f, intensity: 0.8f, parent: west.transform), 0.8f);

            // East strip
            var east = S1MAPI.ProceduralMesh.PrimitiveBuilder.CreateBox("Neon_E",
                new Vector3(x1, stripY, centerZ), new Vector3(stripD, stripH, roomDep),
                neonColor, neonParent.transform);
            east.GetComponent<MeshRenderer>().material = neonMat;
            TrackLight(S1MAPI.ProceduralMesh.PrimitiveBuilder.CreatePointLight("Neon_E_L", new Vector3(0f, -0.1f, 0f),
                neonColor, range: 6f, intensity: 0.8f, parent: east.transform), 0.8f);
        }

        private static void TrackLight(GameObject lightGo, float baseIntensity)
        {
            var light = lightGo?.GetComponent<Light>();
            if (light != null)
                TrackedLights.Add((light, baseIntensity));
        }

        /// <summary>
        /// Adjusts all tracked point light intensities based on time of day.
        /// Called periodically from Core tick (~5 second throttle).
        /// </summary>
        internal static void UpdateLightBrightness()
        {
            if (!AreLightsOn || TrackedLights.Count == 0) return;
            if (Time.time - _lastBrightnessCheck < 5f) return;
            _lastBrightnessCheck = Time.time;

            float mult = StoreHours.GetLightBrightness();
            for (int i = 0; i < TrackedLights.Count; i++)
            {
                var (light, baseIntensity) = TrackedLights[i];
                if (light != null)
                    light.intensity = baseIntensity * mult;
            }
        }

        private static void SpawnFixture(string meshId, string label, Vector3 localPos,
            Vector3 lightOffset, Color lightColor, float range, float intensity,
            float scale = 1f, Quaternion? rotation = null, Color? emissiveOverride = null)
        {
            if (_building == null) return;
            var rot = _building.transform.rotation * (rotation ?? Quaternion.identity);
            var worldPos = _building.transform.TransformPoint(localPos);
            var go = MeshVault.MeshVaultAPI.Spawn(meshId, worldPos, rot, parent: _building.transform);
            if (go == null) return;
            go.name = $"Light_{label}";
            if (scale != 1f) go.transform.localScale = Vector3.one * scale;
            var lightGo = S1MAPI.ProceduralMesh.PrimitiveBuilder.CreatePointLight($"{label}_PL",
                lightOffset, lightColor, range: range, intensity: intensity,
                parent: go.transform);
            var lightComp = lightGo?.GetComponent<Light>();
            if (lightComp != null)
                TrackedLights.Add((lightComp, intensity));
            _lightFixtures.Add(go);

            // Record material swaps for on/off toggling
            var baseMeshId = meshId.Replace("_on", "").Replace("_off", "");
            if (_meshLightMats.TryGetValue(baseMeshId, out var mats))
            {
                Material onMat = emissiveOverride.HasValue
                    ? MaterialPresets.Emissive(emissiveOverride.Value, 2f)
                    : Materials.Find(mats[0]);
                Material offMat = Materials.Find(mats[1]);

                foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var matName = r.material.name.Replace(" (Instance)", "");
                    if (matName == mats[0] || matName == mats[1])
                    {
                        if (emissiveOverride.HasValue && onMat != null)
                            r.material = onMat;
                        _materialSwaps.Add(new LightMatSwap { Renderer = r, OnMat = onMat, OffMat = offMat });
                    }
                }
            }
        }

        // ==================================================================
        //  Wall / Floor material swapping
        // ==================================================================

        /// <summary>
        /// Swaps exterior wall material.
        /// </summary>
        public static void SwapExteriorWallMaterial(Material material)
        {
            if (_registry == null || material == null) return;
            var registry = _registry;
            registry.SetExteriorWallMaterial(material);
        }

        /// <summary>
        /// Swaps interior wall material.
        /// </summary>
        public static void SwapInteriorWallMaterial(Material material)
        {
            if (_registry == null || material == null) return;
            var registry = _registry;
            registry.SetInteriorFaceMaterial(material);
        }

        /// <summary>
        /// Swaps floor material.
        /// </summary>
        public static void SwapFloorMaterial(Material material)
        {
            if (_registry == null || material == null) return;
            var registry = _registry;
            registry.SetMaterial(BuildingPart.Floor, material);
        }
    }
}
