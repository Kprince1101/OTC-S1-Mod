using HarmonyLib;
using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using OverTheCounter.SaveData;
using System;

#if IL2CPP
using Il2CppFishNet.Object;
using Il2CppScheduleOne.Doors;
#else
using FishNet.Object;
using ScheduleOne.Doors;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Patches DoorController.SetIsOpen(bool, EDoorSide) to sync the shack door state
    /// between host and client. This is the chokepoint that actually changes IsOpen and
    /// fires onDoorOpened/onDoorClosed, called via FishNet's RPC chain on both sides.
    ///
    /// ModularSwitch.onToggled is a plain C# delegate — subscribable with a lambda.
    /// DoorController.onDoorOpened is UnityEvent&lt;EDoorSide&gt; — UnityAction&lt;T&gt; is an
    /// IL2CPP class, not a delegate, so lambda subscription fails at compile time.
    /// A Harmony postfix on SetIsOpen(bool, EDoorSide) achieves the same result.
    /// </summary>
    public static class DoorSyncPatch
    {

        public static void TryApply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var method = AccessTools.Method(typeof(DoorController), "SetIsOpen",
                    new[] { typeof(bool), typeof(EDoorSide) });
                if (method == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Network,"DoorController.SetIsOpen(bool, EDoorSide) not found — door sync skipped.");
                    return;
                }
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(DoorSyncPatch), nameof(Postfix)));
                OTCLog.Msg(OTCLog.Systems.Network,"DoorSync patch applied.");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network,$"DoorSync patch failed: {ex.Message}");
            }
        }

        private static void Postfix(DoorController __instance, bool open, EDoorSide openSide)
        {
            if (__instance != WestvilleShack.Door) return;
            if (WestvilleShack.SuppressDoorSync) return;

            // Cache the state directly from the method parameters — do not read back
            // from Door.IsOpen which may not reflect the change by serialization time.
            WestvilleShack.CachedDoorIsOpen = open;
            WestvilleShack.CachedDoorSide = openSide;

            if (Config.VerboseLogging.Value)
                OTCLog.Msg(OTCLog.Systems.Network,$"[DoorSyncPatch] open={open} side={openSide} isHost={NetworkHelper.IsHost}");

            if (NetworkHelper.IsHost)
            {
                // Host publishes to Steam (backup sync for save/load/rejoin).
                ConfigSyncData.Instance?.PublishGameState();
            }
            else
            {
                // If our door is the FishNet-managed instance, SetIsOpen_Server already sent
                // to the host — no quest action needed. Fall back to quest action only for
                // the local clone (no ObjectId) while WaitForFishNetDoor is still running.
                var no = WestvilleShack.Door?.GetComponentInParent<NetworkObject>();
                if (no == null || no.ObjectId == 0)
                    ConfigSyncData.SendQuestAction($"SHACK_DOOR:{(open ? 1 : 0)}:{(int)openSide}");
            }
        }
    }
}
