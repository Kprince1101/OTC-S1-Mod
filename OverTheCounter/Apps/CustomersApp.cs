using S1API.PhoneApp;
using S1API.UI;
using UnityEngine;
using UnityEngine.UI;
using OverTheCounter.SaveData;
using MelonLoader;
using MelonLoader.Utils;
using System.IO;
using System.Collections;
using HarmonyLib;
using System;
using System.Collections.Generic;
using OverTheCounter.Logic;
using OverTheCounter.UI;
using OverTheCounter.Utilities;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Cartel;
using Il2CppScheduleOne.DevUtilities;
using Il2CppTMPro;
#else
using ScheduleOne.Economy;
using ScheduleOne.Employees;
using ScheduleOne.Map;
using ScheduleOne.Cartel;
using ScheduleOne.DevUtilities;
using TMPro;
#endif

namespace OverTheCounter.Apps
{
    public partial class CustomersApp : PhoneApp
    {
        protected override string AppName => "OverTheCounterApp";
        protected override string AppTitle => "OverTheCounter";
        protected override string IconLabel => "OTC";
        protected override EOrientation Orientation => EOrientation.Horizontal;

        protected override string IconFileName => Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "CustomersIcon.png");

        // Layout constants
        internal const float HEADER_HEIGHT = 40f;
        internal const float CELL_WIDTH = 130f;
        internal const float CELL_HEIGHT = 165f;
        internal const int COLUMNS = 4;

        // Tab system
        private enum AppTab { Managers, Employees, Customers }
        private AppTab _activeTab = AppTab.Managers;

        // Root panel
        private GameObject _rootPanel;

        // Header elements
        private TextMeshProUGUI _headerTitle;
        private GameObject _tabContainer;
        private Image _managersTabImage;
        private Image _employeesTabImage;
        private Image _customersTabImage;
        private TextMeshProUGUI _managersTabText;
        private TextMeshProUGUI _employeesTabText;
        private TextMeshProUGUI _customersTabText;

        // Tab styling
        private static readonly Color ActiveTabBg = new Color(0f, 0f, 0f, 0.3f);

        // Managers page
        private GameObject _managersPage;
        private Transform _managersContentParent;

        // Employees page
        private GameObject _employeesPage;
        private Transform _employeesContentParent;

        // Employee detail page (overlay)
        private GameObject _employeeDetailPage;

        // Tier-0 landing page (shown instead of tabs when no subscription)
        private GameObject _landingPage;

        // Collapsible expand state (persists across refreshes within a session)
        internal Dictionary<string, bool> _employeePropertyExpanded = new();

        // Employee filter state (persists across refreshes within a session)
        internal HashSet<EEmployeeType> _employeeTypeFilter;
        internal bool _employeeGroupByProperty = true;

        // Customers page
        private GameObject _customersPage;
        private Transform _customersContentParent;

        // Customers page sub-elements
        private GameObject _billingBar;
        private TextMeshProUGUI _billingText;
        private RectTransform _customersScrollRect;

        // Manager detail page (overlay)
        private GameObject _managerDetailPage;
        private ManagerInstance _detailManager;

        // Customer selection (inline, within customers page)
        private Customer _selectedCustomer;
        private GameObject _selectedCellObj;
        private GameObject _custDetailPanel;
        private RectTransform _custSplitAreaRect;
        private ScrollRect _customersScroll;
        private float _savedScrollPos;

        // Customer map page (full-screen overlay, tier 3)
        private GameObject _custMapPage;

        // Customer detail — refreshable elements
        private RectTransform _custDetailRelFill;
        private RectTransform _custDetailAddFill;
        private TextMeshProUGUI _custDetailWeeklyText;
        private Image _detailMugshotImage; // updated via OnMugshotReady if mugshot arrives late

        // Minimap live tracking
        private RectTransform _minimapImageRect;
        private float _minimapDisplaySize;
        private float _minimapContentW;
        private float _minimapContentH;
        private Image _minimapMarkerIcon; // mugshot inside map marker circle
        private RectTransform _minimapDestRect; // red destination marker
        private Image _minimapBgImage; // container background, blended by pan position

        // Manager detail page - refreshable elements
        private GameObject _detailInvGrid;
        private TextMeshProUGUI _detailStatusText;
        private TextMeshProUGUI _detailCashText;

