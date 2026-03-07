using MelonLoader;
using S1API.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using OverTheCounter.Logic;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using OverTheCounter.SaveData;

#if IL2CPP
using Il2Cpp;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Cartel;
using Il2CppScheduleOne.DevUtilities;
using Il2CppTMPro;
#else
using ScheduleOne.Economy;
using ScheduleOne.NPCs;
using ScheduleOne.Map;
using ScheduleOne.Cartel;
using ScheduleOne.DevUtilities;
using TMPro;
#endif

namespace OverTheCounter.Apps
{
    public partial class CustomersApp
    {
        private void BuildCustomersPage(Transform parent)
        {
            // ── Billing bar (full width, top) ──
            var billingBarObj = UIFactory.Panel("BillingBar", parent, new Color(0.15f, 0.15f, 0.15f));
            var billingBarRt = billingBarObj.GetComponent<RectTransform>();
            billingBarRt.anchorMin = new Vector2(0, 1);
            billingBarRt.anchorMax = new Vector2(1, 1);
            billingBarRt.pivot = new Vector2(0.5f, 1);
            billingBarRt.anchoredPosition = Vector2.zero;
            billingBarRt.sizeDelta = new Vector2(0, 32);
            _billingBar = billingBarObj;

            var billingTextComp = TMPFactory.Text("BillingText", "", billingBarObj.transform, 16, TextAlignmentOptions.Left);
            billingTextComp.richText = true;
            billingTextComp.color = new Color(0.85f, 0.85f, 0.85f);
            var btRect = billingTextComp.gameObject.GetComponent<RectTransform>();
            btRect.anchorMin = Vector2.zero;
            btRect.anchorMax = Vector2.one;
            btRect.offsetMin = new Vector2(12, 0);
            btRect.offsetMax = new Vector2(-12, 0);
            _billingText = billingTextComp;

            // ── Split area (fills remainder below legend+billing) ──
            var splitArea = UIFactory.Panel("SplitArea", parent, Color.clear);
            _custSplitAreaRect = splitArea.GetComponent<RectTransform>();
            _custSplitAreaRect.anchorMin = Vector2.zero;
            _custSplitAreaRect.anchorMax = Vector2.one;
            _custSplitAreaRect.offsetMin = Vector2.zero;
            _custSplitAreaRect.offsetMax = new Vector2(0, -32); // billing(32)

            // ── Left panel (grid) ──
            var leftPanel = UIFactory.Panel("GridPanel", splitArea.transform, Color.clear);
            var leftRect = leftPanel.GetComponent<RectTransform>();
            leftRect.anchorMin = Vector2.zero;
            leftRect.anchorMax = new Vector2(0.65f, 1);
            leftRect.offsetMin = Vector2.zero;
            leftRect.offsetMax = Vector2.zero;

            // ── Vertical divider ──
            var sep = UIFactory.Panel("Divider", splitArea.transform, new Color(0.22f, 0.22f, 0.22f));
            var sepRect = sep.GetComponent<RectTransform>();
            sepRect.anchorMin = new Vector2(0.65f, 0);
            sepRect.anchorMax = new Vector2(0.65f, 1);
            sepRect.pivot = new Vector2(0, 0.5f);
            sepRect.offsetMin = Vector2.zero;
            sepRect.offsetMax = new Vector2(1, 0);

            // ── Right panel (detail) ──
            _custDetailPanel = UIFactory.Panel("DetailPanel", splitArea.transform, new Color(0.13f, 0.13f, 0.13f));
            var rightRect = _custDetailPanel.GetComponent<RectTransform>();
            rightRect.anchorMin = new Vector2(0.65f, 0);
            rightRect.anchorMax = Vector2.one;
            rightRect.offsetMin = new Vector2(2, 0);
            rightRect.offsetMax = Vector2.zero;

            // ── Scroll view inside left panel ──
            var contentRect = UIFactory.ScrollableVerticalList("CustomerScroll", leftPanel.transform, out ScrollRect scrollRect);
            _customersScrollRect = scrollRect.GetComponent<RectTransform>();
            _customersScroll = scrollRect;
            _customersScrollRect.anchorMin = Vector2.zero;
            _customersScrollRect.anchorMax = Vector2.one;
            _customersScrollRect.offsetMin = Vector2.zero;
            _customersScrollRect.offsetMax = Vector2.zero;

            var contentLayout = contentRect.GetComponent<VerticalLayoutGroup>();
            if (contentLayout != null)
            {
                contentLayout.childControlHeight = true;
                contentLayout.childControlWidth = true;
                contentLayout.childForceExpandHeight = false;
                contentLayout.childForceExpandWidth = true;
                contentLayout.childAlignment = TextAnchor.UpperCenter;
                contentLayout.spacing = 10;
                contentLayout.padding = new RectOffset(8, 8, 5, 10);
            }

            scrollRect.horizontal = false;
            scrollRect.scrollSensitivity = 20f;
            _customersContentParent = contentRect.transform;

            // ── Detail panel placeholder ──
            ShowDetailPlaceholder(_custDetailPanel.transform);

            PopulateCustomerList(_customersContentParent);
        }

