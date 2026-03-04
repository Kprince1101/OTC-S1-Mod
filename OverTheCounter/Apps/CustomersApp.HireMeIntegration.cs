using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using S1API.UI;

namespace OverTheCounter.Apps
{
    /// <summary>
    /// Optional soft integration with the HireMe mod (UnicornsCanMod-HireMe).
    /// Detected at runtime via reflection — no hard assembly reference or dependency.
    /// If HireMe is not installed the feature is silently absent.
    /// Works with both Mono and IL2CPP builds of HireMe.
    /// </summary>
    public partial class CustomersApp
    {
        // Reflection cache — populated once on first IsHireMeInstalled() call.
        private static FieldInfo _hireMeInterfaceField;
        private static MethodInfo _hireMeOpenMethod;
        private static bool _hireMeChecked;

        // ─── Reflection helpers ─────────────────────────────────────────────────

        private static bool IsHireMeInstalled()
        {
            if (_hireMeChecked) return _hireMeInterfaceField != null;
            _hireMeChecked = true;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var uiMgrType = asm.GetType("HireMe.UI.UIManager");
                    if (uiMgrType == null) continue;

                    _hireMeInterfaceField = uiMgrType.GetField("hiringInterface",
                        BindingFlags.Public | BindingFlags.Static);

                    if (_hireMeInterfaceField != null)
                    {
                        var ifaceType = _hireMeInterfaceField.FieldType;
                        _hireMeOpenMethod = ifaceType.GetMethod("Open",
                            BindingFlags.Public | BindingFlags.Instance);
                    }

                    break;
                }
            }
            catch { }

            return _hireMeInterfaceField != null;
        }

        // ─── Public/internal API ────────────────────────────────────────────────

        /// <summary>
        /// Call once after Config.Initialize(). If HireMe is installed, auto-enable AlternateHire.
        /// </summary>
        internal static void ApplyHireMeDefaults()
        {
            if (IsHireMeInstalled())
                Config.AlternateHire.SetOverride(true);
        }

        // ─── Session lifecycle ──────────────────────────────────────────────────

        private void TryOpenHireMe()
        {
            if (_hireMeInterfaceField == null || _hireMeOpenMethod == null) return;
            try
            {
                var iface = _hireMeInterfaceField.GetValue(null);
                if (iface == null) return;
                _hireMeOpenMethod.Invoke(iface, null);
            }
            catch { }
        }

        // ─── UI ────────────────────────────────────────────────────────────────

        private void AddHireMeButton(Transform parent)
        {
            if (!IsHireMeInstalled()) return;

            var btn = UIFactory.Panel("HireMeBtn", parent, new Color(0.08f, 0.28f, 0.22f));
            btn.AddComponent<LayoutElement>().preferredWidth = 148;

            var lbl = UIFactory.Text("Label", "\u2795 Hire / Transfer", btn.transform, 15, TextAnchor.MiddleCenter);
            lbl.color = new Color(0.35f, 0.9f, 0.5f);
            var lblRect = lbl.gameObject.GetComponent<RectTransform>();
            lblRect.anchorMin = Vector2.zero;
            lblRect.anchorMax = Vector2.one;
            lblRect.offsetMin = new Vector2(4, 0);
            lblRect.offsetMax = new Vector2(-4, 0);

            var button = btn.AddComponent<Button>();
            button.onClick.AddListener(new Action(TryOpenHireMe));

            var colors = button.colors;
            colors.highlightedColor = new Color(0.12f, 0.40f, 0.30f);
            colors.pressedColor = new Color(0.06f, 0.20f, 0.16f);
            button.colors = colors;
        }
    }
}
