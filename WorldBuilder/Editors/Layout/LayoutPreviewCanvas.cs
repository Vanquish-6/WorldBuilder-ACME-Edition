using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Collections.ObjectModel;
using System.Globalization;

namespace WorldBuilder.Editors.Layout {
    public class LayoutPreviewCanvas : Control {
        private ObservableCollection<ElementTreeNode>? _elements;
        private uint _layoutWidth;
        private uint _layoutHeight;
        private ElementTreeNode? _selectedElement;

        private static readonly IBrush FillBrush = new SolidColorBrush(Color.FromArgb(30, 160, 140, 220));
        private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 160, 140, 220)), 1);
        private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 110, 192, 122)), 2);
        private static readonly IBrush SelectedFill = new SolidColorBrush(Color.FromArgb(40, 110, 192, 122));
        private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromArgb(140, 192, 176, 216));
        private static readonly IBrush DimTextBrush = new SolidColorBrush(Color.FromArgb(80, 192, 176, 216));
        private static readonly IPen CanvasBorderPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 42, 29, 66)), 1);

        public void SetLayout(ObservableCollection<ElementTreeNode>? elements, uint width, uint height, ElementTreeNode? selected) {
            _elements = elements;
            _layoutWidth = width;
            _layoutHeight = height;
            _selectedElement = selected;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context) {
            base.Render(context);

            if (_elements == null || _elements.Count == 0 || _layoutWidth == 0 || _layoutHeight == 0) {
                var noDataText = new FormattedText("No layout selected",
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Normal),
                    12, DimTextBrush);
                context.DrawText(noDataText,
                    new Point((Bounds.Width - noDataText.Width) / 2, (Bounds.Height - noDataText.Height) / 2));
                return;
            }

            double scaleX = (Bounds.Width - 20) / _layoutWidth;
            double scaleY = (Bounds.Height - 20) / _layoutHeight;
            double scale = System.Math.Min(scaleX, scaleY);
            if (scale <= 0) return;

            double offsetX = (Bounds.Width - _layoutWidth * scale) / 2;
            double offsetY = (Bounds.Height - _layoutHeight * scale) / 2;

            context.DrawRectangle(null, CanvasBorderPen,
                new Rect(offsetX, offsetY, _layoutWidth * scale, _layoutHeight * scale));

            var sizeText = new FormattedText($"{_layoutWidth}x{_layoutHeight}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), 9, DimTextBrush);
            context.DrawText(sizeText, new Point(offsetX, offsetY - sizeText.Height - 2));

            foreach (var element in _elements) {
                DrawElement(context, element, offsetX, offsetY, scale, 0);
            }
        }

        private void DrawElement(DrawingContext context, ElementTreeNode node,
            double baseX, double baseY, double scale, int depth) {
            double x = baseX + node.X * scale;
            double y = baseY + node.Y * scale;
            double w = node.Width * scale;
            double h = node.Height * scale;

            foreach (var child in node.Children) {
                if (w < 1 || h < 1) {
                    DrawElement(context, child, node.X + baseX, node.Y + baseY, scale, depth + 1);
                    return;
                }
                else {
                    if (node.Type != 0 && node.BaseLayoutId == 0 && child.Type == 0 && child.BaseLayoutId != 0) {
                        DrawElement(context, child, node.X + baseX, node.Y + baseY, scale, depth + 1);
                    }
                    else {
                        DrawElement(context, child, x, y, scale, depth + 1);
                    }
                }
            }

            var rect = new Rect(x, y, w, h);
            bool isSelected = node == _selectedElement;

            context.DrawRectangle(
                isSelected ? SelectedFill : FillBrush,
                isSelected ? SelectedPen : BorderPen,
                rect);

            if (w > 30 && h > 12) {
                var label = new FormattedText(node.DisplayId,
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Consolas"), System.Math.Min(9, h * 0.6),
                    isSelected ? TextBrush : DimTextBrush);

                if (label.Width < w - 4 && label.Height < h - 2) {
                    context.DrawText(label, new Point(x + 2, y + 1));
                }
            }
        }
    }
}
