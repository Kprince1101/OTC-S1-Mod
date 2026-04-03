using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using S1API.UI;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppTMPro;
using Il2CppScheduleOne.Product;
using Grid = Il2CppScheduleOne.Tiles.Grid;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
#else
using TMPro;
using ScheduleOne.Product;
using Grid = ScheduleOne.Tiles.Grid;
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
#endif

namespace OverTheCounter.Apps
{
    public partial class GreenTabApp
    {
        // ==================================================================
        //  Inventory tab — product inventory with pricing controls
        // ==================================================================

        private Transform _inventoryContent;
        private List<(RectTransform rect, string text)> _inventoryTooltipEntries;
        private TextMeshProUGUI _invTitleLabel;

        // Change detection — skip full rebuild when data hasn't changed
        private int _lastInventoryFingerprint = int.MinValue;

        // Detail view state — set to non-null to show product detail
        internal string _selectedProductId;
        internal string _selectedProductName;

        private void BuildInventoryPanel(Transform parent)
        {
            var panel = UIFactory.Panel("InventoryPanel", parent, BgDark);
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(NAV_WIDTH_FRAC, 0);
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            _tabPanels[AppTab.Inventory] = panel;

            float pad = 10f;

            // Title — property name + inline hint, updated during refresh
            _invTitleLabel = TMPFactory.Text("InvTabTitle", "Inventory",
                panel.transform, 16, TextAlignmentOptions.TopLeft);
            _invTitleLabel.color = Color.white;
            _invTitleLabel.richText = true;
            var titleRect = _invTitleLabel.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(0.95f, 1);
            titleRect.offsetMin = new Vector2(pad, -28);
            titleRect.offsetMax = new Vector2(0, -pad);

            // Scrollable content
            var scrollContainer = UIFactory.Panel("InvScrollContainer", panel.transform, Color.clear);
            var scrollContRect = scrollContainer.GetComponent<RectTransform>();
            scrollContRect.anchorMin = new Vector2(0, 0);
            scrollContRect.anchorMax = new Vector2(1, 1);
            scrollContRect.offsetMin = new Vector2(pad, pad);
            scrollContRect.offsetMax = new Vector2(-pad, -32);

            var scrollView = UIFactory.Panel("InvScrollView", scrollContainer.transform, Color.clear);
            var scrollViewRect = scrollView.GetComponent<RectTransform>();
            scrollViewRect.anchorMin = Vector2.zero;
            scrollViewRect.anchorMax = Vector2.one;
            scrollViewRect.offsetMin = Vector2.zero;
            scrollViewRect.offsetMax = Vector2.zero;

            var scroll = scrollView.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            var viewport = UIFactory.Panel("InvViewport", scrollView.transform, Color.clear);
            var vpRect = viewport.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = Vector2.zero;
            viewport.AddComponent<RectMask2D>();

            var content = new GameObject("InvContent");
            content.transform.SetParent(viewport.transform, false);
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;

            scroll.viewport = vpRect;
            scroll.content = contentRect;

            _inventoryContent = content.transform;

            panel.SetActive(false);
        }

