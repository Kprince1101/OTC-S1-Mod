using OverTheCounter.Logic;
using OverTheCounter.UI;
using S1API.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Property;
using Il2CppTMPro;
#else
using ScheduleOne.Employees;
using ScheduleOne.NPCs;
using ScheduleOne.Property;
using TMPro;
#endif

namespace OverTheCounter.Apps
{
    public partial class CustomersApp
    {
        private string _lastEmployeeListFingerprint;

#if !IL2CPP
        // WorkIssues is a private field on Employee in Mono — cache reflection accessor once
        private static readonly System.Reflection.FieldInfo WorkIssuesField =
            typeof(Employee).GetField("WorkIssues", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
#endif

        internal static (string text, Color color) GetEmployeeStatus(Employee emp)
        {
            try
            {
#if IL2CPP
                var issues = emp.WorkIssues;
                if (issues != null && issues.Count > 0)
                    return ($"\u25CF {issues[0].Reason}", new Color(0.9f, 0.6f, 0.15f));
#else
                var issues = WorkIssuesField?.GetValue(emp) as System.Collections.Generic.List<Employee.NoWorkReason>;
                if (issues != null && issues.Count > 0)
                    return ($"\u25CF {issues[0].Reason}", new Color(0.9f, 0.6f, 0.15f));
#endif
            }
            catch { }

            if (!emp.PaidForToday)
                return ("\u25CF Unpaid", new Color(0.9f, 0.25f, 0.25f));
            if (emp.IsWaitingOutside)
                return ("\u25CF Idle", new Color(0.5f, 0.5f, 0.5f));
            return ("\u25CF Working", new Color(0.2f, 0.75f, 0.2f));
        }

        private static string EmployeeTypeDisplayName(EEmployeeType t) => t switch
        {
            EEmployeeType.Handler => "Packager",
            _ => t.ToString()
        };

        private const float FilterBarH = 38f;

        private void BuildEmployeesPage(Transform parent)
        {
            // Initialize filter state once per session
            if (_employeeTypeFilter == null)
                _employeeTypeFilter = new HashSet<EEmployeeType>
                {
                    EEmployeeType.Botanist, EEmployeeType.Chemist,
                    EEmployeeType.Handler, EEmployeeType.Cleaner
                };

            // ── Filter bar pinned to top ──
            var filterBar = UIFactory.Panel("FilterBar", parent, new Color(0.10f, 0.12f, 0.14f));
            var filterBarRect = filterBar.GetComponent<RectTransform>();
            filterBarRect.anchorMin = new Vector2(0, 1);
            filterBarRect.anchorMax = Vector2.one;
            filterBarRect.offsetMin = new Vector2(0, -FilterBarH);
            filterBarRect.offsetMax = Vector2.zero;
            BuildEmployeeFilterBar(filterBar.transform);

            // ── Scroll area fills the rest ──
            var scrollHost = UIFactory.Panel("ScrollHost", parent, Color.clear);
            var scrollHostRect = scrollHost.GetComponent<RectTransform>();
            scrollHostRect.anchorMin = Vector2.zero;
            scrollHostRect.anchorMax = Vector2.one;
            scrollHostRect.offsetMin = Vector2.zero;
            scrollHostRect.offsetMax = new Vector2(0, -FilterBarH);

            var contentRect = UIFactory.ScrollableVerticalList("EmployeeScroll", scrollHost.transform, out ScrollRect scrollRect);

            var scrollRt = scrollRect.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = Vector2.zero;
            scrollRt.offsetMax = Vector2.zero;

            scrollRect.horizontal = false;
            scrollRect.scrollSensitivity = 20f;

            var contentFitter = contentRect.GetComponent<ContentSizeFitter>();
            if (contentFitter != null)
                contentFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0, 1);
            contentRect.sizeDelta = new Vector2(0, contentRect.sizeDelta.y);

            var contentLayout = contentRect.GetComponent<VerticalLayoutGroup>();
            if (contentLayout != null)
            {
                contentLayout.childControlHeight = true;
                contentLayout.childControlWidth = true;
                contentLayout.childForceExpandHeight = false;
                contentLayout.childForceExpandWidth = true;
                contentLayout.childAlignment = TextAnchor.UpperCenter;
                contentLayout.spacing = 4;
                contentLayout.padding = new RectOffset(10, 10, 8, 12);
            }

            _employeesContentParent = contentRect.transform;
            PopulateEmployeeList(_employeesContentParent);
            _lastEmployeeListFingerprint = BuildEmployeeListFingerprint();
        }

