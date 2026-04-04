using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1MAPI.S1;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Money;
using Grid = Il2CppScheduleOne.Tiles.Grid;
using QualityItemInfoContent = Il2CppScheduleOne.UI.Items.QualityItemInfoContent;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Money;
using Grid = ScheduleOne.Tiles.Grid;
using QualityItemInfoContent = ScheduleOne.UI.Items.QualityItemInfoContent;
#endif

namespace OverTheCounter.Apps
{
    public partial class GreenTabApp
    {
        /// <summary>All building IDs that can appear in the GreenTab dropdown.</summary>
        private static readonly string[] AllBuildingIds =
        {
            PropertySaveData.ShackId,
            Dispensary.DispensaryId,
        };

        /// <summary>Returns owned building IDs eligible for GreenTab customization.</summary>
        private static List<string> GetOwnedBuildings()
        {
            var psd = PropertySaveData.Instance;
            var result = new List<string>();
            if (psd == null) return result;
            foreach (var bid in AllBuildingIds)
            {
                if (psd.IsPropertyOwned(bid))
                    result.Add(bid);
            }
            return result;
        }

        /// <summary>Returns the first counter in the selected building, or null.</summary>
        private CheckoutCounterInstance GetSelectedCounter()
        {
            foreach (var counter in CheckoutCounter.AllCounters)
            {
                if (counter.BuildingId == _selectedBuildingId)
                    return counter;
            }
            return null;
        }

        private static string GetBuildingDisplayName(string buildingId)
        {
            if (buildingId == AllPropertiesId) return "All Properties";
            return BuildingDisplayNames.TryGetValue(buildingId, out var name) ? name : buildingId ?? "Unknown";
        }

        /// <summary>Returns building IDs based on current dropdown selection.</summary>
        private List<string> GetBuildingsForSelection()
        {
            if (_selectedBuildingId == AllPropertiesId)
                return GetOwnedBuildings();
            return new List<string> { _selectedBuildingId };
        }

        /// <summary>Returns store status text and color for a building.</summary>
        private static (string text, Color color) GetStoreStatus(string buildingId)
        {
            bool toggleOn = buildingId == PropertySaveData.ShackId
                ? WestvilleShack.IsStoreOpen
                : Dispensary.IsStoreOpen;

            if (!toggleOn)
                return ("Closed", new Color(0.9f, 0.3f, 0.3f));

            int time = S1API.GameTime.TimeManager.CurrentTime;
            bool withinHours = time >= 800 && time < 2000;
            return withinHours
                ? ("Open", AccentGreen)
                : ("After Hours", new Color(0.4f, 0.65f, 0.95f));
        }

        /// <summary>Returns the placement grid for the given building ID, or null.</summary>
        private static Grid GetGridForBuilding(string buildingId) =>
            buildingId == PropertySaveData.ShackId ? WestvilleShack.ShackGrid : Dispensary.DispensaryGrid;

        private static float GetOnlineBalance()
        {
            try
            {
                return NetworkSingleton<MoneyManager>.Instance?.onlineBalance ?? 0f;
            }
            catch
            {
                return 0f;
            }
        }

        // ==================================================================
        //  Star quality sprite (shared between inventory + sales)
        // ==================================================================

        private static Sprite _starSprite;

        /// <summary>Returns the star sprite used for quality display. Pulls from QualityItemInfoContent.</summary>
        private static Sprite GetStarSprite()
        {
            if (_starSprite != null) return _starSprite;
            try
            {
                var allQiic = Resources.FindObjectsOfTypeAll<QualityItemInfoContent>();
                if (allQiic != null)
                {
                    for (int i = 0; i < allQiic.Length; i++)
                    {
                        if (allQiic[i]?.Star?.sprite != null)
                        {
                            _starSprite = allQiic[i].Star.sprite;
                            return _starSprite;
                        }
                    }
                }
            }
            catch { }
            _starSprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/Knob.psd");
            return _starSprite;
        }

        /// <summary>Returns the color for a quality star based on quality level.</summary>
        private static Color GetQualityStarColor(int quality) => quality switch
        {
            0 => new Color32(80, 145, 50, 255),    // Green
            1 => new Color32(80, 145, 50, 255),    // Green
            2 => new Color32(100, 190, 255, 255),  // Blue
            3 => new Color32(225, 75, 255, 255),   // Purple
            4 => new Color32(255, 200, 50, 255),   // Gold
            _ => Color.white
        };

