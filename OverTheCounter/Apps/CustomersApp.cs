using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Map;
using MelonLoader;
using S1API.PhoneApp;
using S1API.UI;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using OverTheCounter.Utilities;
using OverTheCounter.Logic;
using OverTheCounter.SaveData;
using MelonLoader.Utils;
using System.IO;
using HarmonyLib;

namespace OverTheCounter.Apps
{
    public class CustomersApp : PhoneApp
    {
        protected override string AppName => "OverTheCounterApp";
        protected override string AppTitle => "OverTheCounter";
        protected override string IconLabel => "OTC";
        // Using Horizontal - vertical orientation has layout issues with S1API
        protected override EOrientation Orientation => EOrientation.Horizontal;

        protected override string IconFileName => Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "CustomersIcon.png");

        // Layout Constants
        private const float CELL_WIDTH = 130f;
        private const float CELL_HEIGHT = 165f;
        private const int COLUMNS = 4;

        // Store reference for refresh
        private Transform _contentParent;

        // Tier-gated UI references
        private GameObject _legendObj;
        private Text _headerTitle;

        // Billing bar (fixed, outside scroll view)
        private GameObject _billingBar;
        private Text _billingText;
        private RectTransform _scrollRectTransform;

        // Icon visibility: hide home-screen icon until the player has purchased from Static
        private GameObject _iconObject;
        private bool _iconSearchDone;
        private int _iconSearchFrames;

        // Static instance for access from other classes
        public static CustomersApp Instance { get; private set; }

        protected override void OnCreated()
        {
            base.OnCreated();
            Instance = this;
        }

        /// <summary>
        /// Called every frame from Core.OnLateUpdate().
        /// Finds the home-screen icon (once) and shows/hides it based on CrmTier.
        /// </summary>
        public void UpdateIconVisibility()
        {
            if (!_iconSearchDone)
            {
                // Throttle: search every 30 frames, give up after ~10 seconds
                if (++_iconSearchFrames % 30 != 0) return;
                if (_iconSearchFrames > 600)
                {
                    _iconSearchDone = true;
                    MelonLogger.Warning("[CustomersApp] Could not find phone icon object after 10s.");
                    return;
                }

                _iconObject = FindIconObject();
                if (_iconObject != null)
                    _iconSearchDone = true;
                else
                    return;
            }

            if (_iconObject == null) return;

            bool shouldShow = StaticSaveData.Instance != null && StaticSaveData.Instance.CrmTier > 0;
            if (_iconObject.activeSelf != shouldShow)
                _iconObject.SetActive(shouldShow);
        }

