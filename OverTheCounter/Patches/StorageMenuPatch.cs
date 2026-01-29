using HarmonyLib;
using MelonLoader;
using OverTheCounter.UI;
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
            var type = AccessTools.TypeByName("Il2CppScheduleOne.UI.StorageMenu");
            if (type == null)
            {
                Melon<Core>.Logger.Warning("[StorageMenuOpenPatch] Could not find StorageMenu type.");
                return null;
            }

            var storageEntityType = AccessTools.TypeByName("Il2CppScheduleOne.Storage.StorageEntity");
            if (storageEntityType == null)
            {
                Melon<Core>.Logger.Warning("[StorageMenuOpenPatch] Could not find StorageEntity type.");
                return null;
            }

            var method = AccessTools.Method(type, "Open", new Type[] { storageEntityType });
            if (method == null)
            {
                Melon<Core>.Logger.Warning("[StorageMenuOpenPatch] Could not find Open(StorageEntity) method.");
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
                Melon<Core>.Logger.Error($"[StorageMenuOpenPatch] Error showing overlay: {ex.Message}");
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
            var type = AccessTools.TypeByName("Il2CppScheduleOne.UI.StorageMenu");
            if (type == null)
            {
                Melon<Core>.Logger.Warning("[StorageMenuClosePatch] Could not find StorageMenu type.");
                return null;
            }

            var method = AccessTools.Method(type, "CloseMenu");
            if (method == null)
            {
                Melon<Core>.Logger.Warning("[StorageMenuClosePatch] Could not find CloseMenu method.");
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
                Melon<Core>.Logger.Error($"[StorageMenuClosePatch] Error hiding overlay: {ex.Message}");
            }
        }
    }
}
