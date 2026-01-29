using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using MelonLoader.Utils;
using OverTheCounter.Apps;
using OverTheCounter.Logic;
using OverTheCounter.SaveData;
using S1API.PhoneApp;
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

[assembly: MelonInfo(typeof(OverTheCounter.Core), "OverTheCounter", "1.0.0", "mrell", null)]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace OverTheCounter
{
    public class Core : MelonMod
    {
        private NotificationManager _notificationManager;
        private DesperationManager _desperationManager;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("OverTheCounter Initialized.");

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
                VicSaveData.Instance?.Tick();
            }
            catch (Exception ex)
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
