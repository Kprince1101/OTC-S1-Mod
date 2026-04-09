using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppTMPro;
#else
using TMPro;
#endif

namespace OverTheCounter.UI
{
    /// <summary>
    /// TMP UI factory — based on S1API's UIFactory (Text, RoundedButtonWithLabel)
    /// but using TextMeshProUGUI for crisp SDF rendering and fixing visual artifacts
    /// (grey corners from showMaskGraphic, bitmap Arial at high DPI).
    /// </summary>
    public static class TMPFactory
    {
        /// <summary>
        /// Creates a TextMeshProUGUI element configured with the supplied content and styling.
        /// </summary>
        public static TextMeshProUGUI Text(
            string name,
            string content,
            Transform parent,
            int fontSize = 14,
            TextAlignmentOptions alignment = TextAlignmentOptions.TopLeft,
            FontStyles fontStyle = FontStyles.Normal)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = content;
            tmp.fontSize = fontSize;
            tmp.alignment = alignment;
            tmp.fontStyle = fontStyle;
            tmp.color = Color.white;
            tmp.richText = true;
            tmp.raycastTarget = true;
            SetWrapping(tmp, false);

            return tmp;
        }

        /// <summary>
        /// Sets word wrapping on a TMP text element, using the correct API per build target.
        /// </summary>
        public static void SetWrapping(TextMeshProUGUI tmp, bool wrap)
        {
#if IL2CPP
            tmp.enableWordWrapping = wrap;
#else
            tmp.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
#endif
        }

        private static Sprite _roundedSprite;

        /// <summary>
        /// Creates a rounded button with mask, inner Image, Button, and TMP label.
        /// Drop-in replacement for UIFactory.RoundedButtonWithLabel with no grey corners.
        /// </summary>
        public static (GameObject, Button, TextMeshProUGUI) RoundedButtonWithLabel(
            string name, string label, Transform parent,
            Color bgColor, float width, float height, int fontSize, Color textColor)
        {
            var maskGO = new GameObject(name + "_RoundedMask");
            maskGO.transform.SetParent(parent, false);

            var maskRT = maskGO.AddComponent<RectTransform>();
            maskRT.sizeDelta = new Vector2(width, height);

            var layoutElement = maskGO.AddComponent<LayoutElement>();
            layoutElement.preferredWidth = width;
            layoutElement.preferredHeight = height;

            var maskImage = maskGO.AddComponent<Image>();
            maskImage.sprite = GetRoundedSprite();
            maskImage.type = Image.Type.Sliced;
            maskImage.color = Color.white;

            var mask = maskGO.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            // Inner button fills the mask
            var buttonGO = new GameObject(name);
            buttonGO.transform.SetParent(maskGO.transform, false);

            var rt = buttonGO.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var img = buttonGO.AddComponent<Image>();
            img.color = bgColor;
            img.sprite = GetRoundedSprite();
            img.type = Image.Type.Sliced;

            var btn = buttonGO.AddComponent<Button>();
            btn.targetGraphic = img;

            // TMP label
            var tmp = Text("Label", label, buttonGO.transform, fontSize, TextAlignmentOptions.Center, FontStyles.Bold);
            tmp.color = textColor;

            return (maskGO, btn, tmp);
        }

        internal static Sprite GetRoundedSprite()
        {
            if (_roundedSprite != null)
                return _roundedSprite;

            int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            float radius = 6f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool isCorner =
                        (x < radius && y < radius && Vector2.Distance(new Vector2(x, y), new Vector2(radius, radius)) > radius) ||
                        (x > size - radius - 1 && y < radius && Vector2.Distance(new Vector2(x, y), new Vector2(size - radius - 1, radius)) > radius) ||
                        (x < radius && y > size - radius - 1 && Vector2.Distance(new Vector2(x, y), new Vector2(radius, size - radius - 1)) > radius) ||
                        (x > size - radius - 1 && y > size - radius - 1 && Vector2.Distance(new Vector2(x, y), new Vector2(size - radius - 1, size - radius - 1)) > radius);

                    tex.SetPixel(x, y, isCorner ? new Color(0, 0, 0, 0) : Color.white);
                }
            }

            tex.Apply();
            var border = new Vector4(8, 8, 8, 8);
            _roundedSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            return _roundedSprite;
        }
    }
}
