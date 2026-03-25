using S1API.PhoneApp;
using S1API.UI;
using UnityEngine;
using UnityEngine.UI;
using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;

#if IL2CPP
using Il2CppTMPro;
#else
using TMPro;
#endif

namespace OverTheCounter.Apps
{
    /// <summary>
    /// GreenTab POS — phone app for dispensary customization.
    /// Horizontal orientation. Supports desk style and lighting upgrades.
    /// Split across partial files:
    ///   GreenTabApp.cs           — core, fields, lifecycle, utilities
    ///   GreenTabApp.Navigation.cs — nav bar, sidebar, top bar, dropdown
    ///   GreenTabApp.Cards.cs     — card grid, footer, refresh, click routing
    ///   GreenTabApp.DeskTab.cs   — desk style cards + purchase logic
    ///   GreenTabApp.LightingTab.cs — lighting style cards + purchase logic
    ///   GreenTabApp.Helpers.cs   — balance, counter, building lookups
    /// </summary>
    public partial class GreenTabApp : PhoneApp
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

        // ==================================================================
        //  UI Utilities
        // ==================================================================

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
    }
}
