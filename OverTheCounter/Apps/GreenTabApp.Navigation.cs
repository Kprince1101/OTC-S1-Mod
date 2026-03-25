using OverTheCounter.UI;
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
        //  Left navigation bar (SALES, INVENTORY, EMPLOYEES, CUSTOMIZE)
        // ==================================================================

        private void BuildNavBar(Transform parent)
        {
            var nav = UIFactory.Panel("NavBar", parent, NavBg);
            var navRect = nav.GetComponent<RectTransform>();
            navRect.anchorMin = Vector2.zero;
            navRect.anchorMax = new Vector2(NAV_WIDTH_FRAC, 1f);
            navRect.offsetMin = Vector2.zero;
            navRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            string[] tabs = { "SALES", "INVENTORY", "EMPLOYEES", "CUSTOMIZE" };
            float btnSize = 66f;
            float gap = 6f;
            float borderW = 2f;
            float padTop = 8f;

            for (int i = 0; i < tabs.Length; i++)
            {
                bool isActive = tabs[i] == "CUSTOMIZE";

                // Float to top
                float yPos = -(padTop + i * (btnSize + gap));

                // Outer container — rounded, green border for active
                var tabOuter = RoundedPanel($"NavTab_{tabs[i]}", nav.transform,
                    isActive ? AccentGreen : Color.clear);
                var outerRect = tabOuter.GetComponent<RectTransform>();
                outerRect.anchorMin = new Vector2(0.5f, 1);
                outerRect.anchorMax = new Vector2(0.5f, 1);
                outerRect.pivot = new Vector2(0.5f, 1);
                outerRect.sizeDelta = new Vector2(btnSize, btnSize);
                outerRect.anchoredPosition = new Vector2(0, yPos);

                // Inner fill — rounded
                Color innerBg = isActive ? new Color(0.06f, 0.12f, 0.06f) : Color.clear;
                var tabInner = RoundedPanel($"NavInner_{tabs[i]}", tabOuter.transform, innerBg);
                var innerRect = tabInner.GetComponent<RectTransform>();
                innerRect.anchorMin = Vector2.zero;
                innerRect.anchorMax = Vector2.one;
                innerRect.offsetMin = isActive ? new Vector2(borderW, borderW) : Vector2.zero;
                innerRect.offsetMax = isActive ? new Vector2(-borderW, -borderW) : Vector2.zero;

                // Icon — centered in upper portion, ~half button width
                var iconSprite = LoadIcon(NavIconNames[i]);
                if (iconSprite != null)
                {
                    var iconGo = new GameObject($"NavIcon_{tabs[i]}");
                    iconGo.transform.SetParent(tabInner.transform, false);
                    var iconImg = iconGo.AddComponent<Image>();
                    iconImg.sprite = iconSprite;
                    iconImg.preserveAspect = true;
                    iconImg.color = isActive ? AccentGreen : TextDim;
                    var iconRect = iconGo.GetComponent<RectTransform>();
                    iconRect.anchorMin = new Vector2(0.28f, 0.38f);
                    iconRect.anchorMax = new Vector2(0.72f, 0.82f);
                    iconRect.offsetMin = Vector2.zero;
                    iconRect.offsetMax = Vector2.zero;
                }

                // Label — small text at bottom
                var label = TMPFactory.Text($"NavLabel_{tabs[i]}", tabs[i],
                    tabInner.transform, 8, TextAlignmentOptions.Bottom,
                    isActive ? FontStyles.Bold : FontStyles.Normal);
                label.color = isActive ? AccentGreen : TextDim;
                var labelRect = label.gameObject.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = new Vector2(1, 0.34f);
                labelRect.offsetMin = new Vector2(2, 3);
                labelRect.offsetMax = new Vector2(-2, 0);
            }
        }

        // ==================================================================
        //  Category sidebar (Checkout Desk, Walls, Flooring, Lighting)
        // ==================================================================

        private void BuildSidebar(Transform parent)
        {
            _sidebarParent = parent;
            if (_sidebarPanel != null) UnityEngine.Object.Destroy(_sidebarPanel);

            var sidebar = UIFactory.Panel("Sidebar", parent, SidebarBg);
            _sidebarPanel = sidebar;
            var sbRect = sidebar.GetComponent<RectTransform>();
            sbRect.anchorMin = new Vector2(NAV_WIDTH_FRAC, 0);
            sbRect.anchorMax = new Vector2(NAV_WIDTH_FRAC + SIDEBAR_WIDTH_FRAC, 1f);
            sbRect.offsetMin = Vector2.zero;
            sbRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            // Sidebar title
            var title = TMPFactory.Text("SidebarTitle", "<b>CUSTOMIZATION</b>",
                sidebar.transform, 13, TextAlignmentOptions.TopLeft);
            title.color = TextMuted;
            var titleRect = title.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = Vector2.one;
            titleRect.pivot = new Vector2(0.5f, 1);
            titleRect.sizeDelta = new Vector2(0, 28);
            titleRect.anchoredPosition = Vector2.zero;
            var titleOff = titleRect;
            titleOff.offsetMin = new Vector2(10, 0);
            titleOff.offsetMax = new Vector2(-4, -6);

            // Category pills
            float pillH = 42f;
            float pillGap = 6f;
            float padSide = 8f;

            for (int i = 0; i < Categories.Length; i++)
            {
                var (cat, catLabel, locked) = Categories[i];
                bool active = cat == _activeCategory;

                float yPos = -(34 + i * (pillH + pillGap));

                // Rounded pill background
                Color pillColor = active ? AccentGreen : new Color(AccentGreen.r, AccentGreen.g, AccentGreen.b, 0.18f);
                if (locked && !active) pillColor = new Color(AccentGreen.r, AccentGreen.g, AccentGreen.b, 0.08f);

                var pill = RoundedPanel($"Cat_{cat}", sidebar.transform, pillColor);
                var pillRect = pill.GetComponent<RectTransform>();
                pillRect.anchorMin = new Vector2(0, 1);
                pillRect.anchorMax = new Vector2(1, 1);
                pillRect.pivot = new Vector2(0.5f, 1);
                pillRect.sizeDelta = new Vector2(-padSide * 2, pillH);
                pillRect.anchoredPosition = new Vector2(0, yPos);

                // Category icon — left side inside pill
                var catIconSprite = LoadIcon(CatIconNames[i]);
                if (catIconSprite != null)
                {
                    var catIconGo = new GameObject($"CatIcon_{cat}");
                    catIconGo.transform.SetParent(pill.transform, false);
                    var catIconImg = catIconGo.AddComponent<Image>();
                    catIconImg.sprite = catIconSprite;
                    catIconImg.preserveAspect = true;
                    catIconImg.color = active ? Color.white : (locked ? TextDim : AccentGreen);
                    var catIconRect = catIconGo.GetComponent<RectTransform>();
                    catIconRect.anchorMin = new Vector2(0, 0.2f);
                    catIconRect.anchorMax = new Vector2(0, 0.8f);
                    catIconRect.pivot = new Vector2(0, 0.5f);
                    catIconRect.sizeDelta = new Vector2(20, 0);
                    catIconRect.anchoredPosition = new Vector2(12, 0);
                }

                // Label text — next to icon
                float textLeft = catIconSprite != null ? 38f : 12f;
                var catText = TMPFactory.Text($"CatLabel_{cat}", catLabel,
                    pill.transform, 15, TextAlignmentOptions.Left,
                    active ? FontStyles.Bold : FontStyles.Normal);
                catText.color = active ? Color.white : (locked ? TextDim : AccentGreen);
                var catTextRect = catText.gameObject.GetComponent<RectTransform>();
                catTextRect.anchorMin = Vector2.zero;
                catTextRect.anchorMax = Vector2.one;
                catTextRect.offsetMin = new Vector2(textLeft, 0);
                catTextRect.offsetMax = new Vector2(-30, 0);

                // Lock icon on right side
                if (locked)
                {
                    var lockSprite = LoadIcon("LockIcon");
                    if (lockSprite != null)
                    {
                        var lockIconGo = new GameObject($"CatLockIcon_{cat}");
                        lockIconGo.transform.SetParent(pill.transform, false);
                        var lockIconImg = lockIconGo.AddComponent<Image>();
                        lockIconImg.sprite = lockSprite;
                        lockIconImg.preserveAspect = true;
                        lockIconImg.color = active ? new Color(1f, 1f, 1f, 0.5f) : TextDim;
                        var lockIconRect = lockIconGo.GetComponent<RectTransform>();
                        lockIconRect.anchorMin = new Vector2(1, 0.25f);
                        lockIconRect.anchorMax = new Vector2(1, 0.75f);
                        lockIconRect.pivot = new Vector2(1, 0.5f);
                        lockIconRect.sizeDelta = new Vector2(16, 0);
                        lockIconRect.anchoredPosition = new Vector2(-10, 0);
                    }
                }

                // Click handler for unlocked categories
                if (!locked)
                {
                    var pillBtn = pill.AddComponent<Button>();
                    pillBtn.targetGraphic = pill.GetComponent<Image>();
                    var capturedCat = cat;
                    pillBtn.onClick.AddListener(new Action(() => SelectCategory(capturedCat)));
                }
            }
        }

        private void SelectCategory(Category cat)
        {
            if (cat == _activeCategory) return;
            _activeCategory = cat;

            // Rebuild sidebar to update pill highlight
            if (_sidebarParent != null)
                BuildSidebar(_sidebarParent);

            RefreshCards();
        }

        // ==================================================================
        //  Full-width top bar (logo + title + property dropdown)
        // ==================================================================

        private void BuildTopBar(Transform parent)
        {
            var topBar = UIFactory.Panel("TopBar", parent, TopBarBg);
            var topRect = topBar.GetComponent<RectTransform>();
            topRect.anchorMin = new Vector2(0, 1);
            topRect.anchorMax = Vector2.one;
            topRect.pivot = new Vector2(0.5f, 1);
            topRect.sizeDelta = new Vector2(0, HEADER_HEIGHT);
            topRect.anchoredPosition = Vector2.zero;

            // Logo icon
            float titleLeft = 12f;
            var logoSprite = LoadIcon("GreenTabLogo");
            if (logoSprite != null)
            {
                var logoGo = new GameObject("TopBarLogo");
                logoGo.transform.SetParent(topBar.transform, false);
                var logoImg = logoGo.AddComponent<Image>();
                logoImg.sprite = logoSprite;
                logoImg.preserveAspect = true;
                logoImg.color = AccentGreen;
                var logoRect = logoGo.GetComponent<RectTransform>();
                logoRect.anchorMin = new Vector2(0, 0.15f);
                logoRect.anchorMax = new Vector2(0, 0.85f);
                logoRect.pivot = new Vector2(0, 0.5f);
                logoRect.sizeDelta = new Vector2(22, 0);
                logoRect.anchoredPosition = new Vector2(12, 0);
                titleLeft = 40f;
            }

            // "GreenTab POS" title
            var title = TMPFactory.Text("TopBarTitle", "<b>GreenTab POS</b>",
                topBar.transform, 15, TextAlignmentOptions.Left, FontStyles.Bold);
            title.color = AccentGreen;
            var titleRect = title.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = new Vector2(0.5f, 1);
            titleRect.offsetMin = new Vector2(titleLeft, 0);
            titleRect.offsetMax = Vector2.zero;

            // Property selector (right side) — subtle rounded box
            var propBorder = RoundedPanel("PropBorder", topBar.transform,
                new Color(0.22f, 0.22f, 0.22f));
            var propBorderRect = propBorder.GetComponent<RectTransform>();
            propBorderRect.anchorMin = new Vector2(1, 0.15f);
            propBorderRect.anchorMax = new Vector2(1, 0.85f);
            propBorderRect.pivot = new Vector2(1, 0.5f);
            propBorderRect.sizeDelta = new Vector2(150, 0);
            propBorderRect.anchoredPosition = new Vector2(-10, 0);

            var propBtn = RoundedPanel("PropertyDropdown", propBorder.transform,
                new Color(0.12f, 0.12f, 0.12f, 0.95f));
            var propRect = propBtn.GetComponent<RectTransform>();
            propRect.anchorMin = Vector2.zero;
            propRect.anchorMax = Vector2.one;
            propRect.offsetMin = new Vector2(1, 1);
            propRect.offsetMax = new Vector2(-1, -1);

            _propertyDropdownText = TMPFactory.Text("PropText",
                GetBuildingDisplayName(_selectedBuildingId) + " ▼",
                propBtn.transform, 12, TextAlignmentOptions.Center);
            _propertyDropdownText.color = new Color(0.88f, 0.88f, 0.88f);
            var propTextRect = _propertyDropdownText.gameObject.GetComponent<RectTransform>();
            propTextRect.anchorMin = Vector2.zero;
            propTextRect.anchorMax = Vector2.one;
            propTextRect.offsetMin = new Vector2(8, 0);
            propTextRect.offsetMax = new Vector2(-8, 0);

            var propBtnComp = propBorder.AddComponent<Button>();
            propBtnComp.targetGraphic = propBorder.GetComponent<Image>();
            propBtnComp.onClick.AddListener(new Action(ToggleDropdown));

            // Bottom divider — subtle neutral line
            var divider = UIFactory.Panel("TopBarDivider", topBar.transform,
                new Color(1f, 1f, 1f, 0.08f));
            var divRect = divider.GetComponent<RectTransform>();
            divRect.anchorMin = Vector2.zero;
            divRect.anchorMax = new Vector2(1, 0);
            divRect.pivot = new Vector2(0.5f, 0);
            divRect.sizeDelta = new Vector2(0, 1);
            divRect.anchoredPosition = Vector2.zero;
        }

        // ==================================================================
        //  Dropdown
        // ==================================================================

        private void ShowDropdown()
        {
            // Clean up previous
            if (_dropdownBlocker != null) UnityEngine.Object.Destroy(_dropdownBlocker);
            if (_dropdownPanel != null) UnityEngine.Object.Destroy(_dropdownPanel);

            var buildings = GetBuildingsWithCounters();
            if (buildings.Count <= 1) return;

            // Transparent full-screen blocker catches clicks outside the dropdown
            _dropdownBlocker = UIFactory.Panel("DropdownBlocker", _rootPanel.transform,
                new Color(0, 0, 0, 0.01f), fullAnchor: true);
            _dropdownBlocker.transform.SetAsLastSibling();
            var blockerBtn = _dropdownBlocker.AddComponent<Button>();
            blockerBtn.targetGraphic = _dropdownBlocker.GetComponent<Image>();
            blockerBtn.onClick.AddListener(new Action(HideDropdown));

            // Dropdown panel — parented to root so it renders on top of everything
            _dropdownPanel = RoundedPanel("DropdownPanel", _rootPanel.transform, DropdownBg);
            _dropdownPanel.transform.SetAsLastSibling();
            var dpRect = _dropdownPanel.GetComponent<RectTransform>();
            // Position: top-right, just below header
            dpRect.anchorMin = new Vector2(1, 1);
            dpRect.anchorMax = new Vector2(1, 1);
            dpRect.pivot = new Vector2(1, 1);
            float rowHeight = 28f;
            float panelHeight = buildings.Count * rowHeight + 6;
            dpRect.sizeDelta = new Vector2(155, panelHeight);
            dpRect.anchoredPosition = new Vector2(-8, -(HEADER_HEIGHT + 2));

            var outline = _dropdownPanel.AddComponent<Outline>();
            outline.effectColor = new Color(0.25f, 0.25f, 0.25f);
            outline.effectDistance = new Vector2(1, -1);

            for (int i = 0; i < buildings.Count; i++)
            {
                var bid = buildings[i];
                var row = RoundedPanel($"DropdownRow_{i}", _dropdownPanel.transform,
                    bid == _selectedBuildingId ? CardSelected : Color.clear);
                var rowRect = row.GetComponent<RectTransform>();
                rowRect.anchorMin = new Vector2(0, 1);
                rowRect.anchorMax = new Vector2(1, 1);
                rowRect.pivot = new Vector2(0.5f, 1);
                rowRect.sizeDelta = new Vector2(0, rowHeight);
                rowRect.anchoredPosition = new Vector2(0, -(3 + i * rowHeight));

                var label = TMPFactory.Text($"DropdownLabel_{i}",
                    GetBuildingDisplayName(bid),
                    row.transform, 11, TextAlignmentOptions.Center);
                label.color = new Color(0.88f, 0.88f, 0.88f);
                var labelRect = label.gameObject.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = new Vector2(6, 0);
                labelRect.offsetMax = new Vector2(-6, 0);

                var rowBtn = row.AddComponent<Button>();
                rowBtn.targetGraphic = row.GetComponent<Image>();
                var capturedBid = bid;
                rowBtn.onClick.AddListener(new Action(() => SelectBuilding(capturedBid)));
            }

            _dropdownOpen = true;
        }

        private void HideDropdown()
        {
            if (_dropdownBlocker != null)
            {
                UnityEngine.Object.Destroy(_dropdownBlocker);
                _dropdownBlocker = null;
            }
            if (_dropdownPanel != null)
            {
                UnityEngine.Object.Destroy(_dropdownPanel);
                _dropdownPanel = null;
            }
            _dropdownOpen = false;
        }

        private void ToggleDropdown()
        {
            if (_dropdownOpen)
                HideDropdown();
            else
                ShowDropdown();
        }

        private void SelectBuilding(string buildingId)
        {
            _selectedBuildingId = buildingId;

            if (_propertyDropdownText != null)
                _propertyDropdownText.text = GetBuildingDisplayName(_selectedBuildingId) + " ▼";

            HideDropdown();
            RefreshCards();
        }
    }
}
