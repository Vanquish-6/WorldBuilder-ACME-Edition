using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace WorldBuilder.Lib.Docking {
    public partial class DockablePanelView : UserControl {
        public DockablePanelView() {
            InitializeComponent();

            var headerBorder = this.FindControl<Border>("HeaderBorder");
            if (headerBorder != null) {
                headerBorder.PointerPressed += Header_PointerPressed;
            }

            var dockDragHandle = this.FindControl<Border>("DockDragHandle");
            if (dockDragHandle != null) {
                dockDragHandle.PointerPressed += DockDragHandle_PointerPressed;
            }
        }

        private void Header_PointerPressed(object? sender, PointerPressedEventArgs e) {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
                // If this is inside a window (floating), drag the window
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window != null && window.SystemDecorations == SystemDecorations.BorderOnly) {
                    window.BeginMoveDrag(e);
                }
            }
        }

        private async void DockDragHandle_PointerPressed(object? sender, PointerPressedEventArgs e) {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
                var dragData = new DataObject();

                // Pass ID instead of object reference to be safe across windows
                if (DataContext is DockablePanelViewModel vm) {
                    dragData.Set("DockablePanelId", vm.Id);

                    var result = await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Move);
                }
                e.Handled = true; // Prevent bubbling to header move
            }
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
