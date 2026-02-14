using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WorldBuilder.Views.Components.Viewports {
    public partial class ViewportContainer : UserControl {
        public ViewportContainer() {
            InitializeComponent();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
