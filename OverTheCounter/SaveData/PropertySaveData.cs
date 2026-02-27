using System;
using System.Collections.Generic;
using System.Linq;
using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using S1API.Internal.Abstraction;
using S1API.Saveables;
using UnityEngine;

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
    /// </summary>
    [Serializable]
    public class OtcPlacedItem
    {
        public string BuildingId;
        public string PrefabId;
        public float CoordX;
        public float CoordZ;
        public int Rotation;
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
                WestvilleShack.UnlockDoor();
        }

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
        internal void RestorePlacedItems(string buildingId)
        {
            if (!NetworkHelper.IsHost) return;
            var grid = WestvilleShack.ShackGrid;
            if (grid == null) return;

            var items = GetPlacedItems(buildingId);
            // TODO: restore grid items when grid placement is enabled
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

        /// <summary>Adds or updates a placed item record for a building.</summary>
        public void SavePlacedItem(string buildingId, string prefabId, float coordX, float coordZ, int rotation)
        {
            var existing = _placedItems.FirstOrDefault(
                i => i.BuildingId == buildingId && i.PrefabId == prefabId);
            if (existing != null)
            {
                existing.CoordX = coordX;
                existing.CoordZ = coordZ;
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

        /// <summary>Removes a placed item record (e.g. when picked up).</summary>
        public void RemovePlacedItem(string buildingId, string prefabId)
        {
            _placedItems.RemoveAll(i => i.BuildingId == buildingId && i.PrefabId == prefabId);
        }
    }
}
