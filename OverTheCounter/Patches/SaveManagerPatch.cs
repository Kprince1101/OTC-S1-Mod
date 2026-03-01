using HarmonyLib;
using MelonLoader;
using OverTheCounter.SaveData;
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
        private static readonly MelonLogger.Instance Logger = new("OTC:SavePatch");

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
                    Logger.Warning("SaveManager.Save(string) not found — save capture disabled");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to apply SaveManager patch: {ex.Message}");
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
