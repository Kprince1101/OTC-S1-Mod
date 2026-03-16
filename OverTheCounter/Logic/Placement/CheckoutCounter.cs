using MelonLoader;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.Items;
using S1API.Money;
using S1MAPI.Gltf;
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
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.Product;
using NativeItemInstance = Il2CppScheduleOne.ItemFramework.ItemInstance;
using NativeBuildableItemDef = Il2CppScheduleOne.ItemFramework.BuildableItemDefinition;
using NativeStorableItemDef = Il2CppScheduleOne.ItemFramework.StorableItemDefinition;
using Grid = Il2CppScheduleOne.Tiles.Grid;
#else
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.Building;
using ScheduleOne.Interaction;
using ScheduleOne.Storage;
using ScheduleOne.Product;
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
        private const string SourceItemId = "plastictable";
        private const string CustomItemId = "otc_checkout_counter";

        private static readonly Vector2 DefaultCoord = new(8, 6);
        private const int DefaultRotation = 0;

        private static BuildableItemDefinition _counterDef;
        private static GameObject _counterInstance;
        private static InteractableObject _checkoutInteractable;

        // Cash register
        private static GameObject _registerInstance;
        private static InteractableObject _registerInteractable;
        private static float _registerBalance;

        // Desk corner product display (replaces StorageVisualizer)
        private static Transform _deskTransform;
        private static readonly List<GameObject> _deskDisplayItems = new();
        private static StorageEntity _counterStorageEntity;

        // Desk display layout offsets (finalized via runtime editor)
        private const float DisplayXBase = -0.89f;
        private const float DisplayYBase = -0.38f;
        private const float DisplayZPos = 0.47f;
        private const float DisplayRotX = 90f;
        private const float DisplayScale = 0.5f;

        /// <summary>World position of the checkout counter, or null if not placed.</summary>
        public static Vector3? CounterPosition =>
            _counterInstance != null ? _counterInstance.transform.position : null;

        /// <summary>The counter's Transform, or null if not placed.</summary>
        public static Transform CounterTransform =>
            _counterInstance != null ? _counterInstance.transform : null;

        /// <summary>The counter's StorageEntity, or null if not placed.</summary>
        public static StorageEntity CounterStorageEntity => _counterStorageEntity;

        /// <summary>World position of the counter surface (top of desk).
        /// Desk mesh is at 270° X rotation; desk-local Z=0.55 maps to world Y offset.</summary>
        public static Vector3? SurfacePosition =>
            _counterInstance != null
                ? _counterInstance.transform.position + Vector3.up * 0.6f
                : null;

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

        // =================================================================
        //  Cash register
        // =================================================================

        /// <summary>The cash register's Transform, or null if not placed.</summary>
        public static Transform RegisterTransform =>
            _registerInstance != null ? _registerInstance.transform : null;

        /// <summary>World position of the cash register, or null if not placed.</summary>
        public static Vector3? RegisterPosition =>
            _registerInstance != null ? _registerInstance.transform.position : null;

        /// <summary>Accumulated cash balance in the register.</summary>
        public static float RegisterBalance
        {
            get => _registerBalance;
            set => _registerBalance = value;
        }

        /// <summary>Adds cash to the register balance.</summary>
        public static void DepositToRegister(float amount) =>
            _registerBalance += amount;

        /// <summary>Withdraws all cash from register. Returns amount collected.</summary>
        public static float CollectRegister()
        {
            float amount = _registerBalance;
            _registerBalance = 0f;
            return amount;
        }

        /// <summary>
        /// Updates register interaction prompt and handles collection.
        /// Called from Core.OnLateUpdate when no checkout is active.
        /// Both host and client — client routes collection through host.
        /// </summary>
        public static void TryCollectRegister()
        {
            if (_registerInteractable == null) return;

            // Update prompt based on whether player is looking at register
            var im = Singleton<InteractionManager>.Instance;
            bool isHovered = im?.HoveredInteractableObject == _registerInteractable;

            if (_registerBalance > 0f && isHovered)
            {
                _registerInteractable.SetMessage($"[Q] Withdraw All - Balance: ${_registerBalance:F2}");
                if (_registerInteractable._interactionState != InteractableObject.EInteractableState.Label)
                    _registerInteractable.SetInteractableState(InteractableObject.EInteractableState.Label);

                // Q key to withdraw
                if (Input.GetKeyDown(KeyCode.Q) && !GameInput.IsTyping)
                {
                    if (NetworkHelper.IsHost)
                    {
                        float amount = CollectRegister();
                        Money.ChangeCashBalance(amount, true, true);
                        SaveData.ConfigSyncData.Instance?.PublishCheckoutClear();
                    }
                    else
                    {
                        // Client: ask host to collect and give money
                        SaveData.ConfigSyncData.SendQuestAction("REGISTER_COLLECT");
                    }
                }
            }
            else if (_registerBalance > 0f)
            {
                _registerInteractable.SetMessage($"Balance: ${_registerBalance:F2}");
                if (_registerInteractable._interactionState != InteractableObject.EInteractableState.Label)
                    _registerInteractable.SetInteractableState(InteractableObject.EInteractableState.Label);
            }
            else
            {
                if (_registerInteractable._interactionState != InteractableObject.EInteractableState.Disabled)
                    _registerInteractable.SetInteractableState(InteractableObject.EInteractableState.Disabled);
            }
        }

        // =================================================================
        //  Registration & spawning
        // =================================================================

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
                OTCLog.Error(OTCLog.Systems.Patch,$"Register failed: {ex.Message}\n{ex.StackTrace}");
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
                OTCLog.Warning(OTCLog.Systems.Patch,"Counter already exists — skipping duplicate spawn");
                return;
            }
            if (_counterDef == null)
            {
                OTCLog.Error(OTCLog.Systems.Patch,"Counter definition not registered — call Register() first");
                return;
            }

            try
            {
                // Create native ItemInstance from our custom definition via Registry
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
                    _counterInstance = gridItem.gameObject;
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
                    OTCLog.Error(OTCLog.Systems.Patch,$"Counter DESTROYED at {elapsed:F2}s after spawn — FishNet killed it");
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

        /// <summary>The counter's InteractableObject, used to gate F-key checkout to line-of-sight.</summary>
        public static InteractableObject CounterInteractable => _checkoutInteractable;

        /// <summary>
        /// Saves a reference to the counter's InteractableObject and sets the default message.
        /// Vanilla storage listeners are left intact so E opens storage normally.
        /// </summary>
        private static void CaptureInteractable(GameObject go)
        {
            _checkoutInteractable = go.GetComponentInChildren<InteractableObject>(true);
            if (_checkoutInteractable != null)
                _checkoutInteractable.SetMessage("Storage");
        }

        /// <summary>
        /// Shows or hides checkout info on the computer screen.
        /// Counter interaction message stays "Storage" always — the [F] prompt
        /// is displayed on the computer screen instead of the InteractionCanvas.
        /// </summary>
        public static void SetCheckoutAvailable(bool available)
        {
            if (!available)
                ComputerScreen.HideCheckoutInfo();
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
            CaptureInteractable(go);

            // Add ornate desk (freestanding, visible from all sides)
            var ornateDesk = Meshes.Custom("ornate desk");
            var desk = ornateDesk.Instantiate(
                "OTC_Desk", new Vector3(0f, 0f, 0f),
                Quaternion.Euler(270f, 0f, 0f), go.transform);

            if (desk != null)
            {
                // Computer (all-in-one with built-in monitor) on desk surface
                // Desk has 270 X rotation so local Z = world up
                var computer = Meshes.Computer.Instantiate("OTC_Computer",
                    new Vector3(0.63f, 0.22f, 0.55f),
                    Quaternion.Euler(0f, 0f, 75f), desk.transform);

                // WorldSpace display on the computer's built-in screen
                if (computer != null)
                    ComputerScreen.Create(computer);

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

                // Cash register on right side of desk (opposite from computer)
                SpawnCashRegister(desk.transform);

                _deskTransform = desk.transform;

                // Disable the default StorageVisualizer so products don't render on the invisible plastic table
                DisableStorageVisualizer(go);

                // Hook storage slot changes to update our desk corner display
                HookStorageDisplay(go);
            }
            else
            {
                OTCLog.Warning(OTCLog.Systems.Patch,"Ornate desk mesh not found — counter shows plastic table visual");
            }
        }

        /// <summary>
        /// Loads the CashRegister.glb embedded resource and places it on the desk.
        /// </summary>
        private static void SpawnCashRegister(Transform deskTransform)
        {
            try
            {
                byte[] glbData = EmbeddedResourceLoader.LoadBytes(
                    "OverTheCounter.Resources.CashRegister.glb",
                    Assembly.GetExecutingAssembly());
                if (glbData == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,"Could not load CashRegister.glb embedded resource");
                    return;
                }

                var register = GltfLoader.LoadGlb(glbData);
                if (register == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,"GltfLoader returned null for CashRegister.glb");
                    return;
                }

                register.name = "OTC_CashRegister";
                register.transform.SetParent(deskTransform, false);
                // Right side of desk, on surface (desk has 270 X rotation, so local Z = world up)
                register.transform.localPosition = new Vector3(-0.74f, 0.06f, 0.49f);
                register.transform.localRotation = Quaternion.Euler(345f, 90f, 90f);
                register.transform.localScale = Vector3.one * 0.14f;

                // Disable existing colliders, add a large one (scale=0.14 so local size must compensate)
                foreach (var col in register.GetComponentsInChildren<Collider>(true))
                    col.enabled = false;
                var box = register.AddComponent<BoxCollider>();
                box.size = new Vector3(3f, 3f, 3f);

                // Set layer to match desk so game's InteractionManager raycast can hit it
                int deskLayer = deskTransform.gameObject.layer;
                register.layer = deskLayer;
                foreach (var child in register.GetComponentsInChildren<Transform>(true))
                    child.gameObject.layer = deskLayer;

                // Add InteractableObject as label-only prompt (withdrawal handled via Q key)
                var intObj = register.AddComponent<InteractableObject>();
                intObj.SetMessage("");
                intObj.MaxInteractionRange = 3f;
                intObj.Priority = 10; // higher than counter Storage so register wins when aimed at
                intObj.SetInteractableState(InteractableObject.EInteractableState.Disabled);
                _registerInteractable = intObj;

                _registerInstance = register;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"SpawnCashRegister failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Disables the StorageVisualizer component so stored products don't render
        /// on the invisible plastic table surface. BlockRefreshes prevents slot change
        /// callbacks from spawning new visuals, and existing visuals are destroyed.
        /// </summary>
        private static void DisableStorageVisualizer(GameObject go)
        {
            try
            {
                var visualizer = go.GetComponentInChildren<StorageVisualizer>(true);
                if (visualizer != null)
                {
                    // Prevent any future QueueRefresh calls from doing anything
                    visualizer.BlockRefreshes = true;
                    visualizer.enabled = false;

                    // Destroy all existing visual StoredItem GameObjects
                    try
                    {
#if IL2CPP
                        var activeItems = visualizer.activeStoredItems;
                        if (activeItems != null)
                        {
                            var enumerator = activeItems.GetEnumerator();
                            while (enumerator.MoveNext())
                            {
                                var list = enumerator.Current.Value;
                                if (list == null) continue;
                                for (int i = list.Count - 1; i >= 0; i--)
                                {
                                    if (list[i] != null)
                                        UnityEngine.Object.Destroy(list[i].gameObject);
                                }
                            }
                            enumerator.Dispose();
                            activeItems.Clear();
                        }
#else
                        // activeStoredItems is protected on Mono — destroy children of ItemContainer
                        var container = visualizer.ItemContainer ?? visualizer.transform;
                        for (int i = container.childCount - 1; i >= 0; i--)
                        {
                            var child = container.GetChild(i);
                            if (child != null)
                                UnityEngine.Object.Destroy(child.gameObject);
                        }
#endif
                    }
                    catch (Exception ex2)
                    {
                        OTCLog.Warning(OTCLog.Systems.Patch,$"Clearing activeStoredItems failed: {ex2.Message}");
                    }

                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,$"DisableStorageVisualizer failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Hooks into the counter's StorageEntity slot changes to update desk display visuals.
        /// </summary>
        private static void HookStorageDisplay(GameObject go)
        {
            try
            {
                _counterStorageEntity = go.GetComponentInChildren<StorageEntity>(true);
                if (_counterStorageEntity?.ItemSlots == null) return;

                for (int i = 0; i < _counterStorageEntity.ItemSlots.Count; i++)
                {
                    var slot = _counterStorageEntity.ItemSlots[i];
                    if (slot == null) continue;
#if IL2CPP
                    slot.onItemDataChanged += (Il2CppSystem.Action)RefreshDeskDisplay;
#else
                    slot.onItemDataChanged = (Action)Delegate.Combine(
                        slot.onItemDataChanged, new Action(RefreshDeskDisplay));
#endif
                }

            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,$"HookStorageDisplay failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Rebuilds the desk corner product display from current storage contents.
        /// Shows small product visuals near the cash register.
        /// </summary>
        public static void RefreshDeskDisplay()
        {
            // Clear existing display items
            foreach (var item in _deskDisplayItems)
            {
                if (item != null) UnityEngine.Object.Destroy(item);
            }
            _deskDisplayItems.Clear();

            if (_deskTransform == null || _counterStorageEntity?.ItemSlots == null) return;

            try
            {
                int displayIdx = 0;
                for (int i = 0; i < _counterStorageEntity.ItemSlots.Count && displayIdx < 6; i++)
                {
                    var slot = _counterStorageEntity.ItemSlots[i];
                    if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

#if IL2CPP
                    var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
#else
                    var productItem = slot.ItemInstance as ProductItemInstance;
#endif
                    if (productItem == null) continue;

                    // Get the StoredItem prefab for visuals
                    GameObject prefab = null;
                    ProductDefinition prodDef = null;
                    try
                    {
#if IL2CPP
                        prodDef = productItem.Definition?.TryCast<ProductDefinition>();
#else
                        prodDef = productItem.Definition as ProductDefinition;
#endif
                        var stored = productItem.StoredItem;
                        if (stored != null) prefab = stored.gameObject;
                    }
                    catch { }

                    // 3×2 grid in front of the register
                    int col = displayIdx % 3;
                    int row = displayIdx / 3;
                    float xPos = DisplayXBase + col * 0.11f;
                    float yPos = DisplayYBase + row * 0.14f;
                    float zPos = DisplayZPos;

                    GameObject displayGo;
                    if (prefab != null)
                    {
                        displayGo = UnityEngine.Object.Instantiate(prefab);
                        displayGo.name = $"OTC_DeskDisplay_{displayIdx}";
                        displayGo.transform.SetParent(_deskTransform, false);
                        displayGo.transform.localPosition = new Vector3(xPos, yPos, zPos);
                        displayGo.transform.localRotation = Quaternion.Euler(DisplayRotX, 0f, 0f);
                        displayGo.transform.localScale = Vector3.one * DisplayScale;

                        try
                        {
                            var multiVisuals = displayGo.GetComponentInChildren<MultiTypeVisualsSetter>();
                            if (multiVisuals != null && prodDef != null)
                                multiVisuals.ApplyVisuals(prodDef);
                            else
                            {
                                var setter = displayGo.GetComponentInChildren<ProductVisualsSetter>();
                                if (setter != null && prodDef != null)
                                    setter.ApplyVisuals(prodDef);
                            }
                        }
                        catch { }

                        // Disable all colliders and interaction
                        foreach (var c in displayGo.GetComponentsInChildren<Collider>(true))
                            c.enabled = false;
                    }
                    else
                    {
                        // Fallback: small colored cube
                        displayGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        displayGo.name = $"OTC_DeskDisplay_{displayIdx}";
                        displayGo.transform.SetParent(_deskTransform, false);
                        displayGo.transform.localPosition = new Vector3(xPos, yPos, zPos);
                        displayGo.transform.localScale = new Vector3(0.08f, 0.08f, 0.08f);
                        var col2 = displayGo.GetComponent<Collider>();
                        if (col2 != null) col2.enabled = false;
                    }

                    _deskDisplayItems.Add(displayGo);
                    displayIdx++;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,$"RefreshDeskDisplay failed: {ex.Message}");
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
            // Use reflection to avoid Registry.GetItem overload ambiguity on Mono
            var registryType = typeof(NativeItemInstance).Assembly.GetType("ScheduleOne.Registry");
            if (registryType == null)
            {
                OTCLog.Error(OTCLog.Systems.Patch,"Registry type not found");
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
            _checkoutInteractable = null;
            _registerInstance = null;
            _registerInteractable = null;
            _registerBalance = 0f;
            _deskTransform = null;
            _counterStorageEntity = null;
            foreach (var item in _deskDisplayItems)
            {
                if (item != null) UnityEngine.Object.Destroy(item);
            }
            _deskDisplayItems.Clear();
            ComputerScreen.Cleanup();
            // Don't clear _counterDef — it persists across scene loads
        }
    }
}
