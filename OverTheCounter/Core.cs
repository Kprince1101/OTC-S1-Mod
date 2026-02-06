using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using MelonLoader.Utils;
using OverTheCounter.Apps;
using OverTheCounter.Logic;
using OverTheCounter.NPCs;
using OverTheCounter.Patches;
using OverTheCounter.SaveData;
using S1API.PhoneApp;
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

[assembly: MelonInfo(typeof(OverTheCounter.Core), "OverTheCounter", "1.0.5", "hdlmrell", null)]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace OverTheCounter
{
    public class Core : MelonMod
    {
        private NotificationManager _notificationManager;
        private DesperationManager _desperationManager;
        private DrifterManager _drifterManager;

        public override void OnInitializeMelon()
        {
            Config.Initialize();
            ConfigSyncPatch.TryApply(HarmonyInstance);

            LoggerInstance.Msg("OverTheCounter Initialized.");

            ImmediateQuestWindowConfig.Register();
#if DEBUG
            DebugHelpers.Register();
#endif

            ExtractIcons();
            _notificationManager = new NotificationManager(LoggerInstance);
            _desperationManager = new DesperationManager(LoggerInstance);
            _drifterManager = new DrifterManager(LoggerInstance);
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // Clear stale singletons on every scene transition so save data
            // from a previous save never bleeds into the next one.
            // S1API recreates these from the save file after the scene loads.
            StaticSaveData.ResetInstance();
            VicSaveData.ResetInstance();
            ContactsAppFix.Reset();

            // Drifters are transient - despawn on scene transitions (save/load)
            DrifterInstance.CleanupAll();
            DrifterSpawner.ResetCache();

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

                // Show/hide OTC phone icon based on subscription state
                CustomersApp.Instance?.UpdateIconVisibility();

                _notificationManager.ProcessContractState();
                VicSaveData.Instance?.Tick();
                StaticSaveData.Instance?.Tick();

                ContactsAppFix.Tick();
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
            ExtractResource(iconDir, "RinseCycle.png");
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
