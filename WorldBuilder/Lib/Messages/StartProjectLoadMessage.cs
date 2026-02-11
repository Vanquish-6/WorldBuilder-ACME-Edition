using CommunityToolkit.Mvvm.Messaging.Messages;

namespace WorldBuilder.ViewModels;

/// <summary>
/// Message sent to trigger the loading screen to open an existing project.
/// </summary>
public class StartProjectLoadMessage : ValueChangedMessage<string> {
    public StartProjectLoadMessage(string projectPath) : base(projectPath) { }
}

/// <summary>
/// Message sent to trigger the loading screen to create a new project.
/// </summary>
public class StartProjectCreateMessage {
    public string ProjectName { get; }
    public string ProjectLocation { get; }
    public string BaseDatDirectory { get; }

    public StartProjectCreateMessage(string projectName, string projectLocation, string baseDatDirectory) {
        ProjectName = projectName;
        ProjectLocation = projectLocation;
        BaseDatDirectory = baseDatDirectory;
    }
}
