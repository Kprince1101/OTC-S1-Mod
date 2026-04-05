using OverTheCounter.SaveData;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using S1API.UI;
using System;
using System.Collections.Generic;
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
        //  Left navigation bar (OVERVIEW, SALES, INVENTORY, EMPLOYEES, CUSTOMIZE)
        // ==================================================================

        private readonly List<NavTabRef> _navTabs = new();

        private struct NavTabRef
        {
            public AppTab Tab;
            public Image OuterImage;
            public Image InnerImage;
            public Image IconImage;
            public TextMeshProUGUI Label;
        }

        private void BuildNavBar(Transform parent)
        {
            _navTabs.Clear();

            var nav = UIFactory.Panel("NavBar", parent, NavBg);
            var navRect = nav.GetComponent<RectTransform>();
            navRect.anchorMin = Vector2.zero;
            navRect.anchorMax = new Vector2(NAV_WIDTH_FRAC, 1f);
            navRect.offsetMin = Vector2.zero;
            navRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            // VerticalLayoutGroup for automatic square-button stacking
            var vlg = nav.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4f;
            vlg.padding = new RectOffset(20, 20, 8, 0);
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childAlignment = TextAnchor.UpperCenter;

            string[] tabs = { "HOME", "SALES", "INVENTORY", "EMPLOYEES", "CUSTOMIZE" };
            float borderW = 2f;

            for (int i = 0; i < tabs.Length; i++)
            {
                var appTab = NavTabOrder[i];
                bool isActive = appTab == _activeTab;

                // Outer container — rounded, green border for active
                var tabOuter = RoundedPanel($"NavTab_{tabs[i]}", nav.transform,
                    isActive ? AccentGreen : Color.clear);
                var outerImg = tabOuter.GetComponent<Image>();

                // AspectRatioFitter keeps buttons compact (slightly taller than wide)
                var arf = tabOuter.AddComponent<AspectRatioFitter>();
                arf.aspectMode = AspectRatioFitter.AspectMode.WidthControlsHeight;
                arf.aspectRatio = 1.25f;

                // Inner fill — rounded
                Color innerBg = isActive ? new Color(0.06f, 0.12f, 0.06f) : Color.clear;
                var tabInner = RoundedPanel($"NavInner_{tabs[i]}", tabOuter.transform, innerBg);
                var innerRect = tabInner.GetComponent<RectTransform>();
                innerRect.anchorMin = Vector2.zero;
                innerRect.anchorMax = Vector2.one;
                innerRect.offsetMin = isActive ? new Vector2(borderW, borderW) : Vector2.zero;
                innerRect.offsetMax = isActive ? new Vector2(-borderW, -borderW) : Vector2.zero;
                var innerImg = tabInner.GetComponent<Image>();

                // Icon — centered in upper portion
                Image iconImg = null;
                var iconSprite = LoadIcon(NavIconNames[i]);
                if (iconSprite != null)
                {
                    var iconGo = new GameObject($"NavIcon_{tabs[i]}");
                    iconGo.transform.SetParent(tabInner.transform, false);
                    iconImg = iconGo.AddComponent<Image>();
                    iconImg.sprite = iconSprite;
                    iconImg.preserveAspect = true;
                    iconImg.color = isActive ? AccentGreen : TextDim;
                    var iconRect = iconGo.GetComponent<RectTransform>();
                    iconRect.anchorMin = new Vector2(0.30f, 0.38f);
                    iconRect.anchorMax = new Vector2(0.70f, 0.82f);
                    iconRect.offsetMin = Vector2.zero;
                    iconRect.offsetMax = Vector2.zero;
                }

                // Label — small text at bottom
                var label = TMPFactory.Text($"NavLabel_{tabs[i]}", tabs[i],
                    tabInner.transform, 15, TextAlignmentOptions.Bottom,
                    isActive ? FontStyles.Bold : FontStyles.Normal);
                label.color = isActive ? AccentGreen : TextDim;
                label.enableAutoSizing = true;
                label.fontSizeMin = 8;
                label.fontSizeMax = 14;
                var labelRect = label.gameObject.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = new Vector2(1, 0.36f);
                labelRect.offsetMin = new Vector2(2, 1);
                labelRect.offsetMax = new Vector2(-2, 0);

                // Click handler
                var tabBtn = tabOuter.AddComponent<Button>();
                tabBtn.targetGraphic = outerImg;
                var capturedTab = appTab;
                tabBtn.onClick.AddListener(new Action(() => SwitchTab(capturedTab)));

                _navTabs.Add(new NavTabRef
                {
                    Tab = appTab,
                    OuterImage = outerImg,
                    InnerImage = innerImg,
                    IconImage = iconImg,
                    Label = label
                });
            }
        }

        /// <summary>Updates nav bar visuals to reflect the active tab.</summary>
        private void UpdateNavVisuals()
        {
            float borderW = 2f;
            for (int i = 0; i < _navTabs.Count; i++)
            {
                var navTab = _navTabs[i];
                bool isActive = navTab.Tab == _activeTab;

                if (navTab.OuterImage != null)
                    navTab.OuterImage.color = isActive ? AccentGreen : Color.clear;

                if (navTab.InnerImage != null)
                {
                    navTab.InnerImage.color = isActive ? new Color(0.06f, 0.12f, 0.06f) : Color.clear;
                    var innerRect = navTab.InnerImage.GetComponent<RectTransform>();
                    innerRect.offsetMin = isActive ? new Vector2(borderW, borderW) : Vector2.zero;
                    innerRect.offsetMax = isActive ? new Vector2(-borderW, -borderW) : Vector2.zero;
                }

                if (navTab.IconImage != null)
                    navTab.IconImage.color = isActive ? AccentGreen : TextDim;

                if (navTab.Label != null)
                {
                    navTab.Label.color = isActive ? AccentGreen : TextDim;
                    navTab.Label.fontStyle = isActive ? FontStyles.Bold : FontStyles.Normal;
                }
            }

            // Show logo underline only when Overview is active
            if (_logoUnderline != null)
                _logoUnderline.SetActive(_activeTab == AppTab.Overview);
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
                sidebar.transform, 15, TextAlignmentOptions.TopLeft);
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

            // Logo + title — clickable, navigates to Overview
            var logoBtn = UIFactory.Panel("LogoButton", topBar.transform, Color.clear);
            var logoBtnRect = logoBtn.GetComponent<RectTransform>();
            logoBtnRect.anchorMin = Vector2.zero;
            logoBtnRect.anchorMax = new Vector2(0.4f, 1);
            logoBtnRect.offsetMin = Vector2.zero;
            logoBtnRect.offsetMax = Vector2.zero;

            float titleLeft = 12f;
            var logoSprite = LoadIcon("GreenTabLogo");
            if (logoSprite != null)
            {
                var logoGo = new GameObject("TopBarLogo");
                logoGo.transform.SetParent(logoBtn.transform, false);
                var logoImg = logoGo.AddComponent<Image>();
                logoImg.sprite = logoSprite;
                logoImg.preserveAspect = true;
                logoImg.color = AccentGreen;
                logoImg.raycastTarget = false;
                var logoRect = logoGo.GetComponent<RectTransform>();
                logoRect.anchorMin = new Vector2(0, 0.08f);
                logoRect.anchorMax = new Vector2(0, 0.92f);
                logoRect.pivot = new Vector2(0, 0.5f);
                logoRect.sizeDelta = new Vector2(32, 0);
                logoRect.anchoredPosition = new Vector2(8, 0);
                titleLeft = 46f;
            }

            // "GreenTab POS" title
            var title = TMPFactory.Text("TopBarTitle", "<b>GreenTab POS</b>",
                logoBtn.transform, 15, TextAlignmentOptions.Left, FontStyles.Bold);
            title.color = AccentGreen;
            title.raycastTarget = false;
            _logoTitleGraphic = title;
            var titleRect = title.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = Vector2.one;
            titleRect.offsetMin = new Vector2(titleLeft, 0);
            titleRect.offsetMax = Vector2.zero;

            var logoBtnComp = logoBtn.AddComponent<Button>();
            logoBtnComp.targetGraphic = logoBtn.GetComponent<Image>();
            logoBtnComp.onClick.AddListener(new Action(() => SwitchTab(AppTab.Overview)));

            // Green underline — visible when Overview tab is active
            _logoUnderline = UIFactory.Panel("LogoUnderline", logoBtn.transform, AccentGreen);
            _logoUnderlineImage = _logoUnderline.GetComponent<Image>();
            var ulRect = _logoUnderline.GetComponent<RectTransform>();
            ulRect.anchorMin = new Vector2(0, 0);
            ulRect.anchorMax = new Vector2(0, 0);
            ulRect.pivot = new Vector2(0.5f, 0);
            ulRect.sizeDelta = new Vector2(130, 2);
            ulRect.anchoredPosition = new Vector2(80, 4);
            _logoUnderline.SetActive(_activeTab == AppTab.Overview);

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
                GetBuildingDisplayName(_selectedBuildingId) + " \u25BC",
                propBtn.transform, 15, TextAlignmentOptions.Center);
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

            var buildings = GetOwnedBuildings();
            if (buildings.Count == 0) return;

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
            dpRect.anchorMin = new Vector2(1, 1);
            dpRect.anchorMax = new Vector2(1, 1);
            dpRect.pivot = new Vector2(1, 1);
            float rowHeight = 28f;
            int totalRows = buildings.Count + 1; // +1 for "All Properties"
            float panelHeight = totalRows * rowHeight + 6;
            dpRect.sizeDelta = new Vector2(155, panelHeight);
            dpRect.anchoredPosition = new Vector2(-8, -(HEADER_HEIGHT + 2));

            var outline = _dropdownPanel.AddComponent<Outline>();
            outline.effectColor = new Color(0.25f, 0.25f, 0.25f);
            outline.effectDistance = new Vector2(1, -1);

            // "All Properties" row at top
            var allRow = RoundedPanel("DropdownRow_All", _dropdownPanel.transform,
                _selectedBuildingId == AllPropertiesId ? CardSelected : Color.clear);
            var allRowRect = allRow.GetComponent<RectTransform>();
            allRowRect.anchorMin = new Vector2(0, 1);
            allRowRect.anchorMax = new Vector2(1, 1);
            allRowRect.pivot = new Vector2(0.5f, 1);
            allRowRect.sizeDelta = new Vector2(0, rowHeight);
            allRowRect.anchoredPosition = new Vector2(0, -3);

            var allLabel = TMPFactory.Text("DropdownLabel_All", "All Properties",
                allRow.transform, 15, TextAlignmentOptions.Center);
            allLabel.color = new Color(0.88f, 0.88f, 0.88f);
            var allLabelRect = allLabel.gameObject.GetComponent<RectTransform>();
            allLabelRect.anchorMin = Vector2.zero;
            allLabelRect.anchorMax = Vector2.one;
            allLabelRect.offsetMin = new Vector2(6, 0);
            allLabelRect.offsetMax = new Vector2(-6, 0);

            var allBtn = allRow.AddComponent<Button>();
            allBtn.targetGraphic = allRow.GetComponent<Image>();
            allBtn.onClick.AddListener(new Action(() => SelectBuilding(AllPropertiesId)));

            // Individual building rows
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
                rowRect.anchoredPosition = new Vector2(0, -(3 + (i + 1) * rowHeight));

                var label = TMPFactory.Text($"DropdownLabel_{i}",
                    GetBuildingDisplayName(bid),
                    row.transform, 15, TextAlignmentOptions.Center);
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
            _homeEditMode = false;

            if (_propertyDropdownText != null)
                _propertyDropdownText.text = GetBuildingDisplayName(_selectedBuildingId) + " \u25BC";

            HideDropdown();

            // Refresh active tab with new building context
            SwitchTab(_activeTab);
        }

        private void RefreshDropdownText()
        {
            if (_propertyDropdownText != null)
                _propertyDropdownText.text = GetBuildingDisplayName(_selectedBuildingId) + " \u25BC";
        }
    }
}
