using HarmonyLib;
using Il2CppScheduleOne.Persistence;
using OverTheCounter.SaveData;

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
