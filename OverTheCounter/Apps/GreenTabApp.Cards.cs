using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using S1API.UI;
using System;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppTMPro;
#else
using TMPro;
#endif

namespace OverTheCounter.Apps
{
    public partial class GreenTabApp
    {
        // ==================================================================
        //  Card grid area (scrollable)
        // ==================================================================

        private void BuildCardArea(Transform parent)
        {
            float contentLeft = NAV_WIDTH_FRAC + SIDEBAR_WIDTH_FRAC;

            // Scroll view container (hidden when not on Customize tab)
            var scrollContainer = UIFactory.Panel("CardScrollContainer", parent, Color.clear);
            _cardScrollContainer = scrollContainer;
            var scrollContRect = scrollContainer.GetComponent<RectTransform>();
            scrollContRect.anchorMin = new Vector2(contentLeft, 0);
            scrollContRect.anchorMax = Vector2.one;
            scrollContRect.offsetMin = new Vector2(0, FOOTER_HEIGHT);
            scrollContRect.offsetMax = new Vector2(0, -HEADER_HEIGHT); // below top bar

            // ScrollRect
            var scrollView = UIFactory.Panel("CardScrollView", scrollContainer.transform, Color.clear);
            var scrollViewRect = scrollView.GetComponent<RectTransform>();
            scrollViewRect.anchorMin = Vector2.zero;
            scrollViewRect.anchorMax = Vector2.one;
            scrollViewRect.offsetMin = Vector2.zero;
            scrollViewRect.offsetMax = Vector2.zero;

            _cardScroll = scrollView.AddComponent<ScrollRect>();
            _cardScroll.horizontal = false;
            _cardScroll.vertical = true;
            _cardScroll.movementType = ScrollRect.MovementType.Clamped;

            // Viewport (mask)
            var viewport = UIFactory.Panel("Viewport", scrollView.transform, Color.clear);
            var vpRect = viewport.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = Vector2.zero;
            viewport.AddComponent<RectMask2D>();

            // Content
            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;

            _cardScroll.viewport = vpRect;
            _cardScroll.content = contentRect;

            _cardGrid = content.transform;
        }

        // ==================================================================
        //  Footer bar (balance + apply button)
        // ==================================================================

        private void BuildFooter(Transform parent)
        {
            float contentLeft = NAV_WIDTH_FRAC + SIDEBAR_WIDTH_FRAC;

            var footer = UIFactory.Panel("Footer", parent, FooterBg);
            _footerPanel = footer;
            var footerRect = footer.GetComponent<RectTransform>();
            footerRect.anchorMin = new Vector2(contentLeft, 0);
            footerRect.anchorMax = new Vector2(1, 0);
            footerRect.pivot = new Vector2(0.5f, 0);
            footerRect.sizeDelta = new Vector2(0, FOOTER_HEIGHT);
            footerRect.anchoredPosition = Vector2.zero;

            // Unified rounded panel — balance (left) + apply button (right)
            var balPanel = RoundedPanel("BalPanel", footer.transform,
                new Color(0.12f, 0.12f, 0.12f));
            var balPanelRect = balPanel.GetComponent<RectTransform>();
            balPanelRect.anchorMin = Vector2.zero;
            balPanelRect.anchorMax = Vector2.one;
            balPanelRect.offsetMin = new Vector2(8, 5);
            balPanelRect.offsetMax = new Vector2(-8, -5);

            // Balance label — top-left inside panel
            var balLabel = TMPFactory.Text("BalLabel", "CURRENT BALANCE",
                balPanel.transform, 15, TextAlignmentOptions.Left);
            balLabel.color = TextMuted;
            var balLabelRect = balLabel.gameObject.GetComponent<RectTransform>();
            balLabelRect.anchorMin = new Vector2(0, 0.55f);
            balLabelRect.anchorMax = new Vector2(0.6f, 1);
            balLabelRect.offsetMin = new Vector2(14, 0);
            balLabelRect.offsetMax = new Vector2(0, -2);

            // Balance amount — bottom-left inside panel
            _balanceText = TMPFactory.Text("BalAmount", "$0",
                balPanel.transform, 22, TextAlignmentOptions.Left, FontStyles.Bold);
            _balanceText.color = AccentGreen;
            var balRect = _balanceText.gameObject.GetComponent<RectTransform>();
            balRect.anchorMin = Vector2.zero;
            balRect.anchorMax = new Vector2(0.6f, 0.60f);
            balRect.offsetMin = new Vector2(14, 0);
            balRect.offsetMax = Vector2.zero;

            // Apply button — right side inside panel
            var (applyMask, applyBtnComp, applyLabel) = TMPFactory.RoundedButtonWithLabel(
                "ApplyBtn", "APPLY", balPanel.transform,
                AccentGreenDark, 100, 30, 15, Color.white);
            var applyMaskRect = applyMask.GetComponent<RectTransform>();
            applyMaskRect.anchorMin = new Vector2(1, 0.5f);
            applyMaskRect.anchorMax = new Vector2(1, 0.5f);
            applyMaskRect.pivot = new Vector2(1, 0.5f);
            applyMaskRect.anchoredPosition = new Vector2(-10, 0);

            _applyBtn = applyBtnComp;
            _applyBtnText = applyLabel;
            _applyBtnImage = applyBtnComp.targetGraphic as Image;
            _applyBtn.onClick.AddListener(new Action(OnApplyClicked));
        }

