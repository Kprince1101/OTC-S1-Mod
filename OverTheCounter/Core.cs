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

[assembly: MelonInfo(typeof(OverTheCounter.Core), "OverTheCounter", "1.3.0", "hdlmrell", null)]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: MelonOptionalDependencies("SteamNetworkLib")]

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
            Config.Initialize();
            SaveManagerPatch.Apply(HarmonyInstance);
            NpcTypeDiscoveryPatch.Apply(HarmonyInstance);
            ConfigSyncPatch.TryApply(HarmonyInstance);
            DoorSyncPatch.TryApply(HarmonyInstance);
            ManagerClipboardPatch.Apply(HarmonyInstance);
            try
            {
                BuildingPlacementPatch.Apply(HarmonyInstance);
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"BuildingPlacementPatch.Apply failed: {ex}");
            }

            try
            {
                ConfigReplicatorPatch.Apply(HarmonyInstance);
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"ConfigReplicatorPatch.Apply failed: {ex}");
            }

            ContactsAppFix.Apply(HarmonyInstance);

            if (!ConfigSyncData.IsNetworkLibAvailable)
                LoggerInstance.Warning("SteamNetworkLib not installed — multiplayer sync disabled. " +
                    "Single-player works fine. Install SteamNetworkLib for co-op support.");

            LoggerInstance.Msg("OverTheCounter Initialized.");

            ImmediateQuestWindowConfig.Register();
            MinimapOverlay.Register();
#if DEBUG
            DebugHelpers.Register();
#endif

            ExtractIcons();
            _notificationManager = new NotificationManager(LoggerInstance);
            _desperationManager = new DesperationManager(LoggerInstance);
            _drifterManager = new DrifterManager(LoggerInstance);
            _managerManager = new ManagerController(LoggerInstance);
            _customerManager = new CustomerManager(LoggerInstance);
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
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
            Patches.BellaSummonPatch.Reset();

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
            CheckoutProcess.ResetStatic();
            BuildingGridFactory.Cleanup();
            _loadHooked = false;
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName == "Main")
            {
                Logic.Placement.CheckoutCounter.Register();
                Logic.Placement.WestvilleShack.SpawnBuilding();

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
                }
                else
                    MelonLoader.MelonLogger.Warning("[OTC] LoadManager.Instance is null — cannot hook onLoadComplete");
#else
                var lm = ScheduleOne.Persistence.LoadManager.Instance;
                if (lm != null)
                {
                    lm.onLoadComplete.RemoveListener(OnGameLoaded);
                    lm.onLoadComplete.AddListener(OnGameLoaded);
                    _loadHooked = true;
                }
                else
                    MelonLoader.MelonLogger.Warning("[OTC] LoadManager.Instance is null — cannot hook onLoadComplete");
#endif
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Error($"[OTC] Failed to hook LoadManager.onLoadComplete: {ex.Message}");
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
                Logic.Placement.WestvilleShack.SpawnNetworkedObjects();

                var grid = Logic.Placement.WestvilleShack.ShackGrid;
                if (grid == null)
                {
                    MelonLoader.MelonLogger.Warning("[OTC] ShackGrid is null — cannot restore items");
                    return;
                }

                if (PropertySaveData.Instance != null)
                    PropertySaveData.Instance.RestorePlacedItems(PropertySaveData.ShackId);
                else
                    Logic.Placement.CheckoutCounter.SpawnOnGrid(grid);
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Error($"[OTC] OnGameLoaded restore failed: {ex.Message}");
            }
        }

        public override void OnLateUpdate()
        {
            try
            {
                // Initialize lobby data callbacks on first tick (Steam is ready by now).
                // Must run on both host and client, independent of Saveable lifecycle.
                ConfigSyncData.EnsureNetworkReady();

                // Process incoming SyncVar messages (both host and client).
                ConfigSyncData.ProcessMessages();

                _notificationManager.ProcessContractState();
                VicSaveData.Instance?.Tick();
                StaticSaveData.Instance?.Tick();
                BellaSaveData.Instance?.Tick();
                ManagerSaveData.Instance?.Tick();

                // Retry pending NPC adoptions on client (FishNet timing)
                _drifterManager?.RetryPendingAdoptions();
                _customerManager?.RetryPendingAdoptions();
                ManagerInstance.RetryPendingAdoptions();

                // Immediate wage payment when cash is deposited (host only)
                _managerManager?.CheckImmediateWages();

                // Resume interrupted manager walks (e.g. after dialogue)
                foreach (var mgr in ManagerInstance.Active.Values)
                    mgr.EnsureMoving();

                // Tick supply + distribution run behaviours (host only)
                if (NetworkHelper.IsHost)
                {
                    foreach (var mgr in ManagerInstance.Active.Values)
                    {
                        mgr.SupplyBehaviour?.Tick();
                        mgr.DistributionBehaviour?.Tick();
                    }

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

                // Periodic POS display refresh (2-second throttle for availability updates)
                Logic.Placement.ComputerScreen.Tick();

                // Update drifter quest timers on client (OnTimeTick is host-only)
                _drifterManager?.ClientQuestTick();

            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Error in OnLateUpdate: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public override void OnDeinitializeMelon()
        {
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
        }

        private void ExtractResource(string directory, string fileName)
        {
            string targetPath = Path.Combine(directory, fileName);

            if (!File.Exists(targetPath))
            {
                string resourceName = $"OverTheCounter.Resources.{fileName}";

                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (FileStream fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                        {
                            stream.CopyTo(fileStream);
                        }
                    }
                    else
                    {
                        LoggerInstance.Error($"Could not find embedded resource '{resourceName}'.");
                    }
                }
            }
        }
    }
}
