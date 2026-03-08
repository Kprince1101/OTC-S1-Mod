using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Storage;
#else
using ScheduleOne.Economy;
using ScheduleOne.EntityFramework;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Storage;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Configuration data for a Manager NPC: locker + supply storage + 3 distribution routes.
    /// Serializes container assignments via BuildableItem GUIDs for save/sync.
    /// </summary>
    public class ManagerConfiguration
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:ManagerConfig");

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

        // Persistent upgrade tiers (survive transfers and save/load)
        public int SpeedTier { get; set; }
        public int ExtraInventorySlots { get; set; }

        public class DistributionRoute
        {
            public PlaceableStorageEntity Source { get; set; }
            public PlaceableStorageEntity Destination { get; set; }
            public DeadDrop SourceDeadDrop { get; set; }
            public DeadDrop DestDeadDrop { get; set; }

            public bool HasSource => Source != null || SourceDeadDrop != null;
            public bool HasDest => Destination != null || DestDeadDrop != null;
            public bool IsSourceDeadDrop => SourceDeadDrop != null;
            public bool IsDestDeadDrop => DestDeadDrop != null;
            public bool IsConfigured => HasSource && HasDest;
            public bool IsEmpty => !HasSource && !HasDest;

            /// <summary>Sets the source to a storage container, clearing any dead drop.</summary>
            public void SetSource(PlaceableStorageEntity pse) { Source = pse; SourceDeadDrop = null; }
            /// <summary>Sets the source to a dead drop, clearing any storage container.</summary>
            public void SetSource(DeadDrop dd) { SourceDeadDrop = dd; Source = null; }
            /// <summary>Sets the destination to a storage container, clearing any dead drop.</summary>
            public void SetDest(PlaceableStorageEntity pse) { Destination = pse; DestDeadDrop = null; }
            /// <summary>Sets the destination to a dead drop, clearing any storage container.</summary>
            public void SetDest(DeadDrop dd) { DestDeadDrop = dd; Destination = null; }

            public void ClearSource() { Source = null; SourceDeadDrop = null; }
            public void ClearDest() { Destination = null; DestDeadDrop = null; }

            /// <summary>Gets the StorageEntity for the source (works for both PSE and dead drops).</summary>
            public StorageEntity GetSourceStorage()
                => IsSourceDeadDrop ? SourceDeadDrop?.Storage : Source?.StorageEntity;

            /// <summary>Gets the StorageEntity for the destination (works for both PSE and dead drops).</summary>
            public StorageEntity GetDestStorage()
                => IsDestDeadDrop ? DestDeadDrop?.Storage : Destination?.StorageEntity;

            /// <summary>Gets the transform for the source endpoint.</summary>
            public UnityEngine.Transform GetSourceTransform()
                => IsSourceDeadDrop ? SourceDeadDrop?.transform : Source?.transform;

            /// <summary>Gets the transform for the destination endpoint.</summary>
            public UnityEngine.Transform GetDestTransform()
                => IsDestDeadDrop ? DestDeadDrop?.transform : Destination?.transform;
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
                return true;
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
        /// Validates that a dead drop can be assigned to a specific slot within a route.
        /// Same dead drop cannot be both source AND destination within the same route.
        /// </summary>
        public bool ValidateAssignment(DeadDrop dd, int routeIndex, bool isSource, out string reason)
        {
            if (dd == null)
            {
                reason = "";
                return true;
            }

            if (routeIndex >= 0 && routeIndex < Routes.Length)
            {
                var route = Routes[routeIndex];
                string ddGuid = GetGuid(dd);

                if (isSource && route.DestDeadDrop != null && GetGuid(route.DestDeadDrop) == ddGuid)
                {
                    reason = "Cannot use the same dead drop as both source and destination in the same route";
                    return false;
                }
                if (!isSource && route.SourceDeadDrop != null && GetGuid(route.SourceDeadDrop) == ddGuid)
                {
                    reason = "Cannot use the same dead drop as both source and destination in the same route";
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
                string src = GetRouteEndpointGuid(Routes[i], true);
                string dst = GetRouteEndpointGuid(Routes[i], false);
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

            // Upgrade tiers (segment 5): speedTier,extraSlots — omit if both zero to save space
            if (SpeedTier > 0 || ExtraInventorySlots > 0)
                parts.Add($"{SpeedTier},{ExtraInventorySlots}");

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
                        ResolveRouteEndpoint(srcStr, Routes[i], true);
                        ResolveRouteEndpoint(dstStr, Routes[i], false);
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

                // Upgrade tiers (segment 5): speedTier,extraSlots — defaults 0,0 if absent
                if (parts.Length > 5 && !string.IsNullOrEmpty(parts[5]))
                {
                    var upgParts = parts[5].Split(',');
                    if (upgParts.Length >= 1 && int.TryParse(upgParts[0], out int spd))
                        SpeedTier = Math.Max(0, Math.Min(spd, ManagerUpgrades.MaxSpeedTier));
                    if (upgParts.Length >= 2 && int.TryParse(upgParts[1], out int inv))
                        ExtraInventorySlots = Math.Max(0, Math.Min(inv, ManagerUpgrades.MaxExtraSlots));
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
                Routes[i].ClearSource();
                Routes[i].ClearDest();
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

        internal const string DeadDropPrefix = "dd:";

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

        internal static string GetGuid(DeadDrop dd)
        {
            if (dd == null) return "";
            try
            {
                return DeadDropPrefix + dd.GUID.ToString().Replace("-", "");
            }
            catch (Exception ex)
            {
                Logger.Warning($"GetGuid(DeadDrop) failed: {ex.Message}");
            }
            return "";
        }

        /// <summary>
        /// Gets the serialization GUID for a route endpoint (source or destination),
        /// handling both PlaceableStorageEntity and DeadDrop types.
        /// </summary>
        internal static string GetRouteEndpointGuid(DistributionRoute route, bool isSource)
        {
            if (isSource)
                return route.IsSourceDeadDrop ? GetGuid(route.SourceDeadDrop) : GetGuid(route.Source);
            return route.IsDestDeadDrop ? GetGuid(route.DestDeadDrop) : GetGuid(route.Destination);
        }

        internal static PlaceableStorageEntity ResolveStorage(string guidStr)
        {
            if (string.IsNullOrEmpty(guidStr)) return null;

            try
            {
                var guid = new System.Guid(guidStr);
#if IL2CPP
                var obj = Il2Cpp.GUIDManager.GetObject<BuildableItem>(
                    new GameSystem.Guid(guid.ToByteArray()));
#else
                var obj = GUIDManager.GetObject<BuildableItem>(
                    new System.Guid(guid.ToByteArray()));
#endif

                if (obj != null)
                {
                    var storage = obj.TryCast<PlaceableStorageEntity>();
                    if (storage != null)
                        return storage;

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

        internal static DeadDrop ResolveDeadDrop(string guidStr)
        {
            if (string.IsNullOrEmpty(guidStr)) return null;

            try
            {
                var guid = new System.Guid(guidStr);
#if IL2CPP
                var obj = Il2Cpp.GUIDManager.GetObject<DeadDrop>(
                    new GameSystem.Guid(guid.ToByteArray()));
#else
                var obj = GUIDManager.GetObject<DeadDrop>(
                    new System.Guid(guid.ToByteArray()));
#endif
                if (obj != null) return obj;

                // Fallback: iterate static list
                foreach (var dd in DeadDrop.DeadDrops)
                {
                    if (dd != null && dd.GUID.Equals(guid))
                        return dd;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"ResolveDeadDrop failed for '{guidStr}': {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Resolves a route endpoint GUID string, detecting dd: prefix for dead drops.
        /// Sets the appropriate field on the route.
        /// </summary>
        private static void ResolveRouteEndpoint(string guidStr, DistributionRoute route, bool isSource)
        {
            if (string.IsNullOrEmpty(guidStr)) return;

            if (guidStr.StartsWith(DeadDropPrefix))
            {
                var dd = ResolveDeadDrop(guidStr.Substring(DeadDropPrefix.Length));
                if (dd != null)
                {
                    if (isSource) route.SetSource(dd);
                    else route.SetDest(dd);
                }
            }
            else
            {
                var pse = ResolveStorage(guidStr);
                if (pse != null)
                {
                    if (isSource) route.SetSource(pse);
                    else route.SetDest(pse);
                }
            }
        }
    }
}
