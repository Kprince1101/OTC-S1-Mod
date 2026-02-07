using HarmonyLib;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppSystem.Collections.Generic;

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Defensive patch for S1API bug: ScriptableObject.CreateInstance of AvatarSettings
    /// does not run C# field initializers, leaving FaceLayerSettings/BodyLayerSettings/AccessorySettings
    /// as null IL2CPP references. Avatar.LoadAvatarSettings then crashes with AccessViolationException
    /// when iterating these null lists.
    /// </summary>
    [HarmonyPatch(typeof(Avatar), nameof(Avatar.LoadAvatarSettings))]
    public static class AvatarSettingsSafetyPatch
    {
        public static void Prefix(AvatarSettings settings)
        {
            if (settings == null) return;

            if (settings.FaceLayerSettings == null)
                settings.FaceLayerSettings = new List<AvatarSettings.LayerSetting>();

            if (settings.BodyLayerSettings == null)
                settings.BodyLayerSettings = new List<AvatarSettings.LayerSetting>();

            if (settings.AccessorySettings == null)
                settings.AccessorySettings = new List<AvatarSettings.AccessorySetting>();
        }
    }
}
