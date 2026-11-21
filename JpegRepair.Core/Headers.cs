namespace JpegRepair.Core;

/// <param name="Id">Component identifier as written in the file (not necessarily 1..N).</param>
/// <param name="Index">Position of the component in the frame header.</param>
/// <param name="H">Horizontal sampling factor used for MCU layout (forced to 1 for single-component images).</param>
/// <param name="V">Vertical sampling factor used for MCU layout (forced to 1 for single-component images).</param>
public sealed record FrameComponent(int Id, int Index, int H, int V, int QuantTableId);

public sealed class FrameHeader
{
    public FrameHeader(byte marker, int precision, int width, int height, FrameComponent[] components)
    {
        Marker = marker;
        Precision = precision;
        Width = width;
        Height = height;
        Components = components;

        MaxH = components.Max(c => c.H);
        MaxV = components.Max(c => c.V);
        foreach (var c in components)
        {
            if (MaxH % c.H != 0 || MaxV % c.V != 0)
                throw new JpegNotSupportedException($"Sampling factors {c.H}x{c.V} (max {MaxH}x{MaxV}) are not supported.");
        }

        McuWidth = 8 * MaxH;
        McuHeight = 8 * MaxV;
        McusPerRow = (width + McuWidth - 1) / McuWidth;
        McuRows = (height + McuHeight - 1) / McuHeight;
        BlocksPerMcu = components.Sum(c => c.H * c.V);
    }

    public byte Marker { get; }
    public int Precision { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<FrameComponent> Components { get; }
    public int MaxH { get; }
    public int MaxV { get; }
    public int McuWidth { get; }
    public int McuHeight { get; }
    public int McusPerRow { get; }
    public int McuRows { get; }
    public int McuCount => McusPerRow * McuRows;
    public int BlocksPerMcu { get; }

    internal static FrameHeader Parse(byte marker, ReadOnlySpan<byte> p)
    {
        if (p.Length < 6)
            throw new JpegFormatException("Truncated frame header (SOF).");

        int precision = p[0];
        int height = (p[1] << 8) | p[2];
        int width = (p[3] << 8) | p[4];
        int count = p[5];

        if (precision != 8)
            throw new JpegNotSupportedException($"{precision}-bit JPEGs are not supported.");
        if (height == 0)
            throw new JpegNotSupportedException("JPEGs that give their height in a DNL marker are not supported.");
        if (width == 0)
            throw new JpegFormatException("Frame width is 0.");
        if (count is < 1 or > 4)
            throw new JpegFormatException($"Invalid number of components: {count}.");
        if (p.Length < 6 + 3 * count)
            throw new JpegFormatException("Truncated frame header (SOF).");

        var components = new FrameComponent[count];
        for (int i = 0; i < count; i++)
        {
            int id = p[6 + 3 * i];
            int sampling = p[7 + 3 * i];
            int tq = p[8 + 3 * i];
            int h = sampling >> 4, v = sampling & 15;

            if (h is < 1 or > 4 || v is < 1 or > 4)
                throw new JpegFormatException($"Invalid sampling factors {h}x{v} for component {id}.");
            if (tq > 3)
                throw new JpegFormatException($"Invalid quantization table {tq} for component {id}.");
            for (int j = 0; j < i; j++)
            {
                if (components[j].Id == id)
                    throw new JpegFormatException($"Component id {id} appears twice in the frame header.");
            }

            components[i] = new FrameComponent(id, i, h, v, tq);
        }

        // A single-component scan is non-interleaved: one block per MCU regardless of the declared sampling.
        if (count == 1)
            components[0] = components[0] with { H = 1, V = 1 };

        return new FrameHeader(marker, precision, width, height, components);
    }
}

public sealed record ScanComponent(FrameComponent Component, int DcTableId, int AcTableId);

public sealed class ScanHeader(ScanComponent[] components, int ss, int se, int ah, int al)
{
    public IReadOnlyList<ScanComponent> Components { get; } = components;
    public int SpectralStart { get; } = ss;
    public int SpectralEnd { get; } = se;
    public int ApproxHigh { get; } = ah;
    public int ApproxLow { get; } = al;

    internal static ScanHeader Parse(ReadOnlySpan<byte> p, FrameHeader frame)
    {
        if (p.Length < 1)
            throw new JpegFormatException("Truncated scan header (SOS).");
        int count = p[0];
        if (count is < 1 or > 4)
            throw new JpegFormatException($"Invalid number of scan components: {count}.");
        if (p.Length < 1 + 2 * count + 3)
            throw new JpegFormatException("Truncated scan header (SOS).");

        var components = new ScanComponent[count];
        for (int i = 0; i < count; i++)
        {
            int id = p[1 + 2 * i];
            int tables = p[2 + 2 * i];
            var component = frame.Components.FirstOrDefault(c => c.Id == id)
                            ?? throw new JpegFormatException($"Scan uses component {id}, which is not in the frame header.");
            int td = tables >> 4, ta = tables & 15;
            if (td > 3 || ta > 3)
                throw new JpegFormatException($"Invalid Huffman table selection for component {id}.");
            components[i] = new ScanComponent(component, td, ta);
        }

        int ss = p[1 + 2 * count];
        int se = p[2 + 2 * count];
        int a = p[3 + 2 * count];
        return new ScanHeader(components, ss, se, a >> 4, a & 15);
    }
}
