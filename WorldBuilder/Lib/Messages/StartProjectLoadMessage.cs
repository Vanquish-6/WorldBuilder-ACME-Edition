using CommunityToolkit.Mvvm.Messaging.Messages;

namespace WorldBuilder.ViewModels;

/// <summary>
/// Message sent to trigger the loading screen to open an existing project.
/// </summary>
public class StartProjectLoadMessage : ValueChangedMessage<string> {
    public StartProjectLoadMessage(string projectPath) : base(projectPath) { }
}

/// <summary>
/// Message sent to navigate to the template selection page, carrying the project details from the create form.
/// </summary>
public class ShowTemplateSelectorMessage {
    public string ProjectName { get; }
    public string ProjectLocation { get; }
    public string BaseDatDirectory { get; }

    public ShowTemplateSelectorMessage(string projectName, string projectLocation, string baseDatDirectory) {
        ProjectName = projectName;
        ProjectLocation = projectLocation;
        BaseDatDirectory = baseDatDirectory;
    }
}

/// <summary>
/// Message sent to trigger the loading screen to create a new project.
/// </summary>
public class StartProjectCreateMessage {
    public string ProjectName { get; }
    public string ProjectLocation { get; }
    public string BaseDatDirectory { get; }
    public string SelectedTemplateId { get; }

    public StartProjectCreateMessage(string projectName, string projectLocation, string baseDatDirectory, string selectedTemplateId) {
        ProjectName = projectName;
        ProjectLocation = projectLocation;
        BaseDatDirectory = baseDatDirectory;
        SelectedTemplateId = selectedTemplateId;
    }
}
