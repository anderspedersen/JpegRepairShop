namespace JpegRepair.Core;

/// <summary>
/// Turns decoded coefficients into 32-bit BGRA pixels (byte order B, G, R, A; alpha always 255).
/// That layout matches SkiaSharp's <c>SKColorType.Bgra8888</c> and WPF's <c>PixelFormats.Bgra32</c>.
/// MCU rows are rendered in parallel. Chroma upsampling is nearest-neighbour, so every pixel
/// belongs to exactly one MCU, which keeps MCU edits easy to see.
/// </summary>
public sealed class JpegRenderer
{
    private enum ColorModel { Gray, YCbCr, Rgb }

    private static readonly int[] CrToR = new int[256];
    private static readonly int[] CbToB = new int[256];
    private static readonly int[] CrToG = new int[256];
    private static readonly int[] CbToG = new int[256];

    private readonly FrameHeader _frame;
    private readonly IMcuSource _source;
    private readonly ColorModel _model;
    private readonly float[][] _quant;          // per frame component, natural order
    private readonly int[] _planeOffset;        // per frame component, offset into the MCU sample buffer
    private readonly int[] _blockPlaneOffset;   // per block in the MCU
    private readonly int[] _blockQuant;         // per block: frame component index
    private readonly int[] _blockStride;        // per block: row stride of its component plane
    private readonly int[][] _sampleX;          // per component: x inside MCU -> column in its plane
    private readonly int[][] _sampleY;          // per component: y inside MCU -> plane offset of the row
    private readonly int _samplesPerMcu;

    static JpegRenderer()
    {
        for (int i = 0; i < 256; i++)
        {
            double d = i - 128;
            CrToR[i] = (int)Math.Round(1.402 * d * 65536);
            CbToB[i] = (int)Math.Round(1.772 * d * 65536);
            CrToG[i] = (int)Math.Round(-0.714136 * d * 65536);
            CbToG[i] = (int)Math.Round(-0.344136 * d * 65536);
        }
    }

    public JpegRenderer(JpegFile file, McuLayout layout, IMcuSource source)
    {
        _frame = file.Frame;
        _source = source;
        var components = _frame.Components;

        _model = components.Count switch
        {
            1 => ColorModel.Gray,
            3 when components[0].Id == 'R' && components[1].Id == 'G' && components[2].Id == 'B' => ColorModel.Rgb,
            3 => ColorModel.YCbCr,
            _ => throw new JpegNotSupportedException($"{components.Count}-component (CMYK/YCCK) JPEGs are not supported yet."),
        };

        _quant = new float[components.Count][];
        _planeOffset = new int[components.Count];
        _sampleX = new int[components.Count][];
        _sampleY = new int[components.Count][];

        int offset = 0;
        for (int c = 0; c < components.Count; c++)
        {
            var comp = components[c];
            var table = file.GetQuantizationTable(comp.QuantTableId)
                        ?? throw new JpegFormatException($"Missing quantization table {comp.QuantTableId} for component {comp.Id}.");
            _quant[c] = table.Values.Select(v => (float)v).ToArray();

            _planeOffset[c] = offset;
            int planeWidth = comp.H * 8;
            _sampleX[c] = new int[_frame.McuWidth];
            for (int x = 0; x < _frame.McuWidth; x++)
                _sampleX[c][x] = x * comp.H / _frame.MaxH;
            _sampleY[c] = new int[_frame.McuHeight];
            for (int y = 0; y < _frame.McuHeight; y++)
                _sampleY[c][y] = offset + (y * comp.V / _frame.MaxV) * planeWidth;

            offset += comp.H * comp.V * 64;
        }

        _samplesPerMcu = offset;

        var blocks = layout.Blocks;
        _blockPlaneOffset = new int[blocks.Count];
        _blockQuant = new int[blocks.Count];
        _blockStride = new int[blocks.Count];
        for (int b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            var comp = components[block.ComponentIndex];
            _blockPlaneOffset[b] = _planeOffset[block.ComponentIndex] + block.BlockY * 8 * (comp.H * 8) + block.BlockX * 8;
            _blockQuant[b] = block.ComponentIndex;
            _blockStride[b] = comp.H * 8;
        }
    }

    public int Width => _frame.Width;

    public int Height => _frame.Height;

    /// <summary>Colour used for MCUs that were never decoded (BGRA packed as 0xAARRGGBB).</summary>
    public uint MissingColor { get; set; } = 0xFF303030;

    /// <summary>Renders the whole image into a new Width × Height BGRA buffer.</summary>
    public byte[] RenderBgra()
    {
        var buffer = new byte[Width * Height * 4];
        Render(buffer, Width * 4);
        return buffer;
    }

    public void Render(byte[] bgra, int stride) => RenderMcuRows(0, _frame.McuRows, bgra, stride);

