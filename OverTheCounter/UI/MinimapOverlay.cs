using Il2CppInterop.Runtime.Injection;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI.Phone.Map;
using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace OverTheCounter.UI
{
    [RegisterTypeInIl2Cpp]
    public class MinimapOverlay : MonoBehaviour
    {
        private static readonly MelonLogger.Instance Logger = new("OTC: Minimap");

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

        // Cached config values — rebuild minimap when structural settings change
        private int _cfgSize;
        private bool _cfgCircle;
        private string _cfgPosition;
        private KeyCode _cfgToggleKey;
        private float _cfgIconScale;
        private int _cfgBorderWidth;

        private static readonly HashSet<string> ValidPositions = new()
        {
            "TopLeft", "TopRight", "BottomLeft", "BottomRight"
        };

        public static void Register()
        {
            ClassInjector.RegisterTypeInIl2Cpp<MinimapOverlay>();
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

            if (Input.GetKeyDown(_toggleKey) && !Il2CppScheduleOne.GameInput.IsTyping)
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
                Logger.Warning($"Invalid MinimapPosition '{rawPos}', defaulting to TopRight");
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

            _toggleKey = _cfgToggleKey;
        }

        private bool ConfigChanged()
        {
            return Config.MinimapSize.Value != _cfgSize
                || Config.MinimapCircle.Value != _cfgCircle
                || (Config.MinimapPosition?.Value ?? "TopRight") != _cfgPosition
                || (Config.MinimapToggleKey?.Value ?? KeyCode.N) != _cfgToggleKey
                || Math.Abs(Config.MinimapIconScale.Value - _cfgIconScale) > 0.001f
                || Config.MinimapBorderWidth.Value != _cfgBorderWidth;
        }

        private void ToggleMinimap()
        {
            _zoom = (_zoom + 1) % 4; // 0→1→2→3→0
            _visible = _zoom > 0;

            // Sync zoom back to config so the settings UI reflects the current level
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
            _canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();
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
            catch (Exception ex) { Logger.Warning($"Failed to clone player POI: {ex.Message}"); }

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

            SnapshotConfig();
            _cachedPOIs = null;
            _lastPOIRefresh = 0f;
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
            _poiClones.Clear();
            _customerClones.Clear();
            _activeCloneIds.Clear();
            _cachedPOIs = null;
            _npcPoiTemplate = null;
            _zoom = 0;
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

                Vector3 playerWorldPos = player.transform.position;
                Vector2 playerMapPos = mapUtil.GetMapPosition(playerWorldPos);

                // Scale from ContentRect space to minimap pixels
                float scaleX = _displaySize / _contentW;
                float scaleY = _displaySize / _contentH;

                // Center map image on player position (offset within RotationPivot)
                _mapRect.anchoredPosition = new Vector2(-playerMapPos.x * scaleX, -playerMapPos.y * scaleY);

                // Get player facing direction
                float yRot = 0f;
                try
                {
                    var movement = PlayerMovement.Instance;
                    if (movement != null)
                        yRot = movement.transform.eulerAngles.y;
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
                        }

                        // Absolute map-space position — MapImage panning handles centering on player
                        cloneRect.anchoredPosition = new Vector2(poiMapPos.x * scaleX, poiMapPos.y * scaleY);
                        // Counter-rotate so icons stay upright when map rotates
                        cloneRect.localEulerAngles = new Vector3(0f, 0f, counterRot);
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

                // Keep player marker on top
                if (_playerMarkerRect != null)
                    _playerMarkerRect.transform.SetAsLastSibling();
            }
            catch (Exception ex)
            {
                Logger.Warning($"UpdateMinimap: {ex.Message}");
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
