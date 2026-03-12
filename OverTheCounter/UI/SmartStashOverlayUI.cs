using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.Utilities;
using S1API.UI;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.UI;
using Il2CppTMPro;
using GameCanvasScaler = Il2CppScheduleOne.UI.CanvasScaler;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Storage;
using ScheduleOne.UI;
using TMPro;
using GameCanvasScaler = ScheduleOne.UI.CanvasScaler;
#endif

namespace OverTheCounter.UI
{
    /// <summary>
    /// Side-panel overlay shown alongside the StorageMenu.
    /// Displays the delivery manifest (aggregated contract requirements)
    /// and provides a Smart Fill button to transfer matching items.
    /// </summary>
    public static class SmartStashOverlayUI
    {
        private static GameObject _overlayRoot;
        private static Transform _listContent;
        private static bool _includeAllShifts;
        private static bool _fillBackpackFirst;
        private static bool _isCompact;
        private static TextMeshProUGUI _statusText;
        private static TextMeshProUGUI _collapseText;
        private static RectTransform _panelRect;
        private static GameObject _bodyContainer;
        private static GameSystem.Action _refreshAction;
        private static StorageEntity _subscribedStorageEntity;

        private static float _expandedHeight = 440f;
        private const float CompactHeight = 40f;

        public static void Show()
        {
            if (_overlayRoot != null)
            {
                UnityEngine.Object.Destroy(_overlayRoot);
                _overlayRoot = null;
            }

            _includeAllShifts = true;
            BuildUI();
            SubscribeToChanges();
            RefreshManifest();
        }

        public static void Hide()
        {
            UnsubscribeFromChanges();

            if (_overlayRoot != null)
            {
                UnityEngine.Object.Destroy(_overlayRoot);
                _overlayRoot = null;
            }
            _listContent = null;
            _statusText = null;
            _collapseText = null;
            _panelRect = null;
            _bodyContainer = null;
        }

