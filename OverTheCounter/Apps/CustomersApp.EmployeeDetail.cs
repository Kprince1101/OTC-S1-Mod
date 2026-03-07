using OverTheCounter.UI;
using S1API.UI;
using System;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppTMPro;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.Property;
using Il2CppScheduleOne.UI.Phone.Map;
using Il2CppScheduleOne.Map;
using NPCInventory = Il2CppScheduleOne.NPCs.NPCInventory;
using CashInstance = Il2CppScheduleOne.ItemFramework.CashInstance;
#else
using TMPro;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.Property;
using ScheduleOne.UI.Phone.Map;
using ScheduleOne.Map;
using NPCInventory = ScheduleOne.NPCs.NPCInventory;
using CashInstance = ScheduleOne.ItemFramework.CashInstance;
#endif

namespace OverTheCounter.Apps
{
    public partial class CustomersApp
    {
        // Employee detail state
        private Employee _detailEmployee;
        private TextMeshProUGUI _empDetailStatusText;
        private TextMeshProUGUI _empDetailPaidText;
        private TextMeshProUGUI _empDetailLockerText;
        private GameObject _empDetailIssuesContainer;
        private GameObject _empDetailInvGrid;

        private void ShowEmployeeDetail(Employee emp)
        {
            _detailEmployee = emp;

            // Hide list pages (keep header/tabs visible)
            _managersPage.SetActive(false);
            _employeesPage.SetActive(false);
            _customersPage.SetActive(false);

            if (_employeeDetailPage != null)
                UnityEngine.Object.Destroy(_employeeDetailPage);

            _employeeDetailPage = UIFactory.Panel("EmployeeDetailPage", _rootPanel.transform, new Color(0.12f, 0.12f, 0.12f));
            var detailRect = _employeeDetailPage.GetComponent<RectTransform>();
            detailRect.anchorMin = Vector2.zero;
            detailRect.anchorMax = Vector2.one;
            detailRect.offsetMin = Vector2.zero;
            detailRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            BuildEmployeeDetailContent(_employeeDetailPage.transform, emp);

            StartMinimapTracking();
        }

        private void CloseEmployeeDetail()
        {
            StopMinimapTracking();

            if (_employeeDetailPage != null)
            {
                UnityEngine.Object.Destroy(_employeeDetailPage);
                _employeeDetailPage = null;
            }

            CleanupEmployeeDetailFields();

            _employeesPage.SetActive(true);
            RefreshEmployeesPage();
        }

        private void CleanupEmployeeDetailFields()
        {
            _detailEmployee = null;
            _empDetailStatusText = null;
            _empDetailPaidText = null;
            _empDetailLockerText = null;
            _empDetailIssuesContainer = null;
            _empDetailInvGrid = null;
            _minimapImageRect = null;
            _minimapMarkerIcon = null;
            _minimapDestRect = null;
            _minimapBgImage = null;
        }

        private void BuildEmployeeDetailContent(Transform parent, Employee emp)
        {
            // ── Back button bar ──
            var backBar = UIFactory.Panel("BackBar", parent, new Color(0.15f, 0.15f, 0.15f));
            var backBarRect = backBar.GetComponent<RectTransform>();
            backBarRect.anchorMin = new Vector2(0, 1);
            backBarRect.anchorMax = new Vector2(1, 1);
            backBarRect.pivot = new Vector2(0.5f, 1);
            backBarRect.anchoredPosition = Vector2.zero;
            backBarRect.sizeDelta = new Vector2(0, 32);

            var backBtn = backBar.AddComponent<Button>();
            backBtn.onClick.AddListener(new Action(CloseEmployeeDetail));

            var backText = TMPFactory.Text("BackLabel", "\u25C0  Back", backBar.transform, 16, TextAlignmentOptions.Left);
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

            // ── Left column: Info ──
            string firstName = "Employee";
            string lastName = "";
            string propName = "";
            string typeDisplay = "";
            try
            {
                firstName = emp.FirstName ?? "Employee";
                lastName = emp.LastName ?? "";
                propName = emp.AssignedProperty?.PropertyName ?? emp.AssignedProperty?.PropertyCode ?? "";
                typeDisplay = EmployeeTypeDisplayName(emp.EmployeeType);
            }
            catch { }

            // Mugshot
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

            try
            {
                var mugSprite = emp.MugshotSprite;
                if (mugSprite != null)
                {
                    var mugImg = mugPanel.GetComponent<Image>();
                    mugImg.sprite = mugSprite;
                    mugImg.color = Color.white;
                }
            }
            catch { }

            // Name
            var nameText = TMPFactory.Text("Name", $"<b>{firstName} {lastName}</b>", contentArea.transform, 20, TextAlignmentOptions.Left);
            nameText.color = Color.white;
            var nameRect = nameText.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 1);
            nameRect.anchorMax = new Vector2(0.45f, 1);
            nameRect.pivot = new Vector2(0, 1);
            nameRect.anchoredPosition = new Vector2(120, -14);
            nameRect.sizeDelta = new Vector2(0, 28);

