using MelonLoader;
using OverTheCounter.Logic;
using UnityEngine;
using UnityEngine.UI;
using Il2CppTMPro;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.ItemFramework;
using System.Collections.Generic;
using System.Linq;

[assembly: MelonInfo(typeof(OverTheCounter.Core), "OverTheCounter", "1.0.0", "mrell", null)]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace OverTheCounter
{
    /// <summary>
    /// The main entry point for the MelonLoader Mod.
    /// This class handles the initialization and the game loop hooks.
    /// </summary>
    public class Core : MelonMod
    {
        // Reference to our logic manager
        private NotificationManager _notificationManager;

        /// <summary>
        /// Called once when the mod is loaded.
        /// </summary>
        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("OverTheCounter Initialized.");

            // Initialize our logic manager
            _notificationManager = new NotificationManager(LoggerInstance);
        }

        /// <summary>
        /// Called every frame, after the standard Update loop.
        /// We use LateUpdate to ensure the game has finished its own processing 
        /// of the UI/Contracts before we modify them.
        /// </summary>
        public override void OnLateUpdate()
        {
            try
            {
                // Delegate the heavy lifting to the manager
                _notificationManager.ProcessContractState();
            }
            catch (System.Exception ex)
            {
                // Catching errors here prevents the entire mod loop from crashing the game
                LoggerInstance.Error($"Error in OnLateUpdate: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Called when the mod is unloaded or the game closes.
        /// </summary>
        public override void OnDeinitializeMelon()
        {
            // Ensure we clean up any UI we created so we don't leave ghosts behind
            _notificationManager?.Cleanup();
        }
    }
}