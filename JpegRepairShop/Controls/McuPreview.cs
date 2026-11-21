using System.Windows;
using JpegRepair.Core;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace JpegRepairShop.Controls;

/// <summary>Magnified view of one MCU with pixel and 8x8 block grid lines.</summary>
public sealed class McuPreview : SKElement
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(JpegDocument), typeof(McuPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty McuIndexProperty = DependencyProperty.Register(
        nameof(McuIndex), typeof(int), typeof(McuPreview),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bump to redraw after the document was edited.</summary>
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(McuPreview),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly SKColor Background = new(0x2A, 0x2A, 0x2E);
    private static readonly SKPaint PixelGrid = new() { Color = new SKColor(0, 0, 0, 45), Style = SKPaintStyle.Stroke, StrokeWidth = 0 };
    private static readonly SKPaint BlockGrid = new() { Color = new SKColor(255, 255, 255, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 0 };

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

    public int McuIndex
    {
        get => (int)GetValue(McuIndexProperty);
        set => SetValue(McuIndexProperty, value);
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        var canvas = e.Surface.Canvas;
        canvas.Clear(Background);

        if (Document is not { } doc || McuIndex < 0 || McuIndex >= doc.File.Frame.McuCount || ActualWidth <= 0)
            return;

        canvas.Scale((float)(e.Info.Width / ActualWidth));

        var frame = doc.File.Frame;
        int mw = frame.McuWidth, mh = frame.McuHeight;
        var pixels = new byte[mw * mh * 4];
        doc.Renderer.RenderMcu(McuIndex, pixels, mw * 4);
        using var image = SKImage.FromPixelCopy(new SKImageInfo(mw, mh, SKColorType.Bgra8888, SKAlphaType.Opaque), pixels, mw * 4);

        float width = (float)ActualWidth, height = (float)ActualHeight;
        float cell = Math.Min(width / mw, height / mh);
        if (cell >= 1)
            cell = MathF.Floor(cell);
        float ox = (width - cell * mw) / 2, oy = (height - cell * mh) / 2;

        canvas.DrawImage(image, SKRect.Create(ox, oy, cell * mw, cell * mh),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), null);

        if (cell >= 5)
        {
            for (int x = 1; x < mw; x++)
                canvas.DrawLine(ox + x * cell, oy, ox + x * cell, oy + mh * cell, x % 8 == 0 ? BlockGrid : PixelGrid);
            for (int y = 1; y < mh; y++)
                canvas.DrawLine(ox, oy + y * cell, ox + mw * cell, oy + y * cell, y % 8 == 0 ? BlockGrid : PixelGrid);
        }
    }
}
