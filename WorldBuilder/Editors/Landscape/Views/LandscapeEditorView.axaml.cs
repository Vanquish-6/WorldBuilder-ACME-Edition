using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Chorizite.Core.Render;
using Chorizite.OpenGLSDLBackend;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;
using Silk.NET.OpenGL;
using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Input;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Models;
using WorldBuilder.Editors.Landscape.ViewModels;
using WorldBuilder.Views;

namespace WorldBuilder.Editors.Landscape.Views;

public partial class LandscapeEditorView : Base3DView {
    private GL? _gl;
    private IRenderer? _render;
    private bool _didInit;
    private LandscapeEditorViewModel? _viewModel;
    private ToolViewModelBase? _currentActiveTool => _viewModel?.SelectedTool;
    private Avalonia.Controls.Grid? _mainGrid;

    public PixelSize CanvasSize { get; private set; }

    public LandscapeEditorView() : base() {
        InitializeComponent();
        InitializeBase3DView();

        // check if we are in the designer
        if (Design.IsDesignMode) {
            return;
        }

        _viewModel = ProjectManager.Instance.GetProjectService<LandscapeEditorViewModel>()
            ?? throw new Exception("Failed to get LandscapeEditorViewModel");

        DataContext = _viewModel;

        // Cache grid reference on UI thread for later use from render thread
        _mainGrid = this.FindControl<Avalonia.Controls.Grid>("MainGrid");

        // Restore panel widths from settings (on UI thread)
        var uiState = _viewModel?.Settings.Landscape.UIState;
        if (uiState != null && _mainGrid != null) {
            if (uiState.LeftPanelWidth > 0 && _mainGrid.ColumnDefinitions.Count > 0)
                _mainGrid.ColumnDefinitions[0].Width = new Avalonia.Controls.GridLength(uiState.LeftPanelWidth);
            if (uiState.RightPanelWidth > 0 && _mainGrid.ColumnDefinitions.Count > 4)
                _mainGrid.ColumnDefinitions[4].Width = new Avalonia.Controls.GridLength(uiState.RightPanelWidth);
        }
    }

    protected override void OnGlInit(GL gl, PixelSize canvasSize) {
        _gl = gl;
        CanvasSize = canvasSize;
    }

    public void Init(Project project, OpenGLRenderer render) {
        _viewModel?.Init(project, render, CanvasSize);
        _render = render;
    }

    protected override void OnGlResize(PixelSize canvasSize) {
        CanvasSize = canvasSize;
    }

    protected override void OnGlRender(double deltaTime) {
        try {
            // Initialize on first render when project is available
            if (!_didInit && ProjectManager.Instance.CurrentProject?.DocumentManager != null && Renderer != null) {
                Init(ProjectManager.Instance.CurrentProject, Renderer);
                _didInit = true;
            }

            if (!_didInit) return;
            HandleInput(deltaTime);
            _viewModel?.DoRender(CanvasSize);
            RenderToolOverlay();
        }
        catch (Exception ex) {
            Console.WriteLine($"Render error: {ex}");
        }
    }

    private void HandleInput(double deltaTime) {
        if (_viewModel?.TerrainSystem == null) return;

        // Update camera screen size
        _viewModel.TerrainSystem.Scene.CameraManager.Current.ScreenSize = new Vector2(CanvasSize.Width, CanvasSize.Height);

        // Camera switching (Q key)
        HandleCameraSwitching();

        // Camera movement (WASD)
        HandleCameraMovement(deltaTime);

        // Mouse input
        _viewModel.TerrainSystem.Scene.CameraManager.Current.ProcessMouseMovement(InputState.MouseState);

        // Update active tool
        _currentActiveTool?.Update(deltaTime);
    }

    private void HandleCameraSwitching() {
        if (_viewModel?.TerrainSystem == null) return;

        if (_viewModel.InputManager.IsActionTriggered(InputActions.CameraToggleMode, InputState)) {
            if (_viewModel.TerrainSystem.Scene.CameraManager.Current == _viewModel.TerrainSystem.Scene.PerspectiveCamera) {
                _viewModel.TerrainSystem.Scene.CameraManager.SwitchCamera(_viewModel.TerrainSystem.Scene.TopDownCamera);
                Console.WriteLine("Switched to top-down camera");
            }
            else {
                _viewModel.TerrainSystem.Scene.CameraManager.SwitchCamera(_viewModel.TerrainSystem.Scene.PerspectiveCamera);
                Console.WriteLine("Switched to perspective camera");
            }
        }
    }

