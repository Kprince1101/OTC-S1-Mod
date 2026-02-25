using MelonLoader;
using S1MAPI.Building;
using S1MAPI.Building.Config;
using S1MAPI.Building.Interior;
using S1MAPI.Building.Structural;
using S1MAPI.Gltf;
using S1MAPI.S1;
using S1MAPI.Utils;
using System;
using System.Reflection;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.Doors;
#else
using ScheduleOne.Doors;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Permanent OTC building — east-facing shack in Westville with placement grid.
    /// </summary>
    public static class WestvilleShack
    {
        private static readonly MelonLogger.Instance Logger = new("OTC:WestvilleShack");

        // Room dimensions (smaller than B1's 8x9)
        private const float RoomWidth = 6f;
        private const float RoomHeight = 3.5f;
        private const float RoomDepth = 5f;
        private const float FoundationHeight = 1.1f;

        // SW corner of building footprint — ground slopes ~-3 to -3.5 here
        private static readonly Vector3 BuildingOrigin = new(-167.4f, -4f, 73.5f);

        private static GameObject _building;
        private static bool _initialized;


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
                Logger.Error($"SpawnBuilding failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>Destroys the building and resets state for scene reload.</summary>
        public static void Cleanup()
        {
            if (_building != null) GameObject.Destroy(_building);
            _building = null;
            _initialized = false;
        }

        private static void ConfigureDoor(GameObject doorGo)
        {
            var doorCtrl = doorGo.GetComponentInChildren<DoorController>(true);
            if (doorCtrl != null)
                doorCtrl.PlayerAccess = EDoorAccess.Open;
            else
                Logger.Warning($"Door '{doorGo.name}' has no DoorController");
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
            Logger.Warning($"Scene prop '{goName}' not found");
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
                .AddPrefab(Prefabs.ClassicalWoodenDoor,
                    new Vector3(RoomWidth, 0f, RoomDepth / 2f - 1.2f),
                    Quaternion.Euler(0f, 270f, 0f), onCreated: ConfigureDoor)
                .AddStairs(wall: WallSide.East, foundationHeight: FoundationHeight, style: StairStyle.ClosedRiser)
                .AddParapetRoof(ParapetPreset.Shallow, parapetMaterial: Materials.Find("concrete light beige"), capMaterial: Materials.Find("concrete light beige"));

            _building = builder.Build();

            // Position: room sits on top of foundation
            _building.transform.position = new Vector3(
                BuildingOrigin.x, BuildingOrigin.y + FoundationHeight, BuildingOrigin.z);

            // Placement grid — no interior walls, just exclude exterior wall edges
            BuildingGridFactory.CreateGrid(_building, RoomWidth, RoomDepth, "WestvilleShack_Floor1",
                tileFilter: (x, z) =>
                {
                    if (x == 0 || z == 0) return false;
                    return true;
                });

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
                        Logger.Warning("GltfLoader returned null for ShackSign.glb");
                }
                else
                    Logger.Warning("Could not load ShackSign.glb embedded resource");
            }
            catch (Exception ex)
            {
                Logger.Error($"ShackSign load failed: {ex.Message}");
            }

            TerrainClearer.ClearAroundBuilding(_building, new Vector3(RoomWidth, RoomHeight, RoomDepth),
                new ClearingOptions { Padding = 4f });

            // Exterior props — exterior ground is ~-0.1 in building local space (world Y≈-3.0, building Y=-2.9)
            float extGround = -0.1f;
            new InteriorBuilder(_building.transform, "WestvilleShack_Props")
                // Dumpster on north side
                .AddCustomMesh(Meshes.Dumpster, "Dumpster",
                    new Vector3(2f, extGround - 1.0f, RoomDepth + 2.5f), Quaternion.Euler(-90f, 0f, 0f))
                // Two dumpster lids side by side on top of opening
                .AddCustomMesh(Meshes.DumpsterCover, "DumpsterLid1",
                    new Vector3(1.5f, extGround + 0.2f, RoomDepth + 1.3f), Quaternion.Euler(-90f, 0f, 0f))
                .AddCustomMesh(Meshes.DumpsterCover, "DumpsterLid2",
                    new Vector3(2.5f, extGround + 0.2f, RoomDepth + 1.3f), Quaternion.Euler(-90f, 0f, 0f))
                .Build(_building);

            // Trash can near east entrance
            new InteriorBuilder(_building.transform, "WestvilleShack_TrashCan")
                .AddPrefab(Prefabs.TrashCan, new Vector3(7.0f, extGround - 1.0f, -1.0f), Quaternion.identity, networked: true,
                    onCreated: go =>
                    {
                        // Activate lid (disabled by default) and hide detection/grid visuals
                        foreach (var t in go.GetComponentsInChildren<Transform>(true))
                        {
                            if (t.name.StartsWith("Lid"))
                                t.gameObject.SetActive(true);
                            else if (t.name == "DetectionArea" || t.name == "FootprintTiles" || t.name == "CircleProjector")
                                t.gameObject.SetActive(false);
                        }
                    })
                .Build(_building);

            // Wooden crates — scene cloned, decorative only
            CloneSceneProp("Wood Crate Prop", _building.transform, new Vector3(-1.2f, extGround - 1.0f, 4.2f), Quaternion.identity);
            CloneSceneProp("Wood Crate Prop", _building.transform, new Vector3(-1.2f, extGround - 1.0f, 5.4f), Quaternion.identity);
            CloneSceneProp("Wood Crate Prop", _building.transform, new Vector3(-1.2f, extGround - 0.2f, 4.8f), Quaternion.Euler(0f, 15f, 0f));
            // Lone rotated crate on north-east side
            CloneSceneProp("Wood Crate Prop", _building.transform, new Vector3(7.4f, extGround - 1.0f, 5.1f), Quaternion.Euler(0f, 35f, 0f));

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
                        Logger.Warning("GltfLoader returned null for LeafSign.glb");
                }
                else
                    Logger.Warning("Could not load LeafSign.glb embedded resource");
            }
            catch (Exception ex)
            {
                Logger.Error($"LeafSign load failed: {ex.Message}");
            }

        }
    }
}
