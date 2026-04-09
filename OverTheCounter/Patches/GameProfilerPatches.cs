using HarmonyLib;
using OverTheCounter.Utilities;

#if IL2CPP
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Vehicles;
#else
using ScheduleOne.GameTime;
using ScheduleOne.NPCs;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Vehicles;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Optional Harmony patches that wrap vanilla game Update() methods with
    /// PerfTracker.Begin/End calls. Only installed when Config.ProfilingEnabled
    /// is true at startup — zero overhead otherwise (no Harmony dispatch).
    /// <para>
    /// Region names use a "Game." prefix to visually separate them from OTC
    /// regions in the report. Per-instance systems (NPCs, vehicles) fire once
    /// per entity per frame, so call counts reflect entity count * frame count.
    /// </para>
    /// </summary>
    internal static class GameProfilerPatches
    {
        /// <summary>
        /// Applies all game profiler patches. Only call when profiling is enabled.
        /// </summary>
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            if (!Config.ProfilingEnabled.Value) return;

            harmony.PatchAll(typeof(ProfileNPCMovement));
            harmony.PatchAll(typeof(ProfileNPCBehaviour));
            harmony.PatchAll(typeof(ProfilePlayerMovement));
            harmony.PatchAll(typeof(ProfilePlayerCamera));
            harmony.PatchAll(typeof(ProfileTimeManager));
            harmony.PatchAll(typeof(ProfileLandVehicle));

            OTCLog.Msg(OTCLog.Systems.Patch, "Game profiler patches applied (6 systems).");
        }

        [HarmonyPatch(typeof(NPCMovement), "Update")]
        private static class ProfileNPCMovement
        {
            public static void Prefix() => PerfTracker.Begin("Game.NPCMovement");
            public static void Postfix() => PerfTracker.End("Game.NPCMovement");
        }

        [HarmonyPatch(typeof(NPCBehaviour), "Update")]
        private static class ProfileNPCBehaviour
        {
            public static void Prefix() => PerfTracker.Begin("Game.NPCBehaviour");
            public static void Postfix() => PerfTracker.End("Game.NPCBehaviour");
        }

        [HarmonyPatch(typeof(PlayerMovement), "Update")]
        private static class ProfilePlayerMovement
        {
            public static void Prefix() => PerfTracker.Begin("Game.PlayerMovement");
            public static void Postfix() => PerfTracker.End("Game.PlayerMovement");
        }

        [HarmonyPatch(typeof(PlayerCamera), "Update")]
        private static class ProfilePlayerCamera
        {
            public static void Prefix() => PerfTracker.Begin("Game.PlayerCamera");
            public static void Postfix() => PerfTracker.End("Game.PlayerCamera");
        }

        [HarmonyPatch(typeof(TimeManager), "Update")]
        private static class ProfileTimeManager
        {
            public static void Prefix() => PerfTracker.Begin("Game.TimeManager");
            public static void Postfix() => PerfTracker.End("Game.TimeManager");
        }

        [HarmonyPatch(typeof(LandVehicle), "Update")]
        private static class ProfileLandVehicle
        {
            public static void Prefix() => PerfTracker.Begin("Game.LandVehicle");
            public static void Postfix() => PerfTracker.End("Game.LandVehicle");
        }
    }
}
