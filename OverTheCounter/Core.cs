using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using MelonLoader.Utils;
using OverTheCounter.Apps;
using OverTheCounter.Logic;
using S1API.PhoneApp;
using System.IO;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

[assembly: MelonInfo(typeof(OverTheCounter.Core), "OverTheCounter", "1.0.0", "mrell", null)]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace OverTheCounter
{
    public class Core : MelonMod
    {
        private NotificationManager _notificationManager;
        private DesperationManager _desperationManager;
        private CustomersApp _customersApp;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("OverTheCounter Initialized.");

            // Register custom IL2CPP types before using them
            ImmediateQuestWindowConfig.Register();

            ExtractIcons();
            _notificationManager = new NotificationManager(LoggerInstance);
            _desperationManager = new DesperationManager(LoggerInstance);
        }

        public override void OnLateUpdate()
        {
            try
            {
                _notificationManager.ProcessContractState();
            }
            catch (System.Exception ex)
            {
                LoggerInstance.Error($"Error in OnLateUpdate: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public override void OnDeinitializeMelon()
        {
            _notificationManager?.Cleanup();
            _desperationManager?.Cleanup();
        }

        /// <summary>
        /// Extracts the embedded icon file to the S1API Icons folder so the phone can read it.
        /// </summary>
        private void ExtractIcons()
        {
            string iconDir = Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons");
            if (!Directory.Exists(iconDir))
            {
                Directory.CreateDirectory(iconDir);
            }

            string targetPath = Path.Combine(iconDir, "CustomersIcon.png");

            if (!File.Exists(targetPath))
            {
                LoggerInstance.Msg("Extracting app icon...");
                string resourceName = "OverTheCounter.Resources.CustomersIcon.png";

                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (FileStream fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                        {
                            stream.CopyTo(fileStream);
                        }
                        LoggerInstance.Msg("Icon extracted successfully.");
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