        // ==================================================================
        //  Card refresh dispatcher
        // ==================================================================

        private void RefreshCards()
        {
            // Clear all children of card grid (tracked cards + untracked headers/dividers)
            _cardEntries.Clear();
            for (int i = _cardGrid.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_cardGrid.GetChild(i).gameObject);

            if (_activeCategory == Category.CheckoutDesk)
                RefreshDeskCards();
            else if (_activeCategory == Category.Lighting)
                RefreshLightingCards();
            else if (_activeCategory == Category.Walls)
                RefreshWallCards();
            else if (_activeCategory == Category.Flooring)
                RefreshFloorCards();

            RefreshFooter();
        }

        private void BuildColorRow(Transform parent, string label, Color current, Action<Color> onConfirm)
        {
            var row = RoundedPanel($"Color_{label}", parent, new Color(0.18f, 0.18f, 0.2f));
            var rowLE = row.AddComponent<LayoutElement>();
            rowLE.preferredHeight = 63;
            rowLE.flexibleHeight = 0;

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 10;
            hlg.childForceExpandHeight = false;
            hlg.childForceExpandWidth = false;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.padding = new RectOffset(12, 12, 0, 0);

            var lbl = TMPFactory.Text($"Lbl_{label}", label, row.transform,
                17, TextAlignmentOptions.Left);
            lbl.color = TextMuted;
            var lblLE = lbl.gameObject.AddComponent<LayoutElement>();
            lblLE.flexibleWidth = 1;

            // Clickable color swatch
            var swatchGo = RoundedPanel($"Swatch_{label}", row.transform, current);
            var swatchLE = swatchGo.AddComponent<LayoutElement>();
            swatchLE.preferredWidth = 120;
            swatchLE.preferredHeight = 36;

            var swatchImg = swatchGo.GetComponent<Image>();
            var swatchBtn = swatchGo.AddComponent<Button>();
            swatchBtn.targetGraphic = swatchImg;
            swatchBtn.onClick.AddListener(new Action(() =>
            {
                ColorPickerPopup.Show(swatchImg.color, $"Pick {label}", color =>
                {
                    if (swatchImg != null) swatchImg.color = color;
                    onConfirm?.Invoke(color);
                });
            }));
        }

        private void UpdateCardVisuals()
        {
            float balance = GetOnlineBalance();

            if (_activeCategory == Category.CheckoutDesk)
            {
                var counter = GetSelectedCounter();
                string currentStyle = counter?.CurrentDeskStyleId ?? DeskStyle.Default.Id;

                for (int i = 0; i < _cardEntries.Count; i++)
                {
                    var entry = _cardEntries[i];
                    if (entry.Card == null) continue;

                    bool isEquipped = entry.StyleId == currentStyle;
                    bool isSelected = entry.StyleId == _pendingStyleId;
                    bool canAfford = CanAffordUpgrade(currentStyle, entry.StyleId, balance);

                    entry.CardImage.color = isSelected ? CardSelected : CardBg;
                    entry.EquippedBadge.SetActive(isEquipped);
                    entry.LockOverlay.SetActive(!canAfford && !isEquipped);
                }
            }
            else if (_activeCategory == Category.Lighting)
            {
                string currentLighting = GetCurrentLightingStyleId();

                for (int i = 0; i < _cardEntries.Count; i++)
                {
                    var entry = _cardEntries[i];
                    if (entry.Card == null) continue;

                    bool isEquipped = entry.StyleId == currentLighting;
                    bool isSelected = entry.StyleId == _pendingLightingId;
                    bool canAfford = CanAffordLighting(currentLighting, entry.StyleId, balance);

                    entry.CardImage.color = isSelected ? CardSelected : CardBg;
                    entry.EquippedBadge.SetActive(isEquipped);
                    entry.LockOverlay.SetActive(!canAfford && !isEquipped);
                }
            }
            else if (_activeCategory == Category.Walls)
            {
                string currentExt = GetCurrentExteriorWallStyleId();
                string currentInt = GetCurrentInteriorWallStyleId();

                for (int i = 0; i < _cardEntries.Count; i++)
                {
                    var entry = _cardEntries[i];
                    if (entry.Card == null) continue;

                    bool isExterior = entry.StyleId.StartsWith("ext:");
                    string actualId = entry.StyleId.Substring(4);
                    string currentStyle = isExterior ? currentExt : currentInt;
                    string pendingStyle = isExterior ? _pendingExteriorWallId : _pendingInteriorWallId;

                    bool isEquipped = actualId == currentStyle;
                    bool isSelected = actualId == pendingStyle;
                    bool canAfford = CanAffordWall(currentStyle, actualId, balance, isExterior);

                    entry.CardImage.color = isSelected ? CardSelected : CardBg;
                    entry.EquippedBadge.SetActive(isEquipped);
                    entry.LockOverlay.SetActive(!canAfford && !isEquipped);
                }
            }
            else if (_activeCategory == Category.Flooring)
            {
                string currentFloor = GetCurrentFloorStyleId();

                for (int i = 0; i < _cardEntries.Count; i++)
                {
                    var entry = _cardEntries[i];
                    if (entry.Card == null) continue;

                    bool isEquipped = entry.StyleId == currentFloor;
                    bool isSelected = entry.StyleId == _pendingFloorId;
                    bool canAfford = CanAffordFloor(currentFloor, entry.StyleId, balance);

                    entry.CardImage.color = isSelected ? CardSelected : CardBg;
                    entry.EquippedBadge.SetActive(isEquipped);
                    entry.LockOverlay.SetActive(!canAfford && !isEquipped);
                }
            }
        }

