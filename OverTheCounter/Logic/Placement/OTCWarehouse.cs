using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1MAPI.Building;
using S1MAPI.Building.Config;
using S1MAPI.Building.Structural;
using S1MAPI.S1;
using S1API.Misc;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppFishNet;
using Il2CppFishNet.Object;
using Il2CppFishNet.Observing;
using Il2CppFishNet.Component.Ownership;
using Il2CppScheduleOne.Audio;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.PlayerScripts;
using Grid = Il2CppScheduleOne.Tiles.Grid;
#else
using FishNet;
using FishNet.Object;
using FishNet.Observing;
using FishNet.Component.Ownership;
using ScheduleOne.Audio;
using ScheduleOne.DevUtilities;
using ScheduleOne.Map;
using ScheduleOne.PlayerScripts;
using Grid = ScheduleOne.Tiles.Grid;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Builds the OTC warehouse — a large brick building with garage door entrance
    /// and high windows. Furniture placed via MeshVault FurnitureSlots.
    /// </summary>
    public static class OTCWarehouse
    {
        // Building dimensions
        private const float Width = 12.7f;
        private const float Depth = 10.2f;
        private const float WallHeight = 4.5f;
        private const float FoundationHeight = 0.25f;

        // SW corner origin
        private static readonly Vector3 Origin = new(66.4f, FoundationHeight, -34.0f);

        // Garage door
        private const float DoorWidth = 2.0f;
        private const float DoorHeight = 2.5f;
        private const float DoorThickness = 0.05f;
        private const float DoorOpenDist = 3f;
        private const float DoorCloseDist = 5f;
        private const float DoorSpeed = 2.5f;

        private static readonly FurnitureSlot[] Furniture =
        {
            new() { SlotId = "roof_ac1", DefaultMeshId = "sm_ac_unit",
                    LocalPosition = new(10.6f, 5.07f, 8.0f), EulerAngles = Vector3.zero },
            new() { SlotId = "roof_ac2", DefaultMeshId = "sm_ac_unit",
                    LocalPosition = new(8.6f, 5.07f, 8.0f), EulerAngles = Vector3.zero },
            new() { SlotId = "pallet_rack", DefaultMeshId = "pallet_rack",
                    LocalPosition = new(10.5f, 1.52f, 9.15f), EulerAngles = new(0f, 90f, 0f) },
            new() { SlotId = "pallet", DefaultMeshId = "pallet",
                    LocalPosition = new(9.5f, 1.44f, 9.15f), EulerAngles = Vector3.zero },
            new() { SlotId = "largebox", DefaultMeshId = "largebox",
                    LocalPosition = new(9.1f, 1.73f, 9.3f), EulerAngles = new(0f, 90f, 0f) },
            new() { SlotId = "mediumbox1", DefaultMeshId = "mediumbox",
                    LocalPosition = new(9.58f, 1.66f, 9.35f), EulerAngles = Vector3.zero },
            new() { SlotId = "mediumbox2", DefaultMeshId = "mediumbox",
                    LocalPosition = new(9.9f, 1.66f, 9.35f), EulerAngles = Vector3.zero },
            new() { SlotId = "smallbox1", DefaultMeshId = "smallbox",
                    LocalPosition = new(9.1f, 1.66f, 8.7f), EulerAngles = Vector3.zero },
            new() { SlotId = "smallbox2", DefaultMeshId = "smallbox",
                    LocalPosition = new(9.5f, 1.66f, 8.7f), EulerAngles = new(0f, 50f, 0f) },
            new() { SlotId = "filing_cabinet", DefaultMeshId = "filing_cabinet",
                    LocalPosition = new(3.3f, 0.75f, 9.6f), EulerAngles = new(0f, 90f, 0f) },
            new() { SlotId = "outdoor_chair", DefaultMeshId = "outdoor_chair",
                    LocalPosition = new(11.4f, 0.51f, 7.9f), EulerAngles = Vector3.zero },
            new() { SlotId = "double_sofa", DefaultMeshId = "double_sofa",
                    LocalPosition = new(6.0f, 0.54f, 8.5f), EulerAngles = new(0f, 90f, 0f),
                    MaterialOverrides = new[] { "atm_yellowbutton_mat" } },
            new() { SlotId = "small_trash_bin", DefaultMeshId = "small_trash_bin",
                    LocalPosition = new(6.75f, 0.37f, 7.6f), EulerAngles = Vector3.zero },
            new() { SlotId = "tv_stand", DefaultMeshId = "tv_stand",
                    LocalPosition = new(4.17f, 0.46f, 8.5f), EulerAngles = Vector3.zero },
            new() { SlotId = "tv_flatscreen", DefaultMeshId = "tv_flatscreen_w_stand",
                    LocalPosition = new(4.17f, 1.23f, 8.3f), EulerAngles = new(0f, 180f, 0f) },
            new() { SlotId = "pallet2", DefaultMeshId = "pallet",
                    LocalPosition = new(8.2f, 0.76f, 9.1f), EulerAngles = new(0f, 0f, 70f) },
            new() { SlotId = "ornate_desk", DefaultMeshId = "ornate_desk",
                    LocalPosition = new(1.2f, 0.45f, 9.5f), EulerAngles = Vector3.zero },
            new() { SlotId = "floor_lamp", DefaultMeshId = "floor_lamp",
                    LocalPosition = new(4.0f, 0.93f, 9.6f), EulerAngles = Vector3.zero },
            new() { SlotId = "smallsafe", DefaultMeshId = "smallsafe",
                    LocalPosition = new(0.6f, 1.05f, 9.6f), EulerAngles = new(0f, 150f, 0f) },
            new() { SlotId = "safe", DefaultMeshId = "safe",
                    LocalPosition = new(2.53f, 0.38f, 9.5f), EulerAngles = new(0f, 270f, 0f) },
            new() { SlotId = "cashcounter", DefaultMeshId = "cashcounter",
                    LocalPosition = new(1.9f, 1.07f, 9.6f), EulerAngles = new(0f, 60f, 0f) },
            new() { SlotId = "pallet3", DefaultMeshId = "pallet",
                    LocalPosition = new(11.4f, 1.44f, 9.15f), EulerAngles = Vector3.zero },
            new() { SlotId = "largebox2", DefaultMeshId = "largebox",
                    LocalPosition = new(10.82f, 1.73f, 9.4f), EulerAngles = new(0f, 320f, 0f) },
            new() { SlotId = "largebox3", DefaultMeshId = "largebox",
                    LocalPosition = new(11.9f, 1.73f, 9.0f), EulerAngles = Vector3.zero },
            new() { SlotId = "smallbox3", DefaultMeshId = "smallbox",
                    LocalPosition = new(10.89f, 1.66f, 8.88f), EulerAngles = Vector3.zero },
            new() { SlotId = "mediumbox3", DefaultMeshId = "mediumbox",
                    LocalPosition = new(11.49f, 1.66f, 8.75f), EulerAngles = Vector3.zero },
            new() { SlotId = "pallet4", DefaultMeshId = "pallet",
                    LocalPosition = new(9.5f, 2.84f, 9.15f), EulerAngles = Vector3.zero },
            new() { SlotId = "smallbox4", DefaultMeshId = "smallbox",
                    LocalPosition = new(1.4f, 1.03f, 9.5f), EulerAngles = new(0f, 270f, 0f) },
        };

        /// <summary>Save data key for the warehouse.</summary>
        public const string WarehouseId = "otc_warehouse";

        // Grid covers the south-east portion of the warehouse (near entrance)
        private const int GridMinX = 10; // cut off west side
        private const int GridMaxZ = 9;  // south half, excluding wall edge

        private static bool _initialized;
        private static GameObject _building;
        private static NavigationBuilder _navigationBuilder;
        private static GameObject _lightsFolder;
        private static ModularSwitch _lightSwitch;
        private static bool _suppressSwitchSync;
        private static readonly List<GameObject> _networkedObjects = new();

        /// <summary>Root transform of the warehouse building, or null if not built.</summary>
        public static Transform BuildingTransform => _building?.transform;

        /// <summary>Whether the garage door is currently open (or should be).</summary>
        public static bool IsDoorOpen => _doorShouldBeOpen;

        /// <summary>Whether the interior lights are currently on.</summary>
        public static bool AreLightsOn { get; private set; }

        /// <summary>The placement grid inside the warehouse. Set after build.</summary>
        internal static Grid WarehouseGrid { get; private set; }

        private static GameObject _garageDoor;
        private static Vector3 _doorClosedLocalPos;
        private static Vector3 _doorOpenLocalPos;
        private static bool _doorShouldBeOpen;
        private static bool _doorRoutineActive;

        private static AudioSource _doorStartSound;
        private static AudioSource _doorLoopSound;
        private static AudioSource _doorStopSound;

        /// <summary>Builds the warehouse structure, decorations, and garage door.</summary>
        public static void Initialize()
        {
            if (_initialized) return;

            try
            {
                BuildWarehouse();
                _initialized = true;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"OTCWarehouse.Initialize failed: {ex.Message}");
            }
        }

        /// <summary>Clears terrain trees and objects around the warehouse footprint.</summary>
        public static void ClearTerrain()
        {
            if (_building == null) return;
            var buildingSize = new Vector3(Width, WallHeight, Depth);
            TerrainClearer.ClearAroundBuilding(_building, buildingSize,
                new ClearingOptions { Padding = 4f });
        }

        /// <summary>Rebuilds interior pathfinding after furniture is placed or moved.</summary>
        public static void RebuildNavigation()
        {
            _navigationBuilder?.Rebuild();
        }

        internal static void SetLightsEnabled(bool enabled)
        {
            AreLightsOn = enabled;
            if (_lightsFolder == null) return;
            foreach (var light in _lightsFolder.GetComponentsInChildren<Light>(true))
                light.enabled = enabled;
        }

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

        /// <summary>Applies persisted toggle states after save data is loaded.</summary>
        public static void ApplySavedState(bool lightsOn)
        {
            if (_lightSwitch != null)
            {
                if (lightsOn) _lightSwitch.SwitchOn();
                else _lightSwitch.SwitchOff();
            }
            else
                SetLightsEnabled(lightsOn);
        }

        /// <summary>Spawns FishNet-networked objects (light switch) inside the warehouse.</summary>
        public static void SpawnNetworkedObjects()
        {
            if (_building == null) return;

            try
            {
                // Light switch on interior wall near entrance
                var lightLocalPos = new Vector3(Width - 0.1f, 1.2f, Depth / 2f - 1.5f);
                var switchGo = SpawnNetworkedAt(Prefabs.ModularSwitch,
                    _building.transform.TransformPoint(lightLocalPos),
                    _building.transform.rotation * Quaternion.Euler(0f, 270f, 0f));
                if (switchGo != null)
                {
                    switchGo.name = "OTC_WH_LightSwitch";
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
                                ConfigSyncData.SendQuestAction($"WH_LIGHTS:{(isOn ? 1 : 0)}");
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Warehouse light switch spawn failed: {ex.Message}");
            }
        }

        private static GameObject SpawnNetworkedAt(S1MAPI.Core.PrefabRef prefab, Vector3 worldPos, Quaternion worldRot)
        {
            var prefabGo = prefab.Find();
            if (prefabGo == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"WH SpawnNetworkedAt: prefab not found: {prefab.Name}");
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
                    var networkObserver = instance.GetComponent<NetworkObserver>();
                    if (networkObserver != null)
                        UnityEngine.Object.DestroyImmediate(networkObserver);

                    if (instance.GetComponent<PredictedSpawn>() == null)
                        instance.AddComponent<PredictedSpawn>();

                    instance.transform.SetPositionAndRotation(worldPos, worldRot);
                    netMgr.ServerManager.Spawn(netObj);
                }
                else
                {
                    instance.transform.SetPositionAndRotation(worldPos, worldRot);
                }
            }
            else
            {
                instance.transform.SetPositionAndRotation(worldPos, worldRot);
                MelonCoroutines.Start(ReactivateAfterFishNetStart(instance, prefab.Name));
            }

            instance.SetActive(true);
            return instance;
        }

        private static IEnumerator ReactivateAfterFishNetStart(GameObject go, string name)
        {
            yield return null;
            yield return null;
            if (go != null && !go.activeSelf)
                go.SetActive(true);
        }

        /// <summary>Destroys the warehouse and resets all static state.</summary>
        public static void Cleanup()
        {
            _doorRoutineActive = false;
            _doorShouldBeOpen = false;
            _doorStartSound = null;
            _doorLoopSound = null;
            _doorStopSound = null;
            _lightsFolder = null;
            _lightSwitch = null;
            AreLightsOn = false;
            _suppressSwitchSync = false;
            if (_building != null)
            {
                UnityEngine.Object.Destroy(_building);
                _building = null;
            }
            _garageDoor = null;
            WarehouseGrid = null;
            foreach (var go in _networkedObjects)
                if (go != null) UnityEngine.Object.Destroy(go);
            _networkedObjects.Clear();
            FurnitureManager.CleanupFurniture("OTCWarehouse");
            _initialized = false;
        }

        private static void BuildWarehouse()
        {
            var brickMat = Materials.BrickWallRed;
            var concreteMat = Materials.ConcreteLightGrey;

            var palette = new BuildingPalette
            {
                WallMaterial = brickMat,
                FloorMaterial = concreteMat,
                CeilingMaterial = concreteMat,
                TrimMaterial = brickMat,
                LightColor = new Color(1f, 0.95f, 0.85f),
                LightIntensity = 1.2f,
            };

            // High windows: sill at 3.0m, window near top of wall
            var highWindow = WallOpening.Window(
                width: 8f, height: 1.2f, sillHeight: 3.0f,
                count: 4);

            // West wall: 3 high windows
            var westWindow = WallOpening.Window(
                width: 8f, height: 1.2f, sillHeight: 3.0f,
                count: 3);

            // East door centered, 1 high window on each side
            var entranceDoor = WallOpening.DoorWithWindows(
                doorWidth: 2.0f, doorHeight: 2.5f,
                leftWindow: WallOpening.Window(width: 5f, height: 1.2f, sillHeight: 3.0f, count: 1),
                rightWindow: WallOpening.Window(width: 5f, height: 1.2f, sillHeight: 3.0f, count: 1));

            // Black trim
            var blackMat = Materials.Find("black") ?? Materials.MetalDarkGrey;

            var builder = new BuildingBuilder("OTC_Warehouse")
                .DefineRoom(Width, WallHeight, Depth)
                .WithPalette(palette)
                .AddFloor()
                .AddCeiling()
                .AddWalls(
                    north: highWindow,
                    south: highWindow,
                    east: entranceDoor,
                    west: westWindow)
                .AddBaseMolding(height: 0.3f, material: blackMat)
                .AddDoorFrames(material: blackMat)
                .AddCornerTrim(material: blackMat)
                .AddFoundation(height: FoundationHeight, expandX: 0.2f, expandZ: 0.2f, material: brickMat)
                .AddParapetRoof(parapetMaterial: blackMat, capMaterial: blackMat)
                .AddLights();

            _building = builder.Build();

            // Cache lights folder — start off
            var lightsTransform = _building.transform.Find("Lights");
            if (lightsTransform != null)
            {
                _lightsFolder = lightsTransform.gameObject;
                SetLightsEnabled(false);
            }

            _navigationBuilder = builder.CreateNavigationBuilder();

            // Position must be set before navigation build (uses world coords)
            _building.transform.position = Origin;
            builder.FlattenTerrain();

            CreateGarageDoor();
            FurnitureManager.SpawnFurniture("OTCWarehouse", _building.transform, Furniture);

            _navigationBuilder.Build();

            UI.MapBuildingOverlay.Register(Origin, Width, Depth);

            // Placement grid — south portion of warehouse (near entrance)
            WarehouseGrid = BuildingGridFactory.CreateGrid(_building, Width, Depth, "OTCWarehouse_Floor1",
                tileFilter: (x, z) =>
                {
                    if (x < GridMinX || z == 0) return false; // west cutoff + south wall edge
                    if (z >= GridMaxZ) return false;           // north area (furniture/storage)
                    return true;
                },
                gridCellSize: builder.GridCellSize);
            BuildingGridFactory.RegisterGrid(WarehouseGrid, WarehouseId,
                WarehouseId, RebuildNavigation);

            // Fixed GUID so FishNet can look up the grid on clients
            try
            {
#if IL2CPP
                WarehouseGrid.SetGUID(new Il2CppSystem.Guid("c69f5e4d-2b0a-6a1c-d7e3-9f0a1b2c4e5f"));
#else
                WarehouseGrid.SetGUID(new System.Guid("c69f5e4d-2b0a-6a1c-d7e3-9f0a1b2c4e5f"));
#endif
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"Failed to register WarehouseGrid GUID: {ex.Message}");
            }

            OTCLog.Msg(OTCLog.Systems.Patch, $"OTC Warehouse built at {Origin}");
        }

        private static void CreateGarageDoor()
        {
            _garageDoor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _garageDoor.name = "OTC_GarageDoor";
            _garageDoor.transform.SetParent(_building.transform);
            _garageDoor.transform.localScale = new Vector3(DoorThickness, DoorHeight, DoorWidth);

            _doorClosedLocalPos = new Vector3(Width, DoorHeight / 2f, Depth / 2f);
            _doorOpenLocalPos = _doorClosedLocalPos + Vector3.up * (DoorHeight - 0.3f);
            _garageDoor.transform.localPosition = _doorClosedLocalPos;

            var renderer = _garageDoor.GetComponent<MeshRenderer>();
            if (renderer != null)
                renderer.material = Materials.IndustrialBuildingRollerdoor;

            SetupDoorAudio();

            _doorRoutineActive = true;
            MelonCoroutines.Start(DoorProximityRoutine());
        }

        private static void SetupDoorAudio()
        {
            try
            {
                // Strategy 1: Find Gate component directly
                Gate gate = null;
                var allGates = UnityEngine.Object.FindObjectsOfType<Gate>();
                if (allGates != null && allGates.Length > 0)
                {
                    // Pick the closest gate to the warehouse
                    float bestDist = float.MaxValue;
                    for (int i = 0; i < allGates.Length; i++)
                    {
                        if (allGates[i] == null) continue;
                        float d = Vector3.Distance(allGates[i].transform.position, Origin);
                        OTCLog.Msg(OTCLog.Systems.Patch,
                            $"[DoorAudio] Found Gate '{allGates[i].gameObject.name}' at {allGates[i].transform.position} (dist={d:F1})");
                        if (d < bestDist) { bestDist = d; gate = allGates[i]; }
                    }
                }

                // Strategy 2: Scan nearby area for Gate components on colliders
                if (gate == null)
                {
                    OTCLog.Msg(OTCLog.Systems.Patch, "[DoorAudio] No Gate via FindObjectsOfType, scanning nearby...");
                    var colliders = Physics.OverlapSphere(Origin, 50f);
                    for (int i = 0; i < colliders.Length; i++)
                    {
                        var g = colliders[i].GetComponentInParent<Gate>();
                        if (g != null) { gate = g; break; }
                    }
                }

                if (gate == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, "[DoorAudio] No Gate found anywhere near warehouse");
                    return;
                }

                OTCLog.Msg(OTCLog.Systems.Patch,
                    $"[DoorAudio] Using gate '{gate.gameObject.name}' — logging all audio clips:");

                // Log ALL clips on the gate so we know exact names
                LogGateClips("StartSounds", gate.StartSounds);
                LogGateClips("LoopSounds", gate.LoopSounds);
                LogGateClips("StopSounds", gate.StopSounds);

                // Grab clips
                AudioClip startClip = GetClipFromControllers(gate.StartSounds);
                AudioClip loopClip = GetClipFromControllers(gate.LoopSounds);
                AudioClip stopClip = GetClipFromControllers(gate.StopSounds);

                CreateDoorAudioSource(startClip, "OTC_DoorStartSound", ref _doorStartSound);
                CreateDoorAudioSource(stopClip, "OTC_DoorStopSound", ref _doorStopSound);

                // Loop sound needs to be set to loop mode
                CreateDoorAudioSource(loopClip, "OTC_DoorLoopSound", ref _doorLoopSound);
                if (_doorLoopSound != null)
                    _doorLoopSound.loop = true;

                OTCLog.Msg(OTCLog.Systems.Patch,
                    $"[DoorAudio] Result: start='{startClip?.name ?? "NONE"}', loop='{loopClip?.name ?? "NONE"}', stop='{stopClip?.name ?? "NONE"}'");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"SetupDoorAudio failed: {ex.Message}");
            }
        }

        private static AudioClip GetClipFromControllers(AudioSourceController[] controllers)
        {
            if (controllers == null) return null;
            for (int i = 0; i < controllers.Length; i++)
            {
                if (controllers[i]?.Clip != null)
                    return controllers[i].Clip;
            }
            return null;
        }

        private static void LogGateClips(string label, AudioSourceController[] controllers)
        {
            if (controllers == null || controllers.Length == 0)
            {
                OTCLog.Msg(OTCLog.Systems.Patch, $"[DoorAudio]   {label}: (empty)");
                return;
            }
            for (int i = 0; i < controllers.Length; i++)
            {
                var clip = controllers[i]?.Clip;
                OTCLog.Msg(OTCLog.Systems.Patch,
                    $"[DoorAudio]   {label}[{i}]: '{clip?.name ?? "null"}'");
            }
        }

        private static void CreateDoorAudioSource(AudioClip clip, string name, ref AudioSource target)
        {
            if (clip == null || _garageDoor == null) return;
            var go = new GameObject(name);
            go.transform.SetParent(_garageDoor.transform);
            go.transform.localPosition = Vector3.zero;
            target = go.AddComponent<AudioSource>();
            target.clip = clip;
            target.spatialBlend = 1f;
            target.maxDistance = 15f;
            target.rolloffMode = AudioRolloffMode.Linear;
            target.playOnAwake = false;
            target.volume = 0.3f;
        }

        /// <summary>
        /// Called after purchase. Proximity routine checks ownership each frame,
        /// so this just exists for PurchaseProperty dispatch consistency.
        /// </summary>
        public static void UnlockDoor() { }

        /// <summary>Returns a world position just outside the garage door (on valid outdoor NavMesh).</summary>
        public static Vector3 GetDoorExteriorPosition()
        {
            // Door is on east wall (x=Width), centered on z. 2m east of the door = outside the carving zone.
            return Origin + new Vector3(Width + 2f, -FoundationHeight, Depth / 2f);
        }

        /// <summary>Disables or enables the garage door collider (lets NPCs walk through).</summary>
        public static void SetDoorColliderEnabled(bool enabled)
        {
            if (_garageDoor == null) return;
            var col = _garageDoor.GetComponent<Collider>();
            if (col != null) col.enabled = enabled;
        }

        private static IEnumerator DoorProximityRoutine()
        {
            bool wasOpen = false;
            bool wasMoving = false;
            while (_doorRoutineActive && _garageDoor != null)
            {
                try
                {
                    bool owned = PropertySaveData.Instance?.IsPropertyOwned(WarehouseId) ?? false;
                    var player = PlayerSingleton<PlayerMovement>.Instance;
                    if (player != null)
                    {
                        var doorWorldPos = _garageDoor.transform.parent.TransformPoint(_doorClosedLocalPos);
                        float dist = Vector3.Distance(player.transform.position, doorWorldPos);

                        if (!owned)
                            _doorShouldBeOpen = false;
                        else if (_doorShouldBeOpen && dist > DoorCloseDist)
                            _doorShouldBeOpen = false;
                        else if (!_doorShouldBeOpen && dist < DoorOpenDist)
                            _doorShouldBeOpen = true;

                        // Play start + loop sounds when door begins moving
                        if (_doorShouldBeOpen != wasOpen)
                        {
                            wasOpen = _doorShouldBeOpen;
                            if (_doorStartSound != null)
                                _doorStartSound.Play();
                            if (_doorLoopSound != null)
                                _doorLoopSound.Play();
                        }

                        var target = _doorShouldBeOpen ? _doorOpenLocalPos : _doorClosedLocalPos;
                        var prevPos = _garageDoor.transform.localPosition;
                        _garageDoor.transform.localPosition = Vector3.MoveTowards(
                            prevPos, target, DoorSpeed * Time.deltaTime);

                        // Stop loop + play stop sound when door reaches target
                        bool isMoving = _garageDoor.transform.localPosition != target;
                        if (wasMoving && !isMoving)
                        {
                            if (_doorLoopSound != null)
                                _doorLoopSound.Stop();
                            if (_doorStopSound != null)
                                _doorStopSound.Play();
                        }
                        wasMoving = isMoving;
                    }
                }
                catch { }

                yield return null;
            }
        }
    }
}
