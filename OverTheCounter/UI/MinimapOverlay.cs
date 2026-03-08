using MelonLoader;
using OverTheCounter.Utilities;
using S1API.GameTime;
using S1API.Leveling;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppInterop.Runtime.Injection;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI.Phone.Map;
using Il2CppTMPro;
using GameCanvasScaler = Il2CppScheduleOne.UI.CanvasScaler;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Economy;
using ScheduleOne.Map;
using ScheduleOne.UI;
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI.Phone.Map;
using TMPro;
using GameCanvasScaler = ScheduleOne.UI.CanvasScaler;
#endif

namespace OverTheCounter.UI
{
    [RegisterTypeInIl2Cpp]
    public class MinimapOverlay : MonoBehaviour
    {
        // Zoom levels: smaller = more map visible in the window (more zoomed out)
        private static readonly float[] ZoomSizes = { 0f, 2400f, 1200f, 600f };

        private int _zoom;
        private bool _visible;
        private KeyCode _toggleKey = KeyCode.N;

        // UI hierarchy:
        //   Canvas > Border
        //   Canvas > Container (clip mask)
        //          > RotationPivot (centered, rotation applied here)
        //            > MapImage (offset to center on player)
        //              > Markers (stretches to fill MapImage, holds POI clones)
        //          > PlayerMarker (fixed at center, unaffected by rotation)
        private GameObject _canvasObj;
        private RectTransform _rotationPivot;
        private RectTransform _mapRect;
        private RectTransform _markersParent;
        private Image _borderImage;

        // Map dimensions from MapApp.ContentRect
        private float _contentW = 2048f;
        private float _contentH = 2048f;
        private float _displaySize;

        // POI clone tracking
        private readonly Dictionary<int, RectTransform> _poiClones = new();
        private readonly HashSet<int> _activeCloneIds = new();
        private POI[] _cachedPOIs;
        private float _lastPOIRefresh;

        // Customer marker tracking (unlocked customers have no active POI)
        private readonly Dictionary<int, RectTransform> _customerClones = new();
        private GameObject _npcPoiTemplate; // cached NPCPoI UI clone for stamping customer markers

        // Circle mask sprite (generated once)
        private static Sprite _circleMaskSprite;
        private RectTransform _playerMarkerRect;

        // Time/day display
        private GameObject _timeDayObj;
        private TextMeshProUGUI _timeText;
        private TextMeshProUGUI _dayText;

        // Rank/XP bar
        private GameObject _rankBarObj;
        private TextMeshProUGUI _rankText;
        private Image _xpBarFill;
        private TextMeshProUGUI _xpText;
        private int _lastKnownXP = -1;
        private int _lastKnownTier = -1;
        private readonly List<XPDropLabel> _xpDrops = new List<XPDropLabel>();

        // Remote player POI → Player lookup (rebuilt on POI cache refresh)
        private readonly Dictionary<int, Player> _poiToPlayer = new();

        // Cached config values — rebuild minimap when structural settings change
        private int _cfgSize;
        private bool _cfgCircle;
        private string _cfgPosition;
        private KeyCode _cfgToggleKey;
        private float _cfgIconScale;
        private int _cfgBorderWidth;
        private bool _cfgShowTime;
        private bool _cfgShowDay;
        private bool _cfgUse24Hour;
        private bool _cfgShowRank;

        private static readonly HashSet<string> ValidPositions = new()
        {
            "TopLeft", "TopRight", "BottomLeft", "BottomRight"
        };

        public static void Register()
        {
#if IL2CPP
            ClassInjector.RegisterTypeInIl2Cpp<MinimapOverlay>();
#endif
        }

        private void Awake()
        {
            SnapshotConfig();

            int defaultZoom = Mathf.Clamp(Config.MinimapDefaultZoom.Value, 1, 3);
            if (Config.MinimapEnabled.Value)
            {
                _zoom = defaultZoom;
                _visible = true;
            }
        }

        private void Update()
        {
            // Hide minimap during loading screens / menus
            if (Player.Local == null)
            {
                if (_canvasObj != null) _canvasObj.SetActive(false);
                return;
            }

            // Wait for loading screen to fully close (covers S1API mugshots, other mods, etc.)
            try
            {
                var loadingScreen = Singleton<LoadingScreen>.Instance;
                if (loadingScreen != null && loadingScreen.IsOpen)
                {
                    if (_canvasObj != null) _canvasObj.SetActive(false);
                    return;
                }
            }
            catch { }

            if (_canvasObj != null && !_canvasObj.activeSelf) _canvasObj.SetActive(true);

            // Hot-reload: rebuild if config changed
            if (_canvasObj != null && ConfigChanged())
            {
                int savedZoom = _zoom;
                DestroyMinimap();
                _zoom = savedZoom;
                _visible = _zoom > 0;
                SnapshotConfig();
            }

            // Live-update zoom when preferred zoom config changes
            int preferredZoom = Mathf.Clamp(Config.MinimapDefaultZoom.Value, 1, 3);
            if (_visible && _zoom != preferredZoom && _mapRect != null)
            {
                _zoom = preferredZoom;
                _displaySize = ZoomSizes[_zoom];
                _mapRect.sizeDelta = new Vector2(_displaySize, _displaySize);
            }

            // React to MinimapEnabled being toggled via config/ModsApp
            if (Config.MinimapEnabled.Value && !_visible)
            {
                _zoom = Mathf.Clamp(Config.MinimapDefaultZoom.Value, 1, 3);
                _visible = true;
            }
            else if (!Config.MinimapEnabled.Value && _visible)
            {
                DestroyMinimap();
                _visible = false;
            }

            if (Input.GetKeyDown(_toggleKey) && !ScheduleOne.GameInput.IsTyping)
                ToggleMinimap();

            if (_visible)
                UpdateMinimap();
        }

