using Avalonia;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Chorizite.OpenGLSDLBackend;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Editors.ObjectDebug.ViewModels;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Views;

namespace WorldBuilder.Editors.Monster.Views {

    /// <summary>
    /// 3D preview viewport for the Monster Creator. Unlike WeenieSetupPreviewView,
    /// this always creates a dedicated StaticObjectManager so texture remapping
    /// (applied via TextureOverrides) does not affect any shared landscape renderer.
    /// </summary>
    public partial class MonsterPreviewView : Base3DView {

        public static readonly StyledProperty<uint> SetupDidProperty =
            AvaloniaProperty.Register<MonsterPreviewView, uint>(nameof(SetupDid));

        public static readonly StyledProperty<Dictionary<uint, uint>?> TextureOverridesProperty =
            AvaloniaProperty.Register<MonsterPreviewView, Dictionary<uint, uint>?>(nameof(TextureOverrides));

        public static readonly StyledProperty<HashSet<int>?> HiddenPartIndicesProperty =
            AvaloniaProperty.Register<MonsterPreviewView, HashSet<int>?>(nameof(HiddenPartIndices));

        public static readonly StyledProperty<Dictionary<int, uint>?> GfxObjRemappingProperty =
            AvaloniaProperty.Register<MonsterPreviewView, Dictionary<int, uint>?>(nameof(GfxObjRemapping));

        public static readonly StyledProperty<DatReaderWriter.Types.ColorARGB[]?> CreaturePaletteProperty =
            AvaloniaProperty.Register<MonsterPreviewView, DatReaderWriter.Types.ColorARGB[]?>(nameof(CreaturePalette));

        public uint SetupDid {
            get => GetValue(SetupDidProperty);
            set => SetValue(SetupDidProperty, value);
        }

        public Dictionary<uint, uint>? TextureOverrides {
            get => GetValue(TextureOverridesProperty);
            set => SetValue(TextureOverridesProperty, value);
        }

        public HashSet<int>? HiddenPartIndices {
            get => GetValue(HiddenPartIndicesProperty);
            set => SetValue(HiddenPartIndicesProperty, value);
        }

        public Dictionary<int, uint>? GfxObjRemapping {
            get => GetValue(GfxObjRemappingProperty);
            set => SetValue(GfxObjRemappingProperty, value);
        }

        public DatReaderWriter.Types.ColorARGB[]? CreaturePalette {
            get => GetValue(CreaturePaletteProperty);
            set => SetValue(CreaturePaletteProperty, value);
        }

        private ObjectDebugViewModel? _vm;
        private IDatReaderWriter? _dats;
        private StaticObjectManager? _staticObjectManager;
        private PointerPoint? _lastPointerPoint;
        private bool _isRotating;
        private volatile uint _cachedSetupDid;
        /// <summary>Cached copy of TextureOverrides safe to read from the GL render thread.</summary>
        private Dictionary<uint, uint>? _cachedTextureOverrides;
        /// <summary>Cached copy of HiddenPartIndices safe to read from the GL render thread.</summary>
        private HashSet<int>? _cachedHiddenPartIndices;
        private Dictionary<int, uint>? _cachedGfxObjRemapping;
        private DatReaderWriter.Types.ColorARGB[]? _cachedCreaturePalette;

        static MonsterPreviewView() {
            SetupDidProperty.Changed.AddClassHandler<MonsterPreviewView>((v, _) => v.OnSetupDidChanged());
            TextureOverridesProperty.Changed.AddClassHandler<MonsterPreviewView>((v, _) => v.OnTextureOverridesChanged());
            HiddenPartIndicesProperty.Changed.AddClassHandler<MonsterPreviewView>((v, _) => v.OnHiddenPartIndicesChanged());
            GfxObjRemappingProperty.Changed.AddClassHandler<MonsterPreviewView>((v, _) => v.OnGfxObjRemappingChanged());
            CreaturePaletteProperty.Changed.AddClassHandler<MonsterPreviewView>((v, _) => v.OnCreaturePaletteChanged());
        }

        public MonsterPreviewView() {
            InitializeComponent();
            InitializeBase3DView();
            _vm = new ObjectDebugViewModel();
        }

        void OnSetupDidChanged() {
            _cachedSetupDid = SetupDid;
            ApplySetupDid(_cachedSetupDid);
        }

        void OnTextureOverridesChanged() {
            _cachedTextureOverrides = TextureOverrides;
            ApplyAllOverrides();
        }

        void OnHiddenPartIndicesChanged() {
            _cachedHiddenPartIndices = HiddenPartIndices;
            ApplyAllOverrides();
        }

        void OnGfxObjRemappingChanged() {
            _cachedGfxObjRemapping = GfxObjRemapping;
            ApplyAllOverrides();
        }

