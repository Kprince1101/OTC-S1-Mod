using MelonLoader;
using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using static OverTheCounter.Logic.CustomerInstance;
using S1API.Money;
using System;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne;
using Il2CppScheduleOne.Audio;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.ObjectScripts.Cash;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.VoiceOver;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
using NativeMoneyManager = Il2CppScheduleOne.Money.MoneyManager;
using NativeStorableItemDef = Il2CppScheduleOne.ItemFramework.StorableItemDefinition;
#else
using ScheduleOne;
using ScheduleOne.Audio;
using ScheduleOne.DevUtilities;
using ScheduleOne.Interaction;
using ScheduleOne.ObjectScripts.Cash;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Product;
using ScheduleOne.Storage;
using ScheduleOne.UI;
using ScheduleOne.VoiceOver;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
using NativeMoneyManager = ScheduleOne.Money.MoneyManager;
using NativeStorableItemDef = ScheduleOne.ItemFramework.StorableItemDefinition;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Manages the interactive checkout flow: camera pan, product scanning, payment collection.
    /// One checkout at a time — <see cref="Instance"/> is non-null while active.
    /// </summary>
    public class CheckoutProcess
    {
        private static readonly MelonLogger.Instance Logger = new("OTC:Checkout");

        /// <summary>Active checkout instance, or null when idle.</summary>
        public static CheckoutProcess Instance { get; private set; }

        /// <summary>ID of the customer being checked out.</summary>
        public string CustomerId => _customer?.Id;

        private enum State
        {
            CameraPanning,
            ProductsAppearing,
            WaitingForClicks,
            PaymentAppearing,
            WaitingForPayment,
            CameraReturning
        }

        private struct CheckoutItem
        {
            public GameObject Visual;
            public float Price;
            public string ProductName;
            public bool Scanned;
        }

        // Constants
        private static AudioClip _scanClip;       // cached "Very quick click" for scan beep
        private static GameObject _cashPrefab;     // cached cash StoredItem visual from Registry
        private const float CameraLerpTime = 0.25f;
        private const float CheckoutFOV = 40f;
        private const float CameraPanWait = 0.6f;
        private const float ProductSpawnInterval = 0.3f;
        private const float PostScanDelay = 0.3f;
        private const float CameraReturnWait = 0.6f;
        private const int MaxProducts = 3;
        private const float FallbackPriceMin = 20f;
        private const float FallbackPriceMax = 50f;

        // State
        private readonly CustomerInstance _customer;
        private State _state;
        private float _stateTimer;
        private readonly List<CheckoutItem> _items = new();
        private int _nextItemIndex;
        private float _totalPrice;
        private GameObject _paymentObject;

        // Product data collected at start
        private readonly List<CollectedProduct> _collectedProducts = new();

        private struct CollectedProduct
        {
            public string Name;
            public float Price;
            public string PackagingId;
            public GameObject VisualPrefab;
            public ProductDefinition ProductDef;
        }

        private CheckoutProcess(CustomerInstance customer)
        {
            _customer = customer;
        }

        // =================================================================
        //  Public API
        // =================================================================

        /// <summary>
        /// Checks if the player pressed F while looking at the checkout counter
        /// with a waiting customer. Called from Core.OnLateUpdate (host only).
        /// </summary>
        public static void TryStartCheckout()
        {
            if (Instance != null) return;
            if (GameInput.IsTyping) return;
            if (!Input.GetKeyDown(KeyCode.F)) return;

            // Player must be looking at the checkout counter (line-of-sight via InteractionManager)
            var counterInteractable = CheckoutCounter.CounterInteractable;
            if (counterInteractable == null) return;
            var hovered = Singleton<InteractionManager>.Instance?.HoveredInteractableObject;
            if (hovered != counterInteractable) return;

            // Find a customer waiting at checkout
            CustomerInstance waitingCustomer = null;
            foreach (var c in CustomerInstance.Active.Values)
            {
                if (c.State == CustomerState.CheckingOut && c.ArrivedAtDestination && c.CheckoutArrivalTime > 0f)
                {
                    waitingCustomer = c;
                    break;
                }
            }
            if (waitingCustomer == null) return;

            // Use the products the customer selected during browsing
            var products = CollectSelectedProducts(waitingCustomer.SelectedProducts);
            if (products.Count == 0)
            {
                Logger.Warning("No matching products found in display cabinets for checkout");
                return;
            }

            var process = new CheckoutProcess(waitingCustomer);
            process._collectedProducts.AddRange(products);
            process._state = State.CameraPanning;
            process._stateTimer = Time.time;

            Instance = process;
            ComputerScreen.HideCheckoutInfo();
            process.LockPlayerInput();
        }

        /// <summary>
        /// Ticks the checkout state machine. Called every frame from Core.OnLateUpdate.
        /// </summary>
        public void Tick()
        {
            // Safety: customer or counter gone → abort
            if (!_customer.IsValid || CheckoutCounter.CounterTransform == null)
            {
                Abort();
                return;
            }

            float elapsed = Time.time - _stateTimer;

            switch (_state)
            {
                case State.CameraPanning:
                    if (elapsed >= CameraPanWait)
                    {
                        _state = State.ProductsAppearing;
                        _stateTimer = Time.time;
                        _nextItemIndex = 0;
                    }
                    break;

                case State.ProductsAppearing:
                    TickProductSpawning(elapsed);
                    break;

                case State.WaitingForClicks:
                    TickClickDetection();
                    break;

                case State.PaymentAppearing:
                    if (elapsed >= PostScanDelay)
                    {
                        SpawnPaymentObject();
                        _state = State.WaitingForPayment;
                    }
                    break;

                case State.WaitingForPayment:
                    TickPaymentClick();
                    break;

                case State.CameraReturning:
                    if (elapsed >= CameraReturnWait)
                    {
                        CompleteCheckout();
                    }
                    break;
            }
        }

        // =================================================================
        //  State handlers
        // =================================================================

        private void TickProductSpawning(float elapsed)
        {
            // Spawn one product every interval
            int targetCount = Mathf.Min(
                Mathf.FloorToInt(elapsed / ProductSpawnInterval) + 1,
                _collectedProducts.Count);

            while (_nextItemIndex < targetCount)
            {
                SpawnProductVisual(_nextItemIndex);
                _nextItemIndex++;
            }

            if (_nextItemIndex >= _collectedProducts.Count)
            {
                _state = State.WaitingForClicks;
            }
        }

        private void TickClickDetection()
        {
            if (!Input.GetMouseButtonDown(0)) return;

            var cam = Camera.main;
            if (cam == null) return;

            var ray = cam.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, 10f)) return;

            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Scanned) continue;
                if (hit.collider.gameObject != item.Visual &&
                    !hit.collider.transform.IsChildOf(item.Visual.transform)) continue;

                // Scan this product
                item.Scanned = true;
                _items[i] = item;
                _totalPrice += item.Price;
                UnityEngine.Object.Destroy(item.Visual);
                PlayScanSound();
                break;
            }

            // Check if all scanned
            bool allScanned = true;
            for (int i = 0; i < _items.Count; i++)
            {
                if (!_items[i].Scanned) { allScanned = false; break; }
            }
            if (allScanned)
            {
                _state = State.PaymentAppearing;
                _stateTimer = Time.time;
            }
        }

        private void TickPaymentClick()
        {
            if (!Input.GetMouseButtonDown(0)) return;
            if (_paymentObject == null) return;

            var cam = Camera.main;
            if (cam == null) return;

            var ray = cam.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, 10f)) return;

            if (hit.collider.gameObject != _paymentObject &&
                !hit.collider.transform.IsChildOf(_paymentObject.transform)) return;

            // Process cash payment (playCashSound=true handles the ka-ching)
            try
            {
                Money.ChangeCashBalance(_totalPrice, true, true);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Payment failed: {ex.Message}");
            }

            UnityEngine.Object.Destroy(_paymentObject);
            _paymentObject = null;

            // Start camera return
            _state = State.CameraReturning;
            _stateTimer = Time.time;
            UnlockPlayerInput();
        }

        // =================================================================
        //  Visual spawning
        // =================================================================

        private void SpawnProductVisual(int index)
        {
            var product = _collectedProducts[index];
            var counterTransform = CheckoutCounter.CounterTransform;
            var surfacePos = CheckoutCounter.SurfacePosition.Value;

            // Stagger items on the left side of the desk (away from computer/keyboard)
            float offset = (index - (_collectedProducts.Count - 1) / 2f) * 0.4f;
            var spawnPos = surfacePos - counterTransform.right * 0.3f + counterTransform.right * offset;

            GameObject go;

            if (product.VisualPrefab != null)
            {
                // Instantiate the real game product visual (same prefab display cabinets use)
                go = UnityEngine.Object.Instantiate(product.VisualPrefab);
                go.name = $"OTC_CheckoutProduct_{index}";
                go.transform.position = spawnPos;
                go.transform.rotation = counterTransform.rotation;
                go.transform.localScale = Vector3.one;

                // Apply product-specific visuals (color/fill) — the prefab starts with VisualsContainer disabled.
                // FilledPackaging prefabs use MultiTypeVisualsSetter which routes to the correct
                // drug-type setter (Weed/Meth/Cocaine/Shroom) and activates VisualsContainer.
                try
                {
                    var multiVisuals = go.GetComponentInChildren<MultiTypeVisualsSetter>();
                    if (multiVisuals != null && product.ProductDef != null)
                    {
                        multiVisuals.ApplyVisuals(product.ProductDef);
                    }
                    else
                    {
                        // Fallback: unpackaged product prefab uses ProductVisualsSetter directly
                        var visualsSetter = go.GetComponentInChildren<ProductVisualsSetter>();
                        if (visualsSetter != null && product.ProductDef != null)
                        {
                            visualsSetter.ApplyVisuals(product.ProductDef);
                        }
                        else
                        {
                            Logger.Warning($"  No visual setter found on prefab for {product.Name}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"ApplyVisuals failed for {product.Name}: {ex.Message}");
                }

                // Tilt products toward the overhead camera so their front face is visible
                go.transform.rotation = counterTransform.rotation * Quaternion.Euler(-25f, 0f, 0f);

                // Disable all existing colliders (game pattern), add one for click detection
                foreach (var col in go.GetComponentsInChildren<Collider>())
                    col.enabled = false;
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(0.3f, 0.3f, 0.3f);
            }
            else
            {
                // Fallback to primitive cube
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"OTC_CheckoutProduct_{index}";
                go.transform.position = spawnPos;
                go.transform.localScale = GetProductScale(product.PackagingId);
                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null)
                    renderer.material.color = GetProductColor(product.PackagingId);
            }

            _items.Add(new CheckoutItem
            {
                Visual = go,
                Price = product.Price,
                ProductName = product.Name,
                Scanned = false
            });
        }

        private void SpawnPaymentObject()
        {
            var surfacePos = CheckoutCounter.SurfacePosition.Value;
            var counterTransform = CheckoutCounter.CounterTransform;

            // Position on the left side of desk (away from computer)
            var payPos = surfacePos - counterTransform.right * 0.3f;

            // Use real cash visual from game (same prefab display shelves use)
            CacheCashPrefab();

            GameObject go;
            if (_cashPrefab != null)
            {
                go = UnityEngine.Object.Instantiate(_cashPrefab);
                go.name = "OTC_CheckoutCash";
                go.transform.position = payPos;
                go.transform.localScale = Vector3.one;

                // The cash prefab has CashStackVisuals with bill/note child GOs disabled by default.
                // The game calls ShowAmount() in StoredItem_Cash.InitializeStoredItem → RefreshShownBills.
                // We skip InitializeStoredItem (no grid) and activate visuals directly.
                var cashVisuals = go.GetComponentInChildren<CashStackVisuals>();
                if (cashVisuals != null)
                {
                    cashVisuals.ShowAmount(_totalPrice);
                }
                else
                {
                    Logger.Warning("  CashStackVisuals component not found on cash prefab");
                }

                // Disable existing colliders, add one for click detection
                foreach (var col in go.GetComponentsInChildren<Collider>())
                    col.enabled = false;
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(0.3f, 0.3f, 0.3f);
            }
            else
            {
                // Fallback: green cube with Unlit shader
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "OTC_CheckoutCash";
                go.transform.position = payPos;
                go.transform.localScale = new Vector3(0.25f, 0.08f, 0.15f);
                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    var unlitShader = Shader.Find("Unlit/Color");
                    if (unlitShader != null)
                        renderer.material = new Material(unlitShader);
                    renderer.material.color = new Color(0.1f, 0.9f, 0.1f);
                }
            }

            _paymentObject = go;
        }

        private static void CacheCashPrefab()
        {
            if (_cashPrefab != null) return;
            try
            {
#if IL2CPP
                var def = Registry.GetItem("cash")?.TryCast<NativeStorableItemDef>();
#else
                var def = GetRegistryItem("cash") as NativeStorableItemDef;
#endif
                if (def?.StoredItem != null)
                {
                    _cashPrefab = def.StoredItem.gameObject;
                }
                else
                {
                    Logger.Warning("Cash definition or StoredItem not found in Registry");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"CacheCashPrefab failed: {ex.Message}");
            }
        }

#if !IL2CPP
        /// <summary>
        /// Mono helper: avoids Registry.GetItem overload ambiguity via reflection.
        /// </summary>
        private static object GetRegistryItem(string itemId)
        {
            var registryType = typeof(NativeStorableItemDef).Assembly.GetType("ScheduleOne.Registry");
            if (registryType == null) return null;
            foreach (var m in registryType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (m.Name != "GetItem" || m.IsGenericMethod) continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                    return m.Invoke(null, new object[] { itemId });
            }
            return null;
        }
#endif

        private static Vector3 GetProductScale(string packagingId)
        {
            if (packagingId == null) return new Vector3(0.2f, 0.15f, 0.2f);

            return packagingId.ToLower() switch
            {
                "baggie" => new Vector3(0.25f, 0.06f, 0.18f),
                "jar" => new Vector3(0.15f, 0.25f, 0.15f),
                "brick" => new Vector3(0.35f, 0.15f, 0.25f),
                _ => new Vector3(0.2f, 0.15f, 0.2f)
            };
        }

        private static Color GetProductColor(string packagingId)
        {
            if (packagingId == null) return new Color(0.8f, 0.8f, 0.75f);

            return packagingId.ToLower() switch
            {
                "baggie" => new Color(0.95f, 0.95f, 0.9f),   // off-white
                "jar" => new Color(0.6f, 0.85f, 0.6f),        // green tint
                "brick" => new Color(0.7f, 0.55f, 0.35f),     // brown
                _ => new Color(0.8f, 0.8f, 0.75f)
            };
        }

        // =================================================================
        //  Product collection from display cabinets
        // =================================================================

        /// <summary>
        /// Finds the specific products the customer selected during browsing in the
        /// display cabinets and removes them from storage. Falls back to random collection
        /// if the customer has no selections.
        /// </summary>
        private static List<CollectedProduct> CollectSelectedProducts(List<SelectedProduct> selections)
        {
            var result = new List<CollectedProduct>();
            if (selections == null || selections.Count == 0) return result;

            try
            {
                // Build a flat list of all storage slots with packaged products
                var slots = new List<(StorageEntity storage, int slotIndex, ProductItemInstance productItem, ProductDefinition prodDef)>();

                foreach (var kvp in BuildingGridFactory.GridContainers)
                {
                    var root = kvp.Value;
                    if (root == null) continue;

                    var storages = root.GetComponentsInChildren<StorageEntity>(true);
                    if (storages == null) continue;

                    for (int i = 0; i < storages.Length; i++)
                    {
                        var storage = storages[i];
                        if (storage?.ItemSlots == null) continue;

                        for (int j = 0; j < storage.ItemSlots.Count; j++)
                        {
                            var slot = storage.ItemSlots[j];
                            if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

#if IL2CPP
                            var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
#else
                            var productItem = slot.ItemInstance as ProductItemInstance;
#endif
                            if (productItem == null || productItem.AppliedPackaging == null) continue;

                            ProductDefinition prodDef = null;
                            try
                            {
#if IL2CPP
                                prodDef = productItem.Definition?.TryCast<ProductDefinition>();
#else
                                prodDef = productItem.Definition as ProductDefinition;
#endif
                            }
                            catch { }

                            slots.Add((storage, j, productItem, prodDef));
                        }
                    }
                }

                // For each selected product, find a matching slot and collect it
                foreach (var selection in selections)
                {
                    for (int s = 0; s < slots.Count; s++)
                    {
                        var (storage, slotIndex, productItem, prodDef) = slots[s];
                        string slotProductId = prodDef?.ID;
                        string slotPackagingId = productItem.AppliedPackaging?.ID;

                        if (slotProductId != selection.ProductId || slotPackagingId != selection.PackagingId)
                            continue;

                        // Match found — collect it
                        float price = selection.Price;
                        if (price <= 0) price = UnityEngine.Random.Range(FallbackPriceMin, FallbackPriceMax);

                        GameObject visualPrefab = null;
                        try
                        {
                            var storedItemComponent = productItem.StoredItem;
                            if (storedItemComponent != null)
                                visualPrefab = storedItemComponent.gameObject;
                        }
                        catch { }

                        result.Add(new CollectedProduct
                        {
                            Name = selection.ProductName,
                            Price = price,
                            PackagingId = slotPackagingId,
                            VisualPrefab = visualPrefab,
                            ProductDef = prodDef
                        });

                        // Remove from storage
                        try
                        {
                            var slot = storage.ItemSlots[slotIndex];
                            if (slot.Quantity <= 1)
                                slot.SetStoredItem(null);
                            else
                                slot.SetStoredItem(slot.ItemInstance);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Failed to remove product from slot: {ex.Message}");
                        }

                        // Remove this slot from candidates so it's not reused
                        slots.RemoveAt(s);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"CollectSelectedProducts failed: {ex.Message}");
            }

            return result;
        }

        // =================================================================
        //  Camera & input control
        // =================================================================

        private void LockPlayerInput()
        {
            try
            {
                var counterTransform = CheckoutCounter.CounterTransform;
                var counterPos = CheckoutCounter.CounterPosition.Value;

                // Camera: higher and closer with lower FOV for a tighter framing
                var surfaceCenter = counterPos + Vector3.up * 0.6f - counterTransform.right * 0.2f;
                var overheadPos = surfaceCenter + Vector3.up * 1.4f + counterTransform.forward * 0.6f;
                var lookDir = (surfaceCenter - overheadPos).normalized;
                var overheadRot = Quaternion.LookRotation(lookDir);

                var cam = PlayerSingleton<PlayerCamera>.Instance;
                cam.AddActiveUIElement("OTC_Checkout");
                cam.OverrideTransform(overheadPos, overheadRot, CameraLerpTime, false);
                cam.OverrideFOV(CheckoutFOV, CameraLerpTime);
                cam.FreeMouse();

                PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
                Singleton<HUD>.Instance.canvas.enabled = false;

                PlayCustomerVoice(EVOLineType.Greeting);
            }
            catch (Exception ex)
            {
                Logger.Warning($"LockPlayerInput failed: {ex.Message}");
            }
        }

        private static void UnlockPlayerInput()
        {
            try
            {
                var cam = PlayerSingleton<PlayerCamera>.Instance;
                cam.RemoveActiveUIElement("OTC_Checkout");
                cam.StopFOVOverride(CameraLerpTime);
                cam.StopTransformOverride(CameraLerpTime, true, true);
                cam.LockMouse();

                PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
                Singleton<HUD>.Instance.canvas.enabled = true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"UnlockPlayerInput failed: {ex.Message}");
            }
        }

        // =================================================================
        //  Audio
        // =================================================================

        private static void PlayScanSound()
        {
            try
            {
                if (_scanClip == null) CacheScanClip();
                if (_scanClip == null) return;

                var counterPos = CheckoutCounter.CounterPosition;
                if (!counterPos.HasValue) return;

                AudioSource.PlayClipAtPoint(_scanClip, counterPos.Value, 0.5f);
            }
            catch { }
        }

        private static void CacheScanClip()
        {
            try
            {
                foreach (var src in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                {
                    if (src?.clip != null && src.clip.name == "Very quick click")
                    {
                        _scanClip = src.clip;
                        return;
                    }
                }
                Logger.Warning("Could not find 'Very quick click' audio clip for scan sound");
            }
            catch { }
        }

        private void PlayCustomerVoice(EVOLineType lineType)
        {
            try
            {
                var emitter = _customer.GameNpc?.VoiceOverEmitter;
                if (emitter == null)
                {
                    Logger.Warning($"No VoiceOverEmitter on customer NPC for {lineType}");
                    return;
                }
                emitter.Play(lineType);
            }
            catch (Exception ex)
            {
                Logger.Warning($"PlayCustomerVoice({lineType}) failed: {ex.Message}");
            }
        }

        // =================================================================
        //  Completion & cleanup
        // =================================================================

        private void CompleteCheckout()
        {
            PlayCustomerVoice(EVOLineType.Thanks);

            // Signal customer to exit
            _customer.CheckoutArrivalTime = 0f;
            _customer.ArrivedAtDestination = false;
            _customer.State = CustomerState.ExitingStore;
            _customer.SetAvoidancePriority(10);
            _customer.WalkTo(CustomerSpawnPoints.RampBottomPosition);

            CustomerManager.Instance?.OnCheckoutComplete(_customer.Id);
            Cleanup();
        }

        private void Abort()
        {
            Logger.Warning("Checkout aborted — customer or counter invalid");
            UnlockPlayerInput();

            // If customer is still valid, let them exit
            if (_customer.IsValid)
            {
                _customer.CheckoutArrivalTime = 0f;
                _customer.ArrivedAtDestination = false;
                _customer.State = CustomerState.ExitingStore;
                _customer.SetAvoidancePriority(10);
                _customer.WalkTo(CustomerSpawnPoints.RampBottomPosition);
            }

            CustomerManager.Instance?.OnCheckoutComplete(_customer.Id);
            Cleanup();
        }

        private void Cleanup()
        {
            foreach (var item in _items)
            {
                if (item.Visual != null)
                    UnityEngine.Object.Destroy(item.Visual);
            }
            _items.Clear();

            if (_paymentObject != null)
            {
                UnityEngine.Object.Destroy(_paymentObject);
                _paymentObject = null;
            }

            Instance = null;
        }
    }
}
