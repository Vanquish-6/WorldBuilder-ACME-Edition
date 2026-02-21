using CommunityToolkit.Mvvm.ComponentModel;
using Chorizite.OpenGLSDLBackend;
using WorldBuilder.Lib;
using System;
using Avalonia;
using Avalonia.Input;
using System.Numerics;
using WorldBuilder.Editors.Landscape;

namespace WorldBuilder.ViewModels {
    public partial class ViewportViewModel : ViewModelBase {
        [ObservableProperty]
        private string _title = "Viewport";

        [ObservableProperty]
        private bool _isActive;

        [ObservableProperty]
        private ICamera _camera;

        public TerrainSystem? TerrainSystem { get; set; }

        private OpenGLRenderer? _renderer;
        public OpenGLRenderer? Renderer {
            get => _renderer;
            set {
                if (SetProperty(ref _renderer, value)) {
                    OnRendererChanged?.Invoke(this, value);
                }
            }
        }

        public event EventHandler<OpenGLRenderer?>? OnRendererChanged;

        // Actions provided by the parent ViewModel to handle rendering and input
        public Action<double, PixelSize, AvaloniaInputState>? RenderAction { get; set; }
        public Action<PixelSize>? ResizeAction { get; set; }

        // Input actions
        public Action<KeyEventArgs, bool>? KeyAction { get; set; } // isDown
        public Action<PointerEventArgs, Vector2, AvaloniaInputState>? PointerMovedAction { get; set; }
        public Action<PointerWheelEventArgs, AvaloniaInputState>? PointerWheelAction { get; set; }
        public Action<PointerPressedEventArgs, AvaloniaInputState>? PointerPressedAction { get; set; }
        public Action<PointerReleasedEventArgs, AvaloniaInputState>? PointerReleasedAction { get; set; }

        public ViewportViewModel(ICamera camera) {
            _camera = camera;
        }

        public void SetActive(bool active) {
            IsActive = active;
        }
    }
}
