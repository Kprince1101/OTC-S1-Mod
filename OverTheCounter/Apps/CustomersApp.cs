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
using MelonLoader.Utils;
using System.IO;
using HarmonyLib;

namespace OverTheCounter.Apps
{
    public class CustomersApp : PhoneApp
    {
        protected override string AppName => "CustomersApp";
        protected override string AppTitle => "Customers";
        protected override string IconLabel => "Customers";
        // Using Horizontal - vertical orientation has layout issues with S1API
        protected override EOrientation Orientation => EOrientation.Horizontal;

        protected override string IconFileName => Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "CustomersIcon.png");

        // Layout Constants
        private const float CELL_WIDTH = 130f;
        private const float CELL_HEIGHT = 165f;
        private const int COLUMNS = 4;

        // Store reference for refresh
        private Transform _contentParent;

        // Static instance for access from other classes
        public static CustomersApp Instance { get; private set; }

        protected override void OnCreated()
        {
            base.OnCreated();
            Instance = this;
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
        /// Refreshes the customer list - called via Harmony patch when app opens
        /// </summary>
        private void RefreshCustomerList()
        {
            if (_contentParent == null) return;
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
            var titleObj = UIFactory.Text("Title", "<b>Customers</b>", headerObj.transform, 24, TextAnchor.MiddleLeft);
            var titleRect = titleObj.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = Vector2.one;
            titleRect.offsetMin = new Vector2(15, 0);
            titleRect.offsetMax = new Vector2(-15, 0);

            // 3. Legend/Key bar below header
            var legendObj = UIFactory.Panel("Legend", rootPanel.transform, new Color(0.18f, 0.18f, 0.18f));
            var legendRect = legendObj.GetComponent<RectTransform>();
            legendRect.anchorMin = new Vector2(0, 1);
            legendRect.anchorMax = new Vector2(1, 1);
            legendRect.pivot = new Vector2(0.5f, 1);
            legendRect.anchoredPosition = new Vector2(0, -40);
            legendRect.sizeDelta = new Vector2(0, 25);

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

            // 4. Scroll View Setup (below header and legend)
            var scrollObj = new GameObject("ScrollView");
            var scrollRect = scrollObj.AddComponent<ScrollRect>();
            var scrollRectTransform = scrollObj.GetComponent<RectTransform>();
            scrollRectTransform.SetParent(rootPanel.transform, false);
            scrollRectTransform.anchorMin = Vector2.zero;
            scrollRectTransform.anchorMax = Vector2.one;
            scrollRectTransform.offsetMin = Vector2.zero;
            scrollRectTransform.offsetMax = new Vector2(0, -65); // Leave space for header (40) + legend (25)

            // Viewport
            var viewportObj = new GameObject("Viewport");
            viewportObj.transform.SetParent(scrollObj.transform, false);
            var viewportRect = viewportObj.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMax = new Vector2(-15, 0); // Leave space for scrollbar
            viewportRect.offsetMin = new Vector2(10, 0);
            viewportObj.AddComponent<RectMask2D>();
            // Add invisible image to catch mouse events for scrolling
            var viewportImage = viewportObj.AddComponent<Image>();
            viewportImage.color = Color.clear;
            viewportImage.raycastTarget = true;
            scrollRect.viewport = viewportRect;

            // Vertical Scrollbar
            var scrollbarObj = new GameObject("Scrollbar");
            scrollbarObj.transform.SetParent(scrollObj.transform, false);
            var scrollbarRect = scrollbarObj.AddComponent<RectTransform>();
            scrollbarRect.anchorMin = new Vector2(1, 0);
            scrollbarRect.anchorMax = new Vector2(1, 1);
            scrollbarRect.pivot = new Vector2(1, 0.5f);
            scrollbarRect.sizeDelta = new Vector2(12, 0);
            scrollbarRect.anchoredPosition = Vector2.zero;

            var scrollbarImage = scrollbarObj.AddComponent<Image>();
            scrollbarImage.color = new Color(0.2f, 0.2f, 0.2f, 0.5f);

            var scrollbar = scrollbarObj.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            // Scrollbar handle
            var handleArea = new GameObject("Handle Slide Area");
            handleArea.transform.SetParent(scrollbarObj.transform, false);
            var handleAreaRect = handleArea.AddComponent<RectTransform>();
            handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one;
            handleAreaRect.offsetMin = Vector2.zero;
            handleAreaRect.offsetMax = Vector2.zero;

            var handleObj = new GameObject("Handle");
            handleObj.transform.SetParent(handleArea.transform, false);
            var handleRect = handleObj.AddComponent<RectTransform>();
            handleRect.anchorMin = Vector2.zero;
            handleRect.anchorMax = Vector2.one;
            handleRect.offsetMin = Vector2.zero;
            handleRect.offsetMax = Vector2.zero;

            var handleImage = handleObj.AddComponent<Image>();
            handleImage.color = new Color(0.5f, 0.5f, 0.5f, 0.8f);

            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handleImage;
            scrollRect.verticalScrollbar = scrollbar;
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;

            // Content
            var contentObj = UIFactory.Panel("Content", viewportObj.transform, Color.clear);
            var contentRect = contentObj.GetComponent<RectTransform>();

            // Set up content to stretch horizontally and grow vertically from top
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;

            var contentLayout = contentObj.AddComponent<VerticalLayoutGroup>();
            contentLayout.childControlHeight = true;
            contentLayout.childControlWidth = true;
            contentLayout.childForceExpandHeight = false;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childAlignment = TextAnchor.UpperCenter;
            contentLayout.spacing = 15;
            contentLayout.padding = new RectOffset(20, 20, 15, 15);

            var contentFitter = contentObj.AddComponent<ContentSizeFitter>();
            contentFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.content = contentRect;
            scrollRect.vertical = true;
            scrollRect.horizontal = false;
            scrollRect.scrollSensitivity = 20f;

            // Store reference and Populate Data
            _contentParent = contentObj.transform;
            PopulateCustomerList(_contentParent);
        }

        private void PopulateCustomerList(Transform contentParent)
        {
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
                string headerTitle = group.Key.ToString();
                CreateNeighborhoodSection(contentParent, headerTitle, group.ToList());
            }
        }

