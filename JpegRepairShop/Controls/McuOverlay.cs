using System.Windows;
using System.Windows.Media;

namespace JpegRepairShop.Controls;

/// <summary>
/// Draws the hover and selection outlines on top of a <see cref="JpegCanvas"/> using plain WPF drawing.
/// Kept separate so that moving the mouse only redraws two rectangles instead of repainting the whole picture.
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

        DrawOutline(dc, canvas.GetMcuRect(canvas.HoverMcu), HoverOuter, HoverInner);
        DrawOutline(dc, canvas.GetMcuRect(canvas.SelectedMcu), SelectedOuter, SelectedInner);
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
