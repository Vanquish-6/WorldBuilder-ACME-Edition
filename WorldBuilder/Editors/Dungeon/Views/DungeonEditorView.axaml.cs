using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Docking;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace WorldBuilder.Editors.Dungeon.Views {
    public partial class DungeonEditorView : UserControl {
        private DungeonEditorViewModel? _viewModel;
        private DungeonGraphView? _graphView;
        private Avalonia.Controls.Grid? _mainGrid;
        private readonly Dictionary<IDockable, Window> _floatingWindows = new();

        private Border? _leftGhost;
        private Border? _rightGhost;
        private Border? _topGhost;
        private Border? _bottomGhost;

        public DungeonEditorView() {
            InitializeComponent();

            if (Design.IsDesignMode) return;

            _leftGhost = this.FindControl<Border>("LeftGhost");
            _rightGhost = this.FindControl<Border>("RightGhost");
            _topGhost = this.FindControl<Border>("TopGhost");
            _bottomGhost = this.FindControl<Border>("BottomGhost");

            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
            AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);

            _viewModel = ProjectManager.Instance.GetProjectService<DungeonEditorViewModel>()
                ?? throw new Exception("Failed to get DungeonEditorViewModel");

            DataContext = _viewModel;

            _mainGrid = this.FindControl<Avalonia.Controls.Grid>("MainGrid");

            var uiState = _viewModel.Settings.Dungeon.UIState;
            if (uiState != null && _mainGrid != null) {
                if (uiState.LeftPanelWidth > 0 && _mainGrid.ColumnDefinitions.Count > 0)
                    _mainGrid.ColumnDefinitions[0].Width = new GridLength(uiState.LeftPanelWidth);
                if (uiState.RightPanelWidth > 0 && _mainGrid.ColumnDefinitions.Count > 4)
                    _mainGrid.ColumnDefinitions[4].Width = new GridLength(uiState.RightPanelWidth);
            }

            if (_viewModel.DockingManager != null) {
                _viewModel.DockingManager.FloatingPanels.CollectionChanged += OnFloatingPanelsChanged;
                foreach (var panel in _viewModel.DockingManager.FloatingPanels) {
                    CreateFloatingWindow(panel);
                }
            }

            _graphView = this.FindControl<DungeonGraphView>("DungeonGraph");
            if (_graphView != null) {
                _graphView.DataContext = _viewModel;
                _viewModel.DungeonChanged += (s, e) => RefreshGraph();
                _viewModel.PropertyChanged += (s, e) => {
                    if (e.PropertyName == nameof(DungeonEditorViewModel.HasSelectedCell))
                        RefreshGraph();
                };
            }

            if (ProjectManager.Instance.CurrentProject != null) {
                _viewModel.Init(ProjectManager.Instance.CurrentProject);
            }
        }

        private void RefreshGraph() {
            _graphView?.Refresh(_viewModel?.GetCurrentDocument(), _viewModel?.GetSelectedCellNumber());
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
            base.OnAttachedToVisualTree(e);
            var topLevel = TopLevel.GetTopLevel(this);
            topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
            var topLevel = TopLevel.GetTopLevel(this);
            topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
            base.OnDetachedFromVisualTree(e);

            if (_viewModel?.DockingManager != null) {
                _viewModel.DockingManager.FloatingPanels.CollectionChanged -= OnFloatingPanelsChanged;
            }

            foreach (var window in _floatingWindows.Values) window.Close();
            _floatingWindows.Clear();

            if (_viewModel != null && _mainGrid != null) {
                var uiState = _viewModel.Settings.Dungeon.UIState;
                if (_mainGrid.ColumnDefinitions.Count > 0) {
                    var w = _mainGrid.ColumnDefinitions[0].Width.Value;
                    if (double.IsFinite(w) && w > 0) uiState.LeftPanelWidth = w;
                }
                if (_mainGrid.ColumnDefinitions.Count > 4) {
                    var w = _mainGrid.ColumnDefinitions[4].Width.Value;
                    if (double.IsFinite(w) && w > 0) uiState.RightPanelWidth = w;
                }
            }

            _viewModel?.Cleanup();
        }

        private void OnTopLevelKeyDown(object? sender, KeyEventArgs e) {
            if (_viewModel == null || !IsEffectivelyVisible) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
            if (focused is TextBox) return;

            if (e.Key == Key.C) {
                _viewModel.CopySelectedCells();
                e.Handled = true;
            }
            else if (e.Key == Key.V) {
                _viewModel.PasteCells();
                e.Handled = true;
            }
        }

        private void OnDragOver(object? sender, DragEventArgs e) {
            var id = e.Data.Get("DockablePanelId") as string;
            if (string.IsNullOrEmpty(id)) { e.DragEffects = DragDropEffects.None; return; }
            e.DragEffects = DragDropEffects.Move;

            var pos = e.GetPosition(this);
            var bounds = Bounds;
            HideGhosts();

            double w = bounds.Width, h = bounds.Height;
            double xThresh = Math.Clamp(w * 0.25, 100, 400);
            double yThresh = Math.Clamp(h * 0.25, 100, 300);

            if (pos.X < xThresh && _leftGhost != null) _leftGhost.IsVisible = true;
            else if (pos.X > w - xThresh && _rightGhost != null) _rightGhost.IsVisible = true;
            else if (pos.Y < yThresh && _topGhost != null) _topGhost.IsVisible = true;
            else if (pos.Y > h - yThresh && _bottomGhost != null) _bottomGhost.IsVisible = true;
        }

        private void OnDragLeave(object? sender, DragEventArgs e) => HideGhosts();

        private void OnDrop(object? sender, DragEventArgs e) {
            HideGhosts();
            var id = e.Data.Get("DockablePanelId") as string;
            if (string.IsNullOrEmpty(id) || _viewModel?.DockingManager == null) return;

            var panel = _viewModel.DockingManager.AllPanels.FirstOrDefault(p => p.Id == id);
            if (panel == null) return;

            var pos = e.GetPosition(this);
            var bounds = Bounds;
            double w = bounds.Width, h = bounds.Height;
            double xThresh = Math.Clamp(w * 0.25, 100, 400);
            double yThresh = Math.Clamp(h * 0.25, 100, 300);

            DockLocation? newLocation = null;
            if (pos.X < xThresh) newLocation = DockLocation.Left;
            else if (pos.X > w - xThresh) newLocation = DockLocation.Right;
            else if (pos.Y < yThresh) newLocation = DockLocation.Top;
            else if (pos.Y > h - yThresh) newLocation = DockLocation.Bottom;

            if (newLocation.HasValue) {
                _viewModel.DockingManager.MovePanel(panel, newLocation.Value);
                panel.IsVisible = true;
            }
            else {
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
                foreach (IDockable panel in e.NewItems) CreateFloatingWindow(panel);
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null) {
                foreach (IDockable panel in e.OldItems) {
                    if (_floatingWindows.TryGetValue(panel, out var window)) {
                        _floatingWindows.Remove(panel);
                        window.Close();
                    }
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Reset) {
                foreach (var window in _floatingWindows.Values) window.Close();
                _floatingWindows.Clear();
            }
        }

        private void CreateFloatingWindow(IDockable panel) {
            if (_floatingWindows.ContainsKey(panel)) return;
            var window = new Window {
                Title = panel.Title,
                Content = panel,
                Width = 300, Height = 400,
                ShowInTaskbar = true,
                SystemDecorations = SystemDecorations.BorderOnly,
                ExtendClientAreaToDecorationsHint = true,
                ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
                ExtendClientAreaTitleBarHeightHint = 0
            };
            window.Closing += (s, e) => {
                if (_floatingWindows.ContainsKey(panel)) {
                    _floatingWindows.Remove(panel);
                    panel.IsVisible = false;
                }
            };
            window.Show();
            _floatingWindows[panel] = window;
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
