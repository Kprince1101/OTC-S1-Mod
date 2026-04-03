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

                var endRpcLogic = ResolveEndMeetingRpcLogicMethod();
                if (endRpcLogic != null)
                    harmony.Patch(endRpcLogic,
                        postfix: new HarmonyMethod(typeof(SupplierWarehousePatch), nameof(EndMeeting_Postfix)));
                else
                    OTCLog.Warning(OTCLog.Systems.Patch,
                        "SupplierWarehousePatch: RpcLogic EndMeeting not found; client warehouse warp after meeting may be incomplete");

                var getLocation = AccessTools.Method(typeof(Supplier), "GetAppropriateLocation");
                if (getLocation != null)
                    harmony.Patch(getLocation,
                        prefix: new HarmonyMethod(typeof(SupplierWarehousePatch), nameof(GetAppropriateLocation_Prefix)));

                // Must patch RpcLogic, not only public MeetAtLocation: on clients, FishNet invokes
                // RpcLogic___MeetAtLocation_* via RpcReader without calling the public wrapper, so warehouse
                // cleanup never ran and _assignedSuppliers stayed stale — idle routine kept re-warping to warehouse.
                var meetRpcLogic = ResolveMeetAtLocationRpcLogicMethod();
                if (meetRpcLogic != null)
                    harmony.Patch(meetRpcLogic,
                        prefix: new HarmonyMethod(typeof(SupplierWarehousePatch), nameof(MeetAtLocation_RpcLogicPrefix)));
                else
                    OTCLog.Warning(OTCLog.Systems.Patch,
                        "SupplierWarehousePatch: RpcLogic MeetAtLocation not found; client meetup cleanup may be incomplete");

                // Public method has the stable locationIndex argument we can trust for meetup warp target.
                var publicMeetAtLocation = AccessTools.Method(typeof(Supplier), "MeetAtLocation");
                if (publicMeetAtLocation != null)
                    harmony.Patch(publicMeetAtLocation,
                        prefix: new HarmonyMethod(typeof(SupplierWarehousePatch), nameof(MeetAtLocation_PublicPrefix)));

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

        /// <summary>Resolves FishNet-generated RpcLogic for MeetAtLocation.</summary>
        /// <remarks>Hardcoded IL2CPP RpcLogic names drift; scanning avoids failed AccessTools lookups that spam the log.</remarks>
        private static System.Reflection.MethodBase ResolveMeetAtLocationRpcLogicMethod()
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            foreach (var m in typeof(Supplier).GetMethods(flags))
            {
                if (m.Name.IndexOf("MeetAtLocation", StringComparison.Ordinal) < 0)
                    continue;
                if (m.Name.IndexOf("RpcLogic", StringComparison.Ordinal) < 0)
                    continue;
                var ps = m.GetParameters();
                if (ps.Length == 3)
                    return m;
            }
            return null;
        }

        /// <summary>Resolves FishNet-generated RpcLogic for EndMeeting when present.</summary>
        /// <remarks>
        /// Scans <see cref="Supplier"/> methods on IL2CPP and Mono so FishNet name mangling changes
        /// do not require hardcoded RpcLogic strings (failed AccessTools lookups spam the log).
        /// </remarks>
        private static System.Reflection.MethodBase ResolveEndMeetingRpcLogicMethod()
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            foreach (var m in typeof(Supplier).GetMethods(flags))
            {
                if (m.Name.IndexOf("EndMeeting", StringComparison.Ordinal) < 0)
                    continue;
                if (m.Name.IndexOf("RpcLogic", StringComparison.Ordinal) < 0)
                    continue;
                return m;
            }
            return null;
        }

        /// <summary>
        /// Before RpcLogic runs, always clear warehouse stand state on host + clients.
        /// </summary>
        private static void MeetAtLocation_RpcLogicPrefix(Supplier __instance)
        {
            try
            {
                OTCSupplierArea.CleanupWarehouseSupplier(__instance);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"MeetAtLocation RpcLogic prefix: {ex.Message}");
            }
        }

        /// <summary>
        /// Public MeetAtLocation wrapper gives a stable location index.
        /// Use this to do the release->outside->warp handoff deterministically.
        /// </summary>
        private static void MeetAtLocation_PublicPrefix(Supplier __instance, int locationIndex, int expireIn)
        {
            try
            {
                OTCSupplierArea.CleanupWarehouseSupplier(__instance);
                if (locationIndex < 0 || locationIndex >= SupplierLocation.AllLocations.Count)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,
                        $"MeetAtLocation public prefix: invalid locationIndex={locationIndex} for {__instance?.fullName}");
                    return;
                }

                var loc = SupplierLocation.AllLocations[locationIndex];
                if (loc == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,
                        $"MeetAtLocation public prefix: null location at index={locationIndex} for {__instance?.fullName}");
                    return;
                }

                var meetupPos = loc.SupplierStandPoint != null
                    ? loc.SupplierStandPoint.position
                    : loc.transform.position;
                var meetupForward = loc.SupplierStandPoint != null
                    ? loc.SupplierStandPoint.forward
                    : loc.transform.forward;

                OTCLog.Msg(OTCLog.Systems.Patch,
                    $"MeetAtLocation public prefix: resolved meetup target for {__instance?.fullName} -> {meetupPos} (index {locationIndex})");
                OTCSupplierArea.MarkMeetupStart(__instance, expireIn);
                OTCSupplierArea.ReleaseAndWarpSupplierToMeetup(__instance, meetupPos, meetupForward);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"MeetAtLocation public prefix: {ex.Message}");
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
