using CommunityToolkit.Mvvm.Input;

namespace WorldBuilder.Lib.Docking {
    public interface IDockable {
        string Id { get; }
        string Title { get; set; }
        bool IsVisible { get; set; }
        DockLocation Location { get; set; }
        object Content { get; }

        // Commands
        IRelayCommand CloseCommand { get; }
        IRelayCommand FloatCommand { get; }
        IRelayCommand DockCommand { get; }
    }
}