        /// <summary>Creates a single color-coded quality star inside the given parent.</summary>
        private static void CreateQualityStars(Transform parent, int quality, float starSize = 10f)
        {
            var starSprite = GetStarSprite();
            if (starSprite == null || quality <= 0) return;

            var color = GetQualityStarColor(quality);

            var starGo = new GameObject("Star");
            starGo.transform.SetParent(parent, false);
            var starImg = starGo.AddComponent<Image>();
            starImg.sprite = starSprite;
            starImg.color = color;
            starImg.preserveAspect = true;
            starImg.raycastTarget = false;
            var starRt = starGo.GetComponent<RectTransform>();
            starRt.anchorMin = new Vector2(0, 0.5f);
            starRt.anchorMax = new Vector2(0, 0.5f);
            starRt.pivot = new Vector2(0, 0.5f);
            starRt.sizeDelta = new Vector2(starSize, starSize);
            starRt.anchoredPosition = Vector2.zero;
        }

        /// <summary>Formats 24h time int (e.g. 1330) to 12h string (e.g. "1:30 PM").</summary>
        internal static string FormatTime12h(int time24)
        {
            int h = time24 / 100;
            int m = time24 % 100;
            string ampm = h >= 12 ? "PM" : "AM";
            if (h == 0) h = 12;
            else if (h > 12) h -= 12;
            return $"{h}:{m:D2} {ampm}";
        }

        // ==================================================================
        //  Building-aware style accessors
        // ==================================================================

        private bool IsShackSelected => _selectedBuildingId == PropertySaveData.ShackId;

        private string GetCurrentLightingStyleId() =>
            IsShackSelected
                ? WestvilleShack.CurrentLightingStyleId ?? LightingStyle.Default.Id
                : Dispensary.CurrentLightingStyleId ?? LightingStyle.Default.Id;

        private string GetCurrentExteriorWallStyleId() =>
            IsShackSelected
                ? WestvilleShack.CurrentExteriorWallStyleId ?? WallStyle.ExtDefault.Id
                : Dispensary.CurrentExteriorWallStyleId ?? WallStyle.ExtDefault.Id;

        private string GetCurrentInteriorWallStyleId() =>
            IsShackSelected
                ? WestvilleShack.CurrentInteriorWallStyleId ?? WallStyle.IntDefault.Id
                : Dispensary.CurrentInteriorWallStyleId ?? WallStyle.IntDefault.Id;

        private string GetCurrentFloorStyleId() =>
            IsShackSelected
                ? WestvilleShack.CurrentFloorStyleId ?? FloorStyle.Default.Id
                : Dispensary.CurrentFloorStyleId ?? FloorStyle.Default.Id;

        private void ApplyBuildingLighting(LightingStyle style)
        {
            if (IsShackSelected)
                WestvilleShack.ApplyLightingStyle(style);
            else
                Dispensary.ApplyLightingStyle(style);
        }

        private void ApplyBuildingExteriorWall(string styleId)
        {
            var style = WallStyle.GetExterior(styleId);
            var mat = Materials.Find(style.MaterialName);
            if (IsShackSelected)
            {
                WestvilleShack.CurrentExteriorWallStyleId = styleId;
                if (mat != null) WestvilleShack.SwapExteriorWallMaterial(mat);
            }
            else
            {
                Dispensary.CurrentExteriorWallStyleId = styleId;
                if (mat != null) Dispensary.SwapExteriorWallMaterial(mat);
            }
        }

        private void ApplyBuildingInteriorWall(string styleId)
        {
            var style = WallStyle.GetInterior(styleId);
            var mat = Materials.Find(style.MaterialName);
            if (IsShackSelected)
            {
                WestvilleShack.CurrentInteriorWallStyleId = styleId;
                if (mat != null) WestvilleShack.SwapInteriorWallMaterial(mat);
            }
            else
            {
                Dispensary.CurrentInteriorWallStyleId = styleId;
                if (mat != null) Dispensary.SwapInteriorWallMaterial(mat);
            }
        }

        private void ApplyBuildingFloor(string styleId)
        {
            var newStyle = FloorStyle.Get(styleId);
            var mat = Materials.Find(newStyle.MaterialName);
            if (IsShackSelected)
            {
                WestvilleShack.CurrentFloorStyleId = styleId;
                if (mat != null) WestvilleShack.SwapFloorMaterial(mat);
            }
            else
            {
                Dispensary.CurrentFloorStyleId = styleId;
                if (mat != null) Dispensary.SwapFloorMaterial(mat);
            }
        }

        // ==================================================================
        //  Shared resource sprite loader (base Resources/ path)
        // ==================================================================

        private static Sprite _chevronSprite;

        private static Sprite GetChevronSprite()
        {
            if (_chevronSprite != null) return _chevronSprite;
            try
            {
                string resourceName = "OverTheCounter.Resources.ChevronIcon.png";
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream == null) return null;

                byte[] data = new byte[stream.Length];
                stream.Read(data, 0, data.Length);

                var tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, data))
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }

                _chevronSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.General, $"Failed to load chevron sprite: {ex.Message}");
            }
            return _chevronSprite;
        }
    }
}