            // Type · Property
            string typeAndProp = string.IsNullOrEmpty(propName) ? typeDisplay : $"{typeDisplay} · {propName}";
            var typeText = TMPFactory.Text("TypeProp", typeAndProp, contentArea.transform, 15, TextAlignmentOptions.Left);
            typeText.color = new Color(0.55f, 0.55f, 0.55f);
            var typeRect = typeText.gameObject.GetComponent<RectTransform>();
            typeRect.anchorMin = new Vector2(0, 1);
            typeRect.anchorMax = new Vector2(0.45f, 1);
            typeRect.pivot = new Vector2(0, 1);
            typeRect.anchoredPosition = new Vector2(120, -44);
            typeRect.sizeDelta = new Vector2(0, 22);

            // Status
            var (statusStr, statusColor) = GetEmployeeStatus(emp);
            var statusText = TMPFactory.Text("Status", statusStr, contentArea.transform, 15, TextAlignmentOptions.Left);
            statusText.color = statusColor;
            _empDetailStatusText = statusText;
            var statusRect = statusText.gameObject.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0, 1);
            statusRect.anchorMax = new Vector2(0.45f, 1);
            statusRect.pivot = new Vector2(0, 1);
            statusRect.anchoredPosition = new Vector2(120, -66);
            statusRect.sizeDelta = new Vector2(0, 22);

            // Paid today
            bool paid = false;
            try { paid = emp.PaidForToday; } catch { }
            string paidStr = paid
                ? "Paid today: <color=#66BF4D>Yes</color>"
                : "Paid today: <color=#E84040>No</color>";
            var paidText = TMPFactory.Text("PaidToday", paidStr, contentArea.transform, 15, TextAlignmentOptions.Left);
            paidText.color = new Color(0.7f, 0.7f, 0.7f);
            paidText.richText = true;
            _empDetailPaidText = paidText;
            var paidRect = paidText.gameObject.GetComponent<RectTransform>();
            paidRect.anchorMin = new Vector2(0, 1);
            paidRect.anchorMax = new Vector2(0.45f, 1);
            paidRect.pivot = new Vector2(0, 1);
            paidRect.anchoredPosition = new Vector2(120, -88);
            paidRect.sizeDelta = new Vector2(0, 22);

            // Locker cash
            string lockerStr = "Locker: (no locker)";
            try
            {
                var home = emp.GetHome();
                if (home != null)
                    lockerStr = $"Locker: <color=#66BF4D>${home.GetCashSum():N0}</color>";
            }
            catch { }

            var lockerText = TMPFactory.Text("Locker", lockerStr, contentArea.transform, 15, TextAlignmentOptions.Left);
            lockerText.color = new Color(0.7f, 0.7f, 0.7f);
            lockerText.richText = true;
            _empDetailLockerText = lockerText;
            var lockerRect = lockerText.gameObject.GetComponent<RectTransform>();
            lockerRect.anchorMin = new Vector2(0, 1);
            lockerRect.anchorMax = new Vector2(0.45f, 1);
            lockerRect.pivot = new Vector2(0, 1);
            lockerRect.anchoredPosition = new Vector2(16, -118);
            lockerRect.sizeDelta = new Vector2(0, 22);

            // Daily wage
            float wage = 0;
            try { wage = emp.DailyWage; } catch { }
            var wageText = TMPFactory.Text("Wage", $"Daily wage: ${wage:N0}", contentArea.transform, 15, TextAlignmentOptions.Left);
            wageText.color = new Color(0.7f, 0.7f, 0.7f);
            var wageRect = wageText.gameObject.GetComponent<RectTransform>();
            wageRect.anchorMin = new Vector2(0, 1);
            wageRect.anchorMax = new Vector2(0.45f, 1);
            wageRect.pivot = new Vector2(0, 1);
            wageRect.anchoredPosition = new Vector2(16, -142);
            wageRect.sizeDelta = new Vector2(0, 22);

