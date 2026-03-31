using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Chorizite.OpenGLSDLBackend;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Editors.Landscape.Commands;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Docking;
using WorldBuilder.Services;
using WorldBuilder.Lib.Input;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using DatReaderWriter.DBObjs;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class LandscapeEditorViewModel : ViewModelBase {
        public ObservableCollection<ViewportViewModel> Viewports { get; } = new();

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
        private CameraBookmarksPanelViewModel? _bookmarksPanel;

        [ObservableProperty]
        private WorldMapPanelViewModel? _worldMapPanel;

        [ObservableProperty]
        private object? _leftPanelContent;

        [ObservableProperty]
        private string _leftPanelTitle = "Object Browser";

        [ObservableProperty]
        private string _currentPositionText = "";

        [ObservableProperty]
        private bool _isInPlacementMode;

        [ObservableProperty]
        private string _placementStatusText = "";

        public DockingManager DockingManager { get; } = new();

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

        public bool ShowBuildingInteriors {
            get => Settings.Landscape.Overlay.ShowBuildingInteriors;
            set { Settings.Landscape.Overlay.ShowBuildingInteriors = value; OnPropertyChanged(); }
        }

        public bool ShowWeenieSpawns {
            get => Settings.Landscape.Overlay.ShowWeenieSpawns;
            set {
                Settings.Landscape.Overlay.ShowWeenieSpawns = value;
                OnPropertyChanged();
                if (value) {
                    LoadWeenieSpawnsForLoadedLandblocks();
                }
                else {
                    _weenieLoadedLandblocks.Clear();
                    _weenieLoadingLandblocks.Clear();
                    TerrainSystem?.Scene?.ClearAllWeenieSpawns();
                }
            }
        }

        public bool ShowParticles {
            get => Settings.Landscape.Overlay.ShowParticles;
            set { Settings.Landscape.Overlay.ShowParticles = value; OnPropertyChanged(); }
        }

        public bool ShowSlopeHighlight {
            get => Settings.Landscape.Overlay.ShowSlopeHighlight;
            set { Settings.Landscape.Overlay.ShowSlopeHighlight = value; OnPropertyChanged(); }
        }

        public bool SnapToGrid {
            get => Settings.Landscape.Snap.SnapToGrid;
            set { Settings.Landscape.Snap.SnapToGrid = value; OnPropertyChanged(); }
        }

        [ObservableProperty]
        private bool _showPerformanceOverlay = false;

        [ObservableProperty]
        private string _performanceText = "";

        private readonly System.Diagnostics.Stopwatch _fpsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        private int _frameCount;
        private float _lastFps;
        private long _lastPerfUpdateMs;

        private Project? _project;
        private IDatReaderWriter? _dats;
        public TerrainSystem? TerrainSystem { get; private set; }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, uint> _weenieSetupCache = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, byte> _weenieLoadedLandblocks = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, byte> _weenieLoadingLandblocks = new();

        /// <summary>
        /// Limits parallel landblock spawn queries so many open docs do not exhaust the MySQL connector pool
        /// (Connect Timeout / all pooled connections in use).
        /// </summary>
        private static readonly SemaphoreSlim WeenieSpawnDbConcurrency = new(initialCount: 8, maxCount: 8);
        /// <summary>Current project (for ACE outdoor instance placements).</summary>
        public Project? Project => _project;
        public WorldBuilderSettings Settings { get; }
        public InputManager InputManager { get; }

        private readonly ILogger<TerrainSystem> _logger;
        private readonly TextureImportService? _textureImport;

        public LandscapeEditorViewModel(WorldBuilderSettings settings, ILogger<TerrainSystem> logger, TextureImportService? textureImport = null) {
            Settings = settings;
            _logger = logger;
            _textureImport = textureImport;
            InputManager = InputManager.Instance ?? new InputManager(Settings);
        }

        internal void Init(Project project) {
            _dats = project.DocumentManager.Dats;
            _project = project;

            Viewports.Clear();
            Tools.Clear();
            DockingManager.Clear();

            TerrainSystem = new TerrainSystem(project, _dats, Settings, _logger);

            // Create default viewports
            var pCam = TerrainSystem.Scene.PerspectiveCamera;
            var orthoCam = TerrainSystem.Scene.TopDownCamera;

            var pViewport = new ViewportViewModel(pCam) { Title = "Perspective", IsActive = true, TerrainSystem = TerrainSystem };
            var orthoViewport = new ViewportViewModel(orthoCam) { Title = "Top Down", IsActive = false, TerrainSystem = TerrainSystem };

            // Wire up rendering and input
            pViewport.RenderAction = (dt, size, input) => RenderViewport(pViewport, dt, size, input);
            orthoViewport.RenderAction = (dt, size, input) => RenderViewport(orthoViewport, dt, size, input);

            pViewport.PointerWheelAction = (e, inputState) => HandleViewportWheel(pViewport, e);
            orthoViewport.PointerWheelAction = (e, inputState) => HandleViewportWheel(orthoViewport, e);

            pViewport.KeyAction = (e, isDown) => { if (isDown) HandleViewportKeyDown(e); };
            orthoViewport.KeyAction = (e, isDown) => { if (isDown) HandleViewportKeyDown(e); };

            pViewport.PointerPressedAction = (e, inputState) => HandleViewportPressed(pViewport, e, inputState);
            orthoViewport.PointerPressedAction = (e, inputState) => HandleViewportPressed(orthoViewport, e, inputState);

            pViewport.PointerReleasedAction = (e, inputState) => HandleViewportReleased(pViewport, e, inputState);
            orthoViewport.PointerReleasedAction = (e, inputState) => HandleViewportReleased(orthoViewport, e, inputState);

            Viewports.Add(pViewport);
            Viewports.Add(orthoViewport);

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
                () => TerrainSystem.Scene.ThumbnailService,
                settings: Settings);
            ObjectBrowser.PlacementRequested += OnPlacementRequested;
            ObjectBrowser.WeenieSetupsLoaded += OnWeenieSetupsLoaded;

            TexturePalette = new TerrainTexturePaletteViewModel(TerrainSystem.Scene.SurfaceManager, _textureImport);
            TexturePalette.TextureSelected += OnPaletteTextureSelected;

            BookmarksPanel = new CameraBookmarksPanelViewModel(TerrainSystem, Settings);
            WorldMapPanel = new WorldMapPanelViewModel(TerrainSystem, TerrainSystem.Scene.SurfaceManager);

            LeftPanelContent = ObjectBrowser;
            LeftPanelTitle = "Object Browser";

            TerrainSystem.Scene.LandblockIntegrated += OnLandblockIntegrated;

            InitDocking();
        }

        private void InitDocking() {
            var layouts = Settings.Landscape.UIState.DockingLayout;

            void Register(string id, string title, object content, DockLocation defaultLoc) {
                var panel = new DockablePanelViewModel(id, title, content, DockingManager);
                var saved = layouts.FirstOrDefault(l => l.Id == id);
                if (saved != null) {
                    if (Enum.TryParse<DockLocation>(saved.Location, out var loc)) panel.Location = loc;
                    panel.IsVisible = saved.IsVisible;
                }
                else {
                    panel.Location = defaultLoc;
                }
                DockingManager.RegisterPanel(panel);
            }

            if (ObjectBrowser != null) Register("ObjectBrowser", "Object Browser", ObjectBrowser, DockLocation.Left);
            if (TexturePalette != null) Register("TexturePalette", "Texture Palette", TexturePalette, DockLocation.Left);
            if (LayersPanel != null) Register("Layers", "Layers", LayersPanel, DockLocation.Right);
            if (HistorySnapshotPanel != null) Register("History", "History", HistorySnapshotPanel, DockLocation.Right);
            if (BookmarksPanel != null) Register("Bookmarks", "Bookmarks", BookmarksPanel, DockLocation.Right);
            if (WorldMapPanel != null) Register("WorldMap", "World Map", WorldMapPanel, DockLocation.Right);

            Register("Toolbox", "Tools", new ToolboxViewModel(this), DockLocation.Right);

            // Restore dock region modes
            var uiState = Settings.Landscape.UIState;
            if (Enum.TryParse<DockRegionMode>(uiState.LeftDockMode, out var leftMode))
                DockingManager.LeftMode = leftMode;
            if (Enum.TryParse<DockRegionMode>(uiState.RightDockMode, out var rightMode))
                DockingManager.RightMode = rightMode;
            if (Enum.TryParse<DockRegionMode>(uiState.TopDockMode, out var topMode))
                DockingManager.TopMode = topMode;
            if (Enum.TryParse<DockRegionMode>(uiState.BottomDockMode, out var bottomMode))
                DockingManager.BottomMode = bottomMode;

            // Register Viewports
            foreach (var vp in Viewports) {
                // Use a sanitized ID
                var id = "Viewport_" + vp.Title.Replace(" ", "");

                // Default ortho to hidden to restore toggle behavior
                bool defaultVisible = vp.IsActive; // Assuming IsActive is set correctly before InitDocking
                // Correction: Viewports are added with IsActive=true (Perspective) and false (Ortho)

                // Register
                // Note: Register checks saved layout. If saved, it uses that.
                // If not saved (first run), we want Perspective Visible, Ortho Hidden.

                var panel = new DockablePanelViewModel(id, vp.Title, vp, DockingManager);
                var saved = layouts.FirstOrDefault(l => l.Id == id);
                if (saved != null) {
                    if (Enum.TryParse<DockLocation>(saved.Location, out var loc)) panel.Location = loc;
                    panel.IsVisible = saved.IsVisible;
                }
                else {
                    panel.Location = DockLocation.Center;
                    panel.IsVisible = vp.IsActive; // Default visibility matches initial active state
                }
                DockingManager.RegisterPanel(panel);
            }
        }

        private void RenderViewport(ViewportViewModel viewport, double deltaTime, Avalonia.PixelSize canvasSize, AvaloniaInputState inputState) {
            if (TerrainSystem == null || viewport.Renderer == null || viewport.Camera == null) return;

            // Only process input if viewport is active
            if (viewport.IsActive) {
                HandleViewportInput(viewport, inputState, deltaTime);
            }

            // Update System logic (loading, etc) based on this viewport's camera
            // Note: calling Update multiple times per frame is okay, as it just queues stuff
            viewport.Camera.ScreenSize = new Vector2(canvasSize.Width, canvasSize.Height);
            var view = viewport.Camera.GetViewMatrix();
            var projection = viewport.Camera.GetProjectionMatrix();
            var viewProjection = view * projection;

            TerrainSystem.Update(viewport.Camera.Position, viewProjection);
            TerrainSystem.EditingContext.ClearModifiedLandblocks();

            // Render
            TerrainSystem.Scene.Render(
                viewport.Camera,
                viewport.Renderer,
                (float)canvasSize.Width / canvasSize.Height,
                TerrainSystem.EditingContext,
                canvasSize.Width,
                canvasSize.Height);

            // Update Position HUD ? cursor-based update in HandleViewportInput takes priority.

            // Tool Overlay?
            // Currently RenderToolOverlay was in View.
            // I need to implement it here or via a callback?
            // TerrainSystem.Scene.Render handles some overlays (selection, brush).
            // But `_currentActiveTool?.RenderOverlay` was custom 2D/3D drawing?
            // Let's check `RenderToolOverlay` in View.
            // It calls `tool.RenderOverlay`.
            // I should add `tool.RenderOverlay` call here.
            SelectedTool?.RenderOverlay(viewport.Renderer, viewport.Camera, (float)canvasSize.Width / canvasSize.Height);

            if (ShowPerformanceOverlay && viewport.IsActive) {
                _frameCount++;
                long elapsed = _fpsStopwatch.ElapsedMilliseconds;
                if (elapsed - _lastPerfUpdateMs >= 500) {
                    float seconds = (elapsed - _lastPerfUpdateMs) / 1000f;
                    _lastFps = _frameCount / seconds;
                    _frameCount = 0;
                    _lastPerfUpdateMs = elapsed;

                    var scene = TerrainSystem.Scene;
                    int chunks = scene.GetLoadedChunkCount();
                    PerformanceText = $"FPS: {_lastFps:F0}\nObjects: {scene.LastObjectCount}\nChunks: {chunks}";
                }
            }
        }

        private void HandleViewportKeyDown(KeyEventArgs e) {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            if (ctrl) {
                switch (e.Key) {
                    case Avalonia.Input.Key.G:
                        _ = GotoLandblockCommand.ExecuteAsync(null);
                        return;
                    case Avalonia.Input.Key.B:
                        BookmarksPanel?.AddBookmark();
                        return;
                    case Avalonia.Input.Key.Z:
                        if (shift)
                            TerrainSystem?.History?.Redo();
                        else
                            TerrainSystem?.History?.Undo();
                        TerrainSystem?.Scene.InvalidateStaticObjectsCache();
                        return;
                    case Avalonia.Input.Key.Y:
                        TerrainSystem?.History?.Redo();
                        TerrainSystem?.Scene.InvalidateStaticObjectsCache();
                        return;
                    case Avalonia.Input.Key.C:
                        CopySelectedObject();
                        return;
                    case Avalonia.Input.Key.V:
                        PasteObject();
                        return;
                }
            }

            switch (e.Key) {
                case Avalonia.Input.Key.Delete:
                    DeleteSelectedObject();
                    break;
                case Avalonia.Input.Key.Escape:
                    var escSel = TerrainSystem?.EditingContext.ObjectSelection;
                    if (escSel != null && escSel.IsPlacementMode) {
                        escSel.IsPlacementMode = false;
                        escSel.PlacementPreview = null;
                        escSel.PlacementPreviewMulti = null;
                    } else {
                        escSel?.Deselect();
                    }
                    IsInPlacementMode = false;
                    PlacementStatusText = "";
                    TerrainSystem?.Scene.InvalidateStaticObjectsCache();
                    break;
                case Avalonia.Input.Key.G:
                    SnapToGrid = !SnapToGrid;
                    break;
            }
        }

        private void HandleViewportPressed(ViewportViewModel viewport, PointerPressedEventArgs e, AvaloniaInputState inputState) {
            foreach (var v in Viewports) {
                v.IsActive = v == viewport;
            }
            SelectedTool?.HandleMouseDown(inputState.MouseState);
        }

        private void HandleViewportReleased(ViewportViewModel viewport, PointerReleasedEventArgs e, AvaloniaInputState inputState) {
            SelectedTool?.HandleMouseUp(inputState.MouseState);
        }

        private void HandleViewportWheel(ViewportViewModel viewport, PointerWheelEventArgs e) {
            var camera = viewport.Camera;
            if (camera is PerspectiveCamera perspectiveCamera) {
                perspectiveCamera.ProcessMouseScroll((float)e.Delta.Y);
            }
            else if (camera is OrthographicTopDownCamera orthoCamera) {
                orthoCamera.ProcessMouseScroll((float)e.Delta.Y);
            }
            SyncCameras(camera);
        }

        private void HandleViewportInput(ViewportViewModel viewport, AvaloniaInputState inputState, double deltaTime) {
            // Simplified input handling from View
            var camera = viewport.Camera;

            // Update camera input
            // Mouse movement is processed by camera directly
            camera.ProcessMouseMovement(inputState.MouseState);

            // Keyboard movement
            // Logic copied from View
            bool shiftHeld = inputState.IsKeyDown(Avalonia.Input.Key.LeftShift) || inputState.IsKeyDown(Avalonia.Input.Key.RightShift);
            bool ctrlHeld = inputState.IsKeyDown(Avalonia.Input.Key.LeftCtrl) || inputState.IsKeyDown(Avalonia.Input.Key.RightCtrl);

            if ((shiftHeld || ctrlHeld) && camera is PerspectiveCamera perspCam) {
                float rotateSpeed = 60f * (float)deltaTime;
                if (inputState.IsKeyDown(Avalonia.Input.Key.Left)) perspCam.ProcessKeyboardRotation(rotateSpeed, 0);
                if (inputState.IsKeyDown(Avalonia.Input.Key.Right)) perspCam.ProcessKeyboardRotation(-rotateSpeed, 0);
                if (inputState.IsKeyDown(Avalonia.Input.Key.Up)) perspCam.ProcessKeyboardRotation(0, rotateSpeed);
                if (inputState.IsKeyDown(Avalonia.Input.Key.Down)) perspCam.ProcessKeyboardRotation(0, -rotateSpeed);
            }

            if (inputState.IsKeyDown(Avalonia.Input.Key.W) || ((!shiftHeld && !ctrlHeld) && inputState.IsKeyDown(Avalonia.Input.Key.Up)))
                camera.ProcessKeyboard(CameraMovement.Forward, deltaTime);
            if (inputState.IsKeyDown(Avalonia.Input.Key.S) || ((!shiftHeld && !ctrlHeld) && inputState.IsKeyDown(Avalonia.Input.Key.Down)))
                camera.ProcessKeyboard(CameraMovement.Backward, deltaTime);
            if (inputState.IsKeyDown(Avalonia.Input.Key.A) || ((!shiftHeld && !ctrlHeld) && inputState.IsKeyDown(Avalonia.Input.Key.Left)))
                camera.ProcessKeyboard(CameraMovement.Left, deltaTime);
            if (inputState.IsKeyDown(Avalonia.Input.Key.D) || ((!shiftHeld && !ctrlHeld) && inputState.IsKeyDown(Avalonia.Input.Key.Right)))
                camera.ProcessKeyboard(CameraMovement.Right, deltaTime);

            // Vertical Movement (Space = Up, C = Down)
            if (inputState.IsKeyDown(Avalonia.Input.Key.Space))
                camera.ProcessKeyboard(CameraMovement.Up, deltaTime);
            if (inputState.IsKeyDown(Avalonia.Input.Key.C))
                camera.ProcessKeyboard(CameraMovement.Down, deltaTime);

            // Zoom
            bool zoomIn = inputState.IsKeyDown(Avalonia.Input.Key.OemPlus) || inputState.IsKeyDown(Avalonia.Input.Key.Add);
            bool zoomOut = inputState.IsKeyDown(Avalonia.Input.Key.OemMinus) || inputState.IsKeyDown(Avalonia.Input.Key.Subtract);
            if (zoomIn || zoomOut) {
                float direction = zoomIn ? 1f : -1f;
                if (camera is OrthographicTopDownCamera ortho) {
                    float zoomSpeed = ortho.OrthographicSize * 0.02f;
                    ortho.OrthographicSize = Math.Clamp(ortho.OrthographicSize - direction * zoomSpeed, 1f, 100000f);
                }
                else {
                    camera.ProcessKeyboard(zoomIn ? CameraMovement.Forward : CameraMovement.Backward, deltaTime * 2);
                }
            }

            SelectedTool?.HandleMouseMove(inputState.MouseState);
            SelectedTool?.Update(deltaTime);

            UpdateCursorHud(inputState.MouseState);

            SyncCameras(camera);
        }

        private void UpdateCursorHud(MouseState mouseState) {
            if (TerrainSystem == null) return;

            var hit = mouseState.TerrainHit;
            if (hit.HasValue && hit.Value.Hit) {
                var pos = hit.Value.HitPosition;
                uint lbX = (uint)Math.Floor(pos.X / TerrainDataManager.LandblockLength);
                uint lbY = (uint)Math.Floor(pos.Y / TerrainDataManager.LandblockLength);
                lbX = Math.Clamp(lbX, 0, TerrainDataManager.MapSize - 1);
                lbY = Math.Clamp(lbY, 0, TerrainDataManager.MapSize - 1);
                ushort lbId = (ushort)((lbX << 8) | lbY);

                string terrainLabel = "";
                var entries = TerrainSystem.GetLandblockTerrain(lbId);
                if (entries != null) {
                    int vi = hit.Value.VerticeIndex;
                    if (vi >= 0 && vi < entries.Length) {
                        var typeByte = entries[vi].Type;
                        var terrainType = (DatReaderWriter.Enums.TerrainTextureType)typeByte;
                        terrainLabel = $"  {terrainType}";
                    }
                }

                CurrentPositionText = $"LB: {lbId:X4}  ({lbX}, {lbY})  Z: {pos.Z:F1}{terrainLabel}";
            }
        }

        private void SyncCameras(ICamera source) {
            if (TerrainSystem == null) return;

            var pCam = TerrainSystem.Scene.PerspectiveCamera;
            var orthoCam = TerrainSystem.Scene.TopDownCamera;

            if (source == pCam) {
                // Sync Ortho to Perspective (Focus point)
                // Raycast from perspective camera to ground to find focal point
                var forward = pCam.Front;

                // Plane intersection with Z = current ortho view height (approximate terrain level if we don't have exact raycast)
                // Or simply intersect with Z=0 plane if Z is absolute.
                // Assuming terrain is somewhat around Z=0 or we raycast against terrain?
                // Let's assume ground plane at Z = GetHeightAt(pCam.X, pCam.Y)? No, that's under camera.

                // Simple Plane Z=0 intersection:
                // P = O + D * t
                // P.z = 0 => O.z + D.z * t = 0 => t = -O.z / D.z

                if (Math.Abs(forward.Z) > 0.001f) {
                    // Find ground Z near the camera to be more accurate than 0?
                    float groundZ = TerrainSystem.Scene.DataManager.GetHeightAtPosition(pCam.Position.X, pCam.Position.Y);

                    float t = (groundZ - pCam.Position.Z) / forward.Z;
                    if (t > 0) {
                        var intersect = pCam.Position + forward * t;
                        orthoCam.SetPosition(new Vector3(intersect.X, intersect.Y, orthoCam.Position.Z));
                    }
                }
            }
            // One-way sync: Perspective -> Ortho.
            // Ortho interactions (panning top-down) do not affect Perspective camera to avoid "tight leash" behavior.
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
            var browserPanel = DockingManager.AllPanels.FirstOrDefault(p => p.Id == "ObjectBrowser");
            var texturePanel = DockingManager.AllPanels.FirstOrDefault(p => p.Id == "TexturePalette");

            if (SelectedTool is TexturePaintingToolViewModel) {
                // Sync palette to whatever the active sub-tool has selected
                if (SelectedSubTool is BrushSubToolViewModel brush) {
                    TexturePalette?.SyncSelection(brush.SelectedTerrainType);
                }
                else if (SelectedSubTool is BucketFillSubToolViewModel fill) {
                    TexturePalette?.SyncSelection(fill.SelectedTerrainType);
                }

                if (texturePanel != null) texturePanel.IsVisible = true;
                if (browserPanel != null && texturePanel != null && browserPanel.Location == texturePanel.Location) {
                    // If they are in the same location, maybe hide the browser so texture palette is seen?
                    // For now, let's just make sure TexturePalette is visible.
                }

                LeftPanelContent = TexturePalette;
                LeftPanelTitle = "Terrain Textures";
            }
            else {
                if (browserPanel != null) browserPanel.IsVisible = true;

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

            var centerX = (TerrainDataManager.MapSize / 2f) * TerrainDataManager.LandblockLength;
            var centerY = (TerrainDataManager.MapSize / 2f) * TerrainDataManager.LandblockLength;
            var height = Math.Max(TerrainSystem.Scene.DataManager.GetHeightAtPosition(centerX, centerY), 100f);

            TerrainSystem.Scene.PerspectiveCamera.SetPosition(centerX, centerY, height + 500f);
            TerrainSystem.Scene.PerspectiveCamera.LookAt(new Vector3(centerX, centerY, height));

            TerrainSystem.Scene.TopDownCamera.SetPosition(centerX, centerY, height + 1000f);
            TerrainSystem.Scene.TopDownCamera.OrthographicSize = 1000f;
        }

        [RelayCommand]
        private void ClearCache() {
            if (TerrainSystem == null) return;
            TerrainSystem.Scene.ClearAllCaches();
        }

        [RelayCommand]
        private async Task FreshStart() {
            if (TerrainSystem == null) return;

            var confirmed = await ShowFreshStartConfirmation();
            if (!confirmed) return;

            const byte WATER_DEEP_SEA = 0x14;
            const int MAP_SIZE = 255;
            const int LANDBLOCK_SIZE = 81;

            var waterEntry = new TerrainEntry(road: 0, scenery: 0, type: WATER_DEEP_SEA, height: 0).ToUInt();

            var allChanges = new Dictionary<ushort, Dictionary<byte, uint>>();
            for (int x = 0; x <= MAP_SIZE - 1; x++) {
                for (int y = 0; y <= MAP_SIZE - 1; y++) {
                    var lbKey = (ushort)((x << 8) | y);
                    var existing = TerrainSystem.GetLandblockTerrain(lbKey);
                    if (existing == null) continue;

                    var changes = new Dictionary<byte, uint>();
                    for (byte i = 0; i < LANDBLOCK_SIZE; i++) {
                        if (existing[i].ToUInt() != waterEntry) {
                            changes[i] = waterEntry;
                        }
                    }
                    if (changes.Count > 0) {
                        allChanges[lbKey] = changes;
                    }
                }
            }

            TerrainSystem.TerrainDoc.ApplyBulkImport(allChanges);

            TerrainSystem.DocumentManager.SkipDatStatics = true;

            // Clear all landblock statics (active + inactive in DB) and delete all dungeon documents.
            await TerrainSystem.DocumentManager.ResetWorldDocumentsAsync();

            TerrainSystem.Scene.InvalidateStaticObjectsCache();
            TerrainSystem.Scene.ClearAllCaches();
        }

        [RelayCommand]
        private async Task GenerateWorld() {
            if (TerrainSystem == null || _dats == null) return;

            byte[]? minimapData = null;
            try {
                minimapData = new byte[254 * 254];
                for (int x = 0; x < 254; x++) {
                    for (int y = 0; y < 254; y++) {
                        var terrain = TerrainSystem.GetLandblockTerrain((ushort)((x << 8) | y));
                        minimapData[y * 254 + x] = terrain != null ? terrain[40].Height : (byte)0;
                    }
                }
            }
            catch { minimapData = null; }

            var p = await WorldGen.WorldGeneratorDialogService.ShowDialog(minimapData);
            if (p == null) return;

            var region = TerrainSystem.Region;
            var progressText = new Avalonia.Controls.TextBlock {
                Text = "Starting generation...",
                FontSize = 13, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 400
            };
            var progressDialog = new Avalonia.Controls.StackPanel {
                Margin = new Avalonia.Thickness(24),
                Spacing = 10,
                Children = {
                    new Avalonia.Controls.TextBlock {
                        Text = "Generating World...",
                        FontSize = 16,
                        FontWeight = Avalonia.Media.FontWeight.Bold
                    },
                    progressText,
                    new Avalonia.Controls.ProgressBar { IsIndeterminate = true, Width = 300 }
                }
            };

            var dialogTask = DialogHost.Show(progressDialog, "MainDialogHost");

            var dats = _dats;
            WorldGen.WorldGeneratorResult? result = null;
            try {
                result = await Task.Run(() => {
                    return WorldGen.WorldGenerator.Generate(p, dats, region, msg => {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => progressText.Text = msg);
                    });
                });
            }
            catch (Exception ex) {
                Console.WriteLine($"[WorldGen] Error: {ex.Message}");
            }

            DialogHost.Close("MainDialogHost");
            await dialogTask;

            if (result == null) return;

            TerrainSystem.DocumentManager.SkipDatStatics = true;

            // Clear all landblock statics (active + inactive in DB) and delete all dungeon documents.
            await TerrainSystem.DocumentManager.ResetWorldDocumentsAsync();

            TerrainSystem.TerrainDoc.ApplyBulkImport(result.TerrainChanges);

            // Place buildings as StaticObjects. Blueprint instantiation (LBI + EnvCells) is
            // deferred to export time via LandblockDocument.SaveToDatsInternal — no dat writes here.
            foreach (var (lbKey, plannedBuildings) in result.BuildingPlacements) {
                var docId = $"landblock_{lbKey:X4}";
                var lbDoc = await TerrainSystem.DocumentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId);

                foreach (var planned in plannedBuildings) {
                    lbDoc.AddStaticObject(new StaticObject {
                        Id = planned.ModelId,
                        IsSetup = (planned.ModelId & 0x02000000) != 0,
                        Origin = planned.WorldPosition,
                        Orientation = planned.Orientation,
                        Scale = Vector3.One
                    });
                }
            }

            // Add decorations as regular static objects
            foreach (var (lbKey, decorObjects) in result.DecorationPlacements) {
                var docId = $"landblock_{lbKey:X4}";
                var doc = await TerrainSystem.DocumentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId);
                foreach (var obj in decorObjects) {
                    doc.AddStaticObject(obj);
                }
            }

            TerrainSystem.Scene.InvalidateStaticObjectsCache();
            TerrainSystem.Scene.ClearAllCaches();

            // Queue all new building + decoration models for warmup so they render
            var warmupIds = new HashSet<(uint id, bool isSetup)>();
            foreach (var planned in result.BuildingPlacements.Values.SelectMany(x => x)) {
                warmupIds.Add((planned.ModelId, (planned.ModelId & 0x02000000) != 0));
            }
            foreach (var obj in result.DecorationPlacements.Values.SelectMany(x => x)) {
                warmupIds.Add((obj.Id, obj.IsSetup));
            }
            foreach (var item in warmupIds) {
                TerrainSystem.Scene.QueueModelWarmup(item.id, item.isSetup);
            }

            Console.WriteLine($"[WorldGen] Applied: {result.TotalVerticesModified} terrain vertices, " +
                $"{result.Towns.Count} towns, {result.TotalBuildingsPlaced} buildings, " +
                $"{result.TotalDecorationsPlaced} decorations, " +
                $"{result.TotalRoadVertices} road vertices, {warmupIds.Count} models queued for warmup");

            await ShowWorldGenSummary(result);
        }

        private async Task ShowWorldGenSummary(WorldGen.WorldGeneratorResult result) {
            var townRows = new StackPanel { Spacing = 2 };
            foreach (var t in result.Towns) {
                townRows.Children.Add(new TextBlock {
                    Text = $"{t.Name}  ({t.SizeLabel})  —  LB ({t.CenterLbX}, {t.CenterLbY})  —  {t.BuildingCount} buildings",
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas, Courier New, monospace")
                });
            }

            var panel = new StackPanel {
                Margin = new Avalonia.Thickness(24),
                Spacing = 12,
                Children = {
                    new TextBlock {
                        Text = "World Generation Complete",
                        FontSize = 16, FontWeight = FontWeight.Bold
                    },
                    new TextBlock {
                        Text = $"{result.TotalVerticesModified:N0} terrain vertices  ·  " +
                               $"{result.Towns.Count} settlements  ·  " +
                               $"{result.TotalBuildingsPlaced} buildings (with interiors)  ·  " +
                               $"{result.TotalDecorationsPlaced} decorations  ·  " +
                               $"{result.TotalRoadVertices} road vertices",
                        FontSize = 12, Foreground = Brushes.Gray
                    },
                    new Avalonia.Controls.Separator(),
                    new TextBlock { Text = "Settlements", FontSize = 13, FontWeight = FontWeight.SemiBold },
                    new ScrollViewer {
                        MaxHeight = 300,
                        Content = townRows
                    }
                }
            };

            var exportBtn = new Button {
                Content = "Export Towns CSV",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Avalonia.Thickness(12, 6)
            };
            var closeBtn = new Button {
                Content = "Close",
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Avalonia.Thickness(16, 6)
            };

            var buttonRow = new StackPanel {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10,
                Children = { exportBtn, closeBtn }
            };
            panel.Children.Add(buttonRow);

            exportBtn.Click += async (_, _) => {
                await ExportTownsCsv(result);
            };
            closeBtn.Click += (_, _) => {
                DialogHost.Close("MainDialogHost");
            };

            await DialogHost.Show(panel, "MainDialogHost");
        }

        private async Task ExportTownsCsv(WorldGen.WorldGeneratorResult result) {
            try {
                var topLevel = Avalonia.Application.Current?.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow : null;
                if (topLevel == null) return;

                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                    Title = "Export Towns CSV",
                    SuggestedFileName = "towns.csv",
                    FileTypeChoices = new[] {
                        new FilePickerFileType("CSV Files") { Patterns = new[] { "*.csv" } }
                    }
                });
                if (file == null) return;

                using var stream = await file.OpenWriteAsync();
                using var writer = new System.IO.StreamWriter(stream);
                await writer.WriteLineAsync("Name,Size,Buildings,LandblockHex,OutdoorCellHex,TeleLoc");
                foreach (var t in result.Towns) {
                    var anchor = GetTownTelelocAnchor(t, result.BuildingPlacements);
                    var (lbKey, cellHex, teleloc) = BuildAceTeleLoc(anchor.X, anchor.Y, anchor.Z);
                    string escName = t.Name.Replace("\"", "\"\"", StringComparison.Ordinal);
                    await writer.WriteLineAsync(
                        $"\"{escName}\",{t.SizeLabel},{t.BuildingCount},0x{lbKey:X4},0x{cellHex:X4},\"{teleloc}\"");
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"[WorldGen] CSV export error: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefer centroid of buildings in this settlement so @teleloc lands among structures,
        /// not an empty landblock center.
        /// </summary>
        private static Vector3 GetTownTelelocAnchor(WorldGen.TownSite town,
            Dictionary<ushort, List<WorldGen.PlannedBuilding>> placements) {
            var pts = new List<Vector3>();
            foreach (var list in placements.Values) {
                foreach (var pb in list) {
                    if (!string.Equals(pb.TownName, town.Name, StringComparison.Ordinal))
                        continue;
                    pts.Add(pb.WorldPosition);
                }
            }

            if (pts.Count == 0)
                return town.WorldCenter;

            float sx = 0f, sy = 0f, sz = 0f;
            foreach (var p in pts) {
                sx += p.X;
                sy += p.Y;
                sz += p.Z;
            }

            float n = pts.Count;
            return new Vector3(sx / n, sy / n, sz / n);
        }

        /// <summary>
        /// ACE / @teleloc outdoor format: full id = (landblockKey * 65536) | outdoorCellId,
        /// landblockKey = (lbX * 256) | lbY. Bracket coords are landblock-local X/Y and world Z.
        /// Outdoor cells use indices 1..64; we clamp to inner cells 1..6 like the placement tool.
        /// </summary>
        private static (ushort landblockKey, ushort outdoorCell, string telelocLine) BuildAceTeleLoc(
            float worldX, float worldY, float worldZ) {

            int lbX = Math.Clamp((int)Math.Floor(worldX / 192f), 0, 254);
            int lbY = Math.Clamp((int)Math.Floor(worldY / 192f), 0, 254);
            float localX = worldX - lbX * 192f;
            float localY = worldY - lbY * 192f;

            int cellX = Math.Clamp((int)(localX / 24f), 1, 6);
            int cellY = Math.Clamp((int)(localY / 24f), 1, 6);
            ushort outdoorCell = (ushort)(cellX * 8 + cellY + 1);
            ushort lbKey = (ushort)((lbX << 8) | lbY);
            uint fullId = ((uint)lbKey << 16) | outdoorCell;

            string teleloc = string.Format(CultureInfo.InvariantCulture,
                "0x{0:X8} [{1:F6} {2:F6} {3:F6}] 1.000000 0.000000 0.000000 0.000000",
                fullId, localX, localY, worldZ);
            return (lbKey, outdoorCell, teleloc);
        }

        private async Task<bool> ShowFreshStartConfirmation() {
            bool confirmed = false;

            await DialogHost.Show(new StackPanel {
                Margin = new Avalonia.Thickness(20),
                Spacing = 10,
                Children = {
                    new TextBlock {
                        Text = "Fresh Start",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock {
                        Text = "This will reset ALL terrain to WaterDeepSea and\ndelete ALL static objects and buildings from the world.\n\nThis cannot be undone.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 400,
                        FontSize = 13,
                        Opacity = 0.85
                    },
                    new TextBlock {
                        Text = "Note: Server-side spawns in your ACE database\n(landblock_instance table) will not be removed.\nYou may need to clear that table separately.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 400,
                        FontSize = 11,
                        Opacity = 0.6,
                        FontStyle = Avalonia.Media.FontStyle.Italic
                    },
                    new StackPanel {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = {
                            new Button {
                                Content = "Cancel",
                                Command = new RelayCommand(() => DialogHost.Close("MainDialogHost"))
                            },
                            new Button {
                                Content = "Reset World",
                                Command = new RelayCommand(() => {
                                    confirmed = true;
                                    DialogHost.Close("MainDialogHost");
                                })
                            }
                        }
                    }
                }
            }, "MainDialogHost");

            return confirmed;
        }

        [RelayCommand]
        public void Undo() {
            TerrainSystem?.History?.Undo();
            TerrainSystem?.Scene.InvalidateStaticObjectsCache();
        }

        [RelayCommand]
        public void Redo() {
            TerrainSystem?.History?.Redo();
            TerrainSystem?.Scene.InvalidateStaticObjectsCache();
        }

        [RelayCommand]
        public async Task ImportHeightmap() {
            if (TerrainSystem == null) return;

            var result = await ShowImportHeightmapDialog();
            if (result == null) return;

            var (filePath, isFullWorld, startX, startY, countX, countY) = result.Value;

            if (isFullWorld) {
                startX = 0;
                startY = 0;
                countX = HeightmapImportService.MAP_SIZE;
                countY = HeightmapImportService.MAP_SIZE;
            }

            var statusText = new TextBlock {
                Text = "Loading image...",
                FontSize = 13,
                Foreground = Avalonia.Media.Brushes.White,
                TextWrapping = TextWrapping.Wrap
            };

            var progressDialog = new StackPanel {
                Margin = new Avalonia.Thickness(24),
                Spacing = 12,
                Width = 340,
                Children = {
                    new TextBlock { Text = "Importing Heightmap", FontSize = 16, FontWeight = FontWeight.Bold },
                    statusText,
                    new Avalonia.Controls.ProgressBar { IsIndeterminate = true, Height = 4 }
                }
            };

            var dialogTask = DialogHost.Show(progressDialog, "MainDialogHost");

            var terrainSystem = TerrainSystem;
            var capturedStartX = startX;
            var capturedStartY = startY;
            var capturedCountX = countX;
            var capturedCountY = countY;
            var averageColors = terrainSystem.Scene.SurfaceManager.GetTerrainAverageColors();

            string? errorMessage = null;

            var desc = isFullWorld
                ? "Import heightmap (full world)"
                : $"Import heightmap ({countX}x{countY} landblocks at {startX},{startY})";

            HeightImportCommand? executedCommand = null;

            await Task.Run(() => {
                try {
                    var (targetW, targetH) = HeightmapImportService.GetTargetDimensions(capturedCountX, capturedCountY);
                    var grid = HeightmapImportService.LoadAndResampleRgb(filePath, targetW, targetH);

                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        statusText.Text = $"Processing {capturedCountX}x{capturedCountY} landblocks...");

                    var changes = HeightmapImportService.BuildChanges(
                        grid, capturedStartX, capturedStartY, capturedCountX, capturedCountY,
                        terrainSystem, averageColors);

                    if (changes.Count == 0) return;

                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        statusText.Text = $"Applying {changes.Count} landblock changes...");

                    var command = new HeightImportCommand(terrainSystem.EditingContext, desc, changes);
                    command.Execute();
                    executedCommand = command;
                }
                catch (Exception ex) {
                    errorMessage = ex.Message;
                }
            });

            DialogHost.Close("MainDialogHost");

            if (errorMessage != null) {
                _logger.LogError("Failed to import heightmap: {Error}", errorMessage);
                return;
            }

            if (executedCommand != null) {
                terrainSystem.History?.AddExecutedCommand(executedCommand);
                terrainSystem.Scene.InvalidateStaticObjectsCache();
                terrainSystem.Scene.ClearAllCaches();
            }
        }

        private async Task<(string FilePath, bool IsFullWorld, int StartX, int StartY, int CountX, int CountY)?> ShowImportHeightmapDialog() {
            var topLevel = GetTopLevel();
            if (topLevel == null) return null;

            string? selectedFile = null;
            bool isFullWorld = true;
            int startX = 0, startY = 0, countX = 16, countY = 16;

            var filePathText = new TextBlock {
                Text = "No file selected",
                FontSize = 12,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 320
            };

            var regionPanel = new StackPanel { Spacing = 6, IsVisible = false };
            var startXBox = new NumericUpDown { Minimum = 0, Maximum = 253, Value = 0, ShowButtonSpinner = false, FontSize = 11, Width = 80 };
            var startYBox = new NumericUpDown { Minimum = 0, Maximum = 253, Value = 0, ShowButtonSpinner = false, FontSize = 11, Width = 80 };
            var countXBox = new NumericUpDown { Minimum = 1, Maximum = 254, Value = 16, ShowButtonSpinner = false, FontSize = 11, Width = 80 };
            var countYBox = new NumericUpDown { Minimum = 1, Maximum = 254, Value = 16, ShowButtonSpinner = false, FontSize = 11, Width = 80 };

            regionPanel.Children.Add(new Grid {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,8,*,16,Auto,8,*"),
                Children = {
                    SetGrid(new TextBlock { Text = "Start X", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, FontSize = 11 }, 0, 0),
                    SetGrid(startXBox, 0, 2),
                    SetGrid(new TextBlock { Text = "Start Y", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, FontSize = 11 }, 0, 4),
                    SetGrid(startYBox, 0, 6)
                }
            });
            regionPanel.Children.Add(new Grid {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,8,*,16,Auto,8,*"),
                Children = {
                    SetGrid(new TextBlock { Text = "Width", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, FontSize = 11 }, 0, 0),
                    SetGrid(countXBox, 0, 2),
                    SetGrid(new TextBlock { Text = "Height", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, FontSize = 11 }, 0, 4),
                    SetGrid(countYBox, 0, 6)
                }
            });

            var fullWorldRadio = new RadioButton { Content = "Full World (254x254 landblocks)", IsChecked = true, FontSize = 12 };
            var regionRadio = new RadioButton { Content = "Selected Region", IsChecked = false, FontSize = 12 };

            fullWorldRadio.IsCheckedChanged += (_, _) => {
                regionPanel.IsVisible = regionRadio.IsChecked == true;
            };
            regionRadio.IsCheckedChanged += (_, _) => {
                regionPanel.IsVisible = regionRadio.IsChecked == true;
            };

            bool confirmed = false;

            var browseButton = new Button { Content = "Browse...", FontSize = 11 };
            browseButton.Click += async (_, _) => {
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                    Title = "Select Heightmap Image",
                    AllowMultiple = false,
                    FileTypeFilter = new[] {
                        new FilePickerFileType("Image Files") { Patterns = new[] { "*.png", "*.bmp", "*.jpg", "*.jpeg", "*.tiff", "*.tif", "*.gif" } },
                        new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
                    }
                });
                if (files.Count > 0) {
                    selectedFile = files[0].TryGetLocalPath();
                    filePathText.Text = System.IO.Path.GetFileName(selectedFile) ?? selectedFile;
                    filePathText.Opacity = 1.0;
                }
            };

            await DialogHost.Show(new StackPanel {
                Margin = new Avalonia.Thickness(20),
                Spacing = 12,
                Width = 420,
                Children = {
                    new TextBlock { Text = "Import Heightmap", FontSize = 16, FontWeight = FontWeight.Bold },
                    new TextBlock {
                        Text = "Load a grayscale image as terrain height data.\nPixel brightness (0-255) maps directly to height index.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Opacity = 0.7
                    },
                    new StackPanel {
                        Spacing = 6,
                        Children = {
                            new TextBlock { Text = "Image File", FontSize = 12, FontWeight = FontWeight.SemiBold },
                            new StackPanel {
                                Orientation = Avalonia.Layout.Orientation.Horizontal,
                                Spacing = 8,
                                Children = { browseButton, filePathText }
                            }
                        }
                    },
                    new StackPanel {
                        Spacing = 6,
                        Children = {
                            new TextBlock { Text = "Scope", FontSize = 12, FontWeight = FontWeight.SemiBold },
                            fullWorldRadio,
                            regionRadio,
                            regionPanel
                        }
                    },
                    new StackPanel {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Margin = new Avalonia.Thickness(0, 8, 0, 0),
                        Children = {
                            new Button {
                                Content = "Cancel",
                                Command = new RelayCommand(() => DialogHost.Close("MainDialogHost"))
                            },
                            new Button {
                                Content = "Import",
                                Command = new RelayCommand(() => {
                                    if (selectedFile != null) {
                                        isFullWorld = fullWorldRadio.IsChecked == true;
                                        startX = (int)(startXBox.Value ?? 0);
                                        startY = (int)(startYBox.Value ?? 0);
                                        countX = (int)(countXBox.Value ?? 16);
                                        countY = (int)(countYBox.Value ?? 16);
                                        confirmed = true;
                                    }
                                    DialogHost.Close("MainDialogHost");
                                })
                            }
                        }
                    }
                }
            }, "MainDialogHost");

            if (!confirmed || selectedFile == null) return null;
            return (selectedFile, isFullWorld, startX, startY, countX, countY);
        }

        private static TopLevel? GetTopLevel() {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.MainWindow;
            return null;
        }

        private static Control SetGrid(Control control, int row, int column) {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            return control;
        }

        private StaticObject? _copiedObject;
        private ushort _copiedObjectLandblock;

        [RelayCommand]
        public void CopySelectedObject() {
            var sel = TerrainSystem?.EditingContext.ObjectSelection;
            if (sel == null || !sel.HasSelection || sel.SelectedObject == null) return;
            _copiedObject = sel.SelectedObject.Value;
            _copiedObjectLandblock = sel.SelectedLandblockKey;
        }

        [RelayCommand]
        public void PasteObject() {
            if (_copiedObject == null || TerrainSystem == null) return;
            var src = _copiedObject.Value;
            var sel = TerrainSystem.EditingContext.ObjectSelection;
            var duplicate = new StaticObject {
                Id = src.Id,
                Origin = src.Origin + new Vector3(24f, 24f, 0),
                Orientation = src.Orientation,
                Scale = src.Scale,
                IsSetup = src.IsSetup,
                IsParticleEmitter = src.IsParticleEmitter
            };
            var cmd = new Commands.AddObjectCommand(TerrainSystem.EditingContext, _copiedObjectLandblock, duplicate);
            TerrainSystem.History?.ExecuteCommand(cmd);
            TerrainSystem.Scene.InvalidateStaticObjectsCache();
            sel.Select(duplicate, _copiedObjectLandblock, cmd.AddedIndex, false);
        }

        [RelayCommand]
        public void DeleteSelectedObject() {
            var sel = TerrainSystem?.EditingContext.ObjectSelection;
            if (sel == null || !sel.HasSelection || sel.SelectedObject == null || sel.IsScenery) return;
            if (sel.HasEnvCellSelection) return;

            var commands = new List<Lib.History.ICommand>();
            foreach (var entry in sel.SelectedEntries.OrderByDescending(e => e.ObjectIndex)) {
                if (entry.IsScenery) continue;
                commands.Add(new Commands.RemoveObjectCommand(TerrainSystem!.EditingContext, entry.LandblockKey, entry.ObjectIndex));
            }
            if (commands.Count == 0) return;

            if (commands.Count == 1) {
                TerrainSystem!.History?.ExecuteCommand(commands[0]);
            } else {
                var composite = new Lib.History.CompositeCommand();
                composite.Commands.AddRange(commands);
                TerrainSystem!.History?.ExecuteCommand(composite);
            }
            sel.Deselect();
            TerrainSystem!.Scene.InvalidateStaticObjectsCache();
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
                // Input was 4-char hex like "C6AC" ??? treat cellPart as the landblock ID
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

            var target = new Vector3(centerX, centerY, height);

            TerrainSystem.Scene.PerspectiveCamera.SetPosition(centerX, centerY, height + 200f);
            TerrainSystem.Scene.PerspectiveCamera.LookAt(target);

            TerrainSystem.Scene.TopDownCamera.LookAt(target);
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

            var target = new Vector3(worldX, worldY, worldZ);

            TerrainSystem.Scene.PerspectiveCamera.SetPosition(worldX, worldY, worldZ + 10f);
            TerrainSystem.Scene.PerspectiveCamera.LookAt(target);

            TerrainSystem.Scene.TopDownCamera.LookAt(target);

            return true;
        }

        private async Task<uint?> ShowGotoLandblockDialog() {
            uint? result = null;
            var textBox = new TextBox {
                Text = "",
                Width = 400,
                Watermark = "Search by name, hex ID (C6AC), or X,Y (198,172)"
            };
            var errorText = new TextBlock {
                Text = "",
                Foreground = Brushes.Red,
                FontSize = 12,
                IsVisible = false
            };

            var locationList = new ListBox {
                MaxHeight = 250,
                Width = 400,
                IsVisible = false,
                FontSize = 12,
            };

            void UpdateLocationResults(string? query) {
                if (string.IsNullOrWhiteSpace(query) || query.Length < 2) {
                    locationList.IsVisible = false;
                    locationList.ItemsSource = null;
                    return;
                }

                var results = LocationDatabase.Search(query).Take(50).ToList();
                if (results.Count > 0) {
                    locationList.ItemsSource = results.Select(r => $"{r.Name}  [{r.CellIdHex}]").ToList();
                    locationList.IsVisible = true;
                }
                else {
                    locationList.IsVisible = false;
                    locationList.ItemsSource = null;
                }
            }

            textBox.TextChanged += (s, e) => {
                errorText.IsVisible = false;
                UpdateLocationResults(textBox.Text);
            };

            locationList.SelectionChanged += (s, e) => {
                if (locationList.SelectedIndex < 0) return;
                var query = textBox.Text;
                if (string.IsNullOrWhiteSpace(query)) return;
                var results = LocationDatabase.Search(query).Take(50).ToList();
                if (locationList.SelectedIndex < results.Count) {
                    var selected = results[locationList.SelectedIndex];
                    result = selected.CellId;
                    DialogHost.Close("MainDialogHost");
                }
            };

            await DialogHost.Show(new StackPanel {
                Margin = new Avalonia.Thickness(20),
                Spacing = 10,
                Children = {
                    new TextBlock {
                        Text = "Go to Location",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock {
                        Text = "Search by name or enter a landblock ID in hex,\na full cell ID, or X,Y coordinates.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 400,
                        FontSize = 12,
                        Opacity = 0.7
                    },
                    textBox,
                    locationList,
                    errorText,
                    new StackPanel {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
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
                                    if (result != null) {
                                        DialogHost.Close("MainDialogHost");
                                        return;
                                    }
                                    var parsed = ParseLocationInput(textBox.Text);
                                    if (parsed != null) {
                                        result = parsed;
                                        DialogHost.Close("MainDialogHost");
                                    }
                                    else {
                                        errorText.Text = "Invalid input. Try a name, hex ID, or X,Y coordinates.";
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

            // Try X,Y format ??? landblock only
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

            // 8-char hex ??? full cell ID (e.g. 01D90108 ??? landblock 0x01D9, cell 0x0108)
            if (hex.Length > 4 && hex.Length <= 8) {
                if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fullId))
                    return fullId;
                return null;
            }

            // 1-4 char hex ??? landblock only (e.g. C6AC ??? 0x0000C6AC)
            if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lbId))
                return lbId;

            return null;
        }

        private void OnWeenieSetupsLoaded(object? sender, IReadOnlyList<(uint WeenieClassId, uint SetupId)> mappings) {
            bool anyNew = false;
            foreach (var (wcid, setupId) in mappings) {
                if (setupId != 0 && _weenieSetupCache.TryAdd(wcid, setupId))
                    anyNew = true;
            }
            if (anyNew) {
                ReloadAllWeenieSpawns();
            }
        }

        private void OnLandblockIntegrated(ushort lbKey) {
            if (!ShowWeenieSpawns) return;
            _ = LoadWeenieSpawnsForLandblockAsync(lbKey);
        }

        private void LoadWeenieSpawnsForLoadedLandblocks() {
            var scene = TerrainSystem?.Scene;
            if (scene == null) return;
            var docMgr = TerrainSystem?.DocumentManager;
            if (docMgr == null) return;

            int count = 0;
            foreach (var docId in docMgr.ActiveDocs.Keys) {
                if (!docId.StartsWith("landblock_")) continue;
                var hex = docId.Replace("landblock_", "");
                if (ushort.TryParse(hex, NumberStyles.HexNumber, null, out var lbKey)) {
                    _ = LoadWeenieSpawnsForLandblockAsync(lbKey);
                    count++;
                }
            }
            Console.WriteLine($"[Spawns] Toggle ON — queued {count} landblocks for weenie spawn loading");
        }

        private void ReloadAllWeenieSpawns() {
            foreach (var lbKey in _weenieLoadedLandblocks.Keys.ToList()) {
                _weenieLoadedLandblocks.TryRemove(lbKey, out _);
                _ = LoadWeenieSpawnsForLandblockAsync(lbKey);
            }
        }

        private async Task LoadWeenieSpawnsForLandblockAsync(ushort lbKey) {
            if (_weenieLoadedLandblocks.ContainsKey(lbKey)) return;
            if (!_weenieLoadingLandblocks.TryAdd(lbKey, 0)) return;

            await WeenieSpawnDbConcurrency.WaitAsync();
            try {
                var aceConn = Settings?.AceDbConnection;
                if (aceConn == null || string.IsNullOrWhiteSpace(aceConn.Host)) {
                    Console.WriteLine($"[Spawns] No ACE DB configured — cannot load weenie spawns");
                    return;
                }

                var aceSettings = aceConn.ToAceDbSettings();
                using var connector = new AceDbConnector(aceSettings);

                var err = await connector.TestConnectionAsync();
                if (err != null) {
                    Console.WriteLine($"[Spawns] DB connection failed: {err}");
                    return;
                }

                var records = await connector.GetInstancesAsync(
                    lbKey, cellMin: 1, cellMax: 64, includeAngles: true);
                if (records.Count == 0) {
                    _weenieLoadedLandblocks.TryAdd(lbKey, 0);
                    return;
                }

                var wcids = records.Select(r => r.WeenieClassId).Distinct().ToList();
                var missingWcids = wcids.Where(w => !_weenieSetupCache.ContainsKey(w)).ToList();
                if (missingWcids.Count > 0) {
                    var newSetups = await connector.GetSetupDidsAsync(missingWcids);
                    foreach (var (wcid, setupId) in newSetups) {
                        if (setupId != 0)
                            _weenieSetupCache.TryAdd(wcid, setupId);
                    }
                    Console.WriteLine($"[Spawns] Resolved {newSetups.Count}/{missingWcids.Count} Setup DIDs for {lbKey:X4}");
                }

                int blockX = (lbKey >> 8) & 0xFF;
                int blockY = lbKey & 0xFF;
                var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);

                var spawns = new List<StaticObject>();
                int noSetup = 0;
                foreach (var r in records) {
                    if (!_weenieSetupCache.TryGetValue(r.WeenieClassId, out var setupId) || setupId == 0) {
                        noSetup++;
                        continue;
                    }

                    bool isSetup = (setupId & 0x02000000) != 0;
                    var orientation = (r.AnglesW.HasValue)
                        ? new Quaternion(r.AnglesX ?? 0f, r.AnglesY ?? 0f, r.AnglesZ ?? 0f, r.AnglesW.Value)
                        : Quaternion.Identity;

                    spawns.Add(new StaticObject {
                        Id = setupId,
                        IsSetup = isSetup,
                        Origin = new Vector3(r.OriginX, r.OriginY, r.OriginZ) + lbOffset,
                        Orientation = orientation,
                        Scale = Vector3.One
                    });
                }

                if (spawns.Count > 0) {
                    TerrainSystem?.Scene.SetWeenieSpawns(lbKey, spawns);
                    TerrainSystem?.Scene.InvalidateStaticObjectsCache();
                }

                _weenieLoadedLandblocks.TryAdd(lbKey, 0);
                Console.WriteLine($"[Spawns] {lbKey:X4}: {spawns.Count} rendered, {noSetup} missing model, {records.Count} total DB rows");
            }
            catch (Exception ex) {
                Console.WriteLine($"[Spawns] Failed for {lbKey:X4}: {ex.Message}");
            }
            finally {
                WeenieSpawnDbConcurrency.Release();
                _weenieLoadingLandblocks.TryRemove(lbKey, out _);
            }
        }

        private void OnPlacementRequested(object? sender, EventArgs e) {
            // Switch to the Selector tool's Select sub-tool so placement clicks are handled
            var selectorTool = Tools.OfType<SelectorToolViewModel>().FirstOrDefault();
            if (selectorTool == null) return;

            var selectSubTool = selectorTool.AllSubTools.OfType<SelectSubToolViewModel>().FirstOrDefault();
            if (selectSubTool == null) return;

            // Save placement state ??? tool deactivation clears it
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

            IsInPlacementMode = sel?.IsPlacementMode ?? false;
            PlacementStatusText = ObjectBrowser?.Status ?? "Placing object";
        }

        public void Cleanup() {
            // Save UI state before disposing
            if (SelectedTool != null) {
                var uiState = Settings.Landscape.UIState;
                uiState.LastToolIndex = Tools.IndexOf(SelectedTool);
                if (SelectedSubTool != null && SelectedTool.AllSubTools.Contains(SelectedSubTool)) {
                    uiState.LastSubToolIndex = SelectedTool.AllSubTools.IndexOf(SelectedSubTool);
                }

                // Save docking layout
                uiState.DockingLayout.Clear();
                foreach (var panel in DockingManager.AllPanels.OfType<DockablePanelViewModel>()) {
                    uiState.DockingLayout.Add(new DockingPanelState {
                        Id = panel.Id,
                        Location = panel.Location.ToString(),
                        IsVisible = panel.IsVisible
                    });
                }

                // Save dock region modes
                uiState.LeftDockMode = DockingManager.LeftMode.ToString();
                uiState.RightDockMode = DockingManager.RightMode.ToString();
                uiState.TopDockMode = DockingManager.TopMode.ToString();
                uiState.BottomDockMode = DockingManager.BottomMode.ToString();

                Settings.Save();
            }

            if (ObjectBrowser != null) {
                ObjectBrowser.PlacementRequested -= OnPlacementRequested;
            }
            if (TexturePalette != null) {
                TexturePalette.TextureSelected -= OnPaletteTextureSelected;
            }

            WorldMapPanel?.Dispose();
            TerrainSystem?.Dispose();
            TerrainSystem = null;
        }
    }

}
