using MelonLoader;
using MelonLoader.Utils;
using OverTheCounter.Apps;
using OverTheCounter.Logic;
using OverTheCounter.Logic.Placement;
using OverTheCounter.NPCs;
using OverTheCounter.Patches;
using OverTheCounter.Quests;
using OverTheCounter.SaveData;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using S1API.GameTime;
using S1API.PhoneApp;
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

#if IL2CPP
using Il2CppInterop.Runtime.Injection;
using Il2CppFishNet.Object;
#else
using FishNet.Object;
#endif

[assembly: MelonInfo(typeof(OverTheCounter.Core), "OverTheCounter", "1.5.7", "hdlmrell", null)]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: MelonOptionalDependencies("SteamNetworkLib")]
[assembly: HarmonyDontPatchAll]

namespace OverTheCounter
{
    public class Core : MelonMod
    {
        private NotificationManager _notificationManager;
        private DesperationManager _desperationManager;
        private DrifterManager _drifterManager;
        private ManagerController _managerManager;
        private CustomerManager _customerManager;

        public override void OnInitializeMelon()
        {
            DependencyChecker.RunChecks();
            if (DependencyChecker.HasMissingDeps)
            {
                LoggerInstance.Warning("Missing dependencies — mod features disabled. " +
                    "Check the main menu for details.");
                return;
            }
            OnInitializeMelonImpl();
        }

        private void OnInitializeMelonImpl()
        {
            Config.Initialize();
            Config.SubscribeToChanges();
            CustomersApp.ApplyHireMeDefaults();
            SafeTypeLoadPatch.Apply(HarmonyInstance);
            foreach (var type in typeof(Core).Assembly.GetValidTypes())
                try { HarmonyInstance.CreateClassProcessor(type).Patch(); }
                catch (Exception ex) { OTCLog.Error(OTCLog.Systems.Patch, $"Failed to patch {type.FullName}: {ex.Message}"); }
            NpcTypeDiscoveryPatch.Apply(HarmonyInstance);
            StackSizePatch.Apply(HarmonyInstance);
            ConfigSyncPatch.TryApply(HarmonyInstance);

            ManagerClipboardPatch.Apply(HarmonyInstance);
            try
            {
                BuildingPlacementPatch.Apply(HarmonyInstance);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"BuildingPlacementPatch.Apply failed: {ex}");
            }
            try
            {
                ConfigReplicatorPatch.Apply(HarmonyInstance);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"ConfigReplicatorPatch.Apply failed: {ex}");
            }
            ContactsAppFix.Apply(HarmonyInstance);
            GraffitiPatch.Apply(HarmonyInstance);
            RecipePinPatch.Apply(HarmonyInstance);
            SupplierWarehousePatch.Apply(HarmonyInstance);
            SupplierFleePatch.Apply(HarmonyInstance);
            SaveManagerPatch.Apply(HarmonyInstance);
            ActionListPatch.Apply(HarmonyInstance);
            GameProfilerPatches.Apply(HarmonyInstance);

            TimeManager.OnSleepEnd += OnSleepEnd;
            TimeManager.OnDayPass += OnDayPass;

            if (!ConfigSyncData.IsNetworkLibAvailable)
                OTCLog.Warning(OTCLog.Systems.Network, "SteamNetworkLib not installed — multiplayer sync disabled. " +
                    "Single-player works fine. Install SteamNetworkLib for co-op support.");

            OTCLog.Msg(OTCLog.Systems.Patch, "OverTheCounter Initialized.");

            ImmediateQuestWindowConfig.Register();
            MinimapOverlay.Register();
            RecipeOverlay.Register();
#if DEBUG
            DebugHelpers.Register();
#endif

