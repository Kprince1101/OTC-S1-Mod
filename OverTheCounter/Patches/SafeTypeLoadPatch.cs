using HarmonyLib;
using OverTheCounter.Utilities;
using System;
using System.Linq;
using System.Reflection;

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Patches Assembly.GetTypes()-based scanning to gracefully handle
    /// ReflectionTypeLoadException. This exception occurs when our assembly
    /// contains types referencing SteamNetworkLib (e.g. RawStringSerializer
    /// implements ISyncSerializer) but the DLL is absent at runtime.
    ///
    /// Without this patch, Harmony's AccessTools.GetTypesFromAssembly logs
    /// a warning on ML 0.7.2+ and S1API's NPC discovery skips our entire
    /// assembly, preventing OTC NPCs from spawning.
    /// </summary>
    public static class SafeTypeLoadPatch
    {
        private static bool _applied;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            if (_applied) return;
            _applied = true;

            try
            {
                var getTypesFromAsm = AccessTools.Method(typeof(AccessTools), "GetTypesFromAssembly");
                if (getTypesFromAsm != null)
                {
                    harmony.Patch(getTypesFromAsm,
                        prefix: new HarmonyMethod(typeof(SafeTypeLoadPatch), nameof(GetTypesFromAssemblyPrefix)));
                    OTCLog.Msg(OTCLog.Systems.Patch, "Patched AccessTools.GetTypesFromAssembly (safe type loading).");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Failed to apply safe type load patch: {ex.Message}");
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

        /// <summary>
        /// Prefix for AccessTools.GetTypesFromAssembly. Replaces Harmony's
        /// implementation with one that silently handles ReflectionTypeLoadException
        /// instead of logging a warning on ML 0.7.2+.
        /// </summary>
        public static bool GetTypesFromAssemblyPrefix(Assembly assembly, ref Type[] __result)
        {
            __result = SafeGetTypes(assembly);
            return false;
        }
    }
}