        private static void SubscribeToChanges()
        {
            _refreshAction = (GameSystem.Action)new Action(OnInventoryChanged);

            // Subscribe to storage entity content changes
            try
            {
                var storageMenu = Singleton<StorageMenu>.Instance;
                if (storageMenu != null && storageMenu.IsOpen)
                {
                    var entity = storageMenu.OpenedStorageEntity;
                    if (entity != null)
                    {
                        entity.onContentsChanged += _refreshAction;
                        _subscribedStorageEntity = entity;
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"Could not subscribe to storage changes: {ex.Message}");
            }

            // Subscribe to player hotbar slot changes
            try
            {
                var playerInv = PlayerSingleton<PlayerInventory>.Instance;
                if (playerInv?.hotbarSlots != null)
                {
                    var slots = playerInv.hotbarSlots;
                    for (int i = 0; i < slots.Count; i++)
                    {
                        var slot = slots[i];
                        if (slot != null)
                            slot.onItemDataChanged += _refreshAction;
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"Could not subscribe to hotbar changes: {ex.Message}");
            }
        }

        private static void UnsubscribeFromChanges()
        {
            if (_refreshAction == null) return;

            // Unsubscribe from storage entity
            try
            {
                if (_subscribedStorageEntity != null)
                {
                    _subscribedStorageEntity.onContentsChanged -= _refreshAction;
                    _subscribedStorageEntity = null;
                }
            }
            catch { }

            // Unsubscribe from player hotbar slots
            try
            {
                var playerInv = PlayerSingleton<PlayerInventory>.Instance;
                if (playerInv?.hotbarSlots != null)
                {
                    var slots = playerInv.hotbarSlots;
                    for (int i = 0; i < slots.Count; i++)
                    {
                        var slot = slots[i];
                        if (slot != null)
                            slot.onItemDataChanged -= _refreshAction;
                    }
                }
            }
            catch { }

            _refreshAction = null;
        }

        private static void OnInventoryChanged()
        {
            try
            {
                RefreshManifest();
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"Error during auto-refresh: {ex.Message}");
            }
        }

        private static void BuildUI()
        {
            _overlayRoot = new GameObject("SmartStashOverlayRoot");
            var canvas = _overlayRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 80;

            var scaler = _overlayRoot.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            _overlayRoot.AddComponent<GameCanvasScaler>();

            var panelObj = UIFactory.Panel("SmartStashPanel", _overlayRoot.transform, new Color(0.14f, 0.14f, 0.14f, 0.95f));

            // Sub-canvas scopes the GraphicRaycaster to just the panel area
            var panelCanvas = panelObj.AddComponent<Canvas>();
            panelCanvas.overrideSorting = true;
            panelCanvas.sortingOrder = 81;
            panelObj.AddComponent<GraphicRaycaster>();
            _panelRect = panelObj.GetComponent<RectTransform>();
            _panelRect.anchorMin = new Vector2(1f, 0.5f);
            _panelRect.anchorMax = new Vector2(1f, 0.5f);
            _panelRect.pivot = new Vector2(1f, 0.5f);
            _panelRect.sizeDelta = new Vector2(260f, _expandedHeight);
            _panelRect.anchoredPosition = new Vector2(-10f, 0f);

            // Header
            var headerText = TMPFactory.Text("Header", "<b>Delivery Manifest</b>", panelObj.transform, 16, TextAlignmentOptions.Center);
            var headerRect = headerText.gameObject.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = new Vector2(1, 1);
            headerRect.pivot = new Vector2(0.5f, 1);
            headerRect.anchoredPosition = new Vector2(-12, -6);
            headerRect.sizeDelta = new Vector2(-24, 28);

            // Collapse/expand button (right side of header)
            var collapseBtnObj = new GameObject("CollapseBtn");
            collapseBtnObj.transform.SetParent(panelObj.transform, false);
            var collapseBtnRect = collapseBtnObj.AddComponent<RectTransform>();
            collapseBtnRect.anchorMin = new Vector2(1, 1);
            collapseBtnRect.anchorMax = new Vector2(1, 1);
            collapseBtnRect.pivot = new Vector2(1, 1);
            collapseBtnRect.anchoredPosition = new Vector2(-6, -6);
            collapseBtnRect.sizeDelta = new Vector2(28, 28);

            var collapseBtnImage = collapseBtnObj.AddComponent<Image>();
            collapseBtnImage.color = new Color(0.22f, 0.22f, 0.22f, 0.8f);

            var collapseBtn = collapseBtnObj.AddComponent<Button>();
            var collapseBtnColors = collapseBtn.colors;
            collapseBtnColors.normalColor = new Color(0.22f, 0.22f, 0.22f, 0.8f);
            collapseBtnColors.highlightedColor = new Color(0.35f, 0.35f, 0.35f, 0.9f);
            collapseBtnColors.pressedColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            collapseBtnColors.selectedColor = new Color(0.22f, 0.22f, 0.22f, 0.8f);
            collapseBtn.colors = collapseBtnColors;
            collapseBtn.onClick.AddListener(new Action(OnCompactToggle));

            _collapseText = TMPFactory.Text("CollapseIcon", "\u25B2", collapseBtnObj.transform, 15, TextAlignmentOptions.Center); // ▲ up arrow
            var collapseTextRect = _collapseText.gameObject.GetComponent<RectTransform>();
            collapseTextRect.anchorMin = Vector2.zero;
            collapseTextRect.anchorMax = Vector2.one;
            collapseTextRect.offsetMin = Vector2.zero;
            collapseTextRect.offsetMax = Vector2.zero;

            // Body container — holds everything below the header
            _bodyContainer = new GameObject("BodyContainer");
            _bodyContainer.transform.SetParent(panelObj.transform, false);
            var bodyRect = _bodyContainer.AddComponent<RectTransform>();
            bodyRect.anchorMin = Vector2.zero;
            bodyRect.anchorMax = Vector2.one;
            bodyRect.offsetMin = Vector2.zero;
            bodyRect.offsetMax = new Vector2(0, -38);

            var listContainer = new GameObject("ManifestList");
            listContainer.transform.SetParent(_bodyContainer.transform, false);
            var listRect = listContainer.AddComponent<RectTransform>();
            listRect.anchorMin = new Vector2(0, 0);
            listRect.anchorMax = new Vector2(1, 1);
            listRect.offsetMin = new Vector2(12, 100); // adjusted below if backpack toggle present
            listRect.offsetMax = new Vector2(-8, 0);

            var contentLayout = listContainer.AddComponent<VerticalLayoutGroup>();
            contentLayout.spacing = 3;
            contentLayout.childControlHeight = true;
            contentLayout.childControlWidth = true;
            contentLayout.childForceExpandHeight = false;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childAlignment = TextAnchor.UpperLeft;

            _listContent = listContainer.transform;

            bool hasBackpack = BackpackBridge.IsInstalled();
            _expandedHeight = hasBackpack ? 466f : 440f;
            _panelRect.sizeDelta = new Vector2(260f, _expandedHeight);
            float toggleAreaHeight = hasBackpack ? 126f : 100f;
            listRect.offsetMin = new Vector2(12, toggleAreaHeight);

            var toggleObj = new GameObject("IncludeAllToggle");
            toggleObj.transform.SetParent(_bodyContainer.transform, false);
            var toggleRect = toggleObj.AddComponent<RectTransform>();
            toggleRect.anchorMin = new Vector2(0, 0);
            toggleRect.anchorMax = new Vector2(1, 0);
            toggleRect.pivot = new Vector2(0.5f, 0);
            toggleRect.anchoredPosition = new Vector2(0, hasBackpack ? 94 : 68);
            toggleRect.sizeDelta = new Vector2(0, 24);

            var toggleBg = new GameObject("Background");
            toggleBg.transform.SetParent(toggleObj.transform, false);
            var bgImage = toggleBg.AddComponent<Image>();
            bgImage.color = new Color(0.25f, 0.25f, 0.25f);
            var bgRect = toggleBg.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 0.5f);
            bgRect.anchorMax = new Vector2(0, 0.5f);
            bgRect.pivot = new Vector2(0, 0.5f);
            bgRect.anchoredPosition = new Vector2(10, 0);
            bgRect.sizeDelta = new Vector2(18, 18);

            var checkmark = new GameObject("Checkmark");
            checkmark.transform.SetParent(toggleBg.transform, false);
            var checkImage = checkmark.AddComponent<Image>();
            checkImage.color = new Color(0.4f, 0.8f, 0.4f);
            var checkRect = checkmark.GetComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0.15f, 0.15f);
            checkRect.anchorMax = new Vector2(0.85f, 0.85f);
            checkRect.offsetMin = Vector2.zero;
            checkRect.offsetMax = Vector2.zero;

