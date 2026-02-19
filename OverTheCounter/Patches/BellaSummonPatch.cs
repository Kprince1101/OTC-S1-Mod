using HarmonyLib;
using MelonLoader;
using OverTheCounter.NPCs;
using System;
using System.Collections;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Doors;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.UI.Handover;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Doors;
using ScheduleOne.NPCs;
using ScheduleOne.UI.Handover;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Intercepts the door-knock summon for Bella and handles it manually.
    /// S1API-created NPCs don't have a SummonBehaviour component, so the
    /// vanilla Summon() RPC does nothing. Instead, we directly call
    /// ExitBuilding to make Bella appear, then re-inject her after she
    /// has been idle (not in dialogue) for IDLE_TIMEOUT seconds.
    /// </summary>
    [HarmonyPatch(typeof(StaticDoor))]
    public static class BellaSummonPatch
    {
        private static readonly MelonLogger.Instance Logger = new("BellaSummonPatch");

        private const float IDLE_TIMEOUT = 10f;
        private static bool _bellaSummoned;

        [HarmonyPrefix]
        [HarmonyPatch("NPCSelected")]
        public static bool NPCSelected_Prefix(StaticDoor __instance, NPC npc)
        {
            try
            {
                if (BellaNPC.Instance == null || npc == null) return true;

                // Match by instance ID against Bella's game NPC
                if (BellaNPC.Instance.GameNpc != null &&
                    npc.GetInstanceID() == BellaNPC.Instance.GameNpc.GetInstanceID())
                {
                    if (_bellaSummoned)
                    {
                        if (Config.VerboseLogging.Value)
                            Logger.Msg("Bella is already summoned, ignoring duplicate request");
                        return false;
                    }

                    var building = __instance.Building;
                    if (building == null)
                    {
                        Logger.Warning("Building is null on StaticDoor, falling back to vanilla");
                        return true;
                    }

                    // Use the game's ExitBuilding to make Bella appear at the door.
                    // This handles: warp to door, SetVisible(true), remove from occupants,
                    // face direction, re-enable awareness — the full vanilla flow.
                    string buildingGuid = building.GUID.ToString();
                    if (Config.VerboseLogging.Value)
                        Logger.Msg($"Summoning Bella via ExitBuilding (building={building.BuildingName}, GUID={buildingGuid})");

                    npc.ExitBuilding(buildingGuid);
                    _bellaSummoned = true;

                    // Start timer to re-inject Bella into the building after duration
                    MelonCoroutines.Start(SummonTimer(npc));

                    return false; // Skip vanilla (which calls Summon() — doesn't work for S1API NPCs)
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"NPCSelected_Prefix error: {ex.Message}");
            }

            return true;
        }

        /// <summary>
        /// Polls every second. While Bella is in dialogue, the idle timer resets.
        /// Once she has been idle (no dialogue) for IDLE_TIMEOUT seconds continuously,
        /// re-inject her into the building.
        /// </summary>
        private static IEnumerator SummonTimer(NPC npc)
        {
            float idleTime = 0f;

            while (true)
            {
                yield return new WaitForSeconds(1f);

                try
                {
                    // Bella destroyed or already back in a building — stop tracking
                    if (npc == null || BellaNPC.Instance == null)
                    {
                        if (Config.VerboseLogging.Value)
                            Logger.Msg("SummonTimer: Bella no longer valid, stopping timer");
                        _bellaSummoned = false;
                        yield break;
                    }

                    if (npc.CurrentBuilding != null)
                    {
                        if (Config.VerboseLogging.Value)
                            Logger.Msg("SummonTimer: Bella already back in building, stopping timer");
                        _bellaSummoned = false;
                        yield break;
                    }

                    // Check if dialogue or handover is active — reset idle timer if so
                    bool playerInteracting = false;
                    try
                    {
                        var handler = npc.DialogueHandler;
                        if (handler != null && handler.IsDialogueInProgress)
                            playerInteracting = true;
                    }
                    catch { /* IL2CPP access can fail if object destroyed mid-frame */ }

                    if (!playerInteracting)
                    {
                        try
                        {
                            var handover = Singleton<HandoverScreen>.Instance;
                            if (handover != null && handover.IsOpen)
                                playerInteracting = true;
                        }
                        catch { }
                    }

                    if (playerInteracting)
                    {
                        idleTime = 0f;
                        continue;
                    }

                    idleTime += 1f;

                    if (idleTime >= IDLE_TIMEOUT)
                    {
                        if (Config.VerboseLogging.Value)
                            Logger.Msg($"SummonTimer: Bella idle for {idleTime:F0}s, re-injecting into building");
                        _bellaSummoned = false;
                        BellaNPC.Instance.ReInjectIntoBuilding();
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"SummonTimer error: {ex.Message}");
                    _bellaSummoned = false;
                    yield break;
                }
            }
        }

        /// <summary>
        /// Resets state when the game scene unloads.
        /// </summary>
        internal static void Reset()
        {
            _bellaSummoned = false;
        }
    }
}
