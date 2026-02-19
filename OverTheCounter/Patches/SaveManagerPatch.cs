using HarmonyLib;
using OverTheCounter.SaveData;

#if IL2CPP
using Il2CppScheduleOne.Persistence;
#else
using ScheduleOne.Persistence;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Prefix patch on SaveManager.Save — refreshes all volatile ManagerSaveData fields
    /// right before S1API reads them via reflection.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save), typeof(string))]
    public static class SaveManagerPatch
    {
        public static void Prefix()
        {
            ManagerSaveData.Instance?.CaptureState();
        }
    }
}
