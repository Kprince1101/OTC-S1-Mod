using HarmonyLib;
using MelonLoader;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.Money;
using S1MAPI.Building;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.Tiles;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.Building;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Management;
using S1Property = Il2CppScheduleOne.Property.Property;
using Grid = Il2CppScheduleOne.Tiles.Grid;
#else
using ScheduleOne.Management;
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
        // Cached reflection for GridItem.InitializeGridItem patching
        private static MethodInfo _initBuildableItem;
        private static Type _gridItemType;

        // Cached reflection for GrowLight null-guard patches
        private static MethodInfo _proceduralGridItemDestroy;

        // Stub Property assigned to OTC items so override code
        // (AddConfigurable, RegisterExitListener, etc.) runs without NullRef
        private static object _stubProperty;

        // Tracks items placed on OTC grids so the ParentProperty getter patch
        // knows which items to return the stub for
        internal static readonly HashSet<object> OtcItems = new();

#if !IL2CPP
        // Cached reflection for OnClosestIntersectionChanged patch
        private static FieldInfo _tileIntersectionTileField;

        // Mono: cached reflection to set ParentProperty backing field directly
        private static FieldInfo _parentPropertyBackingField;
#endif

        /// <summary>
        /// Resolves a game type by name, trying the correct prefix for the current build first
        /// to avoid noisy warnings from AccessTools when the wrong-prefix lookup fails.
        /// </summary>
        private static Type FindGameType(string monoName)
        {
#if IL2CPP
            return AccessTools.TypeByName("Il2Cpp" + monoName)
                ?? AccessTools.TypeByName(monoName);
#else
            return AccessTools.TypeByName(monoName)
                ?? AccessTools.TypeByName("Il2Cpp" + monoName);
#endif
        }

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
                    OTCLog.Warning(OTCLog.Systems.Patch,"Grid.Awake not found — placement grid will not work");

                // Patch Tile.Awake — skip temperature delegate setup for OTC tiles
                var tileAwake = AccessTools.Method(typeof(Tile), "Awake");
                if (tileAwake != null)
                {
                    harmony.Patch(tileAwake,
                        prefix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(TileAwakePrefix)));
                }
                else
                    OTCLog.Warning(OTCLog.Systems.Patch,"Tile.Awake not found — OTC tiles may crash on activation");

                // Patch Tile.CanBeBuiltOn — bypass Property.IsOwned check for OTC tiles
                var canBeBuiltOn = AccessTools.Method(typeof(Tile), "CanBeBuiltOn");
                if (canBeBuiltOn != null)
                {
                    harmony.Patch(canBeBuiltOn,
                        prefix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(TileCanBeBuiltOnPrefix)));
                }
                else
                    OTCLog.Warning(OTCLog.Systems.Patch,"Tile.CanBeBuiltOn not found — placement validation will fail");

                // --- Placement flow patches ---

                // Patch Grid.Container getter — return building root for OTC grids
                var containerGetter = AccessTools.PropertyGetter(typeof(Grid), "Container");
                if (containerGetter != null)
                {
                    harmony.Patch(containerGetter,
                        prefix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(GridContainerPrefix)));
                }
                else
                    OTCLog.Warning(OTCLog.Systems.Patch,"Grid.Container getter not found — placed items will crash");

                // Patch GridItem.InitializeGridItem — bypass GetProperty for OTC grids
                _gridItemType = FindGameType("ScheduleOne.EntityFramework.GridItem");
                if (_gridItemType != null)
                {
                    var initGridItem = AccessTools.Method(_gridItemType, "InitializeGridItem");
                    if (initGridItem != null)
                    {
                        harmony.Patch(initGridItem,
                            prefix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(InitializeGridItemPrefix)));
                    }
                    else
                        OTCLog.Warning(OTCLog.Systems.Patch,"GridItem.InitializeGridItem not found — placement will crash");

                    // Cache InitializeBuildableItem for the prefix
                    var buildableType = FindGameType("ScheduleOne.EntityFramework.BuildableItem");
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
                            OTCLog.Warning(OTCLog.Systems.Patch,"ParentProperty backing field not found — will try property setter");