        private void BuildEmployeeFilterBar(Transform parent)
        {
            var hLayout = parent.gameObject.AddComponent<HorizontalLayoutGroup>();
            hLayout.childControlHeight = true;
            hLayout.childControlWidth = true;
            hLayout.childForceExpandHeight = true;
            hLayout.childForceExpandWidth = false;
            hLayout.spacing = 4;
            hLayout.padding = new RectOffset(8, 8, 5, 5);
            hLayout.childAlignment = TextAnchor.MiddleLeft;

            // "Include:" label
            var includeLabel = TMPFactory.Text("IncludeLabel", "Include:", parent, 15, TextAlignmentOptions.Left);
            includeLabel.color = new Color(0.55f, 0.55f, 0.55f);
            var includeLabelLayout = includeLabel.gameObject.AddComponent<LayoutElement>();
            includeLabelLayout.preferredWidth = 62;

            // Type toggles — order matches user spec
            var typeOrder = new (EEmployeeType type, string label)[]
            {
                (EEmployeeType.Chemist,  "Chemists"),
                (EEmployeeType.Botanist, "Botanists"),
                (EEmployeeType.Handler,  "Handlers"),
                (EEmployeeType.Cleaner,  "Cleaners"),
            };

            foreach (var (type, label) in typeOrder)
                AddTypeFilterToggle(parent, type, label);

            // Flexible spacer
            var spacer = new GameObject("Spacer");
            spacer.transform.SetParent(parent, false);
            spacer.AddComponent<LayoutElement>().flexibleWidth = 1;

            // HireMe integration button (only shown when mod is installed)
            AddHireMeButton(parent);

            // Separator
            var sep = UIFactory.Panel("Sep", parent, new Color(0.35f, 0.35f, 0.35f));
            sep.AddComponent<LayoutElement>().preferredWidth = 1;

            // "Group by Property" label + checkbox
            var groupLabel = TMPFactory.Text("GroupByLabel", "Group by Property", parent, 15, TextAlignmentOptions.Right);
            groupLabel.color = new Color(0.55f, 0.55f, 0.55f);
            groupLabel.gameObject.AddComponent<LayoutElement>().preferredWidth = 140;

            AddGroupByToggle(parent);
        }

        private void AddTypeFilterToggle(Transform parent, EEmployeeType type, string label)
        {
            bool active = _employeeTypeFilter.Contains(type);

            var btn = UIFactory.Panel($"Toggle_{type}", parent,
                active ? new Color(0.10f, 0.25f, 0.15f) : new Color(0.18f, 0.18f, 0.18f));
            btn.AddComponent<LayoutElement>().preferredWidth = 90;

            var bgImg = btn.GetComponent<Image>();
            var lbl = TMPFactory.Text("Label", (active ? "\u25CF " : "\u25CB ") + label, btn.transform, 15, TextAlignmentOptions.Center);
            lbl.color = active ? new Color(0.35f, 0.9f, 0.5f) : new Color(0.5f, 0.5f, 0.5f);
            var lblRect = lbl.gameObject.GetComponent<RectTransform>();
            lblRect.anchorMin = Vector2.zero;
            lblRect.anchorMax = Vector2.one;
            lblRect.offsetMin = new Vector2(2, 0);
            lblRect.offsetMax = new Vector2(-2, 0);

            var button = btn.AddComponent<Button>();
            var capturedType = type;
            var capturedLabel = label;
            button.onClick.AddListener(new Action(() =>
            {
                bool nowActive = !_employeeTypeFilter.Contains(capturedType);
                if (nowActive) _employeeTypeFilter.Add(capturedType);
                else _employeeTypeFilter.Remove(capturedType);

                bgImg.color = nowActive ? new Color(0.10f, 0.25f, 0.15f) : new Color(0.18f, 0.18f, 0.18f);
                lbl.text = (nowActive ? "\u25CF " : "\u25CB ") + capturedLabel;
                lbl.color = nowActive ? new Color(0.35f, 0.9f, 0.5f) : new Color(0.5f, 0.5f, 0.5f);
                RebuildEmployeeList();
            }));
        }

