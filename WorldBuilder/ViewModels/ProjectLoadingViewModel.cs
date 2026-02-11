using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using static WorldBuilder.ViewModels.SplashPageViewModel;

namespace WorldBuilder.ViewModels;

public partial class ProjectLoadingViewModel : SplashPageViewModelBase,
    IRecipient<StartProjectLoadMessage>,
    IRecipient<StartProjectCreateMessage> {

    private readonly ProjectManager _projectManager;
    private readonly ILogger<ProjectLoadingViewModel> _log;

    [ObservableProperty]
    private string _projectName = "";

    [ObservableProperty]
    private string _statusMessage = "Preparing...";

    [ObservableProperty]
    private bool _hasError = false;

    [ObservableProperty]
    private string _errorMessage = "";

    public ProjectLoadingViewModel(ProjectManager projectManager, ILogger<ProjectLoadingViewModel> log) {
        _projectManager = projectManager;
        _log = log;

        WeakReferenceMessenger.Default.Register<StartProjectLoadMessage>(this);
        WeakReferenceMessenger.Default.Register<StartProjectCreateMessage>(this);
    }

    public void Receive(StartProjectLoadMessage message) {
        ProjectName = Path.GetFileNameWithoutExtension(message.Value);
        _ = LoadProject(message.Value);
    }

    public void Receive(StartProjectCreateMessage message) {
        ProjectName = message.ProjectName;
        _ = CreateProject(message);
    }

    private async Task LoadProject(string path) {
        try {
            StatusMessage = "Loading project...";
            await _projectManager.LoadProjectAsync(path);
        }
        catch (Exception ex) {
            _log.LogError(ex, "Failed to load project");
            HasError = true;
            ErrorMessage = ex.Message;
            StatusMessage = "Failed to load project";
        }
    }

    private async Task CreateProject(StartProjectCreateMessage message) {
        try {
            StatusMessage = "Creating project...";
            await _projectManager.CreateProjectAsync(
                message.ProjectName,
                message.ProjectLocation,
                message.BaseDatDirectory);
        }
        catch (Exception ex) {
            _log.LogError(ex, "Failed to create project");
            HasError = true;
            ErrorMessage = ex.Message;
            StatusMessage = "Failed to create project";
        }
    }

    [RelayCommand]
    private void GoBack() {
        WeakReferenceMessenger.Default.Send(new SplashPageChangedMessage(SplashPage.ProjectSelection));
    }
}
