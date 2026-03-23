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

        // Upgrade panel
        private GameObject _upgradePanel;
        private readonly List<UpgradeRow> _upgradeRows = new();
        private bool _upgradeActive;
        private string _previewStyleId;
        private object _flashCoroutine;

        private struct UpgradeRow
        {
            public GameObject Root;
            public Image Background;
            public TextMeshProUGUI NameText;
            public TextMeshProUGUI ActionText;
            public string StyleId;
        }

        /// <summary>Callback fired when the player clicks a desk style row.</summary>
        public System.Action<string> OnUpgradeSelected;

        private static readonly Color UpgradeCurrentBg = new(0.06f, 0.15f, 0.06f, 0.8f);
        private static readonly Color UpgradeAvailableBg = new(0.03f, 0.06f, 0.03f, 0.5f);
        private static readonly Color UpgradeLockedBg = new(0.12f, 0.03f, 0.03f, 0.5f);
        private static readonly Color CostColor = new(0.4f, 0.8f, 1f);

        // Display state
        private List<CustomerInstance.SelectedProduct> _allProducts;
        private List<CustomerInstance.SelectedProduct> _sortedProducts;
        private HashSet<string> _fulfilledKeys = new();
        private HashSet<string> _missingKeys = new();
        private HashSet<string> _placedKeys = new();
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
        private const float RefreshInterval = 2f;

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
                    "[R] Upgrades", 11, TextAlignmentOptions.Center,
                    PromptBright);

                // --- Upgrade panel ---
                CreateUpgradePanel();
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
            _placedKeys = new HashSet<string>();
            _placedTotal = 0f;
            _scrollOffset = 0;

            SearchAvailability();
            SortAndDisplay();

            if (_idlePromptText != null) _idlePromptText.gameObject.SetActive(false);
            _checkoutPanel.SetActive(true);
            _lastRefreshTime = Time.time;

            if (_promptText != null)
                _promptText.text = "[R] Checkout";
            _blinkCoroutine = MelonCoroutines.Start(BlinkPromptCoroutine());
        }

        /// <summary>
        /// Shows budtending status on the POS during active checkout.
        /// </summary>
        public void ShowBudtendingStatus(
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

            if (_idlePromptText != null) _idlePromptText.gameObject.SetActive(false);
            _checkoutPanel.SetActive(true);
            _lastRefreshTime = Time.time;

            bool isPaused = CheckoutProcess.Instance?.IsPaused == true;
            if (_promptText != null)
                _promptText.text = isPaused ? "[R] Resume" : "[R] Back out";

            _blinkCoroutine = MelonCoroutines.Start(BlinkPromptCoroutine());
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
            if (_idlePromptText != null && !_upgradeActive)
                _idlePromptText.gameObject.SetActive(true);
        }

        /// <summary>
        /// Periodic refresh. Only refreshes in pre-checkout waiting mode (2s throttle).
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
                    string key = $"{product.ProductId}:{product.PackagingId}";
                    int needed = product.Quantity > 0 ? product.Quantity : 1;
                    int found = 0;

                    if (counterStorage?.ItemSlots != null)
                        found += CountMatchingProducts(counterStorage, product.ProductId, product.PackagingId);

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

        private void SortAndDisplay()
        {
            if (_allProducts == null) return;

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

            StopScroll();
            _scrollOffset = 0;
            int unfulfilledCount = unfulfilled.Count;
            if (unfulfilledCount > MaxProductRows)
                _scrollCoroutine = MelonCoroutines.Start(ScrollCoroutine(unfulfilledCount));

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

                        string key = $"{product.ProductId}:{product.PackagingId}";
                        bool isMissing = _missingKeys.Contains(key);
                        bool isFulfilled = _fulfilledKeys.Contains(key) || _placedKeys.Contains(key);

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
            if (_flashCoroutine != null)
            {
                MelonCoroutines.Stop(_flashCoroutine);
                _flashCoroutine = null;
            }
        }

        // =================================================================
        //  Upgrade screen
        // =================================================================

        private void CreateUpgradePanel()
        {
            _upgradePanel = new GameObject("UpgradePanel");
            _upgradePanel.transform.SetParent(_canvasGo.transform, false);
            var panelRt = _upgradePanel.AddComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            panelRt.anchoredPosition = new Vector2(0f, -15f);

            CreateText("UpTitle", _upgradePanel.transform,
                new Vector2(0f, 50f), new Vector2(180f, 16f),
                "DESK STYLE", 10, TextAlignmentOptions.Center, HeaderLabelColor);

            CreateSeparator("UpSep1", _upgradePanel.transform, 42f);

            int idx = 0;
            foreach (var style in DeskStyle.All.Values)
            {
                float rowY = 30f - idx * RowSpacing;
                var row = CreateUpgradeRow(idx, style, _upgradePanel.transform, rowY);
                _upgradeRows.Add(row);
                idx++;
            }

            CreateSeparator("UpSep2", _upgradePanel.transform, 30f - idx * RowSpacing + 8f);

            CreateText("UpPrompt", _upgradePanel.transform,
                new Vector2(0f, 30f - idx * RowSpacing - 6f), new Vector2(PanelWidth, 16f),
                "[R] Close", 9, TextAlignmentOptions.Center,
                PromptBright);

            _upgradePanel.SetActive(false);
        }

        private UpgradeRow CreateUpgradeRow(int index, DeskStyle style, Transform parent, float yPos)
        {
            float rowWidth = PanelWidth - 10f;

            var rowGo = new GameObject($"UpgradeRow_{index}");
            rowGo.transform.SetParent(parent, false);
            var rowRt = rowGo.AddComponent<RectTransform>();
            rowRt.anchorMin = new Vector2(0.5f, 0.5f);
            rowRt.anchorMax = new Vector2(0.5f, 0.5f);
            rowRt.pivot = new Vector2(0.5f, 0.5f);
            rowRt.sizeDelta = new Vector2(rowWidth, RowHeight);
            rowRt.anchoredPosition = new Vector2(0f, yPos);

            var bgImg = rowGo.AddComponent<Image>();
            bgImg.color = UpgradeAvailableBg;
            bgImg.raycastTarget = false;

            var nameText = CreateText("Name", rowGo.transform,
                Vector2.zero, new Vector2(100f, RowHeight),
                style.DisplayName, 9, TextAlignmentOptions.Left, TextColor);
            var nameRt = nameText.GetComponent<RectTransform>();
            nameRt.anchorMin = new Vector2(0f, 0.5f);
            nameRt.anchorMax = new Vector2(0f, 0.5f);
            nameRt.pivot = new Vector2(0f, 0.5f);
            nameRt.anchoredPosition = new Vector2(10f, 0f);

            // Single right-aligned text for status/cost
            var actionText = CreateText("Action", rowGo.transform,
                Vector2.zero, new Vector2(70f, RowHeight),
                "", 9, TextAlignmentOptions.Right, TitleColor);
            var actionRt = actionText.GetComponent<RectTransform>();
            actionRt.anchorMin = new Vector2(1f, 0.5f);
            actionRt.anchorMax = new Vector2(1f, 0.5f);
            actionRt.pivot = new Vector2(1f, 0.5f);
            actionRt.anchoredPosition = new Vector2(-6f, 0f);

            return new UpgradeRow
            {
                Root = rowGo,
                Background = bgImg,
                NameText = nameText,
                ActionText = actionText,
                StyleId = style.Id
            };
        }

        /// <summary>Shows the desk upgrade panel on the POS screen.</summary>
        public void ShowUpgradeScreen(string previewStyleId = null)
        {
            if (_upgradePanel == null) return;
            StopAnimations();
            if (_checkoutPanel != null) _checkoutPanel.SetActive(false);

            _previewStyleId = previewStyleId;
            RefreshUpgradeRows();

            if (_idlePromptText != null) _idlePromptText.gameObject.SetActive(false);
            _upgradePanel.SetActive(true);
            _upgradeActive = true;

            if (_headerText != null)
                _headerText.text = "GreenTab POS";
        }

        /// <summary>Hides the upgrade panel.</summary>
        public void HideUpgradeScreen()
        {
            if (_upgradePanel != null) _upgradePanel.SetActive(false);
            _upgradeActive = false;
            if (_idlePromptText != null) _idlePromptText.gameObject.SetActive(true);
        }

        /// <summary>Whether the upgrade screen is currently active.</summary>
        public bool IsUpgradeActive => _upgradeActive;

        /// <summary>
        /// Polls for mouse clicks on upgrade rows. Call every frame while upgrade is active.
        /// Uses RectTransformUtility instead of EventSystem for reliable world-space canvas hits.
        /// </summary>
        public void TickUpgradeInput()
        {
            if (!_upgradeActive || _upgradePanel == null) return;
            if (!Input.GetMouseButtonDown(0)) return;

            var cam = _canvas != null ? _canvas.worldCamera : null;
            if (cam == null) cam = Camera.main;
            if (cam == null) return;

            var mousePos = Input.mousePosition;
            for (int i = 0; i < _upgradeRows.Count; i++)
            {
                var row = _upgradeRows[i];
                if (row.Root == null) continue;
                var rowRt = row.Root.GetComponent<RectTransform>();
                if (RectTransformUtility.RectangleContainsScreenPoint(rowRt, mousePos, cam))
                {
                    OnUpgradeSelected?.Invoke(row.StyleId);
                    return;
                }
            }
        }

        private void RefreshUpgradeRows()
        {
            string currentId = _owner?.CurrentDeskStyleId ?? DeskStyle.Default.Id;

            for (int i = 0; i < _upgradeRows.Count; i++)
            {
                var row = _upgradeRows[i];
                var style = DeskStyle.Get(row.StyleId);
                bool isCurrent = row.StyleId == currentId;
                bool isPreviewing = row.StyleId == _previewStyleId;

                if (isCurrent)
                {
                    row.Background.color = UpgradeCurrentBg;
                    row.ActionText.text = "CURRENT";
                    row.ActionText.color = TitleColor;
                }
                else if (isPreviewing)
                {
                    // Previewing — show price, click again to buy
                    row.Background.color = UpgradeCurrentBg;
                    float currentCost = DeskStyle.Get(currentId).Cost;
                    float costDiff = style.Cost - currentCost;
                    if (costDiff > 0f)
                    {
                        row.ActionText.text = $"${costDiff:F0}";
                        row.ActionText.color = CostColor;
                    }
                    else if (costDiff < 0f)
                    {
                        row.ActionText.text = $"+${-costDiff:F0}";
                        row.ActionText.color = PriceColor;
                    }
                    else
                    {
                        row.ActionText.text = "FREE";
                        row.ActionText.color = PriceColor;
                    }
                }
                else
                {
                    row.Background.color = UpgradeAvailableBg;
                    row.ActionText.text = "PREVIEW";
                    row.ActionText.color = HeaderLabelColor;
                }
            }
        }

        /// <summary>Flashes the upgrade row red to indicate insufficient funds.</summary>
        public void FlashInsufficientFunds(string styleId)
        {
            if (_flashCoroutine != null)
                MelonCoroutines.Stop(_flashCoroutine);

            for (int i = 0; i < _upgradeRows.Count; i++)
            {
                if (_upgradeRows[i].StyleId != styleId) continue;
                var row = _upgradeRows[i];
                row.ActionText.text = "NO FUNDS";
                row.ActionText.color = TextMissing;
                row.Background.color = UpgradeLockedBg;
                _flashCoroutine = MelonCoroutines.Start(FlashRowCoroutine(i));
                return;
            }
        }

        private IEnumerator FlashRowCoroutine(int rowIndex)
        {
            // Hold the red state briefly
            yield return new WaitForSeconds(1.2f);

            // Fade back to normal over 0.5s
            float duration = 0.5f;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                if (rowIndex < _upgradeRows.Count)
                {
                    var row = _upgradeRows[rowIndex];
                    row.Background.color = Color.Lerp(UpgradeLockedBg, UpgradeCurrentBg, t);
                    row.ActionText.color = Color.Lerp(TextMissing, CostColor, t);
                }
                yield return null;
            }

            // Restore proper text via full refresh
            _flashCoroutine = null;
            RefreshUpgradeRows();
        }

        /// <summary>
        /// Destroys the canvas.
        /// </summary>
        public void Cleanup()
        {
            StopAnimations();
            _productRows.Clear();
            _upgradeRows.Clear();
            _upgradeActive = false;
            OnUpgradeSelected = null;
            if (_canvasGo != null)
            {
                UnityEngine.Object.Destroy(_canvasGo);
                _canvasGo = null;
            }
            _canvas = null;
            _headerText = null;
            _checkoutPanel = null;
            _upgradePanel = null;
            _promptText = null;
            _idlePromptText = null;
            _totalValueText = null;
            _totalQtyText = null;
            _allProducts = null;
            _sortedProducts = null;
            _fulfilledKeys = new HashSet<string>();
            _missingKeys = new HashSet<string>();
            _placedKeys = new HashSet<string>();
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