        private void AddGroupByToggle(Transform parent)
        {
            bool active = _employeeGroupByProperty;

            var btn = UIFactory.Panel("GroupByToggle", parent,
                active ? new Color(0.10f, 0.25f, 0.15f) : new Color(0.18f, 0.18f, 0.18f));
            btn.AddComponent<LayoutElement>().preferredWidth = 30;

            var bgImg = btn.GetComponent<Image>();
            var chk = TMPFactory.Text("Check", active ? "\u25CF" : "\u25CB", btn.transform, 15, TextAlignmentOptions.Center);
            chk.color = active ? new Color(0.35f, 0.9f, 0.5f) : new Color(0.5f, 0.5f, 0.5f);
            var chkRect = chk.gameObject.GetComponent<RectTransform>();
            chkRect.anchorMin = Vector2.zero;
            chkRect.anchorMax = Vector2.one;
            chkRect.offsetMin = Vector2.zero;
            chkRect.offsetMax = Vector2.zero;

            var button = btn.AddComponent<Button>();
            button.onClick.AddListener(new Action(() =>
            {
                _employeeGroupByProperty = !_employeeGroupByProperty;
                bool nowActive = _employeeGroupByProperty;
                bgImg.color = nowActive ? new Color(0.10f, 0.25f, 0.15f) : new Color(0.18f, 0.18f, 0.18f);
                chk.text = nowActive ? "\u25CF" : "\u25CB";
                chk.color = nowActive ? new Color(0.35f, 0.9f, 0.5f) : new Color(0.5f, 0.5f, 0.5f);
                RebuildEmployeeList();
            }));
        }

        private void RebuildEmployeeList()
        {
            if (_employeesContentParent == null) return;
            ClearChildren(_employeesContentParent);
            PopulateEmployeeList(_employeesContentParent);
            _lastEmployeeListFingerprint = BuildEmployeeListFingerprint();
        }

        private void RefreshEmployeesPage()
        {
            if (_employeesContentParent == null) return;

            string fingerprint = BuildEmployeeListFingerprint();
            if (fingerprint == _lastEmployeeListFingerprint) return;

            ClearChildren(_employeesContentParent);
            PopulateEmployeeList(_employeesContentParent);
            _lastEmployeeListFingerprint = fingerprint;
        }

