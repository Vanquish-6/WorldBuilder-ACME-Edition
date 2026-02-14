using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace WorldBuilder.Lib.Docking {
    public partial class DockingManager : ObservableObject {
        private readonly List<IDockable> _allPanels = new();

        public ObservableCollection<IDockable> LeftPanels { get; } = new();
        public ObservableCollection<IDockable> RightPanels { get; } = new();
        public ObservableCollection<IDockable> TopPanels { get; } = new();
        public ObservableCollection<IDockable> BottomPanels { get; } = new();
        public ObservableCollection<IDockable> FloatingPanels { get; } = new();

        public IEnumerable<IDockable> AllPanels => _allPanels;

        public void RegisterPanel(IDockable panel) {
            if (_allPanels.Any(p => p.Id == panel.Id)) return;
            _allPanels.Add(panel);
            UpdatePanelLocation(panel);
        }

        public void UnregisterPanel(IDockable panel) {
            _allPanels.Remove(panel);
            RemoveFromCollections(panel);
        }

        public void UpdatePanelLocation(IDockable panel) {
            RemoveFromCollections(panel);

            if (!panel.IsVisible) return;

            switch (panel.Location) {
                case DockLocation.Left:
                    LeftPanels.Add(panel);
                    break;
                case DockLocation.Right:
                    RightPanels.Add(panel);
                    break;
                case DockLocation.Top:
                    TopPanels.Add(panel);
                    break;
                case DockLocation.Bottom:
                    BottomPanels.Add(panel);
                    break;
                case DockLocation.Floating:
                    FloatingPanels.Add(panel);
                    break;
            }
        }

        private void RemoveFromCollections(IDockable panel) {
            LeftPanels.Remove(panel);
            RightPanels.Remove(panel);
            TopPanels.Remove(panel);
            BottomPanels.Remove(panel);
            FloatingPanels.Remove(panel);
        }

        [RelayCommand]
        public void TogglePanelVisibility(string panelId) {
            var panel = _allPanels.FirstOrDefault(p => p.Id == panelId);
            if (panel != null) {
                panel.IsVisible = !panel.IsVisible;
                UpdatePanelLocation(panel);
            }
        }

        public void MovePanel(IDockable panel, DockLocation location) {
            panel.Location = location;
            UpdatePanelLocation(panel);
        }
    }
}
