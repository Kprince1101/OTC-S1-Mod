using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using OverTheCounter.Logic.Placement;
using OverTheCounter.Quests;
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
        public string ThreadId;  // Groups messages into collapsible threads

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
        public string DeskStyleId;
    }

    /// <summary>
    /// Persisted toggle states for the shack (lights, store open/close).
    /// </summary>
    [Serializable]
    public class OtcShackState
    {
        public bool LightsOn;
        public bool StoreOpen;
        public string LightingStyleId;
        public string ExteriorWallStyleId;
        public string InteriorWallStyleId;
        public string FloorStyleId;
    }

    /// <summary>
    /// Persisted toggle states for the dispensary (lights, store open/close).
    /// </summary>
    [Serializable]
    public class OtcDispensaryState
    {
        public bool LightsOn;
        public bool StoreOpen;
        public string LightingStyleId;
        public string ExteriorWallStyleId;
        public string InteriorWallStyleId;
        public string FloorStyleId;
    }

    /// <summary>
    /// Persisted toggle states for the warehouse (lights only).
    /// </summary>
    [Serializable]
    public class OtcWarehouseState
    {
        public bool LightsOn;
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
        /// <summary>In-game day number when the sale occurred.</summary>
        public int GameDay;
        /// <summary>Customer NPC name (e.g. "Peter File").</summary>
        public string CustomerName;
        /// <summary>24h time int when the sale occurred (e.g. 1330 = 1:30 PM).</summary>
        public int GameHour;
        /// <summary>Groups line items belonging to the same checkout transaction.</summary>
        public string TransactionId;
        /// <summary>Tip amount paid by the customer for this line item.</summary>
        public float TipAmount;
        /// <summary>Building ID where the sale took place (e.g. "westville_shack").</summary>
        public string BuildingId;
    }

    /// <summary>
    /// Daily snapshot of total inventory count across all OTC buildings.
    /// Used by the GreenTab POS overview chart.
    /// </summary>
    [Serializable]
    public class OtcInventorySnapshot
    {
        /// <summary>In-game day number when the snapshot was taken.</summary>
        public int GameDay;
        /// <summary>Total product count across all owned OTC buildings.</summary>
        public int TotalCount;
    }

    /// <summary>
    /// Saves OTC property ownership state and placed item positions.
    /// The messaging thread lives in <see cref="StaticThreadSaveData"/>.
    /// </summary>
    public class PropertySaveData : Saveable
    {
        public const string ShackId = "westville_shack";
        public const string DispensaryId = "big_dispensary";
        public const string WarehouseId = "otc_warehouse";

        [SaveableField("otc_properties")]
        private List<OtcPropertyRecord> _properties = new();

        [SaveableField("otc_placed_items")]
        private List<OtcPlacedItem> _placedItems = new();

        [SaveableField("otc_shack_initialized")]
        private bool _shackInitialized;

        [SaveableField("otc_shack_state")]
        private OtcShackState _shackState = new();

        [SaveableField("otc_dispensary_state")]
        private OtcDispensaryState _dispensaryState = new();

        [SaveableField("otc_warehouse_state")]
        private OtcWarehouseState _warehouseState = new();

        [SaveableField("otc_sales_log")]
        private List<OtcSaleRecord> _salesLog = new();

        [SaveableField("otc_inventory_snapshots")]
        private List<OtcInventorySnapshot> _inventorySnapshots = new();

        [SaveableField("otc_register_balance")]
        private float _registerBalance;

        [SaveableField("otc_budtenders")]
        private string _budtenderState = "";

        /// <summary>Saved budtender state for deferred restore after counters are placed.</summary>
        public string BudtenderSaveState => _budtenderState;

        /// <summary>Singleton instance, set during construction or load.</summary>
        public static PropertySaveData Instance { get; private set; }

        internal static void ResetInstance() => Instance = null;

        public PropertySaveData()
        {
            Instance = this;
        }

        protected override void OnLoaded()
        {
            OTCLog.Msg(OTCLog.Systems.General,
                $"PropertySaveData.OnLoaded — salesLog={_salesLog?.Count ?? -1} entries");
            Instance = this;

            // Seed _txCounter from existing sales to avoid ID collisions after reload
            _txCounter = 0;
            if (_salesLog != null)
            {
                foreach (var sale in _salesLog)
                {
                    if (sale.TransactionId != null && sale.TransactionId.StartsWith("tx_") &&
                        int.TryParse(sale.TransactionId.Substring(3), out int id) && id >= _txCounter)
                        _txCounter = id + 1;
                }
            }

            try { ConfigSyncData.ApplyPendingGameState(); }
            catch (Exception ex) { OTCLog.Error(OTCLog.Systems.General, $"ApplyPendingGameState failed: {ex.Message}"); }

            if (IsPropertyOwned(ShackId))
            {
                WestvilleShack.UnlockDoor();
                WestvilleShack.ApplySavedState(_shackState.LightsOn, _shackState.StoreOpen,
                    _shackState.LightingStyleId,
                    _shackState.ExteriorWallStyleId, _shackState.InteriorWallStyleId,
                    _shackState.FloorStyleId);
            }

            if (IsPropertyOwned(DispensaryId))
            {
                Dispensary.UnlockDoor();
                Dispensary.ApplySavedState(_dispensaryState.LightsOn, _dispensaryState.StoreOpen,
                    _dispensaryState.LightingStyleId,
                    _dispensaryState.ExteriorWallStyleId, _dispensaryState.InteriorWallStyleId,
                    _dispensaryState.FloorStyleId);
            }

            if (IsPropertyOwned(WarehouseId))
            {
                OTCWarehouse.UnlockDoor();
                OTCWarehouse.ApplySavedState(_warehouseState.LightsOn);
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

        /// <summary>Captures current building toggle states before save serialization.</summary>
        public void CaptureShackState()
        {
            _shackState.LightsOn = WestvilleShack.AreLightsOn;
            _shackState.StoreOpen = WestvilleShack.IsStoreOpen;
            _shackState.LightingStyleId = WestvilleShack.CurrentLightingStyleId;
            _shackState.ExteriorWallStyleId = WestvilleShack.CurrentExteriorWallStyleId;
            _shackState.InteriorWallStyleId = WestvilleShack.CurrentInteriorWallStyleId;
            _shackState.FloorStyleId = WestvilleShack.CurrentFloorStyleId;

            _dispensaryState.LightsOn = Dispensary.AreLightsOn;
            _dispensaryState.StoreOpen = Dispensary.IsStoreOpen;
            _dispensaryState.LightingStyleId = Dispensary.CurrentLightingStyleId;
            _dispensaryState.ExteriorWallStyleId = Dispensary.CurrentExteriorWallStyleId;
            _dispensaryState.InteriorWallStyleId = Dispensary.CurrentInteriorWallStyleId;
            _dispensaryState.FloorStyleId = Dispensary.CurrentFloorStyleId;

            _warehouseState.LightsOn = OTCWarehouse.AreLightsOn;

            // Sum all counter register balances
            _registerBalance = 0f;
            foreach (var counter in CheckoutCounter.AllCounters)
                _registerBalance += counter.RegisterBalance;

            // Capture budtender state
            _budtenderState = Logic.BudtenderController.Serialize();
        }

        // ==================================================================
        // Sales analytics
        // ==================================================================

        /// <summary>Records a product sale for analytics.</summary>
        public void RecordSale(string productId, string productName, int quantity, float pricePerUnit, int qualityLevel, int gameDay,
            string customerName = null, int gameHour = 0, string transactionId = null, float tipAmount = 0f, string buildingId = null)
        {
            _salesLog.Add(new OtcSaleRecord
            {
                ProductId = productId,
                ProductName = productName,
                Quantity = quantity,
                PricePerUnit = pricePerUnit,
                QualityLevel = qualityLevel,
                GameDay = gameDay,
                CustomerName = customerName,
                GameHour = gameHour,
                TransactionId = transactionId,
                TipAmount = tipAmount,
                BuildingId = buildingId
            });
            OnSaleRecorded?.Invoke();
        }

        private int _txCounter;

        /// <summary>Returns a unique transaction ID for grouping products from the same checkout.</summary>
        public string NextTransactionId() => $"tx_{_txCounter++}";

        /// <summary>Fired after each RecordSale call (host-only).</summary>
        public static event Action OnSaleRecorded;

        /// <summary>Returns all recorded sales.</summary>
        public List<OtcSaleRecord> GetSalesLog() => _salesLog;

        /// <summary>Removes sales older than 7 days from the current day.</summary>
        public void TrimSalesLog(int currentDay)
        {
            int cutoff = currentDay - 7;
            _salesLog.RemoveAll(s => s.GameDay < cutoff);
        }

        /// <summary>Serializes the sales log as a newline-delimited string for P2P sync.</summary>
        internal string SerializeSalesLog()
        {
            if (_salesLog.Count == 0) return "";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _salesLog.Count; i++)
            {
                var s = _salesLog[i];
                if (i > 0) sb.Append('\n');
                sb.Append(EscapeField(s.ProductId)).Append('|');
                sb.Append(EscapeField(s.ProductName)).Append('|');
                sb.Append(s.Quantity).Append('|');
                sb.Append(s.PricePerUnit.ToString("R", ci)).Append('|');
                sb.Append(s.QualityLevel).Append('|');
                sb.Append(s.GameDay).Append('|');
                sb.Append(EscapeField(s.CustomerName)).Append('|');
                sb.Append(s.GameHour).Append('|');
                sb.Append(EscapeField(s.TransactionId)).Append('|');
                sb.Append(s.TipAmount.ToString("R", ci)).Append('|');
                sb.Append(EscapeField(s.BuildingId));
            }
            return sb.ToString();
        }

        /// <summary>Replaces the local sales log with data received from the host via P2P.</summary>
        internal void ApplySalesLog(string payload)
        {
            _salesLog.Clear();
            if (string.IsNullOrEmpty(payload)) return;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var lines = payload.Split('\n');
            foreach (var line in lines)
            {
                var f = line.Split('|');
                if (f.Length < 11) continue;
                float.TryParse(f[3], System.Globalization.NumberStyles.Float, ci, out float price);
                int.TryParse(f[2], out int qty);
                int.TryParse(f[4], out int quality);
                int.TryParse(f[5], out int day);
                int.TryParse(f[7], out int hour);
                float.TryParse(f[9], System.Globalization.NumberStyles.Float, ci, out float tip);
                _salesLog.Add(new OtcSaleRecord
                {
                    ProductId = UnescapeField(f[0]),
                    ProductName = UnescapeField(f[1]),
                    Quantity = qty,
                    PricePerUnit = price,
                    QualityLevel = quality,
                    GameDay = day,
                    CustomerName = string.IsNullOrEmpty(f[6]) ? null : UnescapeField(f[6]),
                    GameHour = hour,
                    TransactionId = string.IsNullOrEmpty(f[8]) ? null : UnescapeField(f[8]),
                    TipAmount = tip,
                    BuildingId = string.IsNullOrEmpty(f[10]) ? null : UnescapeField(f[10])
                });
            }
            OnSaleRecorded?.Invoke();
        }

        /// <summary>Escapes pipe and newline characters in a field for delimited serialization.</summary>
        private static string EscapeField(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.IndexOfAny(_escapeChars) < 0) return value;
            var sb = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '|':  sb.Append("\\P"); break;
                    case '\n': sb.Append("\\n"); break;
                    default:   sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static string UnescapeField(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (value.IndexOf('\\') < 0) return value;
            var sb = new System.Text.StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length)
                {
                    switch (value[i + 1])
                    {
                        case '\\': sb.Append('\\'); i++; break;
                        case 'P':  sb.Append('|'); i++; break;
                        case 'n':  sb.Append('\n'); i++; break;
                        default:   sb.Append(value[i]); break;
                    }
                }
                else sb.Append(value[i]);
            }
            return sb.ToString();
        }

        private static readonly char[] _escapeChars = { '\\', '|', '\n' };

        // ==================================================================
        // Inventory snapshots (for GreenTab overview chart)
        // ==================================================================

        private const int MaxSnapshotDays = 7;

        /// <summary>Records a daily inventory snapshot. Keeps only the last 7 days.</summary>
        public void RecordInventorySnapshot(int gameDay, int totalCount)
        {
            // Update existing entry for this day, or append new
            var existing = _inventorySnapshots.FirstOrDefault(s => s.GameDay == gameDay);
            if (existing != null)
            {
                existing.TotalCount = totalCount;
                return;
            }

            _inventorySnapshots.Add(new OtcInventorySnapshot
            {
                GameDay = gameDay,
                TotalCount = totalCount
            });

            // Trim to last 7 entries
            while (_inventorySnapshots.Count > MaxSnapshotDays)
                _inventorySnapshots.RemoveAt(0);
        }

        /// <summary>Returns all inventory snapshots (up to 7 days).</summary>
        public List<OtcInventorySnapshot> GetInventorySnapshots() => _inventorySnapshots;

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

        /// <summary>Creates the warehouse property record and adds the listing to the message thread.</summary>
        public void EnsureWarehouseListing()
        {
            GetOrCreateProperty(WarehouseId);

            var thread = StaticThreadSaveData.Instance;
            if (thread == null) return;

            thread.AddMessage("warehouse_intro",
                "Got a proposition. I know a warehouse, shared space, few people store " +
                "product there. Nobody asks questions. You could set up in the extra bay, " +
                "move your harder stuff through it. Less heat than a storefront. Interested?");

            thread.AddEmbed(new OtcPropertyMessage
            {
                Id = $"{WarehouseId}_card",
                EmbedTitle = "Warehouse",
                EmbedDescription = "Shared warehouse space. Low-profile distribution point " +
                                   "for products that draw attention.",
                EmbedItems = new List<string> { $"${Config.WarehousePurchasePrice.Value:N0}" },
                EmbedLocation = "Westville",
                EmbedImageResource = "OverTheCounter.Resources.WarehousePhoto.png",
                EmbedButtonLabel = $"Pay ${Config.WarehousePurchasePrice.Value:N0}",
                EmbedButtonAction = "purchase_warehouse"
            });
        }

        /// <summary>Creates the dispensary property record and adds the listing to the message thread.</summary>
        public void EnsureDispensaryListing()
        {
            GetOrCreateProperty(DispensaryId);

            var thread = StaticThreadSaveData.Instance;
            if (thread == null) return;

            thread.AddMessage("dispensary_intro",
                "One more thing. Contact of mine has a bigger dispensary available. " +
                "Proper setup with more space, more storage, more customers. Good for " +
                "scaling the legal side while the warehouse handles the rest.");

            thread.AddEmbed(new OtcPropertyMessage
            {
                Id = $"{DispensaryId}_card",
                EmbedTitle = "Big Dispensary",
                EmbedDescription = "Full-size dispensary. More floor space, storage, " +
                                   "and customer capacity.",
                EmbedItems = new List<string> { $"${Config.DispensaryPurchasePrice.Value:N0}" },
                EmbedLocation = "Westville",
                EmbedImageResource = "OverTheCounter.Resources.DispensaryPhoto.png",
                EmbedButtonLabel = $"Pay ${Config.DispensaryPurchasePrice.Value:N0}",
                EmbedButtonAction = "purchase_dispensary"
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
            {
                WestvilleShack.UnlockDoor();
                CreateStorefrontQuest();
            }
            else if (propertyId == DispensaryId)
                Dispensary.UnlockDoor();
            else if (propertyId == WarehouseId)
                OTCWarehouse.UnlockDoor();

            ConfigSyncData.MarkGameStateDirty();
        }

        private static void CreateStorefrontQuest()
        {
            if (StorefrontGrowthQuest.Instance != null) return;
            try
            {
                var quest = (StorefrontGrowthQuest)S1API.Quests.QuestManager
                    .CreateQuest<StorefrontGrowthQuest>();
                if (quest == null)
                {
                    OTCLog.Error(OTCLog.Systems.Quest,
                        "CreateQuest<StorefrontGrowthQuest> returned null.");
                    return;
                }
                quest.Initialize();
                quest.StartQuest();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest,
                    $"CreateStorefrontQuest failed: {ex.Message}");
            }
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
                    {
                        CheckoutCounter.SpawnOnGrid(grid, coord, item.Rotation);

                        // Apply saved desk style before the deferred visual fires (next frame)
                        if (!string.IsNullOrEmpty(item.DeskStyleId))
                        {
                            var counter = CheckoutCounter.AllCounters.Count > 0
                                ? CheckoutCounter.AllCounters[CheckoutCounter.AllCounters.Count - 1]
                                : null;
                            if (counter != null)
                                counter.CurrentDeskStyleId = item.DeskStyleId;
                        }
                    }
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
            else if (propertyId == DispensaryId)
                Dispensary.UnlockDoor();
            else if (propertyId == WarehouseId)
                OTCWarehouse.UnlockDoor();
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

                        // Capture desk style for checkout counters
                        if (placed.PrefabId == "otc_checkout_counter")
                        {
                            var counter = CheckoutCounter.GetCounterByGameObject(item.gameObject);
                            if (counter != null)
                                placed.DeskStyleId = counter.CurrentDeskStyleId;
                        }

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
