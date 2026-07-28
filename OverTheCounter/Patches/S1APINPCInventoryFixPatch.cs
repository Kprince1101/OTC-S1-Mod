using HarmonyLib;
using OverTheCounter.Utilities;
using System;

#if IL2CPP
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.NPCs;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// S1API (ifBars-S1API_Forked) installs a Harmony patch on NPCInventory.Awake()
    /// (S1API.Internal.Patches.NPCPatches.EnsureNPCInventorySafeInit) that reads
    /// NPCInventory.TestItems -- a property that no longer exists on the current game
    /// version. That throws a MissingMethodException every single time ANY NPCInventory
    /// awakens, which happens for every NPC in the world during save load and appears to
    /// jam up the loading-screen transition (stuck loading screen, not a hard hang).
    ///
    /// First attempt: Harmony-patch EnsureNPCInventorySafeInit itself with a prefix that
    /// returns false. That failed to even install ("IL Compile Error") -- Harmony has to
    /// JIT-prepare whatever method it's patching in order to build the detour, and
    /// EnsureNPCInventorySafeInit's own IL contains a call to the now-nonexistent
    /// TestItems getter, so the JIT can't compile it at all, regardless of what patch
    /// we're trying to attach to it. Any Harmony operation that targets that specific
    /// method (patch OR unpatch) hits the same wall.
    ///
    /// Second attempt: patch NPCInventory.Awake() with a HarmonyPriority.First prefix
    /// that returns false, assuming that would skip lower-priority prefixes (S1API's).
    /// That installed cleanly but did NOT stop the crash -- Harmony always runs every
    /// prefix on a patched method regardless of what an earlier one returns; the bool
    /// return only controls whether the ORIGINAL method body runs, not other prefixes.
    /// So S1API's broken prefix kept firing right alongside ours.
    ///
    /// Actual fix: surgically remove S1API's specific prefix from NPCInventory.Awake()'s
    /// patch chain via Harmony.Unpatch(original, patchMethodInfo). This only needs to
    /// JIT-prepare Awake() itself (valid, no bad references) to rebuild the chain minus
    /// that one patch -- it never touches EnsureNPCInventorySafeInit's own broken IL.
    /// Confirmed via log timing that S1API's PatchAll() runs during its own
    /// OnInitializeMelon, well before OTC's Core.cs calls this Apply(), so the patch is
    /// already installed by the time we try to remove it.
    ///
    /// Only applied if BOTH S1API's broken method is present AND NPCInventory.TestItems
    /// is confirmed missing -- if S1API ships a real fix (removing the TestItems
    /// reference) or the game restores the property, this condition stops matching and
    /// Apply() becomes a harmless no-op, leaving S1API's patch alone.
    /// </summary>
    public static class S1APINPCInventoryFixPatch
    {
        private static bool _applied;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            if (_applied) return;
            _applied = true;

            try
            {
                var patchType = AccessTools.TypeByName("S1API.Internal.Patches.NPCPatches");
                var patchMethod = patchType != null ? AccessTools.Method(patchType, "EnsureNPCInventorySafeInit") : null;
                if (patchMethod == null)
                {
                    OTCLog.Msg(OTCLog.Systems.Patch,
                        "S1APINPCInventoryFixPatch: S1API.Internal.Patches.NPCPatches.EnsureNPCInventorySafeInit not found -- nothing to neutralize.");
                    return;
                }

                var testItemsProp = AccessTools.Property(typeof(NPCInventory), "TestItems");
                if (testItemsProp != null)
                {
                    OTCLog.Msg(OTCLog.Systems.Patch,
                        "S1APINPCInventoryFixPatch: NPCInventory.TestItems exists on this game version -- S1API's patch looks compatible, not intervening.");
                    return;
                }

                var awake = AccessTools.Method(typeof(NPCInventory), "Awake");
                if (awake == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,
                        "S1APINPCInventoryFixPatch: NPCInventory.Awake() not found -- cannot neutralize S1API's broken patch.");
                    return;
                }

                harmony.Unpatch(awake, patchMethod);
                OTCLog.Msg(OTCLog.Systems.Patch,
                    "S1APINPCInventoryFixPatch: removed S1API's broken TestItems prefix from NPCInventory.Awake() via Harmony.Unpatch -- vanilla Awake() logic restored.");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"S1APINPCInventoryFixPatch.Apply failed: {ex.Message}");
            }
        }
    }
}
