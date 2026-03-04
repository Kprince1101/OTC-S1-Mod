using S1API.UI;
using System;
using System.Collections;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using OverTheCounter.Logic;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.Money;
using Il2CppScheduleOne.UI.Phone.Map;
using Il2CppScheduleOne.Map;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.Money;
using ScheduleOne.UI.Phone.Map;
using ScheduleOne.Map;
#endif

namespace OverTheCounter.Apps
{
    public partial class CustomersApp
    {
        private void ShowManagerDetail(ManagerInstance mgr)
        {
            _detailManager = mgr;

            // Hide the list page (keep header/tabs visible)
            _managersPage.SetActive(false);
            _customersPage.SetActive(false);

            // Create detail overlay below header
            if (_managerDetailPage != null)
                UnityEngine.Object.Destroy(_managerDetailPage);

            _managerDetailPage = UIFactory.Panel("ManagerDetailPage", _rootPanel.transform, new Color(0.12f, 0.12f, 0.12f));
            var detailRect = _managerDetailPage.GetComponent<RectTransform>();
            detailRect.anchorMin = Vector2.zero;
            detailRect.anchorMax = Vector2.one;
            detailRect.offsetMin = Vector2.zero;
            detailRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            BuildManagerDetailContent(_managerDetailPage.transform, mgr);

            // Start per-frame minimap tracking
            StartMinimapTracking();

            // Subscribe to mugshot callback in case it arrives after the page is built
            if (!mgr.IsMugshotReady)
            {
                mgr.OnMugshotReady += OnDetailMugshotReady;
            }
        }

        private void OnDetailMugshotReady()
        {
            try
            {
                var sprite = _detailManager?.GameNpc?.MugshotSprite;
                if (sprite == null) return;

                // Detail page mugshot frame
                if (_detailMugshotImage != null)
                {
                    _detailMugshotImage.sprite = sprite;
                    _detailMugshotImage.color = Color.white;
                }

                // Minimap marker icon inside the white circle
                if (_minimapMarkerIcon != null)
                {
                    _minimapMarkerIcon.sprite = sprite;
                    _minimapMarkerIcon.color = Color.white;
                }
            }
            catch { }
        }

        private void CloseManagerDetail()
        {
            // Unsubscribe mugshot callback
            if (_detailManager != null)
                _detailManager.OnMugshotReady -= OnDetailMugshotReady;

            StopMinimapTracking();

            if (_managerDetailPage != null)
            {
                UnityEngine.Object.Destroy(_managerDetailPage);
                _managerDetailPage = null;
                _detailManager = null;
                _detailMugshotImage = null;
                _detailInvGrid = null;
                _detailStatusText = null;
                _detailCashText = null;
                _detailBankText = null;
                _detailWageText = null;
                _detailSpeedLabel = null;
                _detailSpeedCostLabel = null;
                _detailSpeedBtn = null;
                _detailSpeedBtnText = null;
                _detailInvUpLabel = null;
                _detailInvCostLabel = null;
                _detailInvBtn = null;
                _detailInvBtnText = null;
                _detailSpeedFill = null;
                _detailInvFill = null;
                _speedErrorText = null;
                _invErrorText = null;
                _minimapImageRect = null;
                _minimapMarkerIcon = null;
                _minimapDestRect = null;
                _minimapBgImage = null;
            }

            // Return to managers list
            _managersPage.SetActive(true);
            RefreshManagersPage();
        }

        private void BuildManagerDetailContent(Transform parent, ManagerInstance mgr)
        {
            // ── Back button bar (top strip) ──
            var backBar = UIFactory.Panel("BackBar", parent, new Color(0.15f, 0.15f, 0.15f));
            var backBarRect = backBar.GetComponent<RectTransform>();
            backBarRect.anchorMin = new Vector2(0, 1);
            backBarRect.anchorMax = new Vector2(1, 1);
            backBarRect.pivot = new Vector2(0.5f, 1);
            backBarRect.anchoredPosition = Vector2.zero;
            backBarRect.sizeDelta = new Vector2(0, 32);

            var backBtn = backBar.AddComponent<Button>();
            backBtn.onClick.AddListener(new Action(CloseManagerDetail));

            var backText = UIFactory.Text("BackLabel", "\u25C0  Back", backBar.transform, 16, TextAnchor.MiddleLeft); // ◀ left triangle
            backText.color = new Color(0.7f, 0.7f, 0.7f);
            var backTextRect = backText.gameObject.GetComponent<RectTransform>();
            backTextRect.anchorMin = Vector2.zero;
            backTextRect.anchorMax = Vector2.one;
            backTextRect.offsetMin = new Vector2(12, 0);
            backTextRect.offsetMax = Vector2.zero;

            // ── Content area below back bar ──
            var contentArea = UIFactory.Panel("ContentArea", parent, Color.clear);
            var contentRect = contentArea.GetComponent<RectTransform>();
            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = Vector2.one;
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = new Vector2(0, -32);

            // ── Left side: Manager info ──
            string firstName = "Manager";
            string lastName = "";
            try
            {
                if (mgr.GameNpc != null)
                {
                    firstName = mgr.GameNpc.FirstName ?? "Manager";
                    lastName = mgr.GameNpc.LastName ?? "";
                }
            }
            catch { }

            // Mugshot (top-left of content)
            var mugFrame = UIFactory.Panel("MugFrame", contentArea.transform, new Color(0.45f, 0.50f, 0.52f));
            var mugFrameRect = mugFrame.GetComponent<RectTransform>();
            mugFrameRect.anchorMin = new Vector2(0, 1);
            mugFrameRect.anchorMax = new Vector2(0, 1);
            mugFrameRect.pivot = new Vector2(0, 1);
            mugFrameRect.anchoredPosition = new Vector2(16, -12);
            mugFrameRect.sizeDelta = new Vector2(90, 90);

            var mugPanel = UIFactory.Panel("Mugshot", mugFrame.transform, new Color(0.15f, 0.15f, 0.15f));
            var mugRect = mugPanel.GetComponent<RectTransform>();
            mugRect.anchorMin = Vector2.zero;
            mugRect.anchorMax = Vector2.one;
            mugRect.offsetMin = new Vector2(2, 2);
            mugRect.offsetMax = new Vector2(-2, -2);

            _detailMugshotImage = mugPanel.GetComponent<Image>();
            try
            {
                var mugSprite = mgr.GameNpc?.MugshotSprite;
                if (mugSprite != null)
                {
                    _detailMugshotImage.sprite = mugSprite;
                    _detailMugshotImage.color = Color.white;
                }
            }
            catch { }

            // Name (right of mugshot)
            var nameText = UIFactory.Text("Name", $"<b>{firstName} {lastName}</b>", contentArea.transform, 20, TextAnchor.MiddleLeft);
            nameText.color = Color.white;
            var nameRect = nameText.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 1);
            nameRect.anchorMax = new Vector2(0.5f, 1);
            nameRect.pivot = new Vector2(0, 1);
            nameRect.anchoredPosition = new Vector2(120, -14);
            nameRect.sizeDelta = new Vector2(0, 28);

