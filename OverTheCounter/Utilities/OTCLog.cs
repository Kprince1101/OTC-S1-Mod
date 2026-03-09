using MelonLoader;

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Centralized logging for all OTC systems.
    /// All output uses the [OverTheCounter] prefix with a per-system label.
    /// Warning and Error always fire. Msg is auto-gated by IsVerbose().
    /// </summary>
    public static class OTCLog
    {
        private static readonly MelonLogger.Instance Logger = new("OverTheCounter");

        /// <summary>Logs a message only if the system's verbose toggle is enabled.</summary>
        public static void Msg(string system, string msg)
        {
            if (IsVerbose(system))
                Logger.Msg($"{system}: {msg}");
        }

        public static void Warning(string system, string msg) => Logger.Warning($"{system}: {msg}");
        public static void Error(string system, string msg) => Logger.Error($"{system}: {msg}");

        /// <summary>Checks the per-system verbose toggle.</summary>
        public static bool IsVerbose(string system) => system switch
        {
            Systems.Manager => Config.ManagerVerboseLogging.Value,
            Systems.Drifter => Config.DrifterVerboseLogging.Value,
            Systems.Desperation => Config.DesperationVerboseLogging.Value,
            Systems.NPC => Config.NpcVerboseLogging.Value,
            Systems.Network => Config.NetworkVerboseLogging.Value,
            Systems.Quest => Config.QuestVerboseLogging.Value,
            Systems.Notification => Config.NotificationVerboseLogging.Value,
            Systems.Patch => Config.PatchVerboseLogging.Value,
            _ => Config.VerboseLogging.Value,
        };

        public static class Systems
        {
            public const string Manager = "Manager";
            public const string Drifter = "Drifter";
            public const string Desperation = "Desperation";
            public const string NPC = "NPC";
            public const string Network = "Network";
            public const string Quest = "Quest";
            public const string Notification = "Notification";
            public const string Patch = "Patch";
            public const string Customer = "Customer";
            public const string General = "General";
        }
    }
}