            ExtractIcons();
            _notificationManager = new NotificationManager();
            _desperationManager = new DesperationManager();
            _drifterManager = new DrifterManager();
            _managerManager = new ManagerController();
            _customerManager = new CustomerManager();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (DependencyChecker.HasMissingDeps)
            {
                if (sceneName == "Menu")
                    DependencyChecker.ShowPopup();
                return;
            }
            OnSceneWasLoadedImpl(buildIndex, sceneName);
        }

        private static bool _meshVaultRegistered;

        private void OnSceneWasLoadedImpl(int buildIndex, string sceneName)
        {
            // Clear stale singletons on every scene transition so save data
            // from a previous save never bleeds into the next one.
            // S1API recreates these from the save file after the scene loads.
            StaticSaveData.ResetInstance();
            StaticThreadSaveData.ResetInstance();
            PropertySaveData.ResetInstance();
            VicSaveData.ResetInstance();
            BellaSaveData.ResetInstance();
            StaticIntroQuest.ResetInstance();
            StaticUpgrade1Quest.ResetInstance();
            StaticUpgrade2Quest.ResetInstance();
            VicIntroQuest.ResetInstance();
            BellaProtocolQuest.ResetInstance();
            StorefrontGrowthQuest.ResetInstance();
            Patches.BellaSummonPatch.Reset();

            // WORKAROUND: S1API bug — SaveableAutoRegistry never clears cached instances
            // between game sessions, causing [SaveableField] values and runtime state to
            // leak from one save into the next. ClearCache() exists but is never called.
            // See tools/s1api_saveable_bug.md for full analysis.
            // Remove this when S1API fixes the bug upstream.
            try
            {
                var registryType = typeof(S1API.Internal.Abstraction.Saveable).Assembly
                    .GetType("S1API.Saveables.SaveableAutoRegistry");
                var clearMethod = registryType?.GetMethod("ClearCache",
                    BindingFlags.Static | BindingFlags.NonPublic);
                clearMethod?.Invoke(null, null);
                OTCLog.Msg(OTCLog.Systems.Patch, "Cleared S1API SaveableAutoRegistry cache");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"Failed to clear SaveableAutoRegistry: {ex.Message}");
            }

            // Drifters + customers are transient - despawn on scene transitions (save/load)
            DrifterInstance.CleanupAll();
            CustomerInstance.CleanupAll();
            DrifterSpawner.ResetCache();

            // Clean up previous scene's managers — respawned from ManagerSaveData after load
            ManagerSaveData.ResetInstance();
            ManagerInstance.CleanupAll();
            ManagerSpawner.ResetCache();
            MugshotUtility.ResetSession();

            if (!GameObject.Find("OTC_MinimapController"))
            {
                var go = new GameObject("OTC_MinimapController");
                go.AddComponent<MinimapOverlay>();
                GameObject.DontDestroyOnLoad(go);
            }

            if (!GameObject.Find("OTC_RecipeOverlay"))
            {
                var go = new GameObject("OTC_RecipeOverlay");
                go.AddComponent<RecipeOverlay>();
                GameObject.DontDestroyOnLoad(go);
            }

#if DEBUG
            if (!GameObject.Find("DebugController"))
            {
                var go = new GameObject("DebugController");
                go.AddComponent<DebugHelpers>();
                GameObject.DontDestroyOnLoad(go);
            }