        /// <summary>
        /// Attempts to locate the home-screen icon button for this app.
        /// Strategy 1: reflection on PhoneApp fields for a GameObject/Transform.
        /// Strategy 2: scene search for a Text component matching our IconLabel.
        /// </summary>
        private GameObject FindIconObject()
        {
            // Strategy 1: Reflection – look for icon/button fields on PhoneApp
            try
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
                foreach (var field in typeof(PhoneApp).GetFields(flags))
                {
                    var value = field.GetValue(this);
                    if (value is GameObject go)
                    {
                        string fn = field.Name.ToLowerInvariant();
                        if (fn.Contains("icon") || fn.Contains("button") || fn.Contains("appbtn"))
                            return go;
                    }
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[CustomersApp] Reflection search failed: {ex.Message}");
            }

            // Strategy 2: Find our icon label "OTC" among all Text components
            try
            {
                foreach (var text in UnityEngine.Object.FindObjectsOfType<Text>(true))
                {
                    if (text.text != IconLabel) continue;

                    // Walk up to the nearest ancestor that has a Button – that's the icon button
                    var current = text.transform.parent;
                    while (current != null)
                    {
                        if (current.GetComponent<Button>() != null)
                            return current.gameObject;
                        current = current.parent;
                    }

                    // No Button ancestor found – return the label's immediate parent
                    if (text.transform.parent != null)
                        return text.transform.parent.gameObject;
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[CustomersApp] Label search failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Clears all children from a transform
        /// </summary>
        private void ClearChildren(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
            }
        }

        /// <summary>
        /// Returns 0 if StaticSaveData is null or SaaS inactive; otherwise returns CrmTier (1-3).
        /// </summary>
        private int GetEffectiveTier()
        {
            if (StaticSaveData.Instance == null || !StaticSaveData.Instance.SaasActive)
                return 0;
            return StaticSaveData.Instance.CrmTier;
        }

        /// <summary>
        /// Returns whether the given region is unlocked for the effective tier.
        /// Tier 0: always false. Tier 1: Northtown and Westville only. Tier 2+: all regions.
        /// </summary>
        private bool IsRegionUnlocked(int effectiveTier, EMapRegion region)
        {
            if (effectiveTier <= 0) return false;
            if (effectiveTier == 1)
                return region == EMapRegion.Northtown || region == EMapRegion.Westville;
            return true;
        }

        /// <summary>
        /// Renders a paywall screen when no active subscription exists.
        /// </summary>
        private void ShowPaywallScreen(Transform contentParent)
        {
            var titleObj = UIFactory.Text("PaywallTitle", "<b>SERVICE OFFLINE</b>", contentParent, 22, TextAnchor.MiddleCenter);
            titleObj.color = new Color(0.7f, 0.2f, 0.2f);
            var titleLayout = titleObj.gameObject.AddComponent<LayoutElement>();
            titleLayout.preferredHeight = 40;
            titleLayout.flexibleWidth = 1;

            var subtitleObj = UIFactory.Text("PaywallSubtitle",
                "Active subscription required.\nVisit Static at the Casino to activate.",
                contentParent, 14, TextAnchor.MiddleCenter);
            subtitleObj.color = new Color(0.6f, 0.6f, 0.6f);
            var subtitleLayout = subtitleObj.gameObject.AddComponent<LayoutElement>();
            subtitleLayout.preferredHeight = 50;
            subtitleLayout.flexibleWidth = 1;
        }

        /// <summary>
        /// Refreshes the customer list - called via Harmony patch when app opens
        /// </summary>
        private void RefreshCustomerList()
        {
            if (_contentParent == null) return;

            int tier = GetEffectiveTier();

            // Update header title with tier suffix
            if (_headerTitle != null)
            {
                switch (tier)
                {
                    case 1:
                        _headerTitle.text = "<b>OverTheCounter</b> <color=#999999><size=16>Lite</size></color>";
                        break;
                    case 2:
                        _headerTitle.text = "<b>OverTheCounter</b> <color=#2ABFBF><size=16>Pro</size></color>";
                        break;
                    case 3:
                        _headerTitle.text = "<b>OverTheCounter</b> <color=#FFD700><size=16>Enterprise</size></color>";
                        break;
                    default:
                        _headerTitle.text = "<b>OverTheCounter</b>";
                        break;
                }
            }

            // Show/hide legend based on tier (visible at tier 2+)
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
                    _billingText.text = "<b>Subscription renews at 5a.m</b>  <color=#4CAF50><b>($1,000)</b></color>";
                else
                    _billingText.text = $"<b>Subscription renews in {displayDays} days</b>  <color=#4CAF50><b>($1,000)</b></color>";
            }

            // Dynamically position billing bar and scroll view based on visible bars
            float yOffset = -40f; // header always 40px
            if (showLegend) yOffset -= 25f;

            if (showBilling && _billingBar != null)
            {
                var billingRt = _billingBar.GetComponent<RectTransform>();
                billingRt.anchoredPosition = new Vector2(0, yOffset);
                yOffset -= 32f;
            }

            if (_scrollRectTransform != null)
                _scrollRectTransform.offsetMax = new Vector2(0, yOffset);

            ClearChildren(_contentParent);
            PopulateCustomerList(_contentParent);
        }

        protected override void OnCreatedUI(GameObject container)
        {
            // 1. Main Background
            var rootPanel = UIFactory.Panel("MainPanel", container.transform, new Color(0.12f, 0.12f, 0.12f), fullAnchor: true);

            // 2. Header Bar (unique teal color for Customers app)
            var headerObj = UIFactory.Panel("Header", rootPanel.transform, new Color(0.15f, 0.35f, 0.45f)); // Teal/blue
            var headerRect = headerObj.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = new Vector2(1, 1);
            headerRect.pivot = new Vector2(0.5f, 1);
            headerRect.anchoredPosition = Vector2.zero;
            headerRect.sizeDelta = new Vector2(0, 40);

            // Header Title
            var titleObj = UIFactory.Text("Title", "<b>OverTheCounter</b>", headerObj.transform, 24, TextAnchor.MiddleLeft);
            var titleRect = titleObj.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = Vector2.one;
            titleRect.offsetMin = new Vector2(15, 0);
            titleRect.offsetMax = new Vector2(-120, 0);
            _headerTitle = titleObj;

            // DEBUG: Trigger Desperation Button (right side of header) using UIFactory
            var (debugMask, debugBtn, debugLabel) = UIFactory.RoundedButtonWithLabel(
                "DebugBtn",
                "DEBUG",
                headerObj.transform,
                new Color(0.6f, 0.2f, 0.2f),
                100, // width
                32,  // height
                12,  // fontSize
                Color.white
            );

            // Position on right side of header
            var debugBtnRect = debugMask.GetComponent<RectTransform>();
            debugBtnRect.anchorMin = new Vector2(1, 0.5f);
            debugBtnRect.anchorMax = new Vector2(1, 0.5f);
            debugBtnRect.pivot = new Vector2(1, 0.5f);
            debugBtnRect.anchoredPosition = new Vector2(-10, 0);

            debugBtn.onClick.AddListener(new System.Action(() =>
            {
                MelonLogger.Msg("[CustomersApp] DEBUG button clicked - triggering random desperation");
                MelonLogger.Msg(DesperationManager.DebugGetStatus());
                bool success = DesperationManager.DebugForceRandomTrigger();
                if (success)
                {
                    RefreshCustomerList();
                }
            }));

            // 3. Legend/Key bar below header
            var legendObj = UIFactory.Panel("Legend", rootPanel.transform, new Color(0.18f, 0.18f, 0.18f));
            var legendRect = legendObj.GetComponent<RectTransform>();
            legendRect.anchorMin = new Vector2(0, 1);
            legendRect.anchorMax = new Vector2(1, 1);
            legendRect.pivot = new Vector2(0.5f, 1);
            legendRect.anchoredPosition = new Vector2(0, -40);
            legendRect.sizeDelta = new Vector2(0, 25);
            _legendObj = legendObj;

            // Legend content - "Addiction:" label + color key
            var legendText = UIFactory.Text("LegendText", "Addiction:", legendObj.transform, 12, TextAnchor.MiddleLeft);
            var legendTextRect = legendText.gameObject.GetComponent<RectTransform>();
            legendTextRect.anchorMin = new Vector2(0, 0);
            legendTextRect.anchorMax = new Vector2(0, 1);
            legendTextRect.pivot = new Vector2(0, 0.5f);
            legendTextRect.anchoredPosition = new Vector2(15, 0);
            legendTextRect.sizeDelta = new Vector2(70, 0);
            legendText.color = new Color(0.7f, 0.7f, 0.7f);

            // Green sample bar
            var greenSample = UIFactory.Panel("GreenSample", legendObj.transform, new Color(0.2f, 0.7f, 0.2f));
            var greenRect = greenSample.GetComponent<RectTransform>();
            greenRect.anchorMin = new Vector2(0, 0.3f);
            greenRect.anchorMax = new Vector2(0, 0.7f);
            greenRect.pivot = new Vector2(0, 0.5f);
            greenRect.anchoredPosition = new Vector2(90, 0);
            greenRect.sizeDelta = new Vector2(30, 0);

            // "High" label
            var highLabel = UIFactory.Text("HighLabel", "High", legendObj.transform, 10, TextAnchor.MiddleLeft);
            var highRect = highLabel.gameObject.GetComponent<RectTransform>();
            highRect.anchorMin = new Vector2(0, 0);
            highRect.anchorMax = new Vector2(0, 1);
            highRect.pivot = new Vector2(0, 0.5f);
            highRect.anchoredPosition = new Vector2(125, 0);
            highRect.sizeDelta = new Vector2(35, 0);
            highLabel.color = new Color(0.6f, 0.6f, 0.6f);

            // Red sample bar
            var redSample = UIFactory.Panel("RedSample", legendObj.transform, new Color(0.5f, 0.2f, 0.2f));
            var redRect = redSample.GetComponent<RectTransform>();
            redRect.anchorMin = new Vector2(0, 0.3f);
            redRect.anchorMax = new Vector2(0, 0.7f);
            redRect.pivot = new Vector2(0, 0.5f);
            redRect.anchoredPosition = new Vector2(165, 0);
            redRect.sizeDelta = new Vector2(30, 0);

            // "Low" label
            var lowLabel = UIFactory.Text("LowLabel", "Low", legendObj.transform, 10, TextAnchor.MiddleLeft);
            var lowRect = lowLabel.gameObject.GetComponent<RectTransform>();
            lowRect.anchorMin = new Vector2(0, 0);
            lowRect.anchorMax = new Vector2(0, 1);
            lowRect.pivot = new Vector2(0, 0.5f);
            lowRect.anchoredPosition = new Vector2(200, 0);
            lowRect.sizeDelta = new Vector2(35, 0);
            lowLabel.color = new Color(0.6f, 0.6f, 0.6f);

            // 4. Billing bar (fixed, between legend and scroll area)
            var billingBarObj = UIFactory.Panel("BillingBar", rootPanel.transform, new Color(0.15f, 0.15f, 0.15f));
            var billingBarRt = billingBarObj.GetComponent<RectTransform>();
            billingBarRt.anchorMin = new Vector2(0, 1);
            billingBarRt.anchorMax = new Vector2(1, 1);
            billingBarRt.pivot = new Vector2(0.5f, 1);
            billingBarRt.anchoredPosition = new Vector2(0, -65); // below legend
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

            // 5. Scroll View Setup using UIFactory (below header, legend, and billing bar)
            var contentRect = UIFactory.ScrollableVerticalList("CustomerScroll", rootPanel.transform, out ScrollRect scrollRect);

            // Position scroll view to fill remaining space (adjusted dynamically in RefreshCustomerList)
            _scrollRectTransform = scrollRect.GetComponent<RectTransform>();
            _scrollRectTransform.anchorMin = Vector2.zero;
            _scrollRectTransform.anchorMax = Vector2.one;
            _scrollRectTransform.offsetMin = Vector2.zero;
            _scrollRectTransform.offsetMax = new Vector2(0, -97); // header(40) + legend(25) + billing(32)

            // Configure the existing VerticalLayoutGroup created by ScrollableVerticalList
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

            scrollRect.scrollSensitivity = 20f;

            // Store reference and Populate Data
            _contentParent = contentRect.transform;
            PopulateCustomerList(_contentParent);
        }

        private void PopulateCustomerList(Transform contentParent)
        {
            int effectiveTier = GetEffectiveTier();

            // Paywall: no active subscription
            if (effectiveTier == 0)
            {
                ShowPaywallScreen(contentParent);
                return;
            }

            // Fetch Lists
            var unlocked = Customer.UnlockedCustomers;
            var locked = Customer.LockedCustomers;

            if ((unlocked == null || unlocked.Count == 0) && (locked == null || locked.Count == 0))
            {
                UIFactory.Text("Empty", "No Customers Known", contentParent, 20, TextAnchor.MiddleCenter);
                return;
            }

            // 2. Merge into a display list to handle sorting/grouping easily
            var displayList = new List<CustomerDisplayData>();

            if (unlocked != null)
            {
                foreach (var c in unlocked) displayList.Add(new CustomerDisplayData { Customer = c, IsLocked = false });
            }
            if (locked != null)
            {
                foreach (var c in locked) displayList.Add(new CustomerDisplayData { Customer = c, IsLocked = true });
            }

            // Group by Region
            var grouped = displayList
                .Where(d => d.Customer != null && d.Customer.NPC != null)
                .GroupBy(d => d.Customer.NPC.Region)
                .OrderBy(g => g.Key);

            // Create sections for each region
            foreach (var group in grouped)
            {
                CreateNeighborhoodSection(contentParent, group.Key.ToString(), group.ToList(), effectiveTier, group.Key);
            }
        }

        private void CreateNeighborhoodSection(Transform parent, string regionName, List<CustomerDisplayData> dataList, int effectiveTier, EMapRegion region)
        {
            bool regionUnlocked = IsRegionUnlocked(effectiveTier, region);

            // Locked regions: show only the header, no customer cells
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

            // Make header stretch full width
            var headerLayout = headerObj.gameObject.AddComponent<LayoutElement>();
            headerLayout.flexibleWidth = 1;

            // Grid
            var gridObj = UIFactory.Panel($"Grid_{regionName}", parent, Color.clear);

            // Make grid stretch full width
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

            // Fitter for grid height
            var gridFitter = gridObj.AddComponent<ContentSizeFitter>();
            gridFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Items
            foreach (var item in dataList)
            {
                CreateCustomerCell(gridObj.transform, item.Customer, item.IsLocked, effectiveTier);
            }
        }

        private void CreateCustomerCell(Transform gridParent, Customer customer, bool isLocked, int effectiveTier)
        {
            // Check if customer is in desperation state
            bool isDesperate = !isLocked && DesperationManager.IsDesperate(customer.NPC.ID);

            // 1. Cell Container - Red tint for desperate customers
            Color cellColor = isDesperate ? new Color(0.4f, 0.15f, 0.15f) : new Color(0.2f, 0.2f, 0.2f);
            var cellObj = UIFactory.Panel($"Cell_{customer.NPC.fullName}", gridParent, cellColor);

            // Add red border/outline for desperate customers
            if (isDesperate)
            {
                var outline = cellObj.AddComponent<Outline>();
                outline.effectColor = new Color(0.9f, 0.2f, 0.2f, 1f);
                outline.effectDistance = new Vector2(2f, 2f);
            }

            // Add Button component
            var btn = cellObj.AddComponent<Button>();

            // 2. Avatar
            var avatarPanel = UIFactory.Panel("AvatarPanel", cellObj.transform, Color.clear);

            // Positioning the avatar panel - sized relative to cell
            var avatarRect = avatarPanel.GetComponent<RectTransform>();
            avatarRect.anchorMin = new Vector2(0.5f, 1);
            avatarRect.anchorMax = new Vector2(0.5f, 1);
            avatarRect.pivot = new Vector2(0.5f, 1);
            avatarRect.anchoredPosition = new Vector2(0, -8);
            avatarRect.sizeDelta = new Vector2(100, 100);

            var avatarLayout = avatarPanel.AddComponent<LayoutElement>();
            avatarLayout.preferredWidth = 100;
            avatarLayout.preferredHeight = 100;

            // Call the sprite creator
            CreateCustomerSprite(avatarPanel.transform, customer.NPC);

            // 3. Addiction Progress Bar - only visible at tier 2+
            if (effectiveTier >= 2)
            {
                var addiction = Mathf.Clamp01(customer.CurrentAddiction);
                CreateAddictionBar(cellObj.transform, addiction);
            }

            // 4. Name Label
            var nameObj = UIFactory.Text("Name", customer.NPC.FirstName, cellObj.transform, 12, TextAnchor.UpperCenter);
            nameObj.color = Color.white;
            var nameRect = nameObj.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 0);
            nameRect.anchorMax = new Vector2(1, 0);
            nameRect.pivot = new Vector2(0.5f, 0);
            nameRect.anchoredPosition = new Vector2(0, 3);
            nameRect.sizeDelta = new Vector2(0, 20);

            // 5. Desperation Indicator
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

            // 6. GPS Button - Tier 3 only
            if (effectiveTier >= 3 && !isLocked)
            {
                var (gpsMask, gpsBtn, gpsLabel) = UIFactory.RoundedButtonWithLabel(
                    "GPSBtn",
                    "GPS",
                    cellObj.transform,
                    new Color(0.15f, 0.35f, 0.45f), // Teal
                    50,  // width
                    20,  // height
                    10,  // fontSize
                    Color.white
                );

                // Position top-right of cell
                var gpsRect = gpsMask.GetComponent<RectTransform>();
                gpsRect.anchorMin = new Vector2(1, 1);
                gpsRect.anchorMax = new Vector2(1, 1);
                gpsRect.pivot = new Vector2(1, 1);
                gpsRect.anchoredPosition = new Vector2(-4, -4);

                gpsBtn.onClick.AddListener(new System.Action(() =>
                {
                    CustomerLocator.PinCustomerToMap(customer);
                }));
            }

            // 7. Cell interactivity based on tier
            if (isLocked)
            {
                // Game-locked customer: dim and disable
                var cg = cellObj.AddComponent<CanvasGroup>();
                cg.alpha = 0.5f;
                btn.interactable = false;
            }
            else if (effectiveTier >= 3)
            {
                // Tier 3: cell click pins to map
                btn.onClick.AddListener(new System.Action(() =>
                {
                    CustomerLocator.PinCustomerToMap(customer);
                }));
            }
            // Tier 1-2 unlocked: no click handler (cell looks normal but does nothing)
        }

