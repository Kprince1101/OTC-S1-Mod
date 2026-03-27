using System.Collections.Generic;
using UnityEngine;

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Defines a lighting style for OTC building interiors.
    /// Each style specifies ceiling fixtures and optional wall/accent lights.
    /// </summary>
    public class LightingStyle
    {
        public string Id { get; }
        public string DisplayName { get; }
        public float Cost { get; }

        /// <summary>MeshVault mesh ID for the primary ceiling fixture.</summary>
        public string CeilingMeshId { get; }
        /// <summary>Light color for the ceiling fixture's point light.</summary>
        public Color LightColor { get; }
        /// <summary>Point light range.</summary>
        public float Range { get; }
        /// <summary>Point light intensity.</summary>
        public float Intensity { get; }
        /// <summary>Local offset of the point light relative to the fixture mesh origin.</summary>
        public Vector3 LightOffset { get; }

        /// <summary>Optional MeshVault mesh ID for wall accent lights (null = none).</summary>
        public string WallMeshId { get; }
        /// <summary>Wall light color (if WallMeshId is set).</summary>
        public Color WallLightColor { get; }

        /// <summary>Whether this style uses emissive neon strips instead of/in addition to fixtures.</summary>
        public bool HasNeonStrips { get; }
        /// <summary>Neon strip emissive color (if HasNeonStrips).</summary>
        public Color NeonColor { get; }

        /// <summary>Optional custom emissive color override for ceiling fixture mesh (null = use baked material).</summary>
        public Color? CeilingEmissiveColor { get; }

        /// <summary>Number of columns in the showroom ceiling grid (3 for large fixtures, 4 for small).</summary>
        public int ShowroomColumns { get; }

        /// <summary>Local Y position for ceiling fixtures (mesh origin mount point).</summary>
        public float FixtureY { get; }

        /// <summary>Scale multiplier for ceiling fixtures (1 = default mesh size).</summary>
        public float FixtureScale { get; }

        public LightingStyle(string id, string displayName, float cost,
            string ceilingMeshId, Color lightColor, float range, float intensity,
            Vector3 lightOffset,
            string wallMeshId = null, Color? wallLightColor = null,
            bool hasNeonStrips = false, Color? neonColor = null,
            Color? ceilingEmissiveColor = null,
            int showroomColumns = 3,
            float fixtureY = 3.7f,
            float fixtureScale = 1f)
        {
            Id = id;
            DisplayName = displayName;
            Cost = cost;
            CeilingMeshId = ceilingMeshId;
            LightColor = lightColor;
            Range = range;
            Intensity = intensity;
            LightOffset = lightOffset;
            WallMeshId = wallMeshId;
            WallLightColor = wallLightColor ?? lightColor;
            HasNeonStrips = hasNeonStrips;
            NeonColor = neonColor ?? new Color(0f, 0.8f, 1f);
            CeilingEmissiveColor = ceilingEmissiveColor;
            ShowroomColumns = showroomColumns;
            FixtureY = fixtureY;
            FixtureScale = fixtureScale;
        }

        // ---- Registry ----

        public static readonly LightingStyle BrassPendant = new(
            "brass_pendant", "Brass Pendant", 0f,
            "otc_mansion_light_on", new Color(1f, 0.85f, 0.55f), 8f, 1.2f,
            new Vector3(0f, -0.3f, 0f),
            wallMeshId: "otc_wall_lantern_on", wallLightColor: new Color(1f, 0.9f, 0.7f));

        public static readonly LightingStyle Fluorescent = new(
            "fluorescent", "Fluorescent", 200f,
            "otc_flurobar_on", new Color(0.95f, 0.95f, 1f), 6f, 1.0f,
            new Vector3(0f, -0.2f, 0f),
            fixtureY: 3.87f);

        public static readonly LightingStyle ModernPanel = new(
            "modern_panel", "Modern Panel", 400f,
            "otc_segmented_light_bar_on", new Color(1f, 1f, 0.95f), 7f, 1.0f,
            new Vector3(0f, -0.5f, 0f),
            fixtureY: 4.17f);

        public static readonly LightingStyle FlushMount = new(
            "flush_mount", "Flush Mount", 300f,
            "otc_round_ceiling_light_on", new Color(1f, 1f, 0.9f), 6f, 1.0f,
            new Vector3(0f, -0.3f, 0f),
            showroomColumns: 4, fixtureY: 3.8f);

        public static readonly LightingStyle NeonTech = new(
            "neon_tech", "Neon Tech", 500f,
            "otc_flurobar_on", new Color(0.6f, 0.2f, 1f), 5f, 0.6f,
            new Vector3(0f, -0.2f, 0f),
            hasNeonStrips: true, neonColor: new Color(0f, 0.8f, 1f),
            ceilingEmissiveColor: new Color(0.6f, 0.2f, 1f),
            fixtureY: 3.87f);

        public static readonly LightingStyle Industrial = new(
            "industrial", "Industrial", 350f,
            "otc_industrial_hanging_light_on", new Color(1f, 0.85f, 0.55f), 7f, 1.1f,
            new Vector3(0f, -0.4f, 0f),
            wallMeshId: "otc_wall_lantern_on", wallLightColor: new Color(1f, 0.9f, 0.7f),
            fixtureY: 4.11f, fixtureScale: 0.5f);

        public static readonly LightingStyle Default = BrassPendant;

        private static readonly Dictionary<string, LightingStyle> _registry = new()
        {
            { BrassPendant.Id, BrassPendant },
            { Fluorescent.Id, Fluorescent },
            { ModernPanel.Id, ModernPanel },
            { FlushMount.Id, FlushMount },
            { NeonTech.Id, NeonTech },
            { Industrial.Id, Industrial },
        };

        /// <summary>All registered lighting styles.</summary>
        public static IReadOnlyDictionary<string, LightingStyle> All => _registry;

        /// <summary>Look up a style by ID, falling back to default.</summary>
        public static LightingStyle Get(string id)
        {
            if (id != null && _registry.TryGetValue(id, out var style))
                return style;
            return Default;
        }
    }
}
