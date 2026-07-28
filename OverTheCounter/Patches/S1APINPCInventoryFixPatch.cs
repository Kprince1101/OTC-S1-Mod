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
    /// Fix: patch NPCInventory.Awake() itself instead (a normal, valid IL2CPP interop
    /// stub with no bad references -- this JIT-prepares fine). Our prefix runs at
    /// HarmonyPriority.First and unconditionally returns false, which skips every
    /// lower-priority prefix on Awake() -- including S1API's broken one -- and also
    /// skips Awake()'s own original body. That sounds worse than it is: in the current
    /// broken state, S1API's prefix throws BEFORE the original Awake() body ever runs
    /// anyway, so vanilla Awake() logic is already not executing for any NPC. This patch
    /// just makes that a clean, silent no-op instead of a thrown-and-logged exception on
    /// every single NPC, rather than changing what actually happens functionally.
    ///
    /// Only applied if BOTH S1API's broken method is present AND NPCInventory.TestItems
    /// is confirmed missing -- if S1API ships a real fix (removing the TestItems
    /// reference) or the game restores the property, this condition stops matching and
    /// Apply() becomes a harmless no-op, leaving Awake() alone.
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

                harmony.Patch(awake,
                    prefix: new HarmonyMethod(typeof(S1APINPCInventoryFixPatch), nameof(Prefix)) { priority = Priority.First });
                OTCLog.Msg(OTCLog.Systems.Patch,
                    "S1APINPCInventoryFixPatch: NPCInventory.Awake() short-circuited (highest priority) to prevent S1API's broken TestItems patch from throwing on every NPC.");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"S1APINPCInventoryFixPatch.Apply failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs before every other patch on NPCInventory.Awake() (including S1API's
        /// broken one) and skips the rest of the chain + the original body entirely.
        /// Matches current (broken) behavior functionally -- Awake()'s vanilla logic
        /// already isn't running, since S1API's prefix throws before reaching it.
        /// </summary>
        public static bool Prefix()
        {
            return false;
        }
    }
}
