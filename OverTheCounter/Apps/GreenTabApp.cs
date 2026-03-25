using S1API.PhoneApp;
using S1API.UI;
using UnityEngine;
using UnityEngine.UI;
using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;
using MelonLoader.Utils;
using System.IO;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Money;
using Il2CppTMPro;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Money;
using TMPro;
#endif

namespace OverTheCounter.Apps
{
    /// <summary>
    /// GreenTab POS — phone app for dispensary customization.
    /// Horizontal orientation. Currently supports desk style upgrades per counter.
    /// </summary>
    public class GreenTabApp : PhoneApp
    {
        protected override string AppName => "GreenTabPOS";
        protected override string AppTitle => "GreenTab";
        protected override string IconLabel => "GreenTab";
        protected override EOrientation Orientation => EOrientation.Horizontal;

        protected override string IconFileName => null;
        protected override Sprite IconSprite => LoadIcon("GreenTabIcon");

        // ---- Layout constants ----
        private const float NAV_WIDTH_FRAC = 0.08f;
        private const float SIDEBAR_WIDTH_FRAC = 0.22f;
        private const float HEADER_HEIGHT = 36f;
        private const float FOOTER_HEIGHT = 52f;
        private const float CARD_WIDTH = 140f;
        private const float CARD_HEIGHT = 110f;
        private const int GRID_COLUMNS = 2;
        private const float CARD_GAP = 8f;

        // ---- Colors (dark neutral base, green accent used sparingly) ----
        private static readonly Color BgDark = new(0.07f, 0.07f, 0.07f);       // #121212 material dark base
        private static readonly Color NavBg = new(0.10f, 0.10f, 0.10f);        // #1A1A1A nav background
        private static readonly Color SidebarBg = new(0.12f, 0.12f, 0.12f);    // #1E1E1E sidebar surface
        private static readonly Color CardBg = new(0.15f, 0.15f, 0.15f);       // #252525 card surface
        private static readonly Color CardSelected = new(0.18f, 0.18f, 0.18f); // #2E2E2E selected card
        private static readonly Color AccentGreen = new(0.30f, 0.69f, 0.31f);  // #4CAF50 material green
        private static readonly Color AccentGreenSecondary = new(0.22f, 0.56f, 0.24f); // #388E3C darker green
        private static readonly Color AccentGreenDark = new(0.18f, 0.49f, 0.20f); // #2E7D32 button green
        private static readonly Color TextMuted = new(0.62f, 0.62f, 0.62f);    // #9E9E9E secondary text
        private static readonly Color TextDim = new(0.38f, 0.38f, 0.38f);      // #616161 disabled text
        private static readonly Color LockedOverlay = new(0.07f, 0.07f, 0.07f, 0.75f);
        private static readonly Color EquippedBadge = new(0.30f, 0.69f, 0.31f); // #4CAF50 green accent
        private static readonly Color FooterBg = new(0.07f, 0.07f, 0.07f);    // match BgDark
        private static readonly Color TopBarBg = new(0.09f, 0.09f, 0.09f);     // #171717 slight lift
        private static readonly Color DropdownBg = new(0.14f, 0.14f, 0.14f);   // #242424 dropdown surface

        // ---- Category sidebar ----
        private enum Category { CheckoutDesk, Walls, Flooring, Lighting }

        private static readonly (Category cat, string label, bool locked)[] Categories =
        {
            (Category.CheckoutDesk, "Checkout Desk", false),
            (Category.Walls, "Walls", true),
            (Category.Flooring, "Flooring", true),
            (Category.Lighting, "Lighting", false), // set true to gate behind unlock
        };

        // ---- State ----
        private Category _activeCategory = Category.CheckoutDesk;
        private string _selectedBuildingId;
        private string _pendingStyleId;
        private DeskStyle _pendingStyle;
        private string _pendingLightingId;

        // ---- UI refs ----
        private GameObject _rootPanel;
        private Transform _sidebarParent;
        private GameObject _sidebarPanel;
        private Transform _cardGrid;
        private TextMeshProUGUI _balanceText;
        private TextMeshProUGUI _propertyDropdownText;
        private Button _applyBtn;
        private TextMeshProUGUI _applyBtnText;
        private Image _applyBtnImage;
        private ScrollRect _cardScroll;
        private GameObject _dropdownPanel;
        private GameObject _dropdownBlocker;
        private bool _dropdownOpen;

        // Card tracking for refresh
        private readonly List<CardEntry> _cardEntries = new();

        private struct CardEntry
        {
            public string StyleId;
            public GameObject Card;
            public Image CardImage;
            public TextMeshProUGUI PriceText;
            public GameObject EquippedBadge;
            public GameObject LockOverlay;
            public Image SwatchImage;
        }

        // ---- Building display names ----
        private static readonly Dictionary<string, string> BuildingDisplayNames = new()
        {
            { PropertySaveData.ShackId, "Westville Shack" },
            { Dispensary.DispensaryId, "Big Dispensary" },
        };