    private void HandleCameraMovement(double deltaTime) {
        if (_viewModel?.TerrainSystem == null) return;

        var camera = _viewModel.TerrainSystem.Scene.CameraManager.Current;

        // Camera Rotation (Perspective only)
        if (camera is PerspectiveCamera perspCam) {
            float rotateSpeed = 60f * (float)deltaTime; // degrees per second
            if (_viewModel.InputManager.IsActionActive(InputActions.CameraRotateLeft, InputState))
                perspCam.ProcessKeyboardRotation(rotateSpeed, 0);
            if (_viewModel.InputManager.IsActionActive(InputActions.CameraRotateRight, InputState))
                perspCam.ProcessKeyboardRotation(-rotateSpeed, 0);
            if (_viewModel.InputManager.IsActionActive(InputActions.CameraRotateUp, InputState))
                perspCam.ProcessKeyboardRotation(0, rotateSpeed);
            if (_viewModel.InputManager.IsActionActive(InputActions.CameraRotateDown, InputState))
                perspCam.ProcessKeyboardRotation(0, -rotateSpeed);
        }

        // Camera Movement
        if (_viewModel.InputManager.IsActionActive(InputActions.CameraMoveForward, InputState))
            camera.ProcessKeyboard(CameraMovement.Forward, deltaTime);
        if (_viewModel.InputManager.IsActionActive(InputActions.CameraMoveBackward, InputState))
            camera.ProcessKeyboard(CameraMovement.Backward, deltaTime);
        if (_viewModel.InputManager.IsActionActive(InputActions.CameraMoveLeft, InputState))
            camera.ProcessKeyboard(CameraMovement.Left, deltaTime);
        if (_viewModel.InputManager.IsActionActive(InputActions.CameraMoveRight, InputState))
            camera.ProcessKeyboard(CameraMovement.Right, deltaTime);

        // Keyboard zoom
        bool zoomIn = _viewModel.InputManager.IsActionActive(InputActions.CameraZoomIn, InputState);
        bool zoomOut = _viewModel.InputManager.IsActionActive(InputActions.CameraZoomOut, InputState);

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
    }

    private void RenderToolOverlay() {
        if (_render == null || _viewModel?.TerrainSystem == null) return;

        _currentActiveTool?.RenderOverlay(
            _render,
            _viewModel.TerrainSystem.Scene.CameraManager.Current,
            (float)CanvasSize.Width / CanvasSize.Height);
    }

