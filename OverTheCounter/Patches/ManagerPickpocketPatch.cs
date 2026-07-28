using HarmonyLib;
using OverTheCounter.Logic;

#if IL2CPP
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.NPCs;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Makes managers impossible to pickpocket. NPCInventory no longer exposes a settable
    /// per-instance CanBePickpocketed flag (see ManagerSpawner.SetupInventory) -- the closest
    /// thing is Framework.Inventory.CanBePickpocketed on the NPC's runtime config object,
    /// which ManagerSpawner also sets to false as a best-effort measure. This patch is the
    /// authoritative guarantee: it postfixes the actual gate method the game calls before a
    /// pickpocket attempt, so managers are protected even if the config flag above turns out
    /// not to be what CanPickpocket() actually reads internally.
    /// </summary>
    [HarmonyPatch(typeof(NPCInventory), nameof(NPCInventory.CanPickpocket))]
    public static class ManagerPickpocketPatch
    {
        [HarmonyPostfix]
        public static void Postfix(NPCInventory __instance, ref bool __result)
        {
            if (!__result) return;
            try
            {
                if (__instance == null) return;
                NPC npc;
                try { npc = __instance._npc; } catch { return; }
                if (npc == null) return;

                if (ManagerInstance.GetByNpc(npc) != null)
                    __result = false;
            }
            catch { }
        }
    }
}
