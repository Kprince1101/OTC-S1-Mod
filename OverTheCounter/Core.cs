using MelonLoader;
using MelonLoader.Utils;
using OverTheCounter.Apps;
using OverTheCounter.Logic;
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
#endif

[assembly: MelonInfo(typeof(OverTheCounter.Core), "OverTheCounter", "1.5.2", "hdlmrell", null)]
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

        public override void OnInitializeMelon()
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
            ManagerClipboardPatch.Apply(HarmonyInstance);
            ContactsAppFix.Apply(HarmonyInstance);
            GraffitiPatch.Apply(HarmonyInstance);
            RecipePinPatch.Apply(HarmonyInstance);
            TimeManager.OnSleepEnd += OnSleepEnd;

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

            // Drifters are transient - despawn on scene transitions (save/load)
            DrifterInstance.CleanupAll();
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

                    // Publish manager state changes (State/PaidForToday) to per-slot SyncVars
                    if (ManagerInstance.StatePublishNeeded)
                    {
                        ManagerInstance.StatePublishNeeded = false;
                        ConfigSyncData.Instance?.PublishManagerState();
                    }
                }

                // Update drifter quest timers on client (OnTimeTick is host-only)
                _drifterManager?.ClientQuestTick();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Error in OnLateUpdate: {ex.Message}\n{ex.StackTrace}");
            }
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
            StaticNPC.Instance?.WarpToSpawn();
            BellaNPC.Instance?.ReInjectIntoBuilding();
        }

        public override void OnDeinitializeMelon()
        {
            TimeManager.OnSleepEnd -= OnSleepEnd;
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
