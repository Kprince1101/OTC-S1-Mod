using HarmonyLib;
using OverTheCounter.Logic;
using OverTheCounter.Utilities;
using System;
using System.Reflection;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.NPCs;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Harmony postfix on S1MAPI's <c>InteriorNavigatorCore.ReleaseNPC</c>.
    /// <para>
    /// When S1MAPI finishes walking an OTC customer through the doorway and releases
    /// them back to exterior NavMesh control, it restores
    /// <c>NPCMovement.HasDestination = true</c> via reflection but only calls
    /// <c>SetDestination</c> if <c>PendingExteriorDestination</c> or
    /// <c>SavedChasePosition</c> is set. OTC's recall flow doesn't populate either,
    /// so <c>CurrentDestination</c> is left at whatever residue the previous
    /// <c>EndSetDestination</c> call wrote — typically <see cref="Vector3.zero"/>.
    /// </para>
    /// <para>
    /// From the next <c>FixedUpdate</c> onward, <c>NPCMovement.UpdateDestination</c>
    /// re-fires <c>SetDestination(currentDestination)</c> every physics tick. The
    /// agent paths toward world origin, and because the doorway NavMeshLink sits
    /// right next to the released position, the agent almost always drops back
    /// through the link and re-enters the building — where S1MAPI then re-captures
    /// it as a new interior target and the cycle repeats.
    /// </para>
    /// <para>
    /// This postfix closes the window: the instant S1MAPI releases the NPC, we look
    /// up the matching OTC customer and call <see cref="CustomerInstance.WalkTo"/>
    /// with <see cref="CustomerInstance.GetSafeExitTarget"/> (a destination
    /// guaranteed to be outside the interior AABB, so S1MAPI's own SetDestination
    /// prefix lets the call through). This runs in the same call, before the next
    /// physics tick, so the bad zero-destination loop never gets a chance to fire.
    /// </para>
    /// <para>
    /// Remove this patch once S1MAPI's upstream ReleaseNPC sets a real destination
    /// for consumer-driven recalls that don't provide <c>PendingExteriorDestination</c>.
    /// See Jira OM-17 for the underlying bug report.
    /// </para>
    /// </summary>
    [HarmonyPatch]
    public static class CustomerInteriorReleasePatch
    {
        // ReSharper disable once UnusedMember.Local
        private static MethodBase TargetMethod()
        {
            try
            {
                // InteriorNavigatorCore is `internal sealed` — reach it via its
                // assembly rather than a compile-time reference.
                var asm = typeof(S1MAPI.Building.NavigationBuilder).Assembly;
                var coreType = asm.GetType("S1MAPI.Building.InteriorNavigatorCore");
                if (coreType == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,
                        "[CustomerInteriorRelease] InteriorNavigatorCore type not found");
                    return null;
                }

                foreach (var m in coreType.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != "ReleaseNPC") continue;
                    var p = m.GetParameters();
                    if (p.Length != 3) continue;
                    if (p[0].ParameterType != typeof(Component)) continue;
                    if (p[2].ParameterType != typeof(bool)) continue;
                    return m;
                }

                OTCLog.Warning(OTCLog.Systems.Patch,
                    "[CustomerInteriorRelease] ReleaseNPC method not found on InteriorNavigatorCore");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,
                    $"[CustomerInteriorRelease] TargetMethod failed: {ex.Message}");
            }
            return null;
        }

        // ReSharper disable once UnusedMember.Local
        // ReSharper disable once InconsistentNaming
        private static void Postfix(Component npc)
        {
            if (npc == null) return;

            try
            {
                int id = npc.GetInstanceID();

                CustomerInstance match = null;
                foreach (var c in CustomerInstance.Active.Values)
                {
                    if (c?.GameNpc?.Movement == null) continue;
                    if (c.GameNpc.Movement.GetInstanceID() == id)
                    {
                        match = c;
                        break;
                    }
                }

                if (match == null) return;

                var exitTarget = match.GetSafeExitTarget();
                if (exitTarget == Vector3.zero)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"[CustomerInteriorRelease] {match.Id}: no safe exit target available, skipping");
                    return;
                }

                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"[CustomerInteriorRelease] {match.Id}: S1MAPI released NPC, setting exit target {exitTarget}");

                match.WalkTo(exitTarget);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Customer,
                    $"[CustomerInteriorRelease] Postfix failed: {ex.Message}");
            }
        }
    }
}
