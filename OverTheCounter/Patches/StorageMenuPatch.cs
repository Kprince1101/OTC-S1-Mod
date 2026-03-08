using HarmonyLib;
using MelonLoader;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using System;
using System.Reflection;

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Shows the Smart Stash overlay when the storage menu opens.
    /// Targets the Open(StorageEntity) overload specifically to avoid AmbiguousMatchException.
    /// </summary>
    [HarmonyPatch]
    public static class StorageMenuOpenPatch
    {
        public static MethodBase TargetMethod()
        {
#if IL2CPP
            var type = AccessTools.TypeByName("Il2CppScheduleOne.UI.StorageMenu");
#else
            var type = AccessTools.TypeByName("ScheduleOne.UI.StorageMenu");
#endif
            if (type == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, "Could not find StorageMenu type.");
                return null;
            }

#if IL2CPP
            var storageEntityType = AccessTools.TypeByName("Il2CppScheduleOne.Storage.StorageEntity");
#else
            var storageEntityType = AccessTools.TypeByName("ScheduleOne.Storage.StorageEntity");
#endif
            if (storageEntityType == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, "Could not find StorageEntity type.");
                return null;
            }

            var method = AccessTools.Method(type, "Open", new Type[] { storageEntityType });
            if (method == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, "Could not find Open(StorageEntity) method.");
            }
            return method;
        }

        public static void Postfix()
        {
            try
            {
                SmartStashOverlayUI.Show();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Error showing overlay: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Hides the Smart Stash overlay when the storage menu closes.
    /// </summary>
    [HarmonyPatch]
    public static class StorageMenuClosePatch
    {
        public static MethodBase TargetMethod()
        {
#if IL2CPP
            var type = AccessTools.TypeByName("Il2CppScheduleOne.UI.StorageMenu");
#else
            var type = AccessTools.TypeByName("ScheduleOne.UI.StorageMenu");
#endif
            if (type == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, "Could not find StorageMenu type.");
                return null;
            }

            var method = AccessTools.Method(type, "CloseMenu");
            if (method == null)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, "Could not find CloseMenu method.");
            }
            return method;
        }

        public static void Postfix()
        {
            try
            {
                SmartStashOverlayUI.Hide();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Error hiding overlay: {ex.Message}");
            }
        }
    }
}
