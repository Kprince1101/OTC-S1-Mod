using OverTheCounter.UI;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.UI.Items;
using Il2CppTMPro;
using GameCanvasScaler = Il2CppScheduleOne.UI.CanvasScaler;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Product;
using ScheduleOne.UI.Items;
using TMPro;
using GameCanvasScaler = ScheduleOne.UI.CanvasScaler;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Right-side overlay HUD showing available products as clickable cards
    /// during the budtending checkout process. Shows up to <see cref="MaxVisible"/>
    /// cards at once; overflow items pop in with a scale animation as cards are placed.
    /// Supports hold-and-drag to place multiple cards in one sweep.
    /// </summary>
    public static class BudtenderHUD
    {
        /// <summary>Data for a single product card in the HUD.</summary>
        public struct SpriteItem
        {
            public string ProductId;
            public string PackagingId;
            public string ProductName;
            public int QualityLevel;
            public int UnitCount;
        }

        private static Sprite _starSprite;
        private static GameObject _canvasGo;
        private static GameObject _panelGo;
        private static GameObject _hintsGo;
        private static GameObject _completeBtnGo;
        private static TextMeshProUGUI _headerText;
        private static readonly List<GameObject> _slots = new();
        private static readonly List<SpriteItem> _itemData = new();
        private static readonly List<int> _queuedIndices = new();
        private static Action<int> _onClickCallback;
        private static Action _onCompleteCallback;
        private static int _initialCount;

        // Hold-to-drag state: hold LMB and sweep over cards to place each one
        private static bool _holdActive;
        private static int _holdLastSlot = -1;

        // Pop-in animation state
        private static int _animatingSlot = -1;
        private static float _animStartTime;

        // Newly promoted card cooldown (can't click for 0.5s after spawning)
        private static int _promotedSlot = -1;
        private static float _promoteReadyTime;

        // Card layout
        private const float CardWidth = 220f;
        private const float CardHeight = 60f;
        private const float CardSpacing = 6f;
        private const float IconSize = 44f;
        private const float PanelPadding = 10f;
        private const float RightMargin = 20f;
        private const float HeaderHeight = 30f;
        private const int MaxVisible = 5;
        private const float PopInDuration = 0.15f;

        // Colors
        private static readonly Color PanelBg = new(0.05f, 0.05f, 0.05f, 0.75f);
        private static readonly Color CardBg = new(0.12f, 0.12f, 0.12f, 0.8f);
        private static readonly Color LabelColor = new(0.90f, 0.93f, 0.90f);
        private static readonly Color DimColor = new(0.55f, 0.58f, 0.55f);
        private static readonly Color HeaderColor = new(0.75f, 0.80f, 0.75f);

        // Quality colors (matching POS)
        private static readonly Color32[] QualityColors =
        {
            new(80, 145, 50, 255),   // 0: Trash
            new(80, 145, 50, 255),   // 1: Low
            new(100, 190, 255, 255), // 2: Medium
            new(225, 75, 255, 255),  // 3: High
            new(255, 200, 50, 255),  // 4: Heavenly
        };

        /// <summary>Whether the HUD is currently visible.</summary>
        public static bool IsVisible => _canvasGo != null && _canvasGo.activeSelf;

        /// <summary>
        /// Shows the HUD with the given product cards. Player clicks a card to place it.
        /// Only the first <see cref="MaxVisible"/> cards are shown; the rest queue.
        /// </summary>
        /// <param name="items">Products available for placement.</param>
        /// <param name="onClickCallback">Called with the slot index when the player clicks a card.</param>
        public static void Show(List<SpriteItem> items, Action<int> onClickCallback)
        {
            Hide();

            _onClickCallback = onClickCallback;
            _initialCount = items?.Count ?? 0;
            _holdActive = false;
            _animatingSlot = -1;
            _promotedSlot = -1;

            // Store item data for deferred card creation
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                    _itemData.Add(items[i]);
            }

            // Create overlay canvas
            _canvasGo = new GameObject("OTC_BudtenderHUD");
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            _canvasGo.AddComponent<GameCanvasScaler>();
            _canvasGo.AddComponent<GraphicRaycaster>();

            // Right-side panel (sized for MaxVisible cards, not total)
            if (_itemData.Count > 0)
            {
                int visibleCount = Math.Min(_itemData.Count, MaxVisible);
                float cardAreaHeight = visibleCount * CardHeight + (visibleCount - 1) * CardSpacing;
                float panelHeight = cardAreaHeight + HeaderHeight + PanelPadding * 2f;

                _panelGo = new GameObject("Panel");
                _panelGo.transform.SetParent(_canvasGo.transform, false);
                var panelRt = _panelGo.AddComponent<RectTransform>();
                panelRt.anchorMin = new Vector2(1f, 0.5f);
                panelRt.anchorMax = new Vector2(1f, 0.5f);
                panelRt.pivot = new Vector2(1f, 0.5f);
                panelRt.sizeDelta = new Vector2(CardWidth + PanelPadding * 2f, panelHeight);
                panelRt.anchoredPosition = new Vector2(-RightMargin, 0f);

                var panelImg = _panelGo.AddComponent<Image>();
                panelImg.color = PanelBg;
                panelImg.raycastTarget = false;

                // Header
                _headerText = TMPFactory.Text("Header", $"Products (0/{_itemData.Count})",
                    _panelGo.transform, 15, TextAlignmentOptions.Center, FontStyles.Bold);
                _headerText.color = HeaderColor;
                _headerText.raycastTarget = false;
                var headerRt = _headerText.GetComponent<RectTransform>();
                headerRt.anchorMin = new Vector2(0f, 1f);
                headerRt.anchorMax = new Vector2(1f, 1f);
                headerRt.pivot = new Vector2(0.5f, 1f);
                headerRt.sizeDelta = new Vector2(0f, HeaderHeight);
                headerRt.anchoredPosition = Vector2.zero;

                // Create cards: GO for visible, null for queued
                for (int i = 0; i < _itemData.Count; i++)
                {
                    if (i < MaxVisible)
                    {
                        var cardGo = CreateCard(i, _itemData[i], _panelGo.transform);
                        _slots.Add(cardGo);
                    }
                    else
                    {
                        _slots.Add(null); // queued — no GO yet
                        _queuedIndices.Add(i);
                    }
                }

                ReflowCards();
            }

            // Control hints panel (bottom-left)
            _hintsGo = CreateControlHints(_canvasGo.transform);
        }

        /// <summary>Hides and destroys the HUD.</summary>
        public static void Hide()
        {
            _slots.Clear();
            _itemData.Clear();
            _queuedIndices.Clear();
            _onClickCallback = null;
            _holdActive = false;
            _holdLastSlot = -1;
            _animatingSlot = -1;
            _promotedSlot = -1;
            if (_canvasGo != null)
            {
                UnityEngine.Object.Destroy(_canvasGo);
                _canvasGo = null;
            }
            _panelGo = null;
            _hintsGo = null;
            _completeBtnGo = null;
            _onCompleteCallback = null;
            _headerText = null;
        }

        /// <summary>Removes a single card after it's been placed on the counter.</summary>
        public static void RemoveItem(int index)
        {
            if (index < 0 || index >= _slots.Count) return;
            var slot = _slots[index];
            if (slot != null)
                UnityEngine.Object.Destroy(slot);
            _slots[index] = null;

            // Promote next queued item into the visible area
            PromoteQueuedCard();
            ReflowCards();
            UpdateHeader();
        }

        /// <summary>Hides the card panel and queued items, keeping the canvas alive for the Complete button.</summary>
        public static void HideCards()
        {
            if (_panelGo != null)
            {
                UnityEngine.Object.Destroy(_panelGo);
                _panelGo = null;
            }
            _slots.Clear();
            _queuedIndices.Clear();
            _animatingSlot = -1;
            _promotedSlot = -1;
        }

        /// <summary>Returns true if all cards have been placed (all removed, none queued).</summary>
        public static bool AllPlaced()
        {
            if (_queuedIndices.Count > 0) return false;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i] != null) return false;
            }
            return _slots.Count > 0;
        }

        /// <summary>
        /// Shows a prompt label and "Complete Early Sale" button below the card panel.
        /// Called when all available cards are placed but the order is partial.
        /// </summary>
        public static void ShowCompleteButton(Action onComplete)
        {
            if (_canvasGo == null) return;
            _onCompleteCallback = onComplete;

            const float promptHeight = 24f;
            const float btnHeight = 40f;
            const float gap = 14f;
            float totalWidth = CardWidth + PanelPadding * 2f;

            // Container for prompt + button
            _completeBtnGo = new GameObject("CompleteArea");
            _completeBtnGo.transform.SetParent(_canvasGo.transform, false);
            var areaRt = _completeBtnGo.AddComponent<RectTransform>();
            areaRt.anchorMin = new Vector2(1f, 0.5f);
            areaRt.anchorMax = new Vector2(1f, 0.5f);
            areaRt.pivot = new Vector2(1f, 1f);
            areaRt.sizeDelta = new Vector2(totalWidth, promptHeight + gap + btnHeight);

            if (_panelGo != null)
            {
                var panelRt = _panelGo.GetComponent<RectTransform>();
                float panelBottom = -panelRt.sizeDelta.y * 0.5f;
                areaRt.anchoredPosition = new Vector2(-RightMargin, panelBottom - 6f);
            }
            else
            {
                areaRt.anchoredPosition = new Vector2(-RightMargin, 0f);
            }

            // Prompt text: "Grab the products in the Order or"
            var promptTmp = TMPFactory.Text("Prompt", "Grab the products in the Order or",
                _completeBtnGo.transform, 15, TextAlignmentOptions.Center, FontStyles.Bold);
            promptTmp.color = LabelColor;
            promptTmp.raycastTarget = false;
            var promptRt = promptTmp.GetComponent<RectTransform>();
            promptRt.anchorMin = new Vector2(0f, 1f);
            promptRt.anchorMax = new Vector2(1f, 1f);
            promptRt.pivot = new Vector2(0.5f, 1f);
            promptRt.sizeDelta = new Vector2(0f, promptHeight);
            promptRt.anchoredPosition = Vector2.zero;

            // Button
            var btnGo = new GameObject("Btn");
            btnGo.transform.SetParent(_completeBtnGo.transform, false);
            var btnRt = btnGo.AddComponent<RectTransform>();
            btnRt.anchorMin = new Vector2(0f, 1f);
            btnRt.anchorMax = new Vector2(1f, 1f);
            btnRt.pivot = new Vector2(0.5f, 1f);
            btnRt.sizeDelta = new Vector2(0f, btnHeight);
            btnRt.anchoredPosition = new Vector2(0f, -(promptHeight + gap));

            var btnImg = btnGo.AddComponent<Image>();
            btnImg.sprite = TMPFactory.GetRoundedSprite();
            btnImg.type = Image.Type.Sliced;
            btnImg.color = new Color(0.2f, 0.5f, 0.2f, 0.9f);
            btnImg.raycastTarget = true;

            var btnLabel = TMPFactory.Text("BtnLabel", "Complete Sale Early",
                btnGo.transform, 16, TextAlignmentOptions.Center, FontStyles.Bold);
            btnLabel.raycastTarget = false;
        }

        /// <summary>
        /// Processes click and hold-to-drag input on HUD cards and the Complete button.
        /// Also drives the pop-in scale animation for queued cards.
        /// Called each frame during WaitingForPlacement.
        /// </summary>
        public static void TickClickDetection()
        {
            if (!IsVisible) return;

            // Drive pop-in animation
            TickAnimation();

            // Mouse released — reset hold state
            if (!Input.GetMouseButton(0))
            {
                _holdActive = false;
                _holdLastSlot = -1;
                return;
            }

            int hitSlot = RaycastSlots(out bool hitComplete);

            // Initial click
            if (Input.GetMouseButtonDown(0))
            {
                if (hitComplete)
                {
                    _onCompleteCallback?.Invoke();
                    return;
                }

                if (hitSlot >= 0 && IsSlotClickable(hitSlot))
                {
                    _onClickCallback?.Invoke(hitSlot);
                    _holdActive = true;
                    _holdLastSlot = hitSlot;
                }
                return;
            }

            // Hold-and-drag: fire when cursor enters a new card
            if (_holdActive && hitSlot >= 0 && hitSlot != _holdLastSlot
                && IsSlotClickable(hitSlot))
            {
                _onClickCallback?.Invoke(hitSlot);
                _holdLastSlot = hitSlot;
            }
        }

        /// <summary>Returns false if the slot is a freshly promoted card still in cooldown.</summary>
        private static bool IsSlotClickable(int slot)
        {
            return slot != _promotedSlot || Time.time >= _promoteReadyTime;
        }

        // =================================================================
        //  Animation
        // =================================================================

        private static void TickAnimation()
        {
            if (_animatingSlot < 0 || _animatingSlot >= _slots.Count) return;
            var go = _slots[_animatingSlot];
            if (go == null) { _animatingSlot = -1; return; }

            float t = (Time.time - _animStartTime) / PopInDuration;
            if (t >= 1f)
            {
                go.transform.localScale = Vector3.one;
                _animatingSlot = -1;
            }
            else
            {
                // Ease-out cubic for snappy pop
                float ease = 1f - (1f - t) * (1f - t) * (1f - t);
                go.transform.localScale = Vector3.one * ease;
            }
        }

        // =================================================================
        //  Queue promotion
        // =================================================================

        /// <summary>
        /// If a visible slot was removed and there are queued items,
        /// create the next queued card with a pop-in animation.
        /// </summary>
        private static void PromoteQueuedCard()
        {
            if (_panelGo == null || _queuedIndices.Count == 0) return;

            // Count current visible (non-null) GOs
            int visibleCount = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i] != null) visibleCount++;
            }

            if (visibleCount >= MaxVisible) return;

            // Force-complete any in-progress animation so the old card
            // doesn't get stuck at a partial scale
            if (_animatingSlot >= 0 && _animatingSlot < _slots.Count
                && _slots[_animatingSlot] != null)
            {
                _slots[_animatingSlot].transform.localScale = Vector3.one;
            }
            _animatingSlot = -1;

            int promoteIdx = _queuedIndices[0];
            _queuedIndices.RemoveAt(0);

            if (promoteIdx < 0 || promoteIdx >= _itemData.Count) return;

            var cardGo = CreateCard(promoteIdx, _itemData[promoteIdx], _panelGo.transform);
            cardGo.transform.localScale = Vector3.zero; // start invisible
            _slots[promoteIdx] = cardGo;

            _animatingSlot = promoteIdx;
            _animStartTime = Time.time;

            // 0.5s cooldown before this card can be clicked/held
            _promotedSlot = promoteIdx;
            _promoteReadyTime = Time.time + 0.5f;
        }

        // =================================================================
        //  Raycast helper
        // =================================================================

        private static int RaycastSlots(out bool hitComplete)
        {
            hitComplete = false;

            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem == null) return -1;

            var pointer = new UnityEngine.EventSystems.PointerEventData(eventSystem)
            {
                position = Input.mousePosition
            };

