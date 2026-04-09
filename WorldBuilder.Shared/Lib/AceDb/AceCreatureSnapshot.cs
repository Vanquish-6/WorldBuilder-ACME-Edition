using System.Collections.Generic;

namespace WorldBuilder.Shared.Lib.AceDb {

    /// <summary>One row from <c>weenie_properties_texture_map</c>.</summary>
    public sealed class AceTextureMapRow {
        public byte Index { get; set; }
        public uint OldId { get; set; }
        public uint NewId { get; set; }
        /// <summary>Optional label used in SQL comments (e.g. "Tail", "Horn").</summary>
        public string Comment { get; set; } = "";
    }

    /// <summary>One row from <c>weenie_properties_anim_part</c>.</summary>
    public sealed class AceAnimPartRow {
        public byte Index { get; set; }
        public uint AnimationId { get; set; }
        /// <summary>Optional label used in SQL comments (e.g. "Invisible tail").</summary>
        public string Comment { get; set; } = "";
    }

    /// <summary>One row from <c>weenie_properties_palette</c> — an explicit sub-palette substitution.</summary>
    public sealed class AcePaletteRow {
        /// <summary>DID of the sub-palette object (0x04...) to read colors from.</summary>
        public uint SubPaletteId { get; set; }
        /// <summary>Offset into the base palette where the substitution begins.</summary>
        public uint Offset { get; set; }
        /// <summary>Number of colors to copy from the sub-palette.</summary>
        public uint Length { get; set; }
    }

    /// <summary>
    /// Combined overrides for one weenie object: texture-map, anim-part, and palette data
    /// used to build an accurate 3D preview (IndexedPalette coloring, ClothingBase tinting).
    /// </summary>
    public sealed class AceCreatureOverrides {
        public uint ObjectId { get; set; }
        public List<AceTextureMapRow> TextureMap { get; } = new();
        public List<AceAnimPartRow> AnimParts { get; } = new();

        /// <summary>DID of the base palette (weenie_properties_d_i_d type 6 = PaletteBase).</summary>
        public uint PaletteBase { get; set; }
        /// <summary>DID of the ClothingBase table (weenie_properties_d_i_d type 7 = ClothingBase).</summary>
        public uint ClothingBase { get; set; }
        /// <summary>Palette template index — selects the colour variant from each PalSet (weenie_properties_int type 3).</summary>
        public int PaletteTemplate { get; set; }
        /// <summary>Brightness multiplier applied to substituted palette colours (weenie_properties_float type 12). Default 1.0.</summary>
        public float Shade { get; set; } = 1f;
        /// <summary>Explicit sub-palette overrides from <c>weenie_properties_palette</c>.</summary>
        public List<AcePaletteRow> PaletteOverrides { get; } = new();
    }
}
