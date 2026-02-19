using MelonLoader;
using S1API.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using OverTheCounter.Logic;
using OverTheCounter.Utilities;
using OverTheCounter.SaveData;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Map;
#else
using ScheduleOne.Economy;
using ScheduleOne.NPCs;
using ScheduleOne.Map;
#endif

namespace OverTheCounter.Apps
{
    public partial class CustomersApp
    {
        private void BuildCustomersPage(Transform parent)
        {
            // Legend bar (tier 2+)
            var legendObj = UIFactory.Panel("Legend", parent, new Color(0.18f, 0.18f, 0.18f));
            var legendRect = legendObj.GetComponent<RectTransform>();
            legendRect.anchorMin = new Vector2(0, 1);
            legendRect.anchorMax = new Vector2(1, 1);
            legendRect.pivot = new Vector2(0.5f, 1);
            legendRect.anchoredPosition = Vector2.zero;
            legendRect.sizeDelta = new Vector2(0, 25);
            _legendObj = legendObj;

            // Legend content
            var legendText = UIFactory.Text("LegendText", "Addiction:", legendObj.transform, 12, TextAnchor.MiddleLeft);
            var legendTextRect = legendText.gameObject.GetComponent<RectTransform>();
            legendTextRect.anchorMin = new Vector2(0, 0);
            legendTextRect.anchorMax = new Vector2(0, 1);
            legendTextRect.pivot = new Vector2(0, 0.5f);
            legendTextRect.anchoredPosition = new Vector2(15, 0);
            legendTextRect.sizeDelta = new Vector2(70, 0);
            legendText.color = new Color(0.7f, 0.7f, 0.7f);

            var greenSample = UIFactory.Panel("GreenSample", legendObj.transform, new Color(0.2f, 0.7f, 0.2f));
            var greenRect = greenSample.GetComponent<RectTransform>();
            greenRect.anchorMin = new Vector2(0, 0.3f);
            greenRect.anchorMax = new Vector2(0, 0.7f);
            greenRect.pivot = new Vector2(0, 0.5f);
            greenRect.anchoredPosition = new Vector2(90, 0);
            greenRect.sizeDelta = new Vector2(30, 0);

            var highLabel = UIFactory.Text("HighLabel", "High", legendObj.transform, 10, TextAnchor.MiddleLeft);
            var highRect = highLabel.gameObject.GetComponent<RectTransform>();
            highRect.anchorMin = new Vector2(0, 0);
            highRect.anchorMax = new Vector2(0, 1);
            highRect.pivot = new Vector2(0, 0.5f);
            highRect.anchoredPosition = new Vector2(125, 0);
            highRect.sizeDelta = new Vector2(35, 0);
            highLabel.color = new Color(0.6f, 0.6f, 0.6f);

            var redSample = UIFactory.Panel("RedSample", legendObj.transform, new Color(0.5f, 0.2f, 0.2f));
            var redRect = redSample.GetComponent<RectTransform>();
            redRect.anchorMin = new Vector2(0, 0.3f);
            redRect.anchorMax = new Vector2(0, 0.7f);
            redRect.pivot = new Vector2(0, 0.5f);
            redRect.anchoredPosition = new Vector2(165, 0);
            redRect.sizeDelta = new Vector2(30, 0);

            var lowLabel = UIFactory.Text("LowLabel", "Low", legendObj.transform, 10, TextAnchor.MiddleLeft);
            var lowRect = lowLabel.gameObject.GetComponent<RectTransform>();
            lowRect.anchorMin = new Vector2(0, 0);
            lowRect.anchorMax = new Vector2(0, 1);
            lowRect.pivot = new Vector2(0, 0.5f);
            lowRect.anchoredPosition = new Vector2(200, 0);
            lowRect.sizeDelta = new Vector2(35, 0);
            lowLabel.color = new Color(0.6f, 0.6f, 0.6f);

            // Billing bar
            var billingBarObj = UIFactory.Panel("BillingBar", parent, new Color(0.15f, 0.15f, 0.15f));
            var billingBarRt = billingBarObj.GetComponent<RectTransform>();
            billingBarRt.anchorMin = new Vector2(0, 1);
            billingBarRt.anchorMax = new Vector2(1, 1);
            billingBarRt.pivot = new Vector2(0.5f, 1);
            billingBarRt.anchoredPosition = new Vector2(0, -25); // below legend
            billingBarRt.sizeDelta = new Vector2(0, 32);
            _billingBar = billingBarObj;

            var billingTextComp = UIFactory.Text("BillingText", "", billingBarObj.transform, 16, TextAnchor.MiddleLeft);
            billingTextComp.supportRichText = true;
            billingTextComp.horizontalOverflow = HorizontalWrapMode.Wrap;
            billingTextComp.color = new Color(0.85f, 0.85f, 0.85f);
            var btRect = billingTextComp.gameObject.GetComponent<RectTransform>();
            btRect.anchorMin = Vector2.zero;
            btRect.anchorMax = Vector2.one;
            btRect.offsetMin = new Vector2(12, 0);
            btRect.offsetMax = new Vector2(-12, 0);
            _billingText = billingTextComp;

            // Scroll view
            var contentRect = UIFactory.ScrollableVerticalList("CustomerScroll", parent, out ScrollRect scrollRect);

            _customersScrollRect = scrollRect.GetComponent<RectTransform>();
            _customersScrollRect.anchorMin = Vector2.zero;
            _customersScrollRect.anchorMax = Vector2.one;
            _customersScrollRect.offsetMin = Vector2.zero;
            _customersScrollRect.offsetMax = new Vector2(0, -57); // legend(25) + billing(32)

            var contentLayout = contentRect.GetComponent<VerticalLayoutGroup>();
            if (contentLayout != null)
            {
                contentLayout.childControlHeight = true;
                contentLayout.childControlWidth = true;
                contentLayout.childForceExpandHeight = false;
                contentLayout.childForceExpandWidth = true;
                contentLayout.childAlignment = TextAnchor.UpperCenter;
                contentLayout.spacing = 15;
                contentLayout.padding = new RectOffset(10, 10, 5, 15);
            }

            scrollRect.horizontal = false;
            scrollRect.scrollSensitivity = 20f;

            _customersContentParent = contentRect.transform;
            PopulateCustomerList(_customersContentParent);
        }

