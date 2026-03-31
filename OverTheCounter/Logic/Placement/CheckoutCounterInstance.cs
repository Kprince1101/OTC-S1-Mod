using MelonLoader;
using OverTheCounter.Utilities;
using S1MAPI.Gltf;
using S1MAPI.S1;
using S1MAPI.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.Product;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
using Grid = Il2CppScheduleOne.Tiles.Grid;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Interaction;
using ScheduleOne.Storage;
using ScheduleOne.Product;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
using Grid = ScheduleOne.Tiles.Grid;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Per-counter instance. Each placed checkout counter creates one of these
    /// to hold its own POS screen, register, storage display, queue, and lock state.
    /// </summary>
    public class CheckoutCounterInstance
    {
        /// <summary>The counter's GridItem GameObject.</summary>
        public GameObject CounterGameObject { get; }

        /// <summary>The Grid this counter is placed on (null if unknown).</summary>
        public Grid ParentGrid { get; }

        /// <summary>The building ID derived from the parent grid, or null.</summary>
        public string BuildingId => ParentGrid != null
            ? BuildingGridFactory.GetBuildingId(ParentGrid)
            : null;

        /// <summary>The counter's Transform.</summary>
        public Transform CounterTransform => CounterGameObject != null ? CounterGameObject.transform : null;

        /// <summary>World position of the counter.</summary>
        public Vector3? CounterPosition =>
            CounterGameObject != null ? CounterGameObject.transform.position : null;

        /// <summary>World position of the counter surface (top of desk).</summary>
        public Vector3? SurfacePosition =>
            CounterGameObject != null
                ? CounterGameObject.transform.position + Vector3.up * 0.6f
                : null;

        /// <summary>Position where a customer should stand to face the counter.</summary>
        public Vector3? CustomerStandPosition
        {
            get
            {
                if (CounterGameObject == null) return null;
                return CounterGameObject.transform.position - CounterGameObject.transform.forward * 1.0f;
            }
        }

        // Interactable
        private InteractableObject _checkoutInteractable;

        /// <summary>The counter's InteractableObject for line-of-sight checkout gating.</summary>
        public InteractableObject CheckoutInteractable => _checkoutInteractable;

        // Register
        private GameObject _registerInstance;
        private InteractableObject _registerInteractable;
        private float _registerBalance;

        /// <summary>The cash register's InteractableObject.</summary>
        public InteractableObject RegisterInteractable => _registerInteractable;

        /// <summary>World position of the cash register.</summary>
        public Vector3? RegisterPosition =>
            _registerInstance != null ? _registerInstance.transform.position : null;

        /// <summary>Accumulated cash balance in the register.</summary>
        public float RegisterBalance
        {
            get => _registerBalance;
            set => _registerBalance = value;
        }

        // Desk display
        private Transform _deskTransform;
        private GameObject _peripheralAnchor;
        private GameObject _taskLight;

        /// <summary>The desk mesh transform (for debug editor positioning).</summary>
        public Transform DeskTransform => _deskTransform;
        private readonly List<GameObject> _deskDisplayItems = new();
        private StorageEntity _counterStorageEntity;

        /// <summary>The counter's StorageEntity.</summary>
        public StorageEntity CounterStorageEntity => _counterStorageEntity;

        /// <summary>POS screen instance for this counter.</summary>
        public ComputerScreen Screen { get; private set; }

        /// <summary>Current desk style ID for this counter.</summary>
        public string CurrentDeskStyleId { get; internal set; } = DeskStyle.Default.Id;

        /// <summary>Customer IDs queued at this counter (index 0 = front).</summary>
        internal List<string> Queue { get; } = new();

        /// <summary>Cached BFS queue positions in local space. Null = not yet computed.</summary>
        internal List<Vector3> CachedQueueSlots;

        /// <summary>Invalidates cached queue positions (e.g. after furniture moved).</summary>
        internal void InvalidateQueueCache() => CachedQueueSlots = null;

        /// <summary>Steam ID of the player currently checking out at this counter, or empty.</summary>
        internal string LockHolder { get; set; } = "";

        // Desk corner product display layout offsets
        private const float DisplayXBase = -0.89f;
        private const float DisplayYBase = -0.38f;
        private const float DisplayZPos = 0.47f;
        private const float DisplayRotX = 90f;
        private const float DisplayScale = 0.5f;

        public CheckoutCounterInstance(GameObject go, Grid grid = null)
        {
            CounterGameObject = go;
            ParentGrid = grid;
        }

        // =================================================================
        //  Register operations
        // =================================================================

        /// <summary>Adds cash to the register balance.</summary>
        public void DepositToRegister(float amount) =>
            _registerBalance += amount;

        /// <summary>Withdraws all cash from register. Returns amount collected.</summary>
        public float CollectRegister()
        {
            float amount = _registerBalance;
            _registerBalance = 0f;
            return amount;
        }

        // =================================================================
        //  Visual setup (called from ApplyVisualDeferred coroutine)
        // =================================================================

        /// <summary>
        /// Replaces the plastic table mesh with the current desk style + peripherals.
        /// </summary>
        public void ApplyDeskVisual()
        {
            CheckoutCounter.DisableOriginalRenderers(CounterGameObject);
            CaptureInteractable();

            var style = DeskStyle.Get(CurrentDeskStyleId);
            SwapDesk(style);
        }

        /// <summary>
        /// Swaps the desk to a different style. Destroys old desk and re-creates
        /// all visuals with the new mesh.
        /// </summary>
        public void SwapDesk(DeskStyle style)
        {
            if (style == null) return;

            // Tear down existing desk visuals
            Screen?.Cleanup();
            Screen = null;
            foreach (var item in _deskDisplayItems)
            {
                if (item != null) UnityEngine.Object.Destroy(item);
            }
            _deskDisplayItems.Clear();
            if (_registerInstance != null)
            {
                UnityEngine.Object.Destroy(_registerInstance);
                _registerInstance = null;
            }
            _registerInteractable = null;
            if (_peripheralAnchor != null)
            {
                UnityEngine.Object.Destroy(_peripheralAnchor);
                _peripheralAnchor = null;
            }
            if (_taskLight != null)
            {
                UnityEngine.Object.Destroy(_taskLight);
                _taskLight = null;
            }
            if (_deskTransform != null)
            {
                UnityEngine.Object.Destroy(_deskTransform.gameObject);
                _deskTransform = null;
            }

            CurrentDeskStyleId = style.Id;

            // Rebuild with new style
            var go = CounterGameObject;
            if (go == null) return;

            GameObject desk = null;
            try
            {
                MeshVault.MeshVaultAPI.Init();
                var worldPos = go.transform.TransformPoint(Vector3.zero);
                var worldRot = go.transform.rotation * style.SpawnRotation;
                desk = MeshVault.MeshVaultAPI.Spawn(style.MeshVaultId, worldPos, worldRot, parent: go.transform);
                if (desk != null)
                {
                    desk.name = "OTC_Desk";
                    desk.transform.localPosition = style.SpawnOffset;
                    desk.transform.localRotation = style.SpawnRotation;
                    desk.transform.localScale = Vector3.one;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"MeshVault spawn failed for '{style.MeshVaultId}': {ex.Message}");
            }

            if (desk != null)
            {
                _deskTransform = desk.transform;

                // Peripheral anchor: fixed 270° X rotation so existing peripheral
                // positions (tuned in that coordinate space) stay correct regardless
                // of the desk style's SpawnRotation.
                _peripheralAnchor = new GameObject("OTC_PeripheralAnchor");
                _peripheralAnchor.transform.SetParent(go.transform, false);
                _peripheralAnchor.transform.localPosition = Vector3.zero;
                _peripheralAnchor.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);

                SpawnPeripherals(_peripheralAnchor.transform);
                SpawnCashRegister(_peripheralAnchor.transform);
                SpawnTaskLight(go.transform);
                DisableStorageVisualizer();
                HookStorageDisplay();
            }
            else
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"Desk mesh not found for style '{style.Id}'");
            }
        }

        /// <summary>
        /// Spawns computer, keyboard, mouse, mousepad on the desk.
        /// </summary>
        private void SpawnPeripherals(Transform deskTransform)
        {
            var computer = Meshes.Computer.Instantiate("OTC_Computer",
                new Vector3(0.63f, 0.22f, 0.55f),
                Quaternion.Euler(0f, 0f, 75f), deskTransform);
            if (computer != null)
            {
                Screen = new ComputerScreen(this);
                Screen.Create(computer);
            }

            var kbBase = Meshes.Keyboard.Instantiate("OTC_KeyboardBase",
                new Vector3(0.70f, -0.13f, 0.48f),
                Quaternion.Euler(0f, 0f, 75f), deskTransform);
            if (kbBase != null) kbBase.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);

            var keys = Meshes.Custom("Keys").Instantiate("OTC_Keys",
                new Vector3(0.56f, -0.11f, 0.47f),
                Quaternion.Euler(0f, 0f, 75f), deskTransform);
            if (keys != null) keys.transform.localScale = new Vector3(0.03f, 0.03f, 0.03f);

            Meshes.Custom("MousePad").Instantiate("OTC_MousePad",
                new Vector3(0.27f, -0.07f, 0.47f),
                Quaternion.Euler(0f, 0f, 75f), deskTransform);

            Meshes.Mouse.Instantiate("OTC_Mouse",
                new Vector3(0.27f, -0.07f, 0.47f),
                Quaternion.Euler(0f, 0f, 75f), deskTransform);
        }

        private void CaptureInteractable()
        {
            _checkoutInteractable = CounterGameObject.GetComponentInChildren<InteractableObject>(true);
            if (_checkoutInteractable != null)
            {
                _checkoutInteractable.SetMessage("Storage");
                _checkoutInteractable.MaxInteractionRange = 2f;
            }
        }

        private void SpawnCashRegister(Transform deskTransform)
        {
            try
            {
                byte[] glbData = EmbeddedResourceLoader.LoadBytes(
                    "OverTheCounter.Resources.CashRegister.glb",
                    Assembly.GetExecutingAssembly());
                if (glbData == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, "Could not load CashRegister.glb embedded resource");
                    return;
                }

                var register = GltfLoader.LoadGlb(glbData);
                if (register == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, "GltfLoader returned null for CashRegister.glb");
                    return;
                }

                register.name = "OTC_CashRegister";
                register.transform.SetParent(deskTransform, false);
                register.transform.localPosition = new Vector3(-0.74f, 0.06f, 0.49f);
                register.transform.localRotation = Quaternion.Euler(345f, 90f, 90f);
                register.transform.localScale = Vector3.one * 0.14f;

                foreach (var col in register.GetComponentsInChildren<Collider>(true))
                    col.enabled = false;
                var box = register.AddComponent<BoxCollider>();
                box.size = new Vector3(3f, 3f, 3f);

                int deskLayer = deskTransform.gameObject.layer;
                register.layer = deskLayer;
                foreach (var child in register.GetComponentsInChildren<Transform>(true))
                    child.gameObject.layer = deskLayer;

                var intObj = register.AddComponent<InteractableObject>();
                intObj.SetMessage("");
                intObj.MaxInteractionRange = 3f;
                intObj.Priority = 10;
                intObj.SetInteractableState(InteractableObject.EInteractableState.Disabled);
                _registerInteractable = intObj;

                _registerInstance = register;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"SpawnCashRegister failed: {ex.Message}");
            }
        }

        private void SpawnTaskLight(Transform counterRoot)
        {
            _taskLight = new GameObject("OTC_TaskLight");
            _taskLight.transform.SetParent(counterRoot, false);
            _taskLight.transform.localPosition = new Vector3(0f, 1.2f, 0f);

            var light = _taskLight.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.95f, 0.85f); // warm white
            light.range = 3f;
            light.intensity = 0.6f;
        }

        /// <summary>Toggles the counter's task light on or off (synced with building light switch).</summary>
        internal void SetTaskLightEnabled(bool enabled)
        {
            if (_taskLight != null)
                _taskLight.SetActive(enabled);
        }

        // =================================================================
        //  Storage display
        // =================================================================

        private void DisableStorageVisualizer()
        {
            try
            {
                var visualizer = CounterGameObject.GetComponentInChildren<StorageVisualizer>(true);
                if (visualizer != null)
                {
                    visualizer.BlockRefreshes = true;
                    visualizer.enabled = false;

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
                        OTCLog.Warning(OTCLog.Systems.Patch, $"Clearing activeStoredItems failed: {ex2.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"DisableStorageVisualizer failed: {ex.Message}");
            }
        }

        private void HookStorageDisplay()
        {
            try
            {
                _counterStorageEntity = CounterGameObject.GetComponentInChildren<StorageEntity>(true);
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
                OTCLog.Warning(OTCLog.Systems.Patch, $"HookStorageDisplay failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Rebuilds the desk corner product display from current storage contents.
        /// </summary>
        public void RefreshDeskDisplay()
        {
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

                    int col = displayIdx % 3;
                    int row = displayIdx / 3;
                    float xPos = DisplayXBase + col * 0.11f;
                    float yPos = DisplayYBase + row * 0.14f;

                    GameObject displayGo;
                    if (prefab != null)
                    {
                        displayGo = UnityEngine.Object.Instantiate(prefab);
                        displayGo.name = $"OTC_DeskDisplay_{displayIdx}";
                        displayGo.transform.SetParent(_deskTransform, false);
                        displayGo.transform.localPosition = new Vector3(xPos, yPos, DisplayZPos);
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

                            CheckoutProcess.StripVisualSetters(displayGo);
                        }
                        catch { }

                        foreach (var c in displayGo.GetComponentsInChildren<Collider>(true))
                            c.enabled = false;
                    }
                    else
                    {
                        displayGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        displayGo.name = $"OTC_DeskDisplay_{displayIdx}";
                        displayGo.transform.SetParent(_deskTransform, false);
                        displayGo.transform.localPosition = new Vector3(xPos, yPos, DisplayZPos);
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
                OTCLog.Warning(OTCLog.Systems.Patch, $"RefreshDeskDisplay failed: {ex.Message}");
            }
        }

        // =================================================================
        //  Cleanup
        // =================================================================

        /// <summary>Destroys all per-counter visual state.</summary>
        public void Cleanup()
        {
            Screen?.Cleanup();
            Screen = null;
            _checkoutInteractable = null;
            _registerInstance = null;
            _registerInteractable = null;
            _registerBalance = 0f;
            _peripheralAnchor = null;
            _deskTransform = null;
            _counterStorageEntity = null;
            foreach (var item in _deskDisplayItems)
            {
                if (item != null) UnityEngine.Object.Destroy(item);
            }
            _deskDisplayItems.Clear();
            Queue.Clear();
            LockHolder = "";
        }
    }
}
