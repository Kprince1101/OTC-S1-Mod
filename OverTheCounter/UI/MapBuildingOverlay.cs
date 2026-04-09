using System.Collections.Generic;
using OverTheCounter.Utilities;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI.Phone.Map;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Map;
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI.Phone.Map;
#endif

namespace OverTheCounter.UI
{
    /// <summary>
    /// Paints dark gray building footprints onto the phone map sprite texture
    /// so OTC buildings appear on both the phone map and minimap.
    /// </summary>
    public static class MapBuildingOverlay
    {
        private struct Footprint
        {
            public Vector3 SwCorner; // world-space SW corner
            public float Width;      // X extent
            public float Depth;      // Z extent
        }

        private static readonly List<Footprint> _footprints = new();
        private static Sprite _originalSprite;
        private static bool _painted;

        // Dark gray matching vanilla building rectangles on the map
        private static readonly Color BuildingColor = new(0.22f, 0.22f, 0.22f, 1f);

        /// <summary>Register a building footprint for map painting.</summary>
        public static void Register(Vector3 swCorner, float width, float depth)
        {
            _footprints.Add(new Footprint { SwCorner = swCorner, Width = width, Depth = depth });
        }

        /// <summary>Clear all registered footprints and restore original sprite.</summary>
        public static void Clear()
        {
            _footprints.Clear();
            if (_originalSprite != null)
            {
                try
                {
                    var mapApp = PlayerSingleton<MapApp>.Instance;
                    if (mapApp != null)
                    {
                        mapApp.MainMapSprite = _originalSprite;
                        mapApp.BackgroundImage.sprite = _originalSprite;
                    }
                }
                catch { }
            }
            _originalSprite = null;
            _painted = false;
        }

        /// <summary>
        /// Paint registered building footprints onto the map sprite.
        /// Call after buildings are spawned and MapApp + MapPositionUtility are ready.
        /// Returns true if painting succeeded, false if singletons aren't ready yet.
        /// </summary>
        public static bool PaintBuildings()
        {
            if (_painted) return true;
            if (_footprints.Count == 0) return true;

            MapApp mapApp;
            MapPositionUtility mapUtil;
            try
            {
                mapApp = PlayerSingleton<MapApp>.Instance;
                mapUtil = Singleton<MapPositionUtility>.Instance;
            }
            catch { return false; }

            if (mapApp == null || mapUtil == null || mapApp.MainMapSprite == null)
                return false;

            var originalSprite = mapApp.MainMapSprite;
            var originalTex = originalSprite.texture;
            if (originalTex == null) return false;

            int w = originalTex.width;
            int h = originalTex.height;

            // Save original for cleanup restore
            _originalSprite = originalSprite;

            // Create a writable texture copy via RenderTexture (IL2CPP-safe)
            var newTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            newTex.filterMode = originalTex.filterMode;
            newTex.wrapMode = originalTex.wrapMode;

            var tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(originalTex, tmp);
            var prev = RenderTexture.active;
            RenderTexture.active = tmp;
            newTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);

            // Map coordinate system: GetMapPosition returns coords where (0,0) is center,
            // ranging roughly -1024..+1024 (MapDimensions/2).
            // The sprite rect defines which sub-region of the texture is displayed.
            // Map coords → texture pixel: px = mapPos.x + rect.x + rect.width/2
            var rect = originalSprite.rect;
            float centerX = rect.x + rect.width * 0.5f;
            float centerY = rect.y + rect.height * 0.5f;

            // Map coords (anchoredPosition) range ±MapDimensions/2.
            // The Image RectTransform is sized to MapDimensions UI units, but the
            // texture may be larger (e.g. 4096px for MapDimensions=2048 → scale=2).
            // texturePixel = mapCoord * scale + textureCenter
            float mapDimensions = mapUtil.MapDimensions;
            float scaleX = rect.width / mapDimensions;
            float scaleY = rect.height / mapDimensions;

            foreach (var fp in _footprints)
            {
                var swWorld = new Vector3(fp.SwCorner.x, 0f, fp.SwCorner.z);
                var neWorld = new Vector3(fp.SwCorner.x + fp.Width, 0f, fp.SwCorner.z + fp.Depth);

                var swMap = mapUtil.GetMapPosition(swWorld);
                var neMap = mapUtil.GetMapPosition(neWorld);

                // Convert map coords → texture pixel coords
                int px0 = Mathf.RoundToInt(swMap.x * scaleX + centerX);
                int py0 = Mathf.RoundToInt(swMap.y * scaleY + centerY);
                int px1 = Mathf.RoundToInt(neMap.x * scaleX + centerX);
                int py1 = Mathf.RoundToInt(neMap.y * scaleY + centerY);

                // Ensure min < max, clamp to texture bounds
                int minX = Mathf.Clamp(Mathf.Min(px0, px1), 0, w - 1);
                int maxX = Mathf.Clamp(Mathf.Max(px0, px1), 0, w - 1);
                int minY = Mathf.Clamp(Mathf.Min(py0, py1), 0, h - 1);
                int maxY = Mathf.Clamp(Mathf.Max(py0, py1), 0, h - 1);

                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                        newTex.SetPixel(x, y, BuildingColor);

            }

            newTex.Apply();

            // Create new sprite matching original's rect and pivot
            var newSprite = Sprite.Create(
                newTex,
                originalSprite.rect,
                new Vector2(originalSprite.pivot.x / originalSprite.rect.width,
                            originalSprite.pivot.y / originalSprite.rect.height),
                originalSprite.pixelsPerUnit);

            mapApp.MainMapSprite = newSprite;
            mapApp.BackgroundImage.sprite = newSprite;

            _painted = true;
            OTCLog.Msg(OTCLog.Systems.Patch, $"Map overlay: painted {_footprints.Count} building footprints");
            return true;
        }
    }
}