#endif
                    }
                }
                else
                    OTCLog.Warning(OTCLog.Systems.Patch,"GridItem type not found — placement patches skipped");

                // Patch BuildManager.CreateGridItem — apply desk visual when counter is placed
                var buildManagerType = FindGameType("ScheduleOne.Building.BuildManager");
                if (buildManagerType != null)
                {
                    var createGridItem = AccessTools.Method(buildManagerType, "CreateGridItem");
                    if (createGridItem != null)
                    {
                        harmony.Patch(createGridItem,
                            postfix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(CreateGridItemPostfix)));
                    }
                    else
                        OTCLog.Warning(OTCLog.Systems.Patch,"BuildManager.CreateGridItem not found — counter visual won't persist on re-placement");
                }

                // Patch BuildStart_Grid.CreateGhostModel — swap ghost visual from plastic table to desk
                var buildStartGridType = FindGameType("ScheduleOne.Building.BuildStart_Grid");
                if (buildStartGridType != null)
                {
                    var createGhostModel = buildStartGridType.GetMethod("CreateGhostModel",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (createGhostModel != null)
                    {
                        harmony.Patch(createGhostModel,
                            postfix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(CreateGhostModelPostfix)));
                    }
                    else
                        OTCLog.Warning(OTCLog.Systems.Patch,"BuildStart_Grid.CreateGhostModel not found — ghost will show plastic table");
                }
                else
                    OTCLog.Warning(OTCLog.Systems.Patch,"BuildStart_Grid type not found");

                // --- BuildUpdate_Grid patches ---
                var buildUpdateGridType = FindGameType("ScheduleOne.Building.BuildUpdate_Grid");
                if (buildUpdateGridType != null)
                {
                    // Patch OnClosestIntersectionChanged — prevent NullRef from
                    // ParentProperty being null on OTC grids (no Property component).
                    // Without this, the closestIntersection setter crashes every frame
                    // the ghost tries to change tiles, locking it to one position.
#if !IL2CPP
                    var tileIntersectionType = FindGameType("ScheduleOne.Building.TileIntersection");
                    if (tileIntersectionType != null)
                        _tileIntersectionTileField = AccessTools.Field(tileIntersectionType, "tile");
#endif

                    var onChanged = AccessTools.Method(buildUpdateGridType, "OnClosestIntersectionChanged");
                    if (onChanged != null)
                    {
                        harmony.Patch(onChanged,
                            prefix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(OnClosestIntersectionChangedPrefix)));
                    }
                    else
                        OTCLog.Warning(OTCLog.Systems.Patch,"OnClosestIntersectionChanged not found — ghost will lock to one tile");
                }

                // Patch BuildableItem.PickupItem — refund desk upgrade cost on counter pickup
                var buildableItemType = FindGameType("ScheduleOne.EntityFramework.BuildableItem");
                if (buildableItemType != null)
                {
                    var pickupItem = AccessTools.Method(buildableItemType, "PickupItem");
                    if (pickupItem != null)
                    {
                        harmony.Patch(pickupItem,
                            prefix: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(PickupItemPrefix)));
                    }
                }

                // --- GrowLight null-guard patches (safety net) ---
                // GrowLight init/destroy access tile.LightExposureNode via the
                // MatchedFootprintTile.MatchedStandardTile chain. BuildingGridFactory
                // now adds LightExposureNode to OTC tiles so this resolves normally.
                // Finalizers remain as a safety net in case the chain is null for
                // any other reason (prevents ghost occupants and stuck racks).
                var growLightType = FindGameType("ScheduleOne.ObjectScripts.GrowLight");
                if (growLightType != null)
                {
                    var glInit = AccessTools.Method(growLightType, "InitializeProceduralGridItem");
                    if (glInit != null)
                    {
                        harmony.Patch(glInit,
                            finalizer: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(GrowLightInitFinalizer)));
                    }

                    var glDestroy = AccessTools.Method(growLightType, "Destroy");
                    if (glDestroy != null)
                    {
                        harmony.Patch(glDestroy,
                            finalizer: new HarmonyMethod(typeof(BuildingPlacementPatch), nameof(GrowLightDestroyFinalizer)));
                    }

                    // Cache ProceduralGridItem.Destroy so the finalizer can call it
                    // when GrowLight.Destroy crashes before reaching base.Destroy()
                    var procGridItemType = FindGameType("ScheduleOne.EntityFramework.ProceduralGridItem");
                    if (procGridItemType != null)
                    {
                        _proceduralGridItemDestroy = AccessTools.Method(procGridItemType, "Destroy");
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"Failed to apply placement patches: {ex.Message}\n{ex.StackTrace}");
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

            if (BuildingGridFactory.GridRegistry.TryGetValue(__instance.OwnerGrid, out var info)
                && info.PropertyId != null)
            {
                __result = PropertySaveData.Instance?.IsPropertyOwned(info.PropertyId) ?? false;
            }
            else
            {
                __result = true; // No ownership check — always buildable
            }
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
                    OTCLog.Error(OTCLog.Systems.Patch,"Property type not found — stub creation failed");
                    return;
                }
                var prop = go.AddComponent(propertyType);
