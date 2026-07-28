using HarmonyLib;
using OverTheCounter.Utilities;
using System;

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
    /// S1API hasn't been updated for this game version yet, and we can't fix its compiled
    /// DLL directly, so this neutralizes S1API's broken patch method itself: we prefix
    /// EnsureNPCInventorySafeInit and skip its body entirely (return false), so the
    /// TestItems access never happens. Found via AccessTools reflection rather than a
    /// compile-time reference since S1API.Internal.Patches may not be a public namespace.
    /// If S1API ships a real fix later, this becomes a harmless no-op (the type/method
    /// lookup just needs to keep succeeding for the patch to apply; if S1API renames or
    /// removes the method in an update, this logs a warning and does nothing rather than
    /// erroring).
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
                var type = AccessTools.TypeByName("S1API.Internal.Patches.NPCPatches");
                if (type == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,
                        "S1APINPCInventoryFixPatch: S1API.Internal.Patches.NPCPatches not found (S1API version may have changed) -- skipping.");
                    return;
                }

                var method = AccessTools.Method(type, "EnsureNPCInventorySafeInit");
                if (method == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,
                        "S1APINPCInventoryFixPatch: EnsureNPCInventorySafeInit method not found -- skipping.");
                    return;
                }

                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(S1APINPCInventoryFixPatch), nameof(Prefix)));
                OTCLog.Msg(OTCLog.Systems.Patch,
                    "S1APINPCInventoryFixPatch: neutralized S1API's broken NPCInventory.TestItems patch.");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"S1APINPCInventoryFixPatch.Apply failed: {ex.Message}");
            }
        }

        /// <summary>Skip S1API's patch body entirely -- it only ever throws now.</summary>
        public static bool Prefix()
        {
            return false;
        }
    }
}