        // ==================================================================
        //  Footer refresh
        // ==================================================================

        private void RefreshFooter()
        {
            float balance = GetOnlineBalance();

            if (_balanceText != null)
                _balanceText.text = $"${balance:F0}";

            bool hasChange = false;

            if (_activeCategory == Category.CheckoutDesk)
            {
                hasChange = _pendingStyleId != null
                    && GetSelectedCounter() != null
                    && _pendingStyleId != (GetSelectedCounter().CurrentDeskStyleId ?? DeskStyle.Default.Id);
            }
            else if (_activeCategory == Category.Lighting)
            {
                string currentLighting = GetCurrentLightingStyleId();
                hasChange = _pendingLightingId != null && _pendingLightingId != currentLighting;
            }
            else if (_activeCategory == Category.Walls)
            {
                string currentExt = GetCurrentExteriorWallStyleId();
                string currentInt = GetCurrentInteriorWallStyleId();
                hasChange = (_pendingExteriorWallId != null && _pendingExteriorWallId != currentExt)
                         || (_pendingInteriorWallId != null && _pendingInteriorWallId != currentInt);
            }
            else if (_activeCategory == Category.Flooring)
            {
                string currentFloor = GetCurrentFloorStyleId();
                hasChange = _pendingFloorId != null && _pendingFloorId != currentFloor;
            }

            if (_applyBtnImage != null)
                _applyBtnImage.color = hasChange ? AccentGreenDark : TextDim;
            if (_applyBtnText != null)
                _applyBtnText.color = hasChange ? Color.white : new Color(0.6f, 0.6f, 0.6f);
        }

        // ==================================================================
        //  Click routing
        // ==================================================================

        private void OnCardClicked(string styleId)
        {
            float balance = GetOnlineBalance();

            if (_activeCategory == Category.CheckoutDesk)
            {
                var counter = GetSelectedCounter();
                if (counter == null) return;

                string currentStyle = counter.CurrentDeskStyleId ?? DeskStyle.Default.Id;

                if (!CanAffordUpgrade(currentStyle, styleId, balance) && styleId != currentStyle)
                    return;

                _pendingStyleId = styleId;
                _pendingStyle = DeskStyle.Get(styleId);
            }
            else if (_activeCategory == Category.Lighting)
            {
                string currentLighting = GetCurrentLightingStyleId();

                if (!CanAffordLighting(currentLighting, styleId, balance) && styleId != currentLighting)
                    return;

                _pendingLightingId = styleId;
            }
            else if (_activeCategory == Category.Walls)
            {
                bool isExterior = styleId.StartsWith("ext:");
                string actualId = styleId.Substring(4);

                if (isExterior)
                {
                    string currentExt = GetCurrentExteriorWallStyleId();
                    if (!CanAffordWall(currentExt, actualId, balance, true) && actualId != currentExt)
                        return;
                    _pendingExteriorWallId = actualId;
                }
                else
                {
                    string currentInt = GetCurrentInteriorWallStyleId();
                    if (!CanAffordWall(currentInt, actualId, balance, false) && actualId != currentInt)
                        return;
                    _pendingInteriorWallId = actualId;
                }
            }
            else if (_activeCategory == Category.Flooring)
            {
                string currentFloor = GetCurrentFloorStyleId();

                if (!CanAffordFloor(currentFloor, styleId, balance) && styleId != currentFloor)
                    return;

                _pendingFloorId = styleId;
            }

            UpdateCardVisuals();
            RefreshFooter();
        }

        private void OnApplyClicked()
        {
            if (_activeCategory == Category.CheckoutDesk)
                ApplyDeskUpgrade();
            else if (_activeCategory == Category.Lighting)
                ApplyLightingUpgrade();
            else if (_activeCategory == Category.Walls)
                ApplyWallUpgrade();
            else if (_activeCategory == Category.Flooring)
                ApplyFloorUpgrade();
        }
    }
}