        void OnCreaturePaletteChanged() {
            _cachedCreaturePalette = CreaturePalette;
            if (_staticObjectManager != null)
                _staticObjectManager.CreaturePalette = _cachedCreaturePalette;
            ApplyAllOverrides();
        }

        void ApplyAllOverrides() {
            if (_staticObjectManager == null) return;
            _staticObjectManager.TextureRemapping = _cachedTextureOverrides;
            _staticObjectManager.HiddenPartIndices = _cachedHiddenPartIndices;
            _staticObjectManager.GfxObjRemapping = _cachedGfxObjRemapping;
            _staticObjectManager.CreaturePalette = _cachedCreaturePalette;
            _staticObjectManager.ClearAll();
            ApplySetupDid(_cachedSetupDid);
        }

        void ApplySetupDid(uint did) {
            if (_vm == null) return;
            if (did == 0)
                _vm.RequestClearPreview();
            else
                _vm.RequestPreviewSetup(did);
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                InvalidateVisual();
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        }

        protected override void OnGlDestroy() {
            _vm?.RequestClearPreview();
            _vm?.Dispose();
        }

        void ThreadSafeInvalidate() {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                InvalidateVisual();
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        }

        protected override void OnGlInit(GL gl, PixelSize canvasSize) {
            _dats = ProjectManager.Instance.CurrentProject?.DatReaderWriter;
            CanvasSize = canvasSize;

            // Always use a dedicated manager — never share with the landscape editor
            // so TextureRemapping only affects this preview viewport.
            _staticObjectManager = new StaticObjectManager(Renderer, _dats!);
            _staticObjectManager.TextureRemapping = _cachedTextureOverrides;
            _staticObjectManager.HiddenPartIndices = _cachedHiddenPartIndices;
            _staticObjectManager.GfxObjRemapping = _cachedGfxObjRemapping;
            _staticObjectManager.CreaturePalette = _cachedCreaturePalette;

            // Wire custom-texture resolver so imported textures show immediately
            // without needing a DAT export first.
            try {
                var textureImport = ProjectManager.Instance.GetProjectService<WorldBuilder.Services.TextureImportService>();
                _staticObjectManager.CustomTextureResolver = (surfTexId) => {
                    var entry = textureImport.Store.GetDungeonSurfaces()
                        .FirstOrDefault(e => e.SurfaceTextureGid == surfTexId);
                    if (entry == null) return null;
                    var rgba = textureImport.LoadTextureRgba(entry, entry.Width, entry.Height);
                    return rgba != null ? (rgba, entry.Width, entry.Height) : null;
                };
            }
            catch { }

            _vm!.Init(Renderer, _dats!, _staticObjectManager, ThreadSafeInvalidate);
            ApplySetupDid(_cachedSetupDid);
        }

        protected override void OnGlKeyDown(KeyEventArgs e) { }
        protected override void OnGlKeyUp(KeyEventArgs e) { }

        // Override Base3DView's guarded pointer methods to bypass the _isPointerOverViewport
        // check (which relies on PointerEntered on a background-less Panel, never fires reliably
        // when the view is hosted in a nested Grid/DockPanel hierarchy).
        // MonsterPreviewView routes pointer events directly to the OnGl* handlers instead.

        protected override void OnPointerPressed(PointerPressedEventArgs e) {
            if (!IsEffectivelyVisible || !IsEnabled) return;
            this.Focus();
            try { OnGlPointerPressed(e); e.Handled = true; }
            catch { }
        }

        protected override void OnPointerMoved(PointerEventArgs e) {
            if (!IsEffectivelyVisible || !IsEnabled) return;
            try {
                var pos = e.GetPosition(this);
                OnGlPointerMoved(e, new System.Numerics.Vector2((float)pos.X, (float)pos.Y));
            }
            catch { }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e) {
            if (!IsEffectivelyVisible || !IsEnabled) return;
            try { OnGlPointerReleased(e); }
            catch { }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
            if (!IsEffectivelyVisible || !IsEnabled) return;
            try { OnGlPointerWheelChanged(e); e.Handled = true; }
            catch { }
        }

        protected override void OnGlPointerMoved(PointerEventArgs e, Vector2 mousePositionScaled) {
            var point = e.GetCurrentPoint(this);
            if (_isRotating && point.Properties.IsLeftButtonPressed) {
                if (_lastPointerPoint.HasValue) {
                    var dx = (float)(point.Position.X - _lastPointerPoint.Value.Position.X);
                    var dy = (float)(point.Position.Y - _lastPointerPoint.Value.Position.Y);
                    _vm?.RotateAround(dy * 0.5f, -dx * 0.5f);
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
            _vm?.Zoom(-(float)e.Delta.Y);
            InvalidateVisual();
        }

        protected override void OnGlRender(double frameTime) {
            _vm?.Render(CanvasSize);
        }

        public PixelSize CanvasSize { get; private set; }

        protected override void OnGlResize(PixelSize canvasSize) => CanvasSize = canvasSize;

        void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
