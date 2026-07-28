using HarmonyLib;
using OverTheCounter.Utilities;
using System;
using System.Linq;
using System.Reflection;

#if IL2CPP
using Il2CppScheduleOne.NPCs.Relation;
#else
using ScheduleOne.NPCs.Relation;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// S1API (ifBars-S1API_Forked) also installs a Harmony prefix on
    /// NPCLoader.Load(DynamicSaveData) -- Il2CppScheduleOne.Persistence.Loaders.NPCLoader,
    /// NOT the similarly-named NPCsLoader -- called NPCLoader_Load_Prefix, that reads
    /// NPCRelationData.DEFAULT_RELATION_DELTA, another property that no longer exists on
    /// the current game version. Unlike the TestItems bug (S1APINPCInventoryFixPatch),
    /// S1API catches this one internally and just logs a warning per NPC ("Failed to load
    /// NPC 'x': Method not found: ...") instead of throwing all the way out -- but it
    /// still fires for essentially every named NPC in the save (dealers, customers, etc.)
    /// on every load, and the practical symptom is those NPCs' relation data (and
    /// possibly their in-world instantiation) never completing -- showing up in-game as
    /// dealers/customers reverting to "locked"/undiscovered on the OTC customer map.
    ///
    /// IMPORTANT: the FIRST version of this fix used the name "NPCsLoader_Load_Prefix"
    /// (with an s), copied from S1API's own log message text -- but that string turned
    /// out to be a mismatch/typo in S1API's own logging, not the real method name. The
    /// real name, confirmed directly from an uncaught trampoline stack trace (which is
    /// authoritative, unlike a hand-written log string), is "NPCLoader_Load_Prefix" (no
    /// s), patching Il2CppScheduleOne.Persistence.Loaders.NPCLoader.Load. That mismatch
    /// meant AccessTools.Method returned null and this whole patch silently no-opped --
    /// nothing was ever actually removed despite the fix appearing to apply cleanly.
    ///
    /// Same fix pattern as S1APINPCInventoryFixPatch: surgically Harmony.Unpatch this one
    /// broken prefix off NPCLoader.Load(), restoring vanilla NPC-relation loading for
    /// everyone. We still don't hardcode the target method itself here -- instead we
    /// search Harmony's own patch registry (GetAllPatchedMethods + GetPatchInfo) for
    /// whichever original method currently has NPCLoader_Load_Prefix attached, and
    /// unpatch that. More robust against getting the target method's exact signature
    /// wrong (only the patch METHOD name needs to be right, which we now have confirmed
    /// from a real stack trace rather than a guess).
    ///
    /// Only applied if BOTH S1API's broken method is present AND
    /// NPCRelationData.DEFAULT_RELATION_DELTA is confirmed missing -- if S1API ships a
    /// real fix or the game restores the member, this condition stops matching and Apply()
    /// becomes a harmless no-op.
    /// </summary>
    public static class S1APINPCRelationFixPatch
    {
        private static bool _applied;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            if (_applied) return;
            _applied = true;

            try
            {
                var patchType = AccessTools.TypeByName("S1API.Internal.Patches.NPCPatches");
                var patchMethod = patchType != null ? AccessTools.Method(patchType, "NPCLoader_Load_Prefix") : null;
                if (patchMethod == null)
                {
                    OTCLog.Msg(OTCLog.Systems.Patch,
                        "S1APINPCRelationFixPatch: S1API.Internal.Patches.NPCPatches.NPCLoader_Load_Prefix not found -- nothing to neutralize.");
                    return;
                }

                var relationDeltaProp = AccessTools.Property(typeof(NPCRelationData), "DEFAULT_RELATION_DELTA");
                if (relationDeltaProp != null)
                {
                    OTCLog.Msg(OTCLog.Systems.Patch,
                        "S1APINPCRelationFixPatch: NPCRelationData.DEFAULT_RELATION_DELTA exists on this game version -- S1API's patch looks compatible, not intervening.");
                    return;
                }

                MethodBase original = null;
                foreach (var candidate in HarmonyLib.Harmony.GetAllPatchedMethods())
                {
                    var info = HarmonyLib.Harmony.GetPatchInfo(candidate);
                    if (info == null) continue;

                    bool matches = info.Prefixes.Any(p => p.PatchMethod == patchMethod)
                        || info.Postfixes.Any(p => p.PatchMethod == patchMethod)
                        || info.Transpilers.Any(p => p.PatchMethod == patchMethod)
                        || info.Finalizers.Any(p => p.PatchMethod == patchMethod);

                    if (matches)
                    {
                        original = candidate;
                        break;
                    }
                }

                if (original == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch,
                        "S1APINPCRelationFixPatch: could not find which method NPCLoader_Load_Prefix is attached to -- cannot neutralize S1API's broken patch.");
                    return;
                }

                harmony.Unpatch(original, patchMethod);
                OTCLog.Msg(OTCLog.Systems.Patch,
                    $"S1APINPCRelationFixPatch: removed S1API's broken NPCLoader_Load_Prefix from {original.DeclaringType?.FullName}.{original.Name}() via Harmony.Unpatch -- vanilla NPC relation loading restored.");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"S1APINPCRelationFixPatch.Apply failed: {ex.Message}");
            }
        }
    }
}
