using HarmonyLib;
using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using System;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.DevUtilities;
#else
using ScheduleOne.Economy;
using ScheduleOne.PlayerScripts;
using ScheduleOne.DevUtilities;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Patches the supplier meeting system so:
    /// 1. After a meeting ends, suppliers warp back to the OTC Warehouse.
    /// 2. When a meetup is requested, warehouse locations are excluded so
    ///    the supplier leaves the warehouse and goes to a normal game location.
    /// 3. When MeetAtLocation is called, any existing warehouse state is cleaned up.
    /// </summary>
    public static class SupplierWarehousePatch
    {
        /// <summary>Patches Supplier meeting methods to integrate warehouse stands.</summary>
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var endMeeting = AccessTools.Method(typeof(Supplier), "EndMeeting");
                if (endMeeting != null)
                    harmony.Patch(endMeeting,
                        postfix: new HarmonyMethod(typeof(SupplierWarehousePatch), nameof(EndMeeting_Postfix)));

                var getLocation = AccessTools.Method(typeof(Supplier), "GetAppropriateLocation");
                if (getLocation != null)
                    harmony.Patch(getLocation,
                        prefix: new HarmonyMethod(typeof(SupplierWarehousePatch), nameof(GetAppropriateLocation_Prefix)));

                var meetAtLocation = AccessTools.Method(typeof(Supplier), "MeetAtLocation");
                if (meetAtLocation != null)
                    harmony.Patch(meetAtLocation,
                        prefix: new HarmonyMethod(typeof(SupplierWarehousePatch), nameof(MeetAtLocation_Prefix)));

                OTCLog.Msg(OTCLog.Systems.Patch, "SupplierWarehousePatch applied");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"SupplierWarehousePatch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// After a meeting ends, warp the supplier back to the warehouse
        /// so they remain visually present with full shop interaction.
        /// </summary>
        private static void EndMeeting_Postfix(Supplier __instance)
        {
            try
            {
                OTCSupplierArea.WarpSupplierToWarehouse(__instance);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"EndMeeting postfix: {ex.Message}");
            }
        }

        /// <summary>
        /// When a real meetup starts (MeetAtLocation), clean up the warehouse state
        /// so the GenericContainer is hidden and dialogue flags are reset.
        /// </summary>
        private static void MeetAtLocation_Prefix(Supplier __instance)
        {
            try
            {
                OTCSupplierArea.CleanupWarehouseSupplier(__instance);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"MeetAtLocation prefix: {ex.Message}");
            }
        }

        /// <summary>
        /// Replaces GetAppropriateLocation to exclude warehouse locations,
        /// forcing meetups to happen at normal game locations.
        /// Same logic as vanilla but filters out OTC warehouse stands.
        /// </summary>
        private static bool GetAppropriateLocation_Prefix(
            Supplier __instance,
            ref int locationIndex,
            ref SupplierLocation __result)
        {
            try
            {
                var warehouseLocs = OTCSupplierArea.WarehouseLocations;
                if (warehouseLocs == null || warehouseLocs.Count == 0)
                    return true; // no warehouse locations registered, run vanilla

                // Rebuild the vanilla logic but skip warehouse locations
                var candidates = new List<SupplierLocation>();
                var allLocations = SupplierLocation.AllLocations;

                for (int i = 0; i < allLocations.Count; i++)
                {
                    var loc = allLocations[i];
                    if (loc == null) continue;
                    if (warehouseLocs.Contains(loc)) continue; // skip warehouse
                    if (loc.IsOccupied) continue;
                    candidates.Add(loc);
                }

                // Remove locations within 30m of any player (same as vanilla)
                for (int i = candidates.Count - 1; i >= 0; i--)
                {
                    foreach (var player in Player.PlayerList)
                    {
                        if (player != null &&
                            Vector3.Distance(candidates[i].transform.position, player.Avatar.CenterPoint) < 30f)
                        {
                            candidates.RemoveAt(i);
                            break;
                        }
                    }
                }

                if (candidates.Count == 0)
                    return true; // no valid non-warehouse locations, fall through to vanilla

                var chosen = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                locationIndex = allLocations.IndexOf(chosen);
                __result = chosen;
                return false;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"GetAppropriateLocation prefix: {ex.Message}");
            }

            return true;
        }
    }
}