        private void RefreshCustomersPage()
        {
            if (_customersContentParent == null) return;

            int tier = GetEffectiveTier();

            // Show/hide legend (tier 2+)
            bool showLegend = tier >= 2;
            if (_legendObj != null)
                _legendObj.SetActive(showLegend);

            // Update billing bar
            bool showBilling = tier > 0;
            if (_billingBar != null)
                _billingBar.SetActive(showBilling);

            if (showBilling && _billingText != null)
            {
                int payDay = StaticSaveData.Instance?.SaasNextPaymentDay ?? 0;
                int passes = StaticSaveData.Instance?.DayPassCount ?? 0;
                int displayDays = payDay - passes;
                if (displayDays < 0) displayDays = 0;

                if (displayDays <= 1)
                    _billingText.text = $"<b>Subscription renews at 5 AM</b>  <color=#4CAF50><b>(${Config.SaasWeeklyCost.Value:N0})</b></color>";
                else
                    _billingText.text = $"<b>Subscription renews in {displayDays} days</b>  <color=#4CAF50><b>(${Config.SaasWeeklyCost.Value:N0})</b></color>";
            }

            // Dynamically position billing bar and scroll view based on visible bars
            float yOffset = 0f;
            if (showLegend) yOffset -= 25f;

            if (showBilling && _billingBar != null)
            {
                var billingRt = _billingBar.GetComponent<RectTransform>();
                billingRt.anchoredPosition = new Vector2(0, yOffset);
                yOffset -= 32f;
            }

            if (_customersScrollRect != null)
                _customersScrollRect.offsetMax = new Vector2(0, yOffset);

            ClearChildren(_customersContentParent);
            PopulateCustomerList(_customersContentParent);
        }

