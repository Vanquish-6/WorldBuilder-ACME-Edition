using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using WorldBuilder.Editors.Dungeon;
using WorldBuilder.Editors.Landscape.ViewModels;
using WorldBuilder.Editors.CharGen;
using WorldBuilder.Editors.Experience;
using WorldBuilder.Editors.Skill;
using WorldBuilder.Editors.Spell;
using WorldBuilder.Editors.SpellSet;
using WorldBuilder.Editors.Vital;
using WorldBuilder.Editors.Layout;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Docking;
using WorldBuilder.Lib.Input;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.Views;

namespace WorldBuilder.ViewModels;

public partial class MainViewModel : ViewModelBase {
    private readonly WorldBuilderSettings _settings;
    private readonly InputManager _inputManager;

    private bool _settingsOpen;

    [ObservableProperty]
    private object? _activeEditor;

    partial void OnActiveEditorChanged(object? value) {
        OnPropertyChanged(nameof(DockingPanels));
        OnPropertyChanged(nameof(IsLandscapeEditorActive));
    }

    /// <summary>True when the active editor is the landscape/terrain editor (for showing Landscape menu).</summary>
    public bool IsLandscapeEditorActive => ActiveEditor is LandscapeEditorViewModel;

    public KeyGesture? ExitGesture => _inputManager.GetKeyGesture(InputActions.AppExit);
    public KeyGesture? GotoLandblockGesture => _inputManager.GetKeyGesture(InputActions.NavigationGoToLandblock);

    private static readonly ObservableCollection<IDockable> _emptyPanels = new();

    public ObservableCollection<IDockable> DockingPanels {
        get {
            var dm = GetActiveDockingManager();
            return dm?.AllPanels ?? _emptyPanels;
        }
    }

    private DockingManager? GetActiveDockingManager() {
        return ActiveEditor switch {
            DungeonEditorViewModel de => de.DockingManager,
            LandscapeEditorViewModel le => le.DockingManager,
            _ => null,
        };
    }

    public MainViewModel() {
        _settings = new WorldBuilderSettings();
        _inputManager = new InputManager(_settings);
    }

    public MainViewModel(WorldBuilderSettings settings) {
        _settings = settings;
        _inputManager = new InputManager(_settings);
        ActiveEditor = ProjectManager.Instance?.GetProjectService<LandscapeEditorViewModel>();
    }

    [RelayCommand]
    private void TogglePanelVisibility(object? parameter) {
        if (parameter is string id) {
            GetActiveDockingManager()?.TogglePanelVisibility(id);
        }
    }

    public KeyGesture? UndoGesture => _inputManager.GetKeyGesture(InputActions.EditUndo);
    public KeyGesture? RedoGesture => _inputManager.GetKeyGesture(InputActions.EditRedo);
    public KeyGesture? CopyGesture => _inputManager.GetKeyGesture(InputActions.EditCopy);
    public KeyGesture? PasteGesture => _inputManager.GetKeyGesture(InputActions.EditPaste);
    public KeyGesture? DeleteGesture => _inputManager.GetKeyGesture(InputActions.EditDelete);

    private LandscapeEditorViewModel? GetLandscapeEditor() =>
        ProjectManager.Instance.GetProjectService<LandscapeEditorViewModel>();

    [RelayCommand]
    private void Undo() {
        if (ActiveEditor is DungeonEditorViewModel de) { de.UndoCommand.Execute(null); return; }
        GetLandscapeEditor()?.UndoCommand.Execute(null);
    }

    [RelayCommand]
    private void Redo() {
        if (ActiveEditor is DungeonEditorViewModel de) { de.RedoCommand.Execute(null); return; }
        GetLandscapeEditor()?.RedoCommand.Execute(null);
    }

    [RelayCommand]
    private void Copy() {
        if (ActiveEditor is DungeonEditorViewModel de) { de.CopySelectedCells(); return; }
        GetLandscapeEditor()?.CopySelectedObjectCommand.Execute(null);
    }

    [RelayCommand]
    private void Paste() {
        if (ActiveEditor is DungeonEditorViewModel de) { de.PasteCells(); return; }
        GetLandscapeEditor()?.PasteObjectCommand.Execute(null);
    }

    [RelayCommand]
    private void Delete() {
        if (ActiveEditor is DungeonEditorViewModel de) {
            if (de.HasSelectedObject) de.DeleteSelectedObjectCommand.Execute(null);
            else de.DeleteSelectedCellCommand.Execute(null);
            return;
        }
        GetLandscapeEditor()?.DeleteSelectedObjectCommand.Execute(null);
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
            var repoService = ProjectManager.Instance.GetProjectService<InstanceRepositionService>()
                ?? new InstanceRepositionService();

            var exportWindow = new ExportDatsWindow();
            exportWindow.DataContext = new ExportDatsWindowViewModel(_settings, project, exportWindow, repoService);

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

    [RelayCommand]
    private void SwitchToLandscapeEditor() {
        ActiveEditor = ProjectManager.Instance?.GetProjectService<LandscapeEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToDungeonEditor() {
        ActiveEditor = ProjectManager.Instance?.GetProjectService<DungeonEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToSpellEditor() {
        ActiveEditor = ProjectManager.Instance?.GetProjectService<SpellEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToSpellSetEditor() {
        ActiveEditor = ProjectManager.Instance?.GetProjectService<SpellSetEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToSkillEditor() {
        ActiveEditor = ProjectManager.Instance?.GetProjectService<SkillEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToExperienceEditor() {
        ActiveEditor = ProjectManager.Instance?.GetProjectService<ExperienceEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToVitalEditor() {
        ActiveEditor = ProjectManager.Instance?.GetProjectService<VitalEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToCharGenEditor() {
        ActiveEditor = ProjectManager.Instance?.GetProjectService<CharGenEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToLayoutEditor() {
        ActiveEditor = ProjectManager.Instance?.GetProjectService<LayoutEditorViewModel>();
    }

    [RelayCommand]
    private void AnalyzeDungeonRooms() {
        var dungeonEditor = ProjectManager.Instance?.GetProjectService<DungeonEditorViewModel>();
        dungeonEditor?.AnalyzeRoomsCommand.Execute(null);
    }

    // Landscape editor menu (only relevant when landscape editor is active)
    [RelayCommand]
    private void LandscapeTogglePerformanceOverlay() {
        var le = GetLandscapeEditor();
        if (le != null) le.ShowPerformanceOverlay = !le.ShowPerformanceOverlay;
    }

    [RelayCommand]
    private void LandscapeClearCache() {
        GetLandscapeEditor()?.ClearCacheCommand.Execute(null);
    }

    [RelayCommand]
    private async Task LandscapeFreshStart() {
        var le = GetLandscapeEditor();
        if (le != null) await le.FreshStartCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task LandscapeImportHeightmap() {
        var le = GetLandscapeEditor();
        if (le != null) await le.ImportHeightmapCommand.ExecuteAsync(null);
    }
}
