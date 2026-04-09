using System.Collections.Generic;

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Defines a floor material style for the dispensary.
    /// Each style maps to a game material name resolved via Materials.Find at runtime.
    /// </summary>
    public class FloorStyle
    {
        public string Id { get; }
        public string DisplayName { get; }
        public float Cost { get; }
        public string MaterialName { get; }

        public FloorStyle(string id, string displayName, float cost, string materialName)
        {
            Id = id;
            DisplayName = displayName;
            Cost = cost;
            MaterialName = materialName;
        }

        // ---- Registry ----

        public static readonly FloorStyle WoodPlanksBrown = new("wood_planks_brown", "Wood Planks", 0f, "wood planks medium brown mat");
        public static readonly FloorStyle TilesLightGrey = new("tiles_light_grey", "Light Grey Tiles", 500f, "tiles light grey");
        public static readonly FloorStyle MansionFloor = new("mansion_floor", "Mansion Floor", 1500f, "mansion_floor");
        public static readonly FloorStyle ConcreteBeige = new("concrete_beige", "Beige Concrete", 300f, "concrete light beige");
        public static readonly FloorStyle TilesBlack = new("tiles_black", "Black Tiles", 600f, "tiles_black");
        public static readonly FloorStyle ConcreteBlack = new("concrete_black", "Black Concrete", 400f, "concrete black");
        public static readonly FloorStyle MetalDarkGrey = new("metal_dark_grey", "Dark Metal", 700f, "metal_darkgrey_mat");
        public static readonly FloorStyle WoodPlanksBlack = new("wood_planks_black", "Black Wood", 800f, "wood planks black mat");
        public static readonly FloorStyle ConcreteGrey = new("concrete_grey", "Grey Concrete", 200f, "concrete_grey0");
        public static readonly FloorStyle ConcreteCrimson = new("concrete_crimson", "Crimson Concrete", 500f, "concrete crimson");
        public static readonly FloorStyle ConcreteNavy = new("concrete_navy", "Navy Concrete", 500f, "concrete deep navy blue");
        public static readonly FloorStyle SmallTileWhite = new("small_tile_white", "White Tile", 400f, "small tile dirty white");
        public static readonly FloorStyle WoodBeige = new("wood_beige", "Beige Wood", 600f, "wood_beige");
        public static readonly FloorStyle OffWhite = new("off_white", "Off White", 300f, "off white");

        public static readonly FloorStyle Default = WoodPlanksBrown;

        private static readonly Dictionary<string, FloorStyle> _registry = new()
        {
            { WoodPlanksBrown.Id, WoodPlanksBrown },
            { TilesLightGrey.Id, TilesLightGrey },
            { MansionFloor.Id, MansionFloor },
            { ConcreteBeige.Id, ConcreteBeige },
            { TilesBlack.Id, TilesBlack },
            { ConcreteBlack.Id, ConcreteBlack },
            { MetalDarkGrey.Id, MetalDarkGrey },
            { WoodPlanksBlack.Id, WoodPlanksBlack },
            { ConcreteGrey.Id, ConcreteGrey },
            { ConcreteCrimson.Id, ConcreteCrimson },
            { ConcreteNavy.Id, ConcreteNavy },
            { SmallTileWhite.Id, SmallTileWhite },
            { WoodBeige.Id, WoodBeige },
            { OffWhite.Id, OffWhite },
        };

        public static IReadOnlyDictionary<string, FloorStyle> All => _registry;

        public static FloorStyle Get(string id)
        {
            if (id != null && _registry.TryGetValue(id, out var style))
                return style;
            return Default;
        }
    }
}