        // ==================================================================
        // Customer list population
        // ==================================================================

        private void ShowPaywallScreen(Transform contentParent)
        {
            bool hasMetStatic = StaticSaveData.Instance != null && StaticSaveData.Instance.CrmTier > 0;

            if (hasMetStatic)
            {
                var titleObj = UIFactory.Text("PaywallTitle", "<b>SERVICE OFFLINE</b>", contentParent, 22, TextAnchor.MiddleCenter);
                titleObj.color = new Color(0.7f, 0.2f, 0.2f);
                var titleLayout = titleObj.gameObject.AddComponent<LayoutElement>();
                titleLayout.preferredHeight = 40;
                titleLayout.flexibleWidth = 1;

                var subtitleObj = UIFactory.Text("PaywallSubtitle",
                    "Active subscription required.\nVisit Static at the Casino to renew.",
                    contentParent, 14, TextAnchor.MiddleCenter);
                subtitleObj.color = new Color(0.6f, 0.6f, 0.6f);
                var subtitleLayout = subtitleObj.gameObject.AddComponent<LayoutElement>();
                subtitleLayout.preferredHeight = 50;
                subtitleLayout.flexibleWidth = 1;
            }
            else
            {
                var titleObj = UIFactory.Text("LicenseTitle", "<b>LICENSE INVALID</b>", contentParent, 22, TextAnchor.MiddleCenter);
                titleObj.color = new Color(0.6f, 0.6f, 0.6f);
                var titleLayout = titleObj.gameObject.AddComponent<LayoutElement>();
                titleLayout.preferredHeight = 40;
                titleLayout.flexibleWidth = 1;

                var subtitleObj = UIFactory.Text("LicenseSubtitle",
                    "Please wait for an authorized\nrepresentative to contact you.",
                    contentParent, 14, TextAnchor.MiddleCenter);
                subtitleObj.color = new Color(0.5f, 0.5f, 0.5f);
                var subtitleLayout = subtitleObj.gameObject.AddComponent<LayoutElement>();
                subtitleLayout.preferredHeight = 50;
                subtitleLayout.flexibleWidth = 1;
            }
        }

        private void PopulateCustomerList(Transform contentParent)
        {
            int effectiveTier = GetEffectiveTier();

            if (effectiveTier == 0)
            {
                ShowPaywallScreen(contentParent);
                return;
            }

            var unlocked = Customer.UnlockedCustomers;
            var locked = Customer.LockedCustomers;

            if ((unlocked == null || unlocked.Count == 0) && (locked == null || locked.Count == 0))
            {
                UIFactory.Text("Empty", "No Customers Known", contentParent, 20, TextAnchor.MiddleCenter);
                return;
            }

            var displayList = new List<CustomerDisplayData>();

            if (unlocked != null)
            {
                foreach (var c in unlocked) displayList.Add(new CustomerDisplayData { Customer = c, IsLocked = false });
            }
            if (locked != null)
            {
                foreach (var c in locked) displayList.Add(new CustomerDisplayData { Customer = c, IsLocked = true });
            }

            var grouped = displayList
                .Where(d => d.Customer != null && d.Customer.NPC != null)
                .GroupBy(d => d.Customer.NPC.Region)
                .OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                CreateNeighborhoodSection(contentParent, group.Key.ToString(), group.ToList(), effectiveTier, group.Key);
            }
        }