        private void RefreshCustomersPage()
        {
            if (_customersContentParent == null) return;

            int tier = GetEffectiveTier();

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

            if (_custSplitAreaRect != null)
                _custSplitAreaRect.offsetMax = new Vector2(0, showBilling ? -32f : 0f);

            // Rebuild grid
            ClearChildren(_customersContentParent);
            PopulateCustomerList(_customersContentParent);

            // Rebuild detail panel
            if (_custDetailPanel != null)
            {
                if (_selectedCustomer != null)
                    PopulateCustomerDetail(_selectedCustomer, tier);
                else
                    ShowDetailPlaceholder(_custDetailPanel.transform);
            }
        }

        // ==================================================================
        // Customer selection
        // ==================================================================

        private void SelectCustomer(Customer customer, GameObject cellObj)
        {
            // Remove highlight from previous selection
            if (_selectedCellObj != null)
                RemoveCellHighlight(_selectedCellObj);

            _selectedCustomer = customer;
            _selectedCellObj = cellObj;

            // Apply highlight to newly selected cell
            if (cellObj != null)
                ApplyCellHighlight(cellObj);

            // Rebuild the detail panel
            int tier = GetEffectiveTier();
            if (_custDetailPanel != null)
                PopulateCustomerDetail(customer, tier);
        }

        private void ApplyCellHighlight(GameObject cellObj)
        {
            var highlight = UIFactory.Panel("SelectHighlight", cellObj.transform, new Color(0.35f, 0.55f, 0.95f, 0.22f));
            var rt = highlight.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var img = highlight.GetComponent<Image>();
            if (img != null) img.raycastTarget = false;

            // Add a border outline
            var border = cellObj.GetComponent<Outline>() ?? cellObj.AddComponent<Outline>();
            border.effectColor = new Color(0.4f, 0.65f, 1f, 1f);
            border.effectDistance = new Vector2(2f, 2f);
        }

        private void RemoveCellHighlight(GameObject cellObj)
        {
            if (cellObj == null) return;
            var highlight = cellObj.transform.Find("SelectHighlight");
            if (highlight != null)
                UnityEngine.Object.Destroy(highlight.gameObject);

            // Only remove outline if not a desperate-cell outline (desperate uses red)
            var outline = cellObj.GetComponent<Outline>();
            if (outline != null && outline.effectColor.b > 0.5f) // blue = selection outline
                UnityEngine.Object.Destroy(outline);
        }

        private void ShowDetailPlaceholder(Transform parent)
        {
            if (parent == null) return;
            ClearChildren(parent);

            var msg = TMPFactory.Text("Placeholder", "Select a customer\nto view details", parent, 16, TextAlignmentOptions.Center);
            msg.color = new Color(0.4f, 0.4f, 0.4f);
            var msgRect = msg.gameObject.GetComponent<RectTransform>();
            msgRect.anchorMin = new Vector2(0, 0.4f);
            msgRect.anchorMax = new Vector2(1, 0.6f);
            msgRect.offsetMin = Vector2.zero;
            msgRect.offsetMax = Vector2.zero;
        }

        // ==================================================================
        // Customer list population
        // ==================================================================