        private void RefreshInventory()
        {
            if (_inventoryContent == null) return;

            // If a product is selected, show detail view instead
            if (_selectedProductId != null)
            {
                RefreshProductDetail();
                return;
            }

            // Gather and merge items from all selected buildings (before destroying UI)
            var buildings = GetBuildingsForSelection();
            var aggregated = new Dictionary<string, InventoryItem>();
            foreach (var bid in buildings)
            {
                var grid = GetGridForBuilding(bid);
                var items = GatherInventoryItems(grid);
                foreach (var item in items)
                {
                    string key = $"{item.ProductId}_{item.Quality}";
                    if (aggregated.TryGetValue(key, out var existing))
                    {
                        existing.Quantity += item.Quantity;
                        aggregated[key] = existing;
                    }
                    else
                    {
                        aggregated[key] = item;
                    }
                }
            }

            var sorted = new List<InventoryItem>(aggregated.Values);
            sorted.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

            // Change detection: skip full rebuild if data is unchanged
            int fingerprint = 17;
            fingerprint = fingerprint * 31 + (_selectedBuildingId?.GetHashCode() ?? 0);
            fingerprint = fingerprint * 31 + (PricingSaveData.Instance?.AutoPricingEnabled == true ? 1 : 0);
            fingerprint = fingerprint * 31 + (int)((PricingSaveData.Instance?.PricingMultiplier ?? 1.25f) * 1000);
            fingerprint = fingerprint * 31 + sorted.Count + buildings.Count * 1000000;
            foreach (var item in sorted)
            {
                fingerprint = fingerprint * 31 + (item.ProductId?.GetHashCode() ?? 0);
                fingerprint = fingerprint * 31 + item.Quantity;
                fingerprint = fingerprint * 31 + item.Quality;
                float price = PricingSaveData.Instance?.GetPrice(item.ProdDef) ?? item.MarketValue;
                fingerprint = fingerprint * 31 + (int)(price * 100);
                fingerprint = fingerprint * 31 + (PricingSaveData.Instance?.IsSellingDisabled(item.ProductId) == true ? 1 : 0);
            }
            if (fingerprint == _lastInventoryFingerprint)
                return;
            _lastInventoryFingerprint = fingerprint;

            // Update title with selected property name + inline hint
            if (_invTitleLabel != null)
            {
                string displayName = GetBuildingDisplayName(_selectedBuildingId);
                _invTitleLabel.text = $"<b>{displayName}</b>  <color=#9E9E9E><size=90%>Select another property via dropdown</size></color>";
            }

            // Clear tooltip entries + content
            HideTooltip();
            _inventoryTooltipEntries = new List<(RectTransform, string)>();
            for (int i = _inventoryContent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_inventoryContent.GetChild(i).gameObject);

            if (buildings.Count == 0)
            {
                var contentRect = _inventoryContent.GetComponent<RectTransform>();
                contentRect.sizeDelta = new Vector2(0, 30);
                var empty = TMPFactory.Text("EmptyInv", "No properties owned.",
                    _inventoryContent, 15, TextAlignmentOptions.Center);
                empty.color = TextDim;
                var emptyRect = empty.gameObject.GetComponent<RectTransform>();
                emptyRect.anchorMin = new Vector2(0.1f, 0);
                emptyRect.anchorMax = new Vector2(0.9f, 1);
                emptyRect.offsetMin = Vector2.zero;
                emptyRect.offsetMax = Vector2.zero;
                return;
            }

            float yOffset = 0f;
            float rowH = 28f;

            // ---- Pricing header ----
            yOffset = BuildPricingHeader(yOffset);

            if (sorted.Count == 0)
            {
                var contentRect = _inventoryContent.GetComponent<RectTransform>();
                contentRect.sizeDelta = new Vector2(0, -yOffset + 30);
                var empty = TMPFactory.Text("EmptyInv", "No products on display",
                    _inventoryContent, 15, TextAlignmentOptions.Center);
                empty.color = TextDim;
                var emptyRect = empty.gameObject.GetComponent<RectTransform>();
                emptyRect.anchorMin = new Vector2(0.1f, 0);
                emptyRect.anchorMax = new Vector2(0.9f, 1);
                emptyRect.offsetMin = Vector2.zero;
                emptyRect.offsetMax = new Vector2(0, yOffset);
                return;
            }

            // Column header
            var headerRow = UIFactory.Panel("InvColHeader", _inventoryContent, new Color(0.12f, 0.12f, 0.12f));
            var headerRect = headerRow.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = new Vector2(1, 1);
            headerRect.pivot = new Vector2(0.5f, 1);
            headerRect.sizeDelta = new Vector2(0, rowH);
            headerRect.anchoredPosition = new Vector2(0, yOffset);

            BuildInvHeaderCell(headerRow.transform, "Product", 0f, 0.40f, 14);
            BuildInvHeaderCell(headerRow.transform, "Qty", 0.40f, 0.52f, 4);
            BuildInvHeaderCell(headerRow.transform, "Quality", 0.52f, 0.72f, 4);
            BuildInvHeaderCell(headerRow.transform, "Price", 0.72f, 1f, 4);

            yOffset -= rowH + 2;

            for (int idx = 0; idx < sorted.Count; idx++)
            {
                var item = sorted[idx];
                bool isDisabled = PricingSaveData.Instance != null &&
                    PricingSaveData.Instance.IsSellingDisabled(item.ProductId);

                Color rowBg = idx % 2 == 0 ? Color.clear : new Color(0.10f, 0.10f, 0.10f, 0.5f);
                var row = UIFactory.Panel($"InvRow_{idx}", _inventoryContent, rowBg);
                var rRect = row.GetComponent<RectTransform>();
                rRect.anchorMin = new Vector2(0, 1);
                rRect.anchorMax = new Vector2(1, 1);
                rRect.pivot = new Vector2(0.5f, 1);
                rRect.sizeDelta = new Vector2(0, rowH);
                rRect.anchoredPosition = new Vector2(0, yOffset);

                // Make row clickable → navigate to product detail
                var rowBtn = row.AddComponent<Button>();
                rowBtn.transition = Selectable.Transition.ColorTint;
                var colors = rowBtn.colors;
                colors.normalColor = rowBg;
                colors.highlightedColor = new Color(0.18f, 0.22f, 0.18f);
                colors.pressedColor = new Color(0.14f, 0.20f, 0.14f);
                rowBtn.colors = colors;

                string capturedProductId = item.ProductId;
                string capturedProductName = item.Name;
                rowBtn.onClick.AddListener(
#if IL2CPP
                    (UnityEngine.Events.UnityAction)(() =>
#else
                    () =>
#endif
                    {
                        _selectedProductId = capturedProductId;
                        _selectedProductName = capturedProductName;
                        _lastInventoryFingerprint = int.MinValue; // force rebuild
                        RefreshInventory();
                    }
#if IL2CPP
                    )
#endif
                );

                // Product name
                Color nameColor = isDisabled
                    ? new Color(0.50f, 0.50f, 0.50f)
                    : new Color(0.85f, 0.85f, 0.85f);
                var nameLabel = TMPFactory.Text("Name", item.Name,
                    row.transform, 15, TextAlignmentOptions.Left);
                nameLabel.color = nameColor;
                nameLabel.overflowMode = TextOverflowModes.Ellipsis;
                nameLabel.raycastTarget = false;
                var nlRect = nameLabel.gameObject.GetComponent<RectTransform>();
                nlRect.anchorMin = new Vector2(0, 0);
                nlRect.anchorMax = new Vector2(0.40f, 1);
                nlRect.offsetMin = new Vector2(14, 0);
                nlRect.offsetMax = Vector2.zero;
                _inventoryTooltipEntries.Add((nlRect, item.Name));

                // Quantity
                var qtyLabel = TMPFactory.Text("Qty", $"x{item.Quantity}",
                    row.transform, 15, TextAlignmentOptions.Left, FontStyles.Bold);
                qtyLabel.color = isDisabled ? TextDim : AccentGreen;
                qtyLabel.raycastTarget = false;
                var qlRect = qtyLabel.gameObject.GetComponent<RectTransform>();
                qlRect.anchorMin = new Vector2(0.40f, 0);
                qlRect.anchorMax = new Vector2(0.52f, 1);
                qlRect.offsetMin = new Vector2(4, 0);
                qlRect.offsetMax = Vector2.zero;

                // Quality stars
                if (item.Quality > 0)
                {
                    var starsContainer = new GameObject("Stars");
                    starsContainer.transform.SetParent(row.transform, false);
                    var scRect = starsContainer.AddComponent<RectTransform>();
                    scRect.anchorMin = new Vector2(0.52f, 0);
                    scRect.anchorMax = new Vector2(0.72f, 1);
                    scRect.offsetMin = new Vector2(4, 0);
                    scRect.offsetMax = Vector2.zero;
                    CreateQualityStars(starsContainer.transform, item.Quality, 12f);
                }

                // Price
                string priceText;
                Color priceColor;
                if (isDisabled)
                {
                    priceText = "Disabled";
                    priceColor = new Color(0.9f, 0.3f, 0.3f);
                }
                else
                {
                    float price = PricingSaveData.Instance?.GetPrice(item.ProdDef) ?? item.MarketValue;
                    bool hasManualOverride = false;
                    if (PricingSaveData.Instance != null)
                    {
                        var ov = PricingSaveData.Instance.GetOverride(item.ProductId);
                        hasManualOverride = ov != null && ov.ManualPrice >= 0f;
                    }
                    priceText = $"${price:F2}";
                    priceColor = hasManualOverride
                        ? new Color(0.4f, 0.65f, 0.95f)  // blue for manual override
                        : new Color(0.85f, 0.85f, 0.85f); // white for auto
                }

                var priceLabel = TMPFactory.Text("Price", priceText,
                    row.transform, 15, TextAlignmentOptions.Left);
                priceLabel.color = priceColor;
                priceLabel.raycastTarget = false;
                var plRect = priceLabel.gameObject.GetComponent<RectTransform>();
                plRect.anchorMin = new Vector2(0.72f, 0);
                plRect.anchorMax = new Vector2(0.93f, 1);
                plRect.offsetMin = new Vector2(4, 0);
                plRect.offsetMax = Vector2.zero;

                // Chevron indicator (right edge)
                var chevSprite = GetChevronSprite();
                if (chevSprite != null)
                {
                    var chevGo = new GameObject("Chevron");
                    chevGo.transform.SetParent(row.transform, false);
                    var chevImg = chevGo.AddComponent<Image>();
                    chevImg.sprite = chevSprite;
                    chevImg.preserveAspect = true;
                    chevImg.raycastTarget = false;
                    chevImg.color = isDisabled ? TextDim : TextMuted;
                    var chevRt = chevGo.GetComponent<RectTransform>();
                    chevRt.anchorMin = new Vector2(1, 0.5f);
                    chevRt.anchorMax = new Vector2(1, 0.5f);
                    chevRt.pivot = new Vector2(1, 0.5f);
                    chevRt.sizeDelta = new Vector2(14, 14);
                    chevRt.anchoredPosition = new Vector2(-6, 0);
                }

                yOffset -= rowH;
            }

            var cRect = _inventoryContent.GetComponent<RectTransform>();
            cRect.sizeDelta = new Vector2(0, -yOffset);
        }

