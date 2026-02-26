using HarmonyLib;
using MelonLoader;
using OverTheCounter.SaveData;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.Tiles;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.Building;
using S1Property = Il2CppScheduleOne.Property.Property;
using Grid = Il2CppScheduleOne.Tiles.Grid;
#else
using ScheduleOne.Tiles;
using Grid = ScheduleOne.Tiles.Grid;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Harmony patches that let OTC-created grids work with the game's placement system.
    /// Bypasses Property ownership checks, GUID registration, and Container lookups
    /// for synthetic grids that have no Property parent.
    /// </summary>
    public static class BuildingPlacementPatch
    {
        private static readonly MelonLogger.Instance Logger = new("OTC:PlacementPatch");

        // Cached reflection for GridItem.InitializeGridItem patching
        private static MethodInfo _initBuildableItem;
        private static Type _gridItemType;

        // Stub Property assigned to OTC items so override code
        // (AddConfigurable, RegisterExitListener, etc.) runs without NullRef
        private static object _stubProperty;

        // Tracks items placed on OTC grids so the ParentProperty getter patch
        // knows which items to return the stub for
        internal static readonly HashSet<object> OtcItems = new();

        // Cached reflection for OnClosestIntersectionChanged patch
        private static FieldInfo _tileIntersectionTileField;


#if !IL2CPP
        // Mono: cached reflection to set ParentProperty backing field directly
        private static FieldInfo _parentPropertyBackingField;
#endif

        /// <summary>
        /// Registers all Harmony patches for OTC grid placement support.
        /// </summary>
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                // --- Core grid/tile patches ---

                // Patch Grid.Awake — skip Property/GUID init for OTC grids
                var gridAwake = AccessTools.Method(typeof(Grid), "Awake");
                if (gridAwake != null)
                {
                    harmony.Patch(gridAwake,
                        prefix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(GridAwakePrefix)));
                }
                else
                    Logger.Warning("Grid.Awake not found — placement grid will not work");

                // Patch Tile.Awake — skip temperature delegate setup for OTC tiles
                var tileAwake = AccessTools.Method(typeof(Tile), "Awake");
                if (tileAwake != null)
                {
                    harmony.Patch(tileAwake,
                        prefix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(TileAwakePrefix)));
                }
                else
                    Logger.Warning("Tile.Awake not found — OTC tiles may crash on activation");

                // Patch Tile.CanBeBuiltOn — bypass Property.IsOwned check for OTC tiles
                var canBeBuiltOn = AccessTools.Method(typeof(Tile), "CanBeBuiltOn");
                if (canBeBuiltOn != null)
                {
                    harmony.Patch(canBeBuiltOn,
                        prefix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(TileCanBeBuiltOnPrefix)));
                }
                else
                    Logger.Warning("Tile.CanBeBuiltOn not found — placement validation will fail");

                // --- Placement flow patches ---

                // Patch Grid.Container getter — return building root for OTC grids
                var containerGetter = AccessTools.PropertyGetter(typeof(Grid), "Container");
                if (containerGetter != null)
                {
                    harmony.Patch(containerGetter,
                        prefix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(GridContainerPrefix)));
                }
                else
                    Logger.Warning("Grid.Container getter not found — placed items will crash");

                // Patch GridItem.InitializeGridItem — bypass GetProperty for OTC grids
                _gridItemType = AccessTools.TypeByName("ScheduleOne.EntityFramework.GridItem")
                    ?? AccessTools.TypeByName("Il2CppScheduleOne.EntityFramework.GridItem");
                if (_gridItemType != null)
                {
                    var initGridItem = AccessTools.Method(_gridItemType, "InitializeGridItem");
                    if (initGridItem != null)
                    {
                        harmony.Patch(initGridItem,
                            prefix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(InitializeGridItemPrefix)));
                    }
                    else
                        Logger.Warning("GridItem.InitializeGridItem not found — placement will crash");

                    // Cache InitializeBuildableItem for the prefix
                    var buildableType = AccessTools.TypeByName("ScheduleOne.EntityFramework.BuildableItem")
                        ?? AccessTools.TypeByName("Il2CppScheduleOne.EntityFramework.BuildableItem");
                    if (buildableType != null)
                    {
                        _initBuildableItem = AccessTools.Method(buildableType, "InitializeBuildableItem");

#if !IL2CPP
                        // Cache backing field for ParentProperty so we can set it directly.
                        // Harmony getter postfix doesn't work on Mono for 'base.Property'
                        // calls (compiled as non-virtual 'call' — bypasses Harmony hooks).
                        _parentPropertyBackingField = buildableType.GetField(
                            "<ParentProperty>k__BackingField",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_parentPropertyBackingField == null)
                            Logger.Warning("ParentProperty backing field not found — will try property setter");
#endif
                    }
                }
                else
                    Logger.Warning("GridItem type not found — placement patches skipped");

                // --- BuildUpdate_Grid patches ---
                var buildUpdateGridType = AccessTools.TypeByName("ScheduleOne.Building.BuildUpdate_Grid")
                    ?? AccessTools.TypeByName("Il2CppScheduleOne.Building.BuildUpdate_Grid");
                if (buildUpdateGridType != null)
                {
                    // Patch OnClosestIntersectionChanged — prevent NullRef from
                    // ParentProperty being null on OTC grids (no Property component).
                    // Without this, the closestIntersection setter crashes every frame
                    // the ghost tries to change tiles, locking it to one position.
                    var tileIntersectionType = AccessTools.TypeByName("ScheduleOne.Building.TileIntersection")
                        ?? AccessTools.TypeByName("Il2CppScheduleOne.Building.TileIntersection");
                    if (tileIntersectionType != null)
                        _tileIntersectionTileField = AccessTools.Field(tileIntersectionType, "tile");

                    var onChanged = AccessTools.Method(buildUpdateGridType, "OnClosestIntersectionChanged");
                    if (onChanged != null)
                    {
                        harmony.Patch(onChanged,
                            prefix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(OnClosestIntersectionChangedPrefix)));
                    }
                    else
                        Logger.Warning("OnClosestIntersectionChanged not found — ghost will lock to one tile");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to apply placement patches: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // =====================================================================
        //  Grid/Tile Awake patches
        // =====================================================================

        private static bool GridAwakePrefix(Grid __instance)
        {
            if (!BuildingGridFactory.OtcGrids.Contains(__instance))
                return true;

            return false;
        }

        private static bool TileAwakePrefix(Tile __instance)
        {
            if (__instance.OwnerGrid == null)
                return true;
            if (!BuildingGridFactory.OtcGrids.Contains(__instance.OwnerGrid))
                return true;
            return false;
        }

        private static bool TileCanBeBuiltOnPrefix(Tile __instance, ref bool __result)
        {
            if (__instance.OwnerGrid == null)
                return true;
            if (!BuildingGridFactory.OtcGrids.Contains(__instance.OwnerGrid))
                return true;

            __result = PropertySaveData.Instance?.IsPropertyOwned(PropertySaveData.ShackId) ?? false;
            return false;
        }

        // =====================================================================
        //  Stub Property for OTC items
        // =====================================================================

        /// <summary>
        /// Creates a lightweight Property component on a hidden inactive GameObject.
        /// Used as ParentProperty for items placed on OTC grids so that override code
        /// (AddConfigurable, RegisterExitListener, CreateWorldspaceUI, etc.) can run
        /// without NullReferenceException. The GO stays inactive so Property.Awake
        /// never fires. Field initializers (Configurables, BuildableItems lists) are
        /// initialized by the native constructor.
        /// </summary>
        private static void EnsureStubProperty()
        {
            if (_stubProperty != null) return;

            try
            {
                var go = new GameObject("OTC_StubProperty");
                go.SetActive(false);
                go.hideFlags = HideFlags.HideAndDontSave;

#if IL2CPP
                var prop = go.AddComponent<S1Property>();
#else
                var propertyType = AccessTools.TypeByName("ScheduleOne.Property.Property");
                if (propertyType == null)
                {
                    Logger.Error("Property type not found — stub creation failed");
                    return;
                }
                var prop = go.AddComponent(propertyType);
#endif
                _stubProperty = prop;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to create stub Property: {ex.Message}");
            }
        }

        // =====================================================================
        //  Placement flow patches
        // =====================================================================

        /// <summary>
        /// Grid.Container getter: for OTC grids, return the building root transform
        /// instead of ParentProperty.Container.transform (which would null-ref).
        /// </summary>
        private static bool GridContainerPrefix(Grid __instance, ref Transform __result)
        {
            if (!BuildingGridFactory.OtcGrids.Contains(__instance))
                return true;

            if (BuildingGridFactory.GridContainers.TryGetValue(__instance, out var container))
                __result = container;
            else
                __result = __instance.transform;

            return false;
        }

        /// <summary>
        /// Sets ParentProperty on a BuildableItem to the stub Property.
        /// On IL2CPP: direct backing field access.
        /// On Mono: reflection on the auto-property backing field.
        /// Must be called AFTER SetGridData (ProcessGridData overwrites ParentProperty to null).
        /// </summary>
        private static void SetParentPropertyToStub(object item)
        {
#if IL2CPP
            if (item is BuildableItem bi && _stubProperty is S1Property stubProp)
                bi._ParentProperty_k__BackingField = stubProp;
#else
            if (_parentPropertyBackingField != null)
            {
                _parentPropertyBackingField.SetValue(item, _stubProperty);
            }
            else
            {
                // Fallback: try protected setter via GetSetMethod(true)
                var buildableType = AccessTools.TypeByName("ScheduleOne.EntityFramework.BuildableItem");
                var setter = buildableType?.GetProperty("ParentProperty",
                    BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(item, new[] { _stubProperty });
                }
                else
                {
                    Logger.Error("Cannot set ParentProperty — no backing field or setter found");
                }
            }
#endif
        }

        /// <summary>
        /// GridItem.InitializeGridItem prefix: for OTC grids, call InitializeBuildableItem
        /// with an empty PropertyCode, then set ParentProperty to our stub so that
        /// subclass overrides (PackagingStation, PlaceableStorageEntity, etc.) can run
        /// their AddConfigurable / RegisterExitListener / CreateWorldspaceUI code
        /// without NullReferenceException.
        ///
        /// Flow: override calls base.InitializeGridItem → this prefix fires → does
        /// custom init + sets ParentProperty → returns false (skips base body) →
        /// control returns to override which continues with non-null ParentProperty.
        /// </summary>
        private static bool InitializeGridItemPrefix(object __instance, object instance,
            Grid grid, Vector2 originCoordinate, int rotation, string GUID)
        {
            if (!BuildingGridFactory.OtcGrids.Contains(grid))
                return true;

            try
            {
                if (_initBuildableItem == null)
                {
                    Logger.Error("InitializeBuildableItem method not cached — cannot place on OTC grid");
                    return true;
                }

                // Base initialization with empty propertyCode (no Property in hierarchy)
                _initBuildableItem.Invoke(__instance, new object[] { instance, GUID, "" });

                // Register as OTC item
                EnsureStubProperty();
                OtcItems.Add(__instance);

                var setGridData = AccessTools.Method(__instance.GetType(), "SetGridData");
                if (setGridData != null)
                {
                    setGridData.Invoke(__instance, new object[] { grid.GUID, originCoordinate, rotation });
                }
                else
                {
                    Logger.Error("SetGridData not found on GridItem");
                }

                // Set ParentProperty AFTER SetGridData — ProcessGridData overwrites it to null
                SetParentPropertyToStub(__instance);

                // Rebuild interior NavMesh so NPCs can navigate around placed furniture
                WestvilleShack.RebuildNavMesh();
            }
            catch (Exception ex)
            {
                Logger.Error($"OTC InitializeGridItem failed: {ex.Message}\n{ex.StackTrace}");
                return true;
            }

            return false;
        }

        // =====================================================================
        //  Heatmap null-guard (prevents NullRef when ghost changes tiles on OTC grids)
        // =====================================================================

        /// <summary>
        /// OnClosestIntersectionChanged prefix: skip the original when either the
        /// previous or current tile belongs to an OTC grid. The original method calls
        /// HeatmapManager.SetHeatmapActive(ParentProperty, ...) which NullRefs because
        /// OTC grids have no Property parent.
        /// </summary>
        private static bool OnClosestIntersectionChangedPrefix(object previous, object current)
        {
#if IL2CPP
            // IL2CPP: direct type access (reflection doesn't work on IL2CPP types)
            if (previous is TileIntersection prevTi && prevTi.tile != null
                && prevTi.tile.OwnerGrid != null
                && BuildingGridFactory.OtcGrids.Contains(prevTi.tile.OwnerGrid))
                return false;

            if (current is TileIntersection currTi && currTi.tile != null
                && currTi.tile.OwnerGrid != null
                && BuildingGridFactory.OtcGrids.Contains(currTi.tile.OwnerGrid))
                return false;
#else
            // Mono: reflection access
            if (_tileIntersectionTileField == null)
                return true;

            if (previous != null)
            {
                var prevTile = _tileIntersectionTileField.GetValue(previous) as Tile;
                if (prevTile != null && prevTile.OwnerGrid != null
                    && BuildingGridFactory.OtcGrids.Contains(prevTile.OwnerGrid))
                    return false;
            }

            if (current != null)
            {
                var currTile = _tileIntersectionTileField.GetValue(current) as Tile;
                if (currTile != null && currTile.OwnerGrid != null
                    && BuildingGridFactory.OtcGrids.Contains(currTile.OwnerGrid))
                    return false;
            }
#endif
            return true;
        }

    }
}
