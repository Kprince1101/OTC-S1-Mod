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
using Il2CppScheduleOne.Effects;
using Grid = Il2CppScheduleOne.Tiles.Grid;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
using ItemInstance = Il2CppScheduleOne.ItemFramework.ItemInstance;
#else
using TMPro;
using ScheduleOne.Effects;
using Grid = ScheduleOne.Tiles.Grid;
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
using ItemInstance = ScheduleOne.ItemFramework.ItemInstance;
#endif

namespace OverTheCounter.Apps
{
    public partial class GreenTabApp
    {
        // ==================================================================
        //  Product detail screen — shown when a product is selected
        //  in the inventory list. Replaces the inventory list content.
        // ==================================================================

        /// <summary>
        /// Renders the product detail view into _inventoryContent.
        /// Called by RefreshInventory() when _selectedProductId is set.
        /// </summary>
        internal void RefreshProductDetail()
        {
            if (_inventoryContent == null) return;

            // Don't rebuild while user is typing in the price input field
            try
            {
                bool isTyping = false;
#if IL2CPP
                isTyping = Il2CppScheduleOne.GameInput.IsTyping;
#else
                isTyping = ScheduleOne.GameInput.IsTyping;
#endif
                if (isTyping) return;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.General, $"IsTyping check failed: {ex.Message}");
            }

            // Clear content
            HideTooltip();
            _inventoryTooltipEntries = new List<(RectTransform, string)>();
            for (int i = _inventoryContent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_inventoryContent.GetChild(i).gameObject);

            // Clear the title (detail view has its own name display)
            if (_invTitleLabel != null)
                _invTitleLabel.text = "";

            // Find the product definition and representative item from storage
            ProductDefinition prodDef = null;
            Sprite productIcon = null;
            int totalQuantity = 0;

            var buildings = GetBuildingsForSelection();
            foreach (var bid in buildings)
            {
                var grid = GetGridForBuilding(bid);
                if (grid == null) continue;
                var storages = PropertyInventory.GetStorages(grid, includePrivate: true);
                foreach (var storage in storages)
                {
                    if (storage?.ItemSlots == null) continue;
                    for (int i = 0; i < storage.ItemSlots.Count; i++)
                    {
                        var slot = storage.ItemSlots[i];
                        if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

#if IL2CPP
                        var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
                        var pd = productItem?.Definition?.TryCast<ProductDefinition>();
#else
                        var productItem = slot.ItemInstance as ProductItemInstance;
                        var pd = productItem?.Definition as ProductDefinition;
#endif
                        if (pd == null || pd.ID != _selectedProductId) continue;

                        totalQuantity += slot.Quantity;
                        if (prodDef == null)
                        {
                            prodDef = pd;
                            try { productIcon = slot.ItemInstance.Icon; }
                            catch (Exception ex)
                            {
                                OTCLog.Warning(OTCLog.Systems.General,
                                    $"Failed to get product icon for {_selectedProductId}: {ex.Message}");
                            }
                        }
                    }
                }
            }

            float yOffset = 0f;

            // ---- Back button (rounded) ----
            var (backMask, backButton, backLabel) = TMPFactory.RoundedButtonWithLabel(
                "BackBtn", "< Back", _inventoryContent,
                new Color(0.15f, 0.15f, 0.15f), 76, 26, 15, AccentGreen);
            var backMaskRect = backMask.GetComponent<RectTransform>();
            backMaskRect.anchorMin = new Vector2(0, 1);
            backMaskRect.anchorMax = new Vector2(0, 1);
            backMaskRect.pivot = new Vector2(0, 1);
            backMaskRect.anchoredPosition = new Vector2(4, yOffset);

            backButton.onClick.AddListener(
#if IL2CPP
                (UnityEngine.Events.UnityAction)(() =>
#else
                () =>
#endif
                {
                    _selectedProductId = null;
                    _selectedProductName = null;
                    _lastInventoryFingerprint = int.MinValue;
                    RefreshInventory();
                }
#if IL2CPP
                )
#endif
            );

