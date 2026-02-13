using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WorldBuilder.Editors.Landscape.ViewModels;
using WorldBuilder.Lib;
using System;

namespace WorldBuilder.Editors.Landscape.Views {
    public partial class LandscapeEditorView : UserControl {
        private LandscapeEditorViewModel? _viewModel;
        private Avalonia.Controls.Grid? _mainGrid;

        public LandscapeEditorView() {
            InitializeComponent();

            if (Design.IsDesignMode) return;

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

            // Initialize ViewModel if needed
            if (ProjectManager.Instance.CurrentProject != null) {
                // Ensure Init is called. In the old code it was lazy in OnGlRender.
                // We call it here. Use a flag in ViewModel if Init is idempotent or check TerrainSystem.
                if (_viewModel.TerrainSystem == null) {
                    _viewModel.Init(ProjectManager.Instance.CurrentProject);
                }
            }
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e) {
            base.OnDetachedFromVisualTree(e);

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