    protected override void OnGlKeyDown(KeyEventArgs e) {
        if (!_didInit || _viewModel?.TerrainSystem == null) return;

        // Go to Landblock
        if (_viewModel.InputManager.MatchesAction(InputActions.NavigationGoToLandblock, e.Key, e.KeyModifiers)) {
            _ = _viewModel.GotoLandblockCommand.ExecuteAsync(null);
        }

        // Undo/Redo
        if (_viewModel.InputManager.MatchesAction(InputActions.EditUndo, e.Key, e.KeyModifiers)) {
            _viewModel.TerrainSystem.History.Undo();
        }
        else if (_viewModel.InputManager.MatchesAction(InputActions.EditRedo, e.Key, e.KeyModifiers) ||
                 _viewModel.InputManager.MatchesAction(InputActions.EditRedoAlternate, e.Key, e.KeyModifiers)) {
            _viewModel.TerrainSystem.History.Redo();
        }

        // Object copy/paste/delete (supports multi-selection)
        var sel = _viewModel.TerrainSystem.EditingContext.ObjectSelection;

        if (_viewModel.InputManager.MatchesAction(InputActions.EditCopy, e.Key, e.KeyModifiers)) {
            if (sel.HasSelection) {
                if (sel.IsMultiSelection) {
                    sel.ClipboardMulti = sel.SelectedEntries
                        .Where(entry => !entry.IsScenery)
                        .Select(entry => entry.Object)
                        .ToList();
                    sel.Clipboard = null;
                    Console.WriteLine($"[Selector] Copied {sel.ClipboardMulti.Count} objects");
                }
                else if (sel.SelectedObject.HasValue) {
                    sel.Clipboard = sel.SelectedObject.Value;
                    sel.ClipboardMulti = null;
                    Console.WriteLine($"[Selector] Copied object 0x{sel.SelectedObject.Value.Id:X8}");
                }
            }
        }

        if (_viewModel.InputManager.MatchesAction(InputActions.EditPaste, e.Key, e.KeyModifiers)) {
            if (sel.ClipboardMulti != null && sel.ClipboardMulti.Count > 0) {
                // Multi-paste: place group relative to their center
                sel.IsPlacementMode = true;
                sel.PlacementPreviewMulti = sel.ClipboardMulti.Select(obj => new WorldBuilder.Shared.Documents.StaticObject {
                    Id = obj.Id,
                    IsSetup = obj.IsSetup,
                    Origin = obj.Origin,
                    Orientation = obj.Orientation,
                    Scale = obj.Scale
                }).ToList();
                sel.PlacementPreview = sel.PlacementPreviewMulti[0];
                Console.WriteLine($"[Selector] Entering multi-placement mode for {sel.ClipboardMulti.Count} objects");
            }
            else if (sel.Clipboard.HasValue) {
                sel.IsPlacementMode = true;
                sel.PlacementPreviewMulti = null;
                sel.PlacementPreview = new WorldBuilder.Shared.Documents.StaticObject {
                    Id = sel.Clipboard.Value.Id,
                    IsSetup = sel.Clipboard.Value.IsSetup,
                    Origin = sel.Clipboard.Value.Origin,
                    Orientation = sel.Clipboard.Value.Orientation,
                    Scale = sel.Clipboard.Value.Scale
                };
                Console.WriteLine($"[Selector] Entering placement mode for 0x{sel.Clipboard.Value.Id:X8}");
            }
        }

        if (_viewModel.InputManager.MatchesAction(InputActions.EditDelete, e.Key, e.KeyModifiers)) {
            if (sel.HasSelection) {
                _ = DeleteSelectedObjectsAsync();
            }
        }

        if (_viewModel.InputManager.MatchesAction(InputActions.EditCancel, e.Key, e.KeyModifiers)) {
            if (sel.IsPlacementMode) {
                sel.IsPlacementMode = false;
                sel.PlacementPreview = null;
                sel.PlacementPreviewMulti = null;
                Console.WriteLine($"[Selector] Cancelled placement mode");
            }
            else if (sel.HasSelection) {
                sel.Deselect();
            }
        }
    }

    protected override void OnGlKeyUp(KeyEventArgs e) {
        if (!_didInit) return;
    }

    protected override void OnGlPointerMoved(PointerEventArgs e, Vector2 mousePositionScaled) {
        if (!_didInit) return;
        _currentActiveTool?.HandleMouseMove(InputState.MouseState);
        UpdateMarqueeOverlay();
    }

    protected override void OnGlPointerWheelChanged(PointerWheelEventArgs e) {
        if (!_didInit || _viewModel?.TerrainSystem == null) return;

        var camera = _viewModel.TerrainSystem.Scene.CameraManager.Current;

        if (camera is PerspectiveCamera perspectiveCamera) {
            perspectiveCamera.ProcessMouseScroll((float)e.Delta.Y);
        }
        else if (camera is OrthographicTopDownCamera orthoCamera) {
            orthoCamera.ProcessMouseScrollAtCursor(
                (float)e.Delta.Y,
                InputState.MouseState.Position,
                new Vector2(CanvasSize.Width, CanvasSize.Height));
        }
    }

    protected override void OnGlPointerPressed(PointerPressedEventArgs e) {
        if (!_didInit) return;

        // Right-click context menu when any object is selected (including scenery)
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed && _viewModel?.TerrainSystem != null) {
            var sel = _viewModel.TerrainSystem.EditingContext.ObjectSelection;

            if (sel.HasSelection) {
                ShowObjectContextMenu(e);
                return; // Don't pass to camera/tool
            }
        }

