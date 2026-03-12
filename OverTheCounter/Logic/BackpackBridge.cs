using OverTheCounter.Utilities;
using System;
using System.Collections;
using System.Reflection;

#if IL2CPP
using Il2CppScheduleOne.ItemFramework;
#else
using ScheduleOne.ItemFramework;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Optional soft integration with the PackRat backpack mod (SirTidez-PackRat).
    /// Detected at runtime via reflection - no hard assembly reference or dependency.
    /// If PackRat is not installed, updated incompatibly, or removed, all methods
    /// degrade silently (IsInstalled=false, GetSlots=empty).
    /// </summary>
    public static class BackpackBridge
    {
        private static bool _checked;
        private static PropertyInfo _instanceProp;
        private static PropertyInfo _isUnlockedProp;
        private static PropertyInfo _itemSlotsProp;
        private static bool _warnedOnce;

        /// <summary>
        /// Returns true if the PackRat mod is loaded and exposes the expected API.
        /// Scans assemblies once and caches the result.
        /// </summary>
        public static bool IsInstalled()
        {
            if (_checked) return _instanceProp != null;
            _checked = true;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = asm.GetType("PackRat.PlayerBackpack");
                    if (type == null) continue;

                    _instanceProp = type.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static);
                    _isUnlockedProp = type.GetProperty("IsUnlocked",
                        BindingFlags.Public | BindingFlags.Instance);
                    _itemSlotsProp = type.GetProperty("ItemSlots",
                        BindingFlags.Public | BindingFlags.Instance);

                    if (_instanceProp == null || _isUnlockedProp == null || _itemSlotsProp == null)
                    {
                        OTCLog.Warning(OTCLog.Systems.Patch,
                            "PackRat detected but API changed - backpack integration disabled");
                        _instanceProp = null;
                    }

                    break;
                }
            }
            catch { }

            return _instanceProp != null;
        }

        /// <summary>
        /// Returns true if PackRat is installed and the player has unlocked a backpack tier.
        /// </summary>
        public static bool IsUnlocked()
        {
            if (!IsInstalled()) return false;
            try
            {
                var instance = _instanceProp.GetValue(null);
                if (instance == null) return false;
                return (bool)_isUnlockedProp.GetValue(instance);
            }
            catch
            {
                WarnOnce();
                return false;
            }
        }

        /// <summary>
        /// Returns the backpack's item slots, or an empty array if unavailable.
        /// </summary>
        public static ItemSlot[] GetSlots()
        {
            if (!IsInstalled()) return Array.Empty<ItemSlot>();
            try
            {
                var instance = _instanceProp.GetValue(null);
                if (instance == null) return Array.Empty<ItemSlot>();

                if (!(bool)_isUnlockedProp.GetValue(instance))
                    return Array.Empty<ItemSlot>();

                var slotsObj = _itemSlotsProp.GetValue(instance);
                if (slotsObj is IList list)
                {
                    var result = new ItemSlot[list.Count];
                    int count = 0;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] is ItemSlot slot)
                            result[count++] = slot;
                    }

                    if (count < result.Length)
                        Array.Resize(ref result, count);

                    return result;
                }
            }
            catch
            {
                WarnOnce();
            }
            return Array.Empty<ItemSlot>();
        }

        private static void WarnOnce()
        {
            if (_warnedOnce) return;
            _warnedOnce = true;
            OTCLog.Warning(OTCLog.Systems.Patch,
                "PackRat backpack access failed - mod may have updated. Backpack integration disabled for this session.");
        }
    }
}
