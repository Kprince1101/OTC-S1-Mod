using HarmonyLib;

#if IL2CPP
using Il2CppScheduleOne.Economy;
#else
using ScheduleOne.Economy;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Guards Customer.SetUpResponseCallbacks from crashing on non-networked
    /// Customer components (e.g. drifters added via AddComponent).
    /// The method is [ObserversRpc] so FishNet rewrites it to call
    /// RpcWriter which accesses base.NetworkManager — null if not spawned.
    /// </summary>
    [HarmonyPatch(typeof(Customer), "SetUpResponseCallbacks")]
    public static class CustomerStartPatch
    {
        public static bool Prefix(Customer __instance)
        {
            try { return __instance.NetworkManager != null; }
            catch { return false; }
        }
    }
}