#endif
            // Permanent building cleanup
            Logic.Placement.CheckoutCounter.Cleanup();
            Logic.Placement.WestvilleShack.Cleanup();
            Logic.Placement.Dispensary.Cleanup();
            Logic.Placement.OTCWarehouse.Cleanup();
            Logic.Placement.OTCSupplierArea.Cleanup();
            Logic.Placement.CasinoDeadDrop.Cleanup();
            CheckoutProcess.ResetStatic();
            BuildingGridFactory.Cleanup();
            _loadHooked = false;
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (DependencyChecker.HasMissingDeps) return;
            OnSceneWasInitializedImpl(buildIndex, sceneName);
        }

        private void OnSceneWasInitializedImpl(int buildIndex, string sceneName)
        {
            if (sceneName == "Main")
            {
                MeshVault.MeshVaultAPI.Init();

                // Register OTC custom meshes + decals (only once — MeshVault persists across scenes)
                if (!_meshVaultRegistered)
                {
                    _meshVaultRegistered = true;
                    try
                    {
                        var meshBytes = S1MAPI.Utils.EmbeddedResourceLoader.LoadBytes(
                            "OverTheCounter.Resources.MeshDatabase.json",
                            System.Reflection.Assembly.GetExecutingAssembly());
                        if (meshBytes != null)
                            MeshVault.MeshVaultAPI.RegisterMeshes(
                                "otc", "OverTheCounter",
                                System.Text.Encoding.UTF8.GetString(meshBytes));
                    }
                    catch (Exception ex)
                    {
                        OTCLog.Warning(OTCLog.Systems.Patch, $"OTC mesh registration failed: {ex.Message}");
                    }

                    try
                    {
                        MeshVault.MeshVaultAPI.RegisterDecals(
                            "otc", "OverTheCounter",
                            System.Reflection.Assembly.GetExecutingAssembly(),
                            "OverTheCounter.Resources.MeshVaultDecals.");
                    }
                    catch (Exception ex)
                    {
                        OTCLog.Warning(OTCLog.Systems.Patch, $"OTC decal registration failed: {ex.Message}");
                    }
                }

                Logic.Placement.CheckoutCounter.Register();
                Logic.Placement.WestvilleShack.SpawnBuilding();
                Logic.Placement.Dispensary.SpawnBuilding();
                Logic.Placement.OTCWarehouse.Initialize();
                Logic.Placement.OTCSupplierArea.Initialize(Logic.Placement.OTCWarehouse.BuildingTransform);

                // Defer grid item spawning until after FishNet is ready
                // (CreateGridItem calls networkObject.Spawn which requires network initialized)
                HookLoadComplete();
            }
        }

        private static bool _loadHooked;

        /// <summary>
        /// Subscribes to LoadManager.onLoadComplete so we can spawn grid items
        /// after FishNet networking is initialized. Safe to call multiple times.
        /// </summary>
        private static void HookLoadComplete()
        {
            if (_loadHooked) return;
            try
            {
#if IL2CPP
                var lm = Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.Persistence.LoadManager>.Instance;
                if (lm != null)
                {
                    lm.onLoadComplete.RemoveListener((UnityEngine.Events.UnityAction)OnGameLoaded);
                    lm.onLoadComplete.AddListener((UnityEngine.Events.UnityAction)OnGameLoaded);
                    _loadHooked = true;
                    OTCLog.Msg(OTCLog.Systems.Patch, "Hooked LoadManager.onLoadComplete");
                }
                else
                    OTCLog.Warning(OTCLog.Systems.Patch, "LoadManager.Instance is null — cannot hook onLoadComplete");
#else
                var lm = ScheduleOne.Persistence.LoadManager.Instance;
                if (lm != null)
                {
                    lm.onLoadComplete.RemoveListener(OnGameLoaded);
                    lm.onLoadComplete.AddListener(OnGameLoaded);
                    _loadHooked = true;
                    OTCLog.Msg(OTCLog.Systems.Patch, "Hooked LoadManager.onLoadComplete");
                }
                else
                    OTCLog.Warning(OTCLog.Systems.Patch, "LoadManager.Instance is null — cannot hook onLoadComplete");
#endif
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Failed to hook LoadManager.onLoadComplete: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when the game save finishes loading. Restores placed grid items
        /// (checkout counter, storage, etc.) now that FishNet networking is ready.
        /// </summary>
        private static void OnGameLoaded()
        {
            try
            {
                Logic.Placement.WestvilleShack.ClearTerrain();
                Logic.Placement.Dispensary.ClearTerrain();
                Logic.Placement.OTCWarehouse.ClearTerrain();

                Logic.Placement.WestvilleShack.SpawnNetworkedObjects();
                Logic.Placement.Dispensary.SpawnNetworkedObjects();
                Logic.Placement.OTCWarehouse.SpawnNetworkedObjects();

                Logic.Placement.CheckoutCounter.AddToShop();
                Logic.Placement.CasinoDeadDrop.Initialize();
                CheckoutProcess.InitP2P();

                // Suppress per-item navigation rebuilds during batch restore —
                // one rebuild per building at the end instead of per item.
                Logic.Placement.BuildingGridFactory.SuppressNavigationRebuild = true;

                // Restore placed items for all OTC grids
                if (PropertySaveData.Instance != null)
                {
                    foreach (var kvp in Logic.Placement.BuildingGridFactory.GridRegistry)
                        PropertySaveData.Instance.RestorePlacedItems(kvp.Value.BuildingId, kvp.Key);
                }
                else
                {
                    // No save data — spawn default checkout counter on shack grid
                    var shackGrid = Logic.Placement.WestvilleShack.ShackGrid;
                    if (shackGrid != null)
                        Logic.Placement.CheckoutCounter.SpawnOnGrid(shackGrid);
                }

                Logic.Placement.BuildingGridFactory.SuppressNavigationRebuild = false;

                // Rebuild pathfinding once per building now that terrain is cleared,
                // MeshVault furniture is placed, and grid items are restored.
                Logic.Placement.WestvilleShack.RebuildNavigation();
                Logic.Placement.Dispensary.RebuildNavigation();
                Logic.Placement.OTCWarehouse.RebuildNavigation();
            }
            catch (Exception ex)
            {
                Logic.Placement.BuildingGridFactory.SuppressNavigationRebuild = false;
                OTCLog.Error(OTCLog.Systems.General, $"OnGameLoaded restore failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public override void OnLateUpdate()
        {
            if (DependencyChecker.HasMissingDeps) return;
            OnLateUpdateImpl();
        }

        private void OnLateUpdateImpl()
        {
            PerfTracker.BeginFrame();
            try
            {
                // Initialize lobby data callbacks on first tick (Steam is ready by now).
                // Must run on both host and client, independent of Saveable lifecycle.
                PerfTracker.Begin("NetworkInit");
                ConfigSyncData.EnsureNetworkReady();
                PerfTracker.End("NetworkInit");

                // Process incoming SyncVar messages (both host and client).
                PerfTracker.Begin("SyncMessages");
                ConfigSyncData.ProcessMessages();
                PerfTracker.End("SyncMessages");

                PerfTracker.Begin("SaveDataTicks");
                _notificationManager.ProcessContractState();
                VicSaveData.Instance?.Tick();
                StaticSaveData.Instance?.Tick();
                BellaSaveData.Instance?.Tick();
                ManagerSaveData.Instance?.Tick();
                PerfTracker.End("SaveDataTicks");

                // Retry pending NPC adoptions on client (FishNet timing)
                PerfTracker.Begin("AdoptionRetries");
                _drifterManager?.RetryPendingAdoptions();
                _customerManager?.RetryPendingAdoptions();
                ManagerInstance.RetryPendingAdoptions();
                PerfTracker.End("AdoptionRetries");

                PerfTracker.Begin("ManagerAI");

                // Immediate wage payment when cash is deposited (host only)
                PerfTracker.Begin("ManagerAI.Wages");
                _managerManager?.CheckImmediateWages();
                PerfTracker.End("ManagerAI.Wages");

                // Resume interrupted manager walks (e.g. after dialogue)
                PerfTracker.Begin("ManagerAI.EnsureMoving");
                foreach (var mgr in ManagerInstance.Active.Values)
                    mgr.EnsureMoving();
                PerfTracker.End("ManagerAI.EnsureMoving");

                // Tick supply + distribution run behaviours (host only)
                PerfTracker.Begin("ManagerAI.SupplyDistribution");
                if (NetworkHelper.IsHost)
                {
                    foreach (var mgr in ManagerInstance.Active.Values)
                    {
                        mgr.SupplyBehaviour?.Tick();
                        mgr.DistributionBehaviour?.Tick();
                    }
                }
                PerfTracker.End("ManagerAI.SupplyDistribution");

                PerfTracker.End("ManagerAI");

                PerfTracker.Begin("NetworkPublish");
                if (NetworkHelper.IsHost)
                {
                    // Publish pending text messages to client via dedicated message SyncVars
                    if (ManagerInstance.HasPendingMessages)
                        ConfigSyncData.Instance?.PublishManagerMessages();
                    if (DrifterManager.HasPendingDrifterMessages)
                        ConfigSyncData.Instance?.PublishDrifterMessages();

                    // Publish customer state changes to clients
                    _customerManager?.PublishIfNeeded();

                    // Publish manager state changes (State/PaidForToday) to per-slot SyncVars
                    if (ManagerInstance.StatePublishNeeded)
                    {
                        ManagerInstance.StatePublishNeeded = false;
                        ConfigSyncData.Instance?.PublishManagerState();
                    }
                }
                PerfTracker.End("NetworkPublish");

                // Client: poll for FishNet-replicated doors arriving in buildings
                PerfTracker.Begin("ClientDoorSetup");
                if (!NetworkHelper.IsHost)
                {
                    Logic.Placement.WestvilleShack.TickClientDoorSetup();
                    Logic.Placement.Dispensary.TickClientDoorSetup();
                }
                PerfTracker.End("ClientDoorSetup");

                PerfTracker.Begin("Checkout");
                // Interactive checkout process (camera, clicks, payment)
                CheckoutProcess.Instance?.Tick();
                CheckoutProcess.TryStartCheckout(); // Both host and client (internal routing)
                CheckoutProcess.PollLockGrant();    // Client: check for lock grant from host

                // Cash register collection — both host and client (client routes through host)
                if (CheckoutProcess.Instance == null)
                    CheckoutCounter.TryCollectRegister();

                // Right-click product pickup while checkout is paused
                if (CheckoutProcess.Instance?.IsPaused == true)
                    CheckoutProcess.TryPickupCounterProduct();
                PerfTracker.End("Checkout");

                // Periodic POS display refresh (2-second throttle for availability updates)
                PerfTracker.Begin("ScreenTicks");
                foreach (var counter in Logic.Placement.CheckoutCounter.AllCounters)
                    counter.Screen?.Tick();
                PerfTracker.End("ScreenTicks");

                // Update drifter quest timers on client (OnTimeTick is host-only)
                PerfTracker.Begin("QuestTicks");
                _drifterManager?.ClientQuestTick();

                // Storefront Growth quest polling (throttled internally)
                Quests.StorefrontGrowthQuest.Instance?.Tick();
                PerfTracker.End("QuestTicks");

            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Error in OnLateUpdate: {ex.Message}\n{ex.StackTrace}");
            }
            PerfTracker.EndFrame();
        }

        /// <summary>
        /// After sleep, the schedule system's warpIfSkipped fires before this callback,
        /// so Static has been warped to position but lost facing. WarpToSpawn() re-applies
        /// both. Bella's schedule was removed so she can't be warped outside, but
        /// ReInjectIntoBuilding() re-disables her schedule as a safety net.
        /// </summary>
        private static void OnSleepEnd(int minutesSkipped)
        {
            if (!NetworkHelper.IsHost) return;
#if IL2CPP
            var lm = Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.Persistence.LoadManager>.Instance;
#else
            var lm = ScheduleOne.Persistence.LoadManager.Instance;
#endif
            if (lm == null || !lm.IsGameLoaded) return;
            StaticNPC.Instance?.WarpToSpawn();
            BellaNPC.Instance?.ReInjectIntoBuilding();
        }

        public override void OnDeinitializeMelon()
        {
            if (DependencyChecker.HasMissingDeps) return;
            OnDeinitializeMelonImpl();
        }

        /// <summary>Daily property maintenance: inventory snapshots and sales log trimming.</summary>
        private static void OnDayPass()
        {
            if (!NetworkHelper.IsHost) return;

            var psd = PropertySaveData.Instance;
            if (psd == null) return;

            // Snapshot total inventory for the overview chart
            int total = 0;
            if (psd.IsPropertyOwned(PropertySaveData.ShackId))
                total += PropertyInventory.GetTotalProductCount(WestvilleShack.ShackGrid);
            if (psd.IsPropertyOwned(PropertySaveData.DispensaryId))
                total += PropertyInventory.GetTotalProductCount(Dispensary.DispensaryGrid);
            psd.RecordInventorySnapshot(TimeManager.ElapsedDays, total);

            // Trim sales log to last 7 days
            psd.TrimSalesLog(TimeManager.ElapsedDays);
        }

        private void OnDeinitializeMelonImpl()
        {
            PerfTracker.WriteReport();
            TimeManager.OnSleepEnd -= OnSleepEnd;
            TimeManager.OnDayPass -= OnDayPass;
            ConfigSyncData.Cleanup();
            _notificationManager?.Cleanup();
            _desperationManager?.Cleanup();
            _drifterManager?.Cleanup();
            _managerManager?.Cleanup();
            _customerManager?.Cleanup();
        }

        /// <summary>
        /// Extracts embedded icons to the S1API Icons folder for phone app usage.
        /// </summary>
        private void ExtractIcons()
        {
            string iconDir = Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons");
            if (!Directory.Exists(iconDir))
            {
                Directory.CreateDirectory(iconDir);
            }

            ExtractResource(iconDir, "CustomersIcon.png");
            ExtractResource(iconDir, "DrifterQuestIcon.png");
            ExtractResource(iconDir, "DrifterProfileIcon.png");
            ExtractResource(iconDir, "RinseCycle.png");
            ExtractResource(iconDir, "CrimeWareQuest.png");
            ExtractResource(iconDir, "ExecutivePrivilege.png");
            ExtractResource(iconDir, "ManagerIcon.png");
            ExtractResource(iconDir, "CheckoutCounter.png");
        }

        private void ExtractResource(string directory, string fileName)
        {
            string targetPath = Path.Combine(directory, fileName);

            if (!File.Exists(targetPath))
            {
                OTCLog.Msg(OTCLog.Systems.Patch, $"Extracting {fileName}...");
                string resourceName = $"OverTheCounter.Resources.{fileName}";

                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (FileStream fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                        {
                            stream.CopyTo(fileStream);
                        }
                        OTCLog.Msg(OTCLog.Systems.Patch, $"{fileName} extracted successfully.");
                    }
                    else
                    {
                        OTCLog.Error(OTCLog.Systems.Patch, $"Could not find embedded resource '{resourceName}'.");
                    }
                }
            }
        }
    }
}