        /// <summary>
        /// Creates an addiction progress bar with percentage label overlaid on the bar
        /// </summary>
        private void CreateAddictionBar(Transform parent, float addictionLevel)
        {
            int percentage = Mathf.RoundToInt(addictionLevel * 100);

            // Progress bar positioned at bottom of cell, above the name
            var barObj = new GameObject("AddictionBar");
            barObj.transform.SetParent(parent, false);
            var barRect = barObj.AddComponent<RectTransform>();
            barRect.anchorMin = new Vector2(0.05f, 0);
            barRect.anchorMax = new Vector2(0.95f, 0);
            barRect.pivot = new Vector2(0.5f, 0);
            barRect.anchoredPosition = new Vector2(0, 28);
            barRect.sizeDelta = new Vector2(0, 16);

            // Background (red - unfilled portion)
            var bgObj = new GameObject("Background");
            bgObj.transform.SetParent(barObj.transform, false);
            var bgRect = bgObj.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            var bgImage = bgObj.AddComponent<Image>();
            bgImage.color = new Color(0.5f, 0.2f, 0.2f, 1f); // Dark red

            // Fill (green - addiction level)
            var fillObj = new GameObject("Fill");
            fillObj.transform.SetParent(barObj.transform, false);
            var fillRect = fillObj.AddComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(addictionLevel, 1);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
            var fillImage = fillObj.AddComponent<Image>();
            fillImage.color = new Color(0.2f, 0.7f, 0.2f, 1f); // Green

            // Label showing percentage - centered ON the bar
            var labelObj = UIFactory.Text("Label", $"{percentage}%", barObj.transform, 11, TextAnchor.MiddleCenter);
            var labelRect = labelObj.gameObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            labelObj.color = Color.white;
        }

