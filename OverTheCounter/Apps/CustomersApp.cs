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
using OverTheCounter.Logic;
using OverTheCounter.Utilities;

#if IL2CPP
using Il2CppScheduleOne.Map;
#else
using ScheduleOne.Map;
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
        private enum AppTab { Managers, Customers }
        private AppTab _activeTab = AppTab.Managers;

        // Root panel
        private GameObject _rootPanel;

        // Header elements
        private Text _headerTitle;
        private GameObject _tabContainer;
        private Image _managersTabImage;
        private Image _customersTabImage;
        private Text _managersTabText;
        private Text _customersTabText;

        // Tab styling
        private static readonly Color ActiveTabBg = new Color(0f, 0f, 0f, 0.3f);

        // Managers page
        private GameObject _managersPage;
        private Transform _managersContentParent;

        // Customers page
        private GameObject _customersPage;
        private Transform _customersContentParent;

        // Customers page sub-elements
        private GameObject _legendObj;
        private GameObject _billingBar;
        private Text _billingText;
        private RectTransform _customersScrollRect;

        // Manager detail page (overlay)
        private GameObject _managerDetailPage;
        private ManagerInstance _detailManager;
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
        private Text _detailStatusText;
        private Text _detailCashText;

        // Upgrade section - refreshable elements
        private Text _detailBankText;
        private Text _detailWageText;
        private Text _detailSpeedLabel;
        private Text _detailSpeedCostLabel;
        private Button _detailSpeedBtn;
        private Text _detailSpeedBtnText;
        private Text _detailInvUpLabel;
        private Text _detailInvCostLabel;
        private Button _detailInvBtn;
        private Text _detailInvBtnText;
        private RectTransform _detailSpeedFill;
        private RectTransform _detailInvFill;
        private Text _speedErrorText;
        private Text _invErrorText;

        // Manager log page (overlay)
        private GameObject _managerLogPage;
        private ManagerInstance _logPageManager;
        private Text _logText;
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

            // Customers page
            _customersPage = UIFactory.Panel("CustomersPage", _rootPanel.transform, Color.clear);
            var custPageRect = _customersPage.GetComponent<RectTransform>();
            custPageRect.anchorMin = Vector2.zero;
            custPageRect.anchorMax = Vector2.one;
            custPageRect.offsetMin = Vector2.zero;
            custPageRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);
            BuildCustomersPage(_customersPage.transform);

            UpdateLayout();
            SwitchTab(_activeTab);
            StartPeriodicRefresh();
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
            var titleObj = UIFactory.Text("Title", "<b>OverTheCounter</b>", headerObj.transform, 24, TextAnchor.MiddleLeft);
            var titleRect = titleObj.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = new Vector2(0.45f, 1);
            titleRect.offsetMin = new Vector2(15, 0);
            titleRect.offsetMax = Vector2.zero;
            _headerTitle = titleObj;

            // Tab container (right portion, hidden when tier < 1)
            _tabContainer = UIFactory.Panel("TabContainer", headerObj.transform, Color.clear);
            var tabRect = _tabContainer.GetComponent<RectTransform>();
            tabRect.anchorMin = new Vector2(0.55f, 0.1f);
            tabRect.anchorMax = new Vector2(0.95f, 0.9f);
            tabRect.offsetMin = Vector2.zero;
            tabRect.offsetMax = Vector2.zero;

            // Managers tab (left half)
            var mgrTab = UIFactory.Panel("ManagersTab", _tabContainer.transform, ActiveTabBg);
            var mgrTabRect = mgrTab.GetComponent<RectTransform>();
            mgrTabRect.anchorMin = Vector2.zero;
            mgrTabRect.anchorMax = new Vector2(0.48f, 1);
            mgrTabRect.offsetMin = Vector2.zero;
            mgrTabRect.offsetMax = Vector2.zero;
            _managersTabImage = mgrTab.GetComponent<Image>();
            mgrTab.AddComponent<Button>().onClick.AddListener(new Action(() => SwitchTab(AppTab.Managers)));

            _managersTabText = UIFactory.Text("MgrLabel", "<b>Managers</b>", mgrTab.transform, 14, TextAnchor.MiddleCenter);
            var mgrTextRect = _managersTabText.gameObject.GetComponent<RectTransform>();
            mgrTextRect.anchorMin = Vector2.zero;
            mgrTextRect.anchorMax = Vector2.one;
            mgrTextRect.offsetMin = Vector2.zero;
            mgrTextRect.offsetMax = Vector2.zero;
            _managersTabText.color = Color.white;

            // Customers tab (right half)
            var custTab = UIFactory.Panel("CustomersTab", _tabContainer.transform, Color.clear);
            var custTabRect = custTab.GetComponent<RectTransform>();
            custTabRect.anchorMin = new Vector2(0.52f, 0);
            custTabRect.anchorMax = Vector2.one;
            custTabRect.offsetMin = Vector2.zero;
            custTabRect.offsetMax = Vector2.zero;
            _customersTabImage = custTab.GetComponent<Image>();
            custTab.AddComponent<Button>().onClick.AddListener(new Action(() => SwitchTab(AppTab.Customers)));

            _customersTabText = UIFactory.Text("CustLabel", "Customers", custTab.transform, 14, TextAnchor.MiddleCenter);
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

            // Close log page if open
            if (_managerLogPage != null)
            {
                UnityEngine.Object.Destroy(_managerLogPage);
                _managerLogPage = null;
                _logPageManager = null;
                _logText = null;
                _logScrollRect = null;
            }

            // Close detail page if open
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
            _customersPage.SetActive(tab == AppTab.Customers);

            // Tab styling
            if (_managersTabImage != null)
                _managersTabImage.color = tab == AppTab.Managers ? ActiveTabBg : Color.clear;
            if (_customersTabImage != null)
                _customersTabImage.color = tab == AppTab.Customers ? ActiveTabBg : Color.clear;
            if (_managersTabText != null)
            {
                _managersTabText.text = tab == AppTab.Managers ? "<b>Managers</b>" : "Managers";
                _managersTabText.color = tab == AppTab.Managers ? Color.white : new Color(0.7f, 0.7f, 0.7f);
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
                        1 => "<b>OverTheCounter</b> <color=#999999><size=16>Lite</size></color>",
                        2 => "<b>OverTheCounter</b> <color=#2ABFBF><size=16>Pro</size></color>",
                        3 => "<b>OverTheCounter</b> <color=#FFD700><size=16>Enterprise</size></color>",
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
            else
                RefreshCustomersPage();
        }

        private void UpdateLayout()
        {
            bool showTabs = GetEffectiveTier() >= 1;
            if (_tabContainer != null)
                _tabContainer.SetActive(showTabs);

            if (!showTabs && _activeTab == AppTab.Customers)
                _activeTab = AppTab.Managers;
        }

        internal void RefreshApp()
        {
            UpdateLayout();
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
            while (_managerDetailPage != null)
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
