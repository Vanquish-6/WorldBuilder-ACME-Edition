using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Numerics;

namespace WorldBuilder.Lib.Settings {
    [SettingCategory("Landscape Editor", Order = 1)]
    public partial class LandscapeEditorSettings : ObservableObject {
        private CameraSettings _camera = new();
        public CameraSettings Camera { get => _camera; set => SetProperty(ref _camera, value); }

        private RenderingSettings _rendering = new();
        public RenderingSettings Rendering { get => _rendering; set => SetProperty(ref _rendering, value); }

        private GridSettings _grid = new();
        public GridSettings Grid { get => _grid; set => SetProperty(ref _grid, value); }

        private OverlaySettings _overlay = new();
        public OverlaySettings Overlay { get => _overlay; set => SetProperty(ref _overlay, value); }

        private SelectionSettings _selection = new();
        public SelectionSettings Selection { get => _selection; set => SetProperty(ref _selection, value); }

        private UIStateSettings _uiState = new();
        public UIStateSettings UIState { get => _uiState; set => SetProperty(ref _uiState, value); }

        public LandscapeEditorSettings() {
        }
    }

    [SettingCategory("Camera", ParentCategory = "Landscape Editor", Order = 0)]
    public partial class CameraSettings : ObservableObject {
        [SettingDescription("Maximum distance for rendering objects in the scene")]
        [SettingRange(100, 100000, 100, 500)]
        [SettingFormat("{0:F0}")]
        [SettingOrder(0)]
        private float _maxDrawDistance = 4000f;
        public float MaxDrawDistance { get => _maxDrawDistance; set => SetProperty(ref _maxDrawDistance, value); }

        [SettingDescription("Camera field of view in degrees")]
        [SettingRange(30, 120, 1, 10)]
        [SettingFormat("{0}°")]
        [SettingOrder(1)]
        private int _fieldOfView = 60;
        public int FieldOfView { get => _fieldOfView; set => SetProperty(ref _fieldOfView, value); }

        [SettingDescription("Mouse look sensitivity multiplier")]
        [SettingRange(0.1, 5.0, 0.1, 0.5)]
        [SettingFormat("{0:F2}")]
        [SettingOrder(2)]
        private float _mouseSensitivity = 1f;
        public float MouseSensitivity { get => _mouseSensitivity; set => SetProperty(ref _mouseSensitivity, value); }

        [SettingDescription("Camera movement speed in units per second")]
        [SettingRange(1, 20000, 10, 50)]
        [SettingFormat("{0:F0}")]
        [SettingOrder(3)]
        private float _movementSpeed = 1000f;
        public float MovementSpeed { get => _movementSpeed; set => SetProperty(ref _movementSpeed, value); }

        // Persisted camera state (not shown in settings UI -- no SettingDescription)
        public float SavedPositionX { get; set; } = float.NaN;
        public float SavedPositionY { get; set; } = float.NaN;
        public float SavedPositionZ { get; set; } = float.NaN;
        public float SavedYaw { get; set; } = float.NaN;
        public float SavedPitch { get; set; } = float.NaN;
        public float SavedOrthoSize { get; set; } = float.NaN;
        public bool SavedIs3D { get; set; } = false;

        public bool HasSavedPosition => !float.IsNaN(SavedPositionX);
    }

    [SettingCategory("Rendering", ParentCategory = "Landscape Editor", Order = 1)]
    public partial class RenderingSettings : ObservableObject {
        [SettingDescription("Intensity of the scene lighting")]
        [SettingRange(0.0, 2.0, 0.05, 0.2)]
        [SettingFormat("{0:F2}")]
        [SettingOrder(1)]
        private float _lightIntensity = 0.45f;
        public float LightIntensity { get => _lightIntensity; set => SetProperty(ref _lightIntensity, value); }
    }

    [SettingCategory("Grid", ParentCategory = "Landscape Editor", Order = 2)]
    public partial class GridSettings : ObservableObject {
        [SettingDescription("Display grid overlay on terrain")]
        [SettingOrder(0)]
        private bool _showGrid = true;
        public bool ShowGrid { get => _showGrid; set => SetProperty(ref _showGrid, value); }

        [SettingDescription("Width of grid lines in pixels")]
        [SettingRange(0.5, 5.0, 0.1, 0.5)]
        [SettingFormat("{0:F1}px")]
        [SettingOrder(1)]
        private float _lineWidth = 1f;
        public float LineWidth { get => _lineWidth; set => SetProperty(ref _lineWidth, value); }

        [SettingDescription("Opacity of grid overlay")]
        [SettingRange(0.0, 1.0, 0.05, 0.1)]
        [SettingFormat("{0:P0}")]
        [SettingOrder(2)]
        private float _opacity = .40f;
        public float Opacity { get => _opacity; set => SetProperty(ref _opacity, value); }

