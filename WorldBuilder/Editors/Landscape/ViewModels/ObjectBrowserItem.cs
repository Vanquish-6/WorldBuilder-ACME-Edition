using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    /// <summary>
    /// Represents a single object entry in the Object Browser thumbnail grid.
    /// </summary>
    public partial class ObjectBrowserItem : ObservableObject {
        /// <summary>
        /// The DAT object ID (Setup or GfxObj).
        /// </summary>
        public uint Id { get; }

        /// <summary>
        /// True if this is a Setup object, false if GfxObj.
        /// </summary>
        public bool IsSetup { get; }

        /// <summary>
        /// Formatted hex display string (e.g. "Setup 0x02001234" or "GfxObj 0x01001234").
        /// </summary>
        public string DisplayId { get; }

        /// <summary>
        /// Keyword tags for tooltip display (e.g. "tree, oak, large"). Null if no tags.
        /// </summary>
        public string? Tags { get; }

        /// <summary>
        /// Rendered thumbnail bitmap. Null until the thumbnail has been generated or loaded from cache.
        /// The UI shows a placeholder when this is null.
        /// </summary>
        [ObservableProperty]
        private Bitmap? _thumbnail;

        public ObjectBrowserItem(uint id, bool isSetup, string? tags) {
            Id = id;
            IsSetup = isSetup;
            Tags = tags;
            DisplayId = isSetup ? $"Setup  0x{id:X8}" : $"GfxObj 0x{id:X8}";
        }
    }
}
