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
    /// Horizontal orientation. Supports desk, wall, floor, and lighting upgrades.
    /// Split across partial files:
    ///   GreenTabApp.cs            — core, fields, lifecycle, utilities
    ///   GreenTabApp.Navigation.cs — nav bar, sidebar, top bar, dropdown
    ///   GreenTabApp.Cards.cs      — card grid, footer, refresh, click routing
    ///   GreenTabApp.DeskTab.cs    — desk style cards + purchase logic
    ///   GreenTabApp.LightingTab.cs— lighting style cards + purchase logic
    ///   GreenTabApp.WallsTab.cs   — wall style cards (exterior + interior)
    ///   GreenTabApp.FlooringTab.cs— floor style cards + purchase logic
    ///   GreenTabApp.Helpers.cs    — balance, counter, building lookups
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
        private const int GRID_COLUMNS = 4;
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
            (Category.Walls, "Walls", false),
            (Category.Flooring, "Flooring", false),
            (Category.Lighting, "Lighting", false), // set true to gate behind unlock
        };

        // ---- State ----
        private Category _activeCategory = Category.CheckoutDesk;
        private string _selectedBuildingId;
        private string _pendingStyleId;
        private DeskStyle _pendingStyle;
        private string _pendingLightingId;
        private string _pendingExteriorWallId;
        private string _pendingInteriorWallId;
        private string _pendingFloorId;

        // ---- UI refs ----
        private GameObject _rootPanel;
        private GameObject _landingPanel;
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
        private bool _wasOpen;

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

        // ---- Wall style swatch colors ----
        private static readonly Dictionary<string, Color> WallSwatchColors = new()
        {
            // Interior
            { "brick_red", new Color(0.65f, 0.25f, 0.20f) },
            { "concrete_charcoal", new Color(0.25f, 0.25f, 0.25f) },
            { "stripes_charcoal", new Color(0.28f, 0.28f, 0.30f) },
            { "metal_green", new Color(0.20f, 0.40f, 0.25f) },
            { "white_lighter", new Color(0.92f, 0.92f, 0.92f) },
            { "mansion_wood", new Color(0.85f, 0.80f, 0.70f) },
            { "alum_grey", new Color(0.60f, 0.62f, 0.65f) },
            { "tiles_black", new Color(0.10f, 0.10f, 0.10f) },
            { "metal_darkgrey", new Color(0.18f, 0.18f, 0.18f) },
            { "concrete_green", new Color(0.25f, 0.45f, 0.30f) },
            { "small_tile_white", new Color(0.85f, 0.82f, 0.78f) },
            // Exterior
            { "ext_brick_red", new Color(0.65f, 0.25f, 0.20f) },
            { "granite_salmon", new Color(0.75f, 0.55f, 0.50f) },
            { "mansion_ext", new Color(0.80f, 0.75f, 0.65f) },
            { "ext_concrete_charcoal", new Color(0.25f, 0.25f, 0.25f) },
            { "ext_metal_darkgrey", new Color(0.18f, 0.18f, 0.18f) },
            { "brick_dark_grey", new Color(0.30f, 0.30f, 0.30f) },
            { "brick_blue", new Color(0.20f, 0.30f, 0.55f) },
            { "brick_warehouse", new Color(0.50f, 0.35f, 0.25f) },
            { "ext_alum_grey", new Color(0.60f, 0.62f, 0.65f) },
        };

        // ---- Floor style swatch colors ----
        private static readonly Dictionary<string, Color> FloorSwatchColors = new()
        {
            { "wood_planks_brown", new Color(0.55f, 0.35f, 0.18f) },
            { "tiles_light_grey", new Color(0.72f, 0.72f, 0.72f) },
            { "mansion_floor", new Color(0.45f, 0.30f, 0.18f) },
            { "concrete_beige", new Color(0.78f, 0.72f, 0.62f) },
            { "tiles_black", new Color(0.10f, 0.10f, 0.10f) },
            { "concrete_black", new Color(0.08f, 0.08f, 0.08f) },
            { "metal_dark_grey", new Color(0.22f, 0.22f, 0.22f) },
            { "wood_planks_black", new Color(0.12f, 0.10f, 0.08f) },
            { "concrete_grey", new Color(0.55f, 0.55f, 0.55f) },
            { "concrete_crimson", new Color(0.55f, 0.12f, 0.12f) },
            { "concrete_navy", new Color(0.10f, 0.15f, 0.35f) },
            { "small_tile_white", new Color(0.85f, 0.82f, 0.78f) },
            { "wood_beige", new Color(0.72f, 0.60f, 0.42f) },
            { "off_white", new Color(0.90f, 0.88f, 0.85f) },
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

            // Pick initial building — first owned one that has a counter
            _selectedBuildingId = GetOwnedBuildings().FirstOrDefault() ?? PropertySaveData.ShackId;

            BuildTopBar(_rootPanel.transform);
            BuildNavBar(_rootPanel.transform);
            BuildSidebar(_rootPanel.transform);
            BuildCardArea(_rootPanel.transform);
            BuildFooter(_rootPanel.transform);
            BuildLandingPage(_rootPanel.transform);

            UpdateLandingVisibility();
            RefreshCards();
            RefreshFooter();

            MelonCoroutines.Start(AppUpdateLoop());
        }

        private void BuildLandingPage(Transform parent)
        {
            _landingPanel = UIFactory.Panel("LandingPage", parent, BgDark, fullAnchor: true);
            _landingPanel.transform.SetAsLastSibling();

            // Icon
            var iconSprite = LoadIcon("GreenTabLogo");
            if (iconSprite != null)
            {
                var iconGo = new GameObject("LandingIcon");
                iconGo.transform.SetParent(_landingPanel.transform, false);
                var iconImg = iconGo.AddComponent<Image>();
                iconImg.sprite = iconSprite;
                iconImg.preserveAspect = true;
                iconImg.color = AccentGreen;
                var iconRect = iconGo.GetComponent<RectTransform>();
                iconRect.anchorMin = new Vector2(0.5f, 0.55f);
                iconRect.anchorMax = new Vector2(0.5f, 0.55f);
                iconRect.pivot = new Vector2(0.5f, 0.5f);
                iconRect.sizeDelta = new Vector2(48, 48);
            }

            // Message
            var msg = TMPFactory.Text("LandingMsg",
                "No properties available yet.\n\nPurchase a property through Static's messages to start customizing.",
                _landingPanel.transform, 16, TextAlignmentOptions.Center);
            msg.color = TextMuted;
            var msgRect = msg.gameObject.GetComponent<RectTransform>();
            msgRect.anchorMin = new Vector2(0.2f, 0.25f);
            msgRect.anchorMax = new Vector2(0.8f, 0.52f);
            msgRect.offsetMin = Vector2.zero;
            msgRect.offsetMax = Vector2.zero;
        }

        private void UpdateLandingVisibility()
        {
            bool hasStore = GetOwnedBuildings().Count > 0;
            if (_landingPanel != null) _landingPanel.SetActive(!hasStore);
        }

        private IEnumerator AppUpdateLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(0.5f);

                bool isOpen;
                try { isOpen = IsOpen(); } catch { isOpen = false; }

                if (isOpen && !_wasOpen)
                {
                    // App just opened — refresh ownership, balance, and card visuals
                    UpdateLandingVisibility();

                    var buildings = GetOwnedBuildings();
                    if (buildings.Count > 0 && !buildings.Contains(_selectedBuildingId))
                    {
                        _selectedBuildingId = buildings[0];
                        if (_propertyDropdownText != null)
                            _propertyDropdownText.text = GetBuildingDisplayName(_selectedBuildingId) + " \u25BC";
                    }

                    RefreshCards();
                    RefreshFooter();
                    UpdateCardVisuals();
                }

                _wasOpen = isOpen;
            }
        }
    }
}
