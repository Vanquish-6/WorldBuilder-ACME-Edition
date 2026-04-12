using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Chorizite.OpenGLSDLBackend;
using Silk.NET.OpenGL;
using System;
using System.Numerics;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Editors.Landscape.ViewModels;
using WorldBuilder.Editors.ObjectDebug.ViewModels;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Views;

namespace WorldBuilder.Editors.ObjectDebug.Views;

public partial class ObjectDebugView : Base3DView {
    private ObjectDebugViewModel? _vm;
    private GL? _gl;
    private IDatReaderWriter? _dats;

    public PixelSize CanvasSize { get; private set; }

    private StaticObjectManager? _staticObjectManager;

    private PointerPoint? _lastPointerPoint;
    private bool _isRotating = false;

    public ObjectDebugView() {
        InitializeComponent();
        InitializeBase3DView();
        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x1a, 0x1a, 0x1a));
        _vm = new ObjectDebugViewModel();
        DataContext = _vm;
    }

    protected override void OnGlDestroy() {
        _vm?.Dispose();
    }

    protected override void OnGlInit(GL gl, PixelSize canvasSize) {
        _dats = ProjectManager.Instance.CurrentProject.DatReaderWriter;
        _gl = gl;
        CanvasSize = canvasSize;

        var mainSceneManager =
            ProjectManager.Instance.GetProjectService<Editors.Landscape.ViewModels.LandscapeEditorViewModel>()
                ?.TerrainSystem?.Scene?.AnyObjectManager;

        _staticObjectManager = mainSceneManager ?? new StaticObjectManager(Renderer, _dats);
        _vm!.Init(Renderer, _dats, _staticObjectManager, InvalidateVisual);
    }

    protected override void OnGlKeyDown(KeyEventArgs e) { }

    protected override void OnGlKeyUp(KeyEventArgs e) { }

    protected override void OnGlPointerMoved(PointerEventArgs e, Vector2 mousePositionScaled) {
        var point = e.GetCurrentPoint(this);
        if (_isRotating && point.Properties.IsLeftButtonPressed) {
            if (_lastPointerPoint.HasValue) {
                var deltaX = (float)(point.Position.X - _lastPointerPoint.Value.Position.X);
                var deltaY = (float)(point.Position.Y - _lastPointerPoint.Value.Position.Y);
                _vm?.RotateAround(deltaY * 0.5f, -deltaX * 0.5f);
                InvalidateVisual();
            }
            _lastPointerPoint = point;
        }
    }

    protected override void OnGlPointerPressed(PointerPressedEventArgs e) {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed) {
            _isRotating = true;
            _lastPointerPoint = point;
            e.Pointer.Capture(this);
        }
    }

    protected override void OnGlPointerReleased(PointerReleasedEventArgs e) {
        if (_isRotating) {
            _isRotating = false;
            _lastPointerPoint = null;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnGlPointerWheelChanged(PointerWheelEventArgs e) {
        var delta = (float)e.Delta.Y;
        _vm?.Zoom(-delta);
        InvalidateVisual();
    }

    protected override void OnGlRender(double frameTime) {
        _vm?.Render(CanvasSize);
    }

    protected override void OnGlResize(PixelSize canvasSize) {
        CanvasSize = canvasSize;
    }
}