            yOffset -= 28;

            if (prodDef == null)
            {
                var noProduct = TMPFactory.Text("NoProd", "Product not found in storage.",
                    _inventoryContent, 15, TextAlignmentOptions.Center);
                noProduct.color = TextDim;
                var npRect = noProduct.gameObject.GetComponent<RectTransform>();
                npRect.anchorMin = new Vector2(0.1f, 1);
                npRect.anchorMax = new Vector2(0.9f, 1);
                npRect.pivot = new Vector2(0.5f, 1);
                npRect.sizeDelta = new Vector2(0, 24);
                npRect.anchoredPosition = new Vector2(0, yOffset);

                var cRect = _inventoryContent.GetComponent<RectTransform>();
                cRect.sizeDelta = new Vector2(0, -yOffset + 30);
                return;
            }

            // ---- Top section: icon + name + basic info ----
            float topSectionH = 70f;
            var topPanel = UIFactory.Panel("TopSection", _inventoryContent, Color.clear);
            var topRect = topPanel.GetComponent<RectTransform>();
            topRect.anchorMin = new Vector2(0, 1);
            topRect.anchorMax = new Vector2(1, 1);
            topRect.pivot = new Vector2(0.5f, 1);
            topRect.sizeDelta = new Vector2(0, topSectionH);
            topRect.anchoredPosition = new Vector2(0, yOffset);

            // Product icon (left side)
            if (productIcon != null)
            {
                var iconGo = new GameObject("ProductIcon");
                iconGo.transform.SetParent(topPanel.transform, false);
                var iconImg = iconGo.AddComponent<Image>();
                iconImg.sprite = productIcon;
                iconImg.preserveAspect = true;
                iconImg.raycastTarget = false;
                var iconRt = iconGo.GetComponent<RectTransform>();
                iconRt.anchorMin = new Vector2(0, 0);
                iconRt.anchorMax = new Vector2(0, 1);
                iconRt.pivot = new Vector2(0, 0.5f);
                iconRt.sizeDelta = new Vector2(60, 0);
                iconRt.anchoredPosition = new Vector2(8, 0);
            }

            float textLeft = productIcon != null ? 0.18f : 0.02f;

            // Product name (large)
            var nameText = TMPFactory.Text("ProdName", prodDef.Name ?? _selectedProductName ?? "Product",
                topPanel.transform, 18, TextAlignmentOptions.TopLeft, FontStyles.Bold);
            nameText.color = Color.white;
            nameText.raycastTarget = false;
            var nameRect = nameText.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(textLeft, 0.5f);
            nameRect.anchorMax = new Vector2(0.7f, 1f);
            nameRect.offsetMin = Vector2.zero;
            nameRect.offsetMax = Vector2.zero;

            // Base price + inventory count
            float marketValue = prodDef.MarketValue;
            var infoText = TMPFactory.Text("ProdInfo",
                $"Market Value: ${marketValue:F2}  |  In Stock: {totalQuantity}",
                topPanel.transform, 15, TextAlignmentOptions.TopLeft);
            infoText.color = TextMuted;
            infoText.raycastTarget = false;
            var infoRect = infoText.gameObject.GetComponent<RectTransform>();
            infoRect.anchorMin = new Vector2(textLeft, 0f);
            infoRect.anchorMax = new Vector2(0.95f, 0.5f);
            infoRect.offsetMin = Vector2.zero;
            infoRect.offsetMax = Vector2.zero;

            yOffset -= topSectionH + 4;

