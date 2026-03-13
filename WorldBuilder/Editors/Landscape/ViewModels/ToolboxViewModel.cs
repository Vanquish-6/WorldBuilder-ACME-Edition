using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class ToolboxViewModel : ViewModelBase {
        private readonly LandscapeEditorViewModel _editor;

        public ObservableCollection<ToolViewModelBase> Tools => _editor.Tools;

        public ToolViewModelBase? SelectedTool => _editor.SelectedTool;
        public SubToolViewModelBase? SelectedSubTool => _editor.SelectedSubTool;

        public IRelayCommand SelectToolCommand => _editor.SelectToolCommand;
        public IRelayCommand SelectSubToolCommand => _editor.SelectSubToolCommand;

        /// <summary>Snap object movement/placement to grid (configurable grid size). Wired from editor.</summary>
        public bool SnapToGrid {
            get => _editor.SnapToGrid;
            set => _editor.SnapToGrid = value;
        }

        public ToolboxViewModel(LandscapeEditorViewModel editor) {
            _editor = editor;
            _editor.PropertyChanged += (s, e) => {
                if (e.PropertyName == nameof(LandscapeEditorViewModel.SelectedTool)) {
                    OnPropertyChanged(nameof(SelectedTool));
                }
                if (e.PropertyName == nameof(LandscapeEditorViewModel.SelectedSubTool)) {
                    OnPropertyChanged(nameof(SelectedSubTool));
                }
                if (e.PropertyName == nameof(LandscapeEditorViewModel.SnapToGrid)) {
                    OnPropertyChanged(nameof(SnapToGrid));
                }
            };
        }
    }
}
