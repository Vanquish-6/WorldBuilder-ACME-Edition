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
using WorldBuilder.Editors.Landscape;

namespace WorldBuilder.Editors.Dungeon {
    public partial class DungeonEditorViewModel : ViewModelBase {
        [ObservableProperty] private string _statusText = "No dungeon loaded";
        [ObservableProperty] private string _landblockInputText = "";
        [ObservableProperty] private string _currentPositionText = "";
        [ObservableProperty] private string _selectedCellInfo = "";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowHoveredCellInfo))]
        private string _hoveredCellInfo = "";
        /// <summary>True when hovering over a cell and no cell is selected.</summary>
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
        [ObservableProperty] private string _cellPosX = "";
        [ObservableProperty] private string _cellPosY = "";
        [ObservableProperty] private string _cellPosZ = "";
        [ObservableProperty] private string _cellRotX = "";
        [ObservableProperty] private string _cellRotY = "";
        [ObservableProperty] private string _cellRotZ = "";
        [ObservableProperty] private ObservableCollection<CellSurfaceSlot> _surfaceSlots = new();
        [ObservableProperty] private int _selectedSurfaceSlot = -1;
        [ObservableProperty] private bool _isDraggingCell;

        [ObservableProperty] private bool _hasSelectedObject;
        [ObservableProperty] private string _selectedObjectInfo = "";
        [ObservableProperty] private string _objPosX = "";
        [ObservableProperty] private string _objPosY = "";
        [ObservableProperty] private string _objPosZ = "";
        [ObservableProperty] private string _objRotDegrees = "";
        [ObservableProperty] private bool _isDraggingObject;

        [ObservableProperty] private bool _showConnectionLines = true;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectedCellPanelTitle))]
        private int _selectedCellCount; // 0 = none, 1 = single, >1 = multi

        public string SelectedCellPanelTitle =>
            SelectedCellCount <= 1 ? "Cell Properties" : $"Cell Properties ({SelectedCellCount} selected)";

        /// <summary>Full cell ID in 0x01D90101 format for copy/paste into in-game commands.</summary>
        public string SelectedCellLocationHex =>
            _selectedCell != null ? $"0x{_selectedCell.CellId:X8}" : "";

        /// <summary>Full teleport line: cellId x y z qx qy qz qw (center of cell, char spawn).</summary>
        public string SelectedCellTeleportCommand =>
            _selectedCell != null ? ComputeCellTeleportLine(_selectedCell) : "";

        private ushort _selectedObjCellNum;
        private int _selectedObjIndex = -1;

        private LoadedEnvCell? _selectedCell; // Primary (first selected), used for position/rotation UI
        private readonly List<LoadedEnvCell> _selectedCells = new(); // Full multi-selection
        private List<DungeonCellData>? _cellClipboard;
        private bool _needsCameraFocus;
        private Vector3 _dragStartHit;
        private Vector3 _dragStartOrigin;

        private IReadOnlyList<(Vector3 From, Vector3 To)> _cachedConnectionLines = Array.Empty<(Vector3, Vector3)>();
        private bool _connectionLinesDirty = true;

        public WorldBuilderSettings Settings { get; }

        private Project? _project;
        private IDatReaderWriter? _dats;
        private DungeonScene? _scene;
        private DungeonDocument? _document;
        private ushort _loadedLandblockKey;

        public DungeonScene? Scene => _scene;
        public DungeonDocument? Document => _document;
        public RoomPaletteViewModel? RoomPalette { get; private set; }
        public DungeonObjectBrowserViewModel? ObjectBrowser { get; private set; }
        public SurfaceBrowserViewModel? SurfaceBrowser { get; private set; }
        public DungeonCommandHistory CommandHistory { get; } = new();

        private readonly TextureImportService? _textureImport;

        public DungeonEditorViewModel(WorldBuilderSettings settings, TextureImportService? textureImport = null) {
            Settings = settings;
            _textureImport = textureImport;
        }

        internal void Init(Project project) {
            _project = project;
            _dats = project.DocumentManager.Dats;
            _scene = new DungeonScene(_dats, Settings);

            RoomPalette = new RoomPaletteViewModel(_dats);
            RoomPalette.RoomSelected += OnRoomSelected;
            OnPropertyChanged(nameof(RoomPalette));

            ObjectBrowser = new DungeonObjectBrowserViewModel(_dats,
                () => _scene?.ThumbnailService);
            ObjectBrowser.PlacementRequested += OnObjectPlacementRequested;
            OnPropertyChanged(nameof(ObjectBrowser));

            SurfaceBrowser = new SurfaceBrowserViewModel(_dats, _textureImport);
            SurfaceBrowser.SurfaceSelected += OnSurfaceSelected;
            OnPropertyChanged(nameof(SurfaceBrowser));

            _ = RoomPalette.LoadRoomsAsync();
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
                _cachedConnectionLines = ComputeConnectionLines();
                _connectionLinesDirty = false;
            }
            _scene.ConnectionLines = _cachedConnectionLines;
            _scene.ShowConnectionLines = ShowConnectionLines;
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
            if (shiftHeld)
                camera.ProcessKeyboard(CameraMovement.Down, deltaTime);
        }

        internal void HandleKeyDown(KeyEventArgs e) {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

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
            if (e.Key == Key.Escape) {
                if (IsObjectPlacementMode) CancelObjectPlacement();
                else if (IsPlacementMode) CancelPlacement();
                else if (HasSelectedObject) DeselectObject();
                else DeselectCell();
            }
        }

        private void UndoRedoUndo() {
            if (_document == null || !CommandHistory.CanUndo) return;
            CommandHistory.Undo(_document);
            DeselectCell();
            RefreshRendering();
            CellCount = _document.Cells.Count;
            StatusText = $"LB {_document.LandblockKey:X4}: {CellCount} cells (Undo: {CommandHistory.LastCommandDescription ?? "none"})";
        }

        private void UndoRedoRedo() {
            if (_document == null || !CommandHistory.CanRedo) return;
            CommandHistory.Redo(_document);
            DeselectCell();
            RefreshRendering();
            CellCount = _document.Cells.Count;
            StatusText = $"LB {_document.LandblockKey:X4}: {CellCount} cells";
        }

        internal void HandlePointerWheel(PointerWheelEventArgs e) {
            _scene?.Camera.ProcessMouseScroll((float)e.Delta.Y);
        }

        internal void HandlePointerPressed(AvaloniaInputState inputState) {
            if (_scene?.EnvCellManager == null) return;

            var mouse = inputState.MouseState;
            if (!mouse.LeftPressed || mouse.RightPressed) return;

            // If in placement mode and dungeon is empty, place first cell at camera target
            if (IsPlacementMode && _pendingRoom != null) {
                Console.WriteLine($"[Dungeon] Click in placement mode: doc={(_document != null ? "yes" : "null")}, cells={_document?.Cells.Count ?? -1}, room={_pendingRoom.DisplayName}");
                if (_document != null && _document.Cells.Count == 0) {
                    PlaceFirstCell();
                    return;
                }
            }

            var camera = _scene.Camera;
            float width = camera.ScreenSize.X;
            float height = camera.ScreenSize.Y;
            if (width <= 0 || height <= 0) return;

            float ndcX = 2.0f * mouse.Position.X / width - 1.0f;
            float ndcY = 2.0f * mouse.Position.Y / height - 1.0f;

            Matrix4x4 projection = camera.GetProjectionMatrix();
            Matrix4x4 view = camera.GetViewMatrix();
            if (!Matrix4x4.Invert(view * projection, out Matrix4x4 vpInverse)) return;

            Vector4 nearW = Vector4.Transform(new Vector4(ndcX, ndcY, -1f, 1f), vpInverse);
            Vector4 farW = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), vpInverse);
            nearW /= nearW.W;
            farW /= farW.W;

            var rayOrigin = new Vector3(nearW.X, nearW.Y, nearW.Z);
            var rayDir = Vector3.Normalize(new Vector3(farW.X, farW.Y, farW.Z) - rayOrigin);

            // Object placement mode
            if (IsObjectPlacementMode && _pendingObjectId != null && _document != null) {
                TryPlaceObject(rayOrigin, rayDir);
                return;
            }

            // Room placement mode - snap to portal
            if (IsPlacementMode && _pendingRoom != null && _document != null) {
                TrySnapToPortal(rayOrigin, rayDir);
                return;
            }

            // Normal mode: try static object raycast first, then cell raycast
            if (!HasDungeon) return;

            // 1. Raycast against static objects (AABB)
            if (_document != null) {
                var objHit = DungeonObjectRaycast.Raycast(rayOrigin, rayDir, _document, _scene);
                if (objHit.Hit) {
                    var cell = _document.GetCell(objHit.CellNumber);
                    if (cell != null && objHit.ObjectIndex < cell.StaticObjects.Count) {
                        bool alreadySelected = HasSelectedObject &&
                            _selectedObjCellNum == objHit.CellNumber &&
                            _selectedObjIndex == objHit.ObjectIndex;

                        SelectObject(objHit.CellNumber, objHit.ObjectIndex,
                            cell.StaticObjects[objHit.ObjectIndex], objHit.HitPosition);

                        if (alreadySelected) {
                            IsDraggingObject = true;
                            _dragStartHit = objHit.HitPosition;
                            _dragStartOrigin = cell.StaticObjects[objHit.ObjectIndex].Origin;
                        }
                        return;
                    }
                }
            }

            // 2. Raycast against cell geometry
            var hit = _scene.EnvCellManager.Raycast(rayOrigin, rayDir);
            if (hit.Hit) {
                DeselectObject();
                bool ctrlAdd = inputState.Modifiers.HasFlag(KeyModifiers.Control);
                bool isAlreadySelected = _selectedCells.Any(c => c.CellId == hit.Cell.CellId);
                if (ctrlAdd) {
                    ToggleCellInSelection(hit.Cell);
                }
                else {
                    SelectCell(hit.Cell);
                }
                if (!ctrlAdd && isAlreadySelected && _selectedCells.Count > 0 && _document != null) {
                    var cellNum = (ushort)(hit.Cell.CellId & 0xFFFF);
                    var dc = _document.GetCell(cellNum);
                    if (dc != null) {
                        IsDraggingCell = true;
                        _dragStartHit = hit.HitPosition;
                        _dragStartOrigin = dc.Origin;
                    }
                }
            }
            else {
                DeselectCell();
                DeselectObject();
            }
        }

        internal void HandlePointerMoved(AvaloniaInputState inputState) {
            if (_scene?.EnvCellManager == null) return;

            bool needsRaycast = IsDraggingCell || IsDraggingObject ||
                (IsObjectPlacementMode && _pendingObjectId != null) ||
                (IsPlacementMode && _pendingRoom != null) ||
                (_document != null && HasDungeon);
            if (!needsRaycast) return;

            var mouse = inputState.MouseState;
            var camera = _scene.Camera;
            float width = camera.ScreenSize.X;
            float height = camera.ScreenSize.Y;
            if (width <= 0 || height <= 0) return;

            float ndcX = 2.0f * mouse.Position.X / width - 1.0f;
            float ndcY = 2.0f * mouse.Position.Y / height - 1.0f;

            Matrix4x4 projection = camera.GetProjectionMatrix();
            Matrix4x4 view = camera.GetViewMatrix();
            if (!Matrix4x4.Invert(view * projection, out Matrix4x4 vpInverse)) return;

            Vector4 nearW = Vector4.Transform(new Vector4(ndcX, ndcY, -1f, 1f), vpInverse);
            Vector4 farW = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), vpInverse);
            nearW /= nearW.W;
            farW /= farW.W;

            var rayOrigin = new Vector3(nearW.X, nearW.Y, nearW.Z);
            var rayDir = Vector3.Normalize(new Vector3(farW.X, farW.Y, farW.Z) - rayOrigin);

            var hit = _scene.EnvCellManager.Raycast(rayOrigin, rayDir);

            // Hover tooltip: show cell info when not dragging/placing (single line for status bar)
            if (!IsDraggingCell && !IsDraggingObject && !IsObjectPlacementMode && !IsPlacementMode && hit.Hit) {
                var roomName = RoomPalette?.GetRoomDisplayName(hit.Cell.EnvironmentId, (ushort)hit.Cell.GpuKey.CellStructure);
                var roomLabel = !string.IsNullOrEmpty(roomName) ? roomName : $"Env 0x{hit.Cell.EnvironmentId:X8}";
                HoveredCellInfo = $"0x{hit.Cell.CellId:X8}  |  {roomLabel}";
            }
            else {
                HoveredCellInfo = "";
            }

            // Object placement preview: update ghost position to follow mouse
            if (IsObjectPlacementMode && _pendingObjectId != null) {
                if (hit.Hit) {
                    _scene.PlacementPreview = new Shared.Documents.StaticObject {
                        Id = _pendingObjectId.Value,
                        IsSetup = _pendingObjectIsSetup,
                        Origin = hit.HitPosition,
                        Orientation = Quaternion.Identity,
                        Scale = Vector3.One
                    };
                }
                else {
                    _scene.PlacementPreview = null;
                }
            }

            // Room placement preview: show wireframe ghost where room would be placed
            if (IsPlacementMode && _pendingRoom != null && _scene != null) {
                var preview = TryComputeRoomPlacementPreview(rayOrigin, rayDir, hit);
                if (preview.HasValue) {
                    _scene.RoomPlacementPreview = new RoomPlacementPreviewData {
                        Origin = preview.Value.Origin,
                        Orientation = preview.Value.Orientation,
                        EnvFileId = _pendingRoom.EnvironmentFileId,
                        CellStructIndex = _pendingRoom.CellStructureIndex
                    };
                }
                else {
                    _scene.RoomPlacementPreview = null;
                }
            }

            // Drag-to-move static object
            if (IsDraggingObject && _document != null && hit.Hit) {
                var cell = _document.GetCell(_selectedObjCellNum);
                if (cell != null && _selectedObjIndex < cell.StaticObjects.Count) {
                    // Convert world-space hit delta to landblock-local delta
                    var worldDelta = hit.HitPosition - _dragStartHit;
                    cell.StaticObjects[_selectedObjIndex].Origin = _dragStartOrigin + worldDelta;
                    UpdateObjectSelectionHighlight();
                    RefreshRendering();
                }
            }

            // Drag-to-move cell
            if (IsDraggingCell && _selectedCell != null && _document != null && hit.Hit) {
                var delta = hit.HitPosition - _dragStartHit;
                var cellNum = (ushort)(_selectedCell.CellId & 0xFFFF);
                var dc = _document.GetCell(cellNum);
                if (dc != null) {
                    dc.Origin = _dragStartOrigin + delta;
                    RefreshRendering();
                }
            }
        }

        internal void HandlePointerReleased(AvaloniaInputState inputState) {
            // Finalize object drag
            if (IsDraggingObject && _document != null) {
                var cell = _document.GetCell(_selectedObjCellNum);
                if (cell != null && _selectedObjIndex < cell.StaticObjects.Count) {
                    var totalDelta = cell.StaticObjects[_selectedObjIndex].Origin - _dragStartOrigin;
                    if (totalDelta.LengthSquared() > 0.001f) {
                        cell.StaticObjects[_selectedObjIndex].Origin = _dragStartOrigin;
                        CommandHistory.Execute(new MoveStaticObjectCommand(_selectedObjCellNum, _selectedObjIndex, totalDelta), _document);
                        _document.MarkDirty();
                        RefreshRendering();
                        UpdateObjectSelectionHighlight();
                        var stab = cell.StaticObjects[_selectedObjIndex];
                        ObjPosX = stab.Origin.X.ToString("F1");
                        ObjPosY = stab.Origin.Y.ToString("F1");
                        ObjPosZ = stab.Origin.Z.ToString("F1");
                        StatusText = $"Moved object 0x{stab.Id:X8}";
                    }
                }
                IsDraggingObject = false;
                return;
            }

            // Finalize cell drag
            if (IsDraggingCell && _selectedCell != null && _document != null) {
                var cellNum = (ushort)(_selectedCell.CellId & 0xFFFF);
                var dc = _document.GetCell(cellNum);
                if (dc != null) {
                    var totalDelta = dc.Origin - _dragStartOrigin;
                    if (totalDelta.LengthSquared() > 0.001f) {
                        dc.Origin = _dragStartOrigin;
                        CommandHistory.Execute(new NudgeCellCommand(cellNum, totalDelta), _document);
                        RefreshRendering();
                        StatusText = $"Moved cell {cellNum:X4} by ({totalDelta.X:F1}, {totalDelta.Y:F1}, {totalDelta.Z:F1})";
                    }
                }
            }

            IsDraggingCell = false;
            IsDraggingObject = false;
        }

        private void SelectCell(LoadedEnvCell cell) {
            _selectedCells.Clear();
            _selectedCells.Add(cell);
            _selectedCell = cell;
            HasSelectedCell = true;
            SelectedCellCount = 1;
            SyncSceneSelection();

            SelectedCellInfo = BuildCellInfoString(cell, includeStatics: true);

            var dc = _document?.GetCell((ushort)(cell.CellId & 0xFFFF));
            if (dc != null) {
                SelectedCellSurfaces = string.Join(", ", dc.Surfaces.Select(s => s.ToString("X4")));
                CellPosX = dc.Origin.X.ToString("F1");
                CellPosY = dc.Origin.Y.ToString("F1");
                CellPosZ = dc.Origin.Z.ToString("F1");

                var euler = QuatToEuler(dc.Orientation);
                CellRotX = euler.X.ToString("F1");
                CellRotY = euler.Y.ToString("F1");
                CellRotZ = euler.Z.ToString("F1");

                RefreshSurfaceSlots(dc);
                SurfaceBrowser?.SetCurrentCellSurfaces(dc.Surfaces);
            }
            OnPropertyChanged(nameof(SelectedCellLocationHex));
            OnPropertyChanged(nameof(SelectedCellTeleportCommand));
        }

        private void ToggleCellInSelection(LoadedEnvCell cell) {
            var idx = _selectedCells.FindIndex(c => c.CellId == cell.CellId);
            if (idx >= 0) {
                _selectedCells.RemoveAt(idx);
            }
            else {
                _selectedCells.Add(cell);
            }
            if (_selectedCells.Count == 0) {
                DeselectCell();
                return;
            }
            _selectedCell = _selectedCells[0];
            HasSelectedCell = true;
            SelectedCellCount = _selectedCells.Count;
            SyncSceneSelection();

            if (SelectedCellCount == 1) {
                SelectedCellInfo = BuildCellInfoString(_selectedCell, includeStatics: true);
                var dc = _document?.GetCell((ushort)(_selectedCell.CellId & 0xFFFF));
                if (dc != null) {
                    SelectedCellSurfaces = string.Join(", ", dc.Surfaces.Select(s => s.ToString("X4")));
                    CellPosX = dc.Origin.X.ToString("F1");
                    CellPosY = dc.Origin.Y.ToString("F1");
                    CellPosZ = dc.Origin.Z.ToString("F1");
                    var euler = QuatToEuler(dc.Orientation);
                    CellRotX = euler.X.ToString("F1");
                    CellRotY = euler.Y.ToString("F1");
                    CellRotZ = euler.Z.ToString("F1");
                    RefreshSurfaceSlots(dc);
                    SurfaceBrowser?.SetCurrentCellSurfaces(dc.Surfaces);
                }
            }
            else {
                SelectedCellInfo = $"{SelectedCellCount} cells selected";
                var primaryDc = _document?.GetCell((ushort)(_selectedCell.CellId & 0xFFFF));
                if (primaryDc != null) {
                    SelectedCellSurfaces = string.Join(", ", primaryDc.Surfaces.Select(s => s.ToString("X4")));
                    RefreshSurfaceSlots(primaryDc);
                    SurfaceBrowser?.SetCurrentCellSurfaces(primaryDc.Surfaces);
                }
                CellPosX = ""; CellPosY = ""; CellPosZ = "";
                CellRotX = ""; CellRotY = ""; CellRotZ = "";
            }
            OnPropertyChanged(nameof(SelectedCellLocationHex));
            OnPropertyChanged(nameof(SelectedCellTeleportCommand));
        }

        private void SyncSceneSelection() {
            if (_scene != null)
                _scene.SelectedCells = _selectedCells.ToList();
        }

        /// <summary>AC location format: "0xCELLID [x y z] w x y z" using raw cell origin from DAT.</summary>
        private string ComputeCellTeleportLine(LoadedEnvCell cell) {
            var dc = _document?.GetCell((ushort)(cell.CellId & 0xFFFF));
            if (dc == null) return $"0x{cell.CellId:X8}";

            if (!Matrix4x4.Decompose(cell.WorldTransform, out _, out var q, out _))
                q = Quaternion.Identity;
            return $"0x{cell.CellId:X8} [{dc.Origin.X:F6} {dc.Origin.Y:F6} {dc.Origin.Z:F6}] {q.W:F6} {q.X:F6} {q.Y:F6} {q.Z:F6}";
        }

        /// <summary>Builds a human-readable cell info string, using friendly room names when available.</summary>
        private string BuildCellInfoString(LoadedEnvCell cell, bool includeStatics) {
            int portalCount = cell.Portals?.Count ?? 0;
            int connectedPortals = 0;
            if (cell.Portals != null) {
                foreach (var p in cell.Portals) {
                    if (p.OtherCellId != 0 && p.OtherCellId != 0xFFFF) connectedPortals++;
                }
            }

            var roomName = RoomPalette?.GetRoomDisplayName(cell.EnvironmentId, (ushort)cell.GpuKey.CellStructure);
            var roomLabel = !string.IsNullOrEmpty(roomName) ? roomName : $"Env 0x{cell.EnvironmentId:X8}";

            var sb = new System.Text.StringBuilder();
            sb.Append($"0x{cell.CellId:X8}  |  {roomLabel}\n");
            sb.Append($"Portals: {connectedPortals}/{portalCount} connected  |  Surfaces: {cell.SurfaceCount}");
            if (includeStatics && cell.Portals != null) {
                var connected = cell.Portals
                    .Where(p => p.OtherCellId != 0 && p.OtherCellId != 0xFFFF)
                    .Select(p => $"0x{p.OtherCellId:X4}")
                    .Distinct()
                    .ToList();
                if (connected.Count > 0) {
                    sb.Append($"\nConnects to: {string.Join(", ", connected)}");
                }
            }
            if (includeStatics) {
                int staticCount = 0;
                var dc = _document?.GetCell((ushort)(cell.CellId & 0xFFFF));
                if (dc != null) staticCount = dc.StaticObjects.Count;
                sb.Append($"\nStatics: {staticCount}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Computes connection lines between portal centroids for all connected cells.
        /// Used to visualize "what connects to what" in the 3D view.
        /// </summary>
        private IReadOnlyList<(Vector3 From, Vector3 To)> ComputeConnectionLines() {
            var result = new List<(Vector3 From, Vector3 To)>();
            if (_document == null || _dats == null || _document.Cells.Count == 0) return result;

            uint lbId = _document.LandblockKey;
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);
            const float dungeonZBump = -50f;

            var drawn = new HashSet<(ushort, ushort)>(); // (min, max) to avoid duplicate lines

            foreach (var cell in _document.Cells) {
                foreach (var cp in cell.CellPortals) {
                    if (cp.OtherCellId == 0 || cp.OtherCellId == 0xFFFF) continue;

                    var otherCell = _document.GetCell(cp.OtherCellId);
                    if (otherCell == null) continue;

                    var key = (Math.Min(cell.CellNumber, cp.OtherCellId), Math.Max(cell.CellNumber, cp.OtherCellId));
                    if (drawn.Contains(key)) continue;
                    drawn.Add(key);

                    uint envFileId = (uint)(cell.EnvironmentId | 0x0D000000);
                    if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env) ||
                        !env.Cells.TryGetValue(cell.CellStructure, out var cellStruct)) continue;

                    var geomA = PortalSnapper.GetPortalGeometry(cellStruct, cp.PolygonId);
                    if (geomA == null) continue;

                    var worldOriginA = cell.Origin + lbOffset + new Vector3(0, 0, dungeonZBump);
                    var (centroidA, _) = PortalSnapper.TransformPortalToWorld(geomA.Value, worldOriginA, cell.Orientation);

                    uint otherEnvFileId = (uint)(otherCell.EnvironmentId | 0x0D000000);
                    if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(otherEnvFileId, out var otherEnv) ||
                        !otherEnv.Cells.TryGetValue(otherCell.CellStructure, out var otherCellStruct)) continue;

                    var backPortal = otherCell.CellPortals.FirstOrDefault(p => p.OtherCellId == cell.CellNumber);
                    if (backPortal == null) continue; // no matching back-link

                    var geomB = PortalSnapper.GetPortalGeometry(otherCellStruct, backPortal.PolygonId);
                    if (geomB == null) continue;

                    var worldOriginB = otherCell.Origin + lbOffset + new Vector3(0, 0, dungeonZBump);
                    var (centroidB, _) = PortalSnapper.TransformPortalToWorld(geomB.Value, worldOriginB, otherCell.Orientation);

                    result.Add((centroidA, centroidB));
                }
            }

            return result;
        }

        private void DeselectCell() {
            _selectedCells.Clear();
            _selectedCell = null;
            HasSelectedCell = false;
            SelectedCellCount = 0;
            SelectedCellInfo = "";
            SurfaceSlots.Clear();
            SelectedSurfaceSlot = -1;
            CellPosX = ""; CellPosY = ""; CellPosZ = "";
            CellRotX = ""; CellRotY = ""; CellRotZ = "";
            if (_scene != null) _scene.SelectedCells = null;
            SurfaceBrowser?.SetCurrentCellSurfaces(null);
            OnPropertyChanged(nameof(SelectedCellLocationHex));
            OnPropertyChanged(nameof(SelectedCellTeleportCommand));
        }

        private void SelectObject(ushort cellNum, int objectIndex, DungeonStabData stab, Vector3 hitPosition) {
            DeselectCell();
            DeselectObject();

            _selectedObjCellNum = cellNum;
            _selectedObjIndex = objectIndex;
            HasSelectedObject = true;

            bool isSetup = (stab.Id & 0xFF000000) == 0x02000000;
            SelectedObjectInfo = $"Object 0x{stab.Id:X8}  |  Cell {cellNum:X4}\n" +
                $"Pos: ({stab.Origin.X:F1}, {stab.Origin.Y:F1}, {stab.Origin.Z:F1})";

            ObjPosX = stab.Origin.X.ToString("F1");
            ObjPosY = stab.Origin.Y.ToString("F1");
            ObjPosZ = stab.Origin.Z.ToString("F1");

            var q = stab.Orientation;
            float deg = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z)) * 180f / MathF.PI;
            ObjRotDegrees = deg.ToString("F1");

            UpdateObjectSelectionHighlight(stab);
        }

        private void DeselectObject() {
            _selectedObjCellNum = 0;
            _selectedObjIndex = -1;
            HasSelectedObject = false;
            SelectedObjectInfo = "";
            ObjPosX = ""; ObjPosY = ""; ObjPosZ = "";
            ObjRotDegrees = "";
            if (_scene != null) _scene.SelectedObjectBounds = null;
        }

        private void UpdateObjectSelectionHighlight(DungeonStabData? stab = null) {
            if (_scene == null || _document == null) return;

            if (stab == null) {
                var cell = _document.GetCell(_selectedObjCellNum);
                if (cell == null || _selectedObjIndex >= cell.StaticObjects.Count) {
                    _scene.SelectedObjectBounds = null;
                    return;
                }
                stab = cell.StaticObjects[_selectedObjIndex];
            }

            bool isSetup = (stab.Id & 0xFF000000) == 0x02000000;
            var bounds = _scene.GetObjectBounds(stab.Id, isSetup);
            if (bounds == null) { _scene.SelectedObjectBounds = null; return; }

            uint lbId = _document.LandblockKey;
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);

            var worldOrigin = stab.Origin + lbOffset;
            worldOrigin.Z += -50f;

            var worldTransform = Matrix4x4.CreateFromQuaternion(stab.Orientation)
                * Matrix4x4.CreateTranslation(worldOrigin);

            var worldMin = Vector3.Transform(bounds.Value.Min, worldTransform);
            var worldMax = Vector3.Transform(bounds.Value.Max, worldTransform);

            _scene.SelectedObjectBounds = (Vector3.Min(worldMin, worldMax), Vector3.Max(worldMin, worldMax));
        }

        #region Cell Editing

        [RelayCommand]
        private void DeleteSelectedCell() {
            if (_selectedCells.Count == 0 || _document == null) return;

            var toRemove = _selectedCells.Select(c => (ushort)(c.CellId & 0xFFFF)).ToList();
            DeselectCell();
            foreach (var cellNum in toRemove) {
                CommandHistory.Execute(new RemoveCellCommand(cellNum), _document);
            }
            RefreshRendering();
            CellCount = _document.Cells.Count;
            StatusText = $"LB {_document.LandblockKey:X4}: removed {toRemove.Count} cell(s), {CellCount} remaining";
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
            if (_selectedCell == null || _document == null) return;
            var cellNum = (ushort)(_selectedCell.CellId & 0xFFFF);
            CommandHistory.Execute(new RotateCellCommand(cellNum, degrees, axis), _document);
            RefreshRendering();
            _needsCameraFocus = false;
        }

        private void NudgeSelectedCell(Vector3 offset) {
            if (_selectedCell == null || _document == null) return;
            var cellNum = (ushort)(_selectedCell.CellId & 0xFFFF);
            CommandHistory.Execute(new NudgeCellCommand(cellNum, offset), _document);
            RefreshRendering();
            _needsCameraFocus = false;
        }

        [RelayCommand]
        private void ApplyPosition() {
            if (_selectedCell == null || _document == null) return;
            if (!float.TryParse(CellPosX, out var x) || !float.TryParse(CellPosY, out var y) || !float.TryParse(CellPosZ, out var z)) {
                StatusText = "Invalid position values";
                return;
            }
            var cellNum = (ushort)(_selectedCell.CellId & 0xFFFF);
            var dc = _document.GetCell(cellNum);
            if (dc == null) return;
            var delta = new Vector3(x, y, z) - dc.Origin;
            if (delta.LengthSquared() < 0.001f) return;
            CommandHistory.Execute(new NudgeCellCommand(cellNum, delta), _document);
            RefreshRendering();
            StatusText = $"Cell {cellNum:X4} moved to ({x:F1}, {y:F1}, {z:F1})";
        }

        [RelayCommand]
        private void ApplyRotation() {
            if (_selectedCell == null || _document == null) return;
            if (!float.TryParse(CellRotX, out var rx) || !float.TryParse(CellRotY, out var ry) || !float.TryParse(CellRotZ, out var rz)) {
                StatusText = "Invalid rotation values";
                return;
            }
            var cellNum = (ushort)(_selectedCell.CellId & 0xFFFF);
            var dc = _document.GetCell(cellNum);
            if (dc == null) return;

            var targetQuat = EulerToQuat(new Vector3(rx, ry, rz));
            dc.Orientation = targetQuat;
            _document.MarkDirty();
            RefreshRendering();
            StatusText = $"Cell {cellNum:X4} rotation set to ({rx:F1}, {ry:F1}, {rz:F1})";
        }

        private static Vector3 QuatToEuler(Quaternion q) {
            // ZYX Euler angles (yaw/pitch/roll) in degrees
            float sinr = 2f * (q.W * q.X + q.Y * q.Z);
            float cosr = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            float rx = MathF.Atan2(sinr, cosr) * 180f / MathF.PI;

            float sinp = 2f * (q.W * q.Y - q.Z * q.X);
            float ry = MathF.Abs(sinp) >= 1f
                ? MathF.CopySign(90f, sinp)
                : MathF.Asin(sinp) * 180f / MathF.PI;

            float siny = 2f * (q.W * q.Z + q.X * q.Y);
            float cosy = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            float rz = MathF.Atan2(siny, cosy) * 180f / MathF.PI;

            return new Vector3(rx, ry, rz);
        }

        private static Quaternion EulerToQuat(Vector3 eulerDeg) {
            float rx = eulerDeg.X * MathF.PI / 180f;
            float ry = eulerDeg.Y * MathF.PI / 180f;
            float rz = eulerDeg.Z * MathF.PI / 180f;
            return Quaternion.Normalize(
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rz) *
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, ry) *
                Quaternion.CreateFromAxisAngle(Vector3.UnitX, rx));
        }

        [ObservableProperty] private string _selectedCellSurfaces = "";

        private void RefreshSurfaceSlots(DungeonCellData dc) {
            var slots = new List<CellSurfaceSlot>(dc.Surfaces.Count);
            for (int i = 0; i < dc.Surfaces.Count; i++) {
                var surfId = dc.Surfaces[i];
                var fullId = (uint)(surfId | 0x08000000);
                var slot = new CellSurfaceSlot(i, surfId, $"Slot {i}: 0x{surfId:X4}");

                var localSlot = slot;
                var localDats = _dats;
                if (localDats != null) {
                    System.Threading.Tasks.Task.Run(() => {
                        var thumb = SurfaceBrowserViewModel.CreateBitmap(
                            GenerateSmallSurfaceThumb(localDats, fullId), 32, 32);
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => localSlot.Thumbnail = thumb);
                    });
                }
                slots.Add(slot);
            }
            SurfaceSlots = new ObservableCollection<CellSurfaceSlot>(slots);
            if (SurfaceSlots.Count > 0) SelectedSurfaceSlot = 0;
        }

        private static byte[] GenerateSmallSurfaceThumb(IDatReaderWriter dats, uint surfaceId) {
            const int sz = 32;
            try {
                if (!dats.TryGet<Surface>(surfaceId, out var surface))
                    return CreateFallbackThumb(sz);
                if (surface.Type.HasFlag(SurfaceType.Base1Solid))
                    return TextureHelpers.CreateSolidColorTexture(surface.ColorValue, sz, sz);
                if (!dats.TryGet<SurfaceTexture>(surface.OrigTextureId, out var surfTex) ||
                    surfTex.Textures?.Any() != true)
                    return CreateFallbackThumb(sz);
                var rsId = surfTex.Textures.Last();
                if (!dats.TryGet<RenderSurface>(rsId, out var rs))
                    return CreateFallbackThumb(sz);
                if (rs.Format == DatReaderWriter.Enums.PixelFormat.PFID_A8R8G8B8 && rs.SourceData.Length >= sz * sz * 4) {
                    var downsampled = SurfaceBrowserViewModel.DownsampleNearest(rs.SourceData, rs.Width, rs.Height, sz, sz);
                    return DatIconLoader.SwizzleBgraToRgba(downsampled, sz * sz);
                }
            }
            catch { }
            return CreateFallbackThumb(sz);
        }

        private static byte[] CreateFallbackThumb(int sz) {
            var data = new byte[sz * sz * 4];
            for (int i = 0; i < data.Length; i += 4) {
                data[i] = 60; data[i + 1] = 40; data[i + 2] = 80; data[i + 3] = 255;
            }
            return data;
        }

        [RelayCommand]
        private void ApplySurfaces() {
            if (_selectedCells.Count == 0 || _document == null) return;

            var newSurfaces = new List<ushort>();
            foreach (var part in SelectedCellSurfaces.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                var hex = part.TrimStart('0', 'x', 'X');
                if (string.IsNullOrEmpty(hex)) hex = "0";
                if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var surfId))
                    newSurfaces.Add(surfId);
            }

            if (newSurfaces.Count == 0) return;

            int updated = 0;
            foreach (var cell in _selectedCells) {
                var cellNum = (ushort)(cell.CellId & 0xFFFF);
                var dc = _document.GetCell(cellNum);
                if (dc == null) continue;
                dc.Surfaces.Clear();
                dc.Surfaces.AddRange(newSurfaces);
                updated++;
            }
            if (updated > 0) {
                _document.MarkDirty();
                RefreshRendering();
                StatusText = $"Updated surfaces on {updated} cell(s)";
            }
        }

        private void OnSurfaceSelected(object? sender, ushort surfaceId) {
            if (_selectedCells.Count == 0 || _document == null) {
                StatusText = "Select a cell first, then pick a surface";
                return;
            }

            int updated = 0;
            int slotIdx = SelectedSurfaceSlot;
            foreach (var cell in _selectedCells) {
                var cellNum = (ushort)(cell.CellId & 0xFFFF);
                var dc = _document.GetCell(cellNum);
                if (dc == null) continue;

                if (slotIdx >= 0 && slotIdx < dc.Surfaces.Count) {
                    dc.Surfaces[slotIdx] = surfaceId;
                }
                else {
                    for (int i = 0; i < dc.Surfaces.Count; i++)
                        dc.Surfaces[i] = surfaceId;
                    if (dc.Surfaces.Count == 0)
                        dc.Surfaces.Add(surfaceId);
                }
                updated++;
            }

            _document.MarkDirty();
            RefreshRendering();
            var primaryDc = _document.GetCell((ushort)(_selectedCell!.CellId & 0xFFFF));
            if (primaryDc != null) {
                SelectedCellSurfaces = string.Join(", ", primaryDc.Surfaces.Select(s => s.ToString("X4")));
                RefreshSurfaceSlots(primaryDc);
            }
            StatusText = slotIdx >= 0
                ? $"Slot {slotIdx}: applied 0x{surfaceId:X4} to {updated} cell(s)"
                : $"Applied 0x{surfaceId:X4} to all slots on {updated} cell(s)";
        }

        [RelayCommand]
        private void DisconnectPortal() {
            if (_selectedCell == null || _document == null) return;

            var cellNum = (ushort)(_selectedCell.CellId & 0xFFFF);
            var dc = _document.GetCell(cellNum);
            if (dc == null || dc.CellPortals.Count == 0) return;

            var lastPortal = dc.CellPortals[^1];
            var otherCellNum = lastPortal.OtherCellId;

            dc.CellPortals.RemoveAt(dc.CellPortals.Count - 1);

            var otherCell = _document.GetCell(otherCellNum);
            if (otherCell != null) {
                otherCell.CellPortals.RemoveAll(cp => cp.OtherCellId == cellNum);
            }

            _document.MarkDirty();
            RefreshRendering();
            SelectCell(_selectedCell);
        }

        internal void CopySelectedCells() {
            if (_selectedCells.Count == 0 || _document == null) {
                Console.WriteLine($"[Dungeon] Copy: nothing to copy (selected={_selectedCells.Count}, doc={(_document != null ? "yes" : "null")})");
                return;
            }

            _cellClipboard = new List<DungeonCellData>();
            foreach (var cell in _selectedCells) {
                var cellNum = (ushort)(cell.CellId & 0xFFFF);
                var dc = _document.GetCell(cellNum);
                if (dc == null) continue;
                _cellClipboard.Add(DeepCloneCell(dc));
            }
            Console.WriteLine($"[Dungeon] Copied {_cellClipboard.Count} cell(s) to clipboard");
            StatusText = $"Copied {_cellClipboard.Count} cell(s)";
        }

        internal void PasteCells() {
            Console.WriteLine($"[Dungeon] Paste: clipboard={_cellClipboard?.Count ?? 0}, doc={(_document != null ? "yes" : "null")}");
            if (_cellClipboard == null || _cellClipboard.Count == 0 || _document == null) return;

            var offset = new Vector3(10f, 0f, 0f);
            var cmd = new PasteCellsCommand(_cellClipboard, offset);
            CommandHistory.Execute(cmd, _document);

            RefreshRendering();
            CellCount = _document.Cells.Count;
            StatusText = $"Pasted {_cellClipboard.Count} cell(s)";

            // Auto-select the pasted cells
            DeselectCell();
            if (_scene?.EnvCellManager != null) {
                uint lbId = _document.LandblockKey;
                foreach (var newCellNum in cmd.CreatedCellNums) {
                    uint fullId = ((uint)lbId << 16) | newCellNum;
                    var loaded = _scene.EnvCellManager.FindCell(fullId);
                    if (loaded != null)
                        _selectedCells.Add(loaded);
                }
                if (_selectedCells.Count > 0) {
                    _selectedCell = _selectedCells[0];
                    HasSelectedCell = true;
                    SelectedCellCount = _selectedCells.Count;
                    SyncSceneSelection();
                    SelectedCellInfo = SelectedCellCount == 1
                        ? BuildCellInfoString(_selectedCell, includeStatics: true)
                        : $"{SelectedCellCount} cells selected";
                }
            }
        }

        private static DungeonCellData DeepCloneCell(DungeonCellData src) {
            var clone = new DungeonCellData {
                CellNumber = src.CellNumber,
                EnvironmentId = src.EnvironmentId,
                CellStructure = src.CellStructure,
                Origin = src.Origin,
                Orientation = src.Orientation,
                Flags = src.Flags,
                RestrictionObj = src.RestrictionObj,
            };
            clone.Surfaces.AddRange(src.Surfaces);
            foreach (var cp in src.CellPortals) {
                clone.CellPortals.Add(new DungeonCellPortalData {
                    OtherCellId = cp.OtherCellId,
                    PolygonId = cp.PolygonId,
                    OtherPortalId = cp.OtherPortalId,
                    Flags = cp.Flags
                });
            }
            clone.VisibleCells.AddRange(src.VisibleCells);
            foreach (var stab in src.StaticObjects) {
                clone.StaticObjects.Add(new DungeonStabData {
                    Id = stab.Id,
                    Origin = stab.Origin,
                    Orientation = stab.Orientation
                });
            }
            return clone;
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
            if (!HasSelectedObject || _document == null) return;
            CommandHistory.Execute(new MoveStaticObjectCommand(_selectedObjCellNum, _selectedObjIndex, offset), _document);
            _document.MarkDirty();
            RefreshRendering();
            UpdateObjectSelectionHighlight();
            RefreshSelectedObjectFields();
        }

        private void RotateSelectedObject(float degrees) {
            if (!HasSelectedObject || _document == null) return;
            CommandHistory.Execute(new RotateStaticObjectCommand(_selectedObjCellNum, _selectedObjIndex, degrees), _document);
            _document.MarkDirty();
            RefreshRendering();
            UpdateObjectSelectionHighlight();
            RefreshSelectedObjectFields();
        }

        [RelayCommand]
        private void DeleteSelectedObject() {
            if (!HasSelectedObject || _document == null) return;
            CommandHistory.Execute(new DeleteStaticObjectCommand(_selectedObjCellNum, _selectedObjIndex), _document);
            _document.MarkDirty();
            DeselectObject();
            RefreshRendering();
            StatusText = "Object deleted";
        }

        [RelayCommand]
        private void ApplyObjPosition() {
            if (!HasSelectedObject || _document == null) return;
            if (!float.TryParse(ObjPosX, out var x) || !float.TryParse(ObjPosY, out var y) || !float.TryParse(ObjPosZ, out var z)) {
                StatusText = "Invalid position values";
                return;
            }
            var cell = _document.GetCell(_selectedObjCellNum);
            if (cell == null || _selectedObjIndex >= cell.StaticObjects.Count) return;
            var delta = new Vector3(x, y, z) - cell.StaticObjects[_selectedObjIndex].Origin;
            if (delta.LengthSquared() < 0.001f) return;
            CommandHistory.Execute(new MoveStaticObjectCommand(_selectedObjCellNum, _selectedObjIndex, delta), _document);
            _document.MarkDirty();
            RefreshRendering();
            UpdateObjectSelectionHighlight();
            RefreshSelectedObjectFields();
            StatusText = $"Object moved to ({x:F1}, {y:F1}, {z:F1})";
        }

        [RelayCommand]
        private void ApplyObjRotation() {
            if (!HasSelectedObject || _document == null) return;
            if (!float.TryParse(ObjRotDegrees, out var targetDeg)) {
                StatusText = "Invalid rotation value";
                return;
            }
            var cell = _document.GetCell(_selectedObjCellNum);
            if (cell == null || _selectedObjIndex >= cell.StaticObjects.Count) return;
            var q = cell.StaticObjects[_selectedObjIndex].Orientation;
            float currentDeg = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z)) * 180f / MathF.PI;
            float delta = targetDeg - currentDeg;
            if (MathF.Abs(delta) < 0.01f) return;
            CommandHistory.Execute(new RotateStaticObjectCommand(_selectedObjCellNum, _selectedObjIndex, delta), _document);
            _document.MarkDirty();
            RefreshRendering();
            UpdateObjectSelectionHighlight();
            RefreshSelectedObjectFields();
            StatusText = $"Object rotated to {targetDeg:F1} deg";
        }

        private void RefreshSelectedObjectFields() {
            if (!HasSelectedObject || _document == null) return;
            var cell = _document.GetCell(_selectedObjCellNum);
            if (cell == null || _selectedObjIndex >= cell.StaticObjects.Count) return;
            var stab = cell.StaticObjects[_selectedObjIndex];
            ObjPosX = stab.Origin.X.ToString("F1");
            ObjPosY = stab.Origin.Y.ToString("F1");
            ObjPosZ = stab.Origin.Z.ToString("F1");
            var q = stab.Orientation;
            float deg = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z)) * 180f / MathF.PI;
            ObjRotDegrees = deg.ToString("F1");
            SelectedObjectInfo = $"Object 0x{stab.Id:X8}  |  Cell {_selectedObjCellNum:X4}\n" +
                $"Pos: ({stab.Origin.X:F1}, {stab.Origin.Y:F1}, {stab.Origin.Z:F1})";
        }

        #endregion

        #region Object Placement

        private void OnObjectPlacementRequested(object? sender, Landscape.ViewModels.ObjectBrowserItem item) {
            _pendingObjectId = item.Id;
            _pendingObjectIsSetup = item.IsSetup;
            IsObjectPlacementMode = true;
            PlacementStatusText = $"Click in viewport to place 0x{item.Id:X8}";
            _scene?.WarmupModel(item.Id, item.IsSetup);
        }

        [RelayCommand]
        private void StartObjectPlacement() {
            if (string.IsNullOrWhiteSpace(ObjectIdInput)) return;

            var hex = ObjectIdInput.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var objId)) {
                StatusText = "Invalid object ID";
                return;
            }

            _pendingObjectId = objId;
            _pendingObjectIsSetup = (objId & 0xFF000000) == 0x02000000;
            IsObjectPlacementMode = true;
            PlacementStatusText = $"Click in viewport to place object 0x{objId:X8}";
            _scene?.WarmupModel(objId, _pendingObjectIsSetup);
        }

        [RelayCommand]
        private void CancelObjectPlacement() {
            _pendingObjectId = null;
            IsObjectPlacementMode = false;
            if (_scene != null) _scene.PlacementPreview = null;
            if (!IsPlacementMode) PlacementStatusText = "";
        }

        private uint? _pendingObjectId;
        private bool _pendingObjectIsSetup;

        private void TryPlaceObject(Vector3 rayOrigin, Vector3 rayDir) {
            if (_pendingObjectId == null || _document == null || _scene == null) return;

            var hit = _scene.EnvCellManager?.Raycast(rayOrigin, rayDir);
            if (hit == null || !hit.Value.Hit) return;

            var targetCell = hit.Value.Cell;
            var cellNum = (ushort)(targetCell.CellId & 0xFFFF);
            var dc = _document.GetCell(cellNum);
            if (dc == null) return;

            // Convert world-space hit position to landblock-local coordinates
            uint lbId = _document.LandblockKey;
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);

            var localOrigin = hit.Value.HitPosition - lbOffset;
            localOrigin.Z += 50f; // Reverse the dungeon depth offset

            dc.StaticObjects.Add(new DungeonStabData {
                Id = _pendingObjectId.Value,
                Origin = localOrigin,
                Orientation = Quaternion.Identity
            });

            _document.MarkDirty();
            RefreshRendering();
            StatusText = $"Placed 0x{_pendingObjectId.Value:X8} in cell {cellNum:X4}";

            // Stay in placement mode so user can place multiple objects
            // but clear the preview momentarily (it'll reappear on next mouse move)
            if (_scene != null) _scene.PlacementPreview = null;
        }

        #endregion

        #region Placement

        private RoomEntry? _pendingRoom;

        private void OnRoomSelected(object? sender, RoomEntry room) {
            if (_document == null) {
                EnsureDocument();
            }

            _pendingRoom = room;
            IsPlacementMode = true;

            if (_document!.Cells.Count == 0) {
                PlacementStatusText = $"Click anywhere in viewport to place '{room.DisplayName}' as first cell";
                Console.WriteLine($"[Dungeon] Room selected: {room.DisplayName} (Env=0x{room.EnvironmentId:X4}, Struct={room.CellStructureIndex}) - FIRST CELL mode");
            }
            else {
                // Count cells with open portals to give useful feedback
                int openCount = 0;
                if (_dats != null) {
                    foreach (var c in _document.Cells) {
                        uint envFileId = (uint)(c.EnvironmentId | 0x0D000000);
                        if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env)) continue;
                        if (!env.Cells.TryGetValue(c.CellStructure, out var cs)) continue;
                        var portalIds = PortalSnapper.GetPortalPolygonIds(cs);
                        var connectedIds = new HashSet<ushort>(c.CellPortals.Select(cp => (ushort)cp.PolygonId));
                        if (portalIds.Any(p => !connectedIds.Contains(p))) openCount++;
                    }
                }
                PlacementStatusText = $"Click a cell surface with an open portal ({openCount} cells have open portals)";
                Console.WriteLine($"[Dungeon] Room selected: {room.DisplayName} - PORTAL SNAP ({openCount} cells with open portals out of {_document.Cells.Count})");
            }
        }

        [RelayCommand]
        private async Task AnalyzeRooms() {
            if (_dats == null) {
                StatusText = "No DAT loaded";
                return;
            }
            StatusText = "Analyzing dungeon rooms...";
            Console.WriteLine("[Dungeon] AnalyzeRooms: starting...");
            try {
                var report = await Task.Run(() => DungeonRoomAnalyzer.Run(_dats));
                Console.WriteLine($"[Dungeon] AnalyzeRooms: done - {report.TotalLandblocksScanned} landblocks, {report.TotalCellsScanned} cells, {report.UniqueRoomTypes} room types");
                var outDir = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "ACME WorldBuilder");
                var outPath = Path.Combine(outDir, "dungeon_room_analysis");
                DungeonRoomAnalyzer.SaveReport(report, outPath);

                StatusText = $"Analysis complete: {report.UniqueRoomTypes} room types, {report.TotalCellsScanned} cells";
                RoomPalette?.ReloadStarterPresets();
                ShowAnalysisResultDialog(report, outPath);
            }
            catch (Exception ex) {
                StatusText = $"Analysis failed: {ex.Message}";
                Console.WriteLine($"[Dungeon] AnalyzeRooms error: {ex}");
                ShowErrorDialog("Analysis Failed", ex.Message);
            }
        }

        private void ShowErrorDialog(string title, string message) {
            var win = new Window { Title = title, Width = 400, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(24) };
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14 });
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            var okBtn = new Button { Content = "OK", Width = 80 };
            okBtn.Click += (s, e) => win.Close();
            panel.Children.Add(okBtn);
            win.Content = panel;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                win.Show(desktop.MainWindow);
            else
                win.Show();
        }

        private void ShowAnalysisResultDialog(DungeonRoomAnalyzer.AnalysisReport report, string outPath) {
            var jsonPath = Path.Combine(Path.GetDirectoryName(outPath) ?? "", Path.GetFileNameWithoutExtension(outPath) + ".json");
            var txtPath = Path.Combine(Path.GetDirectoryName(outPath) ?? "", Path.GetFileNameWithoutExtension(outPath) + ".txt");

            var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(24) };
            panel.Children.Add(new TextBlock {
                Text = "Dungeon Room Analysis Complete",
                FontWeight = FontWeight.SemiBold,
                FontSize = 14
            });
            panel.Children.Add(new TextBlock {
                Text = $"Scanned {report.TotalLandblocksScanned} landblocks, {report.TotalCellsScanned} cells.\n" +
                       $"Found {report.UniqueRoomTypes} unique room types.\n\n" +
                       $"Top starter candidates (for preset list):",
                TextWrapping = TextWrapping.Wrap
            });
            var sb = new System.Text.StringBuilder();
            foreach (var r in report.TopStarterCandidates.Take(12)) {
                sb.AppendLine($"  {r.PortalCount}P: 0x{r.EnvFileId:X8} / #{r.CellStructIndex}  (used {r.UsageCount}x)");
            }
            panel.Children.Add(new TextBlock {
                Text = sb.ToString(),
                FontFamily = "Consolas",
                FontSize = 10,
                TextWrapping = TextWrapping.NoWrap
            });
            panel.Children.Add(new TextBlock {
                Text = $"Report saved to:\n{txtPath}\n{jsonPath}",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 138, 122, 158)),
                TextWrapping = TextWrapping.Wrap
            });
            var win = new Window { Title = "Dungeon Room Analysis", WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var okBtn = new Button { Content = "OK", Width = 80 };
            okBtn.Click += (s, e) => win.Close();
            panel.Children.Add(okBtn);
            win.Content = new ScrollViewer { Content = panel };
            win.Width = 480;
            win.Height = 420;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null) {
                win.Show(desktop.MainWindow);
            }
            else {
                win.Show();
            }
        }

        [RelayCommand]
        private void CancelPlacement() {
            _pendingRoom = null;
            IsPlacementMode = false;
            PlacementStatusText = "";
            if (_scene != null) _scene.RoomPlacementPreview = null;
        }

        [RelayCommand]
        private async Task NewDungeon() {
            if (_dats == null) return;

            var cellId = await ShowNewDungeonDialog();
            if (cellId == null) return;

            var lbKey = (ushort)(cellId.Value >> 16);
            if (lbKey == 0) lbKey = (ushort)(cellId.Value & 0xFFFF);

            _loadedLandblockKey = lbKey;
            _document = GetOrCreateDungeonDoc(lbKey);
            HasDungeon = true;
            CellCount = 0;
            StatusText = $"LB {lbKey:X4}: New dungeon (empty)";

            _scene?.Camera.SetPosition(Vector3.Zero + new Vector3(0, -20f, 10f));
            _scene?.Camera.LookAt(Vector3.Zero);
        }

        private void EnsureDocument() {
            if (_document != null) return;
            _loadedLandblockKey = 0xFFFF;
            _document = GetOrCreateDungeonDoc(_loadedLandblockKey);
            HasDungeon = true;
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

            StatusText = $"LB {_document.LandblockKey:X4}: {_document.Cells.Count} cells";
            CellCount = _document.Cells.Count;

            CancelPlacement();
        }

        /// <summary>
        /// Computes where the room would be placed for preview. Returns (Origin, Orientation) or null if no valid placement.
        /// </summary>
        private (Vector3 Origin, Quaternion Orientation)? TryComputeRoomPlacementPreview(
            Vector3 rayOrigin, Vector3 rayDir, EnvCellManager.EnvCellRaycastHit hit) {

            if (_pendingRoom == null || _document == null || _dats == null) return null;

            // Empty dungeon: first cell goes at origin
            if (_document.Cells.Count == 0) {
                return (Vector3.Zero, Quaternion.Identity);
            }

            // Non-empty: need to hit a cell with an open portal
            if (!hit.Hit) return null;

            var targetCell = hit.Cell;
            var targetCellNum = (ushort)(targetCell.CellId & 0xFFFF);
            var targetDocCell = _document.GetCell(targetCellNum);
            if (targetDocCell == null) return null;

            uint targetEnvFileId = targetCell.EnvironmentId;
            if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(targetEnvFileId, out var targetEnv)) return null;
            if (!targetEnv.Cells.TryGetValue((ushort)targetDocCell.CellStructure, out var targetCellStruct)) return null;

            var connectedPolygons = new HashSet<ushort>();
            foreach (var cp in targetDocCell.CellPortals) {
                connectedPolygons.Add((ushort)cp.PolygonId);
            }

            var targetPortalIds = PortalSnapper.GetPortalPolygonIds(targetCellStruct);
            var openPortals = targetPortalIds.Where(pid => !connectedPolygons.Contains(pid)).ToList();
            ushort? openPortalId = null;
            if (openPortals.Count == 1) {
                openPortalId = openPortals[0];
            }
            else if (openPortals.Count > 1) {
                var hitPos = hit.HitPosition;
                float bestDist = float.MaxValue;
                foreach (var pid in openPortals) {
                    var geom = PortalSnapper.GetPortalGeometry(targetCellStruct, pid);
                    if (geom == null) continue;
                    var (centroid, _) = PortalSnapper.TransformPortalToWorld(geom.Value, targetDocCell.Origin, targetDocCell.Orientation);
                    float d = (centroid - hitPos).LengthSquared();
                    if (d < bestDist) { bestDist = d; openPortalId = pid; }
                }
                openPortalId ??= openPortals[0];
            }
            if (openPortalId == null) return null;

            var targetPortalLocal = PortalSnapper.GetPortalGeometry(targetCellStruct, openPortalId.Value);
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

            var hit = _scene.EnvCellManager?.Raycast(rayOrigin, rayDir);
            if (hit == null || !hit.Value.Hit) {
                Console.WriteLine("[Dungeon] TrySnapToPortal: raycast missed (no cell hit)");
                PlacementStatusText = "Click on a cell to attach room";
                return;
            }

            var targetCell = hit.Value.Cell;
            var targetCellNum = (ushort)(targetCell.CellId & 0xFFFF);
            var targetDocCell = _document.GetCell(targetCellNum);
            if (targetDocCell == null) {
                Console.WriteLine($"[Dungeon] TrySnapToPortal: cell 0x{targetCellNum:X4} not in document");
                return;
            }

            uint targetEnvFileId = targetCell.EnvironmentId;
            if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(targetEnvFileId, out var targetEnv)) {
                Console.WriteLine($"[Dungeon] TrySnapToPortal: Environment 0x{targetEnvFileId:X8} not found");
                return;
            }

            var targetDocCellStruct = (ushort)targetDocCell.CellStructure;
            if (!targetEnv.Cells.TryGetValue(targetDocCellStruct, out var targetCellStruct)) {
                Console.WriteLine($"[Dungeon] TrySnapToPortal: CellStruct {targetDocCellStruct} not in Environment");
                return;
            }

            var connectedPolygons = new HashSet<ushort>();
            foreach (var cp in targetDocCell.CellPortals) {
                connectedPolygons.Add((ushort)cp.PolygonId);
            }

            var targetPortalIds = PortalSnapper.GetPortalPolygonIds(targetCellStruct);
            var openPortals = targetPortalIds.Where(pid => !connectedPolygons.Contains(pid)).ToList();
            ushort? openPortalId = null;
            if (openPortals.Count == 1) {
                openPortalId = openPortals[0];
            }
            else if (openPortals.Count > 1) {
                // Pick the open portal closest to the ray hit (so we attach where user clicked)
                var hitPos = hit.Value.HitPosition;
                float bestDist = float.MaxValue;
                foreach (var pid in openPortals) {
                    var geom = PortalSnapper.GetPortalGeometry(targetCellStruct, pid);
                    if (geom == null) continue;
                    var (centroid, _) = PortalSnapper.TransformPortalToWorld(geom.Value, targetDocCell.Origin, targetDocCell.Orientation);
                    float d = (centroid - hitPos).LengthSquared();
                    if (d < bestDist) { bestDist = d; openPortalId = pid; }
                }
                openPortalId ??= openPortals[0];
            }

            Console.WriteLine($"[Dungeon] TrySnapToPortal: cell 0x{targetCellNum:X4}, portals={targetPortalIds.Count}, connected={connectedPolygons.Count}, open={openPortalId?.ToString("X4") ?? "none"}");

            if (openPortalId == null) {
                PlacementStatusText = "No open portals on this cell - try a cell at a dead end";
                return;
            }

            // Get target portal geometry in local space, then transform to world
            var targetPortalLocal = PortalSnapper.GetPortalGeometry(targetCellStruct, openPortalId.Value);
            if (targetPortalLocal == null) return;

            var (targetCentroidWorld, targetNormalWorld) = PortalSnapper.TransformPortalToWorld(
                targetPortalLocal.Value, targetDocCell.Origin, targetDocCell.Orientation);

            // Load the source room's CellStruct
            uint sourceEnvFileId = (uint)(_pendingRoom.EnvironmentId | 0x0D000000);
            if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(sourceEnvFileId, out var sourceEnv)) return;
            if (!sourceEnv.Cells.TryGetValue(_pendingRoom.CellStructureIndex, out var sourceCellStruct)) return;

            // Pick the source portal that best aligns with the target (facing opposite)
            var sourcePortalId = PortalSnapper.PickBestSourcePortal(sourceCellStruct, targetNormalWorld);
            if (sourcePortalId == null) {
                PlacementStatusText = "Selected room has no portals";
                return;
            }

            var sourcePortalLocal = PortalSnapper.GetPortalGeometry(sourceCellStruct, sourcePortalId.Value);
            if (sourcePortalLocal == null) return;

            // Compute snap transform
            var (newOrigin, newOrientation) = PortalSnapper.ComputeSnapTransform(
                targetCentroidWorld, targetNormalWorld, sourcePortalLocal.Value);

            // Add the new cell with portal connection via command history
            var surfaces = GetSurfacesForRoom(_pendingRoom);
            var cmd = new AddCellCommand(
                _pendingRoom.EnvironmentId, _pendingRoom.CellStructureIndex,
                newOrigin, newOrientation, surfaces,
                connectToCellNum: targetCellNum, connectToPolyId: openPortalId.Value, sourcePolyId: sourcePortalId.Value);
            CommandHistory.Execute(cmd, _document);

            RefreshRendering();

            StatusText = $"LB {_document.LandblockKey:X4}: {_document.Cells.Count} cells";
            CellCount = _document.Cells.Count;

            CancelPlacement();
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
            try {
                // Scan LandBlockInfo entries to find an EnvCell using this environment
                var lbiIds = _dats.Dats.GetAllIdsOfType<LandBlockInfo>().Take(2000).ToArray();
                if (lbiIds.Length == 0) lbiIds = _dats.Dats.Cell.GetAllIdsOfType<LandBlockInfo>().Take(2000).ToArray();
                foreach (var lbiId in lbiIds) {
                    if (!_dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) continue;
                    uint lbId = lbiId >> 16;

                    for (uint i = 0; i < lbi.NumCells && i < 100; i++) {
                        uint cellId = (lbId << 16) | (0x0100 + i);
                        if (!_dats.TryGet<EnvCell>(cellId, out var envCell)) continue;

                        if (envCell.EnvironmentId == environmentId &&
                            envCell.CellStructure == cellStructureIndex &&
                            envCell.Surfaces.Count > 0) {
                            return new List<ushort>(envCell.Surfaces);
                        }
                    }
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"[Dungeon] Error finding default surfaces: {ex.Message}");
            }
            return new List<ushort>();
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
            _scene.RefreshFromDocument(_document);
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
            StatusText = $"Saved dungeon LB {_document.LandblockKey:X4} ({_document.Cells.Count} cells) to project. Use File > Export to write DATs.";
            Console.WriteLine($"[DungeonSave] Saved to project: LB {_document.LandblockKey:X4}, {_document.Cells.Count} cells");
        }

        #endregion

        private async Task<uint?> ShowNewDungeonDialog() {
            uint? result = null;
            var textBox = new TextBox {
                Text = "",
                Width = 300,
                Watermark = "Landblock hex ID for new dungeon (e.g. FFFF)"
            };
            var errorText = new TextBlock {
                Text = "",
                Foreground = Brushes.Red,
                FontSize = 12,
                IsVisible = false
            };

            await DialogHost.Show(new StackPanel {
                Margin = new Avalonia.Thickness(20),
                Spacing = 10,
                Children = {
                    new TextBlock {
                        Text = "New Dungeon",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock {
                        Text = "Enter a landblock ID for the new dungeon.",
                        FontSize = 12,
                        Opacity = 0.7
                    },
                    textBox,
                    errorText,
                    new StackPanel {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = {
                            new Button {
                                Content = "Cancel",
                                Command = new RelayCommand(() => DialogHost.Close("DungeonDialogHost"))
                            },
                            new Button {
                                Content = "Create",
                                Command = new RelayCommand(() => {
                                    var parsed = ParseLandblockInput(textBox.Text);
                                    if (parsed != null) {
                                        result = parsed;
                                        DialogHost.Close("DungeonDialogHost");
                                    }
                                    else {
                                        errorText.Text = "Invalid hex ID.";
                                        errorText.IsVisible = true;
                                    }
                                })
                            }
                        }
                    }
                }
            }, "DungeonDialogHost");

            return result;
        }

        [RelayCommand]
        private async Task StartFromTemplate() {
            if (_dats == null || _project == null) return;

            var dungeons = WorldBuilder.Lib.LocationDatabase.Dungeons
                .OrderBy(d => d.Name)
                .Take(200)
                .ToList();

            if (dungeons.Count == 0) {
                StatusText = "No dungeon templates found";
                return;
            }

            var listBox = new ListBox {
                MaxHeight = 280,
                Width = 450,
                FontSize = 12,
                ItemsSource = dungeons.Select(d => $"{d.Name}  [LB {d.LandblockHex}]").ToList()
            };
            var targetTextBox = new TextBox {
                Text = "",
                Width = 120,
                Watermark = "e.g. FFFF"
            };
            var errorText = new TextBlock {
                Text = "",
                Foreground = Brushes.Red,
                FontSize = 12,
                IsVisible = false
            };

            await DialogHost.Show(new StackPanel {
                Margin = new Avalonia.Thickness(20),
                Spacing = 10,
                Children = {
                    new TextBlock {
                        Text = "Start from template",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock {
                        Text = "Pick a dungeon template, then choose the landblock where to create a copy. " +
                            "The structure (rooms, portals, statics) is copied with new locations; portal connections are preserved.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 450,
                        FontSize = 12,
                        Opacity = 0.8
                    },
                    listBox,
                    new StackPanel {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Children = {
                            new TextBlock { Text = "Target landblock:", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                            targetTextBox,
                            errorText
                        }
                    },
                    new StackPanel {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = {
                            new Button {
                                Content = "Cancel",
                                Command = new RelayCommand(() => DialogHost.Close("DungeonDialogHost"))
                            },
                            new Button {
                                Content = "Create copy",
                                Command = new RelayCommand(() => {
                                    var idx = listBox.SelectedIndex;
                                    if (idx < 0 || idx >= dungeons.Count) {
                                        errorText.Text = "Select a template.";
                                        errorText.IsVisible = true;
                                        return;
                                    }
                                    var targetParsed = ParseLandblockInput(targetTextBox.Text);
                                    if (targetParsed == null) {
                                        errorText.Text = "Invalid landblock ID.";
                                        errorText.IsVisible = true;
                                        return;
                                    }
                                    var sourceLb = dungeons[idx].LandblockId;
                                    var targetLb = (ushort)(targetParsed.Value >> 16);
                                    if (targetLb == 0) targetLb = (ushort)(targetParsed.Value & 0xFFFF);
                                    if (sourceLb == targetLb) {
                                        errorText.Text = "Target must differ from template.";
                                        errorText.IsVisible = true;
                                        return;
                                    }
                                    CopyTemplateToLandblock(sourceLb, targetLb);
                                    DialogHost.Close("DungeonDialogHost");
                                })
                            }
                        }
                    }
                }
            }, "DungeonDialogHost");
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
            StatusText = $"Applied surfaces to {updated} cell(s)";
        }

        /// <summary>
        /// Copy a dungeon from source landblock to target landblock.
        /// Creates a new document at target with same structure, positions, and portal connectivity.
        /// </summary>
        private void CopyTemplateToLandblock(ushort sourceLb, ushort targetLb) {
            if (_scene == null || _project == null || _dats == null) return;

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

            // Use document cells for rendering (handles both DAT-loaded and project-saved state)
            if (_document.Cells.Count > 0) {
                _scene.RefreshFromDocument(_document);
                CellCount = _document.Cells.Count;
                StatusText = $"LB {landblockKey:X4}: {CellCount} cells";
                HasDungeon = true;
                _needsCameraFocus = true;
            }
            else if (_scene.LoadLandblock(landblockKey)) {
                uint lbiId = ((uint)landblockKey << 16) | 0xFFFE;
                int numCells = _dats.TryGet<LandBlockInfo>(lbiId, out var lbi) ? (int)lbi.NumCells : 0;
                CellCount = numCells;
                StatusText = $"LB {landblockKey:X4}: {CellCount} cells (read-only, no document data)";
                HasDungeon = true;
                _needsCameraFocus = true;
            }
            else {
                StatusText = $"LB {landblockKey:X4}: No dungeon cells found (not in project or DAT)";
                HasDungeon = false;
                CellCount = 0;
            }
        }

        private async Task<uint?> ShowOpenDungeonDialog() {
            uint? result = null;
            var textBox = new TextBox {
                Text = LandblockInputText,
                Width = 400,
                Watermark = "Search dungeon by name or enter hex ID (e.g. 01D9)"
            };
            var errorText = new TextBlock {
                Text = "",
                Foreground = Brushes.Red,
                FontSize = 12,
                IsVisible = false
            };

            var locationList = new ListBox {
                MaxHeight = 300,
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

                var results = LocationDatabase.Search(query, typeFilter: "Dungeon").Take(50).ToList();
                if (results.Count > 0) {
                    locationList.ItemsSource = results.Select(r => $"{r.Name}  [{r.LandblockHex}]").ToList();
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
                var results = LocationDatabase.Search(query, typeFilter: "Dungeon").Take(50).ToList();
                if (locationList.SelectedIndex < results.Count) {
                    var selected = results[locationList.SelectedIndex];
                    LandblockInputText = selected.Name;
                    result = selected.CellId;
                    DialogHost.Close("DungeonDialogHost");
                }
            };

            await DialogHost.Show(new StackPanel {
                Margin = new Avalonia.Thickness(20),
                Spacing = 10,
                Children = {
                    new TextBlock {
                        Text = "Open Dungeon",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock {
                        Text = "Search by dungeon name or enter a\nlandblock ID in hex (e.g. 01D9).",
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
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = {
                            new Button {
                                Content = "Cancel",
                                Command = new RelayCommand(() => DialogHost.Close("DungeonDialogHost"))
                            },
                            new Button {
                                Content = "Open",
                                Command = new RelayCommand(() => {
                                    if (result != null) {
                                        DialogHost.Close("DungeonDialogHost");
                                        return;
                                    }
                                    var parsed = ParseLandblockInput(textBox.Text);
                                    if (parsed != null) {
                                        LandblockInputText = textBox.Text ?? "";
                                        result = parsed;
                                        DialogHost.Close("DungeonDialogHost");
                                    }
                                    else {
                                        errorText.Text = "Invalid input. Try a dungeon name or hex ID.";
                                        errorText.IsVisible = true;
                                    }
                                })
                            }
                        }
                    }
                }
            }, "DungeonDialogHost");

            return result;
        }

        internal static uint? ParseLandblockInput(string? input) {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();

            var hex = input;
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex[2..];

            if (hex.Length > 4 && hex.Length <= 8) {
                if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fullId))
                    return fullId;
                return null;
            }

            if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lbId))
                return (uint)(lbId << 16);

            return null;
        }

        public void Cleanup() {
            _scene?.Dispose();
            _scene = null;
        }
    }
}