        private void SnapshotConfig()
        {
            _cfgSize = Mathf.Clamp(Config.MinimapSize.Value, 100, 600);
            if (Config.MinimapSize.Value != _cfgSize)
                Config.MinimapSize.RawEntry.Value = _cfgSize;

            _cfgCircle = Config.MinimapCircle.Value;

            string rawPos = Config.MinimapPosition?.Value ?? "TopRight";
            if (!ValidPositions.Contains(rawPos))
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"Invalid MinimapPosition '{rawPos}', defaulting to TopRight");
                rawPos = "TopRight";
                Config.MinimapPosition.Value = rawPos;
            }
            _cfgPosition = rawPos;

            int rawZoom = Config.MinimapDefaultZoom.Value;
            int clampedZoom = Mathf.Clamp(rawZoom, 1, 3);
            if (rawZoom != clampedZoom)
                Config.MinimapDefaultZoom.RawEntry.Value = clampedZoom;

            _cfgToggleKey = Config.MinimapToggleKey?.Value ?? KeyCode.N;

            _cfgIconScale = Mathf.Clamp(Config.MinimapIconScale.Value, 0.1f, 3f);
            if (Math.Abs(Config.MinimapIconScale.Value - _cfgIconScale) > 0.001f)
                Config.MinimapIconScale.RawEntry.Value = _cfgIconScale;

            _cfgBorderWidth = Mathf.Clamp(Config.MinimapBorderWidth.Value, 2, 10);
            if (Config.MinimapBorderWidth.Value != _cfgBorderWidth)
                Config.MinimapBorderWidth.RawEntry.Value = _cfgBorderWidth;

            _cfgShowTime = Config.MinimapShowTime.Value;
            _cfgShowDay = Config.MinimapShowDay.Value;
            _cfgUse24Hour = Config.MinimapUse24HourClock.Value;
            _cfgShowRank = Config.MinimapShowRank.Value;

