using MelonLoader;
using OverTheCounter.Utilities;
using S1API.Items;
using S1MAPI.S1;
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Building;
using Il2CppScheduleOne.Interaction;
using NativeItemInstance = Il2CppScheduleOne.ItemFramework.ItemInstance;
using NativeBuildableItemDef = Il2CppScheduleOne.ItemFramework.BuildableItemDefinition;
using NativeStorableItemDef = Il2CppScheduleOne.ItemFramework.StorableItemDefinition;
using Grid = Il2CppScheduleOne.Tiles.Grid;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Building;
using ScheduleOne.Interaction;
using NativeItemInstance = ScheduleOne.ItemFramework.ItemInstance;
using NativeBuildableItemDef = ScheduleOne.ItemFramework.BuildableItemDefinition;
using NativeStorableItemDef = ScheduleOne.ItemFramework.StorableItemDefinition;
using Grid = ScheduleOne.Tiles.Grid;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Checkout counter built via BuildableItemCreator.CloneFrom("plastictable").
    /// Shares the vanilla BuiltItem prefab (FishNet-compatible) with a custom definition
    /// registered in the game's Registry. After spawn, the visual is swapped to
    /// DeskPedestal + Computer + Keyboard.
    /// </summary>
    public static class CheckoutCounter
    {
        private static readonly MelonLogger.Instance Logger = new("OTC:CheckoutCounter");

        private const string SourceItemId = "plastictable";
        private const string CustomItemId = "otc_checkout_counter";

        private static readonly Vector2 DefaultCoord = new(8, 6);
        private const int DefaultRotation = 0;

        private static BuildableItemDefinition _counterDef;
        private static GameObject _counterInstance;

        /// <summary>World position of the checkout counter, or null if not placed.</summary>
        public static Vector3? CounterPosition =>
            _counterInstance != null ? _counterInstance.transform.position : null;

        /// <summary>
        /// Position where a customer should stand to face the counter.
        /// Offset slightly in front so the NPC doesn't clip into the desk.
        /// </summary>
        public static Vector3? CustomerStandPosition
        {
            get
            {
                if (_counterInstance == null) return null;
                return _counterInstance.transform.position - _counterInstance.transform.forward * 1.0f;
            }
        }

        /// <summary>
        /// Registers the custom checkout counter definition by cloning plastictable.
        /// Must be called after the game Registry is available (OnSceneWasInitialized).
        /// </summary>
        public static void Register()
        {
            if (_counterDef != null) return;

            try
            {
                _counterDef = BuildableItemCreator.CloneFrom(SourceItemId)
                    .WithBasicInfo(CustomItemId, "Checkout Counter", "Dispensary checkout counter")
                    .WithPricing(0f, 0f)
                    .Build();
            }
            catch (Exception ex)
            {
                Logger.Error($"Register failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Spawns the counter as a GridItem at the default position. Host-only.
        /// </summary>
        public static void SpawnOnGrid(Grid grid)
        {
            SpawnOnGrid(grid, DefaultCoord, DefaultRotation);
        }

        /// <summary>
        /// Spawns a GridItem using the cloned definition, then swaps visual to desk.
        /// </summary>
        public static void SpawnOnGrid(Grid grid, Vector2 coord, int rotation)
        {
            if (!NetworkHelper.IsHost) return;
            if (_counterInstance != null)
            {
                Logger.Warning("Counter already exists — skipping duplicate spawn");
                return;
            }
            if (_counterDef == null)
            {
                Logger.Error("Counter definition not registered — call Register() first");
                return;
            }

            try
            {
                // Create native ItemInstance from our custom definition via Registry
                var nativeInstance = CreateInstanceFromRegistry(CustomItemId);
                if (nativeInstance == null)
                {
                    Logger.Error("Failed to create ItemInstance from custom definition");
                    return;
                }
                var bm = Singleton<BuildManager>.Instance;
                if (bm == null)
                {
                    Logger.Error("BuildManager singleton not available");
                    return;
                }

                var gridItem = bm.CreateGridItem(nativeInstance, grid, coord, rotation);

                if (gridItem != null)
                {
                    _counterInstance = gridItem.gameObject;
                    MelonCoroutines.Start(MonitorAndSwapVisual(gridItem.gameObject));
                }
                else
                {
                    Logger.Error("CreateGridItem returned null");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"SpawnOnGrid failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Creates a native ItemInstance by looking up our custom ID in the game Registry.
        /// </summary>
        private static NativeItemInstance CreateInstanceFromRegistry(string itemId)
        {
#if IL2CPP
            var def = Registry.GetItem(itemId);
            if (def == null)
            {
                Logger.Error($"Registry.GetItem('{itemId}') returned null");
                return null;
            }
            var storableDef = def.TryCast<NativeStorableItemDef>();
            if (storableDef == null)
            {
                Logger.Error($"'{itemId}' is not a StorableItemDefinition (type={def.GetType().Name})");
                return null;
            }
            return storableDef.GetDefaultInstance(1);
#else
            // Access native definition directly from our S1API wrapper
            // (avoids Registry.GetItem overload ambiguity on Mono)
            var nativeDefProp = _counterDef.GetType()
                .GetProperty("S1BuildableItemDefinition", BindingFlags.NonPublic | BindingFlags.Instance);
            if (nativeDefProp == null)
            {
                Logger.Error("S1BuildableItemDefinition property not found on S1API wrapper");
                return null;
            }
            var nativeDef = nativeDefProp.GetValue(_counterDef);
            if (nativeDef == null)
            {
                Logger.Error("Native definition is null");
                return null;
            }
            var getDefaultInstance = nativeDef.GetType().GetMethod("GetDefaultInstance", new[] { typeof(int) });
            if (getDefaultInstance == null)
            {
                Logger.Error("GetDefaultInstance not found on native definition");
                return null;
            }
            return getDefaultInstance.Invoke(nativeDef, new object[] { 1 }) as NativeItemInstance;
#endif
        }

        /// <summary>
        /// Monitors the GridItem for destruction (diagnostic) and swaps the visual once stable.
        /// Logs timing if FishNet destroys it.
        /// </summary>
        private static IEnumerator MonitorAndSwapVisual(GameObject go)
        {
            float spawnTime = Time.time;

            // Check every 100ms for the first 2 seconds to catch FishNet destruction
            for (int i = 0; i < 20; i++)
            {
                yield return new WaitForSeconds(0.1f);
                if (go == null)
                {
                    float elapsed = Time.time - spawnTime;
                    Logger.Error($"Counter DESTROYED at {elapsed:F2}s after spawn — FishNet killed it");
                    _counterInstance = null;
                    yield break;
                }
            }

            // Still alive after 2s — stable. Visual is applied by CreateGridItemPostfix.
        }

        /// <summary>
        /// Disables all InteractableObject components on the counter.
        /// Prevents E key from opening the storage UI (which also blocks
        /// E-to-rotate during placement). Storage is not used on the checkout counter.
        /// </summary>
        public static void DisableStorageInteraction(GameObject go)
        {
            foreach (var intObj in go.GetComponentsInChildren<InteractableObject>(true))
                intObj.enabled = false;
        }

        /// <summary>
        /// Disables original plastic table mesh renderers, preserving system objects and OTC_ children.
        /// </summary>
        public static void DisableOriginalRenderers(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                var objName = r.gameObject.name;
                if (objName == "FootprintTiles" || objName == "CircleProjector" ||
                    objName == "DetectionArea" || IsOtcChild(r.transform, go.transform))
                    continue;
                r.enabled = false;
            }
        }

        /// <summary>
        /// Checks if any ancestor (up to stopAt) has a name starting with "OTC_".
        /// Protects all children of OTC-spawned objects from being disabled.
        /// </summary>
        private static bool IsOtcChild(Transform t, Transform stopAt)
        {
            while (t != null && t != stopAt)
            {
                if (t.name.StartsWith("OTC_")) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Swaps the ghost placement preview from plastic table to desk shape only.
        /// No peripherals (computer/keyboard/mouse) — just the desk mesh.
        /// Game applies its own ghost material (white transparent) automatically.
        /// </summary>
        public static void ApplyGhostVisual(GameObject ghostGo)
        {
            DisableOriginalRenderers(ghostGo);
            DisableStorageInteraction(ghostGo);

            Meshes.Custom("ornate desk").Instantiate("OTC_GhostDesk",
                Vector3.zero, Quaternion.Euler(270f, 0f, 0f), ghostGo.transform);

            // Disable ALL colliders (including any from the new desk mesh)
            // so the ghost doesn't push the player around during placement
            foreach (var col in ghostGo.GetComponentsInChildren<Collider>(true))
            {
                col.enabled = false;
            }
        }

        /// <summary>
        /// Replaces the plastic table mesh with ornate desk + Computer + Keyboard + Mouse.
        /// </summary>
        public static void ApplyDeskVisual(GameObject go)
        {
            DisableOriginalRenderers(go);
            DisableStorageInteraction(go);

            // Add ornate desk (freestanding, visible from all sides)
            var ornateDesk = Meshes.Custom("ornate desk");
            var desk = ornateDesk.Instantiate(
                "OTC_Desk", new Vector3(0f, 0f, 0f),
                Quaternion.Euler(270f, 0f, 0f), go.transform);

            if (desk != null)
            {
                // Computer on desk surface — desk has 270 X rotation so local Z = world up
                var computer = Meshes.Computer.Instantiate("OTC_Computer",
                    new Vector3(0.63f, 0.22f, 0.55f),
                    Quaternion.Euler(0f, 0f, 75f), desk.transform);

                // Keyboard base (game-scale mesh, scaled to match Keys overlay)
                var kbBase = Meshes.Keyboard.Instantiate("OTC_KeyboardBase",
                    new Vector3(0.70f, -0.13f, 0.48f),
                    Quaternion.Euler(0f, 0f, 75f), desk.transform);
                if (kbBase != null) kbBase.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);

                // Keys overlay (world-scale mesh, needs 0.03 scale)
                var keys = Meshes.Custom("Keys").Instantiate("OTC_Keys",
                    new Vector3(0.56f, -0.11f, 0.47f),
                    Quaternion.Euler(0f, 0f, 75f), desk.transform);
                if (keys != null) keys.transform.localScale = new Vector3(0.03f, 0.03f, 0.03f);

                // Mouse pad + mouse on desk surface (right of keyboard)
                var mousePad = Meshes.Custom("MousePad").Instantiate("OTC_MousePad",
                    new Vector3(0.27f, -0.07f, 0.47f),
                    Quaternion.Euler(0f, 0f, 75f), desk.transform);

                var mouse = Meshes.Mouse.Instantiate("OTC_Mouse",
                    new Vector3(0.27f, -0.07f, 0.47f),
                    Quaternion.Euler(0f, 0f, 75f), desk.transform);
            }
            else
            {
                Logger.Warning("Ornate desk mesh not found — counter shows plastic table visual");
            }
        }

        /// <summary>
        /// Spawns a vanilla game item (e.g. displaycabinet) on an OTC grid from save data.
        /// Creates a native ItemInstance from the game Registry and places it via BuildManager.
        /// </summary>
        public static void SpawnVanillaGridItem(Grid grid, string itemId, Vector2 coord, int rotation)
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                var nativeInstance = CreateVanillaInstance(itemId);
                if (nativeInstance == null)
                {
                    Logger.Warning($"Could not create instance for '{itemId}' — skipping restore");
                    return;
                }

                var bm = Singleton<BuildManager>.Instance;
                if (bm == null)
                {
                    Logger.Error("BuildManager not available for vanilla item restore");
                    return;
                }

                bm.CreateGridItem(nativeInstance, grid, coord, rotation);
            }
            catch (Exception ex)
            {
                Logger.Warning($"SpawnVanillaGridItem failed for '{itemId}': {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a native ItemInstance from the game Registry by item ID.
        /// </summary>
        private static NativeItemInstance CreateVanillaInstance(string itemId)
        {
#if IL2CPP
            var def = Registry.GetItem(itemId);
            if (def == null)
            {
                Logger.Warning($"Registry.GetItem('{itemId}') returned null");
                return null;
            }
            var storableDef = def.TryCast<NativeStorableItemDef>();
            if (storableDef == null)
            {
                Logger.Warning($"'{itemId}' is not a StorableItemDefinition");
                return null;
            }
            return storableDef.GetDefaultInstance(1);
#else
            // Use reflection to avoid Registry.GetItem overload ambiguity on Mono
            var registryType = typeof(NativeItemInstance).Assembly.GetType("ScheduleOne.Registry");
            if (registryType == null)
            {
                Logger.Error("Registry type not found");
                return null;
            }
            // Filter to non-generic overload to avoid AmbiguousMatchException
            MethodInfo getItem = null;
            foreach (var m in registryType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "GetItem" || m.IsGenericMethod) continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                {
                    getItem = m;
                    break;
                }
            }
            if (getItem == null)
            {
                Logger.Error("Registry.GetItem(string) not found");
                return null;
            }
            var def = getItem.Invoke(null, new object[] { itemId });
            if (def == null)
            {
                Logger.Warning($"Registry.GetItem('{itemId}') returned null");
                return null;
            }
            var getDefaultInstance = def.GetType().GetMethod("GetDefaultInstance", new[] { typeof(int) });
            if (getDefaultInstance == null)
            {
                Logger.Warning($"GetDefaultInstance not found on {def.GetType().Name}");
                return null;
            }
            return getDefaultInstance.Invoke(def, new object[] { 1 }) as NativeItemInstance;
#endif
        }

        /// <summary>
        /// Updates the tracked counter instance. Called from CreateGridItemPostfix
        /// when the player re-places the counter after picking it up.
        /// </summary>
        public static void SetInstance(GameObject go)
        {
            _counterInstance = go;
        }

        /// <summary>Clears counter reference for scene cleanup.</summary>
        public static void Cleanup()
        {
            _counterInstance = null;
            // Don't clear _counterDef — it persists across scene loads
        }
    }
}
