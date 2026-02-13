using HarmonyLib;
using Il2CppScheduleOne.Quests;

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Prevents Quest.InitializeQuest from being called twice on the same quest.
    ///
    /// S1API's QuestStart Harmony hook calls CreateInternal → InitializeQuest on every
    /// quest when its game component's Start() fires. OTC quests also call InitializeQuest
    /// via TriggerInternalInit() during Initialize(). This double call creates duplicate
    /// journal entries with the journalEntry field pointing to the invisible second copy.
    /// </summary>
    [HarmonyPatch(typeof(Quest), nameof(Quest.InitializeQuest))]
    public static class QuestInitPatch
    {
        public static bool Prefix(Quest __instance)
        {
            return !Quest.Quests.Contains(__instance);
        }
    }
}