        [SettingDescription("Color of the landblock grid lines (RGB values 0-1)")]
        [SettingDisplayName("Landblock Grid Color")]
        [SettingOrder(3)]
        private Vector3 _landblockColor = new(1.0f, 0f, 1.0f);
        public Vector3 LandblockColor { get => _landblockColor; set => SetProperty(ref _landblockColor, value); }

        [SettingDescription("Color of the cell grid lines (RGB values 0-1)")]
        [SettingDisplayName("Cell Grid Color")]
        [SettingOrder(4)]
        private Vector3 _cellColor = new(0f, 1f, 1f);
        public Vector3 CellColor { get => _cellColor; set => SetProperty(ref _cellColor, value); }
    }

    [SettingCategory("Overlays", ParentCategory = "Landscape Editor", Order = 3)]
    public partial class OverlaySettings : ObservableObject {
        [SettingDescription("Show static objects in the viewport")]
        [SettingOrder(0)]
        private bool _showStaticObjects = true;
        public bool ShowStaticObjects { get => _showStaticObjects; set => SetProperty(ref _showStaticObjects, value); }

        [SettingDescription("Show auto-generated scenery objects in the viewport")]
        [SettingOrder(1)]
        private bool _showScenery = true;
        public bool ShowScenery { get => _showScenery; set => SetProperty(ref _showScenery, value); }

        [SettingDescription("Show dungeon room geometry in the viewport")]
        [SettingOrder(2)]
        private bool _showDungeons = true;
        public bool ShowDungeons { get => _showDungeons; set => SetProperty(ref _showDungeons, value); }

        [SettingDescription("Highlight unwalkable slopes with a color overlay")]
        [SettingOrder(3)]
        private bool _showSlopeHighlight = false;
        public bool ShowSlopeHighlight { get => _showSlopeHighlight; set => SetProperty(ref _showSlopeHighlight, value); }

        [SettingDescription("Slope angle threshold in degrees above which terrain is considered unwalkable")]
        [SettingRange(5.0, 85.0, 1.0, 5.0)]
        [SettingFormat("{0:F0}°")]
        [SettingOrder(4)]
        private float _slopeThreshold = 45f;
        public float SlopeThreshold { get => _slopeThreshold; set => SetProperty(ref _slopeThreshold, value); }

        [SettingDescription("Color for unwalkable slope highlighting (RGB values 0-1)")]
        [SettingDisplayName("Slope Highlight Color")]
        [SettingOrder(5)]
        private Vector3 _slopeHighlightColor = new(1.0f, 0.2f, 0.2f);
        public Vector3 SlopeHighlightColor { get => _slopeHighlightColor; set => SetProperty(ref _slopeHighlightColor, value); }

        [SettingDescription("Opacity of the slope highlight overlay")]
        [SettingRange(0.0, 1.0, 0.05, 0.1)]
        [SettingFormat("{0:P0}")]
        [SettingOrder(6)]
        private float _slopeHighlightOpacity = 0.5f;
        public float SlopeHighlightOpacity { get => _slopeHighlightOpacity; set => SetProperty(ref _slopeHighlightOpacity, value); }
    }

    [SettingCategory("Selection", ParentCategory = "Landscape Editor", Order = 4)]
    public partial class SelectionSettings : ObservableObject {
        [SettingDescription("Color of the selection sphere indicator (RGB values 0-1)")]
        [SettingDisplayName("Sphere Color")]
        [SettingOrder(0)]
        private Vector3 _sphereColor = new(1.0f, 1.0f, 1.0f);
        public Vector3 SphereColor { get => _sphereColor; set => SetProperty(ref _sphereColor, value); }

        [SettingDescription("Radius of the selection sphere in units")]
        [SettingDisplayName("Sphere Radius")]
        [SettingRange(0.1, 20.0, 0.1, 1.0)]
        [SettingFormat("{0:F1}")]
        [SettingOrder(1)]
        private float _sphereRadius = 4.6f;
        public float SphereRadius { get => _sphereRadius; set => SetProperty(ref _sphereRadius, value); }
    }

    /// <summary>
    /// Persisted UI state (not shown in settings UI).
    /// </summary>
    public partial class UIStateSettings : ObservableObject {
        /// <summary>Last selected tool index (0=Selector, 1=Terrain, 2=Road, 3=Height)</summary>
        public int LastToolIndex { get; set; } = 0;
        /// <summary>Last selected sub-tool index within the tool</summary>
        public int LastSubToolIndex { get; set; } = 0;
        /// <summary>Width of the left panel (object browser / texture palette)</summary>
        public double LeftPanelWidth { get; set; } = 280;
        /// <summary>Width of the right panel (tools / layers / history)</summary>
        public double RightPanelWidth { get; set; } = 250;

        /// <summary>Persisted state of dockable panels.</summary>
        public System.Collections.Generic.List<DockingPanelState> DockingLayout { get; set; } = new();
    }

    public class DockingPanelState {
        public string Id { get; set; } = "";
        public string Location { get; set; } = "Left";
        public bool IsVisible { get; set; } = true;
    }
}