            // ---- Effects list (multi-column: up to 4 columns, 8 rows each) ----
            if (prodDef.Properties != null && prodDef.Properties.Count > 0)
            {
                var effectsHeader = TMPFactory.Text("EffHdr", "Effects",
                    _inventoryContent, 15, TextAlignmentOptions.Left, FontStyles.Bold);
                effectsHeader.color = TextMuted;
                effectsHeader.raycastTarget = false;
                var ehRect = effectsHeader.gameObject.GetComponent<RectTransform>();
                ehRect.anchorMin = new Vector2(0, 1);
                ehRect.anchorMax = new Vector2(1, 1);
                ehRect.pivot = new Vector2(0.5f, 1);
                ehRect.sizeDelta = new Vector2(0, 20);
                ehRect.anchoredPosition = new Vector2(14, yOffset);

                yOffset -= 22;

                int effectCount = prodDef.Properties.Count;
                const int maxRows = 8;
                int columns = Mathf.CeilToInt(effectCount / (float)maxRows);
                if (columns < 1) columns = 1;
                if (columns > 4) columns = 4;
                int rows = Mathf.CeilToInt(effectCount / (float)columns);
                float colWidth = 1f / columns;
                float effectRowH = 18f;

                for (int ei = 0; ei < effectCount; ei++)
                {
                    var effect = prodDef.Properties[ei];
                    if (effect == null) continue;

                    int col = ei / rows;
                    int row = ei % rows;
                    if (col >= columns) col = columns - 1;

                    string effectName = effect.Name ?? effect.name ?? "Unknown";
                    Color effectColor;
                    try { effectColor = effect.LabelColor; }
                    catch
                    {
                        effectColor = Color.white;
                    }

                    float xMin = col * colWidth;
                    float xMax = (col + 1) * colWidth;

                    var effectLabel = TMPFactory.Text($"Eff_{ei}", $"  {effectName}",
                        _inventoryContent, 15, TextAlignmentOptions.Left);
                    effectLabel.color = effectColor;
                    effectLabel.raycastTarget = false;
                    var elRect = effectLabel.gameObject.GetComponent<RectTransform>();
                    elRect.anchorMin = new Vector2(xMin, 1);
                    elRect.anchorMax = new Vector2(xMax, 1);
                    elRect.pivot = new Vector2(0, 1);
                    elRect.sizeDelta = new Vector2(0, effectRowH);
                    elRect.anchoredPosition = new Vector2(xMin < 0.01f ? 14 : 4, yOffset - row * effectRowH);
                }

                yOffset -= rows * effectRowH + 4;
            }

            // ---- Pricing section ----
            var pricingHeader = TMPFactory.Text("PriceHdr", "Pricing",
                _inventoryContent, 15, TextAlignmentOptions.Left, FontStyles.Bold);
            pricingHeader.color = TextMuted;
            pricingHeader.raycastTarget = false;
            var phRect = pricingHeader.gameObject.GetComponent<RectTransform>();
            phRect.anchorMin = new Vector2(0, 1);
            phRect.anchorMax = new Vector2(1, 1);
            phRect.pivot = new Vector2(0.5f, 1);
            phRect.sizeDelta = new Vector2(0, 20);
            phRect.anchoredPosition = new Vector2(14, yOffset);

            yOffset -= 24;

            float currentPrice = PricingSaveData.Instance?.GetPrice(prodDef) ?? marketValue;
            var overrideEntry = PricingSaveData.Instance?.GetOverride(_selectedProductId);
            bool hasManual = overrideEntry != null && overrideEntry.ManualPrice >= 0f;
            bool isDisabled = overrideEntry != null && overrideEntry.SellingDisabled;

            // Current selling price
            string priceMode = hasManual ? "Manual" : "Auto";
            var priceRow = TMPFactory.Text("CurPrice",
                $"  Selling Price: <b>${currentPrice:F2}</b>  <color=#9E9E9E>({priceMode})</color>",
                _inventoryContent, 15, TextAlignmentOptions.Left);
            priceRow.color = Color.white;
            priceRow.richText = true;
            priceRow.raycastTarget = false;
            var prRect = priceRow.gameObject.GetComponent<RectTransform>();
            prRect.anchorMin = new Vector2(0, 1);
            prRect.anchorMax = new Vector2(0.95f, 1);
            prRect.pivot = new Vector2(0, 1);
            prRect.sizeDelta = new Vector2(0, 20);
            prRect.anchoredPosition = new Vector2(14, yOffset);

