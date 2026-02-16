using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using MelonLoader.Utils;
using OverTheCounter.Apps;
using OverTheCounter.Logic;
using OverTheCounter.NPCs;
using OverTheCounter.Patches;
using OverTheCounter.Quests;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.PhoneApp;
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

[assembly: MelonInfo(typeof(OverTheCounter.Core), "OverTheCounter", "1.2.2", "hdlmrell", null)]
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

        public override void OnInitializeMelon()
        {
            Config.Initialize();
            NpcTypeDiscoveryPatch.Apply(HarmonyInstance);
            ConfigSyncPatch.TryApply(HarmonyInstance);
            ManagerClipboardPatch.Apply(HarmonyInstance);

            if (!ConfigSyncData.IsNetworkLibAvailable)
                LoggerInstance.Warning("SteamNetworkLib not installed — multiplayer sync disabled. " +
                    "Single-player works fine. Install SteamNetworkLib for co-op support.");

            LoggerInstance.Msg("OverTheCounter Initialized.");

            ImmediateQuestWindowConfig.Register();
#if DEBUG
            DebugHelpers.Register();
#endif

            ExtractIcons();
            _notificationManager = new NotificationManager(LoggerInstance);
            _desperationManager = new DesperationManager(LoggerInstance);
            _drifterManager = new DrifterManager(LoggerInstance);
            _managerManager = new ManagerController(LoggerInstance);
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // Clear stale singletons on every scene transition so save data
            // from a previous save never bleeds into the next one.
            // S1API recreates these from the save file after the scene loads.
            StaticSaveData.ResetInstance();
            VicSaveData.ResetInstance();
            BellaSaveData.ResetInstance();
            StaticIntroQuest.ResetInstance();
            StaticUpgrade1Quest.ResetInstance();
            StaticUpgrade2Quest.ResetInstance();
            VicIntroQuest.ResetInstance();
            BellaProtocolQuest.ResetInstance();
            Patches.BellaSummonPatch.Reset();
            ContactsAppFix.Reset();

            // Drifters are transient - despawn on scene transitions (save/load)
            DrifterInstance.CleanupAll();
            DrifterSpawner.ResetCache();

            // Clean up previous scene's managers — respawned from ManagerSaveData after load
            ManagerSaveData.ResetInstance();
            ManagerInstance.CleanupAll();
            ManagerSpawner.ResetCache();
            MugshotUtility.ResetSession();

#if DEBUG
            if (!GameObject.Find("DebugController"))
            {
                var go = new GameObject("DebugController");
                go.AddComponent<DebugHelpers>();
                GameObject.DontDestroyOnLoad(go);
            }
#endif
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

                ContactsAppFix.Tick();

                // Retry pending NPC adoptions on client (FishNet timing)
                _drifterManager?.RetryPendingAdoptions();
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

                    // Publish pending text messages to client via dedicated message SyncVar
                    if (ManagerInstance.HasPendingMessages)
                        ConfigSyncData.Instance?.PublishManagerMessages();
                }

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
                LoggerInstance.Msg($"Extracting {fileName}...");
                string resourceName = $"OverTheCounter.Resources.{fileName}";

                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (FileStream fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                        {
                            stream.CopyTo(fileStream);
                        }
                        LoggerInstance.Msg($"{fileName} extracted successfully.");
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
