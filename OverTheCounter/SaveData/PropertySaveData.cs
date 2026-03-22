using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using S1API.Internal.Abstraction;
using S1API.Saveables;
using System.Reflection;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.Persistence.Datas;
using Il2CppScheduleOne.EntityFramework;
using Grid = Il2CppScheduleOne.Tiles.Grid;
#else
using ScheduleOne.ObjectScripts;
using ScheduleOne.Storage;
using ScheduleOne.ItemFramework;
using ScheduleOne.Persistence;
using ScheduleOne.Persistence.Datas;
using Grid = ScheduleOne.Tiles.Grid;
#endif

namespace OverTheCounter.SaveData
{
    /// <summary>
    /// A single message in the OTC encrypted messaging thread.
    /// Plain text when IsEmbed=false (renders as chat bubble using Text).
    /// Rich embed when IsEmbed=true (renders card from Embed* fields).
    /// </summary>
    [Serializable]
    public class OtcPropertyMessage
    {
        public string Id;
        public string Sender;
        public string Text;
        public bool IsEmbed;

        // Embed fields — only used when IsEmbed = true
        public string EmbedTitle;
        public string EmbedDescription;
        public List<string> EmbedItems;
        public string EmbedImageResource;
        public string EmbedLocation;
        public string EmbedStatus;        // "SOLD", "PURCHASED" — replaces content when set
        public string EmbedButtonLabel;
        public string EmbedButtonAction;  // handler lookup key in CustomersApp
        public bool IsSeen;
    }

    /// <summary>
    /// Per-property record storing ownership state.
    /// </summary>
    [Serializable]
    public class OtcPropertyRecord
    {
        public string PropertyId;
        public bool IsOwned;
    }

    /// <summary>
    /// Persisted record of an item placed on an OTC building grid.
    /// Slots stores per-slot JSON from the game's native ItemData serialization.
    /// </summary>
    [Serializable]
    public class OtcPlacedItem
    {
        public string BuildingId;
        public string PrefabId;
        public float CoordX;
        public float CoordZ;
        public int Rotation;
        public string[] Slots;
    }

    /// <summary>
    /// Persisted toggle states for the shack (lights, store open/close).
    /// </summary>
    [Serializable]
    public class OtcShackState
    {
        public bool LightsOn;
        public bool StoreOpen;
    }

    /// <summary>
    /// Record of a single product sale at the OTC checkout counter.
    /// </summary>
    [Serializable]
    public class OtcSaleRecord
    {
        public string ProductId;
        public string ProductName;
        public int Quantity;
        public float PricePerUnit;
        public int QualityLevel;
        public int GameDay;
    }

    /// <summary>
    /// Saves OTC property ownership state and placed item positions.
    /// The messaging thread lives in <see cref="StaticThreadSaveData"/>.
    /// </summary>
    public class PropertySaveData : Saveable
    {
        public const string ShackId = "westville_shack";

        [SaveableField("otc_properties")]
        private List<OtcPropertyRecord> _properties = new();

        [SaveableField("otc_placed_items")]
        private List<OtcPlacedItem> _placedItems = new();

        [SaveableField("otc_shack_initialized")]
        private bool _shackInitialized;

        [SaveableField("otc_shack_state")]
        private OtcShackState _shackState = new();

        [SaveableField("otc_sales_log")]
        private List<OtcSaleRecord> _salesLog = new();

        [SaveableField("otc_register_balance")]
        private float _registerBalance;

        /// <summary>Singleton instance, set during construction or load.</summary>
        public static PropertySaveData Instance { get; private set; }

        internal static void ResetInstance() => Instance = null;

        public PropertySaveData()
        {
            Instance = this;
        }

        protected override void OnLoaded()
        {
            Instance = this;
            ConfigSyncData.ApplyPendingGameState();

            if (IsPropertyOwned(ShackId))
            {
                WestvilleShack.UnlockDoor();
                WestvilleShack.ApplySavedState(_shackState.LightsOn, _shackState.StoreOpen);
            }

        }

        /// <summary>
        /// Applies the saved register balance to the first counter.
        /// Called after counters have been placed.
        /// </summary>
        public void ApplyRegisterBalance()
        {
            if (_registerBalance <= 0f) return;
            var counters = CheckoutCounter.AllCounters;
            if (counters.Count > 0)
            {
                counters[0].RegisterBalance = _registerBalance;
                _registerBalance = 0f;
            }
        }

        /// <summary>Captures current shack toggle states before save serialization.</summary>
        public void CaptureShackState()
        {
            _shackState.LightsOn = WestvilleShack.AreLightsOn;
            _shackState.StoreOpen = WestvilleShack.IsStoreOpen;

            // Sum all counter register balances
            _registerBalance = 0f;
            foreach (var counter in CheckoutCounter.AllCounters)
                _registerBalance += counter.RegisterBalance;
        }

