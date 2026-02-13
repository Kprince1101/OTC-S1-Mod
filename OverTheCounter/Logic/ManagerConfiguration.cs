using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.ObjectScripts;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Configuration data for a Manager NPC: locker + supply storage + 3 distribution routes.
    /// Serializes container assignments via BuildableItem GUIDs for save/sync.
    /// </summary>
    public class ManagerConfiguration
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("ManagerConfig");

        /// <summary>
        /// The locker entity where the manager draws wages from.
        /// </summary>
        public PlaceableStorageEntity Locker { get; set; }

        /// <summary>
        /// The storage entity the manager restocks from (purchases items into).
        /// </summary>
        public PlaceableStorageEntity SupplyStorage { get; set; }

        /// <summary>
        /// Up to 5 item IDs the manager should keep in stock at the supply storage.
        /// Uses ItemDefinition.ID strings (e.g. "cuke", "pseudoephedrine").
        /// Fixed 5-slot array; null/empty = empty slot.
        /// </summary>
        public string[] StockedItemIds { get; } = new string[5];

        /// <summary>
        /// Max stock threshold for each item slot (quantity to keep in stock).
        /// Fixed 5-slot array; default 20. Range 20-100 in increments of 20.
        /// </summary>
        public int[] StockedThresholds { get; } = new int[5] { 20, 20, 20, 20, 20 };

        /// <summary>
        /// Up to 3 distribution routes (source container → destination container).
        /// </summary>
        public DistributionRoute[] Routes { get; } = new DistributionRoute[3]
        {
            new DistributionRoute(),
            new DistributionRoute(),
            new DistributionRoute()
        };

        public class DistributionRoute
        {
            public PlaceableStorageEntity Source { get; set; }
            public PlaceableStorageEntity Destination { get; set; }

            public bool IsConfigured => Source != null && Destination != null;
            public bool IsEmpty => Source == null && Destination == null;
        }

        /// <summary>
        /// Validates that a container can be assigned to a specific slot within a route.
        /// Same container cannot be both source AND destination within the same route.
        /// Container can be reused across different routes or as supply + route endpoint.
        /// </summary>
        public bool ValidateAssignment(PlaceableStorageEntity container, int routeIndex, bool isSource, out string reason)
        {
            if (container == null)
            {
                reason = "";
                return true; // clearing is always valid
            }

            if (routeIndex >= 0 && routeIndex < Routes.Length)
            {
                var route = Routes[routeIndex];
                if (isSource && route.Destination != null && route.Destination == container)
                {
                    reason = "Cannot use the same container as both source and destination in the same route";
                    return false;
                }
                if (!isSource && route.Source != null && route.Source == container)
                {
                    reason = "Cannot use the same container as both source and destination in the same route";
                    return false;
                }
            }

            reason = "";
            return true;
        }

        /// <summary>
        /// Serializes config to a pipe-delimited string for save/sync.
        /// Format: "lockerGUID;supplyGUID|src1,dst1|src2,dst2|src3,dst3|id:thresh+id:thresh+..."
        /// Empty slots use empty string.
        /// Product slots use id:threshold pairs separated by + in the 5th segment.
        /// </summary>
        public string Serialize()
        {
            var parts = new List<string>();

            parts.Add($"{GetGuid(Locker)};{GetGuid(SupplyStorage)}");

            for (int i = 0; i < Routes.Length; i++)
            {
                string src = GetGuid(Routes[i].Source);
                string dst = GetGuid(Routes[i].Destination);
                // Compact: if src==dst, use "=" shorthand to save ~31 chars per route
                if (!string.IsNullOrEmpty(src) && src == dst)
                    parts.Add($"{src},=");
                else
                    parts.Add($"{src},{dst}");
            }

            // Item IDs with thresholds (id:threshold pairs, + separated)
            // Trim trailing empty slots to save space
            var productParts = new List<string>();
            for (int i = 0; i < StockedItemIds.Length; i++)
                productParts.Add($"{StockedItemIds[i] ?? ""}:{StockedThresholds[i]}");
            while (productParts.Count > 0 && productParts[productParts.Count - 1].StartsWith(":"))
                productParts.RemoveAt(productParts.Count - 1);
            parts.Add(string.Join("+", productParts));

            return string.Join("|", parts);
        }

        /// <summary>
        /// Deserializes config from a pipe-delimited GUID string. Resolves GUIDs via GUIDManager.
        /// Format: lockerGUID;supplyGUID|src,dst|src,dst|src,dst|id:thresh+id:thresh+...
        /// </summary>
        public void Deserialize(string data)
        {
            ClearAll();

            if (string.IsNullOrEmpty(data)) return;

            try
            {
                var parts = data.Split('|');
                if (parts.Length < 1) return;

                // First segment: locker;supply
                var lockerSupply = parts[0].Split(';');
                Locker = ResolveStorage(lockerSupply[0]);
                if (lockerSupply.Length > 1)
                    SupplyStorage = ResolveStorage(lockerSupply[1]);

                // Routes (segments 1-3)
                for (int i = 0; i < Routes.Length && i + 1 < parts.Length; i++)
                {
                    var routeParts = parts[i + 1].Split(',');
                    if (routeParts.Length >= 2)
                    {
                        string srcStr = routeParts[0];
                        // "=" shorthand means dst == src
                        string dstStr = routeParts[1] == "=" ? srcStr : routeParts[1];
                        Routes[i].Source = ResolveStorage(srcStr);
                        Routes[i].Destination = ResolveStorage(dstStr);
                    }
                }

                // Item IDs with thresholds (segment 4, id:threshold pairs, + separated)
                if (parts.Length > 4 && !string.IsNullOrEmpty(parts[4]))
                {
                    var itemEntries = parts[4].Split('+');
                    for (int i = 0; i < Math.Min(itemEntries.Length, StockedItemIds.Length); i++)
                    {
                        var pair = itemEntries[i].Split(':');
                        StockedItemIds[i] = string.IsNullOrEmpty(pair[0]) ? null : pair[0];
                        if (pair.Length > 1 && int.TryParse(pair[1], out int thresh))
                            StockedThresholds[i] = thresh;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Deserialize failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears all configuration assignments.
        /// </summary>
        public void ClearAll()
        {
            Locker = null;
            SupplyStorage = null;
            for (int i = 0; i < StockedItemIds.Length; i++)
                StockedItemIds[i] = null;
            for (int i = 0; i < StockedThresholds.Length; i++)
                StockedThresholds[i] = 20;
            for (int i = 0; i < Routes.Length; i++)
            {
                Routes[i].Source = null;
                Routes[i].Destination = null;
            }
        }

        /// <summary>
        /// Checks whether any configuration has been set.
        /// </summary>
        public bool HasAnyConfig()
        {
            if (Locker != null) return true;
            if (SupplyStorage != null) return true;
            if (StockedItemIds.Any(id => !string.IsNullOrEmpty(id))) return true;
            for (int i = 0; i < Routes.Length; i++)
            {
                if (!Routes[i].IsEmpty) return true;
            }
            return false;
        }

        internal static string GetGuid(PlaceableStorageEntity entity)
        {
            if (entity == null) return "";
            try
            {
                var buildable = entity.TryCast<BuildableItem>();
                if (buildable != null)
                    return buildable.GUID.ToString().Replace("-", "");
            }
            catch (Exception ex)
            {
                Logger.Warning($"GetGuid failed: {ex.Message}");
            }
            return "";
        }

        internal static PlaceableStorageEntity ResolveStorage(string guidStr)
        {
            if (string.IsNullOrEmpty(guidStr)) return null;

            try
            {
                var guid = new System.Guid(guidStr);
                var obj = Il2Cpp.GUIDManager.GetObject<BuildableItem>(
                    new Il2CppSystem.Guid(guid.ToByteArray()));

                if (obj != null)
                {
                    var storage = obj.TryCast<PlaceableStorageEntity>();
                    if (storage != null)
                        return storage;

                    // It might be a GridItem that has a PlaceableStorageEntity component
                    var storageComp = obj.GetComponent<PlaceableStorageEntity>();
                    if (storageComp != null)
                        return storageComp;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"ResolveStorage failed for '{guidStr}': {ex.Message}");
            }

            return null;
        }
    }
}
