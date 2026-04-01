using MelonLoader;
using OverTheCounter.Utilities;
using S1API.GameTime;
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

        // Compass labels (N, E, S, W) — children of Canvas, positioned inside minimap area
        private RectTransform[] _compassRects;

        // Map dimensions from MapApp.ContentRect
        private float _contentW = 2048f;
        private float _contentH = 2048f;
        private float _displaySize;

        // POI clone tracking
        private readonly Dictionary<int, RectTransform> _poiClones = new();
        private readonly HashSet<int> _activeCloneIds = new();
        private readonly HashSet<int> _poiIconSynced = new(); // tracks clones whose icon sprite has been synced
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

        // Computed minimap position (bottom-left corner of border in canvas coords)
        private float _minimapX;
        private float _minimapY;
        private float _minimapTotalSize;

        // Canvas scaler reference + built-with dimensions for change detection
        private UnityEngine.UI.CanvasScaler _unityScaler;
        private float _builtCanvasW;
        private float _builtCanvasH;

        // Remote player POI → Player lookup (rebuilt on POI cache refresh)
        private readonly Dictionary<int, Player> _poiToPlayer = new();

        // Cached config values — rebuild minimap when structural settings change
        private int _cfgSize;
        private bool _cfgCircle;
        private int _cfgHOffset;
        private int _cfgVOffset;
        private bool _cfgInfoOnTop;
        private KeyCode _cfgToggleKey;
        private float _cfgIconScale;
        private int _cfgBorderWidth;
        private bool _cfgShowTime;
        private bool _cfgShowDay;
        private bool _cfgUse24Hour;
        // Performance limiting — see MinimapPerfLimiter for algorithm details
        private readonly MinimapPerfLimiter _perfLimiter = new();

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
            PerfTracker.Begin("MinimapOverlay");
            try
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

                // Hide when game is paused
                if (Singleton<PauseMenu>.InstanceExists && Singleton<PauseMenu>.Instance.IsPaused)
                {
                    if (_canvasObj != null && _canvasObj.activeSelf) _canvasObj.SetActive(false);
                    return;
                }

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
                {
                    if (_perfLimiter.ShouldSkipExpensiveUpdate())
                    {
                        UpdateMinimap();
                        return;
                    }

                    _perfLimiter.BeginTiming();
                    UpdateMinimap();
                    _perfLimiter.EndTiming();
                }
            }
            finally { PerfTracker.End("MinimapOverlay"); }
        }

        private void SnapshotConfig()
        {
            _cfgSize = Mathf.Clamp(Config.MinimapSize.Value, 100, 600);
            if (Config.MinimapSize.Value != _cfgSize)
                Config.MinimapSize.RawEntry.Value = _cfgSize;

            _cfgCircle = Config.MinimapCircle.Value;

            _cfgHOffset = Mathf.Clamp(Config.MinimapHorizontalOffset.Value, 0, 100);
            if (Config.MinimapHorizontalOffset.Value != _cfgHOffset)
                Config.MinimapHorizontalOffset.RawEntry.Value = _cfgHOffset;

            _cfgVOffset = Mathf.Clamp(Config.MinimapVerticalOffset.Value, 0, 100);
            if (Config.MinimapVerticalOffset.Value != _cfgVOffset)
                Config.MinimapVerticalOffset.RawEntry.Value = _cfgVOffset;

            _cfgInfoOnTop = Config.MinimapInfoOnTop.Value;

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
            _toggleKey = _cfgToggleKey;
        }

        private bool ConfigChanged()
        {
            // Detect screen resolution or UI Scale changes that affect canvas dimensions
            GetEffectiveCanvasSize(out float cw, out float ch);
            if (Math.Abs(cw - _builtCanvasW) > 1f || Math.Abs(ch - _builtCanvasH) > 1f)
                return true;

            return Config.MinimapSize.Value != _cfgSize
                || Config.MinimapCircle.Value != _cfgCircle
                || Config.MinimapHorizontalOffset.Value != _cfgHOffset
                || Config.MinimapVerticalOffset.Value != _cfgVOffset
                || Config.MinimapInfoOnTop.Value != _cfgInfoOnTop
                || (Config.MinimapToggleKey?.Value ?? KeyCode.N) != _cfgToggleKey
                || Math.Abs(Config.MinimapIconScale.Value - _cfgIconScale) > 0.001f
                || Config.MinimapBorderWidth.Value != _cfgBorderWidth
                || Config.MinimapShowTime.Value != _cfgShowTime
                || Config.MinimapShowDay.Value != _cfgShowDay
                || Config.MinimapUse24HourClock.Value != _cfgUse24Hour;
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

            // Canvas (ScreenSpace Overlay, below HUD so game tutorials/notifications render on top)
            _canvasObj = new GameObject("OTC_MinimapCanvas");
            var canvas = _canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -1;
            _unityScaler = _canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();
            _unityScaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _unityScaler.referenceResolution = new Vector2(1920, 1080);
            _unityScaler.matchWidthOrHeight = 0.5f;
            _canvasObj.AddComponent<GameCanvasScaler>();
            UnityEngine.Object.DontDestroyOnLoad(_canvasObj);
            Canvas.ForceUpdateCanvases();

            // Compute effective canvas dimensions (accounts for UI Scale + screen aspect ratio)
            GetEffectiveCanvasSize(out float canvasW, out float canvasH);
            OTCLog.Msg(OTCLog.Systems.Patch, $"Canvas effective size: {canvasW:F0}x{canvasH:F0} (screen {Screen.width}x{Screen.height})");
            _builtCanvasW = canvasW;
            _builtCanvasH = canvasH;

            // Border (slightly larger background behind the container)
            var borderObj = new GameObject("MinimapBorder");
            borderObj.transform.SetParent(_canvasObj.transform, false);
            _borderImage = borderObj.AddComponent<Image>();
            _borderImage.color = Config.MinimapBorderColor?.Value ?? new Color(0.2f, 0.2f, 0.2f, 0.9f);
            _borderImage.raycastTarget = false;
            var borderRect = borderObj.GetComponent<RectTransform>();
            int bw = _cfgBorderWidth;
            _minimapTotalSize = size + (bw * 2);
            ApplyFreePosition(borderRect, _minimapTotalSize, margin, _cfgHOffset, _cfgVOffset, canvasW, canvasH);
            _minimapX = borderRect.anchoredPosition.x;
            _minimapY = borderRect.anchoredPosition.y;
            if (circle)
                _borderImage.sprite = GetCircleMaskSprite();

            // Container (dark background + clip mask — inset by border width)
            var containerObj = new GameObject("MinimapContainer");
            containerObj.transform.SetParent(_canvasObj.transform, false);
            var containerImg = containerObj.AddComponent<Image>();
            containerImg.color = new Color(0.05f, 0.05f, 0.05f, 0.85f);
            containerImg.raycastTarget = false;
            var containerRect = containerObj.GetComponent<RectTransform>();
            containerRect.anchorMin = Vector2.zero;
            containerRect.anchorMax = Vector2.zero;
            containerRect.pivot = Vector2.zero;
            containerRect.sizeDelta = new Vector2(size, size);
            containerRect.anchoredPosition = new Vector2(_minimapX + bw, _minimapY + bw);

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

            CreateCompassLabels();

            // Time/day label — positioned above or below minimap based on InfoOnTop
            CreateTimeDayLabel();

            SnapshotConfig();
            _cachedPOIs = null;
            _lastPOIRefresh = 0f;
        }

        // ── Compass labels ──

        private static readonly string[] CompassLetters = { "N", "E", "S", "W" };
        private static readonly float[] CompassWorldAngles = { 0f, 90f, 180f, 270f };

        private void CreateCompassLabels()
        {
            if (_canvasObj == null) return;

            _compassRects = new RectTransform[4];
            for (int i = 0; i < 4; i++)
            {
                var go = new GameObject("Compass_" + CompassLetters[i]);
                go.transform.SetParent(_canvasObj.transform, false);

                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(20f, 20f);

                // N: Image on parent GO for dark circle background, TMP on child
                // S/E/W: TMP directly on GO (no background)
                if (i == 0)
                {
                    var bgImg = go.AddComponent<Image>();
                    bgImg.sprite = GetCircleMaskSprite();
                    bgImg.color = new Color(0f, 0f, 0f, 0.7f);
                    bgImg.raycastTarget = false;

                    var tmp = TMPFactory.Text("NLabel", "N", go.transform, 15,
                        TextAlignmentOptions.Center, FontStyles.Bold);
                    tmp.color = new Color(1f, 0.3f, 0.3f, 0.9f);
                    tmp.raycastTarget = false;
                }
                else
                {
                    var tmp = TMPFactory.Text(CompassLetters[i], CompassLetters[i], go.transform, 15,
                        TextAlignmentOptions.Center, FontStyles.Bold);
                    tmp.color = new Color(0.9f, 0.9f, 0.9f, 0.65f);
                    tmp.raycastTarget = false;
                    // Override stretch anchors — parent GO controls positioning
                    var tmpRt = tmp.rectTransform;
                    tmpRt.anchorMin = Vector2.zero;
                    tmpRt.anchorMax = Vector2.one;
                    tmpRt.offsetMin = Vector2.zero;
                    tmpRt.offsetMax = Vector2.zero;
                }

                _compassRects[i] = rt;
            }
        }

        private void UpdateCompassLabels(float yRot)
        {
            if (_compassRects == null) return;

            bool show = Config.MinimapShowCompass.Value;
            float centerX = _minimapX + _minimapTotalSize / 2f;
            float centerY = _minimapY + _minimapTotalSize / 2f;
            float radius = _cfgSize / 2f - 11f;
            bool rotating = Config.MinimapRotateWithPlayer.Value;

            for (int i = 0; i < 4; i++)
            {
                var rt = _compassRects[i];
                if (rt == null) continue;

                rt.gameObject.SetActive(show);
                if (!show) continue;

                float screenAngleRad = (rotating
                    ? CompassWorldAngles[i] - yRot
                    : CompassWorldAngles[i]) * Mathf.Deg2Rad;

                float dirX = Mathf.Sin(screenAngleRad);
                float dirY = Mathf.Cos(screenAngleRad);

                Vector2 offset = _cfgCircle
                    ? new Vector2(dirX, dirY) * radius
                    : ClampToSquareEdge(dirX, dirY, radius);
                rt.anchoredPosition = new Vector2(centerX + offset.x, centerY + offset.y);
                rt.localEulerAngles = Vector3.zero;
            }
        }

        // ── Edge indicators ──

        /// <summary>Projects a direction vector onto the boundary of a square with the given half-size.</summary>
        private static Vector2 ClampToSquareEdge(float dirX, float dirY, float half)
        {
            float ax = Mathf.Abs(dirX);
            float ay = Mathf.Abs(dirY);
            if (ax < 0.0001f && ay < 0.0001f) return Vector2.zero;

            float scale = ax > ay ? half / ax : half / ay;
            return new Vector2(dirX * scale, dirY * scale);
        }

        private static bool ShouldShowEdgeIndicator(POI poi)
        {
            string goName = poi.gameObject.name;
            if (goName.StartsWith("QuestPoI") || goName.StartsWith("POIPrefab")) return true;
            if (goName.StartsWith("ContractPoI")) return true;
            if (goName.StartsWith("PotentialCustomerPoI")) return true;
            if (goName.StartsWith("PotentialDealerPoI")) return true;
            return false;
        }

        private static readonly string[] ShortDayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        private void CreateTimeDayLabel()
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
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.zero;
            bgRect.pivot = Vector2.zero;

            float labelWidth = _minimapTotalSize;
            float labelHeight = 24f;
            bgRect.sizeDelta = new Vector2(labelWidth, labelHeight);

            float gap = 2f;
            float yPos = _cfgInfoOnTop
                ? _minimapY + _minimapTotalSize + gap
                : _minimapY - gap - labelHeight;
            bgRect.anchoredPosition = new Vector2(_minimapX, yPos);

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

        private void DestroyMinimap()
        {
            if (_canvasObj != null)
            {
                UnityEngine.Object.Destroy(_canvasObj);
                _canvasObj = null;
            }
            _unityScaler = null;
            _mapRect = null;
            _rotationPivot = null;
            _markersParent = null;
            _playerMarkerRect = null;
            _borderImage = null;
            _timeDayObj = null;
            _timeText = null;
            _dayText = null;
            _poiClones.Clear();
            _poiIconSynced.Clear();
            _customerClones.Clear();
            _poiToPlayer.Clear();
            _activeCloneIds.Clear();
            _cachedPOIs = null;
            _npcPoiTemplate = null;
            _zoom = 0;
            _compassRects = null;
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

                UpdateCompassLabels(yRot);

                // Performance limiter: on throttled frames, skip expensive POI/clone work
                if (!_perfLimiter.IsFullUpdate)
                {
                    UpdateMinimapDisplays();
                    return;
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
                float cullThreshold = rotating ? halfSize * 1.5f : halfSize + 20f;
                float iconScale = _cfgIconScale;
                float counterRot = rotating ? -_rotationPivot.localEulerAngles.z : 0f;

                // Mirror POI UI elements as cloned icons
                _activeCloneIds.Clear();
                float yRotRad = yRot * Mathf.Deg2Rad;
                float cosYRot = Mathf.Cos(yRotRad);
                float sinYRot = Mathf.Sin(yRotRad);
                float edgeHalf = halfSize - 10f;

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
                        float distSq = relX * relX + relY * relY;
                        bool beyondCull = _cfgCircle
                            ? distSq > cullThreshold * cullThreshold
                            : Mathf.Abs(relX) > cullThreshold || Mathf.Abs(relY) > cullThreshold;
                        bool edgeWorthy = Config.MinimapShowEdgeIndicators.Value && ShouldShowEdgeIndicator(poi);

                        // Screen-space coords (mask clips in screen space, not map space)
                        float screenX, screenY;
                        if (rotating)
                        {
                            screenX = relX * cosYRot - relY * sinYRot;
                            screenY = relX * sinYRot + relY * cosYRot;
                        }
                        else
                        {
                            screenX = relX;
                            screenY = relY;
                        }

                        // Edge-clamp slightly before the mask edge to avoid partial-clip blind spots
                        float visibleR = halfSize - 5f;
                        bool outsideVisible = _cfgCircle
                            ? distSq > visibleR * visibleR
                            : Mathf.Abs(screenX) > visibleR || Mathf.Abs(screenY) > visibleR;
                        bool showAtEdge = edgeWorthy && outsideVisible;

                        if (beyondCull && !edgeWorthy)
                        {
                            int offId = poi.GetInstanceID();
                            if (_poiClones.TryGetValue(offId, out var offRect) && offRect != null)
                                offRect.gameObject.SetActive(false);
                            continue;
                        }

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

                            // Disable text labels (e.g. quest descriptions)
                            foreach (var txt in clone.GetComponentsInChildren<Text>(true))
                                txt.gameObject.SetActive(false);

                            // For remote player POIs: zero the game's IconContainer rotation
                            // so our root-level rotation controls facing direction cleanly
                            if (_poiToPlayer.ContainsKey(id))
                            {
                                var ic = cloneRect.Find("IconContainer");
                                if (ic != null) ic.localEulerAngles = Vector3.zero;
                            }
                        }

                        // Sync icon sprite from source POI until valid (fixes IL2CPP race where
                        // mugshot hasn't loaded yet when the clone is first created)
                        if (!_poiIconSynced.Contains(id))
                        {
                            try
                            {
                                var srcIcon = poi.IconContainer?.Find("Outline/Icon")?.GetComponent<Image>();
                                if (srcIcon != null && srcIcon.sprite != null)
                                {
                                    var dstIcon = cloneRect.Find("IconContainer/Outline/Icon")?.GetComponent<Image>();
                                    if (dstIcon != null)
                                    {
                                        dstIcon.sprite = srcIcon.sprite;
                                        _poiIconSynced.Add(id);
                                    }
                                }
                            }
                            catch { }
                        }

                        if (showAtEdge)
                        {
                            // Clamp screen-space direction to edge, transform back to markers-parent
                            Vector2 edgeScreen = _cfgCircle
                                ? new Vector2(screenX, screenY).normalized * edgeHalf
                                : ClampToSquareEdge(screenX, screenY, edgeHalf);

                            if (rotating)
                            {
                                float ex = edgeScreen.x * cosYRot + edgeScreen.y * sinYRot;
                                float ey = -edgeScreen.x * sinYRot + edgeScreen.y * cosYRot;
                                cloneRect.anchoredPosition = new Vector2(
                                    playerMapPos.x * scaleX + ex,
                                    playerMapPos.y * scaleY + ey);
                            }
                            else
                            {
                                cloneRect.anchoredPosition = new Vector2(
                                    playerMapPos.x * scaleX + edgeScreen.x,
                                    playerMapPos.y * scaleY + edgeScreen.y);
                            }
                        }
                        else
                        {
                            // Absolute map-space position — MapImage panning handles centering on player
                            cloneRect.anchoredPosition = new Vector2(poiMapPos.x * scaleX, poiMapPos.y * scaleY);
                        }

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
                                if (poi.TryCast<NPCPoI>() != null)
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

                                    // Strip all root children except IconContainer
                                    // (removes pink radius circle, text labels, etc.)
                                    for (int c = cloneRect.childCount - 1; c >= 0; c--)
                                    {
                                        var child = cloneRect.GetChild(c);
                                        if (child.name != "IconContainer")
                                            child.gameObject.SetActive(false);
                                    }

                                    try
                                    {
                                        var iconContainer = cloneRect.Find("IconContainer");
                                        if (iconContainer != null)
                                        {
                                            var outlineImg = iconContainer.Find("Outline")?.GetComponent<Image>();
                                            if (outlineImg != null)
                                                outlineImg.color = Color.white;

                                            var iconImg = iconContainer.Find("Outline/Icon")?.GetComponent<Image>();
                                            if (iconImg != null && cust.NPC.MugshotSprite != null)
                                                iconImg.sprite = cust.NPC.MugshotSprite;
                                        }
                                    }
                                    catch { }

                                    // Render behind potential customer POI clones
                                    cloneRect.SetAsFirstSibling();
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

                UpdateMinimapDisplays();
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"UpdateMinimap: {ex.Message}");
            }
        }

        /// <summary>
        /// Cheap per-frame updates: border color, time/day text, rank/XP bar, XP drop animations.
        /// Called every frame (including throttled frames) so displays stay smooth.
        /// </summary>
        private void UpdateMinimapDisplays()
        {
            if (_borderImage != null && Config.MinimapBorderColor != null)
                _borderImage.color = Config.MinimapBorderColor.Value;

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

            if (_playerMarkerRect != null)
                _playerMarkerRect.transform.SetAsLastSibling();
        }

        /// <summary>
        /// Returns the effective canvas coordinate space. Reads from the Canvas
        /// RectTransform when available (ground truth from Unity's layout system),
        /// falling back to manual CanvasScaler computation on the first frame.
        /// Reading the actual rect avoids mismatch on ultrawide/non-16:9 monitors
        /// where GameCanvasScaler modifies the Unity scaler's referenceResolution.
        /// </summary>
        private void GetEffectiveCanvasSize(out float w, out float h)
        {
            if (_canvasObj != null)
            {
                var rt = _canvasObj.GetComponent<RectTransform>();
                if (rt != null)
                {
                    var rect = rt.rect;
                    if (rect.width > 1f && rect.height > 1f)
                    {
                        w = rect.width;
                        h = rect.height;
                        return;
                    }
                }
            }

            if (_unityScaler == null)
            {
                w = 1920f; h = 1080f;
                return;
            }

            Vector2 refRes = _unityScaler.referenceResolution;
            float sw = Screen.width;
            float sh = Screen.height;
            float match = _unityScaler.matchWidthOrHeight;

            float logW = Mathf.Log(sw / refRes.x, 2f);
            float logH = Mathf.Log(sh / refRes.y, 2f);
            float scaleFactor = Mathf.Pow(2f, Mathf.Lerp(logW, logH, match));

            w = sw / scaleFactor;
            h = sh / scaleFactor;
        }

        private static void ApplyFreePosition(RectTransform rect, float totalSize, float margin,
            int hOffset, int vOffset, float canvasW, float canvasH)
        {
            float xMin = margin;
            float xMax = canvasW - totalSize - margin;
            float yMin = margin;
            float yMax = canvasH - totalSize - margin;

            float x = Mathf.Lerp(xMin, xMax, hOffset / 100f);
            float y = Mathf.Lerp(yMax, yMin, vOffset / 100f);

            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.sizeDelta = new Vector2(totalSize, totalSize);
            rect.anchoredPosition = new Vector2(x, y);
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

                // Check if this is a modded NPC POI (police, cartel, etc.)
                // NPCPoI has a .NPC property — if the NPC isn't a Customer, it's from another mod.
                var npcPoi = poi.TryCast<NPCPoI>();
                if (npcPoi?.NPC != null && npcPoi.NPC.TryCast<Customer>() == null)
                    return Config.MinimapShowModdedNPCs.Value;

                return Config.MinimapShowCustomers.Value;
            }

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