            // Work Issues header
            var issuesLabel = TMPFactory.Text("IssuesLabel", "<b>Work Issues</b>", contentArea.transform, 15, TextAlignmentOptions.Left);
            issuesLabel.color = new Color(0.7f, 0.7f, 0.7f);
            var issuesLabelRect = issuesLabel.gameObject.GetComponent<RectTransform>();
            issuesLabelRect.anchorMin = new Vector2(0, 1);
            issuesLabelRect.anchorMax = new Vector2(0.45f, 1);
            issuesLabelRect.pivot = new Vector2(0, 1);
            issuesLabelRect.anchoredPosition = new Vector2(16, -172);
            issuesLabelRect.sizeDelta = new Vector2(0, 22);

            // Issues container
            var issuesContainer = UIFactory.Panel("IssuesContainer", contentArea.transform, Color.clear);
            _empDetailIssuesContainer = issuesContainer;
            var issuesContainerRect = issuesContainer.GetComponent<RectTransform>();
            issuesContainerRect.anchorMin = new Vector2(0, 1);
            issuesContainerRect.anchorMax = new Vector2(0.45f, 1);
            issuesContainerRect.pivot = new Vector2(0, 1);
            issuesContainerRect.anchoredPosition = new Vector2(16, -196);
            issuesContainerRect.sizeDelta = new Vector2(0, 80);

            var issuesLayout = issuesContainer.AddComponent<VerticalLayoutGroup>();
            issuesLayout.childControlHeight = true;
            issuesLayout.childControlWidth = true;
            issuesLayout.childForceExpandHeight = false;
            issuesLayout.childForceExpandWidth = true;
            issuesLayout.spacing = 4;

            PopulateIssuesContainer(issuesContainer.transform, emp);

            // Inventory header
            var invLabel = TMPFactory.Text("InvLabel", "<b>Inventory</b>", contentArea.transform, 15, TextAlignmentOptions.Left);
            invLabel.color = new Color(0.7f, 0.7f, 0.7f);
            var invLabelRect = invLabel.gameObject.GetComponent<RectTransform>();
            invLabelRect.anchorMin = new Vector2(0, 1);
            invLabelRect.anchorMax = new Vector2(0.45f, 1);
            invLabelRect.pivot = new Vector2(0, 1);
            invLabelRect.anchoredPosition = new Vector2(16, -284);
            invLabelRect.sizeDelta = new Vector2(0, 22);

            NPCInventory npcInventory = null;
            try { npcInventory = emp.GetComponent<NPCInventory>(); }
            catch { }

            int displaySlots = 5;
            if (npcInventory?.ItemSlots != null)
                displaySlots = Math.Max(5, npcInventory.ItemSlots.Count);

            var invPanel = UIFactory.Panel("InvGrid", contentArea.transform, Color.clear);
            _empDetailInvGrid = invPanel;
            int invRows = Math.Max(1, (displaySlots + SLOTS_PER_ROW - 1) / SLOTS_PER_ROW);
            float invGridHeight = invRows * SLOT_SIZE + Math.Max(0, invRows - 1) * SLOT_GAP;
            var invRect = invPanel.GetComponent<RectTransform>();
            invRect.anchorMin = new Vector2(0, 1);
            invRect.anchorMax = new Vector2(0.45f, 1);
            invRect.pivot = new Vector2(0, 1);
            invRect.anchoredPosition = new Vector2(16, -308);
            invRect.sizeDelta = new Vector2(0, invGridHeight);

            var grid = invPanel.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(SLOT_SIZE, SLOT_SIZE);
            grid.spacing = new Vector2(SLOT_GAP, SLOT_GAP);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = SLOTS_PER_ROW;
            grid.childAlignment = TextAnchor.UpperLeft;

            PopulateInvGrid(invPanel.transform, npcInventory, displaySlots);