        private void CreateNeighborhoodSection(Transform parent, string regionName, List<CustomerDisplayData> dataList)
        {
            // Header
            var headerObj = UIFactory.Text($"Header_{regionName}", $"<b>{regionName}</b>", parent, 18, TextAnchor.MiddleCenter);
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
                CreateCustomerCell(gridObj.transform, item.Customer, item.IsLocked);
            }
        }

        private void CreateCustomerCell(Transform gridParent, Customer customer, bool isLocked)
        {
            // 1. Cell Container
            var cellObj = UIFactory.Panel($"Cell_{customer.NPC.fullName}", gridParent, new Color(0.2f, 0.2f, 0.2f));

            // Add Button only if unlocked (or handle locked click differently)
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

            // 3. Addiction Progress Bar
            var addiction = Mathf.Clamp01(customer.CurrentAddiction);
            CreateAddictionBar(cellObj.transform, addiction);

            // 4. Name Label
            var nameObj = UIFactory.Text("Name", customer.NPC.FirstName, cellObj.transform, 12, TextAnchor.UpperCenter);
            var nameRect = nameObj.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 0);
            nameRect.anchorMax = new Vector2(1, 0);
            nameRect.pivot = new Vector2(0.5f, 0);
            nameRect.anchoredPosition = new Vector2(0, 3);
            nameRect.sizeDelta = new Vector2(0, 20);

            // 5. Locked State Styling
            if (isLocked)
            {
                var cg = cellObj.AddComponent<CanvasGroup>();
                cg.alpha = 0.5f; // Dim the cell
                btn.interactable = false; // Disable clicking
            }
            else
            {
                // Click to pin customer on map (cast to System.Action for Il2Cpp compatibility)
                btn.onClick.AddListener(new System.Action(() =>
                {
                    CustomerLocator.PinCustomerToMap(customer);
                }));
            }
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