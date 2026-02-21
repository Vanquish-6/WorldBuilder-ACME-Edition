using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.ComponentModel;
using WorldBuilder.Editors.Landscape.ViewModels;

namespace WorldBuilder.Editors.Landscape.Views;

public partial class KeyboardMappingView : UserControl {
    public KeyboardMappingView() {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e) {
        if (DataContext is KeyboardMappingViewModel vm) {
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(KeyboardMappingViewModel.IsListening)) {
            if (DataContext is KeyboardMappingViewModel vm && vm.IsListening) {
                this.Focus();
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        // If listening for a rebind, capture all keys
        if (DataContext is KeyboardMappingViewModel vm && vm.IsListening) {
            e.Handled = true;
            vm.HandleKeyPress(e.Key, e.KeyModifiers);
        }
        else {
            base.OnKeyDown(e);
        }
    }
}
