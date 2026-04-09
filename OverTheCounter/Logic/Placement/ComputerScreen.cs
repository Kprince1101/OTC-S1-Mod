using MelonLoader;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.UI.Items;
using Il2CppTMPro;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Product;
using ScheduleOne.Storage;
using ScheduleOne.UI.Items;
using TMPro;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// WorldSpace Canvas on a checkout computer monitor.
    /// POS-style display: header row, columnar product grid, total, and blinking [R] Checkout prompt.
    /// Each CheckoutCounterInstance owns its own ComputerScreen instance.
    /// </summary>
    public class ComputerScreen
    {
        private readonly CheckoutCounterInstance _owner;

        private GameObject _canvasGo;
        private Canvas _canvas;
        private TextMeshProUGUI _headerText;
        private GameObject _checkoutPanel;
        private TextMeshProUGUI _promptText;
        private TextMeshProUGUI _totalValueText;
        private TextMeshProUGUI _totalQtyText;
        private TextMeshProUGUI _idlePromptText;

        private const int MaxProductRows = 3;
        private readonly List<ProductRow> _productRows = new();

        private struct ProductRow
        {
            public GameObject Root;
            public Image Background;
            public Image Icon;
            public Image Star;
            public TextMeshProUGUI NameText;
            public TextMeshProUGUI QtyText;
            public TextMeshProUGUI PriceText;
        }

        private static Sprite _starSprite;

        // Display state
        private List<CustomerInstance.SelectedProduct> _allProducts;
        private List<CustomerInstance.SelectedProduct> _sortedProducts;
        private HashSet<string> _fulfilledKeys = new();
        private HashSet<string> _missingKeys = new();
        private Dictionary<string, int> _placedUnitCounts = new();
        private int _scrollOffset;
        private object _scrollCoroutine;
        private object _blinkCoroutine;
        private float _orderTotal;
        private float _placedTotal;
        private int _totalQty;
        private bool _isBudtending;

        // Periodic refresh
        private float _lastRefreshTime;
        private bool _forceRefresh;
        private const float RefreshInterval = 1f;

        // Layout constants
        private const float PanelWidth = 190f;
        private const float PanelHeight = 120f;
        private const float RowHeight = 18f;
        private const float RowSpacing = 18f;

        private const float IconX = 6f;
        private const float StarX = 20f;
        private const float NameX = 34f;
        private const float NameWidth = 72f;
        private const float QtyX = 110f;
        private const float QtyWidth = 25f;
        private const float PriceX = 138f;
        private const float PriceWidth = 42f;

        // Colors
        private static readonly Color ScreenBg = new(0.01f, 0.02f, 0.01f, 0.98f);
        private static readonly Color HeaderLabelColor = new(0.4f, 0.55f, 0.4f);
        private static readonly Color RowBgNormal = new(0.03f, 0.06f, 0.03f, 0.5f);
        private static readonly Color RowBgHighlight = new(0.06f, 0.15f, 0.06f, 0.8f);
        private static readonly Color RowBgMissing = new(0.12f, 0.03f, 0.03f, 0.5f);
        private static readonly Color TextColor = new(0.8f, 0.9f, 0.8f);
        private static readonly Color TextMissing = new(0.9f, 0.3f, 0.3f);
        private static readonly Color PriceColor = new(0.5f, 0.9f, 0.5f);
        private static readonly Color PriceMissing = new(0.6f, 0.25f, 0.25f);
        private static readonly Color TotalValueColor = new(0.4f, 1f, 0.4f);
        private static readonly Color SepColor = new(0.15f, 0.3f, 0.15f, 0.6f);
        private static readonly Color PromptBright = new(1f, 0.95f, 0.3f, 1f);
        private static readonly Color PromptDim = new(1f, 0.95f, 0.3f, 0.2f);
        private static readonly Color TitleColor = new(0.5f, 0.85f, 0.5f);

        public ComputerScreen(CheckoutCounterInstance owner)
        {
            _owner = owner;
        }

        /// <summary>
        /// Creates the WorldSpace Canvas on the computer monitor.
        /// </summary>
        public void Create(GameObject computerGo)
        {
            if (computerGo == null) return;
            if (_canvasGo != null) return;

            try
            {
                _canvasGo = new GameObject("OTC_ComputerScreen");
                _canvasGo.transform.SetParent(computerGo.transform, false);

                _canvasGo.transform.localPosition = new Vector3(-0.1600f, -0.0100f, 0.2450f);
                _canvasGo.transform.localRotation = Quaternion.Euler(0f, 269f, 270f);
                _canvasGo.transform.localScale = new Vector3(-0.0022f, 0.0019f, 0.0019f);

                _canvas = _canvasGo.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.WorldSpace;
                _canvas.sortingOrder = 10;

                _canvasGo.AddComponent<GraphicRaycaster>();

                try
                {
                    var cam = PlayerSingleton<PlayerCamera>.Instance?.Camera;
                    if (cam != null) _canvas.worldCamera = cam;
                }
                catch { }

                var rt = _canvasGo.GetComponent<RectTransform>();
                rt.sizeDelta = new Vector2(200f, 150f);
                rt.pivot = new Vector2(0.5f, 0.5f);

                var bgGo = new GameObject("Background");
                bgGo.transform.SetParent(_canvasGo.transform, false);
                var bgRt = bgGo.AddComponent<RectTransform>();
                bgRt.anchorMin = Vector2.zero;
                bgRt.anchorMax = Vector2.one;
                bgRt.offsetMin = Vector2.zero;
                bgRt.offsetMax = Vector2.zero;
                var bgImg = bgGo.AddComponent<Image>();
                bgImg.color = ScreenBg;
                bgImg.raycastTarget = false;

                _headerText = CreateText("Header", _canvasGo.transform,
                    new Vector2(0f, 60f), new Vector2(180f, 30f),
                    "GreenTab POS", 14, TextAlignmentOptions.Center,
                    TitleColor);

                _checkoutPanel = new GameObject("CheckoutPanel");
                _checkoutPanel.transform.SetParent(_canvasGo.transform, false);
                var panelRt = _checkoutPanel.AddComponent<RectTransform>();
                panelRt.anchorMin = new Vector2(0.5f, 0.5f);
                panelRt.anchorMax = new Vector2(0.5f, 0.5f);
                panelRt.pivot = new Vector2(0.5f, 0.5f);
                panelRt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
                panelRt.anchoredPosition = new Vector2(0f, -15f);

                float rowLeft = -(PanelWidth - 10f) / 2f;

                float headerY = 50f;
                var colItem = CreateText("ColItem", _checkoutPanel.transform,
                    Vector2.zero, new Vector2(NameWidth, 14f),
                    "ITEM", 8, TextAlignmentOptions.Left, HeaderLabelColor);
                AnchorLeftAt(colItem.GetComponent<RectTransform>(), rowLeft + NameX, headerY);

                var colQty = CreateText("ColQty", _checkoutPanel.transform,
                    Vector2.zero, new Vector2(QtyWidth, 14f),
                    "QTY", 8, TextAlignmentOptions.Center, HeaderLabelColor);
                AnchorLeftAt(colQty.GetComponent<RectTransform>(), rowLeft + QtyX, headerY);

                var colPrice = CreateText("ColPrice", _checkoutPanel.transform,
                    Vector2.zero, new Vector2(PriceWidth, 14f),
                    "PRICE", 8, TextAlignmentOptions.Right, HeaderLabelColor);
                AnchorLeftAt(colPrice.GetComponent<RectTransform>(), rowLeft + PriceX, headerY);

                CreateSeparator("Sep1", _checkoutPanel.transform, 42f);

                for (int i = 0; i < MaxProductRows; i++)
                {
                    float rowY = 30f - i * RowSpacing;
                    var row = CreateProductRow(i, _checkoutPanel.transform, rowY);
                    _productRows.Add(row);
                }

                CreateSeparator("Sep2", _checkoutPanel.transform, -22f);

                var totalLabel = CreateText("TotalLabel", _checkoutPanel.transform,
                    Vector2.zero, new Vector2(80f, 16f),
                    "TOTAL", 10, TextAlignmentOptions.Left, Color.white);
                AnchorLeftAt(totalLabel.GetComponent<RectTransform>(), rowLeft + NameX, -32f);

                _totalQtyText = CreateText("TotalQty", _checkoutPanel.transform,
                    Vector2.zero, new Vector2(QtyWidth, 16f),
                    "", 10, TextAlignmentOptions.Center, TextColor);
                AnchorLeftAt(_totalQtyText.GetComponent<RectTransform>(), rowLeft + QtyX, -32f);

                _totalValueText = CreateText("TotalValue", _checkoutPanel.transform,
                    Vector2.zero, new Vector2(PriceWidth, 16f),
                    "$0.00", 11, TextAlignmentOptions.Right, TotalValueColor);
                AnchorLeftAt(_totalValueText.GetComponent<RectTransform>(), rowLeft + PriceX, -32f);

                _promptText = CreateText("Prompt", _checkoutPanel.transform,
                    new Vector2(0f, -44f), new Vector2(PanelWidth, 18f),
                    "[R] Checkout", 11, TextAlignmentOptions.Center,
                    PromptBright);

                _checkoutPanel.SetActive(false);

                // --- Idle prompt (visible when no panel is active) ---
                _idlePromptText = CreateText("IdlePrompt", _canvasGo.transform,
                    new Vector2(0f, -15f), new Vector2(PanelWidth, 18f),
                    _owner.IsStaffed ? "" : "[R] Checkout", 11, TextAlignmentOptions.Center,
                    PromptBright);
                if (_owner.IsStaffed)
                    _idlePromptText.gameObject.SetActive(false);
            }
            catch (System.Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"ComputerScreen.Create failed: {ex.Message}");
            }
        }

        // =================================================================
        //  Public API
        // =================================================================

        /// <summary>
        /// Shows the checkout panel with product list and availability status.
        /// </summary>
        public void ShowCheckoutInfo(List<CustomerInstance.SelectedProduct> products)
        {
            if (_checkoutPanel == null) return;

            StopAnimations();
            _allProducts = new List<CustomerInstance.SelectedProduct>(products);
            _isBudtending = false;
            _placedUnitCounts = new Dictionary<string, int>();
            _placedTotal = 0f;
            _scrollOffset = 0;

            SearchAvailability();
            SortAndDisplay();

            if (_idlePromptText != null) _idlePromptText.gameObject.SetActive(false);
            _checkoutPanel.SetActive(true);
            _lastRefreshTime = Time.time;

            if (_owner.IsStaffed)
            {
                // Budtender handles checkout — no player prompt needed
                if (_promptText != null)
                {
                    _promptText.text = "";
                    _promptText.gameObject.SetActive(false);
                }
            }
            else
            {
                if (_promptText != null)
                    _promptText.text = "[R] Checkout";
                _blinkCoroutine = MelonCoroutines.Start(BlinkPromptCoroutine());
            }
        }

        /// <summary>
        /// Shows budtending status on the POS during active checkout.
        /// </summary>
        public void ShowBudtendingStatus(
            List<CustomerInstance.SelectedProduct> products,
            HashSet<string> missingKeys,
            Dictionary<string, int> placedUnitCounts,
            float placedTotal)
        {
            if (_checkoutPanel == null) return;

            StopAnimations();
            _allProducts = new List<CustomerInstance.SelectedProduct>(products);
            _isBudtending = true;
            _missingKeys = missingKeys ?? new HashSet<string>();
            _placedUnitCounts = placedUnitCounts ?? new Dictionary<string, int>();
            _fulfilledKeys = new HashSet<string>();
            // Build fulfilled keys from unit counts vs requested quantities
            foreach (var p in products)
            {
                if (_placedUnitCounts.TryGetValue(p.ProductId, out int placed) && placed >= p.Quantity)
                    _fulfilledKeys.Add(p.ProductId);
            }
            _placedTotal = placedTotal;
            _scrollOffset = 0;

            SortAndDisplay();

            if (_idlePromptText != null) _idlePromptText.gameObject.SetActive(false);
            _checkoutPanel.SetActive(true);
            _lastRefreshTime = Time.time;

            if (_owner.IsStaffed)
            {
                // Budtender handles checkout — no player prompt
                if (_promptText != null)
                {
                    _promptText.text = "";
                    _promptText.gameObject.SetActive(false);
                }
            }
            else
            {
                bool isPaused = CheckoutProcess.Instance?.IsPaused == true;
                if (_promptText != null)
                    _promptText.text = isPaused ? "[R] Resume" : "[R] Back out";

                _blinkCoroutine = MelonCoroutines.Start(BlinkPromptCoroutine());
            }
        }

        /// <summary>
        /// Hides the checkout panel, returning to store name only.
        /// </summary>
        public void HideCheckoutInfo()
        {
            StopAnimations();
            _allProducts = null;
            _sortedProducts = null;
            _isBudtending = false;
            if (_checkoutPanel != null)
                _checkoutPanel.SetActive(false);
            if (_idlePromptText != null)
            {
                // Update text based on current staffed state (may have changed since Create)
                _idlePromptText.text = _owner.IsStaffed ? "" : "[R] Checkout";
                _idlePromptText.gameObject.SetActive(!_owner.IsStaffed);
            }
        }

        /// <summary>
        /// Periodic refresh. Only refreshes in pre-checkout waiting mode (throttled by RefreshInterval).
        /// </summary>
        public void Tick()
        {
            if (_checkoutPanel == null || !_checkoutPanel.activeSelf) return;
            if (_allProducts == null || _allProducts.Count == 0) return;
            if (_isBudtending) return;

            bool shouldRefresh = _forceRefresh ||
                (Time.time - _lastRefreshTime >= RefreshInterval);

            if (!shouldRefresh) return;

            _forceRefresh = false;
            _lastRefreshTime = Time.time;

            SearchAvailability();
            SortAndDisplay();
        }

        /// <summary>
        /// Forces an immediate POS refresh on the next Tick.
        /// </summary>
        public void ForceRefresh() => _forceRefresh = true;

        // =================================================================
        //  Availability search
        // =================================================================

        private void SearchAvailability()
        {
            _fulfilledKeys.Clear();
            _missingKeys.Clear();

            if (_allProducts == null) return;

            try
            {
                StorageEntity counterStorage = _owner?.CounterStorageEntity;

                foreach (var product in _allProducts)
                {
                    string key = product.ProductId;
                    int needed = product.Quantity > 0 ? product.Quantity : 1;
                    int units = 0;

                    if (counterStorage?.ItemSlots != null)
                        units += CountMatchingUnits(counterStorage, product.ProductId);

                    if (units < needed)
                        units += CountPlayerInventoryUnits(product.ProductId);

                    if (units >= needed)
                        _fulfilledKeys.Add(key);
                    else
                        _missingKeys.Add(key);
                }
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"SearchAvailability failed: {ex.Message}");
            }
        }

        /// <summary>Counts total product UNITS in storage matching productId (any packaging).</summary>
        private static int CountMatchingUnits(StorageEntity storage, string productId)
        {
            if (storage?.ItemSlots == null) return 0;
            int units = 0;

            for (int j = 0; j < storage.ItemSlots.Count; j++)
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

                if (prodDef?.ID == productId)
                    units += slot.Quantity * productItem.AppliedPackaging.Quantity;
            }
            return units;
        }

        /// <summary>Counts total product UNITS in player hotbar matching productId (any packaging).</summary>
        private static int CountPlayerInventoryUnits(string productId)
        {
            int units = 0;
            try
            {
                var inventory = PlayerSingleton<PlayerInventory>.Instance;
                if (inventory?.hotbarSlots == null) return 0;

                for (int i = 0; i < inventory.hotbarSlots.Count; i++)
                {
                    var slot = inventory.hotbarSlots[i];
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

                    if (prodDef?.ID == productId)
                        units += slot.Quantity * productItem.AppliedPackaging.Quantity;
                }
            }
            catch { }
            return units;
        }

        // =================================================================
        //  Sort & display
        // =================================================================

        private void SortAndDisplay()
        {
            if (_allProducts == null) return;

            var unfulfilled = new List<CustomerInstance.SelectedProduct>();
            var fulfilled = new List<CustomerInstance.SelectedProduct>();

            foreach (var product in _allProducts)
            {
                string key = product.ProductId;
                bool isFulfilled = _fulfilledKeys.Contains(key);
                if (isFulfilled)
                    fulfilled.Add(product);
                else
                    unfulfilled.Add(product);
            }

            _sortedProducts = new List<CustomerInstance.SelectedProduct>();
            _sortedProducts.AddRange(unfulfilled);
            _sortedProducts.AddRange(fulfilled);

            _orderTotal = 0f;
            _totalQty = 0;
            foreach (var p in _allProducts)
            {
                int qty = p.Quantity > 0 ? p.Quantity : 1;
                _orderTotal += p.Price * qty;
                _totalQty += qty;
            }

            if (_isBudtending)
            {
                if (_totalValueText != null)
                    _totalValueText.text = $"${_placedTotal:F2}";
            }
            else
            {
                if (_totalValueText != null)
                    _totalValueText.text = $"${_orderTotal:F2}";
            }

            if (_totalQtyText != null)
                _totalQtyText.text = $"x{_totalQty}";

            if (!_isBudtending)
            {
                StopScroll();
                _scrollOffset = 0;
                int unfulfilledCount = unfulfilled.Count;
                if (unfulfilledCount > MaxProductRows)
                    _scrollCoroutine = MelonCoroutines.Start(ScrollCoroutine(unfulfilledCount));
            }

            UpdateVisibleRows();
        }

        private void UpdateVisibleRows()
        {
            if (_sortedProducts == null) return;

            try
            {
                for (int i = 0; i < MaxProductRows; i++)
                {
                    int productIdx = _scrollOffset + i;
                    if (productIdx < _sortedProducts.Count)
                    {
                        var product = _sortedProducts[productIdx];
                        var row = _productRows[i];
                        row.Root.SetActive(true);

                        string key = product.ProductId;
                        bool isMissing = _missingKeys.Contains(key);
                        bool isFulfilled = _fulfilledKeys.Contains(key);

                        if (isFulfilled)
                            row.Background.color = RowBgHighlight;
                        else if (isMissing)
                            row.Background.color = RowBgMissing;
                        else
                            row.Background.color = RowBgNormal;

                        row.Star.color = product.QualityLevel switch
                        {
                            0 => new Color32(80, 145, 50, 255),
                            1 => new Color32(80, 145, 50, 255),
                            2 => new Color32(100, 190, 255, 255),
                            3 => new Color32(225, 75, 255, 255),
                            4 => new Color32(255, 200, 50, 255),
                            _ => Color.white
                        };

                        row.NameText.text = product.ProductName;
                        row.NameText.color = isMissing ? TextMissing : TextColor;

                        int qty = product.Quantity > 0 ? product.Quantity : 1;
                        row.QtyText.text = qty.ToString();
                        row.QtyText.color = isMissing ? TextMissing : TextColor;

                        row.PriceText.text = $"${product.Price * qty:F2}";
                        row.PriceText.color = isMissing ? PriceMissing : PriceColor;

                        try
                        {
                            var iconMgr = Singleton<ProductIconManager>.Instance;
                            if (iconMgr != null && product.ProductId != null)
                            {
                                // PackagingId may be null (customer doesn't care about packaging)
                                // Fall back to "baggie" for icon lookup
                                string pkgId = product.PackagingId ?? "baggie";
                                var sprite = iconMgr.GetIcon(product.ProductId, pkgId, true);
                                if (sprite != null)
                                {
                                    row.Icon.sprite = sprite;
                                    row.Icon.color = isMissing ? new Color(1f, 0.5f, 0.5f, 0.5f) : Color.white;
                                }
                                else
                                {
                                    row.Icon.sprite = null;
                                    row.Icon.color = new Color(0.4f, 0.4f, 0.4f, 0.5f);
                                }
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        _productRows[i].Root.SetActive(false);
                    }
                }
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"UpdateVisibleRows failed: {ex.Message}");
            }
        }

        // =================================================================
        //  Coroutines
        // =================================================================

        private IEnumerator ScrollCoroutine(int unfulfilledCount)
        {
            while (true)
            {
                yield return new WaitForSeconds(3f);
                if (_sortedProducts == null || unfulfilledCount <= MaxProductRows)
                    yield break;

                _scrollOffset++;
                if (_scrollOffset > unfulfilledCount - MaxProductRows)
                    _scrollOffset = 0;

                UpdateVisibleRows();
            }
        }

        private IEnumerator BlinkPromptCoroutine()
        {
            while (true)
            {
                if (_promptText != null) _promptText.color = PromptBright;
                yield return new WaitForSeconds(0.7f);
                if (_promptText != null) _promptText.color = PromptDim;
                yield return new WaitForSeconds(0.5f);
            }
        }

        private void StopScroll()
        {
            if (_scrollCoroutine != null)
            {
                MelonCoroutines.Stop(_scrollCoroutine);
                _scrollCoroutine = null;
            }
        }

        private void StopAnimations()
        {
            StopScroll();
            if (_blinkCoroutine != null)
            {
                MelonCoroutines.Stop(_blinkCoroutine);
                _blinkCoroutine = null;
            }
        }

        /// <summary>
        /// Destroys the canvas.
        /// </summary>
        public void Cleanup()
        {
            StopAnimations();
            _productRows.Clear();
            if (_canvasGo != null)
            {
                UnityEngine.Object.Destroy(_canvasGo);
                _canvasGo = null;
            }
            _canvas = null;
            _headerText = null;
            _checkoutPanel = null;
            _promptText = null;
            _idlePromptText = null;
            _totalValueText = null;
            _totalQtyText = null;
            _allProducts = null;
            _sortedProducts = null;
            _fulfilledKeys = new HashSet<string>();
            _missingKeys = new HashSet<string>();
            _placedUnitCounts = new Dictionary<string, int>();
        }

        // =====================================================================
        //  UI helpers (static — pure factory functions)
        // =====================================================================

        private static ProductRow CreateProductRow(int index, Transform parent, float yPos)
        {
            float rowWidth = PanelWidth - 10f;

            var rowGo = new GameObject($"ProductRow_{index}");
            rowGo.transform.SetParent(parent, false);
            var rowRt = rowGo.AddComponent<RectTransform>();
            rowRt.anchorMin = new Vector2(0.5f, 0.5f);
            rowRt.anchorMax = new Vector2(0.5f, 0.5f);
            rowRt.pivot = new Vector2(0.5f, 0.5f);
            rowRt.sizeDelta = new Vector2(rowWidth, RowHeight);
            rowRt.anchoredPosition = new Vector2(0f, yPos);

            var bgGo = new GameObject("Bg");
            bgGo.transform.SetParent(rowGo.transform, false);
            var bgRt = bgGo.AddComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero;
            bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = new Vector2(-2f, -1f);
            bgRt.offsetMax = new Vector2(2f, 1f);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.color = RowBgNormal;
            bgImg.raycastTarget = false;

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(rowGo.transform, false);
            var iconRt = iconGo.AddComponent<RectTransform>();
            iconRt.anchorMin = new Vector2(0f, 0.5f);
            iconRt.anchorMax = new Vector2(0f, 0.5f);
            iconRt.pivot = new Vector2(0f, 0.5f);
            iconRt.sizeDelta = new Vector2(16f, 16f);
            iconRt.anchoredPosition = new Vector2(IconX, 0f);
            var iconImg = iconGo.AddComponent<Image>();
            iconImg.color = new Color(0.4f, 0.4f, 0.4f, 0.5f);
            iconImg.raycastTarget = false;

            var starGo = new GameObject("Star");
            starGo.transform.SetParent(rowGo.transform, false);
            var starRt = starGo.AddComponent<RectTransform>();
            starRt.anchorMin = new Vector2(0f, 0.5f);
            starRt.anchorMax = new Vector2(0f, 0.5f);
            starRt.pivot = new Vector2(0f, 0.5f);
            starRt.sizeDelta = new Vector2(10f, 10f);
            starRt.anchoredPosition = new Vector2(StarX, 0f);
            var starImg = starGo.AddComponent<Image>();
            starImg.sprite = GetStarSprite();
            starImg.color = Color.white;
            starImg.raycastTarget = false;

            var nameText = CreateText("Name", rowGo.transform,
                Vector2.zero, new Vector2(NameWidth, RowHeight),
                "", 9, TextAlignmentOptions.Left, TextColor);
            var nameRt = nameText.GetComponent<RectTransform>();
            nameRt.anchorMin = new Vector2(0f, 0.5f);
            nameRt.anchorMax = new Vector2(0f, 0.5f);
            nameRt.pivot = new Vector2(0f, 0.5f);
            nameRt.anchoredPosition = new Vector2(NameX, 0f);
            TMPFactory.SetWrapping(nameText, false);
            nameText.overflowMode = TextOverflowModes.Ellipsis;

            var qtyText = CreateText("Qty", rowGo.transform,
                Vector2.zero, new Vector2(QtyWidth, RowHeight),
                "", 9, TextAlignmentOptions.Center, TextColor);
            var qtyRt = qtyText.GetComponent<RectTransform>();
            qtyRt.anchorMin = new Vector2(0f, 0.5f);
            qtyRt.anchorMax = new Vector2(0f, 0.5f);
            qtyRt.pivot = new Vector2(0f, 0.5f);
            qtyRt.anchoredPosition = new Vector2(QtyX, 0f);

            var priceText = CreateText("Price", rowGo.transform,
                Vector2.zero, new Vector2(PriceWidth, RowHeight),
                "", 9, TextAlignmentOptions.Right, PriceColor);
            var priceRt = priceText.GetComponent<RectTransform>();
            priceRt.anchorMin = new Vector2(0f, 0.5f);
            priceRt.anchorMax = new Vector2(0f, 0.5f);
            priceRt.pivot = new Vector2(0f, 0.5f);
            priceRt.anchoredPosition = new Vector2(PriceX, 0f);

            rowGo.SetActive(false);

            return new ProductRow
            {
                Root = rowGo,
                Background = bgImg,
                Icon = iconImg,
                Star = starImg,
                NameText = nameText,
                QtyText = qtyText,
                PriceText = priceText
            };
        }

        private static void CreateSeparator(string name, Transform parent, float yPos)
        {
            var sepGo = new GameObject(name);
            sepGo.transform.SetParent(parent, false);
            var sepRt = sepGo.AddComponent<RectTransform>();
            sepRt.anchorMin = new Vector2(0.5f, 0.5f);
            sepRt.anchorMax = new Vector2(0.5f, 0.5f);
            sepRt.pivot = new Vector2(0.5f, 0.5f);
            sepRt.sizeDelta = new Vector2(PanelWidth - 6f, 1f);
            sepRt.anchoredPosition = new Vector2(0f, yPos);
            var sepImg = sepGo.AddComponent<Image>();
            sepImg.color = SepColor;
            sepImg.raycastTarget = false;
        }

        private static void AnchorLeftAt(RectTransform rt, float x, float y)
        {
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(x, y);
        }

        private static Sprite GetStarSprite()
        {
            if (_starSprite != null) return _starSprite;

            try
            {
                var allQiic = Resources.FindObjectsOfTypeAll<QualityItemInfoContent>();
                if (allQiic != null)
                {
                    for (int i = 0; i < allQiic.Length; i++)
                    {
                        if (allQiic[i]?.Star?.sprite != null)
                        {
                            _starSprite = allQiic[i].Star.sprite;
                            return _starSprite;
                        }
                    }
                }
            }
            catch { }

            _starSprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/Knob.psd");
            return _starSprite;
        }

        private static TextMeshProUGUI CreateText(string name, Transform parent,
            Vector2 anchoredPos, Vector2 sizeDelta,
            string content, int fontSize, TextAlignmentOptions alignment, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = sizeDelta;
            rt.anchoredPosition = anchoredPos;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = content;
            tmp.fontSize = fontSize;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.raycastTarget = false;

            return tmp;
        }
    }
}
