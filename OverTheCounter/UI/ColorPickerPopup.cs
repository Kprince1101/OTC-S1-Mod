using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

#if IL2CPP
using Il2CppTMPro;
using GameCanvasScaler = Il2CppScheduleOne.UI.CanvasScaler;
#else
using TMPro;
using GameCanvasScaler = ScheduleOne.UI.CanvasScaler;
#endif

namespace OverTheCounter.UI
{
    /// <summary>
    /// Reusable modal HSV color picker popup.
    /// Shows a saturation-value box, hue bar, preview swatch, and confirm/cancel buttons.
    /// Usage: ColorPickerPopup.Show(currentColor, title, onConfirm)
    /// </summary>
    public static class ColorPickerPopup
    {
        private static GameObject _canvas;

        // Current HSV state
        private static float _hue;
        private static float _sat;
        private static float _val;

        // UI references updated during interaction
        private static RawImage _svImage;
        private static Texture2D _svTex;
        private static RawImage _hueImage;
        private static Texture2D _hueTex;
        private static Image _previewImage;
        private static Image _svCursor;
        private static Image _hueCursor;
        private static RectTransform _svRect;
        private static RectTransform _hueRect;
        private static TextMeshProUGUI _hexLabel;

        private const int SV_SIZE = 160;
        private const int HUE_WIDTH = 24;
        private const int HUE_HEIGHT = 160;

        /// <summary>
        /// Opens the color picker popup.
        /// </summary>
        /// <param name="initialColor">Starting color.</param>
        /// <param name="title">Title text shown at the top.</param>
        /// <param name="onConfirm">Called with the chosen color when user clicks Confirm.</param>
        public static void Show(Color initialColor, string title, Action<Color> onConfirm)
        {
            if (_canvas != null) return;

            Color.RGBToHSV(initialColor, out _hue, out _sat, out _val);

            // Canvas
            var canvasGO = new GameObject("OTC_ColorPicker");
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 201;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGO.AddComponent<GameCanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();
            _canvas = canvasGO;

            // Backdrop
            var backdrop = new GameObject("Backdrop");
            backdrop.transform.SetParent(canvasGO.transform, false);
            var bdRT = backdrop.AddComponent<RectTransform>();
            bdRT.anchorMin = Vector2.zero;
            bdRT.anchorMax = Vector2.one;
            bdRT.offsetMin = Vector2.zero;
            bdRT.offsetMax = Vector2.zero;
            var bdImg = backdrop.AddComponent<Image>();
            bdImg.color = new Color(0f, 0f, 0f, 0.6f);

            // Panel  (SV box + hue bar + preview + buttons)
            var panel = new GameObject("Panel");
            panel.transform.SetParent(canvasGO.transform, false);
            var panelRT = panel.AddComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(320, 310);
            var panelImg = panel.AddComponent<Image>();
            panelImg.color = new Color(0.1f, 0.1f, 0.12f, 0.97f);
            panelImg.sprite = TMPFactory.GetRoundedSprite();
            panelImg.type = Image.Type.Sliced;

            // Title
            var titleTmp = TMPFactory.Text("Title", title, panel.transform,
                18, TextAlignmentOptions.Center, FontStyles.Bold);
            titleTmp.color = Color.white;
            var titleRT = titleTmp.GetComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0f, 1f);
            titleRT.anchorMax = new Vector2(1f, 1f);
            titleRT.pivot = new Vector2(0.5f, 1f);
            titleRT.sizeDelta = new Vector2(0, 36);
            titleRT.anchoredPosition = new Vector2(0, -4);

            // --- SV box (left side) ---
            var svGo = new GameObject("SVBox");
            svGo.transform.SetParent(panel.transform, false);
            _svRect = svGo.AddComponent<RectTransform>();
            _svRect.anchorMin = new Vector2(0, 1);
            _svRect.anchorMax = new Vector2(0, 1);
            _svRect.pivot = new Vector2(0, 1);
            _svRect.sizeDelta = new Vector2(SV_SIZE, SV_SIZE);
            _svRect.anchoredPosition = new Vector2(16, -44);

            _svTex = new Texture2D(SV_SIZE, SV_SIZE, TextureFormat.RGB24, false);
            _svTex.filterMode = FilterMode.Bilinear;
            _svTex.wrapMode = TextureWrapMode.Clamp;
            _svImage = svGo.AddComponent<RawImage>();
            _svImage.texture = _svTex;

            // SV cursor (small circle indicator)
            _svCursor = CreateCursor("SVCursor", svGo.transform, 12);

            // SV drag handling
            AddDragHandler(svGo, OnSVDrag);

            // --- Hue bar (right of SV box) ---
            var hueGo = new GameObject("HueBar");
            hueGo.transform.SetParent(panel.transform, false);
            _hueRect = hueGo.AddComponent<RectTransform>();
            _hueRect.anchorMin = new Vector2(0, 1);
            _hueRect.anchorMax = new Vector2(0, 1);
            _hueRect.pivot = new Vector2(0, 1);
            _hueRect.sizeDelta = new Vector2(HUE_WIDTH, HUE_HEIGHT);
            _hueRect.anchoredPosition = new Vector2(16 + SV_SIZE + 12, -44);

