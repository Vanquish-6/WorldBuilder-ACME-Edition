using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Chorizite.OpenGLSDLBackend;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DatReaderWriter.Types;
using DialogHostAvalonia;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Chorizite.OpenGLSDLBackend.Lib;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Services;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;
using WorldBuilder.Editors.Dungeon.Tools;
using WorldBuilder.Editors.Landscape;

namespace WorldBuilder.Editors.Dungeon {
    public partial class DungeonEditorViewModel : ViewModelBase {
        [ObservableProperty] private string _statusText = "No dungeon loaded — open or create one to get started.  Pick rooms from the catalog to build your dungeon.";
        [ObservableProperty] private string _landblockInputText = "";
        [ObservableProperty] private string _currentPositionText = "";
        [ObservableProperty] private string _cursorHudText = "";
        [ObservableProperty] private bool _showCursorHud;
        [ObservableProperty] private string _selectedCellInfo = "";        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowHoveredCellInfo))]
        private string _hoveredCellInfo = "";
        public bool ShowHoveredCellInfo => !string.IsNullOrEmpty(HoveredCellInfo) && !HasSelectedCell;
        [ObservableProperty] private int _cellCount;
        [ObservableProperty] private bool _hasDungeon;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowHoveredCellInfo))]
        private bool _hasSelectedCell;
        [ObservableProperty] private bool _isPlacementMode;
        [ObservableProperty] private string _placementStatusText = "";
        [ObservableProperty] private bool _isObjectPlacementMode;
        [ObservableProperty] private string _objectIdInput = "";
        [ObservableProperty] private float _nudgeStep = 1.0f;
        [ObservableProperty] private bool _gridSnapEnabled;
        [ObservableProperty] private float _gridSnapSize = 5.0f;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CameraModeText))]
        private bool _isOrthographic;

        [ObservableProperty] private bool _showGrid = true;

        public string CameraModeText => IsOrthographic ? "3D View" : "Top-Down";

        // Tool system
        public ObservableCollection<DungeonToolBase> Tools { get; } = new();
        [ObservableProperty] private DungeonToolBase? _selectedTool;
        [ObservableProperty] private DungeonSubToolBase? _selectedSubTool;
        public DungeonEditingContext EditingContext { get; } = new();
        public DungeonToolboxViewModel? Toolbox { get; private set; }
        [ObservableProperty] private string _cellPosX = "";
        [ObservableProperty] private string _cellPosY = "";
        [ObservableProperty] private string _cellPosZ = "";
        [ObservableProperty] private string _cellRotX = "";
        [ObservableProperty] private string _cellRotY = "";
        [ObservableProperty] private string _cellRotZ = "";
        [ObservableProperty] private ObservableCollection<CellSurfaceSlot> _surfaceSlots = new();
        [ObservableProperty] private int _selectedSurfaceSlot = -1;
        [ObservableProperty] private ObservableCollection<PortalListEntry> _portalList = new();
        [ObservableProperty] private bool _isDraggingCell;

        [ObservableProperty] private bool _hasSelectedObject;
        [ObservableProperty] private string _selectedObjectInfo = "";
        [ObservableProperty] private string _objPosX = "";
        [ObservableProperty] private string _objPosY = "";
        [ObservableProperty] private string _objPosZ = "";
        [ObservableProperty] private string _objRotDegrees = "";
        [ObservableProperty] private bool _isDraggingObject;

        [ObservableProperty] private bool _showConnectionLines = true;
        [ObservableProperty] private bool _showPortalIndicators = true;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectedCellPanelTitle))]
        private int _selectedCellCount; // 0 = none, 1 = single, >1 = multi

        // Instance Placements (ACE DB generators/items/portals in dungeons)
        public ObservableCollection<InstancePlacementItemViewModel> InstancePlacementItems { get; } = new();
        [ObservableProperty] private string _newPlacementWcid = "";
        [ObservableProperty] private ushort? _newPlacementCellNumber;
        [ObservableProperty] private string _newPlacementX = "0";
        [ObservableProperty] private string _newPlacementY = "0";
        [ObservableProperty] private string _newPlacementZ = "0";
        /// <summary>Cell numbers for the "Add placement" dropdown (from current document).</summary>
        public ObservableCollection<ushort> InstancePlacementCellNumbers { get; } = new();

        [RelayCommand]
        private void ToggleCameraMode() {
            if (_scene == null) return;
            IsOrthographic = !IsOrthographic;
            if (IsOrthographic) {
                _savedPerspectivePos = _scene.Camera.Position;
                _savedPerspectiveYaw = _scene.Camera.Yaw;
                _savedPerspectivePitch = _scene.Camera.Pitch;
            }
            else {
                _scene.Camera.SetPosition(_savedPerspectivePos);
                _scene.Camera.SetYawPitch(_savedPerspectiveYaw, _savedPerspectivePitch);
            }
        }

        [RelayCommand]
        private void ResetCamera() {
            _scene?.FocusCamera();
        }

        /// <summary>Rebuild InstancePlacementItems and InstancePlacementCellNumbers from the current document.</summary>
        private void RefreshInstancePlacementList() {
            InstancePlacementItems.Clear();
            InstancePlacementCellNumbers.Clear();
            if (_document == null) return;
            var cells = _document.Cells.OrderBy(c => c.CellNumber).ToList();
            foreach (var c in cells)
                InstancePlacementCellNumbers.Add(c.CellNumber);
            int i = 0;
            foreach (var p in _document.InstancePlacements) {
                var pos = $"{p.Origin.X:F1}, {p.Origin.Y:F1}, {p.Origin.Z:F1}";
                InstancePlacementItems.Add(new InstancePlacementItemViewModel(i, p.WeenieClassId, p.CellNumber, pos));
                i++;
            }
            if (InstancePlacementCellNumbers.Count > 0 && !NewPlacementCellNumber.HasValue)
                NewPlacementCellNumber = InstancePlacementCellNumbers[0];
        }

        /// <summary>
        /// If ACE DB is configured and the document has no placements yet,
        /// load existing landblock_instance rows for dungeon cells and show them.
        /// </summary>
        private async Task TryLoadDbInstancesAsync(ushort landblockKey) {
            if (_document == null) return;
            if (_document.InstancePlacements.Count > 0) return;
            if (string.IsNullOrWhiteSpace(Settings?.AceDbConnection?.Host)) return;

            try {
                StatusText += " | Loading DB instances...";
                var aceSettings = Settings.AceDbConnection.ToAceDbSettings();
                using var connector = new Shared.Lib.AceDb.AceDbConnector(aceSettings);

                var err = await connector.TestConnectionAsync();
                if (err != null) {
                    StatusText += $" | DB: {err}";
                    return;
                }

                var records = await connector.GetInstancesAsync(
                    landblockKey, cellMin: 0x0100, includeAngles: true);

                if (records.Count == 0) {
                    StatusText += " | No DB instances for this dungeon";
                    return;
                }

                foreach (var r in records) {
                    var dungeonLocal = new System.Numerics.Vector3(r.OriginX, r.OriginY, r.OriginZ);
                    var orientation = new System.Numerics.Quaternion(
                        r.AnglesX ?? 0f, r.AnglesY ?? 0f, r.AnglesZ ?? 0f, r.AnglesW ?? 1f);

                    _document.InstancePlacements.Add(new DungeonInstancePlacement {
                        WeenieClassId = r.WeenieClassId,
                        CellNumber = r.CellId,
                        Origin = dungeonLocal,
                        Orientation = orientation,
                    });
                }

                var wcids = records.Select(r => r.WeenieClassId).Distinct();
                var setupMap = await connector.GetSetupDidsAsync(wcids);
                int withVisual = 0;
                if (_scene != null) {
                    foreach (var (wcid, setupId) in setupMap) {
                        _scene.CacheWeenieSetup(wcid, setupId);
                        withVisual++;
                    }
                }

                RefreshInstancePlacementList();
                RefreshRendering();
                StatusText += $" | {records.Count} DB instance(s), {withVisual} with 3D model";
            }
            catch (Exception ex) {
                StatusText += $" | DB error: {ex.Message}";
                Console.WriteLine($"[Dungeon] DB instance load failed: {ex.Message}");
            }
        }

        [RelayCommand]
        private void AddInstancePlacement() {
            if (_document == null || _document.Cells.Count == 0) return;
            if (!uint.TryParse(NewPlacementWcid.Trim().Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var wcid) && !uint.TryParse(NewPlacementWcid.Trim(), out wcid)) {
                StatusText = "Enter a valid Weenie Class ID (decimal or hex).";
                return;
            }
            ushort cellNum = NewPlacementCellNumber ?? InstancePlacementCellNumbers.FirstOrDefault();
            if (cellNum == 0) return;
            float x = float.TryParse(NewPlacementX, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vx) ? vx : 0f;
            float y = float.TryParse(NewPlacementY, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vy) ? vy : 0f;
            float z = float.TryParse(NewPlacementZ, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vz) ? vz : 0f;
            _document.InstancePlacements.Add(new DungeonInstancePlacement {
                WeenieClassId = wcid,
                CellNumber = cellNum,
                Origin = new System.Numerics.Vector3(x, y, z),
                Orientation = System.Numerics.Quaternion.Identity,
            });
            _document.MarkDirty();
            RefreshInstancePlacementList();
            RefreshRendering();
            StatusText = $"Added weenie {wcid} in room 0x{cellNum:X4}.";
        }

        [RelayCommand]
        private void RemoveInstancePlacement(int index) {
            if (_document == null || index < 0 || index >= _document.InstancePlacements.Count) return;
            _document.InstancePlacements.RemoveAt(index);
            _document.MarkDirty();
            RefreshInstancePlacementList();
            RefreshRendering();
            StatusText = "Removed instance placement.";
        }

        [RelayCommand]
        private void AddPlacementAtSelectedRoom() {
            if (_document == null || Selection?.SelectedCell == null) return;
            var cell = Selection.SelectedCell;
            ushort cellNum = (ushort)(cell.CellId & 0xFFFF);
            var dc = _document.GetCell(cellNum);
            if (dc == null) return;
            NewPlacementCellNumber = cellNum;
            NewPlacementX = dc.Origin.X.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            NewPlacementY = dc.Origin.Y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            NewPlacementZ = dc.Origin.Z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(NewPlacementCellNumber));
            OnPropertyChanged(nameof(NewPlacementX));
            OnPropertyChanged(nameof(NewPlacementY));
            OnPropertyChanged(nameof(NewPlacementZ));
            StatusText = "Prefilled room and position. Enter Weenie Class ID and click Add.";
        }

        public string SelectedCellPanelTitle =>
            SelectedCellCount <= 1 ? "Room Properties" : $"Room Properties ({SelectedCellCount} selected)";

        /// <summary>Full cell ID in 0x01D90101 format for copy/paste into in-game commands.</summary>
        public string SelectedCellLocationHex =>
            Selection?.SelectedCell != null ? $"0x{Selection.SelectedCell.CellId:X8}" : "";

        /// <summary>Full teleport line matching ACE @teleloc format, positioned at the cell floor centroid.</summary>
        public string SelectedCellTeleportCommand =>
            Selection?.SelectedCell != null ? DungeonSelectionManager.ComputeCellTeleportLine(Selection.SelectedCell, _document, _dats) : "";

        private bool _needsCameraFocus;

        private Vector3 _savedPerspectivePos;
        private float _savedPerspectiveYaw;
        private float _savedPerspectivePitch;
        private bool _orthoDragging;
        private Vector2 _orthoDragPrev;

        private IReadOnlyList<(Vector3 From, Vector3 To)> _cachedConnectionLines = Array.Empty<(Vector3, Vector3)>();
        private IReadOnlyList<(Vector3 From, Vector3 To)> _cachedSelectedConnectionLines = Array.Empty<(Vector3, Vector3)>();
        private bool _connectionLinesDirty = true;
        private bool _selectedConnectionLinesDirty = true;

        public DungeonSelectionManager Selection { get; private set; } = null!;
        public CellEditingService CellEditing { get; private set; } = null!;
        public ObjectEditingService ObjectEditing { get; private set; } = null!;
        public DungeonDialogService Dialogs { get; } = new();

        public WorldBuilderSettings Settings { get; }

        private Project? _project;
        private IDatReaderWriter? _dats;
        private DungeonScene? _scene;
        private DungeonDocument? _document;
        private ushort _loadedLandblockKey;

        public DungeonScene? Scene => _scene;
        public DungeonDocument? Document => _document;
        public RoomPaletteViewModel? RoomPalette { get; private set; }
        private DungeonGraphPanelViewModel? _graphPanel;
        public DungeonObjectBrowserViewModel? ObjectBrowser { get; private set; }
        public SurfaceBrowserViewModel? SurfaceBrowser { get; private set; }
        public DungeonHistoryPanelViewModel? HistoryPanel { get; private set; }
        public DungeonCommandHistory CommandHistory { get; } = new();
        public Lib.Docking.DockingManager DockingManager { get; } = new();

        private readonly DungeonOpenPortalCache _openPortalCache = new();


        private readonly TextureImportService? _textureImport;

        public DungeonEditorViewModel(WorldBuilderSettings settings, TextureImportService? textureImport = null) {
            Settings = settings;
            _textureImport = textureImport;
        }

        internal void Init(Project project) {
            _project = project;
            _dats = project.DocumentManager.Dats;
            _scene = new DungeonScene(_dats, Settings);

            // Initialize editing context
            EditingContext.Dats = _dats;
            EditingContext.Scene = _scene;
            EditingContext.CommandHistory = CommandHistory;
            CommandHistory.HistoryLimit = Settings.App.HistoryLimit;
            EditingContext.SelectionChanged += SyncSelectionFromContext;
            EditingContext.RenderingRefreshNeeded += () => {
                _connectionLinesDirty = true;
                NotifyDungeonChanged();
            };
            EditingContext.StatusTextChanged += text => StatusText = text;
            EditingContext.CameraFocusRequested += () => _needsCameraFocus = true;

            Selection = new DungeonSelectionManager(EditingContext);
            Selection.CellSelectionChanged += OnCellSelectionChanged;
            Selection.CellDeselected += OnCellDeselected;
            Selection.ObjectSelectionChanged += OnObjectSelectionChanged;
            Selection.ObjectDeselected += OnObjectDeselected;

            CellEditing = new CellEditingService(EditingContext, Selection, () => _dats, () => RoomPalette);
            ObjectEditing = new ObjectEditingService(EditingContext, Selection);

            RoomPalette = new RoomPaletteViewModel(_dats);
            EditingContext.RoomPalette = RoomPalette;
            RoomPalette.RoomSelected += OnRoomSelected;
            RoomPalette.PrefabSelected += OnPrefabSelected;
            RoomPalette.PrefabHoverChanged += OnPrefabHoverChanged;
            OnPropertyChanged(nameof(RoomPalette));

            // Build portal compatibility index from knowledge base
            var kb = DungeonKnowledgeBuilder.LoadCached();
            if (kb != null) {
                EditingContext.PortalIndex = PortalCompatibilityIndex.Build(kb);
                EditingContext.GeometryCache = new PortalGeometryCache(_dats);
                RoomPalette.PortalIndex = EditingContext.PortalIndex;
            }

            ObjectBrowser = new DungeonObjectBrowserViewModel(_dats,
                () => _scene?.ThumbnailService, settings: Settings);
            ObjectBrowser.PlacementRequested += OnObjectPlacementRequested;
            ObjectBrowser.WeenieSetupsLoaded += (_, mappings) => {
                if (_scene == null) return;
                foreach (var (wcid, setupId) in mappings)
                    _scene.CacheWeenieSetup(wcid, setupId);
                RefreshRendering();
            };
            OnPropertyChanged(nameof(ObjectBrowser));

            SurfaceBrowser = new SurfaceBrowserViewModel(_dats, _textureImport);
            SurfaceBrowser.SurfaceSelected += OnSurfaceSelected;
            OnPropertyChanged(nameof(SurfaceBrowser));

            HistoryPanel = new DungeonHistoryPanelViewModel(
                CommandHistory, () => _document, () => {
                    DeselectCell();
                    RefreshRendering();
                    CellCount = _document?.Cells.Count ?? 0;
                });
            OnPropertyChanged(nameof(HistoryPanel));

            InitTools();
            InitDocking();

            _ = RoomPalette.LoadRoomsAsync();
        }

        private void InitTools() {
            var selectTool = new SelectTool(EditingContext);
            var roomTool = new RoomPlacementTool();
            var objectTool = new ObjectPlacementTool();
            var portalConnectTool = new PortalConnectTool();

            roomTool.CancelRequested += () => { CancelPlacement(); SelectTool(selectTool); };
            objectTool.CancelRequested += () => { CancelObjectPlacement(); SelectTool(selectTool); };

            Tools.Add(selectTool);
            Tools.Add(roomTool);
            Tools.Add(objectTool);
            Tools.Add(portalConnectTool);

            Toolbox = new DungeonToolboxViewModel(this);
            OnPropertyChanged(nameof(Toolbox));

            SelectTool(selectTool);
        }

        [RelayCommand]
        public void SelectTool(DungeonToolBase tool) {
            if (SelectedTool != null) {
                SelectedTool.IsSelected = false;
                SelectedTool.OnDeactivated();
            }
            SelectedTool = tool;
            tool.IsSelected = true;
            SelectedSubTool = tool.SelectedSubTool;
            tool.OnActivated();
            OnPropertyChanged(nameof(SelectedTool));
            OnPropertyChanged(nameof(SelectedSubTool));
        }

        [RelayCommand]
        public void SelectSubTool(DungeonSubToolBase subTool) {
            if (SelectedTool == null) return;
            SelectedTool.ActivateSubTool(subTool);
            SelectedSubTool = subTool;
            OnPropertyChanged(nameof(SelectedSubTool));
        }

        /// <summary>Sync UI-bound selection properties from the editing context.</summary>
        private void SyncSelectionFromContext() {
            var (cellArgs, objArgs) = Selection.SyncFromContext();

            HasSelectedCell = cellArgs?.HasSelection ?? false;
            SelectedCellCount = cellArgs?.Count ?? 0;
            HasSelectedObject = objArgs?.HasSelection ?? false;

            if (cellArgs?.HasSelection == true) {
                OnCellSelectionChanged(cellArgs);
            }
            else if (!HasSelectedObject) {
                OnCellDeselected();
            }

            if (objArgs?.HasSelection == true) {
                SelectObject(objArgs.CellNum, objArgs.ObjectIndex, objArgs.Stab!, objArgs.Stab!.Origin);
            }

            OnPropertyChanged(nameof(SelectedCellLocationHex));
            OnPropertyChanged(nameof(SelectedCellTeleportCommand));
            CellCount = _document?.Cells.Count ?? 0;
        }

        private void InitDocking() {
            var layouts = Settings.Dungeon.UIState.DockingLayout;
            var preferredDock = new Dictionary<string, Lib.Docking.DockLocation>(StringComparer.OrdinalIgnoreCase) {
                ["RoomPalette"] = Lib.Docking.DockLocation.Left,
                ["ObjectBrowser"] = Lib.Docking.DockLocation.Left,
                ["SurfaceBrowser"] = Lib.Docking.DockLocation.Left,
                ["Toolbox"] = Lib.Docking.DockLocation.Right,
                ["History"] = Lib.Docking.DockLocation.Right,
                ["InstancePlacements"] = Lib.Docking.DockLocation.Right,
                ["DungeonGraph"] = Lib.Docking.DockLocation.Right
            };

            // Clear stale layout entries for panels whose default location changed
            layouts.RemoveAll(l => l.Id == "Toolbox");
            foreach (var layout in layouts) {
                if (!preferredDock.TryGetValue(layout.Id, out var expectedLoc)) {
                    continue;
                }

                if (Enum.TryParse<Lib.Docking.DockLocation>(layout.Location, out var savedLoc)
                    && savedLoc != expectedLoc
                    && (savedLoc == Lib.Docking.DockLocation.Bottom
                        || savedLoc == Lib.Docking.DockLocation.Top
                        || savedLoc == Lib.Docking.DockLocation.Center)) {
                    layout.Location = expectedLoc.ToString();
                }
            }

            var graphLayout = layouts.FirstOrDefault(l => l.Id == "DungeonGraph");
            if (graphLayout != null) {
                graphLayout.Location = Lib.Docking.DockLocation.Right.ToString();
                graphLayout.IsVisible = true;
            }

            void Register(string id, string title, object content, Lib.Docking.DockLocation defaultLoc) {
                var panel = new Lib.Docking.DockablePanelViewModel(id, title, content, DockingManager);
                var saved = layouts.FirstOrDefault(l => l.Id == id);
                if (saved != null) {
                    if (Enum.TryParse<Lib.Docking.DockLocation>(saved.Location, out var loc)) panel.Location = loc;
                    panel.IsVisible = saved.IsVisible;
                }
                else {
                    panel.Location = defaultLoc;
                }
                DockingManager.RegisterPanel(panel);
            }

            if (RoomPalette != null) Register("RoomPalette", "Dungeon Pieces", RoomPalette, Lib.Docking.DockLocation.Left);
            if (ObjectBrowser != null) Register("ObjectBrowser", "Object Browser", ObjectBrowser, Lib.Docking.DockLocation.Left);
            if (SurfaceBrowser != null) Register("SurfaceBrowser", "Surfaces", SurfaceBrowser, Lib.Docking.DockLocation.Left);
            if (Toolbox != null) Register("Toolbox", "Tools", Toolbox, Lib.Docking.DockLocation.Right);
            _graphPanel = new DungeonGraphPanelViewModel(this);
            Register("DungeonGraph", "Dungeon Map", _graphPanel, Lib.Docking.DockLocation.Right);
            if (HistoryPanel != null) Register("History", "History", HistoryPanel, Lib.Docking.DockLocation.Right);
            Register("InstancePlacements", "Instance Placements", new InstancePlacementsPanelViewModel(this), Lib.Docking.DockLocation.Right);

            // Normalize known panel locations when legacy layouts put them in top/bottom/center.
            foreach (var panel in DockingManager.AllPanels) {
                if (!preferredDock.TryGetValue(panel.Id, out var expectedLoc)) {
                    continue;
                }

                if (panel.Location == Lib.Docking.DockLocation.Bottom
                    || panel.Location == Lib.Docking.DockLocation.Top
                    || panel.Location == Lib.Docking.DockLocation.Center) {
                    DockingManager.MovePanel(panel, expectedLoc);
                }
            }

            var uiState = Settings.Dungeon.UIState;
            if (Enum.TryParse<Lib.Docking.DockRegionMode>(uiState.LeftDockMode, out var leftMode))
                DockingManager.LeftMode = leftMode;
            if (Enum.TryParse<Lib.Docking.DockRegionMode>(uiState.RightDockMode, out var rightMode))
                DockingManager.RightMode = rightMode;
            if (Enum.TryParse<Lib.Docking.DockRegionMode>(uiState.TopDockMode, out var topMode))
                DockingManager.TopMode = topMode;
            if (Enum.TryParse<Lib.Docking.DockRegionMode>(uiState.BottomDockMode, out var bottomMode))
                DockingManager.BottomMode = bottomMode;

            OnPropertyChanged(nameof(DockingManager));
        }

        private void SaveDockingState() {
            var uiState = Settings.Dungeon.UIState;
            uiState.DockingLayout.Clear();
            foreach (var panel in DockingManager.AllPanels.OfType<Lib.Docking.DockablePanelViewModel>()) {
                uiState.DockingLayout.Add(new Lib.Settings.DockingPanelState {
                    Id = panel.Id,
                    Location = panel.Location.ToString(),
                    IsVisible = panel.IsVisible
                });
            }
            uiState.LeftDockMode = DockingManager.LeftMode.ToString();
            uiState.RightDockMode = DockingManager.RightMode.ToString();
            uiState.TopDockMode = DockingManager.TopMode.ToString();
            uiState.BottomDockMode = DockingManager.BottomMode.ToString();
            Settings.Save();
        }

        /// <summary>
        /// Called by the viewport control on GL init to pass the renderer.
        /// </summary>
        internal void OnRendererReady(OpenGLRenderer renderer) {
            _scene?.InitGpu(renderer);

            if (_scene?.EnvCellManager != null && _textureImport != null) {
                var importSvc = _textureImport;
                _scene.EnvCellManager.CustomTextureResolver = (surfaceId) => {
                    var entry = importSvc.Store.GetDungeonSurfaces()
                        .FirstOrDefault(e => e.SurfaceGid == surfaceId);
                    if (entry == null) return null;

                    var rgba = importSvc.LoadTextureRgba(entry, entry.Width, entry.Height);
                    if (rgba == null) return null;

                    return (rgba, entry.Width, entry.Height);
                };
            }
        }

        /// <summary>
        /// Render one frame. Called by the viewport on the GL thread.
        /// </summary>
        internal void RenderFrame(double deltaTime, Avalonia.PixelSize canvasSize, AvaloniaInputState inputState) {
            if (_scene == null) return;

            HandleInput(inputState, deltaTime);

            _scene.Camera.ScreenSize = new Vector2(canvasSize.Width, canvasSize.Height);
            if (_connectionLinesDirty) {
                _cachedConnectionLines = DungeonSelectionManager.ComputeConnectionLines(_document, _dats);
                _connectionLinesDirty = false;
                _selectedConnectionLinesDirty = true;
            }
            if (_selectedConnectionLinesDirty) {
                _cachedSelectedConnectionLines = Selection.ComputeSelectedConnectionLines(_document, _dats);
                _selectedConnectionLinesDirty = false;
            }
            _scene.ConnectionLines = _cachedConnectionLines;
            _scene.SelectedConnectionLines = _cachedSelectedConnectionLines;
            _scene.ShowConnectionLines = ShowConnectionLines;
            _scene.ShowPortalIndicators = ShowPortalIndicators;
            _scene.ShowGrid = ShowGrid;
            _scene.UseOrthographic = IsOrthographic;
            _scene.Render((float)canvasSize.Width / canvasSize.Height);

            // Deferred camera focus: wait until cells are actually uploaded to GPU
            if (_needsCameraFocus && _scene.EnvCellManager != null && _scene.EnvCellManager.LoadedCellCount > 0) {
                if (_targetCellId >= 0x0100) {
                    _scene.FocusCameraOnCell(_loadedLandblockKey, _targetCellId);
                }
                else {
                    _scene.FocusCamera();
                }
                _needsCameraFocus = false;
                _targetCellId = 0;
            }

            var cam = _scene.Camera.Position;
            CurrentPositionText = $"({cam.X:F0}, {cam.Y:F0}, {cam.Z:F0})";
        }

        private void HandleInput(AvaloniaInputState inputState, double deltaTime) {
            if (_scene == null) return;
            var camera = _scene.Camera;

            if (IsOrthographic) {
                HandleOrthoInput(inputState, deltaTime);
                return;
            }

            camera.ProcessMouseMovement(inputState.MouseState);

            bool shiftHeld = inputState.IsKeyDown(Key.LeftShift) || inputState.IsKeyDown(Key.RightShift);
            bool ctrlHeld = inputState.IsKeyDown(Key.LeftCtrl) || inputState.IsKeyDown(Key.RightCtrl);

            if ((shiftHeld || ctrlHeld)) {
                float rotateSpeed = 60f * (float)deltaTime;
                if (inputState.IsKeyDown(Key.Left)) camera.ProcessKeyboardRotation(rotateSpeed, 0);
                if (inputState.IsKeyDown(Key.Right)) camera.ProcessKeyboardRotation(-rotateSpeed, 0);
                if (inputState.IsKeyDown(Key.Up)) camera.ProcessKeyboardRotation(0, rotateSpeed);
                if (inputState.IsKeyDown(Key.Down)) camera.ProcessKeyboardRotation(0, -rotateSpeed);
            }

            if (inputState.IsKeyDown(Key.W) || ((!shiftHeld && !ctrlHeld) && inputState.IsKeyDown(Key.Up)))
                camera.ProcessKeyboard(CameraMovement.Forward, deltaTime);
            if (inputState.IsKeyDown(Key.S) || ((!shiftHeld && !ctrlHeld) && inputState.IsKeyDown(Key.Down)))
                camera.ProcessKeyboard(CameraMovement.Backward, deltaTime);
            if (inputState.IsKeyDown(Key.A) || ((!shiftHeld && !ctrlHeld) && inputState.IsKeyDown(Key.Left)))
                camera.ProcessKeyboard(CameraMovement.Left, deltaTime);
            if (inputState.IsKeyDown(Key.D) || ((!shiftHeld && !ctrlHeld) && inputState.IsKeyDown(Key.Right)))
                camera.ProcessKeyboard(CameraMovement.Right, deltaTime);

            if (inputState.IsKeyDown(Key.Space))
                camera.ProcessKeyboard(CameraMovement.Up, deltaTime);
            if (inputState.IsKeyDown(Key.C))
                camera.ProcessKeyboard(CameraMovement.Down, deltaTime);
        }

        private void HandleOrthoInput(AvaloniaInputState inputState, double deltaTime) {
            if (_scene == null) return;
            var camera = _scene.Camera;
            var mouse = inputState.MouseState;

            if (mouse.RightPressed) {
                if (!_orthoDragging) {
                    _orthoDragging = true;
                    _orthoDragPrev = mouse.Position;
                }
                else {
                    var delta = mouse.Position - _orthoDragPrev;
                    float pixelsToWorld = _scene.OrthoSize / camera.ScreenSize.Y;
                    camera.SetPosition(camera.Position + new Vector3(
                        -delta.X * pixelsToWorld,
                        delta.Y * pixelsToWorld,
                        0));
                    _orthoDragPrev = mouse.Position;
                }
            }
            else {
                _orthoDragging = false;
            }

            float panSpeed = _scene.OrthoSize * 0.5f * (float)deltaTime;
            if (inputState.IsKeyDown(Key.W) || inputState.IsKeyDown(Key.Up))
                camera.SetPosition(camera.Position + new Vector3(0, panSpeed, 0));
            if (inputState.IsKeyDown(Key.S) || inputState.IsKeyDown(Key.Down))
                camera.SetPosition(camera.Position - new Vector3(0, panSpeed, 0));
            if (inputState.IsKeyDown(Key.A) || inputState.IsKeyDown(Key.Left))
                camera.SetPosition(camera.Position - new Vector3(panSpeed, 0, 0));
            if (inputState.IsKeyDown(Key.D) || inputState.IsKeyDown(Key.Right))
                camera.SetPosition(camera.Position + new Vector3(panSpeed, 0, 0));
        }

        internal void HandleKeyDown(KeyEventArgs e) {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            if (e.Key == Key.Home) {
                ResetCamera();
                return;
            }
            if (ctrl && e.Key == Key.G) {
                _ = OpenLandblockCommand.ExecuteAsync(null);
                return;
            }
            if (ctrl && e.Key == Key.Z) {
                if (shift) UndoRedoRedo();
                else UndoRedoUndo();
                return;
            }
            if (ctrl && e.Key == Key.Y) {
                UndoRedoRedo();
                return;
            }
            if (ctrl && e.Key == Key.C) {
                CopySelectedCells();
                return;
            }
            if (ctrl && e.Key == Key.V) {
                PasteCells();
                return;
            }
            if (ctrl && e.Key == Key.S) {
                SaveDungeonCommand.Execute(null);
                return;
            }
            if (e.Key == Key.Delete) {
                if (HasSelectedObject) DeleteSelectedObjectCommand.Execute(null);
                else DeleteSelectedCellCommand.Execute(null);
                return;
            }

            // Delegate to active tool (handles Escape for placement tools)
            SyncContextBeforeInput();
            if (SelectedTool != null && SelectedTool.HandleKeyDown(e, EditingContext)) return;

            if (e.Key == Key.Escape) {
                if (IsObjectPlacementMode) CancelObjectPlacement();
                else if (IsPlacementMode) CancelPlacement();
                else if (HasSelectedObject) DeselectObject();
                else DeselectCell();
            }
        }

        [RelayCommand]
        private void Undo() => UndoRedoUndo();

        [RelayCommand]
        private void Redo() => UndoRedoRedo();

        private void UndoRedoUndo() {
            if (_document == null || !CommandHistory.CanUndo) return;
            CommandHistory.Undo(_document);
            DeselectCell();
            RefreshRendering();
            CellCount = _document.Cells.Count;
            StatusText = $"{CellCount} rooms (Undo: {CommandHistory.LastCommandDescription ?? "none"})";
        }

        private void UndoRedoRedo() {
            if (_document == null || !CommandHistory.CanRedo) return;
            CommandHistory.Redo(_document);
            DeselectCell();
            RefreshRendering();
            CellCount = _document.Cells.Count;
            StatusText = $"{CellCount} rooms";
        }

        internal void HandlePointerWheel(PointerWheelEventArgs e) {
            if (IsOrthographic && _scene != null) {
                _scene.OrthoSize = Math.Max(10f, _scene.OrthoSize - (float)e.Delta.Y * _scene.OrthoSize * 0.1f);
            }
            else {
                _scene?.Camera.ProcessMouseScroll((float)e.Delta.Y);
            }
        }

        internal void HandlePointerPressed(AvaloniaInputState inputState) {
            if (_scene?.EnvCellManager == null) return;
            var mouse = inputState.MouseState;
            if (!mouse.LeftPressed || mouse.RightPressed) return;

            SyncContextBeforeInput();
            if (SelectedTool != null && SelectedTool.HandleMouseDown(mouse, EditingContext)) return;

            // Legacy fallback for any unhandled cases
            if (IsPlacementMode && _pendingRoom != null && _document != null && _document.Cells.Count == 0) {
                PlaceFirstCell();
            }
        }

        internal void HandlePointerMoved(AvaloniaInputState inputState) {
            if (_scene?.EnvCellManager == null) return;
            SyncContextBeforeInput();
            SelectedTool?.HandleMouseMove(inputState.MouseState, EditingContext);
            UpdateCursorHud(inputState.MouseState);
        }

        internal void HandlePointerReleased(AvaloniaInputState inputState) {
            SyncContextBeforeInput();
            SelectedTool?.HandleMouseUp(inputState.MouseState, EditingContext);
            IsDraggingCell = false;
            IsDraggingObject = false;
        }

        private void UpdateCursorHud(MouseState mouseState) {
            if (_scene?.Camera == null) return;
            var ray = EditingContext.ComputeRay(mouseState);
            if (ray == null) {
                ShowCursorHud = false;
                return;
            }
            var hit = EditingContext.Raycast(ray.Value.origin, ray.Value.direction);
            if (hit.Hit) {
                var p = hit.HitPosition;
                var roomName = EditingContext.RoomPalette?.GetRoomDisplayName(hit.Cell.EnvironmentId, (ushort)hit.Cell.GpuKey.CellStructure);
                var label = !string.IsNullOrEmpty(roomName) ? roomName : $"Env 0x{hit.Cell.EnvironmentId:X4}";
                CursorHudText = $"({p.X:F1}, {p.Y:F1}, {p.Z:F1})  {label}";
                ShowCursorHud = true;
            }
            else {
                ShowCursorHud = false;
            }
        }

        /// <summary>Push current VM state into the editing context before tool input.</summary>
        private void SyncContextBeforeInput() {
            EditingContext.Document = _document;
            EditingContext.Scene = _scene;
            EditingContext.GridSnapEnabled = GridSnapEnabled;
            EditingContext.GridSnapSize = GridSnapSize;
        }

        private void SelectCell(LoadedEnvCell cell) => Selection.SelectCell(cell);
        private void ToggleCellInSelection(LoadedEnvCell cell) => Selection.ToggleCellInSelection(cell);
        private void DeselectCell() => Selection.DeselectCell();
        private void SelectObject(ushort cellNum, int objectIndex, DungeonStabData stab, Vector3 hitPosition) =>
            Selection.SelectObject(cellNum, objectIndex, stab);
        private void DeselectObject() => Selection.DeselectObject();
        private void UpdateObjectSelectionHighlight(DungeonStabData? stab = null) =>
            Selection.UpdateObjectSelectionHighlight(stab);

        private void OnCellSelectionChanged(CellSelectionChangedArgs args) {
            HasSelectedCell = true;
            SelectedCellCount = args.Count;
            if (args.Count == 1 && args.PrimaryCell != null) {
                SelectedCellInfo = Selection.BuildCellInfoString(args.PrimaryCell, true, RoomPalette, _document);
                var dc = _document?.GetCell((ushort)(args.PrimaryCell.CellId & 0xFFFF));
                if (dc != null) {
                    SelectedCellSurfaces = string.Join(", ", dc.Surfaces.Select(s => s.ToString("X4")));
                    CellPosX = dc.Origin.X.ToString("F1");
                    CellPosY = dc.Origin.Y.ToString("F1");
                    CellPosZ = dc.Origin.Z.ToString("F1");
                    var euler = CellEditingService.QuatToEuler(dc.Orientation);
                    CellRotX = euler.X.ToString("F1");
                    CellRotY = euler.Y.ToString("F1");
                    CellRotZ = euler.Z.ToString("F1");
                    RefreshSurfaceSlots(dc);
                    SurfaceBrowser?.SetCurrentCellSurfaces(dc.Surfaces);
                }
            }
            else if (args.PrimaryCell != null) {
                SelectedCellInfo = $"{args.Count} rooms selected";
                var primaryDc = _document?.GetCell((ushort)(args.PrimaryCell.CellId & 0xFFFF));
                if (primaryDc != null) {
                    SelectedCellSurfaces = string.Join(", ", primaryDc.Surfaces.Select(s => s.ToString("X4")));
                    RefreshSurfaceSlots(primaryDc);
                    SurfaceBrowser?.SetCurrentCellSurfaces(primaryDc.Surfaces);
                }
                CellPosX = ""; CellPosY = ""; CellPosZ = "";
                CellRotX = ""; CellRotY = ""; CellRotZ = "";
            }
            RefreshPortalList();
            UpdateConnectedNeighborHighlights();
            _selectedConnectionLinesDirty = true;
            OnPropertyChanged(nameof(SelectedCellLocationHex));
            OnPropertyChanged(nameof(SelectedCellTeleportCommand));
            RefreshGraphView();
        }

        private void OnCellDeselected() {
            HasSelectedCell = false;
            SelectedCellCount = 0;
            SelectedCellInfo = "";
            SurfaceSlots.Clear();
            PortalList.Clear();
            SelectedSurfaceSlot = -1;
            CellPosX = ""; CellPosY = ""; CellPosZ = "";
            CellRotX = ""; CellRotY = ""; CellRotZ = "";
            SurfaceBrowser?.SetCurrentCellSurfaces(null);
            if (_scene != null) {
                _scene.ConnectedNeighborCells = null;
                _scene.SelectedConnectionLines = null;
            }
            _cachedSelectedConnectionLines = Array.Empty<(Vector3, Vector3)>();
            _selectedConnectionLinesDirty = false;
            OnPropertyChanged(nameof(SelectedCellLocationHex));
            OnPropertyChanged(nameof(SelectedCellTeleportCommand));
        }

        private void OnObjectSelectionChanged(ObjectSelectionChangedArgs args) {
            HasSelectedObject = true;
            if (args.Stab != null) {
                var stab = args.Stab;
                SelectedObjectInfo = $"Object 0x{stab.Id:X8}  |  Room {args.CellNum:X4}\n" +
                    $"Pos: ({stab.Origin.X:F1}, {stab.Origin.Y:F1}, {stab.Origin.Z:F1})";
                ObjPosX = stab.Origin.X.ToString("F1");
                ObjPosY = stab.Origin.Y.ToString("F1");
                ObjPosZ = stab.Origin.Z.ToString("F1");
                var q = stab.Orientation;
                float deg = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z)) * 180f / MathF.PI;
                ObjRotDegrees = deg.ToString("F1");
            }
        }

        private void OnObjectDeselected() {
            HasSelectedObject = false;
            SelectedObjectInfo = "";
            ObjPosX = ""; ObjPosY = ""; ObjPosZ = "";
            ObjRotDegrees = "";
        }

        #region Cell Editing

        [RelayCommand]
        private void DeleteSelectedCell() {
            var result = CellEditing.DeleteSelectedCells();
            if (result == null) return;
            RefreshRendering();
            CellCount = result.Value.remaining;
            StatusText = $"Removed {result.Value.removed} room(s), {CellCount} remaining";
        }

        [RelayCommand] private void NudgeCellXPos() => NudgeSelectedCell(new Vector3(NudgeStep, 0, 0));
        [RelayCommand] private void NudgeCellXNeg() => NudgeSelectedCell(new Vector3(-NudgeStep, 0, 0));
        [RelayCommand] private void NudgeCellYPos() => NudgeSelectedCell(new Vector3(0, NudgeStep, 0));
        [RelayCommand] private void NudgeCellYNeg() => NudgeSelectedCell(new Vector3(0, -NudgeStep, 0));
        [RelayCommand] private void NudgeCellZPos() => NudgeSelectedCell(new Vector3(0, 0, NudgeStep));
        [RelayCommand] private void NudgeCellZNeg() => NudgeSelectedCell(new Vector3(0, 0, -NudgeStep));

        [RelayCommand] private void RotateCellXPos() => RotateSelectedCell(90, Vector3.UnitX);
        [RelayCommand] private void RotateCellXNeg() => RotateSelectedCell(-90, Vector3.UnitX);
        [RelayCommand] private void RotateCellYPos() => RotateSelectedCell(90, Vector3.UnitY);
        [RelayCommand] private void RotateCellYNeg() => RotateSelectedCell(-90, Vector3.UnitY);
        [RelayCommand] private void RotateCellZPos() => RotateSelectedCell(90, Vector3.UnitZ);
        [RelayCommand] private void RotateCellZNeg() => RotateSelectedCell(-90, Vector3.UnitZ);
        [RelayCommand] private void RotateCellZ45Pos() => RotateSelectedCell(45, Vector3.UnitZ);
        [RelayCommand] private void RotateCellZ45Neg() => RotateSelectedCell(-45, Vector3.UnitZ);

        private void RotateSelectedCell(float degrees, Vector3 axis) {
            CellEditing.RotateSelectedCell(degrees, axis);
            RefreshRendering();
            _needsCameraFocus = false;
        }

        private void NudgeSelectedCell(Vector3 offset) {
            CellEditing.NudgeSelectedCell(offset);
            RefreshRendering();
            _needsCameraFocus = false;
        }

        [RelayCommand]
        private void ApplyPosition() {
            var status = CellEditing.ApplyPosition(CellPosX, CellPosY, CellPosZ);
            if (status != null) { RefreshRendering(); StatusText = status; }
        }

        [RelayCommand]
        private void ApplyRotation() {
            var status = CellEditing.ApplyRotation(CellRotX, CellRotY, CellRotZ);
            if (status != null) { RefreshRendering(); StatusText = status; }
        }

        [ObservableProperty] private string _selectedCellSurfaces = "";

        private void RefreshSurfaceSlots(DungeonCellData dc) {
            var slots = CellEditing.BuildSurfaceSlots(dc);
            SurfaceSlots = new ObservableCollection<CellSurfaceSlot>(slots);
            if (SurfaceSlots.Count > 0) SelectedSurfaceSlot = 0;
        }

        [RelayCommand]
        private void ApplySurfaces() {
            var status = CellEditing.ApplySurfaces(SelectedCellSurfaces);
            if (status != null) { RefreshRendering(); StatusText = status; }
        }

        [RelayCommand]
        private void MatchToStyle() {
            if (_document == null || _dats == null || Selection.SelectedCells.Count == 0) {
                StatusText = "Select one or more rooms first, then use Match to Style";
                return;
            }

            var kb = DungeonKnowledgeBuilder.LoadCached();
            if (kb == null) {
                StatusText = "Knowledge base not loaded — run Analyze Rooms first";
                return;
            }

            // Determine the style from the first selected cell
            var firstCell = Selection.SelectedCells[0];
            var firstDc = _document.GetCell((ushort)(firstCell.CellId & 0xFFFF));
            if (firstDc == null) return;
            var catalogEntry = kb.Catalog.FirstOrDefault(c => c.EnvId == firstDc.EnvironmentId && c.CellStruct == firstDc.CellStructure);
            var style = catalogEntry?.Style;
            if (string.IsNullOrEmpty(style)) {
                StatusText = "Could not determine style from selected room — try a different room";
                return;
            }

            // Apply retexture via commands for undo support
            var composite = new DungeonCompositeCommand($"Match to Style ({style})");
            int count = 0;
            foreach (var cell in Selection.SelectedCells) {
                var cellNum = (ushort)(cell.CellId & 0xFFFF);
                var dc = _document.GetCell(cellNum);
                if (dc == null || dc.Surfaces.Count == 0) continue;

                var newSurfaces = DungeonGenerator.ComputeRetexturedSurfaces(dc, kb, style);
                if (newSurfaces != null && newSurfaces.Count == dc.Surfaces.Count) {
                    composite.Add(new SetCellSurfacesCommand(cellNum, dc.Surfaces, newSurfaces));
                    count++;
                }
            }

            if (count == 0) {
                StatusText = $"No matching style surfaces found for '{style}'";
                return;
            }

            CommandHistory.Execute(composite, _document);
            RefreshRendering();
            if (Selection.SelectedCell != null) {
                var dc = _document.GetCell((ushort)(Selection.SelectedCell.CellId & 0xFFFF));
                if (dc != null) RefreshSurfaceSlots(dc);
            }
            StatusText = $"Matched {count} room(s) to '{style}' style";
        }

        private void OnSurfaceSelected(object? sender, ushort surfaceId) {
            var (status, surfText, primaryDc) = CellEditing.ApplySurfaceFromBrowser(surfaceId, SelectedSurfaceSlot);
            RefreshRendering();
            if (surfText != null) SelectedCellSurfaces = surfText;
            if (primaryDc != null) RefreshSurfaceSlots(primaryDc);
            StatusText = status;
        }

        [RelayCommand]
        private void DisconnectPortal() {
            CellEditing.DisconnectLastPortal();
            RefreshRendering();
            if (Selection.SelectedCell != null) SelectCell(Selection.SelectedCell);
        }

        [RelayCommand]
        private void DisconnectPortalAt(int index) {
            CellEditing.DisconnectPortalAt(index);
            RefreshRendering();
            if (Selection.SelectedCell != null) SelectCell(Selection.SelectedCell);
        }

        [RelayCommand]
        private void CapPortal(PortalListEntry? entry) {
            if (entry == null || !entry.IsOpen || _document == null || _dats == null) return;
            var cappingService = CreateCappingService();
            if (cappingService == null) { StatusText = "Knowledge base not loaded"; return; }

            var result = cappingService.CapPortal(entry.OwnerCellNum, entry.PolygonId);
            if (result != null) {
                EditingContext.CommandHistory.Record(result);
                RefreshRendering();
                if (Selection.SelectedCell != null) SelectCell(Selection.SelectedCell);
                StatusText = $"Capped portal with dead-end room";
            }
            else {
                StatusText = "No compatible dead-end room found for this portal";
            }
        }

        [RelayCommand]
        private void FilterPaletteForPortal(PortalListEntry? entry) {
            if (entry == null || !entry.IsOpen || RoomPalette == null) return;
            var singlePortal = new List<(ushort envId, ushort cs, ushort polyId)> {
                (entry.OwnerEnvId, entry.OwnerCellStruct, entry.PolygonId)
            };
            RoomPalette.SetActiveOpenPortals(singlePortal);
            RoomPalette.ShowCompatibleOnly = true;
            StatusText = $"Palette filtered to portal — {entry.CompatibleCount} compatible pieces";
        }

        [RelayCommand]
        private async Task CapAllOpenPortals() {
            if (_document == null || _dats == null) return;
            var cappingService = CreateCappingService();
            if (cappingService == null) { StatusText = "Knowledge base not loaded"; return; }

            StatusText = "Capping open portals...";
            var (sealed_, failed, composite) = await Task.Run(() => cappingService.CapAllOpenPortals());

            if (composite != null) {
                EditingContext.CommandHistory.Record(composite);
                RefreshRendering();
                if (Selection.SelectedCell != null) SelectCell(Selection.SelectedCell);
            }
            StatusText = sealed_ > 0
                ? $"Sealed {sealed_} open portal{(sealed_ != 1 ? "s" : "")} with dead-end rooms" + (failed > 0 ? $" ({failed} could not be capped)" : "")
                : "No open portals could be capped";
        }

        private DungeonCappingService? CreateCappingService() {
            if (_document == null || _dats == null) return null;
            var kb = DungeonKnowledgeBuilder.LoadCached();
            if (kb == null || kb.DeadEndIndex.Count == 0) return null;

            var portalIndex = EditingContext.PortalIndex;
            if (portalIndex == null) {
                portalIndex = PortalCompatibilityIndex.Build(kb);
                EditingContext.PortalIndex = portalIndex;
                RoomPalette.PortalIndex = portalIndex;
            }
            var geoCache = EditingContext.GeometryCache ?? new PortalGeometryCache(_dats);
            EditingContext.GeometryCache = geoCache;

            return new DungeonCappingService(_document, _dats, kb, portalIndex, geoCache);
        }

        private void UpdateConnectedNeighborHighlights() {
            if (_scene == null || _document == null) return;
            var neighborNums = Selection.GetConnectedNeighborCellNums();
            if (neighborNums.Count == 0) {
                _scene.ConnectedNeighborCells = null;
                return;
            }
            var lbKey = _document.LandblockKey;
            var loadedCells = _scene.EnvCellManager?.GetLoadedCellsForLandblock(lbKey);
            if (loadedCells == null) { _scene.ConnectedNeighborCells = null; return; }
            var neighbors = new List<LoadedEnvCell>();
            foreach (var cell in loadedCells) {
                var cn = (ushort)(cell.CellId & 0xFFFF);
                if (neighborNums.Contains(cn)) neighbors.Add(cell);
            }
            _scene.ConnectedNeighborCells = neighbors;
        }

        private void RefreshPortalList() {
            PortalList.Clear();
            foreach (var entry in CellEditing.BuildPortalList())
                PortalList.Add(entry);
        }

        internal List<(string Label, Action Action, bool IsEnabled)> GetCellContextMenuItems(Vector3 rayOrigin, Vector3 rayDir) {
            return CellEditing.GetCellContextMenuItems(rayOrigin, rayDir,
                deleteAction: () => { DeleteSelectedCellCommand.Execute(null); },
                favoriteAction: () => { FavoriteSelectedRoomCommand.Execute(null); },
                saveAsPrefabAction: () => { SaveSelectionAsPrefabCommand.Execute(null); });
        }

        internal void CopySelectedCells() {
            var status = CellEditing.CopySelectedCells();
            if (status != null) StatusText = status;
        }

        internal void PasteCells() {
            var result = CellEditing.PasteCells();
            if (result == null) return;
            RefreshRendering();
            CellCount = _document?.Cells.Count ?? 0;
            StatusText = result.Value.status;

            DeselectCell();
            if (_scene?.EnvCellManager != null && _document != null) {
                uint lbId = _document.LandblockKey;
                LoadedEnvCell? first = null;
                foreach (var newCellNum in result.Value.createdNums) {
                    uint fullId = ((uint)lbId << 16) | newCellNum;
                    var loaded = _scene.EnvCellManager.FindCell(fullId);
                    if (loaded != null) {
                        if (first == null) { first = loaded; Selection.SelectCell(loaded); }
                        else Selection.ToggleCellInSelection(loaded);
                    }
                }
            }
        }

        #endregion

        #region Object Editing

        [RelayCommand] private void NudgeObjXPos() => NudgeSelectedObject(new Vector3(NudgeStep, 0, 0));
        [RelayCommand] private void NudgeObjXNeg() => NudgeSelectedObject(new Vector3(-NudgeStep, 0, 0));
        [RelayCommand] private void NudgeObjYPos() => NudgeSelectedObject(new Vector3(0, NudgeStep, 0));
        [RelayCommand] private void NudgeObjYNeg() => NudgeSelectedObject(new Vector3(0, -NudgeStep, 0));
        [RelayCommand] private void NudgeObjZPos() => NudgeSelectedObject(new Vector3(0, 0, NudgeStep));
        [RelayCommand] private void NudgeObjZNeg() => NudgeSelectedObject(new Vector3(0, 0, -NudgeStep));

        [RelayCommand] private void RotateObjCW() => RotateSelectedObject(-90);
        [RelayCommand] private void RotateObjCW45() => RotateSelectedObject(-45);
        [RelayCommand] private void RotateObjCCW45() => RotateSelectedObject(45);
        [RelayCommand] private void RotateObjCCW() => RotateSelectedObject(90);

        private void NudgeSelectedObject(Vector3 offset) {
            ObjectEditing.NudgeSelectedObject(offset);
            RefreshRendering();
            UpdateObjectSelectionHighlight();
            RefreshSelectedObjectFields();
        }

        private void RotateSelectedObject(float degrees) {
            ObjectEditing.RotateSelectedObject(degrees);
            RefreshRendering();
            UpdateObjectSelectionHighlight();
            RefreshSelectedObjectFields();
        }

        [RelayCommand]
        private void DeleteSelectedObject() {
            ObjectEditing.DeleteSelectedObject();
            DeselectObject();
            RefreshRendering();
            StatusText = "Object deleted";
        }

        [RelayCommand]
        private void ApplyObjPosition() {
            var status = ObjectEditing.ApplyObjPosition(ObjPosX, ObjPosY, ObjPosZ);
            if (status != null) {
                RefreshRendering();
                UpdateObjectSelectionHighlight();
                RefreshSelectedObjectFields();
                StatusText = status;
            }
        }

        [RelayCommand]
        private void ApplyObjRotation() {
            var status = ObjectEditing.ApplyObjRotation(ObjRotDegrees);
            if (status != null) {
                RefreshRendering();
                UpdateObjectSelectionHighlight();
                RefreshSelectedObjectFields();
                StatusText = status;
            }
        }

        private void RefreshSelectedObjectFields() {
            var fields = ObjectEditing.GetSelectedObjectFields();
            if (fields == null) return;
            ObjPosX = fields.Value.px;
            ObjPosY = fields.Value.py;
            ObjPosZ = fields.Value.pz;
            ObjRotDegrees = fields.Value.rot;
            SelectedObjectInfo = fields.Value.info;
        }

        #endregion

        #region Object Placement

        private void OnObjectPlacementRequested(object? sender, Landscape.ViewModels.ObjectBrowserItem item) {
            if (item.WeenieClassId.HasValue && item.Id == 0) {
                NewPlacementWcid = item.WeenieClassId.Value.ToString();
                StatusText = $"Weenie {item.WeenieClassId.Value} has no 3D model — use Instance Placements panel to place manually.";
                return;
            }

            if (item.WeenieClassId.HasValue) {
                ObjectEditing.SetPendingWeenie(item.WeenieClassId.Value, item.Id);
                _scene?.CacheWeenieSetup(item.WeenieClassId.Value, item.Id);
                PlacementStatusText = $"Click in viewport to place WCID {item.WeenieClassId.Value}";
            }
            else {
                ObjectEditing.SetPendingObject(item.Id, item.IsSetup);
                PlacementStatusText = $"Click in viewport to place 0x{item.Id:X8}";
            }

            IsObjectPlacementMode = true;
            _scene?.WarmupModel(item.Id, item.IsSetup);

            var objTool = Tools.OfType<ObjectPlacementTool>().FirstOrDefault();
            if (objTool != null) {
                objTool.SetObject(item.Id, item.IsSetup);
                if (SelectedTool != objTool) SelectTool(objTool);
            }
        }

        [RelayCommand]
        private void StartObjectPlacement() {
            var objId = ObjectEditing.ParseObjectId(ObjectIdInput);
            if (objId == null) { StatusText = "Invalid object ID"; return; }

            bool isSetup = (objId.Value & 0xFF000000) == 0x02000000;
            ObjectEditing.SetPendingObject(objId.Value, isSetup);
            IsObjectPlacementMode = true;
            PlacementStatusText = $"Click in viewport to place object 0x{objId.Value:X8}";
            _scene?.WarmupModel(objId.Value, isSetup);

            var objTool = Tools.OfType<ObjectPlacementTool>().FirstOrDefault();
            if (objTool != null) {
                objTool.SetObject(objId.Value, isSetup);
                if (SelectedTool != objTool) SelectTool(objTool);
            }
        }

        [RelayCommand]
        private void CancelObjectPlacement() {
            ObjectEditing.ClearPendingObject();
            IsObjectPlacementMode = false;
            if (_scene != null) _scene.PlacementPreview = null;
            if (!IsPlacementMode) PlacementStatusText = "";
        }

        private void TryPlaceObject(Vector3 rayOrigin, Vector3 rayDir) {
            var status = ObjectEditing.TryPlaceObject(rayOrigin, rayDir);
            if (status != null) {
                RefreshRendering();
                RefreshInstancePlacementList();
                StatusText = status;
            }
        }

        #endregion

        #region Placement

        private RoomEntry? _pendingRoom;

        private void OnRoomSelected(object? sender, RoomEntry room) {
            if (_document == null) EnsureDocument();
            HasDungeon = true;
            EditingContext.Document = _document;

            // Activate the RoomPlacementTool and set the pending room
            var roomTool = Tools.OfType<RoomPlacementTool>().FirstOrDefault();
            if (roomTool != null) {
                roomTool.SetRoom(room);
                if (SelectedTool != roomTool) SelectTool(roomTool);
            }

            _pendingRoom = room;
            IsPlacementMode = true;
            PlacementStatusText = roomTool?.StatusText ?? $"Placing: {room.DisplayName}";
            if (_scene != null) _scene.IsInPlacementMode = true;
        }

        private bool _hoverCameraFocused;

        private void OnPrefabHoverChanged(object? sender, DungeonPrefab? prefab) {
            if (_scene == null || _dats == null) return;

            if (prefab == null) {
                _scene.ClearPreview();
                _scene.PreviewIsSnapped = false;
                return;
            }

            var (previewOrigin, previewRot, snapped) = ComputeHoverPreviewTransform(prefab);
            var previewCells = EditingContext.BuildPrefabEnvCells(prefab, previewOrigin, previewRot);
            _scene.PreviewIsSnapped = snapped;

            if (previewCells.Count > 0) {
                _scene.PreviewEnvCells = previewCells;

                if (!_hoverCameraFocused && !snapped) {
                    var worldOrigin = previewCells[0].Position.Origin;
                    uint lbId = _document?.LandblockKey ?? 0;
                    float bx = ((lbId >> 8) & 0xFF) * 192f;
                    float by = (lbId & 0xFF) * 192f;
                    var worldPos = new Vector3(bx + worldOrigin.X, by + worldOrigin.Y, worldOrigin.Z - 50f);
                    _scene.Camera.SetPosition(worldPos + new Vector3(0, -30f, 15f));
                    _scene.Camera.LookAt(worldPos);
                    _hoverCameraFocused = true;
                }
            }
        }

        /// <summary>
        /// For hover preview: try to snap the prefab to the first compatible open portal
        /// so the user sees exactly how it would look connected. Falls back to an offset
        /// position next to the dungeon if no compatible portal is found.
        /// </summary>
        private (Vector3 origin, Quaternion rotation, bool snapped) ComputeHoverPreviewTransform(DungeonPrefab prefab) {
            if (_document == null || _document.Cells.Count == 0 || _dats == null)
                return (Vector3.Zero, Quaternion.Identity, false);

            if (EditingContext.PortalIndex != null && prefab.OpenFaces.Count > 0) {
                var openPortals = _openPortalCache.GetOrRebuild(_document, _dats, _loadedLandblockKey);
                foreach (var p in openPortals) {
                    var dc = p.Cell;
                    var cs = p.CellStruct;
                    var pid = p.PolyId;

                    foreach (var of in prefab.OpenFaces) {
                        var match = EditingContext.PortalIndex.FindMatch(
                            dc.EnvironmentId, dc.CellStructure, pid,
                            of.EnvId, of.CellStruct);
                        if (match == null) continue;

                        var snapOrigin = dc.Origin + Vector3.Transform(match.RelOffset, dc.Orientation);
                        var snapRot = Quaternion.Normalize(dc.Orientation * match.RelRot);
                        return (snapOrigin, snapRot, true);
                    }

                    // Geometric fallback for the first open face
                    var targetGeom = PortalSnapper.GetPortalGeometry(cs, pid);
                    if (targetGeom == null) continue;
                    var (centroid, normal) = PortalSnapper.TransformPortalToWorld(
                        targetGeom.Value, dc.Origin, dc.Orientation);

                    var firstFace = prefab.OpenFaces[0];
                    uint pfEnvFileId = (uint)(firstFace.EnvId | 0x0D000000);
                    if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(pfEnvFileId, out var pfEnv)) continue;
                    if (!pfEnv.Cells.TryGetValue(firstFace.CellStruct, out var pfCS)) continue;

                    var srcGeom = PortalSnapper.GetPortalGeometry(pfCS, firstFace.PolyId);
                    if (srcGeom == null) continue;

                    if (EditingContext.GeometryCache != null &&
                        !EditingContext.GeometryCache.AreCompatible(
                            dc.EnvironmentId, dc.CellStructure, pid,
                            firstFace.EnvId, firstFace.CellStruct, firstFace.PolyId))
                        continue;

                    var (geoOrigin, geoRot) = PortalSnapper.ComputeSnapTransform(centroid, normal, srcGeom.Value);
                    return (geoOrigin, geoRot, true);
                }
            }

            float avgX = _document.Cells.Average(c => c.Origin.X);
            float avgY = _document.Cells.Average(c => c.Origin.Y);
            float avgZ = _document.Cells.Average(c => c.Origin.Z);
            return (new Vector3(avgX + 30f, avgY, avgZ), Quaternion.Identity, false);
        }

        private void OnPrefabSelected(object? sender, DungeonPrefab prefab) {
            if (_document == null) EnsureDocument();
            HasDungeon = true;
            EditingContext.Document = _document;

            var roomTool = Tools.OfType<RoomPlacementTool>().FirstOrDefault();
            if (roomTool != null) {
                roomTool.SetPrefab(prefab);
                if (SelectedTool != roomTool) SelectTool(roomTool);
                var name = !string.IsNullOrEmpty(prefab.DisplayName) ? prefab.DisplayName : $"Prefab ({prefab.Cells.Count} cells)";
                PlacementStatusText = $"Placing: {name}";
                IsPlacementMode = true;
                if (_scene != null) _scene.IsInPlacementMode = true;
            }
        }

        [RelayCommand]
        private void SaveSelectionAsPrefab() {
            if (Selection.SelectedCells.Count == 0 || _document == null || RoomPalette == null) {
                StatusText = "Select one or more rooms first, then save as favorite piece";
                return;
            }

            var prefab = CellEditing.ExtractSelectionAsPrefab();
            if (prefab == null) {
                StatusText = "Could not extract selection as piece";
                return;
            }

            RoomPalette.AddCustomPrefab(prefab);
            StatusText = $"Saved \"{prefab.DisplayName}\" to favorites ({prefab.Cells.Count} cell{(prefab.Cells.Count != 1 ? "s" : "")}, {prefab.OpenFaces.Count} open door{(prefab.OpenFaces.Count != 1 ? "s" : "")})";
        }

        [RelayCommand]
        private void FavoriteSelectedRoom() {
            if (Selection.SelectedCell == null || _document == null || RoomPalette == null) return;
            var cellNum = (ushort)(Selection.SelectedCell.CellId & 0xFFFF);
            var dc = _document.GetCell(cellNum);
            if (dc == null) return;

            uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
            var room = RoomPalette.GetAllRooms().FirstOrDefault(r =>
                r.EnvironmentFileId == envFileId && r.CellStructureIndex == dc.CellStructure);
            if (room != null) {
                RoomPalette.ToggleFavorite(room);
                StatusText = room.IsFavorite
                    ? $"Added to favorites: Env 0x{dc.EnvironmentId:X4} / #{dc.CellStructure}"
                    : $"Removed from favorites: Env 0x{dc.EnvironmentId:X4} / #{dc.CellStructure}";
            }
            else {
                StatusText = $"Room type not found in palette";
            }
        }

        /// <summary>Favorite all individual small pieces (corridors, chambers, etc.) that use the same room geometries as this dungeon.</summary>
        [RelayCommand]
        private void FavoriteDungeonPieces() {
            if (_document == null || _document.Cells.Count == 0 || RoomPalette == null) {
                StatusText = "Open a dungeon first, then favorite it";
                return;
            }

            var roomTypes = new HashSet<(ushort envId, ushort cs)>();
            foreach (var cell in _document.Cells)
                roomTypes.Add((cell.EnvironmentId, cell.CellStructure));

            var kb = DungeonKnowledgeBuilder.LoadCached();
            if (kb == null || kb.Prefabs.Count == 0) {
                StatusText = "Knowledge base not ready — try again after it finishes building";
                return;
            }

            var matchingSigs = kb.Prefabs
                .Where(pf => pf.Category != "Full Dungeon" &&
                             pf.Cells.Any(c => roomTypes.Contains((c.EnvId, c.CellStruct))))
                .Select(pf => pf.Signature);

            int added = RoomPalette.AddPrefabFavorites(matchingSigs);

            StatusText = added > 0
                ? $"Favorited {added} individual pieces ({roomTypes.Count} room types) — use Generate from Favorites"
                : $"All matching pieces already favorited ({roomTypes.Count} room types)";
            Console.WriteLine($"[Dungeon] FavoriteDungeonPieces: LB {_loadedLandblockKey:X4}, {roomTypes.Count} room types, {added} new favorites");
        }

        [RelayCommand]
        private void ClearAllFavorites() {
            if (RoomPalette == null) return;
            int cleared = RoomPalette.ClearAllPrefabFavorites();
            StatusText = cleared > 0
                ? $"Cleared {cleared} favorites and custom prefabs"
                : "No favorites to clear";
        }

        /// <summary>Save the entire current dungeon as a single custom prefab and favorite it.</summary>
        [RelayCommand]
        private void FavoriteDungeonWhole() {
            if (_document == null || _document.Cells.Count == 0 || RoomPalette == null) {
                StatusText = "Open a dungeon first, then favorite it";
                return;
            }

            var prefab = CellEditing.ExtractDocumentAsPrefab();
            if (prefab == null) {
                StatusText = "Could not extract dungeon as a piece";
                return;
            }

            RoomPalette.AddCustomPrefab(prefab);
            StatusText = $"Saved whole dungeon as \"{prefab.DisplayName}\" ({prefab.Cells.Count} cells) — added to favorites";
            Console.WriteLine($"[Dungeon] FavoriteDungeonWhole: LB {_loadedLandblockKey:X4}, {prefab.Cells.Count} cells, sig={prefab.Signature[..Math.Min(30, prefab.Signature.Length)]}...");
        }

        [RelayCommand]
        private void ComputeVisibility() {
            if (_document == null || _document.Cells.Count == 0) {
                StatusText = "No rooms to compute visibility for.  Add rooms first.";
                return;
            }
            int updated = _document.ComputeVisibleCells();
            StatusText = $"Computed visibility for {updated}/{_document.Cells.Count} rooms";
            Console.WriteLine($"[Dungeon] ComputeVisibility: updated {updated}/{_document.Cells.Count} cells");
        }

        [RelayCommand]
        private void ValidateDungeon() {
            if (_document == null || _document.Cells.Count == 0) {
                StatusText = "No dungeon to validate";
                return;
            }
            var results = _document.ValidateComprehensive();
            ShowValidationDialog(results);
        }

        private void ShowValidationDialog(List<DungeonDocument.ValidationResult> results) {
            var errors = results.Count(r => r.Severity == DungeonDocument.ValidationSeverity.Error);
            var warnings = results.Count(r => r.Severity == DungeonDocument.ValidationSeverity.Warning);
            StatusText = errors > 0 ? $"Validation: {errors} error(s), {warnings} warning(s)"
                : warnings > 0 ? $"Validation: {warnings} warning(s)"
                : "Validation: All clear";

            Dialogs.ShowValidationDialog(results, SelectCellByNumber,
                autoFixPortals: () => {
                    if (_document == null) return;
                    int fixed_ = _document.AutoFixPortals();
                    RefreshRendering();
                    StatusText = $"Auto-fixed {fixed_} one-way portal(s)";
                },
                computeVisibility: () => ComputeVisibility());
        }

        private void SelectCellByNumber(ushort cellNum) {
            if (_scene?.EnvCellManager == null || _document == null) return;
            var lbKey = _document.LandblockKey;
            var cells = _scene.EnvCellManager.GetLoadedCellsForLandblock(lbKey);
            if (cells == null) return;
            var loaded = cells.FirstOrDefault(c => (c.CellId & 0xFFFF) == cellNum);
            if (loaded != null) {
                Selection.SelectCell(loaded);
            }
        }

        public void SelectCellByNumberPublic(ushort cellNum) => SelectCellByNumber(cellNum);
        public DungeonDocument? GetCurrentDocument() => _document;
        public ushort? GetSelectedCellNumber() => Selection.SelectedCell != null ? (ushort)(Selection.SelectedCell.CellId & 0xFFFF) : null;

        public event EventHandler? DungeonChanged;

        private void NotifyDungeonChanged() {
            DungeonChanged?.Invoke(this, EventArgs.Empty);
            RefreshGraphView();
        }

        private void RefreshGraphView() {
            _graphPanel?.RequestRefresh(_document, Selection?.SelectedCell != null
                ? (ushort)(Selection.SelectedCell.CellId & 0xFFFF)
                : null);
        }

        [RelayCommand]
        private async Task AnalyzeRooms() {
            if (_dats == null) {
                StatusText = "No DAT loaded";
                return;
            }
            StatusText = "Analyzing dungeon rooms + building knowledge base...";
            Console.WriteLine("[Dungeon] AnalyzeRooms: starting...");
            try {
                var report = await Task.Run(() => DungeonRoomAnalyzer.Run(_dats));
                Console.WriteLine($"[Dungeon] AnalyzeRooms: done - {report.TotalLandblocksScanned} landblocks, {report.TotalCellsScanned} cells, {report.UniqueRoomTypes} room types");
                var outDir = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "ACME WorldBuilder");
                var outPath = Path.Combine(outDir, "dungeon_room_analysis");
                DungeonRoomAnalyzer.SaveReport(report, outPath);

                StatusText = "Building dungeon knowledge base (adjacency + prefabs)...";
                var kb = DungeonKnowledgeBuilder.LoadCached();
                if (kb == null || kb.Edges.Count == 0) {
                    kb = await Task.Run(() => DungeonKnowledgeBuilder.Build(_dats));
                }
                Console.WriteLine($"[Dungeon] Knowledge base: {kb.TotalEdges} edges, {kb.TotalPrefabs} prefabs");

                StatusText = $"Analysis complete: {report.UniqueRoomTypes} room types, {kb.TotalEdges} adjacency edges, {kb.TotalPrefabs} prefabs";
                RoomPalette?.ReloadStarterPresets();
                ShowAnalysisResultDialog(report, outPath);
            }
            catch (Exception ex) {
                StatusText = $"Analysis failed: {ex.Message}";
                Console.WriteLine($"[Dungeon] AnalyzeRooms error: {ex}");
                ShowErrorDialog("Analysis Failed", ex.Message);
            }
        }

        [RelayCommand]
        private async Task GenerateDungeon() {
            if (_dats == null) {
                StatusText = "No DAT loaded";
                return;
            }

            var kb = DungeonKnowledgeBuilder.LoadCached();
            if (kb == null || kb.Edges.Count == 0) {
                if (RoomPalette?.IsBuildingKnowledgeBase == true) {
                    StatusText = "Knowledge base is rebuilding — please wait for it to finish, then try again";
                }
                else {
                    StatusText = "Knowledge base needs rebuilding — restarting the editor will trigger an automatic rebuild";
                }
                return;
            }

            var result = await ShowGenerateDialog(kb);
            if (result == null) return;

            var modeLabel = result.UseFavoritesOnly ? "favorites-only" : result.Style;
            StatusText = $"Generating {result.RoomCount}-size {modeLabel} dungeon...";
            try {
                ushort lbKey = _loadedLandblockKey != 0 ? _loadedLandblockKey : (ushort)0xFFFF;
                if (_document == null) {
                    _loadedLandblockKey = lbKey;
                    _document = GetOrCreateDungeonDoc(lbKey);
                    HasDungeon = true;
                }

                var allRooms = RoomPalette?.GetAllRooms() ?? new List<RoomEntry>();
                var generated = await Task.Run(() => DungeonGenerator.Generate(result, allRooms, _dats, lbKey));

                if (generated == null || generated.Cells.Count == 0) {
                    StatusText = "Generation failed — not enough adjacency data for this style";
                    return;
                }

                CommandHistory.Clear();
                if (_document != null) {
                    _document.CopyFrom(generated);
                }

                RefreshRendering();
                _needsCameraFocus = true;
                CellCount = _document?.Cells.Count ?? 0;
                int openPortals = CountOpenPortals();

                // Validate the generated dungeon and show a summary
                var validationResults = _document?.ValidateComprehensive();
                int valErrors = validationResults?.Count(r => r.Severity == DungeonDocument.ValidationSeverity.Error) ?? 0;
                int valWarnings = validationResults?.Count(r => r.Severity == DungeonDocument.ValidationSeverity.Warning) ?? 0;

                var branchLabels = new[] { "linear", "moderate", "heavy" };
                var sizeLabels = new[] { "small rooms", "mixed", "large rooms" };
                var opts = new List<string>();
                if (result.UseFavoritesOnly) opts.Add("favorites");
                if (!result.AllowVertical) opts.Add("no vertical");
                if (result.LockStyle) opts.Add("style-locked");
                opts.Add(branchLabels[Math.Clamp(result.Branching, 0, 2)]);
                opts.Add(sizeLabels[Math.Clamp(result.RoomSize, 0, 2)]);
                var optsStr = opts.Count > 0 ? $" [{string.Join(", ", opts)}]" : "";
                var valStr = valErrors > 0 ? $"  ({valErrors} errors, {valWarnings} warnings — run Validate)"
                           : valWarnings > 0 ? $"  ({valWarnings} warnings)"
                           : "";
                StatusText = $"Generated size {CellCount} ({openPortals} open doorways){optsStr}.{valStr}";
                Console.WriteLine($"[Dungeon] Generated: {CellCount} cells, {result.RoomCount} target, {openPortals} open portals, style={result.Style}, favorites={result.UseFavoritesOnly}, seed={result.Seed}");
            }
            catch (Exception ex) {
                StatusText = $"Generation failed: {ex.Message}";
                Console.WriteLine($"[Dungeon] GenerateDungeon error: {ex}");
            }
        }

        private Task<GeneratorParams?> ShowGenerateDialog(DungeonKnowledgeBase kb) {
            var favSigs = RoomPalette?.GetFavoritePrefabSignatures();
            var customPrefabs = RoomPalette?.GetCustomPrefabs();
            return Dialogs.ShowGenerateDialog(kb, _dats, _textureImport, favSigs, customPrefabs);
        }

        private void ShowErrorDialog(string title, string message) =>
            Dialogs.ShowErrorDialog(title, message);

        private void ShowAnalysisResultDialog(DungeonRoomAnalyzer.AnalysisReport report, string outPath) =>
            Dialogs.ShowAnalysisResultDialog(report, outPath);

        [RelayCommand]
        private void CancelPlacement() {
            _pendingRoom = null;
            IsPlacementMode = false;
            PlacementStatusText = "";
            if (_scene != null) {
                _scene.RoomPlacementPreview = null;
                _scene.IsInPlacementMode = false;
                _scene.ClearPreview();
                _scene.HighlightedPortalCellNum = 0;
                _scene.HighlightedPortalPolyId = 0;
            }
        }

        /// <summary>
        /// After placing a room, stay in placement mode so the user can keep building.
        /// Clears the pending room but keeps placement mode active for the next catalog selection.
        /// </summary>
        private void StayInPlacementMode() {
            if (_scene != null) _scene.RoomPlacementPreview = null;
            _pendingRoom = null;
            IsPlacementMode = false;

            int openPortalCount = CountOpenPortals();
            PlacementStatusText = openPortalCount > 0
                ? $"Room placed! {openPortalCount} open doorway{(openPortalCount != 1 ? "s" : "")} — pick another room from the catalog"
                : "Room placed! No open doorways — disconnect one to continue building";
        }

        private int CountOpenPortals() {
            if (_document == null || _dats == null) return 0;
            return _openPortalCache.Count(_document, _dats, _loadedLandblockKey);
        }

        [RelayCommand]
        private async Task NewDungeon() {
            if (_dats == null) return;

            var cellId = await ShowNewDungeonDialog();
            if (cellId == null) return;

            var lbKey = (ushort)(cellId.Value >> 16);
            if (lbKey == 0) lbKey = (ushort)(cellId.Value & 0xFFFF);

            CommandHistory.Clear();
            _loadedLandblockKey = lbKey;
            _document = GetOrCreateDungeonDoc(lbKey);
            RefreshInstancePlacementList();
            HasDungeon = true;
            CellCount = 0;
            StatusText = $"New dungeon — pick a room from the catalog to start building";

            // Position camera at the landblock's world-space position (where cells will render)
            var blockX = (lbKey >> 8) & 0xFF;
            var blockY = lbKey & 0xFF;
            var lbCenter = new Vector3(blockX * 192f, blockY * 192f, -50f);
            _scene?.Camera.SetPosition(lbCenter + new Vector3(0, -30f, 20f));
            _scene?.Camera.LookAt(lbCenter);
        }

        private void EnsureDocument() {
            if (_document != null) return;
            _loadedLandblockKey = 0xAAAA;
            _document = GetOrCreateDungeonDoc(_loadedLandblockKey);
            RefreshInstancePlacementList();
            HasDungeon = true;
            EditingContext.Document = _document;
        }

        private DungeonDocument? GetOrCreateDungeonDoc(ushort lbKey) {
            if (_project == null) return null;
            var docId = $"dungeon_{lbKey:X4}";
            var doc = _project.DocumentManager.GetOrCreateDocumentAsync<DungeonDocument>(docId).GetAwaiter().GetResult();
            if (doc != null) {
                doc.SetLandblockKey(lbKey);
            }
            return doc;
        }

        private void PlaceFirstCell() {
            if (_pendingRoom == null || _document == null || _scene == null || _dats == null) return;

            var surfaces = GetSurfacesForRoom(_pendingRoom);
            Console.WriteLine($"[Dungeon] PlaceFirstCell: Room Env=0x{_pendingRoom.EnvironmentFileId:X8} (id={_pendingRoom.EnvironmentId:X4}), CellStruct={_pendingRoom.CellStructureIndex}, " +
                $"Surfaces=[{string.Join(",", surfaces.Select(s => $"0x{s:X4}"))}] ({surfaces.Count} slots), " +
                $"Portals={_pendingRoom.PortalCount}, Verts={_pendingRoom.VertexCount}, Polys={_pendingRoom.PolygonCount}");

            var cmd = new AddCellCommand(
                _pendingRoom.EnvironmentId, _pendingRoom.CellStructureIndex,
                Vector3.Zero, Quaternion.Identity, surfaces);
            CommandHistory.Execute(cmd, _document);

            RefreshRendering();
            _needsCameraFocus = true;

            StatusText = $"{_document.Cells.Count} rooms — select next room from catalog";
            CellCount = _document.Cells.Count;

            StayInPlacementMode();
        }

        /// <summary>
        /// Computes where the room would be placed for preview. Returns (Origin, Orientation) or null if no valid placement.
        /// Uses FindNearestOpenPortalCell so the ghost shows even when hovering empty space near the dungeon.
        /// </summary>
        private (Vector3 Origin, Quaternion Orientation)? TryComputeRoomPlacementPreview(
            Vector3 rayOrigin, Vector3 rayDir, EnvCellManager.EnvCellRaycastHit hit) {

            if (_pendingRoom == null || _document == null || _dats == null) return null;

            if (_document.Cells.Count == 0) {
                return (Vector3.Zero, Quaternion.Identity);
            }

            // Use the same smart search as TrySnapToPortal: find nearest open portal to the ray
            var bestCell = FindNearestOpenPortalCell(rayOrigin, rayDir);
            if (bestCell == null) return null;

            var (targetDocCell, targetCellStruct, openPortalId, _) = bestCell.Value;

            var targetPortalLocal = PortalSnapper.GetPortalGeometry(targetCellStruct, openPortalId);
            if (targetPortalLocal == null) return null;

            var (targetCentroidWorld, targetNormalWorld) = PortalSnapper.TransformPortalToWorld(
                targetPortalLocal.Value, targetDocCell.Origin, targetDocCell.Orientation);

            uint sourceEnvFileId = _pendingRoom.EnvironmentFileId;
            if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(sourceEnvFileId, out var sourceEnv)) return null;
            if (!sourceEnv.Cells.TryGetValue(_pendingRoom.CellStructureIndex, out var sourceCellStruct)) return null;

            var sourcePortalId = PortalSnapper.PickBestSourcePortal(sourceCellStruct, targetNormalWorld);
            if (sourcePortalId == null) return null;

            var sourcePortalLocal = PortalSnapper.GetPortalGeometry(sourceCellStruct, sourcePortalId.Value);
            if (sourcePortalLocal == null) return null;

            var (newOrigin, newOrientation) = PortalSnapper.ComputeSnapTransform(
                targetCentroidWorld, targetNormalWorld, sourcePortalLocal.Value);
            return (newOrigin, newOrientation);
        }

        private void TrySnapToPortal(Vector3 rayOrigin, Vector3 rayDir) {
            if (_pendingRoom == null || _document == null || _scene == null || _dats == null) return;

            // Find nearest cell with an open portal (smart search, not just raycast hit)
            var bestCell = FindNearestOpenPortalCell(rayOrigin, rayDir);
            if (bestCell == null) {
                PlacementStatusText = "No open doorways — select a room and disconnect a doorway to create one";
                Console.WriteLine("[Dungeon] TrySnapToPortal: no cells with open portals found");
                return;
            }

            var (targetDocCell, targetCellStruct, openPortalId, targetCellNum) = bestCell.Value;
            Console.WriteLine($"[Dungeon] TrySnapToPortal: found cell 0x{targetCellNum:X4} with open portal 0x{openPortalId:X4}");

            var targetPortalLocal = PortalSnapper.GetPortalGeometry(targetCellStruct, openPortalId);
            if (targetPortalLocal == null) return;

            var (targetCentroidWorld, targetNormalWorld) = PortalSnapper.TransformPortalToWorld(
                targetPortalLocal.Value, targetDocCell.Origin, targetDocCell.Orientation);

            uint sourceEnvFileId = (uint)(_pendingRoom.EnvironmentId | 0x0D000000);
            if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(sourceEnvFileId, out var sourceEnv)) return;
            if (!sourceEnv.Cells.TryGetValue(_pendingRoom.CellStructureIndex, out var sourceCellStruct)) return;

            var sourcePortalId = PortalSnapper.PickBestSourcePortal(sourceCellStruct, targetNormalWorld);
            if (sourcePortalId == null) {
                PlacementStatusText = "Selected room has no matching portal face";
                return;
            }

            var sourcePortalLocal = PortalSnapper.GetPortalGeometry(sourceCellStruct, sourcePortalId.Value);
            if (sourcePortalLocal == null) return;

            var (newOrigin, newOrientation) = PortalSnapper.ComputeSnapTransform(
                targetCentroidWorld, targetNormalWorld, sourcePortalLocal.Value);

            var surfaces = GetSurfacesForRoom(_pendingRoom);
            var cmd = new AddCellCommand(
                _pendingRoom.EnvironmentId, _pendingRoom.CellStructureIndex,
                newOrigin, newOrientation, surfaces,
                connectToCellNum: targetCellNum, connectToPolyId: openPortalId, sourcePolyId: sourcePortalId.Value);
            CommandHistory.Execute(cmd, _document);

            RefreshRendering();

            CellCount = _document.Cells.Count;
            StatusText = $"{CellCount} rooms — select next room or click to place again";

            StayInPlacementMode();
        }

        private (DungeonCellData cell, DatReaderWriter.Types.CellStruct cellStruct, ushort portalId, ushort cellNum)?
            FindNearestOpenPortalCell(Vector3 rayOrigin, Vector3 rayDir) {

            if (_document == null || _dats == null) return null;

            var hit = _scene?.EnvCellManager?.Raycast(rayOrigin, rayDir);

            var portals = _openPortalCache.GetOrRebuild(_document, _dats, _loadedLandblockKey);
            if (portals.Count == 0) return null;

            if (hit != null && hit.Value.Hit) {
                var nearest = _openPortalCache.FindNearestToPosition(hit.Value.HitPosition, _document, _dats, _loadedLandblockKey);
                if (nearest != null)
                    return (nearest.Value.Cell, nearest.Value.CellStruct, nearest.Value.PolyId, nearest.Value.CellNum);
            }

            var byRay = _openPortalCache.FindNearestToRay(rayOrigin, rayDir, _document, _dats, _loadedLandblockKey);
            if (byRay == null) return null;
            return (byRay.Value.Cell, byRay.Value.CellStruct, byRay.Value.PolyId, byRay.Value.CellNum);
        }

        private List<ushort> GetSurfacesForRoom(RoomEntry room) {
            if (room.DefaultSurfaces.Count > 0)
                return new List<ushort>(room.DefaultSurfaces);

            if (_dats == null) return new List<ushort>();

            // Search the DAT for an existing EnvCell that uses this Environment+CellStruct
            // and copy its surface list. This gives us the correct number of surfaces with
            // valid IDs for this room shape.
            var surfaces = FindDefaultSurfacesFromDat(room.EnvironmentId, room.CellStructureIndex);
            if (surfaces.Count > 0) {
                room.DefaultSurfaces = surfaces;
                return new List<ushort>(surfaces);
            }

            // Fallback: determine how many surface slots are needed from the CellStruct's polygons,
            // and fill with a generic stone surface
            var slotCount = CountRequiredSurfaceSlots(room);
            if (slotCount > 0) {
                var fallback = new List<ushort>();
                for (int i = 0; i < slotCount; i++)
                    fallback.Add(0x032A); // generic dungeon stone surface
                room.DefaultSurfaces = fallback;
                return fallback;
            }

            return new List<ushort>();
        }

        private List<ushort> FindDefaultSurfacesFromDat(ushort environmentId, ushort cellStructureIndex) {
            if (_dats == null) return new List<ushort>();
            return DungeonDocumentOperations.FindDefaultSurfacesFromDat(_dats, environmentId, cellStructureIndex);
        }

        private int CountRequiredSurfaceSlots(RoomEntry room) {
            if (_dats == null) return 0;
            try {
                uint envFileId = (uint)(room.EnvironmentId | 0x0D000000);
                if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env)) return 0;
                if (!env.Cells.TryGetValue(room.CellStructureIndex, out var cellStruct)) return 0;

                var portalIds = cellStruct.Portals != null ? new HashSet<ushort>(cellStruct.Portals) : new HashSet<ushort>();
                int maxIndex = -1;
                foreach (var kvp in cellStruct.Polygons) {
                    if (portalIds.Contains(kvp.Key)) continue;
                    if (kvp.Value.PosSurface > maxIndex) maxIndex = kvp.Value.PosSurface;
                }
                return maxIndex + 1;
            }
            catch { return 0; }
        }

        private void RefreshRendering() {
            if (_scene == null || _document == null) return;
            _connectionLinesDirty = true;
            _openPortalCache.Invalidate();
            _scene.InvalidateGrid();
            _scene.RefreshFromDocument(_document);
            RefreshOpenPortalIndicators();
            UpdatePaletteCompatibility();
            NotifyDungeonChanged();
        }

        private void UpdatePaletteCompatibility() {
            if (_document == null || _dats == null || RoomPalette == null) return;
            var openPortals = _openPortalCache.GetOpenPortalKeys(_document, _dats, _loadedLandblockKey);
            RoomPalette.SetActiveOpenPortals(openPortals);

            if (openPortals.Count > 0 && !RoomPalette.ShowCompatibleOnly)
                RoomPalette.ShowCompatibleOnly = true;
        }

        private void RefreshOpenPortalIndicators() {
            if (_scene == null || _document == null || _dats == null) {
                if (_scene != null) {
                    _scene.OpenPortalIndicators.Clear();
                    _scene.ConnectedPortalIndicators.Clear();
                }
                return;
            }

            var openIndicators = new List<OpenPortalIndicator>();
            var connectedIndicators = new List<OpenPortalIndicator>();

            // Rebuild the cache so open portal indicators and all other consumers share one pass
            _openPortalCache.Rebuild(_document, _dats, _loadedLandblockKey);

            // Open portals come directly from the cache
            foreach (var p in _openPortalCache.OpenPortals) {
                openIndicators.Add(new OpenPortalIndicator {
                    WorldVertices = p.WorldVertices,
                    Centroid = p.WorldCentroid,
                    Normal = p.WorldNormal,
                    CellNum = p.CellNum,
                    PolyId = p.PolyId
                });
            }

            // Connected portals still need a separate pass (they are excluded from the open cache)
            uint lbId = _document.LandblockKey;
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);
            const float dungeonZBump = -50f;

            foreach (var dc in _document.Cells) {
                uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
                if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(dc.CellStructure, out var cs)) continue;

                var connected = new HashSet<ushort>(dc.CellPortals.Select(cp => cp.PolygonId));
                if (connected.Count == 0) continue;

                var cellOrigin = dc.Origin + lbOffset + new Vector3(0, 0, dungeonZBump);
                var cellRot = dc.Orientation;
                var cellTransform = Matrix4x4.CreateFromQuaternion(cellRot) * Matrix4x4.CreateTranslation(cellOrigin);

                foreach (var pid in connected) {
                    if (!cs.Polygons.TryGetValue(pid, out var poly)) continue;
                    if (poly.VertexIds.Count < 3) continue;

                    var worldVerts = new List<Vector3>();
                    foreach (var vid in poly.VertexIds) {
                        if (cs.VertexArray.Vertices.TryGetValue((ushort)vid, out var vtx))
                            worldVerts.Add(Vector3.Transform(vtx.Origin, cellTransform));
                    }
                    if (worldVerts.Count < 3) continue;

                    var centroid = Vector3.Zero;
                    foreach (var v in worldVerts) centroid += v;
                    centroid /= worldVerts.Count;

                    var geom = PortalSnapper.GetPortalGeometry(cs, pid);
                    var normal = geom != null
                        ? Vector3.Normalize(Vector3.Transform(geom.Value.Normal, cellRot))
                        : Vector3.UnitZ;

                    connectedIndicators.Add(new OpenPortalIndicator {
                        WorldVertices = worldVerts.ToArray(),
                        Centroid = centroid,
                        Normal = normal,
                        CellNum = dc.CellNumber,
                        PolyId = pid
                    });
                }
            }

            _scene.OpenPortalIndicators = openIndicators;
            _scene.ConnectedPortalIndicators = connectedIndicators;
        }

        #endregion

        #region Save

        [RelayCommand]
        private void SaveDungeon() {
            if (_document == null) {
                StatusText = "No dungeon to save";
                return;
            }

            var warnings = _document.Validate();
            foreach (var w in warnings) {
                Console.WriteLine($"[DungeonSave] Warning: {w}");
            }

            _document.ForceSave();
            StatusText = $"Saved dungeon ({_document.Cells.Count} rooms) to project. Use File > Export to write DATs.";
            Console.WriteLine($"[DungeonSave] Saved to project: LB {_document.LandblockKey:X4}, {_document.Cells.Count} cells");
        }

        #endregion

        private Task<uint?> ShowNewDungeonDialog() =>
            Dialogs.ShowNewDungeonDialog();

        [RelayCommand]
        private async Task StartFromTemplate() {
            if (_dats == null || _project == null) return;

            if (!WorldBuilder.Lib.LocationDatabase.Dungeons.Any()) {
                StatusText = "No dungeon templates found";
                return;
            }

            await Dialogs.ShowStartFromTemplateDialog(CopyTemplateToLandblock);
        }

        /// <summary>
        /// Copy a dungeon from source landblock to target landblock.
        /// Creates a new document at target with same structure, positions, and portal connectivity.
        /// </summary>
        public void CopyDungeonTemplate(ushort sourceLb, ushort targetLb) {
            CopyTemplateToLandblock(sourceLb, targetLb);
        }

        /// <summary>
        /// Applies alternate wall and floor surfaces to all cells in the current document (for demo/promo).
        /// Slot 0 = wall, slot 1 = floor. Uses default stone surfaces if not provided.
        /// </summary>
        public void ApplyAlternateSurfacesToAllCells(ushort? wallSurfaceId = null, ushort? floorSurfaceId = null) {
            if (_document == null || _document.Cells.Count == 0) return;
            ushort wall = wallSurfaceId ?? 0x032A;
            ushort floor = floorSurfaceId ?? 0x032B;
            int updated = 0;
            foreach (var dc in _document.Cells) {
                if (dc.Surfaces.Count >= 1) { dc.Surfaces[0] = wall; updated++; }
                if (dc.Surfaces.Count >= 2) { dc.Surfaces[1] = floor; updated++; }
            }
            _document.MarkDirty();
            RefreshRendering();
            StatusText = $"Applied textures to {updated} room(s)";
        }

        /// <summary>
        /// Copy a dungeon from source landblock to target landblock.
        /// Creates a new document at target with same structure, positions, and portal connectivity.
        /// </summary>
        private void CopyTemplateToLandblock(ushort sourceLb, ushort targetLb) {
            if (_scene == null || _project == null || _dats == null) return;

            CommandHistory.Clear();
            var sourceDoc = GetOrCreateDungeonDoc(sourceLb);
            if (sourceDoc == null) {
                StatusText = $"LB {sourceLb:X4}: No dungeon cells to copy";
                return;
            }
            // Force fresh load from DAT to ensure we have raw DAT Z values,
            // not stale world-Z from an old project save.
            sourceDoc.ReloadFromDat(_dats);
            if (sourceDoc.Cells.Count == 0) {
                StatusText = $"LB {sourceLb:X4}: No dungeon cells to copy";
                return;
            }

            var targetDoc = GetOrCreateDungeonDoc(targetLb);
            if (targetDoc == null) {
                StatusText = $"LB {targetLb:X4}: Failed to create document";
                return;
            }

            // Check if the target landblock already has cells (building interiors, etc.)
            // Start dungeon cells after any existing ones to avoid overwriting them.
            ushort startCell = 0x0100;
            uint targetLbiId = ((uint)targetLb << 16) | 0xFFFE;
            if (_dats.TryGet<LandBlockInfo>(targetLbiId, out var targetLbi) && targetLbi.NumCells > 0) {
                startCell = (ushort)(0x0100 + targetLbi.NumCells);
                Console.WriteLine($"[Dungeon] Target LB {targetLb:X4} has {targetLbi.NumCells} existing cells " +
                    $"({targetLbi.Buildings?.Count ?? 0} buildings, {targetLbi.Objects?.Count ?? 0} objects). " +
                    $"Dungeon cells will start at 0x{startCell:X4}.");
            }

            targetDoc.CopyFrom(sourceDoc, startCell);
            _document = targetDoc;
            RefreshInstancePlacementList();
            _loadedLandblockKey = targetLb;
            _targetCellId = 0;

            _scene.RefreshFromDocument(_document);
            CellCount = _document.Cells.Count;
            StatusText = $"LB {targetLb:X4}: {CellCount} cells (copied from {sourceLb:X4})";
            HasDungeon = true;
            _needsCameraFocus = true;
        }

        [RelayCommand]
        public async Task OpenLandblock() {
            if (_dats == null) return;

            var cellId = await ShowOpenDungeonDialog();
            if (cellId == null) return;

            var fullId = cellId.Value;
            var lbId = (ushort)(fullId >> 16);
            var cellPart = (ushort)(fullId & 0xFFFF);

            // If only landblock was given (4-char hex), cellPart might be the landblock
            if (lbId == 0 && cellPart != 0) {
                lbId = cellPart;
                cellPart = 0;
            }

            LoadDungeon(lbId, cellPart >= 0x0100 ? cellPart : (ushort)0);
        }

        private ushort _targetCellId;

        public void LoadDungeon(ushort landblockKey, ushort targetCellId = 0) {
            if (_scene == null || _dats == null) return;

            CommandHistory.Clear();
            _document = GetOrCreateDungeonDoc(landblockKey);
            if (_document == null) {
                StatusText = $"LB {landblockKey:X4}: Failed to create document";
                HasDungeon = false;
                CellCount = 0;
                return;
            }

            _loadedLandblockKey = landblockKey;
            _targetCellId = targetCellId;

            // Diagnostic: dump what cells actually use
            if (_document.Cells.Count > 0) {
                Console.WriteLine($"[Dungeon] LoadDungeon 0x{landblockKey:X4}: {_document.Cells.Count} cells");
                foreach (var c in _document.Cells.Take(5)) {
                    Console.WriteLine($"  Cell 0x{c.CellNumber:X4}: Env=0x{c.EnvironmentId:X4} (file 0x{(c.EnvironmentId | 0x0D000000):X8}), " +
                        $"CellStruct={c.CellStructure}, Surfaces=[{string.Join(",", c.Surfaces.Select(s => $"0x{s:X4}"))}] ({c.Surfaces.Count} slots), " +
                        $"Portals={c.CellPortals.Count}, Statics={c.StaticObjects.Count}, " +
                        $"Pos=({c.Origin.X:F1},{c.Origin.Y:F1},{c.Origin.Z:F1})");
                }
                if (_document.Cells.Count > 5) Console.WriteLine($"  ... and {_document.Cells.Count - 5} more cells");
            }

            // If document is empty, try loading cells from DAT
            if (_document.Cells.Count == 0) {
                _document.ReloadFromDat(_dats);
            }

            if (_document.Cells.Count > 0) {
                RefreshRendering();
                CellCount = _document.Cells.Count;
                int openPortals = CountOpenPortals();
                int connectedPortals = _document.Cells.Sum(c => c.CellPortals.Count);
                StatusText = $"Dungeon loaded: {CellCount} rooms, {connectedPortals / 2} connections, {openPortals} open doorways";
                HasDungeon = true;
                _needsCameraFocus = true;
                RefreshInstancePlacementList();
                _ = TryLoadDbInstancesAsync(landblockKey);
            }
            else {
                StatusText = $"No dungeon rooms found for this landblock";
                HasDungeon = false;
                CellCount = 0;
            }
        }

        private Task<uint?> ShowOpenDungeonDialog() =>
            Dialogs.ShowOpenDungeonDialog(LandblockInputText, text => LandblockInputText = text);

        internal static uint? ParseLandblockInput(string? input) =>
            DungeonDialogService.ParseLandblockInput(input);

        public void Cleanup() {
            SaveDockingState();
            _scene?.Dispose();
            _scene = null;
        }
    }
}
