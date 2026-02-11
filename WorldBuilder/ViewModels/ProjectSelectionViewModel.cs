using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Models;
using static WorldBuilder.ViewModels.SplashPageViewModel;

namespace WorldBuilder.ViewModels;

public partial class ProjectSelectionViewModel : SplashPageViewModelBase {
    private readonly ILogger<ProjectSelectionViewModel> _log;
    private readonly WorldBuilderSettings _settings;
    private readonly ProjectManager _projectManager;

    public ObservableCollection<RecentProject> RecentProjects => _projectManager.RecentProjects;

    public string AppVersion => $"v{App.Version}";

    public ProjectSelectionViewModel(WorldBuilderSettings settings, ProjectManager projectManager, ILogger<ProjectSelectionViewModel> log) {
        _log = log;
        _settings = settings;
        _projectManager = projectManager;
    }

    [RelayCommand]
    private void NewProject() {
        WeakReferenceMessenger.Default.Send(new SplashPageChangedMessage(SplashPage.CreateProject));
    }

    [RelayCommand]
    private async Task OpenExistingProject() {
        var files = await TopLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions() {
            Title = "Open existing project",
            AllowMultiple = false,
            SuggestedStartLocation = await TopLevel.StorageProvider.TryGetFolderFromPathAsync(_settings.App.ProjectsDirectory),
            FileTypeFilter = new[] {
                new FilePickerFileType("WorldBuilder Project") {
                    Patterns = new[] { "*.wbproj" }
                }
            }
        });

        if (files.Count == 0) {
            _log.LogWarning("No project selected");
            return;
        }

        var localPath = files[0].TryGetLocalPath() ?? throw new Exception("Unable to get local path of project file");
        LoadProject(localPath);

        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task OpenRecentProject(RecentProject? project) {
        if (project == null) {
            _log.LogWarning("Recent project is null");
            return;
        }

        if (!File.Exists(project.FilePath)) {
            _log.LogWarning($"Project file no longer exists: {project.FilePath}");
            await _projectManager.RemoveRecentProject(project.FilePath);
            return;
        }

        LoadProject(project.FilePath);
    }

    private void LoadProject(string filePath) {
        _log.LogInformation($"LoadProject: {filePath}");

        // Navigate to loading screen, then send the load message
        // (SplashPageChangedMessage is synchronous, so the loading VM is created and registered
        //  before StartProjectLoadMessage is sent)
        WeakReferenceMessenger.Default.Send(new SplashPageChangedMessage(SplashPage.Loading));
        WeakReferenceMessenger.Default.Send(new StartProjectLoadMessage(filePath));
    }

    [RelayCommand]
    private async Task RemoveRecentProject(RecentProject? project) {
        if (project == null) return;

        await _projectManager.RemoveRecentProject(project.FilePath);
    }

    [RelayCommand]
    private void OpenInExplorer(RecentProject project) {
        if (project?.FileDirectory != null && Directory.Exists(project.FileDirectory)) {
            try {
                Process.Start(new ProcessStartInfo {
                    FileName = project.FileDirectory,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex) {
                _log.LogError(ex, "Failed to open project directory in file explorer");
            }
        }
    }

    [RelayCommand]
    private async Task CopyProjectPath(RecentProject? project) {
        if (project?.FilePath == null) return;

        try {
            var clipboard = TopLevel.Clipboard;
            if (clipboard != null) {
                await clipboard.SetTextAsync(project.FilePath);
            }
        }
        catch (Exception ex) {
            _log.LogError(ex, "Failed to copy project path to clipboard");
        }
    }

    [RelayCommand]
    private async Task DeleteProject(RecentProject? project) {
        if (project == null) return;

        try {
            var dir = project.FileDirectory;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) {
                Directory.Delete(dir, true);
                _log.LogInformation($"Deleted project directory: {dir}");
            }

            await _projectManager.RemoveRecentProject(project.FilePath);
        }
        catch (Exception ex) {
            _log.LogError(ex, "Failed to delete project from disk");
        }
    }
}