            yOffset -= 28;

            // ---- Selling enabled toggle (POS-style pill toggle) ----
            float toggleRowH = 26f;
            var toggleRow = UIFactory.Panel("ToggleRow", _inventoryContent, Color.clear);
            var trRect = toggleRow.GetComponent<RectTransform>();
            trRect.anchorMin = new Vector2(0, 1);
            trRect.anchorMax = new Vector2(1, 1);
            trRect.pivot = new Vector2(0, 1);
            trRect.sizeDelta = new Vector2(0, toggleRowH);
            trRect.anchoredPosition = new Vector2(0, yOffset);

            var toggleLabel = TMPFactory.Text("ToggleLabel", "  Selling Enabled:",
                toggleRow.transform, 15, TextAlignmentOptions.Left);
            toggleLabel.color = TextMuted;
            toggleLabel.raycastTarget = false;
            var tlRect = toggleLabel.gameObject.GetComponent<RectTransform>();
            tlRect.anchorMin = new Vector2(0, 0);
            tlRect.anchorMax = new Vector2(0.35f, 1);
            tlRect.offsetMin = new Vector2(14, 0);
            tlRect.offsetMax = Vector2.zero;

            // Pill toggle
            bool sellingOn = !isDisabled;
            float trackW = 36f, trackH = 18f, thumbSize = 14f;
            float thumbPad = (trackH - thumbSize) * 0.5f;

            var trackGo = new GameObject("SellingToggle");
            trackGo.AddComponent<RectTransform>();
            trackGo.AddComponent<CanvasRenderer>();
            trackGo.AddComponent<Image>();
            trackGo.AddComponent<Button>();
            trackGo.transform.SetParent(toggleRow.transform, false);
            var trackRect = trackGo.GetComponent<RectTransform>();
            trackRect.anchorMin = new Vector2(0.35f, 0.5f);
            trackRect.anchorMax = new Vector2(0.35f, 0.5f);
            trackRect.pivot = new Vector2(0, 0.5f);
            trackRect.sizeDelta = new Vector2(trackW, trackH);
            trackRect.anchoredPosition = Vector2.zero;

            var trackImg = trackGo.GetComponent<Image>();
            trackImg.sprite = TMPFactory.GetRoundedSprite();
            trackImg.type = Image.Type.Sliced;
            trackImg.color = sellingOn ? ToggleOnColor : ToggleOffColor;

            var thumbGo = new GameObject("Thumb");
            thumbGo.AddComponent<RectTransform>();
            thumbGo.AddComponent<CanvasRenderer>();
            thumbGo.AddComponent<Image>();
            thumbGo.transform.SetParent(trackGo.transform, false);
            var thumbRect = thumbGo.GetComponent<RectTransform>();
            thumbRect.anchorMin = new Vector2(sellingOn ? 1 : 0, 0.5f);
            thumbRect.anchorMax = new Vector2(sellingOn ? 1 : 0, 0.5f);
            thumbRect.pivot = new Vector2(sellingOn ? 1 : 0, 0.5f);
            thumbRect.sizeDelta = new Vector2(thumbSize, thumbSize);
            thumbRect.anchoredPosition = new Vector2(sellingOn ? -thumbPad : thumbPad, 0);

            var thumbImg = thumbGo.GetComponent<Image>();
            thumbImg.sprite = TMPFactory.GetRoundedSprite();
            thumbImg.type = Image.Type.Sliced;
            thumbImg.color = Color.white;

