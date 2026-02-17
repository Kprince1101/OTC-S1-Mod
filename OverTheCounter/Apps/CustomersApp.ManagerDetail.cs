using S1API.UI;
using System;
using UnityEngine;
using UnityEngine.UI;
using OverTheCounter.Logic;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI.Phone.Map;
using Il2CppScheduleOne.Map;

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
                _minimapImageRect = null;
                _minimapMarkerIcon = null;
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
                var cashText = UIFactory.Text("Balance", $"Balance: <color=#66BF4D>${cash:N0}</color>", contentArea.transform, 14, TextAnchor.MiddleLeft);
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
            Il2CppScheduleOne.NPCs.NPCInventory npcInventory = null;
            try { npcInventory = mgr.GameNpc?.GetComponent<Il2CppScheduleOne.NPCs.NPCInventory>(); }
            catch { }

            int displaySlots = 5;
            if (npcInventory?.ItemSlots != null)
                displaySlots = Math.Max(5, npcInventory.ItemSlots.Count);

            var invPanel = UIFactory.Panel("InvGrid", contentArea.transform, Color.clear);
            _detailInvGrid = invPanel;
            var invRect = invPanel.GetComponent<RectTransform>();
            invRect.anchorMin = new Vector2(0, 1);
            invRect.anchorMax = new Vector2(0.5f, 1);
            invRect.pivot = new Vector2(0, 1);
            invRect.anchoredPosition = new Vector2(16, -142);
            invRect.sizeDelta = new Vector2(0, 80);

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
                int qty = 0;
                try
                {
                    if (npcInventory?.ItemSlots != null && i < npcInventory.ItemSlots.Count)
                    {
                        var slot = npcInventory.ItemSlots[i];
                        if (slot?.ItemInstance?.Definition != null)
                        {
                            icon = slot.ItemInstance.Definition.Icon;
                            qty = slot.Quantity;
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

                    if (qty > 0)
                    {
                        var qtyText = UIFactory.Text($"Qty_{i}", qty.ToString(), slotPanel.transform, 13, TextAnchor.LowerRight);
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

            // TODO: Upgrade buttons — implement in next update
            // BuildUpgradeButtons(contentArea.transform, mgr);

            // ── Debug log button ──
            BuildDebugLogButton(contentArea.transform, mgr);

            // ── Right side: Minimap ──
            BuildMinimap(contentArea.transform, mgr);
        }

        private void BuildMinimap(Transform contentArea, ManagerInstance mgr)
        {
            // Map container (right half of content area)
            var mapContainer = UIFactory.Panel("MapContainer", contentArea, new Color(0.10f, 0.10f, 0.10f));
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
                var poiBase = (Il2CppScheduleOne.Map.POI)mgr.MapPoI;
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

            // Set initial position
            UpdateMinimapPosition();
        }

        private void UpdateMinimapPosition()
        {
            if (_minimapImageRect == null || _detailManager?.GameNpc == null) return;

            try
            {
                var mapUtil = Singleton<MapPositionUtility>.Instance;
                if (mapUtil == null) return;

                Vector3 worldPos = _detailManager.GameNpc.transform.position;
                Vector2 mapPos = mapUtil.GetMapPosition(worldPos);

                float scaleX = _minimapDisplaySize / _minimapContentW;
                float scaleY = _minimapDisplaySize / _minimapContentH;

                _minimapImageRect.anchoredPosition = new Vector2(-mapPos.x * scaleX, -mapPos.y * scaleY);
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

            // Update cash
            if (_detailCashText != null && _detailManager.HasLocker)
            {
                float cash = _detailManager.GetLockerCash();
                _detailCashText.text = $"Balance: <color=#66BF4D>${cash:N0}</color>";
            }

            // Rebuild inventory slots
            if (_detailInvGrid != null)
            {
                ClearChildren(_detailInvGrid.transform);

                Il2CppScheduleOne.NPCs.NPCInventory npcInventory = null;
                try { npcInventory = _detailManager.GameNpc?.GetComponent<Il2CppScheduleOne.NPCs.NPCInventory>(); }
                catch { }

                int displaySlots = 5;
                if (npcInventory?.ItemSlots != null)
                    displaySlots = Math.Max(5, npcInventory.ItemSlots.Count);

                for (int i = 0; i < displaySlots; i++)
                {
                    var slotPanel = UIFactory.Panel($"Slot_{i}", _detailInvGrid.transform, SlotBg);

                    Sprite icon = null;
                    int qty = 0;
                    try
                    {
                        if (npcInventory?.ItemSlots != null && i < npcInventory.ItemSlots.Count)
                        {
                            var slot = npcInventory.ItemSlots[i];
                            if (slot?.ItemInstance?.Definition != null)
                            {
                                icon = slot.ItemInstance.Definition.Icon;
                                qty = slot.Quantity;
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

                        if (qty > 0)
                        {
                            var qtyText = UIFactory.Text($"Qty_{i}", qty.ToString(), slotPanel.transform, 13, TextAnchor.LowerRight);
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

        private void BuildDebugLogButton(Transform contentArea, ManagerInstance mgr)
        {
            float yPos = -228f;
            var mgrRef = mgr;

            var (mask, btn, label) = UIFactory.RoundedButtonWithLabel(
                "DebugLogBtn", "Debug Log", contentArea,
                new Color(0.25f, 0.25f, 0.25f), 360, 36, 6, new Color(0.7f, 0.7f, 0.7f));
            var btnRect = mask.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0, 1);
            btnRect.anchorMax = new Vector2(0, 1);
            btnRect.pivot = new Vector2(0, 1);
            btnRect.anchoredPosition = new Vector2(16, yPos);

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
                _minimapImageRect = null;
                _minimapMarkerIcon = null;
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
