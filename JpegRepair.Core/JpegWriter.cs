namespace JpegRepair.Core;

public static class JpegWriter
{
    /// <summary>
    /// Builds a file from the original headers (everything up to and including the SOS header, byte for byte),
    /// new entropy-coded data, and whatever followed the scan in the original (EOI plus any trailing bytes).
    /// A truncated original gets an EOI marker added.
    /// </summary>
    public static byte[] Write(JpegFile file, ReadOnlySpan<byte> entropyData)
    {
        var source = file.Source;
        int tailStart = file.Entropy.SourceEnd;
        bool hasTail = file.Entropy.EndMarker != null && tailStart < source.Length;

        using var output = new MemoryStream(file.ScanDataOffset + entropyData.Length + (hasTail ? source.Length - tailStart : 2));
        output.Write(source, 0, file.ScanDataOffset);
        output.Write(entropyData);
        if (hasTail)
        {
            output.Write(source, tailStart, source.Length - tailStart);
        }
        else
        {
            output.WriteByte(0xFF);
            output.WriteByte(Markers.EOI);
        }

        return output.ToArray();
    }
}