#endif
                _stubProperty = prop;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"Failed to create stub Property: {ex.Message}");
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
                    OTCLog.Error(OTCLog.Systems.Patch,"Cannot set ParentProperty — no backing field or setter found");
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
                    OTCLog.Error(OTCLog.Systems.Patch,"InitializeBuildableItem method not cached — cannot place on OTC grid");
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
                    OTCLog.Error(OTCLog.Systems.Patch,"SetGridData not found on GridItem");
                }

                // Set ParentProperty AFTER SetGridData — ProcessGridData overwrites it to null
                SetParentPropertyToStub(__instance);

                // Record placement in PropertySaveData for persistence
                RecordPlacement(grid, instance, originCoordinate, rotation);

                // Rebuild interior pathfinding so NPCs can navigate around placed furniture
                // (suppressed during batch loading — one rebuild at end instead of per item)
                if (!BuildingGridFactory.SuppressNavigationRebuild &&
                    BuildingGridFactory.GridRegistry.TryGetValue(grid, out var gridInfo))
                    SafeRebuildNavigation(gridInfo);

                // Apply desk visual for checkout counter on all paths.
                // CreateGridItemPostfix handles host-initiated placement, but when a CLIENT
                // places the counter FishNet calls InitializeGridItem directly on the HOST,
                // bypassing BuildManager.CreateGridItem — so the postfix never fires on the
                // host for client-placed items. We check for OTC_Desk to avoid double-apply
                // when BuildManager IS the caller (postfix will also fire in that case).
#if IL2CPP
                if (instance is Il2CppScheduleOne.ItemFramework.ItemInstance ii
                    && ii.ID?.ToLower() == "otc_checkout_counter"
                    && __instance is BuildableItem bi
                    && bi.gameObject.transform.Find("OTC_Desk") == null)
                {
                    CheckoutCounter.RegisterInstance(bi.gameObject, grid);
                    MelonCoroutines.Start(ApplyVisualDeferred(bi.gameObject));
                }
#else
                var clientIdProp = instance?.GetType().GetProperty("ID");
                var clientId = clientIdProp?.GetValue(instance) as string;
                if (clientId?.ToLower() == "otc_checkout_counter")
                {
                    var goProp = __instance.GetType().GetProperty("gameObject");
                    var clientGo = goProp?.GetValue(__instance) as GameObject;
                    if (clientGo != null && clientGo.transform.Find("OTC_Desk") == null)
                    {
                        CheckoutCounter.RegisterInstance(clientGo, grid);
                        MelonCoroutines.Start(ApplyVisualDeferred(clientGo));
                    }
                }