        // ==================================================================
        // Sales analytics
        // ==================================================================

        /// <summary>Records a product sale for analytics.</summary>
        public void RecordSale(string productId, string productName, int quantity, float pricePerUnit, int qualityLevel, int gameDay)
        {
            _salesLog.Add(new OtcSaleRecord
            {
                ProductId = productId,
                ProductName = productName,
                Quantity = quantity,
                PricePerUnit = pricePerUnit,
                QualityLevel = qualityLevel,
                GameDay = gameDay
            });
        }

        /// <summary>Returns all recorded sales.</summary>
        public List<OtcSaleRecord> GetSalesLog() => _salesLog;

        // ==================================================================
        // Property record access
        // ==================================================================

        /// <summary>Returns the property record for the given ID, or null.</summary>
        public OtcPropertyRecord GetProperty(string propertyId) =>
            _properties.FirstOrDefault(p => p.PropertyId == propertyId);

        /// <summary>Returns or creates the property record for the given ID.</summary>
        public OtcPropertyRecord GetOrCreateProperty(string propertyId)
        {
            var record = GetProperty(propertyId);
            if (record != null) return record;
            record = new OtcPropertyRecord { PropertyId = propertyId };
            _properties.Add(record);
            return record;
        }

        /// <summary>Returns true if the property is owned.</summary>
        public bool IsPropertyOwned(string propertyId) =>
            GetProperty(propertyId)?.IsOwned ?? false;

        // ==================================================================
        // Shack listing
        // ==================================================================

        /// <summary>Creates the shack property record and adds the listing to the message thread.</summary>
        public void EnsureShackListing()
        {
            GetOrCreateProperty(ShackId);

            var thread = StaticThreadSaveData.Instance;
            if (thread == null) return;

            thread.AddMessage("shack_intro",
                "Got something for you. Property listing from one of my contacts. " +
                "Small operation, good location. Details below.");

            thread.AddEmbed(new OtcPropertyMessage
            {
                Id = $"{ShackId}_card",
                EmbedTitle = "Westville Shack",
                EmbedDescription = "Small dispensary, good location. Move your legal product " +
                                   "through the counter. Door locked until purchased.",
                EmbedItems = new List<string> { $"${Config.ShackPurchasePrice.Value:N0}" },
                EmbedLocation = "Westville",
                EmbedImageResource = "OverTheCounter.Resources.ShackPhoto.png",
                EmbedButtonLabel = "Purchase",
                EmbedButtonAction = "purchase_shack"
            });
        }

        // ==================================================================
        // Purchase
        // ==================================================================

        /// <summary>Marks a property as owned and posts a confirmation to the message thread.</summary>
        public void PurchaseProperty(string propertyId)
        {
            var record = GetOrCreateProperty(propertyId);
            if (record.IsOwned) return;
            record.IsOwned = true;

            var thread = StaticThreadSaveData.Instance;
            thread?.SetEmbedStatus($"{propertyId}_card", "SOLD");
            thread?.AddMessage($"{propertyId}_purchased",
                "Done. Door's unlocked, keys are yours. " +
                "Set up shop and customers will find you.");

            if (propertyId == ShackId)
                WestvilleShack.UnlockDoor();

            ConfigSyncData.Instance?.PublishGameState();
        }

        /// <summary>Restores placed items from save data for a building. Host-only.</summary>
        internal void RestorePlacedItems(string buildingId, Grid grid)
        {
            if (!NetworkHelper.IsHost) return;
            if (grid == null)
            {
                OTCLog.Warning(OTCLog.Systems.General, $"RestorePlacedItems '{buildingId}': grid is null");
                return;
            }

            var items = GetPlacedItems(buildingId);

            // Shack-specific first-load logic: auto-spawn checkout counter
            if (buildingId == ShackId && !_shackInitialized)
            {
                _shackInitialized = true;
                if (items.Count == 0)
                {
                    CheckoutCounter.SpawnOnGrid(grid);
                    return;
                }
            }
            if (items.Count == 0)
                return;

            // Track items with saved slots for deferred restoration
            var itemsWithSlots = new List<OtcPlacedItem>();

            for (int idx = 0; idx < items.Count; idx++)
            {
                var item = items[idx];
                try
                {
                    var coord = new Vector2(item.CoordX, item.CoordZ);
                    if (item.PrefabId == "otc_checkout_counter")
                        CheckoutCounter.SpawnOnGrid(grid, coord, item.Rotation);
                    else
                        CheckoutCounter.SpawnVanillaGridItem(grid, item.PrefabId, coord, item.Rotation);

                    if (item.Slots != null && item.Slots.Length > 0)
                        itemsWithSlots.Add(item);
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.General,$"Failed to restore '{item.PrefabId}' at ({item.CoordX},{item.CoordZ}): {ex.Message}\n{ex.StackTrace}");
                }
            }