            var toggleBtn = trackGo.GetComponent<Button>();
            toggleBtn.transition = Selectable.Transition.None;
            string capturedId = _selectedProductId;
            toggleBtn.onClick.AddListener(
#if IL2CPP
                (UnityEngine.Events.UnityAction)(() =>
#else
                () =>
#endif
                {
                    if (PricingSaveData.Instance == null) return;
                    bool curDisabled = PricingSaveData.Instance.IsSellingDisabled(capturedId);
                    PricingSaveData.Instance.SetSellingDisabled(capturedId, !curDisabled);
                    _lastInventoryFingerprint = int.MinValue;
                    if (NetworkHelper.IsHost) ConfigSyncData.MarkPricingStateDirty();
                    else ConfigSyncData.SendQuestAction($"PRICING_STATE:{PricingSaveData.Instance.Serialize()}");
                    RefreshInventory();
                }
#if IL2CPP
                )
#endif
            );

            yOffset -= toggleRowH + 4;

            // ---- Manual price override (text input) ----
            float manualRowH = 26f;
            var manualRow = UIFactory.Panel("ManualRow", _inventoryContent, Color.clear);
            var mrRect = manualRow.GetComponent<RectTransform>();
            mrRect.anchorMin = new Vector2(0, 1);
            mrRect.anchorMax = new Vector2(1, 1);
            mrRect.pivot = new Vector2(0, 1);
            mrRect.sizeDelta = new Vector2(0, manualRowH);
            mrRect.anchoredPosition = new Vector2(0, yOffset);

            var manualLabel = TMPFactory.Text("ManualLabel", "  Manual Price:",
                manualRow.transform, 15, TextAlignmentOptions.Left);
            manualLabel.color = TextMuted;
            manualLabel.raycastTarget = false;
            var manualLRect = manualLabel.gameObject.GetComponent<RectTransform>();
            manualLRect.anchorMin = new Vector2(0, 0);
            manualLRect.anchorMax = new Vector2(0.35f, 1);
            manualLRect.offsetMin = new Vector2(14, 0);
            manualLRect.offsetMax = Vector2.zero;

            // Text input field for price
            string inputInitial = hasManual ? $"{overrideEntry.ManualPrice:F2}" : "";
            BuildPriceInputField(manualRow.transform, 0.35f, 0.58f, capturedId, inputInitial, prodDef);

            // Clear button
            if (hasManual)
            {
                var (clearMask, clearBtn, clearLbl) = TMPFactory.RoundedButtonWithLabel(
                    "ClearBtn", "Clear", manualRow.transform,
                    new Color(0.25f, 0.12f, 0.12f), 50, 20, 15, new Color(0.9f, 0.5f, 0.5f));
                var clearMaskRect = clearMask.GetComponent<RectTransform>();
                clearMaskRect.anchorMin = new Vector2(0.60f, 0.5f);
                clearMaskRect.anchorMax = new Vector2(0.60f, 0.5f);
                clearMaskRect.pivot = new Vector2(0, 0.5f);
                clearMaskRect.anchoredPosition = Vector2.zero;

                clearBtn.onClick.AddListener(
#if IL2CPP
                    (UnityEngine.Events.UnityAction)(() =>
#else
                    () =>
#endif
                    {
                        PricingSaveData.Instance?.ClearManualPrice(capturedId);
                        _lastInventoryFingerprint = int.MinValue;
                        if (NetworkHelper.IsHost) ConfigSyncData.MarkPricingStateDirty();
                        else ConfigSyncData.SendQuestAction($"PRICING_STATE:{PricingSaveData.Instance?.Serialize()}");
                        RefreshInventory();
                    }
#if IL2CPP
                    )
#endif
                );
            }

            yOffset -= manualRowH + 8;

            var contentRectFinal = _inventoryContent.GetComponent<RectTransform>();
            contentRectFinal.sizeDelta = new Vector2(0, -yOffset);
        }

