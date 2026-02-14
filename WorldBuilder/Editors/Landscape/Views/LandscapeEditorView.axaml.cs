using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WorldBuilder.Editors.Landscape.ViewModels;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Docking;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia.Input;
using Avalonia;
using Avalonia.Media;
using System.Linq;

namespace WorldBuilder.Editors.Landscape.Views {
    public partial class LandscapeEditorView : UserControl {
        private LandscapeEditorViewModel? _viewModel;
        private Avalonia.Controls.Grid? _mainGrid;
        private readonly Dictionary<IDockable, Window> _floatingWindows = new();

        private Border? _leftGhost;
        private Border? _rightGhost;
        private Border? _topGhost;
        private Border? _bottomGhost;

        public LandscapeEditorView() {
            InitializeComponent();

            if (Design.IsDesignMode) return;

            _leftGhost = this.FindControl<Border>("LeftGhost");
            _rightGhost = this.FindControl<Border>("RightGhost");
            _topGhost = this.FindControl<Border>("TopGhost");
            _bottomGhost = this.FindControl<Border>("BottomGhost");

            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
            AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);

            _viewModel = ProjectManager.Instance.GetProjectService<LandscapeEditorViewModel>()
                ?? throw new Exception("Failed to get LandscapeEditorViewModel");

            DataContext = _viewModel;

            _mainGrid = this.FindControl<Avalonia.Controls.Grid>("MainGrid");

            // Restore panel widths
            var uiState = _viewModel.Settings.Landscape.UIState;
            if (uiState != null && _mainGrid != null) {
                if (uiState.LeftPanelWidth > 0 && _mainGrid.ColumnDefinitions.Count > 0)
                    _mainGrid.ColumnDefinitions[0].Width = new Avalonia.Controls.GridLength(uiState.LeftPanelWidth);
                if (uiState.RightPanelWidth > 0 && _mainGrid.ColumnDefinitions.Count > 4)
                    _mainGrid.ColumnDefinitions[4].Width = new Avalonia.Controls.GridLength(uiState.RightPanelWidth);
            }

            // Setup floating windows
            if (_viewModel.DockingManager != null) {
                _viewModel.DockingManager.FloatingPanels.CollectionChanged += OnFloatingPanelsChanged;
                // Just in case panels were added before we subscribed (unlikely if Init is called later)
                foreach (var panel in _viewModel.DockingManager.FloatingPanels) {
                    CreateFloatingWindow(panel);
                }
            }

            // Initialize ViewModel if needed
            if (ProjectManager.Instance.CurrentProject != null) {
                // Ensure Init is called. In the old code it was lazy in OnGlRender.
                // We call it here. Use a flag in ViewModel if Init is idempotent or check TerrainSystem.
                if (_viewModel.TerrainSystem == null) {
                    _viewModel.Init(ProjectManager.Instance.CurrentProject);
                }
            }
        }