        /// <summary>
        /// Creates the customer mugshot sprite inside the avatar panel
        /// </summary>
        private void CreateCustomerSprite(Transform parent, NPC customer)
        {
            var customerIcon = new GameObject(customer.ID + "_sprite");
            customerIcon.transform.SetParent(parent, false);

            var iconRT = customerIcon.AddComponent<RectTransform>();
            iconRT.anchorMin = new Vector2(0, 0);
            iconRT.anchorMax = new Vector2(1, 1);
            iconRT.offsetMin = new Vector2(0, 0);
            iconRT.offsetMax = new Vector2(0, 0);
            var image = customerIcon.AddComponent<Image>();

            var iconSprite = customer.MugshotSprite;
            if (iconSprite != null)
            {
                image.sprite = iconSprite;
            }
            else
            {
                MelonLoader.MelonLogger.Warning($"MugshotSprite is null for NPC: {customer.fullName}");
            }
        }

        private void SetupRectTransform(RectTransform rt, Transform parent)
        {
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // Helper class to wrap the data
        private class CustomerDisplayData
        {
            public Customer Customer;
            public bool IsLocked;
        }
    }

    /// <summary>
    /// Harmony patch to refresh customer list when app is opened
    /// </summary>
    [HarmonyPatch(typeof(PhoneApp), "OpenApp")]
    public static class CustomersAppOpenPatch
    {
        public static void Prefix(PhoneApp __instance)
        {
            if (__instance is CustomersApp)
            {
                MethodInfo refreshMethod = typeof(CustomersApp).GetMethod(
                    "RefreshCustomerList",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (refreshMethod != null)
                {
                    refreshMethod.Invoke(__instance, null);
                }
                else
                {
                    MelonLogger.Error("[CustomersApp] Could not find RefreshCustomerList method.");
                }
            }
        }
    }
}
