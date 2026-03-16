using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.Utilities;
using S1MAPI.Building;
using S1MAPI.Building.Config;
using S1MAPI.Building.Structural;
using S1MAPI.S1;
using System;
using System.Collections;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.Audio;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.Audio;
using ScheduleOne.DevUtilities;
using ScheduleOne.GameTime;
using ScheduleOne.Map;
using ScheduleOne.PlayerScripts;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Builds the OTC warehouse — a large brick building with raised foundation,
    /// riser stairs on east entrance, and high windows.
    /// </summary>
    public static class OTCWarehouse
    {
        // Building dimensions
        private const float Width = 12.7f;
        private const float Depth = 10.2f;
        private const float WallHeight = 4.5f;
        private const float FoundationHeight = 3.7f;

        // SW corner origin
        private static readonly Vector3 Origin = new(71.4f, FoundationHeight, -54.0f);

        // Garage door
        private const float DoorWidth = 2.0f;
        private const float DoorHeight = 2.5f;
        private const float DoorThickness = 0.05f;
        private const float DoorOpenDist = 3f;
        private const float DoorCloseDist = 5f;
        private const float DoorSpeed = 2.5f;

        private static bool _initialized;
        private static GameObject _building;
        private static NavMeshRepairer _navMeshRepairer;

        /// <summary>Root transform of the warehouse building, or null if not built.</summary>
        public static Transform BuildingTransform => _building?.transform;
        private static GameObject _garageDoor;
        private static Vector3 _doorClosedLocalPos;
        private static Vector3 _doorOpenLocalPos;
        private static bool _doorShouldBeOpen;
        private static bool _doorRoutineActive;

        private static AudioSource _doorStartSound;
        private static AudioSource _doorLoopSound;
        private static AudioSource _doorStopSound;

        private static Light _warehouseLight;
        private static bool _lightRoutineActive;

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

        /// <summary>Flattens terrain around the warehouse footprint.</summary>
        public static void ClearTerrain()
        {
            if (_building == null) return;
            TerrainClearer.ClearAroundBuilding(_building, new Vector3(Width, WallHeight, Depth),
                new ClearingOptions { Padding = 4f });
        }

        /// <summary>Destroys the warehouse and resets all static state.</summary>
        public static void Cleanup()
        {
            _doorRoutineActive = false;
            _doorShouldBeOpen = false;
            _doorStartSound = null;
            _doorLoopSound = null;
            _doorStopSound = null;
            _lightRoutineActive = false;
            _warehouseLight = null;
            if (_building != null)
            {
                UnityEngine.Object.Destroy(_building);
                _building = null;
            }
            _garageDoor = null;
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

            // East door offset slightly north of center, 2 windows on south side
            var entranceDoor = WallOpening.DoorWithWindows(
                doorWidth: 2.0f, doorHeight: 2.5f,
                leftWindow: WallOpening.Window(width: 5f, height: 1.2f, sillHeight: 3.0f, count: 2),
                rightWindow: null);
            entranceDoor.Offset = 2.0f;

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
                .AddStairs(WallSide.East, foundationHeight: FoundationHeight, style: StairStyle.ClosedRiser)
                .AddParapetRoof(parapetMaterial: blackMat, capMaterial: blackMat)
                .AddLights();

            _building = builder.Build();

            // NavMesh — use employee agent type for indoor navigation
            int navAgentType = 0;
            if (ManagerSpawner.TryGetEmployeeNavMeshSettings(out int empAgentType, out int _))
                navAgentType = empAgentType;
            _navMeshRepairer = builder.CreateNavMeshRepairer(navAgentType);

            // Position must be set before NavMesh bake (uses world coords)
            _building.transform.position = Origin;
            _navMeshRepairer.Build();

            CreateGarageDoor();
            PlaceDecorations();

            OTCLog.Msg(OTCLog.Systems.Patch, $"OTC Warehouse built at {Origin}");
        }

        private static void PlaceDecorations()
        {
            try
            {
                // Security camera — east wall exterior
                PlaceMesh("SM_Prop_Security_Camera_Head_01",
                    new Vector3(84.2688f, 8.1822f, -54.0943f),
                    Quaternion.Euler(0.00f, 126.00f, 0.00f));

                // Security camera — north end
                PlaceMesh("SM_Prop_Security_Camera_Head_01",
                    new Vector3(84.2688f, 8.1822f, -43.6944f),
                    Quaternion.Euler(0.00f, 77.00f, 0.00f));

                // Security camera — mid east wall
                PlaceMesh("SM_Prop_Security_Camera_01",
                    new Vector3(84.2218f, 6.3070f, -48.4967f),
                    Quaternion.Euler(0.00f, 90.00f, 0.00f));

                // Liquid drums — east side
                PlaceMesh("LiquidDrum_LOD0",
                    new Vector3(84.7243f, 2.4445f, -44.2239f),
                    Quaternion.Euler(0.00f, 0.00f, 0.00f));

                PlaceMesh("LiquidDrum_LOD0",
                    new Vector3(84.7243f, 2.7445f, -45.2239f),
                    Quaternion.Euler(-90.00f, 0.00f, 0.00f));

                // Roof AC unit
                PlaceMesh("RoofAirConditioning1",
                    new Vector3(82.7144f, 8.3150f, -45.6617f),
                    Quaternion.Euler(0.00f, 0.00f, 0.00f));

                // Wall lamp — east exterior
                var lamp = PlaceMesh("SM_Lamp_B",
                    new Vector3(84.2451f, 6.5039f, -46.8573f),
                    Quaternion.Euler(0.00f, 90.00f, 0.00f));
                if (lamp != null)
                    SetupScheduledLight(lamp);

                // Display cabinet — interior north wall
                PlaceMesh("display cabinet",
                    new Vector3(83.5876f, 3.9650f, -49.3576f),
                    Quaternion.Euler(-90.00f, 0.00f, 0.00f));

                // Outdoor chair — north exterior
                PlaceMesh("outdoor chair",
                    new Vector3(82.2127f, 4.1650f, -44.5078f),
                    Quaternion.Euler(-90.00f, 196.00f, 0.00f));

                // Sewerage pipe — north side ground level
                PlaceMesh("seweragepipe",
                    new Vector3(79.1421f, 4.1650f, -44.4847f),
                    Quaternion.Euler(0.00f, 0.00f, 0.00f));

                // Shelf — interior south wall
                PlaceMesh("Shelf",
                    new Vector3(83.8785f, 5.5650f, -51.3996f),
                    Quaternion.Euler(-90.00f, 0.00f, 0.00f));

                // Graffiti — east wall exterior
                PlaceMesh("Graffiti",
                    new Vector3(84.2088f, 5.4015f, -44.7649f),
                    Quaternion.Euler(0.00f, -90.00f, 0.00f));

                // Sign — east wall exterior
                PlaceMesh("sign",
                    new Vector3(84.2098f, 5.2809f, -48.4623f),
                    Quaternion.Euler(-90.00f, 180.00f, 0.00f));

                // Office table — north exterior
                PlaceMesh("Office_Table",
                    new Vector3(79.7847f, 3.6650f, -45.4218f),
                    Quaternion.Euler(0.00f, 0.00f, 0.00f));

                // TV cabinet — interior
                PlaceMesh("TV cabinet",
                    new Vector3(78.0357f, 3.6650f, -45.8642f),
                    Quaternion.Euler(-90.00f, 0.00f, 0.00f));

                // Digital alarm — east wall interior
                PlaceMesh("digital alarm",
                    new Vector3(83.9757f, 5.1650f, -48.2056f),
                    Quaternion.Euler(-90.00f, 0.00f, 0.00f));

                // Standalone sink — south wall interior
                PlaceMesh("standalone sink",
                    new Vector3(83.4333f, 3.6950f, -53.4467f),
                    Quaternion.Euler(0.00f, -90.00f, 90.00f));

                // Tap — interior south wall
                PlaceMesh("Tap",
                    new Vector3(83.3937f, 4.7550f, -53.7230f),
                    Quaternion.Euler(-90.00f, 180.00f, 0.00f));

                // Cannisters — interior east wall
                PlaceMesh("Cannister",
                    new Vector3(83.8918f, 5.7246f, -50.8549f),
                    Quaternion.Euler(-90.00f, 0.00f, 0.00f));
                PlaceMesh("Cannister",
                    new Vector3(83.8918f, 5.7246f, -51.1549f),
                    Quaternion.Euler(-90.00f, 0.00f, 0.00f));
                PlaceMesh("Cannister",
                    new Vector3(83.8918f, 5.7246f, -51.4548f),
                    Quaternion.Euler(-90.00f, 0.00f, 0.00f));
                PlaceMesh("Cannister",
                    new Vector3(83.8918f, 5.7246f, -51.7548f),
                    Quaternion.Euler(-90.00f, 0.00f, 0.00f));

                // TV — on TV cabinet
                PlaceMesh("TV",
                    new Vector3(79.8077f, 4.2550f, -45.3264f),
                    Quaternion.Euler(0.00f, 180.00f, 0.00f));

                // Dumpster — north side
                PlaceMesh("Dumpster",
                    new Vector3(83.5637f, 1.9558f, -42.2958f),
                    Quaternion.Euler(-90.00f, 0.00f, 0.00f));

                // Dumpster covers — north side
                PlaceMesh("Dumpster_Cover",
                    new Vector3(83.0469f, 3.1552f, -43.5053f),
                    Quaternion.Euler(-90.00f, 0.00f, 0.00f));
                PlaceMesh("Dumpster_Cover",
                    new Vector3(84.0969f, 3.1552f, -43.5053f),
                    Quaternion.Euler(-90.00f, 0.00f, 0.00f));

            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"OTCWarehouse decoration failed: {ex.Message}");
            }
        }

        private static GameObject PlaceMesh(string sourceName, Vector3 worldPos, Quaternion rotation, Vector3? scale = null)
        {
            var meshes = Resources.FindObjectsOfTypeAll<MeshFilter>();
            GameObject source = null;
            foreach (var mf in meshes)
            {
                if (mf.gameObject.name == sourceName)
                {
                    source = mf.gameObject;
                    break;
                }
            }
            if (source == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"PlaceMesh: could not find '{sourceName}'");
                return null;
            }

            var clone = UnityEngine.Object.Instantiate(source);
            clone.name = $"OTC_{sourceName}";
            clone.transform.SetParent(_building.transform);
            clone.transform.position = worldPos;
            clone.transform.rotation = rotation;
            clone.transform.localScale = scale ?? Vector3.one;

            // Strip game logic, keep visuals
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                UnityEngine.Object.Destroy(mb);
            foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
                UnityEngine.Object.Destroy(rb);

            clone.SetActive(true);
            foreach (var child in clone.GetComponentsInChildren<Transform>(true))
                child.gameObject.SetActive(true);
            foreach (var r in clone.GetComponentsInChildren<MeshRenderer>(true))
                r.enabled = true;

            return clone;
        }

        private static void CreateGarageDoor()
        {
            _garageDoor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _garageDoor.name = "OTC_GarageDoor";
            _garageDoor.transform.SetParent(_building.transform);
            _garageDoor.transform.localScale = new Vector3(DoorThickness, DoorHeight, DoorWidth);

            _doorClosedLocalPos = new Vector3(Width, DoorHeight / 2f, Depth / 2f + 2.0f);
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

        private static void SetupScheduledLight(GameObject lamp)
        {
            var lightGo = new GameObject("OTC_LampLight");
            lightGo.transform.SetParent(lamp.transform);
            lightGo.transform.localPosition = new Vector3(0f, -0.3f, 0f);

            _warehouseLight = lightGo.AddComponent<Light>();
            _warehouseLight.type = LightType.Point;
            _warehouseLight.color = new Color(1f, 0.85f, 0.6f); // warm
            _warehouseLight.intensity = 1.5f;
            _warehouseLight.range = 8f;
            _warehouseLight.shadows = LightShadows.Soft;
            _warehouseLight.enabled = false;

            _lightRoutineActive = true;
            MelonCoroutines.Start(LightScheduleRoutine());
        }

        private static IEnumerator LightScheduleRoutine()
        {
            var wait = new WaitForSeconds(5f);
            while (_lightRoutineActive && _warehouseLight != null)
            {
                try
                {
                    var tm = NetworkSingleton<TimeManager>.Instance;
                    if (tm != null)
                    {
                        // On at 8PM (2000), off at 6AM (600)
                        bool shouldBeOn = tm.CurrentTime >= 2000 || tm.CurrentTime < 600;
                        if (_warehouseLight.enabled != shouldBeOn)
                            _warehouseLight.enabled = shouldBeOn;
                    }
                }
                catch { }

                yield return wait;
            }
        }

        private static IEnumerator DoorProximityRoutine()
        {
            bool wasOpen = false;
            bool wasMoving = false;
            while (_doorRoutineActive && _garageDoor != null)
            {
                try
                {
                    var player = PlayerSingleton<PlayerMovement>.Instance;
                    if (player != null)
                    {
                        var doorWorldPos = _garageDoor.transform.parent.TransformPoint(_doorClosedLocalPos);
                        float dist = Vector3.Distance(player.transform.position, doorWorldPos);

                        if (_doorShouldBeOpen && dist > DoorCloseDist)
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