        private void OnDragOver(object? sender, DragEventArgs e) {
            var id = e.Data.Get("DockablePanelId") as string;
            if (string.IsNullOrEmpty(id)) {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            e.DragEffects = DragDropEffects.Move;

            // Determine drop zone based on position relative to the whole view
            var pos = e.GetPosition(this);
            var bounds = Bounds;

            HideGhosts();

            // Thresholds (25% of dimension, clamped to sensible min/max)
            double w = bounds.Width;
            double h = bounds.Height;
            double xThresh = Math.Clamp(w * 0.25, 100, 400);
            double yThresh = Math.Clamp(h * 0.25, 100, 300);

            double leftThreshold = xThresh;
            double rightThreshold = w - xThresh;
            double topThreshold = yThresh;
            double bottomThreshold = h - yThresh;

            if (pos.X < leftThreshold) {
                if (_leftGhost != null) _leftGhost.IsVisible = true;
            }
            else if (pos.X > rightThreshold) {
                if (_rightGhost != null) _rightGhost.IsVisible = true;
            }
            else if (pos.Y < topThreshold) {
                if (_topGhost != null) _topGhost.IsVisible = true;
            }
            else if (pos.Y > bottomThreshold) {
                 if (_bottomGhost != null) _bottomGhost.IsVisible = true;
            }
        }

        private void OnDragLeave(object? sender, DragEventArgs e) {
            HideGhosts();
        }

        private void OnDrop(object? sender, DragEventArgs e) {
            HideGhosts();
            var id = e.Data.Get("DockablePanelId") as string;
            if (string.IsNullOrEmpty(id)) return;

            if (_viewModel?.DockingManager == null) return;

            var panel = _viewModel.DockingManager.AllPanels.FirstOrDefault(p => p.Id == id);
            if (panel == null) return;

            var pos = e.GetPosition(this);
            var bounds = Bounds;

            double w = bounds.Width;
            double h = bounds.Height;
            double xThresh = Math.Clamp(w * 0.25, 100, 400);
            double yThresh = Math.Clamp(h * 0.25, 100, 300);

            double leftThreshold = xThresh;
            double rightThreshold = w - xThresh;
            double topThreshold = yThresh;
            double bottomThreshold = h - yThresh;

            DockLocation? newLocation = null;

            if (pos.X < leftThreshold) {
                newLocation = DockLocation.Left;
            }
            else if (pos.X > rightThreshold) {
                newLocation = DockLocation.Right;
            }
            else if (pos.Y < topThreshold) {
                newLocation = DockLocation.Top;
            }
            else if (pos.Y > bottomThreshold) {
                newLocation = DockLocation.Bottom;
            }

            if (newLocation.HasValue) {
                 _viewModel.DockingManager.MovePanel(panel, newLocation.Value);
                 panel.IsVisible = true;
            }
            else {
                 // Check if dropped in center zone (not edge)
                 // If not edge, dock to Center
                 _viewModel.DockingManager.MovePanel(panel, DockLocation.Center);
                 panel.IsVisible = true;
            }
        }

        private void HideGhosts() {
            if (_leftGhost != null) _leftGhost.IsVisible = false;
            if (_rightGhost != null) _rightGhost.IsVisible = false;
            if (_topGhost != null) _topGhost.IsVisible = false;
            if (_bottomGhost != null) _bottomGhost.IsVisible = false;
        }

        private void OnFloatingPanelsChanged(object? sender, NotifyCollectionChangedEventArgs e) {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null) {
                foreach (IDockable panel in e.NewItems) {
                    CreateFloatingWindow(panel);
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null) {
                foreach (IDockable panel in e.OldItems) {
                    if (_floatingWindows.TryGetValue(panel, out var window)) {
                        // Remove from map BEFORE closing to prevent Closing event handler from setting IsVisible=false
                        _floatingWindows.Remove(panel);
                        window.Close();
                    }
                }
            }
             else if (e.Action == NotifyCollectionChangedAction.Reset) {
                 foreach(var window in _floatingWindows.Values) {
                     window.Close();
                 }
                 _floatingWindows.Clear();
             }
        }

        private void CreateFloatingWindow(IDockable panel) {
            if (_floatingWindows.ContainsKey(panel)) return;

            var window = new Window {
                Title = panel.Title,
                Content = panel,
                Width = 300,
                Height = 400,
                ShowInTaskbar = true,
                SystemDecorations = SystemDecorations.BorderOnly,
                ExtendClientAreaToDecorationsHint = true,
                ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
                ExtendClientAreaTitleBarHeightHint = 0
            };

            // Support dragging from floating window to main window
            // The content is the VM. When data template is applied, it will use DockablePanelView.
            // DockablePanelView handles the Drag logic.
            // Since the floating window is a separate top-level window, we need to ensure DragDrop works across windows.
            // Avalonia handles this natively if DragDrop is initiated correctly.

            window.Closing += (s, e) => {
                if (_floatingWindows.ContainsKey(panel)) {
                    _floatingWindows.Remove(panel);
                }
                if (panel.IsVisible) {
                    panel.IsVisible = false;
                }
            };

            window.Show();
            _floatingWindows[panel] = window;
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e) {
            base.OnDetachedFromVisualTree(e);

            foreach(var window in _floatingWindows.Values) {
                window.Close();
            }
            _floatingWindows.Clear();

            // Save panel widths
            var uiState = _viewModel?.Settings.Landscape.UIState;
            if (uiState != null && _mainGrid != null) {
                try {
                    if (_mainGrid.ColumnDefinitions.Count > 0)
                        uiState.LeftPanelWidth = _mainGrid.ColumnDefinitions[0].ActualWidth;
                    if (_mainGrid.ColumnDefinitions.Count > 4)
                        uiState.RightPanelWidth = _mainGrid.ColumnDefinitions[4].ActualWidth;
                }
                catch { }
            }

            _viewModel?.Cleanup();
        }
    }
}
