using Il2CppInterop.Runtime;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Tools;
using Il2CppScheduleOne.UI.Management;
using Il2CppTMPro;
using MelonLoader;
using MelonLoader.Utils;
using OverTheCounter.Logic;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OverTheCounter.UI
{
    /// <summary>
    /// Config panel for Managers that clones the vanilla PackagerConfigPanel (Handler) prefab
    /// for pixel-perfect visual match. Layout: Locker + Supplies + Routes (up to 3).
    /// </summary>
    public static class ManagerConfigPanel
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("ManagerConfigPanel");

        private static GameObject _panelRoot;
        private static ManagerInstance _currentManager;

        // Locker UI refs (from vanilla BedUI / ObjectFieldUI)
        private static TextMeshProUGUI _lockerSelectionLabel;
        private static Image _lockerIcon;
        private static GameObject _lockerNoneSelected;
        private static RectTransform _lockerClearButton;

        // Supply Container UI refs (cloned from BedUI)
        private static TextMeshProUGUI _supplySelectionLabel;
        private static Image _supplyIcon;
        private static GameObject _supplyNoneSelected;
        private static RectTransform _supplyClearButton;

        // Route entry UI refs (from vanilla RouteEntryUI)
        private struct RouteEntryRef
        {
            public GameObject Root;
            public TextMeshProUGUI SourceLabel;
            public Image SourceIcon;
            public TextMeshProUGUI DestLabel;
            public Image DestIcon;
        }
        private static RouteEntryRef[] _routes = new RouteEntryRef[3];
        private static bool[] _routeVisible = new bool[3];
        private static GameObject _addRouteButton;

        // Item slot UI refs (5 squares cloned from ItemSelector.OptionPrefab)
        private struct ItemSlotRef
        {
            public GameObject Root;
            public Image Icon;
            public GameObject NoneIndicator;
            public TextMeshProUGUI ThresholdLabel;
        }
        private static ItemSlotRef[] _itemSlots = new ItemSlotRef[5];

        // Pending item selection (between product pick and threshold confirm)
        private static string _pendingItemId;
        private static int _pendingSlotIndex = -1;
        private static Il2CppScheduleOne.ItemFramework.ItemDefinition _pendingItemDef;
        private static int _pendingStackLimit = 20;

        // Threshold screen (shown after product selection)
        private static GameObject _thresholdScreenRoot;
        private static UnityEngine.UI.Slider _thresholdSlider;
        private static Action _thresholdConfirmAction; // prevent GC of IL2CPP delegate

        // SelectionInfoUI override tracking
        private static SelectionInfoUI _selectionInfo;
        private static bool _originalSelfUpdate;

        // Hold IL2CPP callback refs to prevent GC
        private static Il2CppSystem.Action<Il2CppSystem.Collections.Generic.List<BuildableItem>> _selectorCallback;
        private static Il2CppSystem.Action<ItemSelector.Option> _itemSelectorCallback;

        // Cached manager icon sprite
        private static Sprite _managerIcon;

        public static bool IsOpen => _panelRoot != null;
        public static string CurrentManagerId => _currentManager?.Id;

        /// <summary>
        /// Syncs config changes to all players. Host publishes directly via SyncVar;
        /// clients send the full config to the host via quest action.
        /// </summary>
        private static void SyncConfig()
        {
            if (_currentManager == null) return;
            if (NetworkHelper.IsHost)
            {
                ConfigSyncData.Instance?.PublishManagerState();
            }
            else
            {
                string configStr = ManagerInstance.EncodeConfig(_currentManager.Configuration.Serialize());
                ConfigSyncData.SendQuestAction($"MANAGER_CONFIG:{_currentManager.Id}:{configStr}");
            }
        }

        /// <summary>
        /// Per-frame enforcement of our UI overrides. Called from UpdatePostfix every frame
        /// while our panel is open. Handles the case where ObjectSelector.Close() re-opens the
        /// clipboard (resetting SelectionInfo and NothingSelectedLabel).
        /// </summary>
        public static void EnforceUI()
        {
            try
            {
                // Hide NothingSelectedLabel (ManagementInterface.Open re-enables it on every clipboard open)
                var mi = Singleton<ManagementInterface>.Instance;
                if (mi?.NothingSelectedLabel != null && mi.NothingSelectedLabel.gameObject.activeSelf)
                    mi.NothingSelectedLabel.gameObject.SetActive(false);

                // Force SelectionInfo to show "1x Manager" (ManagementClipboard.Open resets it via Set(emptyList))
                var clipboard = Singleton<ManagementClipboard>.Instance;
                if (clipboard?.SelectionInfo != null)
                {
                    var si = clipboard.SelectionInfo;
                    si.SelfUpdate = false;

                    if (si.Title != null && si.Title.text != "1x Manager")
                        si.Title.text = "1x Manager";

                    if (si.Icon != null)
                    {
                        var icon = GetManagerIcon();
                        if (icon != null)
                        {
                            si.Icon.sprite = icon;
                            if (!si.Icon.gameObject.activeSelf)
                                si.Icon.gameObject.SetActive(true);
                        }
                        else if (si.Icon.gameObject.activeSelf)
                        {
                            si.Icon.gameObject.SetActive(false);
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Opens the config panel for a Manager. Called after ManagementClipboard.Open().
        /// Clones the vanilla PackagerConfigPanel prefab for exact visual match.
        /// </summary>
        public static void Open(ManagerInstance mgr)
        {
            if (mgr == null) return;
            Close();
            _currentManager = mgr;
            CreatePanel();
            Logger.Msg($"Config panel opened for manager {mgr.Id}");
        }

        /// <summary>
        /// Closes and destroys the config panel, restoring vanilla SelectionInfoUI state.
        /// </summary>
        public static void Close()
        {
            // Clean up any pending threshold screen
            CleanupThresholdScreen();
            ClearPendingItemData();

            if (_panelRoot != null)
            {
                UnityEngine.Object.Destroy(_panelRoot);
                _panelRoot = null;
            }

            // Restore SelectionInfoUI
            if (_selectionInfo != null)
            {
                try { _selectionInfo.SelfUpdate = _originalSelfUpdate; } catch { }
                _selectionInfo = null;
            }

            _currentManager = null;
            _lockerSelectionLabel = null;
            _lockerIcon = null;
            _lockerNoneSelected = null;
            _lockerClearButton = null;
            _supplySelectionLabel = null;
            _supplyIcon = null;
            _supplyNoneSelected = null;
            _supplyClearButton = null;
            _itemSlots = new ItemSlotRef[5];
            _routes = new RouteEntryRef[3];
            _routeVisible = new bool[3];
            _addRouteButton = null;
        }

        private static void CreatePanel()
        {
            var mi = Singleton<ManagementInterface>.Instance;
            if (mi == null)
            {
                Logger.Warning("ManagementInterface not available");
                return;
            }

            // 1. Find the Packager (Handler) config panel prefab GameObject
            GameObject prefabGO = null;
            var prefabs = mi.ConfigPanelPrefabs;
            if (prefabs != null)
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    var entry = prefabs[i];
                    if (entry != null && entry.Type == EConfigurableType.Packager && entry.Panel != null)
                    {
                        prefabGO = entry.Panel.gameObject;
                        break;
                    }
                }
            }
            if (prefabGO == null)
            {
                Logger.Warning("Packager config panel prefab not found");
                return;
            }

            // 2. Clone the prefab into PanelContainer
            _panelRoot = UnityEngine.Object.Instantiate(prefabGO, mi.PanelContainer);
            _panelRoot.name = "ManagerConfigPanel";
            _panelRoot.SetActive(true);

            // 3. Read serialized refs from the cloned vanilla components
            var packagerPanel = _panelRoot.GetComponent<PackagerConfigPanel>();
            if (packagerPanel == null)
            {
                Logger.Warning("PackagerConfigPanel component not found on clone");
                return;
            }

            var bedUI = packagerPanel.BedUI;
            var stationsUI = packagerPanel.StationsUI;
            var routesUI = packagerPanel.RoutesUI;

            // 4. Clone BedUI gameObject for the Supply Container field (before destroying components)
            GameObject supplyGO = null;
            ObjectFieldUI supplyFieldUI = null;
            if (bedUI != null)
            {
                supplyGO = UnityEngine.Object.Instantiate(bedUI.gameObject, bedUI.transform.parent);
                supplyGO.transform.SetSiblingIndex(bedUI.transform.GetSiblingIndex() + 1);
                supplyGO.name = "SupplyContainerField";
                supplyFieldUI = supplyGO.GetComponent<ObjectFieldUI>();
            }

            // 5. Position supply clone at StationsUI's location (avoids overlap with Locker)
            //    The parent uses absolute RectTransform positioning, not a LayoutGroup,
            //    so the clone would be on top of the original without this.
            if (supplyGO != null && stationsUI != null)
            {
                var supplyRT = supplyGO.GetComponent<RectTransform>();
                var stationsRT = stationsUI.GetComponent<RectTransform>();
                if (supplyRT != null && stationsRT != null)
                    supplyRT.anchoredPosition = stationsRT.anchoredPosition;
            }

            // 5b. Create Products section with square item slots (OptionPrefab clones)
            var bedRT = bedUI?.GetComponent<RectTransform>();
            var stationsRTRef = stationsUI?.GetComponent<RectTransform>();
            if (bedRT != null && stationsRTRef != null)
            {
                float sectionSpacing = bedRT.anchoredPosition.y - stationsRTRef.anchoredPosition.y;
                float topPad = 75f;
                float lockerSupplyGap = 5f;

                // Push Locker and Supply down for breathing room at top,
                // with extra gap between Locker and Supply
                bedRT.anchoredPosition = new Vector2(bedRT.anchoredPosition.x, bedRT.anchoredPosition.y - topPad);
                var supplyRT2 = supplyGO?.GetComponent<RectTransform>();
                if (supplyRT2 != null)
                    supplyRT2.anchoredPosition = new Vector2(supplyRT2.anchoredPosition.x, supplyRT2.anchoredPosition.y - topPad - lockerSupplyGap);

                // Clone BedUI for "Products" section label (own line above squares)
                var productsGO = UnityEngine.Object.Instantiate(bedUI.gameObject, bedUI.transform.parent);
                productsGO.name = "ProductsSection";
                var productsFieldUI = productsGO.GetComponent<ObjectFieldUI>();

                // Products label sits one section below the new Supply position
                float newSupplyY = stationsRTRef.anchoredPosition.y - topPad - lockerSupplyGap;
                var productsRT = productsGO.GetComponent<RectTransform>();
                if (productsRT != null)
                    productsRT.anchoredPosition = new Vector2(
                        stationsRTRef.anchoredPosition.x,
                        newSupplyY - sectionSpacing);

                // Set label to "Products" — centered, bolder styling, hide all selection UI
                if (productsFieldUI != null)
                {
                    if (productsFieldUI.FieldLabel != null)
                    {
                        productsFieldUI.FieldLabel.text = "Products";
                        productsFieldUI.FieldLabel.fontSize *= 1.15f;
                        productsFieldUI.FieldLabel.fontStyle = Il2CppTMPro.FontStyles.Bold;
                        productsFieldUI.FieldLabel.alignment = Il2CppTMPro.TextAlignmentOptions.Center;
                        // Stretch label across full width for centering
                        var labelRT = productsFieldUI.FieldLabel.rectTransform;
                        if (labelRT != null)
                        {
                            labelRT.anchorMin = new Vector2(0, 0);
                            labelRT.anchorMax = new Vector2(1, 1);
                            labelRT.offsetMin = Vector2.zero;
                            labelRT.offsetMax = Vector2.zero;
                        }
                    }
                    if (productsFieldUI.NoneSelected != null) productsFieldUI.NoneSelected.SetActive(false);
                    if (productsFieldUI.SelectionLabel != null) productsFieldUI.SelectionLabel.gameObject.SetActive(false);
                    if (productsFieldUI.IconImg != null) productsFieldUI.IconImg.gameObject.SetActive(false);
                    if (productsFieldUI.ClearButton != null) productsFieldUI.ClearButton.gameObject.SetActive(false);
                    if (productsFieldUI.MultipleSelected != null) productsFieldUI.MultipleSelected.SetActive(false);
                }

                // Disable click/clear buttons and hide their background images (white bar)
                var prodBtn = FindButtonByPersistentMethod(productsGO, "Clicked");
                if (prodBtn != null)
                {
                    prodBtn.onClick.RemoveAllListeners();
                    prodBtn.interactable = false;
                    var btnImg = prodBtn.GetComponent<Image>();
                    if (btnImg != null) btnImg.enabled = false;
                }
                var prodClearBtn = FindButtonByPersistentMethod(productsGO, "ClearClicked");
                if (prodClearBtn != null)
                {
                    prodClearBtn.onClick.RemoveAllListeners();
                    prodClearBtn.interactable = false;
                }

                // Squares row sits just below the "Products" label (tight gap)
                float squaresRowY = newSupplyY - sectionSpacing - sectionSpacing * 0.55f;

                // Clone OptionPrefab 5 times for product slot squares
                var optionPrefab = mi.ItemSelectorScreen?.OptionPrefab;
                if (optionPrefab != null)
                {
                    // Size squares centered in the panel row
                    float gap = 6f;
                    float squareSize = 65f;
                    float totalRow = 5f * squareSize + 4f * gap;
                    // Use actual parent width instead of a hardcoded estimate
                    var parentRT = bedUI.transform.parent.GetComponent<RectTransform>();
                    float panelWidth = parentRT != null ? parentRT.rect.width : 350f;
                    if (panelWidth <= 0f) panelWidth = 350f; // fallback if layout not yet computed
                    float startX = (panelWidth - totalRow) / 2f;
                    if (startX < 0f) startX = 0f;

                    for (int i = 0; i < 5; i++)
                    {
                        // Squares are siblings in the panel (not children of productsGO)
                        // so they get their own absolute position
                        var cell = UnityEngine.Object.Instantiate(optionPrefab, bedUI.transform.parent);
                        cell.name = $"ProductSlot_{i}";
                        cell.SetActive(true);

                        var cellRT = cell.GetComponent<RectTransform>();
                        if (cellRT != null)
                        {
                            cellRT.anchorMin = new Vector2(0, 1);
                            cellRT.anchorMax = new Vector2(0, 1);
                            cellRT.pivot = new Vector2(0, 1);
                            cellRT.sizeDelta = new Vector2(squareSize, squareSize);
                            cellRT.anchoredPosition = new Vector2(startX + i * (squareSize + gap), squaresRowY);
                        }

                        // Threshold label below the square (e.g. "(20)")
                        var threshGO = new GameObject($"ThresholdLabel_{i}");
                        threshGO.transform.SetParent(bedUI.transform.parent, false);
                        var threshTMP = threshGO.AddComponent<TextMeshProUGUI>();
                        threshTMP.text = "";
                        threshTMP.fontSize = 15;
                        threshTMP.alignment = TextAlignmentOptions.Center;
                        threshTMP.color = new Color(0.25f, 0.25f, 0.25f);
                        var threshRT = threshGO.GetComponent<RectTransform>();
                        threshRT.anchorMin = new Vector2(0, 1);
                        threshRT.anchorMax = new Vector2(0, 1);
                        threshRT.pivot = new Vector2(0.5f, 1);
                        float squareCenter = startX + i * (squareSize + gap) + squareSize / 2f;
                        threshRT.anchoredPosition = new Vector2(squareCenter, squaresRowY - squareSize - 1f);
                        threshRT.sizeDelta = new Vector2(squareSize + 10f, 14);

                        _itemSlots[i] = new ItemSlotRef
                        {
                            Root = cell,
                            Icon = cell.transform.Find("Icon")?.GetComponent<Image>(),
                            NoneIndicator = cell.transform.Find("None")?.gameObject,
                            ThresholdLabel = threshTMP
                        };

                        var cellBtn = cell.GetComponent<Button>();
                        int capturedIndex = i;
                        if (cellBtn != null)
                        {
                            cellBtn.onClick.RemoveAllListeners();
                            cellBtn.onClick.AddListener(new Action(() => OnItemSlotClicked(capturedIndex)));
                        }

                        // Remove hover EventTrigger (designed for ItemSelector grid, not our panel)
                        var trigger = cell.GetComponent<EventTrigger>();
                        if (trigger != null) UnityEngine.Object.Destroy(trigger);
                    }
                }

                // Destroy ObjectFieldUI on products section
                if (productsFieldUI != null) { productsFieldUI.enabled = false; UnityEngine.Object.Destroy(productsFieldUI); }

                // Shift Routes down (topPad + lockerSupplyGap + Products label + squares + threshold labels)
                float totalExtra = topPad + lockerSupplyGap + 1.6f * sectionSpacing + 15f;
                var routesRT = routesUI?.GetComponent<RectTransform>();
                if (routesRT != null)
                    routesRT.anchoredPosition = new Vector2(
                        routesRT.anchoredPosition.x,
                        routesRT.anchoredPosition.y - totalExtra);

                // Extend panel height
                var panelRT = _panelRoot.GetComponent<RectTransform>();
                if (panelRT != null)
                    panelRT.sizeDelta = new Vector2(panelRT.sizeDelta.x, panelRT.sizeDelta.y + totalExtra);
            }

            // Hide Stations section (replaced by our Supply Container clone + item slots)
            if (stationsUI != null)
            {
                stationsUI.gameObject.SetActive(false);
                stationsUI.enabled = false;
            }

            // 6. Setup Locker section (BedUI keeps vanilla "Locker" label)
            SetupLockerSection(bedUI);

            // 7. Setup Supply Container section (cloned from BedUI)
            SetupSupplySection(supplyFieldUI, supplyGO);

            // 8. Setup Routes section (with Add New behavior)
            SetupRoutes(routesUI);

            // 9. Disable/destroy vanilla script components to prevent interference
            if (packagerPanel != null) { packagerPanel.enabled = false; UnityEngine.Object.Destroy(packagerPanel); }
            if (bedUI != null) { bedUI.enabled = false; UnityEngine.Object.Destroy(bedUI); }
            if (supplyFieldUI != null) { supplyFieldUI.enabled = false; UnityEngine.Object.Destroy(supplyFieldUI); }
            if (routesUI != null) { routesUI.enabled = false; UnityEngine.Object.Destroy(routesUI); }

            // 10. Fix SelectionInfoUI to show "1x Manager"
            FixSelectionInfo();

            // 11. Refresh all labels with current configuration
            RefreshLabels();
        }

        // ========== Locker Section ==========

        private static void SetupLockerSection(ObjectFieldUI bedUI)
        {
            if (bedUI == null) return;

            // Set label to "Locker" (vanilla prefab label says "Home")
            if (bedUI.FieldLabel != null)
                bedUI.FieldLabel.text = "Locker";

            _lockerSelectionLabel = bedUI.SelectionLabel;
            _lockerIcon = bedUI.IconImg;
            _lockerNoneSelected = bedUI.NoneSelected;
            _lockerClearButton = bedUI.ClearButton;

            if (bedUI.MultipleSelected != null)
                bedUI.MultipleSelected.SetActive(false);

            var mainBtn = FindButtonByPersistentMethod(bedUI.gameObject, "Clicked");
            if (mainBtn != null)
                RewireButton(mainBtn, () => OnSlotClicked("Locker", -1, false));

            var clearBtn = FindButtonByPersistentMethod(bedUI.gameObject, "ClearClicked");
            if (clearBtn == null && _lockerClearButton != null)
                clearBtn = _lockerClearButton.GetComponent<Button>();
            if (clearBtn != null)
                RewireButton(clearBtn, () => OnClearClicked("Locker", -1, false));
        }

        // ========== Supply Container Section ==========

        private static void SetupSupplySection(ObjectFieldUI fieldUI, GameObject supplyGO)
        {
            if (fieldUI == null || supplyGO == null) return;

            // Change label to "Supply Drop"
            if (fieldUI.FieldLabel != null)
                fieldUI.FieldLabel.text = "Supply Drop";

            _supplySelectionLabel = fieldUI.SelectionLabel;
            _supplyIcon = fieldUI.IconImg;
            _supplyNoneSelected = fieldUI.NoneSelected;
            _supplyClearButton = fieldUI.ClearButton;

            if (fieldUI.MultipleSelected != null)
                fieldUI.MultipleSelected.SetActive(false);

            var mainBtn = FindButtonByPersistentMethod(supplyGO, "Clicked");
            if (mainBtn != null)
                RewireButton(mainBtn, () => OnSlotClicked("Supply", -1, false));

            var clearBtn = FindButtonByPersistentMethod(supplyGO, "ClearClicked");
            if (clearBtn == null && _supplyClearButton != null)
                clearBtn = _supplyClearButton.GetComponent<Button>();
            if (clearBtn != null)
                RewireButton(clearBtn, () => OnClearClicked("Supply", -1, false));
        }

        // ========== Item Slots Section ==========

        private static void OnItemSlotClicked(int slotIndex)
        {
            if (_currentManager == null) return;

            var mi = Singleton<ManagementInterface>.Instance;
            if (mi?.ItemSelectorScreen == null)
            {
                Logger.Warning("ItemSelector not available");
                return;
            }

            // Build options list
            var options = new Il2CppSystem.Collections.Generic.List<ItemSelector.Option>();

            // "None" option first (shows as X icon in the grid)
            options.Add(new ItemSelector.Option("None", null));

            // Add available items (mixing ingredients + consumables)
            var items = GetAvailableItems();
            foreach (var item in items)
            {
                options.Add(new ItemSelector.Option(item.Name, item));
            }

            // Find current selection for highlight
            ItemSelector.Option selectedOption = null;
            string currentId = _currentManager.Configuration.StockedItemIds[slotIndex];
            if (!string.IsNullOrEmpty(currentId))
            {
                for (int i = 0; i < options.Count; i++)
                {
                    if (options[i].Item != null && options[i].Item.ID == currentId)
                    {
                        selectedOption = options[i];
                        break;
                    }
                }
            }

            int capturedIndex = slotIndex;
            _itemSelectorCallback = (Il2CppSystem.Action<ItemSelector.Option>)
                new Action<ItemSelector.Option>(opt => OnItemSelected(capturedIndex, opt));

            mi.ItemSelectorScreen.Initialize(
                "Products", options, selectedOption, _itemSelectorCallback);
            mi.ItemSelectorScreen.Open();
        }

        private static void OnItemSelected(int slotIndex, ItemSelector.Option option)
        {
            if (_currentManager == null) return;
            if (slotIndex < 0 || slotIndex >= 5) return;

            string itemId = option?.Item?.ID;

            // If "None" selected, clear immediately
            if (string.IsNullOrEmpty(itemId))
            {
                _currentManager.Configuration.StockedItemIds[slotIndex] = null;
                _currentManager.Configuration.StockedThresholds[slotIndex] = 0;
                SyncConfig();
                RefreshItemSlots();
                return;
            }

            // If this item is already assigned to another slot, ignore the selection
            var config = _currentManager.Configuration;
            for (int i = 0; i < config.StockedItemIds.Length; i++)
            {
                if (i == slotIndex) continue;
                if (string.Equals(config.StockedItemIds[i], itemId, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            // Store pending selection — show threshold screen after ItemSelector closes
            _pendingItemId = itemId;
            _pendingItemDef = option?.Item;
            _pendingSlotIndex = slotIndex;

            // Use existing threshold if re-configuring, otherwise default to item's stack limit
            int stackLimit = 20;
            try { if (option?.Item != null) stackLimit = option.Item.StackLimit; } catch { }
            if (stackLimit <= 0) stackLimit = 20;
            bool slotHadItem = !string.IsNullOrEmpty(_currentManager.Configuration.StockedItemIds[slotIndex]);
            int initialThreshold = slotHadItem
                ? _currentManager.Configuration.StockedThresholds[slotIndex]
                : stackLimit;

            // Delay one frame so PanelContainer is active when we create the UI
            MelonCoroutines.Start(ShowThresholdScreenDelayed(initialThreshold));
        }

        /// <summary>
        /// Waits one frame for PanelContainer to become active (ItemSelector.Close → MainScreen.Open),
        /// then shows the threshold screen. Without this delay, buttons created while
        /// the hierarchy is inactive never register with Unity's EventSystem.
        /// </summary>
        private static IEnumerator ShowThresholdScreenDelayed(int currentThreshold)
        {
            yield return null;
            ShowThresholdScreen(currentThreshold);
        }

        /// <summary>
        /// Shows a dedicated threshold screen after the user selects a product.
        /// Hides the main config panel and displays item info + slider + confirm button.
        /// Created as a child of PanelContainer so it appears when MainScreen reopens.
        /// </summary>
        private static void ShowThresholdScreen(int currentThreshold)
        {
            CleanupThresholdScreen();

            try
            {
                var mi = Singleton<ManagementInterface>.Instance;
                if (mi == null) return;

                // Hide the main config panel (threshold screen replaces it temporarily)
                if (_panelRoot != null)
                    _panelRoot.SetActive(false);

                _thresholdScreenRoot = new GameObject("ManagerThresholdScreen");
                _thresholdScreenRoot.transform.SetParent(mi.PanelContainer, false);
                var rootRT = _thresholdScreenRoot.AddComponent<RectTransform>();
                rootRT.anchorMin = Vector2.zero;
                rootRT.anchorMax = Vector2.one;
                rootRT.offsetMin = Vector2.zero;
                rootRT.offsetMax = Vector2.zero;

                // Item icon
                if (_pendingItemDef?.Icon != null)
                {
                    var iconGO = new GameObject("ItemIcon");
                    iconGO.transform.SetParent(_thresholdScreenRoot.transform, false);
                    var iconImg = iconGO.AddComponent<Image>();
                    iconImg.sprite = _pendingItemDef.Icon;
                    iconImg.preserveAspect = true;
                    var iconRT = iconGO.GetComponent<RectTransform>();
                    iconRT.anchorMin = new Vector2(0.5f, 1);
                    iconRT.anchorMax = new Vector2(0.5f, 1);
                    iconRT.pivot = new Vector2(0.5f, 1);
                    iconRT.anchoredPosition = new Vector2(0, -20f);
                    iconRT.sizeDelta = new Vector2(90, 90);
                }

                // Item name
                if (_pendingItemDef != null)
                {
                    var nameGO = new GameObject("ItemName");
                    nameGO.transform.SetParent(_thresholdScreenRoot.transform, false);
                    var nameTMP = nameGO.AddComponent<TextMeshProUGUI>();
                    nameTMP.text = _pendingItemDef.Name;
                    nameTMP.fontSize = 18;
                    nameTMP.alignment = TextAlignmentOptions.Center;
                    nameTMP.color = Color.black;
                    var nameRT = nameGO.GetComponent<RectTransform>();
                    nameRT.anchorMin = new Vector2(0, 1);
                    nameRT.anchorMax = new Vector2(1, 1);
                    nameRT.pivot = new Vector2(0.5f, 1);
                    nameRT.anchoredPosition = new Vector2(0, -115f);
                    nameRT.sizeDelta = new Vector2(0, 25);
                }

                // Description text
                var descGO = new GameObject("Description");
                descGO.transform.SetParent(_thresholdScreenRoot.transform, false);
                var descTMP = descGO.AddComponent<TextMeshProUGUI>();
                descTMP.text = "How many should the manager\nkeep in stock at the supply drop?";
                descTMP.fontSize = 14;
                descTMP.alignment = TextAlignmentOptions.Center;
                descTMP.color = new Color(0.25f, 0.25f, 0.25f);
                var descRT = descGO.GetComponent<RectTransform>();
                descRT.anchorMin = new Vector2(0, 1);
                descRT.anchorMax = new Vector2(1, 1);
                descRT.pivot = new Vector2(0.5f, 1);
                descRT.anchoredPosition = new Vector2(0, -150f);
                descRT.sizeDelta = new Vector2(-20f, 40);

                // Clone NumberFieldUI for the slider
                Il2CppScheduleOne.UI.Management.NumberFieldUI sourceField = null;
                var prefabs = mi.ConfigPanelPrefabs;
                if (prefabs != null)
                {
                    for (int i = 0; i < prefabs.Length; i++)
                    {
                        var entry = prefabs[i];
                        if (entry?.Panel == null) continue;
                        sourceField = entry.Panel.GetComponentInChildren<Il2CppScheduleOne.UI.Management.NumberFieldUI>(true);
                        if (sourceField != null) break;
                    }
                }

                if (sourceField != null)
                {
                    var sliderGO = UnityEngine.Object.Instantiate(sourceField.gameObject, _thresholdScreenRoot.transform);
                    sliderGO.name = "ThresholdSlider";

                    var nfUI = sliderGO.GetComponent<Il2CppScheduleOne.UI.Management.NumberFieldUI>();
                    if (nfUI != null)
                    {
                        // Use the item's actual stack limit as the slider step
                        int stackLimit = 20;
                        try { if (_pendingItemDef != null) stackLimit = _pendingItemDef.StackLimit; } catch { }
                        if (stackLimit <= 0) stackLimit = 20;
                        _pendingStackLimit = stackLimit;

                        nfUI.Slider.onValueChanged.RemoveAllListeners();
                        nfUI.FieldLabel.text = "Max Stock";
                        nfUI.Slider.minValue = 1;
                        nfUI.Slider.maxValue = 5;
                        nfUI.Slider.wholeNumbers = true;
                        float sliderVal = Mathf.Clamp(currentThreshold / (float)stackLimit, 1f, 5f);
                        nfUI.Slider.SetValueWithoutNotify(sliderVal);
                        nfUI.ValueLabel.text = (Mathf.RoundToInt(sliderVal) * stackLimit).ToString();
                        nfUI.MinValueLabel.text = stackLimit.ToString();
                        nfUI.MaxValueLabel.text = (stackLimit * 5).ToString();

                        _thresholdSlider = nfUI.Slider;

                        int capturedLimit = stackLimit;
                        nfUI.Slider.onValueChanged.AddListener(new Action<float>(val =>
                        {
                            int displayVal = Mathf.RoundToInt(val) * capturedLimit;
                            nfUI.ValueLabel.text = displayVal.ToString();
                        }));
                    }

                    // Remove any extra description text children (e.g. mixing station text)
                    var texts = sliderGO.GetComponentsInChildren<TextMeshProUGUI>(true);
                    foreach (var txt in texts)
                    {
                        if (nfUI != null && (txt == nfUI.FieldLabel || txt == nfUI.ValueLabel ||
                            txt == nfUI.MinValueLabel || txt == nfUI.MaxValueLabel))
                            continue;
                        UnityEngine.Object.Destroy(txt.gameObject);
                    }

                    var sliderRT = sliderGO.GetComponent<RectTransform>();
                    if (sliderRT != null)
                    {
                        sliderRT.anchorMin = new Vector2(0, 1);
                        sliderRT.anchorMax = new Vector2(1, 1);
                        sliderRT.pivot = new Vector2(0.5f, 1);
                        sliderRT.anchoredPosition = new Vector2(0, -200f);
                        sliderRT.sizeDelta = new Vector2(-20f, 50f);
                    }

                    sliderGO.SetActive(true);
                }

                // Confirm button
                var confirmGO = new GameObject("ConfirmButton");
                confirmGO.transform.SetParent(_thresholdScreenRoot.transform, false);
                var confirmImg = confirmGO.AddComponent<Image>();
                confirmImg.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);
                var confirmBtn = confirmGO.AddComponent<Button>();
                _thresholdConfirmAction = new Action(OnThresholdConfirmed);
                confirmBtn.onClick.AddListener(_thresholdConfirmAction);
                var confirmRT = confirmGO.GetComponent<RectTransform>();
                confirmRT.anchorMin = new Vector2(0.5f, 1);
                confirmRT.anchorMax = new Vector2(0.5f, 1);
                confirmRT.pivot = new Vector2(0.5f, 1);
                confirmRT.anchoredPosition = new Vector2(0, -275f);
                confirmRT.sizeDelta = new Vector2(130, 36);

                var confirmTextGO = new GameObject("Text");
                confirmTextGO.transform.SetParent(confirmGO.transform, false);
                var confirmTMP = confirmTextGO.AddComponent<TextMeshProUGUI>();
                confirmTMP.text = "Confirm";
                confirmTMP.fontSize = 17;
                confirmTMP.alignment = TextAlignmentOptions.Center;
                confirmTMP.color = Color.white;
                var confirmTextRT = confirmTextGO.GetComponent<RectTransform>();
                confirmTextRT.anchorMin = Vector2.zero;
                confirmTextRT.anchorMax = Vector2.one;
                confirmTextRT.offsetMin = Vector2.zero;
                confirmTextRT.offsetMax = Vector2.zero;

                _thresholdScreenRoot.SetActive(true);
                Logger.Msg($"Showing threshold screen for {_pendingItemDef?.Name} (current={currentThreshold})");
            }
            catch (Exception ex)
            {
                Logger.Warning($"ShowThresholdScreen failed: {ex.Message}");
                // On failure, restore config panel
                if (_panelRoot != null) _panelRoot.SetActive(true);
            }
        }

        /// <summary>
        /// Called when the user confirms the threshold selection.
        /// Saves the pending item + threshold, then returns to the config panel.
        /// </summary>
        private static void OnThresholdConfirmed()
        {
            // Save pending data before cleanup clears the pending fields
            string savedItemId = _pendingItemId;
            int savedSlot = _pendingSlotIndex;
            int savedThreshold = 20;

            if (_thresholdSlider != null)
                savedThreshold = Mathf.Clamp(
                    Mathf.RoundToInt(_thresholdSlider.value) * _pendingStackLimit,
                    _pendingStackLimit, _pendingStackLimit * 5);

            // Cleanup first — destroys threshold screen and restores config panel visibility
            CleanupThresholdScreen();
            ClearPendingItemData();

            // Now apply the saved data with the panel visible
            if (_currentManager != null && savedSlot >= 0 && savedSlot < 5 && !string.IsNullOrEmpty(savedItemId))
            {
                _currentManager.Configuration.StockedItemIds[savedSlot] = savedItemId;
                _currentManager.Configuration.StockedThresholds[savedSlot] = savedThreshold;

                SyncConfig();

                RefreshItemSlots();
                Logger.Msg($"Manager {_currentManager.Id}: slot {savedSlot} = {savedItemId} (threshold={savedThreshold})");
            }
        }

        /// <summary>
        /// Cleans up the threshold screen and restores the main config panel.
        /// </summary>
        /// <summary>
        /// Destroys the threshold screen UI and restores config panel visibility.
        /// Does NOT clear pending item data — callers must clear that themselves if needed.
        /// </summary>
        private static void CleanupThresholdScreen()
        {
            if (_thresholdScreenRoot != null)
            {
                UnityEngine.Object.Destroy(_thresholdScreenRoot);
                _thresholdScreenRoot = null;
            }

            _thresholdSlider = null;
            _thresholdConfirmAction = null;

            // Restore config panel visibility
            if (_panelRoot != null)
                _panelRoot.SetActive(true);
        }

        private static void ClearPendingItemData()
        {
            _pendingItemId = null;
            _pendingItemDef = null;
            _pendingSlotIndex = -1;
            _pendingStackLimit = 20;
        }

        private static void RefreshItemSlots()
        {
            if (_currentManager == null) return;
            var config = _currentManager.Configuration;

            for (int i = 0; i < 5; i++)
            {
                var slot = _itemSlots[i];
                if (slot.Root == null) continue;

                string itemId = config.StockedItemIds[i];
                bool hasItem = !string.IsNullOrEmpty(itemId);

                Il2CppScheduleOne.ItemFramework.ItemDefinition itemDef = null;
                if (hasItem)
                {
                    try { itemDef = Il2CppScheduleOne.Registry.GetItem(itemId); }
                    catch { }
                }

                // Show item icon or "None" X indicator (same pattern as ItemSelector.CreateOptions)
                if (slot.Icon != null)
                {
                    if (itemDef?.Icon != null)
                    {
                        slot.Icon.sprite = itemDef.Icon;
                        slot.Icon.gameObject.SetActive(true);
                    }
                    else
                    {
                        slot.Icon.gameObject.SetActive(false);
                    }
                }

                if (slot.NoneIndicator != null)
                    slot.NoneIndicator.SetActive(itemDef == null);

                // Show threshold under the square when an item is assigned
                if (slot.ThresholdLabel != null)
                {
                    if (hasItem)
                        slot.ThresholdLabel.text = $"({config.StockedThresholds[i]})";
                    else
                        slot.ThresholdLabel.text = "";
                }
            }
        }

        /// <summary>
        /// Whitelisted item IDs for the manager product selector.
        /// </summary>
        private static readonly HashSet<string> WhitelistedItemIds = new HashSet<string>
        {
            // Mix Ingredients
            "cuke", "donut", "flumedicine", "gasoline", "energydrink", "mouthwash",
            "banana", "chili", "motoroil", "iodine", "paracetamol", "viagor",
            "horsesemen", "megabean", "addy", "battery",
            // Agriculture
            "extralonglifesoil", "fertilizer", "longlifesoil", "pgr", "soil", "speedgrow",
            // Ingredients
            "acid", "phosphorus",
            // Packaging
            "baggie", "jar",
            // Tools
            "trashbag"
        };

        /// <summary>
        /// Returns whitelisted items available for manager stocking.
        /// </summary>
        private static List<Il2CppScheduleOne.ItemFramework.ItemDefinition> GetAvailableItems()
        {
            var result = new List<Il2CppScheduleOne.ItemFramework.ItemDefinition>();

            try
            {
                var registry = Il2CppScheduleOne.Registry.Instance;
                if (registry == null) return result;

                var allItems = registry.GetAllItems();
                if (allItems == null) return result;

                for (int i = 0; i < allItems.Count; i++)
                {
                    var item = allItems[i];
                    if (item != null && !string.IsNullOrEmpty(item.ID) && WhitelistedItemIds.Contains(item.ID))
                        result.Add(item);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get registry items: {ex.Message}");
            }

            return result;
        }

        // ========== Routes Section ==========

        private static void SetupRoutes(RouteListFieldUI routesUI)
        {
            if (routesUI == null) return;

            if (routesUI.FieldLabel != null)
                routesUI.FieldLabel.text = "Routes";

            // Hide the multi-edit blocker
            if (routesUI.MultiEditBlocker != null)
                routesUI.MultiEditBlocker.gameObject.SetActive(false);

            // Keep AddButton — wire it to our handler
            if (routesUI.AddButton != null)
            {
                _addRouteButton = routesUI.AddButton.gameObject;
                RewireButton(routesUI.AddButton, OnAddRouteClicked);
            }

            var entries = routesUI.RouteEntries;
            if (entries == null) return;

            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry == null) continue;

                if (i < 3)
                {
                    // Save refs for refresh (start hidden, RefreshRouteVisibility will show as needed)
                    entry.gameObject.SetActive(false);
                    _routeVisible[i] = false;

                    _routes[i] = new RouteEntryRef
                    {
                        Root = entry.gameObject,
                        SourceLabel = entry.SourceLabel,
                        SourceIcon = entry.SourceIcon,
                        DestLabel = entry.DestinationLabel,
                        DestIcon = entry.DestinationIcon
                    };

                    int routeIdx = i; // capture for closures

                    var srcBtn = FindButtonByPersistentMethod(entry.gameObject, "SourceClicked");
                    if (srcBtn != null)
                        RewireButton(srcBtn, () => OnSlotClicked("From", routeIdx, true));

                    var destBtn = FindButtonByPersistentMethod(entry.gameObject, "DestinationClicked");
                    if (destBtn != null)
                        RewireButton(destBtn, () => OnSlotClicked("To", routeIdx, false));

                    var deleteBtn = FindButtonByPersistentMethod(entry.gameObject, "DeleteClicked");
                    if (deleteBtn != null)
                        RewireButton(deleteBtn, () => OnDeleteRouteClicked(routeIdx));

                    if (entry.FilterIcon != null)
                        entry.FilterIcon.gameObject.SetActive(false);

                    entry.enabled = false;
                    UnityEngine.Object.Destroy(entry);
                }
                else
                {
                    entry.gameObject.SetActive(false);
                }
            }
        }

        // ========== SelectionInfo Fix ==========

        private static void FixSelectionInfo()
        {
            try
            {
                var clipboard = Singleton<ManagementClipboard>.Instance;
                if (clipboard == null) return;

                _selectionInfo = clipboard.SelectionInfo;
                if (_selectionInfo == null) return;

                _originalSelfUpdate = _selectionInfo.SelfUpdate;
                _selectionInfo.SelfUpdate = false;

                if (_selectionInfo.Title != null)
                    _selectionInfo.Title.text = "1x Manager";

                if (_selectionInfo.Icon != null)
                {
                    var icon = GetManagerIcon();
                    if (icon != null)
                    {
                        _selectionInfo.Icon.sprite = icon;
                        _selectionInfo.Icon.gameObject.SetActive(true);
                    }
                    else
                    {
                        _selectionInfo.Icon.gameObject.SetActive(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"FixSelectionInfo error: {ex.Message}");
            }
        }

        // ========== Button Wiring Helpers ==========

        private static Button FindButtonByPersistentMethod(GameObject root, string methodName)
        {
            var buttons = root.GetComponentsInChildren<Button>(true);
            foreach (var btn in buttons)
            {
                try
                {
                    for (int i = 0; i < btn.onClick.GetPersistentEventCount(); i++)
                    {
                        if (btn.onClick.GetPersistentMethodName(i) == methodName)
                            return btn;
                    }
                }
                catch { }
            }
            return null;
        }

        private static void RewireButton(Button btn, Action handler)
        {
            if (btn == null) return;

            try
            {
                for (int i = 0; i < btn.onClick.GetPersistentEventCount(); i++)
                    btn.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
            }
            catch { }

            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(new Action(handler));
        }

        // ========== Selection Logic ==========

        private static void OnSlotClicked(string slotType, int routeIndex, bool isSource)
        {
            if (_currentManager == null) return;

            var objectSelector = Singleton<ManagementInterface>.Instance?.ObjectSelector;
            if (objectSelector == null)
            {
                Logger.Warning("ObjectSelector not available");
                return;
            }

            // Build current selection list
            var currentList = new Il2CppSystem.Collections.Generic.List<BuildableItem>();
            PlaceableStorageEntity current = GetCurrentSlotEntity(slotType, routeIndex, isSource);
            if (current != null)
            {
                var buildable = current.TryCast<BuildableItem>();
                if (buildable != null)
                    currentList.Add(buildable);
            }

            // Type filter — restrict to PlaceableStorageEntity
            var typeReqs = new Il2CppSystem.Collections.Generic.List<Il2CppSystem.Type>();
            typeReqs.Add(Il2CppType.Of<PlaceableStorageEntity>());

            string instruction;
            switch (slotType)
            {
                case "Locker": instruction = "Select Locker"; break;
                case "Supply": instruction = "Select Supplies Container"; break;
                default: instruction = $"Select Route {routeIndex + 1} {slotType}"; break;
            }

            // Capture slot context for the callback
            string capturedSlot = slotType;
            int capturedRoute = routeIndex;
            bool capturedIsSource = isSource;

            _selectorCallback = (Il2CppSystem.Action<Il2CppSystem.Collections.Generic.List<BuildableItem>>)
                new Action<Il2CppSystem.Collections.Generic.List<BuildableItem>>((objs) =>
                {
                    try
                    {
                        PlaceableStorageEntity selected = null;
                        if (objs != null && objs.Count > 0)
                        {
                            var obj = objs[objs.Count - 1];
                            selected = obj?.TryCast<PlaceableStorageEntity>();
                            if (selected == null)
                                selected = obj?.GetComponent<PlaceableStorageEntity>();

                            // Validate same-route source/dest conflict
                            if (selected != null && capturedRoute >= 0 && _currentManager != null)
                            {
                                if (!_currentManager.Configuration.ValidateAssignment(
                                    selected, capturedRoute, capturedIsSource, out string reason))
                                {
                                    Logger.Warning($"Validation failed: {reason}");
                                    selected = null;
                                }
                            }
                        }
                        ApplySelection(capturedSlot, capturedRoute, capturedIsSource, selected);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"ObjectSelector callback error: {ex.Message}");
                    }
                });

            // Pass null for property to allow cross-property container selection
            objectSelector.Open(
                instruction, "", 1, currentList, typeReqs,
                null,
                null,
                _selectorCallback,
                null);
        }

        private static PlaceableStorageEntity GetCurrentSlotEntity(string slotType, int routeIndex, bool isSource)
        {
            if (_currentManager == null) return null;
            var config = _currentManager.Configuration;

            if (slotType == "Locker")
                return config.Locker;

            if (slotType == "Supply")
                return config.SupplyStorage;

            if (routeIndex >= 0 && routeIndex < config.Routes.Length)
                return isSource ? config.Routes[routeIndex].Source : config.Routes[routeIndex].Destination;

            return null;
        }

        private static void ApplySelection(string slotType, int routeIndex, bool isSource, PlaceableStorageEntity selected)
        {
            if (_currentManager == null) return;
            var config = _currentManager.Configuration;

            if (slotType == "Locker")
            {
                config.Locker = selected;

                // Update EmployeeHome display for locker (shows name + wage when opened)
                if (selected != null)
                {
                    var home = selected.GetComponent<EmployeeHome>();
                    if (home == null)
                        home = selected.GetComponentInParent<EmployeeHome>();
                    if (home != null)
                        _currentManager.AssignLocker(home);
                }
                else
                {
                    _currentManager.ClearLocker();
                }
            }
            else if (slotType == "Supply")
            {
                config.SupplyStorage = selected;
            }
            else if (routeIndex >= 0 && routeIndex < config.Routes.Length)
            {
                if (isSource)
                    config.Routes[routeIndex].Source = selected;
                else
                    config.Routes[routeIndex].Destination = selected;
            }

            SyncConfig();

            RefreshLabels();
            Logger.Msg($"Manager {_currentManager.Id}: {slotType}[{routeIndex}] = {GetStorageName(selected)}");
        }

        private static void OnClearClicked(string slotType, int routeIndex, bool isSource)
        {
            ApplySelection(slotType, routeIndex, isSource, null);
        }

        private static void OnAddRouteClicked()
        {
            if (_currentManager == null) return;

            // Find the first hidden route and make it visible
            for (int i = 0; i < 3; i++)
            {
                if (!_routeVisible[i])
                {
                    _routeVisible[i] = true;
                    RefreshRouteVisibility();
                    return;
                }
            }
        }

        private static void OnDeleteRouteClicked(int routeIndex)
        {
            if (_currentManager == null) return;
            if (routeIndex < 0 || routeIndex >= 3) return;

            // Clear both source and destination
            var config = _currentManager.Configuration;
            config.Routes[routeIndex].Source = null;
            config.Routes[routeIndex].Destination = null;

            // Hide the route entry
            _routeVisible[routeIndex] = false;

            SyncConfig();

            RefreshLabels();
            Logger.Msg($"Manager {_currentManager.Id}: deleted route {routeIndex}");
        }

        // ========== Label Refresh ==========

        private static void RefreshLabels()
        {
            if (_currentManager == null) return;
            var config = _currentManager.Configuration;

            // Locker
            RefreshObjectField(config.Locker, _lockerSelectionLabel, _lockerIcon, _lockerNoneSelected, _lockerClearButton);

            // Supply Container
            RefreshObjectField(config.SupplyStorage, _supplySelectionLabel, _supplyIcon, _supplyNoneSelected, _supplyClearButton);

            // Item Slots
            RefreshItemSlots();

            // Routes — determine visibility from data
            for (int i = 0; i < 3; i++)
            {
                if (!config.Routes[i].IsEmpty)
                    _routeVisible[i] = true;
            }

            RefreshRouteVisibility();

            for (int i = 0; i < 3; i++)
            {
                RefreshRouteEntry(i, config.Routes[i].Source, config.Routes[i].Destination);
            }
        }

        private static void RefreshObjectField(PlaceableStorageEntity entity,
            TextMeshProUGUI selectionLabel, Image icon, GameObject noneSelected, RectTransform clearButton)
        {
            string name = GetStorageName(entity);
            bool hasValue = !string.IsNullOrEmpty(name);

            if (selectionLabel != null)
                selectionLabel.text = hasValue ? name : "None";

            if (icon != null)
            {
                if (hasValue && entity != null)
                {
                    var buildable = entity.TryCast<BuildableItem>();
                    icon.sprite = buildable?.ItemInstance?.Icon;
                    icon.gameObject.SetActive(icon.sprite != null);
                }
                else
                {
                    icon.sprite = null;
                    icon.gameObject.SetActive(false);
                }
            }

            if (noneSelected != null)
                noneSelected.SetActive(!hasValue);

            if (clearButton != null)
                clearButton.gameObject.SetActive(hasValue);
        }

        private static void RefreshRouteVisibility()
        {
            int visibleCount = 0;
            for (int i = 0; i < 3; i++)
            {
                if (_routes[i].Root != null)
                    _routes[i].Root.SetActive(_routeVisible[i]);
                if (_routeVisible[i]) visibleCount++;
            }

            if (_addRouteButton != null)
                _addRouteButton.SetActive(visibleCount < 3);
        }

        private static void RefreshRouteEntry(int index, PlaceableStorageEntity source, PlaceableStorageEntity dest)
        {
            if (index < 0 || index >= _routes.Length) return;
            var entry = _routes[index];
            if (entry.Root == null) return;

            // Source
            string srcName = GetStorageName(source);
            if (entry.SourceLabel != null)
                entry.SourceLabel.text = string.IsNullOrEmpty(srcName) ? "None" : srcName;

            if (entry.SourceIcon != null)
            {
                Sprite srcSprite = null;
                if (source != null)
                {
                    var buildable = source.TryCast<BuildableItem>();
                    srcSprite = buildable?.ItemInstance?.Icon;
                }
                entry.SourceIcon.sprite = srcSprite;
                entry.SourceIcon.gameObject.SetActive(srcSprite != null);

                if (entry.SourceLabel != null)
                {
                    var rt = entry.SourceLabel.rectTransform;
                    rt.offsetMin = new Vector2(srcSprite != null ? 29f : 5f, rt.offsetMin.y);
                }
            }

            // Destination
            string dstName = GetStorageName(dest);
            if (entry.DestLabel != null)
                entry.DestLabel.text = string.IsNullOrEmpty(dstName) ? "None" : dstName;

            if (entry.DestIcon != null)
            {
                Sprite dstSprite = null;
                if (dest != null)
                {
                    var buildable = dest.TryCast<BuildableItem>();
                    dstSprite = buildable?.ItemInstance?.Icon;
                }
                entry.DestIcon.sprite = dstSprite;
                entry.DestIcon.gameObject.SetActive(dstSprite != null);

                if (entry.DestLabel != null)
                {
                    var rt = entry.DestLabel.rectTransform;
                    rt.offsetMin = new Vector2(dstSprite != null ? 29f : 5f, rt.offsetMin.y);
                }
            }
        }

        private static Sprite GetManagerIcon()
        {
            if (_managerIcon != null)
                return _managerIcon;

            try
            {
                string iconPath = Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "ManagerIcon.png");
                _managerIcon = ImageUtils.LoadImage(iconPath);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load manager icon: {ex.Message}");
            }

            return _managerIcon;
        }

        private static string GetStorageName(PlaceableStorageEntity entity)
        {
            if (entity == null) return "";
            try
            {
                var buildable = entity.TryCast<BuildableItem>();
                if (buildable != null)
                {
                    string name = buildable.GetManagementName();
                    if (!string.IsNullOrEmpty(name)) return name;
                    if (buildable.ItemInstance != null)
                        return buildable.ItemInstance.Name;
                }
                return entity.gameObject?.name ?? "Storage";
            }
            catch
            {
                return "Storage";
            }
        }
    }
}
