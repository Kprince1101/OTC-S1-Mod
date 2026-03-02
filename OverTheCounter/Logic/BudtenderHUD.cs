using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.UI.Items;
using Il2CppTMPro;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Product;
using ScheduleOne.UI.Items;
using TMPro;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// ScreenSpace Overlay HUD showing available products as clickable sprites
    /// at the bottom of the screen during the budtending checkout process.
    /// </summary>
    public static class BudtenderHUD
    {
        /// <summary>Data for a single sprite slot in the HUD.</summary>
        public struct SpriteItem
        {
            public string ProductId;
            public string PackagingId;
            public string ProductName;
            public int QualityLevel;
        }

        private static GameObject _canvasGo;
        private static GameObject _panelGo;
        private static GameObject _hintsGo;
        private static GameObject _completeBtnGo;
        private static readonly List<GameObject> _slots = new();
        private static Action<int> _onClickCallback;
        private static Action _onCompleteCallback;

        // Layout
        private const float SlotSize = 80f;
        private const float SlotSpacing = 10f;
        private const float IconSize = 56f;
        private const float PanelPadding = 16f;
        private const float BottomMargin = 40f;

        // Colors
        private static readonly Color PanelBg = new(0.05f, 0.05f, 0.05f, 0.75f);
        private static readonly Color SlotBg = new(0.15f, 0.15f, 0.15f, 0.6f);
        private static readonly Color SlotHover = new(0.25f, 0.35f, 0.25f, 0.8f);
        private static readonly Color LabelColor = new(0.85f, 0.9f, 0.85f);

        /// <summary>Whether the HUD is currently visible.</summary>
        public static bool IsVisible => _canvasGo != null && _canvasGo.activeSelf;

        /// <summary>
        /// Shows the HUD with the given product sprites. Player clicks a sprite to place it.
        /// </summary>
        /// <param name="items">Products available for placement.</param>
        /// <param name="onClickCallback">Called with the slot index when the player clicks a sprite.</param>
        public static void Show(List<SpriteItem> items, Action<int> onClickCallback)
        {
            Hide();

            _onClickCallback = onClickCallback;

            // Create overlay canvas
            _canvasGo = new GameObject("OTC_BudtenderHUD");
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            _canvasGo.AddComponent<CanvasScaler>();
            _canvasGo.AddComponent<GraphicRaycaster>();

            // Panel at bottom center (only if there are sprites to show)
            if (items != null && items.Count > 0)
            {
                float totalWidth = items.Count * SlotSize + (items.Count - 1) * SlotSpacing + PanelPadding * 2f;
                float panelHeight = SlotSize + PanelPadding * 2f + 20f; // extra for label

                _panelGo = new GameObject("Panel");
                _panelGo.transform.SetParent(_canvasGo.transform, false);
                var panelRt = _panelGo.AddComponent<RectTransform>();
                panelRt.anchorMin = new Vector2(0.5f, 0f);
                panelRt.anchorMax = new Vector2(0.5f, 0f);
                panelRt.pivot = new Vector2(0.5f, 0f);
                panelRt.sizeDelta = new Vector2(totalWidth, panelHeight);
                panelRt.anchoredPosition = new Vector2(0f, BottomMargin);

                var panelImg = _panelGo.AddComponent<Image>();
                panelImg.color = PanelBg;
                panelImg.raycastTarget = false;

                // Create sprite slots
                float startX = -((items.Count - 1) * (SlotSize + SlotSpacing)) / 2f;

                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    float xPos = startX + i * (SlotSize + SlotSpacing);

                    var slotGo = CreateSlot(i, item, xPos, _panelGo.transform);
                    _slots.Add(slotGo);
                }
            }

            // Control hints panel (bottom-left)
            _hintsGo = CreateControlHints(_canvasGo.transform);
        }

        /// <summary>Hides and destroys the HUD.</summary>
        public static void Hide()
        {
            _slots.Clear();
            _onClickCallback = null;
            if (_canvasGo != null)
            {
                UnityEngine.Object.Destroy(_canvasGo);
                _canvasGo = null;
            }
            _panelGo = null;
            _hintsGo = null;
            _completeBtnGo = null;
            _onCompleteCallback = null;
        }

        /// <summary>Removes a single sprite slot after it's been placed on the counter.</summary>
        public static void RemoveItem(int index)
        {
            if (index < 0 || index >= _slots.Count) return;
            var slot = _slots[index];
            if (slot != null)
                UnityEngine.Object.Destroy(slot);
            _slots[index] = null;
        }

        /// <summary>Returns true if all slots have been placed (all removed).</summary>
        public static bool AllPlaced()
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i] != null) return false;
            }
            return _slots.Count > 0;
        }

        /// <summary>
        /// Shows a "Complete Sale" button in the center of the sprite panel.
        /// Called when all available sprites are placed but the order is partial.
        /// </summary>
        public static void ShowCompleteButton(Action onComplete)
        {
            if (_canvasGo == null) return;
            _onCompleteCallback = onComplete;

            // Parent to sprite panel if it exists, otherwise to canvas directly
            var parent = _panelGo != null ? _panelGo.transform : _canvasGo.transform;

            _completeBtnGo = new GameObject("CompleteBtn");
            _completeBtnGo.transform.SetParent(parent, false);
            var btnRt = _completeBtnGo.AddComponent<RectTransform>();
            btnRt.anchorMin = new Vector2(0.5f, 0f);
            btnRt.anchorMax = new Vector2(0.5f, 0f);
            btnRt.pivot = new Vector2(0.5f, 0f);
            btnRt.sizeDelta = new Vector2(180f, 40f);
            btnRt.anchoredPosition = new Vector2(0f, BottomMargin + 20f);

            var btnImg = _completeBtnGo.AddComponent<Image>();
            btnImg.color = new Color(0.2f, 0.5f, 0.2f, 0.9f);
            btnImg.raycastTarget = true;

            // Button label (child of button)
            var labelGo = new GameObject("BtnLabel");
            labelGo.transform.SetParent(_completeBtnGo.transform, false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;

            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                tmp.text = "Complete Sale";
                tmp.fontSize = 16;
                tmp.fontStyle = FontStyles.Bold;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = Color.white;
                tmp.raycastTarget = false;
            }
        }

        /// <summary>
        /// Checks for mouse clicks on HUD slots or the Complete button.
        /// Called each frame during WaitingForPlacement.
        /// </summary>
        public static void TickClickDetection()
        {
            if (!IsVisible) return;
            if (!Input.GetMouseButtonDown(0)) return;

            // Raycast through UI to find which element was clicked
            var pointer = new UnityEngine.EventSystems.PointerEventData(
                UnityEngine.EventSystems.EventSystem.current)
            {
                position = Input.mousePosition
            };

#if IL2CPP
            var results = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
#else
            var results = new List<UnityEngine.EventSystems.RaycastResult>();
#endif
            UnityEngine.EventSystems.EventSystem.current?.RaycastAll(pointer, results);

            for (int i = 0; i < results.Count; i++)
            {
                var hitGo = results[i].gameObject;

                // Check Complete button
                if (_completeBtnGo != null &&
                    (hitGo == _completeBtnGo || hitGo.transform.IsChildOf(_completeBtnGo.transform)))
                {
                    _onCompleteCallback?.Invoke();
                    return;
                }

                // Check sprite slots
                for (int s = 0; s < _slots.Count; s++)
                {
                    if (_slots[s] == null) continue;
                    if (hitGo == _slots[s] || hitGo.transform.IsChildOf(_slots[s].transform))
                    {
                        _onClickCallback?.Invoke(s);
                        return;
                    }
                }
            }
        }

        // =================================================================
        //  Slot creation
        // =================================================================

        private static GameObject CreateSlot(int index, SpriteItem item, float xPos, Transform parent)
        {
            var slotGo = new GameObject($"Slot_{index}");
            slotGo.transform.SetParent(parent, false);

            var slotRt = slotGo.AddComponent<RectTransform>();
            slotRt.anchorMin = new Vector2(0.5f, 0.5f);
            slotRt.anchorMax = new Vector2(0.5f, 0.5f);
            slotRt.pivot = new Vector2(0.5f, 0.5f);
            slotRt.sizeDelta = new Vector2(SlotSize, SlotSize);
            slotRt.anchoredPosition = new Vector2(xPos, 10f);

            // Background (clickable)
            var bgImg = slotGo.AddComponent<Image>();
            bgImg.color = SlotBg;
            bgImg.raycastTarget = true;

            // Product icon
            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(slotGo.transform, false);
            var iconRt = iconGo.AddComponent<RectTransform>();
            iconRt.anchorMin = new Vector2(0.5f, 0.5f);
            iconRt.anchorMax = new Vector2(0.5f, 0.5f);
            iconRt.pivot = new Vector2(0.5f, 0.5f);
            iconRt.sizeDelta = new Vector2(IconSize, IconSize);
            iconRt.anchoredPosition = new Vector2(0f, 4f);

            var iconImg = iconGo.AddComponent<Image>();
            iconImg.raycastTarget = false;

            // Try to load product icon sprite
            try
            {
                var iconMgr = Singleton<ProductIconManager>.Instance;
                if (iconMgr != null && item.ProductId != null && item.PackagingId != null)
                {
                    var sprite = iconMgr.GetIcon(item.ProductId, item.PackagingId, true);
                    if (sprite != null)
                    {
                        iconImg.sprite = sprite;
                        iconImg.color = Color.white;
                    }
                    else
                    {
                        iconImg.color = new Color(0.4f, 0.4f, 0.4f, 0.5f);
                    }
                }
            }
            catch { }

            // Product name label below icon
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(slotGo.transform, false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0.5f, 0f);
            labelRt.anchorMax = new Vector2(0.5f, 0f);
            labelRt.pivot = new Vector2(0.5f, 0f);
            labelRt.sizeDelta = new Vector2(SlotSize - 4f, 16f);
            labelRt.anchoredPosition = new Vector2(0f, 2f);

            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = TruncateName(item.ProductName, 10);
            label.fontSize = 9;
            label.alignment = TextAlignmentOptions.Center;
            label.color = LabelColor;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;

            return slotGo;
        }

        // =================================================================
        //  Control hints (bottom-left, matching game's InputPrompt style)
        // =================================================================

        private const float HintRowHeight = 28f;
        private const float HintRowSpacing = 4f;
        private const float HintPadding = 12f;
        private const float HintKeyMinWidth = 40f;
        private const float HintKeyHeight = 24f;
        private const float HintKeyPadding = 8f;
        private static readonly Color HintKeyBg = new(0.18f, 0.18f, 0.18f, 0.95f);
        private static readonly Color HintKeyBorder = new(0.35f, 0.35f, 0.35f, 0.8f);
        private static readonly Color HintKeyText = new(1f, 1f, 1f, 0.95f);
        private static readonly Color HintLabelText = new(0.85f, 0.85f, 0.85f, 0.95f);

        private static GameObject CreateControlHints(Transform parent)
        {
            string[] keys = { "LMB", "RMB", "R" };
            string[] labels = { "Place Product", "Remove", "Exit" };

            float totalHeight = keys.Length * HintRowHeight + (keys.Length - 1) * HintRowSpacing + HintPadding * 2f;
            float panelWidth = 220f;

            var panel = new GameObject("ControlHints");
            panel.transform.SetParent(parent, false);
            var panelRt = panel.AddComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0f, 0f);
            panelRt.anchorMax = new Vector2(0f, 0f);
            panelRt.pivot = new Vector2(0f, 0f);
            panelRt.sizeDelta = new Vector2(panelWidth, totalHeight);
            panelRt.anchoredPosition = new Vector2(20f, BottomMargin);

            for (int i = 0; i < keys.Length; i++)
            {
                float yPos = totalHeight - HintPadding - HintRowHeight * 0.5f
                    - i * (HintRowHeight + HintRowSpacing);
                CreateHintRow(panel.transform, keys[i], labels[i], yPos, panelWidth);
            }

            return panel;
        }

        private static void CreateHintRow(Transform parent, string keyText, string labelText,
            float yPos, float panelWidth)
        {
            // Row container
            var row = new GameObject($"Hint_{keyText}");
            row.transform.SetParent(parent, false);
            var rowRt = row.AddComponent<RectTransform>();
            rowRt.anchorMin = new Vector2(0f, 0f);
            rowRt.anchorMax = new Vector2(0f, 0f);
            rowRt.pivot = new Vector2(0f, 0.5f);
            rowRt.sizeDelta = new Vector2(panelWidth - HintPadding * 2f, HintRowHeight);
            rowRt.anchoredPosition = new Vector2(HintPadding, yPos);

            // Measure key text width to size the badge
            float keyWidth = Mathf.Max(HintKeyMinWidth, keyText.Length * 10f + HintKeyPadding * 2f);

            // Key badge (Image-only parent, same pattern as CreateSlot)
            var badgeGo = new GameObject("Badge");
            badgeGo.transform.SetParent(row.transform, false);
            var badgeRt = badgeGo.AddComponent<RectTransform>();
            badgeRt.anchorMin = new Vector2(0f, 0.5f);
            badgeRt.anchorMax = new Vector2(0f, 0.5f);
            badgeRt.pivot = new Vector2(0f, 0.5f);
            badgeRt.sizeDelta = new Vector2(keyWidth, HintKeyHeight);
            badgeRt.anchoredPosition = Vector2.zero;

            var badgeImg = badgeGo.AddComponent<Image>();
            badgeImg.color = HintKeyBg;
            badgeImg.raycastTarget = false;

            // Border outline (slightly larger Image behind badge)
            var borderGo = new GameObject("Border");
            borderGo.transform.SetParent(row.transform, false);
            borderGo.transform.SetAsFirstSibling();
            var borderRt = borderGo.AddComponent<RectTransform>();
            borderRt.anchorMin = new Vector2(0f, 0.5f);
            borderRt.anchorMax = new Vector2(0f, 0.5f);
            borderRt.pivot = new Vector2(0f, 0.5f);
            borderRt.sizeDelta = new Vector2(keyWidth + 2f, HintKeyHeight + 2f);
            borderRt.anchoredPosition = new Vector2(-1f, 0f);

            var borderImg = borderGo.AddComponent<Image>();
            borderImg.color = HintKeyBorder;
            borderImg.raycastTarget = false;

            // Key text (child of badge — TMP on separate child, not on Image GO)
            var keyLabelGo = new GameObject("KeyText");
            keyLabelGo.transform.SetParent(badgeGo.transform, false);
            var keyLabelRt = keyLabelGo.AddComponent<RectTransform>();
            keyLabelRt.anchorMin = Vector2.zero;
            keyLabelRt.anchorMax = Vector2.one;
            keyLabelRt.offsetMin = Vector2.zero;
            keyLabelRt.offsetMax = Vector2.zero;

            var keyTmp = keyLabelGo.AddComponent<TextMeshProUGUI>();
            if (keyTmp != null)
            {
                keyTmp.text = keyText;
                keyTmp.fontSize = 12;
                keyTmp.fontStyle = FontStyles.Bold;
                keyTmp.alignment = TextAlignmentOptions.Center;
                keyTmp.color = HintKeyText;
                keyTmp.raycastTarget = false;
            }

            // Action label (separate GO next to badge)
            var labelGo = new GameObject("ActionLabel");
            labelGo.transform.SetParent(row.transform, false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0f, 0.5f);
            labelRt.anchorMax = new Vector2(0f, 0.5f);
            labelRt.pivot = new Vector2(0f, 0.5f);
            labelRt.sizeDelta = new Vector2(150f, HintRowHeight);
            labelRt.anchoredPosition = new Vector2(keyWidth + 10f, 0f);

            var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
            if (labelTmp != null)
            {
                labelTmp.text = labelText;
                labelTmp.fontSize = 13;
                labelTmp.alignment = TextAlignmentOptions.MidlineLeft;
                labelTmp.color = HintLabelText;
                labelTmp.raycastTarget = false;
            }
        }

        private static string TruncateName(string name, int maxLen)
        {
            if (string.IsNullOrEmpty(name)) return "???";
            return name.Length <= maxLen ? name : name.Substring(0, maxLen - 1) + "…";
        }
    }
}
