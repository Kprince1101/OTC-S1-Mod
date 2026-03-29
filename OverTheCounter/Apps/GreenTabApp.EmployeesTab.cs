using OverTheCounter.UI;
using S1API.UI;
using UnityEngine;

#if IL2CPP
using Il2CppTMPro;
#else
using TMPro;
#endif

namespace OverTheCounter.Apps
{
    public partial class GreenTabApp
    {
        // ==================================================================
        //  Employees tab (placeholder)
        // ==================================================================

        private TextMeshProUGUI _empTitleLabel;

        private void BuildEmployeesPanel(Transform parent)
        {
            var panel = UIFactory.Panel("EmployeesPanel", parent, BgDark);
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(NAV_WIDTH_FRAC, 0);
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            _tabPanels[AppTab.Employees] = panel;

            float pad = 10f;

            // Title — property name + inline hint, updated during refresh
            _empTitleLabel = TMPFactory.Text("EmpTabTitle", "Employees",
                panel.transform, 16, TextAlignmentOptions.TopLeft);
            _empTitleLabel.color = Color.white;
            _empTitleLabel.richText = true;
            var titleRect = _empTitleLabel.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(0.95f, 1);
            titleRect.offsetMin = new Vector2(pad, -28);
            titleRect.offsetMax = new Vector2(0, -pad);

            // Centered "Coming Soon" message
            var msg = TMPFactory.Text("EmployeesPlaceholder",
                "Coming Soon",
                panel.transform, 18, TextAlignmentOptions.Center, FontStyles.Bold);
            msg.color = TextMuted;
            var msgRect = msg.gameObject.GetComponent<RectTransform>();
            msgRect.anchorMin = new Vector2(0.2f, 0.4f);
            msgRect.anchorMax = new Vector2(0.8f, 0.6f);
            msgRect.offsetMin = Vector2.zero;
            msgRect.offsetMax = Vector2.zero;

            var sub = TMPFactory.Text("EmployeesSub",
                "Employee management will be available in a future update.",
                panel.transform, 15, TextAlignmentOptions.Center);
            sub.color = TextDim;
            var subRect = sub.gameObject.GetComponent<RectTransform>();
            subRect.anchorMin = new Vector2(0.15f, 0.28f);
            subRect.anchorMax = new Vector2(0.85f, 0.4f);
            subRect.offsetMin = Vector2.zero;
            subRect.offsetMax = Vector2.zero;

            panel.SetActive(false);
        }
    }
}