            _hueTex = new Texture2D(1, HUE_HEIGHT, TextureFormat.RGB24, false);
            _hueTex.filterMode = FilterMode.Bilinear;
            _hueTex.wrapMode = TextureWrapMode.Clamp;
            _hueImage = hueGo.AddComponent<RawImage>();
            _hueImage.texture = _hueTex;
            GenerateHueTexture();

            // Hue cursor (horizontal indicator)
            _hueCursor = CreateCursor("HueCursor", hueGo.transform, 6);
            var hueCursorRT = _hueCursor.GetComponent<RectTransform>();
            hueCursorRT.sizeDelta = new Vector2(HUE_WIDTH + 6, 6);

            // Hue drag handling
            AddDragHandler(hueGo, OnHueDrag);

            // --- Preview swatch + hex label (right column) ---
            float rightX = 16 + SV_SIZE + 12 + HUE_WIDTH + 16;
            float rightW = 320 - rightX - 12;

            var previewGo = new GameObject("Preview");
            previewGo.transform.SetParent(panel.transform, false);
            var prevRT = previewGo.AddComponent<RectTransform>();
            prevRT.anchorMin = new Vector2(0, 1);
            prevRT.anchorMax = new Vector2(0, 1);
            prevRT.pivot = new Vector2(0, 1);
            prevRT.sizeDelta = new Vector2(rightW, rightW);
            prevRT.anchoredPosition = new Vector2(rightX, -44);
            _previewImage = previewGo.AddComponent<Image>();
            _previewImage.sprite = TMPFactory.GetRoundedSprite();
            _previewImage.type = Image.Type.Sliced;

            // Hex label under preview
            var hexTmp = TMPFactory.Text("HexLabel", "", panel.transform,
                15, TextAlignmentOptions.Center, FontStyles.Normal);
            hexTmp.color = new Color(0.7f, 0.7f, 0.7f);
            var hexRT = hexTmp.GetComponent<RectTransform>();
            hexRT.anchorMin = new Vector2(0, 1);
            hexRT.anchorMax = new Vector2(0, 1);
            hexRT.pivot = new Vector2(0, 1);
            hexRT.sizeDelta = new Vector2(rightW, 22);
            hexRT.anchoredPosition = new Vector2(rightX, -44 - rightW - 4);

            // --- Buttons ---
            var btnRow = new GameObject("ButtonRow");
            btnRow.transform.SetParent(panel.transform, false);
            var btnRowRT = btnRow.AddComponent<RectTransform>();
            btnRowRT.anchorMin = new Vector2(0.05f, 0f);
            btnRowRT.anchorMax = new Vector2(0.95f, 0f);
            btnRowRT.pivot = new Vector2(0.5f, 0f);
            btnRowRT.sizeDelta = new Vector2(0, 42);
            btnRowRT.anchoredPosition = new Vector2(0, 10);
            var hlg = btnRow.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 12;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleCenter;

            CreateButton(btnRow.transform, "Cancel", new Color(0.3f, 0.3f, 0.32f), Close);

            CreateButton(btnRow.transform, "Confirm", new Color(0.15f, 0.55f, 0.3f), () =>
            {
                var color = CurrentColor();
                Close();
                onConfirm?.Invoke(color);
            });

            // Initial render
            GenerateSVTexture();
            UpdateCursors();
            UpdatePreview();
            // Store hex ref for live updates
            _hexLabel = hexTmp;
            UpdateHexLabel();
        }

        // ==================================================================
        //  Drag handlers
        // ==================================================================

        private static void OnSVDrag(PointerEventData eventData)
        {
            if (_svRect == null) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _svRect, eventData.position, eventData.pressEventCamera, out var local);

            float w = _svRect.sizeDelta.x;
            float h = _svRect.sizeDelta.y;
            _sat = Mathf.Clamp01((local.x + w * _svRect.pivot.x) / w);
            _val = Mathf.Clamp01((local.y + h * _svRect.pivot.y) / h);

