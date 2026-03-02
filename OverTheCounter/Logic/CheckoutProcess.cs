using MelonLoader;
using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using static OverTheCounter.Logic.CustomerInstance;
using S1API.Money;
using System;
using System.Collections;
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
using ItemSlot = Il2CppScheduleOne.ItemFramework.ItemSlot;
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
using ItemSlot = ScheduleOne.ItemFramework.ItemSlot;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Manages the interactive budtending checkout flow:
    /// camera pan → sprite selection → product placement → customer pickup → cash to register.
    /// One checkout at a time — <see cref="Instance"/> is non-null while active or paused.
    /// </summary>
    public class CheckoutProcess
    {
        private static readonly MelonLogger.Instance Logger = new("OTC:Checkout");

        /// <summary>Active checkout instance, or null when idle.</summary>
        public static CheckoutProcess Instance { get; private set; }

        /// <summary>ID of the customer being checked out.</summary>
        public string CustomerId => _customer?.Id;

        /// <summary>Whether the checkout is in a paused (backed-out) state.</summary>
        public bool IsPaused => _state == State.Paused;

        private enum State
        {
            CameraPanning,
            WaitingForPlacement,
            CustomerPickup,
            PaymentAppearing,
            WaitingForPayment,
            CashFlying,
            CameraReturning,
            Paused
        }

        /// <summary>A product that has been placed on the counter during budtending.</summary>
        private struct CounterProduct
        {
            public GameObject Visual;
            public string ProductId;
            public string PackagingId;
            public string ProductName;
            public float Price;
            public int QualityLevel;
            public ItemSlot SourceSlot;     // storage slot to consume from (null if from player inventory)
            public int HotbarIndex;         // player hotbar index (-1 if from storage)
        }

        /// <summary>A product found in storage/inventory, ready to be placed via sprite click.</summary>
        private struct AvailableProduct
        {
            public string ProductId;
            public string PackagingId;
            public string ProductName;
            public float Price;
            public int QualityLevel;
            public GameObject VisualPrefab;
            public ProductDefinition ProductDef;
            public ItemSlot SourceSlot;     // storage slot to consume from (null if from player inventory)
            public int HotbarIndex;         // player hotbar index (-1 if from storage)
        }

        // Constants
        private static AudioClip _scanClip;
        private static GameObject _cashPrefab;
        private const float CameraLerpTime = 0.25f;
        private const float DefaultFOV = 40f;
        private const float CameraPanWait = 0.6f;
        private const float PostPickupDelay = 0.5f;
        private const float PostScanDelay = 0.3f;
        private const float CameraReturnWait = 0.6f;
        private const float CashFlyDuration = 0.8f;
        private const int MaxProducts = 6;

        // Overhead camera offsets (finalized via runtime editor)
        private const float CamRightOffset = -0.05f;
        private const float CamUpOffset = 0.6f;
        private const float CamHeight = 1.1f;
        private const float CamForwardOffset = 1.15f;
        private const float CamFOV = DefaultFOV;

        // Product spawn offsets (finalized via runtime editor)
        private const float ProductRightOffset = -0.20f;
        private const float ProductUpOffset = -0.14f;
        private const float ProductForwardOffset = 0.0f;
        private const float ProductRotX = 0f;

        // State
        private readonly CustomerInstance _customer;
        private State _state;
        private float _stateTimer;
        private float _totalPlacedPrice;
        private GameObject _paymentObject;

        // Products on the counter (placed by player)
        private readonly List<CounterProduct> _counterProducts = new();

        // Products available for placement (from storage/inventory search)
        private readonly List<AvailableProduct> _availableProducts = new();

        // Tracking which requested products are missing (not found anywhere)
        private readonly HashSet<string> _missingProductKeys = new();

        // Tracking which requested products have been placed on counter
        private readonly HashSet<string> _placedProductKeys = new();

        // Whether the "Complete Sale" button has been shown for a partial order
        private bool _completeBtnShown;

        // Animation coroutines
        private object _cashFlyCoroutine;
        private object _pickupAnimCoroutine;

        private CheckoutProcess(CustomerInstance customer)
        {
            _customer = customer;
        }

        // =================================================================
        //  Public API
        // =================================================================

        /// <summary>
        /// Checks if the player pressed R while looking at the checkout counter
        /// with a waiting customer. Handles both fresh start and resume from pause.
        /// Called from Core.OnLateUpdate (host only).
        /// </summary>
        public static void TryStartCheckout()
        {
            if (GameInput.IsTyping) return;
            if (!Input.GetKeyDown(KeyCode.R)) return;

            // Resume from pause
            if (Instance != null && Instance._state == State.Paused)
            {
                Instance.ResumeFromPause();
                return;
            }

            if (Instance != null) return;

            // Player must be looking at the checkout counter
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

            var process = new CheckoutProcess(waitingCustomer);
            Instance = process;

            process.SearchAndShowAvailable();

            process._state = State.CameraPanning;
            process._stateTimer = Time.time;
            ComputerScreen.HideCheckoutInfo();
            process.LockPlayerInput();
        }

        /// <summary>
        /// Checks for right-click on counter products while checkout is paused.
        /// Returns product to player inventory. Called from Core.OnLateUpdate.
        /// </summary>
        public static void TryPickupCounterProduct()
        {
            if (Instance == null || Instance._state != State.Paused) return;
            if (!Input.GetMouseButtonDown(1)) return;

            var cam = Camera.main;
            if (cam == null) return;
            var ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f));

            // RaycastAll so we can hit products sitting on top of the desk
            var hits = Physics.RaycastAll(ray, 5f);
            if (hits.Length == 0) return;

            for (int h = 0; h < hits.Length; h++)
            {
                var hitGo = hits[h].collider.gameObject;
                for (int i = Instance._counterProducts.Count - 1; i >= 0; i--)
                {
                    var product = Instance._counterProducts[i];
                    if (product.Visual == null) continue;
                    if (hitGo != product.Visual &&
                        !hits[h].collider.transform.IsChildOf(product.Visual.transform)) continue;

                    // Try to return the product to player inventory or counter storage
                    if (!ReturnProduct(product))
                        return;

                    // Remove from counter
                    UnityEngine.Object.Destroy(product.Visual);
                    Instance._counterProducts.RemoveAt(i);
                    Instance._totalPlacedPrice -= product.Price;
                    Instance._placedProductKeys.Remove($"{product.ProductId}:{product.PackagingId}");

                    // Update POS display
                    ComputerScreen.ShowBudtendingStatus(
                        Instance._customer.SelectedProducts,
                        Instance._missingProductKeys,
                        Instance._placedProductKeys,
                        Instance._totalPlacedPrice);
                    return;
                }
            }
        }

        /// <summary>
        /// Ticks the checkout state machine. Called every frame from Core.OnLateUpdate.
        /// </summary>
        public void Tick()
        {
            if (_state == State.Paused) return;

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
                        _state = State.WaitingForPlacement;
                        ShowSpriteHUD();
                        UpdatePOSForBudtending();
                    }
                    break;

                case State.WaitingForPlacement:
                    TickPlacement();
                    break;

                case State.CustomerPickup:
                    // Coroutine handles shrink+fly animation → transitions to PaymentAppearing
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

                case State.CashFlying:
                    // Coroutine handles transition to CameraReturning
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
        //  Placement state
        // =================================================================

        private void TickPlacement()
        {
            // Check for back-out keys (R, Tab)
            if (Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.Tab))
            {
                PauseCheckout();
                return;
            }

            // Right-click to remove a product already placed on the counter
            if (Input.GetMouseButtonDown(1))
            {
                TryRemovePlacedProduct();
                return;
            }

            // Delegate click detection to the HUD
            BudtenderHUD.TickClickDetection();

            // Check if all sprites have been placed (or there were none to begin with)
            bool allPlaced = BudtenderHUD.AllPlaced() || _availableProducts.Count == 0;
            if (allPlaced)
            {
                if (_missingProductKeys.Count == 0)
                {
                    // Full order — auto-transition
                    BudtenderHUD.Hide();
                    TransitionToCustomerPickup();
                }
                else if (_completeBtnShown == false)
                {
                    // Partial or empty order — show "Complete Sale" button
                    _completeBtnShown = true;
                    BudtenderHUD.ShowCompleteButton(OnCompleteSaleClicked);
                }
            }
        }

        /// <summary>Called when the player clicks "Complete Sale" for a partial or empty order.</summary>
        private void OnCompleteSaleClicked()
        {
            BudtenderHUD.Hide();

            // If nothing was placed at all, skip pickup/payment — just return camera and let customer leave
            if (_counterProducts.Count == 0)
            {
                PlayCustomerVoice(EVOLineType.Angry);
                _state = State.CameraReturning;
                _stateTimer = Time.time;
                UnlockPlayerInput();
                return;
            }

            TransitionToCustomerPickup();
        }

        /// <summary>
        /// Removes a placed product from the counter via right-click during WaitingForPlacement.
        /// Uses mouse position raycast (overhead camera).
        /// </summary>
        private void TryRemovePlacedProduct()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var ray = cam.ScreenPointToRay(Input.mousePosition);

            var hits = Physics.RaycastAll(ray, 20f);
            if (hits.Length == 0) return;

            for (int h = 0; h < hits.Length; h++)
            {
                var hitGo = hits[h].collider.gameObject;
                for (int i = _counterProducts.Count - 1; i >= 0; i--)
                {
                    var product = _counterProducts[i];
                    if (product.Visual == null) continue;
                    if (hitGo != product.Visual &&
                        !hits[h].collider.transform.IsChildOf(product.Visual.transform)) continue;

                    if (!ReturnProduct(product))
                        return;

                    UnityEngine.Object.Destroy(product.Visual);
                    _counterProducts.RemoveAt(i);
                    _totalPlacedPrice -= product.Price;
                    _placedProductKeys.Remove($"{product.ProductId}:{product.PackagingId}");

                    // Re-search available products and refresh HUD
                    SearchAndShowAvailable();
                    ShowSpriteHUD();
                    UpdatePOSForBudtending();
                    return;
                }
            }
        }

        /// <summary>Called by BudtenderHUD when the player clicks a sprite slot.</summary>
        private void OnSpriteClicked(int index)
        {
            if (index < 0 || index >= _availableProducts.Count) return;
            var available = _availableProducts[index];

            // Spawn 3D visual on counter and consume from source immediately
            SpawnProductOnCounter(available);
            ConsumeFromSource(available);

            // Track placement
            _totalPlacedPrice += available.Price;
            _placedProductKeys.Add($"{available.ProductId}:{available.PackagingId}");

            // Remove sprite from HUD
            BudtenderHUD.RemoveItem(index);
            PlayScanSound();

            // Update POS display
            UpdatePOSForBudtending();
        }

        private void SpawnProductOnCounter(AvailableProduct product)
        {
            var counterTransform = CheckoutCounter.CounterTransform;
            var surfacePos = CheckoutCounter.SurfacePosition.Value;

            // 2×2 grid layout on counter
            int idx = _counterProducts.Count;
            int col = idx % 2;
            int row = idx / 2;
            float xOffset = (col - 0.5f) * 0.3f;
            float zOffset = (row - 0.5f) * 0.25f;
            var spawnPos = surfacePos
                + counterTransform.right * ProductRightOffset
                + counterTransform.right * xOffset
                + counterTransform.forward * ProductForwardOffset
                + counterTransform.forward * zOffset
                + Vector3.up * ProductUpOffset;

            GameObject go;

            if (product.VisualPrefab != null)
            {
                go = UnityEngine.Object.Instantiate(product.VisualPrefab);
                go.name = $"OTC_CounterProduct_{idx}";
                go.transform.position = spawnPos;
                go.transform.rotation = counterTransform.rotation;
                go.transform.localScale = Vector3.one;

                try
                {
                    var multiVisuals = go.GetComponentInChildren<MultiTypeVisualsSetter>();
                    if (multiVisuals != null && product.ProductDef != null)
                    {
                        multiVisuals.ApplyVisuals(product.ProductDef);
                    }
                    else
                    {
                        var visualsSetter = go.GetComponentInChildren<ProductVisualsSetter>();
                        if (visualsSetter != null && product.ProductDef != null)
                            visualsSetter.ApplyVisuals(product.ProductDef);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"ApplyVisuals failed for {product.ProductName}: {ex.Message}");
                }

                go.transform.rotation = counterTransform.rotation * Quaternion.Euler(ProductRotX, 0f, 0f);

                foreach (var c in go.GetComponentsInChildren<Collider>())
                    c.enabled = false;
                go.layer = 0; // Default layer for raycast pickup
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(0.3f, 0.3f, 0.3f);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"OTC_CounterProduct_{idx}";
                go.transform.position = spawnPos;
                go.transform.localScale = GetProductScale(product.PackagingId);
                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null)
                    renderer.material.color = GetProductColor(product.PackagingId);
            }

            _counterProducts.Add(new CounterProduct
            {
                Visual = go,
                ProductId = product.ProductId,
                PackagingId = product.PackagingId,
                ProductName = product.ProductName,
                Price = product.Price,
                QualityLevel = product.QualityLevel,
                SourceSlot = product.SourceSlot,
                HotbarIndex = product.HotbarIndex
            });
        }

        // =================================================================
        //  Pause / Resume
        // =================================================================

        private void PauseCheckout()
        {
            _state = State.Paused;
            BudtenderHUD.Hide();
            UnlockPlayerInput();

            // Show POS with current status (resume prompt)
            ComputerScreen.ShowBudtendingStatus(
                _customer.SelectedProducts,
                _missingProductKeys,
                _placedProductKeys,
                _totalPlacedPrice);
        }

        private void ResumeFromPause()
        {
            // Player must be looking at the counter
            var counterInteractable = CheckoutCounter.CounterInteractable;
            if (counterInteractable == null) return;
            var hovered = Singleton<InteractionManager>.Instance?.HoveredInteractableObject;
            if (hovered != counterInteractable) return;

            // Re-scan sources for remaining unfulfilled items
            SearchAndShowAvailable();

            _state = State.CameraPanning;
            _stateTimer = Time.time;
            ComputerScreen.HideCheckoutInfo();
            LockPlayerInput();
        }

        // =================================================================
        //  Customer pickup → payment → cash fly
        // =================================================================

        private void TransitionToCustomerPickup()
        {
            // Customer plays GrabItem animation
            try
            {
                _customer.GameNpc?.SendAnimationTrigger("GrabItem");
            }
            catch (Exception ex)
            {
                Logger.Warning($"GrabItem animation failed: {ex.Message}");
            }

            _state = State.CustomerPickup;
            _stateTimer = Time.time;

            // Start shrink+travel animation toward NPC
            _pickupAnimCoroutine = MelonCoroutines.Start(ProductPickupAnimation());
        }

        /// <summary>
        /// Animates all counter products shrinking and traveling toward the customer,
        /// then transitions to PaymentAppearing.
        /// </summary>
        private IEnumerator ProductPickupAnimation()
        {
            const float duration = 0.6f;

            // Target: customer chest area
            var customerTransform = _customer.GameNpc?.transform;
            var targetPos = customerTransform != null
                ? customerTransform.position + Vector3.up * 0.8f
                : CheckoutCounter.SurfacePosition ?? Vector3.zero;

            // Capture starting state of each product
            var startPositions = new Vector3[_counterProducts.Count];
            var startScales = new Vector3[_counterProducts.Count];
            for (int i = 0; i < _counterProducts.Count; i++)
            {
                var visual = _counterProducts[i].Visual;
                if (visual != null)
                {
                    startPositions[i] = visual.transform.position;
                    startScales[i] = visual.transform.localScale;
                }
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float smoothT = t * t * (3f - 2f * t); // smoothstep

                // Update target each frame in case NPC moves
                if (customerTransform != null)
                    targetPos = customerTransform.position + Vector3.up * 0.8f;

                for (int i = 0; i < _counterProducts.Count; i++)
                {
                    var visual = _counterProducts[i].Visual;
                    if (visual == null) continue;
                    visual.transform.position = Vector3.Lerp(startPositions[i], targetPos, smoothT);
                    visual.transform.localScale = Vector3.Lerp(startScales[i], Vector3.zero, smoothT);
                }

                yield return null;
            }

            // Destroy all product visuals
            foreach (var p in _counterProducts)
            {
                if (p.Visual != null)
                    UnityEngine.Object.Destroy(p.Visual);
            }

            _pickupAnimCoroutine = null;
            _state = State.PaymentAppearing;
            _stateTimer = Time.time;
        }

        private void TickPaymentClick()
        {
            if (!Input.GetMouseButtonDown(0)) return;
            if (_paymentObject == null) return;

            // Any click collects the cash — there's nothing else to interact with
            _state = State.CashFlying;
            _cashFlyCoroutine = MelonCoroutines.Start(CashFlyCoroutine());
        }

        private IEnumerator CashFlyCoroutine()
        {
            var registerPos = CheckoutCounter.RegisterPosition;
            if (_paymentObject == null || !registerPos.HasValue)
            {
                // No register — fall back to direct deposit
                CheckoutCounter.DepositToRegister(_totalPlacedPrice);
                if (_paymentObject != null)
                    UnityEngine.Object.Destroy(_paymentObject);
                _paymentObject = null;
                _state = State.CameraReturning;
                _stateTimer = Time.time;
                UnlockPlayerInput();
                yield break;
            }

            var startPos = _paymentObject.transform.position;
            var endPos = registerPos.Value + Vector3.up * 0.15f;
            float elapsed = 0f;

            while (elapsed < CashFlyDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / CashFlyDuration);
                float smoothT = t * t * (3f - 2f * t); // smoothstep

                // Arc upward
                float arc = Mathf.Sin(t * Mathf.PI) * 0.4f;
                var pos = Vector3.Lerp(startPos, endPos, smoothT) + Vector3.up * arc;

                // Spin
                float spin = t * 720f;

                if (_paymentObject != null)
                {
                    _paymentObject.transform.position = pos;
                    _paymentObject.transform.rotation = Quaternion.Euler(0f, spin, 0f);
                }
                yield return null;
            }

            // Deposit and cleanup
            CheckoutCounter.DepositToRegister(_totalPlacedPrice);
            PlayCashSound();

            if (_paymentObject != null)
                UnityEngine.Object.Destroy(_paymentObject);
            _paymentObject = null;

            _state = State.CameraReturning;
            _stateTimer = Time.time;
            UnlockPlayerInput();
        }

        // =================================================================
        //  Product source search
        // =================================================================

        /// <summary>
        /// Searches counter storage then player inventory for requested products.
        /// Populates _availableProducts and _missingProductKeys.
        /// Accounts for products already on the counter from previous placement.
        /// </summary>
        private void SearchAndShowAvailable()
        {
            _availableProducts.Clear();
            _missingProductKeys.Clear();

            var requested = _customer.SelectedProducts;
            if (requested == null || requested.Count == 0)
                return;

            try
            {
                // Build lookup of already-placed quantities
                var placedCounts = new Dictionary<string, int>();
                foreach (var cp in _counterProducts)
                {
                    string key = $"{cp.ProductId}:{cp.PackagingId}";
                    placedCounts.TryGetValue(key, out int count);
                    placedCounts[key] = count + 1;
                }

                // Get counter storage
                StorageEntity counterStorage = null;
                if (CheckoutCounter.CounterTransform != null)
                {
                    counterStorage = CheckoutCounter.CounterTransform
                        .GetComponentInChildren<StorageEntity>(true);
                }

                foreach (var selection in requested)
                {
                    string key = $"{selection.ProductId}:{selection.PackagingId}";
                    int qty = selection.Quantity > 0 ? selection.Quantity : 1;

                    // Subtract already-placed count
                    placedCounts.TryGetValue(key, out int alreadyPlaced);
                    int remaining = qty - alreadyPlaced;

                    if (remaining <= 0) continue;

                    int found = 0;

                    // Search counter storage first
                    if (counterStorage?.ItemSlots != null)
                    {
                        found += SearchStorage(counterStorage, selection, remaining - found);
                    }

                    // Then search player inventory
                    if (found < remaining)
                    {
                        found += SearchPlayerInventory(selection, remaining - found);
                    }

                    if (found < remaining)
                        _missingProductKeys.Add(key);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"SearchAndShowAvailable failed: {ex.Message}");
            }
        }

        /// <summary>Searches a StorageEntity for matching products. Returns count found.</summary>
        private int SearchStorage(StorageEntity storage, SelectedProduct selection, int maxNeeded)
        {
            if (storage?.ItemSlots == null || maxNeeded <= 0) return 0;
            int found = 0;

            for (int j = 0; j < storage.ItemSlots.Count && found < maxNeeded; j++)
            {
                var slot = storage.ItemSlots[j];
                if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

#if IL2CPP
                var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
#else
                var productItem = slot.ItemInstance as ProductItemInstance;
#endif
                if (productItem?.AppliedPackaging == null) continue;

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

                string slotProductId = prodDef?.ID;
                string slotPackagingId = productItem.AppliedPackaging?.ID;

                if (slotProductId != selection.ProductId || slotPackagingId != selection.PackagingId)
                    continue;

                // Found a match — add one unit per available quantity up to maxNeeded
                int canTake = Mathf.Min(slot.Quantity, maxNeeded - found);
                for (int u = 0; u < canTake; u++)
                {
                    GameObject visualPrefab = null;
                    try
                    {
                        var stored = productItem.StoredItem;
                        if (stored != null) visualPrefab = stored.gameObject;
                    }
                    catch { }

                    _availableProducts.Add(new AvailableProduct
                    {
                        ProductId = selection.ProductId,
                        PackagingId = selection.PackagingId,
                        ProductName = selection.ProductName,
                        Price = selection.Price,
                        QualityLevel = selection.QualityLevel,
                        VisualPrefab = visualPrefab,
                        ProductDef = prodDef,
                        SourceSlot = slot,
                        HotbarIndex = -1
                    });
                    found++;
                }
            }
            return found;
        }

        /// <summary>Searches player hotbar for matching products. Returns count found.</summary>
        private int SearchPlayerInventory(SelectedProduct selection, int maxNeeded)
        {
            if (maxNeeded <= 0) return 0;
            int found = 0;

            try
            {
                var inventory = PlayerSingleton<PlayerInventory>.Instance;
                if (inventory?.hotbarSlots == null) return 0;

                for (int i = 0; i < inventory.hotbarSlots.Count && found < maxNeeded; i++)
                {
                    var slot = inventory.hotbarSlots[i];
                    if (slot?.ItemInstance == null) continue;

#if IL2CPP
                    var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
#else
                    var productItem = slot.ItemInstance as ProductItemInstance;
#endif
                    if (productItem?.AppliedPackaging == null) continue;

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

                    string slotProductId = prodDef?.ID;
                    string slotPackagingId = productItem.AppliedPackaging?.ID;

                    if (slotProductId != selection.ProductId || slotPackagingId != selection.PackagingId)
                        continue;

                    GameObject visualPrefab = null;
                    try
                    {
                        var stored = productItem.StoredItem;
                        if (stored != null) visualPrefab = stored.gameObject;
                    }
                    catch { }

                    // Take multiple units from the same slot if qty > 1
                    int canTake = Mathf.Min(slot.Quantity, maxNeeded - found);
                    for (int u = 0; u < canTake; u++)
                    {
                        _availableProducts.Add(new AvailableProduct
                        {
                            ProductId = selection.ProductId,
                            PackagingId = selection.PackagingId,
                            ProductName = selection.ProductName,
                            Price = selection.Price,
                            QualityLevel = selection.QualityLevel,
                            VisualPrefab = visualPrefab,
                            ProductDef = prodDef,
                            SourceSlot = null,
                            HotbarIndex = i
                        });
                        found++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"SearchPlayerInventory failed: {ex.Message}");
            }

            return found;
        }

        /// <summary>Consumes one unit from the product's source (storage slot or player hotbar).</summary>
        private static void ConsumeFromSource(ItemSlot sourceSlot, int hotbarIndex, string productName)
        {
            try
            {
                if (sourceSlot != null)
                {
                    if (sourceSlot.Quantity <= 1)
                        sourceSlot.ClearStoredInstance(false);
                    else
                        sourceSlot.ChangeQuantity(-1);
                }
                else if (hotbarIndex >= 0)
                {
                    // From player inventory
                    var inventory = PlayerSingleton<PlayerInventory>.Instance;
                    if (inventory?.hotbarSlots != null && hotbarIndex < inventory.hotbarSlots.Count)
                    {
                        var slot = inventory.hotbarSlots[hotbarIndex];
                        if (slot?.Quantity <= 1)
                            slot?.ClearStoredInstance(false);
                        else
                            slot?.ChangeQuantity(-1);
                    }
                    else
                    {
                        Logger.Warning($"ConsumeFromSource '{productName}': hotbar[{hotbarIndex}] out of range");
                    }
                }
                else
                {
                    Logger.Warning($"ConsumeFromSource '{productName}': no source");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"ConsumeFromSource failed for {productName}: {ex.Message}");
            }
        }

        private static void ConsumeFromSource(CounterProduct product)
            => ConsumeFromSource(product.SourceSlot, product.HotbarIndex, product.ProductName);

        private static void ConsumeFromSource(AvailableProduct product)
            => ConsumeFromSource(product.SourceSlot, product.HotbarIndex, product.ProductName);

        /// <summary>
        /// Returns a product from the counter back to storage.
        /// Priority: player inventory → checkout counter storage → any property storage.
        /// Returns false only if no storage accepts the item.
        /// </summary>
        private static bool ReturnProduct(CounterProduct product)
        {
            try
            {
                // Create item instance to return
#if IL2CPP
                var prodDef = Registry.GetItem(product.ProductId)?.TryCast<ProductDefinition>();
#else
                var prodDef = GetRegistryItem(product.ProductId) as ProductDefinition;
#endif
                if (prodDef == null)
                {
                    Logger.Warning($"ReturnProduct: could not find ProductDefinition for '{product.ProductId}'");
                    return false;
                }

                var instance = prodDef.GetDefaultInstance(1);
#if IL2CPP
                var productInstance = instance?.TryCast<ProductItemInstance>();
#else
                var productInstance = instance as ProductItemInstance;
#endif
                if (productInstance == null)
                {
                    Logger.Warning($"ReturnProduct: GetDefaultInstance returned null for '{product.ProductId}'");
                    return false;
                }

                // Apply packaging
#if IL2CPP
                var packDef = Registry.GetItem(product.PackagingId)?.TryCast<Il2CppScheduleOne.Product.Packaging.PackagingDefinition>();
#else
                var packDef = GetRegistryItem(product.PackagingId) as ScheduleOne.Product.Packaging.PackagingDefinition;
#endif
                if (packDef != null)
                    productInstance.SetPackaging(packDef);

                // 1. Try player inventory (any open slot)
                var inventory = PlayerSingleton<PlayerInventory>.Instance;
                if (inventory != null && inventory.CanItemFitInInventory(productInstance, 1))
                {
                    inventory.AddItemToInventory(productInstance);
                    return true;
                }

                // 2. Try checkout counter storage
                var counterStorage = CheckoutCounter.CounterStorageEntity;
                if (counterStorage != null && counterStorage.CanItemFit(productInstance, 1))
                {
                    counterStorage.InsertItem(productInstance, true);
                    return true;
                }

                // 3. Try any other storage entity on the property grid
                var shackGrid = WestvilleShack.ShackGrid;
                if (shackGrid != null && BuildingGridFactory.GridContainers.TryGetValue(shackGrid, out var buildingRoot))
                {
#if IL2CPP
                    var storages = buildingRoot.GetComponentsInChildren<StorageEntity>(true);
                    for (int i = 0; i < storages.Count; i++)
                    {
                        var s = storages[i];
#else
                    var storages = buildingRoot.GetComponentsInChildren<StorageEntity>(true);
                    foreach (var s in storages)
                    {
#endif
                        if (s == counterStorage) continue; // already tried
                        if (s.CanItemFit(productInstance, 1))
                        {
                            s.InsertItem(productInstance, true);
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"ReturnProduct failed for '{product.ProductName}': {ex.Message}");
                return false;
            }
        }

        // =================================================================
        //  HUD & POS helpers
        // =================================================================

        private void ShowSpriteHUD()
        {
            _completeBtnShown = false;
            var spriteItems = new List<BudtenderHUD.SpriteItem>();
            for (int i = 0; i < _availableProducts.Count; i++)
            {
                var ap = _availableProducts[i];
                spriteItems.Add(new BudtenderHUD.SpriteItem
                {
                    ProductId = ap.ProductId,
                    PackagingId = ap.PackagingId,
                    ProductName = ap.ProductName,
                    QualityLevel = ap.QualityLevel
                });
            }
            BudtenderHUD.Show(spriteItems, OnSpriteClicked);
        }

        private void UpdatePOSForBudtending()
        {
            ComputerScreen.ShowBudtendingStatus(
                _customer.SelectedProducts,
                _missingProductKeys,
                _placedProductKeys,
                _totalPlacedPrice);
        }

        // =================================================================
        //  Payment visual
        // =================================================================

        private void SpawnPaymentObject()
        {
            var surfacePos = CheckoutCounter.SurfacePosition.Value;
            var counterTransform = CheckoutCounter.CounterTransform;

            // Position on the left side of desk
            var payPos = surfacePos - counterTransform.right * 0.3f;

            CacheCashPrefab();

            GameObject go;
            if (_cashPrefab != null)
            {
                go = UnityEngine.Object.Instantiate(_cashPrefab);
                go.name = "OTC_CheckoutCash";
                go.transform.position = payPos;
                go.transform.localScale = Vector3.one;

                var cashVisuals = go.GetComponentInChildren<CashStackVisuals>();
                if (cashVisuals != null)
                    cashVisuals.ShowAmount(_totalPlacedPrice);

                foreach (var col in go.GetComponentsInChildren<Collider>())
                    col.enabled = false;
                go.layer = 0; // Default layer for raycast
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(1f, 1f, 1f);
                box.center = new Vector3(0f, 0.1f, 0f); // align with visual center
            }
            else
            {
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
                    _cashPrefab = def.StoredItem.gameObject;
                else
                    Logger.Warning("Cash definition or StoredItem not found in Registry");
            }
            catch (Exception ex)
            {
                Logger.Warning($"CacheCashPrefab failed: {ex.Message}");
            }
        }

#if !IL2CPP
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
                "baggie" => new Color(0.95f, 0.95f, 0.9f),
                "jar" => new Color(0.6f, 0.85f, 0.6f),
                "brick" => new Color(0.7f, 0.55f, 0.35f),
                _ => new Color(0.8f, 0.8f, 0.75f)
            };
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

                // Look-at point shifted toward computer (positive right = computer side)
                var surfaceCenter = counterPos + Vector3.up * CamUpOffset + counterTransform.right * CamRightOffset;
                var overheadPos = surfaceCenter + Vector3.up * CamHeight + counterTransform.forward * CamForwardOffset;
                var lookDir = (surfaceCenter - overheadPos).normalized;
                var overheadRot = Quaternion.LookRotation(lookDir);

                var cam = PlayerSingleton<PlayerCamera>.Instance;
                cam.AddActiveUIElement("OTC_Checkout");
                cam.OverrideTransform(overheadPos, overheadRot, CameraLerpTime, false);
                cam.OverrideFOV(CamFOV, CameraLerpTime);
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
            }
            catch { }
        }

        private static void PlayCashSound()
        {
            try
            {
#if IL2CPP
                Il2CppScheduleOne.DevUtilities.NetworkSingleton<NativeMoneyManager>.Instance?.PlayCashSound();
#else
                ScheduleOne.DevUtilities.NetworkSingleton<NativeMoneyManager>.Instance?.PlayCashSound();
#endif
            }
            catch { }
        }

        private void PlayCustomerVoice(EVOLineType lineType)
        {
            try
            {
                var emitter = _customer.GameNpc?.VoiceOverEmitter;
                if (emitter == null) return;
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
            // Voice line depends on fulfillment:
            // - 0 products placed → Angry (turned away empty-handed)
            // - Partial order → Annoyed
            // - Full order → Thanks
            if (_counterProducts.Count == 0)
                PlayCustomerVoice(EVOLineType.Angry);
            else if (_missingProductKeys.Count > 0)
                PlayCustomerVoice(EVOLineType.Annoyed);
            else
                PlayCustomerVoice(EVOLineType.Thanks);

            // Products already consumed at sprite-click time (OnSpriteClicked)

            // Record sales analytics — only for products actually placed
            try
            {
                var saveData = SaveData.PropertySaveData.Instance;
                if (saveData != null)
                {
                    int gameDay = S1API.GameTime.TimeManager.ElapsedDays;
                    foreach (var product in _counterProducts)
                    {
                        saveData.RecordSale(
                            product.ProductId,
                            product.ProductName,
                            1,
                            product.Price,
                            product.QualityLevel,
                            gameDay);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to record sale: {ex.Message}");
            }

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
            BudtenderHUD.Hide();
            UnlockPlayerInput();

            // Return counter products to storage before cleanup destroys them
            foreach (var product in _counterProducts)
            {
                if (product.Visual == null) continue;
                if (!ReturnProduct(product))
                    Logger.Warning($"Abort: no storage for '{product.ProductName}' — item lost");
            }

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
            foreach (var item in _counterProducts)
            {
                if (item.Visual != null)
                    UnityEngine.Object.Destroy(item.Visual);
            }
            _counterProducts.Clear();

            if (_paymentObject != null)
            {
                UnityEngine.Object.Destroy(_paymentObject);
                _paymentObject = null;
            }

            if (_pickupAnimCoroutine != null)
            {
                MelonCoroutines.Stop(_pickupAnimCoroutine);
                _pickupAnimCoroutine = null;
            }

            if (_cashFlyCoroutine != null)
            {
                MelonCoroutines.Stop(_cashFlyCoroutine);
                _cashFlyCoroutine = null;
            }

            BudtenderHUD.Hide();
            Instance = null;
        }
    }
}