        // Upgrade section - refreshable elements
        private TextMeshProUGUI _detailBankText;
        private TextMeshProUGUI _detailWageText;
        private TextMeshProUGUI _detailSpeedLabel;
        private TextMeshProUGUI _detailSpeedCostLabel;
        private Button _detailSpeedBtn;
        private TextMeshProUGUI _detailSpeedBtnText;
        private TextMeshProUGUI _detailInvUpLabel;
        private TextMeshProUGUI _detailInvCostLabel;
        private Button _detailInvBtn;
        private TextMeshProUGUI _detailInvBtnText;
        private RectTransform _detailSpeedFill;
        private RectTransform _detailInvFill;
        private TextMeshProUGUI _speedErrorText;
        private TextMeshProUGUI _invErrorText;

        // Manager log page (overlay)
        private GameObject _managerLogPage;
        private ManagerInstance _logPageManager;
        private TextMeshProUGUI _logText;
        private ScrollRect _logScrollRect;

        public static CustomersApp Instance { get; private set; }

        protected override void OnCreated()
        {
            base.OnCreated();
            Instance = this;
        }

        private void ClearChildren(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
        }

        // Distribution route colors — lighter to darker orange for routes 1, 2, 3
        private static readonly Color DistRoute1Color = new Color(0.95f, 0.65f, 0.25f);
        private static readonly Color DistRoute2Color = new Color(0.85f, 0.50f, 0.15f);
        private static readonly Color DistRoute3Color = new Color(0.75f, 0.38f, 0.08f);

        private static Color GetDistributionRouteColor(int routeDisplay)
        {
            return routeDisplay switch
            {
                1 => DistRoute1Color,
                2 => DistRoute2Color,
                _ => DistRoute3Color,
            };
        }

        // Status strings use \u25CF (● filled circle) as a colored bullet prefix
        internal static (string text, Color color) GetStatusDisplay(ManagerInstance mgr)
        {
            var state = mgr.State;
            if (state == ManagerState.Idle && !mgr.PaidForToday)
                state = ManagerState.NoFunds;

            if (state == ManagerState.SupplyRun)
            {
                string phase;
                if (NetworkHelper.IsHost)
                {
                    phase = mgr.SupplyBehaviour?.State switch
                    {
                        ManagerSupplyBehaviour.SupplyState.WalkingToStorage
                            or ManagerSupplyBehaviour.SupplyState.AtStorage => "Depositing",
                        ManagerSupplyBehaviour.SupplyState.WalkingToStore
                            or ManagerSupplyBehaviour.SupplyState.AtStore => "Purchasing",
                        _ => null,
                    };
                }
                else
                {
                    phase = mgr._subPhaseCode switch { 1 => "Depositing", 2 => "Purchasing", _ => null };
                }
                string label = phase != null ? $"\u25CF Supply Run - {phase}" : "\u25CF Supply Run";
                return (label, new Color(0.2f, 0.75f, 0.2f));
            }

            if (state == ManagerState.DistributionRun)
            {
                int route = NetworkHelper.IsHost
                    ? mgr.DistributionBehaviour?.CurrentRouteDisplay ?? 1
                    : 1;
                string phase;
                if (NetworkHelper.IsHost)
                {
                    phase = mgr.DistributionBehaviour?.State switch
                    {
                        ManagerDistributionBehaviour.DistributionState.WalkingToDestProperty
                            or ManagerDistributionBehaviour.DistributionState.WalkingToDest
                            or ManagerDistributionBehaviour.DistributionState.AtDest => "Depositing",
                        ManagerDistributionBehaviour.DistributionState.WalkingToSourceProperty
                            or ManagerDistributionBehaviour.DistributionState.WalkingToSource
                            or ManagerDistributionBehaviour.DistributionState.AtSource => "Picking Up",
                        _ => null,
                    };
                }
                else
                {
                    phase = mgr._subPhaseCode switch { 1 => "Depositing", 2 => "Picking Up", _ => null };
                }
                string label = phase != null
                    ? $"\u25CF Distribution Route {route} - {phase}"
                    : $"\u25CF Distribution Route {route}";
                return (label, GetDistributionRouteColor(route));
            }

            return GetStatusDisplay(state);
        }