            // Business
            string bizName = mgr.AssignedBusiness?.PropertyName ?? mgr.BusinessPropertyCode ?? "Unassigned";
            var bizText = UIFactory.Text("Business", bizName, contentArea.transform, 14, TextAnchor.MiddleLeft);
            bizText.color = new Color(0.55f, 0.55f, 0.55f);
            var bizRect = bizText.gameObject.GetComponent<RectTransform>();
            bizRect.anchorMin = new Vector2(0, 1);
            bizRect.anchorMax = new Vector2(0.5f, 1);
            bizRect.pivot = new Vector2(0, 1);
            bizRect.anchoredPosition = new Vector2(120, -44);
            bizRect.sizeDelta = new Vector2(0, 22);

            // Balance
            if (mgr.HasLocker)
            {
                float cash = mgr.GetLockerCash();
                var cashText = UIFactory.Text("Balance", $"Locker Balance: <color=#66BF4D>${cash:N0}</color>", contentArea.transform, 14, TextAnchor.MiddleLeft);
                cashText.color = new Color(0.7f, 0.7f, 0.7f);
                cashText.supportRichText = true;
                var cashRect = cashText.gameObject.GetComponent<RectTransform>();
                cashRect.anchorMin = new Vector2(0, 1);
                cashRect.anchorMax = new Vector2(0.5f, 1);
                cashRect.pivot = new Vector2(0, 1);
                cashRect.anchoredPosition = new Vector2(120, -66);
                cashRect.sizeDelta = new Vector2(0, 22);
                _detailCashText = cashText;
            }

            // Status
            var (statusStr, statusColor) = GetStatusDisplay(mgr);
            var statusText = UIFactory.Text("Status", statusStr, contentArea.transform, 14, TextAnchor.MiddleLeft);
            statusText.color = statusColor;
            _detailStatusText = statusText;
            var statusRect = statusText.gameObject.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0, 1);
            statusRect.anchorMax = new Vector2(0.5f, 1);
            statusRect.pivot = new Vector2(0, 1);
            statusRect.anchoredPosition = new Vector2(120, -88);
            statusRect.sizeDelta = new Vector2(0, 22);

            // ── Inventory section ──
            var invLabel = UIFactory.Text("InvLabel", "<b>Inventory</b>", contentArea.transform, 14, TextAnchor.MiddleLeft);
            invLabel.color = new Color(0.7f, 0.7f, 0.7f);
            var invLabelRect = invLabel.gameObject.GetComponent<RectTransform>();
            invLabelRect.anchorMin = new Vector2(0, 1);
            invLabelRect.anchorMax = new Vector2(0.5f, 1);
            invLabelRect.pivot = new Vector2(0, 1);
            invLabelRect.anchoredPosition = new Vector2(16, -118);
            invLabelRect.sizeDelta = new Vector2(0, 22);

            // Inventory grid
            ScheduleOne.NPCs.NPCInventory npcInventory = null;
            try { npcInventory = mgr.GameNpc?.GetComponent<ScheduleOne.NPCs.NPCInventory>(); }
            catch { }

            int displaySlots = 5;
            if (npcInventory?.ItemSlots != null)
                displaySlots = Math.Max(5, npcInventory.ItemSlots.Count);

            var invPanel = UIFactory.Panel("InvGrid", contentArea.transform, Color.clear);
            _detailInvGrid = invPanel;
            int invRows = Math.Max(1, (displaySlots + SLOTS_PER_ROW - 1) / SLOTS_PER_ROW);
            float invGridHeight = invRows * SLOT_SIZE + Math.Max(0, invRows - 1) * SLOT_GAP;
            var invRect = invPanel.GetComponent<RectTransform>();
            invRect.anchorMin = new Vector2(0, 1);
            invRect.anchorMax = new Vector2(0.5f, 1);
            invRect.pivot = new Vector2(0, 1);
            invRect.anchoredPosition = new Vector2(16, -142);
            invRect.sizeDelta = new Vector2(0, invGridHeight);

            var grid = invPanel.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(SLOT_SIZE, SLOT_SIZE);
            grid.spacing = new Vector2(SLOT_GAP, SLOT_GAP);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = SLOTS_PER_ROW;
            grid.childAlignment = TextAnchor.UpperLeft;

            for (int i = 0; i < displaySlots; i++)
            {
                var slotPanel = UIFactory.Panel($"Slot_{i}", invPanel.transform, SlotBg);

                Sprite icon = null;
                string displayQty = null;
                try
                {
                    if (npcInventory?.ItemSlots != null && i < npcInventory.ItemSlots.Count)
                    {
                        var slot = npcInventory.ItemSlots[i];
                        if (slot?.ItemInstance?.Definition != null)
                        {
                            icon = slot.ItemInstance.Icon;
                            var cash = slot.ItemInstance.TryCast<ScheduleOne.ItemFramework.CashInstance>();
                            displayQty = cash != null ? $"${cash.Balance:N0}" : slot.Quantity.ToString();
                        }
                    }
                }
                catch { }

                if (icon != null)
                {
                    var iconObj = new GameObject("Icon");
                    iconObj.transform.SetParent(slotPanel.transform, false);
                    var iconImg = iconObj.AddComponent<Image>();
                    iconImg.sprite = icon;
                    iconImg.preserveAspect = true;
                    var iconRect = iconObj.GetComponent<RectTransform>();
                    iconRect.anchorMin = new Vector2(0.06f, 0.06f);
                    iconRect.anchorMax = new Vector2(0.94f, 0.94f);
                    iconRect.offsetMin = Vector2.zero;
                    iconRect.offsetMax = Vector2.zero;

                    if (displayQty != null)
                    {
                        var qtyText = UIFactory.Text($"Qty_{i}", displayQty, slotPanel.transform, 13, TextAnchor.LowerRight);
                        qtyText.color = Color.white;
                        var qtyRect = qtyText.gameObject.GetComponent<RectTransform>();
                        qtyRect.anchorMin = Vector2.zero;
                        qtyRect.anchorMax = Vector2.one;
                        qtyRect.offsetMin = new Vector2(2, 1);
                        qtyRect.offsetMax = new Vector2(-3, 0);

                        var shadow = qtyText.gameObject.AddComponent<Shadow>();
                        shadow.effectColor = new Color(0, 0, 0, 0.85f);
                        shadow.effectDistance = new Vector2(1, -1);
                    }
                }
            }

