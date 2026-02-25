using UnityEngine;

#if IL2CPP
using Il2CppTMPro;
#else
using TMPro;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Helper for adding TextMeshPro signs to buildings.
    /// </summary>
    public static class BuildingSignage
    {
        private const int ExtrusionLayers = 8;
        private const float ExtrusionDepth = 0.06f; // total depth in meters

        /// <summary>
        /// Creates a 3D-extruded TextMeshPro sign and parents it to the given transform.
        /// </summary>
        /// <param name="parent">Transform to attach the sign to.</param>
        /// <param name="text">Display text.</param>
        /// <param name="localPosition">Local position relative to parent.</param>
        /// <param name="localRotation">Local rotation relative to parent.</param>
        /// <param name="fontSize">TMP font size.</param>
        /// <param name="color">Front face color (extrusion uses a darker shade).</param>
        public static GameObject AddTextSign(
            Transform parent,
            string text,
            Vector3 localPosition,
            Quaternion localRotation,
            float fontSize,
            Color color)
        {
            var signGo = new GameObject("Sign_" + text);
            signGo.transform.SetParent(parent);
            signGo.transform.localPosition = localPosition;
            signGo.transform.localRotation = localRotation;

            // Front face (the visible text)
            var tmp = signGo.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;

            var rect = signGo.GetComponent<RectTransform>();
            if (rect != null)
                rect.sizeDelta = new Vector2(10f, 2f);

            tmp.ForceMeshUpdate();

            // Extrusion layers behind the front face for 3D depth
            float step = ExtrusionDepth / ExtrusionLayers;
            var darkerColor = color * 0.6f;
            darkerColor.a = 1f;

            for (int i = 1; i <= ExtrusionLayers; i++)
            {
                var layerGo = new GameObject($"Extrude_{i}");
                layerGo.transform.SetParent(signGo.transform);
                layerGo.transform.localPosition = new Vector3(0f, 0f, step * i);
                layerGo.transform.localRotation = Quaternion.identity;
                layerGo.transform.localScale = Vector3.one;

                var layerTmp = layerGo.AddComponent<TextMeshPro>();
                layerTmp.text = text;
                layerTmp.fontSize = fontSize;
                layerTmp.color = darkerColor;
                layerTmp.alignment = TextAlignmentOptions.Center;

                var layerRect = layerGo.GetComponent<RectTransform>();
                if (layerRect != null)
                    layerRect.sizeDelta = new Vector2(10f, 2f);

                layerTmp.ForceMeshUpdate();
            }

            return signGo;
        }
    }
}