            // Apply saved register balance now that counters are registered
            ApplyRegisterBalance();

            // Defer slot restoration so grid items have time to initialize
            if (itemsWithSlots.Count > 0)
                MelonCoroutines.Start(RestoreSlotContentsDeferred(itemsWithSlots));
        }

        private System.Collections.IEnumerator RestoreSlotContentsDeferred(List<OtcPlacedItem> items)
        {
            // Wait for grid items to be fully spawned and initialized
            yield return new WaitForSeconds(3f);

            foreach (var item in items)
            {
                try
                {
                    // Find the runtime storage entity by its stored origin coordinate
                    foreach (var kvp in BuildingGridFactory.GridContainers)
                    {
                        var root = kvp.Value;
                        if (root == null) continue;

                        var storages = root.GetComponentsInChildren<PlaceableStorageEntity>(true);
                        if (storages == null) continue;

                        for (int i = 0; i < storages.Length; i++)
                        {
                            var storage = storages[i];
                            if (storage == null) continue;

                            var coord = GetOriginCoordinate(storage);
                            if (coord == null) continue;

                            int cx = (int)coord.Value.x;
                            int cz = (int)coord.Value.y;

                            if (cx == (int)item.CoordX && cz == (int)item.CoordZ)
                                RestoreEntitySlots(storage, item.Slots);
                        }
                    }
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.General,$"RestoreSlotContentsDeferred failed for {item.PrefabId}: {ex.Message}");
                }
            }
        }

        // ==================================================================
        // Multiplayer sync
        // ==================================================================

        /// <summary>Applies property ownership from the host (multiplayer sync).</summary>
        public void ApplyHostPropertyState(string propertyId, bool owned)
        {
            if (!owned) return;
            var record = GetOrCreateProperty(propertyId);
            if (record.IsOwned) return;
            record.IsOwned = true;

            if (propertyId == ShackId)
                WestvilleShack.UnlockDoor();
        }

        // ==================================================================
        // Placed items
        // ==================================================================

        /// <summary>Returns all placed items for a building.</summary>
        public List<OtcPlacedItem> GetPlacedItems(string buildingId) =>
            _placedItems.Where(i => i.BuildingId == buildingId).ToList();

        /// <summary>Adds or updates a placed item record for a building. Keyed by grid coordinate.</summary>
        public void SavePlacedItem(string buildingId, string prefabId, float coordX, float coordZ, int rotation)
        {
            var existing = _placedItems.FirstOrDefault(
                i => i.BuildingId == buildingId && i.CoordX == coordX && i.CoordZ == coordZ);
            if (existing != null)
            {
                existing.PrefabId = prefabId;
                existing.Rotation = rotation;
            }
            else
            {
                _placedItems.Add(new OtcPlacedItem
                {
                    BuildingId = buildingId,
                    PrefabId = prefabId,
                    CoordX = coordX,
                    CoordZ = coordZ,
                    Rotation = rotation
                });
            }
        }

        /// <summary>Removes a placed item record at the given grid coordinate.</summary>
        public void RemovePlacedItem(string buildingId, float coordX, float coordZ)
        {
            _placedItems.RemoveAll(i => i.BuildingId == buildingId && i.CoordX == coordX && i.CoordZ == coordZ);
        }

        // ==================================================================
        // Storage contents persistence (mirrors game's ItemSet pattern)
        // ==================================================================


        /// <summary>
        /// Rebuilds <see cref="_placedItems"/> from the live grid state, then snapshots
        /// storage slot contents. Eliminates stale records from moved/removed items.
        /// Call before save serialization.
        /// </summary>
        public void SnapshotStorageContents()
        {
            try
            {
                // Rebuild placed items from what's actually on the grid right now
                _placedItems.Clear();

                foreach (var kvp in BuildingGridFactory.GridContainers)
                {
                    var grid = kvp.Key;
                    var root = kvp.Value;
                    if (root == null) continue;

                    var buildingId = BuildingGridFactory.GetBuildingId(grid);
                    if (buildingId == null) continue;

                    var gridItems = GetAllGridItems(root);
                    if (gridItems == null) continue;

                    foreach (var item in gridItems)
                    {
                        if (item == null) continue;

                        var coord = GetOriginCoordinate(item);
                        if (coord == null) continue;

                        int rotation = GetRotation(item);
                        string itemId = GetItemId(item);
                        if (string.IsNullOrEmpty(itemId)) continue;

                        var placed = new OtcPlacedItem
                        {
                            BuildingId = buildingId,
                            PrefabId = itemId.ToLower(),
                            CoordX = coord.Value.x,
                            CoordZ = coord.Value.y,
                            Rotation = rotation
                        };

                        // Snapshot slots if it's a storage entity
                        if (item is PlaceableStorageEntity storage)
                            placed.Slots = SnapshotEntitySlots(storage);

                        _placedItems.Add(placed);
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.General,$"SnapshotStorageContents failed: {ex}");
            }
        }

        /// <summary>
        /// Finds all GridItem components under a building root transform.
        /// </summary>
        private static Component[] GetAllGridItems(Transform root)
        {
#if IL2CPP
            return root.GetComponentsInChildren<GridItem>(true);
#else
            var gridItemType = Type.GetType("ScheduleOne.EntityFramework.GridItem, Assembly-CSharp");
            if (gridItemType == null) return null;
            return root.GetComponentsInChildren(gridItemType, true);
#endif
        }

        /// <summary>
        /// Reads _originCoordinate from a GridItem (or subclass).
        /// </summary>
        private static Vector2? GetOriginCoordinate(Component entity)
        {
#if IL2CPP
            if (entity is GridItem gi)
                return gi._originCoordinate;
#else
            var t = entity.GetType();
            while (t != null)
            {
                var field = t.GetField("_originCoordinate",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                    return (Vector2)field.GetValue(entity);
                t = t.BaseType;
            }
#endif
            return null;
        }

        /// <summary>
        /// Reads _rotation from a GridItem (or subclass).
        /// </summary>
        private static int GetRotation(Component entity)
        {
#if IL2CPP
            if (entity is GridItem gi)
                return gi._rotation;
#else
            var t = entity.GetType();
            while (t != null)
            {
                var field = t.GetField("_rotation",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                    return (int)field.GetValue(entity);
                t = t.BaseType;
            }
#endif
            return 0;
        }

        /// <summary>
        /// Reads the item definition ID from a GridItem's ItemInstance.
        /// </summary>
        private static string GetItemId(Component entity)
        {
#if IL2CPP
            if (entity is GridItem gi)
                return gi.ItemInstance?.ID;
#else
            // Walk up to BuildableItem which has the ItemInstance property
            var t = entity.GetType();
            while (t != null)
            {
                var prop = t.GetProperty("ItemInstance",
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var instance = prop.GetValue(entity);
                    if (instance == null) return null;
                    var idProp = instance.GetType().GetProperty("ID");
                    return idProp?.GetValue(instance) as string;
                }
                t = t.BaseType;
            }
#endif
            return null;
        }

        /// <summary>
        /// Serializes storage slots using the game's native ItemData format.
        /// Mirrors ItemSet constructor: slot.ItemInstance.GetItemData().GetJson(false)
        /// </summary>
        private string[] SnapshotEntitySlots(PlaceableStorageEntity storage)
        {
            var entity = storage.StorageEntity;
            if (entity?.ItemSlots == null || entity.ItemSlots.Count == 0) return null;

            var emptyJson = new ItemData(string.Empty, 0).GetJson(false);
            var items = new string[entity.ItemSlots.Count];
            bool hasAny = false;

            for (int i = 0; i < entity.ItemSlots.Count; i++)
            {
                var slot = entity.ItemSlots[i];
                if (slot?.ItemInstance != null)
                {
                    items[i] = slot.ItemInstance.GetItemData().GetJson(false);
                    hasAny = true;
                }
                else
                {
                    items[i] = emptyJson;
                }
            }

            return hasAny ? items : null;
        }

        /// <summary>
        /// Restores saved slot contents into a runtime storage entity.
        /// Uses the game's native ItemDeserializer.LoadItem() — same as PlaceableStorageEntityLoader.
        /// </summary>
        private static void RestoreEntitySlots(PlaceableStorageEntity storage, string[] savedSlots)
        {
            var entity = storage?.StorageEntity;
            if (entity?.ItemSlots == null || savedSlots == null || savedSlots.Length == 0) return;

            for (int i = 0; i < savedSlots.Length && i < entity.ItemSlots.Count; i++)
            {
                if (string.IsNullOrEmpty(savedSlots[i])) continue;

                try
                {
                    var instance = ItemDeserializer.LoadItem(savedSlots[i]);
                    entity.ItemSlots[i].SetStoredItem(instance, false);
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.General,$"RestoreEntitySlots slot {i} failed: {ex.Message}");
                }
            }
        }
    }
}
