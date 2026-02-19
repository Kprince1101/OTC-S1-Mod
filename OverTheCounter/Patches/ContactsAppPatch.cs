using MelonLoader;

#if IL2CPP
using ContactsAppType = Il2CppScheduleOne.UI.Phone.ContactsApp.ContactsApp;
#else
using ContactsAppType = ScheduleOne.UI.Phone.ContactsApp.ContactsApp;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Fixes ContactsApp initialization race in multiplayer.
    /// Update() can tick before Start() completes, causing RegionDict to be empty
    /// and appContainer to leak horizontally over other phone apps.
    /// Harmony can't patch this method (generic App&lt;T&gt; base class in IL2CPP),
    /// so we fix the state from Core.OnLateUpdate() instead.
    /// </summary>
    public static class ContactsAppFix
    {
        private static bool _fixed;
        private static ContactsAppType _cachedApp;

        public static void Tick()
        {
            if (_fixed)
                return;

            try
            {
                if (_cachedApp == null)
                {
                    _cachedApp = UnityEngine.Object.FindObjectOfType<ContactsAppType>();
                    if (_cachedApp == null)
                        return;
                }

#if IL2CPP
                var dict = _cachedApp.RegionDict;
                if (dict != null && dict.Count > 0)
                {
                    _fixed = true;
                    return;
                }

                // Start() hasn't populated RegionDict yet — deactivate the container
                // to prevent the horizontal contacts app from leaking over other apps
                var container = _cachedApp.appContainer;
                if (container != null)
                {
                    var go = container.gameObject;
                    if (go != null && go.activeSelf)
                        go.SetActive(false);
                }
#else
                // RegionDict and appContainer are private on Mono — skip the fix
                _fixed = true;
#endif
            }
            catch
            {
                // Silently ignore — the app may not exist yet
            }
        }

        public static void Reset()
        {
            _fixed = false;
            _cachedApp = null;
        }
    }
}