        private void BuildPriceInputField(Transform parent, float xMin, float xMax,
            string productId, string initialValue, ProductDefinition prodDef)
        {
            // Background panel for the input field
            var inputBg = UIFactory.Panel("PriceInputBg", parent, new Color(0.12f, 0.12f, 0.12f));
            var bgRect = inputBg.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(xMin, 0.05f);
            bgRect.anchorMax = new Vector2(xMax, 0.95f);
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            // Dollar sign prefix
            var prefix = TMPFactory.Text("Prefix", "$", inputBg.transform,
                15, TextAlignmentOptions.Left);
            prefix.color = TextMuted;
            prefix.raycastTarget = false;
            var prefixRect = prefix.gameObject.GetComponent<RectTransform>();
            prefixRect.anchorMin = Vector2.zero;
            prefixRect.anchorMax = new Vector2(0.15f, 1);
            prefixRect.offsetMin = new Vector2(4, 0);
            prefixRect.offsetMax = Vector2.zero;

            // Text viewport
            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(inputBg.transform, false);
            var vpRect = viewport.AddComponent<RectTransform>();
            vpRect.anchorMin = new Vector2(0.15f, 0);
            vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = new Vector2(-4, 0);
            viewport.AddComponent<RectMask2D>();

            // Input text
            var inputText = TMPFactory.Text("InputText", initialValue, viewport.transform,
                15, TextAlignmentOptions.Left, FontStyles.Bold);
            inputText.color = string.IsNullOrEmpty(initialValue) ? TextMuted : new Color(0.4f, 0.65f, 0.95f);
            var itRect = inputText.gameObject.GetComponent<RectTransform>();
            itRect.anchorMin = Vector2.zero;
            itRect.anchorMax = Vector2.one;
            itRect.offsetMin = Vector2.zero;
            itRect.offsetMax = Vector2.zero;

            // Placeholder
            var placeholder = TMPFactory.Text("Placeholder", "Auto", viewport.transform,
                15, TextAlignmentOptions.Left, FontStyles.Italic);
            placeholder.color = TextDim;
            var phRect = placeholder.gameObject.GetComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.offsetMin = Vector2.zero;
            phRect.offsetMax = Vector2.zero;

            // TMP_InputField component
            var inputField = inputBg.AddComponent<TMP_InputField>();
            inputField.textViewport = vpRect;
            inputField.textComponent = inputText;
            inputField.placeholder = placeholder;
            inputField.fontAsset = inputText.font;
            inputField.pointSize = 15;
            inputField.contentType = TMP_InputField.ContentType.DecimalNumber;
            inputField.text = initialValue;

            // Lock game input while typing
            inputField.onSelect.AddListener(new Action<string>(_ =>
            {
#if IL2CPP
                Il2CppScheduleOne.GameInput.IsTyping = true;
#else
                ScheduleOne.GameInput.IsTyping = true;
#endif
            }));

            inputField.onDeselect.AddListener(new Action<string>(_ =>
            {
#if IL2CPP
                Il2CppScheduleOne.GameInput.IsTyping = false;
#else
                ScheduleOne.GameInput.IsTyping = false;
#endif
            }));

            // Apply price on end edit
            inputField.onEndEdit.AddListener(new Action<string>(text =>
            {
                // Clear typing lock before refresh (onDeselect may fire after onEndEdit)
#if IL2CPP
                Il2CppScheduleOne.GameInput.IsTyping = false;
#else
                ScheduleOne.GameInput.IsTyping = false;
#endif
                if (PricingSaveData.Instance == null) return;
                if (string.IsNullOrWhiteSpace(text))
                {
                    PricingSaveData.Instance.ClearManualPrice(productId);
                }
                else if (float.TryParse(text, out float val) && val >= 0f)
                {
                    PricingSaveData.Instance.SetManualPrice(productId, val);
                }
                _lastInventoryFingerprint = int.MinValue; // force rebuild
                if (NetworkHelper.IsHost) ConfigSyncData.MarkPricingStateDirty();
                else ConfigSyncData.SendQuestAction($"PRICING_STATE:{PricingSaveData.Instance?.Serialize()}");
                RefreshInventory();
            }));
        }
    }
}
