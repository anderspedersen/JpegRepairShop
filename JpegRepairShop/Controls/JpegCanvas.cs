using System.Windows;
using System.Windows.Input;
using JpegRepair.Core;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace JpegRepairShop.Controls;

/// <summary>
/// Shows a decoded JPEG with zoom/pan, an MCU grid and problem highlighting, and tracks hover/selection of MCUs.
/// Mouse: wheel = zoom at cursor, drag (any button) = pan, left click = select MCU.
/// Keys: arrows = move selection, Ctrl+0 = fit, Ctrl+1 = 100 %, +/- = zoom.
///
/// Performance notes:
/// - Skia only repaints when the view (zoom/pan/size) or the picture changes. Hover and selection outlines are drawn
///   by <see cref="McuOverlay"/> (plain WPF), so moving the mouse does not repaint the image.
/// - The picture is kept as a pyramid of half-size copies built once per change; zoomed out, the level closest to
///   the screen size is drawn with plain bilinear filtering instead of building mipmaps every frame.
/// All layout maths is in WPF device-independent pixels; the Skia canvas is scaled to device pixels when painting.
/// </summary>
public sealed class JpegCanvas : SKElement
{
    private const double MinZoom = 0.02;
    private const double MaxZoom = 64;
    private const double DragThreshold = 4;
    private const int SmallestPyramidLevel = 256;

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(JpegDocument), typeof(JpegCanvas),
        new PropertyMetadata(null, (d, _) => ((JpegCanvas)d).OnDocumentChanged()));

    /// <summary>Bump after the document was edited; the picture is then refreshed from <see cref="JpegDocument.Pixels"/>.</summary>
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(JpegCanvas),
        new PropertyMetadata(0, (d, _) => ((JpegCanvas)d).RefreshImage()));

    public static readonly DependencyProperty SelectedMcuProperty = DependencyProperty.Register(
        nameof(SelectedMcu), typeof(int), typeof(JpegCanvas),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((JpegCanvas)d).OnSelectedMcuChanged()));

    public static readonly DependencyProperty HoverMcuProperty = DependencyProperty.Register(
        nameof(HoverMcu), typeof(int), typeof(JpegCanvas),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((JpegCanvas)d).OverlayChanged?.Invoke(d, EventArgs.Empty)));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(JpegCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Mark bad MCUs (filled here, outlined by <see cref="McuOverlay"/>) and the other problems.</summary>
    public static readonly DependencyProperty ShowBadMcusProperty = DependencyProperty.Register(
        nameof(ShowBadMcus), typeof(bool), typeof(JpegCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((JpegCanvas)d).OverlayChanged?.Invoke(d, EventArgs.Empty)));

    /// <summary>MCUs found by bad-MCU detection; drawn in orange when <see cref="ShowBadMcus"/> is on.</summary>
    public static readonly DependencyProperty BadMcusProperty = DependencyProperty.Register(
        nameof(BadMcus), typeof(BadMcuMap), typeof(JpegCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((JpegCanvas)d).OverlayChanged?.Invoke(d, EventArgs.Empty)));

    /// <summary>The section to outline (see <see cref="JpegDocument.FindSection"/>), or null.</summary>
    public static readonly DependencyProperty SectionProperty = DependencyProperty.Register(
        nameof(Section), typeof(McuSection?), typeof(JpegCanvas),
        new PropertyMetadata(null, (d, _) => ((JpegCanvas)d).OverlayChanged?.Invoke(d, EventArgs.Empty)));

    private static readonly DependencyPropertyKey ZoomPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Zoom), typeof(double), typeof(JpegCanvas), new PropertyMetadata(1.0));

    public static readonly DependencyProperty ZoomProperty = ZoomPropertyKey.DependencyProperty;

    private static readonly SKColor Background = new(0x20, 0x20, 0x24);
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
    private static readonly SKSamplingOptions Bilinear = new(SKFilterMode.Linear, SKMipmapMode.None);
    private static readonly SKPaint GridPaint = new() { Color = new SKColor(255, 255, 255, 45), Style = SKPaintStyle.Stroke, StrokeWidth = 0 };
    private static readonly SKPaint BorderPaint = new() { Color = new SKColor(255, 255, 255, 90), Style = SKPaintStyle.Stroke, StrokeWidth = 0 };
    private static readonly SKPaint CorruptFill = new() { Color = new SKColor(255, 40, 40, 110), Style = SKPaintStyle.Fill };
    private static readonly SKPaint BadFill = new() { Color = new SKColor(255, 150, 0, 120), Style = SKPaintStyle.Fill };
    private static readonly SKPaint BadOutline = new() { Color = new SKColor(255, 170, 0, 220), Style = SKPaintStyle.Stroke, StrokeWidth = 0 };
    private static readonly SKPaint NotBadOutline = new() { Color = new SKColor(255, 170, 0, 110), Style = SKPaintStyle.Stroke, StrokeWidth = 0, PathEffect = SKPathEffect.CreateDash([2f, 2f], 0) };
    private static readonly SKPaint FilledFill = new() { Color = new SKColor(60, 220, 120, 70), Style = SKPaintStyle.Fill };
    private static readonly SKPaint BlankFill = new() { Color = new SKColor(60, 140, 255, 110), Style = SKPaintStyle.Fill };
    private static readonly SKPaint MissingFill = new() { Color = new SKColor(255, 60, 60, 60), Style = SKPaintStyle.Fill };
    private static readonly SKPaint MissingCross = new() { Color = new SKColor(255, 80, 80, 140), Style = SKPaintStyle.Stroke, StrokeWidth = 0 };

    private SKImage[] _levels = [];      // [0] = full size, then half size, quarter size...
    private double _offsetX, _offsetY;   // where the image's top-left corner is, in DIPs
    private bool _fitPending;            // keep fitting on resize until the user zooms or pans
    private Point? _dragStart;
    private MouseButton _dragButton;
    private bool _dragging;
    private double _dragOriginX, _dragOriginY;

    public JpegCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
        SizeChanged += (_, _) =>
        {
            if (_fitPending)
                ZoomToFit();
            Redraw();
        };
    }

    /// <summary>Raised when the hover/selection outlines must be redrawn (hover, selection, zoom or pan changed).</summary>
    public event EventHandler? OverlayChanged;

    public JpegDocument? Document
    {
        get => (JpegDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public int SelectedMcu
    {
        get => (int)GetValue(SelectedMcuProperty);
        set => SetValue(SelectedMcuProperty, value);
    }

    public int HoverMcu
    {
        get => (int)GetValue(HoverMcuProperty);
        set => SetValue(HoverMcuProperty, value);
    }

    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public bool ShowBadMcus
    {
        get => (bool)GetValue(ShowBadMcusProperty);
        set => SetValue(ShowBadMcusProperty, value);
    }

    public BadMcuMap? BadMcus
    {
        get => (BadMcuMap?)GetValue(BadMcusProperty);
        set => SetValue(BadMcusProperty, value);
    }

    public McuSection? Section
    {
        get => (McuSection?)GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }

    /// <summary>Image pixels per DIP.</summary>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        private set => SetValue(ZoomPropertyKey, value);
    }

    public void ZoomToFit()
    {
        _fitPending = true;
        if (Document is not { } doc || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        double zoom = Math.Min(ActualWidth / doc.Width, ActualHeight / doc.Height) * 0.98;
        Zoom = Math.Clamp(zoom, MinZoom, 16);
        _offsetX = (ActualWidth - doc.Width * Zoom) / 2;
        _offsetY = (ActualHeight - doc.Height * Zoom) / 2;
        Redraw();
    }

    public void ZoomToActualSize() => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), 1);

    /// <summary>Rectangle of an MCU in this control's coordinates (DIPs), or <see cref="Rect.Empty"/>.</summary>
    public Rect GetMcuRect(int index)
    {
        if (Document is not { } doc || index < 0 || index >= doc.Frame.McuCount)
            return Rect.Empty;

        var frame = doc.Frame;
        int col = index % frame.McusPerRow, row = index / frame.McusPerRow;
        double w = Math.Min(frame.McuWidth, frame.Width - col * frame.McuWidth);
        double h = Math.Min(frame.McuHeight, frame.Height - row * frame.McuHeight);
        return new Rect(_offsetX + col * frame.McuWidth * Zoom, _offsetY + row * frame.McuHeight * Zoom, w * Zoom, h * Zoom);
    }

    /// <summary>A point of the image (in image pixels) in this control's coordinates (DIPs).</summary>
    public Point ImageToView(double x, double y) => new(_offsetX + x * Zoom, _offsetY + y * Zoom);

    /// <summary>Rebuilds the picture from the document's pixel cache (after loading or an edit), keeping the view.</summary>
    public void RefreshImage()
    {
        foreach (var level in _levels)
            level.Dispose();
        _levels = Document is { } doc ? BuildPyramid(doc.Pixels, doc.Width, doc.Height) : [];
        Redraw();
    }

    private void Redraw()
    {
        InvalidateVisual();
        OverlayChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDocumentChanged()
    {
        _dragStart = null;
        _dragging = false;
        RefreshImage();
        ZoomToFit();
    }

    private void OnSelectedMcuChanged()
    {
        if (EnsureVisible(SelectedMcu))
            Redraw();
        else
            OverlayChanged?.Invoke(this, EventArgs.Empty);
    }

    private static SKImage[] BuildPyramid(byte[] pixels, int width, int height)
    {
        var levels = new List<SKImage> { CreateImage(pixels, width, height) };
        while (Math.Max(width, height) > SmallestPyramidLevel && width > 1 && height > 1)
        {
            (pixels, width, height) = HalveImage(pixels, width, height);
            levels.Add(CreateImage(pixels, width, height));
        }

        return levels.ToArray();
    }

    private static SKImage CreateImage(byte[] pixels, int width, int height) =>
        SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque), pixels, width * 4);

    /// <summary>2×2 box filter (BGRA, opaque).</summary>
    private static (byte[] Pixels, int Width, int Height) HalveImage(byte[] source, int width, int height)
    {
        int w = (width + 1) / 2, h = (height + 1) / 2;
        var result = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            int row0 = 2 * y * width * 4;
            int row1 = Math.Min(2 * y + 1, height - 1) * width * 4;
            int o = y * w * 4;
            for (int x = 0; x < w; x++, o += 4)
            {
                int c0 = 2 * x * 4;
                int c1 = Math.Min(2 * x + 1, width - 1) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    int sum = source[row0 + c0 + ch] + source[row0 + c1 + ch] + source[row1 + c0 + ch] + source[row1 + c1 + ch];
                    result[o + ch] = (byte)((sum + 2) >> 2);
                }

                result[o + 3] = 255;
            }
        });
        return (result, w, h);
    }

    private void ZoomAt(Point anchor, double zoom)
    {
        if (Document == null)
            return;

        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _offsetX = anchor.X - (anchor.X - _offsetX) * zoom / Zoom;
        _offsetY = anchor.Y - (anchor.Y - _offsetY) * zoom / Zoom;
        Zoom = zoom;
        _fitPending = false;
        Redraw();
    }

    /// <summary>Scrolls an MCU into view. Returns true if the view moved.</summary>
    private bool EnsureVisible(int index)
    {
        var rect = GetMcuRect(index);
        if (rect.IsEmpty || ActualWidth <= 0)
            return false;

        var view = new Rect(0, 0, ActualWidth, ActualHeight);
        if (view.Contains(rect))
            return false;

        _offsetX += ActualWidth / 2 - (rect.X + rect.Width / 2);
        _offsetY += ActualHeight / 2 - (rect.Y + rect.Height / 2);
        _fitPending = false;
        return true;
    }

    private int HitTest(Point p)
    {
        if (Document is not { } doc)
            return -1;

        double x = (p.X - _offsetX) / Zoom, y = (p.Y - _offsetY) / Zoom;
        if (x < 0 || y < 0 || x >= doc.Width || y >= doc.Height)
            return -1;

        var frame = doc.Frame;
        return (int)y / frame.McuHeight * frame.McusPerRow + (int)x / frame.McuWidth;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomAt(e.GetPosition(this), Zoom * (e.Delta > 0 ? 1.25 : 1 / 1.25));
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (Document == null || _dragStart != null)
            return;

        _dragStart = e.GetPosition(this);
        _dragButton = e.ChangedButton;
        _dragging = false;
        _dragOriginX = _offsetX;
        _dragOriginY = _offsetY;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);

        if (_dragStart is { } start)
        {
            if (!_dragging && (p - start).Length > DragThreshold)
            {
                _dragging = true;
                Cursor = Cursors.SizeAll;
            }

            if (_dragging)
            {
                _offsetX = _dragOriginX + p.X - start.X;
                _offsetY = _dragOriginY + p.Y - start.Y;
                _fitPending = false;
                Redraw();
            }
        }

        int hover = HitTest(p);
        if (hover != HoverMcu)
            SetCurrentValue(HoverMcuProperty, hover);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragStart == null || e.ChangedButton != _dragButton)
            return;

        if (!_dragging && e.ChangedButton == MouseButton.Left)
            SetCurrentValue(SelectedMcuProperty, HitTest(e.GetPosition(this)));

        EndDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragStart = null;
        _dragging = false;
        Cursor = null;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragStart == null)
            SetCurrentValue(HoverMcuProperty, -1);
    }

    private void EndDrag()
    {
        _dragStart = null;
        _dragging = false;
        Cursor = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Document is not { } doc)
            return;

        var frame = doc.Frame;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        int selected = SelectedMcu;
        int moved = selected;

        switch (e.Key)
        {
            case Key.D0 or Key.NumPad0 when ctrl:
                ZoomToFit();
                break;
            case Key.D1 or Key.NumPad1 when ctrl:
                ZoomAt(center, 1);
                break;
            case Key.Add or Key.OemPlus:
                ZoomAt(center, Zoom * 1.25);
                break;
            case Key.Subtract or Key.OemMinus:
                ZoomAt(center, Zoom / 1.25);
                break;
            case Key.Left:
                moved = selected < 0 ? 0 : Math.Max(0, selected - 1);
                break;
            case Key.Right:
                moved = selected < 0 ? 0 : Math.Min(frame.McuCount - 1, selected + 1);
                break;
            case Key.Up:
                moved = selected < 0 ? 0 : selected - frame.McusPerRow >= 0 ? selected - frame.McusPerRow : selected;
                break;
            case Key.Down:
                moved = selected < 0 ? 0 : selected + frame.McusPerRow < frame.McuCount ? selected + frame.McusPerRow : selected;
                break;
            case Key.Escape:
                moved = -1;
                break;
            default:
                return;
        }

        if (moved != selected)
            SetCurrentValue(SelectedMcuProperty, moved);
        e.Handled = true;
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        var canvas = e.Surface.Canvas;
        canvas.Clear(Background);

        if (_levels.Length == 0 || Document is not { } doc || ActualWidth <= 0)
            return;

        float dpiScale = (float)(e.Info.Width / ActualWidth);
        canvas.Scale(dpiScale);

        var frame = doc.Frame;
        float zoom = (float)Zoom;
        float ox = (float)_offsetX, oy = (float)_offsetY;
        var imageRect = SKRect.Create(ox, oy, doc.Width * zoom, doc.Height * zoom);

        // Pick the smallest pyramid level that still has at least one image pixel per device pixel.
        float devicePixelsPerImagePixel = zoom * dpiScale;
        int level = 0;
        float levelScale = 1;
        while (level + 1 < _levels.Length && devicePixelsPerImagePixel <= levelScale / 2)
        {
            level++;
            levelScale /= 2;
        }

        canvas.DrawImage(_levels[level], imageRect, zoom >= 1 ? Nearest : Bilinear, null);
        canvas.DrawRect(imageRect, BorderPaint);

        float cellW = frame.McuWidth * zoom, cellH = frame.McuHeight * zoom;

        // Visible MCU range.
        int col0 = Math.Max(0, (int)Math.Floor(-ox / cellW));
        int col1 = Math.Min(frame.McusPerRow - 1, (int)Math.Floor(((float)ActualWidth - ox) / cellW));
        int row0 = Math.Max(0, (int)Math.Floor(-oy / cellH));
        int row1 = Math.Min(frame.McuRows - 1, (int)Math.Floor(((float)ActualHeight - oy) / cellH));
        if (col1 < col0 || row1 < row0)
            return;

        canvas.Save();
        canvas.ClipRect(imageRect);

        if (ShowBadMcus && cellW >= 2)
        {
            var bad = BadMcus;
            for (int row = row0; row <= row1; row++)
            {
                for (int col = col0; col <= col1; col++)
                {
                    int index = row * frame.McusPerRow + col;
                    var status = doc.GetStatus(index);
                    var r = SKRect.Create(ox + col * cellW, oy + row * cellH, cellW, cellH);
                    if (status is McuStatus.Ok or McuStatus.Filled)
                    {
                        if (status == McuStatus.Filled)
                            canvas.DrawRect(r, FilledFill);

                        // Detected: doesn't fit 2 or more of its neighbours (decoder found nothing wrong).
                        if (bad?.IsBad(index) == true)
                        {
                            canvas.DrawRect(r, BadFill);
                            if (cellW >= 6)
                                canvas.DrawRect(r, BadOutline);
                        }
                        else if (cellW >= 6 && bad?.Mark(index) == McuMark.NotBad)
                        {
                            // Flagged, but marked as not bad by the user.
                            canvas.DrawRect(r, NotBadOutline);
                        }

                        continue;
                    }

                    if (status == McuStatus.Corrupt)
                    {
                        // A decoding error, unless the user marked it as not bad (its data still looks right).
                        if (bad?.Mark(index) == McuMark.NotBad)
                        {
                            if (cellW >= 6)
                                canvas.DrawRect(r, NotBadOutline);
                        }
                        else
                        {
                            canvas.DrawRect(r, CorruptFill);
                        }
                    }
                    else if (status == McuStatus.Blank)
                    {
                        canvas.DrawRect(r, BlankFill);
                    }
                    else
                    {
                        canvas.DrawRect(r, MissingFill);
                        if (cellW >= 6)
                        {
                            canvas.DrawLine(r.Left, r.Top, r.Right, r.Bottom, MissingCross);
                            canvas.DrawLine(r.Left, r.Bottom, r.Right, r.Top, MissingCross);
                        }
                    }
                }
            }
        }

        if (ShowGrid && cellW >= 8 && cellH >= 8)
        {
            float top = oy + row0 * cellH, bottom = Math.Min(imageRect.Bottom, oy + (row1 + 1) * cellH);
            float left = ox + col0 * cellW, right = Math.Min(imageRect.Right, ox + (col1 + 1) * cellW);
            for (int col = col0; col <= col1 + 1; col++)
                canvas.DrawLine(ox + col * cellW, top, ox + col * cellW, bottom, GridPaint);
            for (int row = row0; row <= row1 + 1; row++)
                canvas.DrawLine(left, oy + row * cellH, right, oy + row * cellH, GridPaint);
        }

        canvas.Restore();
    }
}