        private void ShowPaywallScreen(Transform contentParent)
        {
            bool hasMetStatic = StaticSaveData.Instance != null && StaticSaveData.Instance.CrmTier > 0;

            if (hasMetStatic)
            {
                var titleObj = TMPFactory.Text("PaywallTitle", "<b>SERVICE OFFLINE</b>", contentParent, 22, TextAlignmentOptions.Center);
                titleObj.color = new Color(0.7f, 0.2f, 0.2f);
                var titleLayout = titleObj.gameObject.AddComponent<LayoutElement>();
                titleLayout.preferredHeight = 40;
                titleLayout.flexibleWidth = 1;

                var subtitleObj = TMPFactory.Text("PaywallSubtitle",
                    "Active subscription required.\nVisit Static at the Casino to renew.",
                    contentParent, 15, TextAlignmentOptions.Center);
                subtitleObj.color = new Color(0.6f, 0.6f, 0.6f);
                var subtitleLayout = subtitleObj.gameObject.AddComponent<LayoutElement>();
                subtitleLayout.preferredHeight = 50;
                subtitleLayout.flexibleWidth = 1;
            }
            else
            {
                var titleObj = TMPFactory.Text("LicenseTitle", "<b>LICENSE INVALID</b>", contentParent, 22, TextAlignmentOptions.Center);
                titleObj.color = new Color(0.6f, 0.6f, 0.6f);
                var titleLayout = titleObj.gameObject.AddComponent<LayoutElement>();
                titleLayout.preferredHeight = 40;
                titleLayout.flexibleWidth = 1;

                var subtitleObj = TMPFactory.Text("LicenseSubtitle",
                    "Please wait for an authorized\nrepresentative to contact you.",
                    contentParent, 15, TextAlignmentOptions.Center);
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
                TMPFactory.Text("Empty", "No Customers Known", contentParent, 20, TextAlignmentOptions.Center);
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
                // For Downtown+: only show progress if the previous region is already unlocked
                if (region >= EMapRegion.Downtown && !IsRegionUnlocked(effectiveTier, region - 1))
                    return;

                var lockedHeader = TMPFactory.Text($"Header_{regionName}",
                    $"<b>{regionName}</b>  <color=#555555>[LOCKED]</color>",
                    parent, 16, TextAlignmentOptions.Center);
                lockedHeader.color = new Color(0.45f, 0.45f, 0.45f);
                var lockedLayout = lockedHeader.gameObject.AddComponent<LayoutElement>();
                lockedLayout.preferredHeight = 28f;
                lockedLayout.flexibleWidth = 1;

                // Regions 3+ (Downtown onwards) unlock via cartel influence reduction
                if (region >= EMapRegion.Downtown)
                    CreateCartelInfluenceProgress(parent, region);
                else if (region == EMapRegion.Westville)
                    CreateWestvilleUnlockHint(parent);

                return;
            }

            string headerText = $"<b>{regionName}</b>";
            var headerObj = TMPFactory.Text($"Header_{regionName}", headerText, parent, 18, TextAlignmentOptions.Center);
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

            // Name label
            var nameObj = TMPFactory.Text("Name", customer.NPC.FirstName, cellObj.transform, 15, TextAlignmentOptions.Top);
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
                var urgentLabel = TMPFactory.Text("UrgentLabel", "<b>URGENT!</b>", cellObj.transform, 15, TextAlignmentOptions.Top);
                var urgentRect = urgentLabel.gameObject.GetComponent<RectTransform>();
                urgentRect.anchorMin = new Vector2(0, 1);
                urgentRect.anchorMax = new Vector2(1, 1);
                urgentRect.pivot = new Vector2(0.5f, 1);
                urgentRect.anchoredPosition = new Vector2(0, -2);
                urgentRect.sizeDelta = new Vector2(0, 14);
                urgentLabel.color = new Color(1f, 0.3f, 0.3f);
            }

            // Apply selection highlight if this is the currently selected customer
            if (!isLocked && customer == _selectedCustomer)
            {
                ApplyCellHighlight(cellObj);
                _selectedCellObj = cellObj;
            }

            // Interactivity
            if (isLocked)
            {
                var cg = cellObj.AddComponent<CanvasGroup>();
                cg.alpha = 0.5f;
                btn.interactable = false;
            }
            else
            {
                btn.onClick.AddListener(new Action(() =>
                {
                    SelectCustomer(customer, cellObj);
                }));
            }
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

