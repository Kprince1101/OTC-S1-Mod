using HarmonyLib;
using Il2CppFishNet;
using MelonLoader;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using System;
using System.Reflection;

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Patches ModsApp's ApplyPreferenceChanges so the host pushes updated
    /// config to clients via SyncVar after in-game settings changes.
    /// ModsApp is optional — patches are skipped if the type is not found.
    /// </summary>
    public static class ConfigSyncPatch
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:ConfigSyncPatch");

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
                    Logger.Msg("ModsApp not found — ConfigSync patches skipped.");
                    return;
                }

                var targetMethod = AccessTools.Method(targetType, "ApplyPreferenceChanges", new[] { typeof(string) });
                if (targetMethod == null)
                {
                    Logger.Warning("ModDetailsPanel.ApplyPreferenceChanges not found — ConfigSync patches skipped.");
                    return;
                }

                var postfix = new HarmonyMethod(typeof(ConfigSyncPatch), nameof(Postfix));
                harmony.Patch(targetMethod, postfix: postfix);

                Logger.Msg("ModsApp ConfigSync patches applied.");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to apply ConfigSync patches: {ex.Message}");
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
                Logger.Msg("[ConfigSync] Host config updated and published via SyncVar.");
        }
    }
}