        /// <summary>Builds the pricing controls header. Returns the new yOffset.</summary>
        private float BuildPricingHeader(float startY)
        {
            float yOffset = startY;
            float headerH = 28f;

            var headerPanel = UIFactory.Panel("PricingHeader", _inventoryContent, new Color(0.09f, 0.09f, 0.09f));
            var hpRect = headerPanel.GetComponent<RectTransform>();
            hpRect.anchorMin = new Vector2(0, 1);
            hpRect.anchorMax = new Vector2(1, 1);
            hpRect.pivot = new Vector2(0.5f, 1);
            hpRect.sizeDelta = new Vector2(0, headerH);
            hpRect.anchoredPosition = new Vector2(0, yOffset);

            // Auto-pricing toggle label + button
            var autoLabel = TMPFactory.Text("AutoLabel", "Auto-Pricing:",
                headerPanel.transform, 15, TextAlignmentOptions.Left);
            autoLabel.color = TextMuted;
            autoLabel.raycastTarget = false;
            var alRect = autoLabel.gameObject.GetComponent<RectTransform>();
            alRect.anchorMin = new Vector2(0, 0);
            alRect.anchorMax = new Vector2(0.25f, 1);
            alRect.offsetMin = new Vector2(14, 0);
            alRect.offsetMax = Vector2.zero;

            bool autoEnabled = PricingSaveData.Instance?.AutoPricingEnabled ?? true;
            string autoText = autoEnabled ? "ON" : "OFF";
            Color autoColor = autoEnabled ? AccentGreen : new Color(0.9f, 0.3f, 0.3f);

            var autoBtnGo = UIFactory.Panel("AutoToggle", headerPanel.transform, new Color(0.15f, 0.15f, 0.15f));
            var autoBtnRect = autoBtnGo.GetComponent<RectTransform>();
            autoBtnRect.anchorMin = new Vector2(0.25f, 0.1f);
            autoBtnRect.anchorMax = new Vector2(0.35f, 0.9f);
            autoBtnRect.offsetMin = Vector2.zero;
            autoBtnRect.offsetMax = Vector2.zero;

            var autoPricingLabel = TMPFactory.Text("AutoText", autoText,
                autoBtnGo.transform, 15, TextAlignmentOptions.Center, FontStyles.Bold);
            autoPricingLabel.color = autoColor;
            autoPricingLabel.raycastTarget = false;
            var atRect = autoPricingLabel.gameObject.GetComponent<RectTransform>();
            atRect.anchorMin = Vector2.zero;
            atRect.anchorMax = Vector2.one;
            atRect.offsetMin = Vector2.zero;
            atRect.offsetMax = Vector2.zero;

            var autoBtn = autoBtnGo.AddComponent<Button>();
            autoBtn.onClick.AddListener(
#if IL2CPP
                (UnityEngine.Events.UnityAction)(() =>
#else
                () =>
#endif
                {
                    if (PricingSaveData.Instance == null) return;
                    PricingSaveData.Instance.AutoPricingEnabled = !PricingSaveData.Instance.AutoPricingEnabled;
                    _lastInventoryFingerprint = int.MinValue; // force rebuild
                    ConfigSyncData.Instance?.PublishPricingState();
                    RefreshInventory();
                }
#if IL2CPP
                )
#endif
            );

            // Multiplier label
            var multLabel = TMPFactory.Text("MultLabel", "Markup:",
                headerPanel.transform, 15, TextAlignmentOptions.Left);
            multLabel.color = TextMuted;
            multLabel.raycastTarget = false;
            var mlRect = multLabel.gameObject.GetComponent<RectTransform>();
            mlRect.anchorMin = new Vector2(0.40f, 0);
            mlRect.anchorMax = new Vector2(0.55f, 1);
            mlRect.offsetMin = new Vector2(4, 0);
            mlRect.offsetMax = Vector2.zero;

            float currentMult = PricingSaveData.Instance?.PricingMultiplier ?? 1.25f;

            // Minus button
            var minusBtnGo = UIFactory.Panel("MultMinus", headerPanel.transform, new Color(0.15f, 0.15f, 0.15f));
            var minusRect = minusBtnGo.GetComponent<RectTransform>();
            minusRect.anchorMin = new Vector2(0.55f, 0.1f);
            minusRect.anchorMax = new Vector2(0.62f, 0.9f);
            minusRect.offsetMin = Vector2.zero;
            minusRect.offsetMax = Vector2.zero;

            var minusLabel = TMPFactory.Text("MinusTxt", "-", minusBtnGo.transform,
                15, TextAlignmentOptions.Center, FontStyles.Bold);
            minusLabel.color = Color.white;
            minusLabel.raycastTarget = false;
            var minusLRect = minusLabel.gameObject.GetComponent<RectTransform>();
            minusLRect.anchorMin = Vector2.zero;
            minusLRect.anchorMax = Vector2.one;
            minusLRect.offsetMin = Vector2.zero;
            minusLRect.offsetMax = Vector2.zero;

            var minusBtn = minusBtnGo.AddComponent<Button>();
            minusBtn.onClick.AddListener(
#if IL2CPP
                (UnityEngine.Events.UnityAction)(() =>
#else
                () =>
#endif
                {
                    if (PricingSaveData.Instance == null) return;
                    float raw = PricingSaveData.Instance.PricingMultiplier - 0.05f;
                    PricingSaveData.Instance.PricingMultiplier =
                        Mathf.Max(0f, Mathf.Round(raw * 20f) / 20f);
                    _lastInventoryFingerprint = int.MinValue; // force rebuild
                    ConfigSyncData.Instance?.PublishPricingState();
                    RefreshInventory();
                }
#if IL2CPP
                )
#endif
            );

            // Multiplier value display
            var multiplierValueLabel = TMPFactory.Text("MultValue", $"{currentMult:F2}x",
                headerPanel.transform, 15, TextAlignmentOptions.Center, FontStyles.Bold);
            multiplierValueLabel.color = Color.white;
            multiplierValueLabel.raycastTarget = false;
            var mvRect = multiplierValueLabel.gameObject.GetComponent<RectTransform>();
            mvRect.anchorMin = new Vector2(0.62f, 0);
            mvRect.anchorMax = new Vector2(0.74f, 1);
            mvRect.offsetMin = Vector2.zero;
            mvRect.offsetMax = Vector2.zero;

            // Plus button
            var plusBtnGo = UIFactory.Panel("MultPlus", headerPanel.transform, new Color(0.15f, 0.15f, 0.15f));
            var plusRect = plusBtnGo.GetComponent<RectTransform>();
            plusRect.anchorMin = new Vector2(0.74f, 0.1f);
            plusRect.anchorMax = new Vector2(0.81f, 0.9f);
            plusRect.offsetMin = Vector2.zero;
            plusRect.offsetMax = Vector2.zero;

            var plusLabel = TMPFactory.Text("PlusTxt", "+", plusBtnGo.transform,
                15, TextAlignmentOptions.Center, FontStyles.Bold);
            plusLabel.color = Color.white;
            plusLabel.raycastTarget = false;
            var plusLRect = plusLabel.gameObject.GetComponent<RectTransform>();
            plusLRect.anchorMin = Vector2.zero;
            plusLRect.anchorMax = Vector2.one;
            plusLRect.offsetMin = Vector2.zero;
            plusLRect.offsetMax = Vector2.zero;

            var plusBtn = plusBtnGo.AddComponent<Button>();
            plusBtn.onClick.AddListener(
#if IL2CPP
                (UnityEngine.Events.UnityAction)(() =>
#else
                () =>
#endif
                {
                    if (PricingSaveData.Instance == null) return;
                    float raw = PricingSaveData.Instance.PricingMultiplier + 0.05f;
                    PricingSaveData.Instance.PricingMultiplier =
                        Mathf.Min(10f, Mathf.Round(raw * 20f) / 20f);
                    _lastInventoryFingerprint = int.MinValue; // force rebuild
                    ConfigSyncData.Instance?.PublishPricingState();
                    RefreshInventory();
                }
#if IL2CPP
                )
#endif
            );

            yOffset -= headerH + 4;
            return yOffset;
        }