        // Cartel influence progress bar shown beneath locked region headers (Downtown+)
        private void CreateCartelInfluenceProgress(Transform parent, EMapRegion lockedRegion)
        {
            EMapRegion prevRegion = lockedRegion - 1;

            // If cartel is truced, regions are unavailable regardless of influence
            bool truced = false;
            try
            {
                if (NetworkSingleton<Cartel>.InstanceExists)
                    truced = NetworkSingleton<Cartel>.Instance.Status == ECartelStatus.Truced;
            }
            catch { }

            if (truced)
            {
                var unavailText = TMPFactory.Text("UnavailHint",
                    "Unavailable while cartel is truced",
                    parent, 15, TextAlignmentOptions.Center);
                unavailText.color = new Color(0.45f, 0.45f, 0.45f);
                var unavailLe = unavailText.gameObject.AddComponent<LayoutElement>();
                unavailLe.preferredHeight = 20f;
                unavailLe.flexibleWidth = 1;
                return;
            }

            float influence = 1f;
            try
            {
                if (NetworkSingleton<Cartel>.InstanceExists)
                    influence = NetworkSingleton<Cartel>.Instance.Influence.GetInfluence(prevRegion);
            }
            catch { }

            // Progress: 0 when influence is at max (1.0), 1 when at/below threshold (0.3)
            float progress = Mathf.InverseLerp(1f, 0.3f, influence);
            int influencePts = Mathf.RoundToInt(influence * 1000f);

            var hintText = TMPFactory.Text("InfluenceHint",
                $"Reduce {prevRegion} cartel influence to unlock",
                parent, 15, TextAlignmentOptions.Center);
            hintText.color = new Color(0.45f, 0.45f, 0.45f);
            var hintLe = hintText.gameObject.AddComponent<LayoutElement>();
            hintLe.preferredHeight = 20f;
            hintLe.flexibleWidth = 1;

            var barContainer = UIFactory.Panel("InfluenceBar", parent, Color.clear);
            var barLe = barContainer.AddComponent<LayoutElement>();
            barLe.preferredHeight = 18f;
            barLe.flexibleWidth = 1;

            var barBg = UIFactory.Panel("Bg", barContainer.transform, new Color(0.15f, 0.15f, 0.15f));
            var bgRt = barBg.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero;
            bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero;
            bgRt.offsetMax = Vector2.zero;

            var barFill = UIFactory.Panel("Fill", barContainer.transform, new Color(0.65f, 0.28f, 0.28f));
            var fillRt = barFill.GetComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = new Vector2(progress, 1f);
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;

            var barLabel = TMPFactory.Text("BarLabel", $"{influencePts} / 1000  (need ≤300)", barContainer.transform, 15, TextAlignmentOptions.Center);
            var barLabelRt = barLabel.gameObject.GetComponent<RectTransform>();
            barLabelRt.anchorMin = Vector2.zero;
            barLabelRt.anchorMax = Vector2.one;
            barLabelRt.offsetMin = Vector2.zero;
            barLabelRt.offsetMax = Vector2.zero;
            barLabel.color = Color.white;
            var shadow = barLabel.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.7f);
            shadow.effectDistance = new Vector2(1, -1);

            var spacer = UIFactory.Panel("Spacer", parent, Color.clear);
            var spacerLe = spacer.AddComponent<LayoutElement>();
            spacerLe.preferredHeight = 8f;
            spacerLe.flexibleWidth = 1;
            var spacerImg = spacer.GetComponent<Image>();
            if (spacerImg != null) spacerImg.raycastTarget = false;
        }

        private void CreateWestvilleUnlockHint(Transform parent)
        {
            var hintText = TMPFactory.Text("WestvilleHint",
                "Reach rank Hoodlum I to unlock",
                parent, 15, TextAlignmentOptions.Center);
            hintText.color = new Color(0.45f, 0.45f, 0.45f);
            var hintLe = hintText.gameObject.AddComponent<LayoutElement>();
            hintLe.preferredHeight = 20f;
            hintLe.flexibleWidth = 1;

            var spacer = UIFactory.Panel("Spacer", parent, Color.clear);
            var spacerLe = spacer.AddComponent<LayoutElement>();
            spacerLe.preferredHeight = 8f;
            spacerLe.flexibleWidth = 1;
            var spacerImg = spacer.GetComponent<Image>();
            if (spacerImg != null) spacerImg.raycastTarget = false;
        }

        private class CustomerDisplayData
        {
            public Customer Customer;
            public bool IsLocked;
        }
    }
}
