using HarmonyLib;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using System;
using System.Reflection;

#if IL2CPP
using Il2CppFishNet;
#else
using FishNet;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Patches ModsApp's ApplyPreferenceChanges so the host pushes updated
    /// config to clients via SyncVar after in-game settings changes.
    /// ModsApp is optional — patches are skipped if the type is not found.
    /// </summary>
    public static class ConfigSyncPatch
    {

        /// <summary>
        /// Call during mod initialization to apply patches if ModsApp is present.
        /// </summary>
        public static void TryApply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var targetType = AccessTools.TypeByName("ModsApp.UI.Panels.ModDetailsPanel");
                if (targetType == null)
                {
                    OTCLog.Msg(OTCLog.Systems.Network,"ModsApp not found — ConfigSync patches skipped.");
                    return;
                }

                var targetMethod = AccessTools.Method(targetType, "ApplyPreferenceChanges", new[] { typeof(string) });
                if (targetMethod == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Network,"ModDetailsPanel.ApplyPreferenceChanges not found — ConfigSync patches skipped.");
                    return;
                }

                var postfix = new HarmonyMethod(typeof(ConfigSyncPatch), nameof(Postfix));
                harmony.Patch(targetMethod, postfix: postfix);

                OTCLog.Msg(OTCLog.Systems.Network,"ModsApp ConfigSync patches applied.");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network,$"Failed to apply ConfigSync patches: {ex.Message}");
            }
        }

        /// <summary>
        /// After host applies config changes, refresh the sync payload for connected clients.
        /// </summary>
        private static void Postfix(string modName)
        {
            if (modName != "OverTheCounter") return;
            if (!NetworkHelper.IsHost) return;

            ConfigSyncData.Instance?.RefreshFromConfig();

            if (InstanceFinder.NetworkManager != null && InstanceFinder.IsServer && Config.VerboseLogging.Value)
                OTCLog.Msg(OTCLog.Systems.Network,"[ConfigSync] Host config updated and published via SyncVar.");
        }
    }
}