        internal static (string text, Color color) GetStatusDisplay(ManagerState state)
        {
            return state switch
            {
                ManagerState.SupplyRun => ("\u25CF Supply Run", new Color(0.2f, 0.75f, 0.2f)),
                ManagerState.DistributionRun => ("\u25CF Distribution", new Color(0.85f, 0.50f, 0.15f)),
                ManagerState.NoFunds => ("\u25CF No Funds", new Color(0.9f, 0.25f, 0.25f)),
                ManagerState.Transferring => ("\u25CF Transferring", new Color(0.9f, 0.6f, 0.15f)),
                _ => ("\u25CF Idle", new Color(0.5f, 0.5f, 0.5f)),
            };
        }

        internal int GetEffectiveTier()
        {
            if (StaticSaveData.Instance == null || !StaticSaveData.Instance.SaasActive)
                return 0;
            return StaticSaveData.Instance.CrmTier;
        }

        private bool IsRegionUnlocked(int effectiveTier, EMapRegion region)
        {
            if (effectiveTier <= 0) return false;
            if (effectiveTier == 1)
                return region == EMapRegion.Northtown || region == EMapRegion.Westville;
            // tier 2+: use actual game region unlock state
            try
            {
                var mapData = Singleton<Map>.Instance?.GetRegionData(region);
                if (mapData != null) return mapData.IsUnlocked;
            }
            catch { }
            return true;
        }

        // ==================================================================
        // OnCreatedUI
        // ==================================================================

        protected override void OnCreatedUI(GameObject container)
        {
            _rootPanel = UIFactory.Panel("MainPanel", container.transform, new Color(0.12f, 0.12f, 0.12f), fullAnchor: true);

            BuildHeader(_rootPanel.transform);

            // Managers page
            _managersPage = UIFactory.Panel("ManagersPage", _rootPanel.transform, Color.clear);
            var mgrPageRect = _managersPage.GetComponent<RectTransform>();
            mgrPageRect.anchorMin = Vector2.zero;
            mgrPageRect.anchorMax = Vector2.one;
            mgrPageRect.offsetMin = Vector2.zero;
            mgrPageRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);
            BuildManagersPage(_managersPage.transform);