    /// <summary>Re-renders a band of MCU rows, e.g. everything after an edited MCU.</summary>
    public void RenderMcuRows(int firstRow, int rowCount, byte[] bgra, int stride)
    {
        if (stride < Width * 4 || bgra.Length < (long)stride * (Height - 1) + Width * 4)
            throw new ArgumentException("Buffer is too small for the image.", nameof(bgra));

        firstRow = Math.Max(0, firstRow);
        int endRow = Math.Min(_frame.McuRows, firstRow + rowCount);
        if (endRow <= firstRow)
            return;

        Parallel.For(firstRow, endRow, () => new byte[_samplesPerMcu], (row, _, samples) =>
        {
            for (int col = 0; col < _frame.McusPerRow; col++)
            {
                RenderMcuCore(row * _frame.McusPerRow + col, samples, bgra, stride,
                    col * _frame.McuWidth, row * _frame.McuHeight, Width, Height);
            }

            return samples;
        }, _ => { });
    }

    /// <summary>Renders one MCU (McuWidth × McuHeight, uncropped) at the top left of <paramref name="bgra"/>, for previews.</summary>
    public void RenderMcu(int mcuIndex, byte[] bgra, int stride)
    {
        if (stride < _frame.McuWidth * 4 || bgra.Length < stride * (_frame.McuHeight - 1) + _frame.McuWidth * 4)
            throw new ArgumentException("Buffer is too small for one MCU.", nameof(bgra));
        RenderMcuCore(mcuIndex, new byte[_samplesPerMcu], bgra, stride, 0, 0, _frame.McuWidth, _frame.McuHeight);
    }

    private void RenderMcuCore(int index, byte[] samples, byte[] dest, int stride, int x0, int y0, int maxWidth, int maxHeight)
    {
        int width = Math.Min(_frame.McuWidth, maxWidth - x0);
        int height = Math.Min(_frame.McuHeight, maxHeight - y0);
        if (width <= 0 || height <= 0)
            return;

        if ((uint)index >= (uint)_source.Count || _source.GetStatus(index) == McuStatus.Missing)
        {
            Fill(dest, stride, x0, y0, width, height, MissingColor);
            return;
        }

        var coefficients = _source.GetCoefficients(index);
        for (int b = 0; b < _blockPlaneOffset.Length; b++)
        {
            Idct.Transform(coefficients.Slice(b * 64, 64), _quant[_blockQuant[b]],
                samples.AsSpan(_blockPlaneOffset[b]), _blockStride[b]);
        }

        for (int y = 0; y < height; y++)
        {
            int o = (y0 + y) * stride + x0 * 4;
            switch (_model)
            {
                case ColorModel.Gray:
                {
                    int row0 = _sampleY[0][y];
                    var sx = _sampleX[0];
                    for (int x = 0; x < width; x++, o += 4)
                    {
                        byte g = samples[row0 + sx[x]];
                        dest[o] = g;
                        dest[o + 1] = g;
                        dest[o + 2] = g;
                        dest[o + 3] = 255;
                    }

                    break;
                }

                case ColorModel.YCbCr:
                {
                    int rowY = _sampleY[0][y], rowCb = _sampleY[1][y], rowCr = _sampleY[2][y];
                    int[] sxY = _sampleX[0], sxCb = _sampleX[1], sxCr = _sampleX[2];
                    for (int x = 0; x < width; x++, o += 4)
                    {
                        int lum = samples[rowY + sxY[x]];
                        int cb = samples[rowCb + sxCb[x]];
                        int cr = samples[rowCr + sxCr[x]];
                        dest[o] = Clamp(lum + ((CbToB[cb] + 32768) >> 16));
                        dest[o + 1] = Clamp(lum + ((CbToG[cb] + CrToG[cr] + 32768) >> 16));
                        dest[o + 2] = Clamp(lum + ((CrToR[cr] + 32768) >> 16));
                        dest[o + 3] = 255;
                    }

                    break;
                }

                case ColorModel.Rgb:
                {
                    int rowR = _sampleY[0][y], rowG = _sampleY[1][y], rowB = _sampleY[2][y];
                    for (int x = 0; x < width; x++, o += 4)
                    {
                        dest[o] = samples[rowB + _sampleX[2][x]];
                        dest[o + 1] = samples[rowG + _sampleX[1][x]];
                        dest[o + 2] = samples[rowR + _sampleX[0][x]];
                        dest[o + 3] = 255;
                    }

                    break;
                }
            }
        }
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    private static void Fill(byte[] dest, int stride, int x0, int y0, int width, int height, uint color)
    {
        byte b = (byte)color, g = (byte)(color >> 8), r = (byte)(color >> 16), a = (byte)(color >> 24);
        for (int y = 0; y < height; y++)
        {
            int o = (y0 + y) * stride + x0 * 4;
            for (int x = 0; x < width; x++, o += 4)
            {
                dest[o] = b;
                dest[o + 1] = g;
                dest[o + 2] = r;
                dest[o + 3] = a;
            }
        }
    }
}
