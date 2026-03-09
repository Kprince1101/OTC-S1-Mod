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
    /// WorldSpace Canvas on the checkout computer monitor.
    /// POS-style display: header row, columnar product grid, total, and blinking [R] Checkout prompt.
    /// Supports periodic refresh with 2-second throttle for live availability updates.
    /// </summary>
    public static class ComputerScreen
    {
        private static GameObject _canvasGo;
        private static Canvas _canvas;
        private static TextMeshProUGUI _headerText;
        private static GameObject _checkoutPanel;
        private static TextMeshProUGUI _promptText;
        private static TextMeshProUGUI _totalValueText;
        private static TextMeshProUGUI _totalQtyText;

        private const int MaxProductRows = 3;
        private static readonly List<ProductRow> _productRows = new();

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
        private static List<CustomerInstance.SelectedProduct> _allProducts;
        private static List<CustomerInstance.SelectedProduct> _sortedProducts;
        private static HashSet<string> _fulfilledKeys = new();
        private static HashSet<string> _missingKeys = new();
        private static HashSet<string> _placedKeys = new();
        private static int _scrollOffset;
        private static object _scrollCoroutine;
        private static object _blinkCoroutine;
        private static float _orderTotal;
        private static float _placedTotal;
        private static int _totalQty;
        private static bool _isBudtending;

        // Periodic refresh
        private static float _lastRefreshTime;
        private static bool _forceRefresh;
        private const float RefreshInterval = 2f;

        // Layout: row content area is (PanelWidth - 10) wide, centered in panel
        private const float PanelWidth = 190f;
        private const float PanelHeight = 120f;
        private const float RowHeight = 18f;
        private const float RowSpacing = 18f;

        // Column X offsets within each row (anchored left, row width = PanelWidth - 10 = 180)
        private const float IconX = 6f;
        private const float StarX = 20f;
        private const float NameX = 34f;
        private const float NameWidth = 72f;
        private const float QtyX = 110f;
        private const float QtyWidth = 25f;
        private const float PriceX = 138f;
        private const float PriceWidth = 42f;

        // Colors — CRT terminal aesthetic (bright on near-black)
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

        /// <summary>
        /// Creates the WorldSpace Canvas on the computer monitor.
        /// Called from CheckoutCounter.ApplyDeskVisual after the computer mesh is instantiated.
        /// </summary>
        public static void Create(GameObject computerGo)
        {
            if (computerGo == null) return;
            if (_canvasGo != null) return;

            try
            {
                // Create canvas GO as child of the Computer (all-in-one with built-in screen)
                _canvasGo = new GameObject("OTC_ComputerScreen");
                _canvasGo.transform.SetParent(computerGo.transform, false);

                // Position canvas on the built-in screen face (values from runtime editor).
                _canvasGo.transform.localPosition = new Vector3(-0.1600f, -0.0100f, 0.2450f);
                _canvasGo.transform.localRotation = Quaternion.Euler(0f, 269f, 270f);
                _canvasGo.transform.localScale = new Vector3(-0.0022f, 0.0019f, 0.0019f);

                // Canvas component
                _canvas = _canvasGo.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.WorldSpace;
                _canvas.sortingOrder = 10;

                var rt = _canvasGo.GetComponent<RectTransform>();
                rt.sizeDelta = new Vector2(200f, 150f);
                rt.pivot = new Vector2(0.5f, 0.5f);

                // Background
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

                // Header text (store name — always visible)
                _headerText = CreateText("Header", _canvasGo.transform,
                    new Vector2(0f, 60f), new Vector2(180f, 30f),
                    "GreenTab POS", 14, TextAlignmentOptions.Center,
                    TitleColor);

                // Checkout panel (hidden by default)
                _checkoutPanel = new GameObject("CheckoutPanel");
                _checkoutPanel.transform.SetParent(_canvasGo.transform, false);
                var panelRt = _checkoutPanel.AddComponent<RectTransform>();
                panelRt.anchorMin = new Vector2(0.5f, 0.5f);
                panelRt.anchorMax = new Vector2(0.5f, 0.5f);
                panelRt.pivot = new Vector2(0.5f, 0.5f);
                panelRt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
                panelRt.anchoredPosition = new Vector2(0f, -15f);

                // Row left edge in panel-local coords (row is centered, 180 wide in 190 panel)
                float rowLeft = -(PanelWidth - 10f) / 2f; // -90

                // Column header labels
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

                // Separator under column headers
                CreateSeparator("Sep1", _checkoutPanel.transform, 42f);

                // Product rows (up to 3 visible, scrollable)
                for (int i = 0; i < MaxProductRows; i++)
                {
                    float rowY = 30f - i * RowSpacing;
                    var row = CreateProductRow(i, _checkoutPanel.transform, rowY);
                    _productRows.Add(row);
                }

                // Separator above total
                CreateSeparator("Sep2", _checkoutPanel.transform, -22f);

                // Total row — label + qty + price
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

                // [R] Checkout prompt (blinking)
                _promptText = CreateText("Prompt", _checkoutPanel.transform,
                    new Vector2(0f, -44f), new Vector2(PanelWidth, 18f),
                    "[R] Checkout", 11, TextAlignmentOptions.Center,
                    PromptBright);

                _checkoutPanel.SetActive(false);
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
        /// Called when a customer arrives at checkout (pre-R press).
        /// Starts periodic refresh for live availability updates.
        /// </summary>
        public static void ShowCheckoutInfo(List<CustomerInstance.SelectedProduct> products)
        {
            if (_checkoutPanel == null) return;

            StopAnimations();
            _allProducts = new List<CustomerInstance.SelectedProduct>(products);
            _isBudtending = false;
            _placedKeys = new HashSet<string>();
            _placedTotal = 0f;
            _scrollOffset = 0;

            // Initial availability scan
            SearchAvailability();
            SortAndDisplay();

            _checkoutPanel.SetActive(true);
            _lastRefreshTime = Time.time;

            // Start blinking the checkout prompt
            if (_promptText != null)
                _promptText.text = "[R] Checkout";
            _blinkCoroutine = MelonCoroutines.Start(BlinkPromptCoroutine());
        }

        /// <summary>
        /// Shows budtending status on the POS during active checkout.
        /// Placed items get highlighted, missing items shown in red.
        /// </summary>
        public static void ShowBudtendingStatus(
            List<CustomerInstance.SelectedProduct> products,
            HashSet<string> missingKeys,
            HashSet<string> placedKeys,
            float placedTotal)
        {
            if (_checkoutPanel == null) return;

            StopAnimations();
            _allProducts = new List<CustomerInstance.SelectedProduct>(products);
            _isBudtending = true;
            _missingKeys = missingKeys ?? new HashSet<string>();
            _placedKeys = placedKeys ?? new HashSet<string>();
            _fulfilledKeys = new HashSet<string>(_placedKeys);
            _placedTotal = placedTotal;
            _scrollOffset = 0;

            SortAndDisplay();

            _checkoutPanel.SetActive(true);
            _lastRefreshTime = Time.time;

            // Show [R] Back out prompt during active budtending, or [R] Resume when paused
            bool isPaused = CheckoutProcess.Instance?.IsPaused == true;
            if (_promptText != null)
                _promptText.text = isPaused ? "[R] Resume" : "[R] Back out";

            _blinkCoroutine = MelonCoroutines.Start(BlinkPromptCoroutine());
        }

        /// <summary>
        /// Hides the checkout panel, returning to store name only.
        /// </summary>
        public static void HideCheckoutInfo()
        {
            StopAnimations();
            _allProducts = null;
            _sortedProducts = null;
            _isBudtending = false;
            if (_checkoutPanel != null)
                _checkoutPanel.SetActive(false);
        }

        /// <summary>
        /// Called from Core.OnLateUpdate. Handles periodic POS refresh (2s throttle).
        /// Only refreshes when checkout panel is visible and in pre-checkout waiting mode.
        /// </summary>
        public static void Tick()
        {
            if (_checkoutPanel == null || !_checkoutPanel.activeSelf) return;
            if (_allProducts == null || _allProducts.Count == 0) return;

            // Only auto-refresh in waiting mode (not during active budtending)
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
        /// Forces an immediate POS refresh on the next Tick. Call when player
        /// inventory or storage changes.
        /// </summary>
        public static void ForceRefresh() => _forceRefresh = true;

        // =================================================================
        //  Availability search (lightweight, for pre-checkout display)
        // =================================================================

        /// <summary>
        /// Searches counter storage and player inventory to determine
        /// which requested products are available. Updates _fulfilledKeys and _missingKeys.
        /// Only checks immediate sources (counter + hotbar), not display shelves.
        /// </summary>
        private static void SearchAvailability()
        {
            _fulfilledKeys.Clear();
            _missingKeys.Clear();

            if (_allProducts == null) return;

            try
            {
                // Get counter storage
                StorageEntity counterStorage = null;
                if (CheckoutCounter.CounterTransform != null)
                    counterStorage = CheckoutCounter.CounterTransform
                        .GetComponentInChildren<StorageEntity>(true);

                foreach (var product in _allProducts)
                {
                    string key = $"{product.ProductId}:{product.PackagingId}";
                    int needed = product.Quantity > 0 ? product.Quantity : 1;
                    int found = 0;

                    // Search counter storage
                    if (counterStorage?.ItemSlots != null)
                        found += CountMatchingProducts(counterStorage, product.ProductId, product.PackagingId);

                    // Search player inventory (hotbar)
                    if (found < needed)
                        found += CountPlayerInventory(product.ProductId, product.PackagingId);

                    if (found >= needed)
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

        /// <summary>Counts how many items in a StorageEntity match the given product+packaging.</summary>
        private static int CountMatchingProducts(StorageEntity storage, string productId, string packagingId)
        {
            if (storage?.ItemSlots == null) return 0;
            int count = 0;

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

                if (prodDef?.ID == productId && productItem.AppliedPackaging?.ID == packagingId)
                    count += slot.Quantity;
            }
            return count;
        }

        /// <summary>Counts how many items in the player's hotbar match the given product+packaging.</summary>
        private static int CountPlayerInventory(string productId, string packagingId)
        {
            int count = 0;
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

                    if (prodDef?.ID == productId && productItem.AppliedPackaging?.ID == packagingId)
                        count += slot.Quantity;
                }
            }
            catch { }
            return count;
        }

        // =================================================================
        //  Sort & display
        // =================================================================

        /// <summary>
        /// Sorts products (unfulfilled first, fulfilled last) and updates all visible rows.
        /// </summary>
        private static void SortAndDisplay()
        {
            if (_allProducts == null) return;

            // Build sorted list: unfulfilled first, then fulfilled
            var unfulfilled = new List<CustomerInstance.SelectedProduct>();
            var fulfilled = new List<CustomerInstance.SelectedProduct>();

            foreach (var product in _allProducts)
            {
                string key = $"{product.ProductId}:{product.PackagingId}";
                bool isFulfilled = _fulfilledKeys.Contains(key) || _placedKeys.Contains(key);
                if (isFulfilled)
                    fulfilled.Add(product);
                else
                    unfulfilled.Add(product);
            }

            _sortedProducts = new List<CustomerInstance.SelectedProduct>();
            _sortedProducts.AddRange(unfulfilled);
            _sortedProducts.AddRange(fulfilled);

            // Compute totals
            _orderTotal = 0f;
            _totalQty = 0;
            foreach (var p in _allProducts)
            {
                int qty = p.Quantity > 0 ? p.Quantity : 1;
                _orderTotal += p.Price * qty;
                _totalQty += qty;
            }

            // Update total display
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

            // Determine scroll behavior: only scroll if unfulfilled items exceed visible rows
            StopScroll();
            _scrollOffset = 0;
            int unfulfilledCount = unfulfilled.Count;
            if (unfulfilledCount > MaxProductRows)
                _scrollCoroutine = MelonCoroutines.Start(ScrollCoroutine(unfulfilledCount));

            UpdateVisibleRows();
        }

        /// <summary>Updates the 3 visible product rows from _sortedProducts at _scrollOffset.</summary>
        private static void UpdateVisibleRows()
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

                        string key = $"{product.ProductId}:{product.PackagingId}";
                        bool isMissing = _missingKeys.Contains(key);
                        bool isFulfilled = _fulfilledKeys.Contains(key) || _placedKeys.Contains(key);

                        // Background: fulfilled = highlight, missing = red, normal = default
                        if (isFulfilled)
                            row.Background.color = RowBgHighlight;
                        else if (isMissing)
                            row.Background.color = RowBgMissing;
                        else
                            row.Background.color = RowBgNormal;

                        // Quality star colored by ItemQuality.GetColor() values
                        row.Star.color = product.QualityLevel switch
                        {
                            0 => new Color32(80, 145, 50, 255),   // Trash
                            1 => new Color32(80, 145, 50, 255),   // Poor
                            2 => new Color32(100, 190, 255, 255), // Standard
                            3 => new Color32(225, 75, 255, 255),  // Premium
                            4 => new Color32(255, 200, 50, 255),  // Heavenly
                            _ => Color.white
                        };

                        // Text colors: missing items in red
                        row.NameText.text = product.ProductName;
                        row.NameText.color = isMissing ? TextMissing : TextColor;

                        int qty = product.Quantity > 0 ? product.Quantity : 1;
                        row.QtyText.text = qty.ToString();
                        row.QtyText.color = isMissing ? TextMissing : TextColor;

                        row.PriceText.text = $"${product.Price * qty:F2}";
                        row.PriceText.color = isMissing ? PriceMissing : PriceColor;

                        // Product icon sprite
                        try
                        {
                            var iconMgr = Singleton<ProductIconManager>.Instance;
                            if (iconMgr != null && product.ProductId != null && product.PackagingId != null)
                            {
                                var sprite = iconMgr.GetIcon(product.ProductId, product.PackagingId, true);
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

        /// <summary>Scrolls only through unfulfilled items (stops before fulfilled section).</summary>
        private static IEnumerator ScrollCoroutine(int unfulfilledCount)
        {
            while (true)
            {
                yield return new WaitForSeconds(3f);
                if (_sortedProducts == null || unfulfilledCount <= MaxProductRows)
                    yield break;

                _scrollOffset++;
                // Only scroll far enough to show the last unfulfilled row
                if (_scrollOffset > unfulfilledCount - MaxProductRows)
                    _scrollOffset = 0;

                UpdateVisibleRows();
            }
        }

        private static IEnumerator BlinkPromptCoroutine()
        {
            while (true)
            {
                if (_promptText != null) _promptText.color = PromptBright;
                yield return new WaitForSeconds(0.7f);
                if (_promptText != null) _promptText.color = PromptDim;
                yield return new WaitForSeconds(0.5f);
            }
        }

        private static void StopScroll()
        {
            if (_scrollCoroutine != null)
            {
                MelonCoroutines.Stop(_scrollCoroutine);
                _scrollCoroutine = null;
            }
        }

        private static void StopAnimations()
        {
            StopScroll();
            if (_blinkCoroutine != null)
            {
                MelonCoroutines.Stop(_blinkCoroutine);
                _blinkCoroutine = null;
            }
        }

        /// <summary>
        /// Destroys the canvas. Called on scene cleanup.
        /// </summary>
        public static void Cleanup()
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
            _totalValueText = null;
            _totalQtyText = null;
            _allProducts = null;
            _sortedProducts = null;
            _fulfilledKeys = new HashSet<string>();
            _missingKeys = new HashSet<string>();
            _placedKeys = new HashSet<string>();
        }

        // =====================================================================
        //  UI helpers
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

            // Background highlight bar (stretches to fill row with slight padding)
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

            // Icon
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

            // Quality star
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

            // Product name (left column)
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

            // Quantity (center column)
            var qtyText = CreateText("Qty", rowGo.transform,
                Vector2.zero, new Vector2(QtyWidth, RowHeight),
                "", 9, TextAlignmentOptions.Center, TextColor);
            var qtyRt = qtyText.GetComponent<RectTransform>();
            qtyRt.anchorMin = new Vector2(0f, 0.5f);
            qtyRt.anchorMax = new Vector2(0f, 0.5f);
            qtyRt.pivot = new Vector2(0f, 0.5f);
            qtyRt.anchoredPosition = new Vector2(QtyX, 0f);

            // Price (right column)
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

            // Grab the star sprite from an existing QualityItemInfoContent (game's tooltip prefab)
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

            // Fallback: Unity built-in knob sprite (circle)
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
