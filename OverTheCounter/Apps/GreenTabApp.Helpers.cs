using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using S1MAPI.S1;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Money;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Money;
#endif

namespace OverTheCounter.Apps
{
    public partial class GreenTabApp
    {
        /// <summary>Returns all building IDs that currently have at least one counter.</summary>
        private static List<string> GetBuildingsWithCounters()
        {
            var seen = new HashSet<string>();
            var result = new List<string>();
            foreach (var counter in CheckoutCounter.AllCounters)
            {
                var bid = counter.BuildingId;
                if (bid != null && seen.Add(bid))
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
            // Fallback: any counter
            return CheckoutCounter.AllCounters.Count > 0 ? CheckoutCounter.AllCounters[0] : null;
        }

        private static string GetBuildingDisplayName(string buildingId) =>
            BuildingDisplayNames.TryGetValue(buildingId, out var name) ? name : buildingId ?? "Unknown";

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
    }
}
