// Mono compatibility polyfills for IL2CPP-specific APIs.
// On IL2CPP, TryCast<T>/Cast<T> come from Il2CppInterop.Runtime.
// On Mono, we polyfill them as standard C# casts.
// On IL2CPP, System.Action implicitly converts to UnityAction via Il2CppSystem.Action.
// On Mono, we provide extension overloads so AddListener/RemoveListener accept Action.
#if !IL2CPP
using UnityEngine.Events;

namespace OverTheCounter
{
    public static class Il2CppCompat
    {
        public static T TryCast<T>(this object obj) where T : class => obj as T;
        public static T Cast<T>(this object obj) where T : class => (T)obj;
    }

    public static class UnityEventCompat
    {
        public static void AddListener(this UnityEvent ev, System.Action action)
            => ev.AddListener(new UnityAction(action));

        public static void RemoveListener(this UnityEvent ev, System.Action action)
            => ev.RemoveListener(new UnityAction(action));

        public static void AddListener<T0>(this UnityEvent<T0> ev, System.Action<T0> action)
            => ev.AddListener(new UnityAction<T0>(action));

        public static void RemoveListener<T0>(this UnityEvent<T0> ev, System.Action<T0> action)
            => ev.RemoveListener(new UnityAction<T0>(action));
    }
}
#endif
