using JpegRepair.Core;
using System.Windows;
using System.Windows.Media;

namespace JpegRepairShop.Controls;

/// <summary>
/// Draws the bad-MCU, section, hover and selection outlines on top of a <see cref="JpegCanvas"/> using plain WPF drawing.
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

    // Square caps, so the separate edge lines of an outline meet at the corners.
    private static readonly Pen BadOuter = Frozen(new Pen(Brushes.Black, 4) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square });
    private static readonly Pen BadInner = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x00)), 2)
        { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square });

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

        if (canvas.ShowBadMcus && BadGeometry(canvas, canvas.BadMcus) is { } badOutline)
        {
            dc.DrawGeometry(null, BadOuter, badOutline);
            dc.DrawGeometry(null, BadInner, badOutline);
        }

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

    /// <summary>
    /// Outline around each group of bad MCUs: every MCU edge between a bad MCU and one that is not bad (or the picture
    /// border). Bad here means anything that needs repair: flagged or marked bad, corrupt, blank (inserted) or missing.
    /// Black lines are drawn under all the orange ones, so where groups touch the orange stays on top.
    /// Null if there is nothing to outline.
    /// </summary>
    private static Geometry? BadGeometry(JpegCanvas canvas, BadMcuMap? bad)
    {
        if (canvas.Document is not { } doc)
            return null;

        var frame = doc.Frame;
        int perRow = frame.McusPerRow, rows = frame.McuRows;
        Point P(int column, int row) =>
            canvas.ImageToView(Math.Min(column * frame.McuWidth, frame.Width), Math.Min(row * frame.McuHeight, frame.Height));
        var isBad = new bool[perRow * rows];
        bool any = false;
        for (int i = 0; i < isBad.Length; i++)
        {
            isBad[i] = doc.GetStatus(i) is McuStatus.Corrupt or McuStatus.Blank or McuStatus.Missing || bad?.IsBad(i) == true;
            any |= isBad[i];
        }

        if (!any)
            return null;

        bool IsBad(int column, int row) =>
            column >= 0 && row >= 0 && column < perRow && row < rows && isBad[row * perRow + column];

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            void Line(Point a, Point b)
            {
                g.BeginFigure(a, isFilled: false, isClosed: false);
                g.LineTo(b, isStroked: true, isSmoothJoin: false);
            }

            for (int index = 0; index < isBad.Length; index++)
            {
                if (!isBad[index])
                    continue;
                int c = index % perRow, r = index / perRow;
                if (!IsBad(c, r - 1))
                    Line(P(c, r), P(c + 1, r));
                if (!IsBad(c, r + 1))
                    Line(P(c, r + 1), P(c + 1, r + 1));
                if (!IsBad(c - 1, r))
                    Line(P(c, r), P(c, r + 1));
                if (!IsBad(c + 1, r))
                    Line(P(c + 1, r), P(c + 1, r + 1));
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