            var toggle = toggleObj.AddComponent<Toggle>();
            toggle.isOn = true;
            toggle.graphic = checkImage;
            toggle.targetGraphic = bgImage;
            toggle.onValueChanged.AddListener(new Action<bool>(OnToggleChanged));

            var toggleLabel = TMPFactory.Text("ToggleLabel", "Include All Delivery Windows", toggleObj.transform, 15, TextAlignmentOptions.Left);
            toggleLabel.color = new Color(0.8f, 0.8f, 0.8f);
            var labelRect = toggleLabel.gameObject.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0, 0);
            labelRect.anchorMax = new Vector2(1, 1);
            labelRect.offsetMin = new Vector2(34, 0);
            labelRect.offsetMax = new Vector2(-4, 0);

            // Backpack toggle (only when PackRat installed)
            if (hasBackpack)
                BuildBackpackToggle(_bodyContainer.transform);

            // Smart Fill button
            var (btnMask, btn, btnLabel) = TMPFactory.RoundedButtonWithLabel(
                "SmartFillBtn", "Smart Fill", _bodyContainer.transform,
                new Color(0.2f, 0.5f, 0.2f), 230, 32, 14, Color.white
            );

            var btnRect = btnMask.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 0);
            btnRect.anchorMax = new Vector2(0.5f, 0);
            btnRect.pivot = new Vector2(0.5f, 0);
            btnRect.anchoredPosition = new Vector2(0, 32);

            var btnColors = btn.colors;
            btnColors.normalColor = new Color(0.2f, 0.5f, 0.2f);
            btnColors.highlightedColor = new Color(0.3f, 0.6f, 0.3f);
            btnColors.pressedColor = new Color(0.15f, 0.35f, 0.15f);
            btnColors.selectedColor = new Color(0.2f, 0.5f, 0.2f);
            btn.colors = btnColors;

            btn.onClick.AddListener(new Action(OnSmartFillClicked));

            // Status text
            _statusText = TMPFactory.Text("StatusText", "", _bodyContainer.transform, 15, TextAlignmentOptions.Center);
            _statusText.color = new Color(0.7f, 0.7f, 0.7f);
            var statusRect = _statusText.gameObject.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0, 0);
            statusRect.anchorMax = new Vector2(1, 0);
            statusRect.pivot = new Vector2(0.5f, 0);
            statusRect.anchoredPosition = new Vector2(0, 6);
            statusRect.sizeDelta = new Vector2(0, 22);

            // Apply initial compact state
            if (_isCompact)
                ApplyCompactMode(true);

            _overlayRoot.SetActive(true);
        }

        private static void OnCompactToggle()
        {
            _isCompact = !_isCompact;
            ApplyCompactMode(_isCompact);
        }

        private static void ApplyCompactMode(bool compact)
        {
            if (_bodyContainer != null)
                _bodyContainer.SetActive(!compact);

            if (_panelRect != null)
                _panelRect.sizeDelta = new Vector2(260f, compact ? CompactHeight : _expandedHeight);

            if (_collapseText != null)
                _collapseText.text = compact ? "\u25BC" : "\u25B2"; // ▼ collapsed, ▲ expanded
        }

        private static void RefreshManifest()
        {
            if (_listContent == null) return;

            for (int i = _listContent.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_listContent.GetChild(i).gameObject);
            }

            List<ManifestRequirement> manifest;
            try
            {
                manifest = ContractAggregator.CalculateManifest(_includeAllShifts);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Error calculating manifest: {ex.Message}");
                SetStatus("Error loading manifest");
                return;
            }

            if (manifest.Count == 0)
            {
                SetStatus("Nothing needed!");
                return;
            }

            SetStatus("");

            foreach (var req in manifest)
            {
                string label = $"{req.ProductName}: {req.AmountInInventory}/{req.AmountNeeded}";
                Color textColor = req.Deficit <= 0 ? new Color(0.4f, 0.8f, 0.4f) : Color.white;

                var rowObj = new GameObject($"Row_{req.ProductID}");
                rowObj.transform.SetParent(_listContent, false);

                var rowRect = rowObj.AddComponent<RectTransform>();
                rowRect.sizeDelta = new Vector2(0, 20);

                var rowText = rowObj.AddComponent<TextMeshProUGUI>();
                rowText.text = label;
                rowText.fontSize = 15;
                rowText.color = textColor;
                rowText.alignment = TextAlignmentOptions.Left;
                TMPFactory.SetWrapping(rowText, true);
                rowText.overflowMode = TextOverflowModes.Truncate;

                var rowLayout = rowObj.AddComponent<LayoutElement>();
                rowLayout.preferredHeight = 20f;
                rowLayout.minHeight = 20f;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_listContent as RectTransform);
        }

        private static void OnToggleChanged(bool value)
        {
            _includeAllShifts = value;
            RefreshManifest();
        }

        private static void OnBackpackToggleChanged(bool value)
        {
            _fillBackpackFirst = value;
        }

        private static void BuildBackpackToggle(Transform parent)
        {
            var toggleObj = new GameObject("BackpackToggle");
            toggleObj.transform.SetParent(parent, false);
            var toggleRect = toggleObj.AddComponent<RectTransform>();
            toggleRect.anchorMin = new Vector2(0, 0);
            toggleRect.anchorMax = new Vector2(1, 0);
            toggleRect.pivot = new Vector2(0.5f, 0);
            toggleRect.anchoredPosition = new Vector2(0, 68);
            toggleRect.sizeDelta = new Vector2(0, 24);

            var toggleBg = new GameObject("Background");
            toggleBg.transform.SetParent(toggleObj.transform, false);
            var bgImage = toggleBg.AddComponent<Image>();
            bgImage.color = new Color(0.25f, 0.25f, 0.25f);
            var bgRect = toggleBg.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 0.5f);
            bgRect.anchorMax = new Vector2(0, 0.5f);
            bgRect.pivot = new Vector2(0, 0.5f);
            bgRect.anchoredPosition = new Vector2(10, 0);
            bgRect.sizeDelta = new Vector2(18, 18);

            var checkmark = new GameObject("Checkmark");
            checkmark.transform.SetParent(toggleBg.transform, false);
            var checkImage = checkmark.AddComponent<Image>();
            checkImage.color = new Color(0.4f, 0.8f, 0.4f);
            var checkRect = checkmark.GetComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0.15f, 0.15f);
            checkRect.anchorMax = new Vector2(0.85f, 0.85f);
            checkRect.offsetMin = Vector2.zero;
            checkRect.offsetMax = Vector2.zero;

            var toggle = toggleObj.AddComponent<Toggle>();
            toggle.isOn = _fillBackpackFirst;
            toggle.graphic = checkImage;
            toggle.targetGraphic = bgImage;
            toggle.onValueChanged.AddListener(new Action<bool>(OnBackpackToggleChanged));

            var toggleLabel = TMPFactory.Text("ToggleLabel", "Fill Backpack First", toggleObj.transform, 15, TextAlignmentOptions.Left);
            toggleLabel.color = new Color(0.8f, 0.8f, 0.8f);
            var labelRect = toggleLabel.gameObject.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0, 0);
            labelRect.anchorMax = new Vector2(1, 1);
            labelRect.offsetMin = new Vector2(34, 0);
            labelRect.offsetMax = new Vector2(-4, 0);
        }

        private static void OnSmartFillClicked()
        {
            try
            {
                var storageMenu = Singleton<StorageMenu>.Instance;
                if (storageMenu == null)
                {
                    SetStatus("Storage menu not found");
                    return;
                }

                if (!storageMenu.IsOpen)
                {
                    SetStatus("Storage not open");
                    return;
                }

                StorageEntity openedEntity = storageMenu.OpenedStorageEntity;
                if (openedEntity == null)
                {
                    SetStatus("No storage entity found");
                    return;
                }

                var containerSlots = openedEntity.ItemSlots;
                if (containerSlots == null)
                {
                    SetStatus("Cannot read storage slots");
                    return;
                }

                var playerInv = PlayerSingleton<PlayerInventory>.Instance;
                if (playerInv == null)
                {
                    SetStatus("Player inventory unavailable");
                    return;
                }

                var hotbar = playerInv.hotbarSlots;
                if (hotbar == null)
                {
                    SetStatus("Hotbar unavailable");
                    return;
                }

                // Per-contract needs ensure each contract gets independently optimal
                // packaging (e.g. 6 units = 1 jar + 1 baggie, not half of 2 jars).
                var perContractNeeds = ContractAggregator.CalculatePerContractNeeds(_includeAllShifts);
                if (perContractNeeds.Count == 0)
                {
                    SetStatus("Nothing needed!");
                    return;
                }

                int totalTransferredUnits = 0;
                bool inventoryFull = false;
                var bpSlots = BackpackBridge.GetSlots();
                bool hasBp = bpSlots.Length > 0;

                foreach (var need in perContractNeeds)
                {
                    int remainingUnits = need.Quantity;

                    // Build a list of matching container slot indices, sorted by packaging
                    // multiplier descending (jars first, then baggies)
                    var matchingSlots = new List<(int index, int multiplier)>();
                    for (int s = 0; s < containerSlots.Count; s++)
                    {
                        var slot = containerSlots[s];
                        if (slot == null || slot.ItemInstance == null) continue;

                        string slotId;
                        try { slotId = slot.ItemInstance.ID; }
                        catch { continue; }

                        if (slotId != need.ProductID) continue;
                        if (slot.Quantity <= 0) continue;

                        int mult = ContractAggregator.GetPackagingMultiplier(slot.ItemInstance);
                        if (mult <= 0) continue; // skip unpackaged product
                        matchingSlots.Add((s, mult));
                    }

                    // Sort: higher multiplier (jars) first
                    matchingSlots.Sort((a, b) => b.multiplier.CompareTo(a.multiplier));

                    foreach (var (slotIdx, multiplier) in matchingSlots)
                    {
                        if (remainingUnits <= 0) break;

                        var slot = containerSlots[slotIdx];
                        if (slot == null || slot.ItemInstance == null) continue;

                        int slotQty = slot.Quantity;
                        if (slotQty <= 0) continue;

                        // Floor division: take only what fits without overshooting,
                        // letting the remainder cascade to smaller packaging tiers.
                        int itemsNeeded = remainingUnits / multiplier;
                        int itemsToTake = Math.Min(itemsNeeded, slotQty);

                        // Can't take whole units at this tier — skip to smaller packaging
                        if (itemsToTake <= 0) continue;

                        int placed;

                        if (_fillBackpackFirst && hasBp)
                        {
                            placed = TryPlaceInSlots(bpSlots, slot.ItemInstance, itemsToTake);
                            if (placed < itemsToTake)
                                placed += TryPlaceInHotbar(hotbar, slot.ItemInstance, itemsToTake - placed);
                        }
                        else
                        {
                            placed = TryPlaceInHotbar(hotbar, slot.ItemInstance, itemsToTake);
                            if (placed < itemsToTake && hasBp)
                                placed += TryPlaceInSlots(bpSlots, slot.ItemInstance, itemsToTake - placed);
                        }

                        if (placed <= 0)
                        {
                            inventoryFull = true;
                            break;
                        }

                        try
                        {
                            if (placed >= slotQty)
                                slot.ClearStoredInstance();
                            else
                                slot.ChangeQuantity(-placed);
                        }
                        catch (Exception ex)
                        {
                            OTCLog.Warning(OTCLog.Systems.Patch, $"Error modifying container slot: {ex.Message}");
                        }

                        int unitsTransferred = placed * multiplier;
                        remainingUnits -= unitsTransferred;
                        totalTransferredUnits += unitsTransferred;
                    }

                    if (inventoryFull) break;
                }

                if (totalTransferredUnits == 0)
                    SetStatus(inventoryFull ? "Inventory full!" : "No matching items in storage");
                else if (inventoryFull)
                    SetStatus($"Transferred {totalTransferredUnits} units (inventory full!)");
                else
                    SetStatus($"Transferred {totalTransferredUnits} units");

            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Smart Fill error: {ex.Message}");
                SetStatus("Transfer error!");
            }
        }

        /// <summary>
        /// Tries to place items into the player's hotbar, stacking first, then filling empty slots.
        /// Works in packaged item counts (not product units).
        /// </summary>
        private static int TryPlaceInHotbar(GameSystem.Collections.Generic.List<HotbarSlot> hotbar, ItemInstance sourceItem, int amount)
        {
            int placed = 0;

            // First pass: stack into existing matching slots
            for (int i = 0; i < hotbar.Count && placed < amount; i++)
            {
                var slot = hotbar[i];
                if (slot == null || slot.ItemInstance == null) continue;

                try
                {
                    // checkQuantities: false — we cap the add amount manually below;
                    // default (true) rejects stacking when source slot qty is large
                    if (!slot.ItemInstance.CanStackWith(sourceItem, false)) continue;

                    int stackLimit;
                    try { stackLimit = slot.ItemInstance.StackLimit; }
                    catch { stackLimit = 20; }

                    int canAdd = stackLimit - slot.Quantity;
                    if (canAdd <= 0) continue;

                    int toAdd = Math.Min(canAdd, amount - placed);
                    slot.ChangeQuantity(toAdd);
                    placed += toAdd;
                }
                catch { }
            }

            // Second pass: place into empty slots
            for (int i = 0; i < hotbar.Count && placed < amount; i++)
            {
                var slot = hotbar[i];
                if (slot == null) continue;
                if (slot.ItemInstance != null) continue;

                try
                {
                    int toPlace = amount - placed;

                    int stackLimit;
                    try { stackLimit = sourceItem.StackLimit; }
                    catch { stackLimit = 20; }

                    toPlace = Math.Min(toPlace, stackLimit);

                    var clone = sourceItem.GetCopy(toPlace);
                    slot.SetStoredItem(clone);

                    placed += toPlace;
                }
                catch { }
            }

            return placed;
        }

        /// <summary>
        /// Tries to place items into backpack slots, stacking first, then filling empty slots.
        /// </summary>
        private static int TryPlaceInSlots(ItemSlot[] slots, ItemInstance sourceItem, int amount)
        {
            int placed = 0;

            for (int i = 0; i < slots.Length && placed < amount; i++)
            {
                var slot = slots[i];
                if (slot == null || slot.ItemInstance == null) continue;

                try
                {
                    if (!slot.ItemInstance.CanStackWith(sourceItem, false)) continue;

                    int stackLimit;
                    try { stackLimit = slot.ItemInstance.StackLimit; }
                    catch { stackLimit = 20; }

                    int canAdd = stackLimit - slot.Quantity;
                    if (canAdd <= 0) continue;

                    int toAdd = Math.Min(canAdd, amount - placed);
                    slot.ChangeQuantity(toAdd);
                    placed += toAdd;
                }
                catch { }
            }

            for (int i = 0; i < slots.Length && placed < amount; i++)
            {
                var slot = slots[i];
                if (slot == null) continue;
                if (slot.ItemInstance != null) continue;

                try
                {
                    int toPlace = amount - placed;

                    int stackLimit;
                    try { stackLimit = sourceItem.StackLimit; }
                    catch { stackLimit = 20; }

                    toPlace = Math.Min(toPlace, stackLimit);

                    var clone = sourceItem.GetCopy(toPlace);
                    slot.SetStoredItem(clone);

                    placed += toPlace;
                }
                catch { }
            }

            return placed;
        }

        private static void SetStatus(string message)
        {
            if (_statusText != null)
                _statusText.text = message;
        }
    }
}
