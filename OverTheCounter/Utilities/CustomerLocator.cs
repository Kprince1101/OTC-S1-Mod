using HarmonyLib;
using OverTheCounter.Apps;
using OverTheCounter.Logic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.UI.Phone.Map;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Economy;
using ScheduleOne.Map;
using ScheduleOne.NPCs;
using ScheduleOne.UI.Phone.Map;
#endif

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
                OTCLog.Warning(OTCLog.Systems.NPC, "Cannot pin null customer.");
                return;
            }

            // Instantiate the POI if it doesn't exist yet
            if (customer.GetPotentialCustomerPoI() == null)
            {
                var npcManager = NetworkSingleton<NPCManager>.Instance;

                if (npcManager == null || npcManager.PotentialCustomerPoIPrefab == null)
                {
                    OTCLog.Error(OTCLog.Systems.NPC, "NPCManager or POI Prefab not found. Cannot create map marker.");
                    return;
                }

                var poiGameObject = UnityEngine.Object.Instantiate(npcManager.PotentialCustomerPoIPrefab, customer.transform);
                var poi = poiGameObject.GetComponent<NPCPoI>();

                if (poi != null)
                {
                    customer.SetPotentialCustomerPoI(poi);
                }
            }

            // Configure and enable the POI (re-enables if previously disabled)
            if (customer.GetPotentialCustomerPoI() != null)
            {
                customer.GetPotentialCustomerPoI().SetMainText(customer.NPC.fullName);
                customer.GetPotentialCustomerPoI().SetNPC(customer.NPC);
                customer.GetPotentialCustomerPoI().enabled = true;
            }

            // Open the Map App and focus on the marker
            if (customer.GetPotentialCustomerPoI() != null)
            {
                LastCustomerShownOnMap = customer;

                // Close the CustomersApp first
                if (CustomersApp.Instance != null)
                {
                    CustomersApp.Instance.CloseApp();
                }

                var mapApp = PlayerSingleton<MapApp>.Instance;
                if (mapApp != null && customer.GetPotentialCustomerPoI().UI != null)
                {
                    mapApp.FocusPosition(customer.GetPotentialCustomerPoI().UI.anchoredPosition);
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
                OTCLog.Warning(OTCLog.Systems.Manager, "Cannot locate null manager.");
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
                if (LastCustomerShownOnMap != null && LastCustomerShownOnMap.GetPotentialCustomerPoI() != null)
                {
                    LastCustomerShownOnMap.GetPotentialCustomerPoI().enabled = false;
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