            _toggleKey = _cfgToggleKey;
        }

        private bool ConfigChanged()
        {
            return Config.MinimapSize.Value != _cfgSize
                || Config.MinimapCircle.Value != _cfgCircle
                || (Config.MinimapPosition?.Value ?? "TopRight") != _cfgPosition
                || (Config.MinimapToggleKey?.Value ?? KeyCode.N) != _cfgToggleKey
                || Math.Abs(Config.MinimapIconScale.Value - _cfgIconScale) > 0.001f
                || Config.MinimapBorderWidth.Value != _cfgBorderWidth
                || Config.MinimapShowTime.Value != _cfgShowTime
                || Config.MinimapShowDay.Value != _cfgShowDay
                || Config.MinimapUse24HourClock.Value != _cfgUse24Hour
                || Config.MinimapShowRank.Value != _cfgShowRank;
        }

        private void ToggleMinimap()
        {
            _zoom = (_zoom + 1) % 4; // 0→1→2→3→0
            _visible = _zoom > 0;

            // Sync enabled + zoom back to config so the settings UI reflects current state
            // and the "react to MinimapEnabled" check in Update doesn't re-enable immediately
            Config.MinimapEnabled.RawEntry.Value = _visible;
            if (_visible)
                Config.MinimapDefaultZoom.RawEntry.Value = _zoom;

            if (_visible && _canvasObj == null)
                CreateMinimap();
            else if (!_visible)
                DestroyMinimap();

            if (_visible && _mapRect != null)
            {
                _displaySize = ZoomSizes[_zoom];
                _mapRect.sizeDelta = new Vector2(_displaySize, _displaySize);
            }
        }

        private void CreateMinimap()
        {
            if (_canvasObj != null) return;

            // Get map sprite from the game's MapApp
            Sprite mapSprite = null;
            try
            {
                var mapApp = PlayerSingleton<MapApp>.Instance;
                if (mapApp != null)
                {
                    mapSprite = mapApp.MainMapSprite;
                    if (mapApp.ContentRect != null)
                    {
                        _contentW = mapApp.ContentRect.rect.width;
                        _contentH = mapApp.ContentRect.rect.height;
                    }
                }
            }
            catch { }

            if (mapSprite == null)
                return; // MapApp not ready yet — retry next frame

            int size = _cfgSize;
            bool circle = _cfgCircle;
            int margin = 10;

            // Canvas (ScreenSpace Overlay, high sort order)
            _canvasObj = new GameObject("OTC_MinimapCanvas");
            var canvas = _canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 999;
            var scaler = _canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            _canvasObj.AddComponent<GameCanvasScaler>();
            UnityEngine.Object.DontDestroyOnLoad(_canvasObj);

            // Border (slightly larger background behind the container)
            var borderObj = new GameObject("MinimapBorder");
            borderObj.transform.SetParent(_canvasObj.transform, false);
            _borderImage = borderObj.AddComponent<Image>();
            _borderImage.color = Config.MinimapBorderColor?.Value ?? new Color(0.2f, 0.2f, 0.2f, 0.9f);
            _borderImage.raycastTarget = false;
            var borderRect = borderObj.GetComponent<RectTransform>();
            int bw = _cfgBorderWidth;
            ApplyPosition(borderRect, size + (bw * 2), margin - bw, _cfgPosition);
            if (circle)
                _borderImage.sprite = GetCircleMaskSprite();

            // Container (dark background + clip mask)
            var containerObj = new GameObject("MinimapContainer");
            containerObj.transform.SetParent(_canvasObj.transform, false);
            var containerImg = containerObj.AddComponent<Image>();
            containerImg.color = new Color(0.05f, 0.05f, 0.05f, 0.85f);
            containerImg.raycastTarget = false;
            var containerRect = containerObj.GetComponent<RectTransform>();
            ApplyPosition(containerRect, size, margin, _cfgPosition);

            if (circle)
            {
                containerImg.sprite = GetCircleMaskSprite();
                var mask = containerObj.AddComponent<Mask>();
                mask.showMaskGraphic = true;
            }
            else
            {
                containerObj.AddComponent<RectMask2D>();
            }

            // Rotation pivot (centered in container — rotation applied here so map spins around player)
            var pivotObj = new GameObject("RotationPivot");
            pivotObj.transform.SetParent(containerObj.transform, false);
            _rotationPivot = pivotObj.AddComponent<RectTransform>();
            _rotationPivot.anchorMin = new Vector2(0.5f, 0.5f);
            _rotationPivot.anchorMax = new Vector2(0.5f, 0.5f);
            _rotationPivot.pivot = new Vector2(0.5f, 0.5f);
            _rotationPivot.sizeDelta = Vector2.zero;
            _rotationPivot.anchoredPosition = Vector2.zero;

            // Map image (oversized, child of pivot, pans to center on player)
            var mapObj = new GameObject("MapImage");
            mapObj.transform.SetParent(pivotObj.transform, false);
            var mapImage = mapObj.AddComponent<Image>();
            mapImage.sprite = mapSprite;
            mapImage.preserveAspect = true;
            mapImage.raycastTarget = false;

            _mapRect = mapObj.GetComponent<RectTransform>();
            _mapRect.anchorMin = new Vector2(0.5f, 0.5f);
            _mapRect.anchorMax = new Vector2(0.5f, 0.5f);
            _mapRect.pivot = new Vector2(0.5f, 0.5f);
            _displaySize = ZoomSizes[_zoom];
            _mapRect.sizeDelta = new Vector2(_displaySize, _displaySize);

            // Markers parent (child of MapImage — moves and rotates with map)
            var markersObj = new GameObject("Markers");
            markersObj.transform.SetParent(mapObj.transform, false);
            _markersParent = markersObj.AddComponent<RectTransform>();
            _markersParent.anchorMin = Vector2.zero;
            _markersParent.anchorMax = Vector2.one;
            _markersParent.sizeDelta = Vector2.zero;
            _markersParent.anchoredPosition = Vector2.zero;

            // Player marker — clone the game's native player POI icon
            try
            {
                var playerPoi = Player.Local?.PoI;
                if (playerPoi?.UI != null)
                {
                    var markerClone = UnityEngine.Object.Instantiate(playerPoi.UI.gameObject, containerObj.transform, false);
                    markerClone.name = "PlayerMarker";
                    _playerMarkerRect = markerClone.GetComponent<RectTransform>();

                    // Disable animations, text labels, and raycast — but leave layout intact
                    foreach (var anim in markerClone.GetComponentsInChildren<Animation>(true))
                        anim.enabled = false;
                    foreach (var txt in markerClone.GetComponentsInChildren<Text>(true))
                        txt.gameObject.SetActive(false);
                    foreach (var graphic in markerClone.GetComponentsInChildren<Graphic>(true))
                        graphic.raycastTarget = false;

                    // Center the root in the container — keep original sizeDelta so children lay out correctly
                    _playerMarkerRect.anchorMin = new Vector2(0.5f, 0.5f);
                    _playerMarkerRect.anchorMax = new Vector2(0.5f, 0.5f);
                    _playerMarkerRect.pivot = new Vector2(0.5f, 0.5f);
                    _playerMarkerRect.anchoredPosition = Vector2.zero;
                    _playerMarkerRect.localScale = Vector3.one * 0.65f; // scaled down to fit minimap without dominating POI icons

                    // The game's live POI rotates IconContainer to show facing direction.
                    // Zero it so our root-level rotation in UpdateMinimap controls direction cleanly.
                    var iconContainer = _playerMarkerRect.Find("IconContainer");
                    if (iconContainer != null)
                        iconContainer.localEulerAngles = Vector3.zero;
                }
            }
            catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.Patch, $"Failed to clone player POI: {ex.Message}"); }

            // Fallback if clone failed — simple green circle
            if (_playerMarkerRect == null)
            {
                var markerObj = new GameObject("PlayerMarker");
                markerObj.transform.SetParent(containerObj.transform, false);
                var markerImg = markerObj.AddComponent<Image>();
                markerImg.sprite = GetCircleMaskSprite();
                markerImg.color = new Color(0.2f, 1f, 0.3f, 1f);
                markerImg.raycastTarget = false;
                _playerMarkerRect = markerObj.GetComponent<RectTransform>();
                _playerMarkerRect.anchorMin = new Vector2(0.5f, 0.5f);
                _playerMarkerRect.anchorMax = new Vector2(0.5f, 0.5f);
                _playerMarkerRect.pivot = new Vector2(0.5f, 0.5f);
                _playerMarkerRect.sizeDelta = new Vector2(14, 14);
                _playerMarkerRect.anchoredPosition = Vector2.zero;
            }

            // Time/day label — positioned below minimap for top corners, above for bottom
            bool hasTimeDay = _cfgShowTime || _cfgShowDay;
            CreateTimeDayLabel(size, margin);
            CreateRankBar(size, margin, hasTimeDay);

            SnapshotConfig();
            _cachedPOIs = null;
            _lastPOIRefresh = 0f;
        }

        private static readonly string[] ShortDayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        private static readonly string[] RankNames =
        {
            "Street Rat", "Hoodlum", "Peddler", "Hustler", "Bagman",
            "Enforcer", "Shot Caller", "Block Boss", "Underlord", "Baron", "Kingpin"
        };

        private static readonly string[] RomanTiers = { "", "I", "II", "III", "IV", "V" };

        private class XPDropLabel
        {
            public TextMeshProUGUI Label;
            public RectTransform Rect;
            public float Timer;
            public float StartY;
            public float StartX;
            public const float Duration = 1.4f;
            public const float Rise = 40f;
        }

        private void CreateTimeDayLabel(int size, int margin)
        {
            bool showTime = Config.MinimapShowTime.Value;
            bool showDay = Config.MinimapShowDay.Value;
            if (!showTime && !showDay) return;

            _timeDayObj = new GameObject("MinimapTimeDay");
            _timeDayObj.transform.SetParent(_canvasObj.transform, false);

            // Dark semi-transparent background
            var bgImage = _timeDayObj.AddComponent<Image>();
            bgImage.color = new Color(0.08f, 0.08f, 0.08f, 0.85f);
            bgImage.raycastTarget = false;

            var bgRect = _timeDayObj.GetComponent<RectTransform>();

            bool isTop = _cfgPosition.StartsWith("Top");
            bool isRight = _cfgPosition.EndsWith("Right");

            // Anchor to same horizontal edge as minimap
            float anchorX = isRight ? 1f : 0f;
            float pivotX = isRight ? 1f : 0f;

            // Vertical: below minimap for top, above for bottom
            float anchorY = isTop ? 1f : 0f;
            float pivotY = isTop ? 1f : 0f;

            bgRect.anchorMin = new Vector2(anchorX, anchorY);
            bgRect.anchorMax = new Vector2(anchorX, anchorY);
            bgRect.pivot = new Vector2(pivotX, pivotY);

            float labelWidth = size + (_cfgBorderWidth * 2);
            float labelHeight = 24f;
            bgRect.sizeDelta = new Vector2(labelWidth, labelHeight);

            // Position: flush with minimap border edges
            float xSign = isRight ? -1f : 1f;
            float bw = _cfgBorderWidth;
            float xPos = xSign * (margin - bw);

            // Vertical offset from screen edge: minimap occupies (margin-bw) to (margin-bw + size+2*bw)
            // For top: label goes right below border → y = -(margin - bw + size + 2*bw + gap)
            // For bottom: label goes right above border → y = (margin - bw + size + 2*bw + gap)
            float minimapTotalHeight = size + (bw * 2);
            float gap = 2f;
            float yOffset = (margin - bw) + minimapTotalHeight + gap;

            // BottomRight has a +80 offset for the HUD bar
            if (_cfgPosition == "BottomRight") yOffset += 80;

            float yPos = isTop ? -yOffset : yOffset;
            bgRect.anchoredPosition = new Vector2(xPos, yPos);

            // Layout: day on left, time on right (or centered if only one)
            if (showDay)
            {
                var dayObj = new GameObject("DayLabel");
                dayObj.transform.SetParent(_timeDayObj.transform, false);
                _dayText = dayObj.AddComponent<TextMeshProUGUI>();
                _dayText.fontSize = 15;
                _dayText.fontStyle = FontStyles.Bold;
                _dayText.color = new Color(0.55f, 0.85f, 1f); // light blue
                _dayText.alignment = showTime ? TextAlignmentOptions.Left : TextAlignmentOptions.Center;
                _dayText.raycastTarget = false;
                _dayText.text = "";

                var dayRect = dayObj.GetComponent<RectTransform>();
                dayRect.anchorMin = Vector2.zero;
                dayRect.anchorMax = Vector2.one;
                dayRect.offsetMin = new Vector2(8, 0);
                dayRect.offsetMax = new Vector2(-8, 0);
            }

            if (showTime)
            {
                var timeObj = new GameObject("TimeLabel");
                timeObj.transform.SetParent(_timeDayObj.transform, false);
                _timeText = timeObj.AddComponent<TextMeshProUGUI>();
                _timeText.fontSize = 15;
                _timeText.fontStyle = FontStyles.Bold;
                _timeText.color = new Color(1f, 0.9f, 0.4f); // warm gold
                _timeText.alignment = showDay ? TextAlignmentOptions.Right : TextAlignmentOptions.Center;
                _timeText.raycastTarget = false;
                _timeText.text = "";

                var timeRect = timeObj.GetComponent<RectTransform>();
                timeRect.anchorMin = Vector2.zero;
                timeRect.anchorMax = Vector2.one;
                timeRect.offsetMin = new Vector2(8, 0);
                timeRect.offsetMax = new Vector2(-8, 0);
            }
        }

        private void CreateRankBar(int size, int margin, bool belowTimeDay)
        {
            if (!Config.MinimapShowRank.Value) return;

            _rankBarObj = new GameObject("MinimapRankBar");
            _rankBarObj.transform.SetParent(_canvasObj.transform, false);

            var bgImage = _rankBarObj.AddComponent<Image>();
            bgImage.color = new Color(0.08f, 0.08f, 0.08f, 0.85f);
            bgImage.raycastTarget = false;

            var bgRect = _rankBarObj.GetComponent<RectTransform>();

            bool isTop = _cfgPosition.StartsWith("Top");
            bool isRight = _cfgPosition.EndsWith("Right");

            float anchorX = isRight ? 1f : 0f;
            float pivotX = isRight ? 1f : 0f;
            float anchorY = isTop ? 1f : 0f;
            float pivotY = isTop ? 1f : 0f;

            bgRect.anchorMin = new Vector2(anchorX, anchorY);
            bgRect.anchorMax = new Vector2(anchorX, anchorY);
            bgRect.pivot = new Vector2(pivotX, pivotY);

            float labelWidth = size + (_cfgBorderWidth * 2);
            float panelHeight = 52f;
            bgRect.sizeDelta = new Vector2(labelWidth, panelHeight);

            float xSign = isRight ? -1f : 1f;
            float bw = _cfgBorderWidth;
            float xPos = xSign * (margin - bw);
            float minimapTotalHeight = size + (bw * 2);
            float gap = 2f;
            float timeDayHeight = belowTimeDay ? 24f + gap : 0f;
            float yOffset = (margin - bw) + minimapTotalHeight + gap + timeDayHeight;
            if (_cfgPosition == "BottomRight") yOffset += 80;
            float yPos = isTop ? -yOffset : yOffset;
            bgRect.anchoredPosition = new Vector2(xPos, yPos);

            // Rank text (upper ~50% of panel)
            var rankObj = new GameObject("RankLabel");
            rankObj.transform.SetParent(_rankBarObj.transform, false);
            _rankText = rankObj.AddComponent<TextMeshProUGUI>();
            _rankText.fontSize = 15;
            _rankText.fontStyle = FontStyles.Bold;
            _rankText.color = new Color(1f, 0.9f, 0.4f);
            _rankText.alignment = TextAlignmentOptions.Center;
            _rankText.raycastTarget = false;
            _rankText.text = "";
            var rankRect = rankObj.GetComponent<RectTransform>();
            rankRect.anchorMin = new Vector2(0f, 0.52f);
            rankRect.anchorMax = new Vector2(1f, 1f);
            rankRect.offsetMin = new Vector2(6, 0);
            rankRect.offsetMax = new Vector2(-6, -4);

            // XP bar background
            var xpBgObj = new GameObject("XPBarBg");
            xpBgObj.transform.SetParent(_rankBarObj.transform, false);
            var xpBgImg = xpBgObj.AddComponent<Image>();
            xpBgImg.color = new Color(0.15f, 0.15f, 0.15f, 1f);
            xpBgImg.raycastTarget = false;
            var xpBgRect = xpBgObj.GetComponent<RectTransform>();
            xpBgRect.anchorMin = new Vector2(0f, 0.38f);
            xpBgRect.anchorMax = new Vector2(1f, 0.52f);
            xpBgRect.offsetMin = new Vector2(6, 0);
            xpBgRect.offsetMax = new Vector2(-6, 0);

            // XP fill (child of bar bg — anchorMax.x driven by fill ratio)
            var xpFillObj = new GameObject("XPBarFill");
            xpFillObj.transform.SetParent(xpBgObj.transform, false);
            _xpBarFill = xpFillObj.AddComponent<Image>();
            _xpBarFill.color = new Color(0.25f, 0.85f, 0.35f, 1f);
            _xpBarFill.raycastTarget = false;
            var xpFillRect = xpFillObj.GetComponent<RectTransform>();
            xpFillRect.anchorMin = Vector2.zero;
            xpFillRect.anchorMax = new Vector2(0f, 1f);
            xpFillRect.offsetMin = Vector2.zero;
            xpFillRect.offsetMax = Vector2.zero;

            // XP text (lower portion of panel)
            var xpTextObj = new GameObject("XPLabel");
            xpTextObj.transform.SetParent(_rankBarObj.transform, false);
            _xpText = xpTextObj.AddComponent<TextMeshProUGUI>();
            _xpText.fontSize = 15;
            _xpText.fontStyle = FontStyles.Normal;
            _xpText.color = new Color(0.65f, 0.65f, 0.65f, 1f);
            _xpText.alignment = TextAlignmentOptions.Center;
            _xpText.raycastTarget = false;
            _xpText.text = "";
            var xpTextRect = xpTextObj.GetComponent<RectTransform>();
            xpTextRect.anchorMin = new Vector2(0f, 0f);
            xpTextRect.anchorMax = new Vector2(1f, 0.28f);
            xpTextRect.offsetMin = new Vector2(6, 3);
            xpTextRect.offsetMax = new Vector2(-6, 0);
        }

        private void DestroyMinimap()
        {
            if (_canvasObj != null)
            {
                UnityEngine.Object.Destroy(_canvasObj);
                _canvasObj = null;
            }
            _mapRect = null;
            _rotationPivot = null;
            _markersParent = null;
            _playerMarkerRect = null;
            _borderImage = null;
            _timeDayObj = null;
            _timeText = null;
            _dayText = null;
            _rankBarObj = null;
            _rankText = null;
            _xpBarFill = null;
            _xpText = null;
            _lastKnownXP = -1;
            _lastKnownTier = -1;
            foreach (var d in _xpDrops)
                if (d.Label != null) UnityEngine.Object.Destroy(d.Label.gameObject);
            _xpDrops.Clear();
            _poiClones.Clear();
            _customerClones.Clear();
            _poiToPlayer.Clear();
            _activeCloneIds.Clear();
            _cachedPOIs = null;
            _npcPoiTemplate = null;
            _zoom = 0;
        }

        private void SpawnXPDrop(int delta, bool levelUp = false, int levels = 1)
        {
            if (_rankBarObj == null || _canvasObj == null) return;
            if (delta == 0 && !levelUp) return;

            var barRect = _rankBarObj.GetComponent<RectTransform>();

            var dropObj = new GameObject("XPDrop");
            dropObj.transform.SetParent(_canvasObj.transform, false);

            var label = dropObj.AddComponent<TextMeshProUGUI>();
            label.fontSize = 15;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;

            if (delta > 0 && levelUp)
            {
                // Combined: green XP + royal purple level-up
                label.color = new Color(0.4f, 1f, 0.4f, 1f);
                label.richText = true;
                string lvlPart = levels == 1 ? "+1 Level" : $"+{levels} Levels";
                label.text = $"+{delta} XP <color=#9B40E8>— {lvlPart}</color>";
            }
            else if (delta > 0)
            {
                label.color = new Color(0.4f, 1f, 0.4f, 1f);
                label.text = $"+{delta} XP";
            }
            else
            {
                // Standalone level-up (no XP delta this frame)
                label.color = new Color(0.6f, 0.3f, 1f, 1f);
                label.fontSize = 15;
                label.text = levels == 1 ? "+1 Level" : $"+{levels} Levels";
            }

            var dropRect = dropObj.GetComponent<RectTransform>();
            dropRect.anchorMin = barRect.anchorMin;
            dropRect.anchorMax = barRect.anchorMax;
            dropRect.pivot = barRect.pivot;
            dropRect.sizeDelta = new Vector2(barRect.sizeDelta.x, 20f);

            // Start just above the top edge of the rank bar
            float pivotY = barRect.pivot.y;
            float topEdge = barRect.anchoredPosition.y + (pivotY < 0.5f ? barRect.sizeDelta.y : 0f);
            float startY = topEdge + 4f;
            dropRect.anchoredPosition = new Vector2(barRect.anchoredPosition.x, startY);

            _xpDrops.Add(new XPDropLabel
            {
                Label = label,
                Rect = dropRect,
                Timer = 0f,
                StartY = startY,
                StartX = barRect.anchoredPosition.x
            });
        }

        private void UpdateXPDrops()
        {
            for (int i = _xpDrops.Count - 1; i >= 0; i--)
            {
                var drop = _xpDrops[i];
                drop.Timer += Time.deltaTime;
                float t = Mathf.Clamp01(drop.Timer / XPDropLabel.Duration);

                drop.Rect.anchoredPosition = new Vector2(drop.StartX, drop.StartY + XPDropLabel.Rise * t);

                float alpha = t < 0.4f ? 1f : Mathf.Clamp01(1f - (t - 0.4f) / 0.6f);
                var c = drop.Label.color;
                drop.Label.color = new Color(c.r, c.g, c.b, alpha);

                if (drop.Timer >= XPDropLabel.Duration)
                {
                    UnityEngine.Object.Destroy(drop.Label.gameObject);
                    _xpDrops.RemoveAt(i);
                }
            }
        }

        private void UpdateMinimap()
        {
            // Lazy init — MapApp may not be ready on first frame
            if (_canvasObj == null)
            {
                CreateMinimap();
                if (_canvasObj == null) return;
            }

            if (_mapRect == null || _rotationPivot == null) return;

            try
            {
                var player = Player.Local;
                if (player == null) return;

                var mapUtil = Singleton<MapPositionUtility>.Instance;
                if (mapUtil == null) return;

                Vector3 playerWorldPos = player.CurrentVehicle != null
                    ? player.CurrentVehicle.transform.position
                    : player.transform.position;
                Vector2 playerMapPos = mapUtil.GetMapPosition(playerWorldPos);

                // Scale from ContentRect space to minimap pixels
                float scaleX = _displaySize / _contentW;
                float scaleY = _displaySize / _contentH;

                // Center map image on player position (offset within RotationPivot)
                _mapRect.anchoredPosition = new Vector2(-playerMapPos.x * scaleX, -playerMapPos.y * scaleY);

                // Get player facing direction (use vehicle rotation when in a vehicle)
                float yRot = 0f;
                try
                {
                    if (player.CurrentVehicle != null)
                        yRot = player.CurrentVehicle.transform.eulerAngles.y;
                    else
                    {
                        var movement = PlayerMovement.Instance;
                        if (movement != null)
                            yRot = movement.transform.eulerAngles.y;
                    }
                }
                catch { }

                // Rotation: spin RotationPivot (centered on player marker) so map rotates around the player
                if (Config.MinimapRotateWithPlayer.Value)
                {
                    _rotationPivot.localEulerAngles = new Vector3(0f, 0f, yRot);
                    // Marker always points up when map rotates (direction shown by map orientation)
                    if (_playerMarkerRect != null)
                        _playerMarkerRect.localEulerAngles = Vector3.zero;
                }
                else
                {
                    _rotationPivot.localEulerAngles = Vector3.zero;
                    // Marker rotates to show player facing direction
                    // Sprite points up (0°=north), yRot is CW degrees, UI Z-rotation is CCW
                    if (_playerMarkerRect != null)
                        _playerMarkerRect.localEulerAngles = new Vector3(0f, 0f, -yRot);
                }

                // Refresh POI cache periodically
                if (_cachedPOIs == null || Time.time - _lastPOIRefresh > 5f)
                {
                    _cachedPOIs = UnityEngine.Object.FindObjectsOfType<POI>();
                    _lastPOIRefresh = Time.time;

                    // Build POI → remote Player lookup so we can update their facing direction
                    _poiToPlayer.Clear();
                    try
                    {
                        var playerList = Player.PlayerList;
                        for (int i = 0; i < playerList.Count; i++)
                        {
                            var p = playerList[i];
                            if (p == null || p == player) continue;
                            var poi = p.PoI;
                            if (poi != null) _poiToPlayer[poi.GetInstanceID()] = p;
                        }
                    }
                    catch { }
                }

                // Force-update POI positions (they only update when MapApp is open)
                foreach (var poi in _cachedPOIs)
                {
                    try
                    {
                        if (poi != null && poi.AutoUpdatePosition && poi.UI != null)
                            poi.UpdatePosition();
                    }
                    catch { }
                }

                // Culling threshold — expand when rotating to avoid pop-in at corners
                float halfSize = _cfgSize / 2f;
                bool rotating = Config.MinimapRotateWithPlayer.Value;
                float cullThreshold = rotating ? halfSize * 1.5f : halfSize;
                float iconScale = _cfgIconScale;
                float counterRot = rotating ? -_rotationPivot.localEulerAngles.z : 0f;

                // Mirror POI UI elements as cloned icons
                _activeCloneIds.Clear();

                foreach (var poi in _cachedPOIs)
                {
                    try
                    {
                        if (poi == null || poi.UI == null) continue;
                        if (poi == (POI)player.PoI) continue;
                        if (!IsPoiVisible(poi)) continue;

                        Vector2 poiMapPos = poi.UI.anchoredPosition;

                        // Cull check uses player-relative distance in minimap pixels
                        float relX = (poiMapPos.x - playerMapPos.x) * scaleX;
                        float relY = (poiMapPos.y - playerMapPos.y) * scaleY;
                        if (Mathf.Abs(relX) > cullThreshold || Mathf.Abs(relY) > cullThreshold)
                            continue;

                        int id = poi.GetInstanceID();
                        _activeCloneIds.Add(id);

                        if (!_poiClones.TryGetValue(id, out var cloneRect) || cloneRect == null)
                        {
                            var clone = UnityEngine.Object.Instantiate(poi.UI.gameObject, _markersParent, false);
                            clone.name = "PoiClone_" + poi.name;
                            cloneRect = clone.GetComponent<RectTransform>();
                            cloneRect.anchorMin = new Vector2(0.5f, 0.5f);
                            cloneRect.anchorMax = new Vector2(0.5f, 0.5f);
                            cloneRect.pivot = new Vector2(0.5f, 0.5f);
                            cloneRect.localScale = new Vector3(iconScale, iconScale, iconScale);
                            _poiClones[id] = cloneRect;

                            // For remote player POIs: zero the game's IconContainer rotation
                            // so our root-level rotation controls facing direction cleanly
                            if (_poiToPlayer.ContainsKey(id))
                            {
                                var ic = cloneRect.Find("IconContainer");
                                if (ic != null) ic.localEulerAngles = Vector3.zero;
                            }
                        }

                        // Absolute map-space position — MapImage panning handles centering on player
                        cloneRect.anchoredPosition = new Vector2(poiMapPos.x * scaleX, poiMapPos.y * scaleY);

                        // Counter-rotate so icons stay upright when map rotates.
                        // For remote players, also apply their facing direction.
                        float cloneRot = counterRot;
                        if (_poiToPlayer.TryGetValue(id, out var remotePlayer))
                        {
                            float remoteYRot = remotePlayer.CurrentVehicle != null
                                ? remotePlayer.CurrentVehicle.transform.eulerAngles.y
                                : remotePlayer.transform.eulerAngles.y;
                            cloneRot += -remoteYRot;
                        }
                        cloneRect.localEulerAngles = new Vector3(0f, 0f, cloneRot);
                        cloneRect.gameObject.SetActive(true);
                    }
                    catch { }
                }

                // Hide clones for POIs no longer in view
                foreach (var kvp in _poiClones)
                {
                    if (!_activeCloneIds.Contains(kvp.Key) && kvp.Value != null)
                        kvp.Value.gameObject.SetActive(false);
                }

                // Unlocked customer markers (their POIs are disabled, so FindObjectsOfType misses them)
                if (Config.MinimapShowCustomers.Value)
                {
                    // Grab an NPCPoI UI as a cloning template (once)
                    if (_npcPoiTemplate == null)
                    {
                        foreach (var poi in _cachedPOIs)
                        {
                            try
                            {
                                if (poi == null || poi.UI == null) continue;
                                if (poi.gameObject.name.StartsWith("NPCPoI"))
                                {
                                    _npcPoiTemplate = poi.UI.gameObject;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }

                    var unlocked = Customer.UnlockedCustomers;
                    for (int i = 0; i < unlocked.Count; i++)
                    {
                        try
                        {
                            var cust = unlocked[i];
                            if (cust == null || cust.NPC == null) continue;

                            Vector2 custMapPos = mapUtil.GetMapPosition(cust.transform.position);

                            float relX = (custMapPos.x - playerMapPos.x) * scaleX;
                            float relY = (custMapPos.y - playerMapPos.y) * scaleY;
                            if (Mathf.Abs(relX) > cullThreshold || Mathf.Abs(relY) > cullThreshold)
                            {
                                int outId = cust.GetInstanceID();
                                if (_customerClones.TryGetValue(outId, out var outRect) && outRect != null)
                                    outRect.gameObject.SetActive(false);
                                continue;
                            }

                            int id = cust.GetInstanceID();

                            if (!_customerClones.TryGetValue(id, out var cloneRect) || cloneRect == null)
                            {
                                if (_npcPoiTemplate != null)
                                {
                                    // Clone the NPCPoI UI for a proper mugshot marker
                                    var clone = UnityEngine.Object.Instantiate(_npcPoiTemplate, _markersParent, false);
                                    clone.name = "CustClone_" + (cust.NPC.FirstName ?? "");
                                    cloneRect = clone.GetComponent<RectTransform>();

                                    // Disable interactivity
                                    foreach (var graphic in clone.GetComponentsInChildren<Graphic>(true))
                                        graphic.raycastTarget = false;
                                    foreach (var txt in clone.GetComponentsInChildren<Text>(true))
                                        txt.gameObject.SetActive(false);

                                    // Set the mugshot
                                    try
                                    {
                                        var iconContainer = cloneRect.Find("IconContainer");
                                        if (iconContainer != null)
                                        {
                                            var iconImg = iconContainer.Find("Outline/Icon")?.GetComponent<Image>();
                                            if (iconImg != null && cust.NPC.MugshotSprite != null)
                                                iconImg.sprite = cust.NPC.MugshotSprite;
                                        }
                                    }
                                    catch { }
                                }
                                else
                                {
                                    // Fallback: simple circle if no template found
                                    var dot = new GameObject("CustClone_" + (cust.NPC.FirstName ?? ""));
                                    dot.transform.SetParent(_markersParent, false);
                                    var img = dot.AddComponent<Image>();
                                    img.sprite = GetCircleMaskSprite();
                                    img.color = new Color(0.4f, 0.85f, 1f, 0.9f);
                                    img.raycastTarget = false;
                                    cloneRect = dot.GetComponent<RectTransform>();
                                    cloneRect.sizeDelta = new Vector2(12, 12);
                                }

                                cloneRect.anchorMin = new Vector2(0.5f, 0.5f);
                                cloneRect.anchorMax = new Vector2(0.5f, 0.5f);
                                cloneRect.pivot = new Vector2(0.5f, 0.5f);
                                cloneRect.localScale = new Vector3(iconScale, iconScale, iconScale);
                                _customerClones[id] = cloneRect;
                            }

                            cloneRect.anchoredPosition = new Vector2(custMapPos.x * scaleX, custMapPos.y * scaleY);
                            cloneRect.localEulerAngles = new Vector3(0f, 0f, counterRot);
                            cloneRect.gameObject.SetActive(true);
                        }
                        catch { }
                    }
                }
                else
                {
                    // Hide all customer clones when setting is off
                    foreach (var kvp in _customerClones)
                    {
                        if (kvp.Value != null)
                            kvp.Value.gameObject.SetActive(false);
                    }
                }

                // Live-update border color (no rebuild needed)
                if (_borderImage != null && Config.MinimapBorderColor != null)
                    _borderImage.color = Config.MinimapBorderColor.Value;

                // Update time/day display
                if (_timeText != null)
                {
                    try
                    {
                        if (_cfgUse24Hour)
                        {
                            int t = TimeManager.CurrentTime;
                            _timeText.text = $"{t / 100:D2}:{t % 100:D2}";
                        }
                        else
                        {
                            _timeText.text = TimeManager.GetFormatted12HourTime();
                        }
                    }
                    catch { _timeText.text = ""; }
                }

                if (_dayText != null)
                {
                    try
                    {
                        int dayIdx = (int)TimeManager.CurrentDay;
                        _dayText.text = (dayIdx >= 0 && dayIdx < ShortDayNames.Length)
                            ? ShortDayNames[dayIdx]
                            : TimeManager.CurrentDay.ToString().Substring(0, 3);
                    }
                    catch { _dayText.text = ""; }
                }

                // Keep player marker on top
                if (_playerMarkerRect != null)
                    _playerMarkerRect.transform.SetAsLastSibling();

                // Update rank/XP bar
                if (_rankText != null)
                {
                    try
                    {
                        if (LevelManager.Exists)
                        {
                            var rank = LevelManager.Rank;
                            int tier = LevelManager.Tier;
                            int rankIdx = (int)rank;
                            string rankName = rankIdx >= 0 && rankIdx < RankNames.Length
                                ? RankNames[rankIdx]
                                : rank.ToString();
                            string tierStr = tier >= 1 && tier <= 5 ? RomanTiers[tier] : tier.ToString();
                            _rankText.text = $"{rankName} {tierStr}";

                            int xp = LevelManager.XP;
                            bool tierChanged = _lastKnownTier >= 0 && tier > _lastKnownTier;
                            int tierDelta = tierChanged ? tier - _lastKnownTier : 0;

                            if (_lastKnownXP >= 0 && xp > _lastKnownXP)
                                SpawnXPDrop(xp - _lastKnownXP, tierChanged, tierDelta);
                            else if (tierChanged)
                                SpawnXPDrop(0, true, tierDelta); // XP already reset; standalone level drop

                            _lastKnownXP = xp;
                            _lastKnownTier = tier;

                            float xpToNext = LevelManager.XPToNextTier;
                            float ratio = xpToNext > 0 ? Mathf.Clamp01(xp / xpToNext) : 1f;
                            if (_xpBarFill != null)
                                _xpBarFill.rectTransform.anchorMax = new Vector2(ratio, 1f);
                            if (_xpText != null)
                                _xpText.text = $"{xp} / {Mathf.RoundToInt(xpToNext)} XP";
                        }
                    }
                    catch { }
                }

                UpdateXPDrops();
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"UpdateMinimap: {ex.Message}");
            }
        }

        private static void ApplyPosition(RectTransform rect, int size, int margin, string pos)
        {
            float m = margin;

            switch (pos)
            {
                case "TopLeft":
                    rect.anchorMin = new Vector2(0, 1);
                    rect.anchorMax = new Vector2(0, 1);
                    rect.pivot = new Vector2(0, 1);
                    rect.anchoredPosition = new Vector2(m, -m);
                    break;
                case "BottomRight":
                    rect.anchorMin = new Vector2(1, 0);
                    rect.anchorMax = new Vector2(1, 0);
                    rect.pivot = new Vector2(1, 0);
                    rect.anchoredPosition = new Vector2(-m, m + 80); // +80 clears the bottom HUD bar
                    break;
                case "BottomLeft":
                    rect.anchorMin = new Vector2(0, 0);
                    rect.anchorMax = new Vector2(0, 0);
                    rect.pivot = new Vector2(0, 0);
                    rect.anchoredPosition = new Vector2(m, m);
                    break;
                default: // TopRight (defensive — SnapshotConfig validates before reaching here)
                    rect.anchorMin = new Vector2(1, 1);
                    rect.anchorMax = new Vector2(1, 1);
                    rect.pivot = new Vector2(1, 1);
                    rect.anchoredPosition = new Vector2(-m, -m);
                    break;
            }

            rect.sizeDelta = new Vector2(size, size);
        }

        private static bool IsPoiVisible(POI poi)
        {
            string goName = poi.gameObject.name;

            // NPC-specific POI prefabs (checked by GO name prefix)
            if (goName.StartsWith("PotentialCustomerPoI"))
                return Config.MinimapShowPotentialCustomers.Value;

            if (goName.StartsWith("PotentialDealerPoI"))
                return Config.MinimapShowDealers.Value;

            if (goName.StartsWith("ContractPoI"))
                return Config.MinimapShowContracts.Value;

            if (goName.StartsWith("QuestPoI"))
                return Config.MinimapShowQuests.Value;

            // NPCPoI(Clone) — check text to distinguish dealers, managers, and customers
            if (goName.StartsWith("NPCPoI"))
            {
                string text = poi.MainText ?? "";
                if (text.Contains("(Dealer)"))
                    return Config.MinimapShowDealers.Value;
                if (text.Contains("(Manager)"))
                    return Config.MinimapShowManagers.Value;
                return Config.MinimapShowCustomers.Value;
            }

            // OTC quest POIs (POIPrefab from our quest system)
            if (goName.StartsWith("POIPrefab"))
                return Config.MinimapShowQuests.Value;

            // Generic POI — classify by text and UI prefab name
            string mainText = poi.MainText ?? "";

            if (mainText.StartsWith("Dead Drop"))
                return Config.MinimapShowDeadDrops.Value;
            if (mainText.Contains("(Owned)"))
                return Config.MinimapShowProperties.Value;

            // Check UI prefab name for property POIs that don't say "(Owned)"
            if (poi.UI != null && poi.UI.gameObject.name.StartsWith("PropertyPoI"))
                return Config.MinimapShowProperties.Value;

            return true;
        }

        private static Sprite GetCircleMaskSprite()
        {
            if (_circleMaskSprite != null) return _circleMaskSprite;

            int res = 256;
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
            float center = res / 2f;
            float radius = center;

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                    tex.SetPixel(x, y, dist <= radius ? Color.white : Color.clear);
                }
            }
            tex.Apply();

            _circleMaskSprite = Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f));
            return _circleMaskSprite;
        }
    }
}
