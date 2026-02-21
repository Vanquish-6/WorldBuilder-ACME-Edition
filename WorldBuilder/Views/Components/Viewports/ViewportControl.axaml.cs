using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Silk.NET.OpenGL;
using System;
using System.Numerics;
using WorldBuilder.Lib;
using WorldBuilder.ViewModels;
using WorldBuilder.Editors.Landscape;

namespace WorldBuilder.Views.Components.Viewports {
    public partial class ViewportControl : Base3DView {
        private ViewportViewModel? _viewModel;
        private bool _didInit;

        public ViewportControl() {
            InitializeComponent();
            InitializeBase3DView();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnDataContextChanged(EventArgs e) {
            base.OnDataContextChanged(e);
            _viewModel = DataContext as ViewportViewModel;
        }

        protected override void OnGlInit(GL gl, PixelSize canvasSize) {
            if (_viewModel != null) {
                // Dispatch update to UI thread as it triggers PropertyChanged
                Dispatcher.UIThread.Post(() => {
                    if (_viewModel != null) _viewModel.Renderer = Renderer;
                });
                _didInit = true;
            }
        }

        protected override void OnGlRender(double deltaTime) {
            if (!_didInit || _viewModel == null) return;

            // Re-set Renderer if needed (e.g. context loss/recreation)
            // Use local check to avoid cross-thread property read issues if any
            // Actually reading Renderer property from ViewModel (which is ObservableObject)
            // might not be thread safe if it raises events on read (it doesn't).
            // But writing definitely needs Dispatcher.
            // For safety, we can just skip the check and set it if we suspect it changed,
            // or trust OnGlInit handled it.
            // Context loss usually triggers OnGlInit again.
            // So we rely on OnGlInit.

            _viewModel.RenderAction?.Invoke(deltaTime, new PixelSize((int)Bounds.Width, (int)Bounds.Height), InputState);
        }

        protected override void OnGlResize(PixelSize canvasSize) {
            _viewModel?.ResizeAction?.Invoke(canvasSize);
        }

        protected override void OnGlDestroy() {
            if (_viewModel != null) {
                Dispatcher.UIThread.Post(() => {
                    if (_viewModel != null) _viewModel.Renderer = null;
                });
            }
        }

        protected override void OnGlKeyDown(KeyEventArgs e) {
            _viewModel?.KeyAction?.Invoke(e, true);
        }

        protected override void OnGlKeyUp(KeyEventArgs e) {
            _viewModel?.KeyAction?.Invoke(e, false);
        }

        protected override void OnGlPointerMoved(PointerEventArgs e, Vector2 mousePositionScaled) {
             _viewModel?.PointerMovedAction?.Invoke(e, mousePositionScaled, InputState);
        }

        protected override void OnGlPointerWheelChanged(PointerWheelEventArgs e) {
            _viewModel?.PointerWheelAction?.Invoke(e, InputState);
        }

        protected override void OnGlPointerPressed(PointerPressedEventArgs e) {
            // Force update mouse state with pressed button flags before invoking command
            UpdateMouseState(e.GetPosition(this), e.GetCurrentPoint(this).Properties);
            _viewModel?.PointerPressedAction?.Invoke(e, InputState);
            e.Pointer.Capture(this);
        }

        protected override void OnGlPointerReleased(PointerReleasedEventArgs e) {
            UpdateMouseState(e.GetPosition(this), e.GetCurrentPoint(this).Properties);
            _viewModel?.PointerReleasedAction?.Invoke(e, InputState);
            e.Pointer.Capture(null);
        }

        protected override void UpdateMouseState(Point position, PointerPointProperties properties) {
            if (_viewModel?.TerrainSystem == null || _viewModel.Camera == null) return;

            var scale = InputScale;
            if (scale == Vector2.Zero) scale = Vector2.One;

            var width = (int)(Bounds.Width * scale.X);
            var height = (int)(Bounds.Height * scale.Y);
            if (width == 0) width = (int)Bounds.Width;
            if (height == 0) height = (int)Bounds.Height;

            InputState.UpdateMouseState(
                position,
                properties,
                width,
                height,
                scale,
                _viewModel.Camera,
                _viewModel.TerrainSystem
            );
        }
    }
}
