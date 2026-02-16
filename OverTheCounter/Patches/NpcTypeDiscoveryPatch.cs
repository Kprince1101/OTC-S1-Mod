using HarmonyLib;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Fixes a bug in S1API's NPC type discovery that prevents our NPCs from spawning
    /// when SteamNetworkLib is not installed.
    ///
    /// Root cause:
    ///   Our NetworkSyncBridge.cs contains RawStringSerializer which implements
    ///   SteamNetworkLib.Sync.ISyncSerializer. When SteamNetworkLib is absent,
    ///   calling Assembly.GetTypes() on the OverTheCounter assembly throws a
    ///   ReflectionTypeLoadException — the CLR cannot resolve ISyncSerializer.
    ///
    /// Why S1API doesn't handle this:
    ///   Both NPC.CreateWrapperForNetworkSpawnedNPC (S1API.Entities/NPC.cs ~line 720)
    ///   and NPC.PreRegisterAllNpcPrefabs (~line 942) wrap GetTypes() in a bare
    ///   catch { continue; } which skips the ENTIRE assembly on any exception.
    ///   This means VicNPC, StaticNPC, and BellaNPC are never discovered.
    ///
    /// The fix:
    ///   A Harmony transpiler replaces Assembly.GetTypes() calls in those two methods
    ///   with SafeGetTypes(), which catches ReflectionTypeLoadException and returns
    ///   the partial type list (all types that loaded successfully). This is the same
    ///   pattern S1API already uses in SaveableAutoRegistry (~line 92).
    ///
    /// Why a transpiler instead of prefix/postfix:
    ///   The bare catch { continue; } swallows the exception before any postfix runs.
    ///   A prefix can't change the GetTypes() call itself. The transpiler replaces the
    ///   call instruction directly, so the exception is handled before the bare catch
    ///   ever sees it.
    /// </summary>
    public static class NpcTypeDiscoveryPatch
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:NpcTypePatch");

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var npcType = AccessTools.TypeByName("S1API.Entities.NPC");
                if (npcType == null)
                {
                    Logger.Warning("S1API.Entities.NPC not found — NPC type discovery patch skipped.");
                    return;
                }

                var transpiler = new HarmonyMethod(typeof(NpcTypeDiscoveryPatch), nameof(Transpiler));

                var createWrapper = AccessTools.Method(npcType, "CreateWrapperForNetworkSpawnedNPC");
                if (createWrapper != null)
                {
                    harmony.Patch(createWrapper, transpiler: transpiler);
                    Logger.Msg("Patched CreateWrapperForNetworkSpawnedNPC (safe GetTypes).");
                }

                var preRegister = AccessTools.Method(npcType, "PreRegisterAllNpcPrefabs");
                if (preRegister != null)
                {
                    harmony.Patch(preRegister, transpiler: transpiler);
                    Logger.Msg("Patched PreRegisterAllNpcPrefabs (safe GetTypes).");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to apply NPC type discovery patches: {ex.Message}");
            }
        }

        /// <summary>
        /// Replaces Assembly.GetTypes() calls with SafeGetTypes() in the IL stream.
        /// Scans each instruction; when it finds a call to Assembly.GetTypes(),
        /// it emits a call to SafeGetTypes() instead. All other instructions pass through.
        /// </summary>
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getTypesMethod = typeof(Assembly).GetMethod(nameof(Assembly.GetTypes), Type.EmptyTypes);
            var safeMethod = typeof(NpcTypeDiscoveryPatch).GetMethod(nameof(SafeGetTypes), BindingFlags.Public | BindingFlags.Static);

            foreach (var instruction in instructions)
            {
                if (instruction.Calls(getTypesMethod))
                    yield return new CodeInstruction(OpCodes.Call, safeMethod);
                else
                    yield return instruction;
            }
        }

        /// <summary>
        /// Drop-in replacement for Assembly.GetTypes(). On success, returns the full
        /// type array. On ReflectionTypeLoadException (some types reference missing
        /// assemblies), returns only the types that loaded — filtering out null entries
        /// that represent the unresolvable types.
        /// </summary>
        public static Type[] SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null).ToArray();
            }
        }
    }
}