        private void CreateNeighborhoodSection(Transform parent, string regionName, List<CustomerDisplayData> dataList, int effectiveTier, EMapRegion region)
        {
            bool regionUnlocked = IsRegionUnlocked(effectiveTier, region);

            if (!regionUnlocked)
            {
                var lockedHeader = UIFactory.Text($"Header_{regionName}",
                    $"<b>{regionName}</b> <color=#666666>[NO SIGNAL]</color>",
                    parent, 18, TextAnchor.MiddleCenter);
                lockedHeader.color = new Color(0.5f, 0.5f, 0.5f);
                var lockedLayout = lockedHeader.gameObject.AddComponent<LayoutElement>();
                lockedLayout.flexibleWidth = 1;
                return;
            }

            string headerText = $"<b>{regionName}</b>";
            var headerObj = UIFactory.Text($"Header_{regionName}", headerText, parent, 18, TextAnchor.MiddleCenter);
            headerObj.color = new Color(0.8f, 0.8f, 0.8f);

            var headerLayout = headerObj.gameObject.AddComponent<LayoutElement>();
            headerLayout.flexibleWidth = 1;

            var gridObj = UIFactory.Panel($"Grid_{regionName}", parent, Color.clear);

            var gridLayoutElement = gridObj.AddComponent<LayoutElement>();
            gridLayoutElement.flexibleWidth = 1;

            var gridLayout = gridObj.AddComponent<GridLayoutGroup>();
            gridLayout.cellSize = new Vector2(CELL_WIDTH, CELL_HEIGHT);
            gridLayout.spacing = new Vector2(10, 10);
            gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gridLayout.constraintCount = COLUMNS;
            gridLayout.childAlignment = TextAnchor.UpperCenter;
            gridLayout.startCorner = GridLayoutGroup.Corner.UpperLeft;
            gridLayout.startAxis = GridLayoutGroup.Axis.Horizontal;

            var gridFitter = gridObj.AddComponent<ContentSizeFitter>();
            gridFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            foreach (var item in dataList)
            {
                CreateCustomerCell(gridObj.transform, item.Customer, item.IsLocked, effectiveTier);
            }
        }

        private void CreateCustomerCell(Transform gridParent, Customer customer, bool isLocked, int effectiveTier)
        {
            bool isDesperate = !isLocked && DesperationManager.IsDesperate(customer.NPC.ID);

            Color cellColor = isDesperate ? new Color(0.4f, 0.15f, 0.15f) : new Color(0.2f, 0.2f, 0.2f);
            var cellObj = UIFactory.Panel($"Cell_{customer.NPC.fullName}", gridParent, cellColor);

            if (isDesperate)
            {
                var outline = cellObj.AddComponent<Outline>();
                outline.effectColor = new Color(0.9f, 0.2f, 0.2f, 1f);
                outline.effectDistance = new Vector2(2f, 2f);
            }

            var btn = cellObj.AddComponent<Button>();

            // Avatar
            var avatarPanel = UIFactory.Panel("AvatarPanel", cellObj.transform, Color.clear);

            var avatarRect = avatarPanel.GetComponent<RectTransform>();
            avatarRect.anchorMin = new Vector2(0.5f, 1);
            avatarRect.anchorMax = new Vector2(0.5f, 1);
            avatarRect.pivot = new Vector2(0.5f, 1);
            avatarRect.anchoredPosition = new Vector2(0, -8);
            avatarRect.sizeDelta = new Vector2(100, 100);

            var avatarLayout = avatarPanel.AddComponent<LayoutElement>();
            avatarLayout.preferredWidth = 100;
            avatarLayout.preferredHeight = 100;

            CreateCustomerSprite(avatarPanel.transform, customer.NPC);

            // Addiction bar (tier 2+)
            if (effectiveTier >= 2)
            {
                var addiction = Mathf.Clamp01(customer.CurrentAddiction);
                CreateAddictionBar(cellObj.transform, addiction);
            }

            // Name label
            var nameObj = UIFactory.Text("Name", customer.NPC.FirstName, cellObj.transform, 12, TextAnchor.UpperCenter);
            nameObj.color = Color.white;
            var nameRect = nameObj.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 0);
            nameRect.anchorMax = new Vector2(1, 0);
            nameRect.pivot = new Vector2(0.5f, 0);
            nameRect.anchoredPosition = new Vector2(0, 3);
            nameRect.sizeDelta = new Vector2(0, 20);