        // ---- Desk style swatch colors (just colored squares, no previews) ----
        private static readonly Dictionary<string, Color> StyleSwatchColors = new()
        {
            { "ornate_desk", new Color(0.55f, 0.35f, 0.18f) },            // warm wood brown
            { "otc_dealership_desk", new Color(0.70f, 0.72f, 0.75f) },     // sleek silver
            { "otc_midnight_desk", new Color(0.12f, 0.12f, 0.15f) },       // noir black
            { "otc_glass_desk", new Color(0.45f, 0.65f, 0.75f) },          // glass blue
            { "otc_led_desk", new Color(0.20f, 0.85f, 0.65f) },            // LED cyan-green
        };

        // ---- Lighting style swatch colors ----
        private static readonly Dictionary<string, Color> LightingSwatchColors = new()
        {
            { "brass_pendant", new Color(0.83f, 0.68f, 0.21f) },        // warm brass gold
            { "fluorescent", new Color(0.85f, 0.85f, 0.95f) },          // cool white
            { "modern_panel", new Color(0.90f, 0.90f, 0.85f) },         // neutral white
            { "flush_mount", new Color(0.95f, 0.92f, 0.80f) },          // warm white
            { "neon_tech", new Color(0f, 0.8f, 1f) },                   // cyan neon
            { "industrial", new Color(1f, 0.85f, 0.55f) },              // warm brass
        };

        // ---- Nav tab icon names (order matches tabs array in BuildNavBar) ----
        private static readonly string[] NavIconNames =
            { "SalesIcon", "InventoryIcon", "EmployeesIcon", "CustomizeIcon" };

        // ---- Category icon names (order matches Categories array) ----
        private static readonly string[] CatIconNames =
            { "CatDeskIcon", "CatWallsIcon", "CatFlooringIcon", "CatLightingIcon" };

        // ---- Cached icon sprites ----
        private static readonly Dictionary<string, Sprite> _iconCache = new();

        /// <summary>Creates a panel with rounded corners using TMPFactory's 9-slice rounded sprite.</summary>
        private static GameObject RoundedPanel(string name, Transform parent, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            var img = go.AddComponent<Image>();
            img.sprite = TMPFactory.GetRoundedSprite();
            img.type = Image.Type.Sliced;
            img.color = color;
            return go;
        }

        /// <summary>Creates a vertical gradient texture (top color → bottom color).</summary>
        private static Sprite GetGradientSprite(Color top, Color bottom)
        {
            int w = 4, h = 32;
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false);
            for (int y = 0; y < h; y++)
            {
                var c = Color.Lerp(bottom, top, (float)y / (h - 1));
                for (int x = 0; x < w; x++)
                    tex.SetPixel(x, y, c);
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
        }

