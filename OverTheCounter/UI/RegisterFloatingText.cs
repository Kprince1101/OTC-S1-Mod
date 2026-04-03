using MelonLoader;
using OverTheCounter.Logic.Placement;
using System.Collections;
using UnityEngine;

#if IL2CPP
using Il2CppTMPro;
#else
using TMPro;
#endif

namespace OverTheCounter.UI
{
    /// <summary>
    /// Spawns a worldspace floating text notification above a checkout counter's register.
    /// Shows the sale total and tip, then rises upward and fades out.
    /// </summary>
    public static class RegisterFloatingText
    {
        private const float Duration = 2.5f;
        private const float FadeStart = 1.5f;
        private const float RiseDistance = 0.5f;
        private const float CanvasScale = 0.005f;

        /// <summary>
        /// Shows a floating sale/tip notification above the counter's register.
        /// </summary>
        public static void Show(CheckoutCounterInstance counter, float saleTotal, float tip)
        {
            if (counter == null) return;

            var pos = counter.RegisterPosition;
            if (!pos.HasValue) pos = counter.SurfacePosition;
            if (!pos.HasValue) return;

            Vector3 startPos = pos.Value + Vector3.up * 0.7f;
            MelonCoroutines.Start(AnimateCoroutine(startPos, saleTotal, tip));
        }

        private static IEnumerator AnimateCoroutine(Vector3 startPos, float saleTotal, float tip)
        {
            // Root object
            var root = new GameObject("RegisterFloatingText");
            root.transform.position = startPos;

            // Worldspace canvas
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 100;

            var canvasRect = root.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(400, 60);
            root.transform.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);

            // Text
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(root.transform, false);

            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = 28;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            tmp.richText = true;
            TMPFactory.SetWrapping(tmp, false);

            if (tip > 0f)
                tmp.text = $"<color=#66FF66>+${saleTotal:F0}</color> / <color=#FFD700>${tip:F0} tip</color>";
            else
                tmp.text = $"<color=#66FF66>+${saleTotal:F0}</color>";

            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            // Animate
            var cam = Camera.main;
            float elapsed = 0f;
            while (elapsed < Duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / Duration);

                // Rise
                root.transform.position = startPos + Vector3.up * (RiseDistance * t);

                // Billboard — face camera
                if (cam == null) cam = Camera.main;
                if (cam != null)
                    root.transform.rotation = cam.transform.rotation;

                // Fade
                float alpha = elapsed < FadeStart
                    ? 1f
                    : Mathf.Clamp01(1f - (elapsed - FadeStart) / (Duration - FadeStart));
                var color = tmp.color;
                tmp.color = new Color(color.r, color.g, color.b, alpha);
                tmp.faceColor = new Color32(255, 255, 255, (byte)(alpha * 255));

                yield return null;
            }

            UnityEngine.Object.Destroy(root);
        }
    }
}
