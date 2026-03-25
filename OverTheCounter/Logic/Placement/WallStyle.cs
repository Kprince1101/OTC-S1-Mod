using System.Collections.Generic;

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Defines a wall material style. Used for both exterior and interior wall selections.
    /// Each style maps to a game material name resolved via Materials.Find at runtime.
    /// </summary>
    public class WallStyle
    {
        public string Id { get; }
        public string DisplayName { get; }
        public float Cost { get; }
        public string MaterialName { get; }

        public WallStyle(string id, string displayName, float cost, string materialName)
        {
            Id = id;
            DisplayName = displayName;
            Cost = cost;
            MaterialName = materialName;
        }

        // ---- Exterior wall styles ----

        public static readonly WallStyle ExtDefault = new("ext_brick_red", "Red Brick", 0f, "brick red");
        public static readonly WallStyle ExtGraniteSalmon = new("granite_salmon", "Salmon Granite", 600f, "granite dull salmon lighter");
        public static readonly WallStyle ExtMansion = new("mansion_ext", "Mansion Exterior", 1500f, "mansion_exteriorwall");
        public static readonly WallStyle ExtConcreteCharcoal = new("ext_concrete_charcoal", "Charcoal Concrete", 400f, "concrete charcoal");
        public static readonly WallStyle ExtMetalDarkGrey = new("ext_metal_darkgrey", "Dark Metal", 700f, "metal_verydarkgrey_mat");
        public static readonly WallStyle ExtBrickDarkGrey = new("brick_dark_grey", "Dark Grey Brick", 300f, "brick dark grey");
        public static readonly WallStyle ExtBrickBlue = new("brick_blue", "Police Blue Brick", 500f, "brick police blue");
        public static readonly WallStyle ExtBrickWarehouse = new("brick_warehouse", "Warehouse Brick", 400f, "brick warehouse");
        public static readonly WallStyle ExtAlumGrey = new("ext_alum_grey", "Aluminium Grey", 700f, "alum sheet med grey");

        private static readonly Dictionary<string, WallStyle> _exteriorRegistry = new()
        {
            { ExtDefault.Id, ExtDefault },
            { ExtGraniteSalmon.Id, ExtGraniteSalmon },
            { ExtMansion.Id, ExtMansion },
            { ExtConcreteCharcoal.Id, ExtConcreteCharcoal },
            { ExtMetalDarkGrey.Id, ExtMetalDarkGrey },
            { ExtBrickDarkGrey.Id, ExtBrickDarkGrey },
            { ExtBrickBlue.Id, ExtBrickBlue },
            { ExtBrickWarehouse.Id, ExtBrickWarehouse },
            { ExtAlumGrey.Id, ExtAlumGrey },
        };

        public static IReadOnlyDictionary<string, WallStyle> ExteriorStyles => _exteriorRegistry;

        public static WallStyle GetExterior(string id)
        {
            if (id != null && _exteriorRegistry.TryGetValue(id, out var style))
                return style;
            return ExtDefault;
        }

        // ---- Interior wall styles ----

        public static readonly WallStyle IntDefault = new("brick_red", "Red Brick", 0f, "brick red");
        public static readonly WallStyle IntConcreteCharcoal = new("concrete_charcoal", "Charcoal Concrete", 400f, "concrete charcoal");
        public static readonly WallStyle IntStripesCharcoal = new("stripes_charcoal", "Charcoal Stripes", 600f, "wall stripes charcoal");
        public static readonly WallStyle IntMetalGreen = new("metal_green", "Green Metal", 500f, "metal warehouse trim green mat");
        public static readonly WallStyle IntWhiteLighter = new("white_lighter", "White", 300f, "white lighter");
        public static readonly WallStyle IntMansionWood = new("mansion_wood", "Mansion Wood", 1200f, "mansion_whitewood_mat");
        public static readonly WallStyle IntAlumGrey = new("alum_grey", "Aluminium Grey", 700f, "alum sheet med grey");
        public static readonly WallStyle IntTilesBlack = new("tiles_black", "Black Tiles", 500f, "tiles_black");
        public static readonly WallStyle IntMetalDarkGrey = new("metal_darkgrey", "Dark Metal", 700f, "metal_verydarkgrey_mat");
        public static readonly WallStyle IntConcreteGreen = new("concrete_green", "Green Concrete", 500f, "concrete clothing store green");
        public static readonly WallStyle IntSmallTileWhite = new("small_tile_white", "White Tile", 400f, "small tile dirty white");

        private static readonly Dictionary<string, WallStyle> _interiorRegistry = new()
        {
            { IntDefault.Id, IntDefault },
            { IntConcreteCharcoal.Id, IntConcreteCharcoal },
            { IntStripesCharcoal.Id, IntStripesCharcoal },
            { IntMetalGreen.Id, IntMetalGreen },
            { IntWhiteLighter.Id, IntWhiteLighter },
            { IntMansionWood.Id, IntMansionWood },
            { IntAlumGrey.Id, IntAlumGrey },
            { IntTilesBlack.Id, IntTilesBlack },
            { IntMetalDarkGrey.Id, IntMetalDarkGrey },
            { IntConcreteGreen.Id, IntConcreteGreen },
            { IntSmallTileWhite.Id, IntSmallTileWhite },
        };

        public static IReadOnlyDictionary<string, WallStyle> InteriorStyles => _interiorRegistry;

        public static WallStyle GetInterior(string id)
        {
            if (id != null && _interiorRegistry.TryGetValue(id, out var style))
                return style;
            return IntDefault;
        }
    }
}