            // ── Upgrade section ──
            float upgradeTopY = -142 - invGridHeight - 16;
            BuildUpgradeSection(contentArea.transform, mgr, upgradeTopY);

            // ── Debug log button ──
            BuildDebugLogButton(contentArea.transform, mgr);

            // ── Right side: Minimap ──
            BuildMinimap(contentArea.transform, mgr);
        }

        private void BuildMinimap(Transform contentArea, ManagerInstance mgr)
        {
            // Map container (right half of content area)
            var mapContainer = UIFactory.Panel("MapContainer", contentArea, new Color(0.20f, 0.259f, 0.298f));
            var mapContainerRect = mapContainer.GetComponent<RectTransform>();
            mapContainerRect.anchorMin = new Vector2(0.5f, 0);
            mapContainerRect.anchorMax = Vector2.one;
            mapContainerRect.offsetMin = new Vector2(8, 8);
            mapContainerRect.offsetMax = new Vector2(-8, -8);

            // Try to get the map sprite from the game's MapApp
            Sprite mapSprite = null;
            try
            {
                var mapApp = PlayerSingleton<MapApp>.Instance;
                if (mapApp != null)
                    mapSprite = mapApp.MainMapSprite;
            }
            catch { }

            if (mapSprite == null)
            {
                var noMapText = UIFactory.Text("NoMap", "Map unavailable", mapContainer.transform, 14, TextAnchor.MiddleCenter);
                noMapText.color = new Color(0.4f, 0.4f, 0.4f);
                var noMapRect = noMapText.gameObject.GetComponent<RectTransform>();
                noMapRect.anchorMin = Vector2.zero;
                noMapRect.anchorMax = Vector2.one;
                noMapRect.offsetMin = Vector2.zero;
                noMapRect.offsetMax = Vector2.zero;
                return;
            }

            // Clip mask so the map doesn't overflow the container
            mapContainer.AddComponent<RectMask2D>();

            _minimapBgImage = mapContainer.GetComponent<Image>();

            // Map image (oversized, positioned to center on manager)
            var mapObj = new GameObject("MapImage");
            mapObj.transform.SetParent(mapContainer.transform, false);
            var mapImage = mapObj.AddComponent<Image>();
            mapImage.sprite = mapSprite;
            mapImage.preserveAspect = true;

            _minimapImageRect = mapObj.GetComponent<RectTransform>();
            _minimapImageRect.anchorMin = new Vector2(0.5f, 0.5f);
            _minimapImageRect.anchorMax = new Vector2(0.5f, 0.5f);
            _minimapImageRect.pivot = new Vector2(0.5f, 0.5f);
            _minimapDisplaySize = 1200f;
            _minimapImageRect.sizeDelta = new Vector2(_minimapDisplaySize, _minimapDisplaySize);

            // Cache the content rect dimensions for coordinate conversion
            _minimapContentW = mapSprite.rect.width;
            _minimapContentH = mapSprite.rect.height;
            try
            {
                var mapApp = PlayerSingleton<MapApp>.Instance;
                if (mapApp?.ContentRect != null)
                {
                    _minimapContentW = mapApp.ContentRect.rect.width;
                    _minimapContentH = mapApp.ContentRect.rect.height;
                }
            }
            catch { }

            // Manager position marker — vanilla structure: Outline (white circle) + Icon (mugshot inside)
            float markerSize = 32f;
            var markerObj = new GameObject("Marker");
            markerObj.transform.SetParent(mapContainer.transform, false);
            var markerImg = markerObj.AddComponent<Image>();
            markerImg.color = Color.white;
            var markerRect = markerObj.GetComponent<RectTransform>();
            markerRect.anchorMin = new Vector2(0.5f, 0.5f);
            markerRect.anchorMax = new Vector2(0.5f, 0.5f);
            markerRect.pivot = new Vector2(0.5f, 0.5f);
            markerRect.sizeDelta = new Vector2(markerSize, markerSize);
            markerRect.anchoredPosition = Vector2.zero;

            // Grab the Outline sprite from the manager's existing map POI (the white circle border)
            try
            {
                var poiBase = (ScheduleOne.Map.POI)mgr.MapPoI;
                var outlineTransform = poiBase?.IconContainer?.Find("Outline");
                var outlineImg = outlineTransform?.GetComponent<Image>();
                if (outlineImg?.sprite != null)
                    markerImg.sprite = outlineImg.sprite;
            }
            catch { }

            // Mugshot icon inside the circle (child, inset slightly like vanilla Outline/Icon)
            var markerIconObj = new GameObject("MarkerIcon");
            markerIconObj.transform.SetParent(markerObj.transform, false);
            _minimapMarkerIcon = markerIconObj.AddComponent<Image>();
            _minimapMarkerIcon.preserveAspect = true;
            _minimapMarkerIcon.color = Color.clear; // hidden until mugshot is ready
            var markerIconRect = markerIconObj.GetComponent<RectTransform>();
            markerIconRect.anchorMin = new Vector2(0.1f, 0.1f);
            markerIconRect.anchorMax = new Vector2(0.9f, 0.9f);
            markerIconRect.offsetMin = Vector2.zero;
            markerIconRect.offsetMax = Vector2.zero;

            // Apply mugshot if already available
            try
            {
                var mugSprite = mgr.GameNpc?.MugshotSprite;
                if (mugSprite != null)
                {
                    _minimapMarkerIcon.sprite = mugSprite;
                    _minimapMarkerIcon.color = Color.white;
                }
            }
            catch { }

            // Destination marker — red semi-transparent square on the map image
            var destObj = new GameObject("DestMarker");
            destObj.transform.SetParent(_minimapImageRect.transform, false);
            var destImg = destObj.AddComponent<Image>();
            destImg.color = new Color(1f, 0f, 0f, 0.45f);
            _minimapDestRect = destObj.GetComponent<RectTransform>();
            _minimapDestRect.anchorMin = new Vector2(0.5f, 0.5f);
            _minimapDestRect.anchorMax = new Vector2(0.5f, 0.5f);
            _minimapDestRect.pivot = new Vector2(0.5f, 0.5f);
            _minimapDestRect.sizeDelta = new Vector2(8f, 8f);
            _minimapDestRect.gameObject.SetActive(false);

            // Set initial position
            UpdateMinimapPosition();
        }

        private void UpdateMinimapPosition()
        {
            if (_minimapImageRect == null) return;

            Vector3? worldPosNullable = null;
            if (_detailEmployee != null)
                worldPosNullable = _detailEmployee.transform.position;
            else if (_selectedCustomer?.NPC != null)
                worldPosNullable = _selectedCustomer.NPC.transform.position;
            else if (_detailManager?.GameNpc != null)
                worldPosNullable = _detailManager.GameNpc.transform.position;

            if (worldPosNullable == null) return;

            try
            {
                var mapUtil = Singleton<MapPositionUtility>.Instance;
                if (mapUtil == null) return;

                Vector3 worldPos = worldPosNullable.Value;
                Vector2 mapPos = mapUtil.GetMapPosition(worldPos);

                float scaleX = _minimapDisplaySize / _minimapContentW;
                float scaleY = _minimapDisplaySize / _minimapContentH;

                float rawX = -mapPos.x * scaleX;
                float rawY = -mapPos.y * scaleY;

                _minimapImageRect.anchoredPosition = new Vector2(rawX, rawY);

                // Background: blue (#33424C) when left/ocean edge visible, green (#475B3D) when right/land edge visible
                if (_minimapBgImage != null)
                {
                    _minimapBgImage.color = rawX < 0
                        ? new Color(0.278f, 0.357f, 0.239f) // green #475B3D (right edge showing)
                        : new Color(0.20f, 0.259f, 0.298f); // blue  #33424C (left edge showing)
                }

                // Update destination marker — employees and customers have no destination marker
                if (_minimapDestRect != null)
                {
                    if (_detailEmployee != null || _selectedCustomer != null)
                    {
                        _minimapDestRect.gameObject.SetActive(false);
                    }
                    else
                    {
                        var movement = _detailManager?.GameNpc?.Movement;
                        if (movement != null && movement.HasDestination)
                        {
                            Vector2 destMapPos = mapUtil.GetMapPosition(movement.CurrentDestination);
                            _minimapDestRect.anchoredPosition = new Vector2(destMapPos.x * scaleX, destMapPos.y * scaleY);
                            _minimapDestRect.gameObject.SetActive(true);
                        }
                        else
                        {
                            _minimapDestRect.gameObject.SetActive(false);
                        }
                    }
                }
            }
            catch { }
        }

        private void RefreshDetailInventory()
        {
            if (_detailManager == null) return;

            // Update status
            if (_detailStatusText != null)
            {
                var (statusStr, statusColor) = GetStatusDisplay(_detailManager);
                _detailStatusText.text = statusStr;
                _detailStatusText.color = statusColor;
            }

            // Refresh upgrade labels (bank, wage, tier progress — picks up SyncVar changes on client)
            RefreshUpgradeLabels(_detailManager);

            // Update cash
            if (_detailCashText != null && _detailManager.HasLocker)
            {
                float cash = _detailManager.GetLockerCash();
                _detailCashText.text = $"Locker Balance: <color=#66BF4D>${cash:N0}</color>";
            }

            // Rebuild inventory slots
            if (_detailInvGrid != null)
            {
                ScheduleOne.NPCs.NPCInventory npcInventory = null;
                try { npcInventory = _detailManager.GameNpc?.GetComponent<ScheduleOne.NPCs.NPCInventory>(); }
                catch { }

                int displaySlots = 5;
                if (npcInventory?.ItemSlots != null)
                    displaySlots = Math.Max(5, npcInventory.ItemSlots.Count);

                // Slot count changed (inventory upgrade) — full page rebuild needed for layout
                if (displaySlots != _detailInvGrid.transform.childCount)
                {
                    var mgr = _detailManager;
                    ShowManagerDetail(mgr);
                    return;
                }

                ClearChildren(_detailInvGrid.transform);

                for (int i = 0; i < displaySlots; i++)
                {
                    var slotPanel = UIFactory.Panel($"Slot_{i}", _detailInvGrid.transform, SlotBg);

                    Sprite icon = null;
                    string displayQty = null;
                    try
                    {
                        if (npcInventory?.ItemSlots != null && i < npcInventory.ItemSlots.Count)
                        {
                            var slot = npcInventory.ItemSlots[i];
                            if (slot?.ItemInstance?.Definition != null)
                            {
                                icon = slot.ItemInstance.Icon;
                                var cash = slot.ItemInstance.TryCast<ScheduleOne.ItemFramework.CashInstance>();
                                displayQty = cash != null ? $"${cash.Balance:N0}" : slot.Quantity.ToString();
                            }
                        }
                    }
                    catch { }

                    if (icon != null)
                    {
                        var iconObj = new GameObject("Icon");
                        iconObj.transform.SetParent(slotPanel.transform, false);
                        var iconImg = iconObj.AddComponent<Image>();
                        iconImg.sprite = icon;
                        iconImg.preserveAspect = true;
                        var iconRect = iconObj.GetComponent<RectTransform>();
                        iconRect.anchorMin = new Vector2(0.06f, 0.06f);
                        iconRect.anchorMax = new Vector2(0.94f, 0.94f);
                        iconRect.offsetMin = Vector2.zero;
                        iconRect.offsetMax = Vector2.zero;

                        if (displayQty != null)
                        {
                            var qtyText = UIFactory.Text($"Qty_{i}", displayQty, slotPanel.transform, 13, TextAnchor.LowerRight);
                            qtyText.color = Color.white;
                            var qtyRect = qtyText.gameObject.GetComponent<RectTransform>();
                            qtyRect.anchorMin = Vector2.zero;
                            qtyRect.anchorMax = Vector2.one;
                            qtyRect.offsetMin = new Vector2(2, 1);
                            qtyRect.offsetMax = new Vector2(-3, 0);

                            var shadow = qtyText.gameObject.AddComponent<Shadow>();
                            shadow.effectColor = new Color(0, 0, 0, 0.85f);
                            shadow.effectDistance = new Vector2(1, -1);
                        }
                    }
                }
            }
        }

        private void BuildUpgradeSection(Transform content, ManagerInstance mgr, float topY)
        {
            // Bank balance + daily wage row
            float bankBalance = 0f;
            try { bankBalance = NetworkSingleton<MoneyManager>.Instance?.onlineBalance ?? 0f; } catch { }
            float dailyWage = mgr.GetDailyWage();

            _detailBankText = UIFactory.Text("BankText", $"Bank Balance: <color=#66BF4D>${bankBalance:N0}</color>", content, 13, TextAnchor.MiddleLeft);
            _detailBankText.supportRichText = true;
            _detailBankText.color = new Color(0.6f, 0.6f, 0.6f);
            var bankRect = _detailBankText.gameObject.GetComponent<RectTransform>();
            bankRect.anchorMin = new Vector2(0, 1);
            bankRect.anchorMax = new Vector2(0.25f, 1);
            bankRect.pivot = new Vector2(0, 1);
            bankRect.anchoredPosition = new Vector2(16, topY);
            bankRect.sizeDelta = new Vector2(0, 20);

            _detailWageText = UIFactory.Text("WageText", $"Wage: <color=#F5A623>${dailyWage:F0}/day</color>", content, 13, TextAnchor.MiddleRight);
            _detailWageText.supportRichText = true;
            _detailWageText.color = new Color(0.6f, 0.6f, 0.6f);
            var wageRect = _detailWageText.gameObject.GetComponent<RectTransform>();
            wageRect.anchorMin = new Vector2(0.25f, 1);
            wageRect.anchorMax = new Vector2(0.5f, 1);
            wageRect.pivot = new Vector2(1, 1);
            wageRect.anchoredPosition = new Vector2(-16, topY);
            wageRect.sizeDelta = new Vector2(0, 20);

            float cardY = topY - 26;
            float cardHeight = 195;
            Color cardBg = new Color(0.18f, 0.18f, 0.18f);
            Color barBgColor = new Color(0.08f, 0.08f, 0.08f);
            Color barFillColor = new Color(0.15f, 0.75f, 0.70f);
            bool isHost = NetworkHelper.IsHost;

            // ── Left card: Manager Agility ──
            var speedCard = UIFactory.Panel("SpeedCard", content, cardBg);
            var speedCardRect = speedCard.GetComponent<RectTransform>();
            speedCardRect.anchorMin = new Vector2(0, 1);
            speedCardRect.anchorMax = new Vector2(0.232f, 1);
            speedCardRect.pivot = new Vector2(0, 1);
            speedCardRect.anchoredPosition = new Vector2(16, cardY);
            speedCardRect.sizeDelta = new Vector2(0, cardHeight);

            // Shadow to elevate speed card
            var speedShadow = speedCard.AddComponent<Shadow>();
            speedShadow.effectColor = new Color(0, 0, 0, 0.6f);
            speedShadow.effectDistance = new Vector2(3, -3);

            var speedTitle = UIFactory.Text("SpeedTitle", "<b>WALK SPEED</b>", speedCard.transform, 18, TextAnchor.MiddleCenter);
            speedTitle.supportRichText = true;
            speedTitle.color = new Color(0.9f, 0.9f, 0.9f);
            var speedTitleRect = speedTitle.gameObject.GetComponent<RectTransform>();
            speedTitleRect.anchorMin = new Vector2(0, 1);
            speedTitleRect.anchorMax = new Vector2(1, 1);
            speedTitleRect.pivot = new Vector2(0.5f, 1);
            speedTitleRect.anchoredPosition = new Vector2(0, -8);
            speedTitleRect.sizeDelta = new Vector2(0, 26);

            bool speedMaxed = ManagerUpgrades.IsSpeedMaxed(mgr.Configuration.SpeedTier);

            _detailSpeedLabel = UIFactory.Text("SpeedTier", $"Tier {mgr.Configuration.SpeedTier}/{ManagerUpgrades.MaxSpeedTier}", speedCard.transform, 14, TextAnchor.MiddleLeft);
            _detailSpeedLabel.color = new Color(0.6f, 0.6f, 0.6f);
            var speedTierRect = _detailSpeedLabel.gameObject.GetComponent<RectTransform>();
            speedTierRect.anchorMin = new Vector2(0, 1);
            speedTierRect.anchorMax = new Vector2(1, 1);
            speedTierRect.pivot = new Vector2(0, 1);
            speedTierRect.anchoredPosition = new Vector2(12, -34);
            speedTierRect.sizeDelta = new Vector2(-24, 20);

            // Progress bar
            var speedBarBg = UIFactory.Panel("SpeedBarBg", speedCard.transform, barBgColor);
            var speedBarBgRect = speedBarBg.GetComponent<RectTransform>();
            speedBarBgRect.anchorMin = new Vector2(0, 1);
            speedBarBgRect.anchorMax = new Vector2(1, 1);
            speedBarBgRect.pivot = new Vector2(0.5f, 1);
            speedBarBgRect.anchoredPosition = new Vector2(0, -56);
            speedBarBgRect.sizeDelta = new Vector2(-24, 18);

            float speedFillPct = (float)mgr.Configuration.SpeedTier / ManagerUpgrades.MaxSpeedTier;
            var speedBarFill = UIFactory.Panel("SpeedBarFill", speedBarBg.transform, barFillColor);
            _detailSpeedFill = speedBarFill.GetComponent<RectTransform>();
            _detailSpeedFill.anchorMin = Vector2.zero;
            _detailSpeedFill.anchorMax = new Vector2(speedFillPct, 1f);
            _detailSpeedFill.offsetMin = Vector2.zero;
            _detailSpeedFill.offsetMax = Vector2.zero;

            // Speed upgrade button
            string speedBtnStr = speedMaxed ? "MAXED" : $"UPGRADE: ${ManagerUpgrades.GetNextSpeedBuyIn(mgr.Configuration.SpeedTier):N0}";
            var (speedMask, speedBtnComp, speedBtnLabel) = UIFactory.RoundedButtonWithLabel(
                "SpeedUpgradeBtn", speedBtnStr, speedCard.transform,
                speedMaxed ? new Color(0.25f, 0.25f, 0.25f) : new Color(0.20f, 0.45f, 0.20f),
                130, 30, 4, speedMaxed ? new Color(0.4f, 0.4f, 0.4f) : Color.white);
            var speedBtnRect = speedMask.GetComponent<RectTransform>();
            speedBtnRect.anchorMin = new Vector2(0.5f, 1);
            speedBtnRect.anchorMax = new Vector2(0.5f, 1);
            speedBtnRect.pivot = new Vector2(0.5f, 1);
            speedBtnRect.anchoredPosition = new Vector2(0, -134);
            speedBtnLabel.fontSize = 12;
            speedBtnLabel.alignment = TextAnchor.MiddleCenter;
            _detailSpeedBtn = speedBtnComp;
            _detailSpeedBtnText = speedBtnLabel;

            // Insufficient funds error (hidden until triggered)
            _speedErrorText = UIFactory.Text("SpeedError", "Insufficient funds", speedCard.transform, 12, TextAnchor.MiddleCenter);
            _speedErrorText.color = new Color(0.9f, 0.25f, 0.25f, 0f);
            var speedErrRect = _speedErrorText.gameObject.GetComponent<RectTransform>();
            speedErrRect.anchorMin = new Vector2(0, 1);
            speedErrRect.anchorMax = new Vector2(1, 1);
            speedErrRect.pivot = new Vector2(0.5f, 1);
            speedErrRect.anchoredPosition = new Vector2(0, -112);
            speedErrRect.sizeDelta = new Vector2(0, 16);

            string speedFooterStr = speedMaxed ? "" : $"+${ManagerUpgrades.GetNextSpeedDailyFee(mgr.Configuration.SpeedTier):F0} Daily Maintenance";
            _detailSpeedCostLabel = UIFactory.Text("SpeedFooter", speedFooterStr, speedCard.transform, 15, TextAnchor.MiddleCenter);
            _detailSpeedCostLabel.color = new Color(0.5f, 0.5f, 0.5f);
            var speedFooterRect = _detailSpeedCostLabel.gameObject.GetComponent<RectTransform>();
            speedFooterRect.anchorMin = new Vector2(0, 1);
            speedFooterRect.anchorMax = new Vector2(1, 1);
            speedFooterRect.pivot = new Vector2(0.5f, 1);
            speedFooterRect.anchoredPosition = new Vector2(0, -168);
            speedFooterRect.sizeDelta = new Vector2(0, 20);

            if (!speedMaxed)
            {
                var mgrRef = mgr;
                speedBtnComp.onClick.AddListener(new Action(() =>
                {
                    if (isHost)
                    {
                        if (mgrRef.TryPurchaseSpeedUpgrade())
                        {
                            RefreshUpgradeLabels(mgrRef);
                            mgrRef.AssignLocker(mgrRef.AssignedLocker);
                            MelonCoroutines.Start(RefreshBankNextFrame(mgrRef));
                        }
                        else
                        {
                            FlashError(_speedErrorText);
                        }
                    }
                    else
                    {
                        float cost = ManagerUpgrades.GetNextSpeedBuyIn(mgrRef.Configuration.SpeedTier);
                        var mm = NetworkSingleton<MoneyManager>.Instance;
                        if (mm != null && mm.onlineBalance < cost)
                        {
                            FlashError(_speedErrorText);
                        }
                        else
                        {
                            mgrRef.Configuration.SpeedTier++;
                            RefreshUpgradeLabels(mgrRef);
                            ConfigSyncData.SendQuestAction($"MANAGER_UPGRADE_SPEED:{mgrRef.Id}");
                        }
                    }
                }));
            }
            else
            {
                speedBtnComp.interactable = false;
            }

            // ── Right card: Carry Capacity ──
            var invCard = UIFactory.Panel("InvCard", content, cardBg);
            var invCardRect = invCard.GetComponent<RectTransform>();
            invCardRect.anchorMin = new Vector2(0.270f, 1);
            invCardRect.anchorMax = new Vector2(0.5f, 1);
            invCardRect.pivot = new Vector2(0, 1);
            invCardRect.anchoredPosition = new Vector2(0, cardY);
            invCardRect.sizeDelta = new Vector2(0, cardHeight);

            // Shadow to elevate inv card
            var invShadow = invCard.AddComponent<Shadow>();
            invShadow.effectColor = new Color(0, 0, 0, 0.6f);
            invShadow.effectDistance = new Vector2(3, -3);

            var invTitle = UIFactory.Text("InvTitle", "<b>CARRY CAPACITY</b>", invCard.transform, 18, TextAnchor.MiddleCenter);
            invTitle.supportRichText = true;
            invTitle.color = new Color(0.9f, 0.9f, 0.9f);
            var invTitleRect = invTitle.gameObject.GetComponent<RectTransform>();
            invTitleRect.anchorMin = new Vector2(0, 1);
            invTitleRect.anchorMax = new Vector2(1, 1);
            invTitleRect.pivot = new Vector2(0.5f, 1);
            invTitleRect.anchoredPosition = new Vector2(0, -8);
            invTitleRect.sizeDelta = new Vector2(0, 26);

            bool invMaxed = ManagerUpgrades.IsInventoryMaxed(mgr.Configuration.ExtraInventorySlots);
            int currentSlots = ManagerUpgrades.GetTotalSlots(mgr.Configuration.ExtraInventorySlots);
            int maxSlots = ManagerUpgrades.GetTotalSlots(ManagerUpgrades.MaxExtraSlots);

            _detailInvUpLabel = UIFactory.Text("InvSlots", $"{currentSlots}/{maxSlots} Slots", invCard.transform, 14, TextAnchor.MiddleLeft);
            _detailInvUpLabel.color = new Color(0.6f, 0.6f, 0.6f);
            var invSlotsRect = _detailInvUpLabel.gameObject.GetComponent<RectTransform>();
            invSlotsRect.anchorMin = new Vector2(0, 1);
            invSlotsRect.anchorMax = new Vector2(1, 1);
            invSlotsRect.pivot = new Vector2(0, 1);
            invSlotsRect.anchoredPosition = new Vector2(12, -34);
            invSlotsRect.sizeDelta = new Vector2(-24, 20);

            // Progress bar
            var invBarBg = UIFactory.Panel("InvBarBg", invCard.transform, barBgColor);
            var invBarBgRect = invBarBg.GetComponent<RectTransform>();
            invBarBgRect.anchorMin = new Vector2(0, 1);
            invBarBgRect.anchorMax = new Vector2(1, 1);
            invBarBgRect.pivot = new Vector2(0.5f, 1);
            invBarBgRect.anchoredPosition = new Vector2(0, -56);
            invBarBgRect.sizeDelta = new Vector2(-24, 18);

            float invFillPct = ManagerUpgrades.MaxExtraSlots > 0
                ? (float)mgr.Configuration.ExtraInventorySlots / ManagerUpgrades.MaxExtraSlots : 0f;
            var invBarFill = UIFactory.Panel("InvBarFill", invBarBg.transform, barFillColor);
            _detailInvFill = invBarFill.GetComponent<RectTransform>();
            _detailInvFill.anchorMin = Vector2.zero;
            _detailInvFill.anchorMax = new Vector2(invFillPct, 1f);
            _detailInvFill.offsetMin = Vector2.zero;
            _detailInvFill.offsetMax = Vector2.zero;

            // Inventory upgrade button
            string invBtnStr = invMaxed ? "MAXED" : $"UPGRADE: ${ManagerUpgrades.GetNextSlotBuyIn(mgr.Configuration.ExtraInventorySlots):N0}";
            var (invMask, invBtnComp, invBtnLabel) = UIFactory.RoundedButtonWithLabel(
                "InvUpgradeBtn", invBtnStr, invCard.transform,
                invMaxed ? new Color(0.25f, 0.25f, 0.25f) : new Color(0.20f, 0.45f, 0.20f),
                130, 30, 4, invMaxed ? new Color(0.4f, 0.4f, 0.4f) : Color.white);
            var invBtnRect = invMask.GetComponent<RectTransform>();
            invBtnRect.anchorMin = new Vector2(0.5f, 1);
            invBtnRect.anchorMax = new Vector2(0.5f, 1);
            invBtnRect.pivot = new Vector2(0.5f, 1);
            invBtnRect.anchoredPosition = new Vector2(0, -134);
            invBtnLabel.fontSize = 12;
            invBtnLabel.alignment = TextAnchor.MiddleCenter;
            _detailInvBtn = invBtnComp;
            _detailInvBtnText = invBtnLabel;

            // Insufficient funds error (hidden until triggered)
            _invErrorText = UIFactory.Text("InvError", "Insufficient funds", invCard.transform, 12, TextAnchor.MiddleCenter);
            _invErrorText.color = new Color(0.9f, 0.25f, 0.25f, 0f);
            var invErrRect = _invErrorText.gameObject.GetComponent<RectTransform>();
            invErrRect.anchorMin = new Vector2(0, 1);
            invErrRect.anchorMax = new Vector2(1, 1);
            invErrRect.pivot = new Vector2(0.5f, 1);
            invErrRect.anchoredPosition = new Vector2(0, -112);
            invErrRect.sizeDelta = new Vector2(0, 16);

            string invFooterStr = invMaxed ? "" : $"+${ManagerUpgrades.SlotDailyFee:F0} Daily Maintenance";
            _detailInvCostLabel = UIFactory.Text("InvFooter", invFooterStr, invCard.transform, 15, TextAnchor.MiddleCenter);
            _detailInvCostLabel.color = new Color(0.5f, 0.5f, 0.5f);
            var invFooterRect = _detailInvCostLabel.gameObject.GetComponent<RectTransform>();
            invFooterRect.anchorMin = new Vector2(0, 1);
            invFooterRect.anchorMax = new Vector2(1, 1);
            invFooterRect.pivot = new Vector2(0.5f, 1);
            invFooterRect.anchoredPosition = new Vector2(0, -168);
            invFooterRect.sizeDelta = new Vector2(0, 20);

            if (!invMaxed)
            {
                var mgrRef = mgr;
                invBtnComp.onClick.AddListener(new Action(() =>
                {
                    if (isHost)
                    {
                        if (mgrRef.TryPurchaseInventoryUpgrade())
                        {
                            mgrRef.AssignLocker(mgrRef.AssignedLocker);
                            ShowManagerDetail(mgrRef); // full rebuild for new slot count
                            MelonCoroutines.Start(RefreshBankNextFrame(mgrRef));
                        }
                        else
                        {
                            FlashError(_invErrorText);
                        }
                    }
                    else
                    {
                        float cost = ManagerUpgrades.GetNextSlotBuyIn(mgrRef.Configuration.ExtraInventorySlots);
                        var mm = NetworkSingleton<MoneyManager>.Instance;
                        if (mm != null && mm.onlineBalance < cost)
                        {
                            FlashError(_invErrorText);
                        }
                        else
                        {
                            mgrRef.Configuration.ExtraInventorySlots++;
                            mgrRef.ApplyInventoryCapacity();
                            ShowManagerDetail(mgrRef);
                            ConfigSyncData.SendQuestAction($"MANAGER_UPGRADE_INV:{mgrRef.Id}");
                        }
                    }
                }));
            }
            else
            {
                invBtnComp.interactable = false;
            }
        }

        private void RefreshUpgradeLabels(ManagerInstance mgr)
        {
            // Bank balance
            if (_detailBankText != null)
            {
                float bank = 0f;
                try { bank = NetworkSingleton<MoneyManager>.Instance?.onlineBalance ?? 0f; } catch { }
                _detailBankText.text = $"Bank Balance: <color=#66BF4D>${bank:N0}</color>";
            }

            // Daily wage
            if (_detailWageText != null)
                _detailWageText.text = $"Wage: <color=#F5A623>${mgr.GetDailyWage():F0}/day</color>";

            // Speed card
            if (_detailSpeedLabel != null)
            {
                bool maxed = ManagerUpgrades.IsSpeedMaxed(mgr.Configuration.SpeedTier);
                _detailSpeedLabel.text = $"Tier {mgr.Configuration.SpeedTier}/{ManagerUpgrades.MaxSpeedTier}";

                if (_detailSpeedFill != null)
                    _detailSpeedFill.anchorMax = new Vector2((float)mgr.Configuration.SpeedTier / ManagerUpgrades.MaxSpeedTier, 1f);

                if (_detailSpeedBtnText != null)
                    _detailSpeedBtnText.text = maxed ? "MAXED" : $"UPGRADE: ${ManagerUpgrades.GetNextSpeedBuyIn(mgr.Configuration.SpeedTier):N0}";

                if (_detailSpeedCostLabel != null)
                    _detailSpeedCostLabel.text = maxed ? "" : $"+${ManagerUpgrades.GetNextSpeedDailyFee(mgr.Configuration.SpeedTier):F0} Daily Maintenance";

                if (_detailSpeedBtn != null && maxed)
                    _detailSpeedBtn.interactable = false;
            }

            // Inventory card
            if (_detailInvUpLabel != null)
            {
                bool maxed = ManagerUpgrades.IsInventoryMaxed(mgr.Configuration.ExtraInventorySlots);
                int slots = ManagerUpgrades.GetTotalSlots(mgr.Configuration.ExtraInventorySlots);
                int maxSlots = ManagerUpgrades.GetTotalSlots(ManagerUpgrades.MaxExtraSlots);
                _detailInvUpLabel.text = $"{slots}/{maxSlots} Slots";

                if (_detailInvFill != null)
                {
                    float pct = ManagerUpgrades.MaxExtraSlots > 0
                        ? (float)mgr.Configuration.ExtraInventorySlots / ManagerUpgrades.MaxExtraSlots : 0f;
                    _detailInvFill.anchorMax = new Vector2(pct, 1f);
                }

                if (_detailInvBtnText != null)
                    _detailInvBtnText.text = maxed ? "MAXED" : $"UPGRADE: ${ManagerUpgrades.GetNextSlotBuyIn(mgr.Configuration.ExtraInventorySlots):N0}";

                if (_detailInvCostLabel != null)
                    _detailInvCostLabel.text = maxed ? "" : $"+${ManagerUpgrades.SlotDailyFee:F0} Daily Maintenance";

                if (_detailInvBtn != null && maxed)
                    _detailInvBtn.interactable = false;
            }
        }

        private IEnumerator RefreshBankNextFrame(ManagerInstance mgr)
        {
            yield return null;
            RefreshUpgradeLabels(mgr);
        }

        private void FlashError(Text errorText)
        {
            if (errorText == null) return;
            MelonCoroutines.Start(FlashErrorRoutine(errorText));
        }

        private IEnumerator FlashErrorRoutine(Text errorText)
        {
            Color c = errorText.color;
            float t = 0f;
            while (t < 0.15f)
            {
                t += Time.deltaTime;
                errorText.color = new Color(c.r, c.g, c.b, Mathf.Lerp(0f, 1f, t / 0.15f));
                yield return null;
            }
            errorText.color = new Color(c.r, c.g, c.b, 1f);

            yield return new WaitForSeconds(1.2f);

            t = 0f;
            while (t < 0.5f)
            {
                t += Time.deltaTime;
                errorText.color = new Color(c.r, c.g, c.b, Mathf.Lerp(1f, 0f, t / 0.5f));
                yield return null;
            }
            errorText.color = new Color(c.r, c.g, c.b, 0f);
        }

        private void BuildDebugLogButton(Transform contentArea, ManagerInstance mgr)
        {
            // Log buffer is only populated on the host — hide on clients
            if (!NetworkHelper.IsHost) return;

            var mgrRef = mgr;

            var (mask, btn, label) = UIFactory.RoundedButtonWithLabel(
                "DebugLogBtn", "Debug Log", contentArea,
                new Color(0.25f, 0.25f, 0.25f), 360, 36, 6, new Color(0.7f, 0.7f, 0.7f));
            var btnRect = mask.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0, 0);
            btnRect.anchorMax = new Vector2(0, 0);
            btnRect.pivot = new Vector2(0, 0);
            btnRect.anchoredPosition = new Vector2(16, 12);

            label.fontSize = 12;
            label.alignment = TextAnchor.MiddleCenter;

            btn.onClick.AddListener(new Action(() => ShowManagerLog(mgrRef)));
        }

        private void ShowManagerLog(ManagerInstance mgr)
        {
            // Clean up detail page
            if (_detailManager != null)
                _detailManager.OnMugshotReady -= OnDetailMugshotReady;
            StopMinimapTracking();

            if (_managerDetailPage != null)
            {
                UnityEngine.Object.Destroy(_managerDetailPage);
                _managerDetailPage = null;
                _detailMugshotImage = null;
                _detailInvGrid = null;
                _detailStatusText = null;
                _detailCashText = null;
                _detailBankText = null;
                _detailWageText = null;
                _detailSpeedLabel = null;
                _detailSpeedCostLabel = null;
                _detailSpeedBtn = null;
                _detailSpeedBtnText = null;
                _detailInvUpLabel = null;
                _detailInvCostLabel = null;
                _detailInvBtn = null;
                _detailInvBtnText = null;
                _detailSpeedFill = null;
                _detailInvFill = null;
                _speedErrorText = null;
                _invErrorText = null;
                _minimapImageRect = null;
                _minimapMarkerIcon = null;
                _minimapDestRect = null;
                _minimapBgImage = null;
            }
            _detailManager = null;

            BuildManagerLogPage(mgr);
        }

        internal void CloseManagerLog()
        {
            if (_managerLogPage != null)
            {
                UnityEngine.Object.Destroy(_managerLogPage);
                _managerLogPage = null;
                _logText = null;
                _logScrollRect = null;
            }

            var mgr = _logPageManager;
            _logPageManager = null;
            if (mgr != null)
                ShowManagerDetail(mgr);
        }
    }
}
