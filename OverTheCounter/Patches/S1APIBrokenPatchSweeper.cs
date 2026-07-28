using HarmonyLib;
using OverTheCounter.Utilities;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace OverTheCounter.Patches
{
    /// <summary>
    /// S1API (ifBars-S1API_Forked) hasn't been updated for this game version, and keeps
    /// turning up individual Harmony patch methods (in S1API.Internal.Patches.NPCPatches
    /// and similar) whose IL references game members that no longer exist on this
    /// version -- confirmed so far: NPCInventory.TestItems (EnsureNPCInventorySafeInit,
    /// handled by S1APINPCInventoryFixPatch), NPCRelationData.DEFAULT_RELATION_DELTA
    /// (NPCLoader_Load_Prefix, handled by S1APINPCRelationFixPatch), and now
    /// NPCInventory.StartupItems (NPCInventory_Awake_Postfix -- a POSTFIX on
    /// NPCInventory.Awake(), separate from the TestItems prefix on the same method).
    /// Each one throws a MissingMethodException the first time it's actually invoked,
    /// caught by Il2CppInterop and logged rather than crashing the game, but still
    /// disrupts whatever it was supposed to do -- confirmed causing customer/dealer
    /// relationship data, message threads, and NPC startup inventory to fail to load
    /// correctly.
    ///
    /// Chasing each one by name as it turns up is fragile -- S1API's own log message
    /// text for the relation-delta bug didn't even match its real method name
    /// (NPCsLoader_Load_Prefix vs the real NPCLoader_Load_Prefix), which cost a whole
    /// round of "fixed" that silently did nothing. Instead of guessing more names, this
    /// sweeps every Harmony patch currently installed by any type under the S1API.*
    /// namespace and tests whether it can actually be JIT-prepared
    /// (RuntimeHelpers.PrepareMethod) -- this is exactly the same check Harmony itself
    /// performs when using a method as a patch target, and it's what produced the
    /// original "IL Compile Error" we saw when trying to patch EnsureNPCInventorySafeInit
    /// directly. A method whose own IL calls a now-missing member fails this every time;
    /// a working method just gets JIT-compiled a little early, which is harmless. Any
    /// patch that fails is surgically removed via Harmony.Unpatch, restoring vanilla
    /// behavior for whatever it was patching. This catches the known cases above AND any
    /// other broken S1API patch we haven't hit in a log yet, without more guessing.
    ///
    /// Runs after the two specific fixes (redundant with them, which is fine --
    /// Harmony.Unpatch on an already-removed patch is a safe no-op) as a general safety
    /// net, in case S1API has other broken patches this session hasn't triggered yet.
    /// </summary>
    public static class S1APIBrokenPatchSweeper
    {
        private static bool _applied;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            if (_applied) return;
            _applied = true;

            int removed = 0;
            try
            {
                foreach (var original in HarmonyLib.Harmony.GetAllPatchedMethods().ToList())
                {
                    HarmonyLib.Patches info;
                    try { info = HarmonyLib.Harmony.GetPatchInfo(original); }
                    catch { continue; }
                    if (info == null) continue;

                    var allPatches = info.Prefixes
                        .Concat(info.Postfixes)
                        .Concat(info.Transpilers)
                        .Concat(info.Finalizers);

                    foreach (var patch in allPatches)
                    {
                        var declaringType = patch.PatchMethod?.DeclaringType;
                        if (declaringType?.Namespace == null) continue;
                        if (!declaringType.Namespace.StartsWith("S1API")) continue;

                        if (TryPrepare(patch.PatchMethod)) continue;

                        try
                        {
                            harmony.Unpatch(original, patch.PatchMethod);
                            removed++;
                            OTCLog.Warning(OTCLog.Systems.Patch,
                                $"S1APIBrokenPatchSweeper: removed broken S1API patch " +
                                $"{declaringType.FullName}.{patch.PatchMethod.Name} from " +
                                $"{original.DeclaringType?.FullName}.{original.Name}() -- its IL " +
                                $"references a game member that no longer exists on this version.");
                        }
                        catch (Exception unpatchEx)
                        {
                            OTCLog.Warning(OTCLog.Systems.Patch,
                                $"S1APIBrokenPatchSweeper: found broken patch " +
                                $"{declaringType.FullName}.{patch.PatchMethod.Name} but failed to " +
                                $"remove it: {unpatchEx.Message}");
                        }
                    }
                }

                OTCLog.Msg(OTCLog.Systems.Patch,
                    $"S1APIBrokenPatchSweeper: swept all installed S1API patches, removed {removed} broken one(s).");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"S1APIBrokenPatchSweeper.Apply failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Attempts to JIT-prepare a patch method the same way Harmony would when using
        /// it as a detour target. Throws exactly when the method's own IL references a
        /// member that no longer exists (the same "IL Compile Error" class of failure).
        /// Harmless for a working method -- just JITs it a little earlier than usual.
        /// </summary>
        private static bool TryPrepare(MethodInfo method)
        {
            if (method == null) return false;
            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
