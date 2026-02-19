#if IL2CPP
using Il2CppFishNet;
#else
using FishNet;
#endif

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Multiplayer authority helper. All shared-state mutations must be gated
    /// behind <see cref="IsHost"/> so only the server/host executes them.
    /// Returns true in single-player (no NetworkManager present).
    /// </summary>
    public static class NetworkHelper
    {
        /// <summary>
        /// True when running as server/host. Safe to call anytime —
        /// returns true in single-player (no NetworkManager present).
        /// </summary>
        public static bool IsHost =>
            InstanceFinder.NetworkManager == null || InstanceFinder.IsServer;
    }
}
