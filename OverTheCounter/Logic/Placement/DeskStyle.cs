using System.Collections.Generic;
using UnityEngine;

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Defines a checkout counter desk style with MeshVault mesh ID and cost.
    /// </summary>
    public class DeskStyle
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string MeshVaultId { get; }
        public float Cost { get; }
        /// <summary>Local rotation to apply after spawning (corrects mesh coordinate space).</summary>
        public Quaternion SpawnRotation { get; }
        /// <summary>Local position offset to apply after spawning.</summary>
        public Vector3 SpawnOffset { get; }

        public DeskStyle(string id, string displayName, string meshVaultId, float cost,
            Quaternion? spawnRotation = null, Vector3? spawnOffset = null)
        {
            Id = id;
            DisplayName = displayName;
            MeshVaultId = meshVaultId;
            Cost = cost;
            SpawnRotation = spawnRotation ?? Quaternion.identity;
            SpawnOffset = spawnOffset ?? Vector3.zero;
        }

        // ---- Registry ----

        public static readonly DeskStyle OrnateDesk = new("ornate_desk", "Ornate Desk", "ornate_desk", 0f,
            Quaternion.Euler(0f, 180f, 0f));
        public static readonly DeskStyle DealershipDesk = new("otc_dealership_desk", "Modern Desk", "otc_dealership_desk", 500f,
            Quaternion.Euler(270f, 0f, 0f), new Vector3(0f, 0.04f, 0f));
        public static readonly DeskStyle MidnightDesk = new("otc_midnight_desk", "Noir & Gold", "otc_midnight_desk", 750f,
            Quaternion.Euler(270f, 0f, 0f), new Vector3(0f, 0.04f, 0f));
        public static readonly DeskStyle GlassDesk = new("otc_glass_desk", "Industrial Glass", "otc_glass_desk", 500f,
            Quaternion.Euler(270f, 0f, 0f), new Vector3(0f, 0.04f, 0f));
        public static readonly DeskStyle LedDesk = new("otc_led_desk", "High-Tech LED", "otc_led_desk", 750f,
            Quaternion.Euler(270f, 0f, 0f), new Vector3(0f, 0.04f, 0f));

        public static readonly DeskStyle Default = OrnateDesk;

        private static readonly Dictionary<string, DeskStyle> _registry = new()
        {
            { OrnateDesk.Id, OrnateDesk },
            { DealershipDesk.Id, DealershipDesk },
            { MidnightDesk.Id, MidnightDesk },
            { GlassDesk.Id, GlassDesk },
            { LedDesk.Id, LedDesk },
        };

        /// <summary>All registered desk styles.</summary>
        public static IReadOnlyDictionary<string, DeskStyle> All => _registry;

        /// <summary>Look up a style by ID, falling back to default.</summary>
        public static DeskStyle Get(string id)
        {
            if (id != null && _registry.TryGetValue(id, out var style))
                return style;
            return Default;
        }
    }
}