#endif
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"OTC InitializeGridItem failed: {ex.Message}\n{ex.StackTrace}");
                return true;
            }

            return false;
        }

        // =====================================================================
        //  Pickup refund (refunds desk upgrade cost when counter is picked up)
        // =====================================================================

        private static void PickupItemPrefix(object __instance)
        {
            try
            {
                GameObject go = null;
#if IL2CPP
                go = (__instance as BuildableItem)?.gameObject;
#else
                var goProp = __instance?.GetType().GetProperty("gameObject");
                go = goProp?.GetValue(__instance) as GameObject;
#endif
                if (go == null) return;

                var counter = CheckoutCounter.GetCounterByGameObject(go);
                if (counter == null) return;

                var style = DeskStyle.Get(counter.CurrentDeskStyleId);
                float refund = style.Cost - DeskStyle.Default.Cost;
                if (refund > 0f)
                {
                    Money.CreateOnlineTransaction(
                        "Desk Refund", refund, 1f,
                        $"Picked up counter with {style.DisplayName}");
                    OTCLog.Msg(OTCLog.Systems.Patch, $"Refunded ${refund:F0} for {style.DisplayName} on counter pickup");
                }

                counter.Cleanup();
                CheckoutCounter.UnregisterInstance(go);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"PickupItemPrefix failed: {ex.Message}");
            }
        }

        // =====================================================================
        //  GrowLight null-guard finalizers (safety net)
        // =====================================================================
        // BuildingGridFactory now adds LightExposureNode to every OTC tile,
        // so the MatchedStandardTile.LightExposureNode chain resolves normally.
        // These finalizers remain as a safety net for any remaining edge cases.

        /// <summary>
        /// Safety net: suppresses NullRef from GrowLight.InitializeProceduralGridItem
        /// on OTC grids if LightExposureNode is somehow missing.
        /// </summary>
        private static Exception GrowLightInitFinalizer(object __instance, Exception __exception)
        {
            if (__exception is NullReferenceException)
            {
                OTCLog.Msg(OTCLog.Systems.Patch,
                    "GrowLight init: suppressed NullRef on OTC grid");
                return null;
            }
            return __exception;
        }

        /// <summary>
        /// Safety net: catches NullRef from GrowLight.Destroy and calls
        /// ProceduralGridItem.Destroy to clear position data (prevents rack
        /// from being permanently stuck as "supporting another item").
        /// </summary>
        private static Exception GrowLightDestroyFinalizer(object __instance, Exception __exception)
        {
            if (__exception is NullReferenceException)
            {
                try
                {
                    _proceduralGridItemDestroy?.Invoke(__instance, null);
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,
                        $"GrowLight destroy: base.Destroy fallback failed: {ex.Message}");
                }

                OTCLog.Msg(OTCLog.Systems.Patch,
                    "GrowLight destroy: suppressed NullRef, called base.Destroy");
                return null;
            }
            return __exception;
        }

        /// <summary>
        /// Extracts the item ID from an ItemInstance and saves its grid position
        /// to PropertySaveData so the item can be restored on load.
        /// </summary>
        private static void RecordPlacement(Grid grid, object itemInstance, Vector2 coord, int rotation)
        {
            try
            {
                if (PropertySaveData.Instance == null) return;
                var buildingId = BuildingGridFactory.GetBuildingId(grid);
                if (buildingId == null) return;

#if IL2CPP
                if (itemInstance is Il2CppScheduleOne.ItemFramework.ItemInstance ii)
                {
                    PropertySaveData.Instance.SavePlacedItem(
                        buildingId, ii.ID.ToLower(), coord.x, coord.y, rotation);
                }
#else
                var idProp = itemInstance?.GetType().GetProperty("ID");
                if (idProp != null)
                {
                    var id = idProp.GetValue(itemInstance) as string;
                    if (!string.IsNullOrEmpty(id))
                    {
                        PropertySaveData.Instance.SavePlacedItem(
                            buildingId, id.ToLower(), coord.x, coord.y, rotation);
                    }
                }
#endif
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,$"RecordPlacement failed: {ex.Message}");
            }
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

        // =====================================================================
        //  CreateGhostModel postfix — swap ghost visual from plastic table to desk
        // =====================================================================

        /// <summary>
        /// After the ghost model is created for placement preview, check if it's
        /// our checkout counter and swap the plastic table mesh with the desk.
        /// The game applies its own ghost material (white transparent), so we just
        /// need to swap the mesh shape — materials are handled automatically.
        /// </summary>
        private static void CreateGhostModelPostfix(object __result, object itemDefinition)
        {
            if (__result == null || itemDefinition == null) return;

            try
            {
                // IL2CPP types don't expose fields via reflection — use GetProperty instead.
                var defId = (itemDefinition.GetType().GetProperty("ID")?.GetValue(itemDefinition)
                    ?? itemDefinition.GetType().GetField("ID")?.GetValue(itemDefinition)) as string;
                if (defId != "otc_checkout_counter") return;

                var goProp = __result.GetType().GetProperty("gameObject");
                var ghostGo = goProp?.GetValue(__result) as GameObject;
                if (ghostGo == null) return;

                CheckoutCounter.ApplyGhostVisual(ghostGo);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,$"CreateGhostModelPostfix: {ex.Message}");
            }
        }

        // =====================================================================
        //  CreateGridItem postfix — apply desk visual for checkout counter
        // =====================================================================

        /// <summary>
        /// After any GridItem is created via BuildManager.CreateGridItem, check if it's
        /// our checkout counter and apply the desk visual. Also strips ConfigurationReplicator
        /// from any item placed on an OTC grid — these items have no vanilla Property, so
        /// Configuration is never initialized on the client, causing NullRef crashes during
        /// multiplayer loading when the host sends config RPCs.
        /// </summary>
        private static void CreateGridItemPostfix(object __result, object item)
        {
            if (__result == null) return;

            try
            {
                // Get the gameObject from the result GridItem
                var goProp = __result.GetType().GetProperty("gameObject");
                var go = goProp?.GetValue(__result) as GameObject;

                // Strip ConfigurationReplicator from OTC grid items.
                // PlaceableStorageEntity (plastictable, storage racks, etc.) has
                // [RequireComponent(typeof(ConfigurationReplicator))]. On vanilla grids
                // the game assigns Configuration via Property.AddConfigurable(). OTC grids
                // have no vanilla Property, so Configuration stays null on the client.
                // Nulling Configuration on the host's replicator prevents it from sending RPCs that
                // the client can't process. The Prefix guard in ConfigReplicatorPatch.cs
                // serves as a safety net.
                if (go != null)
                {
                    var ownerGridProp = __result.GetType().GetProperty("OwnerGrid");
                    var ownerGrid = ownerGridProp?.GetValue(__result) as Grid;
                    if (ownerGrid != null && BuildingGridFactory.OtcGrids.Contains(ownerGrid))
                    {
                        var configReplicator = go.GetComponent<ConfigurationReplicator>();
                        if (configReplicator != null)
                        {
                            // Null out Configuration so ReplicateField() bails immediately
                            // (safer than Destroy — avoids FishNet NetworkBehaviour issues)
                            configReplicator.Configuration = null;
                        }
                    }
                }

                // Apply checkout counter visual
                string itemId = null;
#if IL2CPP
                if (item is Il2CppScheduleOne.ItemFramework.ItemInstance ii)
                    itemId = ii.Definition?.ID;
#else
                var idProp = item?.GetType().GetProperty("ID");
                itemId = idProp?.GetValue(item) as string;
                if (string.IsNullOrEmpty(itemId))
                {
                    var defProp = item?.GetType().GetProperty("Definition");
                    var def = defProp?.GetValue(item);
                    if (def != null)
                    {
                        var defIdProp = def.GetType().GetProperty("ID");
                        itemId = defIdProp?.GetValue(def) as string;
                    }
                }
#endif

                if (itemId == "otc_checkout_counter" && go != null)
                {
                    var gridProp = __result.GetType().GetProperty("OwnerGrid");
                    var counterGrid = gridProp?.GetValue(__result) as Grid;
                    CheckoutCounter.RegisterInstance(go, counterGrid);
                    MelonCoroutines.Start(ApplyVisualDeferred(go));
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,$"CreateGridItemPostfix: {ex.Message}");
            }
        }

        /// <summary>
        /// Wait for the GridItem to finish initialization, then swap visual.
        /// Disables renderers twice — once immediately, once after a delay to catch
        /// any renderers the game re-enables during async initialization.
        /// </summary>
        private static System.Collections.IEnumerator ApplyVisualDeferred(GameObject go)
        {
            yield return null; // Wait one frame for init
            if (go == null) yield break;
            CheckoutCounter.ApplyDeskVisual(go);

            // Second pass: re-disable any renderers the game may have re-enabled
            yield return new WaitForSeconds(0.5f);
            if (go == null) yield break;
            CheckoutCounter.DisableOriginalRenderers(go);
        }

        // =====================================================================
        //  Safe nav rebuild — preserves NPCs inside the building
        // =====================================================================

        /// <summary>
        /// Wraps RebuildNavigation with NPC position preservation.
        /// S1MAPI's Rebuild() calls Remove() which ReleaseAllNPCs() — warping everyone
        /// outside and re-enabling their NavMeshAgent. This workaround snapshots NPC
        /// positions before rebuild, then re-sends them to the same local positions after.
        /// </summary>
        private static void SafeRebuildNavigation(OtcGridInfo gridInfo)
        {
            if (gridInfo.RebuildNavigation == null) return;

            var buildingId = gridInfo.BuildingId;

            // Snapshot budtenders in this building
            var budtenderSnapshots = new List<(BudtenderInstance bt, Vector3 worldPos)>();
            foreach (var bt in BudtenderInstance.Active.Values)
            {
                if (bt.AssignedCounter?.BuildingId != buildingId) continue;
                if (bt.GameNpc == null) continue;
                if (bt.State == BudtenderState.Off || bt.State == BudtenderState.LeavingBuilding) continue;
                budtenderSnapshots.Add((bt, bt.GameNpc.transform.position));
            }

            // Snapshot customers in this building
            var customerSnapshots = new List<(CustomerInstance ci, Vector3 worldPos)>();
            foreach (var ci in CustomerInstance.Active.Values)
            {
                if (ci.GameNpc == null || ci.Target == null) continue;
                if (ci.Target.BuildingId != buildingId) continue;
                if (ci.State == CustomerState.ExitingStore || ci.State == CustomerState.Despawning) continue;
                // Only snapshot if they're actually inside (not still approaching on exterior)
                var nav = ci.Target.NavBuilder;
                if (nav != null && nav.IsNPCInside(ci.GameNpc.Movement))
                    customerSnapshots.Add((ci, ci.GameNpc.transform.position));
            }

            // Rebuild the navigation grid
            gridInfo.RebuildNavigation.Invoke();

            // Restore budtenders — warp back and re-send to same position
            foreach (var (bt, worldPos) in budtenderSnapshots)
            {
                if (bt.GameNpc == null) continue;
                try
                {
                    bt.GameNpc.Movement?.Warp(worldPos);
                    bt.ResendToPosition(worldPos);
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,
                        $"SafeRebuild: failed to restore budtender {bt.Id}: {ex.Message}");
                }
            }

            // Restore customers — warp back and re-send to same position
            foreach (var (ci, worldPos) in customerSnapshots)
            {
                if (ci.GameNpc == null) continue;
                try
                {
                    ci.GameNpc.Movement?.Warp(worldPos);
                    ci.ResendToPosition(worldPos);
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,
                        $"SafeRebuild: failed to restore customer {ci.Id}: {ex.Message}");
                }
            }

            // Restore suppliers — must use full door-exterior → walk-in process (warps alone don't work)
            if (buildingId == OTCWarehouse.WarehouseId)
            {
                int supplierCount = 0;
                foreach (var supplier in OTCSupplierArea.GetAssignedSuppliers())
                {
                    try
                    {
                        OTCSupplierArea.WarpSupplierToWarehouse(supplier);
                        supplierCount++;
                    }
                    catch (Exception ex)
                    {
                        OTCLog.Warning(OTCLog.Systems.Patch,
                            $"SafeRebuild: failed to restore supplier {supplier.FullName}: {ex.Message}");
                    }
                }

                if (supplierCount > 0)
                    OTCLog.Msg(OTCLog.Systems.Patch,
                        $"SafeRebuild: restored {supplierCount} suppliers in {buildingId}");
            }

            if (budtenderSnapshots.Count > 0 || customerSnapshots.Count > 0)
                OTCLog.Msg(OTCLog.Systems.Patch,
                    $"SafeRebuild: restored {budtenderSnapshots.Count} budtenders, {customerSnapshots.Count} customers in {buildingId}");
        }

    }
}
