using HarmonyLib;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using System;

#if IL2CPP
using Il2CppScheduleOne.Persistence;
#else
using ScheduleOne.Persistence;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Prefix patch on SaveManager.Save — refreshes all volatile save data fields
    /// right before S1API reads them via reflection.
    /// </summary>
    public static class SaveManagerPatch
    {

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var saveMethod = AccessTools.Method(typeof(SaveManager), "Save", new[] { typeof(string) });
                if (saveMethod != null)
                {
                    harmony.Patch(saveMethod,
                        prefix: new HarmonyMethod(typeof(SaveManagerPatch), nameof(Prefix)));
                }
                else
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,"SaveManager.Save(string) not found — save capture disabled");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch,$"Failed to apply SaveManager patch: {ex.Message}");
            }
        }

        public static void Prefix()
        {
            ManagerSaveData.Instance?.CaptureState();
            PropertySaveData.Instance?.CaptureShackState();
            PropertySaveData.Instance?.SnapshotStorageContents();
        }
    }
}