        /// <summary>Loads an icon from embedded resources in Resources/GreenTabPOS/.</summary>
        private static Sprite LoadIcon(string name)
        {
            if (_iconCache.TryGetValue(name, out var cached)) return cached;
            try
            {
                string resourceName = $"OverTheCounter.Resources.GreenTabPOS.{name}.png";
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream == null) return null;

                byte[] data = new byte[stream.Length];
                stream.Read(data, 0, data.Length);

                var tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, data)) return null;

                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                _iconCache[name] = sprite;
                return sprite;
            }
            catch { return null; }
        }

        // ==================================================================
        //  Lifecycle
        // ==================================================================

        protected override void OnCreatedUI(GameObject container)
        {
            _rootPanel = UIFactory.Panel("GreenTabRoot", container.transform, BgDark, fullAnchor: true);

            // Pick initial building — first one that has a counter
            _selectedBuildingId = GetBuildingsWithCounters().FirstOrDefault() ?? PropertySaveData.ShackId;

            BuildTopBar(_rootPanel.transform);
            BuildNavBar(_rootPanel.transform);
            BuildSidebar(_rootPanel.transform);
            BuildCardArea(_rootPanel.transform);
            BuildFooter(_rootPanel.transform);

            RefreshCards();
            RefreshFooter();

            // Balance shows $0 at creation time because MoneyManager isn't ready yet.
            // Poll until it's available, then refresh.
            MelonCoroutines.Start(RefreshWhenMoneyReady());
        }

        private IEnumerator RefreshWhenMoneyReady()
        {
            // MoneyManager.Instance may exist early but balance isn't populated yet.
            // Poll for a few seconds to catch when balance becomes available.
            for (int i = 0; i < 10; i++)
            {
                yield return new WaitForSeconds(1f);
                RefreshFooter();
                UpdateCardVisuals();
            }
        }

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
        //  Card grid area (scrollable)
        // ==================================================================

        private void BuildCardArea(Transform parent)
        {
            float contentLeft = NAV_WIDTH_FRAC + SIDEBAR_WIDTH_FRAC;

            // Scroll view container
            var scrollContainer = UIFactory.Panel("CardScrollContainer", parent, Color.clear);
            var scrollContRect = scrollContainer.GetComponent<RectTransform>();
            scrollContRect.anchorMin = new Vector2(contentLeft, 0);
            scrollContRect.anchorMax = Vector2.one;
            scrollContRect.offsetMin = new Vector2(0, FOOTER_HEIGHT);
            scrollContRect.offsetMax = new Vector2(0, -HEADER_HEIGHT); // below top bar

            // ScrollRect
            var scrollView = UIFactory.Panel("CardScrollView", scrollContainer.transform, Color.clear);
            var scrollViewRect = scrollView.GetComponent<RectTransform>();
            scrollViewRect.anchorMin = Vector2.zero;
            scrollViewRect.anchorMax = Vector2.one;
            scrollViewRect.offsetMin = Vector2.zero;
            scrollViewRect.offsetMax = Vector2.zero;

            _cardScroll = scrollView.AddComponent<ScrollRect>();
            _cardScroll.horizontal = false;
            _cardScroll.vertical = true;
            _cardScroll.movementType = ScrollRect.MovementType.Clamped;

            // Viewport (mask)
            var viewport = UIFactory.Panel("Viewport", scrollView.transform, Color.clear);
            var vpRect = viewport.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = Vector2.zero;
            viewport.AddComponent<RectMask2D>();

            // Content
            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;

            _cardScroll.viewport = vpRect;
            _cardScroll.content = contentRect;

            _cardGrid = content.transform;
        }

        // ==================================================================
        //  Footer bar (balance + apply button)
        // ==================================================================

        private void BuildFooter(Transform parent)
        {
            float contentLeft = NAV_WIDTH_FRAC + SIDEBAR_WIDTH_FRAC;

            var footer = UIFactory.Panel("Footer", parent, FooterBg);
            var footerRect = footer.GetComponent<RectTransform>();
            footerRect.anchorMin = new Vector2(contentLeft, 0);
            footerRect.anchorMax = new Vector2(1, 0);
            footerRect.pivot = new Vector2(0.5f, 0);
            footerRect.sizeDelta = new Vector2(0, FOOTER_HEIGHT);
            footerRect.anchoredPosition = Vector2.zero;

            // Unified rounded panel — balance (left) + apply button (right)
            var balPanel = RoundedPanel("BalPanel", footer.transform,
                new Color(0.12f, 0.12f, 0.12f));
            var balPanelRect = balPanel.GetComponent<RectTransform>();
            balPanelRect.anchorMin = Vector2.zero;
            balPanelRect.anchorMax = Vector2.one;
            balPanelRect.offsetMin = new Vector2(8, 5);
            balPanelRect.offsetMax = new Vector2(-8, -5);

            // Balance label — top-left inside panel
            var balLabel = TMPFactory.Text("BalLabel", "CURRENT BALANCE",
                balPanel.transform, 10, TextAlignmentOptions.Left);
            balLabel.color = TextMuted;
            var balLabelRect = balLabel.gameObject.GetComponent<RectTransform>();
            balLabelRect.anchorMin = new Vector2(0, 0.55f);
            balLabelRect.anchorMax = new Vector2(0.6f, 1);
            balLabelRect.offsetMin = new Vector2(14, 0);
            balLabelRect.offsetMax = new Vector2(0, -2);

            // Balance amount — bottom-left inside panel
            _balanceText = TMPFactory.Text("BalAmount", "$0",
                balPanel.transform, 22, TextAlignmentOptions.Left, FontStyles.Bold);
            _balanceText.color = AccentGreen;
            var balRect = _balanceText.gameObject.GetComponent<RectTransform>();
            balRect.anchorMin = Vector2.zero;
            balRect.anchorMax = new Vector2(0.6f, 0.60f);
            balRect.offsetMin = new Vector2(14, 0);
            balRect.offsetMax = Vector2.zero;

            // Apply button — right side inside panel
            var (applyMask, applyBtnComp, applyLabel) = TMPFactory.RoundedButtonWithLabel(
                "ApplyBtn", "APPLY", balPanel.transform,
                AccentGreenDark, 100, 30, 13, Color.white);
            var applyMaskRect = applyMask.GetComponent<RectTransform>();
            applyMaskRect.anchorMin = new Vector2(1, 0.5f);
            applyMaskRect.anchorMax = new Vector2(1, 0.5f);
            applyMaskRect.pivot = new Vector2(1, 0.5f);
            applyMaskRect.anchoredPosition = new Vector2(-10, 0);

            _applyBtn = applyBtnComp;
            _applyBtnText = applyLabel;
            _applyBtnImage = applyBtnComp.targetGraphic as Image;
            _applyBtn.onClick.AddListener(new Action(OnApplyClicked));
        }

        // ==================================================================
        //  Card creation and refresh
        // ==================================================================

        private void RefreshCards()
        {
            // Clear existing
            foreach (var entry in _cardEntries)
            {
                if (entry.Card != null)
                    UnityEngine.Object.Destroy(entry.Card);
            }
            _cardEntries.Clear();

            if (_activeCategory == Category.CheckoutDesk)
                RefreshDeskCards();
            else if (_activeCategory == Category.Lighting)
                RefreshLightingCards();

            RefreshFooter();
        }

        private void RefreshDeskCards()
        {
            var counter = GetSelectedCounter();
            string currentStyle = counter?.CurrentDeskStyleId ?? DeskStyle.Default.Id;
            _pendingStyleId = currentStyle;
            _pendingStyle = DeskStyle.Get(currentStyle);

            float balance = GetOnlineBalance();
            var styles = DeskStyle.All.Values.ToList();

            int rows = (styles.Count + GRID_COLUMNS - 1) / GRID_COLUMNS;
            float totalHeight = 10 + rows * (CARD_HEIGHT + CARD_GAP);
            var contentRect = _cardGrid.GetComponent<RectTransform>();
            contentRect.sizeDelta = new Vector2(0, totalHeight);

            for (int i = 0; i < styles.Count; i++)
            {
                var style = styles[i];
                int col = i % GRID_COLUMNS;
                int row = i / GRID_COLUMNS;

                bool isEquipped = style.Id == currentStyle;
                bool isSelected = style.Id == _pendingStyleId;
                bool canAfford = CanAffordUpgrade(currentStyle, style.Id, balance);

                float xPos = 10 + col * (CARD_WIDTH + CARD_GAP);
                float yPos = -(8 + row * (CARD_HEIGHT + CARD_GAP));

                var card = CreateDeskCard(style, isEquipped, isSelected, canAfford, xPos, yPos);
                _cardEntries.Add(card);
            }
        }

        private void RefreshLightingCards()
        {
            string currentLighting = Dispensary.CurrentLightingStyleId ?? LightingStyle.Default.Id;
            _pendingLightingId = currentLighting;

            float balance = GetOnlineBalance();
            var styles = LightingStyle.All.Values.ToList();

            int rows = (styles.Count + GRID_COLUMNS - 1) / GRID_COLUMNS;
            float totalHeight = 10 + rows * (CARD_HEIGHT + CARD_GAP);
            var contentRect = _cardGrid.GetComponent<RectTransform>();
            contentRect.sizeDelta = new Vector2(0, totalHeight);

            for (int i = 0; i < styles.Count; i++)
            {
                var style = styles[i];
                int col = i % GRID_COLUMNS;
                int row = i / GRID_COLUMNS;

                bool isEquipped = style.Id == currentLighting;
                bool isSelected = style.Id == _pendingLightingId;
                bool canAfford = CanAffordLighting(currentLighting, style.Id, balance);

                float xPos = 10 + col * (CARD_WIDTH + CARD_GAP);
                float yPos = -(8 + row * (CARD_HEIGHT + CARD_GAP));

                var card = CreateLightingCard(style, isEquipped, isSelected, canAfford, xPos, yPos);
                _cardEntries.Add(card);
            }
        }

        private CardEntry CreateDeskCard(DeskStyle style, bool isEquipped, bool isSelected, bool canAfford,
            float xPos, float yPos)
        {
            var card = UIFactory.Panel($"Card_{style.Id}", _cardGrid, isSelected ? CardSelected : CardBg);
            var cardRect = card.GetComponent<RectTransform>();
            cardRect.anchorMin = new Vector2(0, 1);
            cardRect.anchorMax = new Vector2(0, 1);
            cardRect.pivot = new Vector2(0, 1);
            cardRect.sizeDelta = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            cardRect.anchoredPosition = new Vector2(xPos, yPos);

            var cardImage = card.GetComponent<Image>();

            // Colored square swatch (top portion of card)
            var swatchColor = StyleSwatchColors.TryGetValue(style.Id, out var sc) ? sc : TextMuted;
            var swatch = UIFactory.Panel("Swatch", card.transform, swatchColor);
            var swatchRect = swatch.GetComponent<RectTransform>();
            swatchRect.anchorMin = new Vector2(0.1f, 0.42f);
            swatchRect.anchorMax = new Vector2(0.9f, 0.92f);
            swatchRect.offsetMin = Vector2.zero;
            swatchRect.offsetMax = Vector2.zero;
            var swatchImage = swatch.GetComponent<Image>();

            // Name label
            var nameLabel = TMPFactory.Text($"Name_{style.Id}", style.DisplayName,
                card.transform, 12, TextAlignmentOptions.Left, FontStyles.Bold);
            nameLabel.color = Color.white;
            var nameRect = nameLabel.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = Vector2.zero;
            nameRect.anchorMax = new Vector2(1, 0.38f);
            nameRect.offsetMin = new Vector2(6, 14);
            nameRect.offsetMax = new Vector2(-6, 0);

            // Price label
            string priceStr = style.Cost <= 0 ? "FREE" : $"${style.Cost:F0}";
            var priceLabel = TMPFactory.Text($"Price_{style.Id}", priceStr,
                card.transform, 11, TextAlignmentOptions.Left);
            priceLabel.color = style.Cost <= 0 ? AccentGreen : TextMuted;
            var priceRect = priceLabel.gameObject.GetComponent<RectTransform>();
            priceRect.anchorMin = Vector2.zero;
            priceRect.anchorMax = new Vector2(1, 0.20f);
            priceRect.offsetMin = new Vector2(6, 0);
            priceRect.offsetMax = new Vector2(-6, 0);

            // EQUIPPED badge
            GameObject equippedBadge = null;
            if (isEquipped)
            {
                equippedBadge = UIFactory.Panel("EquippedBadge", card.transform, EquippedBadge);
                var badgeRect = equippedBadge.GetComponent<RectTransform>();
                badgeRect.anchorMin = new Vector2(1, 1);
                badgeRect.anchorMax = new Vector2(1, 1);
                badgeRect.pivot = new Vector2(1, 1);
                badgeRect.sizeDelta = new Vector2(60, 16);
                badgeRect.anchoredPosition = new Vector2(-4, -4);

                var badgeText = TMPFactory.Text("EquippedText", "EQUIPPED",
                    equippedBadge.transform, 9, TextAlignmentOptions.Center, FontStyles.Bold);
                badgeText.color = Color.white;
                var btRect = badgeText.gameObject.GetComponent<RectTransform>();
                btRect.anchorMin = Vector2.zero;
                btRect.anchorMax = Vector2.one;
                btRect.offsetMin = Vector2.zero;
                btRect.offsetMax = Vector2.zero;
            }
            else
            {
                equippedBadge = new GameObject("EquippedBadge");
                equippedBadge.transform.SetParent(card.transform, false);
                equippedBadge.SetActive(false);
            }

            // Lock overlay — always created with children, starts hidden.
            // UpdateCardVisuals toggles visibility once balance is available.
            var lockOverlay = UIFactory.Panel("LockOverlay", card.transform, LockedOverlay);
            var lockOvRect = lockOverlay.GetComponent<RectTransform>();
            lockOvRect.anchorMin = Vector2.zero;
            lockOvRect.anchorMax = Vector2.one;
            lockOvRect.offsetMin = Vector2.zero;
            lockOvRect.offsetMax = Vector2.zero;

            var lockSp = LoadIcon("LockIcon");
            if (lockSp != null)
            {
                var lockIconGo = new GameObject("LockIconOverlay");
                lockIconGo.transform.SetParent(lockOverlay.transform, false);
                var lockIconImg = lockIconGo.AddComponent<Image>();
                lockIconImg.sprite = lockSp;
                lockIconImg.preserveAspect = true;
                lockIconImg.color = new Color(0.9f, 0.3f, 0.3f, 0.7f);
                var lockIconRect = lockIconGo.GetComponent<RectTransform>();
                lockIconRect.anchorMin = new Vector2(0.35f, 0.45f);
                lockIconRect.anchorMax = new Vector2(0.65f, 0.85f);
                lockIconRect.offsetMin = Vector2.zero;
                lockIconRect.offsetMax = Vector2.zero;
            }

            var lockText = TMPFactory.Text("LockText", "INSUFFICIENT FUNDS",
                lockOverlay.transform, 10, TextAlignmentOptions.Center, FontStyles.Bold);
            lockText.color = new Color(0.9f, 0.3f, 0.3f);
            var lockTextRect = lockText.gameObject.GetComponent<RectTransform>();
            lockTextRect.anchorMin = new Vector2(0, 0);
            lockTextRect.anchorMax = new Vector2(1, 0.45f);
            lockTextRect.offsetMin = Vector2.zero;
            lockTextRect.offsetMax = Vector2.zero;

            lockOverlay.SetActive(false);

            // Click handler
            string styleId = style.Id;
            var btn = card.AddComponent<Button>();
            btn.targetGraphic = cardImage;
            btn.onClick.AddListener(new Action(() => OnCardClicked(styleId)));

            return new CardEntry
            {
                StyleId = style.Id,
                Card = card,
                CardImage = cardImage,
                PriceText = priceLabel,
                EquippedBadge = equippedBadge,
                LockOverlay = lockOverlay,
                SwatchImage = swatchImage,
            };
        }

        private CardEntry CreateLightingCard(LightingStyle style, bool isEquipped, bool isSelected, bool canAfford,
            float xPos, float yPos)
        {
            var card = UIFactory.Panel($"Card_{style.Id}", _cardGrid, isSelected ? CardSelected : CardBg);
            var cardRect = card.GetComponent<RectTransform>();
            cardRect.anchorMin = new Vector2(0, 1);
            cardRect.anchorMax = new Vector2(0, 1);
            cardRect.pivot = new Vector2(0, 1);
            cardRect.sizeDelta = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            cardRect.anchoredPosition = new Vector2(xPos, yPos);

            var cardImage = card.GetComponent<Image>();

            // Colored swatch representing the light color
            var swatchColor = LightingSwatchColors.TryGetValue(style.Id, out var sc) ? sc : TextMuted;
            var swatch = UIFactory.Panel("Swatch", card.transform, swatchColor);
            var swatchRect = swatch.GetComponent<RectTransform>();
            swatchRect.anchorMin = new Vector2(0.1f, 0.42f);
            swatchRect.anchorMax = new Vector2(0.9f, 0.92f);
            swatchRect.offsetMin = Vector2.zero;
            swatchRect.offsetMax = Vector2.zero;
            var swatchImage = swatch.GetComponent<Image>();

            // Name label
            var nameLabel = TMPFactory.Text($"Name_{style.Id}", style.DisplayName,
                card.transform, 12, TextAlignmentOptions.Left, FontStyles.Bold);
            nameLabel.color = Color.white;
            var nameRect = nameLabel.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = Vector2.zero;
            nameRect.anchorMax = new Vector2(1, 0.38f);
            nameRect.offsetMin = new Vector2(6, 14);
            nameRect.offsetMax = new Vector2(-6, 0);

            // Price label
            string priceStr = style.Cost <= 0 ? "FREE" : $"${style.Cost:F0}";
            var priceLabel = TMPFactory.Text($"Price_{style.Id}", priceStr,
                card.transform, 11, TextAlignmentOptions.Left);
            priceLabel.color = style.Cost <= 0 ? AccentGreen : TextMuted;
            var priceRect = priceLabel.gameObject.GetComponent<RectTransform>();
            priceRect.anchorMin = Vector2.zero;
            priceRect.anchorMax = new Vector2(1, 0.20f);
            priceRect.offsetMin = new Vector2(6, 0);
            priceRect.offsetMax = new Vector2(-6, 0);

            // EQUIPPED badge
            GameObject equippedBadge = null;
            if (isEquipped)
            {
                equippedBadge = UIFactory.Panel("EquippedBadge", card.transform, EquippedBadge);
                var badgeRect = equippedBadge.GetComponent<RectTransform>();
                badgeRect.anchorMin = new Vector2(1, 1);
                badgeRect.anchorMax = new Vector2(1, 1);
                badgeRect.pivot = new Vector2(1, 1);
                badgeRect.sizeDelta = new Vector2(60, 16);
                badgeRect.anchoredPosition = new Vector2(-4, -4);

                var badgeText = TMPFactory.Text("EquippedText", "EQUIPPED",
                    equippedBadge.transform, 9, TextAlignmentOptions.Center, FontStyles.Bold);
                badgeText.color = Color.white;
                var btRect = badgeText.gameObject.GetComponent<RectTransform>();
                btRect.anchorMin = Vector2.zero;
                btRect.anchorMax = Vector2.one;
                btRect.offsetMin = Vector2.zero;
                btRect.offsetMax = Vector2.zero;
            }
            else
            {
                equippedBadge = new GameObject("EquippedBadge");
                equippedBadge.transform.SetParent(card.transform, false);
                equippedBadge.SetActive(false);
            }

            // Lock overlay
            var lockOverlay = UIFactory.Panel("LockOverlay", card.transform, LockedOverlay);
            var lockOvRect = lockOverlay.GetComponent<RectTransform>();
            lockOvRect.anchorMin = Vector2.zero;
            lockOvRect.anchorMax = Vector2.one;
            lockOvRect.offsetMin = Vector2.zero;
            lockOvRect.offsetMax = Vector2.zero;

            var lockSp = LoadIcon("LockIcon");
            if (lockSp != null)
            {
                var lockIconGo = new GameObject("LockIconOverlay");
                lockIconGo.transform.SetParent(lockOverlay.transform, false);
                var lockIconImg = lockIconGo.AddComponent<Image>();
                lockIconImg.sprite = lockSp;
                lockIconImg.preserveAspect = true;
                lockIconImg.color = new Color(0.9f, 0.3f, 0.3f, 0.7f);
                var lockIconRect = lockIconGo.GetComponent<RectTransform>();
                lockIconRect.anchorMin = new Vector2(0.35f, 0.45f);
                lockIconRect.anchorMax = new Vector2(0.65f, 0.85f);
                lockIconRect.offsetMin = Vector2.zero;
                lockIconRect.offsetMax = Vector2.zero;
            }

            var lockText = TMPFactory.Text("LockText", "INSUFFICIENT FUNDS",
                lockOverlay.transform, 10, TextAlignmentOptions.Center, FontStyles.Bold);
            lockText.color = new Color(0.9f, 0.3f, 0.3f);
            var lockTextRect = lockText.gameObject.GetComponent<RectTransform>();
            lockTextRect.anchorMin = new Vector2(0, 0);
            lockTextRect.anchorMax = new Vector2(1, 0.45f);
            lockTextRect.offsetMin = Vector2.zero;
            lockTextRect.offsetMax = Vector2.zero;

            lockOverlay.SetActive(false);

            // Click handler
            string styleId = style.Id;
            var btn = card.AddComponent<Button>();
            btn.targetGraphic = cardImage;
            btn.onClick.AddListener(new Action(() => OnCardClicked(styleId)));

            return new CardEntry
            {
                StyleId = style.Id,
                Card = card,
                CardImage = cardImage,
                PriceText = priceLabel,
                EquippedBadge = equippedBadge,
                LockOverlay = lockOverlay,
                SwatchImage = swatchImage,
            };
        }

        private void UpdateCardVisuals()
        {
            float balance = GetOnlineBalance();

            if (_activeCategory == Category.CheckoutDesk)
            {
                var counter = GetSelectedCounter();
                string currentStyle = counter?.CurrentDeskStyleId ?? DeskStyle.Default.Id;

                for (int i = 0; i < _cardEntries.Count; i++)
                {
                    var entry = _cardEntries[i];
                    if (entry.Card == null) continue;

                    bool isEquipped = entry.StyleId == currentStyle;
                    bool isSelected = entry.StyleId == _pendingStyleId;
                    bool canAfford = CanAffordUpgrade(currentStyle, entry.StyleId, balance);

                    entry.CardImage.color = isSelected ? CardSelected : CardBg;
                    entry.EquippedBadge.SetActive(isEquipped);
                    entry.LockOverlay.SetActive(!canAfford && !isEquipped);
                }
            }
            else if (_activeCategory == Category.Lighting)
            {
                string currentLighting = Dispensary.CurrentLightingStyleId ?? LightingStyle.Default.Id;

                for (int i = 0; i < _cardEntries.Count; i++)
                {
                    var entry = _cardEntries[i];
                    if (entry.Card == null) continue;

                    bool isEquipped = entry.StyleId == currentLighting;
                    bool isSelected = entry.StyleId == _pendingLightingId;
                    bool canAfford = CanAffordLighting(currentLighting, entry.StyleId, balance);

                    entry.CardImage.color = isSelected ? CardSelected : CardBg;
                    entry.EquippedBadge.SetActive(isEquipped);
                    entry.LockOverlay.SetActive(!canAfford && !isEquipped);
                }
            }
        }

        // ==================================================================
        //  Footer refresh
        // ==================================================================

        private void RefreshFooter()
        {
            float balance = GetOnlineBalance();

            if (_balanceText != null)
                _balanceText.text = $"${balance:F0}";

            bool hasChange = false;

            if (_activeCategory == Category.CheckoutDesk)
            {
                hasChange = _pendingStyleId != null
                    && GetSelectedCounter() != null
                    && _pendingStyleId != (GetSelectedCounter().CurrentDeskStyleId ?? DeskStyle.Default.Id);
            }
            else if (_activeCategory == Category.Lighting)
            {
                string currentLighting = Dispensary.CurrentLightingStyleId ?? LightingStyle.Default.Id;
                hasChange = _pendingLightingId != null && _pendingLightingId != currentLighting;
            }

            if (_applyBtnImage != null)
                _applyBtnImage.color = hasChange ? AccentGreenDark : TextDim;
            if (_applyBtnText != null)
                _applyBtnText.color = hasChange ? Color.white : new Color(0.6f, 0.6f, 0.6f);
        }

        // ==================================================================
        //  Interaction handlers
        // ==================================================================

        private void OnCardClicked(string styleId)
        {
            float balance = GetOnlineBalance();

            if (_activeCategory == Category.CheckoutDesk)
            {
                var counter = GetSelectedCounter();
                if (counter == null) return;

                string currentStyle = counter.CurrentDeskStyleId ?? DeskStyle.Default.Id;

                if (!CanAffordUpgrade(currentStyle, styleId, balance) && styleId != currentStyle)
                    return;

                _pendingStyleId = styleId;
                _pendingStyle = DeskStyle.Get(styleId);
            }
            else if (_activeCategory == Category.Lighting)
            {
                string currentLighting = Dispensary.CurrentLightingStyleId ?? LightingStyle.Default.Id;

                if (!CanAffordLighting(currentLighting, styleId, balance) && styleId != currentLighting)
                    return;

                _pendingLightingId = styleId;
            }

            UpdateCardVisuals();
            RefreshFooter();
        }

        private void OnApplyClicked()
        {
            if (_activeCategory == Category.CheckoutDesk)
                ApplyDeskUpgrade();
            else if (_activeCategory == Category.Lighting)
                ApplyLightingUpgrade();
        }

        private void ApplyDeskUpgrade()
        {
            var counter = GetSelectedCounter();
            if (counter == null) return;

            string currentStyle = counter.CurrentDeskStyleId ?? DeskStyle.Default.Id;
            if (_pendingStyleId == null || _pendingStyleId == currentStyle) return;

            var newStyle = DeskStyle.Get(_pendingStyleId);
            float balance = GetOnlineBalance();
            float cost = GetUpgradeCost(currentStyle, _pendingStyleId);

            if (cost > 0 && balance < cost) return;

            if (cost > 0)
            {
                try
                {
                    var mm = NetworkSingleton<MoneyManager>.Instance;
                    mm?.CreateOnlineTransaction("Desk Upgrade", -cost, 1, $"Upgraded to {newStyle.DisplayName}");
                }
                catch (Exception ex)
                {
                    OTCLog.Error(OTCLog.Systems.Patch, $"Desk payment failed: {ex.Message}");
                    return;
                }
            }

            int swapped = 0;
            foreach (var c in CheckoutCounter.AllCounters)
            {
                if (c.BuildingId == _selectedBuildingId)
                {
                    c.SwapDesk(newStyle);
                    swapped++;
                }
            }
            OTCLog.Msg(OTCLog.Systems.Patch, $"Swapped {swapped}/{CheckoutCounter.AllCounters.Count} counters to '{newStyle.DisplayName}' (building={_selectedBuildingId})");

            try { ConfigSyncData.Instance?.PublishGameState(); }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"Failed to sync desk change: {ex.Message}");
            }

            OTCLog.Msg(OTCLog.Systems.Patch, $"Desk upgraded to '{newStyle.DisplayName}' for ${cost:F0}");
            RefreshCards();
        }

        private void ApplyLightingUpgrade()
        {
            string currentLighting = Dispensary.CurrentLightingStyleId ?? LightingStyle.Default.Id;
            if (_pendingLightingId == null || _pendingLightingId == currentLighting) return;

            var newStyle = LightingStyle.Get(_pendingLightingId);
            float balance = GetOnlineBalance();
            float cost = GetLightingUpgradeCost(currentLighting, _pendingLightingId);

            if (cost > 0 && balance < cost) return;

            if (cost > 0)
            {
                try
                {
                    var mm = NetworkSingleton<MoneyManager>.Instance;
                    mm?.CreateOnlineTransaction("Lighting Upgrade", -cost, 1, $"Upgraded to {newStyle.DisplayName}");
                }
                catch (Exception ex)
                {
                    OTCLog.Error(OTCLog.Systems.Patch, $"Lighting payment failed: {ex.Message}");
                    return;
                }
            }

            Dispensary.ApplyLightingStyle(newStyle);

            try { ConfigSyncData.Instance?.PublishGameState(); }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"Failed to sync lighting change: {ex.Message}");
            }

            OTCLog.Msg(OTCLog.Systems.Patch, $"Lighting upgraded to '{newStyle.DisplayName}' for ${cost:F0}");
            RefreshCards();
        }

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

        // ==================================================================
        //  Helpers
        // ==================================================================

        /// <summary>Returns all building IDs that currently have at least one counter.</summary>
        private static List<string> GetBuildingsWithCounters()
        {
            var seen = new HashSet<string>();
            var result = new List<string>();
            foreach (var counter in CheckoutCounter.AllCounters)
            {
                var bid = counter.BuildingId;
                if (bid != null && seen.Add(bid))
                    result.Add(bid);
            }
            return result;
        }

        /// <summary>Returns the first counter in the selected building, or null.</summary>
        private CheckoutCounterInstance GetSelectedCounter()
        {
            foreach (var counter in CheckoutCounter.AllCounters)
            {
                if (counter.BuildingId == _selectedBuildingId)
                    return counter;
            }
            // Fallback: any counter
            return CheckoutCounter.AllCounters.Count > 0 ? CheckoutCounter.AllCounters[0] : null;
        }

        private static string GetBuildingDisplayName(string buildingId) =>
            BuildingDisplayNames.TryGetValue(buildingId, out var name) ? name : buildingId ?? "Unknown";

        private static float GetOnlineBalance()
        {
            try
            {
                return NetworkSingleton<MoneyManager>.Instance?.onlineBalance ?? 0f;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// Calculates the cost to change from one style to another.
        /// Upgrading: costs the new style's price minus the old style's price (minimum 0).
        /// Downgrading: free.
        /// </summary>
        private static float GetUpgradeCost(string fromId, string toId)
        {
            var from = DeskStyle.Get(fromId);
            var to = DeskStyle.Get(toId);
            float diff = to.Cost - from.Cost;
            return diff > 0 ? diff : 0;
        }

        private static bool CanAffordUpgrade(string currentId, string targetId, float balance)
        {
            if (currentId == targetId) return true;
            float cost = GetUpgradeCost(currentId, targetId);
            return cost <= 0 || balance >= cost;
        }

        private static float GetLightingUpgradeCost(string fromId, string toId)
        {
            var from = LightingStyle.Get(fromId);
            var to = LightingStyle.Get(toId);
            float diff = to.Cost - from.Cost;
            return diff > 0 ? diff : 0;
        }

        private static bool CanAffordLighting(string currentId, string targetId, float balance)
        {
            if (currentId == targetId) return true;
            float cost = GetLightingUpgradeCost(currentId, targetId);
            return cost <= 0 || balance >= cost;
        }
    }
}
