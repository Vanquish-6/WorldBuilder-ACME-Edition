using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Views.Components.Viewports {
    public partial class ViewportPanel : UserControl {
        public ViewportPanel() {
            InitializeComponent();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