        private static void BuildInvHeaderCell(Transform parent, string text, float xMin, float xMax, float padLeft)
        {
            var label = TMPFactory.Text($"Hdr_{text}", text, parent, 15, TextAlignmentOptions.Left, FontStyles.Bold);
            label.color = TextMuted;
            var rt = label.gameObject.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(xMin, 0);
            rt.anchorMax = new Vector2(xMax, 1);
            rt.offsetMin = new Vector2(padLeft, 0);
            rt.offsetMax = new Vector2(-2, 0);
        }

        internal struct InventoryItem
        {
            public string ProductId;
            public string Name;
            public int Quantity;
            public int Quality;
            public float MarketValue;
            public ProductDefinition ProdDef;
        }

        /// <summary>Enumerates display storage and aggregates products by productId + quality.</summary>
        private static List<InventoryItem> GatherInventoryItems(Grid grid)
        {
            var result = new List<InventoryItem>();
            if (grid == null) return result;

            var storages = PropertyInventory.GetStorages(grid, includePrivate: true);
            var aggregated = new Dictionary<string, InventoryItem>();

            foreach (var storage in storages)
            {
                if (storage?.ItemSlots == null) continue;
                for (int i = 0; i < storage.ItemSlots.Count; i++)
                {
                    var slot = storage.ItemSlots[i];
                    if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

                    string name = slot.ItemInstance.Name ?? "Unknown";
                    int quality = 0;
                    string productId = null;
                    float marketValue = 0f;
                    ProductDefinition prodDef = null;

#if IL2CPP
                    var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
                    prodDef = productItem?.Definition?.TryCast<ProductDefinition>();
#else
                    var productItem = slot.ItemInstance as ProductItemInstance;
                    prodDef = productItem?.Definition as ProductDefinition;
#endif
                    if (prodDef != null)
                    {
                        name = prodDef.Name ?? name;
                        quality = (int)(productItem?.Quality ?? 0);
                        productId = prodDef.ID;
                        marketValue = prodDef.MarketValue;
                    }

                    // Skip non-product items (raw materials etc.)
                    if (productId == null) continue;

                    string key = $"{productId}_{quality}";
                    if (aggregated.TryGetValue(key, out var existing))
                    {
                        existing.Quantity += slot.Quantity;
                        aggregated[key] = existing;
                    }
                    else
                    {
                        aggregated[key] = new InventoryItem
                        {
                            ProductId = productId,
                            Name = name,
                            Quantity = slot.Quantity,
                            Quality = quality,
                            MarketValue = marketValue,
                            ProdDef = prodDef
                        };
                    }
                }
            }

            result.AddRange(aggregated.Values);
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            return result;
        }
    }
}
