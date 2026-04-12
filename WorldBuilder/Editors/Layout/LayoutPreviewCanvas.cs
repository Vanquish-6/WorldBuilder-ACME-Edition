using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace WorldBuilder.Editors.Layout {
    public class LayoutPreviewCanvas : Control {
        enum PreviewHit {
            None,
            Body,
            N, NE, E, SE, S, SW, W, NW
        }

        private ObservableCollection<ElementTreeNode>? _elements;
        private uint _layoutWidth;
        private uint _layoutHeight;
        private ElementTreeNode? _selectedElement;
        private IReadOnlyDictionary<uint, WriteableBitmap?>? _elementTextures;

        private double _drawOffsetX;
        private double _drawOffsetY;
        private double _drawScale;
        private bool _hasDrawGeometry;
        private Rect _selectedScreenRect;
        private bool _selectedScreenRectValid;

        private PreviewHit _hoverHit = PreviewHit.None;
        private bool _pointerDown;
        private PreviewHit _activeHit = PreviewHit.None;
        private Point _pointerStart;
        private uint _startX, _startY, _startW, _startH;

        private const double CornerHitPx = 11;
        private const double EdgeHitPx = 9;
        private const double HandleDrawPx = 6;

        private static readonly IBrush FillBrush = new SolidColorBrush(Color.FromArgb(30, 160, 140, 220));
        private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 160, 140, 220)), 1);
        private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 110, 192, 122)), 2);
        private static readonly IBrush SelectedFill = new SolidColorBrush(Color.FromArgb(40, 110, 192, 122));
        private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromArgb(140, 192, 176, 216));
        private static readonly IBrush DimTextBrush = new SolidColorBrush(Color.FromArgb(80, 192, 176, 216));
        private static readonly IPen CanvasBorderPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 42, 29, 66)), 1);
        private static readonly IBrush HandleFill = new SolidColorBrush(Color.FromArgb(240, 230, 240, 255));
        private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.FromArgb(200, 80, 200, 120)), 1);

        public LayoutPreviewCanvas() {
            Focusable = true;
            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;
            PointerExited += (_, _) => {
                if (!_pointerDown) {
                    _hoverHit = PreviewHit.None;
                    Cursor = Cursor.Default;
                }
            };
        }

        public void SetLayout(ObservableCollection<ElementTreeNode>? elements, uint width, uint height, ElementTreeNode? selected,
            IReadOnlyDictionary<uint, WriteableBitmap?>? elementTextures = null) {
            _elements = elements;
            _layoutWidth = width;
            _layoutHeight = height;
            _selectedElement = selected;
            _elementTextures = elementTextures;
            _pointerDown = false;
            _activeHit = PreviewHit.None;
            InvalidateVisual();
        }

        static bool Near(Point p, Point c, double r) {
            var dx = p.X - c.X;
            var dy = p.Y - c.Y;
            return dx * dx + dy * dy <= r * r;
        }

        PreviewHit HitTest(Point p) {
            if (!_selectedScreenRectValid || !_hasDrawGeometry || _selectedElement == null)
                return PreviewHit.None;

            var r = _selectedScreenRect;
            if (Near(p, r.TopLeft, CornerHitPx)) return PreviewHit.NW;
            if (Near(p, r.TopRight, CornerHitPx)) return PreviewHit.NE;
            if (Near(p, r.BottomRight, CornerHitPx)) return PreviewHit.SE;
            if (Near(p, r.BottomLeft, CornerHitPx)) return PreviewHit.SW;

            if (p.X >= r.Left && p.X <= r.Right && System.Math.Abs(p.Y - r.Top) <= EdgeHitPx) return PreviewHit.N;
            if (p.X >= r.Left && p.X <= r.Right && System.Math.Abs(p.Y - r.Bottom) <= EdgeHitPx) return PreviewHit.S;
            if (p.Y >= r.Top && p.Y <= r.Bottom && System.Math.Abs(p.X - r.Left) <= EdgeHitPx) return PreviewHit.W;
            if (p.Y >= r.Top && p.Y <= r.Bottom && System.Math.Abs(p.X - r.Right) <= EdgeHitPx) return PreviewHit.E;

            if (r.Contains(p)) return PreviewHit.Body;
            return PreviewHit.None;
        }

        static Cursor CursorForHit(PreviewHit h) => h switch {
            PreviewHit.N or PreviewHit.S => new Cursor(StandardCursorType.SizeNorthSouth),
            PreviewHit.E or PreviewHit.W => new Cursor(StandardCursorType.SizeWestEast),
            PreviewHit.NW => new Cursor(StandardCursorType.TopLeftCorner),
            PreviewHit.SE => new Cursor(StandardCursorType.BottomRightCorner),
            PreviewHit.NE => new Cursor(StandardCursorType.TopRightCorner),
            PreviewHit.SW => new Cursor(StandardCursorType.BottomLeftCorner),
            PreviewHit.Body => new Cursor(StandardCursorType.SizeAll),
            _ => Cursor.Default
        };

        void OnPointerPressed(object? sender, PointerPressedEventArgs e) {
            if (!_hasDrawGeometry || _selectedElement == null) return;
            var p = e.GetPosition(this);
            var hit = HitTest(p);
            if (hit == PreviewHit.None) return;

            _pointerDown = true;
            _activeHit = hit;
            _pointerStart = p;
            _startX = _selectedElement.X;
            _startY = _selectedElement.Y;
            _startW = _selectedElement.Width;
            _startH = _selectedElement.Height;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        void OnPointerMoved(object? sender, PointerEventArgs e) {
            var p = e.GetPosition(this);

            if (!_pointerDown) {
                if (_selectedScreenRectValid && _selectedElement != null) {
                    var h = HitTest(p);
                    if (h != _hoverHit) {
                        _hoverHit = h;
                        Cursor = CursorForHit(h);
                    }
                }
                else {
                    if (_hoverHit != PreviewHit.None) {
                        _hoverHit = PreviewHit.None;
                        Cursor = Cursor.Default;
                    }
                }
                return;
            }

            if (_selectedElement == null || !_hasDrawGeometry || _drawScale <= 0) return;

            var dx = (p.X - _pointerStart.X) / _drawScale;
            var dy = (p.Y - _pointerStart.Y) / _drawScale;
            var rdx = System.Math.Round(dx);
            var rdy = System.Math.Round(dy);

            long nx = _startX, ny = _startY, nw = _startW, nh = _startH;

            switch (_activeHit) {
                case PreviewHit.Body:
                    nx = (long)_startX + (long)rdx;
                    ny = (long)_startY + (long)rdy;
                    break;
                case PreviewHit.E:
                    nw = (long)_startW + (long)rdx;
                    break;
                case PreviewHit.S:
                    nh = (long)_startH + (long)rdy;
                    break;
                case PreviewHit.SE:
                    nw = (long)_startW + (long)rdx;
                    nh = (long)_startH + (long)rdy;
                    break;
                case PreviewHit.W:
                    nx = (long)_startX + (long)rdx;
                    nw = (long)_startW - (long)rdx;
                    break;
                case PreviewHit.N:
                    ny = (long)_startY + (long)rdy;
                    nh = (long)_startH - (long)rdy;
                    break;
                case PreviewHit.NW:
                    nx = (long)_startX + (long)rdx;
                    ny = (long)_startY + (long)rdy;
                    nw = (long)_startW - (long)rdx;
                    nh = (long)_startH - (long)rdy;
                    break;
                case PreviewHit.NE:
                    ny = (long)_startY + (long)rdy;
                    nw = (long)_startW + (long)rdx;
                    nh = (long)_startH - (long)rdy;
                    break;
                case PreviewHit.SW:
                    nx = (long)_startX + (long)rdx;
                    nw = (long)_startW - (long)rdx;
                    nh = (long)_startH + (long)rdy;
                    break;
                default:
                    e.Handled = true;
                    return;
            }

            var ux = (uint)System.Math.Max(0, nx);
            var uy = (uint)System.Math.Max(0, ny);
            var uw = (uint)System.Math.Max(1, nw);
            var uh = (uint)System.Math.Max(1, nh);

            ClampToLayout(ref ux, ref uy, ref uw, ref uh);
            _selectedElement.SetBounds(ux, uy, uw, uh);
            e.Handled = true;
        }

        void ClampToLayout(ref uint x, ref uint y, ref uint w, ref uint h) {
            if (_layoutWidth > 0) {
                if (w > _layoutWidth) w = _layoutWidth;
                if (x > _layoutWidth - w) x = _layoutWidth >= w ? _layoutWidth - w : 0;
            }

            if (_layoutHeight > 0) {
                if (h > _layoutHeight) h = _layoutHeight;
                if (y > _layoutHeight - h) y = _layoutHeight >= h ? _layoutHeight - h : 0;
            }
        }

        void OnPointerReleased(object? sender, PointerReleasedEventArgs e) {
            if (!_pointerDown) return;
            _pointerDown = false;
            _activeHit = PreviewHit.None;
            if (e.Pointer.Captured == this)
                e.Pointer.Capture(null);

            var p = e.GetPosition(this);
            _hoverHit = _selectedScreenRectValid && _selectedElement != null ? HitTest(p) : PreviewHit.None;
            Cursor = CursorForHit(_hoverHit);
            e.Handled = true;
        }

        public override void Render(DrawingContext context) {
            base.Render(context);

            if (_elements == null || _elements.Count == 0 || _layoutWidth == 0 || _layoutHeight == 0) {
                _hasDrawGeometry = false;
                _selectedScreenRectValid = false;
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
            if (scale <= 0) {
                _hasDrawGeometry = false;
                return;
            }

            double offsetX = (Bounds.Width - _layoutWidth * scale) / 2;
            double offsetY = (Bounds.Height - _layoutHeight * scale) / 2;

            _drawOffsetX = offsetX;
            _drawOffsetY = offsetY;
            _drawScale = scale;
            _hasDrawGeometry = true;
            _selectedScreenRectValid = false;

            context.DrawRectangle(null, CanvasBorderPen,
                new Rect(offsetX, offsetY, _layoutWidth * scale, _layoutHeight * scale));

            var sizeText = new FormattedText($"{_layoutWidth}x{_layoutHeight}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), 9, DimTextBrush);
            context.DrawText(sizeText, new Point(offsetX, offsetY - sizeText.Height - 2));

            // Lower ReadOrder / ZLevel first, higher last so stacked siblings match client draw order.
            foreach (var element in _elements.OrderBy(e => e.ReadOrder).ThenBy(e => e.ZLevel).ThenBy(e => e.ElementId)) {
                DrawElement(context, element, offsetX, offsetY, scale, 0);
            }

            if (_selectedScreenRectValid && _selectedElement != null) {
                DrawResizeHandles(context, _selectedScreenRect);
            }
        }

        void DrawResizeHandles(DrawingContext context, Rect r) {
            double h = HandleDrawPx;
            var corners = new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft };
            foreach (var c in corners) {
                var hr = new Rect(c.X - h / 2, c.Y - h / 2, h, h);
                context.DrawRectangle(HandleFill, HandlePen, hr);
            }

            var midT = new Point((r.Left + r.Right) / 2, r.Top);
            var midR = new Point(r.Right, (r.Top + r.Bottom) / 2);
            var midB = new Point((r.Left + r.Right) / 2, r.Bottom);
            var midL = new Point(r.Left, (r.Top + r.Bottom) / 2);
            foreach (var c in new[] { midT, midR, midB, midL }) {
                var hr = new Rect(c.X - h / 2, c.Y - h / 2, h, h);
                context.DrawRectangle(HandleFill, HandlePen, hr);
            }
        }

        private void DrawElement(DrawingContext context, ElementTreeNode node,
            double baseX, double baseY, double scale, int depth) {
            double x = baseX + node.X * scale;
            double y = baseY + node.Y * scale;
            double w = node.Width * scale;
            double h = node.Height * scale;

            // Paint this node *before* descendants so children (e.g. Cancel) are not covered by the parent's
            // semi-opaque fill/border. Previously children were drawn first, which hid small controls under panels.
            if (w >= 1 && h >= 1)
                DrawElementVisuals(context, node, x, y, w, h);

            foreach (var child in node.Children.OrderBy(c => c.ReadOrder).ThenBy(c => c.ZLevel).ThenBy(c => c.ElementId)) {
                if (w < 1 || h < 1) {
                    DrawElement(context, child, node.X + baseX, node.Y + baseY, scale, depth + 1);
                    return;
                }

                if (node.Type != 0 && node.BaseLayoutId == 0 && child.Type == 0 && child.BaseLayoutId != 0) {
                    DrawElement(context, child, node.X + baseX, node.Y + baseY, scale, depth + 1);
                }
                else {
                    DrawElement(context, child, x, y, scale, depth + 1);
                }
            }
        }

        void DrawElementVisuals(DrawingContext context, ElementTreeNode node, double x, double y, double w, double h) {
            var rect = new Rect(x, y, w, h);
            bool isSelected = node == _selectedElement;
            if (isSelected) {
                _selectedScreenRect = rect;
                _selectedScreenRectValid = true;
            }

            if (_elementTextures != null &&
                _elementTextures.TryGetValue(node.ElementId, out var tex) &&
                tex != null &&
                w >= 1 && h >= 1) {
                try {
                    context.DrawImage(tex, rect);
                }
                catch {
                    // Corrupt / odd-sized bitmap — fall back to flat fill
                }
            }

            context.DrawRectangle(
                isSelected ? SelectedFill : FillBrush,
                isSelected ? SelectedPen : BorderPen,
                rect);

            if (w > 30 && h > 12) {
                var cap = node.Caption;
                var capShort = string.IsNullOrWhiteSpace(cap)
                    ? ""
                    : (cap.Length > 36 ? cap[..35] + "…" : cap);
                var line = string.IsNullOrEmpty(capShort)
                    ? node.DisplayId
                    : $"{node.DisplayId} · {capShort}";
                var label = new FormattedText(line,
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
