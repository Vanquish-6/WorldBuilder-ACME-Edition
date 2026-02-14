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
using WorldBuilder.Lib.Input;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using DatReaderWriter.DBObjs;
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
        private TerrainTexturePaletteViewModel? _texturePalette;

        [ObservableProperty]
        private object? _leftPanelContent;

        [ObservableProperty]
        private string _leftPanelTitle = "Object Browser";

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

        public bool ShowDungeons {
            get => Settings.Landscape.Overlay.ShowDungeons;
            set { Settings.Landscape.Overlay.ShowDungeons = value; OnPropertyChanged(); }
        }

        public bool ShowSlopeHighlight {
            get => Settings.Landscape.Overlay.ShowSlopeHighlight;
            set { Settings.Landscape.Overlay.ShowSlopeHighlight = value; OnPropertyChanged(); }
        }

        private Project? _project;
        private IDatReaderWriter? _dats;
        public TerrainSystem? TerrainSystem { get; private set; }
        public WorldBuilderSettings Settings { get; }
        public InputManager InputManager { get; }

        private readonly ILogger<TerrainSystem> _logger;

        public LandscapeEditorViewModel(WorldBuilderSettings settings, ILogger<TerrainSystem> logger) {
            Settings = settings;
            _logger = logger;
            InputManager = InputManager.Instance ?? new InputManager(Settings);
        }

        internal void Init(Project project, OpenGLRenderer render, Avalonia.PixelSize canvasSize) {
            _dats = project.DocumentManager.Dats;
            _project = project;

            TerrainSystem = new TerrainSystem(render, project, _dats, Settings, _logger);

            Tools.Add(TerrainSystem.Services.GetRequiredService<SelectorToolViewModel>());
            Tools.Add(TerrainSystem.Services.GetRequiredService<TexturePaintingToolViewModel>());
            Tools.Add(TerrainSystem.Services.GetRequiredService<RoadDrawingToolViewModel>());
            Tools.Add(TerrainSystem.Services.GetRequiredService<HeightToolViewModel>());

            // Restore last selected tool/sub-tool from settings, or default to first
            var uiState = Settings.Landscape.UIState;
            int toolIdx = Math.Clamp(uiState.LastToolIndex, 0, Tools.Count - 1);
            var tool = Tools[toolIdx];
            int subIdx = Math.Clamp(uiState.LastSubToolIndex, 0, Math.Max(0, tool.AllSubTools.Count - 1));
            if (tool.AllSubTools.Count > 0) {
                SelectSubTool(tool.AllSubTools[subIdx]);
            }

            var documentStorageService = project.DocumentManager.DocumentStorageService;
            HistorySnapshotPanel = new HistorySnapshotPanelViewModel(TerrainSystem, documentStorageService, TerrainSystem.History);
            LayersPanel = new LayersViewModel(TerrainSystem);
            ObjectBrowser = new ObjectBrowserViewModel(
                TerrainSystem.EditingContext, _dats,
                TerrainSystem.Scene.ThumbnailService);
            ObjectBrowser.PlacementRequested += OnPlacementRequested;

            TexturePalette = new TerrainTexturePaletteViewModel(TerrainSystem.Scene.SurfaceManager);
            TexturePalette.TextureSelected += OnPaletteTextureSelected;

            LeftPanelContent = ObjectBrowser;
            LeftPanelTitle = "Object Browser";

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

            UpdateLeftPanel();
        }

        private void UpdateLeftPanel() {
            if (SelectedTool is TexturePaintingToolViewModel) {
                // Sync palette to whatever the active sub-tool has selected
                if (SelectedSubTool is BrushSubToolViewModel brush) {
                    TexturePalette?.SyncSelection(brush.SelectedTerrainType);
                }
                else if (SelectedSubTool is BucketFillSubToolViewModel fill) {
                    TexturePalette?.SyncSelection(fill.SelectedTerrainType);
                }
                LeftPanelContent = TexturePalette;
                LeftPanelTitle = "Terrain Textures";
            }
            else {
                LeftPanelContent = ObjectBrowser;
                LeftPanelTitle = "Object Browser";
            }
        }

        private void OnPaletteTextureSelected(object? sender, DatReaderWriter.Enums.TerrainTextureType type) {
            // Push the selected texture to the active brush/fill sub-tool
            if (SelectedSubTool is BrushSubToolViewModel brush) {
                brush.SelectedTerrainType = type;
            }
            else if (SelectedSubTool is BucketFillSubToolViewModel fill) {
                fill.SelectedTerrainType = type;
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

            UpdateLeftPanel();
        }

        [RelayCommand]
        private void ResetCamera() {
            if (TerrainSystem == null) return;

            var camera = TerrainSystem.Scene.CameraManager.Current;
            // Default: center of the map, looking down from above
            var centerX = (TerrainDataManager.MapSize / 2f) * TerrainDataManager.LandblockLength;
            var centerY = (TerrainDataManager.MapSize / 2f) * TerrainDataManager.LandblockLength;
            var height = Math.Max(TerrainSystem.Scene.DataManager.GetHeightAtPosition(centerX, centerY), 100f);

            if (camera is OrthographicTopDownCamera ortho) {
                ortho.SetPosition(centerX, centerY, height + 1000f);
                ortho.OrthographicSize = 1000f;
            }
            else if (camera is PerspectiveCamera persp) {
                persp.SetPosition(centerX, centerY, height + 500f);
                persp.LookAt(new Vector3(centerX, centerY, height));
            }
        }

        [RelayCommand]
        public async Task GotoLandblock() {
            if (TerrainSystem == null) return;

            var cellId = await ShowGotoLandblockDialog();
            if (cellId == null) return;

            NavigateToCell(cellId.Value);
        }

        /// <summary>
        /// Navigates to a full cell ID (0xLLLLCCCC) or landblock-only ID (0x0000LLLL).
        /// If the cell portion is an EnvCell (>= 0x0100), navigates the camera to the
        /// EnvCell's position underground. Otherwise, navigates to the overworld.
        /// </summary>
        public void NavigateToCell(uint fullCellId) {
            if (TerrainSystem == null) return;

            var lbId = (ushort)(fullCellId >> 16);
            var cellPart = (ushort)(fullCellId & 0xFFFF);

            // If only a landblock ID was given (no cell), go to overworld
            if (lbId == 0 && cellPart != 0) {
                // Input was 4-char hex like "C6AC" — treat cellPart as the landblock ID
                NavigateToLandblock(cellPart);
                return;
            }

            // If an EnvCell is specified (>= 0x0100), try to navigate to its position
            if (cellPart >= 0x0100 && cellPart <= 0xFFFD) {
                if (NavigateToEnvCell(lbId, cellPart)) return;
            }

            // Fallback: navigate to the overworld of the landblock
            NavigateToLandblock(lbId);
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

        /// <summary>
        /// Navigates the camera to a specific EnvCell's position within a landblock.
        /// Returns true if successful, false if the EnvCell couldn't be read.
        /// </summary>
        private bool NavigateToEnvCell(ushort landblockId, ushort cellId) {
            if (TerrainSystem == null) return false;

            var dats = TerrainSystem.Dats;
            uint fullId = ((uint)landblockId << 16) | cellId;
            if (!dats.TryGet<EnvCell>(fullId, out var envCell)) return false;

            var lbX = (landblockId >> 8) & 0xFF;
            var lbY = landblockId & 0xFF;
            var worldX = lbX * 192f + envCell.Position.Origin.X;
            var worldY = lbY * 192f + envCell.Position.Origin.Y;
            var worldZ = envCell.Position.Origin.Z;

            var camera = TerrainSystem.Scene.CameraManager.Current;
            if (camera is OrthographicTopDownCamera) {
                camera.LookAt(new Vector3(worldX, worldY, worldZ));
            }
            else {
                // Position perspective camera at the EnvCell, slightly offset
                camera.SetPosition(worldX, worldY, worldZ + 10f);
                camera.LookAt(new Vector3(worldX, worldY, worldZ));
            }
            return true;
        }

        private async Task<uint?> ShowGotoLandblockDialog() {
            uint? result = null;
            var textBox = new TextBox {
                Text = "",
                Width = 300,
                Watermark = "Hex ID (e.g. C6AC or 01D90108) or X,Y (e.g. 198,172)"
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
                        Text = "Go to Location",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock {
                        Text = "Enter a landblock ID in hex (e.g. C6AC),\na full cell ID (e.g. 01D90108),\nor X,Y coordinates (e.g. 198,172).",
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
                                    var parsed = ParseLocationInput(textBox.Text);
                                    if (parsed != null) {
                                        result = parsed;
                                        DialogHost.Close("MainDialogHost");
                                    }
                                    else {
                                        errorText.Text = "Invalid input. Use hex (C6AC or 01D90108) or X,Y (198,172).";
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

        /// <summary>
        /// Parses user input for the Go To dialog. Returns a uint where:
        /// - 4-char hex (e.g. "C6AC") returns 0x0000C6AC (landblock only, cell=0)
        /// - 8-char hex (e.g. "01D90108") returns 0x01D90108 (full cell ID)
        /// - X,Y (e.g. "198,172") returns 0x0000C6AC (landblock only)
        /// </summary>
        internal static uint? ParseLocationInput(string? input) {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();

            // Try X,Y format → landblock only
            if (input.Contains(',')) {
                var parts = input.Split(',');
                if (parts.Length == 2
                    && byte.TryParse(parts[0].Trim(), out var x)
                    && byte.TryParse(parts[1].Trim(), out var y)) {
                    return (uint)((x << 8) | y);
                }
                return null;
            }

            // Try hex format (with or without 0x prefix)
            var hex = input;
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex[2..];

            // 8-char hex → full cell ID (e.g. 01D90108 → landblock 0x01D9, cell 0x0108)
            if (hex.Length > 4 && hex.Length <= 8) {
                if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fullId))
                    return fullId;
                return null;
            }

            // 1-4 char hex → landblock only (e.g. C6AC → 0x0000C6AC)
            if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lbId))
                return lbId;

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
            // Save UI state before disposing
            if (SelectedTool != null) {
                var uiState = Settings.Landscape.UIState;
                uiState.LastToolIndex = Tools.IndexOf(SelectedTool);
                if (SelectedSubTool != null && SelectedTool.AllSubTools.Contains(SelectedSubTool)) {
                    uiState.LastSubToolIndex = SelectedTool.AllSubTools.IndexOf(SelectedSubTool);
                }
                Settings.Save();
            }

            TerrainSystem?.Dispose();
        }
    }

}