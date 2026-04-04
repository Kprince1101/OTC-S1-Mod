using MelonLoader;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.Items;
using S1API.Money;
using S1API.Shops;
using S1MAPI.S1;
using S1MAPI.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
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
using ScheduleOne;
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
    /// Manages counter definition, registration, spawning, and a registry of
    /// per-counter instances. Each placed counter gets its own CheckoutCounterInstance.
    /// </summary>
    public static class CheckoutCounter
    {
        private const string SourceItemId = "plastictable";
        private const string CustomItemId = "otc_checkout_counter";

        private static readonly Vector2 DefaultCoord = new(8, 6);
        private const int DefaultRotation = 0;

        private static BuildableItemDefinition _counterDef;
        private static Sprite _cachedIcon;

        private static readonly string[] HardwareShopNames =
            { "Handy Hank's Hardware", "Dan's Hardware" };

        // =================================================================
        //  Counter registry
        // =================================================================

        private static readonly List<CheckoutCounterInstance> _counters = new();

        /// <summary>All registered counter instances (read-only view).</summary>
        public static IReadOnlyList<CheckoutCounterInstance> AllCounters => _counters;

        /// <summary>
        /// Registers a counter instance for the given GridItem GameObject.
        /// Returns the existing instance if already registered.
        /// </summary>
        public static CheckoutCounterInstance RegisterInstance(GameObject go, Grid grid = null)
        {
            var existing = GetCounterByGameObject(go);
            if (existing != null) return existing;

            var instance = new CheckoutCounterInstance(go, grid);
            _counters.Add(instance);

            var buildingId = instance.BuildingId;
            OTCLog.Msg(OTCLog.Systems.Patch, $"RegisterInstance: counter #{_counters.Count} grid={grid?.name ?? "null"} buildingId={buildingId ?? "null"} style={instance.CurrentDeskStyleId}");

            // Inherit desk style from existing counters in the same building
            if (buildingId != null)
            {
                foreach (var c in _counters)
                {
                    if (c != instance && c.BuildingId == buildingId
                        && !string.IsNullOrEmpty(c.CurrentDeskStyleId))
                    {
                        instance.CurrentDeskStyleId = c.CurrentDeskStyleId;
                        break;
                    }
                }
            }

            return instance;
        }

        /// <summary>Removes a counter instance from the registry.</summary>
        public static void UnregisterInstance(GameObject go)
        {
            _counters.RemoveAll(c => c.CounterGameObject == go);
        }

        /// <summary>Finds the counter instance whose checkout InteractableObject matches.</summary>
        public static CheckoutCounterInstance GetCounterByInteractable(InteractableObject interactable)
        {
            if (interactable == null) return null;
            for (int i = 0; i < _counters.Count; i++)
            {
                if (_counters[i].CheckoutInteractable == interactable)
                    return _counters[i];
            }
            return null;
        }

        /// <summary>Finds the counter instance whose register InteractableObject matches.</summary>
        public static CheckoutCounterInstance GetCounterByRegister(InteractableObject interactable)
        {
            if (interactable == null) return null;
            for (int i = 0; i < _counters.Count; i++)
            {
                if (_counters[i].RegisterInteractable == interactable)
                    return _counters[i];
            }
            return null;
        }

        /// <summary>Finds the counter instance by its GridItem GameObject.</summary>
        public static CheckoutCounterInstance GetCounterByGameObject(GameObject go)
        {
            if (go == null) return null;
            for (int i = 0; i < _counters.Count; i++)
            {
                if (_counters[i].CounterGameObject == go)
                    return _counters[i];
            }
            return null;
        }

        /// <summary>Returns the registry index of a counter, or -1 if not found.</summary>
        public static int GetCounterIndex(CheckoutCounterInstance counter)
        {
            if (counter == null) return -1;
            return _counters.IndexOf(counter);
        }

        /// <summary>Returns the counter at the given registry index, or null if out of range.</summary>
        public static CheckoutCounterInstance GetCounterByIndex(int index)
        {
            if (index < 0 || index >= _counters.Count) return null;
            return _counters[index];
        }

        // =================================================================
        //  Cash register collection (iterates all counters)
        // =================================================================

        /// <summary>
        /// Updates register interaction prompts and handles collection for all counters.
        /// Called from Core.OnLateUpdate when no checkout is active.
        /// Both host and client — client routes collection through host.
        /// </summary>
        public static void TryCollectRegister()
        {
            var im = Singleton<InteractionManager>.Instance;
            if (im == null) return;
            var hovered = im.HoveredInteractableObject;

            for (int i = 0; i < _counters.Count; i++)
            {
                var counter = _counters[i];
                var regInteractable = counter.RegisterInteractable;
                if (regInteractable == null) continue;

                bool isHovered = hovered == regInteractable;

                if (counter.RegisterBalance > 0f && isHovered)
                {
                    regInteractable.SetMessage($"[Q] Withdraw All - Balance: ${counter.RegisterBalance:F2}");
                    if (regInteractable._interactionState != InteractableObject.EInteractableState.Label)
                        regInteractable.SetInteractableState(InteractableObject.EInteractableState.Label);

                    if (Input.GetKeyDown(KeyCode.Q) && !GameInput.IsTyping)
                    {
                        if (NetworkHelper.IsHost)
                        {
                            float amount = counter.RegisterBalance;
                            Money.ChangeCashBalance(amount, true, true);
                            counter.CollectRegister();
                            ConfigSyncData.Instance?.PublishCheckoutClear();
                        }
                        else
                        {
                            // Host-authoritative: request collection, cash awarded on response
                            CheckoutProcess.RequestRegisterCollect(i);
                        }
                    }
                }
                else if (counter.RegisterBalance > 0f)
                {
                    regInteractable.SetMessage($"Balance: ${counter.RegisterBalance:F2}");
                    if (regInteractable._interactionState != InteractableObject.EInteractableState.Label)
                        regInteractable.SetInteractableState(InteractableObject.EInteractableState.Label);
                }
                else
                {
                    if (regInteractable._interactionState != InteractableObject.EInteractableState.Disabled)
                        regInteractable.SetInteractableState(InteractableObject.EInteractableState.Disabled);
                }
            }
        }

        // =================================================================
        //  Registration & spawning
        // =================================================================

        /// <summary>
        /// Loads the checkout counter icon from the embedded PNG resource.
        /// </summary>
        private static void LoadCounterIcon()
        {
            if (_cachedIcon != null)
            {
                _counterDef.Icon = _cachedIcon;
                return;
            }

            try
            {
                byte[] pngData = EmbeddedResourceLoader.LoadBytes(
                    "OverTheCounter.Resources.CheckoutCounter.png",
                    Assembly.GetExecutingAssembly());
                if (pngData == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, "CheckoutCounter.png embedded resource not found");
                    return;
                }

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (tex.LoadImage(pngData))
                {
                    _cachedIcon = Sprite.Create(tex,
                        new Rect(0, 0, tex.width, tex.height),
                        new Vector2(0.5f, 0.5f), 100f);
                    _cachedIcon.name = "OTC_CheckoutCounterIcon";
                    _counterDef.Icon = _cachedIcon;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"LoadCounterIcon failed: {ex.Message}");
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
                    .WithPricing(150f, 0.5f)
                    .Build();

                LoadCounterIcon();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"Register failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Adds the checkout counter to both hardware stores.
        /// Must be called after game load completes (shops aren't available during OnSceneWasInitialized).
        /// </summary>
        public static void AddToShop()
        {
            if (_counterDef == null) return;

            try
            {
                int added = ShopManager.AddToShops(_counterDef, 150f, HardwareShopNames);
                OTCLog.Msg(OTCLog.Systems.Patch, $"Added checkout counter to {added} hardware store(s)");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"AddToShop failed: {ex.Message}\n{ex.StackTrace}");
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
            if (_counterDef == null)
            {
                OTCLog.Error(OTCLog.Systems.Patch,"Counter definition not registered — call Register() first");
                return;
            }

            try
            {
                var nativeInstance = CreateInstanceFromRegistry(CustomItemId);
                if (nativeInstance == null)
                {
                    OTCLog.Error(OTCLog.Systems.Patch,"Failed to create ItemInstance from custom definition");
                    return;
                }
                var bm = NetworkSingleton<BuildManager>.Instance;
                if (bm == null)
                {
                    OTCLog.Error(OTCLog.Systems.Patch,"BuildManager singleton not available");
                    return;
                }

                var gridItem = bm.CreateGridItem(nativeInstance, grid, coord, rotation);

                if (gridItem != null)
                {
                    RegisterInstance(gridItem.gameObject, grid);
                    MelonCoroutines.Start(MonitorAndSwapVisual(gridItem.gameObject));
                }
                else
                {
                    OTCLog.Error(OTCLog.Systems.Patch,"CreateGridItem returned null");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"SpawnOnGrid failed: {ex.Message}\n{ex.StackTrace}");
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
                OTCLog.Error(OTCLog.Systems.Patch,$"Registry.GetItem('{itemId}') returned null");
                return null;
            }
            var storableDef = def.TryCast<NativeStorableItemDef>();
            if (storableDef == null)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"'{itemId}' is not a StorableItemDefinition (type={def.GetType().Name})");
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
                OTCLog.Error(OTCLog.Systems.Patch,"S1BuildableItemDefinition property not found on S1API wrapper");
                return null;
            }
            var nativeDef = nativeDefProp.GetValue(_counterDef);
            if (nativeDef == null)
            {
                OTCLog.Error(OTCLog.Systems.Patch,"Native definition is null");
                return null;
            }
            var getDefaultInstance = nativeDef.GetType().GetMethod("GetDefaultInstance", new[] { typeof(int) });
            if (getDefaultInstance == null)
            {
                OTCLog.Error(OTCLog.Systems.Patch,"GetDefaultInstance not found on native definition");
                return null;
            }
            return getDefaultInstance.Invoke(nativeDef, new object[] { 1 }) as NativeItemInstance;
#endif
        }

        /// <summary>
        /// Monitors the GridItem for destruction (diagnostic) and swaps the visual once stable.
        /// </summary>
        private static IEnumerator MonitorAndSwapVisual(GameObject go)
        {
            float spawnTime = Time.time;

            for (int i = 0; i < 20; i++)
            {
                yield return new WaitForSeconds(0.1f);
                if (go == null)
                {
                    float elapsed = Time.time - spawnTime;
                    OTCLog.Error(OTCLog.Systems.Patch,$"Counter DESTROYED at {elapsed:F2}s after spawn — FishNet killed it");
                    UnregisterInstance(go);
                    yield break;
                }
            }
        }

        // =================================================================
        //  Visual helpers (static utilities)
        // =================================================================

        /// <summary>
        /// Disables all InteractableObject components on the counter.
        /// Prevents E key from opening the storage UI during placement.
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
        /// </summary>
        public static void ApplyGhostVisual(GameObject ghostGo)
        {
            DisableOriginalRenderers(ghostGo);
            DisableStorageInteraction(ghostGo);

            Meshes.Custom("ornate desk").Instantiate("OTC_GhostDesk",
                Vector3.zero, Quaternion.Euler(270f, 0f, 0f), ghostGo.transform);

            foreach (var col in ghostGo.GetComponentsInChildren<Collider>(true))
            {
                col.enabled = false;
            }
        }

        /// <summary>
        /// Applies the desk visual for a registered counter instance.
        /// Looks up the instance by GameObject and delegates to its ApplyDeskVisual method.
        /// </summary>
        public static void ApplyDeskVisual(GameObject go)
        {
            var counter = GetCounterByGameObject(go);
            if (counter == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, "ApplyDeskVisual called for unregistered counter");
                return;
            }
            counter.ApplyDeskVisual();
        }

        // =================================================================
        //  Vanilla item factories
        // =================================================================

        /// <summary>
        /// Spawns a vanilla game item on an OTC grid from save data.
        /// </summary>
        public static void SpawnVanillaGridItem(Grid grid, string itemId, Vector2 coord, int rotation)
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                var nativeInstance = CreateVanillaInstance(itemId);
                if (nativeInstance == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,$"Could not create instance for '{itemId}' — skipping restore");
                    return;
                }

                var bm = NetworkSingleton<BuildManager>.Instance;
                if (bm == null)
                {
                    OTCLog.Error(OTCLog.Systems.Patch,"BuildManager not available for vanilla item restore");
                    return;
                }

                bm.CreateGridItem(nativeInstance, grid, coord, rotation);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,$"SpawnVanillaGridItem failed for '{itemId}': {ex.Message}");
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
                OTCLog.Warning(OTCLog.Systems.Patch,$"Registry.GetItem('{itemId}') returned null");
                return null;
            }
            var storableDef = def.TryCast<NativeStorableItemDef>();
            if (storableDef == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,$"'{itemId}' is not a StorableItemDefinition");
                return null;
            }
            return storableDef.GetDefaultInstance(1);
