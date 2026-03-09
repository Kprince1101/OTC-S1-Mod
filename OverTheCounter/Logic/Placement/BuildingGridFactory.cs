using HarmonyLib;
using OverTheCounter.Utilities;
using System;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.Tiles;
using Grid = Il2CppScheduleOne.Tiles.Grid;
#else
using System.Reflection;
using ScheduleOne.Tiles;
using Grid = ScheduleOne.Tiles.Grid;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Creates Grid + Tile hierarchies on S1MAPI building floors so the game's
    /// existing placement system (BuildManager / TileDetector) works inside them.
    /// </summary>
    public static class BuildingGridFactory
    {
        /// <summary>Tracks OTC-created grids so Harmony patches can identify them.</summary>
        internal static readonly HashSet<Grid> OtcGrids = new();
        /// <summary>Maps OTC grids to their building root transform (used as Container).</summary>
        internal static readonly Dictionary<Grid, Transform> GridContainers = new();
        private static readonly List<GameObject> _gridRoots = new();

        private const float TileSize = 0.5f;

#if !IL2CPP
        // Mono: cached reflection for protected/private Grid members
        private static readonly FieldInfo _coordDictField =
            AccessTools.Field(typeof(Grid), "_coordinateToTile");
        private static readonly PropertyInfo _widthProp =
            AccessTools.Property(typeof(Grid), "Width");
        private static readonly PropertyInfo _heightProp =
            AccessTools.Property(typeof(Grid), "Height");
#endif

        /// <summary>
        /// Creates a placement grid covering the full floor of a building room.
        /// Must be called AFTER the building is built and positioned.
        /// </summary>
        /// <param name="tileFilter">Optional filter: return true to include tile at (x, z), false to exclude.
        /// Used to block tiles under interior walls.</param>
        public static Grid CreateGrid(GameObject buildingRoot, float roomWidth, float roomDepth,
            string buildingName, Func<int, int, bool> tileFilter = null)
        {
            int tileLayer = LayerMask.NameToLayer("Tile");
            if (tileLayer < 0)
            {
                OTCLog.Error(OTCLog.Systems.Patch,"'Tile' physics layer not found — cannot create placement grid.");
                return null;
            }

            int tilesX = (int)(roomWidth / TileSize);
            int tilesZ = (int)(roomDepth / TileSize);

            // Create grid GO inactive so we can set up before Awake fires
            var gridGo = new GameObject($"OTC_Grid_{buildingName}");
            gridGo.SetActive(false);
            gridGo.transform.SetParent(buildingRoot.transform, false);
            gridGo.transform.localPosition = Vector3.zero;

            var grid = gridGo.AddComponent<Grid>();
            OtcGrids.Add(grid);
            GridContainers[grid] = buildingRoot.transform;

            // Create tiles as children
            for (int x = 0; x < tilesX; x++)
            {
                for (int z = 0; z < tilesZ; z++)
                {
                    if (tileFilter != null && !tileFilter(x, z))
                        continue;

                    var tileGo = new GameObject($"Tile_{x}_{z}");
                    tileGo.transform.SetParent(gridGo.transform, false);
                    // Position at grid corners (not centers) — GetMatchedCoordinate
                    // divides by TileSize and rounds, so positions must be exact multiples
                    tileGo.transform.localPosition = new Vector3(
                        x * TileSize,
                        0f,
                        z * TileSize);
                    tileGo.layer = tileLayer;

                    var tile = tileGo.AddComponent<IndoorTile>();
                    tile.x = x;
                    tile.y = z;
                    tile.OwnerGrid = grid;

                    var col = tileGo.AddComponent<BoxCollider>();
                    col.isTrigger = true;
                    col.size = new Vector3(TileSize, 0.1f, TileSize);
                    // Offset collider below floor surface so the forward ray
                    // (LookRaycast_ExcludeBuildables, includeTriggers=true) hits the
                    // S1MAPI floor BoxCollider first (continuous surface → smooth ghost
                    // tracking). OverlapSphere(radius=0.25) still reaches tiles at -0.15.
                    // transform.position stays at (x*0.5, 0, z*0.5) for coordinate math.
                    col.center = new Vector3(0f, -0.15f, 0f);

                    // Populates grid.Tiles + grid.CoordinateTilePairs (game code)
                    grid.RegisterTile(tile);
                }
            }

            // No FloorCollider needed — S1MAPI's .AddFloor() creates a BoxCollider
            // (from CreatePrimitive(Cube)) on the Default layer that serves the same
            // purpose as vanilla property floor meshes for forward + downward raycasts.

            // Activate — Grid.Awake and Tile.Awake fire, caught by our Harmony patches
            gridGo.SetActive(true);

            // Manually initialize what our Awake prefix skipped
            InitializeOtcGrid(grid);

            _gridRoots.Add(gridGo);
            return grid;
        }

        /// <summary>
        /// Populates the coordinate→tile dictionary and sets grid dimensions.
        /// Called after activation (Awake was skipped by our prefix).
        /// </summary>
        private static void InitializeOtcGrid(Grid grid)
        {
            // Build _coordinateToTile dictionary from the Tiles list
            for (int i = 0; i < grid.Tiles.Count; i++)
            {
                var tile = grid.Tiles[i];
                var coord = new Coordinate(tile.x, tile.y);
#if IL2CPP
                grid._coordinateToTile[coord] = tile;
#else
                var dict = (Dictionary<Coordinate, Tile>)_coordDictField.GetValue(grid);
                dict[coord] = tile;
#endif
            }

            // Calculate grid dimensions from tile coordinates
            int maxX = 0, maxY = 0;
            for (int i = 0; i < grid.Tiles.Count; i++)
            {
                var tile = grid.Tiles[i];
                if (tile.x > maxX) maxX = tile.x;
                if (tile.y > maxY) maxY = tile.y;
            }

#if IL2CPP
            grid.Width = maxX + 1;
            grid.Height = maxY + 1;
#else
            _widthProp.SetValue(grid, maxX + 1);
            _heightProp.SetValue(grid, maxY + 1);
#endif

            // Register with GUIDManager so GridItem.SetGridData can look us up
            RegisterGridGuid(grid);

        }

        /// <summary>
        /// Generates a unique GUID for the OTC grid and registers it with GUIDManager.
        /// Required for GridItem.SetGridData → GUIDManager.GetObject&lt;Grid&gt;(guid) to find us.
        /// </summary>
        private static void RegisterGridGuid(Grid grid)
        {
            try
            {
#if IL2CPP
                // IL2CPP: use direct API calls (reflection doesn't work with Il2CppSystem types)
                var il2cppGuid = Il2CppSystem.Guid.NewGuid();
                grid.SetGUID(il2cppGuid);
                Il2Cpp.GUIDManager.RegisterObject(grid.Cast<Il2CppScheduleOne.IGUIDRegisterable>(), null);
#else
                // Mono: reflection-based registration
                var setGuidMethod = AccessTools.Method(typeof(Grid), "SetGUID", new[] { typeof(Guid) });
                if (setGuidMethod == null)
                {
                    foreach (var iface in typeof(Grid).GetInterfaces())
                    {
                        if (!iface.Name.Contains("IGUIDRegisterable")) continue;
                        setGuidMethod = iface.GetMethod("SetGUID", new[] { typeof(Guid) });
                        break;
                    }
                }

                var newGuid = Guid.NewGuid();
                if (setGuidMethod != null)
                {
                    setGuidMethod.Invoke(grid, new object[] { newGuid });
                }
                else
                {
                    var guidProp = AccessTools.Property(typeof(Grid), "GUID");
                    guidProp?.SetValue(grid, newGuid);
                }

                var guidMgrType = AccessTools.TypeByName("GUIDManager");
                var registerMethod = AccessTools.Method(guidMgrType, "RegisterObject");
                registerMethod?.Invoke(null, new object[] { grid, null });

#endif
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,$"Failed to register grid GUID: {ex.Message}");
            }
        }

        /// <summary>Destroys all OTC grids and clears tracking state.</summary>
        public static void Cleanup()
        {
            foreach (var go in _gridRoots)
            {
                if (go != null) GameObject.Destroy(go);
            }
            _gridRoots.Clear();
            OtcGrids.Clear();
            GridContainers.Clear();
        }
    }
}
