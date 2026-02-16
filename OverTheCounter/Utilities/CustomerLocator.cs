using HarmonyLib;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.UI.Phone.Map;
using MelonLoader;
using OverTheCounter.Apps;
using OverTheCounter.Logic;
using UnityEngine;

namespace OverTheCounter.Utilities
{
    public static class CustomerLocator
    {
        // Reference to the last tracked customer to allow for cleanup
        public static Customer LastCustomerShownOnMap = null;

        /// <summary>
        /// Adapts the EnableNPCPoI functionality to find a customer on the map.
        /// </summary>
        public static void PinCustomerToMap(Customer customer)
        {
            if (customer == null || customer.NPC == null)
            {
                MelonLogger.Warning("Cannot pin null customer.");
                return;
            }

            // Instantiate the POI if it doesn't exist yet
            if (customer.potentialCustomerPoI == null)
            {
                var npcManager = NetworkSingleton<NPCManager>.Instance;

                if (npcManager == null || npcManager.PotentialCustomerPoIPrefab == null)
                {
                    MelonLogger.Error("NPCManager or POI Prefab not found. Cannot create map marker.");
                    return;
                }

                var poiGameObject = UnityEngine.Object.Instantiate(npcManager.PotentialCustomerPoIPrefab, customer.transform);
                var poi = poiGameObject.GetComponent<NPCPoI>();

                if (poi != null)
                {
                    customer.potentialCustomerPoI = poi;
                }
            }

            // Configure and enable the POI (re-enables if previously disabled)
            if (customer.potentialCustomerPoI != null)
            {
                customer.potentialCustomerPoI.SetMainText(customer.NPC.fullName);
                customer.potentialCustomerPoI.SetNPC(customer.NPC);
                customer.potentialCustomerPoI.enabled = true;
            }

            // Open the Map App and focus on the marker
            if (customer.potentialCustomerPoI != null)
            {
                LastCustomerShownOnMap = customer;

                // Close the CustomersApp first
                if (CustomersApp.Instance != null)
                {
                    CustomersApp.Instance.CloseApp();
                }

                var mapApp = PlayerSingleton<MapApp>.Instance;
                if (mapApp != null && customer.potentialCustomerPoI.UI != null)
                {
                    mapApp.FocusPosition(customer.potentialCustomerPoI.UI.anchoredPosition);
                    mapApp.SkipFocusPlayer = true;
                    mapApp.SetOpen(true);
                }
            }
        }

        /// <summary>
        /// Opens the map focused on the manager's world position.
        /// Managers already have permanent POIs on the map, so no temporary POI is created.
        /// </summary>
        public static void PinManagerToMap(ManagerInstance mgr)
        {
            if (mgr?.GameNpc == null)
            {
                MelonLogger.Warning("Cannot locate null manager.");
                return;
            }

            var mapPosUtil = Singleton<MapPositionUtility>.Instance;
            var mapApp = PlayerSingleton<MapApp>.Instance;
            if (mapPosUtil == null || mapApp == null) return;

            var mapPos = mapPosUtil.GetMapPosition(mgr.GameNpc.transform.position);

            if (CustomersApp.Instance != null)
                CustomersApp.Instance.CloseApp();

            mapApp.FocusPosition(mapPos);
            mapApp.SkipFocusPlayer = true;
            mapApp.SetOpen(true);
        }

        /// <summary>
        /// Hides the customer marker and resets map focus when the map is closed.
        /// </summary>
        [HarmonyPatch(typeof(MapApp), "SetOpen")]
        public static class ClearCustomerOnMapClosePatch
        {
            public static void Prefix(bool open)
            {
                if (open) return;

                // Cleanup customer POI
                if (LastCustomerShownOnMap != null && LastCustomerShownOnMap.potentialCustomerPoI != null)
                {
                    LastCustomerShownOnMap.potentialCustomerPoI.enabled = false;
                    LastCustomerShownOnMap = null;
                }

                // Reset focus to player for next normal map open
                if (PlayerSingleton<MapApp>.Instance != null)
                {
                    PlayerSingleton<MapApp>.Instance.SkipFocusPlayer = false;
                }
            }
        }
    }
}