            // Employees page
            _employeesPage = UIFactory.Panel("EmployeesPage", _rootPanel.transform, Color.clear);
            var empPageRect = _employeesPage.GetComponent<RectTransform>();
            empPageRect.anchorMin = Vector2.zero;
            empPageRect.anchorMax = Vector2.one;
            empPageRect.offsetMin = Vector2.zero;
            empPageRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);
            BuildEmployeesPage(_employeesPage.transform);

            // Customers page
            _customersPage = UIFactory.Panel("CustomersPage", _rootPanel.transform, Color.clear);
            var custPageRect = _customersPage.GetComponent<RectTransform>();
            custPageRect.anchorMin = Vector2.zero;
            custPageRect.anchorMax = Vector2.one;
            custPageRect.offsetMin = Vector2.zero;
            custPageRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);
            BuildCustomersPage(_customersPage.transform);

            // Landing page (covers content area at tier 0)
            _landingPage = UIFactory.Panel("LandingPage", _rootPanel.transform, new Color(0.10f, 0.10f, 0.12f));
            var landingRect = _landingPage.GetComponent<RectTransform>();
            landingRect.anchorMin = Vector2.zero;
            landingRect.anchorMax = Vector2.one;
            landingRect.offsetMin = Vector2.zero;
            landingRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);
            BuildLandingPage(_landingPage.transform);

            SwitchTab(_activeTab);
            UpdateLayout();
            StartPeriodicRefresh();
        }

        // ==================================================================
        // Landing page (tier 0 — shown instead of tabs)
        // ==================================================================

        // Tier accent colors — used across the app wherever tier is referenced
        internal const string Tier1Color = "#5B9FD4";
        internal const string Tier2Color = "#2ABFBF";
        internal const string Tier3Color = "#FFD700";

        private void BuildLandingPage(Transform parent)
        {
            // Centre column
            var col = UIFactory.Panel("LandingCol", parent, Color.clear);
            var colRect = col.GetComponent<RectTransform>();
            colRect.anchorMin = new Vector2(0.05f, 0.04f);
            colRect.anchorMax = new Vector2(0.95f, 0.96f);
            colRect.offsetMin = Vector2.zero;
            colRect.offsetMax = Vector2.zero;

            // Title
            var titleText = TMPFactory.Text("LandingTitle", "<b>OverTheCounter</b>", col.transform, 42, TextAlignmentOptions.Center);
            var titleRect = titleText.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 0.86f);
            titleRect.anchorMax = Vector2.one;
            titleRect.offsetMin = Vector2.zero;
            titleRect.offsetMax = Vector2.zero;
            titleText.color = Color.white;

            // Subtitle
            var subText = TMPFactory.Text("LandingSubtitle",
                $"<color=#888888>Business Suite  ·  <color={Tier1Color}>Tier 1</color> Required</color>",
                col.transform, 18, TextAlignmentOptions.Center);
            subText.richText = true;
            var subRect = subText.gameObject.GetComponent<RectTransform>();
            subRect.anchorMin = new Vector2(0, 0.77f);
            subRect.anchorMax = new Vector2(1, 0.87f);
            subRect.offsetMin = Vector2.zero;
            subRect.offsetMax = Vector2.zero;

            // Divider — full width
            var div = UIFactory.Panel("Divider", col.transform, new Color(0.25f, 0.55f, 0.65f, 0.4f));
            var divRect = div.GetComponent<RectTransform>();
            divRect.anchorMin = new Vector2(0, 0.725f);
            divRect.anchorMax = new Vector2(1, 0.735f);
            divRect.offsetMin = Vector2.zero;
            divRect.offsetMax = Vector2.zero;

            // Feature cards (three across)
            AddLandingFeatureCard(col.transform, 0, "Customer CRM", "Buyer habits, preferences,\nand purchase history\nin one place.");
            AddLandingFeatureCard(col.transform, 1, "Manager Oversight", "See your managers\nin real time: where they are\nand what they carry.");
            AddLandingFeatureCard(col.transform, 2, "Employee Tracking", "Every employee by property,\nstatus, and inventory,\nat a glance.");

            var ctaText = TMPFactory.Text("CTAText",
                $"Available at <color={Tier1Color}>Tier 1</color>  ·  Expand your operation to unlock",
                col.transform, 17, TextAlignmentOptions.Center);
            ctaText.richText = true;
            var ctaTextRect = ctaText.gameObject.GetComponent<RectTransform>();
            ctaTextRect.anchorMin = new Vector2(0, 0.02f);
            ctaTextRect.anchorMax = new Vector2(1, 0.16f);
            ctaTextRect.offsetMin = Vector2.zero;
            ctaTextRect.offsetMax = Vector2.zero;
            ctaText.color = new Color(0.6f, 0.72f, 0.78f);
        }

        private void AddLandingFeatureCard(Transform parent, int index, string title, string body)
        {
            const float gap = 0.02f;
            const float cardW = (1f - 2f * gap) / 3f;
            float startX = gap + index * (cardW + gap);

            var card = UIFactory.Panel($"Feature{index}", parent, new Color(0.13f, 0.32f, 0.42f, 0.55f));
            var cardRect = card.GetComponent<RectTransform>();
            cardRect.anchorMin = new Vector2(startX, 0.21f);
            cardRect.anchorMax = new Vector2(startX + cardW, 0.70f);
            cardRect.offsetMin = Vector2.zero;
            cardRect.offsetMax = Vector2.zero;

            // Title — top 30%, MiddleCenter so it's not glued to the top edge
            var titleText = TMPFactory.Text("CardTitle", $"<b>{title}</b>", card.transform, 26, TextAlignmentOptions.Center);
            var titleRect = titleText.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 0.68f);
            titleRect.anchorMax = Vector2.one;
            titleRect.offsetMin = new Vector2(8, 0);
            titleRect.offsetMax = new Vector2(-8, -4);
            titleText.color = new Color(0.4f, 0.85f, 0.9f);

            // Body — bottom 68%, MiddleCenter so text fills the zone
            var bodyText = TMPFactory.Text("CardBody", body, card.transform, 24, TextAlignmentOptions.Center);
            var bodyRect = bodyText.gameObject.GetComponent<RectTransform>();
            bodyRect.anchorMin = Vector2.zero;
            bodyRect.anchorMax = new Vector2(1, 0.68f);
            bodyRect.offsetMin = new Vector2(8, 4);
            bodyRect.offsetMax = new Vector2(-8, 0);
            bodyText.color = new Color(0.72f, 0.72f, 0.72f);
        }

        // ==================================================================
        // Header (with inline tabs)
        // ==================================================================

        private void BuildHeader(Transform parent)
        {
            var headerObj = UIFactory.Panel("Header", parent, new Color(0.15f, 0.35f, 0.45f));
            var headerRect = headerObj.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = new Vector2(1, 1);
            headerRect.pivot = new Vector2(0.5f, 1);
            headerRect.anchoredPosition = Vector2.zero;
            headerRect.sizeDelta = new Vector2(0, HEADER_HEIGHT);

            // Title (left)
            var titleObj = TMPFactory.Text("Title", "<b>OverTheCounter</b>", headerObj.transform, 24, TextAlignmentOptions.Left);
            var titleRect = titleObj.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = new Vector2(0.45f, 1);
            titleRect.offsetMin = new Vector2(15, 0);
            titleRect.offsetMax = Vector2.zero;
            _headerTitle = titleObj;

            // Tab container (right portion — always visible)
            _tabContainer = UIFactory.Panel("TabContainer", headerObj.transform, Color.clear);
            var tabRect = _tabContainer.GetComponent<RectTransform>();
            tabRect.anchorMin = new Vector2(0.45f, 0.1f);
            tabRect.anchorMax = new Vector2(0.98f, 0.9f);
            tabRect.offsetMin = Vector2.zero;
            tabRect.offsetMax = Vector2.zero;

            // Managers tab (left third)
            var mgrTab = UIFactory.Panel("ManagersTab", _tabContainer.transform, ActiveTabBg);
            var mgrTabRect = mgrTab.GetComponent<RectTransform>();
            mgrTabRect.anchorMin = Vector2.zero;
            mgrTabRect.anchorMax = new Vector2(0.32f, 1);
            mgrTabRect.offsetMin = Vector2.zero;
            mgrTabRect.offsetMax = Vector2.zero;
            _managersTabImage = mgrTab.GetComponent<Image>();
            mgrTab.AddComponent<Button>().onClick.AddListener(new Action(() => SwitchTab(AppTab.Managers)));

            _managersTabText = TMPFactory.Text("MgrLabel", "<b>Managers</b>", mgrTab.transform, 15, TextAlignmentOptions.Center);
            var mgrTextRect = _managersTabText.gameObject.GetComponent<RectTransform>();
            mgrTextRect.anchorMin = Vector2.zero;
            mgrTextRect.anchorMax = Vector2.one;
            mgrTextRect.offsetMin = Vector2.zero;
            mgrTextRect.offsetMax = Vector2.zero;
            _managersTabText.color = Color.white;

            // Employees tab (middle third)
            var empTab = UIFactory.Panel("EmployeesTab", _tabContainer.transform, Color.clear);
            var empTabRect = empTab.GetComponent<RectTransform>();
            empTabRect.anchorMin = new Vector2(0.34f, 0);
            empTabRect.anchorMax = new Vector2(0.66f, 1);
            empTabRect.offsetMin = Vector2.zero;
            empTabRect.offsetMax = Vector2.zero;
            _employeesTabImage = empTab.GetComponent<Image>();
            empTab.AddComponent<Button>().onClick.AddListener(new Action(() => SwitchTab(AppTab.Employees)));

            _employeesTabText = TMPFactory.Text("EmpLabel", "Employees", empTab.transform, 15, TextAlignmentOptions.Center);
            var empTextRect = _employeesTabText.gameObject.GetComponent<RectTransform>();
            empTextRect.anchorMin = Vector2.zero;
            empTextRect.anchorMax = Vector2.one;
            empTextRect.offsetMin = Vector2.zero;
            empTextRect.offsetMax = Vector2.zero;
            _employeesTabText.color = new Color(0.7f, 0.7f, 0.7f);

            // Customers tab (right third)
            var custTab = UIFactory.Panel("CustomersTab", _tabContainer.transform, Color.clear);
            var custTabRect = custTab.GetComponent<RectTransform>();
            custTabRect.anchorMin = new Vector2(0.68f, 0);
            custTabRect.anchorMax = Vector2.one;
            custTabRect.offsetMin = Vector2.zero;
            custTabRect.offsetMax = Vector2.zero;
            _customersTabImage = custTab.GetComponent<Image>();
            custTab.AddComponent<Button>().onClick.AddListener(new Action(() => SwitchTab(AppTab.Customers)));

            _customersTabText = TMPFactory.Text("CustLabel", "Customers", custTab.transform, 15, TextAlignmentOptions.Center);
            var custTextRect = _customersTabText.gameObject.GetComponent<RectTransform>();
            custTextRect.anchorMin = Vector2.zero;
            custTextRect.anchorMax = Vector2.one;
            custTextRect.offsetMin = Vector2.zero;
            custTextRect.offsetMax = Vector2.zero;
            _customersTabText.color = new Color(0.7f, 0.7f, 0.7f);
        }

        // ==================================================================
        // Tab switching
        // ==================================================================

        private void SwitchTab(AppTab tab)
        {
            _activeTab = tab;
            if (_landingPage != null) _landingPage.SetActive(false);

            // Close log page if open
            if (_managerLogPage != null)
            {
                UnityEngine.Object.Destroy(_managerLogPage);
                _managerLogPage = null;
                _logPageManager = null;
                _logText = null;
                _logScrollRect = null;
            }

            // Close employee detail page if open
            if (_employeeDetailPage != null)
            {
                StopMinimapTracking();
                UnityEngine.Object.Destroy(_employeeDetailPage);
                _employeeDetailPage = null;
                CleanupEmployeeDetailFields();
            }

            // Close customer map page if open
            if (_custMapPage != null)
            {
                StopMinimapTracking();
                UnityEngine.Object.Destroy(_custMapPage);
                _custMapPage = null;
                CleanupCustomerDetailFields();
                if (_customersPage != null) _customersPage.SetActive(true);
            }

            // Close manager detail page if open
            if (_managerDetailPage != null)
            {
                if (_detailManager != null)
                    _detailManager.OnMugshotReady -= OnDetailMugshotReady;
                StopMinimapTracking();
                UnityEngine.Object.Destroy(_managerDetailPage);
                _managerDetailPage = null;
                _detailManager = null;
                _detailMugshotImage = null;
                _detailInvGrid = null;
                _detailStatusText = null;
                _detailCashText = null;
                _detailBankText = null;
                _detailWageText = null;
                _detailSpeedLabel = null;
                _detailSpeedCostLabel = null;
                _detailSpeedBtn = null;
                _detailSpeedBtnText = null;
                _detailInvUpLabel = null;
                _detailInvCostLabel = null;
                _detailInvBtn = null;
                _detailInvBtnText = null;
                _detailSpeedFill = null;
                _detailInvFill = null;
                _speedErrorText = null;
                _invErrorText = null;
                _minimapImageRect = null;
                _minimapMarkerIcon = null;
                _minimapDestRect = null;
                _minimapBgImage = null;
            }

            _managersPage.SetActive(tab == AppTab.Managers);
            _employeesPage.SetActive(tab == AppTab.Employees);
            _customersPage.SetActive(tab == AppTab.Customers);

            // Tab styling
            if (_managersTabImage != null)
                _managersTabImage.color = tab == AppTab.Managers ? ActiveTabBg : Color.clear;
            if (_employeesTabImage != null)
                _employeesTabImage.color = tab == AppTab.Employees ? ActiveTabBg : Color.clear;
            if (_customersTabImage != null)
                _customersTabImage.color = tab == AppTab.Customers ? ActiveTabBg : Color.clear;

            if (_managersTabText != null)
            {
                _managersTabText.text = tab == AppTab.Managers ? "<b>Managers</b>" : "Managers";
                _managersTabText.color = tab == AppTab.Managers ? Color.white : new Color(0.7f, 0.7f, 0.7f);
            }
            if (_employeesTabText != null)
            {
                _employeesTabText.text = tab == AppTab.Employees ? "<b>Employees</b>" : "Employees";
                _employeesTabText.color = tab == AppTab.Employees ? Color.white : new Color(0.7f, 0.7f, 0.7f);
            }
            if (_customersTabText != null)
            {
                _customersTabText.text = tab == AppTab.Customers ? "<b>Customers</b>" : "Customers";
                _customersTabText.color = tab == AppTab.Customers ? Color.white : new Color(0.7f, 0.7f, 0.7f);
            }

            // Header title — tier suffix only on Customers tab
            if (_headerTitle != null)
            {
                if (tab == AppTab.Customers)
                {
                    int tier = GetEffectiveTier();
                    _headerTitle.text = tier switch
                    {
                        1 => $"<b>OverTheCounter</b> <color={Tier1Color}><size=16>Lite</size></color>",
                        2 => $"<b>OverTheCounter</b> <color={Tier2Color}><size=16>Pro</size></color>",
                        3 => $"<b>OverTheCounter</b> <color={Tier3Color}><size=16>Enterprise</size></color>",
                        _ => "<b>OverTheCounter</b>"
                    };
                }
                else
                {
                    _headerTitle.text = "<b>OverTheCounter</b>";
                }
            }

            if (tab == AppTab.Managers)
                RefreshManagersPage();
            else if (tab == AppTab.Employees)
                RefreshEmployeesPage();
            else
                RefreshCustomersPage();
        }

        private void UpdateLayout()
        {
            int effectiveTier = GetEffectiveTier();
            bool hasTier = effectiveTier >= 1;

            // At tier 0: show landing page, hide everything else
            if (_landingPage != null)
                _landingPage.SetActive(!hasTier);
            if (_tabContainer != null)
                _tabContainer.SetActive(hasTier);
            if (_managersPage != null && !hasTier)
                _managersPage.SetActive(false);
            if (_employeesPage != null && !hasTier)
                _employeesPage.SetActive(false);
            if (_customersPage != null && !hasTier)
                _customersPage.SetActive(false);
        }

        internal void RefreshApp()
        {
            UpdateLayout();
            if (GetEffectiveTier() < 1) return;
            if (_managerLogPage != null)
            {
                RefreshLogContent();
                return;
            }
            if (_managerDetailPage != null)
            {
                RefreshDetailInventory();
                return;
            }
            if (_employeeDetailPage != null)
            {
                RefreshEmployeeDetail();
                return;
            }
            SwitchTab(_activeTab);
        }

        // Periodic refresh for live data (inventory, status, cash)
        private const float MANAGER_REFRESH_INTERVAL = 3f;
        private object _refreshCoroutine;
        private object _minimapCoroutine;

        private void StartPeriodicRefresh()
        {
            if (_refreshCoroutine != null) return;
            _refreshCoroutine = MelonCoroutines.Start(PeriodicRefreshRoutine());
        }

        private IEnumerator PeriodicRefreshRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(MANAGER_REFRESH_INTERVAL);
                if (_managerLogPage != null)
                    RefreshLogContent();
                else if (_managerDetailPage != null)
                    RefreshDetailInventory();
                else if (_employeeDetailPage != null && _employeeDetailPage.activeInHierarchy)
                    RefreshEmployeeDetail();
                else if (_customersPage != null && _customersPage.activeInHierarchy && _selectedCustomer != null)
                    RefreshCustomerDetail();
                else if (_employeesPage != null && _employeesPage.activeInHierarchy)
                    RefreshEmployeesPage();
                else if (_managersPage != null && _managersPage.activeInHierarchy)
                    RefreshManagersPage();
            }
        }

        // Per-frame minimap tracking coroutine (Update() doesn't fire on IL2CPP managed classes)
        internal void StartMinimapTracking()
        {
            StopMinimapTracking();
            _minimapCoroutine = MelonCoroutines.Start(MinimapTrackingRoutine());
        }

        internal void StopMinimapTracking()
        {
            if (_minimapCoroutine != null)
            {
                MelonCoroutines.Stop(_minimapCoroutine);
                _minimapCoroutine = null;
            }
        }

        private IEnumerator MinimapTrackingRoutine()
        {
            while (_managerDetailPage != null || _employeeDetailPage != null || _custMapPage != null)
            {
                UpdateMinimapPosition();
                yield return null; // every frame
            }
            _minimapCoroutine = null;
        }
    }

    [HarmonyPatch(typeof(PhoneApp), "OpenApp")]
    public static class CustomersAppOpenPatch
    {
        public static void Prefix(PhoneApp __instance)
        {
            if (__instance is CustomersApp app)
            {
                app.RefreshApp();
            }
        }
    }
}
