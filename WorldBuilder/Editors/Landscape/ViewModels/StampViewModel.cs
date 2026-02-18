using System;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class StampViewModel : ObservableObject {
        public TerrainStamp Stamp { get; }

        private Bitmap? _thumbnail;
        public Bitmap? Thumbnail {
            get {
                if (_thumbnail == null) {
                    LoadThumbnail();
                }
                return _thumbnail;
            }
            private set => SetProperty(ref _thumbnail, value);
        }

        public string Name => Stamp.Name;
        public string Description => Stamp.Description;
        public DateTime Created => Stamp.Created;
        public int WidthInVertices => Stamp.WidthInVertices;
        public int HeightInVertices => Stamp.HeightInVertices;

        public StampViewModel(TerrainStamp stamp) {
            Stamp = stamp;
        }

        private void LoadThumbnail() {
            if (string.IsNullOrEmpty(Stamp.Filename)) return;

            var thumbPath = Path.ChangeExtension(Stamp.Filename, ".png");
            if (File.Exists(thumbPath)) {
                try {
                    // Read to memory stream to verify we don't lock the file
                    var bytes = File.ReadAllBytes(thumbPath);
                    using var stream = new MemoryStream(bytes);
                    Thumbnail = new Bitmap(stream);
                }
                catch (Exception ex) {
                    Console.WriteLine($"[StampViewModel] Error loading thumbnail: {ex.Message}");
                }
            }
        }
    }
}