            // ── Right side: Minimap ──
            BuildEmployeeMinimap(contentArea.transform, emp);
        }

        private void PopulateIssuesContainer(Transform parent, Employee emp)
        {
            ClearChildren(parent);

            try
            {
#if IL2CPP
                var issues = emp.WorkIssues;
#else
                var issues = WorkIssuesField?.GetValue(emp) as System.Collections.Generic.List<Employee.NoWorkReason>;
#endif
                if (issues == null || issues.Count == 0)
                {
                    var noIssueText = TMPFactory.Text("NoIssues", "No issues", parent, 15, TextAlignmentOptions.Left);
                    noIssueText.color = new Color(0.55f, 0.55f, 0.55f);
                    var noIssueLayout = noIssueText.gameObject.AddComponent<LayoutElement>();
                    noIssueLayout.preferredHeight = 18;
                    return;
                }

                foreach (var issue in issues)
                {
                    try
                    {
                        var reasonText = TMPFactory.Text("Reason", issue.Reason ?? "", parent, 15, TextAlignmentOptions.Left);
                        reasonText.color = Color.white;
                        var reasonLayout = reasonText.gameObject.AddComponent<LayoutElement>();
                        reasonLayout.preferredHeight = 18;

                        if (!string.IsNullOrEmpty(issue.Fix))
                        {
                            var fixText = TMPFactory.Text("Fix", $"  Fix: {issue.Fix}", parent, 15, TextAlignmentOptions.Left);
                            fixText.color = new Color(0.5f, 0.5f, 0.5f);
                            var fixLayout = fixText.gameObject.AddComponent<LayoutElement>();
                            fixLayout.preferredHeight = 16;
                        }
                    }
                    catch { }
                }
            }
            catch
            {
                var noIssueText = TMPFactory.Text("NoIssues", "No issues", parent, 15, TextAlignmentOptions.Left);
                noIssueText.color = new Color(0.55f, 0.55f, 0.55f);
                var noIssueLayout = noIssueText.gameObject.AddComponent<LayoutElement>();
                noIssueLayout.preferredHeight = 18;
            }
        }

        private void PopulateInvGrid(Transform parent, NPCInventory npcInventory, int displaySlots)
        {
            ClearChildren(parent);

            for (int i = 0; i < displaySlots; i++)
            {
                var slotPanel = UIFactory.Panel($"Slot_{i}", parent, SlotBg);

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
#if IL2CPP
                            var cash = slot.ItemInstance.TryCast<CashInstance>();
#else
                            var cash = slot.ItemInstance as CashInstance;
#endif
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
                        var qtyText = TMPFactory.Text($"Qty_{i}", displayQty, slotPanel.transform, 15, TextAlignmentOptions.BottomRight);
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

        private void BuildEmployeeMinimap(Transform contentArea, Employee emp)
        {
            var mapContainer = UIFactory.Panel("MapContainer", contentArea, new Color(0.20f, 0.259f, 0.298f));
            var mapContainerRect = mapContainer.GetComponent<RectTransform>();
            mapContainerRect.anchorMin = new Vector2(0.5f, 0);
            mapContainerRect.anchorMax = Vector2.one;
            mapContainerRect.offsetMin = new Vector2(8, 8);
            mapContainerRect.offsetMax = new Vector2(-8, -8);

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
                var noMapText = TMPFactory.Text("NoMap", "Map unavailable", mapContainer.transform, 15, TextAlignmentOptions.Center);
                noMapText.color = new Color(0.4f, 0.4f, 0.4f);
                var noMapRect = noMapText.gameObject.GetComponent<RectTransform>();
                noMapRect.anchorMin = Vector2.zero;
                noMapRect.anchorMax = Vector2.one;
                noMapRect.offsetMin = Vector2.zero;
                noMapRect.offsetMax = Vector2.zero;
                return;
            }

            mapContainer.AddComponent<RectMask2D>();
            _minimapBgImage = mapContainer.GetComponent<Image>();

            var mapObj = new GameObject("MapImage");
            mapObj.transform.SetParent(mapContainer.transform, false);
            var mapImage = mapObj.AddComponent<Image>();
            mapImage.sprite = mapSprite;
            mapImage.preserveAspect = true;

            _minimapImageRect = mapObj.GetComponent<RectTransform>();
            _minimapImageRect.anchorMin = new Vector2(0.5f, 0.5f);
            _minimapImageRect.anchorMax = new Vector2(0.5f, 0.5f);
            _minimapImageRect.pivot = new Vector2(0.5f, 0.5f);
            _minimapDisplaySize = 2400f;
            _minimapImageRect.sizeDelta = new Vector2(_minimapDisplaySize, _minimapDisplaySize);

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

            // Position marker — programmatic circle sprite so we don't depend on any POI being loaded
            float markerSize = 32f;
            var markerObj = new GameObject("Marker");
            markerObj.transform.SetParent(mapContainer.transform, false);
            var markerImg = markerObj.AddComponent<Image>();
            markerImg.sprite = GetOrCreateCircleSprite();
            markerImg.color = Color.white;
            var markerRect = markerObj.GetComponent<RectTransform>();
            markerRect.anchorMin = new Vector2(0.5f, 0.5f);
            markerRect.anchorMax = new Vector2(0.5f, 0.5f);
            markerRect.pivot = new Vector2(0.5f, 0.5f);
            markerRect.sizeDelta = new Vector2(markerSize, markerSize);
            markerRect.anchoredPosition = Vector2.zero;

            // Mugshot icon inside the circle
            var markerIconObj = new GameObject("MarkerIcon");
            markerIconObj.transform.SetParent(markerObj.transform, false);
            _minimapMarkerIcon = markerIconObj.AddComponent<Image>();
            _minimapMarkerIcon.preserveAspect = true;
            _minimapMarkerIcon.color = Color.clear;
            var markerIconRect = markerIconObj.GetComponent<RectTransform>();
            markerIconRect.anchorMin = new Vector2(0.1f, 0.1f);
            markerIconRect.anchorMax = new Vector2(0.9f, 0.9f);
            markerIconRect.offsetMin = Vector2.zero;
            markerIconRect.offsetMax = Vector2.zero;

            try
            {
                var mugSprite = emp.MugshotSprite;
                if (mugSprite != null)
                {
                    _minimapMarkerIcon.sprite = mugSprite;
                    _minimapMarkerIcon.color = Color.white;
                }
            }
            catch { }

            // Destination marker — inactive for employees
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

            UpdateMinimapPosition();
        }

        private void RefreshEmployeeDetail()
        {
            var emp = _detailEmployee;
            if (emp == null) return;

            if (_empDetailStatusText != null)
            {
                var (statusStr, statusColor) = GetEmployeeStatus(emp);
                _empDetailStatusText.text = statusStr;
                _empDetailStatusText.color = statusColor;
            }

            if (_empDetailPaidText != null)
            {
                bool paid = false;
                try { paid = emp.PaidForToday; } catch { }
                _empDetailPaidText.text = paid
                    ? "Paid today: <color=#66BF4D>Yes</color>"
                    : "Paid today: <color=#E84040>No</color>";
            }

            if (_empDetailLockerText != null)
            {
                string lockerStr = "Locker: (no locker)";
                try
                {
                    var home = emp.GetHome();
                    if (home != null)
                        lockerStr = $"Locker: <color=#66BF4D>${home.GetCashSum():N0}</color>";
                }
                catch { }
                _empDetailLockerText.text = lockerStr;
            }

            if (_empDetailIssuesContainer != null)
                PopulateIssuesContainer(_empDetailIssuesContainer.transform, emp);

            if (_empDetailInvGrid != null)
            {
                NPCInventory npcInventory = null;
                try { npcInventory = emp.GetComponent<NPCInventory>(); }
                catch { }

                int displaySlots = 5;
                if (npcInventory?.ItemSlots != null)
                    displaySlots = Math.Max(5, npcInventory.ItemSlots.Count);

                if (displaySlots != _empDetailInvGrid.transform.childCount)
                {
                    var empRef = emp;
                    ShowEmployeeDetail(empRef);
                    return;
                }

                PopulateInvGrid(_empDetailInvGrid.transform, npcInventory, displaySlots);
            }
        }

        // Cached programmatic circle sprite — created once, reused across all employee detail views
        private static Sprite _circleSprite;
        private static Sprite GetOrCreateCircleSprite()
        {
            if (_circleSprite != null) return _circleSprite;
            const int size = 64;
            float center = size / 2f - 0.5f;
            float r2 = center * center;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - center, dy = y - center;
                pixels[y * size + x] = (dx * dx + dy * dy) <= r2 ? Color.white : Color.clear;
            }
            tex.SetPixels(pixels);
            tex.Apply();
            _circleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            return _circleSprite;
        }
    }
}