        private string BuildEmployeeListFingerprint()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(_employeeGroupByProperty ? "G" : "F");
                foreach (var prop in Property.OwnedProperties)
                {
                    if (prop?.Employees == null) continue;
                    foreach (var emp in prop.Employees)
                    {
                        if (emp == null || emp.Fired) continue;
                        if (_employeeTypeFilter != null && !_employeeTypeFilter.Contains(emp.EmployeeType)) continue;
                        var (status, _) = GetEmployeeStatus(emp);
                        sb.Append(emp.ID).Append(':').Append(status).Append('|');
                    }
                }
                return sb.ToString();
            }
            catch { return null; }
        }

        private void PopulateEmployeeList(Transform contentParent)
        {
            // Collect all matching employees with their property
            var allEmployees = new List<(Employee emp, Property prop)>();
            try
            {
                foreach (var prop in Property.OwnedProperties)
                {
                    if (prop?.Employees == null) continue;
                    foreach (var emp in prop.Employees)
                    {
                        if (emp == null || emp.Fired) continue;
                        if (_employeeTypeFilter != null && !_employeeTypeFilter.Contains(emp.EmployeeType)) continue;
                        allEmployees.Add((emp, prop));
                    }
                }
            }
            catch { }

            if (allEmployees.Count == 0)
            {
                var emptyText = TMPFactory.Text("EmptyMsg", "No employees match the current filters.", contentParent, 15, TextAlignmentOptions.Center);
                emptyText.color = new Color(0.5f, 0.5f, 0.5f);
                var emptyLayout = emptyText.gameObject.AddComponent<LayoutElement>();
                emptyLayout.preferredHeight = 60;
                emptyLayout.flexibleWidth = 1;
                return;
            }

            if (_employeeGroupByProperty)
            {
                // Group by property, collapsible — employees directly in each group (no type sub-groups)
                var byProp = new Dictionary<Property, List<Employee>>();
                foreach (var (emp, prop) in allEmployees)
                {
                    if (!byProp.ContainsKey(prop))
                        byProp[prop] = new List<Employee>();
                    byProp[prop].Add(emp);
                }

                var sortedProps = byProp.Keys.ToList();
                sortedProps.Sort((a, b) => string.Compare(
                    a.PropertyName ?? a.PropertyCode,
                    b.PropertyName ?? b.PropertyCode,
                    StringComparison.OrdinalIgnoreCase));

                foreach (var prop in sortedProps)
                {
                    string propCode = prop.PropertyCode;
                    string propName = prop.PropertyName ?? propCode;
                    var employees = byProp[prop];

                    if (!_employeePropertyExpanded.ContainsKey(propCode))
                        _employeePropertyExpanded[propCode] = true;
                    bool propExpanded = _employeePropertyExpanded[propCode];

                    // Collapsible property header
                    var propHeaderObj = UIFactory.Panel($"PropHeader_{propCode}", contentParent, new Color(0.10f, 0.22f, 0.24f));
                    propHeaderObj.AddComponent<LayoutElement>().preferredHeight = 32f;

                    string propArrow = propExpanded ? "\u25BC " : "\u25BA ";
                    var propLabel = TMPFactory.Text("PropName",
                        $"<b>{propArrow}{propName}</b>  <color=#AAAAAA><size=12>{employees.Count}</size></color>",
                        propHeaderObj.transform, 15, TextAlignmentOptions.Left);
                    propLabel.color = Color.white;
                    propLabel.richText = true;
                    var propLabelRect = propLabel.gameObject.GetComponent<RectTransform>();
                    propLabelRect.anchorMin = Vector2.zero;
                    propLabelRect.anchorMax = Vector2.one;
                    propLabelRect.offsetMin = new Vector2(10, 0);
                    propLabelRect.offsetMax = Vector2.zero;

                    // Property content container
                    var propContent = UIFactory.Panel($"PropContent_{propCode}", contentParent, Color.clear);
                    var propVLayout = propContent.AddComponent<VerticalLayoutGroup>();
                    propVLayout.childControlHeight = true;
                    propVLayout.childControlWidth = true;
                    propVLayout.childForceExpandHeight = false;
                    propVLayout.childForceExpandWidth = true;
                    propVLayout.spacing = 3;
                    var propSizeFitter = propContent.AddComponent<ContentSizeFitter>();
                    propSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                    propContent.SetActive(propExpanded);

                    // Wire toggle
                    var propBtn = propHeaderObj.AddComponent<Button>();
                    var propLabelCaptured = propLabel;
                    var propContentCaptured = propContent;
                    string propCodeCaptured = propCode;
                    int propCount = employees.Count;
                    string propNameCaptured = propName;
                    propBtn.onClick.AddListener(new Action(() =>
                    {
                        bool nowExpanded = !_employeePropertyExpanded[propCodeCaptured];
                        _employeePropertyExpanded[propCodeCaptured] = nowExpanded;
                        propContentCaptured.SetActive(nowExpanded);
                        string arrow = nowExpanded ? "\u25BC " : "\u25BA ";
                        propLabelCaptured.text = $"<b>{arrow}{propNameCaptured}</b>  <color=#AAAAAA><size=12>{propCount}</size></color>";
                    }));

                    foreach (var emp in employees)
                        CreateEmployeeCard(propContent.transform, emp, propName);
                }
            }
            else
            {
                // Flat list sorted by name
                allEmployees.Sort((a, b) => string.Compare(
                    $"{a.emp.FirstName} {a.emp.LastName}",
                    $"{b.emp.FirstName} {b.emp.LastName}",
                    StringComparison.OrdinalIgnoreCase));

                foreach (var (emp, prop) in allEmployees)
                    CreateEmployeeCard(contentParent, emp, prop.PropertyName ?? prop.PropertyCode);
            }
        }

        private void CreateEmployeeCard(Transform parent, Employee emp, string propName)
        {
            ScheduleOne.NPCs.NPCInventory npcInventory = null;
            try { npcInventory = emp.GetComponent<ScheduleOne.NPCs.NPCInventory>(); }
            catch { }

            int displaySlots = 5;
            if (npcInventory?.ItemSlots != null)
                displaySlots = Math.Max(5, npcInventory.ItemSlots.Count);

            float cardHeight = CardHeight(displaySlots);

            string empId = "";
            try { empId = emp.ID ?? ""; } catch { }

            var cardObj = UIFactory.Panel($"EmpCard_{empId}", parent, new Color(0.18f, 0.18f, 0.18f));
            var cardLayout = cardObj.AddComponent<LayoutElement>();
            cardLayout.preferredHeight = cardHeight;
            cardLayout.flexibleWidth = 1;

            // ── Mugshot ──
            var mugFrame = UIFactory.Panel("MugFrame", cardObj.transform, new Color(0.45f, 0.50f, 0.52f));
            var mugFrameRect = mugFrame.GetComponent<RectTransform>();
            mugFrameRect.anchorMin = new Vector2(0, 1);
            mugFrameRect.anchorMax = new Vector2(0, 1);
            mugFrameRect.pivot = new Vector2(0, 1);
            mugFrameRect.anchoredPosition = new Vector2(8, -8);
            mugFrameRect.sizeDelta = new Vector2(69, 69);

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
                    var mugImage = mugPanel.GetComponent<Image>();
                    mugImage.sprite = mugSprite;
                    mugImage.color = Color.white;
                }
            }
            catch { }

            // ── Left: Name ──
            string firstName = "Employee";
            string lastName = "";
            try
            {
                firstName = emp.FirstName ?? "Employee";
                lastName = emp.LastName ?? "";
            }
            catch { }

            var nameText = TMPFactory.Text("Name", $"<b>{firstName} {lastName}</b>", cardObj.transform, 15, TextAlignmentOptions.Left);
            nameText.color = Color.white;
            var nameRect = nameText.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 1);
            nameRect.anchorMax = new Vector2(0.25f, 1);
            nameRect.pivot = new Vector2(0, 1);
            nameRect.anchoredPosition = new Vector2(92, -10);
            nameRect.sizeDelta = new Vector2(0, 20);

            // ── Left: Property + Locker Cash ──
            string lockerLine = propName;
            try
            {
                var home = emp.GetHome();
                if (home != null)
                {
                    float cash = home.GetCashSum();
                    lockerLine = $"{propName} - <color=#66BF4D>${cash:N0}</color>";
                }
            }
            catch { }

            var lockerText = TMPFactory.Text("LockerLine", lockerLine, cardObj.transform, 15, TextAlignmentOptions.Left);
            lockerText.color = new Color(0.55f, 0.55f, 0.55f);
            lockerText.richText = true;
            var lockerRect = lockerText.gameObject.GetComponent<RectTransform>();
            lockerRect.anchorMin = new Vector2(0, 1);
            lockerRect.anchorMax = new Vector2(0.25f, 1);
            lockerRect.pivot = new Vector2(0, 1);
            lockerRect.anchoredPosition = new Vector2(92, -32);
            lockerRect.sizeDelta = new Vector2(0, 18);

            // ── Left: Status ──
            var (statusStr, statusColor) = GetEmployeeStatus(emp);
            var statusText = TMPFactory.Text("Status", statusStr, cardObj.transform, 15, TextAlignmentOptions.Left);
            statusText.color = statusColor;
            var statusRect = statusText.gameObject.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0, 1);
            statusRect.anchorMax = new Vector2(0.25f, 1);
            statusRect.pivot = new Vector2(0, 1);
            statusRect.anchoredPosition = new Vector2(92, -52);
            statusRect.sizeDelta = new Vector2(0, 18);

            // ── Center: Inventory Slots Grid ──
            var invPanel = UIFactory.Panel("Inventory", cardObj.transform, Color.clear);
            var invRect = invPanel.GetComponent<RectTransform>();
            invRect.anchorMin = new Vector2(0.40f, 1);
            invRect.anchorMax = new Vector2(0.72f, 1);
            invRect.pivot = new Vector2(0, 1);
            invRect.anchoredPosition = new Vector2(0, -6);
            int invRows = Math.Max(1, (displaySlots + SLOTS_PER_ROW - 1) / SLOTS_PER_ROW);
            float invGridHeight = invRows * SLOT_SIZE + Math.Max(0, invRows - 1) * SLOT_GAP;
            invRect.sizeDelta = new Vector2(0, invGridHeight);

            var grid = invPanel.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(SLOT_SIZE, SLOT_SIZE);
            grid.spacing = new Vector2(SLOT_GAP, SLOT_GAP);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = SLOTS_PER_ROW;
            grid.childAlignment = TextAnchor.UpperCenter;

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

            // ── Right: Chevron → employee detail ──
            var empRef = emp;
            if (_chevronSprite == null) _chevronSprite = LoadIconResource("ChevronIcon");

            var (chevMask, chevBtn, chevLabel) = TMPFactory.RoundedButtonWithLabel(
                "DetailBtn", "\u203A", cardObj.transform,
                new Color(0.15f, 0.35f, 0.45f), 65, 65, 8, Color.white);
            var chevRect = chevMask.GetComponent<RectTransform>();
            chevRect.anchorMin = new Vector2(0.96f, 1);
            chevRect.anchorMax = new Vector2(0.96f, 1);
            chevRect.pivot = new Vector2(0.5f, 1);
            chevRect.anchoredPosition = new Vector2(0, -10);
            chevLabel.gameObject.SetActive(false);

            if (_chevronSprite != null)
            {
                var chevIcon = new GameObject("ChevronIcon");
                chevIcon.transform.SetParent(chevBtn.transform, false);
                var chevIconImg = chevIcon.AddComponent<Image>();
                chevIconImg.sprite = _chevronSprite;
                chevIconImg.preserveAspect = true;
                var chevIconRect = chevIcon.GetComponent<RectTransform>();
                chevIconRect.anchorMin = new Vector2(0.15f, 0.15f);
                chevIconRect.anchorMax = new Vector2(0.85f, 0.85f);
                chevIconRect.offsetMin = Vector2.zero;
                chevIconRect.offsetMax = Vector2.zero;
            }

            chevBtn.onClick.AddListener(new Action(() => ShowEmployeeDetail(empRef)));
        }
    }
}
