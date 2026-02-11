using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DatReaderWriter.Enums;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class TerrainTextureItem : ViewModelBase {
        public TerrainTextureType TextureType { get; }
        public Bitmap? Thumbnail { get; }
        public string DisplayName { get; }

        [ObservableProperty]
        private bool _isSelected;

        public TerrainTextureItem(TerrainTextureType type, Bitmap? thumbnail) {
            TextureType = type;
            Thumbnail = thumbnail;
            DisplayName = type.ToString();
        }
    }

    public partial class TerrainTexturePaletteViewModel : ViewModelBase {
        public ObservableCollection<TerrainTextureItem> Textures { get; } = new();

        [ObservableProperty]
        private TerrainTextureItem? _selectedTexture;

        /// <summary>
        /// Fires when the user selects a different texture in the palette.
        /// </summary>
        public event EventHandler<TerrainTextureType>? TextureSelected;

        public TerrainTexturePaletteViewModel(LandSurfaceManager surfaceManager) {
            var thumbnails = surfaceManager.GetTerrainThumbnails(64);
            var available = surfaceManager.GetAvailableTerrainTextures();

            foreach (var desc in available) {
                thumbnails.TryGetValue(desc.TerrainType, out var thumb);
                Textures.Add(new TerrainTextureItem(desc.TerrainType, thumb));
            }

            if (Textures.Count > 0) {
                SelectedTexture = Textures[0];
                SelectedTexture.IsSelected = true;
            }
        }

        [RelayCommand]
        private void SelectTexture(TerrainTextureItem item) {
            if (SelectedTexture != null) {
                SelectedTexture.IsSelected = false;
            }
            SelectedTexture = item;
            item.IsSelected = true;
            TextureSelected?.Invoke(this, item.TextureType);
        }

        /// <summary>
        /// Syncs the palette selection to match an externally-set texture type
        /// (e.g. when switching back to a tool that already had a texture selected).
        /// </summary>
        public void SyncSelection(TerrainTextureType type) {
            var match = Textures.FirstOrDefault(t => t.TextureType == type);
            if (match != null && match != SelectedTexture) {
                if (SelectedTexture != null) SelectedTexture.IsSelected = false;
                SelectedTexture = match;
                match.IsSelected = true;
            }
        }
    }
}