#else
            var registryType = typeof(NativeItemInstance).Assembly.GetType("ScheduleOne.Registry");
            if (registryType == null)
            {
                OTCLog.Error(OTCLog.Systems.Patch,"Registry type not found");
                return null;
            }
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
                OTCLog.Error(OTCLog.Systems.Patch,"Registry.GetItem(string) not found");
                return null;
            }
            var def = getItem.Invoke(null, new object[] { itemId });
            if (def == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,$"Registry.GetItem('{itemId}') returned null");
                return null;
            }
            var getDefaultInstance = def.GetType().GetMethod("GetDefaultInstance", new[] { typeof(int) });
            if (getDefaultInstance == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,$"GetDefaultInstance not found on {def.GetType().Name}");
                return null;
            }
            return getDefaultInstance.Invoke(def, new object[] { 1 }) as NativeItemInstance;
#endif
        }

        // =================================================================
        //  Cleanup
        // =================================================================

        /// <summary>Cleans up all counter instances and resets registry + definition.</summary>
        public static void Cleanup()
        {
            foreach (var counter in _counters)
                counter.Cleanup();
            _counters.Clear();
            // Must clear _counterDef so Register() re-adds it to the game Registry,
            // which clears "runtime items" on scene transitions.
            _counterDef = null;
        }
    }
}
