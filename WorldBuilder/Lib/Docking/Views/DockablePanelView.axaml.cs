using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace WorldBuilder.Lib.Docking.Views {
    public partial class DockablePanelView : UserControl {
        public DockablePanelView() {
            InitializeComponent();

            var dragHandle = this.FindControl<Border>("DragHandle");
            if (dragHandle != null) {
                dragHandle.PointerPressed += DragHandle_PointerPressed;
            }
        }

        private async void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e) {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
                var dragData = new DataObject();
                // Pass the ViewModel as the data
                if (DataContext != null) {
                    dragData.Set("DockablePanel", DataContext);

                    var result = await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Move);
                }
            }
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
