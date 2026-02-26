using HarmonyLib;
using MelonLoader;
using OverTheCounter.Logic;
using System;
using UnityEngine;
using UnityEngine.AI;

#if IL2CPP
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.NPCs;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Enables autoTraverseOffMeshLink on any NPC whose destination is near an OTC building.
    /// Without this, vanilla NPCs (e.g. RequestProductBehaviour follow) get stuck at the
    /// NavMeshLink on our building's stairs because the game prefab ships with
    /// autoTraverseOffMeshLink = false.
    ///
    /// The flag is only set when needed (destination near building) and doesn't permanently
    /// change vanilla pathfinding — it only affects NavMeshLink traversal which only exists
    /// on our custom buildings.
    /// </summary>
    [HarmonyPatch(typeof(NPCMovement))]
    public static class NpcNavMeshLinkPatch
    {
        private static readonly MelonLogger.Instance Logger = new("OTC:NavLinkPatch");

        // Radius around each OTC building entrance where we enable link traversal
        private const float BuildingProximity = 20f;
        private const float BuildingProximitySqr = BuildingProximity * BuildingProximity;

        /// <summary>
        /// Prefix on SetDestination(Vector3, Action, float, float) — the public overload
        /// that all movement eventually flows through.
        /// If the destination is near an OTC building entrance, enable off-mesh link traversal.
        /// </summary>
        [HarmonyPatch(nameof(NPCMovement.SetDestination),
            new[] { typeof(Vector3), typeof(Action<NPCMovement.WalkResult>), typeof(float), typeof(float) })]
        [HarmonyPrefix]
        public static void SetDestinationPrefix(NPCMovement __instance, Vector3 pos)
        {
            try
            {
                if (IsNearOtcBuilding(pos) || IsNearOtcBuilding(__instance.transform.position))
                {
                    var agent = __instance.GetComponent<NavMeshAgent>();
                    if (agent != null && !agent.autoTraverseOffMeshLink)
                    {
                        agent.autoTraverseOffMeshLink = true;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Checks if a position is near any OTC building entrance.
        /// Uses 2D distance (XZ) to avoid height differences from foundation.
        /// </summary>
        private static bool IsNearOtcBuilding(Vector3 pos)
        {
            // Check against all known OTC building entrances
            var entrance = CustomerSpawnPoints.EntrancePosition;
            float dx = pos.x - entrance.x;
            float dz = pos.z - entrance.z;
            return (dx * dx + dz * dz) <= BuildingProximitySqr;
        }
    }
}
