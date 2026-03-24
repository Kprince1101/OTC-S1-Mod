using MelonLoader;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1MAPI.Building;
using S1MAPI.Building.Components;
using S1MAPI.Building.Config;
using S1MAPI.Building.Interior;
using S1MAPI.Building.Structural;
using S1MAPI.S1;
using S1MAPI.Utils;
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
    /// Large OTC dispensary — north-facing building with lobby, showroom, and backroom.
    /// </summary>
    public static class Dispensary
    {
        // Room dimensions (doubled backroom + showroom, 10x14)
        private const float RoomWidth = 14f;
        private const float RoomHeight = 4.0f;
        private const float RoomDepth = 16.5f;
        private const float FoundationHeight = 0.15f;

        // Interior wall positions (Z in local space, south=0 north=14)
        private const float BackroomWallZ = 5.0f;    // backroom/showroom boundary
        private const float LobbyWallZ = 12.2f;      // showroom/lobby boundary

        // SW corner of building footprint
        private static readonly Vector3 BuildingOrigin = new(114.16f, 0f, -11.85f);

        // Apron extends 0.5m past building on north side
        private const float ApronDepth = 0.5f;

        private static GameObject _building;
        private static NavigationBuilder _navigationBuilder;
        private static GameObject _lightsFolder;
        private static ModularSwitch _lightSwitch;
        private static ModularSwitch _openCloseSwitch;
        private static bool _initialized;
        private static bool _suppressSwitchSync;
        private static readonly List<GameObject> _networkedObjects = new();
        private static readonly List<GameObject> _sidewalks = new();
        internal static DoorController Door;

        /// <summary>Whether the store is currently open for customers.</summary>
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

        /// <summary>Save data key for the dispensary.</summary>
        public const string DispensaryId = "big_dispensary";

        /// <summary>The placement grid inside the dispensary (showroom + backroom).</summary>
        internal static Grid DispensaryGrid { get; private set; }

        // Furniture placed via MeshVault (positions are local to building root)
        private static readonly FurnitureSlot[] Furniture =
        {
            new() { SlotId = "lobby_sofa", DefaultMeshId = "double_sofa",
                    LocalPosition = new(9.5244f, 0.545f, 15.8527f), EulerAngles = Vector3.zero,
                    MaterialOverrides = new[] { "plastic black mat", null } },
            new() { SlotId = "lobby_sofa_2", DefaultMeshId = "double_sofa",
                    LocalPosition = new(12.1901f, 0.545f, 15.8505f), EulerAngles = Vector3.zero,
                    MaterialOverrides = new[] { "plastic black mat", null } },
            new() { SlotId = "lobby_sofa_3", DefaultMeshId = "double_sofa",
                    LocalPosition = new(12.1901f, 0.545f, 12.8505f), EulerAngles = new(0f, -180f, 0f),
                    MaterialOverrides = new[] { "plastic black mat", null } },
            new() { SlotId = "lobby_bar", DefaultMeshId = "desk_counter_l",
                    LocalPosition = new(4.1374f, 0.625f, 13.1012f), EulerAngles = Vector3.zero },
            new() { SlotId = "lobby_tv_stand", DefaultMeshId = "tv_stand",
                    LocalPosition = new(9.6141f, 0.455f, 12.8497f), EulerAngles = new(0f, -90f, 0f) },
            new() { SlotId = "lobby_computer", DefaultMeshId = "computer_old",
                    LocalPosition = new(4.939f, 1.305f, 13.3942f), EulerAngles = new(0f, -20f, 0f) },
            new() { SlotId = "lobby_keyboard", DefaultMeshId = "keyboard",
                    LocalPosition = new(4.9274f, 1.065f, 13.221f), EulerAngles = new(0f, -20f, 0f) },
            new() { SlotId = "lobby_mousepad", DefaultMeshId = "mouse_pad",
                    LocalPosition = new(5.3241f, 1.055f, 13.2325f), EulerAngles = new(0f, -20f, 0f) },
            new() { SlotId = "lobby_mouse", DefaultMeshId = "mouse",
                    LocalPosition = new(5.3341f, 1.065f, 13.2403f), EulerAngles = new(0f, -20f, 0f) },
            new() { SlotId = "lobby_books", DefaultMeshId = "books_vertical",
                    LocalPosition = new(9.5952f, 0.865f, 12.8607f), EulerAngles = new(0f, 90f, 0f) },
            new() { SlotId = "lobby_tv", DefaultMeshId = "tv_flatscreen",
                    LocalPosition = new(9.6426f, 1.865f, 12.3326f), EulerAngles = new(0f, 90f, 0f) },
            new() { SlotId = "lobby_wall_ac", DefaultMeshId = "wall_ac",
                    LocalPosition = new(-0.2447f, 3.0162f, 15.0671f), EulerAngles = new(0f, 90f, 0f) },
            new() { SlotId = "exterior_trash_bin", DefaultMeshId = "small_trash_bin",
                    LocalPosition = new(-1.5f, -FoundationHeight + 0.37f, RoomDepth + 1.0f), EulerAngles = Vector3.zero },
        };

        /// <summary>
        /// Builds and positions the dispensary with placement grid and interior layout.
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
                OTCLog.Error(OTCLog.Systems.Patch, $"Dispensary SpawnBuilding failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>Destroys the building and resets state for scene reload.</summary>
        public static void Cleanup()
        {
            _navigationBuilder?.Remove();
            _navigationBuilder = null;
            _lightsFolder = null;
            _lightSwitch = null;
            _openCloseSwitch = null;
            Door = null;
            IsStoreOpen = false;
            AreLightsOn = false;
            DispensaryGrid = null;
            FurnitureManager.CleanupFurniture("Dispensary");
            if (_building != null) GameObject.Destroy(_building);
            _building = null;
            foreach (var go in _networkedObjects)
                if (go != null) GameObject.Destroy(go);
            _networkedObjects.Clear();
            foreach (var go in _sidewalks)
                if (go != null) GameObject.Destroy(go);
            _sidewalks.Clear();
            _initialized = false;
            _suppressSwitchSync = false;
            _awaitingClientDoors = false;
            _clientDoorsConfigured = 0;
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

        /// <summary>
        /// Clears terrain trees and scene objects around the dispensary.
        /// Deferred to onLoadComplete so terrain data is fully populated.
        /// </summary>
        public static void ClearTerrain()
        {
            if (_building == null) return;
            // Clear trees/objects around building footprint + apron
            var buildingSize = new Vector3(RoomWidth, RoomHeight, RoomDepth + ApronDepth);
            TerrainClearer.ClearAroundBuilding(_building, buildingSize,
                new ClearingOptions { Padding = 2f });
        }

        private static GameObject SpawnNetworkedAt(S1MAPI.Core.PrefabRef prefab, Vector3 worldPos, Quaternion worldRot, Action<GameObject> preSpawnConfigure = null)
        {
            var prefabGo = prefab.Find();
            if (prefabGo == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"Dispensary SpawnNetworkedAt: prefab not found: {prefab.Name}");
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
                    preSpawnConfigure?.Invoke(instance);
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

        private static System.Collections.IEnumerator ReactivateAfterFishNetStart(GameObject go, string name)
        {
            yield return null;
            yield return null;
            if (go != null && !go.activeSelf)
                go.SetActive(true);
        }

        /// <summary>
        /// Spawns networked objects (doors, switches) after FishNet is initialized.
        /// </summary>
        public static void SpawnNetworkedObjects()
        {
            if (_building == null) return;

            // Interior doors — placed via S1MAPI PrefabPlacer for proper FishNet replication.
            // Press-E interaction calls SetIsOpen_Server (RunLocally=true, RequireOwnership=false)
            // which syncs door state to all peers automatically.
            try
            {
                var placer = new PrefabPlacer(_building.transform);

                // Metal glass door — lobby/showroom boundary
                var lobbyDoorGo = placer.Place(Prefabs.MetalGlassDoor,
                    new Vector3(RoomWidth / 2f, 0f, LobbyWallZ), Quaternion.identity, networked: true,
                    enableComponents: true,
                    onReady: (door) =>
                    {
                        var dc = door.GetComponentInChildren<DoorController>(true);
                        if (dc != null)
                        {
                            dc.PlayerAccess = EDoorAccess.Open;
                            dc.AutoOpenForPlayer = false;
                        }
                    });
                if (lobbyDoorGo != null)
                    _networkedObjects.Add(lobbyDoorGo);

                // Classical wooden door — backroom/showroom boundary
                var backDoorGo = placer.Place(Prefabs.ClassicalWoodenDoor,
                    new Vector3(RoomWidth / 2f, 0f, BackroomWallZ), Quaternion.identity, networked: true,
                    enableComponents: true,
                    onReady: (door) =>
                    {
                        var dc = door.GetComponentInChildren<DoorController>(true);
                        if (dc != null)
                        {
                            dc.PlayerAccess = EDoorAccess.Open;
                            dc.AutoOpenForPlayer = false;
                        }
                    });
                if (backDoorGo != null)
                    _networkedObjects.Add(backDoorGo);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Dispensary door spawn failed: {ex.Message}");
            }

            // Light switch — lobby side of lobby/showroom wall
            try
            {
                var lightLocalPos = new Vector3(RoomWidth - 0.1f, 1.2f, LobbyWallZ + 0.3f);
                var switchGo = SpawnNetworkedAt(Prefabs.ModularSwitch,
                    _building.transform.TransformPoint(lightLocalPos),
                    _building.transform.rotation * Quaternion.Euler(0f, 270f, 0f));
                if (switchGo != null)
                {
                    switchGo.name = "OTC_LightSwitch";
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
                                ConfigSyncData.SendQuestAction($"DISP_LIGHTS:{(isOn ? 1 : 0)}");
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Dispensary light switch spawn failed: {ex.Message}");
            }

            // Open/Close switch — next to light switch
            try
            {
                var storeLocalPos = new Vector3(RoomWidth - 0.1f, 1.2f, LobbyWallZ + 0.55f);
                var openSwitchGo = SpawnNetworkedAt(Prefabs.ModularSwitch,
                    _building.transform.TransformPoint(storeLocalPos),
                    _building.transform.rotation * Quaternion.Euler(0f, 270f, 0f));
                if (openSwitchGo != null)
                {
                    openSwitchGo.name = "OTC_OpenCloseSwitch";
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
                                ConfigSyncData.SendQuestAction($"DISP_STORE:{(isOn ? 1 : 0)}");
                        }
                    };
                    UpdateOpenCloseSwitchMessages();
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Dispensary open/close switch spawn failed: {ex.Message}");
            }

            // Trash can is placed as MeshVault furniture in the Furniture array (decorative only)
        }

        /// <summary>Client: true while waiting for FishNet-replicated interior doors.</summary>
        private static bool _awaitingClientDoors;
        private static int _clientDoorsConfigured;

        /// <summary>
        /// Client-side frame tick: checks if FishNet-replicated interior doors have been
        /// parented to the building by S1MAPI's linker. Called from Core.OnLateUpdate
        /// every frame until both doors are found (no timeout — deterministic).
        /// </summary>
        internal static void TickClientDoorSetup()
        {
            if (!_awaitingClientDoors || _building == null) return;

            var doors = _building.GetComponentsInChildren<DoorController>(true);
            if (doors == null) return;

            foreach (var dc in doors)
            {
                if (dc == null) continue;
                // Track by AutoOpenForPlayer (prefab default is true).
                // PlayerAccess may already be Open from FishNet sync, but we
                // still need to disable auto-open to prevent auto-close.
                if (dc.AutoOpenForPlayer)
                {
                    dc.PlayerAccess = EDoorAccess.Open;
                    dc.AutoOpenForPlayer = false;
                    _clientDoorsConfigured++;
                }
            }

            if (_clientDoorsConfigured >= 2)
                _awaitingClientDoors = false;
        }

        /// <summary>
        /// Rebuilds interior pathfinding after furniture is placed or moved.
        /// </summary>
        public static void RebuildNavigation()
        {
            _navigationBuilder?.Rebuild();
        }

        /// <summary>Toggles the pathfinding debug grid visualization.</summary>
        public static void VisualizePathGrid(bool show = true) => _navigationBuilder?.VisualizePathGrid(show);

        private static void ConfigureDoor(GameObject doorGo)
        {
            var doorCtrl = doorGo.GetComponentInChildren<DoorController>(true);
            Door = doorCtrl;
            if (doorCtrl != null)
            {
                // Always unlocked for now (no purchase system yet)
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
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, $"Failed to set OpenableByNPCs: {ex.Message}");
                }
            }
        }

        /// <summary>Unlocks the dispensary door at runtime.</summary>
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
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, $"Failed to set OpenableByNPCs: {ex.Message}");
                }
            }
        }

        /// <summary>Restores switch states from save data after load.</summary>
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

        /// <summary>Updates the open/close switch interaction messages based on current state.</summary>
        public static void UpdateOpenCloseSwitchMessages()
        {
            if (_openCloseSwitch == null) return;
            const string hours = " (8AM - 8PM)";
            _openCloseSwitch.SetInteractionMessages(
                $"Close Store{hours}",
                $"Open Store{hours}");
        }

        /// <summary>
        /// Applies door open/close state received from host sync (save/load restore).
        /// </summary>
        internal static void SetDoorFromSync(bool isOpen, int sideValue)
        {
            if (Door != null)
                Door.SetIsOpen(isOpen, (EDoorSide)sideValue);
        }

        private static void CreateConcreteApron()
        {
            // Cube has actual depth so edges are visible when raised above ground
            var apron = GameObject.CreatePrimitive(PrimitiveType.Cube);
            apron.name = "OTC_ConcreteApron";
            apron.transform.SetParent(_building.transform, false);
            // Position/scale from MeshPlacer log
            apron.transform.localPosition = new Vector3(4.0f, -0.27f, 16.85f);
            apron.transform.localScale = new Vector3(2.4f, 0.4f, 2.0f);

            var renderer = apron.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                var mat = Materials.Find("concrete_docks_ramps");
                if (mat != null)
                    renderer.material = mat;
                else
                    renderer.material = Materials.ConcreteLightGrey;
            }

            var col = apron.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);
        }

        private static void BuildRoom()
        {
            var wallMat = Materials.Find("brick red") ?? MaterialPresets.Opaque(new Color(0.5f, 0.15f, 0.1f));
            var trimBlack = MaterialPresets.Opaque(Color.black);

            var palette = new BuildingPalette
            {
                FloorMaterial = Materials.WoodPlanksMediumBrown,
                WallMaterial = wallMat,
                CeilingMaterial = Materials.ConcreteLightGrey,
                TrimMaterial = trimBlack,
            };

            var foundationMat = Materials.Find("concrete_docks_ramps") ?? Materials.ConcreteLightGrey;

            // North wall: [sliding door] [window] [window] — door left, 2 windows right
            var blackFrame = MaterialPresets.Opaque(Color.black);
            var northOpening = WallOpening.DoorWithWindows(
                doorWidth: 1.9f, doorHeight: 2.2f,
                leftWindow: WallOpening.Window(width: 1.2f, height: 2.5f, sillHeight: 0.4f, count: 1, frameMaterial: blackFrame),
                rightWindow: WallOpening.Window(width: 3f, height: 2.5f, sillHeight: 0.4f, count: 3, frameMaterial: blackFrame));
            northOpening.Offset = -3f; // push door toward west side

            var builder = new BuildingBuilder("Dispensary")
                .DefineRoom(RoomWidth, RoomHeight, RoomDepth)
                .WithPalette(palette)
                .AddFloor()
                .AddCeiling()
                .AddWalls(
                    north: northOpening,
                    south: null,
                    east: null,
                    west: null)
                .AddDoorFrames(trimBlack)
                .AddLights(intensity: 1.2f)
                .AddFoundation(height: FoundationHeight, expandX: 0.1f, expandZ: 0.1f, material: foundationMat)
                .AddBaseMolding(material: trimBlack)
                .AddCornerTrim(material: trimBlack)
                .AddAmbientLighting()
                // Interior wall: showroom/lobby boundary (metal glass door)
                .AddInteriorWall(InteriorWallAxis.X, LobbyWallZ, opening: WallOpening.Door(width: 1.05f, height: 2.1f))
                // Interior wall: backroom/showroom boundary (classical wooden door)
                .AddInteriorWall(InteriorWallAxis.X, BackroomWallZ, opening: WallOpening.Door(width: 1.05f, height: 2.1f))
                .AddInteriorDoorFrames(material: trimBlack)
                .AddSlidingDoors(
                    new Vector3(RoomWidth / 2f - 3f, -0.058f, RoomDepth - 0.065f),
                    Quaternion.identity,
                    "8AM-8PM",
                    onCreated: door =>
                    {
                        // Workaround: S1MAPI sign text may not apply — set it manually
                        var tmps = door.GetComponentsInChildren<TMPro.TextMeshPro>(true);
                        foreach (var tmp in tmps)
                            tmp.text = "8AM-8PM";
                    })
                .AddParapetRoof(ParapetPreset.Shallow, parapetMaterial: trimBlack,
                    capMaterial: trimBlack);

            _building = builder.Build();

            // Cache lights folder — start off
            var lightsTransform = _building.transform.Find("Lights");
            if (lightsTransform != null)
            {
                _lightsFolder = lightsTransform.gameObject;
                SetLightsEnabled(false);
            }

            _navigationBuilder = builder.CreateNavigationBuilder();

            // Position: room sits on top of foundation
            _building.transform.position = new Vector3(
                BuildingOrigin.x, BuildingOrigin.y + FoundationHeight, BuildingOrigin.z);

            builder.FlattenTerrain();

            // Spawn furniture from mesh database
            FurnitureManager.SpawnFurniture("Dispensary", _building.transform, Furniture);

            // Static decals
            try
            {
                MeshVault.MeshVaultAPI.Init();
                // Estonia flag — lobby wall (local: -0.16, 1.45, 13.15)
                var estoniaPos = _building.transform.TransformPoint(new Vector3(-0.16f, 1.45f, 13.15f));
                var estoniaRot = _building.transform.rotation * Quaternion.Euler(0f, 90f, 0f);
                MeshVault.MeshVaultAPI.SpawnDecal("otc_Estonia", estoniaPos, estoniaRot,
                    scale: new Vector3(2f, 2f, 1f));
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Furniture, $"Dispensary decal spawn failed: {ex.Message}");
            }

            _navigationBuilder.Build();

            // Placement grid — showroom + backroom only (exclude lobby Z >= LobbyWallZ)
            // Also exclude exterior wall edge tiles (x==0, z==0).
            int lobbyTileZ = (int)(LobbyWallZ / 0.5f);
            DispensaryGrid = BuildingGridFactory.CreateGrid(_building, RoomWidth, RoomDepth, "Dispensary_Floor1",
                tileFilter: (x, z) =>
                {
                    if (x == 0 || z == 0) return false;
                    if (z >= lobbyTileZ) return false; // no placement in lobby
                    return true;
                },
                gridCellSize: builder.GridCellSize);
            BuildingGridFactory.RegisterGrid(DispensaryGrid, DispensaryId,
                null, RebuildNavigation);

            // Concrete apron along north wall
            CreateConcreteApron();

            // Register grid with a fixed GUID
            try
            {
#if IL2CPP
                DispensaryGrid.SetGUID(new Il2CppSystem.Guid("b58f4d3c-1a9e-5f0b-c6d2-8e7f0a1b3d4e"));
#else
                DispensaryGrid.SetGUID(new System.Guid("b58f4d3c-1a9e-5f0b-c6d2-8e7f0a1b3d4e"));
#endif
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"Failed to register DispensaryGrid GUID: {ex.Message}");
            }
        }
    }
}
