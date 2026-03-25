using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using System.Collections.Generic;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Money;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Money;
#endif

namespace OverTheCounter.Apps
{
    public partial class GreenTabApp
    {
        /// <summary>Returns all building IDs that currently have at least one counter.</summary>
        private static List<string> GetBuildingsWithCounters()
        {
            var seen = new HashSet<string>();
            var result = new List<string>();
            foreach (var counter in CheckoutCounter.AllCounters)
            {
                var bid = counter.BuildingId;
                if (bid != null && seen.Add(bid))
                    result.Add(bid);
            }
            return result;
        }

        /// <summary>Returns the first counter in the selected building, or null.</summary>
        private CheckoutCounterInstance GetSelectedCounter()
        {
            foreach (var counter in CheckoutCounter.AllCounters)
            {
                if (counter.BuildingId == _selectedBuildingId)
                    return counter;
            }
            // Fallback: any counter
            return CheckoutCounter.AllCounters.Count > 0 ? CheckoutCounter.AllCounters[0] : null;
        }

        private static string GetBuildingDisplayName(string buildingId) =>
            BuildingDisplayNames.TryGetValue(buildingId, out var name) ? name : buildingId ?? "Unknown";

        private static float GetOnlineBalance()
        {
            try
            {
                return NetworkSingleton<MoneyManager>.Instance?.onlineBalance ?? 0f;
            }
            catch
            {
                return 0f;
            }
        }
    }
}
