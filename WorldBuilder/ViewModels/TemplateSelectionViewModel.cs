using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using WorldBuilder.Lib.Templates;
using static WorldBuilder.ViewModels.SplashPageViewModel;

namespace WorldBuilder.ViewModels;

public partial class TemplateCardViewModel : ObservableObject {
    public IWorldTemplate Template { get; }
    public string Name => Template.Name;
    public string Description => Template.Description;
    public string[] Tags => Template.Tags;
    public Bitmap? Preview { get; }

    private bool _isSelected;
    public bool IsSelected {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public TemplateCardViewModel(IWorldTemplate template) {
        Template = template;
        Preview = TryLoadPreview(template.PreviewResourceName);
    }

    private static Bitmap? TryLoadPreview(string resourceName) {
        try {
            var uri = new Uri($"avares://WorldBuilder/{resourceName.Replace("WorldBuilder.", "")}");
            if (AssetLoader.Exists(uri)) {
                using var stream = AssetLoader.Open(uri);
                return new Bitmap(stream);
            }
        }
        catch {
            // Preview image is optional — fall back gracefully to the placeholder in the UI
        }
        return null;
    }
}

public partial class TemplateSelectionViewModel : SplashPageViewModelBase, IRecipient<ShowTemplateSelectorMessage> {
    private readonly ILogger<TemplateSelectionViewModel> _log;

    [ObservableProperty]
    private string _projectName = string.Empty;

    [ObservableProperty]
    private string _projectLocation = string.Empty;

    private string _baseDatDirectory = string.Empty;

    public ObservableCollection<TemplateCardViewModel> Templates { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateProjectCommand))]
    private TemplateCardViewModel? _selectedCard;

    public bool CanCreate => SelectedCard != null;

    public TemplateSelectionViewModel(ILogger<TemplateSelectionViewModel> log) {
        _log = log;

        foreach (var template in WorldTemplateRegistry.All) {
            Templates.Add(new TemplateCardViewModel(template));
        }

        if (Templates.Count > 0) {
            SelectCard(Templates[0]);
        }

        WeakReferenceMessenger.Default.Register<ShowTemplateSelectorMessage>(this);
    }

    public void Receive(ShowTemplateSelectorMessage message) {
        ProjectName = message.ProjectName;
        ProjectLocation = message.ProjectLocation;
        _baseDatDirectory = message.BaseDatDirectory;
    }

    [RelayCommand]
    private void SelectCard(TemplateCardViewModel card) {
        foreach (var c in Templates) {
            c.IsSelected = false;
        }
        card.IsSelected = true;
        SelectedCard = card;
    }

    [RelayCommand]
    private void GoBack() {
        WeakReferenceMessenger.Default.Send(new SplashPageChangedMessage(SplashPage.CreateProject));
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreateProject() {
        if (SelectedCard == null) return;

        WeakReferenceMessenger.Default.Send(new SplashPageChangedMessage(SplashPage.Loading));
        WeakReferenceMessenger.Default.Send(new StartProjectCreateMessage(
            ProjectName,
            ProjectLocation,
            _baseDatDirectory,
            SelectedCard.Template.Id));
    }
}