            UpdateCursors();
            UpdatePreview();
            UpdateHexLabel();
        }

        private static void OnHueDrag(PointerEventData eventData)
        {
            if (_hueRect == null) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _hueRect, eventData.position, eventData.pressEventCamera, out var local);

            float h = _hueRect.sizeDelta.y;
            _hue = Mathf.Clamp01((local.y + h * _hueRect.pivot.y) / h);

            GenerateSVTexture();
            UpdateCursors();
            UpdatePreview();
            UpdateHexLabel();
        }

        // ==================================================================
        //  Texture generation
        // ==================================================================

        private static void GenerateSVTexture()
        {
            if (_svTex == null) return;
            for (int y = 0; y < SV_SIZE; y++)
            {
                float v = (float)y / (SV_SIZE - 1);
                for (int x = 0; x < SV_SIZE; x++)
                {
                    float s = (float)x / (SV_SIZE - 1);
                    _svTex.SetPixel(x, y, Color.HSVToRGB(_hue, s, v));
                }
            }
            _svTex.Apply();
        }

        private static void GenerateHueTexture()
        {
            if (_hueTex == null) return;
            for (int y = 0; y < HUE_HEIGHT; y++)
            {
                float h = (float)y / (HUE_HEIGHT - 1);
                _hueTex.SetPixel(0, y, Color.HSVToRGB(h, 1f, 1f));
            }
            _hueTex.Apply();
        }

        // ==================================================================
        //  UI updates
        // ==================================================================

        private static void UpdateCursors()
        {
            if (_svCursor != null)
            {
                var rt = _svCursor.GetComponent<RectTransform>();
                rt.anchoredPosition = new Vector2(_sat * SV_SIZE, _val * SV_SIZE);
            }
            if (_hueCursor != null)
            {
                var rt = _hueCursor.GetComponent<RectTransform>();
                rt.anchoredPosition = new Vector2(HUE_WIDTH * 0.5f, _hue * HUE_HEIGHT);
            }
        }

        private static void UpdatePreview()
        {
            if (_previewImage != null)
                _previewImage.color = CurrentColor();
        }

        private static void UpdateHexLabel()
        {
            if (_hexLabel != null)
            {
                var c = CurrentColor();
                _hexLabel.text = $"#{ColorUtility.ToHtmlStringRGB(c)}";
            }
        }

        /// <summary>Returns the currently selected color from the HSV picker.</summary>
        public static Color CurrentColor() => Color.HSVToRGB(_hue, _sat, _val);

        // ==================================================================
        //  Helpers
        // ==================================================================

        private static Image CreateCursor(string name, Transform parent, float size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            var img = go.AddComponent<Image>();
            img.color = Color.white;
            img.raycastTarget = false;

            // Dark outline ring via child
            var outline = new GameObject("Outline");
            outline.transform.SetParent(go.transform, false);
            var oRT = outline.AddComponent<RectTransform>();
            oRT.anchorMin = Vector2.zero;
            oRT.anchorMax = Vector2.one;
            oRT.offsetMin = new Vector2(2, 2);
            oRT.offsetMax = new Vector2(-2, -2);
            var oImg = outline.AddComponent<Image>();
            oImg.color = new Color(0.05f, 0.05f, 0.05f);
            oImg.raycastTarget = false;

            return img;
        }

        private static void AddDragHandler(GameObject go, Action<PointerEventData> handler)
        {
            var trigger = go.AddComponent<EventTrigger>();

            var dragEntry = new EventTrigger.Entry { eventID = EventTriggerType.Drag };
            dragEntry.callback.AddListener(new Action<BaseEventData>(e =>
            {
                if (e is PointerEventData ped) handler(ped);
            }));
            trigger.triggers.Add(dragEntry);

            // Also handle initial click (PointerDown) so single taps work
            var downEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            downEntry.callback.AddListener(new Action<BaseEventData>(e =>
            {
                if (e is PointerEventData ped) handler(ped);
            }));
            trigger.triggers.Add(downEntry);
        }

        private static void CreateButton(Transform parent, string label, Color bgColor, Action onClick)
        {
            var btnGO = new GameObject($"Btn_{label}");
            btnGO.transform.SetParent(parent, false);
            var btnImg = btnGO.AddComponent<Image>();
            btnImg.color = bgColor;
            btnImg.sprite = TMPFactory.GetRoundedSprite();
            btnImg.type = Image.Type.Sliced;
            var btn = btnGO.AddComponent<Button>();
            btn.targetGraphic = btnImg;
            btn.onClick.AddListener(new Action(onClick));

            var txt = TMPFactory.Text($"Label_{label}", label, btnGO.transform,
                16, TextAlignmentOptions.Center, FontStyles.Bold);
            txt.color = Color.white;
            var txtRT = txt.GetComponent<RectTransform>();
            txtRT.anchorMin = Vector2.zero;
            txtRT.anchorMax = Vector2.one;
            txtRT.offsetMin = Vector2.zero;
            txtRT.offsetMax = Vector2.zero;
        }

        private static void Close()
        {
            if (_canvas != null)
            {
                GameObject.Destroy(_canvas);
                _canvas = null;
            }
            if (_svTex != null)
            {
                GameObject.Destroy(_svTex);
                _svTex = null;
            }
            if (_hueTex != null)
            {
                GameObject.Destroy(_hueTex);
                _hueTex = null;
            }
            _svImage = null;
            _hueImage = null;
            _previewImage = null;
            _svCursor = null;
            _hueCursor = null;
            _svRect = null;
            _hueRect = null;
            _hexLabel = null;
        }
    }
}