#if IL2CPP
            var results = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
#else
            var results = new List<UnityEngine.EventSystems.RaycastResult>();
#endif
            eventSystem.RaycastAll(pointer, results);

            for (int i = 0; i < results.Count; i++)
            {
                var hitGo = results[i].gameObject;

                // Check Complete button
                if (_completeBtnGo != null &&
                    (hitGo == _completeBtnGo || hitGo.transform.IsChildOf(_completeBtnGo.transform)))
                {
                    hitComplete = true;
                    return -1;
                }

                // Check card slots
                for (int s = 0; s < _slots.Count; s++)
                {
                    if (_slots[s] == null) continue;
                    if (hitGo == _slots[s] || hitGo.transform.IsChildOf(_slots[s].transform))
                        return s;
                }
            }

            return -1;
        }

        // =================================================================
        //  Card creation
        // =================================================================

        private static GameObject CreateCard(int index, SpriteItem item, Transform parent)
        {
            var cardGo = new GameObject($"Card_{index}");
            cardGo.transform.SetParent(parent, false);

            var cardRt = cardGo.AddComponent<RectTransform>();
            cardRt.sizeDelta = new Vector2(CardWidth, CardHeight);

            // Background (clickable)
            var bgImg = cardGo.AddComponent<Image>();
            bgImg.color = CardBg;
            bgImg.raycastTarget = true;

            // Product icon (left side)
            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(cardGo.transform, false);
            var iconRt = iconGo.AddComponent<RectTransform>();
            iconRt.anchorMin = new Vector2(0f, 0.5f);
            iconRt.anchorMax = new Vector2(0f, 0.5f);
            iconRt.pivot = new Vector2(0f, 0.5f);
            iconRt.sizeDelta = new Vector2(IconSize, IconSize);
            iconRt.anchoredPosition = new Vector2(8f, 0f);

            var iconImg = iconGo.AddComponent<Image>();
            iconImg.raycastTarget = false;

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

            // Text area (right of icon)
            float textX = 8f + IconSize + 8f;
            float textWidth = CardWidth - textX - 8f;

            // Product name (top line)
            var nameTmp = TMPFactory.Text("Name", item.ProductName ?? "???",
                cardGo.transform, 15, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            nameTmp.color = LabelColor;
            nameTmp.overflowMode = TextOverflowModes.Ellipsis;
            nameTmp.raycastTarget = false;
            var nameRt = nameTmp.GetComponent<RectTransform>();
            nameRt.anchorMin = new Vector2(0f, 0.5f);
            nameRt.anchorMax = new Vector2(0f, 0.5f);
            nameRt.pivot = new Vector2(0f, 0.5f);
            nameRt.sizeDelta = new Vector2(textWidth, 22f);
            nameRt.anchoredPosition = new Vector2(textX, 10f);

            // Bottom line: quality star sprite + packaging text
            int qi = Mathf.Clamp(item.QualityLevel, 0, QualityColors.Length - 1);
            const float starSize = 12f;
            const float starGap = 4f;

            var starGo = new GameObject("Star");
            starGo.transform.SetParent(cardGo.transform, false);
            var starRt = starGo.AddComponent<RectTransform>();
            starRt.anchorMin = new Vector2(0f, 0.5f);
            starRt.anchorMax = new Vector2(0f, 0.5f);
            starRt.pivot = new Vector2(0f, 0.5f);
            starRt.sizeDelta = new Vector2(starSize, starSize);
            starRt.anchoredPosition = new Vector2(textX, -10f);
            var starImg = starGo.AddComponent<Image>();
            starImg.sprite = GetStarSprite();
            starImg.color = (Color)QualityColors[qi];
            starImg.raycastTarget = false;

            string pkgLabel = FormatPackaging(item.PackagingId, item.UnitCount);
            float pkgX = textX + starSize + starGap;
            float pkgWidth = CardWidth - pkgX - 8f;

            var detailTmp = TMPFactory.Text("Detail", pkgLabel,
                cardGo.transform, 13, TextAlignmentOptions.MidlineLeft);
            detailTmp.color = DimColor;
            detailTmp.overflowMode = TextOverflowModes.Ellipsis;
            detailTmp.raycastTarget = false;
            var detailRt = detailTmp.GetComponent<RectTransform>();
            detailRt.anchorMin = new Vector2(0f, 0.5f);
            detailRt.anchorMax = new Vector2(0f, 0.5f);
            detailRt.pivot = new Vector2(0f, 0.5f);
            detailRt.sizeDelta = new Vector2(pkgWidth, 18f);
            detailRt.anchoredPosition = new Vector2(pkgX, -10f);

            return cardGo;
        }

        // =================================================================
        //  Layout helpers
        // =================================================================

        /// <summary>Repositions visible cards top-to-bottom and resizes panel.</summary>
        private static void ReflowCards()
        {
            if (_panelGo == null) return;
            var panelRt = _panelGo.GetComponent<RectTransform>();

            int visibleCount = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i] != null) visibleCount++;
            }

            // Resize panel to fit visible cards (capped at MaxVisible)
            int displayCount = Math.Min(visibleCount, MaxVisible);
            float cardAreaHeight = displayCount > 0
                ? displayCount * CardHeight + (displayCount - 1) * CardSpacing
                : 0f;
            float panelHeight = cardAreaHeight + HeaderHeight + PanelPadding * 2f;
            panelRt.sizeDelta = new Vector2(CardWidth + PanelPadding * 2f, panelHeight);

            // Reposition cards
            int visualIdx = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i] == null) continue;
                var rt = _slots[i].GetComponent<RectTransform>();
                PositionCard(rt, visualIdx, panelHeight);
                visualIdx++;
            }
        }

        private static void PositionCard(RectTransform rt, int visualIndex, float panelHeight)
        {
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            float yPos = -(HeaderHeight + PanelPadding + visualIndex * (CardHeight + CardSpacing));
            rt.anchoredPosition = new Vector2(0f, yPos);
        }

        private static void UpdateHeader()
        {
            if (_headerText == null) return;
            int remaining = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i] != null) remaining++;
            }
            remaining += _queuedIndices.Count;
            int placed = _initialCount - remaining;
            _headerText.text = $"Products ({placed}/{_initialCount})";
        }

        private static string FormatPackaging(string packagingId, int unitCount)
        {
            if (string.IsNullOrEmpty(packagingId)) return $"x{unitCount}";

            string label = packagingId switch
            {
                "baggie" => "Baggie",
                "jar" => "Jar",
                "brick" => "Brick",
                _ => packagingId
            };

            return unitCount > 1 ? $"{label} (x{unitCount})" : label;
        }

        /// <summary>Lazy-loads the quality star sprite from the game's QualityItemInfoContent.</summary>
        private static Sprite GetStarSprite()
        {
            if (_starSprite != null) return _starSprite;

            try
            {
                var allQiic = Resources.FindObjectsOfTypeAll<QualityItemInfoContent>();
                if (allQiic != null)
                {
                    for (int j = 0; j < allQiic.Length; j++)
                    {
                        if (allQiic[j]?.Star?.sprite != null)
                        { _starSprite = allQiic[j].Star.sprite; break; }
                    }
                }
            }
            catch { }

            _starSprite ??= Resources.GetBuiltinResource<Sprite>("UI/Skin/Knob.psd");
            return _starSprite;
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
        private const float BottomMargin = 40f;
        private static readonly Color HintKeyBg = new(0.18f, 0.18f, 0.18f, 0.95f);
        private static readonly Color HintKeyBorder = new(0.35f, 0.35f, 0.35f, 0.8f);
        private static readonly Color HintKeyText = new(1f, 1f, 1f, 0.95f);
        private static readonly Color HintLabelText = new(0.85f, 0.85f, 0.85f, 0.95f);

        private static GameObject CreateControlHints(Transform parent)
        {
            string[] keys = { "LMB", "RMB", "R" };
            string[] labels = { "Place Product (hold)", "Remove", "Exit" };

            float totalHeight = keys.Length * HintRowHeight + (keys.Length - 1) * HintRowSpacing + HintPadding * 2f;
            float panelWidth = 240f;

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
            var row = new GameObject($"Hint_{keyText}");
            row.transform.SetParent(parent, false);
            var rowRt = row.AddComponent<RectTransform>();
            rowRt.anchorMin = new Vector2(0f, 0f);
            rowRt.anchorMax = new Vector2(0f, 0f);
            rowRt.pivot = new Vector2(0f, 0.5f);
            rowRt.sizeDelta = new Vector2(panelWidth - HintPadding * 2f, HintRowHeight);
            rowRt.anchoredPosition = new Vector2(HintPadding, yPos);

            float keyWidth = Mathf.Max(HintKeyMinWidth, keyText.Length * 10f + HintKeyPadding * 2f);

            // Key badge
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

            // Border outline
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

            // Key text
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

            // Action label
            var labelGo = new GameObject("ActionLabel");
            labelGo.transform.SetParent(row.transform, false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0f, 0.5f);
            labelRt.anchorMax = new Vector2(0f, 0.5f);
            labelRt.pivot = new Vector2(0f, 0.5f);
            labelRt.sizeDelta = new Vector2(160f, HintRowHeight);
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

    }
}
