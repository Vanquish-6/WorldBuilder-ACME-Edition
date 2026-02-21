using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WorldBuilder.Editors.Landscape.ViewModels;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Docking;
using WorldBuilder.Lib.Input;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Views;

namespace WorldBuilder.ViewModels;

public partial class MainViewModel : ViewModelBase {
    private readonly WorldBuilderSettings _settings;
    private readonly InputManager _inputManager;

    private bool _settingsOpen;

    public KeyGesture? ExitGesture => _inputManager.GetKeyGesture(InputActions.AppExit);
    public KeyGesture? GotoLandblockGesture => _inputManager.GetKeyGesture(InputActions.NavigationGoToLandblock);

    // We expose a collection of dockable panels for the Windows menu
    public IEnumerable<IDockable> DockingPanels {
        get {
            var landscapeEditor = ProjectManager.Instance.GetProjectService<LandscapeEditorViewModel>();
            if (landscapeEditor != null) {
                return landscapeEditor.DockingManager.AllPanels;
            }
            return new List<IDockable>();
        }
    }

    public MainViewModel() {
        _settings = new WorldBuilderSettings();
        _inputManager = new InputManager(_settings);
    }

    public MainViewModel(WorldBuilderSettings settings) {
        _settings = settings;
        _inputManager = new InputManager(_settings);
    }

    [RelayCommand]
    private void TogglePanelVisibility(object? parameter) {
        if (parameter is string id) {
             var landscapeEditor = ProjectManager.Instance.GetProjectService<LandscapeEditorViewModel>();
             if (landscapeEditor != null) {
                 landscapeEditor.DockingManager.TogglePanelVisibility(id);
             }
        }
    }

    [RelayCommand]
    private void Exit() {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            desktop.Shutdown();
        }
    }

    [RelayCommand]
    private void OpenSettingsWindow() {
        if (_settingsOpen) return;

        var settingsWindow = new SettingsWindow {
            DataContext = _settings
        };

        settingsWindow.Closed += (s, e) => {
            _settingsOpen = false;
        };

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            settingsWindow.Show();
            _settingsOpen = true;
        }
        else {
            throw new Exception("Unable to open settings window");
        }
    }

    [RelayCommand]
    private async Task GotoLandblock() {
        var landscapeEditor = ProjectManager.Instance.GetProjectService<LandscapeEditorViewModel>();
        if (landscapeEditor != null) {
            await landscapeEditor.GotoLandblockCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task OpenExportDatsWindow() {

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            if (desktop.MainWindow == null) throw new Exception("Unable to open export DATs window, main window is null.");

            var project = ProjectManager.Instance.CurrentProject
                ?? throw new Exception("No project open, cannot export DATs.");
            var viewModel = new ExportDatsWindowViewModel(_settings, project, desktop.MainWindow);

            var exportWindow = new ExportDatsWindow();
            exportWindow.DataContext = new ExportDatsWindowViewModel(_settings, project, exportWindow);

            await exportWindow.ShowDialog(desktop.MainWindow);
        }
        else {
            throw new Exception("Unable to open settings window");
        }
    }

    [RelayCommand]
    private void OpenKeyboardShortcuts() {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
             var vm = new KeyboardMappingViewModel(_inputManager, _settings);
             var window = new KeyboardMappingWindow {
                 DataContext = vm
             };
             window.Show(desktop.MainWindow);
        }
    }
}
