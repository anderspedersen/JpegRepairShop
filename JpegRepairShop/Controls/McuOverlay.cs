using JpegRepair.Core;
using System.Windows;
using System.Windows.Media;

namespace JpegRepairShop.Controls;

/// <summary>
/// Draws the section, hover and selection outlines on top of a <see cref="JpegCanvas"/> using plain WPF drawing.
/// Kept separate so that moving the mouse only redraws a few outlines instead of repainting the whole picture.
/// Place it in the same grid cell as the canvas (same size, same coordinates).
/// </summary>
public sealed class McuOverlay : FrameworkElement
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(JpegCanvas), typeof(McuOverlay),
        new PropertyMetadata(null, OnTargetChanged));

    private static readonly Pen HoverOuter = Frozen(new Pen(Brushes.Black, 3));
    private static readonly Pen HoverInner = Frozen(new Pen(Brushes.White, 1));
    private static readonly Pen SelectedOuter = Frozen(new Pen(Brushes.Black, 4));
    private static readonly Pen SelectedInner = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD0, 0x00)), 2));
    private static readonly Pen SectionOuter = Frozen(new Pen(Brushes.Black, 4) { LineJoin = PenLineJoin.Miter });
    private static readonly Pen SectionInner = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)), 2) { LineJoin = PenLineJoin.Miter });

    public McuOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public JpegCanvas? Target
    {
        get => (JpegCanvas?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Target is not { } canvas)
            return;

        if (canvas.Section is { } section && SectionGeometry(canvas, section) is { } geometry)
        {
            dc.DrawGeometry(null, SectionOuter, geometry);
            dc.DrawGeometry(null, SectionInner, geometry);
        }

        DrawOutline(dc, canvas.GetMcuRect(canvas.HoverMcu), HoverOuter, HoverInner);
        DrawOutline(dc, canvas.GetMcuRect(canvas.SelectedMcu), SelectedOuter, SelectedInner);
    }

    /// <summary>
    /// Outline of a run of MCUs in read order: the part of its first row from its first MCU to the right edge, the whole
    /// rows in between, and its last row from the left edge to its last MCU. Two separate boxes if it spans two rows and
    /// those parts do not overlap.
    /// </summary>
    private static Geometry? SectionGeometry(JpegCanvas canvas, McuSection section)
    {
        if (canvas.Document is not { } doc)
            return null;

        var frame = doc.Frame;
        int perRow = frame.McusPerRow;
        int r0 = section.First / perRow, c0 = section.First % perRow;
        int r1 = section.Last / perRow, c1 = section.Last % perRow;
        Point P(int column, int row) =>
            canvas.ImageToView(Math.Min(column * frame.McuWidth, frame.Width), Math.Min(row * frame.McuHeight, frame.Height));

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            void Polygon(params Point[] points)
            {
                g.BeginFigure(points[0], isFilled: false, isClosed: true);
                g.PolyLineTo(points[1..], isStroked: true, isSmoothJoin: false);
            }

            if (r0 == r1)
            {
                Polygon(P(c0, r0), P(c1 + 1, r0), P(c1 + 1, r0 + 1), P(c0, r0 + 1));
            }
            else if (r1 == r0 + 1 && c1 + 1 <= c0)
            {
                Polygon(P(c0, r0), P(perRow, r0), P(perRow, r0 + 1), P(c0, r0 + 1));
                Polygon(P(0, r1), P(c1 + 1, r1), P(c1 + 1, r1 + 1), P(0, r1 + 1));
            }
            else
            {
                Polygon(P(c0, r0), P(perRow, r0), P(perRow, r1), P(c1 + 1, r1),
                        P(c1 + 1, r1 + 1), P(0, r1 + 1), P(0, r0 + 1), P(c0, r0 + 1));
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static void DrawOutline(DrawingContext dc, Rect rect, Pen outer, Pen inner)
    {
        if (rect.IsEmpty)
            return;
        dc.DrawRectangle(null, outer, rect);
        dc.DrawRectangle(null, inner, rect);
    }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var overlay = (McuOverlay)d;
        if (e.OldValue is JpegCanvas oldCanvas)
            oldCanvas.OverlayChanged -= overlay.OnOverlayChanged;
        if (e.NewValue is JpegCanvas newCanvas)
            newCanvas.OverlayChanged += overlay.OnOverlayChanged;
        overlay.InvalidateVisual();
    }

    private void OnOverlayChanged(object? sender, EventArgs e) => InvalidateVisual();

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
