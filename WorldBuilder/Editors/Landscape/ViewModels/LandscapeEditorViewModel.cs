using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Chorizite.OpenGLSDLBackend;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class LandscapeEditorViewModel : ViewModelBase {
        [ObservableProperty] private ObservableCollection<ToolViewModelBase> _tools = new();

        [ObservableProperty]
        private SubToolViewModelBase? _selectedSubTool;

        [ObservableProperty]
        private ToolViewModelBase? _selectedTool;

        [ObservableProperty]
        private HistorySnapshotPanelViewModel? _historySnapshotPanel;

        [ObservableProperty]
        private LayersViewModel? _layersPanel;

        [ObservableProperty]
        private ObjectBrowserViewModel? _objectBrowser;

        [ObservableProperty]
        private string _currentPositionText = "";

        // Overlay toggle properties (bound to toolbar buttons)
        public bool ShowGrid {
            get => Settings.Landscape.Grid.ShowGrid;
            set { Settings.Landscape.Grid.ShowGrid = value; OnPropertyChanged(); }
        }

        public bool ShowStaticObjects {
            get => Settings.Landscape.Overlay.ShowStaticObjects;
            set { Settings.Landscape.Overlay.ShowStaticObjects = value; OnPropertyChanged(); }
        }

        public bool ShowScenery {
            get => Settings.Landscape.Overlay.ShowScenery;
            set { Settings.Landscape.Overlay.ShowScenery = value; OnPropertyChanged(); }
        }

        public bool ShowSlopeHighlight {
            get => Settings.Landscape.Overlay.ShowSlopeHighlight;
            set { Settings.Landscape.Overlay.ShowSlopeHighlight = value; OnPropertyChanged(); }
        }

        private Project? _project;
        private IDatReaderWriter? _dats;
        public TerrainSystem? TerrainSystem { get; private set; }
        public WorldBuilderSettings Settings { get; }

        private readonly ILogger<TerrainSystem> _logger;

        public LandscapeEditorViewModel(WorldBuilderSettings settings, ILogger<TerrainSystem> logger) {
            Settings = settings;
            _logger = logger;
        }

        internal void Init(Project project, OpenGLRenderer render, Avalonia.PixelSize canvasSize) {
            _dats = project.DocumentManager.Dats;
            _project = project;

            TerrainSystem = new TerrainSystem(render, project, _dats, Settings, _logger);

            Tools.Add(TerrainSystem.Services.GetRequiredService<SelectorToolViewModel>());
            Tools.Add(TerrainSystem.Services.GetRequiredService<TexturePaintingToolViewModel>());
            Tools.Add(TerrainSystem.Services.GetRequiredService<RoadDrawingToolViewModel>());
            Tools.Add(TerrainSystem.Services.GetRequiredService<HeightToolViewModel>());

            if (Tools.Count > 0 && Tools[0].AllSubTools.Count > 0) {
                SelectSubTool(Tools[0].AllSubTools[0]);
            }

            var documentStorageService = project.DocumentManager.DocumentStorageService;
            HistorySnapshotPanel = new HistorySnapshotPanelViewModel(TerrainSystem, documentStorageService, TerrainSystem.History);
            LayersPanel = new LayersViewModel(TerrainSystem);
            ObjectBrowser = new ObjectBrowserViewModel(
                TerrainSystem.EditingContext, _dats,
                TerrainSystem.Scene.ThumbnailService);
            ObjectBrowser.PlacementRequested += OnPlacementRequested;

            UpdateTerrain(canvasSize);
        }

        internal void DoRender(Avalonia.PixelSize canvasSize) {
            if (TerrainSystem == null) return;

            UpdateTerrain(canvasSize);

            TerrainSystem.Scene.Render(
                TerrainSystem.Scene.CameraManager.Current,
                (float)canvasSize.Width / canvasSize.Height,
                TerrainSystem.EditingContext,
                canvasSize.Width,
                canvasSize.Height);
        }

        private void UpdateTerrain(Avalonia.PixelSize canvasSize) {
            if (TerrainSystem == null) return;

            TerrainSystem.Scene.CameraManager.Current.ScreenSize = new Vector2(canvasSize.Width, canvasSize.Height);
            var view = TerrainSystem.Scene.CameraManager.Current.GetViewMatrix();
            var projection = TerrainSystem.Scene.CameraManager.Current.GetProjectionMatrix();
            var viewProjection = view * projection;

            TerrainSystem.Update(TerrainSystem.Scene.CameraManager.Current.Position, viewProjection);

            TerrainSystem.EditingContext.ClearModifiedLandblocks();

            // Update position HUD
            var cam = TerrainSystem.Scene.CameraManager.Current.Position;
            uint lbX = (uint)Math.Max(0, cam.X / TerrainDataManager.LandblockLength);
            uint lbY = (uint)Math.Max(0, cam.Y / TerrainDataManager.LandblockLength);
            lbX = Math.Clamp(lbX, 0, TerrainDataManager.MapSize - 1);
            lbY = Math.Clamp(lbY, 0, TerrainDataManager.MapSize - 1);
            ushort lbId = (ushort)((lbX << 8) | lbY);
            CurrentPositionText = $"LB: {lbId:X4}  ({lbX}, {lbY})";
        }

        [RelayCommand]
        private void SelectTool(ToolViewModelBase tool) {
            if (tool == SelectedTool) return;

            SelectedTool?.OnDeactivated();
            SelectedSubTool?.OnDeactivated();
            if (SelectedSubTool != null) SelectedSubTool.IsSelected = false;
            if (SelectedTool != null) SelectedTool.IsSelected = false;

            SelectedTool = tool;
            SelectedTool.IsSelected = true;

            // Select the first sub-tool by default
            if (tool.AllSubTools.Count > 0) {
                var firstSub = tool.AllSubTools[0];
                SelectedSubTool = firstSub;
                tool.OnActivated();
                tool.ActivateSubTool(firstSub);
                firstSub.IsSelected = true;
            }
        }

        [RelayCommand]
        private void SelectSubTool(SubToolViewModelBase subTool) {
            var parentTool = Tools.FirstOrDefault(t => t.AllSubTools.Contains(subTool));

            if (parentTool != SelectedTool) {
                // Switching tools entirely
                SelectedTool?.OnDeactivated();
                if (SelectedTool != null) SelectedTool.IsSelected = false;
            }

            SelectedSubTool?.OnDeactivated();
            if (SelectedSubTool != null) SelectedSubTool.IsSelected = false;

            SelectedTool = parentTool;
            if (parentTool != null) parentTool.IsSelected = true;
            SelectedSubTool = subTool;
            parentTool?.OnActivated();
            parentTool?.ActivateSubTool(subTool);
            SelectedSubTool.IsSelected = true;
        }

        [RelayCommand]
        public async Task GotoLandblock() {
            if (TerrainSystem == null) return;

            var landblockId = await ShowGotoLandblockDialog();
            if (landblockId == null) return;

            NavigateToLandblock(landblockId.Value);
        }

        public void NavigateToLandblock(ushort landblockId) {
            if (TerrainSystem == null) return;

            var lbX = (landblockId >> 8) & 0xFF;
            var lbY = landblockId & 0xFF;

            var centerX = lbX * TerrainDataManager.LandblockLength + TerrainDataManager.LandblockLength / 2f;
            var centerY = lbY * TerrainDataManager.LandblockLength + TerrainDataManager.LandblockLength / 2f;
            var height = TerrainSystem.Scene.DataManager.GetHeightAtPosition(centerX, centerY);

            var camera = TerrainSystem.Scene.CameraManager.Current;
            if (camera is OrthographicTopDownCamera) {
                camera.LookAt(new Vector3(centerX, centerY, height));
            }
            else {
                // Position perspective camera above the landblock center, looking down at it
                camera.SetPosition(centerX, centerY, height + 200f);
                camera.LookAt(new Vector3(centerX, centerY, height));
            }
        }

        private async Task<ushort?> ShowGotoLandblockDialog() {
            ushort? result = null;
            var textBox = new TextBox {
                Text = "",
                Width = 300,
                Watermark = "Hex ID (e.g. C6AC) or X,Y (e.g. 198,172)"
            };
            var errorText = new TextBlock {
                Text = "",
                Foreground = Brushes.Red,
                FontSize = 12,
                IsVisible = false
            };

            await DialogHost.Show(new StackPanel {
                Margin = new Avalonia.Thickness(20),
                Spacing = 15,
                Children = {
                    new TextBlock {
                        Text = "Go to Landblock",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock {
                        Text = "Enter a landblock ID in hex (e.g. C6AC)\nor as X,Y coordinates (e.g. 198,172).",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 300,
                        FontSize = 12,
                        Opacity = 0.7
                    },
                    textBox,
                    errorText,
                    new StackPanel {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = {
                            new Button {
                                Content = "Cancel",
                                Command = new RelayCommand(() => DialogHost.Close("MainDialogHost"))
                            },
                            new Button {
                                Content = "Go",
                                Command = new RelayCommand(() => {
                                    var parsed = ParseLandblockInput(textBox.Text);
                                    if (parsed != null) {
                                        result = parsed;
                                        DialogHost.Close("MainDialogHost");
                                    }
                                    else {
                                        errorText.Text = "Invalid input. Use hex (C6AC) or X,Y (198,172).";
                                        errorText.IsVisible = true;
                                    }
                                })
                            }
                        }
                    }
                }
            }, "MainDialogHost");

            return result;
        }

        internal static ushort? ParseLandblockInput(string? input) {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();

            // Try X,Y format
            if (input.Contains(',')) {
                var parts = input.Split(',');
                if (parts.Length == 2
                    && byte.TryParse(parts[0].Trim(), out var x)
                    && byte.TryParse(parts[1].Trim(), out var y)) {
                    return (ushort)((x << 8) | y);
                }
                return null;
            }

            // Try hex format (with or without 0x prefix)
            var hex = input;
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex[2..];

            if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexId))
                return hexId;

            return null;
        }

        private void OnPlacementRequested(object? sender, EventArgs e) {
            // Switch to the Selector tool's Select sub-tool so placement clicks are handled
            var selectorTool = Tools.OfType<SelectorToolViewModel>().FirstOrDefault();
            if (selectorTool == null) return;

            var selectSubTool = selectorTool.AllSubTools.OfType<SelectSubToolViewModel>().FirstOrDefault();
            if (selectSubTool == null) return;

            // Save placement state — tool deactivation clears it
            var sel = TerrainSystem?.EditingContext.ObjectSelection;
            var wasPlacing = sel?.IsPlacementMode ?? false;
            var preview = sel?.PlacementPreview;
            var previewMulti = sel?.PlacementPreviewMulti;

            SelectSubTool(selectSubTool);

            // Restore placement state after tool switch
            if (wasPlacing && sel != null) {
                sel.IsPlacementMode = true;
                sel.PlacementPreview = preview;
                sel.PlacementPreviewMulti = previewMulti;
            }
        }

        public void Cleanup() {
            TerrainSystem?.Dispose();
        }
    }

}