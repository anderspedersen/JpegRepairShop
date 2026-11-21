namespace JpegRepair.Core;

/// <summary>Read-only convenience wrapper: parse, decode the scan and render. For editing use <see cref="JpegDocument"/>.</summary>
public sealed class DecodedJpeg
{
    private DecodedJpeg(JpegFile file, ScanDecodeResult scan)
    {
        File = file;
        Scan = scan;
        Renderer = new JpegRenderer(file, scan.Layout, scan);
    }

    public JpegFile File { get; }

    public ScanDecodeResult Scan { get; }

    public JpegRenderer Renderer { get; }

    public int Width => File.Frame.Width;

    public int Height => File.Frame.Height;

    /// <summary>Throws <see cref="JpegFormatException"/> if the headers are unusable; damaged scan data is reported in <see cref="Scan"/>.</summary>
    public static DecodedJpeg Decode(byte[] data)
    {
        var file = JpegFile.Parse(data);
        return new DecodedJpeg(file, ScanDecoder.Decode(file));
    }

    public static DecodedJpeg Load(string path) => Decode(System.IO.File.ReadAllBytes(path));

    public byte[] RenderBgra() => Renderer.RenderBgra();

    /// <summary>Re-encodes the current coefficients into a complete JPEG file.</summary>
    public byte[] Save() => JpegWriter.Write(File, ScanEncoder.Encode(File, Scan.Layout,
        Scan.Coefficients.AsSpan(0, File.Frame.McuCount * Scan.Layout.CoefficientsPerMcu)));
}