        _currentActiveTool?.HandleMouseDown(InputState.MouseState);
    }

    protected override void OnGlPointerReleased(PointerReleasedEventArgs e) {
        if (!_didInit) return;
        _currentActiveTool?.HandleMouseUp(InputState.MouseState);
        UpdateMarqueeOverlay();
    }

    protected override void UpdateMouseState(Point position, PointerPointProperties properties) {
        base.UpdateMouseState(position, properties);

        if (!_didInit || _viewModel?.TerrainSystem == null) return;

        // Clamp mouse position to control bounds
        var controlWidth = (int)Bounds.Width;
        var controlHeight = (int)Bounds.Height;
        var clampedPosition = new Point(
            Math.Max(0, Math.Min(controlWidth - 1, position.X)),
            Math.Max(0, Math.Min(controlHeight - 1, position.Y))
        );

        // Update input state with terrain system for raycasting
        InputState.UpdateMouseState(
            clampedPosition,
            properties,
            CanvasSize.Width,
            CanvasSize.Height,
            InputScale,
            _viewModel.TerrainSystem.Scene.CameraManager.Current,
            _viewModel.TerrainSystem); // Changed from TerrainProvider
    }

    private void UpdateMarqueeOverlay() {
        var marqueeRect = this.FindControl<Avalonia.Controls.Border>("MarqueeRect");
        if (marqueeRect == null) return;

        // Get the active select sub-tool
        var selectTool = _viewModel?.SelectedSubTool as ViewModels.SelectSubToolViewModel;
        if (selectTool == null || !selectTool.IsMarqueeActive) {
            marqueeRect.IsVisible = false;
            return;
        }

        var start = selectTool.MarqueeStart;
        var end = selectTool.MarqueeEnd;

        float x = MathF.Min(start.X, end.X);
        float y = MathF.Min(start.Y, end.Y);
        float w = MathF.Abs(end.X - start.X);
        float h = MathF.Abs(end.Y - start.Y);

        marqueeRect.IsVisible = true;
        marqueeRect.Margin = new Avalonia.Thickness(x, y, 0, 0);
        marqueeRect.Width = Math.Max(1, w);
        marqueeRect.Height = Math.Max(1, h);
    }

    private void ShowObjectContextMenu(PointerPressedEventArgs e) {
        if (_viewModel?.TerrainSystem == null) return;

        var sel = _viewModel.TerrainSystem.EditingContext.ObjectSelection;
        bool hasEditableSelection = sel.HasSelection &&
            sel.SelectedEntries.Any(entry => !entry.IsScenery && entry.ObjectIndex >= 0);
        bool hasClipboard = sel.Clipboard.HasValue || (sel.ClipboardMulti != null && sel.ClipboardMulti.Count > 0);

        var menu = new ContextMenu();

        // Copy (available for any selection, including scenery)
        if (sel.HasSelection) {
            var copyItem = new MenuItem { Header = "Copy", InputGesture = _viewModel.InputManager.GetKeyGesture(InputActions.EditCopy) };
            copyItem.Click += (_, _) => {
                if (sel.IsMultiSelection) {
                    sel.ClipboardMulti = sel.SelectedEntries
                        .Select(entry => entry.Object)
                        .ToList();
                    sel.Clipboard = null;
                }
                else if (sel.SelectedObject.HasValue) {
                    sel.Clipboard = sel.SelectedObject.Value;
                    sel.ClipboardMulti = null;
                }
            };
            menu.Items.Add(copyItem);
        }

        // Snap to Terrain (only for non-scenery editable objects)
        if (hasEditableSelection) {
            var snapItem = new MenuItem { Header = "Snap to Terrain" };
            snapItem.Click += (_, _) => {
                var selectTool = _viewModel.Tools
                    .OfType<ViewModels.SelectorToolViewModel>().FirstOrDefault()
                    ?.AllSubTools.OfType<ViewModels.SelectSubToolViewModel>().FirstOrDefault();
                if (selectTool?.SnapToTerrainCommand?.CanExecute(null) == true) {
                    selectTool.SnapToTerrainCommand.Execute(null);
                }
            };
            snapItem.IsEnabled = !sel.IsMultiSelection && !sel.IsScenery;
            menu.Items.Add(snapItem);
        }

        // Paste (available when clipboard has content)
        if (hasClipboard) {
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());

            var pasteItem = new MenuItem { Header = "Paste", InputGesture = _viewModel.InputManager.GetKeyGesture(InputActions.EditPaste) };
            pasteItem.Click += (_, _) => {
                if (sel.ClipboardMulti != null && sel.ClipboardMulti.Count > 0) {
                    sel.IsPlacementMode = true;
                    sel.PlacementPreviewMulti = sel.ClipboardMulti.Select(obj => new StaticObject {
                        Id = obj.Id, IsSetup = obj.IsSetup,
                        Origin = obj.Origin, Orientation = obj.Orientation, Scale = obj.Scale
                    }).ToList();
                    sel.PlacementPreview = sel.PlacementPreviewMulti[0];
                }
                else if (sel.Clipboard.HasValue) {
                    sel.IsPlacementMode = true;
                    sel.PlacementPreviewMulti = null;
                    sel.PlacementPreview = new StaticObject {
                        Id = sel.Clipboard.Value.Id, IsSetup = sel.Clipboard.Value.IsSetup,
                        Origin = sel.Clipboard.Value.Origin, Orientation = sel.Clipboard.Value.Orientation,
                        Scale = sel.Clipboard.Value.Scale
                    };
                }
            };
            menu.Items.Add(pasteItem);
        }

        // Delete (only for non-scenery editable objects)
        if (hasEditableSelection) {
            menu.Items.Add(new Separator());

            var deleteItem = new MenuItem { Header = "Delete", InputGesture = _viewModel.InputManager.GetKeyGesture(InputActions.EditDelete) };
            deleteItem.Click += (_, _) => {
                _ = DeleteSelectedObjectsAsync();
            };
            menu.Items.Add(deleteItem);
        }

        menu.Open(this);
    }

    private async Task DeleteSelectedObjectsAsync() {
        if (_viewModel?.TerrainSystem == null) return;

        var sel = _viewModel.TerrainSystem.EditingContext.ObjectSelection;
        var toDelete = sel.SelectedEntries
            .Where(entry => !entry.IsScenery && entry.ObjectIndex >= 0)
            .OrderByDescending(entry => entry.ObjectIndex)
            .ToList();

        if (toDelete.Count == 0) return;

        var objectLabel = toDelete.Count == 1
            ? $"object 0x{toDelete[0].Object.Id:X8}"
            : $"{toDelete.Count} objects";

        var confirmed = await ShowConfirmationDialog(
            "Delete Object(s)",
            $"Are you sure you want to delete {objectLabel}?");

        if (!confirmed) return;

        var commands = toDelete.Select(entry =>
            (WorldBuilder.Lib.History.ICommand)new WorldBuilder.Editors.Landscape.Commands.RemoveObjectCommand(
                _viewModel.TerrainSystem.EditingContext,
                entry.LandblockKey,
                entry.ObjectIndex)
        ).ToList();

        var compound = new WorldBuilder.Editors.Landscape.Commands.CompoundCommand(
            $"Delete {toDelete.Count} object(s)", commands);
        _viewModel.TerrainSystem.History.ExecuteCommand(compound);
        Console.WriteLine($"[Selector] Deleted {toDelete.Count} object(s)");
    }

    private async Task<bool> ShowConfirmationDialog(string title, string message) {
        bool result = false;
        await DialogHost.Show(new Avalonia.Controls.StackPanel {
            Margin = new Avalonia.Thickness(20),
            Spacing = 15,
            Children = {
                new Avalonia.Controls.TextBlock {
                    Text = title, FontSize = 16, FontWeight = Avalonia.Media.FontWeight.Bold
                },
                new Avalonia.Controls.TextBlock {
                    Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 400
                },
                new Avalonia.Controls.StackPanel {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = {
                        new Avalonia.Controls.Button {
                            Content = "Cancel", Command = new RelayCommand(() => DialogHost.Close("MainDialogHost"))
                        },
                        new Avalonia.Controls.Button {
                            Content = "Delete",
                            Command = new RelayCommand(() => {
                                result = true;
                                DialogHost.Close("MainDialogHost");
                            })
                        }
                    }
                }
            }
        }, "MainDialogHost");
        return result;
    }

    protected override void OnGlDestroy() {
        // Save panel widths before cleanup (use cached grid, read values safely)
        var uiState = _viewModel?.Settings.Landscape.UIState;
        if (uiState != null && _mainGrid != null) {
            try {
                if (_mainGrid.ColumnDefinitions.Count > 0)
                    uiState.LeftPanelWidth = _mainGrid.ColumnDefinitions[0].ActualWidth;
                if (_mainGrid.ColumnDefinitions.Count > 4)
                    uiState.RightPanelWidth = _mainGrid.ColumnDefinitions[4].ActualWidth;
            }
            catch {
                // Column widths may not be accessible from this thread; ignore
            }
        }

        _currentActiveTool?.OnDeactivated();
        _viewModel?.Cleanup();
    }
}