            // Desperation indicator
            if (isDesperate)
            {
                var urgentLabel = UIFactory.Text("UrgentLabel", "<b>URGENT!</b>", cellObj.transform, 10, TextAnchor.UpperCenter);
                var urgentRect = urgentLabel.gameObject.GetComponent<RectTransform>();
                urgentRect.anchorMin = new Vector2(0, 1);
                urgentRect.anchorMax = new Vector2(1, 1);
                urgentRect.pivot = new Vector2(0.5f, 1);
                urgentRect.anchoredPosition = new Vector2(0, -2);
                urgentRect.sizeDelta = new Vector2(0, 14);
                urgentLabel.color = new Color(1f, 0.3f, 0.3f);
            }

            // GPS button (tier 3)
            if (effectiveTier >= 3 && !isLocked)
            {
                var (gpsMask, gpsBtn, gpsLabel) = UIFactory.RoundedButtonWithLabel(
                    "GPSBtn", "GPS", cellObj.transform,
                    new Color(0.15f, 0.35f, 0.45f), 50, 20, 10, Color.white);

                var gpsRect = gpsMask.GetComponent<RectTransform>();
                gpsRect.anchorMin = new Vector2(1, 1);
                gpsRect.anchorMax = new Vector2(1, 1);
                gpsRect.pivot = new Vector2(1, 1);
                gpsRect.anchoredPosition = new Vector2(-4, -4);

                gpsBtn.onClick.AddListener(new Action(() =>
                {
                    CustomerLocator.PinCustomerToMap(customer);
                }));
            }

            // Interactivity
            if (isLocked)
            {
                var cg = cellObj.AddComponent<CanvasGroup>();
                cg.alpha = 0.5f;
                btn.interactable = false;
            }
            else if (effectiveTier >= 3)
            {
                btn.onClick.AddListener(new Action(() =>
                {
                    CustomerLocator.PinCustomerToMap(customer);
                }));
            }
        }

        private void CreateAddictionBar(Transform parent, float addictionLevel)
        {
            int percentage = Mathf.RoundToInt(addictionLevel * 100);

            var barObj = new GameObject("AddictionBar");
            barObj.transform.SetParent(parent, false);
            var barRect = barObj.AddComponent<RectTransform>();
            barRect.anchorMin = new Vector2(0.05f, 0);
            barRect.anchorMax = new Vector2(0.95f, 0);
            barRect.pivot = new Vector2(0.5f, 0);
            barRect.anchoredPosition = new Vector2(0, 28);
            barRect.sizeDelta = new Vector2(0, 16);

            var bgObj = new GameObject("Background");
            bgObj.transform.SetParent(barObj.transform, false);
            var bgRect = bgObj.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            var bgImage = bgObj.AddComponent<Image>();
            bgImage.color = new Color(0.5f, 0.2f, 0.2f, 1f);

            var fillObj = new GameObject("Fill");
            fillObj.transform.SetParent(barObj.transform, false);
            var fillRect = fillObj.AddComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(addictionLevel, 1);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
            var fillImage = fillObj.AddComponent<Image>();
            fillImage.color = new Color(0.2f, 0.7f, 0.2f, 1f);

            var labelObj = UIFactory.Text("Label", $"{percentage}%", barObj.transform, 11, TextAnchor.MiddleCenter);
            var labelRect = labelObj.gameObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            labelObj.color = Color.white;
        }

        private void CreateCustomerSprite(Transform parent, NPC customer)
        {
            var customerIcon = new GameObject(customer.ID + "_sprite");
            customerIcon.transform.SetParent(parent, false);

            var iconRT = customerIcon.AddComponent<RectTransform>();
            iconRT.anchorMin = Vector2.zero;
            iconRT.anchorMax = Vector2.one;
            iconRT.offsetMin = Vector2.zero;
            iconRT.offsetMax = Vector2.zero;
            var image = customerIcon.AddComponent<Image>();

            var iconSprite = customer.MugshotSprite;
            if (iconSprite != null)
            {
                image.sprite = iconSprite;
            }
            else
            {
                MelonLogger.Warning($"MugshotSprite is null for NPC: {customer.fullName}");
            }
        }

        private class CustomerDisplayData
        {
            public Customer Customer;
            public bool IsLocked;
        }
    }
}
